param(
    [string]$Dir = 'C:\peel-dev\review-5f8a-gui',
    [string]$App = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Peel\bin\Release\net8.0-windows\Peel.exe'),
    # gui-guard.ps1: user idle to wait for before driving, and how long to wait.
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 1800
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
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
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
  public static bool Front(long h) { var w = new IntPtr(h); ShowWindow(w, 9); keybd_event(0x12,0,0,UIntPtr.Zero); var ok = SetForegroundWindow(w); keybd_event(0x12,0,2,UIntPtr.Zero); System.Threading.Thread.Sleep(300); return GetForegroundWindow() == w; }
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
    mouse_event(2,0,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(4,0,0,0,UIntPtr.Zero); }
  public static void TapEx(byte vk, bool ext) { uint f = ext ? 1u : 0u; keybd_event(vk,0,f,UIntPtr.Zero);
    System.Threading.Thread.Sleep(45); keybd_event(vk,0,f|2u,UIntPtr.Zero); }
  public static void Key(byte vk, bool down) { keybd_event(vk, 0, down ? 0u : 2u, UIntPtr.Zero); }
  public static void Hotkey() { Key(0x11,true); System.Threading.Thread.Sleep(40); Key(0x12,true); System.Threading.Thread.Sleep(40);
    Key(0x0D,true); System.Threading.Thread.Sleep(60); Key(0x0D,false); System.Threading.Thread.Sleep(40);
    Key(0x12,false); System.Threading.Thread.Sleep(40); Key(0x11,false); }
  public static void CtrlT() { Key(0x11,true); System.Threading.Thread.Sleep(40); TapEx(0x54,false); System.Threading.Thread.Sleep(40); Key(0x11,false); }
  public static void CtrlShiftTab() { Key(0x11,true); Key(0x10,true); System.Threading.Thread.Sleep(40); TapEx(0x09,false); System.Threading.Thread.Sleep(40); Key(0x10,false); Key(0x11,false); }
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
    while ((Get-Date) -lt $d) { $f = [regex]::Matches((PeelLog), $p); if ($f.Count -ge $atLeast) { return $f[$f.Count-1] }; Start-Sleep -Milliseconds 100 }
    $null
}
function Highlight() { $m = [regex]::Matches((PeelLog), 'keyboard highlight (.+)'); if ($m.Count) { $m[$m.Count-1].Groups[1].Value.Trim() } }
function AimAt($t) { for ($i=0; $i -lt 30; $i++) { if ((Highlight) -eq $t) { return $true }; $s = CountLog 'keyboard highlight '; [In]::TapEx(0x27,$true); WaitLog 'keyboard highlight ' 3000 ($s+1) | Out-Null }; $false }
function ShellWins() { @((New-Object -ComObject Shell.Application).Windows()) }
function DumpWins($tag) { foreach ($w in ShellWins) { try { L ("  $tag win hwnd={0} url={1} sel={2}" -f $w.HWND, $w.LocationURL, (@($w.Document.SelectedItems()) | ForEach-Object { $_.Name }) -join ';') } catch { } } }
function CloseMine() { foreach ($w in ShellWins) { try { if ($w.LocationURL -like '*review-5f8a-gui*') { $w.Quit() } } catch { } } }
function FindItem($name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
        (New-Object System.Windows.Automation.OrCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $stem)))))
    for ($i=0; $i -lt 25; $i++) { $it = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c); if ($it) { return $it }; Start-Sleep -Milliseconds 300 }
    $null
}
function SafeClick($x, $y) { $root = [In]::RootAt($x, $y); $fg = [In]::GetForegroundWindow().ToInt64(); if ($root -ne $fg -or -not ([In]::Fg() -match 'CabinetWClass')) { throw "refusing click at $x,$y : root=$root fg=$([In]::Fg())" }; [In]::Click($x, $y) }
function FrontDir($d) { $u = ([uri]$d).AbsoluteUri; $w = ShellWins | Where-Object { $_.LocationURL -eq $u } | Select-Object -First 1; if (-not $w) { throw "no explorer for $d" }; $ok = [In]::Front([long]$w.HWND); L ("front $d -> $ok fg=" + [In]::Fg()); if (-not $ok) { throw "cannot bring $d to front" } }
function ClickItem($name) { $it = FindItem $name; if (-not $it) { throw "$name not visible" }; $r = $it.Current.BoundingRectangle; SafeClick ([int]($r.X+24)) ([int]($r.Y+$r.Height/2)); Start-Sleep -Milliseconds 400 }
function StartPeel($log) {
    $script:peelLog = $log; Remove-Item $log -ErrorAction SilentlyContinue
    $env:PEEL_LOG = $log; $env:PEEL_EXIT_AFTER = '150'
    $p = Start-Process $App -PassThru
    WaitLog 'peel shell start' 15000 | Out-Null
    WaitLog 'registry preloaded|keyboard hotkey|could not register' 20000 | Out-Null
    Start-Sleep -Seconds 2
    $p
}
function Files($d) { (@(Get-ChildItem $d -File | ForEach-Object { $_.Name }) | Sort-Object) -join ',' }

$cursor = [System.Windows.Forms.Cursor]::Position
$peel = $null; $holder = $null
try {
    Start-GuiGuard -Dir $Dir -IdleSeconds $IdleSeconds -IdleTimeout $IdleTimeout -Log { param($m) L $m }
    L ("screens: " + ((@([System.Windows.Forms.Screen]::AllScreens) | ForEach-Object { "$($_.DeviceName) $($_.Bounds)" }) -join ' | '))
    foreach ($n in 'A','B','C') { $d = Join-Path $Dir $n; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $d | Out-Null
        Set-Content -Path (Join-Path $d ("{0}.txt" -f $n.ToLower())) -Value "review $n`r`nline two`r`n" -Encoding UTF8 }
    $A = Join-Path $Dir 'A'; $B = Join-Path $Dir 'B'; $C = Join-Path $Dir 'C'

    # ---- T: two tabs in one Explorer window ----
    $peel = StartPeel (Join-Path $Dir 'peel-T.log')
    CloseMine
    Start-Process explorer.exe $A | Out-Null; Start-Sleep -Seconds 3
    FrontDir $A
    ClickItem 'a.txt'
    L ("T fg after click a.txt: " + [In]::Fg())
    [In]::CtrlT(); Start-Sleep -Seconds 2
    $hw = [In]::GetForegroundWindow().ToInt64()
    $tab = ShellWins | Where-Object { $_.HWND -eq $hw -and $_.LocationURL -notlike '*review-5f8a-gui/A*' } | Select-Object -First 1
    if (-not $tab) { throw 'no second tab found' }
    $tab.Navigate2($B); Start-Sleep -Seconds 3
    ClickItem 'b.txt'
    DumpWins 'T'
    L ("T fg before hotkey: " + [In]::Fg())
    [In]::Hotkey()
    $drawn = WaitLog 'keyboard wheel convert page 0: (.+)' 8000
    L "T drawn: $($drawn.Value)"
    if ($drawn) { L ("T aim MD: " + (AimAt 'MD')); [In]::TapEx(0x0D,$false); WaitLog 'keyboard run ' 6000 | Out-Null; Start-Sleep -Seconds 6 }
    L "T1 (active tab B, b.txt selected; expect B\b.md): A=[$(Files $A)] B=[$(Files $B)]"
    Check 'T1 active tab B converts b.txt' ((Files $B) -eq 'b.md,b.txt' -and (Files $A) -eq 'a.txt') "A=[$(Files $A)] B=[$(Files $B)]"
    # switch back to tab A and press again
    [In]::CtrlShiftTab(); Start-Sleep -Seconds 2
    L ("T fg after tab switch: " + [In]::Fg())
    DumpWins 'T2'
    $runs = CountLog 'keyboard run '; $draws = CountLog 'keyboard wheel convert page 0'
    [In]::Hotkey()
    $drawn = WaitLog 'keyboard wheel convert page 0: (.+)' 8000 ($draws+1)
    if ($drawn) { L ("T2 aim MD: " + (AimAt 'MD')); [In]::TapEx(0x0D,$false); WaitLog 'keyboard run ' 6000 ($runs+1) | Out-Null; Start-Sleep -Seconds 6 }
    L "T2 (active tab A, a.txt selected; expect A\a.md): A=[$(Files $A)] B=[$(Files $B)]"
    Check 'T2 active tab A converts a.txt' ((Files $A) -eq 'a.md,a.txt') "A=[$(Files $A)] B=[$(Files $B)]"
    CloseMine; Start-Sleep -Seconds 1

    # ---- F: input lands in Explorer between hotkey and wheel show ----
    Start-Process explorer.exe $C | Out-Null; Start-Sleep -Seconds 3
    FrontDir $C
    $it = FindItem 'c.txt'; $r = $it.Current.BoundingRectangle; $cx = [int]($r.X+24); $cy = [int]($r.Y+$r.Height/2)
    SafeClick $cx $cy; Start-Sleep -Milliseconds 400
    $shownN = CountLog 'keyboard wheel shown'
    $exh = [In]::GetForegroundWindow().ToInt64(); [In]::Hotkey(); if ([In]::RootAt($cx,$cy) -eq $exh) { [In]::Click($cx, $cy); L 'F clicked explorer right after hotkey' } else { L 'F skipped click, root not explorer' }
    $shown = WaitLog 'keyboard wheel shown' 8000 ($shownN+1)
    Start-Sleep -Milliseconds 600
    L "F shown: $($shown.Value)"
    L ("F fg after show: " + [In]::Fg())
    $hidN = CountLog 'keyboard wheel hidden'
    [In]::TapEx(0x1B,$false)
    $hid = WaitLog 'keyboard wheel hidden' 3000 ($hidN+1)
    L "F after Esc hidden: $($hid.Value)"
    Check 'F wheel is not left open' ((CountLog 'keyboard wheel shown') -le (CountLog 'keyboard wheel hidden')) "shown=$(CountLog 'keyboard wheel shown') hidden=$(CountLog 'keyboard wheel hidden')"
    $shownN = CountLog 'keyboard wheel shown'
    L ("F fg after Esc: " + [In]::Fg())
    $ign = CountLog 'ignored'
    SafeClick $cx $cy; [In]::Hotkey(); Start-Sleep -Seconds 3
    L "F second hotkey ignored lines: before=$ign after=$(CountLog 'ignored')"
    Check 'F hotkey opens the wheel again' ((CountLog 'keyboard wheel shown') -gt $shownN -and (CountLog 'ignored') -eq $ign) "shown $shownN->$(CountLog 'keyboard wheel shown'), ignored $ign->$(CountLog 'ignored')"
    $p = Get-Process Peel -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle } | ForEach-Object { $_.MainWindowTitle }
    # clean: Esc and a click on the item to deactivate anything
    [In]::TapEx(0x1B,$false); Start-Sleep -Milliseconds 500

    # ---- M: modifier switching and Alt menu mode ----
    FrontDir $C; SafeClick $cx $cy; Start-Sleep -Milliseconds 500
    $shownN = CountLog 'keyboard wheel shown'
    [In]::Hotkey()
    $shown = WaitLog 'keyboard wheel shown' 8000 ($shownN+1)
    Start-Sleep -Milliseconds 800
    L ("M fg after show: " + [In]::Fg())
    foreach ($k in @(@('Shift',0x10),@('Ctrl',0x11),@('Alt',0x12))) {
        $w = CountLog 'keyboard wheel \S+ page'
        [In]::TapEx([byte]$k[1], $false); Start-Sleep -Milliseconds 700
        $last = [regex]::Matches((PeelLog), 'keyboard wheel (\S+) page \d+:.*|no keyboard wheel is bound to \S+')
        L ("M after {0}: wheel lines {1}->{2}; last={3}; fg={4}" -f $k[0], $w, (CountLog 'keyboard wheel \S+ page'), $(if ($last.Count) { $last[$last.Count-1].Value } else { '' }), [In]::Fg())
    }
    $h = CountLog 'keyboard highlight '
    [In]::TapEx(0x27,$true); Start-Sleep -Milliseconds 700
    L "M Right after Alt tap: highlight lines $h->$(CountLog 'keyboard highlight ')"
    $hidN = CountLog 'keyboard wheel hidden'
    [In]::TapEx(0x1B,$false); Start-Sleep -Milliseconds 700
    L "M Esc after Alt: hidden lines $hidN->$(CountLog 'keyboard wheel hidden')"
    Check 'M Esc still reaches the wheel after an Alt tap' ((CountLog 'keyboard wheel hidden') -gt $hidN) "hidden $hidN->$(CountLog 'keyboard wheel hidden')"
    [In]::TapEx(0x1B,$false); Start-Sleep -Milliseconds 300
    L "C/M peel log tail:"; (Get-Content $peelLog | Select-Object -Last 25) | ForEach-Object { L "   $_" }
    Stop-Process -Id $peel.Id -ErrorAction SilentlyContinue; $peel = $null; Start-Sleep -Seconds 2
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
    Start-Sleep -Seconds 3
    L ("K holder: " + (Get-Content (Join-Path $Dir 'holder.txt') -ErrorAction SilentlyContinue))
    $peel = StartPeel (Join-Path $Dir 'peel-K.log')
    Start-Sleep -Seconds 3
    L ("K peel log hotkey lines: " + ((Select-String -Path $peelLog -Pattern 'hotkey' | ForEach-Object { $_.Line }) -join ' || '))
    L ("K top-level visible Peel windows: " + ((Get-Process -Id $peel.Id).MainWindowTitle))
    Check 'K a taken hotkey shows a toast' ((CountLog 'hotkey conflict toast shown') -ge 1) "conflict toast lines: $(CountLog 'hotkey conflict toast shown')"
}
catch { L "DRIVER ERROR: $_ at $($_.InvocationInfo.ScriptLineNumber)" }
finally {
    # First: end any drag and let go of every button and key (gui-guard.ps1).
    try { L (Stop-GuiGuard).Text } catch { L "teardown failed: $_" }
    [System.Windows.Forms.Cursor]::Position = $cursor
    if ($holder) { Stop-Process -Id $holder.Id -ErrorAction SilentlyContinue }
    if ($peel) { Stop-Process -Id $peel.Id -ErrorAction SilentlyContinue }
    Get-Process Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $App } | Stop-Process -ErrorAction SilentlyContinue
    CloseMine
    L 'done'
}
