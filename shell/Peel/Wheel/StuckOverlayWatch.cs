// Last line of defence for the overlay.
//
// The wheel is only ever meant to be on screen during a drag. If Windows
// skips a low level hook callback (it does that silently when a hook answers
// too slowly), the button up never arrives and nothing in the normal flow
// takes the wheel down: a topmost drop target would sit on the desktop
// swallowing the next drag. So the watch does not trust the remembered drag
// state at all. It asks Windows for the physical button instead, and hides the
// wheel once the button has been up for two ticks in a row.
//
// Two ticks, not one, because a real drop arrives after the button comes up:
// one tick of grace keeps the watch from stealing a drop that is on its way.
namespace Peel.Wheel;

internal sealed class StuckOverlayWatch
{
    private readonly int _ticksBeforeHiding;
    private int _ticksWithButtonUp;

    internal StuckOverlayWatch(int ticksBeforeHiding = 2) => _ticksBeforeHiding = ticksBeforeHiding;

    /// <summary>One timer tick. True means the wheel is stuck and must go.</summary>
    internal bool Tick(bool wheelVisible, bool buttonDown)
    {
        if (!wheelVisible || buttonDown)
        {
            _ticksWithButtonUp = 0;
            return false;
        }
        _ticksWithButtonUp++;
        if (_ticksWithButtonUp < _ticksBeforeHiding)
        {
            return false;
        }
        _ticksWithButtonUp = 0;
        return true;
    }
}
