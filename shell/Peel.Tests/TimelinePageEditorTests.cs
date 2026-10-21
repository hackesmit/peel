// The timeline and page editors, tested where they can be tested without a desktop.
//
// The windows are thin over these classes, and these are where a wrong output
// would come from: a moment on the bar read as the wrong second, a cut list
// the engine splits differently, a box the engine evens away from what was
// covered, a page order sent backwards. The scripted run on George drives the
// windows; this pins what they are built on.
//
// The entries come from real `peel options` output for an MP4, an MP3 and a
// PDF, so a renamed parameter fails here instead of on George.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Peel.Editors;
using Peel.Editors.Pages;
using Peel.Editors.Timeline;
using Peel.Engine;
using Xunit;

namespace Peel.Tests;

public class TimelinePageEditorTests
{
    // Captured from `peel options a.mp4`, `a.mp3` and `a.pdf` on hqbox,
    // 2026-10-21, keeping the entries that need an editor.
    private const string MediaTools = """
    {"ok": true, "files": [], "wheels": [{"family": "tool", "label": "File tools", "default_modifier": "Ctrl", "groups": [{"group": "Media", "entries": [{"id": "av.video_crop", "family": "tool", "label": "Crop", "group": "Video", "inputs": ["mp4", "mov", "mkv", "webm", "avi", "wmv"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "x", "type": "int", "label": "X", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "y", "type": "int", "label": "Y", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "w", "type": "int", "label": "Width", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "h", "type": "int", "label": "Height", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "quality", "type": "enum", "label": "Quality", "default": "medium", "required": false, "choices": ["high", "medium", "low"], "minimum": null, "maximum": null, "help": "Ignored when a target size is given."}], "backends": ["ffmpeg", "ffprobe"], "missing_backends": [], "available": true}, {"id": "av.video_redact", "family": "tool", "label": "Redact", "group": "Video", "inputs": ["mp4", "mov", "mkv", "webm", "avi", "wmv"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "x", "type": "int", "label": "X", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "y", "type": "int", "label": "Y", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "w", "type": "int", "label": "Width", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "h", "type": "int", "label": "Height", "default": null, "required": true, "choices": [], "minimum": 0, "maximum": null, "help": ""}, {"name": "strength", "type": "int", "label": "Blur", "default": 12, "required": false, "choices": [], "minimum": 1, "maximum": 50, "help": ""}, {"name": "start", "type": "timecode", "label": "From", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": "Empty blurs the whole clip."}, {"name": "end", "type": "timecode", "label": "To", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": ""}, {"name": "quality", "type": "enum", "label": "Quality", "default": "medium", "required": false, "choices": ["high", "medium", "low"], "minimum": null, "maximum": null, "help": "Ignored when a target size is given."}], "backends": ["ffmpeg", "ffprobe"], "missing_backends": [], "available": true}, {"id": "av.video_split", "family": "tool", "label": "Split", "group": "Video", "inputs": ["mp4", "mov", "mkv", "webm", "avi", "wmv"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "at", "type": "list", "label": "Cut at", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": "Timestamps, for example 0:10,0:25."}, {"name": "every", "type": "float", "label": "Every (seconds)", "default": null, "required": false, "choices": [], "minimum": 0.1, "maximum": null, "help": ""}, {"name": "quality", "type": "enum", "label": "Quality", "default": "medium", "required": false, "choices": ["high", "medium", "low"], "minimum": null, "maximum": null, "help": "Ignored when a target size is given."}], "backends": ["ffmpeg", "ffprobe"], "missing_backends": [], "available": true}, {"id": "av.video_trim", "family": "tool", "label": "Trim", "group": "Video", "inputs": ["mp4", "mov", "mkv", "webm", "avi", "wmv"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "start", "type": "timecode", "label": "Start", "default": 0, "required": false, "choices": [], "minimum": null, "maximum": null, "help": ""}, {"name": "end", "type": "timecode", "label": "End", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": "Empty means the end of the file."}, {"name": "quality", "type": "enum", "label": "Quality", "default": "medium", "required": false, "choices": ["high", "medium", "low"], "minimum": null, "maximum": null, "help": "Ignored when a target size is given."}], "backends": ["ffmpeg", "ffprobe"], "missing_backends": [], "available": true}, {"id": "av.audio_bleep", "family": "tool", "label": "Bleep", "group": "Audio", "inputs": ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "ranges", "type": "list", "label": "Ranges", "default": null, "required": true, "choices": [], "minimum": null, "maximum": null, "help": "Each range is start-end, for example 0:03-0:05,1:10-1:12."}, {"name": "frequency", "type": "int", "label": "Tone (Hz)", "default": 1000, "required": false, "choices": [], "minimum": 100, "maximum": 8000, "help": ""}], "backends": ["ffmpeg", "ffprobe"], "missing_backends": [], "available": true}, {"id": "av.audio_metadata", "family": "tool", "label": "Metadata", "group": "Audio", "inputs": ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "title", "type": "string", "label": "Title", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": ""}, {"name": "artist", "type": "string", "label": "Artist", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": ""}, {"name": "album", "type": "string", "label": "Album", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": ""}], "backends": ["ffmpeg", "ffprobe"], "missing_backends": [], "available": true}, {"id": "av.audio_trim", "family": "tool", "label": "Trim", "group": "Audio", "inputs": ["mp3", "m4a", "wav", "flac", "ogg", "opus", "aiff", "wma"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "start", "type": "timecode", "label": "Start", "default": 0, "required": false, "choices": [], "minimum": null, "maximum": null, "help": ""}, {"name": "end", "type": "timecode", "label": "End", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": "Empty means the end of the file."}], "backends": ["ffmpeg", "ffprobe"], "missing_backends": [], "available": true}, {"id": "pdf.organize", "family": "tool", "label": "Organize", "group": "PDF", "inputs": ["pdf"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "pages", "type": "list", "label": "Page order", "default": null, "required": true, "choices": [], "minimum": null, "maximum": null, "help": "1-based page numbers in the order to keep. Pages left out are deleted."}, {"name": "rotate", "type": "json", "label": "Rotations", "default": null, "required": false, "choices": [], "minimum": null, "maximum": null, "help": "Per-page clockwise rotation added to the page's current rotation, keyed by source page, e.g. {\"1\": 90, \"3\": 180}."}], "backends": ["pymupdf"], "missing_backends": [], "available": true}, {"id": "pdf.split", "family": "tool", "label": "Split", "group": "PDF", "inputs": ["pdf"], "output_ext": null, "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "ranges", "type": "string", "label": "Page ranges", "default": "", "required": false, "choices": [], "minimum": null, "maximum": null, "help": "Leave empty to write one file per page, or give ranges like \"1-3,5,8-10\"."}], "backends": ["pymupdf"], "missing_backends": [], "available": true}]}]}], "warnings": []}
    """;

    private static readonly Dictionary<string, ActionEntry> Entries =
        JsonSerializer.Deserialize<OptionsResult>(
                MediaTools, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .Wheels.SelectMany(wheel => wheel.Groups).SelectMany(group => group.Entries)
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);

    private static ParamSchema Schema(string id) => new(Entries[id]);

    // --- the registry the windows are built for -----------------------------

    [Fact]
    public void EveryMediaAndPdfEditorTheEngineDeclaresHasAWindow()
    {
        var ids = new[]
        {
            "av.video_crop", "av.video_redact", "av.video_split", "av.video_trim",
            "av.audio_bleep", "av.audio_metadata", "av.audio_trim", "pdf.organize", "pdf.split",
        };
        Assert.Equal(ids.OrderBy(id => id), Entries.Keys.OrderBy(id => id));
        foreach (var id in ids)
        {
            Assert.True(Entries[id].NeedsEditor, id);
            Assert.True(EditorHost.Knows(id), id);
            Assert.True(MediaEditors.Handles(id) ^ PageEditors.Handles(id), id);
        }
        Assert.False(EditorHost.Knows("av.video_snapshot"));
    }

    [Fact]
    public void TheParametersTheWindowsSendAreTheOnesTheEngineDeclares()
    {
        Assert.True(Schema("av.video_trim").Declares("start") && Schema("av.video_trim").Declares("end"));
        Assert.True(Schema("av.audio_trim").Declares("start") && Schema("av.audio_trim").Declares("end"));
        Assert.True(Schema("av.video_split").Declares("at") && Schema("av.video_split").Declares("every"));
        Assert.True(Schema("av.audio_bleep").Declares("ranges") && Schema("av.audio_bleep").Declares("frequency"));
        foreach (var name in new[] { "x", "y", "w", "h", "strength", "start", "end", "quality" })
        {
            Assert.True(Schema("av.video_redact").Declares(name), name);
        }
        foreach (var name in new[] { "x", "y", "w", "h", "quality" })
        {
            Assert.True(Schema("av.video_crop").Declares(name), name);
        }
        Assert.True(Schema("pdf.organize").Declares("pages") && Schema("pdf.organize").Declares("rotate"));
        Assert.True(Schema("pdf.split").Declares("ranges"));
        Assert.Equal(new[] { "high", "medium", "low" }, Schema("av.video_trim").Choices("quality"));
        Assert.Equal("medium", Schema("av.video_trim").Text("quality", "?"));
        Assert.Equal(1000.0, Schema("av.audio_bleep").Number("frequency", 0));
        Assert.Equal(12.0, Schema("av.video_redact").Number("strength", 0));
        Assert.Equal(new[] { "title", "artist", "album" },
            Entries["av.audio_metadata"].Params.Where(spec => spec.Type == "string").Select(spec => spec.Name));
    }

    [Fact]
    public void EachTimelineToolGetsItsKind()
    {
        Assert.Equal(TimelineKind.Trim, TimelineEditor.KindOf("av.video_trim"));
        Assert.Equal(TimelineKind.Trim, TimelineEditor.KindOf("av.audio_trim"));
        Assert.Equal(TimelineKind.Split, TimelineEditor.KindOf("av.video_split"));
        Assert.Equal(TimelineKind.Bleep, TimelineEditor.KindOf("av.audio_bleep"));
        Assert.Equal(TimelineKind.Range, TimelineEditor.KindOf("av.video_redact"));
        Assert.Equal(TimelineKind.Scrub, TimelineEditor.KindOf("av.video_crop"));
    }

    // --- timecodes ------------------------------------------------------------

    [Theory]
    [InlineData("90", 90)]
    [InlineData("0:03.250", 3.25)]
    [InlineData("1:02:03.5", 3723.5)]
    [InlineData(" 2.5 ", 2.5)]
    [InlineData("00:00:03.023000000", 3.023)]
    public void ATypedTimeMeansWhatTheEngineReadsIt(string text, double seconds)
    {
        Assert.True(Timecode.TryParse(text, out var parsed), text);
        Assert.Equal(seconds, parsed, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("1:60")]
    [InlineData("3,5")]
    [InlineData("1:2:3:4")]
    [InlineData("abc")]
    [InlineData(".")]
    [InlineData("1..2")]
    public void ATimeTheEngineWouldReadDifferentlyIsRefused(string text)
    {
        Assert.False(Timecode.TryParse(text, out _), text);
    }

    [Fact]
    public void TimesAreShownWithMinutesAndSentAsPlainSecondsInAnyLocale()
    {
        Assert.Equal("0:03.250", Timecode.Format(3.25));
        Assert.Equal("1:02:03.500", Timecode.Format(3723.5));
        Assert.Equal("0:00.000", Timecode.Format(double.NaN));
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("3.25", Timecode.Engine(3.25));
            Assert.Equal("0:03.250", Timecode.Format(3.25));
            Assert.Equal("1.5", new TimelinePlan(TimelineKind.Split, 10).SetEvery(1.5)
                ? Timecode.Engine(1.5) : "refused");
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
        Assert.Equal("1.235", Timecode.Engine(1.23456));
        Assert.Equal("0", Timecode.Engine(-4));
    }

    [Fact]
    public void TheBarMapsPositionsToSecondsAndClampsPastEitherEnd()
    {
        var axis = new TimeAxis(30, 600);
        Assert.Equal(15, axis.ToSeconds(300), 9);
        Assert.Equal(0.0, axis.ToSeconds(-40));
        Assert.Equal(30.0, axis.ToSeconds(900));
        Assert.Equal(300, axis.ToX(15), 9);
        Assert.Equal(600.0, axis.ToX(99));
        Assert.Equal(0.4, axis.Span(8), 9);
        Assert.Equal(0.0, axis.ToSeconds(double.NaN));
    }

    // --- trim -------------------------------------------------------------------

    [Fact]
    public void AnUntouchedTrimIsRefusedBecauseItWouldOnlyCopyTheFile()
    {
        var plan = new TimelinePlan(TimelineKind.Trim, 3);
        Assert.NotNull(plan.Problem());
        plan.SetStart(1);
        Assert.Null(plan.Problem());
    }

    [Fact]
    public void ATrimSendsOnlyTheEndsTheUserMoved()
    {
        var plan = new TimelinePlan(TimelineKind.Trim, 3);
        plan.SetStart(1);
        plan.SetEnd(2.5);
        Assert.Equal(new Dictionary<string, string> { ["start"] = "1", ["end"] = "2.5" }, plan.ToParams());
        Assert.Equal("Keeping 0:01.000 to 0:02.500 (1.500 s)", plan.Describe());

        plan.SetEnd(3);
        Assert.Equal(new Dictionary<string, string> { ["start"] = "1" }, plan.ToParams());

        var tail = new TimelinePlan(TimelineKind.Trim, 3);
        tail.SetEnd(2);
        Assert.Equal(new Dictionary<string, string> { ["end"] = "2" }, tail.ToParams());
    }

    [Fact]
    public void TheStartCannotPassTheEndNorTheEndTheStart()
    {
        var plan = new TimelinePlan(TimelineKind.Trim, 10);
        plan.SetEnd(4);
        plan.SetStart(9);
        Assert.Equal(4 - TimelinePlan.MinSpan, plan.Start, 9);
        plan.SetEnd(-3);
        Assert.Equal(plan.Start + TimelinePlan.MinSpan, plan.End, 9);
        plan.SetEnd(99);
        Assert.Equal(10.0, plan.End);
        Assert.True(plan.EndIsEnd);
    }

    // --- split --------------------------------------------------------------

    [Fact]
    public void CutsThatWouldLeaveAnEmptyPartAreRefused()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 3);
        Assert.NotNull(plan.Problem());
        Assert.False(plan.AddCut(0.01));
        Assert.False(plan.AddCut(2.99));
        Assert.True(plan.AddCut(2));
        Assert.True(plan.AddCut(1));
        Assert.False(plan.AddCut(1.02));
        Assert.Equal(new[] { 1.0, 2.0 }, plan.Cuts);
        Assert.Equal(new Dictionary<string, string> { ["at"] = "1,2" }, plan.ToParams());
        Assert.Equal("2 cuts, 3 parts", plan.Describe());
        Assert.Null(plan.Problem());
    }

    [Fact]
    public void ACutCanBeMovedButNotOntoAnother()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 10);
        plan.AddCut(2);
        plan.AddCut(5);
        Assert.True(plan.MoveCut(0, 3));
        Assert.Equal(new[] { 3.0, 5.0 }, plan.Cuts);
        Assert.False(plan.MoveCut(0, 5.01));
        Assert.Equal(new[] { 3.0, 5.0 }, plan.Cuts);
        Assert.Equal(1, plan.CutNear(5.2, 0.4));
        Assert.Equal(-1, plan.CutNear(7, 0.4));
    }

    [Fact]
    public void TypedCutsReplaceTheCutsOrChangeNothing()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 30);
        Assert.True(plan.TrySetCuts("0:10, 0:25", out _));
        Assert.Equal("0:10.000, 0:25.000", plan.CutsText());
        Assert.False(plan.TrySetCuts("0:05, soon", out var problem));
        Assert.Contains("soon", problem);
        Assert.Equal(new[] { 10.0, 25.0 }, plan.Cuts);
        Assert.False(plan.TrySetCuts("0:05, 0:40", out problem));
        Assert.Equal(new[] { 10.0, 25.0 }, plan.Cuts);
    }

    [Fact]
    public void AnIntervalReplacesTheCutsAndIsSentAsEvery()
    {
        var plan = new TimelinePlan(TimelineKind.Split, 10);
        plan.AddCut(4);
        Assert.False(plan.SetEvery(0.05));
        Assert.False(plan.SetEvery(10));
        Assert.True(plan.SetEvery(2.5));
        Assert.Empty(plan.Cuts);
        Assert.Equal(new Dictionary<string, string> { ["every"] = "2.5" }, plan.ToParams());
        Assert.Equal("Cutting every 2.5 s, about 4 parts", plan.Describe());
        plan.AddCut(3);
        Assert.Null(plan.Every);
    }

    // --- bleep and range ----------------------------------------------------

    [Fact]
    public void BleepRangesAreOrderedClampedAndSentAsStartDashEnd()
    {
        var plan = new TimelinePlan(TimelineKind.Bleep, 3);
        Assert.NotNull(plan.Problem());
        Assert.True(plan.AddRange(2, 1));
        Assert.True(plan.AddRange(2.5, 9));
        Assert.False(plan.AddRange(1, 1.01));
        Assert.Equal(new Dictionary<string, string> { ["ranges"] = "1-2,2.5-3" }, plan.ToParams());
        Assert.Equal("2 ranges bleeped (1.500 s)", plan.Describe());
        plan.Undo();
        Assert.Single(plan.Ranges);
    }

    [Fact]
    public void TypedBleepRangesAreCheckedBeforeTheyReplaceAnything()
    {
        var plan = new TimelinePlan(TimelineKind.Bleep, 60);
        Assert.True(plan.TrySetRanges("0:03-0:05, 1.5-2", out _));
        Assert.Equal("0:03.000-0:05.000, 0:01.500-0:02.000", plan.RangesText());
        Assert.False(plan.TrySetRanges("0:05-0:03", out var problem));
        Assert.Contains("ends before", problem);
        Assert.False(plan.TrySetRanges("0:03", out _));
        Assert.False(plan.TrySetRanges("2:00-2:10", out _));
        Assert.Equal(2, plan.Ranges.Count);
    }

    [Fact]
    public void ARedactionWithNoRangeBlursTheWholeClipAndSendsNoTimes()
    {
        var plan = new TimelinePlan(TimelineKind.Range, 8);
        Assert.Null(plan.Problem());
        Assert.Empty(plan.ToParams());
        Assert.Equal("Blurred for the whole clip", plan.Describe());
        Assert.True(plan.SetSpan(5, 2));
        Assert.Equal(new Dictionary<string, string> { ["start"] = "2", ["end"] = "5" }, plan.ToParams());
        plan.ClearRange();
        Assert.Empty(plan.ToParams());
        Assert.False(plan.SetSpan(3, 3.01));
    }

    [Fact]
    public void AFileOfNoKnownLengthCannotBeApplied()
    {
        Assert.NotNull(new TimelinePlan(TimelineKind.Range, double.NaN).Problem());
        Assert.NotNull(new TimelinePlan(TimelineKind.Scrub, 0).Problem());
    }

    // --- the box on a frame --------------------------------------------------

    [Fact]
    public void ACropBoxIsEvenedDownSoTheWindowShowsTheSizeTheOutputGets()
    {
        var region = new RegionPlan(320, 240, covers: false);
        Assert.NotNull(region.Problem());
        region.Set(new Int32Rect(10, 10, 151, 99));
        Assert.Equal(new Int32Rect(10, 10, 150, 98), region.Region);
        Assert.Equal(new Dictionary<string, string> { ["x"] = "10", ["y"] = "10", ["w"] = "150", ["h"] = "98" },
            region.ToParams());
    }

    [Fact]
    public void ARedactionBoxIsEvenedUpSoNoCoveredPixelIsLeftClear()
    {
        var region = new RegionPlan(320, 240, covers: true);
        region.Set(new Int32Rect(10, 10, 151, 99));
        Assert.Equal(new Int32Rect(10, 10, 152, 100), region.Region);
        // At the right and bottom edges it grows the other way.
        region.Set(new Int32Rect(169, 141, 151, 99));
        Assert.Equal(new Int32Rect(168, 140, 152, 100), region.Region);
        var box = region.Region!.Value;
        Assert.True(box.X + box.Width <= 320 && box.Y + box.Height <= 240);
        Assert.True(box.X <= 169 && box.X + box.Width >= 320);
    }

    [Fact]
    public void TypedBoxNumbersThatLeaveTheFrameAreRefusedAndChangeNothing()
    {
        var region = new RegionPlan(320, 240, covers: true);
        Assert.True(region.TrySet("100", "50", "40", "30", out _));
        Assert.False(region.TrySet("300", "50", "40", "30", out var problem));
        Assert.Contains("does not fit", problem);
        Assert.False(region.TrySet("1", "1", "1", "30", out _));
        Assert.False(region.TrySet("-1", "1", "10", "30", out _));
        Assert.Equal(new Int32Rect(100, 50, 40, 30), region.Region);
    }

    // --- what ffprobe says ------------------------------------------------------

    [Fact]
    public void AnMp3sLengthAndTagsAreReadFromFfprobe()
    {
        // ffprobe -show_format -show_streams -of json on a tagged MP3, trimmed.
        var info = MediaInfo.Parse("""
            {"format":{"duration":"3.000000","tags":{"artist":"Old artist","title":"Old title","encoder":"Lavf62.12.103"}},
             "streams":[{"codec_type":"audio","duration":"3.000000","disposition":{"attached_pic":0},"tags":null},
                        {"codec_type":"video","duration":"3.000000","disposition":{"attached_pic":1}}]}
            """);
        Assert.Equal(3.0, info.Duration, 6);
        Assert.True(info.HasAudio);
        Assert.False(info.HasVideo);
        Assert.Equal("Old title", info.Tag("title"));
        Assert.Equal("Old artist", info.Tag("artist"));
        Assert.Equal(string.Empty, info.Tag("album"));
    }

    [Fact]
    public void AMatroskaLengthComesFromItsDurationTag()
    {
        var info = MediaInfo.Parse("""
            {"format":{"duration":"3.023000","tags":{"ENCODER":"Lavf62.12.103"}},
             "streams":[{"codec_type":"video","duration":null,"tags":{"DURATION":"00:00:03.523000000"}},
                        {"codec_type":"audio","duration":null,"tags":{"TITLE":"Stream title","DURATION":"00:00:03.023000000"}}]}
            """);
        Assert.Equal(3.523, info.Duration, 6);
        Assert.True(info.HasVideo);
        Assert.Equal("Stream title", info.Tag("title"));
    }

    [Fact]
    public void UnreadableProbeOutputIsAFileOfNoKnownLength()
    {
        Assert.Equal(0.0, MediaInfo.Parse("not json").Duration);
        Assert.Equal(0.0, MediaInfo.Parse("{}").Duration);
        Assert.Equal(0.0, MediaInfo.Parse("""{"format":{"duration":"N/A"}}""").Duration);
    }

    [Fact]
    public void FfprobeIsFoundWhereTheEngineLooksForItInTheSameOrder()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\tools\ffprobe.exe",
            @"C:\bin\ffprobe.exe",
            @"C:\Users\d\AppData\Local\Microsoft\WinGet\Links\ffprobe.exe",
            @"C:\Users\d\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_x\ffmpeg-8\bin\ffprobe.exe",
        };
        var environment = new Dictionary<string, string?>
        {
            ["PEEL_BACKEND_FFPROBE"] = @"D:\tools\ffprobe.exe",
            ["PATH"] = @"C:\nothing;C:\bin",
            ["LOCALAPPDATA"] = @"C:\Users\d\AppData\Local",
        };
        IEnumerable<string> Folders(string folder, string pattern) => folder.EndsWith("Packages")
            ? new[] { folder + @"\Gyan.FFmpeg_x" }
            : folder.EndsWith("Gyan.FFmpeg_x") ? new[] { folder + @"\ffmpeg-8" } : Array.Empty<string>();
        string? Find() => MediaProbe.Locate(name => environment.GetValueOrDefault(name), files.Contains, Folders);

        Assert.Equal(@"D:\tools\ffprobe.exe", Find());
        environment["PEEL_BACKEND_FFPROBE"] = @"D:\missing\ffprobe.exe";
        Assert.Equal(@"C:\bin\ffprobe.exe", Find());
        environment["PATH"] = @"C:\nothing";
        Assert.Equal(@"C:\Users\d\AppData\Local\Microsoft\WinGet\Links\ffprobe.exe", Find());
        files.Remove(@"C:\Users\d\AppData\Local\Microsoft\WinGet\Links\ffprobe.exe");
        Assert.Equal(@"C:\Users\d\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_x\ffmpeg-8\bin\ffprobe.exe", Find());
        files.Clear();
        Assert.Null(Find());
    }

    [Fact]
    public void TheFrameStripAsksForAboutTheRightNumberOfFramesAndNeverBelowTheEnginesFloor()
    {
        Assert.Equal(0.25, FrameGrab.StripInterval(3, 12), 9);
        Assert.Equal(50, FrameGrab.StripInterval(600, 12), 9);
        Assert.Equal(0.1, FrameGrab.StripInterval(0.5, 12), 9);
    }

    // --- pages ------------------------------------------------------------------

    [Fact]
    public void AnUnchangedPageOrderIsRefusedBecauseItWouldOnlyCopyThePdf()
    {
        var plan = new PagePlan(3);
        Assert.NotNull(plan.OrganizeProblem());
        Assert.Equal(new Dictionary<string, string> { ["pages"] = "1,2,3" }, plan.ToOrganizeParams());
    }

    [Fact]
    public void DroppingAPageBeforeAnotherSendsTheNewOrder()
    {
        var plan = new PagePlan(3);
        Assert.True(plan.MoveBefore(2, 0));
        Assert.Equal(new Dictionary<string, string> { ["pages"] = "3,1,2" }, plan.ToOrganizeParams());
        Assert.Equal("Keeping all 3 pages in the order 3, 1, 2", plan.DescribeOrganize());
        // Dropping a page where it already is does nothing.
        Assert.False(plan.MoveBefore(0, 0));
        Assert.False(plan.MoveBefore(0, 1));
        Assert.True(plan.MoveBefore(0, 3));
        Assert.Equal("1,2,3", plan.ToOrganizeParams()["pages"]);
        Assert.False(plan.Changed);
    }

    [Fact]
    public void TurnsAddUpAndAreSentKeyedBySourcePage()
    {
        var plan = new PagePlan(4);
        plan.MoveBefore(3, 0);
        Assert.True(plan.Turn(0, 90));
        Assert.True(plan.Turn(0, -180));
        Assert.True(plan.Turn(2, 90));
        Assert.False(plan.Turn(1, 45));
        Assert.Equal(new Dictionary<string, string> { ["pages"] = "4,1,2,3", ["rotate"] = """{"4":270,"2":90}""" },
            plan.ToOrganizeParams());
        Assert.True(plan.Turn(0, 90));
        Assert.DoesNotContain("\"4\"", plan.ToOrganizeParams()["rotate"]);
    }

    [Fact]
    public void PagesCanBeDeletedButNotTheLastOneAndEveryEditUndoes()
    {
        var plan = new PagePlan(2);
        Assert.True(plan.Remove(0));
        Assert.False(plan.Remove(0));
        Assert.Equal("2", plan.ToOrganizeParams()["pages"]);
        Assert.Equal("Keeping 1 of 2 pages in the order 2", plan.DescribeOrganize());
        plan.Undo();
        Assert.Equal("1,2", plan.ToOrganizeParams()["pages"]);
        plan.Turn(1, 90);
        plan.Reset();
        Assert.False(plan.Changed);
        plan.Undo();
        Assert.True(plan.Changed);
    }

    [Fact]
    public void CutsBetweenPagesBecomeTheEnginesRanges()
    {
        var plan = new PagePlan(5);
        Assert.Equal(string.Empty, plan.RangesFromCuts());
        Assert.True(plan.ToggleCut(3));
        Assert.Equal("1-3,4-5", plan.RangesFromCuts());
        Assert.True(plan.ToggleCut(1));
        Assert.True(plan.ToggleCut(4));
        Assert.Equal("1,2-3,4,5", plan.RangesFromCuts());
        Assert.False(plan.ToggleCut(5));
        Assert.False(plan.ToggleCut(0));
        Assert.True(plan.ToggleCut(1));
        Assert.Equal("1-3,4,5", plan.RangesFromCuts());
    }

    [Theory]
    [InlineData("", 5, 5)]
    [InlineData("1-3,5", 5, 2)]
    [InlineData(" 2 , 4-5 ", 5, 2)]
    [InlineData("1-3,,4", 5, 2)]
    [InlineData("0", 5, -1)]
    [InlineData("6", 5, -1)]
    [InlineData("3-1", 5, -1)]
    [InlineData("1-2-3", 5, -1)]
    [InlineData("a", 5, -1)]
    [InlineData(",", 5, -1)]
    public void TypedRangesAreCheckedTheWayTheEngineChecksThem(string text, int count, int files)
    {
        Assert.Equal(files, PagePlan.CheckRanges(text, count, out var problem));
        Assert.Equal(files < 0, problem.Length > 0);
    }

    [Fact]
    public void ADropInTheGridLandsBetweenTheTwoThumbnailsItIsBetween()
    {
        // Two rows of three, 100 wide and 150 tall, 10 apart.
        var items = new List<Rect>();
        for (var i = 0; i < 5; i++)
        {
            items.Add(new Rect(10 + (i % 3) * 110, 10 + (i / 3) * 160, 100, 150));
        }
        Assert.Equal(0, PageGrid.InsertionIndex(new Point(20, 50), items));
        Assert.Equal(1, PageGrid.InsertionIndex(new Point(70, 50), items));
        Assert.Equal(3, PageGrid.InsertionIndex(new Point(400, 50), items));
        Assert.Equal(3, PageGrid.InsertionIndex(new Point(5, 200), items));
        Assert.Equal(5, PageGrid.InsertionIndex(new Point(300, 220), items));
        Assert.Equal(5, PageGrid.InsertionIndex(new Point(300, 900), items));
        Assert.Equal(0, PageGrid.InsertionIndex(new Point(0, -50), items));
        Assert.Equal(0, PageGrid.InsertionIndex(new Point(5, 5), Array.Empty<Rect>()));
    }
}
