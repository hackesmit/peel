// Draws the wheel: a light round plate, one raised rounded petal for each
// segment with a gap of plate around it, the hovered petal filled with the
// wheel's accent, and a dark bold label in each.
//
// Drawing happens on the UI thread while a drag is in flight, so it stays
// cheap: no effects, no layout, petal geometries rebuilt only when the segment
// count changes, and a repaint on hover that touches nothing but the brushes.
// The plate's shadow is a radial gradient, not a DropShadowEffect, for the
// same reason.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Peel.Wheel;

internal sealed class WheelRenderer : FrameworkElement
{
    /// <summary>How long the wheel takes to fade and scale in, and out.</summary>
    internal static readonly TimeSpan OpenTime = TimeSpan.FromMilliseconds(140);
    internal static readonly TimeSpan CloseTime = TimeSpan.FromMilliseconds(110);

    private const double ClosedScale = 0.86;

    private Geometry[] _petals = Array.Empty<Geometry>();
    private readonly WheelGeometry _geometry = WheelGeometry.Default;
    private readonly Brush _shadow;
    private readonly Geometry _plate;
    private readonly Pen _plateEdge = FrozenPen(WheelTheme.PlateEdge, 1);
    private readonly Pen _plateRim = FrozenPen(WheelTheme.PlateRim, 1.5);
    private readonly Pen _petalHighlight = FrozenPen(WheelTheme.PetalHighlight, 1);
    private readonly ScaleTransform _scale = new(1, 1);
    private WheelPlan _plan = WheelPlan.Loading;
    private Brush _accent = WheelTheme.AccentFor(string.Empty);
    private int _hot = -1;
    private bool _upper;
    private readonly Typeface _labelFace;
    private readonly Typeface _nameFace;
    private readonly Typeface _detailFace;

    /// <param name="drawsHub">
    /// False for the drag wheel, whose hub is a hole in its window: there the
    /// hub is drawn by WheelHubWindow, which sits over the hole and lets a
    /// drop pass through it.
    /// </param>
    internal WheelRenderer(bool drawsHub = true)
    {
        DrawsHub = drawsHub;
        IsHitTestVisible = false;
        RenderTransform = _scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        _labelFace = new Typeface(new FontFamily(WheelTheme.FontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _nameFace = NameFace;
        _detailFace = new Typeface(new FontFamily(WheelTheme.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _shadow = ShadowBrush(_geometry);
        _plate = PlateShape(_geometry, drawsHub);
    }

    internal bool DrawsHub { get; }

    internal WheelGeometry Metrics => _geometry;

    internal WheelPlan Plan => _plan;

    internal int Hot => _hot;

    /// <summary>The label the hub pill shows: the hovered segment's, or empty.</summary>
    internal string HotLabel =>
        _hot >= 0 && _hot < _plan.Count && _plan.Segments[_hot].Kind != SegmentKind.Empty
            ? Display(_plan.Segments[_hot].Label)
            : string.Empty;

    /// <summary>Tool and action names: a touch narrower than the bold format
    /// names, so a long one fits a thin petal.</summary>
    internal static readonly Typeface NameFace =
        new(new FontFamily(WheelTheme.FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>The smallest a label shrinks to before it is trimmed instead.</summary>
    internal const double MinLabelSize = 9;

    /// <summary>
    /// Format names are shown in capitals, as on the reference: a wheel whose
    /// every entry is a short name (MP3, WEBP, DOCX) reads as formats. Tool and
    /// action names (Compress, Copy Path) keep their case, because capitals
    /// make them too wide for a twelve petal wheel.
    /// </summary>
    internal static bool ReadsAsFormats(WheelPlan plan)
    {
        var entries = 0;
        foreach (var segment in plan.Segments)
        {
            if (segment.Kind != SegmentKind.Entry)
            {
                continue;
            }
            entries++;
            if (segment.Label.Length > 5 || segment.Label.Contains(' '))
            {
                return false;
            }
        }
        return entries > 0;
    }

    private string Display(string label) => _upper ? label.ToUpper(CultureInfo.CurrentUICulture) : label;

    /// <summary>Raised after the hovered segment changes, so a hub drawn elsewhere can follow.</summary>
    internal event Action? HotChanged;

    internal void SetPlan(WheelPlan plan, Brush accent)
    {
        var sameShape = plan.Count == _plan.Count;
        _plan = plan;
        _accent = accent;
        _upper = ReadsAsFormats(plan);
        _hot = -1;
        if (!sameShape)
        {
            _petals = Array.Empty<Geometry>();
        }
        InvalidateVisual();
        HotChanged?.Invoke();
    }

    internal void SetHot(int index)
    {
        if (index == _hot)
        {
            return;
        }
        _hot = index;
        InvalidateVisual();
        HotChanged?.Invoke();
    }

    /// <summary>Fade and scale in from a little smaller, as the wheel appears.</summary>
    internal void PlayOpen() => Animate(this, _scale, ClosedScale, 0, 1, 1, OpenTime, null);

    /// <summary>Fade and shrink a little, then call <paramref name="done"/>.</summary>
    internal void PlayClose(Action done) => Animate(this, _scale, 1, 1, 0.94, 0, CloseTime, done);

    /// <summary>Stop any open or close animation and show the wheel at rest.</summary>
    internal void Settle() => Animate(this, _scale, 1, 1, 1, 1, TimeSpan.Zero, null);

    /// <summary>
    /// The open and close motion, shared with the hub window so the two halves
    /// of the drag wheel move as one.
    /// </summary>
    internal static void Animate(UIElement element, ScaleTransform scale, double fromScale, double fromOpacity,
        double toScale, double toOpacity, TimeSpan time, Action? done)
    {
        if (time == TimeSpan.Zero)
        {
            element.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            element.Opacity = toOpacity;
            scale.ScaleX = scale.ScaleY = toScale;
            done?.Invoke();
            return;
        }
        var ease = new CubicEase { EasingMode = toOpacity > fromOpacity ? EasingMode.EaseOut : EasingMode.EaseIn };
        var fade = new DoubleAnimation(fromOpacity, toOpacity, time) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        var grow = new DoubleAnimation(fromScale, toScale, time) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        if (done is not null)
        {
            fade.Completed += (_, _) => done();
        }
        element.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    protected override void OnRender(DrawingContext context)
    {
        var count = _plan.Count;
        if (count == 0)
        {
            return;
        }
        if (_petals.Length != count)
        {
            _petals = WheelPetals.Build(_geometry, count);
        }
        var centre = new Point(_geometry.Centre, _geometry.Centre);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        context.DrawEllipse(_shadow, null, centre, _geometry.OuterRadius, _geometry.OuterRadius);
        context.DrawGeometry(WheelTheme.Plate, _plateEdge, _plate);
        context.DrawEllipse(null, _plateRim, centre, _geometry.PlateRadius - 1.5, _geometry.PlateRadius - 1.5);

        // The petals' shadow first, one pixel down, so no petal's shadow
        // falls on its neighbour.
        context.PushTransform(new TranslateTransform(0, 1.2));
        foreach (var petal in _petals)
        {
            context.DrawGeometry(WheelTheme.PetalShadow, null, petal);
        }
        context.Pop();

        for (var i = 0; i < count; i++)
        {
            var kind = _plan.Segments[i].Kind;
            var hot = i == _hot && kind != SegmentKind.Empty;
            var fill = hot ? _accent : kind == SegmentKind.Empty ? WheelTheme.EmptyPetal : WheelTheme.Petal;
            context.DrawGeometry(fill, hot ? null : _petalHighlight, _petals[i]);
        }

        var width = LabelWidth(count);
        var size = count >= 10 ? 11 : count >= 8 ? 12 : WheelTheme.LabelSize;
        for (var i = 0; i < count; i++)
        {
            var segment = _plan.Segments[i];
            var hot = i == _hot && segment.Kind != SegmentKind.Empty;
            var (dx, dy) = _geometry.LabelOffset(count, i);
            var brush = hot ? WheelTheme.HotLabel : segment.Kind == SegmentKind.Entry ? WheelTheme.Label : WheelTheme.Quiet;
            var text = Display(segment.Label);
            var face = _upper ? _labelFace : _nameFace;
            var label = Text(text, FitSize(text, size, width, face, dip), width, face, brush, dip);
            // The group line is only worth its space on the segment being
            // aimed at. Drawn on every one, a twelve segment wheel is a wall
            // of overlapping text.
            var detail = hot && segment.Detail.Length > 0
                ? Text(segment.Detail, WheelTheme.DetailSize, width, _detailFace, WheelTheme.HotDetail, dip)
                : null;
            var height = label.Height + (detail is null ? 0 : detail.Height + 1);
            var top = centre.Y + dy - (height / 2);
            // A centred FormattedText centres each line in MaxTextWidth, so the
            // text box starts half that width left of the label point.
            context.DrawText(label, new Point(centre.X + dx - (width / 2), top));
            if (detail is not null)
            {
                context.DrawText(detail, new Point(centre.X + dx - (width / 2), top + label.Height + 1));
            }
        }

        if (DrawsHub)
        {
            WheelHub.Draw(context, centre, _geometry, HotLabel, dip);
        }
    }

    /// <summary>
    /// How wide a label may be: the narrower of the petal's depth and its own
    /// width where the label sits, so a busy wheel wraps or trims instead of
    /// writing over its neighbours.
    /// </summary>
    internal double LabelWidth(int count)
    {
        var thickness = (_geometry.PetalOuter - _geometry.PetalInner) * 0.9;
        if (count <= 2)
        {
            return thickness;
        }
        var chord = (2 * _geometry.LabelRadiusFor(count) * Math.Sin(Math.PI / count)) - WheelGeometry.Gap - 4;
        return Math.Max(40, Math.Min(thickness, chord));
    }

    /// <summary>
    /// The size at which the label's longest word fits the petal, from
    /// <paramref name="size"/> down to <see cref="MinLabelSize"/>; past that it
    /// is trimmed. A word cut in the middle ("COM...") is worse than a smaller one.
    /// </summary>
    internal static double FitSize(string text, double size, double width, Typeface face, double dip)
    {
        var longest = string.Empty;
        foreach (var word in text.Split(' '))
        {
            if (word.Length > longest.Length)
            {
                longest = word;
            }
        }
        while (size > MinLabelSize)
        {
            var measured = new FormattedText(longest, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                face, size, Brushes.Black, dip);
            if (measured.WidthIncludingTrailingWhitespace <= width)
            {
                break;
            }
            size = Math.Max(MinLabelSize, size - 0.5);
        }
        return size;
    }

    private static FormattedText Text(string value, double size, double maxWidth, Typeface face, Brush brush, double dip) =>
        new(
            value,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            face,
            size,
            brush,
            dip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    /// <summary>A soft ring of shadow from the plate's edge out to the disc's.</summary>
    private static Brush ShadowBrush(WheelGeometry wheel)
    {
        var edge = wheel.PlateRadius / wheel.OuterRadius;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), edge - 0.05),
                new GradientStop(Color.FromArgb(0x30, 0, 0, 0), edge - 0.01),
                new GradientStop(Color.FromArgb(0x14, 0, 0, 0), edge + ((1 - edge) * 0.4)),
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1),
            },
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The plate's disc, or for the drag wheel a ring around the hub: the
    /// region clips the hole anyway, and leaving it unpainted keeps what this
    /// element draws identical to what the window shows.
    /// </summary>
    private static Geometry PlateShape(WheelGeometry wheel, bool drawsHub)
    {
        var centre = new Point(wheel.Centre, wheel.Centre);
        Geometry plate = new EllipseGeometry(centre, wheel.PlateRadius, wheel.PlateRadius);
        if (!drawsHub)
        {
            plate = new CombinedGeometry(GeometryCombineMode.Exclude, plate,
                new EllipseGeometry(centre, wheel.InnerRadius, wheel.InnerRadius));
        }
        plate.Freeze();
        return plate;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
