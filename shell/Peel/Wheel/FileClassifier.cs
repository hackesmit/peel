// Tells folders from files for the wheel's filter, off the UI thread.
//
// Directory.Exists on a path from an SMB share that has gone away can take
// tens of seconds. Run inside OLE's drag callback that would freeze the
// source's drag loop (Explorer) and Peel's own dispatcher, so Escape and the
// watchdog would stop working too. Here it runs on the thread pool with a
// deadline, at most two classifications at a time; past the deadline every
// path is guessed from its name (an extension means a file), and `peel run`
// still checks for real before it touches anything.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Peel.Wheel;

internal static class FileClassifier
{
    private static readonly SemaphoreSlim Slots = new(2, 2);

    internal static readonly TimeSpan DefaultDeadline = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Classify the paths, or guess them if that takes longer than the
    /// deadline. Never throws. <paramref name="isDirectory"/> is replaceable
    /// for the tests.
    /// </summary>
    internal static async Task<(IReadOnlyList<DraggedFile> Files, bool Guessed)> ClassifyAsync(
        IReadOnlyList<string> paths,
        TimeSpan deadline,
        Func<string, bool>? isDirectory = null)
    {
        isDirectory ??= Directory.Exists;
        var work = Task.Run(() =>
        {
            if (!Slots.Wait(deadline))
            {
                // Earlier classifications are still stuck on a dead share.
                return null;
            }
            try
            {
                return (IReadOnlyList<DraggedFile>)paths.Select(path => DraggedFile.Classify(path, isDirectory)).ToList();
            }
            finally
            {
                Slots.Release();
            }
        });
        var finished = await Task.WhenAny(work, Task.Delay(deadline)).ConfigureAwait(false);
        if (finished == work && work.IsCompletedSuccessfully && work.Result is { } files)
        {
            return (files, false);
        }
        // Observe a late failure so it never surfaces as unobserved.
        _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        return (paths.Select(DraggedFile.Guess).ToList(), true);
    }
}
