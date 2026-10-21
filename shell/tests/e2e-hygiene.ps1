<#
Shared helpers for the GUI test scripts. Dot-source it:  . (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')

Find-PeelApp      the Peel.exe of the newest build folder, chosen by the
                  target framework version in the folder name, never the first
                  folder alphabetically (an old net8.0-windows build left in a
                  dev copy would otherwise shadow net8.0-windows10.0.x).
Register-ShareSheets
                  right after Start-GuiGuard: notes the share sheets already on
                  screen. They are not this run's, so nothing here closes them.
Close-ShareSheets dismiss the share sheets this run opened (any not noted by
                  Register-ShareSheets), and only while no person has touched
                  George, checked again before each sheet is closed or brought
                  to the front. It must run in the interactive session (a scheduled
                  task with an Interactive principal, as the e2e drivers do). A
                  share sheet left on screen sits over the test window and
                  swallows the next OLE drag, which is how every drag e2e on
                  George failed after an earlier Share run.
Clear-ClipboardIfOurs <paths>
                  empties the clipboard only when it still holds exactly the
                  file list this run put there (no paths: never touched).
Clear-ClipboardTextIfOurs <text>
                  the same for text the run put there (install/smoke.ps1's
                  Copy Path). Both check and empty under one OpenClipboard, so
                  nothing copied in between is ever emptied.

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

    /// <summary>Ask this sheet to close with WM_CLOSE.</summary>
    public static void PostClose(IntPtr h) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }

    public static void Front(IntPtr h) { SetForegroundWindow(h); System.Threading.Thread.Sleep(150); }
}
"@
}

if (-not ('PeelClipboard' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class PeelClipboard {
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint f);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint f);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr h, uint i, StringBuilder s, uint n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    const uint CF_UNICODETEXT = 13, CF_HDROP = 15;

    /// <summary>Test seam: runs with the clipboard open, after the check and before the empty.</summary>
    public static Action AfterCheck;

    /// <summary>1 emptied, 0 not the run's (left alone), -1 could not open it (left alone).</summary>
    public static int ClearIfFiles(string[] paths) { return ClearIf(() => SameFiles(Files(), paths)); }
    public static int ClearIfText(string text) { return ClearIf(() => Text() == text); }

    // Opened for a window of ours (message only): an open for no window is
    // not exclusive, another open for no window gets in and can close it.
    static int ClearIf(Func<bool> ours) {
        IntPtr window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) return -1;
        try {
            bool open = false;
            for (int i = 0; i < 10 && !(open = OpenClipboard(window)); i++) Thread.Sleep(100);
            if (!open) return -1;
            try {
                if (!ours()) return 0;
                if (AfterCheck != null) AfterCheck();
                return EmptyClipboard() ? 1 : -1;
            }
            finally { CloseClipboard(); }
        }
        finally { DestroyWindow(window); }
    }

    static string[] Files() {
        if (!IsClipboardFormatAvailable(CF_HDROP)) return new string[0];
        IntPtr h = GetClipboardData(CF_HDROP);
        if (h == IntPtr.Zero) return new string[0];
        uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
        var files = new string[n];
        for (uint i = 0; i < n; i++) {
            var s = new StringBuilder((int)DragQueryFile(h, i, null, 0) + 1);
            DragQueryFile(h, i, s, (uint)s.Capacity);
            files[i] = s.ToString();
        }
        return files;
    }

    static string Text() {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        IntPtr h = GetClipboardData(CF_UNICODETEXT);
        if (h == IntPtr.Zero) return null;
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
    }

    static bool SameFiles(string[] now, string[] mine) {
        if (now.Length != mine.Length) return false;
        var a = new string[now.Length]; var b = new string[mine.Length];
        for (int i = 0; i < a.Length; i++) { a[i] = now[i].ToLowerInvariant(); b[i] = mine[i].ToLowerInvariant(); }
        Array.Sort(a, StringComparer.Ordinal); Array.Sort(b, StringComparer.Ordinal);
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Test helper: whether another thread can open the clipboard right now.</summary>
    public static bool OtherThreadCanOpen() {
        bool opened = false;
        var t = new Thread(() => { opened = OpenClipboard(IntPtr.Zero); if (opened) CloseClipboard(); });
        t.Start(); t.Join();
        return opened;
    }
}
"@
}

$script:ShareSheetsBefore = $null

function Get-ShareSheetHandles { @([PeelShareSheet]::Find() | ForEach-Object { $_.ToInt64() }) }
# One call each, so a test can stand in for the windows (test-gui-guard.ps1).
function Send-ShareSheetClose([long]$Handle) { [PeelShareSheet]::PostClose([IntPtr]$Handle) }
function Show-ShareSheet([long]$Handle) { [PeelShareSheet]::Front([IntPtr]$Handle) }
# A person has touched George since the guard's watcher started.
function Test-PersonTouched { [bool](('PeelGuard' -as [type]) -and [PeelGuard]::Touched) }

# The run's own sheets: those on screen now that were not there at the start.
function Select-OwnShareSheets([long[]]$Now, [long[]]$Before) {
    @($Now | Where-Object { $Before -notcontains $_ })
}

function Register-ShareSheets {
    $script:ShareSheetsBefore = [long[]]@(Get-ShareSheetHandles)
    $n = $script:ShareSheetsBefore.Count
    $text = if ($n -gt 0) { "share sheets on screen before the run $n, left alone (not this run's)" } else { 'no share sheet on screen before the run' }
    return [pscustomobject]@{ Found = $n; Text = $text }
}

# Returns a one-line description for the caller's log: how many of the run's
# own sheets were found, and how many of them are still on screen afterwards
# (Left 0 means clean). A sheet that stays after WM_CLOSE gets Escape through
# the GUI guard's cleanup path (gui-guard.ps1). Nothing is touched when the
# run never drove, when a person touched George (a sheet may be theirs now),
# or when the sheets on screen at the start were never noted. A person is
# looked for again before each close and each bring to front, and the first
# sign of one stops the rest.
function Close-ShareSheets {
    $untouched = { param($why) [pscustomobject]@{ Found = 0; Left = 0; Text = "share sheets not touched: $why" } }
    if ('PeelGuard' -as [type]) {
        if ([PeelGuard]::RunDir -and -not [PeelGuard]::Drove) { return (& $untouched 'this run did not drive') }
        if (Test-PersonTouched) { return (& $untouched 'a person touched George during the run') }
    }
    if ($null -eq $script:ShareSheetsBefore) { return (& $untouched 'the sheets on screen at the start were not noted (Register-ShareSheets)') }
    $stopped = ''
    $mine = Select-OwnShareSheets (Get-ShareSheetHandles) $script:ShareSheetsBefore
    foreach ($h in $mine) {
        if (Test-PersonTouched) { $stopped = '; stopped: a person touched George'; break }
        Send-ShareSheetClose $h
    }
    if ($mine.Count -gt 0) { Start-Sleep -Milliseconds 800 }
    if (-not $stopped) {
        $stayed = Select-OwnShareSheets (Get-ShareSheetHandles) $script:ShareSheetsBefore
        if ($stayed.Count -gt 0 -and ('PeelGuard' -as [type])) {
            foreach ($h in $stayed) {
                if (Test-PersonTouched) { $stopped = '; stopped: a person touched George'; break }
                Show-ShareSheet $h
                [void][PeelGuard]::CleanupTap([byte]0x1B)
            }
            Start-Sleep -Milliseconds 800
        }
    }
    $left = (Select-OwnShareSheets (Get-ShareSheetHandles) $script:ShareSheetsBefore).Count
    return [pscustomobject]@{ Found = $mine.Count; Left = $left; Text = "share sheets of this run found $($mine.Count), left $left; not this run's: $($script:ShareSheetsBefore.Count)$stopped" }
}

# The Copy action's clipboard: emptied only when it still holds exactly the
# files this run put there. $Paths empty (a run that aborted before Copy, or
# never got that far) means the clipboard is not touched at all.
function Clear-ClipboardIfOurs([string[]]$Paths) {
    if (-not $Paths -or $Paths.Count -eq 0) { return 'clipboard not touched: this run put nothing on it' }
    switch ([PeelClipboard]::ClearIfFiles($Paths)) {
        1 { return 'clipboard cleared: it still held this run''s files' }
        0 { return 'clipboard not touched: it no longer holds this run''s files' }
        default { return 'clipboard not touched: it could not be opened' }
    }
}

# The same for text this run put there. $Text empty: never touched.
function Clear-ClipboardTextIfOurs([string]$Text) {
    if (-not $Text) { return 'clipboard not touched: this run put nothing on it' }
    switch ([PeelClipboard]::ClearIfText($Text)) {
        1 { return 'clipboard cleared: it still held this run''s text' }
        0 { return 'clipboard not touched: it no longer holds this run''s text' }
        default { return 'clipboard not touched: it could not be opened' }
    }
}
