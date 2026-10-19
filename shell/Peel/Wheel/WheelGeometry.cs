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
    internal static readonly WheelGeometry Default = new(58, 164);

    /// <summary>Window side length in device independent pixels, with room for the shadow.</summary>
    internal double Size => (OuterRadius + Padding) * 2;

    internal const double Padding = 8;

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
    internal double LabelRadius => InnerRadius + (0.58 * (OuterRadius - InnerRadius));

    /// <summary>Where a segment's label sits, as an offset from the wheel centre.</summary>
    internal (double X, double Y) LabelOffset(int count, int index)
    {
        var step = 360.0 / Math.Max(count, 1);
        return Offset(index * step, LabelRadius);
    }
}
