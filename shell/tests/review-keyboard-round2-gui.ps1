param(
    [string]$Dir = 'C:\peel-dev\review2-5f8a-gui',
    [string]$App = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Peel\bin\Release\net8.0-windows\Peel.exe'),
    # gui-guard.ps1: user idle to wait for before driving, and how long to wait.
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 1800,
    # The run folder a runner made for this run (gui-guard.ps1, Invoke-GuiTask).
    [string]$GuardDir = ''
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Dir | Out-Null
$drive = Join-Path $Dir 'drive.log'
Remove-Item $drive -ErrorAction SilentlyContinue
function L($m) { Add-Content $drive ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $m) }
function Check($name, $ok, $detail) { L ("CHECK {0} {1}: {2}" -f $name, $(if ($ok) { 'PASS' } else { 'FAIL' }), $detail) }

Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class In {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public static string Fg() { var h = GetForegroundWindow(); var t = new StringBuilder(256); var c = new StringBuilder(256);
    GetWindowText(h, t, 256); GetClassName(h, c, 256); return h.ToInt64() + " [" + c + "] " + t; }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  public static long RootAt(int x, int y) { var p = new POINT(); p.X = x; p.Y = y; return GetAncestor(WindowFromPoint(p), 2).ToInt64(); }
}
public static class Hold {
  [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint m, uint vk);
}
'@
[void][In]::SetProcessDPIAware()
# Idle wait, real input abort, and the teardown that ends any drag.
. (Join-Path $PSScriptRoot 'gui-guard.ps1')
$peelLog = $null
function PeelLog() { if ($peelLog -and (Test-Path $peelLog)) { return (Get-Content $peelLog -Raw -ErrorAction SilentlyContinue) } ; '' }
function CountLog($p) { [regex]::Matches((PeelLog), $p).Count }
function WaitLog($p, $ms, $atLeast = 1) {
    $d = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $d) { $f = [regex]::Matches((PeelLog), $p); if ($f.Count -ge $atLeast) { return $f[$f.Count-1] }; Wait-Gui 100 }
    $null
}
function Highlight() { $m = [regex]::Matches((PeelLog), 'keyboard highlight (.+)'); if ($m.Count) { $m[$m.Count-1].Groups[1].Value.Trim() } }
function AimAt($t) { for ($i=0; $i -lt 30; $i++) { if ((Highlight) -eq $t) { return $true }; $s = CountLog 'keyboard highlight '; [PeelGuard]::TapEx(0x27,$true); WaitLog 'keyboard highlight ' 3000 ($s+1) | Out-Null }; $false }
function ShellWins() { @((New-Object -ComObject Shell.Application).Windows()) }
function DumpWins($tag) { foreach ($w in ShellWins) { try { L ("  $tag win hwnd={0} url={1} sel={2}" -f $w.HWND, $w.LocationURL, (@($w.Document.SelectedItems()) | ForEach-Object { $_.Name }) -join ';') } catch { } } }
# The scratch folders live under this run's own folder (gui-guard.ps1), so a
# run never deletes another run's folders, and gui-teardown.ps1 closes the
# Explorer windows on them if this driver is killed. No run folder yet: no window is ours.
$script:Work = $null
function CloseMine() {
    if (-not $script:Work) { return }
    $prefix = ([uri]($script:Work.TrimEnd('\') + '\')).AbsoluteUri
    foreach ($w in ShellWins) { try { if ("$($w.LocationURL)/" -like "$prefix*") { $w.Quit() } } catch { } }
}
function FindItem($name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
        (New-Object System.Windows.Automation.OrCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $stem)))))
    for ($i=0; $i -lt 25; $i++) { $it = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c); if ($it) { return $it }; Wait-Gui 300 }
    $null
}
function SafeClick($x, $y) { $root = [In]::RootAt($x, $y); $fg = [In]::GetForegroundWindow().ToInt64(); if ($root -ne $fg -or -not ([In]::Fg() -match 'CabinetWClass')) { throw "refusing click at $x,$y : root=$root fg=$([In]::Fg())" }; [PeelGuard]::Click($x, $y) }
function FrontDir($d) { $u = ([uri]$d).AbsoluteUri; $w = ShellWins | Where-Object { $_.LocationURL -eq $u } | Select-Object -First 1; if (-not $w) { throw "no explorer for $d" }; $ok = [PeelGuard]::Front([long]$w.HWND); L ("front $d -> $ok fg=" + [In]::Fg()); if (-not $ok) { throw "cannot bring $d to front" } }
function ClickItem($name) { $it = FindItem $name; if (-not $it) { throw "$name not visible" }; $r = $it.Current.BoundingRectangle; SafeClick ([int]($r.X+24)) ([int]($r.Y+$r.Height/2)); Wait-Gui 400 }
function StartPeel($log) {
    $script:peelLog = $log; Remove-Item $log -ErrorAction SilentlyContinue
    $env:PEEL_LOG = $log; $env:PEEL_EXIT_AFTER = '540'
    $p = Start-Process $App -PassThru
    WaitLog 'peel shell start' 15000 | Out-Null
    WaitLog 'registry preloaded|keyboard hotkey|could not register' 20000 | Out-Null
    Wait-Gui 2000
    $p
}
function Files($d) { (@(Get-ChildItem $d -File | ForEach-Object { $_.Name }) | Sort-Object) -join ',' }


function MkDir($n) { $d = Join-Path $script:Work $n; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $d | Out-Null
    Set-Content -Path (Join-Path $d ("{0}.txt" -f $n.ToLower())) -Value "review2 $n`r`nline two`r`n" -Encoding UTF8; $d }
function NewTab($d) {
    $hw = [In]::GetForegroundWindow().ToInt64()
    $before = @(ShellWins | ForEach-Object { $_.LocationURL })
    [PeelGuard]::CtrlT(); Wait-Gui 2000
    $tab = ShellWins | Where-Object { $_.HWND -eq $hw -and ($before -notcontains $_.LocationURL) } | Select-Object -First 1
    if (-not $tab) { $tab = ShellWins | Where-Object { $_.HWND -eq $hw -and $_.LocationURL -notlike '*review2-5f8a-gui*' } | Select-Object -First 1 }
    if (-not $tab) { throw "no new tab found" }
    if ($d) { $tab.Navigate2($d); Wait-Gui 3000 }
}
function Shown() { CountLog 'keyboard wheel shown' }
function Hidden() { CountLog 'keyboard wheel hidden' }
function RunMD($tag) {
    $draws = CountLog 'keyboard wheel convert page 0'; $runs = CountLog 'keyboard run '
    [PeelGuard]::Hotkey()
    $drawn = WaitLog 'keyboard wheel convert page 0: (.+)' 9000 ($draws+1)
    L "$tag drawn: $($drawn.Value)"
    if ($drawn) { Wait-Gui 300; L ("$tag aim MD: " + (AimAt 'MD')); [PeelGuard]::TapEx(0x0D,$false); WaitLog 'keyboard run ' 6000 ($runs+1) | Out-Null; Wait-Gui 5000 }
    else { Wait-Gui 3000 }
}
function OpenWheel($tag) {
    $n = Shown; [PeelGuard]::Hotkey(); $s = WaitLog 'keyboard wheel shown' 9000 ($n+1); Wait-Gui 700
    L ("$tag shown=" + [bool]$s + " fg=" + [In]::Fg()); [bool]$s
}
function PeelProc() { Get-Process Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $App } | Select-Object -First 1 }

$cursor = [System.Windows.Forms.Cursor]::Position
$peel = $null
try {
    Start-GuiGuard -Dir $Dir -RunDir $GuardDir -PauseInstalledFor $App -IdleSeconds $IdleSeconds -IdleTimeout $IdleTimeout -Log { param($m) L $m }
    $script:Work = Join-Path ([PeelGuard]::RunDir) 'scratch'
    L ("screens: " + ((@([System.Windows.Forms.Screen]::AllScreens) | ForEach-Object { "$($_.DeviceName) $($_.Bounds)" }) -join ' | '))
    $peel = StartPeel (Join-Path $Dir 'peel-R2.log')
    CloseMine

    # ---- T3: three tabs, each with a selection, active is the third ----
    $A = MkDir 'A'; $B = MkDir 'B'; $C = MkDir 'C'
    Start-Process explorer.exe $A | Out-Null; Wait-Gui 3000
    FrontDir $A; ClickItem 'a.txt'
    NewTab $B; ClickItem 'b.txt'
    NewTab $C; ClickItem 'c.txt'
    DumpWins 'T3'
    RunMD 'T3'
    Check 'T3 three tabs, active third converts c.txt only' ((Files $C) -eq 'c.md,c.txt' -and (Files $A) -eq 'a.txt' -and (Files $B) -eq 'b.txt') "A=[$(Files $A)] B=[$(Files $B)] C=[$(Files $C)]"
    # ---- T4: tab switched right before the hotkey (no settle time) ----
    [PeelGuard]::CtrlShiftTab()
    RunMD 'T4'
    Check 'T4 tab switched just before the hotkey converts b.txt' ((Files $B) -eq 'b.md,b.txt' -and (Files $A) -eq 'a.txt') "A=[$(Files $A)] B=[$(Files $B)] C=[$(Files $C)]"
    # ---- H: active tab is Home (new tab, nothing to convert), another tab has a selection ----
    NewTab $null
    DumpWins 'H'
    L ("H fg: " + [In]::Fg())
    RunMD 'H'
    L ("H peel lines: " + ((Select-String -Path $peelLog -Pattern 'explorer frame|no files' | Select-Object -Last 2 | ForEach-Object { $_.Line }) -join ' || '))
    Check 'H Home tab active converts nothing from the other tabs' ((Files $A) -eq 'a.txt' -and (Files $B) -eq 'b.md,b.txt' -and (Files $C) -eq 'c.md,c.txt') "A=[$(Files $A)] B=[$(Files $B)] C=[$(Files $C)]"
    CloseMine; Wait-Gui 1000

    # ---- S: a tab showing search results ----
    $S = MkDir 'S'
    Start-Process explorer.exe $S | Out-Null; Wait-Gui 3000
    FrontDir $S
    $hw = [In]::GetForegroundWindow().ToInt64()
    $w = ShellWins | Where-Object { $_.HWND -eq $hw } | Select-Object -First 1
    $q = 'search-ms:displayname=review2&crumb=System.Generic.String%3As.txt&crumb=location:' + [uri]::EscapeDataString($S)
    $w.Navigate2($q); Wait-Gui 6000
    ClickItem 's.txt'
    DumpWins 'S'
    RunMD 'S'
    Check 'S search results tab converts the selected result' ((Files $S) -eq 's.md,s.txt') "S=[$(Files $S)]"
    CloseMine; Wait-Gui 1000

    # ---- W: two Explorer windows, the front one wins ----
    $P = MkDir 'P'; $Q = MkDir 'Q'
    Start-Process explorer.exe $P | Out-Null; Wait-Gui 3000
    FrontDir $P; ClickItem 'p.txt'
    Start-Process explorer.exe $Q | Out-Null; Wait-Gui 3000
    FrontDir $Q; ClickItem 'q.txt'
    RunMD 'W'
    Check 'W two windows, front window converts' ((Files $Q) -eq 'q.md,q.txt' -and (Files $P) -eq 'p.txt') "P=[$(Files $P)] Q=[$(Files $Q)]"
    CloseMine; Wait-Gui 1000

    # ---- focus loss variants on one window ----
    $R = MkDir 'R'
    Start-Process explorer.exe $R | Out-Null; Wait-Gui 3000
    FrontDir $R
    $it = FindItem 'r.txt'; $rc = $it.Current.BoundingRectangle; $rx = [int]($rc.X+24); $ry = [int]($rc.Y+$rc.Height/2)
    SafeClick $rx $ry; Wait-Gui 400
    $hwR = [In]::GetForegroundWindow().ToInt64()
    $win = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$hwR).Current.BoundingRectangle
    # AltTab
    if (OpenWheel 'AT') { $h = Hidden; [PeelGuard]::Key(0x12,$true); Wait-Gui 60; [PeelGuard]::TapEx(0x09,$false); Wait-Gui 120; [PeelGuard]::Key(0x12,$false); Wait-Gui 1200
        Check 'AT Alt+Tab away closes the wheel' ((Hidden) -gt $h) "hidden $h->$(Hidden) fg=$([In]::Fg())" } else { Check 'AT wheel opened' $false 'not shown' }
    FrontDir $R; SafeClick $rx $ry; Wait-Gui 400
    # Win key
    if (OpenWheel 'WK') { $h = Hidden; [PeelGuard]::TapEx(0x5B,$true); Wait-Gui 1500
        Check 'WK Win key closes the wheel' ((Hidden) -gt $h) "hidden $h->$(Hidden) fg=$([In]::Fg())"; [PeelGuard]::TapEx(0x1B,$false); Wait-Gui 800 } else { Check 'WK wheel opened' $false 'not shown' }
    FrontDir $R; SafeClick $rx $ry; Wait-Gui 400
    # click elsewhere (bottom right of the Explorer window, far from the disc)
    if (OpenWheel 'CE') { $h = Hidden; $cx = [int]($win.Right - 60); $cy = [int]($win.Bottom - 60)
        if ([In]::RootAt($cx,$cy) -eq $hwR) { [PeelGuard]::Click($cx,$cy) } else { L "CE root at $cx,$cy is not explorer" }
        Wait-Gui 800
        Check 'CE click elsewhere closes the wheel' ((Hidden) -gt $h) "hidden $h->$(Hidden) fg=$([In]::Fg())" } else { Check 'CE wheel opened' $false 'not shown' }
    FrontDir $R; SafeClick $rx $ry; Wait-Gui 400
    # Alt held through Esc: Esc closes the wheel while Alt is down, Alt goes up afterwards in Explorer
    if (OpenWheel 'AE') { $h = Hidden; [PeelGuard]::Key(0x12,$true); Wait-Gui 300; [PeelGuard]::TapEx(0x1B,$false); Wait-Gui 300; [PeelGuard]::Key(0x12,$false); Wait-Gui 600
        L ("AE hidden $h->$(Hidden) fg=" + [In]::Fg()) }
    [PeelGuard]::TapEx(0x1B,$false); Wait-Gui 400

    # ---- X: 100 hotkey presses, open/close/reopen; threads and handles must not grow ----
    FrontDir $R; SafeClick $rx $ry; Wait-Gui 400
    $pp = PeelProc; $pp.Refresh(); $t0 = $pp.Threads.Count; $h0 = $pp.HandleCount
    $ign0 = CountLog 'ignored'
    for ($i = 0; $i -lt 100; $i++) { [PeelGuard]::Hotkey(); Wait-Gui 200 }
    Wait-Gui 15000
    [PeelGuard]::TapEx(0x1B,$false); Wait-Gui 3000
    $pp.Refresh(); $t1 = $pp.Threads.Count; $h1 = $pp.HandleCount
    L "X threads $t0->$t1 handles $h0->$h1 shown=$(Shown) hidden=$(Hidden) abandoned=$(CountLog 'abandoned')"
    Check 'X 100 presses do not leak threads' (($t1 - $t0) -lt 10) "threads $t0->$t1"
    Check 'X 100 presses do not leak handles' (($h1 - $h0) -lt 500) "handles $h0->$h1"
    Check 'X wheel not left open after Esc' ((Shown) -le (Hidden)) "shown=$(Shown) hidden=$(Hidden)"
    # after the storm the path still works end to end
    FrontDir $R; SafeClick $rx $ry; Wait-Gui 400
    RunMD 'X2'
    Check 'X2 conversion still works after 100 presses' ((Files $R) -eq 'r.md,r.txt') "R=[$(Files $R)]"
    $errs = @(Select-String -Path $peelLog -Pattern 'RCW|InvalidComObject|unhandled|AccessViolation|threw' | ForEach-Object { $_.Line })
    L ("X error lines: " + ($errs | Select-Object -First 5) -join ' || ')
    Check 'X no COM release or unhandled errors in the log' ($errs.Count -eq 0) "count=$($errs.Count)"
    Check 'X no ignored hotkey' ((CountLog 'ignored') -eq $ign0) "ignored $ign0->$(CountLog 'ignored')"
    L "peel log tail:"; (Get-Content $peelLog | Select-Object -Last 15) | ForEach-Object { L "   $_" }
}
catch {
    # A guard abort (a person at George, or no idle) is not a driver error.
    $script:GuiAbort = Get-GuiAbort $_
    if ($script:GuiAbort) { L "ABORTED: $_" } else { L "DRIVER ERROR: $_ at $($_.InvocationInfo.ScriptLineNumber)" }
}
finally {
    # First: end any drag and let go of every button and key (gui-guard.ps1).
    try { L (Stop-GuiGuard).Text } catch { L "teardown failed: $_" }
    Restore-GuiCursor $cursor
    if ($peel) { Stop-Process -Id $peel.Id -ErrorAction SilentlyContinue }
    Get-Process Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $App } | Stop-Process -ErrorAction SilentlyContinue
    CloseMine
    $driveText = Get-Content $drive -ErrorAction SilentlyContinue
    $fails = @($driveText | Where-Object { $_ -match 'CHECK .* FAIL|DRIVER ERROR' })
    # The runner's sign that this finally ran (gui-guard.ps1, Invoke-GuiTask).
    [pscustomobject]@{ ok = ($fails.Count -eq 0 -and -not $script:GuiAbort); aborted = $script:GuiAbort
        drove = [PeelGuard]::Drove; injections = [PeelGuard]::Injections; cleanups = [PeelGuard]::Cleanups; runDir = [PeelGuard]::RunDir } |
        ConvertTo-Json | Set-Content (Join-Path $Dir 'result.json') -Encoding UTF8
    L 'done'
}
