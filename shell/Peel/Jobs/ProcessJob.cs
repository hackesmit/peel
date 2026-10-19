// A Windows job object around one engine process and everything it starts.
//
// The job is created with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, and its handle
// is not inheritable, so the only handle is Peel's. Three ways the engine can
// outlive its reason to run, and what ends it in each:
//   * the run times out or is cancelled: Terminate() ends the whole tree,
//     including a backend that outlived the engine and still holds its pipes;
//   * Peel quits: EngineClient terminates every live job;
//   * Peel is killed or crashes: Windows closes the handle and the kill on
//     close limit ends the tree.
//
// The process is assigned right after Process.Start returns. Children created
// after that are in the job automatically. The window before it is the time
// Python needs to import the engine, far longer than one system call, so an
// engine cannot start a backend outside the job in practice.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Peel.Hooks;

namespace Peel.Jobs;

internal sealed class ProcessJob : IDisposable
{
    private IntPtr _handle;

    private ProcessJob(IntPtr handle) => _handle = handle;

    /// <summary>A new kill on close job, or null if Windows refused one.</summary>
    internal static ProcessJob? Create()
    {
        var handle = Native.CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            Log.Write($"cannot create a job object, error {Marshal.GetLastWin32Error()}");
            return null;
        }
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Native.SetInformationJobObject(handle, Native.JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            Log.Write($"cannot set kill on close on a job object, error {Marshal.GetLastWin32Error()}");
            Native.CloseHandle(handle);
            return null;
        }
        return new ProcessJob(handle);
    }

    /// <summary>Put a started process in the job. False if Windows refused.</summary>
    internal bool Assign(Process process)
    {
        var handle = _handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            if (Native.AssignProcessToJobObject(handle, process.Handle))
            {
                return true;
            }
            Log.Write($"cannot put engine pid {process.Id} in its job, error {Marshal.GetLastWin32Error()}");
        }
        catch (Exception error)
        {
            Log.Write("cannot put the engine in its job", error);
        }
        return false;
    }

    /// <summary>End every process in the job now.</summary>
    internal void Terminate()
    {
        var handle = _handle;
        if (handle != IntPtr.Zero && !Native.TerminateJobObject(handle, 1))
        {
            Log.Write($"cannot terminate an engine job, error {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>Closing the last handle kills whatever is still in the job.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            Native.CloseHandle(handle);
        }
    }
}
