// Pure drag state: what the low level hooks mean, with no Windows calls in it
// so the unit tests can drive the whole interaction without a mouse.
using System.Collections.Generic;

namespace Peel.Hooks;

internal enum DragSignal
{
    None,

    /// <summary>The left button is down and the pointer has passed the system drag threshold.</summary>
    DragStarted,

    /// <summary>The button came back up while a drag was in progress.</summary>
    DragEnded,

    /// <summary>A modifier was tapped during a drag: open, switch or close the wheel.</summary>
    WheelRequested,

    /// <summary>Escape was pressed: close the wheel and let the drag end normally.</summary>
    Cancel,

    /// <summary>
    /// A modifier was tapped during a gesture that is not a file drag (a
    /// window being moved or sized). No wheel; Note says why.
    /// </summary>
    WheelRefused,
}

internal readonly record struct DragUpdate(DragSignal Signal, int X, int Y, string Modifier = "", string Note = "")
{
    internal static readonly DragUpdate Nothing = new(DragSignal.None, 0, 0);
}

/// <summary>
/// What the state machine asks Windows at the moment a modifier is tapped,
/// behind an interface so the tests can answer instead. Called on the hook
/// thread: every answer must be a cheap call that sends no window message.
/// </summary>
internal interface IDragProbe
{
    /// <summary>The physical primary button, not the remembered one.</summary>
    bool PrimaryButtonDown();

    /// <summary>Null for a gesture that may be a file drag, else the reason it is not.</summary>
    string? NotAFileDrag();
}

internal sealed class DragStateMachine
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkEscape = 0x1B;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;

    private readonly int _thresholdX;
    private readonly int _thresholdY;
    private readonly HashSet<int> _keysDown = new();
    private readonly IDragProbe? _probe;
    private bool _buttonDown;
    private int _downX;
    private int _downY;

    internal DragStateMachine(int thresholdX, int thresholdY, IDragProbe? probe = null)
    {
        _probe = probe;
        // A zero threshold would call every click a drag.
        _thresholdX = thresholdX > 0 ? thresholdX : 4;
        _thresholdY = thresholdY > 0 ? thresholdY : 4;
    }

    internal bool Dragging { get; private set; }

    internal DragUpdate OnMouse(MouseHookEvent message)
    {
        switch (message.Message)
        {
            case Native.WM_LBUTTONDOWN:
                _buttonDown = true;
                Dragging = false;
                _downX = message.X;
                _downY = message.Y;
                return DragUpdate.Nothing;

            case Native.WM_LBUTTONUP:
                var wasDragging = Dragging;
                _buttonDown = false;
                Dragging = false;
                return wasDragging
                    ? new DragUpdate(DragSignal.DragEnded, message.X, message.Y)
                    : DragUpdate.Nothing;

            case Native.WM_MOUSEMOVE:
                if (!_buttonDown || Dragging)
                {
                    return DragUpdate.Nothing;
                }
                var movedX = message.X - _downX;
                var movedY = message.Y - _downY;
                if (System.Math.Abs(movedX) <= _thresholdX && System.Math.Abs(movedY) <= _thresholdY)
                {
                    return DragUpdate.Nothing;
                }
                Dragging = true;
                return new DragUpdate(DragSignal.DragStarted, message.X, message.Y);

            default:
                return DragUpdate.Nothing;
        }
    }

    internal DragUpdate OnKey(KeyHookEvent message)
    {
        var down = message.Message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        if (!down)
        {
            _keysDown.Remove(message.VkCode);
            return DragUpdate.Nothing;
        }
        if (!_keysDown.Add(message.VkCode))
        {
            // Key repeat, not a new tap.
            return DragUpdate.Nothing;
        }
        if (message.VkCode == VkEscape)
        {
            return new DragUpdate(DragSignal.Cancel, 0, 0);
        }
        var modifier = ModifierName(message.VkCode);
        if (modifier.Length == 0 || !Dragging)
        {
            return DragUpdate.Nothing;
        }
        if (_probe is not null)
        {
            // A button up the hook never saw (released over an elevated
            // window, Win+L, a UAC prompt) leaves Dragging true. Ask Windows
            // for the physical button before opening anything, and end the
            // stale drag if it is up.
            if (!_probe.PrimaryButtonDown())
            {
                _buttonDown = false;
                Dragging = false;
                return new DragUpdate(DragSignal.DragEnded, 0, 0, modifier, "the button is up, a button up was missed");
            }
            var reason = _probe.NotAFileDrag();
            if (reason is not null)
            {
                return new DragUpdate(DragSignal.WheelRefused, 0, 0, modifier, reason);
            }
        }
        return new DragUpdate(DragSignal.WheelRequested, 0, 0, modifier);
    }

    /// <summary>
    /// The modifier names match the engine's own <c>default_modifier</c> strings,
    /// so which wheel a key opens is decided by registry data, never here.
    /// </summary>
    internal static string ModifierName(int vkCode) => vkCode switch
    {
        VkShift or VkLShift or VkRShift => "Shift",
        VkControl or VkLControl or VkRControl => "Ctrl",
        VkMenu or VkLMenu or VkRMenu => "Alt",
        _ => string.Empty,
    };
}
