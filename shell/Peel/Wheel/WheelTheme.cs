// Colours and fonts for the wheel. The accent says which wheel is open, which
// matters because the hub is a hole in the window and cannot hold a title.
using System;
using System.Windows.Media;

namespace Peel.Wheel;

internal static class WheelTheme
{
    internal static readonly Brush Segment = Frozen(Color.FromArgb(0xF2, 0x23, 0x23, 0x2B));
    internal static readonly Brush SegmentEdge = Frozen(Color.FromArgb(0xFF, 0x12, 0x12, 0x16));
    internal static readonly Brush Label = Frozen(Colors.White);
    internal static readonly Brush HotLabel = Frozen(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1F));
    internal static readonly Brush Detail = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush HotDetail = Frozen(Color.FromArgb(0xC0, 0x1A, 0x1A, 0x1F));
    internal static readonly Brush Quiet = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

    internal const string FontFamily = "Segoe UI";
    internal const double LabelSize = 14;
    internal const double DetailSize = 10.5;

    private static readonly Brush ConvertAccent = Frozen(Color.FromArgb(0xFF, 0xFF, 0x8A, 0x1E));
    private static readonly Brush ToolAccent = Frozen(Color.FromArgb(0xFF, 0x3F, 0xA7, 0xFF));
    private static readonly Brush ActionAccent = Frozen(Color.FromArgb(0xFF, 0x49, 0xC9, 0x6D));
    private static readonly Brush NeutralAccent = Frozen(Color.FromArgb(0xFF, 0xB0, 0xB0, 0xBC));

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
}
