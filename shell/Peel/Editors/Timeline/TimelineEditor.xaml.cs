// Trim, Split, Bleep, and the video Redact and Crop: one window over a timeline.
//
// The five tools are the same window because they are the same task, finding
// moments in a file, with a different thing to say about them (TimelinePlan).
// Redact and Crop also need a place in the frame, so for those the picture is
// the engine's own snapshot of the frame under the playhead (FrameGrab) and
// the box is drawn on it (RegionPlan); for the others the picture is a
// MediaElement, because there the user wants to hear and see where to cut.
//
// When Media Foundation cannot play a file (Opus, OGG, many MKVs and WebMs
// on a stock Windows), the window does not fail: the frame strip under the
// bar still comes from the engine, the picture falls back to the strip frame
// nearest the playhead, and the times work the same. The stage says so.
//
// Fields and bar are kept in step the way CropEditor keeps its box and its
// numbers: a change on the bar rewrites the fields, a typed field changes the
// plan and the bar but is never rewritten under the caret, and the line at the
// bottom says what will be sent ("Keeping 0:01.000 to 0:02.500"). That line
// is what the user and the scripted test both read before pressing Enter.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Peel.Engine;

namespace Peel.Editors.Timeline;

internal partial class TimelineEditor : EditorWindowBase
{
    private const double StageWidth = 720;
    private const double StageHeight = 405;
    private const double AudioStageHeight = 64;
    private const double BarHeight = 60;
    private const int StripFrames = 12;

    private readonly string _id;
    private readonly string _path;
    private readonly ParamSchema _schema;
    private readonly EngineClient _engine;
    private readonly TimelinePlan _plan;
    private readonly RegionPlan? _region;
    private readonly TimelineBar _bar;
    private readonly bool _video;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly List<RadioButton> _qualities = new();
    private IReadOnlyList<Frame> _strip = Array.Empty<Frame>();
    private PreviewMap _map;
    private TextBox? _start;
    private TextBox? _end;
    private TextBox? _cuts;
    private TextBox? _every;
    private TextBox? _ranges;
    private TextBox? _tone;
    private TextBox? _strength;
    private TextBox? _x;
    private TextBox? _y;
    private TextBox? _w;
    private TextBox? _h;
    private string _quality = string.Empty;
    private IReadOnlyDictionary<string, string> _problems = new Dictionary<string, string>();
    private bool _writing;
    private bool _playable;
    private bool _playing;
    private readonly LatestRun _snapshot = new();
    private Point _from;
    private bool _drawing;

    internal TimelineEditor(
        ActionEntry entry, string path, MediaInfo info, ParamSchema schema, EngineClient engine, BitmapSource? frame)
    {
        InitializeComponent();
        _id = entry.Id;
        _path = path;
        _schema = schema;
        _engine = engine;
        _video = info.HasVideo;
        var kind = KindOf(entry.Id);
        _plan = new TimelinePlan(kind, info.LengthFor(kind), kind == TimelineKind.Split ? info.VideoStart : 0);
        if (frame is not null && (entry.Id == "av.video_redact" || entry.Id == "av.video_crop"))
        {
            _region = new RegionPlan(frame.PixelWidth, frame.PixelHeight, covers: entry.Id == "av.video_redact");
        }

        var name = System.IO.Path.GetFileName(path);
        Title = $"{entry.Label} {name}";
        Heading.Text = entry.Label;
        Subject.Text = string.Format(CultureInfo.InvariantCulture, "{0}, {1}{2}",
            name, Timecode.Format(info.Duration),
            _region is null ? string.Empty : $", {_region.FrameWidth} by {_region.FrameHeight} pixels");
        ApplyButton.Content = entry.Id switch
        {
            "av.video_redact" => "Blur",
            _ => entry.Label,
        };

        BuildStage(frame);
        _bar = new TimelineBar(_plan, entry.Id, StageWidth, BarHeight);
        BarHost.Child = _bar;
        _bar.Scrubbed += OnScrubbed;
        _bar.Changed += () =>
        {
            WriteFields();
            Recheck();
            Describe();
        };
        BuildMarks();
        BuildFields();
        BuildRegion();
        BuildQuality();

        _clock.Tick += (_, _) => FollowPlayer();
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            ShowFrameAt(_bar.Playhead);
        };
        Closed += (_, _) => Shut();
        Loaded += (_, _) => _ = LoadStripAsync();
        _bar.SetPlayhead(0);
        ShowPlayhead();
        WriteFields();
        Describe();
    }

    internal static TimelineKind KindOf(string id) => id switch
    {
        "av.video_trim" or "av.audio_trim" => TimelineKind.Trim,
        "av.video_split" => TimelineKind.Split,
        "av.audio_bleep" => TimelineKind.Bleep,
        "av.video_redact" => TimelineKind.Range,
        _ => TimelineKind.Scrub,
    };

    protected override IReadOnlyDictionary<string, string>? Collect()
    {
        // Every field again, from what it says now: a field refused earlier
        // must not leave its last good value to be sent.
        _problems = TimelineFields.Read(_plan, _region, Texts(), typing: false);
        _bar.Render();
        RedrawRegion();
        var problem = _problems.Values.FirstOrDefault() ?? _plan.Problem() ?? _region?.Problem();
        if (problem is not null)
        {
            Hint.Text = problem;
            return null;
        }
        var sent = _plan.ToParams();
        if (_region is not null)
        {
            foreach (var pair in _region.ToParams())
            {
                sent[pair.Key] = pair.Value;
            }
        }
        if (!Extra(_tone, "frequency", 1000, sent) || !Extra(_strength, "strength", 12, sent))
        {
            return null;
        }
        if (_quality.Length > 0 && _quality != _schema.Text("quality", "medium"))
        {
            sent["quality"] = _quality;
        }
        Pause();
        return sent;
    }

    /// <summary>
    /// A whole number field the engine declares with a range. Sent only when
    /// it differs from the engine's default; refused here, with the range,
    /// rather than by a failed job.
    /// </summary>
    private bool Extra(TextBox? field, string name, double fallback, Dictionary<string, string> sent)
    {
        if (field is null)
        {
            return true;
        }
        var least = _schema.Minimum(name, double.MinValue);
        var most = _schema.Maximum(name, double.MaxValue);
        if (!int.TryParse(field.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < least || value > most)
        {
            Hint.Text = string.Format(CultureInfo.InvariantCulture, "{0} has to be a whole number from {1} to {2}.",
                _schema.Label(name, name), least, most);
            return false;
        }
        if (value != (int)_schema.Number(name, fallback))
        {
            sent[name] = value.ToString(CultureInfo.InvariantCulture);
        }
        return true;
    }

    // --- the picture ------------------------------------------------------------

    private void BuildStage(BitmapSource? frame)
    {
        if (_region is not null && frame is not null)
        {
            // The frame is the engine's, and the box is drawn in its pixels.
            _map = new PreviewMap(_region.FrameWidth, _region.FrameHeight, StageWidth, StageHeight);
            Stage.Width = _map.ViewSize.Width;
            Stage.Height = _map.ViewSize.Height;
            Still.Stretch = Stretch.Fill;
            Still.Source = frame;
            Overlay.Visibility = Visibility.Visible;
            Overlay.Cursor = Cursors.Cross;
            Overlay.MouseLeftButtonDown += OnRegionDown;
            Overlay.MouseMove += OnRegionMove;
            Overlay.MouseLeftButtonUp += OnRegionUp;
            LogCanvas(_id, Overlay, _region.FrameWidth, _region.FrameHeight);
            // The picture is a still the engine took, so there is nothing to play.
            PlayButton.Visibility = Visibility.Collapsed;
            return;
        }
        Stage.Width = StageWidth;
        Stage.Height = _video ? StageHeight : AudioStageHeight;
        StageNote.Text = _video ? "Opening the video" : "Opening the sound";
        Player.MediaOpened += (_, _) =>
        {
            _playable = true;
            Player.Visibility = _video ? Visibility.Visible : Visibility.Collapsed;
            Still.Visibility = Visibility.Collapsed;
            StageNote.Text = _video ? string.Empty : "Press Play to listen. Click the bar to move the playhead.";
            Player.Position = TimeSpan.FromSeconds(_bar.Playhead);
            Log.Write($"{_id}: media player opened {System.IO.Path.GetFileName(_path)}");
        };
        Player.MediaFailed += (_, e) =>
        {
            _playable = false;
            Player.Visibility = Visibility.Collapsed;
            Still.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = false;
            StageNote.Text = _video
                ? "Windows cannot play this video here. The frames below come from the engine and the times work the same."
                : "Windows cannot play this sound here. The times still work the same.";
            Log.Write($"{_id}: media player cannot open {_path}: {e.ErrorException?.Message}");
            ShowStripFrame(_bar.Playhead);
        };
        Player.MediaEnded += (_, _) => Pause();
        try
        {
            Player.Source = new Uri(_path, UriKind.Absolute);
            // Manual: Pause opens the file and shows its first frame without playing.
            Player.Pause();
        }
        catch (Exception error)
        {
            Log.Write($"{_id}: media player refused {_path}", error);
            PlayButton.IsEnabled = false;
        }
    }

    private async Task LoadStripAsync()
    {
        if (!_video)
        {
            return;
        }
        var frames = await FrameGrab.StripAsync(
            _engine, _path, _plan.Duration, StripFrames, (int)BarHeight * 2, _closing.Token).ConfigureAwait(true);
        if (_closing.IsCancellationRequested)
        {
            return;
        }
        _strip = frames;
        _bar.SetStrip(frames);
        Log.Write($"{_id}: frame strip of {frames.Count} frame(s)");
        if (_region is null && !_playable)
        {
            ShowStripFrame(_bar.Playhead);
        }
    }

    private void ShowStripFrame(double seconds)
    {
        if (_strip.Count == 0 || _region is not null)
        {
            return;
        }
        Still.Source = _strip.OrderBy(frame => Math.Abs(frame.Seconds - seconds)).First().Bitmap;
    }

    /// <summary>The region tools: ask the engine for the frame under the playhead, the latest ask wins.</summary>
    private async void ShowFrameAt(double seconds)
    {
        if (_region is null)
        {
            return;
        }
        // One snapshot run at a time: a newer ask cancels this one's engine.
        // FrameGrab never throws: a frame it cannot take leaves the last one up.
        var (_, latest, frame) = await _snapshot.RunAsync(
            token => FrameGrab.AtAsync(_engine, _path, seconds, token), _closing.Token).ConfigureAwait(true);
        var bitmap = frame.Bitmap;
        if (!latest || _closing.IsCancellationRequested || bitmap is null)
        {
            return;
        }
        if (bitmap.PixelWidth == _region.FrameWidth && bitmap.PixelHeight == _region.FrameHeight)
        {
            Still.Source = bitmap;
        }
    }

    // --- the playhead -------------------------------------------------------------

    private void OnScrubbed(double seconds)
    {
        Pause();
        ShowPlayhead();
        if (_region is not null)
        {
            // A frame is an engine run: wait until the scrub settles.
            _settle.Stop();
            _settle.Start();
        }
        else if (_playable)
        {
            Player.Position = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            ShowStripFrame(seconds);
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (!_playable)
        {
            return;
        }
        if (_playing)
        {
            Pause();
            return;
        }
        var from = _bar.Playhead;
        if (_plan.Kind == TimelineKind.Trim && (from < _plan.Start || from >= _plan.End - TimelinePlan.MinSpan))
        {
            // Play what is kept.
            from = _plan.Start;
        }
        else if (from >= _plan.Duration - TimelinePlan.MinSpan)
        {
            from = 0;
        }
        Player.Position = TimeSpan.FromSeconds(from);
        Player.Play();
        _playing = true;
        _clock.Start();
        PlayButton.Content = "Pause";
    }

    private void Pause()
    {
        if (!_playing)
        {
            return;
        }
        _playing = false;
        _clock.Stop();
        try
        {
            Player.Pause();
        }
        catch (Exception error)
        {
            Log.Write($"{_id}: cannot pause the player", error);
        }
        PlayButton.Content = "Play";
    }

    private void FollowPlayer()
    {
        var at = Player.Position.TotalSeconds;
        var stop = _plan.Kind == TimelineKind.Trim && _bar.Playhead < _plan.End ? _plan.End : _plan.Duration;
        if (at >= stop)
        {
            at = stop;
            Pause();
        }
        _bar.SetPlayhead(at);
        ShowPlayhead();
    }

    private void ShowPlayhead() =>
        PlayheadText.Text = $"{Timecode.Format(_bar.Playhead)} of {Timecode.Format(_plan.Duration)}";

    // --- marks --------------------------------------------------------------------

    private void BuildMarks()
    {
        switch (_plan.Kind)
        {
            case TimelineKind.Trim:
                Mark("Start here", "TimelineStartHere", () => _plan.SetStart(_bar.Playhead));
                Mark("End here", "TimelineEndHere", () => _plan.SetEnd(_bar.Playhead));
                break;
            case TimelineKind.Range:
                Mark("From here", "TimelineStartHere", () => _plan.SetStart(_bar.Playhead));
                Mark("To here", "TimelineEndHere", () => _plan.SetEnd(_bar.Playhead));
                Mark("Whole clip", "TimelineWhole", _plan.ClearRange);
                break;
            case TimelineKind.Split:
                Mark("Cut here", "TimelineCutHere", () =>
                {
                    if (!_plan.AddCut(_bar.Playhead))
                    {
                        Hint.Text = "A cut here would leave a part too short to keep.";
                    }
                });
                Mark("Undo", "TimelineUndo", _plan.Undo);
                Mark("Clear", "TimelineClear", _plan.Clear);
                break;
            case TimelineKind.Bleep:
                Mark("Undo", "TimelineUndo", _plan.Undo);
                Mark("Clear", "TimelineClear", _plan.Clear);
                break;
        }
    }

    private void Mark(string label, string automationId, Action act)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("EditorButton") };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) =>
        {
            act();
            WriteFields();
            Recheck();
            _bar.Render();
            Describe();
        };
        MarkPanel.Children.Add(button);
    }

    // --- fields -------------------------------------------------------------------

    private void BuildFields()
    {
        switch (_plan.Kind)
        {
            case TimelineKind.Trim:
                _start = Field(FieldPanel, _schema.Label("start", "Start"), "TimelineStart", 96);
                _end = Field(FieldPanel, _schema.Label("end", "End"), "TimelineEnd", 96);
                break;
            case TimelineKind.Range:
                _start = Field(FieldPanel, _schema.Label("start", "From"), "TimelineStart", 96);
                _end = Field(FieldPanel, _schema.Label("end", "To"), "TimelineEnd", 96);
                break;
            case TimelineKind.Split:
                _cuts = Field(FieldPanel, _schema.Label("at", "Cut at"), "TimelineCuts", 260);
                _every = Field(FieldPanel, _schema.Label("every", "Every (seconds)"), "TimelineEvery", 64);
                break;
            case TimelineKind.Bleep:
                _ranges = Field(FieldPanel, _schema.Label("ranges", "Ranges"), "TimelineRanges", 300);
                _tone = Field(FieldPanel, _schema.Label("frequency", "Tone (Hz)"), "TimelineTone", 64);
                _tone.Text = _schema.Number("frequency", 1000).ToString(CultureInfo.InvariantCulture);
                break;
        }
        foreach (var field in new[] { _start, _end, _cuts, _every, _ranges })
        {
            if (field is not null)
            {
                field.TextChanged += OnTimeFieldChanged;
            }
        }
        FieldPanel.Visibility = FieldPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TextBox Field(Panel panel, string label, string automationId, double width)
    {
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("EditorLabel") });
        var box = new TextBox
        {
            Style = (Style)FindResource("EditorField"),
            Width = width,
            Margin = new Thickness(0, 0, 12, 0),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, automationId);
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        panel.Children.Add(box);
        return box;
    }

    /// <summary>The plan, as text, into every time field. Never while the user is typing in one.</summary>
    private void WriteFields()
    {
        _writing = true;
        try
        {
            switch (_plan.Kind)
            {
                case TimelineKind.Trim:
                    _start!.Text = Timecode.Format(_plan.Start);
                    _end!.Text = Timecode.Format(_plan.End);
                    break;
                case TimelineKind.Range:
                    _start!.Text = _plan.HasRange ? Timecode.Format(_plan.Start) : string.Empty;
                    _end!.Text = _plan.HasRange ? Timecode.Format(_plan.End) : string.Empty;
                    break;
                case TimelineKind.Split:
                    _cuts!.Text = _plan.CutsText();
                    _every!.Text = _plan.Every is { } every
                        ? every.ToString("0.###", CultureInfo.InvariantCulture)
                        : string.Empty;
                    break;
                case TimelineKind.Bleep:
                    _ranges!.Text = _plan.RangesText();
                    break;
            }
        }
        finally
        {
            _writing = false;
        }
    }

    private void OnTimeFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_writing)
        {
            return;
        }
        // Cuts and an interval are one or the other: typing in one empties
        // the other.
        _writing = true;
        try
        {
            if (sender == _cuts && _every is not null && _cuts.Text.Trim().Length > 0)
            {
                _every.Text = string.Empty;
            }
            else if (sender == _every && _cuts is not null && _every.Text.Trim().Length > 0)
            {
                _cuts.Text = string.Empty;
            }
        }
        finally
        {
            _writing = false;
        }
        Recheck();
        _bar.Render();
        Describe();
    }

    /// <summary>Every field into the plans, as the user is typing.</summary>
    private void Recheck() => _problems = TimelineFields.Read(_plan, _region, Texts(), typing: true);

    private FieldTexts Texts() => new()
    {
        Start = _start?.Text,
        End = _end?.Text,
        Cuts = _cuts?.Text,
        Every = _every?.Text,
        Ranges = _ranges?.Text,
        X = _x?.Text,
        Y = _y?.Text,
        W = _w?.Text,
        H = _h?.Text,
    };

    private void Describe()
    {
        if (_problems.Values.FirstOrDefault() is { } problem)
        {
            Hint.Text = problem;
            return;
        }
        var parts = new List<string>();
        var timeline = _plan.Describe();
        if (timeline.Length > 0)
        {
            parts.Add(timeline);
        }
        if (_region is not null)
        {
            parts.Add((_region.Covers ? "box " : "Cropping to ") + _region.Describe());
        }
        var said = string.Join(", ", parts);
        Hint.Text = said.Length > 0 ? $"{said}. Enter applies, Escape cancels." : "Enter applies, Escape cancels.";
    }

    // --- the box on the frame -----------------------------------------------------

    private void BuildRegion()
    {
        if (_region is null)
        {
            return;
        }
        RegionPanel.Visibility = Visibility.Visible;
        _x = Field(RegionPanel, _schema.Label("x", "X"), "RegionX", 60);
        _y = Field(RegionPanel, _schema.Label("y", "Y"), "RegionY", 60);
        _w = Field(RegionPanel, _schema.Label("w", "Width"), "RegionW", 60);
        _h = Field(RegionPanel, _schema.Label("h", "Height"), "RegionH", 60);
        foreach (var field in new[] { _x, _y, _w, _h })
        {
            field.TextChanged += OnRegionFieldChanged;
        }
        if (_schema.Declares("strength"))
        {
            _strength = Field(RegionPanel, _schema.Label("strength", "Blur"), "RegionStrength", 48);
            _strength.Text = _schema.Number("strength", 12).ToString(CultureInfo.InvariantCulture);
        }
        RedrawRegion();
    }

    private void OnRegionDown(object sender, MouseButtonEventArgs e)
    {
        _drawing = true;
        _from = _map.ToImage(e.GetPosition(Overlay));
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnRegionMove(object sender, MouseEventArgs e)
    {
        if (_drawing)
        {
            RedrawRegion(Box(_from, _map.ToImage(e.GetPosition(Overlay))));
        }
    }

    private void OnRegionUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing || _region is null)
        {
            return;
        }
        _drawing = false;
        Overlay.ReleaseMouseCapture();
        var to = _map.ToImage(e.GetPosition(Overlay));
        var dragged = PixelBox.FromDrag(_from, to, _region.FrameWidth, _region.FrameHeight);
        // A click is not a box: the engine would blur or keep two pixels.
        if (dragged.Width >= 2 && dragged.Height >= 2)
        {
            _region.Set(Box(_from, to));
            WriteRegionFields();
            Recheck();
        }
        RedrawRegion();
        Describe();
        e.Handled = true;
    }

    /// <summary>
    /// A drag as a box in frame pixels. A redaction grows to what the preview
    /// can show (PixelBox.Covering), so the cover drawn is what is destroyed.
    /// </summary>
    private Int32Rect Box(Point from, Point to) => _region is { Covers: true }
        ? PixelBox.Covering(_map, from, to)
        : PixelBox.FromDrag(from, to, _map.ImageWidth, _map.ImageHeight);

    private void WriteRegionFields()
    {
        if (_region?.Region is not { } box)
        {
            return;
        }
        _writing = true;
        try
        {
            _x!.Text = box.X.ToString(CultureInfo.InvariantCulture);
            _y!.Text = box.Y.ToString(CultureInfo.InvariantCulture);
            _w!.Text = box.Width.ToString(CultureInfo.InvariantCulture);
            _h!.Text = box.Height.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _writing = false;
        }
    }

    private void OnRegionFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_writing || _region is null)
        {
            return;
        }
        // Half typed is not a mistake yet (TimelineFields says nothing until
        // all four are there); applying with one missing is.
        Recheck();
        RedrawRegion();
        Describe();
    }

    private void RedrawRegion(Int32Rect? pending = null)
    {
        if (_region is null)
        {
            return;
        }
        Overlay.Children.Clear();
        if (pending is { } drawing)
        {
            ShowBox(drawing, 0.6);
        }
        else if (_region.Region is { } box)
        {
            ShowBox(box, 1.0);
        }
    }

    private void ShowBox(Int32Rect box, double opacity)
    {
        var view = _map.ToView(box.X, box.Y, box.Width, box.Height);
        var shape = new Rectangle
        {
            Width = view.Width,
            Height = view.Height,
            Stroke = (Brush)FindResource("EditorAccent"),
            StrokeThickness = 1.4,
            Fill = _region!.Covers
                ? new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x14))
                : Brushes.Transparent,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(shape, view.X);
        Canvas.SetTop(shape, view.Y);
        Overlay.Children.Add(shape);
    }

    // --- quality ------------------------------------------------------------------

    private void BuildQuality()
    {
        var choices = _schema.Choices("quality");
        if (choices.Count == 0)
        {
            return;
        }
        QualityPanel.Visibility = Visibility.Visible;
        _quality = _schema.Text("quality", choices[0]);
        QualityPanel.Children.Add(new TextBlock
        {
            Text = _schema.Label("quality", "Quality"),
            Style = (Style)FindResource("EditorLabel"),
        });
        foreach (var choice in choices)
        {
            var label = choice.Length == 0 ? choice : char.ToUpperInvariant(choice[0]) + choice[1..];
            var button = new RadioButton
            {
                Content = label,
                Style = (Style)FindResource("EditorToggle"),
                GroupName = "TimelineQuality",
                IsChecked = choice == _quality,
                Tag = choice,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"TimelineQuality{label}");
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Checked += (sender, _) => _quality = (string)((RadioButton)sender).Tag;
            _qualities.Add(button);
            QualityPanel.Children.Add(button);
        }
    }

    // --- closing --------------------------------------------------------------------

    private void Shut()
    {
        _clock.Stop();
        _settle.Stop();
        _closing.Cancel();
        try
        {
            Player.Stop();
            Player.Close();
        }
        catch (Exception error)
        {
            Log.Write($"{_id}: cannot close the player", error);
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => ApplyAndClose();

    private void OnCancel(object sender, RoutedEventArgs e) => CancelAndClose();
}
