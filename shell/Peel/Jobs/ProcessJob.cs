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
// The process is created inside the job (PROC_THREAD_ATTRIBUTE_JOB_LIST) and
// suspended (ContainedProcess), so there is no moment it exists outside the
// job and every child it ever starts is in the job too. If Windows refuses the
// job or the attribute, the engine is not run at all.
using System;
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

    /// <summary>
    /// The job handle, for PROC_THREAD_ATTRIBUTE_JOB_LIST; IntPtr.Zero once
    /// disposed. Only valid until Dispose, which the caller orders after the
    /// process creation that uses it.
    /// </summary>
    internal IntPtr Handle => _handle;

    /// <summary>True if <paramref name="process"/> is in this job; false if not, or if Windows cannot say.</summary>
    internal bool Contains(IntPtr process)
    {
        var handle = _handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        if (Native.IsProcessInJob(process, handle, out var inJob))
        {
            return inJob;
        }
        Log.Write($"cannot ask whether a process is in its job, error {Marshal.GetLastWin32Error()}");
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
