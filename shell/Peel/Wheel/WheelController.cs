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
//
// Sessions: every new drag, and every fresh showing of the wheel, starts a new
// session number. Anything asynchronous (the folder check, the engine's
// options) carries the number it started with and is dropped if the session
// has moved on, so the files of one drag can never be drawn on, or run from,
// the wheel of another.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Peel.Actions;
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
    private readonly StuckOverlayWatch _watch = new();

    private WheelCatalog _registry = WheelCatalog.From(null);
    private WheelCatalog _forFiles = WheelCatalog.From(null);
    private IReadOnlyList<DraggedFile> _files = Array.Empty<DraggedFile>();
    private string[] _paths = Array.Empty<string>();
    private bool _classified;
    private int _session;
    private CancellationTokenSource? _pending;
    private string _family = string.Empty;
    private int _page;

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
        _window.Hidden += _ => NewSession();

        // A missed button up must not leave an overlay sitting on the desktop.
        // The watch asks Windows for the physical button rather than trusting
        // the drag state, because the state is exactly what a missed hook
        // event gets wrong.
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _watchdog.Tick += (_, _) =>
        {
            if (_watch.Tick(_window.IsVisible, Native.LeftButtonDown()))
            {
                _window.HideNow("watchdog, the button is not down");
            }
        };
        _watchdog.Start();
    }

    internal void Attach(DragWatcher watcher)
    {
        watcher.DragStarted += OnDragStarted;
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

    /// <summary>Every wheel the engine registers, for the settings window.</summary>
    internal WheelCatalog Registry => _registry;

    private void OnWheelRequested(string pressed, int x, int y)
    {
        // Settings may have remapped the keys: look the wheel up by the
        // engine's name for the key that was pressed.
        var modifier = Peel.Settings.SettingsStore.Current.WheelKeyFor(pressed);
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
        if (!_window.IsVisible)
        {
            NewSession();
            _family = family;
            _page = 0;
            _window.ShowAt(x, y);
        }
        _family = family;
        _page = 0;
        Render();
    }

    /// <summary>
    /// A new drag passed the threshold: whatever the previous drag left on
    /// screen or in flight is over. Its wheel goes, its timers stop, its
    /// paths and its pending engine call are dropped.
    /// </summary>
    private void OnDragStarted(int x, int y)
    {
        _window.HideNow("a new drag started");
        NewSession();
    }

    private void NewSession()
    {
        _session++;
        _pending?.Cancel();
        _pending = null;
        _files = Array.Empty<DraggedFile>();
        _paths = Array.Empty<string>();
        _classified = false;
        _forFiles = WheelCatalog.From(null);
        _family = string.Empty;
        _page = 0;
    }

    /// <summary>
    /// The paths arrived from OLE. Returns at once: the wheel shows its
    /// loading state while the folder check and the engine run elsewhere.
    /// </summary>
    private void OnFilesSeen(string[] paths)
    {
        var session = _session;
        _paths = paths;
        _files = Array.Empty<DraggedFile>();
        _classified = false;
        _page = 0;
        Render();
        _ = ClassifyAsync(paths, session);
        _ = ConfirmWithEngineAsync(paths, session);
    }

    private async Task ClassifyAsync(string[] paths, int session)
    {
        try
        {
            var (files, guessed) = await FileClassifier.ClassifyAsync(paths, FileClassifier.DefaultDeadline).ConfigureAwait(false);
            if (guessed)
            {
                Log.Write($"the folder check took too long for {paths.Length} path(s), guessing from the names");
            }
            await _ui.InvokeAsync(() =>
            {
                if (session != _session)
                {
                    return;
                }
                _files = files;
                _classified = true;
                Render();
            });
        }
        catch (Exception error)
        {
            Log.Write("classifying the dragged paths failed", error);
        }
    }

    /// <summary>
    /// Ask the engine what these exact files allow. Cached per file set, so a
    /// second drag of the same selection costs nothing.
    /// </summary>
    private async Task ConfirmWithEngineAsync(string[] paths, int session)
    {
        var key = string.Join("\u0000", paths);
        if (_cache.TryGetValue(key, out var cached))
        {
            Apply(cached, session, "cache");
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
                Apply(catalog, session, "engine");
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

    private void Apply(WheelCatalog catalog, int session, string origin)
    {
        if (!_window.IsVisible || session != _session)
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

    private static string Labels(WheelPlan plan) => string.Join(",", plan.Segments.Select(s => s.Label));

    private void Render()
    {
        if (_family.Length == 0)
        {
            return;
        }
        var accent = WheelTheme.AccentFor(_family);
        if (_paths.Length == 0 || !_classified)
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
        if (!_classified || !files.SequenceEqual(_paths, StringComparer.Ordinal))
        {
            // The window already checked the drop against its own session;
            // this is the same check against the controller's, in case the
            // two ever disagree.
            Log.Write($"drop on {entry.Id} refused: the files are not this session's");
            return;
        }
        if (files.Length == 0)
        {
            Log.Write($"drop on {entry.Id} with no readable paths");
            _jobs.Report($"Peel could not read the dragged files, so {entry.Label} did not run.");
            return;
        }
        if (entry.IsShellAction)
        {
            // Share, Float, Copy, Move and Terminal are Windows UI, not engine
            // work; the engine marks them handled_by=shell and ActionHost turns
            // the drop into the action. This handler runs on the UI thread (a
            // WPF Drop event), which is where ActionHost needs to be called.
            ActionHost.Run(entry, files, _jobs, _ui);
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
