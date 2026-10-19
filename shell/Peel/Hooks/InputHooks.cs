// WH_MOUSE_LL plus WH_KEYBOARD_LL, installed on a thread of their own.
//
// Windows delivers a low level hook on the thread that installed it, and gives
// that thread a few hundred milliseconds to answer before it starts dropping
// the hook. The UI thread of a WPF app paints, runs layout and waits on the
// engine, so the hooks live on a dedicated thread with its own dispatcher
// instead: a slow repaint of the wheel can then never cost the user an input
// event. Handlers of these events run on that hook thread and must do pure
// state work only, never file or UI work.
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace Peel.Hooks;

internal readonly record struct MouseHookEvent(int Message, int X, int Y);

internal readonly record struct KeyHookEvent(int Message, int VkCode);

internal sealed class InputHooks : IDisposable
{
    private readonly Native.HookProc _mouseProc;
    private readonly Native.HookProc _keyProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private long _worstTicks;

    internal InputHooks()
    {
        // Held in fields: a collected delegate means Windows calls freed memory.
        _mouseProc = OnMouse;
        _keyProc = OnKey;
    }

    /// <summary>Raised on the hook thread for mouse moves and left button changes.</summary>
    internal event Action<MouseHookEvent>? Mouse;

    /// <summary>Raised on the hook thread for key down and key up.</summary>
    internal event Action<KeyHookEvent>? Key;

    internal bool Installed => _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero;

    /// <summary>Worst time one hook callback has taken, in milliseconds.</summary>
    internal double WorstCallMs => Interlocked.Read(ref _worstTicks) * 1000.0 / Stopwatch.Frequency;

    internal void Start()
    {
        if (_thread is not null)
        {
            return;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "peel-hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        Log.Write($"hooks installed mouse={_mouseHook != IntPtr.Zero} keyboard={_keyHook != IntPtr.Zero}");
    }

    private void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        var module = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, module, 0);
        _ready.Set();
        Dispatcher.Run();
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (code >= 0 && Mouse is { } handler)
            {
                var message = (int)wParam;
                if (message is Native.WM_MOUSEMOVE or Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP)
                {
                    var data = System.Runtime.InteropServices.Marshal
                        .PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    handler(new MouseHookEvent(message, data.X, data.Y));
                }
            }
        }
        catch (Exception error)
        {
            Log.Write("mouse hook handler failed", error);
        }
        Record(started);
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (code >= 0 && Key is { } handler)
            {
                var data = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                handler(new KeyHookEvent((int)wParam, data.VkCode));
            }
        }
        catch (Exception error)
        {
            Log.Write("key hook handler failed", error);
        }
        Record(started);
        // Peel never swallows a key. The modifier still reaches Explorer, so a
        // cancelled drag keeps whatever copy or move meaning the user chose.
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Record(long started)
    {
        var elapsed = Stopwatch.GetTimestamp() - started;
        if (elapsed > Interlocked.Read(ref _worstTicks))
        {
            Interlocked.Exchange(ref _worstTicks, elapsed);
        }
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }
        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        _ready.Dispose();
    }
}
