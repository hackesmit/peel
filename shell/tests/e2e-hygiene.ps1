<#
Shared helpers for the GUI test scripts. Dot-source it:  . (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')

Find-PeelApp      the Peel.exe of the newest build folder, chosen by the
                  target framework version in the folder name, never the first
                  folder alphabetically (an old net8.0-windows build left in a
                  dev copy would otherwise shadow net8.0-windows10.0.x).
Close-ShareSheets dismiss every Windows share sheet on the desktop. It must run
                  in the interactive session (a scheduled task with an
                  Interactive principal, as the e2e drivers do). A share sheet
                  left on screen sits over the test window and swallows the next
                  OLE drag, which is how every drag e2e on George failed after
                  an earlier Share run.

On Windows 11 the share sheet is a top level ApplicationFrameWindow owned by
explorer.exe whose child is a Windows.UI.Core.CoreWindow titled "Share" from
ShellExperienceHost. That pair is what is matched, so no other window is ever
touched.
#>

function Get-TfmVersion([string]$name) {
    # net8.0-windows10.0.19041.0 -> 8.0 / 10.0.19041.0; plain net8.0-windows -> 8.0 / 0.0
    if ($name -match '^net(?<net>[0-9.]+)-windows(?<win>[0-9.]*)$') {
        $win = if ($Matches.win) { $Matches.win } else { '0.0' }
        try { return [pscustomobject]@{ Net = [version]$Matches.net; Win = [version]$win } } catch { }
    }
    return [pscustomobject]@{ Net = [version]'0.0'; Win = [version]'0.0' }
}

function Find-PeelApp([string]$Root) {
    $release = Join-Path $Root 'shell\Peel\bin\Release'
    if (-not (Test-Path $release)) { return $null }
    $candidates = Get-ChildItem -Path $release -Filter 'net*-windows*' -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'Peel.exe') } |
        ForEach-Object { $v = Get-TfmVersion $_.Name; [pscustomobject]@{ Dir = $_.FullName; Net = $v.Net; Win = $v.Win } } |
        Sort-Object -Property Net, Win -Descending
    $best = $candidates | Select-Object -First 1
    if ($best) { return (Join-Path $best.Dir 'Peel.exe') }
    return $null
}

if (-not ('PeelShareSheet' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class PeelShareSheet {
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);

    private static string Text(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
    private static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    private static string Owner(IntPtr h) {
        uint pid; GetWindowThreadProcessId(h, out pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
    }
    private static bool Cloaked(IntPtr h) { int v; return DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0; }

    /// <summary>Visible, uncloaked share sheet frames on this desktop.</summary>
    public static List<IntPtr> Find() {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h) || Cloaked(h) || Class(h) != "ApplicationFrameWindow") return true;
            bool share = false;
            EnumChildWindows(h, (c, x) => {
                if (Class(c) == "Windows.UI.Core.CoreWindow" && Text(c) == "Share"
                    && string.Equals(Owner(c), "ShellExperienceHost", StringComparison.OrdinalIgnoreCase)) { share = true; return false; }
                return true;
            }, IntPtr.Zero);
            if (share) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Ask each sheet to close: WM_CLOSE, then Escape if it stays.</summary>
    public static int CloseAll() {
        var sheets = Find();
        foreach (var h in sheets) PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
        System.Threading.Thread.Sleep(800);
        foreach (var h in Find()) {
            SetForegroundWindow(h);
            System.Threading.Thread.Sleep(150);
            keybd_event(0x1B, 0, 0, UIntPtr.Zero);
            keybd_event(0x1B, 0, 2, UIntPtr.Zero);
        }
        System.Threading.Thread.Sleep(800);
        return sheets.Count;
    }
}
"@
}

# Returns a one-line description for the caller's log: how many sheets were
# found, and how many are still on screen afterwards (0 means clean).
function Close-ShareSheets {
    $found = [PeelShareSheet]::CloseAll()
    $left = ([PeelShareSheet]::Find()).Count
    return [pscustomobject]@{ Found = $found; Left = $left; Text = "share sheets found $found, left $left" }
}
