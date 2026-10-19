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
    /// clipped to the image and never smaller than one pixel each way.
    /// </summary>
    internal static Int32Rect FromDrag(Point from, Point to, int imageWidth, int imageHeight)
    {
        var left = (int)Math.Round(Math.Min(from.X, to.X));
        var top = (int)Math.Round(Math.Min(from.Y, to.Y));
        var right = (int)Math.Round(Math.Max(from.X, to.X));
        var bottom = (int)Math.Round(Math.Max(from.Y, to.Y));
        left = Math.Clamp(left, 0, Math.Max(0, imageWidth - 1));
        top = Math.Clamp(top, 0, Math.Max(0, imageHeight - 1));
        right = Math.Clamp(right, left + 1, imageWidth);
        bottom = Math.Clamp(bottom, top + 1, imageHeight);
        return new Int32Rect(left, top, right - left, bottom - top);
    }
}
