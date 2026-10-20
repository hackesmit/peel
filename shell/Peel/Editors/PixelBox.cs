// A rectangle the user dragged, in image pixels, fit for the engine.
//
// Two drags out of three are drawn right to left or bottom to top, and both
// image.redact and image.annotate refuse a region with no area (w or h below
// one). Normalising and clamping in one place keeps that out of every window.
using System;
using System.Windows;

namespace Peel.Editors;

internal static class PixelBox
{
    /// <summary>
    /// The box between two points, whichever way round they were dragged,
    /// clipped to the image and never smaller than one pixel each way. The
    /// points are continuous image coordinates (PreviewMap.ToImage), and the
    /// rounding only ever grows the box: the near edges are floored and the
    /// far edges ceiled, so every pixel the drag touched is inside it. For a
    /// redaction that is the whole point: a box that rounds inward leaves a
    /// strip the user saw covered.
    /// </summary>
    internal static Int32Rect FromDrag(Point from, Point to, int imageWidth, int imageHeight)
    {
        imageWidth = Math.Max(1, imageWidth);
        imageHeight = Math.Max(1, imageHeight);
        var left = Edge(Math.Floor(Math.Min(from.X, to.X)), imageWidth);
        var top = Edge(Math.Floor(Math.Min(from.Y, to.Y)), imageHeight);
        var right = Edge(Math.Ceiling(Math.Max(from.X, to.X)), imageWidth);
        var bottom = Edge(Math.Ceiling(Math.Max(from.Y, to.Y)), imageHeight);
        (left, right) = AtLeastOne(left, right, imageWidth);
        (top, bottom) = AtLeastOne(top, bottom, imageHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The region grown, never shrunk, until it measures at least one device
    /// independent unit each way on the preview, so that what the window
    /// draws over it is exactly what is sent. Without this a region of 2 by 2
    /// pixels on a 0.19 scale preview draws 0.37 units wide, and any window
    /// that bumps it up to something visible draws a cover over pixels the
    /// engine never touches. Grown around its centre, and pushed back inside
    /// the image at an edge.
    /// </summary>
    internal static Int32Rect AtLeastVisible(Int32Rect box, double scale, int imageWidth, int imageHeight)
    {
        imageWidth = Math.Max(1, imageWidth);
        imageHeight = Math.Max(1, imageHeight);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return box;
        }
        var least = (int)Math.Ceiling(1.0 / scale - 1e-9);
        var (x, w) = Grow(box.X, box.Width, least, imageWidth);
        var (y, h) = Grow(box.Y, box.Height, least, imageHeight);
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>
    /// A drag on a preview as the region to destroy: FromDrag, then
    /// AtLeastVisible at that preview's scale. The redact window draws and
    /// sends this one rectangle.
    /// </summary>
    internal static Int32Rect Covering(PreviewMap map, Point from, Point to) =>
        AtLeastVisible(FromDrag(from, to, map.ImageWidth, map.ImageHeight), map.Scale, map.ImageWidth, map.ImageHeight);

    private static (int Start, int Length) Grow(int start, int length, int least, int limit)
    {
        if (length >= least)
        {
            return (start, length);
        }
        var target = Math.Min(least, limit);
        var extra = target - length;
        var newStart = start - extra / 2;
        newStart = Math.Clamp(newStart, 0, limit - target);
        // Pushing back inside the image must not uncover the original box.
        newStart = Math.Min(newStart, start);
        var end = Math.Max(newStart + target, start + length);
        return (newStart, end - newStart);
    }

    private static int Edge(double value, int limit) =>
        double.IsFinite(value) ? (int)Math.Clamp(value, 0, limit) : 0;

    private static (int Low, int High) AtLeastOne(int low, int high, int limit)
    {
        if (high > low)
        {
            return (low, high);
        }
        // A click, not a drag: one pixel, inside the image.
        low = Math.Min(low, limit - 1);
        return (low, low + 1);
    }
}
