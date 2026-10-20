// The global hotkey that opens the wheel without a drag.
//
// RegisterHotKey needs a window with a message loop; the UI thread has one, so
// a message-only HwndSource owned here receives WM_HOTKEY and raises Pressed on
// the UI thread. The chord comes from the settings (default Ctrl+Alt+Enter),
// always with MOD_NOREPEAT, so holding it fires once, not a stream. A saved
// chord is applied with Rebind; PEEL_HOTKEY=off lets a test machine or a user
// turn it off entirely.
using System;
using System.Windows.Interop;
using Peel.Hooks;
using Peel.Settings;

namespace Peel.Keyboard;

internal sealed class HotkeyListener : IDisposable
{
    // HWND_MESSAGE: a message-only window, never shown, never a taskbar entry.
    private static readonly IntPtr HwndMessage = new(-3);
    private const int HotkeyId = 0x7E51; // "PEEL" is taken; any app-unique id does.

    private HwndSource? _source;
    private bool _registered;
    private HotkeyChord _chord = HotkeyChord.Default;

    /// <summary>Raised on the UI thread when the chord is pressed.</summary>
    internal event Action? Pressed;

    internal bool Active => _registered;

    /// <summary>PEEL_HOTKEY=off turned the hotkey off on purpose; nothing to tell the user.</summary>
    internal bool DisabledByEnvironment { get; private set; }

    /// <summary>The chord registered, or asked for last.</summary>
    internal HotkeyChord Chord => _chord;

    /// <summary>The message the tray shows when the chord could not be registered.</summary>
    internal string ConflictMessage => ConflictFor(_chord);

    internal static string ConflictFor(HotkeyChord chord) =>
        $"Peel could not use {chord} because another app uses it. Drag files to open the wheel, or pick another shortcut in Settings.";

    /// <summary>
    /// Register a chord. Returns false (and logs) when it cannot, most often
    /// because another app already owns it; Peel then runs without the
    /// hotkey rather than failing to start.
    /// </summary>
    internal bool Register(HotkeyChord chord)
    {
        _chord = chord;
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

            _registered = KeyboardNative.RegisterHotKey(_source.Handle, HotkeyId, chord.ModifierFlags, chord.Vk);
            if (!_registered)
            {
                Log.Write($"could not register the {chord} hotkey (another app may own it); Peel runs without it");
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
                return false;
            }
            Log.Write($"keyboard hotkey registered: {chord}");
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

    /// <summary>
    /// Swap to a new chord. The old one is released first (the new chord may
    /// share its keys); if the new one cannot be registered, the old one is
    /// taken back so a failed change never leaves Peel with no hotkey.
    /// </summary>
    internal bool Rebind(HotkeyChord chord)
    {
        if (DisabledByEnvironment)
        {
            return false;
        }
        var previous = _chord;
        var had = _registered;
        Dispose();
        if (Register(chord))
        {
            return true;
        }
        if (had && Register(previous))
        {
            Log.Write($"kept the {previous} hotkey after {chord} failed");
        }
        return false;
    }

    /// <summary>
    /// Let the chord through while the settings window records a new one,
    /// then take it back. Without this, pressing the current chord there
    /// would open the wheel instead of being recorded.
    /// </summary>
    internal void Pause(bool paused)
    {
        if (DisabledByEnvironment || _source is null)
        {
            return;
        }
        if (paused && _registered)
        {
            KeyboardNative.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
        else if (!paused && !_registered)
        {
            _registered = KeyboardNative.RegisterHotKey(_source.Handle, HotkeyId, _chord.ModifierFlags, _chord.Vk);
            if (!_registered)
            {
                Log.Write($"could not take the {_chord} hotkey back after the settings window");
            }
        }
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
