// The regions to hide, and how. Both come from image.redact's own schema: the
// window offers whatever `mode` choices the engine declares, so a third way of
// hiding added in the engine appears here without a change.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Peel.Editors.Image;

internal sealed class RedactPlan
{
    private static readonly string[] FallbackModes = { "blur", "box" };

    private readonly List<Int32Rect> _regions = new();

    internal RedactPlan(ParamSchema schema)
    {
        Modes = schema.Choices("mode") is { Count: > 0 } modes ? modes : FallbackModes;
        Mode = schema.Text("mode", Modes[0]);
    }

    internal IReadOnlyList<string> Modes { get; }

    internal string Mode { get; set; }

    internal IReadOnlyList<Int32Rect> Regions => _regions;

    internal bool Any => _regions.Count > 0;

    internal void Add(Int32Rect region) => _regions.Add(region);

    internal void Undo()
    {
        if (_regions.Count > 0)
        {
            _regions.RemoveAt(_regions.Count - 1);
        }
    }

    internal void Clear() => _regions.Clear();

    internal string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var region in _regions)
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", region.X);
                writer.WriteNumber("y", region.Y);
                writer.WriteNumber("w", Math.Max(1, region.Width));
                writer.WriteNumber("h", Math.Max(1, region.Height));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal Dictionary<string, string> ToParams() => new(StringComparer.Ordinal)
    {
        ["regions"] = ToJson(),
        ["mode"] = Mode,
    };

    internal string Describe() => _regions.Count == 1
        ? string.Format(CultureInfo.InvariantCulture, "1 region, {0}", Mode)
        : string.Format(CultureInfo.InvariantCulture, "{0} regions, {1}", _regions.Count, Mode);
}
