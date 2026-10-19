// The interaction, start to finish: a modifier tap during a drag opens a
// wheel, the drag data names the files, the engine names the segments, and a
// drop on one of them starts a job.
//
// The wheel has to be on screen before anyone knows what is being dragged:
// OLE only offers the data once the pointer is over the window, and at the
// moment the wheel opens the pointer is at its centre. So the wheel opens in a
// reading state, paints the entries predicted from the registry preloaded at
// start up as soon as the paths arrive, and replaces them if the engine's own
// answer for these files turns out to differ. The engine stays the authority:
// it validates again in `peel run`, and its refusal is what the user reads.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Peel.Engine;
using Peel.Hooks;
using Peel.Jobs;

namespace Peel.Wheel;

internal sealed class WheelController
{
    private const int CacheLimit = 16;

    private readonly EngineClient _engine;
    private readonly WheelWindow _window;
    private readonly JobRunner _jobs;
    private readonly IEditorHost _editors;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, WheelCatalog> _cache = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _watchdog;

    private WheelCatalog _registry = WheelCatalog.From(null);
    private WheelCatalog _forFiles = WheelCatalog.From(null);
    private IReadOnlyList<DraggedFile> _files = Array.Empty<DraggedFile>();
    private CancellationTokenSource? _pending;
    private string _family = string.Empty;
    private int _page;
    private bool _dragging;

    internal WheelController(
        EngineClient engine,
        WheelWindow window,
        JobRunner jobs,
        IEditorHost editors,
        Dispatcher ui)
    {
        _engine = engine;
        _window = window;
        _jobs = jobs;
        _editors = editors;
        _ui = ui;
        _window.FilesSeen += OnFilesSeen;
        _window.Dropped += OnDropped;
        _window.MoreRequested += OnMoreRequested;

        // A missed button up (Windows can drop a slow hook) must not leave an
        // overlay sitting on the desktop.
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _watchdog.Tick += (_, _) =>
        {
            if (_window.IsVisible && !_dragging)
            {
                _window.HideNow("watchdog, no drag in progress");
            }
        };
        _watchdog.Start();
    }

    internal void Attach(DragWatcher watcher)
    {
        watcher.DragStarted += (_, _) => _dragging = true;
        watcher.DragEnded += OnDragEnded;
        watcher.WheelRequested += OnWheelRequested;
        watcher.Cancelled += () => _window.HideNow("escape");
    }

    /// <summary>
    /// Read the whole registry once at start up. It gives the modifier to
    /// wheel mapping and the entry list the first drag is drawn from.
    /// </summary>
    internal async Task PreloadAsync()
    {
        var options = await _engine.OptionsAsync(Array.Empty<string>()).ConfigureAwait(false);
        var catalog = WheelCatalog.From(options);
        await _ui.InvokeAsync(() =>
        {
            if (!options.Ok)
            {
                Log.Write($"preload failed: {options.Message}");
                return;
            }
            _registry = catalog;
            Log.Write($"registry preloaded: {string.Join(", ", catalog.Families)}");
        });
    }

    private void OnWheelRequested(string modifier, int x, int y)
    {
        var family = _forFiles.FamilyForModifier(modifier) ?? _registry.FamilyForModifier(modifier);
        if (family is null)
        {
            Log.Write($"no wheel is bound to {modifier}");
            return;
        }
        if (_window.IsVisible && _family == family)
        {
            _window.HideNow($"{modifier} tapped again");
            return;
        }
        _family = family;
        _page = 0;
        if (!_window.IsVisible)
        {
            _files = Array.Empty<DraggedFile>();
            _forFiles = WheelCatalog.From(null);
            _window.ShowAt(x, y);
        }
        Render();
    }

    private void OnFilesSeen(string[] paths)
    {
        _files = paths.Select(DraggedFile.FromPath).ToList();
        _page = 0;
        Render();
        _ = ConfirmWithEngineAsync(paths);
    }

    /// <summary>
    /// Ask the engine what these exact files allow. Cached per file set, so a
    /// second drag of the same selection costs nothing.
    /// </summary>
    private async Task ConfirmWithEngineAsync(string[] paths)
    {
        var key = string.Join("\u0000", paths);
        if (_cache.TryGetValue(key, out var cached))
        {
            Apply(cached, paths, "cache");
            return;
        }
        _pending?.Cancel();
        var source = new CancellationTokenSource();
        _pending = source;
        try
        {
            var options = await _engine.OptionsAsync(paths, source.Token).ConfigureAwait(false);
            if (!options.Ok)
            {
                Log.Write($"peel options failed: {options.Message}");
                return;
            }
            var catalog = WheelCatalog.From(options);
            await _ui.InvokeAsync(() =>
            {
                if (_cache.Count >= CacheLimit)
                {
                    _cache.Clear();
                }
                _cache[key] = catalog;
                Apply(catalog, paths, "engine");
            });
        }
        catch (Exception error)
        {
            Log.Write("peel options call failed", error);
        }
        finally
        {
            // _pending is only ever read and written on the UI thread, so the
            // next drag can never cancel a source that is already disposed.
            await _ui.InvokeAsync(() =>
            {
                if (ReferenceEquals(_pending, source))
                {
                    _pending = null;
                }
            });
            source.Dispose();
        }
    }

    private void Apply(WheelCatalog catalog, string[] paths, string origin)
    {
        if (!_window.IsVisible || !SameFiles(paths))
        {
            return;
        }
        var before = Labels(_window.Plan);
        _forFiles = catalog.WithModifiersFrom(_registry);
        Render();
        var after = Labels(_window.Plan);
        if (before != after)
        {
            Log.Write($"segments from {origin}: {after}");
        }
    }

    private bool SameFiles(string[] paths) =>
        _files.Count == paths.Length && _files.Select(file => file.Path).SequenceEqual(paths);

    private static string Labels(WheelPlan plan) => string.Join(",", plan.Segments.Select(s => s.Label));

    private void Render()
    {
        if (_family.Length == 0)
        {
            return;
        }
        var accent = WheelTheme.AccentFor(_family);
        if (_files.Count == 0)
        {
            _window.SetPlan(WheelPlan.Loading, accent);
            return;
        }
        var source = _forFiles.IsEmpty ? _registry : _forFiles;
        var entries = WheelPlanner.Filter(WheelPlanner.Flatten(source.Wheel(_family)), _files);
        var plan = WheelPlanner.Build(_family, source.LabelFor(_family), entries, _page);
        _window.SetPlan(plan, accent);
        // The scripted end to end test aims at a segment by reading this line,
        // so it prints the segments in the order they are drawn, clockwise
        // from twelve o'clock.
        Log.Write($"wheel {_family} page {_page}: {Labels(plan)}");
    }

    private void OnMoreRequested()
    {
        _page++;
        Render();
    }

    private void OnDragEnded()
    {
        _dragging = false;
        // OLE delivers the drop after the hook sees the button come up, so the
        // window stays up a moment longer. Hiding here would lose the drop.
        _window.HideAfter(TimeSpan.FromMilliseconds(500));
    }

    private void OnDropped(WheelSegment segment, string[] files)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Empty:
                return;
            case SegmentKind.More:
                Log.Write("dropped on More, nothing to run");
                return;
        }
        var entry = segment.Entry!;
        if (files.Length == 0)
        {
            Log.Write($"drop on {entry.Id} with no readable paths");
            _jobs.Report($"Peel could not read the dragged files, so {entry.Label} did not run.");
            return;
        }
        if (entry.IsShellAction)
        {
            // Share, Float and Terminal are Windows UI, not engine work. The
            // file actions bead implements them; until then say so plainly.
            Log.Write($"{entry.Id} is a shell action and is not built yet");
            _jobs.Report($"{entry.Label} is not built yet.");
            return;
        }
        var parameters = new Dictionary<string, string>();
        if (entry.NeedsEditor && !_editors.TryCollectParams(entry, files, out parameters))
        {
            return;
        }
        _jobs.Start(entry, files, parameters);
    }
}
