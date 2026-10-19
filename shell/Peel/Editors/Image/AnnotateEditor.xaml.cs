// Annotate: pen, arrow, box and text over the picture.
//
// The shapes are kept in image pixels (AnnotatePlan) and drawn from there on
// every change, so what is on screen and what goes to the engine can never
// drift apart: one list, one direction. Nothing is rasterised here. The engine
// does the drawing when the job runs, which is also why the preview's arrow
// head is close to pillow's rather than identical.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Peel.Editors.Image;

internal partial class AnnotateEditor : EditorWindowBase
{
    private const double StageWidth = 900;
    private const double StageHeight = 560;

    /// <summary>A pen point is kept only once the pointer has left the pixel.</summary>
    private const double PenStep = 1.0;

    private static readonly (string Label, string Colour)[] Inks =
    {
        ("Red", "#ff3b30"),
        ("Yellow", "#ffcc00"),
        ("Green", "#34c759"),
        ("Blue", "#0a84ff"),
        ("White", "#ffffff"),
        ("Black", "#000000"),
    };

    private static readonly (string Label, int Width)[] Nibs =
    {
        ("Thin", 2),
        ("Medium", 5),
        ("Thick", 10),
    };

    private readonly AnnotatePlan _plan = new();
    private readonly PreviewMap _map;
    private readonly List<RadioButton> _tools = new();
    private readonly List<RadioButton> _inks = new();
    private readonly List<RadioButton> _nibs = new();
    private readonly List<Point> _stroke = new();
    private ShapeKind _tool = ShapeKind.Pen;
    private string _ink = Inks[0].Colour;
    private int _nib = Nibs[1].Width;
    private Point _from;
    private bool _drawing;

    internal AnnotateEditor(string path, PreviewSource preview)
    {
        InitializeComponent();
        _map = new PreviewMap(preview.FileWidth, preview.FileHeight, StageWidth, StageHeight);
        Title = $"Annotate {System.IO.Path.GetFileName(path)}";
        Subject.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0}, {1} by {2} pixels",
            System.IO.Path.GetFileName(path), preview.FileWidth, preview.FileHeight);

        Picture.Source = preview.Bitmap;
        Picture.Width = _map.ViewSize.Width;
        Picture.Height = _map.ViewSize.Height;
        Stage.Width = _map.ViewSize.Width;
        Stage.Height = _map.ViewSize.Height;

        // Text on a 12 megapixel photo at pillow's default 16 pixels is
        // invisible, so the first guess scales with the picture. The user can
        // type over it; the engine has no say in this one, because `shapes` is
        // free form JSON and the size is per shape.
        WordSize.Text = Math.Max(16, Math.Min(preview.FileWidth, preview.FileHeight) / 14)
            .ToString(CultureInfo.InvariantCulture);

        BuildTools();
        RingInk(_inks[0]);
        LogCanvas("image.annotate", Overlay, preview.FileWidth, preview.FileHeight);
        Overlay.MouseLeftButtonDown += OnDown;
        Overlay.MouseMove += OnMove;
        Overlay.MouseLeftButtonUp += OnUp;
        Overlay.Cursor = Cursors.Cross;
        Caption.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                // Enter here is not Apply (HandlesEnter says so); it means the
                // words are ready, so say what to do with them.
                Hint.Text = "Now click where the words go.";
                e.Handled = true;
            }
        };
        Redraw();
    }

    protected override IReadOnlyDictionary<string, string>? Collect()
    {
        if (!_plan.Any)
        {
            Hint.Text = "Draw something first, or press Escape to leave the picture alone.";
            return null;
        }
        return _plan.ToParams();
    }

    /// <summary>Enter inside the caption box places the words, it does not apply.</summary>
    protected override bool HandlesEnter(IInputElement? focused) => ReferenceEquals(focused, Caption);

    private void BuildTools()
    {
        foreach (var (kind, label) in new[]
                 {
                     (ShapeKind.Pen, "Pen"),
                     (ShapeKind.Arrow, "Arrow"),
                     (ShapeKind.Rect, "Box"),
                     (ShapeKind.Text, "Text"),
                 })
        {
            var button = Choice("AnnotateTool", $"AnnotateTool{label}", label, kind == _tool);
            button.Checked += (_, _) =>
            {
                _tool = kind;
                Redraw();
            };
            _tools.Add(button);
            ToolPanel.Children.Add(button);
        }
        ToolPanel.Children.Add(Spacer());
        foreach (var (label, colour) in Inks)
        {
            var button = Choice("AnnotateInk", $"AnnotateInk{label}", label, colour == _ink);
            button.Background = (Brush)new BrushConverter().ConvertFromString(colour)!;
            button.Foreground = label is "White" or "Yellow" ? Brushes.Black : Brushes.White;
            button.MinWidth = 58;
            button.Checked += (sender, _) =>
            {
                _ink = colour;
                RingInk((RadioButton)sender);
            };
            _inks.Add(button);
            ToolPanel.Children.Add(button);
        }
        ToolPanel.Children.Add(Spacer());
        foreach (var (label, width) in Nibs)
        {
            var button = Choice("AnnotateWidth", $"AnnotateWidth{label}", label, width == _nib);
            button.Checked += (_, _) => _nib = width;
            _nibs.Add(button);
            ToolPanel.Children.Add(button);
        }
    }

    private RadioButton Choice(string group, string automationId, string label, bool on)
    {
        var button = new RadioButton
        {
            Content = label,
            Style = (Style)FindResource("EditorToggle"),
            GroupName = group,
            IsChecked = on,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>
    /// A swatch keeps its own colour when it is chosen (a local Background
    /// outranks the style's checked trigger), so a white ring is what shows
    /// which one is active.
    /// </summary>
    private void RingInk(RadioButton chosen)
    {
        foreach (var button in _inks)
        {
            var on = ReferenceEquals(button, chosen);
            button.BorderBrush = on ? Brushes.White : (Brush)FindResource("EditorEdge");
            button.BorderThickness = new Thickness(on ? 2.5 : 1);
        }
    }

    private UIElement Spacer() => new Border { Width = 14 };

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var at = _map.ToImage(e.GetPosition(Overlay));
        if (_tool == ShapeKind.Text)
        {
            Place(at);
            e.Handled = true;
            return;
        }
        _drawing = true;
        _from = at;
        _stroke.Clear();
        _stroke.Add(at);
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_drawing)
        {
            return;
        }
        var at = _map.ToImage(e.GetPosition(Overlay));
        if (_tool == ShapeKind.Pen)
        {
            var last = _stroke[^1];
            if (Math.Abs(at.X - last.X) >= PenStep || Math.Abs(at.Y - last.Y) >= PenStep)
            {
                _stroke.Add(at);
            }
        }
        Redraw(Pending(at));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing)
        {
            return;
        }
        _drawing = false;
        Overlay.ReleaseMouseCapture();
        var at = _map.ToImage(e.GetPosition(Overlay));
        if (Pending(at) is { } shape)
        {
            _plan.Add(shape);
        }
        _stroke.Clear();
        Redraw();
        e.Handled = true;
    }

    /// <summary>
    /// The shape the current drag would commit, or null when it is still too
    /// small to mean anything (a stray click must not leave a dot behind).
    /// </summary>
    private ShapeSpec? Pending(Point at)
    {
        switch (_tool)
        {
            case ShapeKind.Pen:
                return _stroke.Count < 2
                    ? null
                    : new ShapeSpec { Kind = ShapeKind.Pen, Points = _stroke.ToList(), Colour = _ink, Width = _nib };
            case ShapeKind.Arrow:
                return Math.Abs(at.X - _from.X) < 2 && Math.Abs(at.Y - _from.Y) < 2
                    ? null
                    : new ShapeSpec { Kind = ShapeKind.Arrow, From = _from, To = at, Colour = _ink, Width = _nib };
            case ShapeKind.Rect:
                var box = PixelBox.FromDrag(_from, at, _map.ImageWidth, _map.ImageHeight);
                return box.Width < 2 || box.Height < 2
                    ? null
                    : new ShapeSpec { Kind = ShapeKind.Rect, Box = box, Colour = _ink, Width = _nib };
            default:
                return null;
        }
    }

    private void Place(Point at)
    {
        var words = Caption.Text.Trim();
        if (words.Length == 0)
        {
            Hint.Text = "Type the words first, then click where they go.";
            Caption.Focus();
            return;
        }
        var size = int.TryParse(WordSize.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var typed)
            ? Math.Max(1, typed)
            : 16;
        _plan.Add(new ShapeSpec
        {
            Kind = ShapeKind.Text,
            From = at,
            Text = words,
            Colour = _ink,
            Width = _nib,
            FontSize = size,
        });
        Redraw();
    }

    private void Redraw(ShapeSpec? pending = null)
    {
        Overlay.Children.Clear();
        foreach (var shape in _plan.Shapes)
        {
            Show(shape, 1.0);
        }
        if (pending is not null)
        {
            Show(pending, 0.75);
        }
        var drawn = _plan.Shapes.Count;
        Hint.Text = drawn == 0
            ? $"{ToolName(_tool)}. Drag on the picture. Enter draws, Escape cancels."
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} shape{1}. Enter draws, Escape cancels.",
                drawn, drawn == 1 ? string.Empty : "s");
    }

    private static string ToolName(ShapeKind kind) => kind switch
    {
        ShapeKind.Pen => "Pen",
        ShapeKind.Arrow => "Arrow",
        ShapeKind.Rect => "Box",
        _ => "Text",
    };

    /// <summary>Draw one shape on the overlay, in preview units.</summary>
    private void Show(ShapeSpec shape, double opacity)
    {
        var ink = (Brush)new BrushConverter().ConvertFromString(shape.Colour)!;
        var thickness = Math.Max(1, shape.Width * _map.Scale);
        switch (shape.Kind)
        {
            case ShapeKind.Pen:
                var line = new Polyline
                {
                    Stroke = ink,
                    StrokeThickness = thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = opacity,
                    IsHitTestVisible = false,
                };
                foreach (var point in shape.Points)
                {
                    line.Points.Add(new Point(point.X * _map.Scale, point.Y * _map.Scale));
                }
                Overlay.Children.Add(line);
                break;
            case ShapeKind.Arrow:
                var from = new Point(shape.From.X * _map.Scale, shape.From.Y * _map.Scale);
                var to = new Point(shape.To.X * _map.Scale, shape.To.Y * _map.Scale);
                Overlay.Children.Add(new Line
                {
                    X1 = from.X,
                    Y1 = from.Y,
                    X2 = to.X,
                    Y2 = to.Y,
                    Stroke = ink,
                    StrokeThickness = thickness,
                    Opacity = opacity,
                    IsHitTestVisible = false,
                });
                // pillow's head: max(width * 3, 8) long, 0.45 radians either side.
                var head = Math.Max(shape.Width * 3, 8) * _map.Scale;
                var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
                var wing = new Polygon
                {
                    Fill = ink,
                    Opacity = opacity,
                    IsHitTestVisible = false,
                    Points = new PointCollection
                    {
                        to,
                        new(to.X - head * Math.Cos(angle - 0.45), to.Y - head * Math.Sin(angle - 0.45)),
                        new(to.X - head * Math.Cos(angle + 0.45), to.Y - head * Math.Sin(angle + 0.45)),
                    },
                };
                Overlay.Children.Add(wing);
                break;
            case ShapeKind.Rect:
                var view = _map.ToView(shape.Box.X, shape.Box.Y, shape.Box.Width, shape.Box.Height);
                var rectangle = new Rectangle
                {
                    Width = Math.Max(1, view.Width),
                    Height = Math.Max(1, view.Height),
                    Stroke = ink,
                    StrokeThickness = thickness,
                    Opacity = opacity,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rectangle, view.X);
                Canvas.SetTop(rectangle, view.Y);
                Overlay.Children.Add(rectangle);
                break;
            default:
                var text = new TextBlock
                {
                    Text = shape.Text,
                    Foreground = ink,
                    FontSize = Math.Max(4, shape.FontSize * _map.Scale),
                    Opacity = opacity,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(text, shape.From.X * _map.Scale);
                Canvas.SetTop(text, shape.From.Y * _map.Scale);
                Overlay.Children.Add(text);
                break;
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        _plan.Undo();
        Redraw();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _plan.Clear();
        Redraw();
    }

    private void OnApply(object sender, RoutedEventArgs e) => ApplyAndClose();

    private void OnCancel(object sender, RoutedEventArgs e) => CancelAndClose();
}
