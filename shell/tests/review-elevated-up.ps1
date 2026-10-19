<#
Reviewer reproduction for hq-plct.7 round 1: a drag that ends over an
elevated window.

A medium integrity low level hook is not told about input that goes to a
higher integrity window. If the button comes up over an elevated window, Peel
never sees WM_LBUTTONUP, DragStateMachine keeps Dragging = true, and the next
plain Shift, Ctrl or Alt press (no button held) opens the wheel on the desktop.

Run from hqbox (ssh session 0) with run-review-elevated.ps1, which starts Peel
at medium integrity and this driver elevated, both in session 1.

Control:  drag from Explorer, release over Explorer. Expect "drag end".
Attack:   drag from Explorer, release over an elevated Notepad, then tap Shift
          with no button held. Expect no "tapped mid drag" and no wheel.
#>
param(
    [string]$Dir = 'C:\peel-dev\review-e2e',
    [string]$Source = 'sample.txt'
)

$ErrorActionPreference = 'Stop'
$test = Join-Path $Dir 'testdir'
$driveLog = Join-Path $Dir 'drive.log'
$peelLog = Join-Path $Dir 'peel.log'
$resultPath = Join-Path $Dir 'result.json'
$checks = [System.Collections.ArrayList]::new()
$script:cursor = @(0, 0)

function L($message) { Add-Content $driveLog ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $message) }
function Check($name, $ok, $detail) {
    $null = $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = "$detail" })
    L ("{0} {1}: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function PeelLog() { if (Test-Path $peelLog) { return (Get-Content $peelLog -Raw -ErrorAction SilentlyContinue) } ; return '' }
function CountLog($pattern) { return [regex]::Matches((PeelLog), $pattern).Count }
function WaitLog($pattern, $timeoutMs, $atLeast = 1) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $found = [regex]::Matches((PeelLog), $pattern)
        if ($found.Count -ge $atLeast) { return $found[$found.Count - 1] }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class In {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static void Move(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    int ax = (int)(((x - vx) * 65535.0) / (vw - 1)), ay = (int)(((y - vy) * 65535.0) / (vh - 1));
    mouse_event(0x0001 | 0x8000 | 0x4000, ax, ay, 0, UIntPtr.Zero);
  }
  public static void Down() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
  public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
  public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
'@
[void][In]::SetProcessDPIAware()

function MoveTo($x, $y) { [In]::Move([int]$x, [int]$y); $script:cursor = @([int]$x, [int]$y) }
function GlideTo($x, $y, $steps = 10) {
    $from = $script:cursor
    for ($i = 1; $i -le $steps; $i++) {
        MoveTo ($from[0] + (($x - $from[0]) * $i / $steps)) ($from[1] + (($y - $from[1]) * $i / $steps))
        Start-Sleep -Milliseconds 35
    }
}
function FindByName($name, $type) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $a = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $b = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $stem)
    $t = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    $cond = New-Object System.Windows.Automation.AndCondition($t, (New-Object System.Windows.Automation.OrCondition($a, $b)))
    for ($i = 0; $i -lt 25; $i++) {
        $found = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($found) { return $found }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function CloseTestExplorer() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $_.LocationURL -like '*review-e2e/testdir*' } | ForEach-Object { $_.Quit() }
    } catch { }
}
function DragFromItem() {
    $item = FindByName $Source ([System.Windows.Automation.ControlType]::ListItem)
    if (-not $item) { throw "$Source is not visible in Explorer" }
    $r = $item.Current.BoundingRectangle
    $x = [int]($r.X + 24); $y = [int]($r.Y + $r.Height / 2)
    $script:grab = @($x, $y)
    L "grab at $x,$y"
    [void][In]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 250
    MoveTo $x $y; Start-Sleep -Milliseconds 120
    [In]::Down(); Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 8; $i++) { MoveTo ($x + $i * 10) ($y + $i * 5); Start-Sleep -Milliseconds 30 }
}

$notepad = $null
$originalCursor = [System.Windows.Forms.Cursor]::Position
try {
    New-Item -ItemType Directory -Force $test | Out-Null
    Set-Content -Path (Join-Path $test $Source) -Value 'x' -Encoding UTF8
    $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    Check 'driver is elevated' $elevated "admin token: $elevated"
    Check 'peel is up' ((WaitLog 'registry preloaded' 30000) -ne $null) 'registry preloaded'
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    L "work area $bounds"
    $half = [int]($bounds.Width / 2)

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3
    $shellWin = $null
    for ($i = 0; $i -lt 20 -and -not $shellWin; $i++) {
        $shellWin = (New-Object -ComObject Shell.Application).Windows() | Where-Object { $_.LocationURL -like '*review-e2e/testdir*' } | Select-Object -First 1
        if (-not $shellWin) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $shellWin) { throw 'test Explorer window not found' }
    $explorerHwnd = [IntPtr][int64]$shellWin.HWND
    [void][In]::MoveWindow($explorerHwnd, $bounds.X, $bounds.Y + 40, $half - 20, 500, $true)

    # An elevated classic window: a WinForms form in a child of this elevated driver.
    $formCmd = "Add-Type -AssemblyName System.Windows.Forms; `$f = New-Object System.Windows.Forms.Form; `$f.Text = 'PeelReviewElevated'; `$f.StartPosition = 'Manual'; `$f.Left = $($bounds.X + $half + 20); `$f.Top = $($bounds.Y + 40); `$f.Width = $($half - 40); `$f.Height = 500; [void]`$f.ShowDialog()"
    $notepad = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-WindowStyle', 'Hidden', '-Command', $formCmd) -PassThru
    Start-Sleep -Seconds 4
    Start-Sleep -Milliseconds 500
    [void][In]::SetForegroundWindow($explorerHwnd)
    Start-Sleep -Milliseconds 800

    # Control: release over Explorer.
    DragFromItem
    Check 'control: drag start seen' ((WaitLog 'drag start at' 4000 1) -ne $null) 'drag start'
    # Release on the blank file list of the same folder (a no-op drop). Never
    # near the navigation pane: a drop there moves the file into that folder.
    GlideTo ($script:grab[0] + 160) ($script:grab[1] + 180)
    [In]::Up()
    Check 'control: drag end seen' ((WaitLog 'drag end' 3000 1) -ne $null) 'drag end after release over Explorer'
    Start-Sleep -Seconds 1

    # Attack: release over the elevated Notepad.
    $endsBefore = CountLog 'drag end'
    $tapsBefore = CountLog 'tapped mid drag'
    $shownBefore = CountLog 'wheel shown at'
    DragFromItem
    Check 'attack: drag start seen' ((WaitLog 'drag start at' 4000 2) -ne $null) 'second drag start'
    GlideTo ($bounds.X + $half + 200) ($bounds.Y + 300) 14
    Start-Sleep -Milliseconds 300
    [In]::Up()
    Start-Sleep -Milliseconds 1500
    $endsAfter = CountLog 'drag end'
    L "drag end lines before/after release over elevated window: $endsBefore/$endsAfter"
    Check 'attack: button up over elevated window reaches Peel' ($endsAfter -gt $endsBefore) "drag end count $endsBefore to $endsAfter"

    # Back to a medium window under the pointer, then a plain Shift press with no button held.
    MoveTo ($script:grab[0] + 160) ($script:grab[1] + 180)
    Start-Sleep -Milliseconds 300
    [In]::Tap([byte]0xA0)
    Start-Sleep -Milliseconds 1500
    $tapsAfter = CountLog 'tapped mid drag'
    $shownAfter = CountLog 'wheel shown at'
    Check 'attack: plain Shift with no button held opens no wheel' (($tapsAfter -eq $tapsBefore) -and ($shownAfter -eq $shownBefore)) "tapped mid drag $tapsBefore to $tapsAfter, wheel shown $shownBefore to $shownAfter"
    [In]::Tap([byte]0x1B)
    Start-Sleep -Seconds 5
}
catch {
    Check 'driver finished' $false $_.Exception.Message
}
finally {
    try { if ($notepad -and -not $notepad.HasExited) { $notepad.Kill() } } catch { }
    CloseTestExplorer
    [void][In]::SetCursorPos($originalCursor.X, $originalCursor.Y)
    $ok = -not ($checks | Where-Object { -not $_.ok })
    [pscustomobject]@{ ok = $ok; checks = $checks } | ConvertTo-Json -Depth 4 | Set-Content $resultPath
}
