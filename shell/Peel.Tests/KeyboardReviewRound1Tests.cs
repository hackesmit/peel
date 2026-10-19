// Keyboard path, review round 1: the active Explorer tab pick, the drive root
// and virtual item rules, and every way an open keyboard wheel closes on its
// own. All pure, so they run without a desktop; the GUI side is covered by
// tests/e2e-keyboard.ps1 (scenario C) and tests/review-keyboard-gui.ps1.
using System;
using System.Collections.Generic;
using Peel.Keyboard;
using Xunit;

namespace Peel.Tests;

public class KeyboardReviewRound1Tests
{
    private static readonly IntPtr TabA = new(0x1001);
    private static readonly IntPtr TabB = new(0x1002);

    // ---- BLOCK 1: the active tab, not the first tab with the frame's HWND ----

    [Fact]
    public void Picks_the_active_tab_even_when_it_is_enumerated_last()
    {
        var candidates = new List<TabCandidate> { new(0, TabA), new(3, TabB) };
        var picked = SelectionRules.PickActiveTab(candidates, TabB, out var reason);
        Assert.Equal(3, picked);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Picks_the_active_tab_when_it_is_enumerated_first()
    {
        var candidates = new List<TabCandidate> { new(1, TabA), new(2, TabB) };
        Assert.Equal(1, SelectionRules.PickActiveTab(candidates, TabA, out _));
    }

    [Fact]
    public void A_single_window_without_tabs_is_unambiguous()
    {
        var candidates = new List<TabCandidate> { new(4, IntPtr.Zero) };
        Assert.Equal(4, SelectionRules.PickActiveTab(candidates, IntPtr.Zero, out _));
    }

    [Fact]
    public void Several_tabs_and_no_match_refuses_rather_than_guessing()
    {
        var candidates = new List<TabCandidate> { new(0, TabA), new(1, TabB) };
        var picked = SelectionRules.PickActiveTab(candidates, new IntPtr(0x9999), out var reason);
        Assert.Equal(-1, picked);
        Assert.Contains("tab", reason);

        picked = SelectionRules.PickActiveTab(candidates, IntPtr.Zero, out reason);
        Assert.Equal(-1, picked);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void The_visible_tab_window_is_the_active_one()
    {
        var tabs = new List<(IntPtr, bool)> { (TabA, false), (TabB, true) };
        Assert.Equal(TabB, SelectionRules.PickVisibleTab(tabs));
        Assert.Equal(IntPtr.Zero, SelectionRules.PickVisibleTab(new List<(IntPtr, bool)> { (TabA, false) }));
        Assert.Equal(IntPtr.Zero, SelectionRules.PickVisibleTab(new List<(IntPtr, bool)>()));
    }

    // ---- drive roots and virtual items ----

    private static SelectedKind Kind(string? path, bool file = false, bool dir = false) =>
        SelectionRules.Classify(path, _ => file, _ => dir);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    public void Drive_and_share_roots_are_rejected(string path)
    {
        Assert.Equal(SelectedKind.DriveRoot, Kind(path, dir: true));
    }

    [Theory]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Libraries")]
    public void Namespace_items_are_virtual(string? path)
    {
        Assert.Equal(SelectedKind.Virtual, Kind(path, file: true, dir: true));
    }

    [Fact]
    public void Real_files_and_folders_pass()
    {
        Assert.Equal(SelectedKind.File, Kind(@"C:\work\a.txt", file: true));
        Assert.Equal(SelectedKind.Folder, Kind(@"C:\work\photos", dir: true));
        Assert.Equal(SelectedKind.File, Kind(@"\\server\share\a.txt", file: true));
        Assert.Equal(SelectedKind.Virtual, Kind(@"C:\work\gone.txt"));
    }

    [Fact]
    public void Skipped_items_get_a_clear_message()
    {
        Assert.Equal(string.Empty, SelectionRules.SkippedMessage(0, 0, 3));
        Assert.StartsWith("Peel cannot convert a drive", SelectionRules.SkippedMessage(1, 0, 0));
        Assert.Contains("not a file or folder", SelectionRules.SkippedMessage(0, 1, 0));
        Assert.StartsWith("Peel skipped a drive", SelectionRules.SkippedMessage(1, 0, 2));
        Assert.Contains("2 drives or items", SelectionRules.SkippedMessage(1, 1, 0));
    }

    // ---- BLOCK 2: the wheel always closes on its own ----

    [Fact]
    public void A_wheel_that_never_gets_the_keyboard_closes_after_the_grace()
    {
        var life = new KeyboardWheelLifetime();
        life.Opened(1000);
        Assert.Null(life.Check(1100, wheelIsForeground: false, clickOutside: false));
        Assert.Equal("the wheel did not get the keyboard", life.Check(1000 + KeyboardWheelLifetime.GraceMs, false, false));
    }

    [Fact]
    public void A_wheel_that_loses_the_foreground_closes()
    {
        var life = new KeyboardWheelLifetime();
        life.Opened(0);
        Assert.Null(life.Check(50, true, false));
        Assert.Null(life.Check(500, true, false));
        Assert.Equal("focus left the wheel", life.Check(600, false, false));
    }

    [Fact]
    public void A_click_outside_closes_at_once_even_inside_the_grace()
    {
        var life = new KeyboardWheelLifetime();
        life.Opened(0);
        Assert.Equal("click outside the wheel", life.Check(10, true, true));
    }

    [Fact]
    public void An_idle_wheel_times_out_and_a_key_resets_the_clock()
    {
        var life = new KeyboardWheelLifetime(idleMs: 5000);
        life.Opened(0);
        Assert.Null(life.Check(4000, true, false));
        life.Activity(4000);
        Assert.Null(life.Check(8000, true, false));
        Assert.Equal("timeout", life.Check(9000, true, false));
    }

    [Fact]
    public void A_closed_wheel_never_asks_to_close_again()
    {
        var life = new KeyboardWheelLifetime();
        life.Opened(0);
        life.Closed();
        Assert.False(life.IsOpen);
        Assert.Null(life.Check(100_000, false, true));
        life.Opened(200_000);
        Assert.True(life.IsOpen);
        Assert.Null(life.Check(200_010, false, false));
    }

    [Fact]
    public void A_bad_idle_value_falls_back_to_the_default()
    {
        var life = new KeyboardWheelLifetime(idleMs: 0);
        life.Opened(0);
        Assert.Null(life.Check(KeyboardWheelLifetime.DefaultIdleMs - 1, true, false));
        Assert.Equal("timeout", life.Check(KeyboardWheelLifetime.DefaultIdleMs, true, false));
    }
}
