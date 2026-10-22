// hq-plct.27, from lucy round 2 on hq-plct.11: every settled playhead move in
// a region tool started its own av.video_snapshot run and none was cancelled,
// so a user scrubbing in short hops had a pile of engines decoding frames
// nobody would see. The editor now routes its snapshots through LatestRun:
// one run at a time, a newer ask cancels the older, the latest ask wins.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Peel.Editors.Timeline;
using Xunit;

namespace Peel.Tests;

public class TimelineSnapshotTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A stand-in for an engine run: it ends when told to, or when cancelled
    /// once Dying is released (the engine client kills the process and waits
    /// for its pipes, so a cancelled run takes a moment to end).
    /// </summary>
    private sealed class FakeRun
    {
        internal readonly TaskCompletionSource<string> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Dying = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancelSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal bool Cancelled;

        internal async Task<string> RunAsync(string name, CancellationToken token)
        {
            Token = token;
            Started.SetResult();
            try
            {
                return await Finish.Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // What FrameGrab does: a cancelled run is an answer, not a throw.
                Cancelled = true;
                CancelSeen.TrySetResult();
                await Dying.Task;
                return $"{name} cancelled";
            }
        }
    }

    [Fact]
    public async Task ScrubbingKeepsOneSnapshotRunAndTheLatestWins()
    {
        var gate = new LatestRun();
        var runs = Enumerable.Range(0, 5).Select(_ => new FakeRun()).ToArray();
        var asks = new List<Task<(bool Ran, bool Latest, string? Value)>>();

        asks.Add(gate.RunAsync(token => runs[0].RunAsync("0", token), CancellationToken.None));
        await runs[0].Started.Task.WaitAsync(Wait);
        Assert.Equal(1, gate.Active);

        // Four more settles while the first is still decoding.
        for (var i = 1; i < runs.Length; i++)
        {
            var run = runs[i];
            var name = i.ToString();
            asks.Add(gate.RunAsync(token => run.RunAsync(name, token), CancellationToken.None));
        }

        // The first run was cancelled; nothing else starts while it dies. Its
        // catch runs as a continuation, so wait for it rather than read a flag.
        await runs[0].CancelSeen.Task.WaitAsync(Wait);
        Assert.All(runs.Skip(1), run => Assert.False(run.Started.Task.IsCompleted));
        runs[0].Dying.SetResult();
        // Once it has ended, only the latest ask starts.
        await runs[^1].Started.Task.WaitAsync(Wait);
        Assert.Equal(1, gate.Active);
        runs[^1].Finish.SetResult("frame 4");
        var results = await Task.WhenAll(asks).WaitAsync(Wait);

        Assert.Equal(1, gate.Peak);
        Assert.Equal(0, gate.Active);
        // The ones overtaken while they waited never started an engine at all.
        for (var i = 1; i < runs.Length - 1; i++)
        {
            Assert.False(runs[i].Started.Task.IsCompleted, $"ask {i} started");
            Assert.False(results[i].Ran);
            Assert.False(results[i].Latest);
        }
        Assert.True(results[0].Ran);
        Assert.False(results[0].Latest);
        Assert.Equal((true, true, "frame 4"), results[^1]);
    }

    [Fact]
    public async Task ASnapshotAfterTheLastOneEndedRunsAndWins()
    {
        var gate = new LatestRun();
        var first = await gate.RunAsync(_ => Task.FromResult("a"), CancellationToken.None).WaitAsync(Wait);
        var second = await gate.RunAsync(_ => Task.FromResult("b"), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal((true, true, "a"), first);
        Assert.Equal((true, true, "b"), second);
        Assert.Equal(1, gate.Peak);
    }

    [Fact]
    public async Task ClosingTheEditorCancelsTheRunningSnapshot()
    {
        var gate = new LatestRun();
        using var closing = new CancellationTokenSource();
        var run = new FakeRun();
        var ask = gate.RunAsync(token => run.RunAsync("0", token), closing.Token);
        await run.Started.Task.WaitAsync(Wait);
        run.Dying.SetResult();
        closing.Cancel();
        var result = await ask.WaitAsync(Wait);
        await run.CancelSeen.Task.WaitAsync(Wait);
        Assert.False(result.Latest);
        Assert.Equal(0, gate.Active);
    }

    [Fact]
    public async Task ManyAsksFromManyThreadsNeverOverlap()
    {
        var gate = new LatestRun();
        var asks = Enumerable.Range(0, 200).Select(i => Task.Run(() => gate.RunAsync(async token =>
        {
            await Task.Delay(1, CancellationToken.None);
            return i;
        }, CancellationToken.None))).ToArray();
        var results = await Task.WhenAll(asks).WaitAsync(Wait);
        Assert.Equal(1, gate.Peak);
        Assert.Equal(0, gate.Active);
        Assert.Contains(results, r => r.Ran && r.Latest);
    }

    // Review round 1 (lucy): a newer ask publishes itself under the lock and
    // cancels the older run only after it, so latest has to be the request
    // order read under the lock. Published pauses the newer ask in that gap.
    // The asks go through Task.Run so their continuations run on the pool
    // while the paused ask blocks.

    private static Task<(bool Ran, bool Latest, string? Value)> Ask(LatestRun gate, FakeRun run, string name) =>
        Task.Run(() => gate.RunAsync(token => run.RunAsync(name, token), CancellationToken.None));

    [Fact]
    public async Task ARunThatEndsBeforeTheNewerAskCancelsItIsNotLatest()
    {
        var gate = new LatestRun();
        var first = new FakeRun();
        var firstAsk = Ask(gate, first, "0");
        await first.Started.Task.WaitAsync(Wait);

        gate.Published = () =>
        {
            // The newer ask is published but has not cancelled the first yet,
            // and the first run's frame arrives right now.
            first.Finish.SetResult("frame 0");
            Assert.True(firstAsk.Wait(Wait), "the first ask did not end");
        };
        var second = new FakeRun();
        var secondAsk = Ask(gate, second, "1");

        var result = await firstAsk.WaitAsync(Wait);
        Assert.Equal((true, false, "frame 0"), result);
        await second.Started.Task.WaitAsync(Wait);
        second.Finish.SetResult("frame 1");
        Assert.Equal((true, true, "frame 1"), await secondAsk.WaitAsync(Wait));
        Assert.False(first.Cancelled);
        Assert.Equal(1, gate.Peak);
    }

    [Fact]
    public async Task AnAskOvertakenButNotYetCancelledNeverStarts()
    {
        var gate = new LatestRun();
        var runs = Enumerable.Range(0, 3).Select(_ => new FakeRun()).ToArray();
        var asks = new Task<(bool Ran, bool Latest, string? Value)>[3];
        asks[0] = Ask(gate, runs[0], "0");
        await runs[0].Started.Task.WaitAsync(Wait);
        // The second ask cancels the first and waits for it to die.
        asks[1] = Ask(gate, runs[1], "1");
        await runs[0].CancelSeen.Task.WaitAsync(Wait);

        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Published = () =>
        {
            // The third ask is published but has not cancelled the second yet,
            // and the first run ends now, which lets the second go on.
            runs[0].Dying.SetResult();
            var went = Task.WhenAny(asks[1], runs[1].Started.Task).Wait(Wait);
            paused.SetResult(went && !runs[1].Started.Task.IsCompleted);
        };
        asks[2] = Ask(gate, runs[2], "2");

        Assert.True(await paused.Task.WaitAsync(Wait), "the overtaken ask started an engine");
        Assert.Equal((false, false, (string?)null), await asks[1].WaitAsync(Wait));
        await runs[2].Started.Task.WaitAsync(Wait);
        runs[2].Finish.SetResult("frame 2");
        Assert.Equal((true, true, "frame 2"), await asks[2].WaitAsync(Wait));
        Assert.Equal(1, gate.Peak);
    }
}
