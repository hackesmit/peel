<#
Peel keyboard-path end to end test: select a file in Explorer, press the global
hotkey, steer the wheel's highlight with the arrow keys, press Enter, and check
that the engine wrote the file.

This has to run in the interactive session (session 1): a process started over
ssh lands in session 0, where there is no desktop and no foreground Explorer, so
run it through run-e2e.ps1 -Test keyboard, which schedules it with an
Interactive principal.

Nothing about the registry is hard coded. The wheel's own log line
("keyboard wheel convert page 0: MD,PDF,...") lists the segments clockwise from
twelve o'clock, and "keyboard highlight <label>" says where the highlight rests;
the script taps an arrow key until the highlight is the segment it wants, then
Enter. When a family lands in the engine the same script covers it.

Scenario A: select the source, press the hotkey, arrow to the named segment,
            Enter, expect the output next to the source.
Scenario B: select again, press the hotkey, press Escape, expect the wheel gone
            and nothing written, and focus back on Explorer.
Scenario C: press the hotkey and click the file at once (the wheel may never
            get the keyboard), then press the hotkey again: the wheel opens
            again, so the hotkey is never left dead. Escape closes it.
Scenario D: press the hotkey and touch nothing: the wheel closes on its own
            after the idle timeout (PEEL_KEYBOARD_IDLE_MS, 8 s here).

The only mouse action is one click to select the file and give Explorer focus;
there is no drag, so nothing is ever dropped.
#>
param(
    [string]$Dir = 'C:\peel-dev\e2e-keyboard',
    [string]$App = 'C:\peel-dev\peel-5f8a-hq-plct-8\shell\Peel\bin\Release\net8.0-windows\Peel.exe',
    [string]$Peel = "$env:LOCALAPPDATA\Peel\venv\Scripts\peel.exe",
    [string]$Source = 'sample.txt',
    [string]$Segment = 'MD',
    [string]$Expect = 'sample.md',
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
$script:GuiAbort = $null

New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item $driveLog, $peelLog, $resultPath -ErrorAction SilentlyContinue
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

function Highlight() {
    $m = [regex]::Matches((PeelLog), 'keyboard highlight (.+)')
    if ($m.Count -gt 0) { return $m[$m.Count - 1].Groups[1].Value.Trim() }
    return $null
}

Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class In {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  public static string FgClass() { var c = new System.Text.StringBuilder(256); GetClassName(GetForegroundWindow(), c, 256); return c.ToString(); }
}
'@
[void][In]::SetProcessDPIAware()
# Idle wait, real input abort, and the teardown that ends any drag.
. (Join-Path $PSScriptRoot 'gui-guard.ps1')

$VkRight = [byte]0x27
$VkEnter = [byte]0x0D
$VkEsc = [byte]0x1B

function CloseTestExplorer() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $_.LocationURL -like '*peel-dev/e2e-keyboard/testdir*' } |
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

# One click: select the file and make its Explorer window the foreground one,
# which is the window the hotkey reads the selection from. No drag.
function SelectItem($item) {
    $r = $item.Current.BoundingRectangle
    $x = [int]($r.X + 24); $y = [int]($r.Y + $r.Height / 2)
    L "click to select at $x,$y"
    [void][PeelGuard]::Place($x, $y); Start-Sleep -Milliseconds 150
    [PeelGuard]::Down(); Start-Sleep -Milliseconds 70; [PeelGuard]::Up()
    Start-Sleep -Milliseconds 350
}

# Tap an arrow until the highlight is the wanted segment. Each wait is for a
# highlight line this loop produced, never one an earlier step left behind.
function AimAt($target, $labels) {
    for ($i = 0; $i -lt ($labels.Count + 2); $i++) {
        if ((Highlight) -eq $target) { return }
        $seen = CountLog 'keyboard highlight '
        [PeelGuard]::TapEx($VkRight, $true)
        WaitLog 'keyboard highlight ' 3000 ($seen + 1) | Out-Null
    }
}

$peelProcess = $null
$originalCursor = [System.Windows.Forms.Cursor]::Position
try {
    . (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')
    Set-Content -Path (Join-Path $test $Source) -Value "Peel keyboard test`r`nline two`r`n" -Encoding UTF8
    L "source ready: $test\$Source"

    # The wheel can only offer what the engine offers; read it so this test
    # keeps telling the truth as the registry grows.
    $options = & $Peel options (Join-Path $test $Source) | ConvertFrom-Json
    $convert = $options.wheels | Where-Object { $_.family -eq 'convert' }
    $offered = @($convert.groups | ForEach-Object { $_.entries } | ForEach-Object { $_.label })
    Check 'engine offers the segment' ($offered -contains $Segment) "peel options convert wheel: $($offered -join ',')"
    if ($offered -notcontains $Segment) { throw "the engine does not offer a $Segment conversion for $Source" }

    Start-GuiGuard -Dir $Dir -RunDir $GuardDir -PauseInstalledFor $App -IdleSeconds $IdleSeconds -IdleTimeout $IdleTimeout -Log { param($m) L $m }
    # Note the share sheets already on screen: they are not this run's, so
    # Close-ShareSheets leaves them alone (e2e-hygiene.ps1).
    L ("start: " + (Register-ShareSheets).Text)
    $originalCursor = [System.Windows.Forms.Cursor]::Position

    $env:PEEL_LOG = $peelLog
    $env:PEEL_EXIT_AFTER = '240'
    $env:PEEL_KEYBOARD_IDLE_MS = '8000'
    $peelProcess = Start-Process $App -PassThru
    L "started $App as pid $($peelProcess.Id)"
    Check 'app starts' ((WaitLog 'peel shell start' 15000) -ne $null) 'peel shell start in the log'
    Check 'hotkey registers' ((WaitLog 'keyboard hotkey registered' 15000) -ne $null) 'Ctrl+Alt+Enter registered'
    Check 'registry preloads' ((WaitLog 'registry preloaded' 20000) -ne $null) 'registry preloaded in the log'

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3
    $item = FindItem $Source
    if (-not $item) { throw "$Source is not visible in Explorer" }
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })

    # Scenario A: select, hotkey, arrow to the segment, Enter.
    SelectItem $item
    [PeelGuard]::Hotkey()
    $shown = WaitLog 'keyboard wheel shown at (\d+),(\d+)' 6000
    Check 'hotkey opens the wheel' ($shown -ne $null) "$($shown.Value)"
    if (-not $shown) { throw 'the keyboard wheel never opened' }

    $drawn = WaitLog 'keyboard wheel convert page 0: (.+)' 6000
    Check 'conversion wheel is drawn from the engine' ($drawn -ne $null) "$($drawn.Value)"
    if (-not $drawn) { throw 'the conversion wheel never drew its segments' }
    $labels = @($drawn.Groups[1].Value.Trim() -split ',')
    if ($labels -notcontains $Segment) { throw "segment $Segment is not on the wheel: $($labels -join ',')" }

    # An arrow key reaches the focused wheel and drives the highlight. On a
    # one-segment wheel the highlight wraps back to the same segment; the point
    # here is that the key is plumbed through to the navigator (the movement
    # logic itself is pinned by KeyboardNavigatorTests). A busier registry makes
    # AimAt below step across real segments.
    $seen = CountLog 'keyboard highlight '
    [PeelGuard]::TapEx($VkRight, $true)
    $moved = WaitLog 'keyboard highlight ' 3000 ($seen + 1)
    Check 'an arrow key drives the highlight' ($moved -ne $null) "highlight after Right: $($moved.Value)"

    AimAt $Segment $labels
    Check 'arrow keys reach the segment' ((Highlight) -eq $Segment) "highlight=$(Highlight), target=$Segment"

    [PeelGuard]::Tap($VkEnter)
    $ran = WaitLog 'keyboard run (\S+) on (\d+) file' 6000
    Check 'Enter runs the highlighted segment' ($ran -ne $null) "$($ran.Value)"

    $expected = Join-Path $test $Expect
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $expected) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Check 'the engine wrote the output' (Test-Path $expected) $expected

    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'exactly one new file' ($new.Count -eq 1) "new files: $($new -join ',')"

    # Scenario B: open again, Escape, nothing runs.
    Start-Sleep -Milliseconds 800
    $item = FindItem $Source
    if (-not $item) { throw "$Source vanished from Explorer" }
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $runsBefore = CountLog 'keyboard run '
    $shownSeen = CountLog 'keyboard wheel shown at'
    SelectItem $item
    [PeelGuard]::Hotkey()
    $shown = WaitLog 'keyboard wheel shown at (\d+),(\d+)' 6000 ($shownSeen + 1)
    Check 'hotkey opens the wheel again' ($shown -ne $null) "$($shown.Value)"
    WaitLog 'keyboard wheel convert page 0: (.+)' 6000 | Out-Null
    [PeelGuard]::Tap($VkEsc)
    $cancelled = WaitLog 'keyboard wheel hidden \(escape\)' 4000
    Check 'Escape closes the wheel' ($cancelled -ne $null) "$($cancelled.Value)"
    Start-Sleep -Seconds 2
    Check 'Escape runs nothing' ((CountLog 'keyboard run ') -eq $runsBefore) "runs seen: $(CountLog 'keyboard run ') (was $runsBefore)"
    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'a cancelled hotkey writes nothing' ($new.Count -eq 0) "new files: $($new -join ',')"

    Start-Sleep -Milliseconds 600
    Check 'Escape returns focus to Explorer' ([In]::FgClass() -eq 'CabinetWClass') "foreground class after Escape: $([In]::FgClass())"

    # Scenario C: hotkey, then a click on Explorer at once, then the hotkey again.
    $item = FindItem $Source
    if (-not $item) { throw "$Source vanished from Explorer" }
    SelectItem $item
    $shownSeen = CountLog 'keyboard wheel shown at'
    [PeelGuard]::Hotkey()
    [PeelGuard]::Click()
    L 'clicked Explorer right after the hotkey'
    $shown = WaitLog 'keyboard wheel shown at (\d+),(\d+)' 6000 ($shownSeen + 1)
    Start-Sleep -Milliseconds 1200
    $visibleAfterClick = (CountLog 'keyboard wheel shown at') -gt (CountLog 'keyboard wheel hidden')
    L ("after the click: shown=$($shown -ne $null) still open=$visibleAfterClick fg=$([In]::FgClass())")
    Check 'an unfocused wheel does not stay open' ((-not $visibleAfterClick) -or ([In]::FgClass() -like 'HwndWrapper*')) "still open=$visibleAfterClick fg=$([In]::FgClass())"
    $item = FindItem $Source
    if ($item -and [In]::FgClass() -ne 'CabinetWClass') { SelectItem $item }
    $shownSeen = CountLog 'keyboard wheel shown at'
    [PeelGuard]::Hotkey()
    $again = WaitLog 'keyboard wheel shown at (\d+),(\d+)' 6000 ($shownSeen + 1)
    Check 'the hotkey opens the wheel again after a click' ($again -ne $null) "$($again.Value)"
    Check 'no press is ignored' ((CountLog 'ignored') -eq 0) "ignored lines: $(CountLog 'ignored')"
    WaitLog 'keyboard wheel convert page 0: (.+)' 6000 | Out-Null
    $hiddenSeen = CountLog 'keyboard wheel hidden \(escape\)'
    [PeelGuard]::Tap($VkEsc)
    $closed = WaitLog 'keyboard wheel hidden \(escape\)' 4000 ($hiddenSeen + 1)
    Check 'Escape closes the reopened wheel' ($closed -ne $null) "$($closed.Value)"

    # Scenario D: the idle timeout.
    Start-Sleep -Milliseconds 600
    $item = FindItem $Source
    if (-not $item) { throw "$Source vanished from Explorer" }
    SelectItem $item
    $shownSeen = CountLog 'keyboard wheel shown at'
    [PeelGuard]::Hotkey()
    $shown = WaitLog 'keyboard wheel shown at (\d+),(\d+)' 6000 ($shownSeen + 1)
    $timedOut = WaitLog 'keyboard wheel hidden \(timeout\)' 14000
    Check 'an idle wheel closes after the timeout' (($shown -ne $null) -and ($timedOut -ne $null)) "$($timedOut.Value)"

    $text = PeelLog
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
        when   = (Get-Date).ToString('o')
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
    L ("done, {0} of {1} checks passed" -f ($checks.Count - $failed.Count), $checks.Count)
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}
