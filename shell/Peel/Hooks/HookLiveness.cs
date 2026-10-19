// Is Windows still calling Peel's low level hooks?
//
// Windows unhooks a low level hook silently once it has timed out too often
// (LowLevelHooksTimeout). Peel would then keep running with "hooks on" in its
// tray and never open a wheel again. GetLastInputInfo needs no hook and says
// when the user last touched the mouse or keyboard; if that keeps running
// ahead of the last hook callback, the hooks are gone and get reinstalled.
//
// Two checks in a row must agree, and reinstalls are spaced out, because input
// that goes to an elevated window or the secure desktop also skips a medium
// integrity hook. A reinstall in that case costs nothing but a log line.
namespace Peel.Hooks;

internal sealed class HookLiveness
{
    private readonly uint _lagMs;
    private readonly uint _spacingMs;
    private int _strikes;
    private uint? _lastReinstall;

    internal HookLiveness(uint lagMs = 3000, uint spacingMs = 60000)
    {
        _lagMs = lagMs;
        _spacingMs = spacingMs;
    }

    /// <summary>
    /// One periodic check. All three values are on the GetTickCount clock,
    /// which wraps every 49.7 days, so they are compared by difference.
    /// True means: reinstall the hooks now.
    /// </summary>
    internal bool Check(uint now, uint lastInput, uint lastHook)
    {
        var behind = unchecked(lastInput - lastHook);
        // A difference past half the clock means the hook is the newer one.
        var stale = behind > _lagMs && behind < uint.MaxValue / 2;
        _strikes = stale ? _strikes + 1 : 0;
        if (_strikes < 2)
        {
            return false;
        }
        if (_lastReinstall is { } last && unchecked(now - last) < _spacingMs)
        {
            return false;
        }
        _strikes = 0;
        _lastReinstall = now;
        return true;
    }
}
