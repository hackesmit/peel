// Finds and dismisses the Windows share sheet, so a sheet Peel raised never
// stays on screen after Peel is done with it. A sheet left behind outlives the
// tray app (closing the anchor window does not take it down) and sits over
// whatever is under it, swallowing the next drag there (review round 1: every
// drag e2e on George failed behind one for over an hour).
//
// On Windows 11 the sheet is a top level ApplicationFrameWindow (owned by
// explorer.exe) whose child is a Windows.UI.Core.CoreWindow titled "Share" from
// ShellExperienceHost. Only that pair is matched. A sheet does not say which
// app raised it, so Peel never closes "every sheet": ShareOwner below claims
// only the one sheet that appeared after Peel raised its own, forgets it the
// moment it leaves the screen, and closes only that window, with messages
// posted to it (never a keystroke sent to whatever has the foreground).
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
    /// Only messages posted to that window are used.</summary>
    internal static bool Close(IntPtr sheet)
    {
        if (!IsShown(sheet))
        {
            return true;
        }
        PostMessage(sheet, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(500);
        if (IsShown(sheet))
        {
            PostMessage(sheet, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
            Thread.Sleep(300);
        }
        return !IsShown(sheet);
    }

    /// <summary>
    /// Close every share sheet on screen and return how many are left. Only
    /// the scripted probe uses it, to leave the desktop clean; Peel itself
    /// closes only the sheet it owns (ShareOwner).
    /// </summary>
    internal static int CloseAll()
    {
        var sheets = Find();
        var left = sheets.Count(h => !Close(h));
        Log.Write($"share: dismissed {sheets.Count - left} of {sheets.Count} share sheet(s)");
        return left;
    }
}

/// <summary>
/// Which share sheet is Peel's. Before Peel raises its sheet it notes the sheets
/// already on screen; the one sheet that appears afterwards is claimed (none
/// when two appear, since then it cannot tell). The claim ends as soon as that
/// sheet leaves the screen, so a sheet another app opens later, even in the
/// same reused window, is never Peel's. Dismiss closes only the claimed sheet.
/// </summary>
internal sealed class ShareOwner
{
    private readonly Func<IReadOnlyCollection<IntPtr>> _visible;
    private readonly Func<IntPtr, bool> _shown;
    private readonly Func<IntPtr, bool> _close;
    private HashSet<IntPtr> _before = new();

    internal ShareOwner(Func<IReadOnlyCollection<IntPtr>> visible, Func<IntPtr, bool> shown, Func<IntPtr, bool> close)
    {
        _visible = visible;
        _shown = shown;
        _close = close;
    }

    /// <summary>The claimed sheet, or IntPtr.Zero.</summary>
    internal IntPtr Owned { get; private set; }

    /// <summary>True while Peel has raised a sheet it has not found yet.</summary>
    internal bool Waiting { get; private set; }

    /// <summary>Note the sheets on screen just before Peel raises its own.
    /// Peel's earlier sheet, if still up, is not counted as someone else's,
    /// since the system may reuse its window for the new request.</summary>
    internal void BeforeRaise()
    {
        _before = _visible().Where(h => h != Owned).ToHashSet();
        Owned = IntPtr.Zero;
        Waiting = true;
    }

    /// <summary>Claim the new sheet, or drop a claimed sheet that has left the
    /// screen. Returns true while there is still something to watch.</summary>
    internal bool Poll()
    {
        if (Owned != IntPtr.Zero)
        {
            if (!_shown(Owned))
            {
                Owned = IntPtr.Zero;
            }
        }
        else if (Waiting)
        {
            var fresh = _visible().Where(h => !_before.Contains(h)).ToList();
            if (fresh.Count == 1)
            {
                Owned = fresh[0];
                Waiting = false;
            }
            else if (fresh.Count > 1)
            {
                Waiting = false;
            }
        }
        return Owned != IntPtr.Zero || Waiting;
    }

    /// <summary>Stop looking for a sheet that never appeared.</summary>
    internal void StopWaiting() => Waiting = false;

    /// <summary>Close Peel's sheet if it is still up. Returns false only when
    /// Peel's sheet stayed on screen.</summary>
    internal bool Dismiss()
    {
        Waiting = false;
        var sheet = Owned;
        Owned = IntPtr.Zero;
        return sheet == IntPtr.Zero || !_shown(sheet) || _close(sheet);
    }
}
