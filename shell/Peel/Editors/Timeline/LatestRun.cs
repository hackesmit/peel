// At most one engine run at a time for one kind of ask, and the latest ask wins.
//
// The region tools ask the engine for the frame under the playhead every time
// the playhead settles. Each ask is a whole `peel run av.video_snapshot`, so a
// user scrubbing in short hops used to leave a trail of engine processes all
// decoding frames nobody would see. Run cancels the ask before it (the engine
// client kills that engine), waits for it to end, and only then starts; an ask
// overtaken while it waits never starts at all.
//
// "Latest" is the order the asks came in, read under the lock, not whether a
// run has been cancelled yet: a newer ask publishes itself first and cancels
// the older one a moment later, and in that gap the older run can end.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Peel.Editors.Timeline;

internal sealed class LatestRun
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private Task _last = Task.CompletedTask;
    private int _active;
    private int _peak;

    /// <summary>Runs going now (0 or 1).</summary>
    internal int Active => Volatile.Read(ref _active);

    /// <summary>The most runs that were ever going at once.</summary>
    internal int Peak => Volatile.Read(ref _peak);

    /// <summary>
    /// Test seam: called on the asking thread after an ask is published as the
    /// latest and before it cancels the one before it.
    /// </summary>
    internal Action? Published { get; set; }

    /// <summary>
    /// Cancel the run before this one, wait for it to end, then run
    /// <paramref name="work"/>. Latest is true only when no newer ask came in
    /// and <paramref name="token"/> was not cancelled; Ran is false when the
    /// work never started because a newer ask overtook it first.
    /// </summary>
    internal async Task<(bool Ran, bool Latest, T? Value)> RunAsync<T>(
        Func<CancellationToken, Task<T>> work, CancellationToken token)
    {
        var mine = CancellationTokenSource.CreateLinkedTokenSource(token);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? overtaken;
        Task before;
        lock (_gate)
        {
            overtaken = _current;
            _current = mine;
            before = _last;
            _last = done.Task;
        }
        try
        {
            Published?.Invoke();
            // Outside the lock: a cancelled run may end inline, right here.
            overtaken?.Cancel();
            // Each run completes its own task in finally, so this never throws.
            await before.ConfigureAwait(true);
            // The overtaken run has ended, and only this ask still holds its
            // source (it left _current under the lock above).
            overtaken?.Dispose();
            lock (_gate)
            {
                // A newer ask may be published and not have cancelled this
                // one yet: it is still overtaken, so it never starts.
                if (!IsLatest(mine))
                {
                    return (false, false, default);
                }
                var now = Interlocked.Increment(ref _active);
                InterlockedMax(ref _peak, now);
            }
            T value;
            try
            {
                value = await work(mine.Token).ConfigureAwait(true);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
            bool latest;
            lock (_gate)
            {
                latest = IsLatest(mine);
            }
            return (true, latest, value);
        }
        finally
        {
            lock (_gate)
            {
                // Still the latest: nobody else holds it, so it goes now. An
                // overtaken source is the next ask's to dispose, once this
                // run has ended (done, below).
                if (ReferenceEquals(_current, mine))
                {
                    _current = null;
                    mine.Dispose();
                }
            }
            done.SetResult();
        }
    }

    // Call under _gate.
    private bool IsLatest(CancellationTokenSource mine) =>
        ReferenceEquals(_current, mine) && !mine.IsCancellationRequested;

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen)
        {
            var was = Interlocked.CompareExchange(ref target, value, seen);
            if (was == seen)
            {
                return;
            }
            seen = was;
        }
    }
}
