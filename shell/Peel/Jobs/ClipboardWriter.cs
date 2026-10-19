// Puts text on the Windows clipboard for an engine result that carries
// data.clipboard_text (Copy Path today).
//
// The clipboard is a shared lock: any app that has it open makes OpenClipboard
// fail, and clipboard managers, remote desktop and Office hold it briefly all
// the time. So the write runs on the STA UI thread, as the clipboard requires,
// and is retried a few times with an awaited pause between tries, which keeps
// the UI thread free while another app finishes with it. When it still cannot
// get through, the caller says so in the toast instead of reporting success.
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Peel.Engine;

namespace Peel.Jobs;

internal readonly record struct ClipboardOutcome(bool Ok, int Attempts, string Error);

internal static class ClipboardWriter
{
    internal const int DefaultAttempts = 10;

    internal static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(60);

    /// <summary>The text an engine result asks to put on the clipboard, or null.</summary>
    internal static string? TextFrom(RunResult result)
    {
        if (result.Data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return result.Data.TryGetProperty("clipboard_text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
    }

    /// <summary>
    /// Try <paramref name="set"/> up to <paramref name="attempts"/> times. Any
    /// exception counts as contention and is retried; the last one is reported.
    /// </summary>
    internal static async Task<ClipboardOutcome> TrySetAsync(
        string text,
        Action<string> set,
        int attempts,
        TimeSpan pause,
        Func<TimeSpan, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var last = string.Empty;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                set(text);
                return new ClipboardOutcome(true, attempt, string.Empty);
            }
            catch (Exception error)
            {
                last = error.Message;
                if (attempt < attempts)
                {
                    await delay(pause);
                }
            }
        }
        return new ClipboardOutcome(false, attempts, last);
    }

    /// <summary>
    /// One real attempt, on the calling STA thread. WinForms is asked for a
    /// single try with no built in sleep, so the retry loop above stays the
    /// only one and never blocks the thread. The text is read back so a write
    /// another app overwrote at once is not reported as done.
    /// </summary>
    internal static void SetOnce(string text)
    {
        System.Windows.Forms.Clipboard.SetDataObject(text, true, 1, 0);
        var back = System.Windows.Forms.Clipboard.GetText();
        if (!string.Equals(back, text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the clipboard did not keep the text");
        }
    }
}
