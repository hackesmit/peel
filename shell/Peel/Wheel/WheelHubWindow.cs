// The drag wheel's hub, drawn in a window of its own over the hole.
//
// The drag wheel's hub has to be a real hole (see WheelWindow): a drop there
// must reach the window underneath, and a window region clips drawing as well
// as hit testing, so the wheel itself cannot paint its hub. This window sits
// exactly over the hole and paints it: plate, light disc, and the pill naming
// the petal being aimed at.
//
// It must never be a drop target or catch the pointer, or the hole stops being
// one. A layered window (AllowsTransparency) with WS_EX_TRANSPARENT is skipped
// by WindowFromPoint, which is how both the mouse and OLE pick the window under
// the pointer, so a drop in the hub still lands on what is below. FallsThrough
// proves it on every showing and the wheel logs the answer, which is what the
// e2e drag test checks.
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Peel.Hooks;

namespace Peel.Wheel;

internal sealed class WheelHubWindow : Window
{
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint GA_ROOT = 2;

    // A little wider than the hole so the plate it paints overlaps the
    // wheel's plate and no seam shows at the region's hard edge.
    private const double Overlap = 2;

    private readonly HubFace _face;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly IntPtr _handle;

    internal WheelHubWindow(IntPtr owner)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        AllowDrop = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Peel wheel hub";
        Width = Side;
        Height = Side;
        _face = new HubFace { RenderTransform = _scale, RenderTransformOrigin = new Point(0.5, 0.5) };
        Content = new Grid { Children = { _face } };

        // Owned by the wheel, so it always stays above it. Set on the handle:
        // Window.Owner refuses a window that has never been shown.
        _handle = new WindowInteropHelper(this) { Owner = owner }.EnsureHandle();
        var style = Native.GetWindowStyleEx(_handle);
        Native.SetWindowStyleEx(_handle, style | WS_EX_TRANSPARENT | WS_EX_LAYERED
            | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
    }

    /// <summary>Side of the window in device independent pixels.</summary>
    internal static double Side => 2 * (WheelGeometry.Default.InnerRadius + Overlap);

    internal IntPtr Handle => _handle;

    internal void SetLabel(string label)
    {
        if (_face.Label == label)
        {
            return;
        }
        _face.Label = label;
        _face.InvalidateVisual();
    }

    /// <summary>Show centred on a screen point (physical pixels) at the given scale.</summary>
    internal void ShowAt(int screenX, int screenY, double scale)
    {
        var side = (int)Math.Round(Side * scale);
        var left = screenX - (side / 2);
        var top = screenY - (side / 2);
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, left, top, side, side, Native.SWP_NOACTIVATE);
        if (!IsVisible)
        {
            Show();
        }
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, left, top, side, side,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    internal void PlayOpen() => WheelRenderer.Animate(_face, _scale, 0.86, 0, 1, 1, WheelRenderer.OpenTime, null);

    internal void PlayClose() => WheelRenderer.Animate(_face, _scale, 1, 1, 0.94, 0, WheelRenderer.CloseTime, null);

    internal void Settle() => WheelRenderer.Animate(_face, _scale, 1, 1, 1, 1, TimeSpan.Zero, null);

    /// <summary>
    /// True when the window under a screen point is neither this one nor
    /// <paramref name="wheel"/>: a drop there goes to whatever is below.
    /// </summary>
    internal bool FallsThrough(int screenX, int screenY, IntPtr wheel)
    {
        var hit = WindowFromPoint(new POINT { X = screenX, Y = screenY });
        var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
        return root != _handle && root != wheel;
    }

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

    private sealed class HubFace : FrameworkElement
    {
        internal string Label { get; set; } = string.Empty;

        protected override void OnRender(DrawingContext context)
        {
            var wheel = WheelGeometry.Default;
            var centre = new Point(Side / 2, Side / 2);
            // Plate first, out to the window's edge, so the ring of plate
            // between the hub and the petals continues the wheel's.
            context.DrawEllipse(WheelTheme.Plate, null, centre, Side / 2, Side / 2);
            WheelHub.Draw(context, centre, wheel, Label, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
    }
}
