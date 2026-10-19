// Bridge between the hook thread and the UI thread: the state machine decides,
// this class forwards the decision to the dispatcher and logs it.
using System;
using System.Windows.Threading;

namespace Peel.Hooks;

internal sealed class DragWatcher : IDisposable
{
    private readonly InputHooks _hooks;
    private readonly Dispatcher _ui;
    private readonly DragStateMachine _state;

    internal DragWatcher(InputHooks hooks, Dispatcher ui)
    {
        _hooks = hooks;
        _ui = ui;
        _state = new DragStateMachine(
            Native.GetSystemMetrics(Native.SM_CXDRAG),
            Native.GetSystemMetrics(Native.SM_CYDRAG),
            new WindowsDragProbe());
        _hooks.Mouse += OnMouse;
        _hooks.Key += OnKey;
    }

    /// <summary>A drag passed the threshold. Screen pixels.</summary>
    internal event Action<int, int>? DragStarted;

    /// <summary>The button came up while dragging.</summary>
    internal event Action? DragEnded;

    /// <summary>A modifier was tapped mid drag. Modifier name plus cursor in screen pixels.</summary>
    internal event Action<string, int, int>? WheelRequested;

    /// <summary>Escape.</summary>
    internal event Action? Cancelled;

    private void OnMouse(MouseHookEvent message) => Dispatch(_state.OnMouse(message));

    private void OnKey(KeyHookEvent message) => Dispatch(_state.OnKey(message));

    private void Dispatch(DragUpdate update)
    {
        switch (update.Signal)
        {
            case DragSignal.None:
                return;

            case DragSignal.DragStarted:
                Log.Write($"drag start at {update.X},{update.Y}");
                Post(() => DragStarted?.Invoke(update.X, update.Y));
                return;

            case DragSignal.DragEnded:
                Log.Write(update.Note.Length > 0 ? $"drag end ({update.Note})" : "drag end");
                Post(() => DragEnded?.Invoke());
                return;

            case DragSignal.WheelRefused:
                Log.Write($"modifier {update.Modifier} tapped, no wheel: {update.Note}");
                return;

            case DragSignal.Cancel:
                Log.Write("escape pressed");
                Post(() => Cancelled?.Invoke());
                return;

            case DragSignal.WheelRequested:
                // The key event carries no position, so ask for the cursor here
                // on the hook thread: by the time the UI thread runs, the
                // pointer may have moved on.
                var x = update.X;
                var y = update.Y;
                if (Native.GetCursorPos(out var point))
                {
                    x = point.X;
                    y = point.Y;
                }
                Log.Write($"modifier {update.Modifier} tapped mid drag at {x},{y}");
                Post(() => WheelRequested?.Invoke(update.Modifier, x, y));
                return;
        }
    }

    private void Post(Action action)
    {
        try
        {
            _ui.BeginInvoke(DispatcherPriority.Send, action);
        }
        catch (Exception error)
        {
            Log.Write("cannot reach the UI thread", error);
        }
    }

    public void Dispose()
    {
        _hooks.Mouse -= OnMouse;
        _hooks.Key -= OnKey;
    }
}
