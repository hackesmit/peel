// The typed fields of a timeline window, read into its plans all at once.
//
// The window rereads every field whenever one changes, and once more when the
// user applies, so what is sent is always what the fields say now. Reading one
// field at a time with one shared error let a valid End clear the error of a
// bad Start and apply the Start from before it. Here each field has its own
// problem, and while any field has one the window does not apply.
//
// A field the user has not touched reads back what the plan wrote into it, so
// rereading it changes nothing (times are written to the millisecond, which is
// what the engine is sent anyway).
using System.Collections.Generic;

namespace Peel.Editors.Timeline;

/// <summary>What each field of the window holds. Null is a field this window does not have.</summary>
internal sealed record FieldTexts
{
    internal string? Start { get; init; }
    internal string? End { get; init; }
    internal string? Cuts { get; init; }
    internal string? Every { get; init; }
    internal string? Ranges { get; init; }
    internal string? X { get; init; }
    internal string? Y { get; init; }
    internal string? W { get; init; }
    internal string? H { get; init; }
}

internal static class TimelineFields
{
    internal const string Start = "start";
    internal const string End = "end";
    internal const string Cuts = "cuts";
    internal const string Every = "every";
    internal const string Ranges = "ranges";
    internal const string Box = "box";

    /// <summary>
    /// Read every field into the plans. Returns each refused field with its
    /// reason, in the order the fields sit in the window; empty when all are
    /// good. A refused field leaves its part of the plan as it was.
    /// <paramref name="typing"/> is true while the user is still typing: then
    /// a box with some of its four numbers missing is not yet a mistake.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Read(
        TimelinePlan plan, RegionPlan? region, FieldTexts texts, bool typing)
    {
        var problems = new Dictionary<string, string>(System.StringComparer.Ordinal);
        if (texts.Start is not null && texts.End is not null)
        {
            ReadSpan(plan, texts.Start.Trim(), texts.End.Trim(), problems);
        }
        if (texts.Cuts is not null && texts.Every is not null)
        {
            ReadSplit(plan, texts.Cuts.Trim(), texts.Every.Trim(), problems);
        }
        if (texts.Ranges is not null && !plan.TrySetRanges(texts.Ranges, out var rangeProblem))
        {
            problems[Ranges] = rangeProblem;
        }
        if (region is not null && texts.X is not null && texts.Y is not null && texts.W is not null && texts.H is not null)
        {
            ReadBox(region, texts, typing, problems);
        }
        return problems;
    }

    private static void ReadSpan(TimelinePlan plan, string start, string end, Dictionary<string, string> problems)
    {
        if (plan.Kind == TimelineKind.Range && start.Length == 0 && end.Length == 0)
        {
            plan.ClearRange();
            return;
        }
        var from = Time(plan, start, 0, Start, problems);
        var to = Time(plan, end, plan.Duration, End, problems);
        if (from is null || to is null)
        {
            return;
        }
        if (to.Value - from.Value < TimelinePlan.MinSpan)
        {
            problems[End] = to.Value <= from.Value
                ? $"The end ({Timecode.Format(to.Value)}) is not after the start ({Timecode.Format(from.Value)})."
                : $"Keep at least {TimelinePlan.MinSpan:0.##} s between the start and the end.";
            return;
        }
        plan.SetSpan(from.Value, to.Value);
    }

    private static double? Time(
        TimelinePlan plan, string text, double blank, string name, Dictionary<string, string> problems)
    {
        if (text.Length == 0)
        {
            return blank;
        }
        if (!Timecode.TryParse(text, out var seconds))
        {
            problems[name] = $"\"{text}\" is not a time. Write 0:03.5 or 3.5.";
            return null;
        }
        if (seconds > plan.Duration + 0.0005)
        {
            problems[name] = $"{Timecode.Format(seconds)} is past the end ({Timecode.Format(plan.Duration)}).";
            return null;
        }
        return System.Math.Min(seconds, plan.Duration);
    }

    private static void ReadSplit(TimelinePlan plan, string cuts, string every, Dictionary<string, string> problems)
    {
        // The window empties one of the two when the user types in the
        // other; if both still hold something, the cuts win, as in ToParams.
        if (cuts.Length > 0)
        {
            if (!plan.TrySetCuts(cuts, out var problem))
            {
                problems[Cuts] = problem;
            }
            return;
        }
        if (every.Length > 0)
        {
            if (!Timecode.TryParse(every, out var seconds) || !plan.SetEvery(seconds))
            {
                problems[Every] =
                    $"Every has to be a number of seconds from 0.1 to less than {Timecode.Format(plan.Duration - plan.First)}.";
            }
            return;
        }
        plan.TrySetCuts(string.Empty, out _);
        plan.SetEvery(null);
    }

    private static void ReadBox(RegionPlan region, FieldTexts texts, bool typing, Dictionary<string, string> problems)
    {
        var four = new[] { texts.X!, texts.Y!, texts.W!, texts.H! };
        var blank = System.Array.FindAll(four, text => text.Trim().Length == 0).Length;
        if (blank == 4 && region.Region is null)
        {
            // Nothing drawn and nothing typed: the plan's own Problem says so.
            return;
        }
        if (blank > 0)
        {
            if (!typing)
            {
                problems[Box] = "Write whole numbers of pixels in all four boxes.";
            }
            return;
        }
        if (!region.TrySet(texts.X!, texts.Y!, texts.W!, texts.H!, out var problem))
        {
            problems[Box] = problem;
        }
    }
}
