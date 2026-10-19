// Redact: drag over what has to go, and say whether it is blurred or boxed.
//
// The window shows the regions solid rather than blurred on purpose. A preview
// blur would look like the result and is not: image.redact pixelates and then
// blurs at a radius taken from the region's own size, and a user who judges
// "you can still read it" from a preview blur that is weaker than the engine's
// would redact less than they meant to. A flat block says "this will be gone"
// and cannot be read as a promise about the strength of the blur.
//
// The window also says what the engine does beyond the pixels: redact drops
// every tag, the location included. That is a safety property the user should
// know about before the file is written, not after.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Peel.Editors.Image;

internal partial class RedactEditor : EditorWindowBase
{
    private const double StageWidth = 900;
    private const double StageHeight = 560;

    private readonly RedactPlan _plan;
    private readonly PreviewMap _map;
    private readonly List<ToggleButton> _modes = new();
    private Point _from;
    private bool _drawing;

    internal RedactEditor(string path, PreviewSource preview, ParamSchema schema)
    {
        InitializeComponent();
        _plan = new RedactPlan(schema);
        _map = new PreviewMap(preview.FileWidth, preview.FileHeight, StageWidth, StageHeight);
        Title = $"Redact {System.IO.Path.GetFileName(path)}";
        Subject.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0}, {1} by {2} pixels",
            System.IO.Path.GetFileName(path), preview.FileWidth, preview.FileHeight);

        Picture.Source = preview.Bitmap;
        Picture.Width = _map.ViewSize.Width;
        Picture.Height = _map.ViewSize.Height;
        Stage.Width = _map.ViewSize.Width;
        Stage.Height = _map.ViewSize.Height;

        BuildModes(schema);
        LogCanvas("image.redact", Overlay, preview.FileWidth, preview.FileHeight);
        Overlay.MouseLeftButtonDown += OnDown;
        Overlay.MouseMove += OnMove;
        Overlay.MouseLeftButtonUp += OnUp;
        Overlay.Cursor = Cursors.Cross;
        Redraw();
    }

    protected override IReadOnlyDictionary<string, string>? Collect()
    {
        if (!_plan.Any)
        {
            Hint.Text = "Drag over what has to go, or press Escape to leave the picture alone.";
            return null;
        }
        return _plan.ToParams();
    }

    private void BuildModes(ParamSchema schema)
    {
        ModePanel.Children.Add(new TextBlock
        {
            Text = schema.Label("mode", "Hide with"),
            Style = (Style)FindResource("EditorLabel"),
        });
        foreach (var mode in _plan.Modes)
        {
            var label = mode.Length == 0 ? mode : char.ToUpperInvariant(mode[0]) + mode[1..];
            var button = new ToggleButton
            {
                Content = label,
                Style = (Style)FindResource("EditorToggle"),
                IsChecked = mode == _plan.Mode,
                Tag = mode,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"RedactMode{label}");
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (sender, _) =>
            {
                _plan.Mode = (string)((ToggleButton)sender).Tag;
                foreach (var other in _modes)
                {
                    other.IsChecked = ReferenceEquals(other, sender);
                }
                Redraw();
            };
            _modes.Add(button);
            ModePanel.Children.Add(button);
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _drawing = true;
        _from = _map.ToImage(e.GetPosition(Overlay));
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_drawing)
        {
            return;
        }
        Redraw(PixelBox.FromDrag(_from, _map.ToImage(e.GetPosition(Overlay)), _map.ImageWidth, _map.ImageHeight));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing)
        {
            return;
        }
        _drawing = false;
        Overlay.ReleaseMouseCapture();
        var box = PixelBox.FromDrag(_from, _map.ToImage(e.GetPosition(Overlay)), _map.ImageWidth, _map.ImageHeight);
        // A click with no drag is not a region: the engine would hide a single
        // pixel and the user would think nothing happened.
        if (box.Width >= 2 && box.Height >= 2)
        {
            _plan.Add(box);
        }
        Redraw();
        e.Handled = true;
    }

    private void Redraw(Int32Rect? pending = null)
    {
        Overlay.Children.Clear();
        foreach (var region in _plan.Regions)
        {
            Show(region, 1.0);
        }
        if (pending is { } box)
        {
            Show(box, 0.6);
        }
        Hint.Text = _plan.Any
            ? $"{_plan.Describe()}. Enter hides them, Escape cancels."
            : "Drag over what has to go. Enter hides it, Escape cancels.";
    }

    private void Show(Int32Rect region, double opacity)
    {
        var view = _map.ToView(region.X, region.Y, region.Width, region.Height);
        var block = new Rectangle
        {
            Width = Math.Max(1, view.Width),
            Height = Math.Max(1, view.Height),
            Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)),
            Stroke = (Brush)FindResource("EditorAccent"),
            StrokeThickness = 1,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(block, view.X);
        Canvas.SetTop(block, view.Y);
        Overlay.Children.Add(block);
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
