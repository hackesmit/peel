// Where a pixel of the file sits on the screen, and back.
//
// Every editor that lets the user point at the image (crop, annotate, redact)
// has the same bug waiting for it: the preview is not the image. It is fitted
// into the window, letterboxed, and on George it is then scaled again by the
// monitor's DPI. A click read as a preview coordinate and sent to the engine as
// an image coordinate crops the wrong rectangle, and nothing about the result
// looks wrong enough to notice. So the mapping lives here on its own, in one
// place, with tests, and the windows never do the arithmetic themselves.
//
// Device independent units in, image pixels out. WPF hands mouse positions in
// those units already, so DPI is not this type's problem.
using System;
using System.Windows;

namespace Peel.Editors;

internal readonly struct PreviewMap
{
    /// <summary>
    /// A small image is enlarged so it can be worked on, but only so far: past
    /// this a single pixel becomes a tile and pointing at it is guesswork.
    /// </summary>
    internal const double MaxZoom = 8.0;

    internal PreviewMap(int imageWidth, int imageHeight, double viewWidth, double viewHeight)
    {
        ImageWidth = Math.Max(1, imageWidth);
        ImageHeight = Math.Max(1, imageHeight);
        var fit = Math.Min(viewWidth / ImageWidth, viewHeight / ImageHeight);
        Scale = !double.IsFinite(fit) || fit <= 0 ? 1.0 : Math.Min(fit, MaxZoom);
    }

    internal int ImageWidth { get; }

    internal int ImageHeight { get; }

    /// <summary>Preview units for one image pixel.</summary>
    internal double Scale { get; }

    /// <summary>The size the preview is drawn at, which the window sets on the Image.</summary>
    internal Size ViewSize => new(ImageWidth * Scale, ImageHeight * Scale);

    /// <summary>
    /// A point on the preview as an image pixel, clamped into the image. The
    /// clamp matters: the user drags past the edge of the picture constantly,
    /// and the engine refuses a crop box that leaves the image.
    /// </summary>
    internal Point ToImage(Point view) => new(
        Math.Clamp(Math.Floor(view.X / Scale), 0, ImageWidth - 1),
        Math.Clamp(Math.Floor(view.Y / Scale), 0, ImageHeight - 1));

    /// <summary>The same, unclamped, for a drag that is allowed to overshoot.</summary>
    internal Point ToImageRaw(Point view) => new(view.X / Scale, view.Y / Scale);

    /// <summary>An image rectangle as a preview rectangle.</summary>
    internal Rect ToView(int x, int y, int width, int height) =>
        new(x * Scale, y * Scale, Math.Max(0, width) * Scale, Math.Max(0, height) * Scale);
}
