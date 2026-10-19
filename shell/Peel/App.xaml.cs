// Peel's start up: hooks, wheel, tray, engine. There is no main window.
//
// The one rule that outranks every feature here: a fault in Peel must never
// cost the user a drag. Every unhandled exception is caught, logged and
// swallowed after the overlay is taken off the screen, the hooks never swallow
// an input event, and a watchdog hides the wheel if a button up is ever
// missed.
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Peel.Engine;
using Peel.Hooks;
using Peel.Jobs;
using Peel.Wheel;

namespace Peel;

public partial class App : Application
{
    private Mutex? _single;
    private InputHooks? _hooks;
    private DragWatcher? _watcher;
    private WheelWindow? _wheel;
    private WheelController? _controller;
    private ToastHost? _toasts;
    private Tray? _tray;
    private EngineClient? _engine;
    private DispatcherTimer? _hookReport;
    private JobRunner? _jobs;
    private readonly HookLiveness _liveness = new();
    private double _reportedWorst;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Start();
        Log.Write($"peel shell start, pid {Environment.ProcessId}, session {System.Diagnostics.Process.GetCurrentProcess().SessionId}");

        _single = new Mutex(true, @"Local\PeelTrayApp", out var mine);
        if (!mine)
        {
            Log.Write("another Peel is already running in this session, exiting");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("unhandled UI exception", args.Exception);
            SafeHideWheel();
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("unhandled exception: " + args.ExceptionObject);
            SafeHideWheel();
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _engine = new EngineClient();
        Log.Write($"engine: {_engine.Location?.Description ?? "not found"}");

        _toasts = new ToastHost();
        var jobs = new JobRunner(_engine, _toasts, Dispatcher);
        _jobs = jobs;
        var editors = new MissingEditorHost(jobs);

        _wheel = new WheelWindow();
        _wheel.Warm();
        _controller = new WheelController(_engine, _wheel, jobs, editors, Dispatcher);

        _hooks = new InputHooks();
        _hooks.Start();
        _watcher = new DragWatcher(_hooks, Dispatcher);
        _controller.Attach(_watcher);

        _tray = new Tray(jobs, Status, Quit);

        if (!_hooks.Installed)
        {
            jobs.Report("Peel cannot watch the mouse and keyboard, so the wheel will not open. See the log.");
        }
        if (_engine.Location is null)
        {
            jobs.Report("The Peel engine is not installed. Run install\\setup.ps1, then start Peel again.");
        }

        _ = _controller.PreloadAsync();

        // Evidence for the hook budget: the worst callback so far, whenever it
        // changes. The end to end test asserts this stays far under 10 ms.
        _hookReport = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _hookReport.Tick += (_, _) =>
        {
            ReportHooks();
            CheckHooksAlive();
        };
        _hookReport.Start();

        ExitAfterIfAsked();
    }

    private string Status()
    {
        var engine = _engine?.Location?.Description ?? "engine not found";
        var hooks = _hooks?.Installed == true ? "hooks on" : "hooks off";
        return $"{engine}, {hooks}";
    }

    private void ReportHooks()
    {
        if (_hooks is null)
        {
            return;
        }
        var worst = _hooks.WorstCallMs;
        if (worst > _reportedWorst + 0.05)
        {
            _reportedWorst = worst;
            Log.Write($"worst hook call {worst:0.000}ms");
        }
    }

    /// <summary>
    /// Windows can take a low level hook away without a word. If input keeps
    /// arriving that the hooks never saw, put them back.
    /// </summary>
    private void CheckHooksAlive()
    {
        if (_hooks is null || !_hooks.Installed || Native.LastInputTick() is not { } lastInput)
        {
            return;
        }
        var lastHook = _hooks.LastEventTick;
        if (_liveness.Check(unchecked((uint)Environment.TickCount), lastInput, lastHook))
        {
            _hooks.Reinstall($"input at {lastInput} but the last hook call at {lastHook}");
        }
    }

    private void SafeHideWheel()
    {
        try
        {
            _wheel?.HideNow("fault");
        }
        catch (Exception error)
        {
            Log.Write("cannot hide the wheel after a fault", error);
        }
    }

    /// <summary>
    /// PEEL_EXIT_AFTER=&lt;seconds&gt; makes the app quit by itself. The scripted
    /// end to end test sets it so a failed run can never leave a tray app
    /// behind on George.
    /// </summary>
    private void ExitAfterIfAsked()
    {
        var value = Environment.GetEnvironmentVariable("PEEL_EXIT_AFTER");
        if (!int.TryParse(value, out var seconds) || seconds <= 0)
        {
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log.Write($"PEEL_EXIT_AFTER={seconds} reached, quitting");
            Quit();
        };
        timer.Start();
    }

    private void Quit()
    {
        Log.Write("quit requested");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // First, so no engine process outlives the tray app: queued jobs are
        // cancelled and running ones are killed with their whole tree.
        _jobs?.Shutdown();
        _engine?.KillAll();
        ReportHooks();
        _hookReport?.Stop();
        _watcher?.Dispose();
        _hooks?.Dispose();
        _tray?.Dispose();
        _toasts?.CloseAll();
        Log.Write("peel shell stop");
        Log.Stop();
        _single?.Dispose();
        base.OnExit(e);
    }
}
