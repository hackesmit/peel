// What image.metadata read, and what to send back.
//
// The Metadata window is the one editor that asks the engine a question before
// it shows anything: `action=view` writes no file and returns the tags under
// data.metadata, which fills the form. Save sends `action=set` and Strip sends
// `action=strip`. The engine's own rules are repeated here only as checks the
// user can see before the job runs, so a mistake is a sentence in the window
// rather than a failed toast: a location needs both halves, and set needs at
// least one of title, author or location.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Peel.Editors.Image;

internal sealed class MetadataFields
{
    internal string File { get; private set; } = string.Empty;

    internal string Format { get; private set; } = string.Empty;

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal string Title { get; set; } = string.Empty;

    internal string Author { get; set; } = string.Empty;

    /// <summary>As typed, so an unreadable number can be reported as typed.</summary>
    internal string Latitude { get; set; } = string.Empty;

    internal string Longitude { get; set; } = string.Empty;

    /// <summary>Every tag the engine could name, for the read only list.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> Tags { get; private set; } =
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>
    /// Read the answer to `action=view`. Anything missing stays empty: a
    /// photo with no tags at all is the normal case, not an error.
    /// </summary>
    internal static MetadataFields FromView(JsonElement data)
    {
        var fields = new MetadataFields();
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object)
        {
            return fields;
        }
        fields.File = Text(metadata, "file");
        fields.Format = Text(metadata, "format");
        fields.Width = Whole(metadata, "width");
        fields.Height = Whole(metadata, "height");
        fields.Title = Text(metadata, "title");
        fields.Author = Text(metadata, "author");
        fields.Latitude = Decimal(metadata, "latitude");
        fields.Longitude = Decimal(metadata, "longitude");
        if (metadata.TryGetProperty("exif", out var exif) && exif.ValueKind == JsonValueKind.Object)
        {
            fields.Tags = exif.EnumerateObject()
                .Select(tag => new KeyValuePair<string, string>(tag.Name, Scalar(tag.Value)))
                .OrderBy(tag => tag.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return fields;
    }

    /// <summary>
    /// The parameters for Save, or the reason the form is not ready. Both
    /// halves of the location or neither, and something to write.
    /// </summary>
    internal bool TrySet(out Dictionary<string, string> parameters, out string problem)
    {
        parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["action"] = "set" };
        problem = string.Empty;
        var title = Title.Trim();
        var author = Author.Trim();
        var hasLatitude = TryNumber(Latitude, out var latitude);
        var hasLongitude = TryNumber(Longitude, out var longitude);
        if (Latitude.Trim().Length > 0 && !hasLatitude)
        {
            problem = $"\"{Latitude.Trim()}\" is not a latitude. Use a number such as 32.4567.";
            return false;
        }
        if (Longitude.Trim().Length > 0 && !hasLongitude)
        {
            problem = $"\"{Longitude.Trim()}\" is not a longitude. Use a number such as -116.6123.";
            return false;
        }
        if (hasLatitude != hasLongitude)
        {
            problem = "A location needs both a latitude and a longitude.";
            return false;
        }
        if (hasLatitude && (latitude < -90 || latitude > 90))
        {
            problem = "A latitude runs from -90 to 90.";
            return false;
        }
        if (hasLongitude && (longitude < -180 || longitude > 180))
        {
            problem = "A longitude runs from -180 to 180.";
            return false;
        }
        if (title.Length == 0 && author.Length == 0 && !hasLatitude)
        {
            problem = "Give a title, an author or a location to save.";
            return false;
        }
        if (title.Length > 0)
        {
            parameters["title"] = title;
        }
        if (author.Length > 0)
        {
            parameters["author"] = author;
        }
        if (hasLatitude)
        {
            parameters["latitude"] = latitude.ToString("0.########", CultureInfo.InvariantCulture);
            parameters["longitude"] = longitude.ToString("0.########", CultureInfo.InvariantCulture);
        }
        return true;
    }

    internal static Dictionary<string, string> StripParams() => new(StringComparer.Ordinal)
    {
        ["action"] = "strip",
    };

    internal static Dictionary<string, string> ViewParams() => new(StringComparer.Ordinal)
    {
        ["action"] = "view",
    };

    /// <summary>
    /// A number the user typed. Invariant only: George is an en-US machine, the
    /// engine parses with float(), and accepting a comma here would send the
    /// engine a string it reads as a different number.
    /// </summary>
    internal static bool TryNumber(string raw, out double value) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Whole(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
         && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string Decimal(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
         && value.TryGetDouble(out var number)
            ? number.ToString("0.########", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText(),
    };
}
