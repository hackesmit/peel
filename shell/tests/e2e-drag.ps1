<#
Peel end to end test: drive a real Explorer drag with SendInput, open the wheel
with a modifier tap, drop on a segment, and check that the engine wrote the
file.

This has to run in the interactive session (session 1). A process started over
ssh lands in session 0, where there is no desktop to drag on, so run it through
run-e2e.ps1, which schedules it with an Interactive principal.

Nothing about the registry is hard coded. The wheel's own log line
("wheel convert page 0: MD,PDF,...") lists the segments clockwise from twelve
o'clock, and the script works out the angle of the one it wants from that. Which
modifier opens which wheel comes from `peel options`, so when a family lands in
the engine the same script covers it.

Scenario A: drag the source file, tap the conversion modifier, drop on the named
            segment, expect the output next to the source.
Scenario B: drag again, tap every modifier, expect the wheel the registry says
            each one opens (or the log saying nothing is bound to it), then
            Escape, and expect nothing written.
Scenario C: drag again, open the wheel, release in the hub, expect the drop to
            fall through: no job runs and nothing is written.
#>
param(
    [string]$Dir = 'C:\peel-dev\e2e',
    [string]$App = 'C:\peel-dev\peel-4500-hq-plct-7\shell\Peel\bin\Release\net8.0-windows\Peel.exe',
    [string]$Peel = "$env:LOCALAPPDATA\Peel\venv\Scripts\peel.exe",
    [string]$Source = 'sample.txt',
    [string]$Segment = 'MD',
    [string]$Expect = 'sample.md',
    # gui-guard.ps1: wait for this much user idle before driving (0 when a
    # caller that already waited and drove runs this, as smoke.ps1 does).
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 1800,
    # The run folder a runner made for this run (gui-guard.ps1, Invoke-GuiTask).
    [string]$GuardDir = ''
)

$ErrorActionPreference = 'Stop'
$test = Join-Path $Dir 'testdir'
$driveLog = Join-Path $Dir 'drive.log'
$peelLog = Join-Path $Dir 'peel.log'
$shot = Join-Path $Dir 'shot-wheel.png'
$resultPath = Join-Path $Dir 'result.json'
$checks = [System.Collections.ArrayList]::new()
$script:cursor = @(0, 0)
$script:GuiAbort = $null

New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item $driveLog, $peelLog, $shot, $resultPath -ErrorAction SilentlyContinue
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
        Assert-NoUserInput
        Start-Sleep -Milliseconds 100
    }
    return $null
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class In {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
'@
[void][In]::SetProcessDPIAware()
# Idle wait, real input abort, and the teardown that ends any drag.
. (Join-Path $PSScriptRoot 'gui-guard.ps1')

$modifierKeys = @{ 'Shift' = [byte]0xA0; 'Ctrl' = [byte]0xA2; 'Alt' = [byte]0xA4 }
$VkEsc = [byte]0x1B

function MoveTo($x, $y) {
    Assert-NoUserInput
    [PeelGuard]::Move([int]$x, [int]$y)
    $script:cursor = @([int]$x, [int]$y)
}

function Jiggle($times = 4) {
    for ($i = 1; $i -le $times; $i++) {
        MoveTo ($script:cursor[0] + 2) ($script:cursor[1] + 2)
        Start-Sleep -Milliseconds 70
    }
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
            Where-Object { $_.LocationURL -like '*peel-dev/e2e/testdir*' } |
            ForEach-Object { $_.Quit() }
    } catch { }
}

function FindItem($name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    # Explorer hides known extensions, so look for both spellings.
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
    if (-not (Confirm-GrabPoint $item $x $y)) { throw "another window stays over the test Explorer at $x,$y" }
    [void][PeelGuard]::Place($x, $y); Start-Sleep -Milliseconds 250
    MoveTo $x $y; Start-Sleep -Milliseconds 120
    [PeelGuard]::Down(); Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 14; $i++) {
        MoveTo ($x + $i * 14) ($y + $i * 7)
        Start-Sleep -Milliseconds 30
    }
}

function ApproachRing($centre) {
    # A hand aiming at a segment moves out of the hub and onto the ring, which
    # is when OLE offers the drag data to the overlay. A pointer that never
    # leaves the hub never enters the window, by design: the hub is a hole.
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
    $radius = 111.0   # midway between the hub (58) and the rim (164), in pixels at 100 percent
    return @([int]($centre[0] + [Math]::Sin($bearing) * $radius),
             [int]($centre[1] - [Math]::Cos($bearing) * $radius))
}

$peelProcess = $null
$originalCursor = [System.Windows.Forms.Cursor]::Position
try {
    . (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')
    Set-Content -Path (Join-Path $test $Source) -Value "Peel end to end test`r`nline two`r`n" -Encoding UTF8
    L "source ready: $test\$Source"

    # The wheel can only offer what the engine offers, and which key opens
    # which wheel is the engine's own default_modifier. Read both, so this test
    # keeps telling the truth as the registry grows.
    $options = & $Peel options (Join-Path $test $Source) | ConvertFrom-Json
    $registry = & $Peel options | ConvertFrom-Json
    $convert = $options.wheels | Where-Object { $_.family -eq 'convert' }
    $offered = @($convert.groups | ForEach-Object { $_.entries } | ForEach-Object { $_.label })
    Check 'engine offers the segment' ($offered -contains $Segment) "peel options convert wheel: $($offered -join ',')"
    if ($offered -notcontains $Segment) { throw "the engine does not offer a $Segment conversion for $Source" }
    $bound = @{}
    foreach ($wheel in $registry.wheels) { $bound[[string]$wheel.default_modifier] = [string]$wheel.family }
    L "registry wheels: $(($bound.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')"

    Start-GuiGuard -Dir $Dir -RunDir $GuardDir -PauseInstalledFor $App -IdleSeconds $IdleSeconds -IdleTimeout $IdleTimeout -Log { param($m) L $m }
    # Note the share sheets already on screen: they are not this run's, so
    # Close-ShareSheets leaves them alone (e2e-hygiene.ps1).
    L ("start: " + (Register-ShareSheets).Text)
    $originalCursor = [System.Windows.Forms.Cursor]::Position

    $env:PEEL_LOG = $peelLog
    $env:PEEL_EXIT_AFTER = '240'
    $peelProcess = Start-Process $App -PassThru
    L "started $App as pid $($peelProcess.Id)"
    Check 'app starts' ((WaitLog 'peel shell start' 15000) -ne $null) 'peel shell start in the log'
    Check 'registry preloads' ((WaitLog 'registry preloaded' 20000) -ne $null) 'registry preloaded in the log'

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3
    $item = FindItem $Source
    if (-not $item) { throw "$Source is not visible in Explorer" }
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })

    # Scenario A: open the conversion wheel and drop on a segment.
    StartDragFrom $item
    Check 'drag detected' ((WaitLog 'drag start at' 4000) -ne $null) 'hook saw the drag'

    [PeelGuard]::Tap($modifierKeys['Shift'])
    $shown = WaitLog 'wheel shown at (\d+),(\d+)' 4000
    Check 'wheel opens on the modifier tap' ($shown -ne $null) "$($shown.Value)"
    if (-not $shown) { throw 'the wheel never opened' }
    $centre = @([int]$shown.Groups[1].Value, [int]$shown.Groups[2].Value)

    # OLE offers the drag data only once the pointer is over the window, and
    # the hub is a hole, so the pointer has to reach the ring first.
    ApproachRing $centre
    $files = WaitLog 'drag carries (\d+) file' 4000
    Check 'wheel reads the dragged files' ($files -ne $null) "$($files.Value)"
    Check 'hub is cut out of the window' ((WaitLog 'hub hole cut=True' 3000) -ne $null) 'SetWindowRgn made the hub a hole'

    $drawn = WaitLog 'wheel convert page 0: (.+)' 5000
    Check 'conversion wheel is drawn from the engine' ($drawn -ne $null) "$($drawn.Value)"
    if (-not $drawn) { throw 'the conversion wheel never drew its segments' }
    $labels = @($drawn.Groups[1].Value.Trim() -split ',')
    $target = SegmentPoint $centre $labels $Segment
    if (-not $target) { throw "segment $Segment is not on the wheel: $($labels -join ',')" }
    L "aiming at $Segment, index $([array]::IndexOf($labels, $Segment)) of $($labels.Count), point $($target[0]),$($target[1])"

    GlideTo $target[0] $target[1]
    Start-Sleep -Milliseconds 400

    $bitmap = New-Object System.Drawing.Bitmap 460, 460
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($centre[0] - 230, $centre[1] - 230, 0, 0, $bitmap.Size)
    $bitmap.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Check 'wheel screenshot taken' (Test-Path $shot) $shot

    [PeelGuard]::Up()
    $drop = WaitLog "drop on $Segment with (\d+) file" 6000
    Check 'drop lands on the segment' ($drop -ne $null) "$($drop.Value)"

    $expected = Join-Path $test $Expect
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $expected) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Check 'the engine wrote the output' (Test-Path $expected) $expected

    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'exactly one new file' ($new.Count -eq 1) "new files: $($new -join ',')"

    # Scenario B: every modifier, then Escape.
    Start-Sleep -Milliseconds 800
    $item = FindItem $Source
    if (-not $item) { throw "$Source vanished from Explorer" }
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    StartDragFrom $item
    Check 'second drag detected' ((WaitLog 'drag start at' 4000 2) -ne $null) 'hook saw the second drag'

    $centreB = $script:cursor
    foreach ($modifier in @('Shift', 'Ctrl', 'Alt')) {
        $family = $bound[$modifier]
        $pattern = if ($family) { "wheel $family page 0: (.+)" } else { "no wheel is bound to $modifier" }
        # Count the matches already in the log: every wait below is for a line
        # this tap produces, never one an earlier scenario left behind.
        $seen = CountLog $pattern
        [PeelGuard]::Tap($modifierKeys[$modifier])
        ApproachRing $centreB
        $line = WaitLog $pattern 6000 ($seen + 1)
        if ($family) {
            Check "$modifier opens the $family wheel" ($line -ne $null) "$($line.Value)"
        }
        else {
            Check "$modifier has no wheel in this registry" ($line -ne $null) "$($line.Value)"
        }
    }

    [PeelGuard]::Tap($VkEsc)
    $cancelled = WaitLog 'wheel hidden \(escape\)' 4000
    Check 'Escape closes the wheel' ($cancelled -ne $null) "$($cancelled.Value)"

    GlideTo ($script:cursor[0] + 30) ($script:cursor[1] + 60) 4
    [PeelGuard]::Up()
    Start-Sleep -Seconds 2
    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'a cancelled drag writes nothing' ($new.Count -eq 0) "new files: $($new -join ',')"

    # Scenario C: release in the hub. The hub is a hole in the window, so the
    # drop belongs to whatever is underneath and Peel must not run anything.
    Start-Sleep -Milliseconds 800
    $item = FindItem $Source
    if (-not $item) { throw "$Source vanished from Explorer" }
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $dropsBefore = CountLog 'drop on '
    $shownSeen = CountLog 'wheel shown at'
    $drawnSeen = CountLog 'wheel convert page 0: '
    StartDragFrom $item
    [PeelGuard]::Tap($modifierKeys['Shift'])
    $shown = WaitLog 'wheel shown at (\d+),(\d+)' 4000 ($shownSeen + 1)
    Check 'wheel opens again' ($shown -ne $null) "$($shown.Value)"
    if ($shown) {
        $centre = @([int]$shown.Groups[1].Value, [int]$shown.Groups[2].Value)
        ApproachRing $centre
        $drawn = WaitLog 'wheel convert page 0: (.+)' 6000 ($drawnSeen + 1)
        Check 'wheel is loaded before the hub drop' ($drawn -ne $null) "$($drawn.Value)"
        GlideTo $centre[0] $centre[1] 4
    }
    Start-Sleep -Milliseconds 300
    [PeelGuard]::Up()
    Start-Sleep -Seconds 3
    Check 'a drop in the hub runs nothing' ((CountLog 'drop on ') -eq $dropsBefore) "drops seen: $(CountLog 'drop on ') (was $dropsBefore)"
    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'a drop in the hub writes nothing' ($new.Count -eq 0) "new files: $($new -join ',')"

    # Scenario D: Peel dies mid drag. The constraint is that a fault in the
    # tray app must never cost the user a drag, so the drop has to go through
    # to Explorer as if Peel had never been there.
    Start-Sleep -Milliseconds 800
    New-Item -ItemType Directory -Force (Join-Path $test 'sink') | Out-Null
    Start-Sleep -Milliseconds 1200
    $item = FindItem $Source
    $sink = FindItem 'sink'
    if (-not $item -or -not $sink) { throw 'the source or the sink folder is not visible in Explorer' }
    $sinkRect = $sink.Current.BoundingRectangle
    StartDragFrom $item
    $shownSeen = CountLog 'wheel shown at'
    [PeelGuard]::Tap($modifierKeys['Shift'])
    $shown = WaitLog 'wheel shown at (\d+),(\d+)' 4000 ($shownSeen + 1)
    if ($shown) { ApproachRing @([int]$shown.Groups[1].Value, [int]$shown.Groups[2].Value) }
    Stop-Process -Id $peelProcess.Id -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
    Check 'the app is gone' ((Get-Process -Id $peelProcess.Id -ErrorAction SilentlyContinue) -eq $null) 'Peel.exe was killed mid drag'
    GlideTo ([int]($sinkRect.X + $sinkRect.Width / 2)) ([int]($sinkRect.Y + $sinkRect.Height / 2)) 10
    Start-Sleep -Milliseconds 500
    [PeelGuard]::Up()
    $moved = Join-Path (Join-Path $test 'sink') $Source
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-Path $moved) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Check 'a crash mid drag leaves the drop to Explorer' (Test-Path $moved) $moved

    # The hook budget. The app reports its worst callback whenever it grows.
    $text = PeelLog
    $worst = [regex]::Matches($text, 'worst hook call ([0-9.]+)ms') |
        ForEach-Object { [double]$_.Groups[1].Value } |
        Measure-Object -Maximum
    $worstMs = if ($worst.Count -gt 0) { $worst.Maximum } else { -1 }
    Check 'hook callbacks stay under 10 ms' (($worstMs -ge 0) -and ($worstMs -lt 10)) "worst hook call ${worstMs}ms"
    Check 'no unhandled exceptions' (-not ($text -match 'unhandled')) 'log has no unhandled exception'
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
    $summary = [pscustomobject]@{
        ok      = ($failed.Count -eq 0 -and -not $script:GuiAbort)
        aborted = $script:GuiAbort
        checks  = $checks
        log    = $peelLog
        shot   = $shot
        when   = (Get-Date).ToString('o')
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
    L ("done, {0} of {1} checks passed" -f ($checks.Count - $failed.Count), $checks.Count)
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}
