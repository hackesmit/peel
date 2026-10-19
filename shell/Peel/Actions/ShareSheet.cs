// Finds and dismisses the Windows share sheet, so a sheet Peel raised never
// stays on screen after Peel is done with it. A sheet left behind outlives the
// tray app (closing the anchor window does not take it down) and sits over
// whatever is under it, swallowing the next drag there (review round 1: every
// drag e2e on George failed behind one for over an hour).
//
// On Windows 11 the sheet is a top level ApplicationFrameWindow (owned by
// explorer.exe) whose child is a Windows.UI.Core.CoreWindow titled "Share" from
// ShellExperienceHost. Only that pair is matched, so no other window is ever
// touched. shell/tests/e2e-hygiene.ps1 uses the same match for the GUI tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Peel.Actions;

internal static class ShareSheet
{
    private const uint WM_CLOSE = 0x0010;
    private const int DWMWA_CLOAKED = 14;
    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x2;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
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

    /// <summary>The share sheet frames visible on this desktop now.</summary>
    internal static List<IntPtr> Find()
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || Cloaked(h) || Class(h) != "ApplicationFrameWindow")
            {
                return true;
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
            if (share)
            {
                found.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Ask every share sheet to close (WM_CLOSE, then Escape for one that
    /// stays) and return how many are still on screen afterwards.
    /// </summary>
    internal static int CloseAll()
    {
        var sheets = Find();
        if (sheets.Count == 0)
        {
            return 0;
        }
        foreach (var h in sheets)
        {
            PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        Thread.Sleep(500);
        foreach (var h in Find())
        {
            SetForegroundWindow(h);
            Thread.Sleep(100);
            keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        Thread.Sleep(300);
        var left = Find().Count;
        Log.Write($"share: dismissed {sheets.Count - left} of {sheets.Count} share sheet(s)");
        return left;
    }
}
