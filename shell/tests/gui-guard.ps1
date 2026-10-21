<#
Shared guard for every GUI driver that moves the real mouse or presses real
keys on George. Dot-source it:  . (Join-Path $PSScriptRoot 'gui-guard.ps1')

Why: on 2026-10-20 an aborted SendInput drag left an Explorer drag image (a
SysDragImage window, a file icon ghost) stuck on Daniel's screen. The cause was
his own mouse breaking the injected drag. So a driver must not start while
someone uses George, must stop when someone touches the mouse or keyboard, and
must always end its drag, even when a check throws or the run is killed.

In a driver (interactive session):
  Start-GuiGuard -Dir $Dir -IdleSeconds 300 -IdleTimeout 1800 -Log { param($m) L $m }
                    waits for user idle (GetLastInputInfo), logs the wait, then
                    starts the real input watcher (a low level mouse and
                    keyboard hook on its own thread: an event without
                    LLMHF_INJECTED / LLKHF_INJECTED is a person) and a watchdog
                    process that tears down if this driver dies before its
                    finally (Stop-ScheduledTask, a crash). Writes driving.txt
                    in Dir when it starts to drive.
  Assert-NoUserInput  throws "PEEL_ABORT user input: ..." once a person has
                    touched George. Every driver calls it from MoveTo and
                    WaitLog, so a drive stops within about 100 ms.
  Get-GuiAbort $_   in the driver's catch: the abort reason ('user input',
                    'user busy') for a guard abort, or $null for a real failure.
  Stop-GuiGuard     first thing in the driver's finally: Invoke-GuiTeardown,
                    stop the watcher, stop the watchdog. Returns the teardown.

Anywhere in the interactive session:
  Invoke-GuiTeardown  end any drag and leave nothing held: if a mouse button is
                    down, tap Escape (cancels an OLE drag), put a small window
                    of our own that accepts no drop under the cursor, then
                    release the button over it, so even a drag that ignored
                    Escape drops on nothing. Then release every modifier that
                    is down, and hide and close every visible SysDragImage
                    window. Returns what it did and what is left.

From session 0 (a runner over ssh), after a driver timed out or died:
  Invoke-InteractiveTeardown -Report <json> [-Shot <png>]
                    runs gui-teardown.ps1 as a one shot Interactive task.

Inside a driver the guard releases only what the driver itself pressed (the
watcher sees every injected down and up) once a person has touched George, so
a teardown after "user input" never cancels the person's own drag.
#>

if (-not ('PeelGuard' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class PeelGuard {
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint m; public IntPtr w; public IntPtr l; public uint t; public int x; public int y; public uint p; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc f, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static uint IdleMs() {
        var info = new LASTINPUTINFO(); info.cbSize = 8;
        GetLastInputInfo(ref info);
        return (uint)Environment.TickCount - info.dwTime;
    }

    public static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    // Mouse buttons as virtual keys, and the mouse_event flag that lets each go.
    public static readonly int[] Buttons = { 0x01, 0x02, 0x04 };
    static uint UpFlag(int vk) { return vk == 0x01 ? 0x0004u : vk == 0x02 ? 0x0010u : 0x0040u; }
    // Left and right Shift, Ctrl, Alt and Windows.
    public static readonly int[] Modifiers = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
    static bool Extended(int vk) { return vk == 0xA3 || vk == 0xA5 || vk == 0x5B || vk == 0x5C; }

    public static void ButtonUp(int vk) { mouse_event(UpFlag(vk), 0, 0, 0, UIntPtr.Zero); }
    public static void KeyUp(int vk) { keybd_event((byte)vk, 0, (Extended(vk) ? 1u : 0u) | 2u, UIntPtr.Zero); }
    public static void TapEscape() {
        keybd_event(0x1B, 0, 0, UIntPtr.Zero); Thread.Sleep(60); keybd_event(0x1B, 0, 2, UIntPtr.Zero);
    }
    public static void Nudge() {
        mouse_event(0x0001, 1, 1, 0, UIntPtr.Zero); Thread.Sleep(40);
        mouse_event(0x0001, -1, -1, 0, UIntPtr.Zero); Thread.Sleep(40);
    }
    // Show without taking the focus, on top of everything.
    public static void ShowShield(IntPtr h, int x, int y, int size) {
        SetWindowPos(h, new IntPtr(-1), x, y, size, size, 0x0010 | 0x0040);
        ShowWindow(h, 4);
    }

    static string Class(IntPtr h) { var s = new StringBuilder(64); GetClassName(h, s, 64); return s.ToString(); }

    /// <summary>Visible top level SysDragImage windows: the drag image helper's ghost.</summary>
    public static List<IntPtr> DragImages() {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => { if (IsWindowVisible(h) && Class(h) == "SysDragImage") found.Add(h); return true; }, IntPtr.Zero);
        return found;
    }

    public static int HideDragImages() {
        var images = DragImages();
        foreach (var h in images) { ShowWindow(h, 0); PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
        return images.Count;
    }

    // ---- the real input watcher -------------------------------------------
    static Thread watcher;
    static uint watcherId;
    static IntPtr mouseHook, keyHook;
    static HookProc mouseProc = MouseProc, keyProc = KeyProc;
    static int real;
    static string firstReal = "";
    static readonly object gate = new object();
    static readonly HashSet<int> held = new HashSet<int>();

    public static int RealInput { get { return real; } }
    public static string FirstReal { get { lock (gate) return firstReal; } }
    public static bool Watching { get { return watcher != null; } }
    public static int[] Held() { lock (gate) { var a = new int[held.Count]; held.CopyTo(a); return a; } }

    static void Real(string what) {
        lock (gate) { if (real == 0) firstReal = what + " at " + DateTime.Now.ToString("HH:mm:ss.fff"); }
        Interlocked.Increment(ref real);
    }
    static void Track(int vk, bool down) { lock (gate) { if (down) held.Add(vk); else held.Remove(vk); } }

    static IntPtr MouseProc(int code, IntPtr w, IntPtr l) {
        if (code >= 0) {
            int flags = Marshal.ReadInt32(l, 12), m = (int)w;
            if ((flags & 1) == 0) Real("mouse message 0x" + m.ToString("x"));
            else if (m == 0x201 || m == 0x202) Track(0x01, m == 0x201);
            else if (m == 0x204 || m == 0x205) Track(0x02, m == 0x204);
            else if (m == 0x207 || m == 0x208) Track(0x04, m == 0x207);
        }
        return CallNextHookEx(mouseHook, code, w, l);
    }

    static IntPtr KeyProc(int code, IntPtr w, IntPtr l) {
        if (code >= 0) {
            int vk = Marshal.ReadInt32(l, 0), flags = Marshal.ReadInt32(l, 8), m = (int)w;
            if ((flags & 0x10) == 0) Real("key 0x" + vk.ToString("x"));
            else Track(vk, m == 0x100 || m == 0x104);
        }
        return CallNextHookEx(keyHook, code, w, l);
    }

    public static bool StartWatch() {
        if (watcher != null) return true;
        real = 0; lock (gate) { firstReal = ""; held.Clear(); }
        var ready = new ManualResetEvent(false);
        bool ok = false;
        var t = new Thread(() => {
            watcherId = GetCurrentThreadId();
            var mod = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(14, mouseProc, mod, 0);
            keyHook = SetWindowsHookEx(13, keyProc, mod, 0);
            ok = mouseHook != IntPtr.Zero && keyHook != IntPtr.Zero;
            ready.Set();
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
        });
        t.IsBackground = true;
        t.Start();
        ready.WaitOne(5000);
        watcher = t;
        return ok;
    }

    public static void StopWatch() {
        if (watcher == null) return;
        PostThreadMessage(watcherId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        watcher.Join(2000);
        watcher = null;
    }
}
"@
}
[void][PeelGuard]::SetProcessDPIAware()

$script:GuiGuardHere = $PSScriptRoot
$script:GuiGuardLog = { param($m) }
$script:GuiGuardWatchdog = $null

function Write-GuiGuardLog($message) { & $script:GuiGuardLog $message }

# Waits until nobody has touched George for $Seconds (0 skips the wait, for a
# driver nested in another that already waited), or throws a 'user busy' abort
# after $Timeout seconds.
function Wait-UserIdle([int]$Seconds, [int]$Timeout) {
    if ($Seconds -le 0) { Write-GuiGuardLog 'idle wait skipped (-IdleSeconds 0)'; return }
    $started = Get-Date
    $deadline = $started.AddSeconds($Timeout)
    Write-GuiGuardLog ("waiting for $Seconds s of user idle (idle now {0:0} s)" -f ([PeelGuard]::IdleMs() / 1000))
    while ([PeelGuard]::IdleMs() -lt ($Seconds * 1000)) {
        if ((Get-Date) -gt $deadline) {
            throw "PEEL_ABORT user busy: no $Seconds s of user idle within $Timeout s, so nothing was driven"
        }
        Start-Sleep -Seconds 5
    }
    Write-GuiGuardLog ("George idle for {0:0} s after waiting {1:0} s" -f ([PeelGuard]::IdleMs() / 1000), ((Get-Date) - $started).TotalSeconds)
}

function Start-GuiGuard {
    param(
        [Parameter(Mandatory)] [string]$Dir,
        [int]$IdleSeconds = 300,
        [int]$IdleTimeout = 1800,
        [scriptblock]$Log = { param($m) }
    )
    $script:GuiGuardLog = $Log
    New-Item -ItemType Directory -Force $Dir | Out-Null
    $driving = Join-Path $Dir 'driving.txt'
    Remove-Item $driving -ErrorAction SilentlyContinue
    # The runner kills this pid if it loses track of the task (gui-guard.ps1, Invoke-GuiTask).
    Set-Content (Join-Path $Dir 'driver.pid') $PID
    # A teardown that a dead driver never got to run: the watchdog does it.
    $report = Join-Path $Dir 'teardown-watchdog.json'
    Remove-Item $report -ErrorAction SilentlyContinue
    $teardown = Join-Path $script:GuiGuardHere 'gui-teardown.ps1'
    $script:GuiGuardWatchdog = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$teardown`"", '-WaitPid', $PID, '-Report', "`"$report`"")
    Write-GuiGuardLog "watchdog pid $($script:GuiGuardWatchdog.Id) waits on driver pid $PID"
    Wait-UserIdle $IdleSeconds $IdleTimeout
    if (-not [PeelGuard]::StartWatch()) { throw 'the real input watcher could not install its hooks' }
    Write-GuiGuardLog 'real input watcher on: any input without the injected flag aborts the drive'
    Set-Content $driving (Get-Date).ToString('o')
}

function Assert-NoUserInput {
    if ([PeelGuard]::Watching -and [PeelGuard]::RealInput -gt 0) {
        throw "PEEL_ABORT user input: $([PeelGuard]::FirstReal)"
    }
}

function Get-GuiAbort($errorRecord) {
    if ("$errorRecord" -match '^PEEL_ABORT (user input|user busy)') { return $Matches[1] }
    return $null
}

function Invoke-GuiTeardown {
    $done = [System.Collections.ArrayList]::new()
    $seenPerson = [PeelGuard]::Watching -and [PeelGuard]::RealInput -gt 0
    if ([PeelGuard]::Watching) {
        # What the driver pressed and never let go, from the watcher.
        $held = @([PeelGuard]::Held())
        # Before anyone touched George every held key is ours, whoever the
        # hook saw press it (a key pressed before the watcher started).
        if (-not $seenPerson) {
            $held += @([PeelGuard]::Buttons + [PeelGuard]::Modifiers | Where-Object { [PeelGuard]::IsDown($_) })
        }
    }
    else {
        $held = @([PeelGuard]::Buttons + [PeelGuard]::Modifiers | Where-Object { [PeelGuard]::IsDown($_) })
    }
    $held = @($held | Sort-Object -Unique)
    $buttons = @($held | Where-Object { [PeelGuard]::Buttons -contains $_ })
    $keys = @($held | Where-Object { [PeelGuard]::Buttons -notcontains $_ })
    $imagesFound = ([PeelGuard]::DragImages()).Count

    if ($buttons.Count -gt 0) {
        # Escape first: the drag loop of the window that started the drag
        # takes it as a cancel. Then the shield, so that a drag that missed
        # the Escape drops on a window with no drop target: no file moves.
        [PeelGuard]::TapEscape(); $null = $done.Add('escape')
        Start-Sleep -Milliseconds 250
        $shield = $null
        try {
            Add-Type -AssemblyName System.Windows.Forms
            $at = [System.Windows.Forms.Cursor]::Position
            $shield = New-Object System.Windows.Forms.Form
            $shield.FormBorderStyle = 'None'
            $shield.ShowInTaskbar = $false
            $shield.Opacity = 0.02
            $shield.AllowDrop = $false
            [PeelGuard]::ShowShield($shield.Handle, $at.X - 40, $at.Y - 40, 80)
            [System.Windows.Forms.Application]::DoEvents()
            [PeelGuard]::Nudge()
            [System.Windows.Forms.Application]::DoEvents()
            $null = $done.Add('shield')
        }
        catch { $null = $done.Add("no shield ($($_.Exception.Message))") }
        foreach ($vk in $buttons) { [PeelGuard]::ButtonUp($vk) }
        $null = $done.Add('released buttons ' + (($buttons | ForEach-Object { '0x{0:x2}' -f $_ }) -join ','))
        Start-Sleep -Milliseconds 300
        if ($shield) {
            [System.Windows.Forms.Application]::DoEvents()
            $shield.Close(); $shield.Dispose()
        }
    }
    if ($keys.Count -gt 0) {
        foreach ($vk in $keys) { [PeelGuard]::KeyUp($vk) }
        $null = $done.Add('released keys ' + (($keys | ForEach-Object { '0x{0:x2}' -f $_ }) -join ','))
    }
    Start-Sleep -Milliseconds 200
    $hidden = [PeelGuard]::HideDragImages()
    if ($hidden -gt 0) { $null = $done.Add("hid $hidden drag image(s)"); Start-Sleep -Milliseconds 300 }

    $imagesLeft = ([PeelGuard]::DragImages()).Count
    $stillDown = @([PeelGuard]::Buttons + [PeelGuard]::Modifiers | Where-Object { [PeelGuard]::IsDown($_) } |
        ForEach-Object { '0x{0:x2}' -f $_ })
    $actions = if ($done.Count -gt 0) { $done -join ', ' } else { 'nothing held' }
    return [pscustomobject]@{
        Actions          = $actions
        DragImagesFound  = $imagesFound
        DragImagesLeft   = $imagesLeft
        StillDown        = ($stillDown -join ',')
        PersonSeen       = [bool]$seenPerson
        Clean            = ($imagesLeft -eq 0 -and ($seenPerson -or $stillDown.Count -eq 0))
        Text             = "teardown: $actions; drag images found $imagesFound, left $imagesLeft; still down: $(if ($stillDown) { $stillDown -join ',' } else { 'none' })"
    }
}

function Stop-GuiGuard {
    $result = Invoke-GuiTeardown
    [PeelGuard]::StopWatch()
    if ($script:GuiGuardWatchdog) {
        Stop-Process -Id $script:GuiGuardWatchdog.Id -Force -ErrorAction SilentlyContinue
        $script:GuiGuardWatchdog = $null
    }
    return $result
}

# Session 0 side: run gui-teardown.ps1 in the logged on user's desktop.
function Invoke-InteractiveTeardown {
    param([Parameter(Mandatory)] [string]$Report, [string]$Shot = '')
    $teardown = Join-Path $script:GuiGuardHere 'gui-teardown.ps1'
    Remove-Item $Report -ErrorAction SilentlyContinue
    $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Report "{1}"' -f $teardown, $Report
    if ($Shot) { $arguments += ' -Shot "{0}"' -f $Shot }
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    $task = 'PeelGuiTeardown-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    Register-ScheduledTask -TaskName $task -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments) `
        -Principal $principal -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        $deadline = (Get-Date).AddSeconds(60)
        do { Start-Sleep -Seconds 1 } while (-not (Test-Path $Report) -and (Get-Date) -lt $deadline)
        Start-Sleep -Milliseconds 300
    }
    finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path $Report)) { return $null }
    return (Get-Content $Report -Raw | ConvertFrom-Json)
}

# Runner side (session 0): run a driver as a scheduled task in the desktop
# session and make sure its drag ends whatever happens. The task name is unique
# per run, so two runners never stop or delete each other's task. The drive
# budget (-TimeoutSeconds) starts when the driver writes driving.txt, after its
# idle wait (bounded by -IdleTimeout). A task that times out, or vanishes, has
# its driver killed by pid; then Complete-GuiRun tears down. Returns
# @{ TimedOut; Died; Teardown }.
function Invoke-GuiTask {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Arguments,
        [Parameter(Mandatory)] [string]$Dir,
        [Parameter(Mandatory)] [string]$ResultPath,
        [int]$TimeoutSeconds = 900,
        [int]$IdleTimeout = 1800,
        [ValidateSet('Limited', 'Highest')] [string]$RunLevel = 'Limited'
    )
    $drivingPath = Join-Path $Dir 'driving.txt'
    $pidPath = Join-Path $Dir 'driver.pid'
    Remove-Item $drivingPath, $pidPath, (Join-Path $Dir 'teardown-*.json') -ErrorAction SilentlyContinue
    $task = '{0}-{1}' -f $Name, ([guid]::NewGuid().ToString('N').Substring(0, 8))
    # $env:USERDOMAIN is WORKGROUP on George, which is not an account domain.
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel $RunLevel
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds ($IdleTimeout + $TimeoutSeconds + 300))
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $Arguments
    $timedOut = $false
    $vanished = $false
    $teardown = $null
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        $deadline = (Get-Date).AddSeconds($IdleTimeout + 120)
        $driving = $false
        do {
            Start-Sleep -Seconds 3
            if (-not $driving -and (Test-Path $drivingPath)) {
                $driving = $true
                $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            }
            $found = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
            $state = if ($found) { [string]$found.State } else { 'Missing' }
        } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        if ($state -eq 'Running') {
            Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
            $timedOut = $true
            Write-Host "TIMEOUT after $TimeoutSeconds seconds of driving"
        }
        elseif ($state -eq 'Missing') {
            $vanished = $true
            Write-Host "the scheduled task $task disappeared while it ran"
        }
    }
    finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
        $died = $timedOut -or $vanished -or -not (Test-Path $ResultPath)
        if ($died -and (Test-Path $pidPath)) {
            $driver = [int](Get-Content $pidPath -Raw)
            if (Get-Process -Id $driver -ErrorAction SilentlyContinue) {
                Stop-Process -Id $driver -Force -ErrorAction SilentlyContinue
                Write-Host "killed the driver, pid $driver"
            }
        }
        # Give a killed driver's watchdog time to report before the runner acts.
        if ($died -and (Test-Path $pidPath)) {
            $wait = (Get-Date).AddSeconds(15)
            while (-not (Test-Path (Join-Path $Dir 'teardown-watchdog.json')) -and (Get-Date) -lt $wait) { Start-Sleep -Milliseconds 500 }
        }
        $teardown = Complete-GuiRun -Dir $Dir -DriverDied $died
    }
    return [pscustomobject]@{ TimedOut = $timedOut; Died = $died; Teardown = $teardown }
}

# Runner side, after the driver's task ended: print the watchdog's report if
# the watchdog had to act, and when the driver timed out or left no result,
# tear down from here too, with a screenshot. Returns the runner's report.
function Complete-GuiRun {
    param([Parameter(Mandatory)] [string]$Dir, [bool]$DriverDied)
    $watchdog = Join-Path $Dir 'teardown-watchdog.json'
    if (Test-Path $watchdog) {
        $w = Get-Content $watchdog -Raw | ConvertFrom-Json
        Write-Host "TEARDOWN (watchdog): $($w.text)"
    }
    if (-not $DriverDied) { return $null }
    $r = Invoke-InteractiveTeardown -Report (Join-Path $Dir 'teardown-runner.json') -Shot (Join-Path $Dir 'shot-after-teardown.png')
    if ($r) { Write-Host "TEARDOWN (runner): $($r.text); screenshot $($r.shot)" }
    else { Write-Host 'TEARDOWN (runner): the interactive teardown did not report (is Daniel signed in?)' }
    return $r
}
