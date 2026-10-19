// Keyboard path, review round 2: pins for the cases the round 2 GUI run
// (tests/review-keyboard-round2-gui.ps1) exercised on George: three tabs, a
// Home tab in front of tabs that hold a selection, and a wheel that loses the
// keyboard to Alt+Tab, the Win key or a click. All pure, so they run without a
// desktop.
using System;
using System.Collections.Generic;
using Peel.Keyboard;
using Xunit;

namespace Peel.Tests;

public class ReviewKeyboardRound2Tests
{
    private static readonly IntPtr TabA = new(0x2001);
    private static readonly IntPtr TabB = new(0x2002);
    private static readonly IntPtr TabC = new(0x2003);
    private static readonly IntPtr TabHome = new(0x2004);

    [Fact]
    public void Three_tabs_pick_the_visible_one_wherever_it_is_enumerated()
    {
        var candidates = new List<TabCandidate> { new(0, TabA), new(1, TabB), new(2, TabC) };
        Assert.Equal(1, SelectionRules.PickActiveTab(candidates, TabB, out _));
        Assert.Equal(2, SelectionRules.PickActiveTab(candidates, TabC, out _));
    }

    [Fact]
    public void A_home_tab_in_front_is_picked_not_a_tab_with_a_selection()
    {
        // On George build 26200 a Home tab is enumerated with an empty URL and
        // its own tab handle, so the pick lands on it and nothing is converted.
        var candidates = new List<TabCandidate> { new(0, TabA), new(1, TabB), new(2, TabC), new(3, TabHome) };
        Assert.Equal(3, SelectionRules.PickActiveTab(candidates, TabHome, out _));
    }

    [Fact]
    public void Several_tabs_whose_handles_cannot_be_read_refuse()
    {
        var candidates = new List<TabCandidate> { new(0, IntPtr.Zero), new(1, IntPtr.Zero) };
        Assert.Equal(-1, SelectionRules.PickActiveTab(candidates, TabA, out var reason));
        Assert.NotEqual(string.Empty, reason);
    }

    [Fact]
    public void Losing_the_foreground_after_holding_it_closes_after_the_grace()
    {
        var life = new KeyboardWheelLifetime(30_000);
        life.Opened(0);
        Assert.Null(life.Check(100, wheelIsForeground: true, clickOutside: false));
        // Alt+Tab, the Win key or a click on Explorer: the wheel is no longer in front.
        Assert.Null(life.Check(200, wheelIsForeground: false, clickOutside: false));
        Assert.Equal("focus left the wheel", life.Check(400, wheelIsForeground: false, clickOutside: false));
    }

    [Fact]
    public void Reopening_resets_the_lifetime_so_a_second_press_is_not_closed_at_once()
    {
        var life = new KeyboardWheelLifetime(1_000);
        life.Opened(0);
        Assert.Equal("timeout", life.Check(1_500, wheelIsForeground: true, clickOutside: false));
        life.Closed();
        life.Opened(2_000);
        Assert.Null(life.Check(2_100, wheelIsForeground: false, clickOutside: false));
        Assert.Null(life.Check(2_500, wheelIsForeground: true, clickOutside: false));
    }
}
