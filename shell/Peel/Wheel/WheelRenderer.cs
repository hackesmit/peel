// Draws the ring. One donut wedge for each segment, the hovered one in the
// wheel's accent colour, the label inside it.
//
// Drawing happens on the UI thread while a drag is in flight, so it stays
// cheap: no effects, no layout, geometries rebuilt only when the segment count
// changes, and a repaint on hover that touches nothing but the brushes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Peel.Wheel;

internal sealed class WheelRenderer : FrameworkElement
{
    private readonly List<Geometry> _wedges = new();
    private WheelGeometry _geometry = WheelGeometry.Default;
    private WheelPlan _plan = WheelPlan.Loading;
    private Brush _accent = WheelTheme.AccentFor(string.Empty);
    private int _hot = -1;
    private Typeface? _labelFace;
    private Typeface? _detailFace;

    internal WheelRenderer()
    {
        IsHitTestVisible = false;
        _labelFace = new Typeface(new FontFamily(WheelTheme.FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _detailFace = new Typeface(new FontFamily(WheelTheme.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    internal WheelGeometry Metrics => _geometry;

    internal WheelPlan Plan => _plan;

    internal int Hot => _hot;

    internal void SetPlan(WheelPlan plan, Brush accent)
    {
        var sameShape = plan.Count == _plan.Count;
        _plan = plan;
        _accent = accent;
        _hot = -1;
        if (!sameShape)
        {
            _wedges.Clear();
        }
        InvalidateVisual();
    }

    internal void SetHot(int index)
    {
        if (index == _hot)
        {
            return;
        }
        _hot = index;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var count = _plan.Count;
        if (count == 0)
        {
            return;
        }
        EnsureWedges(count);
        var centre = new Point(_geometry.Centre, _geometry.Centre);
        var edge = new Pen(WheelTheme.SegmentEdge, 2);
        edge.Freeze();

        for (var i = 0; i < count; i++)
        {
            var hot = i == _hot && _plan.Segments[i].Kind != SegmentKind.Empty;
            context.DrawGeometry(hot ? _accent : WheelTheme.Segment, edge, _wedges[i]);
        }

        var width = LabelWidth(count);
        var size = count >= 10 ? 11.5 : count >= 8 ? 12.5 : WheelTheme.LabelSize;
        for (var i = 0; i < count; i++)
        {
            var segment = _plan.Segments[i];
            var hot = i == _hot && segment.Kind != SegmentKind.Empty;
            var (dx, dy) = _geometry.LabelOffset(count, i);
            var label = Text(segment.Label, size, width, _labelFace!, hot ? WheelTheme.HotLabel : WheelTheme.Label);
            // The group line is only worth its space on the segment being
            // aimed at. Drawn on every one, a twelve segment wheel is a wall
            // of overlapping text.
            var detail = hot && segment.Detail.Length > 0
                ? Text(segment.Detail, WheelTheme.DetailSize, width, _detailFace!, WheelTheme.HotDetail)
                : null;
            var height = label.Height + (detail is null ? 0 : detail.Height + 1);
            var top = centre.Y + dy - (height / 2);
            context.DrawText(label, new Point(centre.X + dx - (label.Width / 2), top));
            if (detail is not null)
            {
                context.DrawText(detail, new Point(centre.X + dx - (detail.Width / 2), top + label.Height + 1));
            }
        }
    }

    /// <summary>
    /// How wide a label may be: the narrower of the ring's thickness and the
    /// segment's own width where the label sits, so a busy wheel wraps or
    /// trims instead of writing over its neighbours.
    /// </summary>
    private double LabelWidth(int count)
    {
        var thickness = (_geometry.OuterRadius - _geometry.InnerRadius) * 0.92;
        if (count <= 2)
        {
            return thickness;
        }
        var chord = 2 * _geometry.LabelRadius * Math.Sin(Math.PI / count) * 0.96;
        return Math.Max(44, Math.Min(thickness, chord));
    }

    private FormattedText Text(string value, double size, double maxWidth, Typeface face, Brush brush) =>
        new(
            value,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            face,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    private void EnsureWedges(int count)
    {
        if (_wedges.Count == count)
        {
            return;
        }
        _wedges.Clear();
        for (var i = 0; i < count; i++)
        {
            _wedges.Add(Wedge(count, i));
        }
    }

    /// <summary>One donut slice: outer arc clockwise, inner arc back.</summary>
    private Geometry Wedge(int count, int index)
    {
        var centre = _geometry.Centre;
        var (start, end) = _geometry.Arc(count, index);
        var sweep = end - start;
        var large = sweep >= 180;

        var outerStart = At(centre, start, _geometry.OuterRadius);
        var outerEnd = At(centre, end, _geometry.OuterRadius);
        var innerEnd = At(centre, end, _geometry.InnerRadius);
        var innerStart = At(centre, start, _geometry.InnerRadius);

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        if (count == 1)
        {
            // A single segment is a full ring: two half arcs, or the arc
            // collapses to nothing.
            var outerHalf = At(centre, start + 180, _geometry.OuterRadius);
            var innerHalf = At(centre, start + 180, _geometry.InnerRadius);
            figure.Segments.Add(Arc(outerHalf, _geometry.OuterRadius, true, SweepDirection.Clockwise));
            figure.Segments.Add(Arc(outerStart, _geometry.OuterRadius, true, SweepDirection.Clockwise));
            var ring = new PathGeometry();
            ring.Figures.Add(figure);
            var hole = new PathFigure { StartPoint = innerStart, IsClosed = true, IsFilled = true };
            hole.Segments.Add(Arc(innerHalf, _geometry.InnerRadius, true, SweepDirection.Clockwise));
            hole.Segments.Add(Arc(innerStart, _geometry.InnerRadius, true, SweepDirection.Clockwise));
            ring.Figures.Add(hole);
            ring.Freeze();
            return ring;
        }

        figure.Segments.Add(Arc(outerEnd, _geometry.OuterRadius, large, SweepDirection.Clockwise));
        figure.Segments.Add(new LineSegment(innerEnd, true));
        figure.Segments.Add(Arc(innerStart, _geometry.InnerRadius, large, SweepDirection.Counterclockwise));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static ArcSegment Arc(Point to, double radius, bool large, SweepDirection direction) =>
        new(to, new Size(radius, radius), 0, large, direction, true);

    private static Point At(double centre, double bearing, double radius)
    {
        var (x, y) = WheelGeometry.Offset(bearing, radius);
        return new Point(centre + x, centre + y);
    }
}
