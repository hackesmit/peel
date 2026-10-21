<#
Wheel rendering test: screenshot the wheel with a realistic number of segments.

The engine registry on a given machine holds whatever families have landed, and
today that can be a single conversion, which proves nothing about how a ring of
nine or of more than one page looks. This run points Peel at the fake engine in
tests\FakeEngine, which serves a canned `peel options` payload and refuses to
run anything, so the only thing exercised is the drawing and the paging.

Runs in the interactive session, like e2e-drag.ps1. Use run-wheel-render.ps1.
#>
param(
    [string]$Dir = 'C:\peel-dev\e2e-render',
    [string]$App = 'C:\peel-dev\peel-4500-hq-plct-7\shell\Peel\bin\Release\net8.0-windows\Peel.exe',
    [string]$Fake = 'C:\peel-dev\peel-4500-hq-plct-7\shell\tests\FakeEngine\bin\Release\net8.0\FakeEngine.exe',
    [string]$Options = 'C:\peel-dev\peel-4500-hq-plct-7\shell\tests\fake-options.json',
    # gui-guard.ps1: user idle to wait for before driving, and how long to wait.
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 1800,
    # The run folder a runner made for this run (gui-guard.ps1, Invoke-GuiTask).
    [string]$GuardDir = ''
)

$ErrorActionPreference = 'Stop'
$test = Join-Path $Dir 'testdir'
$driveLog = Join-Path $Dir 'drive.log'
$peelLog = Join-Path $Dir 'peel.log'
$resultPath = Join-Path $Dir 'result.json'
$checks = [System.Collections.ArrayList]::new()
$script:cursor = @(0, 0)
$script:GuiAbort = $null

New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item (Join-Path $Dir '*.png'), $driveLog, $peelLog, $resultPath -ErrorAction SilentlyContinue
Remove-Item $test -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $test | Out-Null

function L($message) { Add-Content $driveLog ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $message) }
function Check($name, $ok, $detail) {
    $null = $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = "$detail" })
    L ("{0} {1}: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function PeelLog() {
    if (Test-Path $peelLog) { return (Get-Content $peelLog -Raw -ErrorAction SilentlyContinue) }
    return ''
}
function WaitLog($pattern, $timeoutMs, $atLeast = 1) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $found = [regex]::Matches((PeelLog), $pattern)
        if ($found.Count -ge $atLeast) { return $found[$found.Count - 1] }
        Assert-NoUserInput
        Start-Sleep -Milliseconds 100
    }
    return $null
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class In2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
'@
[void][In2]::SetProcessDPIAware()
# Idle wait, real input abort, and the teardown that ends any drag.
. (Join-Path $PSScriptRoot 'gui-guard.ps1')

function MoveTo($x, $y) { Assert-NoUserInput; [PeelGuard]::Move([int]$x, [int]$y); $script:cursor = @([int]$x, [int]$y) }
function Jiggle($times = 4) {
    for ($i = 1; $i -le $times; $i++) { MoveTo ($script:cursor[0] + 2) ($script:cursor[1] + 2); Start-Sleep -Milliseconds 70 }
}
function GlideTo($x, $y, $steps = 8) {
    $from = $script:cursor
    for ($i = 1; $i -le $steps; $i++) {
        MoveTo ($from[0] + (($x - $from[0]) * $i / $steps)) ($from[1] + (($y - $from[1]) * $i / $steps))
        Start-Sleep -Milliseconds 35
    }
}
function SegmentPoint($centre, $count, $index) {
    $bearing = ($index * (360.0 / $count)) * [Math]::PI / 180.0
    return @([int]($centre[0] + [Math]::Sin($bearing) * 111.0), [int]($centre[1] - [Math]::Cos($bearing) * 111.0))
}
function Shoot($centre, $name) {
    $path = Join-Path $Dir $name
    $bitmap = New-Object System.Drawing.Bitmap 460, 460
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($centre[0] - 230, $centre[1] - 230, 0, 0, $bitmap.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    L "screenshot $path"
    return $path
}
function CloseTestExplorer() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $_.LocationURL -like '*peel-dev/e2e-render/testdir*' } | ForEach-Object { $_.Quit() }
    } catch { }
}
function FindItem($name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $a = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $b = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $stem)
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $cond = New-Object System.Windows.Automation.AndCondition($c, (New-Object System.Windows.Automation.OrCondition($a, $b)))
    for ($i = 0; $i -lt 25; $i++) {
        $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($item) { return $item }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

$peelProcess = $null
$originalCursor = [System.Windows.Forms.Cursor]::Position
$shots = [System.Collections.ArrayList]::new()
try {
    . (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')
    if (-not (Test-Path $Fake)) { throw "build the fake engine first: $Fake" }
    Set-Content -Path (Join-Path $test 'sample.txt') -Value "wheel rendering test`r`n" -Encoding UTF8

    Start-GuiGuard -Dir $Dir -RunDir $GuardDir -PauseInstalledFor $App -IdleSeconds $IdleSeconds -IdleTimeout $IdleTimeout -Log { param($m) L $m }
    # Note the share sheets already on screen: they are not this run's, so
    # Close-ShareSheets leaves them alone (e2e-hygiene.ps1).
    L ("start: " + (Register-ShareSheets).Text)
    $originalCursor = [System.Windows.Forms.Cursor]::Position

    $env:PEEL_LOG = $peelLog
    $env:PEEL_EXIT_AFTER = '240'
    $env:PEEL_CLI = $Fake
    $env:PEEL_FAKE_OPTIONS = $Options
    $peelProcess = Start-Process $App -PassThru
    Check 'app starts on the fake engine' ((WaitLog 'registry preloaded: convert, tool, action' 20000) -ne $null) 'fake registry preloaded'

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3
    $item = FindItem 'sample.txt'
    if (-not $item) { throw 'sample.txt is not visible in Explorer' }

    $r = $item.Current.BoundingRectangle
    $x = [int]($r.X + 24); $y = [int]($r.Y + $r.Height / 2)
    [void][PeelGuard]::Place($x, $y); Start-Sleep -Milliseconds 250
    MoveTo $x $y; Start-Sleep -Milliseconds 120
    [PeelGuard]::Down(); Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 14; $i++) { MoveTo ($x + $i * 14) ($y + $i * 7); Start-Sleep -Milliseconds 30 }

    [PeelGuard]::Tap([byte]0xA0)
    $shown = WaitLog 'wheel shown at (\d+),(\d+)' 4000
    if (-not $shown) { throw 'the wheel never opened' }
    $centre = @([int]$shown.Groups[1].Value, [int]$shown.Groups[2].Value)
    Jiggle
    $drawn = WaitLog 'wheel convert page 0: (.+)' 6000
    Check 'nine conversions fill one ring' ($drawn -and (@($drawn.Groups[1].Value.Trim() -split ',').Count -eq 9)) "$($drawn.Value)"
    $labels = @($drawn.Groups[1].Value.Trim() -split ',')
    $target = SegmentPoint $centre $labels.Count 2
    GlideTo $target[0] $target[1]
    Start-Sleep -Milliseconds 400
    $null = $shots.Add((Shoot $centre 'shot-convert-9.png'))

    # The tools wheel has more entries than fit, so the last segment pages.
    [PeelGuard]::Tap([byte]0xA2)
    $tools = WaitLog 'wheel tool page 0: (.+)' 6000
    $toolLabels = @($tools.Groups[1].Value.Trim() -split ',')
    Check 'a long tools list pages' (($toolLabels.Count -eq 12) -and ($toolLabels[-1] -eq 'More')) "$($tools.Value)"
    $target = SegmentPoint $centre $toolLabels.Count 11
    GlideTo $target[0] $target[1]
    Start-Sleep -Milliseconds 400
    $null = $shots.Add((Shoot $centre 'shot-tools-page1.png'))

    # Resting on More turns the page.
    $paged = WaitLog 'wheel tool page 1: (.+)' 4000
    Check 'resting on More turns the page' ($paged -ne $null) "$($paged.Value)"
    if ($paged) {
        $pagedLabels = @($paged.Groups[1].Value.Trim() -split ',')
        $target = SegmentPoint $centre $pagedLabels.Count 1
        GlideTo $target[0] $target[1]
        Start-Sleep -Milliseconds 400
        $null = $shots.Add((Shoot $centre 'shot-tools-page2.png'))
    }

    [PeelGuard]::Tap([byte]0xA4)
    $actionsLine = WaitLog 'wheel action page 0: (.+)' 6000
    Check 'the actions wheel draws' ($actionsLine -ne $null) "$($actionsLine.Value)"
    if ($actionsLine) {
        $actionLabels = @($actionsLine.Groups[1].Value.Trim() -split ',')
        $target = SegmentPoint $centre $actionLabels.Count 0
        GlideTo $target[0] $target[1]
        Start-Sleep -Milliseconds 400
        $null = $shots.Add((Shoot $centre 'shot-actions.png'))
    }

    [PeelGuard]::Tap([byte]0x1B)
    Start-Sleep -Milliseconds 300
    [PeelGuard]::Up()
    Start-Sleep -Seconds 1
    Check 'nothing ran' (-not ((PeelLog) -match 'job start')) 'the fake engine refuses to run anything'
    Check 'no unhandled exceptions' (-not ((PeelLog) -match 'unhandled')) 'log has no unhandled exception'
}
catch {
    $script:GuiAbort = Get-GuiAbort $_
    if ($script:GuiAbort) { L "ABORTED: $_" } else { Check 'driver completed' $false "$_" }
}
finally {
    # First: end any drag and let go of every button and key (gui-guard.ps1).
    try { L (Stop-GuiGuard).Text } catch { L "teardown failed: $_" }
    try { L ("end: " + (Close-ShareSheets).Text) } catch { L "end: share sheet check failed: $_" }
    Restore-GuiCursor $originalCursor
    if ($peelProcess) { Stop-Process -Id $peelProcess.Id -ErrorAction SilentlyContinue }
    CloseTestExplorer
    $failed = @($checks | Where-Object { -not $_.ok })
    [pscustomobject]@{ ok = ($failed.Count -eq 0 -and -not $script:GuiAbort); aborted = $script:GuiAbort; checks = $checks; shots = $shots; log = $peelLog } |
        ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
    L ("done, {0} of {1} checks passed" -f ($checks.Count - $failed.Count), $checks.Count)
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}
