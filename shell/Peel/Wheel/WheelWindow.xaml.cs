// The overlay: a topmost, never activated, per pixel transparent window that
// is also an OLE drop target.
//
// Two things here are load bearing and were proven by the spike in
// spikes/drag-overlay before any of this was written:
//
//   * The window must not be hidden the moment the hook sees the button come
//     up. OLE delivers DragDrop after that, and hiding first turns the drop
//     into a DragLeave, so the user's drop is lost. HideAfter waits.
//   * The hub is a real hole, cut with SetWindowRgn, not a transparent pixel.
//     Windows resolves a drop target with WindowFromPoint, which respects the
//     region, so dropping in the middle reaches the window below and the
//     normal Explorer drop happens, which is what "cancel" has to mean.
//
// The hole is cut only once the drag data has arrived. At the moment the wheel
// opens the pointer is at the centre, so a window with a hole would never be
// offered the data and the wheel would have nothing to show.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Peel.Hooks;

namespace Peel.Wheel;

internal partial class WheelWindow : Window
{
    private readonly WheelRenderer _renderer = new();
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _holeTimer;
    private readonly DispatcherTimer _dwellTimer;
    private IntPtr _handle;
    private bool _holeCut;
    private string[] _files = Array.Empty<string>();

    internal WheelWindow()
    {
        InitializeComponent();
        Width = WheelGeometry.Default.Size;
        Height = WheelGeometry.Default.Size;
        Root.Children.Add(_renderer);

        _hideTimer = Timer(TimeSpan.FromMilliseconds(500), () => HideNow("timer"));
        // If the drag source never offers us data (an elevated window, or a
        // source with no file format), cut the hole anyway so the user can
        // still drop through the middle onto whatever is below.
        _holeTimer = Timer(TimeSpan.FromMilliseconds(450), CutHole);
        _dwellTimer = Timer(TimeSpan.FromMilliseconds(650), OnDwell);

        _handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Native.GetWindowStyleEx(_handle);
        Native.SetWindowStyleEx(_handle, style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
    }

    /// <summary>The paths the drag is carrying, as soon as OLE offers them.</summary>
    internal event Action<string[]>? FilesSeen;

    /// <summary>A drop landed on a segment.</summary>
    internal event Action<WheelSegment, string[]>? Dropped;

    /// <summary>The pointer rested on the More segment: turn the page.</summary>
    internal event Action? MoreRequested;

    internal WheelPlan Plan => _renderer.Plan;

    internal IReadOnlyList<string> Files => _files;

    internal void SetPlan(WheelPlan plan, Brush accent)
    {
        _dwellTimer.Stop();
        _renderer.SetPlan(plan, accent);
    }

    /// <summary>
    /// Show the wheel centred on a screen point (physical pixels, as the hook
    /// reports them). The window is positioned before it is shown so it never
    /// appears at its previous place first.
    /// </summary>
    internal void ShowAt(int screenX, int screenY)
    {
        _hideTimer.Stop();
        var scale = Native.ScaleForPoint(screenX, screenY);
        var side = (int)Math.Round(WheelGeometry.Default.Size * scale);
        var left = screenX - (side / 2);
        var top = screenY - (side / 2);
        ClearHole();
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, left, top, side, side, Native.SWP_NOACTIVATE);
        if (!IsVisible)
        {
            Show();
        }
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, left, top, side, side,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _holeTimer.Stop();
        _holeTimer.Start();
        Log.Write($"wheel shown at {screenX},{screenY} ({side}px, scale {scale:0.##})");
    }

    /// <summary>
    /// Warm the composition path once at start up, offscreen, so the first
    /// real drag does not pay for it.
    /// </summary>
    internal void Warm()
    {
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, -30000, -30000, 10, 10, Native.SWP_NOACTIVATE);
        Show();
        Hide();
    }

    internal void HideAfter(TimeSpan delay)
    {
        if (!IsVisible)
        {
            return;
        }
        _hideTimer.Stop();
        _hideTimer.Interval = delay;
        _hideTimer.Start();
    }

    internal void HideNow(string reason)
    {
        _hideTimer.Stop();
        _holeTimer.Stop();
        _dwellTimer.Stop();
        if (!IsVisible)
        {
            return;
        }
        Log.Write($"wheel hidden ({reason})");
        Hide();
        ClearHole();
        _files = Array.Empty<string>();
        _renderer.SetHot(-1);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        TakeFiles(e);
        Track(e);
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_files.Length == 0)
        {
            TakeFiles(e);
        }
        Track(e);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dwellTimer.Stop();
        _renderer.SetHot(-1);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        TakeFiles(e);
        var index = SegmentAt(e);
        var plan = _renderer.Plan;
        var segment = index >= 0 && index < plan.Count ? plan.Segments[index] : null;
        Log.Write($"drop on {(segment is null ? "nothing" : segment.Label)} with {_files.Length} file(s)");
        e.Effects = segment is { Runnable: true } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (segment is not null)
        {
            Dropped?.Invoke(segment, _files);
        }
        HideNow("dropped");
    }

    private void TakeFiles(DragEventArgs e)
    {
        if (_files.Length > 0)
        {
            return;
        }
        string[]? paths = null;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            }
        }
        catch (Exception error)
        {
            // A source can refuse to render a format it advertised.
            Log.Write("cannot read the drag data", error);
        }
        if (paths is null || paths.Length == 0)
        {
            return;
        }
        _files = paths;
        Log.Write($"drag carries {paths.Length} file(s): {string.Join("|", paths.Take(5))}");
        CutHole();
        FilesSeen?.Invoke(paths);
    }

    private void Track(DragEventArgs e)
    {
        var index = SegmentAt(e);
        var plan = _renderer.Plan;
        var segment = index >= 0 && index < plan.Count ? plan.Segments[index] : null;
        e.Effects = segment is { Runnable: true } ? DragDropEffects.Copy : DragDropEffects.None;
        if (index != _renderer.Hot)
        {
            _renderer.SetHot(index);
            _dwellTimer.Stop();
            if (segment is { Kind: SegmentKind.More })
            {
                _dwellTimer.Start();
            }
        }
    }

    private int SegmentAt(DragEventArgs e)
    {
        var point = e.GetPosition(this);
        var centre = WheelGeometry.Default.Centre;
        return _renderer.Metrics.SegmentAt(_renderer.Plan.Count, point.X - centre, point.Y - centre);
    }

    private void OnDwell()
    {
        _dwellTimer.Stop();
        if (IsVisible && _renderer.Plan.Segments.ElementAtOrDefault(_renderer.Hot) is { Kind: SegmentKind.More })
        {
            Log.Write("dwell on More, turning the page");
            MoreRequested?.Invoke();
        }
    }

    /// <summary>
    /// Cut the hub out of the window so a drop there falls through to the
    /// window below. Called once the drag data has arrived.
    /// </summary>
    private void CutHole()
    {
        _holeTimer.Stop();
        if (_holeCut || !IsVisible)
        {
            return;
        }
        if (!Native.GetWindowRect(_handle, out var rect))
        {
            return;
        }
        var side = Math.Min(rect.Width, rect.Height);
        if (side <= 0)
        {
            return;
        }
        var scale = side / WheelGeometry.Default.Size;
        var centre = side / 2.0;
        var outer = WheelGeometry.Default.OuterRadius * scale;
        var inner = WheelGeometry.Default.InnerRadius * scale;
        var ring = Native.CreateEllipticRgn(
            (int)Math.Round(centre - outer), (int)Math.Round(centre - outer),
            (int)Math.Round(centre + outer), (int)Math.Round(centre + outer));
        var hub = Native.CreateEllipticRgn(
            (int)Math.Round(centre - inner), (int)Math.Round(centre - inner),
            (int)Math.Round(centre + inner), (int)Math.Round(centre + inner));
        var donut = Native.CreateRectRgn(0, 0, 1, 1);
        Native.CombineRgn(donut, ring, hub, Native.RGN_DIFF);
        // SetWindowRgn takes ownership of the region it is given.
        var applied = Native.SetWindowRgn(_handle, donut, true) != 0;
        Native.DeleteObject(ring);
        Native.DeleteObject(hub);
        _holeCut = applied;
        Log.Write($"hub hole cut={applied}");
        if (!applied)
        {
            Native.DeleteObject(donut);
        }
    }

    private void ClearHole()
    {
        if (!_holeCut)
        {
            return;
        }
        Native.SetWindowRgn(_handle, IntPtr.Zero, false);
        _holeCut = false;
    }

    private static DispatcherTimer Timer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tick();
        };
        return timer;
    }
}
