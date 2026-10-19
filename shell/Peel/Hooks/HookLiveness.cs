// Is Windows still calling each of Peel's low level hooks?
//
// Windows unhooks a low level hook silently once it has timed out too often
// (LowLevelHooksTimeout), and it can take one hook and leave the other. Peel
// would then keep running with "hooks on" in its tray and never open a wheel
// again. So each hook is judged on its own, against evidence of its own kind
// of input, and the answer to a dead hook is to reinstall both:
//   * the mouse hook: the cursor moved since the last check and
//     GetLastInputInfo saw input, yet the mouse hook stayed quiet for longer
//     than the lag;
//   * both hooks: GetLastInputInfo runs ahead of both of them;
//   * the keyboard hook: keys leave no evidence that can be read without a
//     hook (GetAsyncKeyState only sees a key at the instant it is asked), so
//     both hooks are reinstalled as a routine once the user has been idle for
//     a while, at most once every RefreshMs. A dead keyboard hook is back by
//     the next pause in typing, and an idle reinstall costs no input event.
// A reinstall that left a hook missing is tried again every RetryMs, for as
// long as it takes, instead of turning recovery off.
//
// Two checks in a row must agree before a live reinstall, and those are spaced
// out, because input that goes to an elevated window or the secure desktop
// also skips a medium integrity hook. A reinstall then costs a log line.
namespace Peel.Hooks;

/// <summary>One periodic sample. All ticks are on the GetTickCount clock.</summary>
internal readonly record struct HookSample(
    uint Now,
    bool Installed,
    uint LastInput,
    uint LastMouse,
    uint LastKey,
    bool CursorMoved);

internal sealed class HookLiveness
{
    private readonly uint _lagMs;
    private readonly uint _spacingMs;
    private readonly uint _retryMs;
    private readonly uint _idleMs;
    private readonly uint _refreshMs;
    private int _strikes;
    private uint? _lastReinstall;
    private uint? _since;

    internal HookLiveness(
        uint lagMs = 3000,
        uint spacingMs = 60000,
        uint retryMs = 30000,
        uint idleMs = 15000,
        uint refreshMs = 600000)
    {
        _lagMs = lagMs;
        _spacingMs = spacingMs;
        _retryMs = retryMs;
        _idleMs = idleMs;
        _refreshMs = refreshMs;
    }

    /// <summary>
    /// One periodic check. Returns why to reinstall both hooks now, or null.
    /// The clock wraps every 49.7 days, so ticks are compared by difference.
    /// </summary>
    internal string? Check(HookSample sample)
    {
        var now = sample.Now;
        _since ??= now;

        if (!sample.Installed)
        {
            // A failed install is retried, spaced out, for as long as it takes.
            _strikes = 0;
            return Due(now, _retryMs) ? Mark(now, "a hook is not installed") : null;
        }

        var mouseStale = sample.CursorMoved && Behind(sample.LastInput, sample.LastMouse) > _lagMs;
        var bothStale = Behind(sample.LastInput, sample.LastMouse) > _lagMs
            && Behind(sample.LastInput, sample.LastKey) > _lagMs;
        _strikes = mouseStale || bothStale ? _strikes + 1 : 0;
        if (_strikes >= 2 && Due(now, _spacingMs))
        {
            _strikes = 0;
            return Mark(now, mouseStale
                ? $"the cursor moved but the mouse hook last ran at {sample.LastMouse}, input at {sample.LastInput}"
                : $"input at {sample.LastInput} but the hooks last ran at {sample.LastMouse} and {sample.LastKey}");
        }

        var idle = Behind(now, sample.LastInput) >= _idleMs;
        var sinceRefresh = unchecked(now - (_lastReinstall ?? _since.Value));
        if (idle && sinceRefresh >= _refreshMs)
        {
            return Mark(now, "routine refresh while idle (a dead keyboard hook leaves no other trace)");
        }
        return null;
    }

    /// <summary>
    /// How far <paramref name="later"/> is ahead of <paramref name="earlier"/>;
    /// 0 when it is not ahead (a difference past half the clock means the
    /// other one is the newer).
    /// </summary>
    private static uint Behind(uint later, uint earlier)
    {
        var difference = unchecked(later - earlier);
        return difference < uint.MaxValue / 2 ? difference : 0;
    }

    private bool Due(uint now, uint spacing) =>
        _lastReinstall is not { } last || unchecked(now - last) >= spacing;

    private string Mark(uint now, string reason)
    {
        _lastReinstall = now;
        return reason;
    }
}
