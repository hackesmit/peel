// Where a moment of the file sits on the timeline bar, and back.
//
// The same trap as PreviewMap, one dimension down: the bar is not the file. It
// is some hundreds of device independent units wide, the file is minutes long,
// and a cut read as a bar position and sent as seconds lands somewhere else.
// The arithmetic lives here, with tests, and the windows never do it.
using System;

namespace Peel.Editors.Timeline;

internal readonly struct TimeAxis
{
    internal TimeAxis(double duration, double width)
    {
        Duration = double.IsFinite(duration) && duration > 0 ? duration : 1;
        Width = double.IsFinite(width) && width > 0 ? width : 1;
    }

    internal double Duration { get; }

    internal double Width { get; }

    /// <summary>A bar position as seconds, clamped to the file. Past either end means that end.</summary>
    internal double ToSeconds(double x) =>
        double.IsFinite(x) ? Math.Clamp(x / Width * Duration, 0, Duration) : 0;

    /// <summary>Seconds as a bar position, clamped to the bar.</summary>
    internal double ToX(double seconds) =>
        double.IsFinite(seconds) ? Math.Clamp(seconds / Duration * Width, 0, Width) : 0;

    /// <summary>How many seconds a distance on the bar stands for: a grab tolerance in time.</summary>
    internal double Span(double distance) => Math.Abs(distance) / Width * Duration;
}
