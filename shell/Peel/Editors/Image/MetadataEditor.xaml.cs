// Metadata: what the file says about itself, and what to write instead.
//
// This is the one editor that asks the engine a question first. The host runs
// `image.metadata --param action=view`, which writes nothing and returns the
// tags, and hands the answer here. Save sends action=set; Remove all tags
// sends action=strip. Neither touches the original: the engine writes a new
// file next to it ("name (Metadata).jpg"), which is why nothing here asks the
// user to confirm.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Peel.Editors.Image;

internal partial class MetadataEditor : EditorWindowBase
{
    private readonly MetadataFields _fields;
    private readonly bool _canClearLocation;
    private bool _stripping;

    internal MetadataEditor(string path, MetadataFields fields, ParamSchema schema)
    {
        InitializeComponent();
        _fields = fields;
        var name = fields.File.Length > 0 ? fields.File : System.IO.Path.GetFileName(path);
        Title = $"Metadata of {name}";
        Subject.Text = fields.Width > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0}, {1}, {2} by {3} pixels",
                name, fields.Format, fields.Width, fields.Height)
            : name;

        _canClearLocation = schema.Declares("clear_location");
        LabelTitle.Text = schema.Label("title", "Title");
        LabelAuthor.Text = schema.Label("author", "Author");
        LabelLatitude.Text = schema.Label("latitude", "Latitude");
        LabelLongitude.Text = schema.Label("longitude", "Longitude");

        FieldTitle.Text = fields.Title;
        FieldAuthor.Text = fields.Author;
        FieldLatitude.Text = fields.Latitude;
        FieldLongitude.Text = fields.Longitude;
        foreach (var tag in fields.Tags)
        {
            TagList.Children.Add(Row(tag.Key, tag.Value));
        }
        if (fields.Tags.Count == 0)
        {
            Hint.Text = "This file carries no tags yet. "
                + "Enter saves, Escape cancels. Peel writes a new file and never changes this one.";
        }
        Loaded += (_, _) =>
        {
            FieldTitle.Focus();
            FieldTitle.SelectAll();
        };
    }

    /// <summary>One tag: its name, then its value, both as plain text.</summary>
    private static UIElement Row(string name, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var key = new TextBlock
        {
            Text = name,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xA7, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(key, $"MetaTag{name}");
        var said = new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(said, 1);
        row.Children.Add(key);
        row.Children.Add(said);
        return row;
    }

    protected override IReadOnlyDictionary<string, string>? Collect()
    {
        if (_stripping)
        {
            return MetadataFields.StripParams();
        }
        _fields.Title = FieldTitle.Text;
        _fields.Author = FieldAuthor.Text;
        _fields.Latitude = FieldLatitude.Text;
        _fields.Longitude = FieldLongitude.Text;
        if (_fields.TrySet(out var parameters, out var problem, _canClearLocation))
        {
            return parameters;
        }
        // The engine would refuse this too, but a sentence in the window beats
        // a failed toast after the wheel has gone.
        Problem.Text = problem;
        return null;
    }

    protected override bool HandlesEnter(IInputElement? focused) => false;

    private void OnStrip(object sender, RoutedEventArgs e)
    {
        _stripping = true;
        ApplyAndClose();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        _stripping = false;
        ApplyAndClose();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CancelAndClose();
}
