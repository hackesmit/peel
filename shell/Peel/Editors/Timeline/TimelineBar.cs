// The bar under the preview: the file from left to right, with the user's marks on it.
//
// It draws what TimelinePlan holds and turns the mouse into plan edits; it
// never decides what a time means. TimeAxis does the arithmetic. One gesture
// grammar for every tool, so the bar feels the same in each:
//
//   click            move the playhead (and the picture with it)
//   drag a handle    move the start or end (Trim), or a cut (Split)
//   drag elsewhere   Trim and Split: scrub. Bleep: add a range. Range: set it.
//   double click     Split: add a cut there
//
// Like an editor canvas, the bar is a Canvas, which has no UI Automation peer,
// so it says where it is in device pixels in the log ("editor timeline ... at
// x,y size wxh duration Nms"). The scripted test on George aims a real mouse
// at a moment of the file from that line.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Peel.Editors.Timeline;

internal sealed class TimelineBar : Canvas
{
    /// <summary>How close, in device independent units, a press has to be to grab a handle or a cut.</summary>
    internal const double Grab = 8;

    /// <summary>Movement below this is a click, not a drag.</summary>
    private const double DragSlop = 4;

    private const double StripTop = 6;

    private readonly TimelinePlan _plan;
    private readonly string _id;
    private IReadOnlyList<Frame> _strip = Array.Empty<Frame>();
    private Gesture _gesture;
    private int _grabbedCut = -1;
    private double _downX;
    private double _nowX;
    private bool _moved;

    private enum Gesture
    {
        None,
        Scrub,
        StartHandle,
        EndHandle,
        Cut,
        Sweep,
    }

    internal TimelineBar(TimelinePlan plan, string id, double width, double height)
    {
        _plan = plan;
        _id = id;
        Width = width;
        Height = height;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1A));
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        Focusable = false;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => _gesture = Gesture.None;
        Loaded += (_, _) => Say();
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window)
            {
                window.LocationChanged += (_, _) => Say();
                window.ContentRendered += (_, _) => Say();
            }
        };
    }

    internal TimeAxis Axis => new(_plan.Duration, Width);

    /// <summary>Where the playhead is, in seconds.</summary>
    internal double Playhead { get; private set; }

    /// <summary>The user moved the playhead (click or scrub). Not raised by SetPlayhead.</summary>
    internal event Action<double>? Scrubbed;

    /// <summary>The plan changed through the bar.</summary>
    internal event Action? Changed;

    internal void SetStrip(IReadOnlyList<Frame> frames)
    {
        _strip = frames;
        Render();
    }

    internal void SetPlayhead(double seconds)
    {
        Playhead = Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, _plan.Duration);
        Render();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X;
        _downX = x;
        _nowX = x;
        _moved = false;
        var axis = Axis;
        var at = axis.ToSeconds(x);
        _gesture = Gesture.Scrub;
        _grabbedCut = -1;

        if (e.ClickCount == 2 && _plan.Kind == TimelineKind.Split)
        {
            if (_plan.AddCut(at))
            {
                Changed?.Invoke();
            }
            _gesture = Gesture.None;
            Render();
            e.Handled = true;
            return;
        }
        switch (_plan.Kind)
        {
            case TimelineKind.Trim when Math.Abs(axis.ToX(_plan.End) - x) <= Grab
                && Math.Abs(axis.ToX(_plan.End) - x) <= Math.Abs(axis.ToX(_plan.Start) - x):
                _gesture = Gesture.EndHandle;
                break;
            case TimelineKind.Trim when Math.Abs(axis.ToX(_plan.Start) - x) <= Grab:
                _gesture = Gesture.StartHandle;
                break;
            case TimelineKind.Split:
                _grabbedCut = _plan.CutNear(at, axis.Span(Grab));
                if (_grabbedCut >= 0)
                {
                    _gesture = Gesture.Cut;
                }
                break;
            case TimelineKind.Bleep or TimelineKind.Range:
                _gesture = Gesture.Sweep;
                break;
        }
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_gesture == Gesture.None || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        _nowX = e.GetPosition(this).X;
        if (Math.Abs(_nowX - _downX) >= DragSlop)
        {
            _moved = true;
        }
        var at = Axis.ToSeconds(_nowX);
        switch (_gesture)
        {
            case Gesture.Scrub:
                Scrub(at);
                break;
            case Gesture.StartHandle:
                _plan.SetStart(at);
                Changed?.Invoke();
                Scrub(_plan.Start);
                break;
            case Gesture.EndHandle:
                _plan.SetEnd(at);
                Changed?.Invoke();
                Scrub(_plan.End);
                break;
            case Gesture.Cut:
                if (_plan.MoveCut(_grabbedCut, at))
                {
                    _grabbedCut = _plan.CutNear(at, 1e-9);
                    Changed?.Invoke();
                }
                Render();
                break;
            case Gesture.Sweep:
                Render();
                break;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.None)
        {
            return;
        }
        var gesture = _gesture;
        _gesture = Gesture.None;
        ReleaseMouseCapture();
        _nowX = e.GetPosition(this).X;
        var axis = Axis;
        if (gesture == Gesture.Sweep)
        {
            if (_moved)
            {
                var from = axis.ToSeconds(_downX);
                var to = axis.ToSeconds(_nowX);
                var taken = _plan.Kind == TimelineKind.Bleep ? _plan.AddRange(from, to) : _plan.SetSpan(from, to);
                if (taken)
                {
                    Changed?.Invoke();
                }
            }
            else
            {
                Scrub(axis.ToSeconds(_nowX));
            }
        }
        else if (gesture == Gesture.Scrub || !_moved)
        {
            Scrub(axis.ToSeconds(_nowX));
        }
        Render();
        e.Handled = true;
    }

    private void Scrub(double seconds)
    {
        SetPlayhead(seconds);
        Scrubbed?.Invoke(Playhead);
    }

    internal void Render()
    {
        Children.Clear();
        var axis = Axis;
        var stripHeight = Height - StripTop * 2;
        DrawStrip(stripHeight);

        var shade = new SolidColorBrush(Color.FromArgb(0xA8, 0x0B, 0x0B, 0x10));
        var accent = (Brush)TryFindResource("EditorAccent") ?? Brushes.DeepSkyBlue;
        var warn = (Brush)TryFindResource("EditorWarn") ?? Brushes.Orange;
        switch (_plan.Kind)
        {
            case TimelineKind.Trim:
                Box(0, axis.ToX(_plan.Start), shade);
                Box(axis.ToX(_plan.End), Width, shade);
                Handle(axis.ToX(_plan.Start), accent);
                Handle(axis.ToX(_plan.End), accent);
                break;
            case TimelineKind.Split:
                foreach (var x in _plan.CutMarks(axis))
                {
                    Line(x, warn, 2);
                }
                foreach (var x in _plan.EveryMarks(axis))
                {
                    Line(x, warn, 1);
                }
                break;
            case TimelineKind.Bleep:
                foreach (var (from, to) in _plan.Ranges)
                {
                    Band(axis.ToX(from), axis.ToX(to), warn);
                }
                break;
            case TimelineKind.Range:
                if (_plan.HasRange)
                {
                    Band(axis.ToX(_plan.Start), axis.ToX(_plan.End), warn);
                }
                break;
        }
        if (_gesture == Gesture.Sweep && _moved)
        {
            Band(Math.Min(_downX, _nowX), Math.Max(_downX, _nowX), accent, 0.35);
        }
        Line(axis.ToX(Playhead), Brushes.White, 1.5);
    }

    private void DrawStrip(double stripHeight)
    {
        if (_strip.Count == 0)
        {
            return;
        }
        var first = _strip[0].Bitmap;
        var tile = first.PixelHeight > 0 ? stripHeight * first.PixelWidth / first.PixelHeight : stripHeight;
        tile = Math.Max(8, tile);
        var slots = (int)Math.Ceiling(Width / tile);
        var axis = Axis;
        for (var slot = 0; slot < slots; slot++)
        {
            // The frame nearest the moment in the middle of this slot.
            var middle = axis.ToSeconds((slot + 0.5) * tile);
            var frame = _strip.OrderBy(candidate => Math.Abs(candidate.Seconds - middle)).First();
            var image = new System.Windows.Controls.Image
            {
                Source = frame.Bitmap,
                Width = tile,
                Height = stripHeight,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
                Opacity = 0.85,
            };
            SetLeft(image, slot * tile);
            SetTop(image, StripTop);
            Children.Add(image);
        }
    }

    private void Box(double from, double to, Brush fill)
    {
        if (to <= from)
        {
            return;
        }
        var box = new Rectangle { Width = to - from, Height = Height, Fill = fill, IsHitTestVisible = false };
        SetLeft(box, from);
        Children.Add(box);
    }

    private void Band(double from, double to, Brush fill, double opacity = 0.5)
    {
        var band = new Rectangle
        {
            Width = Math.Max(1, to - from),
            Height = Height,
            Fill = fill,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        SetLeft(band, from);
        Children.Add(band);
    }

    private void Line(double x, Brush stroke, double thickness)
    {
        Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x,
            X2 = x,
            Y1 = 0,
            Y2 = Height,
            Stroke = stroke,
            StrokeThickness = thickness,
            IsHitTestVisible = false,
        });
    }

    private void Handle(double x, Brush fill)
    {
        Line(x, fill, 2);
        var grip = new Rectangle
        {
            Width = 9,
            Height = 18,
            RadiusX = 2,
            RadiusY = 2,
            Fill = fill,
            Stroke = Brushes.Black,
            StrokeThickness = 0.8,
            IsHitTestVisible = false,
        };
        SetLeft(grip, x - 4.5);
        SetTop(grip, (Height - 18) / 2);
        Children.Add(grip);
    }

    /// <summary>Where the bar is on screen, in device pixels, and how long the file is.</summary>
    private void Say()
    {
        try
        {
            if (!IsLoaded || ActualWidth <= 0 || !IsVisible)
            {
                return;
            }
            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            Log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "editor timeline {0} at {1},{2} size {3}x{4} duration {5}ms",
                _id,
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X - topLeft.X),
                (int)Math.Round(bottomRight.Y - topLeft.Y),
                (long)Math.Round(_plan.Duration * 1000)));
        }
        catch (Exception error)
        {
            Log.Write($"cannot report where the {_id} timeline is", error);
        }
    }
}
