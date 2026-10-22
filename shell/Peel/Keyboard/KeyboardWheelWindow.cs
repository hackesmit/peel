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
// and Escape reach it. Losing focus (a click elsewhere, Alt+Tab), never getting
// it, a click outside the disc, or sitting idle means the user looked away, so
// the wheel dismisses itself (KeyboardWheelLifetime decides; a 100 ms timer and
// every Deactivated ask it). Every hide raises Hidden, so the controller resets
// its open state on every close path, not only the ones it started.
//
// Closing fades out the same way the drag wheel does (WheelRenderer.PlayClose,
// with WheelFade's fallback for an animation that never completes). Input ends
// the moment the close begins: the window turns click-through, ignores keys,
// stops its watch and reports itself not visible, and Hidden is raised then,
// not when the fade ends. A wheel shown again during the fade cancels the
// close, so the fade's end cannot hide the wheel that came back.
//
// A tapped Alt switches wheels; on its own, Windows would also turn the Alt
// key up into SC_KEYMENU and put the window in menu mode, which eats the next
// arrow key. The window swallows the Alt key up and SC_KEYMENU while open.
using System;
using System.Windows;
using System.Windows.Threading;
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
    private readonly KeyboardWheelLifetime _lifetime = new(IdleFromEnvironment());
    private readonly DispatcherTimer _watch;
    private readonly DispatcherTimer _fadeTimer;
    private readonly WheelFade _fade = new();
    private int _fadeToken;
    private IntPtr _handle;

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

        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += OnDeactivated;

        _watch = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
        _watch.Tick += (_, _) => Watch();
        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = WheelFade.Fallback };
        _fadeTimer.Tick += (_, _) => FinishClose(_fadeToken);
    }

    /// <summary>A navigation key was pressed while the wheel had focus.</summary>
    internal event Action<NavKey>? KeyPressed;

    /// <summary>The wheel must close on its own (focus lost, click outside, idle); the argument is why.</summary>
    internal event Action<string>? Dismissed;

    /// <summary>The wheel was hidden, by any path; the argument is why.</summary>
    internal event Action<string>? Hidden;

    internal IntPtr Handle => _handle;

    /// <summary>
    /// Visible and not on its way out. Hides Window.IsVisible on purpose: a
    /// wheel fading out has already ended its session.
    /// </summary>
    internal new bool IsVisible => base.IsVisible && !_fade.Closing;

    /// <summary>The wheel is the foreground window, so it holds the keyboard.</summary>
    internal bool IsForeground => IsVisible && KeyboardNative.GetForegroundWindow() == _handle;

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
        CancelClose();
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
        _renderer.PlayOpen();
        _lifetime.Opened(_clock.ElapsedMilliseconds);
        _watch.Start();
        Activate();
        Focus();
        Log.Write($"keyboard wheel shown at {screenX},{screenY} ({side}px, scale {scale:0.##})");
    }

    internal void HideNow(string reason)
    {
        if (!IsVisible)
        {
            return;
        }
        _watch.Stop();
        _lifetime.Closed();
        Log.Write($"keyboard wheel hidden ({reason})");
        // Input ends now, the window goes when the fade ends.
        var token = _fadeToken = _fade.Begin();
        Native.SetWindowStyleEx(_handle, Native.GetWindowStyleEx(_handle) | WheelHubWindow.WS_EX_TRANSPARENT);
        _renderer.SetHot(-1);
        _fadeTimer.Stop();
        _fadeTimer.Start();
        _renderer.PlayClose(() => FinishClose(token));
        Hidden?.Invoke(reason);
    }

    private void FinishClose(int token)
    {
        _fadeTimer.Stop();
        if (!_fade.Finish(token))
        {
            return;
        }
        Hide();
        Native.SetWindowRgn(_handle, IntPtr.Zero, false);
        Native.SetWindowStyleEx(_handle, Native.GetWindowStyleEx(_handle) & ~(long)WheelHubWindow.WS_EX_TRANSPARENT);
        _renderer.Settle();
    }

    /// <summary>A wheel shown again while it fades out comes straight back.</summary>
    private void CancelClose()
    {
        _fadeTimer.Stop();
        if (!_fade.Cancel())
        {
            return;
        }
        Native.SetWindowStyleEx(_handle, Native.GetWindowStyleEx(_handle) & ~(long)WheelHubWindow.WS_EX_TRANSPARENT);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_fade.Closing)
        {
            // A wheel on its way out takes no more keys.
            e.Handled = true;
            return;
        }
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
        _lifetime.Activity(_clock.ElapsedMilliseconds);
        KeyPressed?.Invoke(key);
    }

    private static void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // Alt up would otherwise reach DefWindowProc as SC_KEYMENU (menu mode).
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            e.Handled = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // base: a wheel fading out still keeps Alt out of menu mode.
        if (!base.IsVisible)
        {
            return IntPtr.Zero;
        }
        if (message == KeyboardNative.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == KeyboardNative.SC_KEYMENU)
        {
            handled = true;
        }
        else if (message == KeyboardNative.WM_SYSKEYUP && wParam.ToInt64() == KeyboardNative.VK_MENU)
        {
            handled = true;
        }
        return IntPtr.Zero;
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

    private void OnDeactivated(object? sender, EventArgs e) => Watch();

    /// <summary>Ask the lifetime whether the wheel must close, and close it if so.</summary>
    private void Watch()
    {
        if (!IsVisible)
        {
            _watch.Stop();
            return;
        }
        var reason = _lifetime.Check(_clock.ElapsedMilliseconds, IsForeground, ClickOutside());
        if (reason is null)
        {
            return;
        }
        if (Dismissed is null)
        {
            HideNow(reason);
            return;
        }
        Dismissed.Invoke(reason);
        if (IsVisible)
        {
            HideNow(reason);
        }
    }

    private bool ClickOutside()
    {
        if (!KeyboardNative.AnyMouseButtonDown() || !Native.GetCursorPos(out var point))
        {
            return false;
        }
        if (!Native.GetWindowRect(_handle, out var rect))
        {
            return true;
        }
        // The disc is the window's inscribed circle (SetDisc clips to it).
        var radius = (rect.Right - rect.Left) / 2.0;
        var dx = point.X - ((rect.Left + rect.Right) / 2.0);
        var dy = point.Y - ((rect.Top + rect.Bottom) / 2.0);
        return (dx * dx) + (dy * dy) > radius * radius;
    }

    private static long IdleFromEnvironment() =>
        long.TryParse(Environment.GetEnvironmentVariable("PEEL_KEYBOARD_IDLE_MS"), out var ms) && ms > 0
            ? ms
            : KeyboardWheelLifetime.DefaultIdleMs;

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
