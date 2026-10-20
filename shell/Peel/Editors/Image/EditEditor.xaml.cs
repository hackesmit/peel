// Edit: rotate, flip and the three factors, with the engine's own maths shown
// live.
//
// The preview is not an approximation. Adjust is pillow's ImageEnhance, in the
// engine's order, so what the slider shows is what the file gets. It runs on a
// buffer no bigger than LiveSide because three passes over a 20 megapixel photo
// at every slider tick would make the window feel broken; the only visible
// consequence is that the contrast midpoint comes from the preview's own mean,
// which differs from the full size mean by well under one level.
//
// Rotate and flip are a LayoutTransform on the preview rather than pixel work:
// they are lossless in the engine too, and the window resizing as the picture
// turns is the clearest way to show what will be written.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Peel.Editors.Image;

internal partial class EditEditor : EditorWindowBase
{
    private const double StageWidth = 820;
    private const double StageHeight = 540;

    /// <summary>Longest side of the buffer the sliders work on.</summary>
    private const int LiveSide = 800;

    private readonly EditPlan _plan;
    private readonly byte[] _original;
    private readonly byte[] _working;
    private readonly WriteableBitmap _canvas;
    private readonly int _pixels;
    private readonly DispatcherTimer _settle;
    private readonly List<RadioButton> _rotations = new();
    private readonly List<RadioButton> _flips = new();
    private readonly Dictionary<string, Slider> _factors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _readouts = new(StringComparer.Ordinal);

    internal EditEditor(string path, int fileCount, PreviewSource preview, ParamSchema schema)
    {
        InitializeComponent();
        _plan = new EditPlan(schema);
        Title = $"Edit {System.IO.Path.GetFileName(path)}";
        Subject.Text = fileCount > 1
            ? string.Format(CultureInfo.InvariantCulture, "{0} and {1} more files", System.IO.Path.GetFileName(path), fileCount - 1)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}, {1} by {2} pixels",
                System.IO.Path.GetFileName(path), preview.FileWidth, preview.FileHeight);

        var live = Shrink(preview.Bitmap);
        _pixels = live.PixelWidth * live.PixelHeight;
        var stride = live.PixelWidth * 4;
        _original = new byte[stride * live.PixelHeight];
        live.CopyPixels(_original, stride, 0);
        _working = new byte[_original.Length];
        _canvas = new WriteableBitmap(live.PixelWidth, live.PixelHeight, 96, 96, PixelFormats.Bgra32, null);

        var map = new PreviewMap(preview.FileWidth, preview.FileHeight, StageWidth, StageHeight);
        Picture.Source = _canvas;
        Picture.Width = map.ViewSize.Width;
        Picture.Height = map.ViewSize.Height;

        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Paint();
        };
        // A timer that outlives its window is the bug the toasts had: stop it.
        Closed += (_, _) => _settle.Stop();

        BuildRotations();
        BuildFlips();
        BuildFactor(0, "brightness", schema.Label("brightness", "Brightness"));
        BuildFactor(1, "contrast", schema.Label("contrast", "Contrast"));
        BuildFactor(2, "saturation", schema.Label("saturation", "Saturation"));
        Paint();
        Turn();
    }

    protected override IReadOnlyDictionary<string, string>? Collect() => _plan.ToParams();

    /// <summary>
    /// A buffer the sliders can run over at interactive speed. The preview
    /// bitmap is already at most PreviewSource.LongestSide; this is smaller
    /// again, because the work here is three passes per tick.
    /// </summary>
    private static BitmapSource Shrink(BitmapSource source)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= LiveSide)
        {
            return source;
        }
        var scale = LiveSide / (double)longest;
        var smaller = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(smaller, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private void BuildRotations()
    {
        RotatePanel.Children.Add(new TextBlock
        {
            Text = _plan.Rotations.Count > 0 ? "Rotate" : string.Empty,
            Style = (Style)FindResource("EditorLabel"),
        });
        foreach (var choice in _plan.Rotations)
        {
            var button = Choice("EditRotate", $"EditRotate{choice}", choice, choice, choice == _plan.Rotate);
            button.Checked += (sender, _) =>
            {
                _plan.Rotate = (string)((RadioButton)sender).Tag;
                Turn();
            };
            _rotations.Add(button);
            RotatePanel.Children.Add(button);
        }
    }

    private void BuildFlips()
    {
        FlipPanel.Children.Add(new TextBlock
        {
            Text = _plan.Flips.Count > 0 ? "Flip" : string.Empty,
            Style = (Style)FindResource("EditorLabel"),
        });
        foreach (var choice in _plan.Flips)
        {
            var button = Choice("EditFlip", $"EditFlip{choice}", Sentence(choice), choice, choice == _plan.Flip);
            button.Checked += (sender, _) =>
            {
                _plan.Flip = (string)((RadioButton)sender).Tag;
                Turn();
            };
            _flips.Add(button);
            FlipPanel.Children.Add(button);
        }
    }

    /// <summary>
    /// One choice in a group. The value the engine wants goes in the Tag and
    /// the label is only what the user reads, so a choice the engine renames
    /// needs nothing here.
    /// </summary>
    private RadioButton Choice(string group, string automationId, string label, string value, bool on)
    {
        var button = new RadioButton
        {
            Content = label,
            Style = (Style)FindResource("EditorToggle"),
            GroupName = group,
            IsChecked = on,
            Tag = value,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private void BuildFactor(int row, string name, string label)
    {
        var caption = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("EditorLabel"),
            Margin = new Thickness(0, 4, 8, 4),
        };
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        Sliders.Children.Add(caption);

        var slider = new Slider
        {
            Style = (Style)FindResource("EditorSlider"),
            Minimum = _plan.Minimum(name),
            Maximum = _plan.Maximum(name),
            Value = _plan.Default(name),
            SmallChange = 0.05,
            LargeChange = 0.25,
            Margin = new Thickness(0, 4, 8, 4),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(slider, $"Edit{Sentence(name)}");
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        slider.ValueChanged += (_, args) =>
        {
            Set(name, args.NewValue);
            _readouts[name].Text = EditPlan.Readout(Factor(name));
            // Coalesce: a slider drag raises this on every pixel of travel.
            _settle.Stop();
            _settle.Start();
            Say();
        };
        Grid.SetRow(slider, row);
        Grid.SetColumn(slider, 1);
        Sliders.Children.Add(slider);
        _factors[name] = slider;

        var readout = new TextBlock
        {
            Text = EditPlan.Readout(_plan.Default(name)),
            Style = (Style)FindResource("EditorText"),
            MinWidth = 38,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(readout, $"Edit{Sentence(name)}Value");
        Grid.SetRow(readout, row);
        Grid.SetColumn(readout, 2);
        Sliders.Children.Add(readout);
        _readouts[name] = readout;
    }

    private double Factor(string name) => name switch
    {
        "brightness" => _plan.Brightness,
        "contrast" => _plan.Contrast,
        _ => _plan.Saturation,
    };

    private void Set(string name, double value)
    {
        switch (name)
        {
            case "brightness":
                _plan.Brightness = value;
                break;
            case "contrast":
                _plan.Contrast = value;
                break;
            default:
                _plan.Saturation = value;
                break;
        }
    }

    /// <summary>Redraw the preview with the three factors applied.</summary>
    private void Paint()
    {
        Array.Copy(_original, _working, _original.Length);
        Adjust.Apply(_working, _pixels, _plan.Brightness, _plan.Contrast, _plan.Saturation);
        _canvas.WritePixels(
            new Int32Rect(0, 0, _canvas.PixelWidth, _canvas.PixelHeight),
            _working,
            _canvas.PixelWidth * 4,
            0);
    }

    /// <summary>Turn and mirror the preview the way the engine will.</summary>
    private void Turn()
    {
        var group = new TransformGroup();
        if (_plan.FlipsHorizontally)
        {
            group.Children.Add(new ScaleTransform(-1, 1));
        }
        if (_plan.FlipsVertically)
        {
            group.Children.Add(new ScaleTransform(1, -1));
        }
        if (_plan.RotateDegrees != 0)
        {
            group.Children.Add(new RotateTransform(_plan.RotateDegrees));
        }
        Picture.LayoutTransform = group;
        Say();
    }

    private void Say()
    {
        var changes = _plan.ToParams();
        Hint.Text = changes.Count == 0
            ? "Enter saves, Escape cancels."
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}. Enter saves, Escape cancels.",
                string.Join(", ", changes.Select(change => $"{change.Key} {change.Value}")));
    }

    private static string Sentence(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _plan.Reset();
        foreach (var (name, slider) in _factors)
        {
            slider.Value = _plan.Default(name);
        }
        // Checking the default unchecks the rest of its group by itself, and
        // the handler it runs sets the plan to the value it already has.
        Show(_rotations, _plan.DefaultRotate);
        Show(_flips, _plan.DefaultFlip);
        Paint();
        Turn();
    }

    private static void Show(IEnumerable<RadioButton> group, string value)
    {
        foreach (var button in group)
        {
            if ((string)button.Tag == value)
            {
                button.IsChecked = true;
            }
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => ApplyAndClose();

    private void OnCancel(object sender, RoutedEventArgs e) => CancelAndClose();
}
