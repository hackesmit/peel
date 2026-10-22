// The petal shapes: one rounded tile per segment, cut out of the ring with a
// clear gap of plate on every side.
//
// Built once per segment count and frozen, so a hover repaint never touches
// them. Each petal starts as a ring slice whose radial edges are offset a
// constant distance from the segment boundary (so the gap does not widen
// towards the rim like a wedge), then its corners are rounded by a
// morphological opening: shrink by the corner radius, grow back by it. That
// rounds every convex corner by exactly that radius and leaves the arcs and
// the straight edges where they were, whatever the segment count.
using System;
using System.Windows;
using System.Windows.Media;

namespace Peel.Wheel;

internal static class WheelPetals
{
    private const double Tolerance = 0.05;

    /// <summary>One frozen geometry per segment, in wheel coordinates (the
    /// window's device independent pixels, centre at <see cref="WheelGeometry.Centre"/>).</summary>
    internal static Geometry[] Build(WheelGeometry wheel, int count)
    {
        var petals = new Geometry[Math.Max(count, 0)];
        for (var i = 0; i < petals.Length; i++)
        {
            petals[i] = Petal(wheel, count, i);
        }
        return petals;
    }

    internal static Geometry Petal(WheelGeometry wheel, int count, int index)
    {
        var centre = new Point(wheel.Centre, wheel.Centre);
        if (count == 1)
        {
            var ring = Geometry.Combine(
                new EllipseGeometry(centre, wheel.PetalOuter, wheel.PetalOuter),
                new EllipseGeometry(centre, wheel.PetalInner, wheel.PetalInner),
                GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);
            ring.Freeze();
            return ring;
        }

        var sharp = Slice(wheel, count, index, centre);
        var radius = wheel.CornerRadius(count);
        var pen = new Pen(Brushes.Black, 2 * radius)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        var shrunk = Geometry.Combine(sharp, sharp.GetWidenedPathGeometry(pen, Tolerance, ToleranceType.Absolute),
            GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);
        var rounded = Geometry.Combine(shrunk, shrunk.GetWidenedPathGeometry(pen, Tolerance, ToleranceType.Absolute),
            GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
        rounded.Freeze();
        return rounded;
    }

    /// <summary>The sharp cornered slice: outer arc clockwise, inner arc back.</summary>
    private static PathGeometry Slice(WheelGeometry wheel, int count, int index, Point centre)
    {
        var (start, end) = wheel.Arc(count, index);
        var outer = wheel.PetalOuter;
        var inner = wheel.PetalInner;
        var outerGap = WheelGeometry.HalfGapAngle(outer);
        var innerGap = WheelGeometry.HalfGapAngle(inner);

        var figure = new PathFigure { StartPoint = At(centre, start + outerGap, outer), IsClosed = true, IsFilled = true };
        var outerSweep = end - start - (2 * outerGap);
        var innerSweep = end - start - (2 * innerGap);
        figure.Segments.Add(new ArcSegment(At(centre, end - outerGap, outer), new Size(outer, outer), 0,
            outerSweep >= 180, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(centre, end - innerGap, inner), true));
        figure.Segments.Add(new ArcSegment(At(centre, start + innerGap, inner), new Size(inner, inner), 0,
            innerSweep >= 180, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point At(Point centre, double bearing, double radius)
    {
        var (x, y) = WheelGeometry.Offset(bearing, radius);
        return new Point(centre.X + x, centre.Y + y);
    }
}
