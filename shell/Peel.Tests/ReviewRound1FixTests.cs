// The pure parts of the review round 1 fixes for hq-plct.7: the physical
// button check and the move-size refusal (Opus non-blocker, lucy 5), the
// non OLE gesture watch (lucy 5), the drag data check (lucy 1 and 5), hook
// liveness (Opus non-blocker), the clipboard retry (lucy 4) and the folder
// check with a deadline (lucy 6).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Peel.Engine;
using Peel.Hooks;
using Peel.Jobs;
using Peel.Wheel;
using Xunit;

namespace Peel.Tests;

public class ReviewRound1FixTests
{
    private const int Move = 0x0200;
    private const int Down = 0x0201;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int VkShift = 0xA0;

    private sealed class Probe : IDragProbe
    {
        public bool Button { get; set; } = true;

        public string? Reason { get; set; }

        public bool PrimaryButtonDown() => Button;

        public string? NotAFileDrag() => Reason;
    }

    private static DragStateMachine Dragging(Probe probe)
    {
        var machine = new DragStateMachine(4, 4, probe);
        machine.OnMouse(new MouseHookEvent(Down, 100, 100));
        machine.OnMouse(new MouseHookEvent(Move, 130, 120));
        Assert.True(machine.Dragging);
        return machine;
    }

    [Fact]
    public void AModifierWithTheButtonPhysicallyDownOpensTheWheel()
    {
        var machine = Dragging(new Probe());
        Assert.Equal(DragSignal.WheelRequested, machine.OnKey(new KeyHookEvent(KeyDown, VkShift)).Signal);
    }

    [Fact]
    public void AMissedButtonUpEndsTheDragInsteadOfOpeningAWheel()
    {
        // Released over an elevated window: the hook never saw the button up.
        var probe = new Probe();
        var machine = Dragging(probe);
        probe.Button = false;

        var update = machine.OnKey(new KeyHookEvent(KeyDown, VkShift));

        Assert.Equal(DragSignal.DragEnded, update.Signal);
        Assert.False(machine.Dragging);
        machine.OnKey(new KeyHookEvent(KeyUp, VkShift));
        Assert.Equal(DragSignal.None, machine.OnKey(new KeyHookEvent(KeyDown, VkShift)).Signal);
    }

    [Fact]
    public void AWindowBeingMovedGetsNoWheel()
    {
        var machine = Dragging(new Probe { Reason = "a window is being moved or sized" });
        var update = machine.OnKey(new KeyHookEvent(KeyDown, VkShift));
        Assert.Equal(DragSignal.WheelRefused, update.Signal);
        Assert.Contains("moved", update.Note);
        Assert.True(machine.Dragging);
    }

    [Fact]
    public void TheWheelWaitsWhileThePointerHoldsStill()
    {
        var watch = new ArrivalWatch(8, graceMs: 300);
        Assert.False(watch.ShouldGiveUp(0, 0));
        Assert.False(watch.ShouldGiveUp(3, 5000));
    }

    [Fact]
    public void TheWheelLeavesWhenThePointerMovedAndNoOleDragCame()
    {
        var watch = new ArrivalWatch(8, graceMs: 300);
        Assert.False(watch.ShouldGiveUp(20, 1000));
        Assert.False(watch.ShouldGiveUp(25, 1200));
        Assert.True(watch.ShouldGiveUp(30, 1300));
    }

    [Fact]
    public void AnOleDragThatEnteredKeepsTheWheel()
    {
        var watch = new ArrivalWatch(8, graceMs: 300);
        Assert.False(watch.ShouldGiveUp(20, 1000));
        watch.Entered();
        Assert.False(watch.ShouldGiveUp(200, 9000));
        watch.Reset();
        Assert.False(watch.ShouldGiveUp(20, 10000));
        Assert.True(watch.ShouldGiveUp(20, 10400));
    }

    [Fact]
    public void OnlyFilePathsCountAsFiles()
    {
        var files = new DataObject(DataFormats.FileDrop, new[] { @"C:\a\photo.png", @"C:\a\b.txt" });
        Assert.Equal(new[] { @"C:\a\photo.png", @"C:\a\b.txt" }, DropData.FilePaths(files));

        Assert.Null(DropData.FilePaths(new DataObject(DataFormats.UnicodeText, "selected text")));
        // An Outlook attachment: a descriptor and a stream, no path.
        var contents = new DataObject();
        contents.SetData("FileGroupDescriptorW", new System.IO.MemoryStream(new byte[8]));
        Assert.Null(DropData.FilePaths(contents));
        Assert.Null(DropData.FilePaths(new DataObject(DataFormats.FileDrop, Array.Empty<string>())));
        Assert.Null(DropData.FilePaths(null));
    }

    // Both hooks dead: input arrives that neither hook saw. Idle refresh is
    // pushed out of the way so only the live check is under test.
    private static HookSample Both(uint now, uint input, uint hook) =>
        new(now, true, input, hook, hook, CursorMoved: false);

    [Fact]
    public void HooksThatStopSeeingInputAreReinstalledAfterTwoChecks()
    {
        var liveness = new HookLiveness(lagMs: 3000, spacingMs: 60000, refreshMs: uint.MaxValue / 4);
        Assert.Null(liveness.Check(Both(10000, input: 10000, hook: 9990)));
        Assert.Null(liveness.Check(Both(20000, input: 19000, hook: 10000)));
        Assert.NotNull(liveness.Check(Both(25000, input: 24000, hook: 10000)));
        // Spaced out: not again within a minute.
        Assert.Null(liveness.Check(Both(30000, input: 29000, hook: 10000)));
        Assert.Null(liveness.Check(Both(35000, input: 34000, hook: 10000)));
        Assert.NotNull(liveness.Check(Both(90000, input: 89000, hook: 10000)));
    }

    [Fact]
    public void LiveHooksAndAWrappedClockAreNotAFault()
    {
        var liveness = new HookLiveness(lagMs: 3000, refreshMs: uint.MaxValue / 4);
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(liveness.Check(Both(5000, input: 4000, hook: 4100)));
        }
        // The tick count wrapped between the hook call and the input.
        Assert.Null(liveness.Check(Both(100, input: 50, hook: uint.MaxValue - 10)));
        Assert.Null(liveness.Check(Both(200, input: 60, hook: uint.MaxValue - 10)));
    }

    private static RunResult WithClipboard(string text) => JsonSerializer.Deserialize<RunResult>(
        "{\"ok\": true, \"outputs\": [], \"message\": \"Copied 1 path\", \"data\": {\"clipboard_text\": "
        + JsonSerializer.Serialize(text) + "}}")!;

    [Fact]
    public void TheClipboardTextComesFromTheResultData()
    {
        Assert.Equal(@"C:\a\photo.png", ClipboardWriter.TextFrom(WithClipboard(@"C:\a\photo.png")));
        Assert.Null(ClipboardWriter.TextFrom(JsonSerializer.Deserialize<RunResult>("""{"ok": true, "outputs": []}""")!));
        Assert.Null(ClipboardWriter.TextFrom(RunResult.Failed("x", "y")));
    }

    [Fact]
    public async Task ABusyClipboardIsRetriedUntilItIsFree()
    {
        var calls = 0;
        var pauses = 0;
        var outcome = await ClipboardWriter.TrySetAsync("text", _ =>
        {
            if (++calls < 4)
            {
                throw new ExternalException("OpenClipboard failed");
            }
        }, attempts: 10, TimeSpan.FromMilliseconds(1), _ => { pauses++; return Task.CompletedTask; });
        Assert.True(outcome.Ok);
        Assert.Equal(4, outcome.Attempts);
        Assert.Equal(3, pauses);
    }

    [Fact]
    public async Task AClipboardThatStaysBusyIsReportedAsAFailure()
    {
        var outcome = await ClipboardWriter.TrySetAsync("text",
            _ => throw new ExternalException("OpenClipboard failed"),
            attempts: 5, TimeSpan.Zero, _ => Task.CompletedTask);
        Assert.False(outcome.Ok);
        Assert.Equal(5, outcome.Attempts);
        Assert.Contains("OpenClipboard", outcome.Error);
    }

    [Fact]
    public async Task FoldersAreToldFromFilesOffTheCallingThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var seenOn = new List<int>();
        var (files, guessed) = await FileClassifier.ClassifyAsync(
            new[] { @"\\nas\photos\2026", @"\\nas\photos\a.png" },
            TimeSpan.FromSeconds(5),
            path => { lock (seenOn) seenOn.Add(Environment.CurrentManagedThreadId); return !path.EndsWith(".png"); });
        Assert.False(guessed);
        Assert.True(files[0].IsDirectory);
        Assert.False(files[1].IsDirectory);
        Assert.Equal("png", files[1].Extension);
        Assert.DoesNotContain(caller, seenOn);
    }

    [Fact]
    public async Task ADeadShareIsGuessedFromTheNamesAfterTheDeadline()
    {
        using var release = new ManualResetEventSlim(false);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (files, guessed) = await FileClassifier.ClassifyAsync(
                new[] { @"\\gone\share\Vacaciones", @"\\gone\share\IMG_0001.JPG" },
                TimeSpan.FromMilliseconds(300),
                _ => { release.Wait(TimeSpan.FromSeconds(30)); return false; });
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
            Assert.True(guessed);
            Assert.True(files[0].IsDirectory);
            Assert.False(files[1].IsDirectory);
            Assert.Equal("jpg", files[1].Extension);
        }
        finally
        {
            release.Set();
        }
    }
}
