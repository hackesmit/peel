// The global hotkey that opens the wheel without a drag.
//
// RegisterHotKey needs a window with a message loop; the UI thread has one, so
// a message-only HwndSource owned here receives WM_HOTKEY and raises Pressed on
// the UI thread. The default chord is Ctrl+Alt+Enter with MOD_NOREPEAT, so
// holding it fires once, not a stream. Remapping the chord is a later bead
// (settings); the default lives here and PEEL_HOTKEY=off lets a test machine or
// a user turn it off entirely.
using System;
using System.Windows.Interop;
using Peel.Hooks;

namespace Peel.Keyboard;

internal sealed class HotkeyListener : IDisposable
{
    // HWND_MESSAGE: a message-only window, never shown, never a taskbar entry.
    private static readonly IntPtr HwndMessage = new(-3);
    private const int HotkeyId = 0x7E51; // "PEEL" is taken; any app-unique id does.

    private HwndSource? _source;
    private bool _registered;

    /// <summary>Raised on the UI thread when the chord is pressed.</summary>
    internal event Action? Pressed;

    internal bool Active => _registered;

    /// <summary>PEEL_HOTKEY=off turned the hotkey off on purpose; nothing to tell the user.</summary>
    internal bool DisabledByEnvironment { get; private set; }

    /// <summary>The message the tray shows when the chord could not be registered.</summary>
    internal const string ConflictMessage =
        "Peel could not use Ctrl+Alt+Enter because another app uses it. Drag files to open the wheel.";

    /// <summary>
    /// Register the default chord. Returns false (and logs) when it cannot,
    /// most often because another app already owns Ctrl+Alt+Enter; Peel then
    /// runs without the hotkey rather than failing to start.
    /// </summary>
    internal bool Register()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PEEL_HOTKEY"), "off", StringComparison.OrdinalIgnoreCase))
        {
            Log.Write("PEEL_HOTKEY=off, the keyboard hotkey is disabled");
            DisabledByEnvironment = true;
            return false;
        }
        try
        {
            var parameters = new HwndSourceParameters("PeelHotkey")
            {
                ParentWindow = HwndMessage,
                WindowStyle = 0,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            var modifiers = KeyboardNative.MOD_CONTROL | KeyboardNative.MOD_ALT | KeyboardNative.MOD_NOREPEAT;
            _registered = KeyboardNative.RegisterHotKey(_source.Handle, HotkeyId, modifiers, KeyboardNative.VK_RETURN);
            if (!_registered)
            {
                Log.Write("could not register the Ctrl+Alt+Enter hotkey (another app may own it); Peel runs without it");
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
                return false;
            }
            Log.Write("keyboard hotkey registered: Ctrl+Alt+Enter");
            return true;
        }
        catch (Exception error)
        {
            Log.Write("registering the keyboard hotkey failed", error);
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == KeyboardNative.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                Pressed?.Invoke();
            }
            catch (Exception error)
            {
                Log.Write("the hotkey handler threw", error);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            if (_registered)
            {
                KeyboardNative.UnregisterHotKey(_source.Handle, HotkeyId);
                _registered = false;
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
