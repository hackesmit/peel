param(
    [string]$Dir = 'C:\peel-dev\review-5f8a-gui',
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
    $env:PEEL_LOG = $log; $env:PEEL_EXIT_AFTER = '150'
    $p = Start-Process $App -PassThru
    WaitLog 'peel shell start' 15000 | Out-Null
    WaitLog 'registry preloaded|keyboard hotkey|could not register' 20000 | Out-Null
    Wait-Gui 2000
    $p
}
function Files($d) { (@(Get-ChildItem $d -File | ForEach-Object { $_.Name }) | Sort-Object) -join ',' }

$cursor = [System.Windows.Forms.Cursor]::Position
$peel = $null; $holder = $null
$script:GuiAbort = $null
try {
    Start-GuiGuard -Dir $Dir -RunDir $GuardDir -PauseInstalledFor $App -IdleSeconds $IdleSeconds -IdleTimeout $IdleTimeout -Log { param($m) L $m }
    L ("screens: " + ((@([System.Windows.Forms.Screen]::AllScreens) | ForEach-Object { "$($_.DeviceName) $($_.Bounds)" }) -join ' | '))
    $script:Work = Join-Path ([PeelGuard]::RunDir) 'scratch'
    foreach ($n in 'A','B','C') { $d = Join-Path $script:Work $n; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $d | Out-Null
        Set-Content -Path (Join-Path $d ("{0}.txt" -f $n.ToLower())) -Value "review $n`r`nline two`r`n" -Encoding UTF8 }
    $A = Join-Path $script:Work 'A'; $B = Join-Path $script:Work 'B'; $C = Join-Path $script:Work 'C'

    # ---- T: two tabs in one Explorer window ----
    $peel = StartPeel (Join-Path $Dir 'peel-T.log')
    CloseMine
    Start-Process explorer.exe $A | Out-Null; Wait-Gui 3000
    FrontDir $A
    ClickItem 'a.txt'
    L ("T fg after click a.txt: " + [In]::Fg())
    [PeelGuard]::CtrlT(); Wait-Gui 2000
    $hw = [In]::GetForegroundWindow().ToInt64()
    $tab = ShellWins | Where-Object { $_.HWND -eq $hw -and $_.LocationURL -ne ([uri]$A).AbsoluteUri } | Select-Object -First 1
    if (-not $tab) { throw 'no second tab found' }
    $tab.Navigate2($B); Wait-Gui 3000
    ClickItem 'b.txt'
    DumpWins 'T'
    L ("T fg before hotkey: " + [In]::Fg())
    [PeelGuard]::Hotkey()
    $drawn = WaitLog 'keyboard wheel convert page 0: (.+)' 8000
    L "T drawn: $($drawn.Value)"
    if ($drawn) { L ("T aim MD: " + (AimAt 'MD')); [PeelGuard]::TapEx(0x0D,$false); WaitLog 'keyboard run ' 6000 | Out-Null; Wait-Gui 6000 }
    L "T1 (active tab B, b.txt selected; expect B\b.md): A=[$(Files $A)] B=[$(Files $B)]"
    Check 'T1 active tab B converts b.txt' ((Files $B) -eq 'b.md,b.txt' -and (Files $A) -eq 'a.txt') "A=[$(Files $A)] B=[$(Files $B)]"
    # switch back to tab A and press again
    [PeelGuard]::CtrlShiftTab(); Wait-Gui 2000
    L ("T fg after tab switch: " + [In]::Fg())
    DumpWins 'T2'
    $runs = CountLog 'keyboard run '; $draws = CountLog 'keyboard wheel convert page 0'
    [PeelGuard]::Hotkey()
    $drawn = WaitLog 'keyboard wheel convert page 0: (.+)' 8000 ($draws+1)
    if ($drawn) { L ("T2 aim MD: " + (AimAt 'MD')); [PeelGuard]::TapEx(0x0D,$false); WaitLog 'keyboard run ' 6000 ($runs+1) | Out-Null; Wait-Gui 6000 }
    L "T2 (active tab A, a.txt selected; expect A\a.md): A=[$(Files $A)] B=[$(Files $B)]"
    Check 'T2 active tab A converts a.txt' ((Files $A) -eq 'a.md,a.txt') "A=[$(Files $A)] B=[$(Files $B)]"
    CloseMine; Wait-Gui 1000

    # ---- F: input lands in Explorer between hotkey and wheel show ----
    Start-Process explorer.exe $C | Out-Null; Wait-Gui 3000
    FrontDir $C
    $it = FindItem 'c.txt'; $r = $it.Current.BoundingRectangle; $cx = [int]($r.X+24); $cy = [int]($r.Y+$r.Height/2)
    SafeClick $cx $cy; Wait-Gui 400
    $shownN = CountLog 'keyboard wheel shown'
    $exh = [In]::GetForegroundWindow().ToInt64(); [PeelGuard]::Hotkey(); if ([In]::RootAt($cx,$cy) -eq $exh) { [PeelGuard]::Click($cx, $cy); L 'F clicked explorer right after hotkey' } else { L 'F skipped click, root not explorer' }
    $shown = WaitLog 'keyboard wheel shown' 8000 ($shownN+1)
    Wait-Gui 600
    L "F shown: $($shown.Value)"
    L ("F fg after show: " + [In]::Fg())
    $hidN = CountLog 'keyboard wheel hidden'
    [PeelGuard]::TapEx(0x1B,$false)
    $hid = WaitLog 'keyboard wheel hidden' 3000 ($hidN+1)
    L "F after Esc hidden: $($hid.Value)"
    Check 'F wheel is not left open' ((CountLog 'keyboard wheel shown') -le (CountLog 'keyboard wheel hidden')) "shown=$(CountLog 'keyboard wheel shown') hidden=$(CountLog 'keyboard wheel hidden')"
    $shownN = CountLog 'keyboard wheel shown'
    L ("F fg after Esc: " + [In]::Fg())
    $ign = CountLog 'ignored'
    SafeClick $cx $cy; [PeelGuard]::Hotkey(); Wait-Gui 3000
    L "F second hotkey ignored lines: before=$ign after=$(CountLog 'ignored')"
    Check 'F hotkey opens the wheel again' ((CountLog 'keyboard wheel shown') -gt $shownN -and (CountLog 'ignored') -eq $ign) "shown $shownN->$(CountLog 'keyboard wheel shown'), ignored $ign->$(CountLog 'ignored')"
    $p = Get-Process Peel -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle } | ForEach-Object { $_.MainWindowTitle }
    # clean: Esc and a click on the item to deactivate anything
    [PeelGuard]::TapEx(0x1B,$false); Wait-Gui 500

    # ---- M: modifier switching and Alt menu mode ----
    FrontDir $C; SafeClick $cx $cy; Wait-Gui 500
    $shownN = CountLog 'keyboard wheel shown'
    [PeelGuard]::Hotkey()
    $shown = WaitLog 'keyboard wheel shown' 8000 ($shownN+1)
    Wait-Gui 800
    L ("M fg after show: " + [In]::Fg())
    foreach ($k in @(@('Shift',0x10),@('Ctrl',0x11),@('Alt',0x12))) {
        $w = CountLog 'keyboard wheel \S+ page'
        [PeelGuard]::TapEx([byte]$k[1], $false); Wait-Gui 700
        $last = [regex]::Matches((PeelLog), 'keyboard wheel (\S+) page \d+:.*|no keyboard wheel is bound to \S+')
        L ("M after {0}: wheel lines {1}->{2}; last={3}; fg={4}" -f $k[0], $w, (CountLog 'keyboard wheel \S+ page'), $(if ($last.Count) { $last[$last.Count-1].Value } else { '' }), [In]::Fg())
    }
    $h = CountLog 'keyboard highlight '
    [PeelGuard]::TapEx(0x27,$true); Wait-Gui 700
    L "M Right after Alt tap: highlight lines $h->$(CountLog 'keyboard highlight ')"
    $hidN = CountLog 'keyboard wheel hidden'
    [PeelGuard]::TapEx(0x1B,$false); Wait-Gui 700
    L "M Esc after Alt: hidden lines $hidN->$(CountLog 'keyboard wheel hidden')"
    Check 'M Esc still reaches the wheel after an Alt tap' ((CountLog 'keyboard wheel hidden') -gt $hidN) "hidden $hidN->$(CountLog 'keyboard wheel hidden')"
    [PeelGuard]::TapEx(0x1B,$false); Wait-Gui 300
    L "C/M peel log tail:"; (Get-Content $peelLog | Select-Object -Last 25) | ForEach-Object { L "   $_" }
    Stop-Process -Id $peel.Id -ErrorAction SilentlyContinue; $peel = $null; Wait-Gui 2000
    CloseMine

    # ---- K: the chord is taken by another app ----
    $hs = Join-Path $Dir 'holder.ps1'
    Set-Content $hs -Value @'
Add-Type 'using System; using System.Runtime.InteropServices; public static class H { [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint m, uint vk); }'
$ok = [H]::RegisterHotKey([IntPtr]::Zero, 1, 0x4003, 0x0D)
Set-Content (Join-Path $PSScriptRoot 'holder.txt') "registered=$ok"
Start-Sleep -Seconds 25
'@
    $holder = Start-Process powershell.exe -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',$hs -PassThru -WindowStyle Hidden
    Wait-Gui 3000
    L ("K holder: " + (Get-Content (Join-Path $Dir 'holder.txt') -ErrorAction SilentlyContinue))
    $peel = StartPeel (Join-Path $Dir 'peel-K.log')
    Wait-Gui 3000
    L ("K peel log hotkey lines: " + ((Select-String -Path $peelLog -Pattern 'hotkey' | ForEach-Object { $_.Line }) -join ' || '))
    L ("K top-level visible Peel windows: " + ((Get-Process -Id $peel.Id).MainWindowTitle))
    Check 'K a taken hotkey shows a toast' ((CountLog 'hotkey conflict toast shown') -ge 1) "conflict toast lines: $(CountLog 'hotkey conflict toast shown')"
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
    if ($holder) { Stop-Process -Id $holder.Id -ErrorAction SilentlyContinue }
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
