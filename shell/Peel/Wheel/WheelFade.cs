// Who finishes a wheel's close fade, with no Windows types in it so the unit
// tests can drive every order of events.
//
// A wheel closing fades out first and really hides when the fade ends. Two
// things can say "the fade is over": the animation's Completed event, and a
// fallback timer a little longer than the fade, because WPF does not run an
// animation while the screen is locked or the session is disconnected, and a
// Completed that never fires would leave a click-through wheel on screen for
// good. Whichever comes first finishes the close; the other is ignored.
//
// A wheel shown again while it fades cancels the close. Every close gets its
// own token, so a fade or timer left over from an earlier close can never hide
// the wheel that came back.
using System;

namespace Peel.Wheel;

internal sealed class WheelFade
{
    /// <summary>How long a close may take before it is finished without the animation.</summary>
    internal static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(400);

    private int _token;

    /// <summary>A close has begun and is not finished or cancelled yet.</summary>
    internal bool Closing { get; private set; }

    /// <summary>Begin a close. Returns the token its fade and its fallback pass to <see cref="Finish"/>.</summary>
    internal int Begin()
    {
        Closing = true;
        return ++_token;
    }

    /// <summary>
    /// The fade (or its fallback) is over. True only for the first call with
    /// the current close's token: that caller hides the window.
    /// </summary>
    internal bool Finish(int token)
    {
        if (!Closing || token != _token)
        {
            return false;
        }
        Closing = false;
        return true;
    }

    /// <summary>The wheel is shown again. True when a close was in flight and is now void.</summary>
    internal bool Cancel()
    {
        if (!Closing)
        {
            return false;
        }
        Closing = false;
        _token++;
        return true;
    }
}
