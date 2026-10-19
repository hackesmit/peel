// What the low level hooks mean. These run the whole interaction without a
// mouse: a click that is not a drag, a drag that is, a modifier tapped at the
// wrong moment, key repeat, and Escape.
using Peel.Hooks;
using Xunit;

namespace Peel.Tests;

public class DragStateMachineTests
{
    private const int Move = 0x0200;
    private const int Down = 0x0201;
    private const int Up = 0x0202;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SysKeyDown = 0x0104;
    private const int VkShift = 0xA0;
    private const int VkCtrl = 0xA2;
    private const int VkAlt = 0xA4;
    private const int VkEscape = 0x1B;

    private static DragStateMachine Machine() => new(4, 4);

    private static DragStateMachine Dragging()
    {
        var machine = Machine();
        machine.OnMouse(new MouseHookEvent(Down, 100, 100));
        machine.OnMouse(new MouseHookEvent(Move, 130, 120));
        Assert.True(machine.Dragging);
        return machine;
    }

    [Fact]
    public void AClickWithoutMovementIsNotADrag()
    {
        var machine = Machine();
        machine.OnMouse(new MouseHookEvent(Down, 100, 100));
        var moved = machine.OnMouse(new MouseHookEvent(Move, 102, 101));
        Assert.Equal(DragSignal.None, moved.Signal);
        Assert.False(machine.Dragging);
        Assert.Equal(DragSignal.None, machine.OnMouse(new MouseHookEvent(Up, 102, 101)).Signal);
    }

    [Fact]
    public void MovingPastTheThresholdStartsADragOnce()
    {
        var machine = Machine();
        machine.OnMouse(new MouseHookEvent(Down, 100, 100));
        var first = machine.OnMouse(new MouseHookEvent(Move, 120, 100));
        Assert.Equal(DragSignal.DragStarted, first.Signal);
        Assert.Equal(DragSignal.None, machine.OnMouse(new MouseHookEvent(Move, 140, 100)).Signal);
    }

    [Fact]
    public void ButtonUpEndsTheDragAndOnlyThen()
    {
        var machine = Dragging();
        Assert.Equal(DragSignal.DragEnded, machine.OnMouse(new MouseHookEvent(Up, 200, 200)).Signal);
        Assert.False(machine.Dragging);
        Assert.Equal(DragSignal.None, machine.OnMouse(new MouseHookEvent(Up, 200, 200)).Signal);
    }

    [Fact]
    public void AModifierOutsideADragDoesNothing()
    {
        var machine = Machine();
        Assert.Equal(DragSignal.None, machine.OnKey(new KeyHookEvent(KeyDown, VkShift)).Signal);
    }

    [Fact]
    public void EachModifierNamesItsOwnWheel()
    {
        foreach (var (vk, name) in new[] { (VkShift, "Shift"), (VkCtrl, "Ctrl"), (VkAlt, "Alt") })
        {
            var machine = Dragging();
            var update = machine.OnKey(new KeyHookEvent(vk == VkAlt ? SysKeyDown : KeyDown, vk));
            Assert.Equal(DragSignal.WheelRequested, update.Signal);
            Assert.Equal(name, update.Modifier);
        }
    }

    [Fact]
    public void KeyRepeatIsNotASecondTap()
    {
        var machine = Dragging();
        Assert.Equal(DragSignal.WheelRequested, machine.OnKey(new KeyHookEvent(KeyDown, VkShift)).Signal);
        Assert.Equal(DragSignal.None, machine.OnKey(new KeyHookEvent(KeyDown, VkShift)).Signal);
        machine.OnKey(new KeyHookEvent(KeyUp, VkShift));
        Assert.Equal(DragSignal.WheelRequested, machine.OnKey(new KeyHookEvent(KeyDown, VkShift)).Signal);
    }

    [Fact]
    public void AnotherModifierSwitchesWheelsWithoutReleasingTheFirst()
    {
        var machine = Dragging();
        Assert.Equal("Shift", machine.OnKey(new KeyHookEvent(KeyDown, VkShift)).Modifier);
        Assert.Equal("Ctrl", machine.OnKey(new KeyHookEvent(KeyDown, VkCtrl)).Modifier);
    }

    [Fact]
    public void EscapeCancelsWhetherOrNotADragIsRunning()
    {
        Assert.Equal(DragSignal.Cancel, Machine().OnKey(new KeyHookEvent(KeyDown, VkEscape)).Signal);
        Assert.Equal(DragSignal.Cancel, Dragging().OnKey(new KeyHookEvent(KeyDown, VkEscape)).Signal);
    }

    [Fact]
    public void ANonModifierKeyIsIgnored()
    {
        var machine = Dragging();
        Assert.Equal(DragSignal.None, machine.OnKey(new KeyHookEvent(KeyDown, 0x41)).Signal);
    }

    [Fact]
    public void BothSidesOfEachModifierCountAndNothingElseDoes()
    {
        Assert.Equal("Shift", DragStateMachine.ModifierName(0xA1));
        Assert.Equal("Ctrl", DragStateMachine.ModifierName(0x11));
        Assert.Equal("Alt", DragStateMachine.ModifierName(0xA5));
        Assert.Equal(string.Empty, DragStateMachine.ModifierName(0x41));
    }

    [Fact]
    public void ANewDragAfterACancelledOneStillWorks()
    {
        var machine = Dragging();
        machine.OnMouse(new MouseHookEvent(Up, 200, 200));
        machine.OnMouse(new MouseHookEvent(Down, 300, 300));
        Assert.Equal(DragSignal.DragStarted, machine.OnMouse(new MouseHookEvent(Move, 340, 330)).Signal);
    }
}
