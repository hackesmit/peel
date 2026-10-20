// Finds and dismisses the Windows share sheet, so a sheet Peel raised never
// stays on screen after Peel is done with it. A sheet left behind outlives the
// tray app (closing the anchor window does not take it down) and sits over
// whatever is under it, swallowing the next drag there (review round 1: every
// drag e2e on George failed behind one for over an hour).
//
// On Windows 11 the sheet is a top level ApplicationFrameWindow (owned by
// explorer.exe) whose child is a Windows.UI.Core.CoreWindow titled "Share" from
// ShellExperienceHost. Only that pair is matched. A sheet does not say which
// app raised it, so Peel never closes "every sheet": ShareOwner below closes
// only a sheet owned by Peel's own live anchor window, and leaves any other
// sheet open, with messages posted to that window (never a keystroke sent to
// whatever has the foreground).
// shell/tests/e2e-hygiene.ps1 uses the same match for the GUI tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Peel.Actions;

internal static class ShareSheet
{
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;
    private const int DWMWA_CLOAKED = 14;
    private const uint GW_OWNER = 4;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);

    private static string Text(IntPtr h)
    {
        var s = new StringBuilder(256);
        GetWindowText(h, s, s.Capacity);
        return s.ToString();
    }

    private static string Class(IntPtr h)
    {
        var s = new StringBuilder(256);
        GetClassName(h, s, s.Capacity);
        return s.ToString();
    }

    private static string Owner(IntPtr h)
    {
        GetWindowThreadProcessId(h, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool Cloaked(IntPtr h) =>
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var value, sizeof(int)) == 0 && value != 0;

    private static bool IsShareFrame(IntPtr h)
    {
        if (!IsWindowVisible(h) || Cloaked(h) || Class(h) != "ApplicationFrameWindow")
        {
            return false;
        }
        var share = false;
        EnumChildWindows(h, (child, _) =>
        {
            if (Class(child) == "Windows.UI.Core.CoreWindow" && Text(child) == "Share"
                && string.Equals(Owner(child), "ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
            {
                share = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return share;
    }

    /// <summary>The share sheet frames visible on this desktop now.</summary>
    internal static List<IntPtr> Find()
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsShareFrame(h))
            {
                found.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>True while <paramref name="sheet"/> is still a share sheet on
    /// screen (not destroyed, hidden or cloaked).</summary>
    internal static bool IsShown(IntPtr sheet) => IsWindow(sheet) && IsShareFrame(sheet);

    /// <summary>True when <paramref name="sheet"/> is owned, directly or up
    /// its owner chain, by the live window <paramref name="anchor"/>.</summary>
    internal static bool OwnedBy(IntPtr sheet, IntPtr anchor)
    {
        if (anchor == IntPtr.Zero || !IsWindow(anchor))
        {
            return false;
        }
        var h = sheet;
        for (var depth = 0; depth < 8; depth++)
        {
            h = GetWindow(h, GW_OWNER);
            if (h == IntPtr.Zero)
            {
                return false;
            }
            if (h == anchor)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Ask one sheet to close and report whether it left the screen.
    /// <paramref name="mayClose"/> is checked again right before each message,
    /// so a sheet that stopped qualifying is left alone. Only messages posted
    /// to that window are used.</summary>
    internal static bool Close(IntPtr sheet, Func<IntPtr, bool> mayClose)
    {
        if (!IsShown(sheet) || !mayClose(sheet))
        {
            return !IsShown(sheet);
        }
        PostMessage(sheet, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(500);
        if (IsShown(sheet) && mayClose(sheet))
        {
            PostMessage(sheet, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
            Thread.Sleep(300);
        }
        return !IsShown(sheet);
    }

    /// <summary>
    /// Close every share sheet on screen and return how many are left. Only
    /// the scripted probe uses it, to leave the desktop clean; Peel itself
    /// closes only a sheet proven its own (ShareOwner).
    /// </summary>
    internal static int CloseAll()
    {
        var sheets = Find();
        var left = sheets.Count(h => !Close(h, _ => true));
        Log.Write($"share: dismissed {sheets.Count - left} of {sheets.Count} share sheet(s)");
        return left;
    }
}

/// <summary>
/// Which share sheets Peel may close. A sheet does not say which app raised it,
/// and neither visibility nor a window handle that stays the same proves
/// anything (a dismissed sheet's window can come back as another app's, lucy
/// round 3). So the one proof accepted is a window relation Windows keeps: the
/// sheet is owned by Peel's own anchor window, which is still alive (a live
/// window's handle is never reused). It is checked when the sheet is closed,
/// not remembered from an earlier look. A sheet without that proof is left
/// open, whoever raised it (fail closed).
/// </summary>
internal sealed class ShareOwner
{
    private readonly Func<IReadOnlyCollection<IntPtr>> _visible;
    private readonly Func<IntPtr, IntPtr, bool> _ownedBy;
    private readonly Func<IntPtr, Func<IntPtr, bool>, bool> _close;

    /// <param name="visible">The sheets on screen now.</param>
    /// <param name="ownedBy">True when the sheet is owned by the anchor now.</param>
    /// <param name="close">Close a sheet while the check still holds; true
    /// when it left the screen.</param>
    internal ShareOwner(
        Func<IReadOnlyCollection<IntPtr>> visible, Func<IntPtr, IntPtr, bool> ownedBy,
        Func<IntPtr, Func<IntPtr, bool>, bool> close)
    {
        _visible = visible;
        _ownedBy = ownedBy;
        _close = close;
    }

    /// <summary>Close every sheet on screen that is proven to belong to
    /// <paramref name="anchor"/>. Returns how many of those stayed up.</summary>
    internal int Dismiss(IntPtr anchor)
    {
        if (anchor == IntPtr.Zero)
        {
            return 0;
        }
        bool Ours(IntPtr sheet) => _ownedBy(sheet, anchor);
        return _visible().Count(sheet => Ours(sheet) && !_close(sheet, Ours));
    }
}
