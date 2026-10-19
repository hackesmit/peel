// Crop: drag the handles, or type the numbers.
//
// Everything about where the box is lives in CropPlan; this window turns mouse
// positions into image pixels (PreviewMap) and draws. The two halves are kept
// in step through Render, which writes the four fields; a drag therefore shows
// its numbers. Typing does not write them back, because that would move the
// caret mid number, so the clamp a typed box ran into is shown in the line at
// the bottom ("Cropping to 200 by 150 at 200,0"). That line is what the user
// and the scripted test both read before pressing Enter.
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
using Peel.Editors;

namespace Peel.Editors.Image;

internal partial class CropEditor : EditorWindowBase
{
    /// <summary>The preview never grows past this, so the window always fits.</summary>
    private const double StageWidth = 900;
    private const double StageHeight = 600;
    private const double HandleSize = 11;

    private static readonly (CropHandle Handle, double AtX, double AtY)[] Spots =
    {
        (CropHandle.TopLeft, 0, 0),
        (CropHandle.Top, 0.5, 0),
        (CropHandle.TopRight, 1, 0),
        (CropHandle.Right, 1, 0.5),
        (CropHandle.BottomRight, 1, 1),
        (CropHandle.Bottom, 0.5, 1),
        (CropHandle.BottomLeft, 0, 1),
        (CropHandle.Left, 0, 0.5),
    };

    private readonly CropPlan _plan;
    private readonly PreviewMap _map;
    private readonly Path _mask = new();
    private readonly Rectangle _box = new();
    private readonly List<Rectangle> _handles = new();
    private readonly List<ToggleButton> _aspects = new();
    private bool _writing;

    internal CropEditor(string path, PreviewSource preview, ParamSchema schema)
    {
        InitializeComponent();
        _plan = new CropPlan(preview.FileWidth, preview.FileHeight);
        _map = new PreviewMap(preview.FileWidth, preview.FileHeight, StageWidth, StageHeight);
        Title = $"Crop {System.IO.Path.GetFileName(path)}";
        Subject.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0}, {1} by {2} pixels",
            System.IO.Path.GetFileName(path), preview.FileWidth, preview.FileHeight);

        Picture.Source = preview.Bitmap;
        Picture.Width = _map.ViewSize.Width;
        Picture.Height = _map.ViewSize.Height;
        Stage.Width = _map.ViewSize.Width;
        Stage.Height = _map.ViewSize.Height;

        _mask.Fill = new SolidColorBrush(Color.FromArgb(0x96, 0x0B, 0x0B, 0x10));
        _mask.IsHitTestVisible = false;
        Overlay.Children.Add(_mask);

        _box.Stroke = (Brush)FindResource("EditorAccent");
        _box.StrokeThickness = 1.4;
        _box.IsHitTestVisible = false;
        Overlay.Children.Add(_box);

        foreach (var _ in Spots)
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = (Brush)FindResource("EditorAccent"),
                Stroke = Brushes.Black,
                StrokeThickness = 0.8,
                IsHitTestVisible = false,
            };
            _handles.Add(handle);
            Overlay.Children.Add(handle);
        }

        BuildAspects();
        // The four labels are the engine's own ("Left", "Top", "Width",
        // "Height" today): the schema names every field the window shows.
        LabelX.Text = schema.Label("x", "Left");
        LabelY.Text = schema.Label("y", "Top");
        LabelW.Text = schema.Label("w", "Width");
        LabelH.Text = schema.Label("h", "Height");
        LogCanvas("image.crop", Overlay, preview.FileWidth, preview.FileHeight);
        Overlay.MouseLeftButtonDown += OnDown;
        Overlay.MouseMove += OnMove;
        Overlay.MouseLeftButtonUp += OnUp;
        Overlay.MouseLeave += (_, _) => Cursor = Cursors.Arrow;
        foreach (var field in new[] { FieldX, FieldY, FieldW, FieldH })
        {
            field.TextChanged += OnFieldChanged;
        }
        Render();
    }

    protected override IReadOnlyDictionary<string, string>? Collect() => _plan.ToParams();

    protected override bool HandlesEnter(IInputElement? focused) => false;

    private void BuildAspects()
    {
        AspectPanel.Children.Add(new TextBlock
        {
            Text = "Ratio",
            Style = (Style)FindResource("EditorLabel"),
        });
        foreach (var aspect in _plan.Aspects)
        {
            var button = new ToggleButton
            {
                Content = aspect.Label,
                Style = (Style)FindResource("EditorToggle"),
                IsChecked = aspect.Ratio is null,
                Tag = aspect,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"CropAspect{aspect.Label}");
            System.Windows.Automation.AutomationProperties.SetName(button, aspect.Label);
            button.Click += (sender, _) =>
            {
                var chosen = (CropAspect)((ToggleButton)sender).Tag;
                _plan.SetAspect(chosen.Ratio);
                foreach (var other in _aspects)
                {
                    other.IsChecked = ReferenceEquals(other, sender);
                }
                Render();
            };
            _aspects.Add(button);
            AspectPanel.Children.Add(button);
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var view = e.GetPosition(Overlay);
        var handle = HandleAt(view);
        var point = _map.ToImageRaw(view);
        // A press off the box starts a new one, and so does a press anywhere
        // inside a box that is still the whole picture: that is the state the
        // window opens in, and moving it is a no-op, so a drag there has to
        // mean "crop to this" like it does in every other crop tool.
        if (handle == CropHandle.None || (handle == CropHandle.Body && _plan.CoversWholeImage))
        {
            _plan.Set((int)point.X, (int)point.Y, 1, 1);
            handle = CropHandle.BottomRight;
            point = new Point(_plan.X, _plan.Y);
        }
        _plan.Begin(handle, point);
        Overlay.CaptureMouse();
        Render();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var view = e.GetPosition(Overlay);
        if (!_plan.Dragging)
        {
            Cursor = CursorFor(HandleAt(view));
            return;
        }
        _plan.DragTo(_map.ToImageRaw(view));
        Render();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _plan.End();
        Overlay.ReleaseMouseCapture();
        Render();
        e.Handled = true;
    }

    /// <summary>
    /// Which handle the pointer is on, Body for inside the box, or None.
    /// Measured in preview units so a handle is the same size to the hand
    /// whatever the zoom.
    /// </summary>
    private CropHandle HandleAt(Point view)
    {
        var box = _map.ToView(_plan.X, _plan.Y, _plan.Width, _plan.Height);
        foreach (var (handle, atX, atY) in Spots)
        {
            var centre = new Point(box.X + box.Width * atX, box.Y + box.Height * atY);
            if (Math.Abs(centre.X - view.X) <= HandleSize && Math.Abs(centre.Y - view.Y) <= HandleSize)
            {
                return handle;
            }
        }
        return box.Contains(view) ? CropHandle.Body : CropHandle.None;
    }

    private static Cursor CursorFor(CropHandle handle) => handle switch
    {
        CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
        CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW,
        CropHandle.Left or CropHandle.Right => Cursors.SizeWE,
        CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
        CropHandle.Body => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_writing)
        {
            return;
        }
        // A typed number is a free form box: holding the ratio would move the
        // side the user did not touch, which reads as the field being ignored.
        _plan.SetAspect(null);
        foreach (var button in _aspects)
        {
            button.IsChecked = ((CropAspect)button.Tag).Ratio is null;
        }
        _plan.Set(
            Whole(FieldX, _plan.X),
            Whole(FieldY, _plan.Y),
            Whole(FieldW, _plan.Width),
            Whole(FieldH, _plan.Height));
        // Only the overlay: rewriting the fields here would move the caret
        // while the user is still typing.
        Draw();
    }

    private static int Whole(TextBox field, int fallback) =>
        int.TryParse(field.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private void Render()
    {
        Draw();
        _writing = true;
        FieldX.Text = _plan.X.ToString(CultureInfo.InvariantCulture);
        FieldY.Text = _plan.Y.ToString(CultureInfo.InvariantCulture);
        FieldW.Text = _plan.Width.ToString(CultureInfo.InvariantCulture);
        FieldH.Text = _plan.Height.ToString(CultureInfo.InvariantCulture);
        _writing = false;
    }

    private void Draw()
    {
        var box = _map.ToView(_plan.X, _plan.Y, _plan.Width, _plan.Height);
        var whole = new RectangleGeometry(new Rect(0, 0, _map.ViewSize.Width, _map.ViewSize.Height));
        var hole = new RectangleGeometry(box);
        _mask.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { whole, hole } };
        _box.Width = Math.Max(1, box.Width);
        _box.Height = Math.Max(1, box.Height);
        Canvas.SetLeft(_box, box.X);
        Canvas.SetTop(_box, box.Y);
        for (var i = 0; i < _handles.Count; i++)
        {
            var (_, atX, atY) = Spots[i];
            Canvas.SetLeft(_handles[i], box.X + box.Width * atX - HandleSize / 2);
            Canvas.SetTop(_handles[i], box.Y + box.Height * atY - HandleSize / 2);
        }
        Hint.Text = _plan.CoversWholeImage
            ? "Drag a corner or type the numbers. Enter crops, Escape cancels."
            : $"Cropping to {_plan.Describe()}. Enter crops, Escape cancels.";
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _plan.Reset();
        foreach (var button in _aspects)
        {
            button.IsChecked = ((CropAspect)button.Tag).Ratio is null;
        }
        Render();
    }

    private void OnApply(object sender, RoutedEventArgs e) => ApplyAndClose();

    private void OnCancel(object sender, RoutedEventArgs e) => CancelAndClose();
}
