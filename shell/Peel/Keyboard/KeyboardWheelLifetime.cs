// When an open keyboard wheel has to close on its own, with no Windows types in
// it so the unit tests can drive every close path with a fake clock.
//
// The wheel only works while it holds the keyboard. If it loses the foreground
// (a click on Explorer, Alt+Tab), never gets it (the foreground lock refused
// the activation, or the user clicked elsewhere while the selection was being
// read), sees a mouse click outside its disc, or sits untouched for the idle
// timeout, it must close: a topmost wheel that cannot hear the keyboard is
// stuck on screen and, before this class, left the hotkey dead. The window
// asks Check on a short timer and on every Deactivated; the first non-null
// answer is the close reason.
namespace Peel.Keyboard;

internal sealed class KeyboardWheelLifetime
{
    /// <summary>
    /// Activating the wheel can bounce one transient deactivation back from the
    /// window that lost focus; a foreground check inside this window is not
    /// trusted yet. A click outside is trusted at once.
    /// </summary>
    internal const long GraceMs = 300;

    /// <summary>An open wheel nobody touches closes after this long.</summary>
    internal const long DefaultIdleMs = 30_000;

    private readonly long _idleMs;
    private long _openedAt;
    private long _lastActivity;
    private bool _open;
    private bool _hadForeground;

    internal KeyboardWheelLifetime(long idleMs = DefaultIdleMs)
    {
        _idleMs = idleMs > 0 ? idleMs : DefaultIdleMs;
    }

    internal bool IsOpen => _open;

    internal void Opened(long nowMs)
    {
        _open = true;
        _openedAt = nowMs;
        _lastActivity = nowMs;
        _hadForeground = false;
    }

    /// <summary>A key reached the wheel: the user is still here.</summary>
    internal void Activity(long nowMs) => _lastActivity = nowMs;

    internal void Closed() => _open = false;

    /// <summary>
    /// The reason the wheel must close now, or null to keep it open.
    /// </summary>
    /// <param name="nowMs">The clock.</param>
    /// <param name="wheelIsForeground">The wheel is the foreground window.</param>
    /// <param name="clickOutside">A mouse button is down with the cursor off the wheel.</param>
    internal string? Check(long nowMs, bool wheelIsForeground, bool clickOutside)
    {
        if (!_open)
        {
            return null;
        }
        if (wheelIsForeground)
        {
            _hadForeground = true;
        }
        if (clickOutside)
        {
            return "click outside the wheel";
        }
        if (nowMs - _openedAt < GraceMs)
        {
            return null;
        }
        if (!wheelIsForeground)
        {
            return _hadForeground ? "focus left the wheel" : "the wheel did not get the keyboard";
        }
        if (nowMs - _lastActivity >= _idleMs)
        {
            return "timeout";
        }
        return null;
    }
}
