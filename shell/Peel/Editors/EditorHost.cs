// The seam between the wheel and the editor windows.
//
// One thing here is load bearing and easy to break by accident: this is called
// from inside OLE's drop callback. WheelWindow.OnDrop raises Dropped while the
// dragging application's DoDragDrop waits on the return, so a window shown
// here, modal or not, freezes Explorer until the user is done with it. The
// method therefore queues the editor on the dispatcher, returns false so the
// wheel does not start a job, and starts the job itself when the user applies.
// False means "not now", not "failed": see IEditorHost.
//
// An entry this host does not recognise keeps the old behaviour (a toast
// saying the editor is not built), which is what the video, audio and PDF
// editors still get until their own bead lands.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Peel.Editors.Image;
using Peel.Engine;
using Peel.Jobs;

namespace Peel.Editors;

internal sealed class EditorHost : IEditorHost
{
    /// <summary>
    /// How long a preview may take before the user is told something is
    /// happening. A JPEG is a few milliseconds; a HEIC goes through the engine.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(600);

    private readonly EngineClient _engine;
    private readonly JobRunner _jobs;
    private readonly Dispatcher _ui;

    internal EditorHost(EngineClient engine, JobRunner jobs, Dispatcher ui)
    {
        _engine = engine;
        _jobs = jobs;
        _ui = ui;
    }

    /// <summary>The entries this host has a window for.</summary>
    internal static bool Knows(string id) => id is
        "image.crop" or "image.edit" or "image.annotate" or "image.redact" or "image.metadata";

    public bool TryCollectParams(ActionEntry entry, IReadOnlyList<string> files, out Dictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>();
        if (!Knows(entry.Id))
        {
            Log.Write($"{entry.Id} needs an editor, which is not built yet");
            _jobs.Report($"{entry.Label} needs a window to set it up. That editor is not yet built.");
            return false;
        }
        if (files.Count == 0)
        {
            return false;
        }
        var chosen = new List<string>(files);
        // Never inside the OLE callback: Explorer's drag loop is waiting.
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _ = OpenAsync(entry, chosen).ContinueWith(
                task => Log.Write($"{entry.Id}: the editor task faulted: {task.Exception}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        });
        Log.Write($"{entry.Id}: editor queued for {files.Count} file(s)");
        return false;
    }

    private async Task OpenAsync(ActionEntry entry, IReadOnlyList<string> files)
    {
        var schema = new ParamSchema(entry);
        DispatcherTimer? waiting = null;
        try
        {
            // A JPEG preview is immediate; a HEIC goes through the engine and
            // the user should not be left wondering whether the drop worked.
            waiting = new DispatcherTimer { Interval = Patience };
            waiting.Tick += (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                _jobs.Report($"Opening {entry.Label} for {System.IO.Path.GetFileName(files[0])}");
            };
            waiting.Start();
            PreviewSource? preview = null;
            EditorWindowBase? window;
            if (entry.Id == "image.metadata")
            {
                window = await MetadataWindowAsync(entry, files, schema).ConfigureAwait(true);
            }
            else
            {
                (window, preview) = await PictureWindowAsync(entry, files, schema).ConfigureAwait(true);
            }
            if (window is null)
            {
                return;
            }
            window.Applied += collected =>
            {
                // The parameters go in the log, shortened: the scripted test
                // reads them back and a support question about a wrong output
                // is answered by what was actually sent.
                var said = string.Join(" ", collected
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={Short(pair.Value)}"));
                Log.Write($"{entry.Id}: editor applied {said}");
                // The regions, the crop box and the strokes were drawn on the
                // bytes the preview was decoded from. Check they are still the
                // bytes on disk right before the engine opens the file.
                if (preview is not null && !preview.StillMatches(files[0], out var problem))
                {
                    Log.Write($"{entry.Id}: refused, {files[0]} changed since its preview was decoded");
                    _jobs.Report(problem);
                    return;
                }
                _jobs.Start(entry, files, collected);
            };
            window.Cancelled += () => Log.Write($"{entry.Id}: editor cancelled");
            window.Show();
            window.Activate();
            Log.Write($"{entry.Id}: editor open");
        }
        catch (Exception error)
        {
            // A fault here must cost the user a toast, never the tray app.
            Log.Write($"{entry.Id}: the editor could not open", error);
            _jobs.Report($"{entry.Label} could not open: {error.Message}");
        }
        finally
        {
            waiting?.Stop();
        }
    }

    /// <summary>A parameter value cut down to something a log line can hold.</summary>
    private static string Short(string value) =>
        value.Length <= 160 ? value : value[..160] + "...";

    private async Task<(EditorWindowBase? Window, PreviewSource? Preview)> PictureWindowAsync(
        ActionEntry entry, IReadOnlyList<string> files, ParamSchema schema)
    {
        var (preview, problem) = await PreviewSource.LoadAsync(files[0], _engine, CancellationToken.None)
            .ConfigureAwait(true);
        if (preview is null)
        {
            Log.Write($"{entry.Id}: no preview for {files[0]}: {problem}");
            _jobs.Report(problem.Length > 0 ? problem : $"Peel cannot show {System.IO.Path.GetFileName(files[0])}.");
            return (null, null);
        }
        Log.Write($"{entry.Id}: preview {preview.FileWidth}x{preview.FileHeight} via {preview.How}");
        EditorWindowBase window = entry.Id switch
        {
            "image.crop" => new CropEditor(files[0], preview, schema),
            "image.annotate" => new AnnotateEditor(files[0], preview),
            "image.redact" => new RedactEditor(files[0], preview, schema),
            _ => new EditEditor(files[0], files.Count, preview, schema),
        };
        return (window, preview);
    }

    private async Task<EditorWindowBase?> MetadataWindowAsync(
        ActionEntry entry, IReadOnlyList<string> files, ParamSchema schema)
    {
        // view writes nothing: it is how the form is filled.
        var result = await _engine.RunAsync(entry.Id, new[] { files[0] }, MetadataFields.ViewParams())
            .ConfigureAwait(true);
        if (!result.Ok)
        {
            var message = result.Message.Length > 0 ? result.Message : "the engine could not read its tags";
            Log.Write($"{entry.Id}: view failed: {message}");
            _jobs.Report($"Peel could not read the metadata of {System.IO.Path.GetFileName(files[0])}: {message}");
            return null;
        }
        var fields = MetadataFields.FromView(result.Data);
        Log.Write($"{entry.Id}: read {fields.Tags.Count} tag(s) from {files[0]}");
        return new MetadataEditor(files[0], fields, schema);
    }
}
