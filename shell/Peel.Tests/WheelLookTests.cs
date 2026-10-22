// The wheel's look (hq-i9v0.1): a light plate, separated rounded petals, dark
// labels, the hovered petal in the wheel's accent, and a hub that is drawn on
// the keyboard wheel but left a hole on the drag wheel. The geometry tests pin
// the shapes for every segment count the wheel can show; the render tests draw
// a real WheelRenderer offscreen and read its pixels, so a theme change that
// turns the wheel dark again, or paints into the drag wheel's hole, fails here
// before anyone looks at a screenshot.
using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Peel.Wheel;
using Xunit;

namespace Peel.Tests;

public class WheelLookTests
{
    private static readonly WheelGeometry Wheel = WheelGeometry.Default;
    private static readonly Point Centre = new(Wheel.Centre, Wheel.Centre);

    [Fact]
    public void TheDrawnLayersNestInsideTheHitTest()
    {
        Assert.True(Wheel.HubRadius < Wheel.InnerRadius, "the drawn hub fits inside the drag wheel's hole");
        Assert.True(Wheel.PetalInner >= Wheel.InnerRadius + WheelGeometry.Gap - 0.001, "a gap of plate between hub and petals");
        Assert.True(Wheel.PetalOuter < Wheel.PlateRadius, "a rim of plate beyond the petals");
        Assert.True(Wheel.PlateRadius < Wheel.OuterRadius, "the plate's shadow fits inside the window's disc");
        Assert.True(Wheel.OuterRadius < Wheel.Centre, "the disc fits inside the window");
        Assert.True(WheelHubWindow.Side / 2 > Wheel.InnerRadius, "the hub window covers the whole hole");
    }

    [Fact]
    public void ItIsAboutThreeDesktopIconsAcross()
    {
        // A Windows desktop icon cell is about 75 to 80 px wide at 100 percent.
        var across = 2 * Wheel.PlateRadius;
        Assert.InRange(across, 2.5 * 76, 4.0 * 76);
    }

    [Fact]
    public void CornersAreRoundButLeaveEveryPetalABody()
    {
        for (var count = 2; count <= WheelPlanner.MaxSegments; count++)
        {
            var step = 360.0 / count;
            var innerWidth = 2 * Wheel.PetalInner * Math.Sin((step / 2 - WheelGeometry.HalfGapAngle(Wheel.PetalInner)) * Math.PI / 180);
            var radius = Wheel.CornerRadius(count);
            Assert.True(radius >= 2, $"{count} segments: corners are rounded ({radius})");
            Assert.True(radius <= innerWidth / 3 + 0.001, $"{count} segments: radius {radius} leaves the inner edge {innerWidth} a body");
        }
    }

    [Fact]
    public void TheGapKeepsItsWidthFromHubToRim()
    {
        foreach (var radius in new[] { Wheel.PetalInner, Wheel.LabelRadius, Wheel.PetalOuter })
        {
            // Two neighbours each stop HalfGapAngle short of the boundary; the
            // chord between their edges is the gap, wherever it is measured.
            var half = WheelGeometry.HalfGapAngle(radius) * Math.PI / 180;
            Assert.Equal(WheelGeometry.Gap, 2 * radius * Math.Sin(half), 6);
        }
    }

    [Fact]
    public void PetalsStayInTheRingAndApartForEveryCount() => OnSta(() =>
    {
        var hub = new EllipseGeometry(Centre, Wheel.PetalInner - 0.5, Wheel.PetalInner - 0.5);
        var beyond = Geometry.Combine(
            new RectangleGeometry(new Rect(0, 0, Wheel.Size, Wheel.Size)),
            new EllipseGeometry(Centre, Wheel.PetalOuter + 0.5, Wheel.PetalOuter + 0.5),
            GeometryCombineMode.Exclude, null);
        for (var count = 1; count <= WheelPlanner.MaxSegments; count++)
        {
            var petals = WheelPetals.Build(Wheel, count);
            Assert.Equal(count, petals.Length);
            var ideal = Math.PI * ((Wheel.PetalOuter * Wheel.PetalOuter) - (Wheel.PetalInner * Wheel.PetalInner)) / count;
            for (var i = 0; i < count; i++)
            {
                var area = petals[i].GetArea();
                Assert.True(area > 0.6 * ideal, $"{count} segments, petal {i}: area {area:0} of {ideal:0}");
                Assert.True(Overlap(petals[i], hub) < 0.5, $"{count} segments, petal {i} reaches into the hub");
                Assert.True(Overlap(petals[i], beyond) < 0.5, $"{count} segments, petal {i} reaches past the rim");
                var (dx, dy) = Wheel.LabelOffset(count, i);
                Assert.True(petals[i].FillContains(new Point(Centre.X + dx, Centre.Y + dy)),
                    $"{count} segments: petal {i} holds its own label point");
                if (count > 1)
                {
                    // Grown by most of the gap, a petal still does not touch
                    // its neighbour: the plate shows between them.
                    var next = petals[(i + 1) % count];
                    var grown = petals[i].GetWidenedPathGeometry(new Pen(Brushes.Black, 2 * (WheelGeometry.Gap - 1.5)));
                    Assert.True(Overlap(next, petals[i]) < 0.5, $"{count} segments: petals {i} and {(i + 1) % count} overlap");
                    Assert.True(Overlap(next, grown) < 0.5, $"{count} segments: no clear gap after petal {i}");
                }
            }
        }
    });

    [Fact]
    public void EachWheelHasItsOwnAccentAndConvertIsOrange()
    {
        var convert = Top(WheelTheme.AccentFor("convert"));
        var tool = Top(WheelTheme.AccentFor("tool"));
        var action = Top(WheelTheme.AccentFor("action"));
        Assert.NotEqual(convert, tool);
        Assert.NotEqual(convert, action);
        Assert.NotEqual(tool, action);
        Assert.True(convert.R > 0xE0 && convert.G is > 0x40 and < 0x90 && convert.B < 0x40, $"convert accent {convert}");
        Assert.True(tool.B > tool.R && tool.B > tool.G, $"tool accent is blue: {tool}");
        Assert.True(action.G > action.R && action.G > action.B, $"action accent is green: {action}");
        Assert.True(Top(WheelTheme.AccentFor("future")).A == 0xFF, "an unknown family still draws");
    }

    [Fact]
    public void TheWheelIsLightWithDarkLabelsAndAnOrangeHoveredPetal() => OnSta(() =>
    {
        var plan = Plan("MP3", "M4A", "FLAC", "OGG", "OPUS", "AIFF");
        var renderer = new WheelRenderer(drawsHub: true);
        renderer.SetPlan(plan, WheelTheme.AccentFor("convert"));
        renderer.SetHot(1);
        var pixels = Render(renderer, "wheel-hot.png");

        // The plate's rim, between the petals' outer edge and the plate's.
        var rim = pixels.At(Centre.X, Centre.Y - ((Wheel.PetalOuter + Wheel.PlateRadius) / 2));
        Assert.True(IsLight(rim, 0xD0), $"plate is light grey: {rim}");
        // A plain petal, between its label and its outer edge.
        var petal = pixels.At(Centre.X, Centre.Y - (Wheel.PetalOuter - 8));
        Assert.True(IsLight(petal, 0xE8), $"petal is near white: {petal}");
        // The hovered petal (index 1 of 6, at 60 degrees), off its label.
        var (hx, hy) = WheelGeometry.Offset(60, Wheel.PetalOuter - 8);
        var hot = pixels.At(Centre.X + hx, Centre.Y + hy);
        Assert.True(hot.R > 0xD0 && hot.G is > 0x30 and < 0x90 && hot.B < 0x50, $"hovered petal is orange: {hot}");
        // The label is dark: the darkest pixel around a plain petal's label.
        var (lx, ly) = Wheel.LabelOffset(6, 0);
        var ink = pixels.Darkest(Centre.X + lx, Centre.Y + ly, 14, 7);
        Assert.True(ink.R < 0x50 && ink.G < 0x50 && ink.B < 0x50, $"label is near black: {ink}");
        // The hub is drawn, light, with the hovered label in a white pill.
        var hub = pixels.At(Centre.X, Centre.Y - (Wheel.HubRadius - 4));
        Assert.True(IsLight(hub, 0xE6), $"hub is light: {hub}");
        var pill = pixels.At(Centre.X - 22, Centre.Y);
        Assert.True(IsLight(pill, 0xFA), $"pill is white: {pill}");
        var pillInk = pixels.Darkest(Centre.X, Centre.Y, 12, 5);
        Assert.True(pillInk.R < 0x60, $"pill names the hovered petal: {pillInk}");
        Assert.Equal("M4A", renderer.HotLabel);
    });

    [Fact]
    public void TheDragWheelLeavesItsHubForTheHubWindow() => OnSta(() =>
    {
        var renderer = new WheelRenderer(drawsHub: false);
        renderer.SetPlan(Plan("MD", "PDF", "HTML"), WheelTheme.AccentFor("convert"));
        var pixels = Render(renderer, "wheel-drag.png");
        // Inside the hole the wheel paints nothing: the hub window does.
        Assert.Equal(0, pixels.At(Centre.X, Centre.Y).A);
        Assert.Equal(0, pixels.At(Centre.X + Wheel.HubRadius - 4, Centre.Y).A);
        Assert.Equal(string.Empty, renderer.HotLabel);
    });

    [Fact]
    public void AnEmptyOrLoadingSegmentNamesNothingInTheHub() => OnSta(() =>
    {
        var renderer = new WheelRenderer();
        renderer.SetPlan(WheelPlan.Loading, WheelTheme.AccentFor("convert"));
        renderer.SetHot(0);
        Assert.Equal(string.Empty, renderer.HotLabel);
        var seen = 0;
        renderer.HotChanged += () => seen++;
        renderer.SetPlan(Plan("A", "B", "C"), WheelTheme.AccentFor("tool"));
        renderer.SetHot(2);
        renderer.SetHot(2);
        Assert.Equal(2, seen);
        Assert.Equal("C", renderer.HotLabel);
    });

    [Fact]
    public void FormatWheelsReadInCapitalsAndToolWheelsKeepTheirCase()
    {
        Assert.True(WheelRenderer.ReadsAsFormats(Plan("mp3", "WEBP", "docx")));
        Assert.False(WheelRenderer.ReadsAsFormats(Plan("Compress", "Crop", "Trim")));
        Assert.False(WheelRenderer.ReadsAsFormats(Plan("Copy Path", "Move")));
        Assert.False(WheelRenderer.ReadsAsFormats(WheelPlan.Loading));
    }

    [Fact]
    public void EveryToolNameFitsATwelvePetalWheelWithoutACut() => OnSta(() =>
    {
        // The longest single words the fake registry's tools wheel shows
        // (tests/fake-options.json), on a full twelve petal page.
        var renderer = new WheelRenderer();
        var width = renderer.LabelWidth(WheelPlanner.MaxSegments);
        var face = WheelRenderer.NameFace;
        foreach (var word in new[] { "Compress", "Normalize", "Annotate", "Metadata", "Watermark", "Organize", "Terminal" })
        {
            var size = WheelRenderer.FitSize(word, 11, width, face, 1);
            var measured = new FormattedText(word, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, size, Brushes.Black, 1);
            Assert.True(measured.WidthIncludingTrailingWhitespace <= width, $"{word} at {size} is {measured.WidthIncludingTrailingWhitespace:0.0} of {width:0.0}");
        }
        Assert.Equal(11, WheelRenderer.FitSize("Crop", 11, width, face, 1));
    });

    private static WheelPlan Plan(params string[] labels)
    {
        var segments = Array.ConvertAll(labels, label => new WheelSegment(SegmentKind.Entry, label, string.Empty, null));
        return new WheelPlan("convert", "Convert", segments, 0, 1);
    }

    private static double Overlap(Geometry a, Geometry b) =>
        Geometry.Combine(a, b, GeometryCombineMode.Intersect, null).GetArea();

    private static Color Top(Brush brush) => brush switch
    {
        LinearGradientBrush gradient => gradient.GradientStops[0].Color,
        SolidColorBrush solid => solid.Color,
        _ => throw new InvalidOperationException(brush.GetType().Name),
    };

    private static bool IsLight(Color c, byte floor) => c.A == 0xFF && c.R >= floor && c.G >= floor && c.B >= floor;

    private static Pixels Render(WheelRenderer renderer, string name)
    {
        var side = (int)Math.Ceiling(Wheel.Size);
        renderer.Width = side;
        renderer.Height = side;
        renderer.Measure(new Size(side, side));
        renderer.Arrange(new Rect(0, 0, side, side));
        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(renderer);
        // Kept for a person to look at; the run folder is throwaway.
        var dir = Path.Combine(Path.GetTempPath(), "peel-wheel-look");
        Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(dir, name)))
        {
            encoder.Save(file);
        }
        var data = new byte[side * side * 4];
        bitmap.CopyPixels(data, side * 4, 0);
        return new Pixels(data, side);
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA body timed out");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class Pixels
    {
        private readonly byte[] _data;
        private readonly int _side;

        internal Pixels(byte[] data, int side)
        {
            _data = data;
            _side = side;
        }

        internal Color At(double x, double y)
        {
            var i = (((int)Math.Round(y) * _side) + (int)Math.Round(x)) * 4;
            return Color.FromArgb(_data[i + 3], _data[i + 2], _data[i + 1], _data[i]);
        }

        internal Color Darkest(double x, double y, int halfWidth, int halfHeight)
        {
            var best = At(x, y);
            for (var dy = -halfHeight; dy <= halfHeight; dy++)
            {
                for (var dx = -halfWidth; dx <= halfWidth; dx++)
                {
                    var c = At(x + dx, y + dy);
                    if (c.R + c.G + c.B < best.R + best.G + best.B)
                    {
                        best = c;
                    }
                }
            }
            return best;
        }
    }
}
