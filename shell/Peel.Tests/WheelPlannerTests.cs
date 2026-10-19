// Which entries reach the wheel, and how they are laid out when there are more
// of them than a person can aim at.
using System.Collections.Generic;
using System.Linq;
using Peel.Engine;
using Peel.Wheel;
using Xunit;

namespace Peel.Tests;

public class WheelPlannerTests
{
    private static ActionEntry Entry(
        string id,
        string label,
        string[]? inputs = null,
        int minFiles = 1,
        int? maxFiles = null,
        bool acceptsDirs = false,
        bool available = true) =>
        new()
        {
            Id = id,
            Family = "convert",
            Label = label,
            Group = "Images",
            Inputs = (inputs ?? new[] { "png" }).ToList(),
            MinFiles = minFiles,
            MaxFiles = maxFiles,
            AcceptsDirs = acceptsDirs,
            Available = available,
        };

    private static IReadOnlyList<DraggedFile> Files(params string[] names) =>
        names.Select(name => new DraggedFile(name, System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant(), false)).ToList();

    [Fact]
    public void AnEntryTakesOnlyItsOwnInputExtensions()
    {
        var entry = Entry("img.to_webp", "WEBP", new[] { "png", "jpg" });
        Assert.True(WheelPlanner.Accepts(entry, Files(@"C:\a\photo.png")));
        Assert.False(WheelPlanner.Accepts(entry, Files(@"C:\a\clip.mp4")));
    }

    [Fact]
    public void ExtensionsAreMatchedWithoutCase()
    {
        var entry = Entry("img.to_webp", "WEBP", new[] { "png" });
        Assert.True(WheelPlanner.Accepts(entry, Files(@"C:\a\PHOTO.PNG")));
    }

    [Fact]
    public void AStarEntryTakesAnything()
    {
        var entry = Entry("files.copy_path", "Copy Path", new[] { "*" }, acceptsDirs: true);
        Assert.True(WheelPlanner.Accepts(entry, Files(@"C:\a\clip.mp4")));
        Assert.True(WheelPlanner.Accepts(entry, new[] { new DraggedFile(@"C:\a\folder", string.Empty, true) }));
    }

    [Fact]
    public void FoldersNeedAcceptsDirs()
    {
        var entry = Entry("img.to_webp", "WEBP", new[] { "*" });
        Assert.False(WheelPlanner.Accepts(entry, new[] { new DraggedFile(@"C:\a\folder", string.Empty, true) }));
    }

    [Fact]
    public void FileCountLimitsAreRespected()
    {
        var merge = Entry("pdf.merge", "Merge", new[] { "pdf" }, minFiles: 2);
        Assert.False(WheelPlanner.Accepts(merge, Files(@"C:\a\one.pdf")));
        Assert.True(WheelPlanner.Accepts(merge, Files(@"C:\a\one.pdf", @"C:\a\two.pdf")));

        var single = Entry("img.crop", "Crop", new[] { "png" }, maxFiles: 1);
        Assert.False(WheelPlanner.Accepts(single, Files(@"C:\a\one.png", @"C:\a\two.png")));
    }

    [Fact]
    public void EveryFileMustBeAccepted()
    {
        var entry = Entry("img.to_webp", "WEBP", new[] { "png" });
        Assert.False(WheelPlanner.Accepts(entry, Files(@"C:\a\one.png", @"C:\a\two.mp4")));
    }

    [Fact]
    public void AnEntryWithAMissingBackendNeverReachesTheWheel()
    {
        var entries = new[] { Entry("a", "PNG"), Entry("b", "WEBP", available: false) };
        var kept = WheelPlanner.Filter(entries, Files(@"C:\a\photo.png"));
        Assert.Equal(new[] { "PNG" }, kept.Select(entry => entry.Label));
    }

    [Fact]
    public void ASmallWheelShowsEveryEntryOnOnePage()
    {
        var entries = Enumerable.Range(0, 5).Select(i => Entry($"id{i}", $"L{i}")).ToList();
        var plan = WheelPlanner.Build("convert", "Conversions", entries);
        Assert.Equal(5, plan.Count);
        Assert.Equal(1, plan.PageCount);
        Assert.All(plan.Segments, segment => Assert.Equal(SegmentKind.Entry, segment.Kind));
    }

    [Fact]
    public void ABigWheelPagesWithAMoreSegment()
    {
        var entries = Enumerable.Range(0, 20).Select(i => Entry($"id{i}", $"L{i}")).ToList();
        var plan = WheelPlanner.Build("convert", "Conversions", entries, page: 0, maxSegments: 6);

        Assert.Equal(6, plan.Count);
        Assert.Equal(4, plan.PageCount);
        Assert.Equal(SegmentKind.More, plan.Segments[^1].Kind);
        Assert.Equal(new[] { "L0", "L1", "L2", "L3", "L4" }, plan.Segments.Take(5).Select(s => s.Label));

        var second = WheelPlanner.Build("convert", "Conversions", entries, page: 1, maxSegments: 6);
        Assert.Equal(new[] { "L5", "L6", "L7", "L8", "L9" }, second.Segments.Take(5).Select(s => s.Label));
        Assert.Equal(1, second.Page);
    }

    [Fact]
    public void PagingWrapsRoundAndNeverGoesOutOfRange()
    {
        var entries = Enumerable.Range(0, 20).Select(i => Entry($"id{i}", $"L{i}")).ToList();
        var wrapped = WheelPlanner.Build("convert", "Conversions", entries, page: 4, maxSegments: 6);
        Assert.Equal(0, wrapped.Page);
        var last = WheelPlanner.Build("convert", "Conversions", entries, page: 3, maxSegments: 6);
        Assert.Equal(3, last.Page);
        Assert.Equal(new[] { "L15", "L16", "L17", "L18", "L19" }, last.Segments.Take(5).Select(s => s.Label));
    }

    [Fact]
    public void NothingValidStillDrawsAWheelThatSaysSo()
    {
        var plan = WheelPlanner.Build("tool", "File tools", System.Array.Empty<ActionEntry>());
        Assert.Equal(1, plan.Count);
        Assert.Equal(SegmentKind.Empty, plan.Segments[0].Kind);
        Assert.False(plan.Segments[0].Runnable);
    }

    [Fact]
    public void OnlyEntrySegmentsAreRunnable()
    {
        var entries = Enumerable.Range(0, 8).Select(i => Entry($"id{i}", $"L{i}")).ToList();
        var plan = WheelPlanner.Build("convert", "Conversions", entries, page: 0, maxSegments: 4);
        Assert.True(plan.Segments[0].Runnable);
        Assert.False(plan.Segments[^1].Runnable);
    }
}
