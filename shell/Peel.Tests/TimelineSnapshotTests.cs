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

        // The first run was cancelled; nothing else starts while it dies.
        Assert.True(runs[0].Cancelled);
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
        Assert.True(run.Cancelled);
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
}
