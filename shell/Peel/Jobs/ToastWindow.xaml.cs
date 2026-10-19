// A job's progress and its outcome, bottom right, never stealing focus.
//
// Peel does not use Windows toast notifications: those need a packaged app
// identity, and a tray app that converts a file in half a second should not
// need an install story to say so.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Peel.Hooks;

namespace Peel.Jobs;

internal enum ToastKind
{
    Running,
    Done,
    Failed,
    Info,
}

internal partial class ToastWindow : Window
{
    private static readonly Brush RunningBrush = Freeze(Color.FromRgb(0x5A, 0x5A, 0x6A));
    private static readonly Brush DoneBrush = Freeze(Color.FromRgb(0x49, 0xC9, 0x6D));
    private static readonly Brush FailedBrush = Freeze(Color.FromRgb(0xE5, 0x5B, 0x5B));
    private static readonly Brush InfoBrush = Freeze(Color.FromRgb(0xFF, 0x8A, 0x1E));

    private readonly DispatcherTimer _dismiss;
    private readonly Storyboard _pulse;
    private IReadOnlyList<string> _outputs = Array.Empty<string>();
    private bool _closed;

    internal ToastWindow(string title, string message, ToastKind kind)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        Accent.Fill = BrushFor(kind);
        Accent.Width = 312;

        _pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var fade = new DoubleAnimation(0.25, 1.0, new Duration(TimeSpan.FromMilliseconds(700)));
        Storyboard.SetTarget(fade, Accent);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        _pulse.Children.Add(fade);

        _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _dismiss.Tick += (_, _) =>
        {
            _dismiss.Stop();
            Close();
        };
        // Closing a window does not stop its timer or its animation: both hold
        // the window alive, and a tray app that runs for weeks would collect
        // one of each for every job.
        Closed += (_, _) =>
        {
            _dismiss.Stop();
            _pulse.Stop();
            _closed = true;
        };

        MouseLeftButtonUp += (_, _) => Reveal();
        MouseRightButtonUp += (_, _) => Close();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowStyleEx(
                handle,
                Native.GetWindowStyleEx(handle) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };

        if (kind == ToastKind.Running)
        {
            Loaded += (_, _) => _pulse.Begin();
        }
        else
        {
            _dismiss.Start();
        }
    }

    /// <summary>Swap a running toast to its outcome and start the countdown.</summary>
    internal void Finish(ToastKind kind, string message, IReadOnlyList<string> outputs)
    {
        if (_closed)
        {
            // The user dismissed it while the job was still running.
            return;
        }
        _pulse.Stop();
        Accent.Opacity = 1;
        Accent.Fill = BrushFor(kind);
        MessageText.Text = message;
        _outputs = outputs;
        if (outputs.Count > 0)
        {
            TitleText.Text += "  (click to show)";
        }
        _dismiss.Interval = TimeSpan.FromSeconds(kind == ToastKind.Failed ? 12 : 5);
        _dismiss.Start();
    }

    /// <summary>Open Explorer on the first output.</summary>
    private void Reveal()
    {
        try
        {
            if (_outputs.Count > 0 && (File.Exists(_outputs[0]) || Directory.Exists(_outputs[0])))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_outputs[0]}\"") { UseShellExecute = true });
            }
        }
        catch (Exception error)
        {
            Log.Write("cannot open Explorer on the output", error);
        }
        Close();
    }

    private static Brush BrushFor(ToastKind kind) => kind switch
    {
        ToastKind.Done => DoneBrush,
        ToastKind.Failed => FailedBrush,
        ToastKind.Info => InfoBrush,
        _ => RunningBrush,
    };

    private static Brush Freeze(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
