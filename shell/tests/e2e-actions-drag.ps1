<#
Peel end to end test for the Alt wheel file actions, driven as a real Explorer
drag. It proves the wheel -> ActionHost wiring that hq-plct.9 added: tap Alt
during a drag, drop on an action segment, and the shell performs the action.

Runs in the interactive desktop session (session 1) only, scheduled by
run-actions-drag.ps1, because a drag and the share sheet need a real desktop.

Nothing is hard coded: the segment labels come from Peel's own log line
("wheel action page 0: Copy,Copy Path,..."), which lists them clockwise from
twelve o'clock, and which modifier opens the action wheel comes from
`peel options` (default_modifier). So the test keeps telling the truth as the
registry grows.

Scenario Copy:  drop on Copy, expect the files on the clipboard as a file list.
Scenario Share: drop on Share, expect the share sheet raised (Peel's log) and a
                screenshot of it for read back, then Escape to dismiss it.

Drops land only inside this test's own folder under C:\peel-dev; the pointer
never goes near Explorer's navigation pane.
#>
param(
    [string]$Dir = 'C:\peel-dev\e2e-actions',
    [string]$App,
    # The engine the app under test must use. The five shell actions are new,
    # so the installed engine may not have them yet; point both the app (via
    # PEEL_CLI) and this script's own `peel options` read at the dev engine.
    [string]$Engine,
    [string]$Source = 'sample.txt'
)
if (-not $Engine) { $Engine = "$env:LOCALAPPDATA\Peel\venv\Scripts\peel.exe" }
$Peel = $Engine

$ErrorActionPreference = 'Stop'
$test = Join-Path $Dir 'testdir'
$driveLog = Join-Path $Dir 'drive.log'
$peelLog = Join-Path $Dir 'peel.log'
$shareShot = Join-Path $Dir 'shot-share.png'
$resultPath = Join-Path $Dir 'result.json'
$checks = [System.Collections.ArrayList]::new()
$script:cursor = @(0, 0)

New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item $driveLog, $peelLog, $shareShot, $resultPath -ErrorAction SilentlyContinue
Remove-Item $test -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $test | Out-Null

function L($message) {
    Add-Content $driveLog ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $message)
}

function Check($name, $ok, $detail) {
    $null = $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = "$detail" })
    L ("{0} {1}: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

function PeelLog() {
    if (Test-Path $peelLog) { return (Get-Content $peelLog -Raw -ErrorAction SilentlyContinue) }
    return ''
}

function CountLog($pattern) {
    return [regex]::Matches((PeelLog), $pattern).Count
}

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
  public static void Move(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    int ax = (int)(((x - vx) * 65535.0) / (vw - 1)), ay = (int)(((y - vy) * 65535.0) / (vh - 1));
    mouse_event(0x0001 | 0x8000 | 0x4000, ax, ay, 0, UIntPtr.Zero);
  }
  public static void Place(int x, int y) { SetCursorPos(x, y); }
  public static void Down() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
  public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
  public static void Tap(byte vk) {
    keybd_event(vk, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(60);
    keybd_event(vk, 0, 2, UIntPtr.Zero);
  }
}
'@
[void][In]::SetProcessDPIAware()

$VkAlt = [byte]0xA4
$VkEsc = [byte]0x1B

function MoveTo($x, $y) {
    [In]::Move([int]$x, [int]$y)
    $script:cursor = @([int]$x, [int]$y)
}

function GlideTo($x, $y, $steps = 8) {
    $from = $script:cursor
    for ($i = 1; $i -le $steps; $i++) {
        MoveTo ($from[0] + (($x - $from[0]) * $i / $steps)) ($from[1] + (($y - $from[1]) * $i / $steps))
        Start-Sleep -Milliseconds 35
    }
}

function CloseTestExplorer() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $_.LocationURL -like '*peel-dev/e2e-actions/testdir*' } |
            ForEach-Object { $_.Quit() }
    } catch { }
}

function FindItem($name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $withExt = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $withoutExt = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $stem)
    $isItem = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $cond = New-Object System.Windows.Automation.AndCondition(
        $isItem, (New-Object System.Windows.Automation.OrCondition($withExt, $withoutExt)))
    for ($i = 0; $i -lt 25; $i++) {
        $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($item) { return $item }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function StartDragFrom($item) {
    $r = $item.Current.BoundingRectangle
    $x = [int]($r.X + 24); $y = [int]($r.Y + $r.Height / 2)
    L "grab at $x,$y"
    [void][In]::Place($x, $y); Start-Sleep -Milliseconds 250
    MoveTo $x $y; Start-Sleep -Milliseconds 120
    [In]::Down(); Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 14; $i++) {
        MoveTo ($x + $i * 14) ($y + $i * 7)
        Start-Sleep -Milliseconds 30
    }
}

function ApproachRing($centre) {
    # The hub is a hole, so a pointer that never leaves it never enters the
    # overlay and the wheel never gets the drag data. Move out to the ring.
    $here = $script:cursor
    GlideTo ($centre[0]) ($centre[1] - 111) 6
    Start-Sleep -Milliseconds 250
    GlideTo $here[0] $here[1] 4
}

function SegmentPoint($centre, $labels, $label) {
    $index = [array]::IndexOf($labels, $label)
    if ($index -lt 0) { return $null }
    $step = 360.0 / $labels.Count
    $bearing = ($index * $step) * [Math]::PI / 180.0
    $radius = 111.0
    return @([int]($centre[0] + [Math]::Sin($bearing) * $radius),
             [int]($centre[1] - [Math]::Cos($bearing) * $radius))
}

# Open the action wheel on a fresh drag and drop on the named segment. Returns
# the drop log match, or $null if the wheel or segment never appeared.
function DropOnAction($segmentLabel) {
    $item = FindItem $Source
    if (-not $item) { throw "$Source is not visible in Explorer" }
    $dropSeen = CountLog "drop on $segmentLabel with"
    $shownSeen = CountLog 'wheel shown at'
    $dragSeen = CountLog 'drag start at'
    $drawnSeen = CountLog 'wheel action page 0:'
    StartDragFrom $item
    # Wait for Explorer's drag loop: tapping the modifier before OLE is live
    # opens a wheel the drag data never reaches ("not a file drag").
    if (-not (WaitLog 'drag start at' 4000 ($dragSeen + 1))) { return $null }
    Start-Sleep -Milliseconds 200
    [In]::Tap($VkAlt)
    $shown = WaitLog 'wheel shown at (\d+),(\d+)' 4000 ($shownSeen + 1)
    if (-not $shown) { return $null }
    $centre = @([int]$shown.Groups[1].Value, [int]$shown.Groups[2].Value)
    ApproachRing $centre
    $files = WaitLog 'drag carries (\d+) file' 4000
    if (-not $files) { L "wheel never got the drag data"; return $null }
    $drawn = WaitLog 'wheel action page 0: (.+)' 5000 ($drawnSeen + 1)
    if (-not $drawn) { return $null }
    $labels = @($drawn.Groups[1].Value.Trim() -split ',')
    L "action wheel labels: $($labels -join '|')"
    $target = SegmentPoint $centre $labels $segmentLabel
    if (-not $target) { throw "segment '$segmentLabel' not on the wheel: $($labels -join ',')" }
    GlideTo $target[0] $target[1]
    Start-Sleep -Milliseconds 400
    return @{ centre = $centre; dropSeen = $dropSeen }
}

$peelProcess = $null
try {
    # A share sheet left by an earlier run sits over the test window and
    # swallows the drag, so clear the desktop first (e2e-hygiene.ps1).
    . (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')
    L ("start: " + (Close-ShareSheets).Text)
    if (-not $App) {
        $App = Find-PeelApp (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    }
    if (-not (Test-Path $App)) { throw "Peel.exe not found: $App" }

    Set-Content -Path (Join-Path $test $Source) -Value "Peel action test`r`nline two`r`n" -Encoding UTF8
    L "source ready: $test\$Source"

    $registry = & $Peel options | ConvertFrom-Json
    $actionWheel = $registry.wheels | Where-Object { $_.family -eq 'action' }
    Check 'engine has an action wheel' ($actionWheel -ne $null) "default_modifier: $($actionWheel.default_modifier)"
    Check 'the action wheel opens on Alt' ($actionWheel.default_modifier -eq 'Alt') "$($actionWheel.default_modifier)"

    $env:PEEL_LOG = $peelLog
    $env:PEEL_EXIT_AFTER = '240'
    # The app inherits this and uses the dev engine, which has the five new
    # shell actions the installed engine may lack. PEEL_CLI only accepts an .exe.
    if ($Engine -and (Test-Path $Engine) -and ([System.IO.Path]::GetExtension($Engine) -eq '.exe')) {
        $env:PEEL_CLI = $Engine
        L "app engine via PEEL_CLI: $Engine"
    }
    $peelProcess = Start-Process $App -PassThru
    L "started $App as pid $($peelProcess.Id)"
    Check 'app starts' ((WaitLog 'peel shell start' 15000) -ne $null) 'peel shell start in the log'
    Check 'registry preloads' ((WaitLog 'registry preloaded' 20000) -ne $null) 'registry preloaded in the log'

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3

    # Scenario Copy: drop on Copy, expect the files on the clipboard.
    $copy = DropOnAction 'Copy'
    Check 'action wheel opens and draws (Copy)' ($copy -ne $null) 'wheel action page 0 drawn'
    if ($copy) {
        [In]::Up()
        $drop = WaitLog 'drop on Copy with (\d+) file' 6000 ($copy.dropSeen + 1)
        Check 'drop lands on Copy' ($drop -ne $null) "$($drop.Value)"
        # CopyAction puts the files on the clipboard as a file list.
        Start-Sleep -Milliseconds 800
        $onClipboard = $null
        for ($i = 0; $i -lt 10; $i++) {
            $onClipboard = [System.Windows.Forms.Clipboard]::GetFileDropList()
            if ($onClipboard -and $onClipboard.Count -gt 0) { break }
            Start-Sleep -Milliseconds 300
        }
        $names = @()
        if ($onClipboard) { $names = @($onClipboard | ForEach-Object { Split-Path $_ -Leaf }) }
        Check 'Copy put the file on the clipboard' ($names -contains $Source) "clipboard files: $($names -join ',')"
    }

    Start-Sleep -Milliseconds 1000

    # Scenario Share: drop on Share, expect the share sheet raised and painted.
    $share = DropOnAction 'Share'
    Check 'action wheel opens and draws (Share)' ($share -ne $null) 'wheel action page 0 drawn'
    if ($share) {
        $raisedSeen = CountLog 'share: share sheet raised'
        [In]::Up()
        $drop = WaitLog 'drop on Share with (\d+) file' 6000 ($share.dropSeen + 1)
        Check 'drop lands on Share' ($drop -ne $null) "$($drop.Value)"
        $offered = WaitLog 'share: offered (\d+) item' 6000
        Check 'Share offered the files to the sheet' ($offered -ne $null) "$($offered.Value)"
        $raised = WaitLog 'share: share sheet raised' 6000 ($raisedSeen + 1)
        Check 'Share raised the share sheet' ($raised -ne $null) "$($raised.Value)"
        # Give the system flyout a moment to paint, then screenshot the whole
        # primary screen (the flyout anchors to the right edge, not the cursor).
        Start-Sleep -Seconds 2
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($shareShot, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose(); $bitmap.Dispose()
        Check 'share sheet screenshot taken' (Test-Path $shareShot) $shareShot
        # Dismiss the sheet so nothing lingers on Daniel's desktop. Escape
        # alone did not (a sheet stayed on George for over an hour), so close
        # it by its window and check that it is gone.
        $gone = Close-ShareSheets
        Check 'share sheet dismissed after the screenshot' ($gone.Found -ge 1 -and $gone.Left -eq 0) $gone.Text
    }
}
catch {
    Check 'driver completed without throwing' $false "$_"
}
finally {
    if ($peelProcess) {
        try { Stop-Process -Id $peelProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Get-Process -Name 'Peel' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # Nothing may stay on Daniel's desktop, the share sheet least of all.
    try {
        $endClean = Close-ShareSheets
        L ("end: " + $endClean.Text)
        Check 'no share sheet left on screen' ($endClean.Left -eq 0) $endClean.Text
    }
    catch { Check 'no share sheet left on screen' $false "$_" }
    CloseTestExplorer
    # Leave the clipboard clean: the Copy scenario put files on it.
    try { [System.Windows.Forms.Clipboard]::Clear() } catch { }
    $allOk = -not ($checks | Where-Object { -not $_.ok })
    $result = [pscustomobject]@{ ok = [bool]$allOk; checks = $checks }
    $result | ConvertTo-Json -Depth 5 | Set-Content -Path $resultPath -Encoding UTF8
    L ("done, ok=$allOk")
}
