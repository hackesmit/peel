// What the keyboard means while the wheel is open, with no Windows or WPF types
// in it so the unit tests can drive the whole interaction with no keyboard.
//
// This is the keyboard twin of Hooks/DragStateMachine: the drag path turns
// mouse and modifier events into meaning, this turns key presses into a moved
// highlight, a run, a page turn, a wheel switch or a cancel. The controller
// above it does the Windows work (show the overlay, call the engine, start the
// job); everything decided here is pure.
//
// The wheel is radial, but arrow navigation is a plain cycle around the ring:
// Right and Down advance one segment clockwise, Left and Up go one back, both
// wrapping. Empty segments (the "Nothing here" placeholder) are skipped, so the
// highlight only ever rests on something you can act on. A directional
// nearest-bearing mapping reads better but is ambiguous at the top and bottom
// of the ring; cycling is unambiguous and is what a person driving a menu from
// the keyboard expects.
using Peel.Wheel;

namespace Peel.Keyboard;

/// <summary>A key the open wheel cares about. Everything else is <see cref="Other"/>.</summary>
internal enum NavKey
{
    Left,
    Right,
    Up,
    Down,
    Enter,
    Escape,
    Shift,
    Ctrl,
    Alt,
    Other,
}

internal enum NavAction
{
    /// <summary>Nothing to do for this key.</summary>
    None,

    /// <summary>The highlight moved to <see cref="NavResult.Index"/>.</summary>
    Move,

    /// <summary>Enter on a runnable segment: run the entry at <see cref="NavResult.Index"/>.</summary>
    Run,

    /// <summary>Enter on the More segment: turn to the next page.</summary>
    Page,

    /// <summary>Escape: close the wheel and run nothing.</summary>
    Cancel,

    /// <summary>A modifier was tapped: switch to the wheel bound to <see cref="NavResult.Modifier"/>.</summary>
    Switch,
}

internal readonly record struct NavResult(NavAction Action, int Index = -1, string Modifier = "")
{
    internal static readonly NavResult Nothing = new(NavAction.None);
}

/// <summary>
/// The highlight state of an open keyboard wheel. One plan at a time; set a new
/// plan whenever the controller redraws (a new selection, a wheel switch or a
/// page turn) and the highlight snaps to the first segment worth resting on.
/// </summary>
internal sealed class WheelKeyboardNavigator
{
    private WheelPlan _plan = WheelPlan.Loading;
    private int _highlight = -1;

    /// <summary>The segment the highlight sits on, or -1 when nothing is selectable.</summary>
    internal int Highlight => _highlight;

    /// <summary>
    /// Adopt a freshly drawn plan and rest the highlight on its first
    /// selectable segment. A plan with nothing to offer leaves the highlight
    /// at -1, so Enter does nothing until a real entry appears.
    /// </summary>
    internal void SetPlan(WheelPlan plan)
    {
        _plan = plan;
        _highlight = FirstSelectable();
    }

    internal NavResult OnKey(NavKey key)
    {
        switch (key)
        {
            case NavKey.Left:
            case NavKey.Up:
                return Step(-1);

            case NavKey.Right:
            case NavKey.Down:
                return Step(1);

            case NavKey.Enter:
                return Activate();

            case NavKey.Escape:
                return new NavResult(NavAction.Cancel);

            case NavKey.Shift:
                return new NavResult(NavAction.Switch, Modifier: "Shift");

            case NavKey.Ctrl:
                return new NavResult(NavAction.Switch, Modifier: "Ctrl");

            case NavKey.Alt:
                return new NavResult(NavAction.Switch, Modifier: "Alt");

            default:
                return NavResult.Nothing;
        }
    }

    private NavResult Step(int direction)
    {
        var next = NextSelectable(direction);
        if (next < 0)
        {
            return NavResult.Nothing;
        }
        _highlight = next;
        return new NavResult(NavAction.Move, next);
    }

    private NavResult Activate()
    {
        if (!Selectable(_highlight))
        {
            return NavResult.Nothing;
        }
        return _plan.Segments[_highlight].Kind switch
        {
            SegmentKind.Entry => new NavResult(NavAction.Run, _highlight),
            SegmentKind.More => new NavResult(NavAction.Page, _highlight),
            _ => NavResult.Nothing,
        };
    }

    private bool Selectable(int index) =>
        index >= 0 && index < _plan.Count && _plan.Segments[index].Kind != SegmentKind.Empty;

    private int FirstSelectable()
    {
        for (var i = 0; i < _plan.Count; i++)
        {
            if (Selectable(i))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The next selectable segment in a direction, wrapping, or -1 when the
    /// wheel holds nothing selectable. Starts from the current highlight, or
    /// from just before the first segment when nothing is highlighted yet.
    /// </summary>
    private int NextSelectable(int direction)
    {
        var count = _plan.Count;
        if (count == 0)
        {
            return -1;
        }
        var cursor = _highlight < 0 ? (direction > 0 ? -1 : 0) : _highlight;
        for (var step = 0; step < count; step++)
        {
            cursor = (((cursor + direction) % count) + count) % count;
            if (Selectable(cursor))
            {
                return cursor;
            }
        }
        return -1;
    }
}
