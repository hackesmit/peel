// Review round 1 of hq-plct.11, each finding pinned where it lived.
//
// 1. A Split bar ran to the end of the longest stream, so on a file whose
//    audio outlasts its picture the window took cuts the engine then dropped.
// 2. The typed fields shared one error: a valid End cleared a bad Start, and
//    the window applied the Start from before it. Blank box numbers were
//    never checked at all.
// 3. An interval of 0.1 s over a day drew 864,000 lines on every render.
// 4. Undo on Split took back the latest cut in time, not the last one made.
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Peel.Editors.Timeline;
using Xunit;

namespace Peel.Tests;

public class TimelineRound2Tests
{
    // ffprobe -show_format -show_streams on testsrc 4 s plus sine 8 s, trimmed.
    private const string ShortPictureLongAudio = """
        {"format":{"start_time":"0.000000","duration":"8.000000"},
         "streams":[{"codec_type":"video","start_time":"0.000000","duration":"4.000000","disposition":{"attached_pic":0}},
                    {"codec_type":"audio","start_time":"0.000000","duration":"8.000000","disposition":{"attached_pic":0}}]}
        """;

    // --- 1. split and redact stop where the picture stops ----------------------

    [Fact]
    public void ASplitBarEndsWhereThePictureEndsNotWhereTheAudioDoes()
    {
        var info = MediaInfo.Parse(ShortPictureLongAudio);
        Assert.Equal(8.0, info.Duration, 6);
        Assert.Equal(4.0, info.VideoEnd, 6);
        Assert.Equal(4.0, info.LengthFor(TimelineKind.Split), 6);
        Assert.Equal(4.0, info.LengthFor(TimelineKind.Range), 6);
        // A trim keeps the audio, so its bar is still the whole file.
        Assert.Equal(8.0, info.LengthFor(TimelineKind.Trim), 6);

        var plan = new TimelinePlan(TimelineKind.Split, info.LengthFor(TimelineKind.Split), info.VideoStart);
        Assert.True(plan.AddCut(2));
        Assert.False(plan.AddCut(6));
        Assert.False(plan.TrySetCuts("2, 6", out var problem));
        Assert.Contains("0:06.000", problem);
        Assert.Equal("1 cut at 0:02.000, 2 parts", plan.Describe());
    }

    [Fact]
    public void APictureThatStartsLateIsReadOnTheFilesTimeline()
    {
        var info = MediaInfo.Parse("""
            {"format":{"start_time":"0.000000","duration":"4.000000"},
             "streams":[{"codec_type":"audio","start_time":"0.000000","duration":"4.000000"},
                        {"codec_type":"video","start_time":"1.000000","duration":"3.000000"}]}
            """);
        Assert.Equal(1.0, info.VideoStart, 6);
        Assert.Equal(4.0, info.VideoEnd, 6);
        var plan = new TimelinePlan(TimelineKind.Split, info.LengthFor(TimelineKind.Split), info.VideoStart);
        // A cut before the first frame would leave part 1 with no picture.
        Assert.False(plan.AddCut(0.5));
        Assert.True(plan.AddCut(2));
    }

    [Fact]
    public void AMatroskaPictureEndsAtItsDurationTagAndAudioOnlyHasNoPicture()
    {
        var mkv = MediaInfo.Parse("""
            {"format":{"duration":"8.000000"},
             "streams":[{"codec_type":"video","tags":{"DURATION":"00:00:04.000000000"}},
                        {"codec_type":"audio","tags":{"DURATION":"00:00:08.000000000"}}]}
            """);
        Assert.Equal(4.0, mkv.VideoEnd, 6);
        var mp3 = MediaInfo.Parse("""{"format":{"duration":"3.0"},"streams":[{"codec_type":"audio","duration":"3.0"}]}""");
        Assert.Equal(0.0, mp3.VideoEnd);
        Assert.Equal(3.0, mp3.LengthFor(TimelineKind.Split), 6);
    }

    // --- 2. every field is read, each with its own problem -------------------------

    private static FieldTexts Span(string start, string end) => new() { Start = start, End = end };

    [Fact]
    public void AValidEndDoesNotClearABadStart()
    {
        var plan = new TimelinePlan(TimelineKind.Trim, 10);
        Assert.Empty(TimelineFields.Read(plan, null, Span("1", "8"), typing: true));
        Assert.Equal(1.0, plan.Start, 6);

        var problems = TimelineFields.Read(plan, null, Span("abc", "8"), typing: true);
        Assert.Contains(TimelineFields.Start, problems.Keys);
        // The user fixes End next; Start is still wrong, and still said.
        problems = TimelineFields.Read(plan, null, Span("abc", "6"), typing: false);
        Assert.Equal(new[] { TimelineFields.Start }, problems.Keys.ToArray());
        Assert.Contains("abc", problems[TimelineFields.Start]);
        // The plan kept its last good span; the window refuses while a problem stands.
        Assert.Equal(1.0, plan.Start, 6);
    }

    [Fact]
    public void BothEndsAreReadTogetherSoTheirOrderOfTypingDoesNotMatter()
    {
        var plan = new TimelinePlan(TimelineKind.Trim, 10);
        Assert.Empty(TimelineFields.Read(plan, null, Span("2", "3"), typing: true));
        // Moving both later in one go: read one at a time, the old end (3)
        // would have clamped the new start (7).
        Assert.Empty(TimelineFields.Read(plan, null, Span("7", "9"), typing: false));
        Assert.Equal(7.0, plan.Start, 6);
        Assert.Equal(9.0, plan.End, 6);

        var problems = TimelineFields.Read(plan, null, Span("9", "7"), typing: false);
        Assert.Contains(TimelineFields.End, problems.Keys);
        Assert.Equal(7.0, plan.Start, 6);
        problems = TimelineFields.Read(plan, null, Span("2", "11"), typing: false);
        Assert.Contains(TimelineFields.End, problems.Keys);
    }

    [Fact]
    public void ARangeWithBothFieldsEmptyIsTheWholeClip()
    {
        var plan = new TimelinePlan(TimelineKind.Range, 8);
        Assert.Empty(TimelineFields.Read(plan, null, Span("2", "5"), typing: true));
        Assert.True(plan.HasRange);
        Assert.Empty(TimelineFields.Read(plan, null, Span("", ""), typing: false));
        Assert.False(plan.HasRange);
        Assert.Empty(plan.ToParams());
    }

    [Fact]
    public void ABlankBoxNumberIsRefusedWhenApplyingButNotWhileTyping()
    {
        var plan = new TimelinePlan(TimelineKind.Range, 8);
        var region = new RegionPlan(320, 240, covers: true);
        var drawn = new FieldTexts { Start = "", End = "", X = "10", Y = "10", W = "40", H = "30" };
        Assert.Empty(TimelineFields.Read(plan, region, drawn, typing: false));
        Assert.Equal(new Int32Rect(10, 10, 40, 30), region.Region);

        var cleared = drawn with { W = "" };
        Assert.Empty(TimelineFields.Read(plan, region, cleared, typing: true));
        var problems = TimelineFields.Read(plan, region, cleared, typing: false);
        Assert.Contains(TimelineFields.Box, problems.Keys);

        var outside = drawn with { X = "300" };
        Assert.Contains(TimelineFields.Box, TimelineFields.Read(plan, region, outside, typing: true).Keys);

        // Nothing drawn and nothing typed is the plan's own problem, not a field's.
        var empty = new RegionPlan(320, 240, covers: true);
        var none = drawn with { X = "", Y = "", W = "", H = "" };
        Assert.Empty(TimelineFields.Read(plan, empty, none, typing: false));
        Assert.NotNull(empty.Problem());
    }

    [Fact]
    public void ABadCutListAndABadIntervalAreEachTheirOwnProblem()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 10);
        Assert.Contains(TimelineFields.Cuts,
            TimelineFields.Read(plan, null, new FieldTexts { Cuts = "2, x", Every = "" }, typing: false).Keys);
        Assert.Contains(TimelineFields.Every,
            TimelineFields.Read(plan, null, new FieldTexts { Cuts = "", Every = "abc" }, typing: false).Keys);
        Assert.Empty(TimelineFields.Read(plan, null, new FieldTexts { Cuts = "", Every = "2.5" }, typing: false));
        Assert.Equal(2.5, plan.Every);
        Assert.Empty(TimelineFields.Read(plan, null, new FieldTexts { Cuts = "", Every = "" }, typing: false));
        Assert.NotNull(plan.Problem());
    }

    // --- 3. markers are coalesced per pixel ------------------------------------------

    [Fact]
    public void AnIntervalOverADayDrawsAtMostOneLinePerPixel()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 24 * 3600);
        Assert.True(plan.SetEvery(0.1));
        var axis = new TimeAxis(plan.Duration, 720);
        var clock = Stopwatch.StartNew();
        var marks = plan.EveryMarks(axis);
        clock.Stop();
        Assert.InRange(marks.Count, 700, 721);
        Assert.Equal(marks.Count, marks.Select(x => (int)x).Distinct().Count());
        Assert.True(clock.ElapsedMilliseconds < 200, $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AnIntervalWiderThanAPixelDrawsEachCutWhereTheEnginePutsIt()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 10);
        Assert.True(plan.SetEvery(2));
        Assert.Equal(new[] { 20.0, 40.0, 60.0, 80.0 }, plan.EveryMarks(new TimeAxis(10, 100)));
        // From the first frame, as av._cut_points counts an interval.
        var late = new TimelinePlan(TimelineKind.Split, 10, first: 1);
        Assert.True(late.SetEvery(4));
        Assert.Equal(new[] { 50.0, 90.0 }, late.EveryMarks(new TimeAxis(10, 100)));
    }

    [Fact]
    public void ThousandsOfTypedCutsDrawOneLinePerPixel()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 3600);
        var text = string.Join(",", Enumerable.Range(1, 5000).Select(i => (i * 0.7).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.True(plan.TrySetCuts(text, out var problem), problem);
        Assert.Equal(5000, plan.Cuts.Count);
        var marks = plan.CutMarks(new TimeAxis(3600, 720));
        Assert.InRange(marks.Count, 1, 721);
    }

    // --- 4. undo takes back the last cut made ----------------------------------------

    [Fact]
    public void UndoTakesBackTheLastCutMadeNotTheLatestInTime()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 10);
        Assert.True(plan.AddCut(5));
        Assert.True(plan.AddCut(2));
        Assert.True(plan.AddCut(8));
        Assert.True(plan.AddCut(3));
        plan.Undo();
        Assert.Equal(new[] { 2.0, 5.0, 8.0 }, plan.Cuts);
        // The window writes the cuts back and rereads them: that is not a new list.
        Assert.True(plan.TrySetCuts(plan.CutsText(), out _));
        plan.Undo();
        Assert.Equal(new[] { 2.0, 5.0 }, plan.Cuts);
        // A moved cut keeps its place in the order: 2 was made after 5.
        Assert.True(plan.MoveCut(0, 1));
        plan.Undo();
        Assert.Equal(new[] { 5.0 }, plan.Cuts);
        plan.Undo();
        Assert.Empty(plan.Cuts);
        plan.Undo();
        Assert.Empty(plan.Cuts);
    }

    [Fact]
    public void ARemovedCutLeavesTheRestOfTheOrder()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 10);
        plan.AddCut(6);
        plan.AddCut(1);
        plan.AddCut(4);
        plan.RemoveCut(plan.CutNear(4, 0.01));
        plan.Undo();
        Assert.Equal(new[] { 6.0 }, plan.Cuts);
        // A typed list is made in the order it is written.
        Assert.True(plan.TrySetCuts("7, 2", out _));
        plan.Undo();
        Assert.Equal(new[] { 7.0 }, plan.Cuts);
    }

    // --- the odd frame a redaction now covers exactly ----------------------------------

    [Fact]
    public void AnOddFrameCoveredEdgeToEdgeIsSentAtItsFullSize()
    {
        // av.video_redact blurs an odd box at its exact size (round 2), so the
        // box the window sends must be the whole frame, not one column short.
        var region = new RegionPlan(321, 241, covers: true);
        region.Set(new Int32Rect(0, 0, 321, 241));
        Assert.Equal(new Dictionary<string, string> { ["x"] = "0", ["y"] = "0", ["w"] = "321", ["h"] = "241" },
            region.ToParams());
    }
}
