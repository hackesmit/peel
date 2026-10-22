// Closing a wheel (hq-i9v0.2): both wheels fade out through WheelRenderer's
// close animation, input ends the moment the close begins, a wheel shown again
// during the fade stays up, and a fade whose Completed never fires (a locked
// screen runs no animations) still hides the window through WheelFade's
// fallback. The window tests show real windows far off screen and pump the
// dispatcher, so the timers and the animation really run.
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Peel.Keyboard;
using Peel.Wheel;
using Xunit;

namespace Peel.Tests;

public class WheelCloseTests
{
    private const int OffScreen = -30000;

    [Fact]
    public void TheFirstEndOfAFadeFinishesItAndTheSecondIsIgnored()
    {
        var fade = new WheelFade();
        Assert.False(fade.Closing);
        var token = fade.Begin();
        Assert.True(fade.Closing);
        Assert.True(fade.Finish(token), "the fade's end hides the window");
        Assert.False(fade.Finish(token), "the fallback after it does nothing");
        Assert.False(fade.Closing);
    }

    [Fact]
    public void AReopenDuringTheFadeVoidsThatFadesEnd()
    {
        var fade = new WheelFade();
        var first = fade.Begin();
        Assert.True(fade.Cancel(), "the wheel came back during the fade");
        Assert.False(fade.Closing);
        Assert.False(fade.Finish(first), "the old fade's end must not hide the wheel that came back");
        var second = fade.Begin();
        Assert.False(fade.Finish(first), "nor finish the next close early");
        Assert.True(fade.Finish(second));
        Assert.False(fade.Cancel(), "nothing to cancel once finished");
    }

    [Fact]
    public void TheFallbackIsLongerThanTheFadeAndShort()
    {
        Assert.True(WheelFade.Fallback > WheelRenderer.CloseTime + TimeSpan.FromMilliseconds(150));
        Assert.True(WheelFade.Fallback <= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void TheKeyboardWheelFadesOutWithInputEndedAtOnce() => WheelLookTests.OnSta(() =>
    {
        var window = new KeyboardWheelWindow();
        var hidden = 0;
        var keys = 0;
        window.Hidden += _ => hidden++;
        window.KeyPressed += _ => keys++;
        window.ShowAt(OffScreen, OffScreen);
        Assert.True(window.IsVisible);

        window.HideNow("escape");
        Assert.Equal(1, hidden);
        Assert.False(window.IsVisible, "a closing wheel reports itself gone at once");
        Assert.True(((Window)window).IsVisible, "but stays on screen for the fade");
        Assert.True(Transparent(window.Handle), "and lets the pointer through");
        Press(window, Key.Enter);
        Assert.Equal(0, keys);
        window.HideNow("escape");
        Assert.Equal(1, hidden);

        Pump(WheelRenderer.CloseTime + TimeSpan.FromMilliseconds(150));
        Assert.False(((Window)window).IsVisible, "the fade's end hides the window");
        Assert.False(Transparent(window.Handle), "a hidden wheel takes input again when it next opens");
        window.Close();
    });

    [Fact]
    public void AKeyboardWheelShownAgainDuringTheFadeStaysUp() => WheelLookTests.OnSta(() =>
    {
        var window = new KeyboardWheelWindow();
        window.ShowAt(OffScreen, OffScreen);
        window.HideNow("hotkey pressed again");
        window.ShowAt(OffScreen, OffScreen);
        // Off screen it never holds the foreground; keep the focus watch out
        // of a test that is about the fade.
        Field<DispatcherTimer>(window, "_watch").Stop();
        Assert.True(window.IsVisible);
        Assert.False(Transparent(window.Handle));

        Pump(WheelFade.Fallback + TimeSpan.FromMilliseconds(200));
        Assert.True(window.IsVisible, "neither the old fade nor its fallback hid the reopened wheel");
        Assert.True(((Window)window).IsVisible);
        window.Close();
    });

    [Fact]
    public void AKeyboardWheelWhoseFadeNeverEndsIsStillHidden() => WheelLookTests.OnSta(() =>
    {
        var window = new KeyboardWheelWindow();
        // A fade far longer than the fallback: its Completed never comes in
        // time, as on a locked screen.
        Renderer(window).CloseDuration = TimeSpan.FromSeconds(30);
        window.ShowAt(OffScreen, OffScreen);
        window.HideNow("escape");
        Pump(WheelRenderer.CloseTime + TimeSpan.FromMilliseconds(100));
        Assert.True(((Window)window).IsVisible, "no Completed came");

        Pump(WheelFade.Fallback);
        Assert.False(((Window)window).IsVisible, "the fallback hid it");
        window.Close();
    });

    [Fact]
    public void AStaleFadeEndDoesNotStopTheNextClosesFallback() => WheelLookTests.OnSta(() =>
    {
        // Review round 1 (hq-i9v0.2): the cancelled close's Completed came in
        // during the next close and stopped that close's fallback timer.
        var window = new KeyboardWheelWindow();
        try
        {
            window.ShowAt(OffScreen, OffScreen);
            window.HideNow("hotkey pressed again");
            window.ShowAt(OffScreen, OffScreen);
            Renderer(window).CloseDuration = TimeSpan.FromSeconds(30);
            window.HideNow("escape");

            Pump(WheelFade.Fallback + TimeSpan.FromMilliseconds(400));
            Assert.False(((Window)window).IsVisible, "the fallback still hid the wheel after the stale fade ended");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TheKeyboardWheelHandsTheForegroundBackTheMomentItCloses() => WheelLookTests.OnSta(() =>
    {
        var window = new KeyboardWheelWindow();
        try
        {
            var explorer = new IntPtr(0x1234);
            var foreground = explorer;
            window.FocusReturn.Windows = new KeyboardFocusReturn.Desktop(
                () => foreground,
                hwnd => hwnd == explorer,
                (_, _) => IntPtr.Zero,
                hwnd =>
                {
                    foreground = hwnd;
                    return true;
                });
            var reasons = new System.Collections.Generic.List<string>();
            window.Hidden += reasons.Add;
            window.ShowAt(OffScreen, OffScreen);
            foreground = window.Handle; // the wheel took the keyboard
            Renderer(window).CloseDuration = TimeSpan.FromSeconds(30);

            window.HideNow("escape");
            Assert.True(explorer == foreground, $"foreground {foreground} after the close, hidden for: {string.Join(", ", reasons)}, before {window.FocusReturn.Before}");
            Assert.True(((Window)window).IsVisible, "the wheel is still fading");
            Assert.True(NoActivate(window.Handle), "and cannot take the foreground again");

            window.ShowAt(OffScreen, OffScreen);
            Assert.False(NoActivate(window.Handle), "a wheel shown again takes the keyboard");
            Assert.Equal(explorer, window.FocusReturn.Before);
            window.HideNow("test over");
            Pump(WheelFade.Fallback + TimeSpan.FromMilliseconds(100));
            Assert.False(NoActivate(window.Handle), "a hidden wheel can be activated when it next opens");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TheForegroundGoesBackToTheWindowInFrontBeforeTheWheel()
    {
        var wheel = new IntPtr(1);
        var explorer = new IntPtr(2);
        var under = new IntPtr(3);
        var alive = new System.Collections.Generic.HashSet<IntPtr> { explorer, under };
        var foreground = explorer;
        var focus = new KeyboardFocusReturn(wheel)
        {
            Windows = new KeyboardFocusReturn.Desktop(
                () => foreground,
                alive.Contains,
                (_, _) => under,
                hwnd =>
                {
                    foreground = hwnd;
                    return true;
                }),
        };

        focus.Opening();
        foreground = wheel;
        focus.Opening();
        Assert.Equal(explorer, focus.Before);
        Assert.Equal(explorer, focus.Closing(0, 0));
        Assert.Equal(explorer, foreground);

        // The user clicked elsewhere first: the wheel does not take it back.
        foreground = under;
        Assert.Equal(IntPtr.Zero, focus.Closing(0, 0));
        Assert.Equal(under, foreground);

        // The window in front before is gone: the one under the wheel gets it.
        foreground = wheel;
        alive.Remove(explorer);
        Assert.Equal(under, focus.Closing(0, 0));
        Assert.Equal(under, foreground);

        // Nothing usable under it either: the foreground stays where it is.
        foreground = wheel;
        alive.Clear();
        Assert.Equal(IntPtr.Zero, focus.Closing(0, 0));
        Assert.Equal(wheel, foreground);
    }

    [Fact]
    public void ADragWheelWhoseFadeNeverEndsIsStillHidden() => WheelLookTests.OnSta(() =>
    {
        var window = new WheelWindow();
        Field<WheelRenderer>(window, "_renderer").CloseDuration = TimeSpan.FromSeconds(30);
        window.ShowAt(OffScreen, OffScreen);
        window.HideNow("dropped");
        Assert.False(window.IsVisible);
        Pump(WheelRenderer.CloseTime + TimeSpan.FromMilliseconds(100));
        Assert.True(((Window)window).IsVisible, "no Completed came");

        Pump(WheelFade.Fallback);
        Assert.False(((Window)window).IsVisible, "the fallback hid it");
        window.Close();
    });

    [Fact]
    public void ADragWheelFadesOutAndComesBackWhenShownAgain() => WheelLookTests.OnSta(() =>
    {
        var window = new WheelWindow();
        window.ShowAt(OffScreen, OffScreen);
        window.HideNow("timer");
        Pump(WheelRenderer.CloseTime + TimeSpan.FromMilliseconds(150));
        Assert.False(((Window)window).IsVisible, "the fade's end hides the window");

        window.ShowAt(OffScreen, OffScreen);
        window.HideNow("timer");
        window.ShowAt(OffScreen, OffScreen);
        Pump(WheelFade.Fallback + TimeSpan.FromMilliseconds(200));
        Assert.True(window.IsVisible, "a reopen during the fade is not hidden");
        window.HideNow("test over");
        window.Close();
    });

    private static WheelRenderer Renderer(KeyboardWheelWindow window) => Field<WheelRenderer>(window, "_renderer");

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static bool Transparent(IntPtr handle) =>
        (Peel.Hooks.Native.GetWindowStyleEx(handle) & WheelHubWindow.WS_EX_TRANSPARENT) != 0;

    private static bool NoActivate(IntPtr handle) =>
        (Peel.Hooks.Native.GetWindowStyleEx(handle) & Peel.Hooks.Native.WS_EX_NOACTIVATE) != 0;

    private static void Press(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window)!;
        var args = new KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args);
    }

    /// <summary>Run the dispatcher (timers, animations) for a while.</summary>
    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.Background) { Interval = time };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            frame.Continue = false;
        };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }
}
