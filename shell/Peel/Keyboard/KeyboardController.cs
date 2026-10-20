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
// a time; a generation number drops a slow answer whose press has been
// superseded. A press while the wheel is open closes it and reads again, so a
// press always ends with a wheel over the current selection and the hotkey can
// never be left dead by a wheel that lost the keyboard.
//
// The Explorer read runs on its own STA thread (ExplorerSelection.ReadAsync),
// never on the UI thread that hosts the low level hooks. The modifier map is the
// per-file catalog's merged with the full registry's (fetched once, lazily), the
// same rule the drag path uses, so Shift, Ctrl and Alt reach every bound wheel.
// After Escape, a run or a timeout, focus goes back to the Explorer window the
// hotkey read from.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Peel.Actions;
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
    private IntPtr _returnTo;
    private WheelCatalog? _registry;
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
        _window.Dismissed += reason => Close(reason, restoreFocus: reason == "timeout");
        _window.Hidden += _ => Reset();
    }

    internal void Attach(HotkeyListener hotkey) => hotkey.Pressed += OnHotkey;

    /// <summary>On the UI thread (the hotkey source lives there).</summary>
    private void OnHotkey()
    {
        var foreground = KeyboardNative.GetForegroundWindow();
        if (_window.IsVisible)
        {
            // A press while the wheel is open reads the selection again. If the
            // wheel itself is in front, the window to read is the one it was
            // opened over.
            if (foreground == _window.Handle)
            {
                foreground = _returnTo;
            }
            Log.Write("hotkey pressed while the keyboard wheel is open, reopening");
            Close("hotkey pressed again", restoreFocus: false);
        }
        var generation = ++_generation;
        _returnTo = foreground;
        _ = OpenAsync(foreground, generation);
    }

    private async Task OpenAsync(IntPtr foreground, int generation)
    {
        try
        {
            var selection = await ExplorerSelection.ReadAsync(foreground, ExplorerSelection.DefaultTimeout).ConfigureAwait(false);
            if (!selection.HasFiles)
            {
                Log.Write($"hotkey pressed, no files: {selection.Reason}");
                Report(selection.Reason);
                return;
            }
            if (selection.Notice.Length > 0)
            {
                Report(selection.Notice);
            }
            var paths = selection.Paths.ToArray();
            Log.Write($"hotkey pressed, {paths.Length} file(s) selected");

            var registryTask = _registry is null ? _engine.OptionsAsync(Array.Empty<string>()) : null;
            var (files, _) = await FileClassifier.ClassifyAsync(paths, FileClassifier.DefaultDeadline).ConfigureAwait(false);
            var options = await _engine.OptionsAsync(paths).ConfigureAwait(false);
            var registry = registryTask is null ? null : await registryTask.ConfigureAwait(false);
            await _ui.InvokeAsync(() =>
            {
                if (registry is not null && registry.Ok)
                {
                    _registry = WheelCatalog.From(registry);
                }
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
                if (_registry is not null)
                {
                    catalog = catalog.WithModifiersFrom(_registry);
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

    /// <summary>A toast from any thread: the toast windows live on the UI thread.</summary>
    private void Report(string message)
    {
        Log.Write($"keyboard toast: {message}");
        _ = _ui.InvokeAsync(() => _jobs.Report(message));
    }

    /// <summary>Render a family's page, point the navigator at it and light the highlight.</summary>
    private void Draw(string family, int page)
    {
        _family = family;
        _page = page;
        var accent = WheelTheme.AccentFor(family);
        // A family bound only in the registry (not offered for these files)
        // still opens, from the registry's entries filtered by the files, as
        // the drag wheel does.
        var source = _catalog.Wheel(family) is null && _registry?.Wheel(family) is not null ? _registry : _catalog;
        var entries = WheelPlanner.Filter(WheelPlanner.Flatten(source.Wheel(family)), _files);
        var plan = WheelPlanner.Build(family, source.LabelFor(family), entries, page);
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
                Close("escape", restoreFocus: true);
                return;

            default:
                return;
        }
    }

    private void Switch(string pressed)
    {
        var modifier = Peel.Settings.SettingsStore.Current.WheelKeyFor(pressed);
        var family = _catalog.FamilyForModifier(modifier) ?? _registry?.FamilyForModifier(modifier);
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
        Close($"ran {entry.Id}", restoreFocus: true);
        if (files.Length == 0)
        {
            return;
        }
        if (entry.IsShellAction)
        {
            // Share, Float, Copy, Move and Terminal are Windows UI, not engine
            // work; ActionHost performs them, the same as the drop path. OnKey
            // runs on the UI thread (a WPF key event), which is where ActionHost
            // needs to be called.
            ActionHost.Run(entry, files, _jobs, _ui);
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

    /// <summary>
    /// Take the wheel down. With <paramref name="restoreFocus"/>, and only when
    /// the wheel held the foreground (so the user did not already move on), the
    /// Explorer window the hotkey read from is brought back to the front.
    /// </summary>
    private void Close(string reason, bool restoreFocus)
    {
        var hadForeground = _window.IsForeground;
        var returnTo = _returnTo;
        _window.HideNow(reason);
        Reset();
        if (restoreFocus && hadForeground && returnTo != IntPtr.Zero && KeyboardNative.IsWindow(returnTo))
        {
            var ok = KeyboardNative.SetForegroundWindow(returnTo);
            Log.Write($"keyboard focus returned to {returnTo.ToInt64()}: {ok}");
        }
    }

    /// <summary>Forget the open wheel. Runs on every hide, whoever hid it.</summary>
    private void Reset()
    {
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
