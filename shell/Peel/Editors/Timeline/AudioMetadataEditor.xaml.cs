// Metadata for a sound file: title, artist, album.
//
// The form arrives filled with what the file already says, read by ffprobe
// (MediaProbe), the reader av.audio_metadata itself checks its output with.
// Only the fields the user changed are sent, and av.audio_metadata copies the
// stream, so saving cannot cost quality.
//
// The fields are the engine's: one row per string parameter the entry
// declares, labelled with the engine's label. Emptying a field is refused
// here: av.audio_metadata writes the values it is given and then checks each
// one landed, and an empty value is not a tag ffmpeg keeps, so the job would
// fail after the window had closed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Peel.Engine;

namespace Peel.Editors.Timeline;

internal partial class AudioMetadataEditor : EditorWindowBase
{
    private static readonly string[] Fallback = { "title", "artist", "album" };

    private readonly List<(string Name, string Label, string Was, TextBox Box)> _fields = new();

    internal AudioMetadataEditor(ActionEntry entry, string path, MediaInfo info, ParamSchema schema)
    {
        InitializeComponent();
        var name = System.IO.Path.GetFileName(path);
        Title = $"Metadata of {name}";
        Subject.Text = $"{name}, {Timecode.Format(info.Duration)}";

        var names = entry.Params.Where(spec => spec.Type == "string" && spec.Name.Length > 0)
            .Select(spec => spec.Name).ToList();
        if (names.Count == 0)
        {
            names = Fallback.ToList();
        }
        foreach (var field in names)
        {
            var label = schema.Label(field, char.ToUpperInvariant(field[0]) + field[1..]);
            var was = info.Tag(field);
            var row = Form.RowDefinitions.Count;
            Form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("EditorLabel"),
                Margin = new Thickness(0, 4, 8, 4),
            };
            Grid.SetRow(caption, row);
            var box = new TextBox
            {
                Text = was,
                Style = (Style)FindResource("EditorField"),
                Margin = new Thickness(0, 4, 0, 4),
            };
            var id = "AudioMeta" + char.ToUpperInvariant(field[0]) + field[1..];
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, id);
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            Form.Children.Add(caption);
            Form.Children.Add(box);
            _fields.Add((field, label, was, box));
        }
        Loaded += (_, _) =>
        {
            if (_fields.Count > 0)
            {
                _fields[0].Box.Focus();
                _fields[0].Box.SelectAll();
            }
        };
    }

    protected override IReadOnlyDictionary<string, string>? Collect()
    {
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, label, was, box) in _fields)
        {
            var now = box.Text.Trim();
            if (now == was.Trim())
            {
                continue;
            }
            if (now.Length == 0)
            {
                Problem.Text = $"Peel cannot remove the {label.ToLowerInvariant()}. Type a new one, or put the old one back.";
                return null;
            }
            sent[name] = now;
        }
        if (sent.Count == 0)
        {
            Problem.Text = "Change a field first, or press Escape to leave the file as it is.";
            return null;
        }
        return sent;
    }

    private void OnApply(object sender, RoutedEventArgs e) => ApplyAndClose();

    private void OnCancel(object sender, RoutedEventArgs e) => CancelAndClose();
}
