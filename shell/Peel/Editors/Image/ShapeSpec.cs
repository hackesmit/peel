// One annotation, in the shape the engine's schema describes.
//
// image.annotate takes a `shapes` JSON list and its help text is the contract:
// "pen: points, color, width. arrow: from, to, color, width. rect: x, y, w, h,
// color, width, fill. text: x, y, text, color, size." Writing that JSON here,
// with a writer rather than string concatenation, is what keeps a file name or
// a caption with a quote in it from turning into a parse error.
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;

namespace Peel.Editors.Image;

internal enum ShapeKind
{
    Pen,
    Arrow,
    Rect,
    Text,
}

internal sealed class ShapeSpec
{
    internal ShapeKind Kind { get; init; }

    /// <summary>A pen stroke, in image pixels. The engine needs two or more.</summary>
    internal IReadOnlyList<Point> Points { get; init; } = Array.Empty<Point>();

    internal Point From { get; init; }

    internal Point To { get; init; }

    internal Int32Rect Box { get; init; }

    internal string Text { get; init; } = string.Empty;

    /// <summary>"#RRGGBB", which the engine parses with ImageColor.</summary>
    internal string Colour { get; init; } = "#ff3b30";

    internal int Width { get; init; } = 3;

    internal int FontSize { get; init; } = 16;

    /// <summary>A filled box, or null for an outline.</summary>
    internal string? Fill { get; init; }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", Kind switch
        {
            ShapeKind.Pen => "pen",
            ShapeKind.Arrow => "arrow",
            ShapeKind.Rect => "rect",
            _ => "text",
        });
        writer.WriteString("color", Colour);
        writer.WriteNumber("width", Math.Max(1, Width));
        switch (Kind)
        {
            case ShapeKind.Pen:
                writer.WriteStartArray("points");
                foreach (var point in Points)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue((int)Math.Round(point.X));
                    writer.WriteNumberValue((int)Math.Round(point.Y));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                break;
            case ShapeKind.Arrow:
                WritePoint(writer, "from", From);
                WritePoint(writer, "to", To);
                break;
            case ShapeKind.Rect:
                writer.WriteNumber("x", Box.X);
                writer.WriteNumber("y", Box.Y);
                writer.WriteNumber("w", Math.Max(1, Box.Width));
                writer.WriteNumber("h", Math.Max(1, Box.Height));
                if (Fill is not null)
                {
                    writer.WriteString("fill", Fill);
                }
                break;
            default:
                writer.WriteNumber("x", (int)Math.Round(From.X));
                writer.WriteNumber("y", (int)Math.Round(From.Y));
                writer.WriteString("text", Text);
                writer.WriteNumber("size", Math.Max(1, FontSize));
                break;
        }
        writer.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter writer, string name, Point point)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue((int)Math.Round(point.X));
        writer.WriteNumberValue((int)Math.Round(point.Y));
        writer.WriteEndArray();
    }
}
