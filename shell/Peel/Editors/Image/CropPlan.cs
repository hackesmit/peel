// The crop rectangle, in image pixels, with no window attached.
//
// The window owns handles and the mouse; this owns the rule that the box stays
// inside the picture and keeps its aspect ratio. The engine refuses a box that
// leaves the image (image.crop raises on x + w > width), so clamping is not a
// nicety: an unclamped drag past the edge of a photo is a failed job and a
// toast the user cannot act on.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace Peel.Editors.Image;

internal enum CropHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Body,
}

internal sealed record CropAspect(string Label, double? Ratio);

internal sealed class CropPlan
{
    private CropHandle _handle = CropHandle.None;
    private Int32Rect _started;
    private Point _from;

    internal CropPlan(int imageWidth, int imageHeight)
    {
        ImageWidth = Math.Max(1, imageWidth);
        ImageHeight = Math.Max(1, imageHeight);
        Reset();
    }

    internal int ImageWidth { get; }

    internal int ImageHeight { get; }

    internal int X { get; private set; }

    internal int Y { get; private set; }

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    /// <summary>Width over height, or null when the box is free.</summary>
    internal double? Aspect { get; private set; }

    internal bool CoversWholeImage => X == 0 && Y == 0 && Width == ImageWidth && Height == ImageHeight;

    /// <summary>The presets the window offers. Original is this image's own ratio.</summary>
    internal IReadOnlyList<CropAspect> Aspects => new[]
    {
        new CropAspect("Free", null),
        new CropAspect("Original", (double)ImageWidth / ImageHeight),
        new CropAspect("1:1", 1.0),
        new CropAspect("4:3", 4.0 / 3.0),
        new CropAspect("3:2", 3.0 / 2.0),
        new CropAspect("16:9", 16.0 / 9.0),
        new CropAspect("3:4", 3.0 / 4.0),
        new CropAspect("9:16", 9.0 / 16.0),
    };

    internal void Reset()
    {
        Aspect = null;
        X = 0;
        Y = 0;
        Width = ImageWidth;
        Height = ImageHeight;
    }

    /// <summary>
    /// Switch the ratio, keeping the centre of the box where it is and the box
    /// as large as both the old box and the image allow.
    /// </summary>
    internal void SetAspect(double? aspect)
    {
        Aspect = aspect is > 0 ? aspect : null;
        if (Aspect is not { } ratio)
        {
            return;
        }
        var centreX = X + Width / 2.0;
        var centreY = Y + Height / 2.0;
        double width = Width;
        double height = Height;
        if (width / height > ratio)
        {
            width = height * ratio;
        }
        else
        {
            height = width / ratio;
        }
        (width, height) = FitRatio(width, height, ratio);
        Place(centreX - width / 2, centreY - height / 2, width, height);
    }

    /// <summary>
    /// Set the box from the numeric fields. Returns the box it settled on,
    /// which is the asked for box clamped into the image.
    /// </summary>
    internal void Set(int x, int y, int width, int height)
    {
        Place(x, y, width, height);
    }

    /// <summary>Remember where a drag began, so every move is measured from it.</summary>
    internal void Begin(CropHandle handle, Point imagePoint)
    {
        _handle = handle;
        _started = new Int32Rect(X, Y, Width, Height);
        _from = imagePoint;
    }

    internal bool Dragging => _handle != CropHandle.None;

    internal void End() => _handle = CropHandle.None;

    /// <summary>Continue the drag begun at <see cref="Begin"/> to this pixel.</summary>
    internal void DragTo(Point imagePoint)
    {
        if (_handle == CropHandle.None)
        {
            return;
        }
        var dx = imagePoint.X - _from.X;
        var dy = imagePoint.Y - _from.Y;
        if (_handle == CropHandle.Body)
        {
            // Moving never changes the size, so it clamps by sliding the box
            // back inside rather than by shrinking it.
            var x = Math.Clamp(_started.X + dx, 0, ImageWidth - _started.Width);
            var y = Math.Clamp(_started.Y + dy, 0, ImageHeight - _started.Height);
            X = (int)Math.Round(x);
            Y = (int)Math.Round(y);
            return;
        }

        double left = _started.X;
        double top = _started.Y;
        double right = _started.X + _started.Width;
        double bottom = _started.Y + _started.Height;
        if (_handle is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft)
        {
            left = _started.X + dx;
        }
        if (_handle is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight)
        {
            right = _started.X + _started.Width + dx;
        }
        if (_handle is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight)
        {
            top = _started.Y + dy;
        }
        if (_handle is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight)
        {
            bottom = _started.Y + _started.Height + dy;
        }
        // Dragging an edge past its opposite flips the box rather than
        // collapsing it, which is what every other editor on Windows does.
        var width = Math.Abs(right - left);
        var height = Math.Abs(bottom - top);
        var anchorX = Math.Min(left, right);
        var anchorY = Math.Min(top, bottom);

        if (Aspect is { } ratio)
        {
            if (_handle is CropHandle.Left or CropHandle.Right)
            {
                height = width / ratio;
                anchorY = _started.Y + _started.Height / 2.0 - height / 2;
            }
            else if (_handle is CropHandle.Top or CropHandle.Bottom)
            {
                width = height * ratio;
                anchorX = _started.X + _started.Width / 2.0 - width / 2;
            }
            else
            {
                // A corner: the side the pointer moved most decides the size,
                // and the box grows away from the corner that stayed put.
                if (width / Math.Max(1e-6, height) > ratio)
                {
                    height = width / ratio;
                }
                else
                {
                    width = height * ratio;
                }
                var anchoredRight = _handle is CropHandle.TopLeft or CropHandle.BottomLeft;
                var anchoredBottom = _handle is CropHandle.TopLeft or CropHandle.TopRight;
                anchorX = anchoredRight ? _started.X + _started.Width - width : _started.X;
                anchorY = anchoredBottom ? _started.Y + _started.Height - height : _started.Y;
            }
            (width, height) = FitRatio(width, height, ratio);
        }
        Place(anchorX, anchorY, width, height);
    }

    /// <summary>
    /// The parameters image.crop declares: x, y, w and h, as whole pixels.
    /// Nothing else is sent, because nothing else is in the schema.
    /// </summary>
    internal Dictionary<string, string> ToParams() => new(StringComparer.Ordinal)
    {
        ["x"] = X.ToString(CultureInfo.InvariantCulture),
        ["y"] = Y.ToString(CultureInfo.InvariantCulture),
        ["w"] = Width.ToString(CultureInfo.InvariantCulture),
        ["h"] = Height.ToString(CultureInfo.InvariantCulture),
    };

    internal string Describe() =>
        string.Format(CultureInfo.InvariantCulture, "{0} by {1} at {2},{3}", Width, Height, X, Y);

    /// <summary>Shrink a ratio-locked size until it fits the image.</summary>
    private (double Width, double Height) FitRatio(double width, double height, double ratio)
    {
        if (width > ImageWidth)
        {
            width = ImageWidth;
            height = width / ratio;
        }
        if (height > ImageHeight)
        {
            height = ImageHeight;
            width = height * ratio;
        }
        return (Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// The one place the box is written. Rounds to whole pixels, keeps at least
    /// one pixel of each side, and pushes the box inside the image.
    /// </summary>
    private void Place(double x, double y, double width, double height)
    {
        var w = (int)Math.Round(Math.Clamp(width, 1, ImageWidth));
        var h = (int)Math.Round(Math.Clamp(height, 1, ImageHeight));
        w = Math.Clamp(w, 1, ImageWidth);
        h = Math.Clamp(h, 1, ImageHeight);
        var left = (int)Math.Round(Math.Clamp(x, 0, ImageWidth - w));
        var top = (int)Math.Round(Math.Clamp(y, 0, ImageHeight - h));
        X = left;
        Y = top;
        Width = w;
        Height = h;
    }
}
