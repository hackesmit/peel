// One engine process, started inside its kill on close job before it can run.
//
// Process.Start cannot do this: the process is already running when it
// returns, so anything the engine starts before AssignProcessToJobObject is
// outside the job and survives Peel (lucy round 2). Here CreateProcessW starts
// it with CREATE_SUSPENDED and PROC_THREAD_ATTRIBUTE_JOB_LIST, so it is born
// in the job: a Peel crash at any point after creation closes the job handle
// and Windows ends the suspended process (lucy, hq-plct.17; assigning it after
// creation left a window where a crash orphaned it). Only then is its first
// thread resumed, through a ResumeGate: the caller cancels that gate from its
// cancellation callbacks, registered before the process is created, and the
// gate takes one lock around both the cancel and the resume decision with
// ResumeThread. So a cancel either lands first and the engine is ended still
// suspended, or lands after ResumeThread and the caller's own kill path ends
// it; there is no moment between the last check and the resume where a cancel
// is missed (lucy, hq-plct.17 review). If Windows refuses the attribute (before Windows 10 1607) or
// the process is somehow not in the job, it is ended while still suspended
// and the caller gets an error: nothing ever runs outside a job (fail closed).
//
// Its standard handles are three anonymous pipes. Only those three handles are
// inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so an engine started at the
// same moment for another drag can never hold this one's pipes open. Standard
// input is a pipe whose writing end is closed at once: the engine reads EOF.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Peel.Hooks;

namespace Peel.Jobs;

internal sealed class ContainedProcess : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly AnonymousPipeServerStream _stdout;
    private readonly AnonymousPipeServerStream _stderr;

    private ContainedProcess(SafeProcessHandle process, int id, AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr)
    {
        _process = process;
        Id = id;
        _stdout = stdout;
        _stderr = stderr;
    }

    internal int Id { get; }

    internal Stream StandardOutput => _stdout;

    internal Stream StandardError => _stderr;

    /// <summary>
    /// Create the process suspended inside <paramref name="job"/>, run
    /// <paramref name="beforeResume"/> (which may refuse by throwing, as a
    /// cancelled call does), then resume it through <paramref name="gate"/>,
    /// which refuses with OperationCanceledException once cancelled. Throws
    /// Win32Exception or the refusal; in every failure the process has been
    /// ended without running.
    /// </summary>
    internal static ContainedProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        ProcessJob job,
        Action<int>? afterCreate = null,
        Action? beforeResume = null,
        ResumeGate? gate = null)
    {
        gate ??= new ResumeGate();
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var handles = new[]
        {
            stdin.ClientSafePipeHandle.DangerousGetHandle(),
            stdout.ClientSafePipeHandle.DangerousGetHandle(),
            stderr.ClientSafePipeHandle.DangerousGetHandle(),
        };
        // UpdateProcThreadAttribute keeps a pointer to the value, not a copy,
        // so the job handle lives in pinned memory until the list is deleted.
        var jobs = new[] { job.Handle };
        var pinned = default(GCHandle);
        var pinnedJobs = default(GCHandle);
        var attributes = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var info = default(Native.PROCESS_INFORMATION);
        var created = false;
        try
        {
            // Both inside the try, so a failure of either frees the other and
            // the pipes (lucy, hq-plct.17).
            pinned = GCHandle.Alloc(handles, GCHandleType.Pinned);
            pinnedJobs = GCHandle.Alloc(jobs, GCHandleType.Pinned);
            if (jobs[0] == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The job that ends the engine with Peel is already closed, so Peel did not run the engine.");
            }
            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
            {
                Marshal.FreeHGlobal(attributes);
                attributes = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot prepare the engine's start attributes");
            }
            if (!Native.UpdateProcThreadAttribute(attributes, 0, Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    pinned.AddrOfPinnedObject(), new IntPtr(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot limit the handles the engine inherits");
            }
            if (!Native.UpdateProcThreadAttribute(attributes, 0, Native.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                    pinnedJobs.AddrOfPinnedObject(), new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
            {
                // Older than Windows 10 1607: the engine could not be born in
                // its job, so it is not started at all.
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows cannot start the engine inside the job that ends it with Peel, so Peel did not run it");
            }

            var startup = new Native.STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            startup.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = handles[0];
            startup.StartupInfo.hStdOutput = handles[1];
            startup.StartupInfo.hStdError = handles[2];
            startup.lpAttributeList = attributes;

            environmentBlock = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            var commandLine = new StringBuilder(CommandLine(fileName, arguments));
            if (!Native.CreateProcess(fileName, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    Native.CREATE_SUSPENDED | Native.CREATE_UNICODE_ENVIRONMENT | Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_NO_WINDOW,
                    environmentBlock, null, ref startup, out info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            created = true;

            if (!job.Contains(info.hProcess))
            {
                Log.Write($"engine pid {info.dwProcessId} was created outside its job");
                throw new InvalidOperationException(
                    "Windows did not put the engine in the job that ends it with Peel, so Peel did not run it.");
            }
            afterCreate?.Invoke(info.dwProcessId);
            beforeResume?.Invoke();
            gate.Resume(info.hThread);

            Native.CloseHandle(info.hThread);
            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            // Closing the writing end of standard input: the engine reads EOF.
            stdin.Dispose();
            return new ContainedProcess(new SafeProcessHandle(info.hProcess, ownsHandle: true), info.dwProcessId, stdout, stderr);
        }
        catch
        {
            if (created)
            {
                // Still suspended: it has not run, and now it never will.
                Native.TerminateProcess(info.hProcess, 1);
                Native.CloseHandle(info.hThread);
                Native.CloseHandle(info.hProcess);
            }
            stdin.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
            if (pinned.IsAllocated)
            {
                pinned.Free();
            }
            if (pinnedJobs.IsAllocated)
            {
                pinnedJobs.Free();
            }
        }
    }

    /// <summary>Completes when the process has exited.</summary>
    internal async Task WaitForExitAsync(CancellationToken token)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handle = new ExitHandle(_process);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            handle, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            using (token.Register(() => exited.TrySetCanceled(token)))
            {
                await exited.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    internal int ExitCode => Native.GetExitCodeProcess(_process.DangerousGetHandle(), out var code)
        ? unchecked((int)code)
        : -1;

    internal void Kill()
    {
        if (!_process.IsClosed)
        {
            Native.TerminateProcess(_process.DangerousGetHandle(), 1);
        }
    }

    /// <summary>Close our ends of the pipes, so a blocked reader returns.</summary>
    internal void ClosePipes()
    {
        _stdout.Dispose();
        _stderr.Dispose();
    }

    public void Dispose()
    {
        ClosePipes();
        _process.Dispose();
    }

    /// <summary>
    /// A Windows command line that the C runtime (and so Python) splits back
    /// into exactly <paramref name="arguments"/>: the rules .NET's
    /// ProcessStartInfo.ArgumentList uses. The executable is quoted as is,
    /// since a path cannot hold a quote.
    /// </summary>
    internal static string CommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        line.Append('"').Append(fileName).Append('"');
        foreach (var argument in arguments)
        {
            line.Append(' ');
            AppendArgument(line, argument);
        }
        return line.ToString();
    }

    private static void AppendArgument(StringBuilder line, string argument)
    {
        if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            line.Append(argument);
            return;
        }
        line.Append('"');
        var i = 0;
        while (i < argument.Length)
        {
            var c = argument[i++];
            if (c == '\\')
            {
                var slashes = 1;
                while (i < argument.Length && argument[i] == '\\')
                {
                    i++;
                    slashes++;
                }
                if (i == argument.Length)
                {
                    // Doubled, so the closing quote stays a quote.
                    line.Append('\\', slashes * 2);
                }
                else if (argument[i] == '"')
                {
                    line.Append('\\', slashes * 2 + 1).Append('"');
                    i++;
                }
                else
                {
                    line.Append('\\', slashes);
                }
                continue;
            }
            if (c == '"')
            {
                line.Append('\\').Append('"');
                continue;
            }
            line.Append(c);
        }
        line.Append('"');
    }

    /// <summary>
    /// This process's environment with <paramref name="overrides"/> applied,
    /// as the sorted, double NUL terminated block CreateProcessW expects.
    /// </summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
        {
            if (pair.Key is string name && name.Length > 0 && name[0] != '=' && pair.Value is string value)
            {
                variables[name] = value;
            }
        }
        foreach (var pair in overrides)
        {
            variables[pair.Key] = pair.Value;
        }
        var block = new StringBuilder();
        foreach (var pair in variables)
        {
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }
        block.Append('\0');
        return block.ToString();
    }

    private sealed class ExitHandle : WaitHandle
    {
        internal ExitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}

/// <summary>
/// One lock around cancelling a suspended engine and resuming it. Cancel and
/// Resume never overlap: a Cancel before the resume makes Resume refuse, and a
/// Cancel after it changes nothing here (the caller's kill path, watching the
/// same cancellation, ends the running engine). Cancel never waits for more
/// than a ResumeThread call, so it is safe in a cancellation callback.
/// </summary>
internal sealed class ResumeGate
{
    private readonly object _lock = new();
    private bool _cancelled;
    private bool _resumed;

    /// <summary>True when this stopped the resume; false when it had already happened.</summary>
    internal bool Cancel()
    {
        lock (_lock)
        {
            if (_resumed)
            {
                return false;
            }
            _cancelled = true;
            return true;
        }
    }

    internal void Resume(IntPtr thread)
    {
        lock (_lock)
        {
            if (_cancelled)
            {
                throw new OperationCanceledException("The engine was cancelled before it was resumed.");
            }
            if (Native.ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot resume the engine");
            }
            _resumed = true;
        }
    }
}
