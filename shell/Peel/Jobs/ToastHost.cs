// Stacks the toasts in the bottom right corner of the work area and keeps the
// stack tidy as they come and go.
using System;
using System.Collections.Generic;
using System.Windows;

namespace Peel.Jobs;

internal sealed class ToastHost
{
    private const int MaxVisible = 4;
    private const double Gap = 8;
    private const double Margin = 16;

    private readonly List<ToastWindow> _open = new();

    internal ToastWindow Show(string title, string message, ToastKind kind)
    {
        var toast = new ToastWindow(title, message, kind);
        toast.Closed += (_, _) =>
        {
            _open.Remove(toast);
            Arrange();
        };
        _open.Add(toast);
        while (_open.Count > MaxVisible)
        {
            _open[0].Close();
        }
        toast.Show();
        Arrange();
        return toast;
    }

    internal void CloseAll()
    {
        foreach (var toast in _open.ToArray())
        {
            toast.Close();
        }
    }

    private void Arrange()
    {
        var work = SystemParameters.WorkArea;
        var bottom = work.Bottom - Margin;
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var toast = _open[i];
            toast.UpdateLayout();
            var height = toast.ActualHeight > 0 ? toast.ActualHeight : 72;
            toast.Left = work.Right - toast.Width - Margin;
            toast.Top = bottom - height;
            bottom -= height + Gap;
        }
    }
}
