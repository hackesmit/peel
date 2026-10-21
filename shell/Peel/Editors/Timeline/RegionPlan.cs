// The rectangle a video crop or a video redaction is about, in frame pixels.
//
// The frame is the engine's own snapshot (FrameGrab), so its pixel size is
// the frame size av.video_crop and av.video_redact check a box against, and a
// box inside it is never refused for leaving the frame.
//
// One rule the image editors do not need: the video encoder wants even sizes,
// and the engine floors an odd width or height to even (av._even) without
// saying so. For a crop that only costs a pixel, so a crop box is evened DOWN
// here and the window shows the size the output will really have. For a
// redaction a floored box would leave the last column or row of what the user
// covered in the clear, so a redaction box is evened UP, pushed back inside
// the frame at an edge: every pixel the user covered is still blurred.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace Peel.Editors.Timeline;

internal sealed class RegionPlan
{
    internal RegionPlan(int frameWidth, int frameHeight, bool covers)
    {
        FrameWidth = Math.Max(1, frameWidth);
        FrameHeight = Math.Max(1, frameHeight);
        Covers = covers;
    }

    internal int FrameWidth { get; }

    internal int FrameHeight { get; }

    /// <summary>True for a redaction (even up, never lose a covered pixel), false for a crop (even down).</summary>
    internal bool Covers { get; }

    internal Int32Rect? Region { get; private set; }

    internal void Set(Int32Rect box) => Region = Even(Clip(box));

    internal void Clear() => Region = null;

    /// <summary>
    /// The four typed numbers as the region, or false with the reason. Nothing
    /// changes on a refusal, so the box on screen is still the last good one.
    /// </summary>
    internal bool TrySet(string x, string y, string w, string h, out string problem)
    {
        if (!Whole(x, out var left) || !Whole(y, out var top) || !Whole(w, out var width) || !Whole(h, out var height))
        {
            problem = "Write whole numbers of pixels in all four boxes.";
            return false;
        }
        if (width < 2 || height < 2)
        {
            problem = "The box has to be at least 2 pixels wide and tall.";
            return false;
        }
        if (left + width > FrameWidth || top + height > FrameHeight)
        {
            problem = string.Format(CultureInfo.InvariantCulture,
                "A {0} by {1} box at {2},{3} does not fit the {4} by {5} frame.",
                width, height, left, top, FrameWidth, FrameHeight);
            return false;
        }
        Region = Even(new Int32Rect(left, top, width, height));
        problem = string.Empty;
        return true;
    }

    internal string? Problem() => Region is null
        ? (Covers ? "Drag over what has to be blurred first." : "Drag the part of the frame to keep first.")
        : null;

    internal Dictionary<string, string> ToParams()
    {
        var box = Region ?? new Int32Rect(0, 0, FrameWidth, FrameHeight);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x"] = box.X.ToString(CultureInfo.InvariantCulture),
            ["y"] = box.Y.ToString(CultureInfo.InvariantCulture),
            ["w"] = box.Width.ToString(CultureInfo.InvariantCulture),
            ["h"] = box.Height.ToString(CultureInfo.InvariantCulture),
        };
    }

    internal string Describe() => Region is { } box
        ? string.Format(CultureInfo.InvariantCulture, "{0} by {1} at {2},{3}", box.Width, box.Height, box.X, box.Y)
        : "no box yet";

    private Int32Rect Clip(Int32Rect box)
    {
        var left = Math.Clamp(box.X, 0, FrameWidth - 1);
        var top = Math.Clamp(box.Y, 0, FrameHeight - 1);
        var right = Math.Clamp(box.X + Math.Max(1, box.Width), left + 1, FrameWidth);
        var bottom = Math.Clamp(box.Y + Math.Max(1, box.Height), top + 1, FrameHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private Int32Rect Even(Int32Rect box)
    {
        var (x, w) = EvenSpan(box.X, box.Width, FrameWidth);
        var (y, h) = EvenSpan(box.Y, box.Height, FrameHeight);
        return new Int32Rect(x, y, w, h);
    }

    private (int Start, int Length) EvenSpan(int start, int length, int limit)
    {
        int grown;
        if (length % 2 == 0)
        {
            grown = Math.Max(2, length);
        }
        else if (!Covers)
        {
            grown = Math.Max(2, length - 1);
        }
        else if (start + length + 1 <= limit || start > 0)
        {
            // Grow by one: to the right if there is room, else to the left.
            grown = length + 1;
        }
        else
        {
            // An odd frame covered edge to edge cannot be evened up; the
            // engine floors it, and that is the one pixel it drops.
            return (start, length);
        }
        grown = Math.Min(grown, limit);
        return (Math.Clamp(start, 0, limit - grown), grown);
    }

    private static bool Whole(string text, out int value) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
