// Who gets the foreground back when the keyboard wheel closes (hq-i9v0.2).
//
// The keyboard wheel takes the foreground to read the arrow keys. When it
// closes it fades out first, and a faded wheel that still held the foreground
// would swallow whatever the user types next and make a hotkey press during
// the fade read the wheel instead of Explorer. So the foreground goes back the
// moment the close begins, not when the fade ends.
//
// It goes to the window that was in front when the wheel opened; if that one
// is gone or hidden, to the top level window under the wheel's centre (the
// closing wheel is click-through by then, so WindowFromPoint looks past it).
// It goes back only while the wheel still holds the foreground: a wheel that
// closes because the user clicked elsewhere must not steal it back.
//
// The desktop calls are delegates so the unit tests can stand in for Windows.
using System;
using System.Runtime.InteropServices;

namespace Peel.Keyboard;

internal sealed class KeyboardFocusReturn
{
    private readonly IntPtr _wheel;
    private IntPtr _before;

    internal KeyboardFocusReturn(IntPtr wheel)
    {
        _wheel = wheel;
    }

    /// <summary>The desktop this works against; the tests replace it.</summary>
    internal Desktop Windows { get; set; } = Desktop.Real;

    /// <summary>The window that was in front when the wheel last opened.</summary>
    internal IntPtr Before => _before;

    /// <summary>The wheel is opening: remember who is in front, unless it is the wheel itself.</summary>
    internal void Opening()
    {
        var foreground = Windows.Foreground();
        if (foreground != _wheel && foreground != IntPtr.Zero)
        {
            _before = foreground;
        }
    }

    /// <summary>
    /// The wheel is closing: hand the foreground back if the wheel still has
    /// it. Returns the window it went to, or zero when nothing was handed back.
    /// </summary>
    internal IntPtr Closing(int centreX, int centreY)
    {
        if (Windows.Foreground() != _wheel)
        {
            return IntPtr.Zero;
        }
        var target = Windows.Usable(_before) ? _before : Windows.RootAt(centreX, centreY);
        if (target == IntPtr.Zero || target == _wheel || !Windows.Usable(target))
        {
            return IntPtr.Zero;
        }
        return Windows.Activate(target) ? target : IntPtr.Zero;
    }

    internal sealed record Desktop(
        Func<IntPtr> Foreground,
        Func<IntPtr, bool> Usable,
        Func<int, int, IntPtr> RootAt,
        Func<IntPtr, bool> Activate)
    {
        internal static readonly Desktop Real = new(
            KeyboardNative.GetForegroundWindow,
            hwnd => hwnd != IntPtr.Zero && KeyboardNative.IsWindow(hwnd) && KeyboardNative.IsWindowVisible(hwnd),
            (x, y) =>
            {
                var hit = WindowFromPoint(new POINT { X = x, Y = y });
                return hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
            },
            KeyboardNative.SetForegroundWindow);
    }

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
