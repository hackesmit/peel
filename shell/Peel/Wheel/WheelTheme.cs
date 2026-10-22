// Colours and fonts for the wheel: a light plate with raised near white petals,
// dark bold labels, and the hovered petal filled with the wheel's accent. The
// accent says which wheel is open (orange converts, blue tools, green
// actions), so Daniel can tell them apart at a glance.
using System;
using System.Windows;
using System.Windows.Media;

namespace Peel.Wheel;

internal static class WheelTheme
{
    // The plate: very light grey with a lighter rim inside its edge and a
    // faint darker line outside it. Solid and opaque, because the hub window
    // paints the same colour over the hole and the two must meet with no seam.
    internal static readonly Brush Plate = Frozen(Color.FromArgb(0xFF, 0xE4, 0xE4, 0xE8));
    internal static readonly Brush PlateRim = Frozen(Color.FromArgb(0xD8, 0xFA, 0xFA, 0xFC));
    internal static readonly Brush PlateEdge = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x88));

    // A petal: near white at the top fading to a soft grey, a bright edge, and
    // a faint shadow one pixel below it, which together read as raised.
    internal static readonly Brush Petal = Vertical(Color.FromArgb(0xFF, 0xFC, 0xFC, 0xFD), Color.FromArgb(0xFF, 0xEC, 0xEC, 0xEF));
    internal static readonly Brush PetalHighlight = Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush PetalShadow = Frozen(Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
    internal static readonly Brush EmptyPetal = Vertical(Color.FromArgb(0xFF, 0xF1, 0xF1, 0xF3), Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE9));

    internal static readonly Brush Label = Frozen(Color.FromArgb(0xFF, 0x16, 0x16, 0x1A));
    internal static readonly Brush HotLabel = Frozen(Colors.White);
    internal static readonly Brush Detail = Frozen(Color.FromArgb(0xA0, 0x16, 0x16, 0x1A));
    internal static readonly Brush HotDetail = Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush Quiet = Frozen(Color.FromArgb(0x90, 0x16, 0x16, 0x1A));

    // The hub: a light disc, and a white pill on it that names the petal
    // being aimed at.
    internal static readonly Brush Hub = Vertical(Color.FromArgb(0xFF, 0xF7, 0xF7, 0xF9), Color.FromArgb(0xFF, 0xEE, 0xEE, 0xF1));
    internal static readonly Brush HubEdge = Frozen(Color.FromArgb(0x30, 0x80, 0x80, 0x88));
    internal static readonly Brush Pill = Frozen(Colors.White);
    internal static readonly Brush PillShadow = Frozen(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
    internal static readonly Brush PillLabel = Frozen(Color.FromArgb(0xFF, 0x16, 0x16, 0x1A));

    internal const string FontFamily = "Segoe UI";
    internal const double LabelSize = 13.5;
    internal const double DetailSize = 10;
    internal const double PillSize = 11;

    private static readonly Brush ConvertAccent = Vertical(Color.FromArgb(0xFF, 0xFF, 0x6E, 0x24), Color.FromArgb(0xFF, 0xEC, 0x52, 0x14));
    private static readonly Brush ToolAccent = Vertical(Color.FromArgb(0xFF, 0x3E, 0x9C, 0xFF), Color.FromArgb(0xFF, 0x1F, 0x78, 0xE8));
    private static readonly Brush ActionAccent = Vertical(Color.FromArgb(0xFF, 0x3D, 0xC4, 0x64), Color.FromArgb(0xFF, 0x22, 0xA2, 0x4A));
    private static readonly Brush NeutralAccent = Vertical(Color.FromArgb(0xFF, 0x9A, 0x9A, 0xA6), Color.FromArgb(0xFF, 0x7E, 0x7E, 0x8A));

    /// <summary>
    /// One accent per engine family. An unknown family still draws, in grey:
    /// a new wheel in the registry must never be invisible.
    /// </summary>
    internal static Brush AccentFor(string family) => family.ToLowerInvariant() switch
    {
        "convert" => ConvertAccent,
        "tool" => ToolAccent,
        "action" => ActionAccent,
        _ => NeutralAccent,
    };

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    /// <summary>Top to bottom over whatever shape it fills.</summary>
    private static Brush Vertical(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, new Point(0.5, 0), new Point(0.5, 1));
        brush.Freeze();
        return brush;
    }
}
