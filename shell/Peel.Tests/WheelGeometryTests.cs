// The ring maths. Every one of these is a thing the wheel gets wrong if the
// angle convention slips: the spike labelled top and bottom the wrong way
// round, and nothing but a test catches that before a user drops on Compress
// when they aimed at Crop.
using Peel.Wheel;
using Xunit;

namespace Peel.Tests;

public class WheelGeometryTests
{
    private static readonly WheelGeometry Wheel = WheelGeometry.Default;

    private static double MidRadius => (Wheel.InnerRadius + Wheel.OuterRadius) / 2;

    [Fact]
    public void FirstSegmentIsAtTwelveOClock()
    {
        Assert.Equal(0, Wheel.SegmentAt(4, 0, -MidRadius));
    }

    [Fact]
    public void SegmentsRunClockwise()
    {
        Assert.Equal(1, Wheel.SegmentAt(4, MidRadius, 0));
        Assert.Equal(2, Wheel.SegmentAt(4, 0, MidRadius));
        Assert.Equal(3, Wheel.SegmentAt(4, -MidRadius, 0));
    }

    [Fact]
    public void TheHubIsNotASegment()
    {
        Assert.Equal(-1, Wheel.SegmentAt(4, 0, 0));
        Assert.Equal(-1, Wheel.SegmentAt(4, Wheel.InnerRadius - 2, 0));
    }

    [Fact]
    public void BeyondTheOuterEdgeIsNotASegment()
    {
        Assert.Equal(-1, Wheel.SegmentAt(4, Wheel.OuterRadius + 2, 0));
    }

    [Fact]
    public void NoSegmentsMeansNoHit()
    {
        Assert.Equal(-1, Wheel.SegmentAt(0, 0, -MidRadius));
    }

    [Fact]
    public void EverySegmentIsReachableAtItsOwnLabel()
    {
        for (var count = 1; count <= WheelPlanner.MaxSegments; count++)
        {
            for (var index = 0; index < count; index++)
            {
                var (dx, dy) = Wheel.LabelOffset(count, index);
                Assert.Equal(index, Wheel.SegmentAt(count, dx, dy));
            }
        }
    }

    [Fact]
    public void SegmentBoundariesDoNotOverlapOrLeaveGaps()
    {
        const int count = 7;
        var step = 360.0 / count;
        for (var index = 0; index < count; index++)
        {
            var (start, end) = Wheel.Arc(count, index);
            Assert.Equal(step, end - start, 6);
            var justInside = start + 0.4;
            var justBefore = end - 0.4;
            foreach (var bearing in new[] { justInside, justBefore })
            {
                var (dx, dy) = WheelGeometry.Offset(bearing, MidRadius);
                Assert.Equal(index, Wheel.SegmentAt(count, dx, dy));
            }
        }
    }

    [Fact]
    public void BearingIsClockwiseFromTheTop()
    {
        Assert.Equal(0, WheelGeometry.Bearing(0, -10), 6);
        Assert.Equal(90, WheelGeometry.Bearing(10, 0), 6);
        Assert.Equal(180, WheelGeometry.Bearing(0, 10), 6);
        Assert.Equal(270, WheelGeometry.Bearing(-10, 0), 6);
    }

    [Fact]
    public void TheWindowIsBigEnoughForTheRing()
    {
        Assert.True(Wheel.Centre >= Wheel.OuterRadius);
        Assert.Equal(Wheel.Size / 2, Wheel.Centre, 6);
    }
}
