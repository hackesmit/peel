// The hub's drawing: a light disc with a white pill that names the petal being
// aimed at, empty when nothing is. Shared by the keyboard wheel, which draws
// it in its own window, and the drag wheel's hub window (WheelHubWindow),
// because the drag wheel's own hub is a hole and cannot hold a pixel.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Peel.Wheel;

internal static class WheelHub
{
    private static readonly Typeface PillFace =
        new(new FontFamily(WheelTheme.FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Pen HubPen = FrozenPen(WheelTheme.HubEdge, 1);

    /// <summary>Draw the hub centred on <paramref name="centre"/>.</summary>
    internal static void Draw(DrawingContext context, Point centre, WheelGeometry wheel, string label, double pixelsPerDip)
    {
        context.DrawEllipse(WheelTheme.Hub, HubPen, centre, wheel.HubRadius, wheel.HubRadius);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }
        var maxWidth = (2 * wheel.HubRadius) - 18;
        var text = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            PillFace,
            WheelTheme.PillSize,
            WheelTheme.PillLabel,
            pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };
        var height = Math.Ceiling(text.Height) + 10;
        var width = Math.Min((2 * wheel.HubRadius) - 8, Math.Max(56, text.WidthIncludingTrailingWhitespace + 26));
        var pill = new Rect(centre.X - (width / 2), centre.Y - (height / 2), width, height);
        var shadow = pill;
        shadow.Offset(0, 1);
        context.DrawRoundedRectangle(WheelTheme.PillShadow, null, shadow, height / 2, height / 2);
        context.DrawRoundedRectangle(WheelTheme.Pill, null, pill, height / 2, height / 2);
        // MaxTextWidth with centred alignment centres the line in a box that
        // wide, so the box starts at the centre less half of it.
        context.DrawText(text, new Point(centre.X - (maxWidth / 2), centre.Y - (text.Height / 2)));
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
