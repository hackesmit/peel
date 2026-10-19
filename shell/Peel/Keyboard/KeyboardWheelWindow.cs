// The overlay the keyboard path shows: the same wheel the drag path draws, but
// driven by the keyboard instead of a drag.
//
// It is deliberately NOT the drag wheel (Wheel/WheelWindow). That window is an
// OLE drop target whose whole lifecycle assumes a drag in progress: the hub is
// a hole so a drop there falls through, StuckOverlayWatch hides it the moment
// the physical mouse button is up, and ArrivalWatch hides it when no OLE drag
// reaches it. Every one of those is correct for a drag and fatal for a wheel
// that opens with no button down and no drag. So this is its own window, but it
// reuses the exact same WheelRenderer, geometry and theme, so the two wheels
// cannot drift apart visually.
//
// Unlike the drag wheel this one takes focus: it is how the arrow keys, Enter
// and Escape reach it. Losing focus (a click elsewhere, Alt+Tab) means the user
// looked away, so the wheel dismisses itself, exactly as releasing a drag off
// the ring does. A short grace window after it opens absorbs the one transient
// deactivation that activating the window can itself cause.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Peel.Hooks;
using Peel.Wheel;

namespace Peel.Keyboard;

internal sealed class KeyboardWheelWindow : Window
{
    private readonly WheelRenderer _renderer = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IntPtr _handle;
    private long _readyAtMs;

    internal KeyboardWheelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Peel keyboard wheel";
        Width = WheelGeometry.Default.Size;
        Height = WheelGeometry.Default.Size;

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_renderer);
        Content = root;

        _handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Native.GetWindowStyleEx(_handle);
        // A tool window, topmost, kept off the taskbar; NOT WS_EX_NOACTIVATE,
        // because this window has to be able to take the keyboard.
        Native.SetWindowStyleEx(_handle, style | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);

        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += OnDeactivated;
    }

    /// <summary>A navigation key was pressed while the wheel had focus.</summary>
    internal event Action<NavKey>? KeyPressed;

    /// <summary>The wheel lost focus after it had settled: treat as a cancel.</summary>
    internal event Action? Dismissed;

    internal WheelPlan Plan => _renderer.Plan;

    internal void SetPlan(WheelPlan plan, Brush accent) => _renderer.SetPlan(plan, accent);

    internal void SetHot(int index) => _renderer.SetHot(index);

    /// <summary>
    /// Show the wheel centred on a screen point (physical pixels) and take the
    /// keyboard. The window is sized and positioned before it is shown so it
    /// never flashes at its previous place.
    /// </summary>
    internal void ShowAt(int screenX, int screenY)
    {
        var scale = Native.ScaleForPoint(screenX, screenY);
        var side = (int)Math.Round(WheelGeometry.Default.Size * scale);
        var left = screenX - (side / 2);
        var top = screenY - (side / 2);
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, left, top, side, side, Native.SWP_NOACTIVATE);
        SetDisc(side);
        if (!IsVisible)
        {
            Show();
        }
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, left, top, side, side, Native.SWP_SHOWWINDOW);
        Activate();
        Focus();
        // The activation above can fire one Deactivated on the window we took
        // focus from bouncing back; ignore deactivations for a moment so that
        // transient does not close the wheel the instant it opens.
        _readyAtMs = _clock.ElapsedMilliseconds + 250;
        Log.Write($"keyboard wheel shown at {screenX},{screenY} ({side}px, scale {scale:0.##})");
    }

    internal void HideNow(string reason)
    {
        if (!IsVisible)
        {
            return;
        }
        Log.Write($"keyboard wheel hidden ({reason})");
        Hide();
        Native.SetWindowRgn(_handle, IntPtr.Zero, false);
        _renderer.SetHot(-1);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = Map(e);
        if (key == NavKey.Other)
        {
            return;
        }
        // Arrow keys repeat so a held key walks the ring; the rest act once per
        // physical press, so a held hotkey chord cannot auto-run a segment or
        // flip wheels under the user's hand.
        var repeatable = key is NavKey.Left or NavKey.Right or NavKey.Up or NavKey.Down;
        if (e.IsRepeat && !repeatable)
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        KeyPressed?.Invoke(key);
    }

    private static NavKey Map(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key switch
        {
            Key.Left => NavKey.Left,
            Key.Right => NavKey.Right,
            Key.Up => NavKey.Up,
            Key.Down => NavKey.Down,
            Key.Enter => NavKey.Enter, // Key.Return is the same value
            Key.Escape => NavKey.Escape,
            Key.LeftShift or Key.RightShift => NavKey.Shift,
            Key.LeftCtrl or Key.RightCtrl => NavKey.Ctrl,
            Key.LeftAlt or Key.RightAlt => NavKey.Alt,
            _ => NavKey.Other,
        };
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!IsVisible || _clock.ElapsedMilliseconds < _readyAtMs)
        {
            return;
        }
        Dismissed?.Invoke();
    }

    /// <summary>Clip the window to the ring's disc so it is no bigger than the wheel.</summary>
    private void SetDisc(int side)
    {
        if (side <= 0)
        {
            return;
        }
        var scale = side / WheelGeometry.Default.Size;
        var centre = side / 2.0;
        var outer = WheelGeometry.Default.OuterRadius * scale;
        var region = Native.CreateEllipticRgn(
            (int)Math.Round(centre - outer), (int)Math.Round(centre - outer),
            (int)Math.Round(centre + outer), (int)Math.Round(centre + outer));
        if (Native.SetWindowRgn(_handle, region, true) == 0)
        {
            Native.DeleteObject(region);
        }
    }
}
