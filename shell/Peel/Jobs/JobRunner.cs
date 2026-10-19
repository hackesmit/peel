// Runs `peel run` off the UI thread and reports it in a toast.
//
// Nothing here blocks the drag: by the time a job starts, the drop is over and
// the wheel is gone. At most a few engine processes run at once, because one
// drop of fifty files is already one process that handles all of them.
//
// Quit cancels every job: queued ones never start, running ones are killed
// with their whole process tree (EngineClient owns them through job objects).
// A result that carries data.clipboard_text is put on the clipboard before the
// toast says done; if the clipboard stays busy the toast says that instead.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Peel.Engine;

namespace Peel.Jobs;

internal sealed record JobRecord(DateTime When, string Label, bool Ok, string Message, IReadOnlyList<string> Outputs);

internal sealed class JobRunner
{
    private const int MaxRecent = 10;

    private readonly EngineClient _engine;
    private readonly ToastHost _toasts;
    private readonly Dispatcher _ui;
    private readonly SemaphoreSlim _slots = new(3, 3);
    private readonly List<JobRecord> _recent = new();
    private readonly CancellationTokenSource _quit = new();

    internal JobRunner(EngineClient engine, ToastHost toasts, Dispatcher ui)
    {
        _engine = engine;
        _toasts = toasts;
        _ui = ui;
    }

    /// <summary>Every finished job, for the tray menu and for later beads.</summary>
    internal IReadOnlyList<JobRecord> Recent
    {
        get
        {
            lock (_recent)
            {
                return _recent.ToList();
            }
        }
    }

    /// <summary>Raised on the UI thread when a job finishes.</summary>
    internal event Action<ActionEntry, RunResult>? Completed;

    /// <summary>Say something to the user without running anything.</summary>
    internal void Report(string message) => _toasts.Show("Peel", message, ToastKind.Info);

    internal void Start(ActionEntry entry, IReadOnlyList<string> files, IReadOnlyDictionary<string, string> parameters)
    {
        var count = files.Count;
        var subject = count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{count} files";
        var toast = _toasts.Show(entry.Label, $"Working on {subject}", ToastKind.Running);
        Log.Write($"job start {entry.Id} on {count} file(s)");

        _ = Task.Run(async () =>
        {
            RunResult result;
            var holding = false;
            try
            {
                await _slots.WaitAsync(_quit.Token).ConfigureAwait(false);
                holding = true;
                result = await _engine.RunAsync(entry.Id, files, parameters, _quit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_quit.IsCancellationRequested)
            {
                result = RunResult.Failed($"{entry.Label} was stopped because Peel quit.", "cancelled");
            }
            catch (Exception error)
            {
                Log.Write($"job {entry.Id} threw", error);
                result = RunResult.Failed($"{entry.Label} failed: {error.Message}", "shell_job");
            }
            finally
            {
                if (holding)
                {
                    _slots.Release();
                }
            }

            if (_quit.IsCancellationRequested)
            {
                Log.Write($"job end {entry.Id} after quit: {result.Message}");
                return;
            }
            try
            {
                await _ui.InvokeAsync(() => FinishAsync(entry, result, toast)).Task.Unwrap().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Log.Write($"job {entry.Id} could not report its result", error);
            }
        });
    }

    /// <summary>
    /// Quitting: queued jobs never start and running engines are killed with
    /// everything they started. Synchronous, so App.OnExit can call it last.
    /// </summary>
    internal void Shutdown()
    {
        try
        {
            _quit.Cancel();
        }
        catch (Exception error)
        {
            Log.Write("cancelling the jobs failed", error);
        }
        _engine.KillAll();
    }

    /// <summary>On the UI thread: clipboard first, then the toast and the record.</summary>
    private async Task FinishAsync(ActionEntry entry, RunResult result, ToastWindow toast)
    {
        var ok = result.Ok;
        var message = result.Message.Length > 0
            ? result.Message
            : ok ? $"{entry.Label} done" : $"{entry.Label} failed";
        if (ok && ClipboardWriter.TextFrom(result) is { } text)
        {
            var copied = await ClipboardWriter.TrySetAsync(
                text, ClipboardWriter.SetOnce, ClipboardWriter.DefaultAttempts, ClipboardWriter.DefaultPause);
            Log.Write($"clipboard for {entry.Id}: ok={copied.Ok} after {copied.Attempts} attempt(s) {copied.Error}");
            if (!copied.Ok)
            {
                ok = false;
                message = $"{entry.Label}: another app is holding the clipboard, so nothing was copied. Try again. ({copied.Error})";
            }
        }
        Log.Write($"job end {entry.Id} ok={ok} outputs={result.Outputs.Count} {message}");
        toast.Finish(ok ? ToastKind.Done : ToastKind.Failed, message, result.Outputs);
        Remember(new JobRecord(DateTime.Now, entry.Label, ok, message, result.Outputs));
        Completed?.Invoke(entry, result);
    }

    private void Remember(JobRecord record)
    {
        lock (_recent)
        {
            _recent.Insert(0, record);
            if (_recent.Count > MaxRecent)
            {
                _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
            }
        }
    }
}
