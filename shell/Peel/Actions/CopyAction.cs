// Copy: put the dragged files on the clipboard as files (CF_HDROP), so the
// next Paste in Explorer, a file dialog or an app drops the files themselves,
// not their paths. Copy Path (text, handled by the engine) is the other one.
//
// The clipboard is a shared lock, exactly as ClipboardWriter explains for Copy
// Path: any app that has it open makes the set fail, so the write runs on the
// STA UI thread and is retried a few times with an awaited pause between tries.
// There is no read back, because reading the clipboard blocks while it is busy.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using System.Windows.Threading;
using Peel.Jobs;

namespace Peel.Actions;

internal static class CopyAction
{
    /// <summary>The CF_HDROP list to place: every dropped path, in order.</summary>
    internal static StringCollection FileList(IReadOnlyList<string> files)
    {
        var list = new StringCollection();
        foreach (var file in files)
        {
            list.Add(file);
        }
        return list;
    }

    /// <summary>One real attempt on the calling STA thread. Throws on contention.</summary>
    internal static void SetOnce(IReadOnlyList<string> files) =>
        System.Windows.Forms.Clipboard.SetFileDropList(FileList(files));

    internal static void Run(IReadOnlyList<string> files, JobRunner jobs, Dispatcher ui)
    {
        _ = RunAsync(files, jobs, ui);
    }

    private static async Task RunAsync(IReadOnlyList<string> files, JobRunner jobs, Dispatcher ui)
    {
        var last = string.Empty;
        for (var attempt = 1; attempt <= ClipboardWriter.DefaultAttempts; attempt++)
        {
            try
            {
                await ui.InvokeAsync(() => SetOnce(files));
                var count = files.Count;
                var noun = count == 1 ? "file" : "files";
                Log.Write($"copy: {count} {noun} on the clipboard after {attempt} attempt(s)");
                jobs.Report($"Copied {count} {noun}. Paste where you need them.");
                return;
            }
            catch (Exception error)
            {
                last = error.Message;
                if (attempt < ClipboardWriter.DefaultAttempts)
                {
                    await Task.Delay(ClipboardWriter.DefaultPause);
                }
            }
        }
        Log.Write($"copy failed after {ClipboardWriter.DefaultAttempts} attempts: {last}");
        jobs.Report($"Copy could not reach the clipboard: another app is holding it. Try again. ({last})");
    }
}
