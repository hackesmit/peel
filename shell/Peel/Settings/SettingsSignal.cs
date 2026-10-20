// Starting Peel.exe while it already runs opens the running Peel's settings
// window. Peel has no main window and no Start menu page, so this is how a
// second double click (or `Peel.exe --settings`) does something useful
// instead of nothing.
//
// The second process sets a named event in its own session and exits; the
// first waits on it from the thread pool and shows the window on the UI
// thread. The second process is the one the user just started, so it holds
// the foreground right and hands it over before signalling.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Peel.Settings;

internal sealed class SettingsSignal : IDisposable
{
    private const string Name = @"Local\PeelShowSettings";
    private const int AsfwAny = -1;

    private readonly EventWaitHandle _event;
    private RegisteredWaitHandle? _wait;

    private SettingsSignal(EventWaitHandle handle) => _event = handle;

    /// <summary>The running Peel: call <paramref name="show"/> whenever another start asks.</summary>
    internal static SettingsSignal Listen(Action show)
    {
        var signal = new SettingsSignal(new EventWaitHandle(false, EventResetMode.AutoReset, Name));
        signal._wait = ThreadPool.RegisterWaitForSingleObject(
            signal._event,
            (_, _) =>
            {
                try
                {
                    show();
                }
                catch (Exception error)
                {
                    Log.Write("showing the settings on request failed", error);
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return signal;
    }

    /// <summary>The second start: ask the running Peel to show its settings.</summary>
    internal static bool Ask()
    {
        try
        {
            AllowSetForegroundWindow(AsfwAny);
            if (EventWaitHandle.TryOpenExisting(Name, out var handle))
            {
                using (handle)
                {
                    return handle.Set();
                }
            }
        }
        catch (Exception error)
        {
            Log.Write("asking the running Peel for its settings failed", error);
        }
        return false;
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
