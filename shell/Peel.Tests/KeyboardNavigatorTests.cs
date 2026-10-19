// The keyboard twin of DragStateMachineTests: the whole highlight interaction
// driven with no keyboard, because the navigator is pure. Arrow cycling,
// skipping empty segments, Enter on an entry versus the More segment, Escape,
// and the modifier switches are all pinned here.
using System.Collections.Generic;
using System.Linq;
using Peel.Keyboard;
using Peel.Wheel;
using Xunit;

namespace Peel.Tests;

public class KeyboardNavigatorTests
{
    private static WheelPlan Plan(params SegmentKind[] kinds)
    {
        var segments = kinds
            .Select((kind, i) => new WheelSegment(
                kind,
                kind switch { SegmentKind.Entry => $"E{i}", SegmentKind.More => "More", _ => "..." },
                string.Empty,
                null))
            .ToList();
        return new WheelPlan("convert", "Conversions", segments, 0, 1);
    }

    private static WheelKeyboardNavigator On(WheelPlan plan)
    {
        var navigator = new WheelKeyboardNavigator();
        navigator.SetPlan(plan);
        return navigator;
    }

    [Fact]
    public void SetPlanRestsOnTheFirstEntry()
    {
        var navigator = On(Plan(SegmentKind.Entry, SegmentKind.Entry, SegmentKind.Entry));
        Assert.Equal(0, navigator.Highlight);
    }

    [Fact]
    public void APlanWithNothingSelectableHighlightsNothing()
    {
        Assert.Equal(-1, On(WheelPlan.Loading).Highlight);
        Assert.Equal(-1, On(Plan(SegmentKind.Empty)).Highlight);
    }

    [Fact]
    public void RightAndDownAdvanceClockwiseAndWrap()
    {
        foreach (var key in new[] { NavKey.Right, NavKey.Down })
        {
            var navigator = On(Plan(SegmentKind.Entry, SegmentKind.Entry, SegmentKind.Entry));
            Assert.Equal(new NavResult(NavAction.Move, 1), navigator.OnKey(key));
            Assert.Equal(new NavResult(NavAction.Move, 2), navigator.OnKey(key));
            Assert.Equal(new NavResult(NavAction.Move, 0), navigator.OnKey(key));
        }
    }

    [Fact]
    public void LeftAndUpGoBackAndWrap()
    {
        foreach (var key in new[] { NavKey.Left, NavKey.Up })
        {
            var navigator = On(Plan(SegmentKind.Entry, SegmentKind.Entry, SegmentKind.Entry));
            Assert.Equal(new NavResult(NavAction.Move, 2), navigator.OnKey(key));
            Assert.Equal(new NavResult(NavAction.Move, 1), navigator.OnKey(key));
        }
    }

    [Fact]
    public void EmptySegmentsAreSkipped()
    {
        // entry, empty, entry, more: the highlight never rests on the gap.
        var navigator = On(Plan(SegmentKind.Entry, SegmentKind.Empty, SegmentKind.Entry, SegmentKind.More));
        Assert.Equal(0, navigator.Highlight);
        Assert.Equal(2, navigator.OnKey(NavKey.Right).Index);
        Assert.Equal(3, navigator.OnKey(NavKey.Right).Index);
        Assert.Equal(0, navigator.OnKey(NavKey.Right).Index);
    }

    [Fact]
    public void TheMoveResultMatchesTheHighlight()
    {
        var navigator = On(Plan(SegmentKind.Entry, SegmentKind.Entry));
        var result = navigator.OnKey(NavKey.Right);
        Assert.Equal(NavAction.Move, result.Action);
        Assert.Equal(navigator.Highlight, result.Index);
    }

    [Fact]
    public void EnterOnAnEntryRunsIt()
    {
        var navigator = On(Plan(SegmentKind.Entry, SegmentKind.Entry));
        navigator.OnKey(NavKey.Right);
        Assert.Equal(new NavResult(NavAction.Run, 1), navigator.OnKey(NavKey.Enter));
    }

    [Fact]
    public void EnterOnMoreTurnsThePage()
    {
        var navigator = On(Plan(SegmentKind.Entry, SegmentKind.More));
        navigator.OnKey(NavKey.Right);
        Assert.Equal(NavAction.Page, navigator.OnKey(NavKey.Enter).Action);
    }

    [Fact]
    public void EnterWithNothingSelectableDoesNothing()
    {
        Assert.Equal(NavResult.Nothing, On(Plan(SegmentKind.Empty)).OnKey(NavKey.Enter));
    }

    [Fact]
    public void EscapeCancels()
    {
        Assert.Equal(NavAction.Cancel, On(Plan(SegmentKind.Entry)).OnKey(NavKey.Escape).Action);
    }

    [Fact]
    public void EachModifierAsksForItsOwnWheel()
    {
        var navigator = On(Plan(SegmentKind.Entry));
        Assert.Equal(new NavResult(NavAction.Switch, -1, "Shift"), navigator.OnKey(NavKey.Shift));
        Assert.Equal(new NavResult(NavAction.Switch, -1, "Ctrl"), navigator.OnKey(NavKey.Ctrl));
        Assert.Equal(new NavResult(NavAction.Switch, -1, "Alt"), navigator.OnKey(NavKey.Alt));
    }

    [Fact]
    public void ArrowsOnAnEmptyWheelDoNothing()
    {
        var navigator = On(Plan(SegmentKind.Empty));
        Assert.Equal(NavResult.Nothing, navigator.OnKey(NavKey.Right));
        Assert.Equal(NavResult.Nothing, navigator.OnKey(NavKey.Left));
        Assert.Equal(-1, navigator.Highlight);
    }

    [Fact]
    public void AnUnrelatedKeyIsIgnored()
    {
        Assert.Equal(NavResult.Nothing, On(Plan(SegmentKind.Entry)).OnKey(NavKey.Other));
    }

    [Fact]
    public void SwitchingPlansReHomesTheHighlight()
    {
        var navigator = On(Plan(SegmentKind.Entry, SegmentKind.Entry, SegmentKind.Entry));
        navigator.OnKey(NavKey.Right);
        navigator.OnKey(NavKey.Right);
        Assert.Equal(2, navigator.Highlight);
        navigator.SetPlan(Plan(SegmentKind.Empty, SegmentKind.Entry));
        Assert.Equal(1, navigator.Highlight);
    }
}
