// The keyboard path, start to finish: the hotkey fires, the foreground
// Explorer selection names the files, the engine names the segments, the arrow
// keys move the highlight and Enter runs it.
//
// It is the keyboard twin of Wheel/WheelController and shares nothing of that
// class's drag machinery, because there is no drag: no OLE, no button, no hub
// to fall through. What it does share is the engine and the job runner, so a
// conversion started from the keyboard shows the same toast and lands in the
// same recent-jobs list as one started from a drag, and the rules for a shell
// action or an entry that needs an editor are the same as the drop path's.
//
// A hotkey press is deliberate and rare, so unlike the drag wheel this one can
// afford to ask the engine what the exact files allow before it draws: it opens
// already knowing its segments, with no "reading the drag" state. One press at
// a time; a generation number drops a slow engine answer whose press has been
// superseded.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Peel.Engine;
using Peel.Hooks;
using Peel.Jobs;
using Peel.Wheel;

namespace Peel.Keyboard;

internal sealed class KeyboardController
{
    private const string ConversionFamily = "convert";

    private readonly EngineClient _engine;
    private readonly KeyboardWheelWindow _window;
    private readonly JobRunner _jobs;
    private readonly IEditorHost _editors;
    private readonly Dispatcher _ui;
    private readonly WheelKeyboardNavigator _navigator = new();

    private int _generation;
    private string[] _paths = Array.Empty<string>();
    private IReadOnlyList<DraggedFile> _files = Array.Empty<DraggedFile>();
    private WheelCatalog _catalog = WheelCatalog.From(null);
    private string _family = string.Empty;
    private int _page;

    internal KeyboardController(
        EngineClient engine,
        KeyboardWheelWindow window,
        JobRunner jobs,
        IEditorHost editors,
        Dispatcher ui)
    {
        _engine = engine;
        _window = window;
        _jobs = jobs;
        _editors = editors;
        _ui = ui;
        _window.KeyPressed += OnKey;
        _window.Dismissed += () => Close("focus left the wheel");
    }

    internal void Attach(HotkeyListener hotkey) => hotkey.Pressed += OnHotkey;

    /// <summary>On the UI thread (the hotkey source lives there).</summary>
    private void OnHotkey()
    {
        if (_window.IsVisible)
        {
            // A second press while the wheel is open is a no-op: the open wheel
            // already has the keyboard.
            Log.Write("hotkey pressed while the keyboard wheel is open, ignored");
            return;
        }
        var selection = ExplorerSelection.Read();
        if (!selection.HasFiles)
        {
            Log.Write($"hotkey pressed, no files: {selection.Reason}");
            _jobs.Report(selection.Reason);
            return;
        }
        var paths = selection.Paths.ToArray();
        var generation = ++_generation;
        Log.Write($"hotkey pressed, {paths.Length} file(s) selected");
        _ = OpenAsync(paths, generation);
    }

    private async Task OpenAsync(string[] paths, int generation)
    {
        try
        {
            var (files, _) = await FileClassifier.ClassifyAsync(paths, FileClassifier.DefaultDeadline).ConfigureAwait(false);
            var options = await _engine.OptionsAsync(paths).ConfigureAwait(false);
            await _ui.InvokeAsync(() =>
            {
                if (generation != _generation)
                {
                    return;
                }
                if (!options.Ok)
                {
                    Log.Write($"keyboard options failed: {options.Message}");
                    _jobs.Report(options.Message.Length > 0 ? options.Message : "Peel could not read these files.");
                    return;
                }
                var catalog = WheelCatalog.From(options);
                if (catalog.IsEmpty)
                {
                    _jobs.Report(paths.Length == 1
                        ? "Peel has nothing to offer for this file."
                        : "Peel has nothing to offer for these files.");
                    return;
                }
                _paths = paths;
                _files = files;
                _catalog = catalog;
                var family = catalog.Families.Contains(ConversionFamily, StringComparer.OrdinalIgnoreCase)
                    ? ConversionFamily
                    : catalog.Families[0];
                if (!Native.GetCursorPos(out var point))
                {
                    point = new Native.POINT { X = 0, Y = 0 };
                }
                _window.ShowAt(point.X, point.Y);
                Draw(family, 0);
            });
        }
        catch (Exception error)
        {
            Log.Write("opening the keyboard wheel failed", error);
        }
    }

    /// <summary>Render a family's page, point the navigator at it and light the highlight.</summary>
    private void Draw(string family, int page)
    {
        _family = family;
        _page = page;
        var accent = WheelTheme.AccentFor(family);
        var entries = WheelPlanner.Filter(WheelPlanner.Flatten(_catalog.Wheel(family)), _files);
        var plan = WheelPlanner.Build(family, _catalog.LabelFor(family), entries, page);
        _window.SetPlan(plan, accent);
        _navigator.SetPlan(plan);
        _window.SetHot(_navigator.Highlight);
        // The e2e test reads these two lines: the first lists the segments
        // clockwise from twelve o'clock (as the drag wheel's does), the second
        // says where the highlight rests.
        Log.Write($"keyboard wheel {family} page {page}: {Labels(plan)}");
        Log.Write($"keyboard highlight {HighlightLabel(plan)}");
    }

    private void OnKey(NavKey key)
    {
        var result = _navigator.OnKey(key);
        switch (result.Action)
        {
            case NavAction.Move:
                _window.SetHot(result.Index);
                Log.Write($"keyboard highlight {HighlightLabel(_window.Plan)}");
                return;

            case NavAction.Run:
                Run(result.Index);
                return;

            case NavAction.Page:
                Draw(_family, _page + 1);
                return;

            case NavAction.Switch:
                Switch(result.Modifier);
                return;

            case NavAction.Cancel:
                Close("escape");
                return;

            default:
                return;
        }
    }

    private void Switch(string modifier)
    {
        var family = _catalog.FamilyForModifier(modifier);
        if (family is null)
        {
            Log.Write($"no keyboard wheel is bound to {modifier}");
            return;
        }
        if (string.Equals(family, _family, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Draw(family, 0);
    }

    private void Run(int index)
    {
        var plan = _window.Plan;
        if (index < 0 || index >= plan.Count)
        {
            return;
        }
        var segment = plan.Segments[index];
        if (!segment.Runnable)
        {
            return;
        }
        var entry = segment.Entry!;
        var files = _paths;
        // Take the wheel down before the job starts: the choice is made.
        Close($"ran {entry.Id}");
        if (files.Length == 0)
        {
            return;
        }
        if (entry.IsShellAction)
        {
            // Share, Float, Terminal and the rest are Windows UI, not engine
            // work; the file-actions bead builds them. Same message the drop
            // path gives.
            Log.Write($"{entry.Id} is a shell action and is not built yet");
            _jobs.Report($"{entry.Label} is not built yet.");
            return;
        }
        var parameters = new Dictionary<string, string>();
        if (entry.NeedsEditor && !_editors.TryCollectParams(entry, files, out parameters))
        {
            return;
        }
        Log.Write($"keyboard run {entry.Id} on {files.Length} file(s)");
        _jobs.Start(entry, files, parameters);
    }

    private void Close(string reason)
    {
        _window.HideNow(reason);
        _generation++;
        _paths = Array.Empty<string>();
        _files = Array.Empty<DraggedFile>();
        _catalog = WheelCatalog.From(null);
        _family = string.Empty;
        _page = 0;
    }

    private string HighlightLabel(WheelPlan plan)
    {
        var index = _navigator.Highlight;
        return index >= 0 && index < plan.Count ? plan.Segments[index].Label : "none";
    }

    private static string Labels(WheelPlan plan) => string.Join(",", plan.Segments.Select(s => s.Label));
}
