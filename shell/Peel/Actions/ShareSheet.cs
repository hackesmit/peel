// Finds and dismisses the Windows share sheet, for the scripted probe only
// (shell/tests/ActionProbe), which leaves George's desktop clean after it
// raises a sheet: a sheet left behind sits over whatever is under it and
// swallows the next drag there (review round 1). Peel itself never closes a
// sheet; it is left to the user (review round 4).
//
// On Windows 11 the sheet is a top level ApplicationFrameWindow (owned by
// explorer.exe) whose child is a Windows.UI.Core.CoreWindow titled "Share" from
// ShellExperienceHost. Only that pair is matched, and only messages posted to
// that window are used (never a keystroke sent to whatever has the foreground).
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

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
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

    /// <summary>Ask one sheet to close and report whether it left the screen.
    /// <paramref name="mayClose"/> is checked again right before each message,
    /// so a sheet that stopped qualifying is left alone. Only messages posted
    /// to that window are used.</summary>
    private static bool Close(IntPtr sheet, Func<IntPtr, bool> mayClose)
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
    /// never closes a sheet.
    /// </summary>
    internal static int CloseAll()
    {
        var sheets = Find();
        var left = sheets.Count(h => !Close(h, _ => true));
        Log.Write($"share: dismissed {sheets.Count - left} of {sheets.Count} share sheet(s)");
        return left;
    }
}
