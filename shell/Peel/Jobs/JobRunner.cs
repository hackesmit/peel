// Runs `peel run` off the UI thread and reports it in a toast.
//
// Nothing here blocks the drag: by the time a job starts, the drop is over and
// the wheel is gone. At most a few engine processes run at once, because one
// drop of fifty files is already one process that handles all of them.
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
            await _slots.WaitAsync().ConfigureAwait(false);
            RunResult result;
            try
            {
                result = await _engine.RunAsync(entry.Id, files, parameters).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Log.Write($"job {entry.Id} threw", error);
                result = RunResult.Failed($"{entry.Label} failed: {error.Message}", "shell_job");
            }
            finally
            {
                _slots.Release();
            }

            await _ui.InvokeAsync(() =>
            {
                var message = result.Message.Length > 0
                    ? result.Message
                    : result.Ok ? $"{entry.Label} done" : $"{entry.Label} failed";
                Log.Write($"job end {entry.Id} ok={result.Ok} outputs={result.Outputs.Count} {message}");
                toast.Finish(result.Ok ? ToastKind.Done : ToastKind.Failed, message, result.Outputs);
                Remember(new JobRecord(DateTime.Now, entry.Label, result.Ok, message, result.Outputs));
                Completed?.Invoke(entry, result);
            });
        });
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
