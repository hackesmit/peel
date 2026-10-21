// What the user has marked on the timeline, and the parameters it means.
//
// One model for the four timeline tools, because they are the same gesture on
// the same bar with a different meaning:
//
//   Trim    a start and an end          av.video_trim, av.audio_trim
//   Split   cut points, or an interval  av.video_split
//   Bleep   any number of ranges        av.audio_bleep
//   Range   one optional range          av.video_redact (from, to)
//   Scrub   nothing but the playhead    av.video_crop (to pick the frame to crop on)
//
// Times are clamped into the file and kept apart by MinSpan, so the window can
// never hold a selection the engine refuses for being empty or backwards. The
// engine still checks everything (it knows where the last frame is, which this
// does not), and its message is what the user sees if it refuses.
//
// Only what the user changed is sent: a trim that keeps the end of the file
// sends no end, so the engine's own "to the end" stays the definition of it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Peel.Editors.Timeline;

internal enum TimelineKind
{
    Trim,
    Split,
    Bleep,
    Range,
    Scrub,
}

internal sealed class TimelinePlan
{
    /// <summary>
    /// The shortest selection worth sending. A trim, a part or a bleep shorter
    /// than this is a mis-click, not an intention.
    /// </summary>
    internal const double MinSpan = 0.05;

    private readonly List<double> _cuts = new();

    // The same cuts in the order they were made, so Undo takes back the last
    // one made rather than the latest in time.
    private readonly List<double> _made = new();
    private readonly List<(double From, double To)> _ranges = new();

    /// <param name="duration">Where the bar ends: the file, or for Split and Range the end of the picture (MediaInfo.LengthFor).</param>
    /// <param name="first">Split only: where the picture starts. A cut at or before it would leave part 1 with no picture.</param>
    internal TimelinePlan(TimelineKind kind, double duration, double first = 0)
    {
        Kind = kind;
        Duration = double.IsFinite(duration) && duration > 0 ? duration : 0;
        First = double.IsFinite(first) ? Math.Clamp(first, 0, Duration) : 0;
        Start = 0;
        End = Duration;
    }

    internal TimelineKind Kind { get; }

    internal double Duration { get; }

    internal double First { get; }

    /// <summary>Trim: the first moment kept. Range: where it begins.</summary>
    internal double Start { get; private set; }

    /// <summary>Trim: the last moment kept. Range: where it ends.</summary>
    internal double End { get; private set; }

    /// <summary>Range only: whether a range was chosen at all. Without one the whole clip is meant.</summary>
    internal bool HasRange { get; private set; }

    internal IReadOnlyList<double> Cuts => _cuts;

    internal IReadOnlyList<(double From, double To)> Ranges => _ranges;

    /// <summary>Split only: cut every so many seconds instead of at the cuts.</summary>
    internal double? Every { get; private set; }

    internal bool EndIsEnd => End >= Duration - 0.0005;

    // --- trim and range -----------------------------------------------------

    internal void SetStart(double seconds)
    {
        Start = Math.Clamp(Finite(seconds), 0, Math.Max(0, End - MinSpan));
        HasRange = true;
    }

    internal void SetEnd(double seconds)
    {
        End = Math.Clamp(Finite(seconds), Math.Min(Duration, Start + MinSpan), Duration);
        HasRange = true;
    }

    /// <summary>Both ends at once, in either order: a drag across the bar.</summary>
    internal bool SetSpan(double a, double b)
    {
        var from = Math.Clamp(Math.Min(Finite(a), Finite(b)), 0, Duration);
        var to = Math.Clamp(Math.Max(Finite(a), Finite(b)), 0, Duration);
        if (to - from < MinSpan)
        {
            return false;
        }
        Start = from;
        End = to;
        HasRange = true;
        return true;
    }

    /// <summary>Range only: back to the whole clip.</summary>
    internal void ClearRange()
    {
        Start = 0;
        End = Duration;
        HasRange = false;
    }

    // --- split --------------------------------------------------------------

    /// <summary>
    /// A cut at this moment, unless it would leave a part shorter than MinSpan
    /// (at the very start, the very end, or next to another cut).
    /// </summary>
    internal bool AddCut(double seconds)
    {
        var at = Finite(seconds);
        if (!Fits(at))
        {
            return false;
        }
        _cuts.Add(at);
        _cuts.Sort();
        _made.Add(at);
        Every = null;
        return true;
    }

    private bool Fits(double at) =>
        at >= First + MinSpan && at <= Duration - MinSpan && !_cuts.Any(cut => Math.Abs(cut - at) < MinSpan);

    internal void RemoveCut(int index)
    {
        if (index >= 0 && index < _cuts.Count)
        {
            _made.RemoveAt(_made.LastIndexOf(_cuts[index]));
            _cuts.RemoveAt(index);
        }
    }

    /// <summary>Move one cut, keeping it a cut: if the new place is refused, it stays where it was.</summary>
    internal bool MoveCut(int index, double seconds)
    {
        if (index < 0 || index >= _cuts.Count)
        {
            return false;
        }
        var was = _cuts[index];
        var at = Finite(seconds);
        _cuts.RemoveAt(index);
        if (Fits(at))
        {
            // A moved cut keeps its place in the order the cuts were made.
            _made[_made.LastIndexOf(was)] = at;
            _cuts.Add(at);
            _cuts.Sort();
            return true;
        }
        _cuts.Add(was);
        _cuts.Sort();
        return false;
    }

    /// <summary>The index of the cut within <paramref name="tolerance"/> seconds, or -1.</summary>
    internal int CutNear(double seconds, double tolerance)
    {
        var best = -1;
        var distance = double.MaxValue;
        for (var i = 0; i < _cuts.Count; i++)
        {
            var d = Math.Abs(_cuts[i] - seconds);
            if (d <= tolerance && d < distance)
            {
                best = i;
                distance = d;
            }
        }
        return best;
    }

    internal bool SetEvery(double? seconds)
    {
        if (seconds is null)
        {
            Every = null;
            return true;
        }
        if (!double.IsFinite(seconds.Value) || seconds.Value < 0.1 || seconds.Value >= Duration - First)
        {
            return false;
        }
        Every = seconds;
        _cuts.Clear();
        _made.Clear();
        return true;
    }

    // --- bleep --------------------------------------------------------------

    internal bool AddRange(double a, double b)
    {
        var from = Math.Clamp(Math.Min(Finite(a), Finite(b)), 0, Duration);
        var to = Math.Clamp(Math.Max(Finite(a), Finite(b)), 0, Duration);
        if (to - from < MinSpan)
        {
            return false;
        }
        _ranges.Add((from, to));
        return true;
    }

    internal void Undo()
    {
        switch (Kind)
        {
            case TimelineKind.Bleep when _ranges.Count > 0:
                _ranges.RemoveAt(_ranges.Count - 1);
                break;
            case TimelineKind.Split when _made.Count > 0:
                _cuts.Remove(_made[^1]);
                _made.RemoveAt(_made.Count - 1);
                break;
        }
    }

    internal void Clear()
    {
        _cuts.Clear();
        _made.Clear();
        _ranges.Clear();
        Every = null;
        Start = 0;
        End = Duration;
        HasRange = false;
    }

    // --- text fields --------------------------------------------------------

    /// <summary>
    /// Replace the cuts with a typed list ("0:10, 0:25"). On a bad entry
    /// nothing changes and the reason comes back, naming the entry.
    /// </summary>
    internal bool TrySetCuts(string text, out string problem)
    {
        var parsed = new List<double>();
        foreach (var item in Items(text))
        {
            if (!Timecode.TryParse(item, out var at))
            {
                problem = $"\"{item}\" is not a time. Write 0:10 or 10.5.";
                return false;
            }
            parsed.Add(at);
        }
        if (Every is null && parsed.Count == _cuts.Count
            && parsed.OrderBy(at => at).Zip(_cuts).All(pair => Math.Abs(pair.First - pair.Second) < 0.0005))
        {
            // The same cuts written back to the millisecond (the window
            // rereads every field): keep them, and the order they were made in.
            problem = string.Empty;
            return true;
        }
        var before = _cuts.ToList();
        var made = _made.ToList();
        var every = Every;
        _cuts.Clear();
        _made.Clear();
        foreach (var at in parsed)
        {
            if (!AddCut(at))
            {
                _cuts.Clear();
                _cuts.AddRange(before);
                _made.Clear();
                _made.AddRange(made);
                Every = every;
                problem = $"A cut at {Timecode.Format(at)} would leave a part shorter than {MinSpan:0.##} s "
                    + $"or fall outside the picture ({Timecode.Format(First)} to {Timecode.Format(Duration)}).";
                return false;
            }
        }
        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// Where the bar draws the cuts: one line per whole pixel column at most,
    /// so a list of thousands of cuts is still a bar of a few hundred lines.
    /// </summary>
    internal IReadOnlyList<double> CutMarks(TimeAxis axis)
    {
        var marks = new List<double>();
        var column = double.NegativeInfinity;
        foreach (var cut in _cuts)
        {
            var x = axis.ToX(cut);
            if (Math.Floor(x) > column)
            {
                marks.Add(x);
                column = Math.Floor(x);
            }
        }
        return marks;
    }

    /// <summary>
    /// Where the bar draws an interval's cuts, at most one per whole pixel
    /// column. Every 0.1 s over a day is 864,000 cuts; the walk jumps to the
    /// first cut in the next column instead of visiting each one, so it costs
    /// the width of the bar, not the length of the file. The cuts are where
    /// the engine puts them: from the first frame, every so often, stopping
    /// short of the end.
    /// </summary>
    internal IReadOnlyList<double> EveryMarks(TimeAxis axis)
    {
        var marks = new List<double>();
        if (Every is not { } every || Duration <= 0)
        {
            return marks;
        }
        var stop = Duration - MinSpan;
        var step = 1L;
        var column = double.NegativeInfinity;
        while (true)
        {
            var at = First + every * step;
            if (at >= stop)
            {
                break;
            }
            var x = axis.ToX(at);
            // The jump below can land a rounding error short of the next
            // column; then the next step crosses it.
            if (Math.Floor(x) > column)
            {
                marks.Add(x);
                column = Math.Floor(x);
            }
            // The first cut whose column is past this one.
            var next = (Math.Floor(x) + 1) / axis.Width * axis.Duration;
            step = Math.Max(step + 1, (long)Math.Ceiling((next - First) / every));
        }
        return marks;
    }

    /// <summary>Replace the ranges with a typed list ("0:03-0:05, 1:10-1:12").</summary>
    internal bool TrySetRanges(string text, out string problem)
    {
        var parsed = new List<(double, double)>();
        foreach (var item in Items(text))
        {
            var halves = item.Split('-');
            if (halves.Length != 2
                || !Timecode.TryParse(halves[0], out var from)
                || !Timecode.TryParse(halves[1], out var to))
            {
                problem = $"\"{item}\" is not a range. Write start-end, for example 0:03-0:05.";
                return false;
            }
            if (to <= from)
            {
                problem = $"\"{item}\" ends before it starts.";
                return false;
            }
            parsed.Add((from, to));
        }
        var before = _ranges.ToList();
        _ranges.Clear();
        foreach (var (from, to) in parsed)
        {
            if (!AddRange(from, to))
            {
                _ranges.Clear();
                _ranges.AddRange(before);
                problem = $"{Timecode.Format(from)}-{Timecode.Format(to)} is not inside the file "
                    + $"({Timecode.Format(Duration)}), or shorter than {MinSpan:0.##} s.";
                return false;
            }
        }
        problem = string.Empty;
        return true;
    }

    internal string CutsText() => string.Join(", ", _cuts.Select(Timecode.Format));

    internal string RangesText() =>
        string.Join(", ", _ranges.Select(range => $"{Timecode.Format(range.From)}-{Timecode.Format(range.To)}"));

    private static IEnumerable<string> Items(string text) =>
        (text ?? string.Empty).Split(',').Select(item => item.Trim()).Where(item => item.Length > 0);

    // --- what is sent ---------------------------------------------------------

    /// <summary>Why the window cannot apply yet, or null when it can.</summary>
    internal string? Problem()
    {
        if (Duration <= 0)
        {
            return "Peel cannot read how long this file is.";
        }
        return Kind switch
        {
            TimelineKind.Trim when Start <= 0 && EndIsEnd =>
                "Move the start or the end first: as it is, the whole file is kept.",
            TimelineKind.Split when _cuts.Count == 0 && Every is null =>
                "Add a cut first: click the bar and press Cut here, or give an interval.",
            TimelineKind.Bleep when _ranges.Count == 0 =>
                "Drag over the part to bleep first.",
            _ => null,
        };
    }

    internal Dictionary<string, string> ToParams()
    {
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (Kind)
        {
            case TimelineKind.Trim:
                if (Start > 0)
                {
                    sent["start"] = Timecode.Engine(Start);
                }
                if (!EndIsEnd)
                {
                    sent["end"] = Timecode.Engine(End);
                }
                break;
            case TimelineKind.Split:
                if (_cuts.Count > 0)
                {
                    sent["at"] = string.Join(",", _cuts.Select(Timecode.Engine));
                }
                else if (Every is { } every)
                {
                    sent["every"] = every.ToString("0.###", CultureInfo.InvariantCulture);
                }
                break;
            case TimelineKind.Bleep:
                sent["ranges"] = string.Join(",",
                    _ranges.Select(range => $"{Timecode.Engine(range.From)}-{Timecode.Engine(range.To)}"));
                break;
            case TimelineKind.Range:
                if (HasRange)
                {
                    sent["start"] = Timecode.Engine(Start);
                    sent["end"] = Timecode.Engine(End);
                }
                break;
        }
        return sent;
    }

    /// <summary>The line the user and the scripted test read before pressing Enter.</summary>
    internal string Describe()
    {
        switch (Kind)
        {
            case TimelineKind.Trim:
                return string.Format(CultureInfo.InvariantCulture, "Keeping {0} to {1} ({2:0.000} s)",
                    Timecode.Format(Start), Timecode.Format(End), End - Start);
            case TimelineKind.Split:
                if (Every is { } every)
                {
                    var parts = (int)Math.Ceiling((Duration - First) / every - 1e-9);
                    return string.Format(CultureInfo.InvariantCulture, "Cutting every {0:0.###} s, about {1} parts",
                        every, Math.Max(1, parts));
                }
                return _cuts.Count == 1
                    ? $"1 cut at {Timecode.Format(_cuts[0])}, 2 parts"
                    : $"{_cuts.Count} cuts, {_cuts.Count + 1} parts";
            case TimelineKind.Bleep:
                var total = _ranges.Sum(range => range.To - range.From);
                return _ranges.Count == 1
                    ? string.Format(CultureInfo.InvariantCulture, "1 range bleeped ({0:0.000} s)", total)
                    : string.Format(CultureInfo.InvariantCulture, "{0} ranges bleeped ({1:0.000} s)", _ranges.Count, total);
            case TimelineKind.Scrub:
                return string.Empty;
            default:
                return HasRange
                    ? $"Blurred from {Timecode.Format(Start)} to {Timecode.Format(End)}"
                    : "Blurred for the whole clip";
        }
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
