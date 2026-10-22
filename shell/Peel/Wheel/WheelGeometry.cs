// The ring maths, in one place and with no WPF types, so the unit tests can
// check every angle without opening a window.
//
// Bearings are measured clockwise from twelve o'clock, which is how the wheel
// reads to a person: segment 0 sits at the top, segment 1 to its right. Screen
// y grows downward, so the conversion from a bearing to a point is
// (sin b, -cos b). The feasibility spike got top and bottom the wrong way
// round by using atan2(dy, dx) directly; the tests below pin it down.
using System;

namespace Peel.Wheel;

internal readonly record struct WheelGeometry(double InnerRadius, double OuterRadius)
{
    // InnerRadius and OuterRadius are the hit test: the hub hole and the edge
    // of the window's disc. Everything drawn sits inside OuterRadius, because
    // the window region clips the drawing too.
    internal static readonly WheelGeometry Default = new(54, 150);

    /// <summary>Window side length in device independent pixels.</summary>
    internal double Size => (OuterRadius + Padding) * 2;

    internal const double Padding = 4;

    /// <summary>Width of the soft shadow around the plate, inside the disc.</summary>
    internal const double ShadowWidth = 6;

    /// <summary>Clear plate between two petals, between a petal and the hub,
    /// and between a petal and the rim.</summary>
    internal const double Gap = 6;

    /// <summary>The light round plate the petals sit on.</summary>
    internal double PlateRadius => OuterRadius - ShadowWidth;

    /// <summary>Outer edge of every petal: a rim of plate shows beyond it.</summary>
    internal double PetalOuter => PlateRadius - Gap;

    /// <summary>Inner edge of every petal, a gap of plate past the hub hole.</summary>
    internal double PetalInner => InnerRadius + Gap;

    /// <summary>The drawn hub: a light disc a little inside the hole, so a
    /// ring of plate separates it from the petals as it does at the rim.</summary>
    internal double HubRadius => InnerRadius - 2;

    /// <summary>
    /// Half the clear gap, as an angle, at a radius. A radial edge offset by a
    /// constant distance from the segment boundary meets a circle of radius r
    /// at asin(g / 2r) past the boundary, so the gap keeps its width all the
    /// way out instead of opening up like a wedge.
    /// </summary>
    internal static double HalfGapAngle(double radius) =>
        Math.Asin(Math.Min(1, (Gap / 2) / radius)) * 180.0 / Math.PI;

    /// <summary>
    /// How round a petal's corners are. Big and soft on a sparse wheel, as on
    /// the reference; capped to a third of the petal's narrowest width (its
    /// inner edge) so a twelve segment wheel still keeps a body.
    /// </summary>
    internal double CornerRadius(int count)
    {
        const double Preferred = 20;
        if (count <= 1)
        {
            return 0;
        }
        var step = 360.0 / count;
        var innerWidth = 2 * PetalInner * Math.Sin((step / 2 - HalfGapAngle(PetalInner)) * Math.PI / 180.0);
        return Math.Max(2, Math.Min(Preferred, innerWidth / 3));
    }

    internal double Centre => Size / 2;

    /// <summary>
    /// Which segment a point falls in, measured from the wheel centre with y
    /// growing downward. Returns -1 for the hub and for anything beyond the
    /// outer edge, which both mean "no segment".
    /// </summary>
    internal int SegmentAt(int count, double dx, double dy)
    {
        if (count <= 0)
        {
            return -1;
        }
        var radius = Math.Sqrt((dx * dx) + (dy * dy));
        if (radius < InnerRadius || radius > OuterRadius)
        {
            return -1;
        }
        var step = 360.0 / count;
        var bearing = Bearing(dx, dy);
        var index = (int)Math.Floor(((bearing + (step / 2)) % 360) / step);
        return index % count;
    }

    /// <summary>Clockwise degrees from twelve o'clock, in [0, 360).</summary>
    internal static double Bearing(double dx, double dy)
    {
        var degrees = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        return (degrees + 360.0) % 360.0;
    }

    /// <summary>Start and end bearing of one segment, in degrees.</summary>
    internal (double Start, double End) Arc(int count, int index)
    {
        var step = 360.0 / Math.Max(count, 1);
        var centre = index * step;
        return (centre - (step / 2), centre + (step / 2));
    }

    /// <summary>Offset from the centre of a point at a bearing and radius.</summary>
    internal static (double X, double Y) Offset(double bearing, double radius)
    {
        var radians = bearing * Math.PI / 180.0;
        return (Math.Sin(radians) * radius, -Math.Cos(radians) * radius);
    }

    /// <summary>Radius the labels sit at: a little past the middle of the ring,
    /// because the further out they are the more room each one has.</summary>
    internal double LabelRadius => PetalInner + (0.55 * (PetalOuter - PetalInner));

    /// <summary>Where a segment's label sits, as an offset from the wheel centre.</summary>
    internal (double X, double Y) LabelOffset(int count, int index)
    {
        var step = 360.0 / Math.Max(count, 1);
        return Offset(index * step, LabelRadius);
    }
}
