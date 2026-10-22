// The overlay: a topmost, never activated, per pixel transparent window that
// is also an OLE drop target.
//
// Load bearing, proven by the spike in spikes/drag-overlay:
//
//   * The window must not be hidden the moment the hook sees the button come
//     up. OLE delivers DragDrop after that, and hiding first turns the drop
//     into a DragLeave, so the user's drop is lost. HideAfter waits.
//   * The hub is a real hole, cut with SetWindowRgn, not a transparent pixel.
//     Windows resolves a drop target with WindowFromPoint, which respects the
//     region, so dropping in the middle reaches the window below and the
//     normal Explorer drop happens, which is what "cancel" has to mean.
//
// The window region is always the ring's disc, never the square: before the
// drag data arrives it is the whole disc (the pointer starts at the centre and
// the window has to be under it to be offered the data), and once the files
// are known the hub is cut out. A drop in a square corner outside the ring
// therefore always falls through.
//
// The hub is painted by WheelHubWindow, a click-through window over the hole,
// so the wheel looks whole while the hole still passes a drop through.
//
// Closing fades the wheel out for a moment. For that moment the window is made
// click-through (WS_EX_TRANSPARENT) and reports itself not visible, so nothing
// can land on a wheel that is leaving and every caller sees it gone at once.
//
// One showing of the wheel is one session. The paths it read from OLE belong
// to that session only: HideNow forgets them, and a drop is accepted only when
// the drop's own data names the same paths the session read. A drag whose
// data carries no file paths dismisses the wheel at once, and so does a
// gesture that moves over the wheel without OLE ever entering it (text
// selection, a rubber band, a source OLE cannot reach this window from).
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
    private readonly WheelRenderer _renderer = new(drawsHub: false);
    private readonly WheelHubWindow _hub;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _arrivalTimer;
    private readonly DispatcherTimer _dwellTimer;
    private readonly ArrivalWatch _arrival = new(
        Math.Max(8, 2 * Math.Max(Native.GetSystemMetrics(Native.SM_CXDRAG), Native.GetSystemMetrics(Native.SM_CYDRAG))));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IntPtr _handle;
    private bool _holeCut;
    private bool _rejected;
    private bool _closing;
    private int _closeGeneration;
    private int _shownX;
    private int _shownY;
    private string[] _files = Array.Empty<string>();

    internal WheelWindow()
    {
        InitializeComponent();
        Width = WheelGeometry.Default.Size;
        Height = WheelGeometry.Default.Size;
        Root.Children.Add(_renderer);

        _hideTimer = Timer(TimeSpan.FromMilliseconds(500), () => HideNow("timer"));
        // Watches for a gesture that is not an OLE drag: see ArrivalWatch.
        _arrivalTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _arrivalTimer.Tick += (_, _) => OnArrivalTick();
        _dwellTimer = Timer(TimeSpan.FromMilliseconds(650), OnDwell);

        _handle = new WindowInteropHelper(this).EnsureHandle();
        var style = Native.GetWindowStyleEx(_handle);
        Native.SetWindowStyleEx(_handle, style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST);
        _hub = new WheelHubWindow(_handle);
        _renderer.HotChanged += () => _hub.SetLabel(_renderer.HotLabel);

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

    /// <summary>The wheel went away and forgot its session. Carries the reason.</summary>
    internal event Action<string>? Hidden;

    internal WheelPlan Plan => _renderer.Plan;

    /// <summary>
    /// Visible and not on its way out. Hides Window.IsVisible on purpose: a
    /// wheel fading out has already ended its session, and the controller must
    /// treat it as gone.
    /// </summary>
    internal new bool IsVisible => base.IsVisible && !_closing;

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
        CancelClose();
        var scale = Native.ScaleForPoint(screenX, screenY);
        var side = (int)Math.Round(WheelGeometry.Default.Size * scale);
        var left = screenX - (side / 2);
        var top = screenY - (side / 2);
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, left, top, side, side, Native.SWP_NOACTIVATE);
        // Disc first, before the window is visible: no frame ever shows a
        // hit testable square.
        SetRegion(side, withHub: false);
        if (!IsVisible)
        {
            Show();
        }
        Native.SetWindowPos(_handle, Native.HWND_TOPMOST, left, top, side, side,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _hub.ShowAt(screenX, screenY, scale);
        _renderer.PlayOpen();
        _hub.PlayOpen();
        _shownX = screenX;
        _shownY = screenY;
        _arrival.Reset();
        _arrivalTimer.Stop();
        _arrivalTimer.Start();
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
        Native.SetWindowPos(_hub.Handle, Native.HWND_TOPMOST, -30000, -30000, 10, 10, Native.SWP_NOACTIVATE);
        _hub.Show();
        _hub.Hide();
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

    /// <summary>
    /// Take the wheel off the screen and forget the session, visible or not:
    /// the paths of one drag must never serve another.
    /// </summary>
    internal void HideNow(string reason)
    {
        _hideTimer.Stop();
        _arrivalTimer.Stop();
        _dwellTimer.Stop();
        _files = Array.Empty<string>();
        _rejected = false;
        _renderer.SetHot(-1);
        if (!IsVisible)
        {
            return;
        }
        Log.Write($"wheel hidden ({reason})");
        // Click-through first, so the fade can never catch a drop or the
        // pointer, then fade; the window really goes when the fade ends.
        _closing = true;
        var generation = ++_closeGeneration;
        Native.SetWindowStyleEx(_handle, Native.GetWindowStyleEx(_handle) | WheelHubWindow.WS_EX_TRANSPARENT);
        _hub.PlayClose();
        _renderer.PlayClose(() => FinishClose(generation));
        Hidden?.Invoke(reason);
    }

    private void FinishClose(int generation)
    {
        if (!_closing || generation != _closeGeneration)
        {
            return;
        }
        _closing = false;
        Hide();
        _hub.Hide();
        ClearRegion();
        Native.SetWindowStyleEx(_handle, Native.GetWindowStyleEx(_handle) & ~(long)WheelHubWindow.WS_EX_TRANSPARENT);
        _renderer.Settle();
        _hub.Settle();
    }

    /// <summary>A wheel shown again while it fades out comes straight back.</summary>
    private void CancelClose()
    {
        if (!_closing)
        {
            return;
        }
        _closing = false;
        _closeGeneration++;
        ClearRegion();
        Native.SetWindowStyleEx(_handle, Native.GetWindowStyleEx(_handle) & ~(long)WheelHubWindow.WS_EX_TRANSPARENT);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _arrival.Entered();
        TakeFiles(e);
        Track(e);
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        _arrival.Entered();
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
        e.Handled = true;
        // The drop's own data, read now. It has to name exactly the paths
        // this session read on DragEnter; anything else is a drop from some
        // other drag landing on a wheel that is still on screen.
        var paths = DropData.FilePaths(e.Data);
        if (_rejected || _files.Length == 0 || paths is null || !paths.SequenceEqual(_files, StringComparer.Ordinal))
        {
            Log.Write($"drop refused: the drop's data does not match this wheel's drag ({paths?.Length ?? 0} vs {_files.Length} file(s))");
            e.Effects = DragDropEffects.None;
            HideNow("drop without matching data");
            return;
        }
        var index = SegmentAt(e);
        var plan = _renderer.Plan;
        var segment = index >= 0 && index < plan.Count ? plan.Segments[index] : null;
        Log.Write($"drop on {(segment is null ? "nothing" : segment.Label)} with {paths.Length} file(s)");
        e.Effects = segment is { Runnable: true } ? DragDropEffects.Copy : DragDropEffects.None;
        if (segment is not null)
        {
            Dropped?.Invoke(segment, paths);
        }
        HideNow("dropped");
    }

    /// <summary>
    /// Read the paths once per session. No filesystem call happens here: this
    /// runs inside OLE's callback while the source's drag loop waits on it.
    /// </summary>
    private void TakeFiles(DragEventArgs e)
    {
        if (_files.Length > 0 || _rejected)
        {
            return;
        }
        var paths = DropData.FilePaths(e.Data);
        if (paths is null)
        {
            // Text, a browser image, an Outlook attachment (FileContents with
            // no path): not something the engine can take. Leave at once so
            // the window below gets the drag back on the next pointer move.
            _rejected = true;
            e.Effects = DragDropEffects.None;
            Log.Write($"drag carries no file paths ({DropData.Describe(e.Data)}), wheel dismissed");
            Dispatcher.BeginInvoke(DispatcherPriority.Send, () => HideNow("not a file drag"));
            return;
        }
        _files = paths;
        _arrivalTimer.Stop();
        Log.Write($"drag carries {paths.Length} file(s): {string.Join("|", paths.Take(5))}");
        CutHole();
        FilesSeen?.Invoke(paths);
    }

    private void OnArrivalTick()
    {
        if (!IsVisible || _files.Length > 0)
        {
            _arrivalTimer.Stop();
            return;
        }
        if (!Native.GetCursorPos(out var point))
        {
            return;
        }
        var distance = Math.Sqrt(Math.Pow(point.X - _shownX, 2) + Math.Pow(point.Y - _shownY, 2));
        if (_arrival.ShouldGiveUp(distance, _clock.ElapsedMilliseconds))
        {
            Log.Write($"no OLE drag reached the wheel after the pointer moved {distance:0}px, not a file drag");
            HideNow("no drag data");
        }
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

    /// <summary>Cut the hub out of the disc, once the drag data has arrived.</summary>
    private void CutHole()
    {
        if (_holeCut || !IsVisible || !Native.GetWindowRect(_handle, out var rect))
        {
            return;
        }
        var applied = SetRegion(Math.Min(rect.Width, rect.Height), withHub: true);
        Log.Write($"hub hole cut={applied}");
        Log.Write($"hub falls through={_hub.FallsThrough(_shownX, _shownY, _handle)}");
    }

    /// <summary>
    /// Make the window's hit region the ring's disc, with or without the hub
    /// cut out. <paramref name="side"/> is the window side in physical pixels.
    /// </summary>
    private bool SetRegion(int side, bool withHub)
    {
        if (side <= 0)
        {
            return false;
        }
        var scale = side / WheelGeometry.Default.Size;
        var centre = side / 2.0;
        var outer = WheelGeometry.Default.OuterRadius * scale;
        var region = Native.CreateEllipticRgn(
            (int)Math.Round(centre - outer), (int)Math.Round(centre - outer),
            (int)Math.Round(centre + outer), (int)Math.Round(centre + outer));
        if (withHub)
        {
            var inner = WheelGeometry.Default.InnerRadius * scale;
            var hub = Native.CreateEllipticRgn(
                (int)Math.Round(centre - inner), (int)Math.Round(centre - inner),
                (int)Math.Round(centre + inner), (int)Math.Round(centre + inner));
            var donut = Native.CreateRectRgn(0, 0, 1, 1);
            Native.CombineRgn(donut, region, hub, Native.RGN_DIFF);
            Native.DeleteObject(region);
            Native.DeleteObject(hub);
            region = donut;
        }
        // SetWindowRgn takes ownership of the region it is given.
        var applied = Native.SetWindowRgn(_handle, region, true) != 0;
        if (!applied)
        {
            Native.DeleteObject(region);
        }
        _holeCut = applied && withHub;
        return applied;
    }

    private void ClearRegion()
    {
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
