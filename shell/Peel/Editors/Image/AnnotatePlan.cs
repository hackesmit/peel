// The list of annotations, and the one parameter image.annotate asks for.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Peel.Editors.Image;

internal sealed class AnnotatePlan
{
    private readonly List<ShapeSpec> _shapes = new();

    internal IReadOnlyList<ShapeSpec> Shapes => _shapes;

    internal bool Any => _shapes.Count > 0;

    internal void Add(ShapeSpec shape) => _shapes.Add(shape);

    /// <summary>Undo is the last shape drawn, which is all the bead asks for.</summary>
    internal ShapeSpec? Undo()
    {
        if (_shapes.Count == 0)
        {
            return null;
        }
        var last = _shapes[^1];
        _shapes.RemoveAt(_shapes.Count - 1);
        return last;
    }

    internal void Clear() => _shapes.Clear();

    internal string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var shape in _shapes)
            {
                shape.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal Dictionary<string, string> ToParams() => new(StringComparer.Ordinal)
    {
        ["shapes"] = ToJson(),
    };
}
