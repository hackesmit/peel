// What every editor window has in common.
//
// An editor opens the moment a drag ends, so Peel is not the foreground
// application and Windows may refuse it the focus. It is shown topmost and
// activated, and gives the topmost up as soon as it has the focus, so it is
// never a window the user cannot cover.
//
// The two keys the SPEC names are here rather than in each window: Enter
// applies, Escape cancels. A window whose Enter means something else while a
// field has the focus overrides OnApplyKey.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace Peel.Editors;

internal abstract class EditorWindowBase : Window
{
    private bool _settled;

    protected EditorWindowBase()
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Activated += (_, _) =>
        {
            if (!_settled)
            {
                _settled = true;
                Topmost = false;
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => Report();
    }

    /// <summary>The parameters to run with. Raised once, before the window closes.</summary>
    internal event Action<IReadOnlyDictionary<string, string>>? Applied;

    /// <summary>Raised once when the window closes without applying.</summary>
    internal event Action? Cancelled;

    private IReadOnlyDictionary<string, string>? _result;
    private bool _reported;

    /// <summary>
    /// The parameters this window collected, or null when it is not ready.
    /// Returning null is how a window refuses Enter: it has already said why.
    /// </summary>
    protected abstract IReadOnlyDictionary<string, string>? Collect();

    /// <summary>Apply and close. Safe to call from a button or from Enter.</summary>
    protected void ApplyAndClose()
    {
        var collected = Collect();
        if (collected is null)
        {
            return;
        }
        _result = collected;
        Close();
    }

    protected void CancelAndClose()
    {
        _result = null;
        Close();
    }

    /// <summary>
    /// Enter while this element has the focus means something to the window
    /// itself, so it is not an Apply. Default: nothing does.
    /// </summary>
    protected virtual bool HandlesEnter(IInputElement? focused) => false;

    /// <summary>
    /// Say where the drawing area is on screen, in device pixels, and what
    /// image size it stands for. The scripted test on George aims a real mouse
    /// at an image pixel from this line, the same way the wheel test aims at a
    /// segment from the wheel's own log line. It is also the only way a test
    /// can find the area: a WPF Canvas has no UI Automation peer, so it is not
    /// in the automation tree at all, however it is named.
    /// </summary>
    protected void LogCanvas(string id, FrameworkElement canvas, int imageWidth, int imageHeight)
    {
        void Say()
        {
            try
            {
                if (!canvas.IsLoaded || canvas.ActualWidth <= 0 || !IsVisible)
                {
                    return;
                }
                var topLeft = canvas.PointToScreen(new Point(0, 0));
                var bottomRight = canvas.PointToScreen(new Point(canvas.ActualWidth, canvas.ActualHeight));
                Log.Write(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "editor canvas {0} at {1},{2} size {3}x{4} image {5}x{6}",
                    id,
                    (int)Math.Round(topLeft.X),
                    (int)Math.Round(topLeft.Y),
                    (int)Math.Round(bottomRight.X - topLeft.X),
                    (int)Math.Round(bottomRight.Y - topLeft.Y),
                    imageWidth,
                    imageHeight));
            }
            catch (Exception error)
            {
                Log.Write($"cannot report where the {id} canvas is", error);
            }
        }

        canvas.Loaded += (_, _) => Say();
        LocationChanged += (_, _) => Say();
        ContentRendered += (_, _) => Say();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CancelAndClose();
                break;
            case Key.Enter when !HandlesEnter(Keyboard.FocusedElement):
                e.Handled = true;
                ApplyAndClose();
                break;
        }
    }

    /// <summary>
    /// Exactly one of Applied or Cancelled, exactly once, whatever closed the
    /// window: the button, Enter, Escape, Alt+F4 or a fault.
    /// </summary>
    private void Report()
    {
        if (_reported)
        {
            return;
        }
        _reported = true;
        if (_result is { } parameters)
        {
            Applied?.Invoke(parameters);
        }
        else
        {
            Cancelled?.Invoke();
        }
    }
}
