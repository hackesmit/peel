// The real answers for IDragProbe, from Windows.
//
// Which gestures are not file drags, and how this tells:
//   * Moving or sizing a window by its title bar or border. Windows runs a
//     modal move loop for that and flags the thread with GUI_INMOVESIZE, which
//     GetGUIThreadInfo reads without sending the window a message (so a hung
//     window cannot stall the hook). Both the foreground thread and the
//     thread under the pointer are asked.
//   * Every other non file gesture (text selection, a rubber band, a drag
//     that carries text or a browser image) cannot be told apart this early.
//     The wheel handles those itself: it goes away as soon as OLE shows it
//     data with no file paths, or as soon as the pointer has moved over it
//     and no OLE drag ever entered it (WheelWindow, ArrivalWatch).

namespace Peel.Hooks;

internal sealed class WindowsDragProbe : IDragProbe
{
    public bool PrimaryButtonDown() => Native.LeftButtonDown();

    public string? NotAFileDrag()
    {
        if (Native.ThreadInfo(0, out var foreground) && (foreground.Flags & Native.GUI_INMOVESIZE) != 0)
        {
            return $"a window is being moved or sized ({Native.ClassOf(foreground.MoveSize)})";
        }
        if (Native.GetCursorPos(out var point))
        {
            var thread = Native.ThreadAt(point.X, point.Y);
            if (thread != 0 && Native.ThreadInfo(thread, out var under) && (under.Flags & Native.GUI_INMOVESIZE) != 0)
            {
                return $"a window is being moved or sized ({Native.ClassOf(under.MoveSize)})";
            }
        }
        return null;
    }
}
