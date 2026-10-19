// Did an OLE drag ever reach the wheel?
//
// The hooks call any left button drag a drag, and a modifier tap opens the
// wheel. For an OLE drag, Windows offers the data to the wheel on the first
// pointer move over it (the pointer starts at its centre, inside the disc).
// If the pointer has clearly moved and still no DragEnter came, the gesture
// is not an OLE drag at all: selecting text, a rubber band selection, or a
// source whose drag cannot reach this window (an elevated one, through UIPI).
// The wheel then leaves, so it never sits in the way of that gesture.
//
// A wheel whose pointer has not moved waits: holding still after the tap is a
// normal pause before aiming, and gives OLE no reason to call yet.
namespace Peel.Wheel;

internal sealed class ArrivalWatch
{
    private readonly double _moveThreshold;
    private readonly long _graceMs;
    private bool _entered;
    private long? _movedAt;

    internal ArrivalWatch(double moveThreshold, long graceMs = 350)
    {
        _moveThreshold = moveThreshold;
        _graceMs = graceMs;
    }

    internal void Reset()
    {
        _entered = false;
        _movedAt = null;
    }

    /// <summary>OLE called the window (DragEnter or DragOver).</summary>
    internal void Entered() => _entered = true;

    /// <summary>
    /// One poll. <paramref name="distance"/> is how far the pointer is from
    /// where the wheel opened, in physical pixels. True means give up.
    /// </summary>
    internal bool ShouldGiveUp(double distance, long nowMs)
    {
        if (_entered)
        {
            return false;
        }
        if (_movedAt is null)
        {
            if (distance < _moveThreshold)
            {
                return false;
            }
            _movedAt = nowMs;
        }
        return nowMs - _movedAt.Value >= _graceMs;
    }
}
