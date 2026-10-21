<#
Run a Peel GUI test in the interactive desktop session and report the result.

Processes started over ssh live in session 0, which has no desktop: a drag
driven from there moves nothing and the tray app cannot show a window. A
scheduled task with an Interactive principal runs in the logged on user's
session instead, which is where these tests have to happen.

From hqbox:
  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\run-e2e.ps1"
  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\run-e2e.ps1 -Test render"

drag:    the real thing. Explorer drag, modifier tap, drop on a segment, and the
         engine writes the file. Uses the installed engine, so the segment it
         aims at has to exist in the registry on this machine.
render:  the wheel drawing and its paging, against the fake engine in
         tests\FakeEngine. Writes screenshots and runs nothing.
editors: each image editor, opened from the tools wheel by a real drag, driven
         with UI Automation, applied, and its output checked pixel by pixel or
         tag by tag. Uses the installed engine and its venv python.
keyboard: the keyboard path. Select a file in Explorer, press the global hotkey
         (Ctrl+Alt+Enter), drive the highlight with the arrow keys, press Enter,
         and the engine writes the file. No drag, no mouse past the first click.
abort-fail, abort-kill: the GUI guard itself (gui-guard.ps1, e2e-drag-abort.ps1).
         A real Explorer drag is held and then the driver throws (fail) or is
         killed by this runner after -TimeoutSeconds of driving (kill, use a
         short one such as 20). Passes only when no SysDragImage window and no
         held button is left, with a screenshot after the teardown.

This runner takes C:\peel-dev\gui.lock itself (do not create it first: it
would wait on you) and holds it until the teardown and the restart of the
installed Peel are done. Every driver waits for -IdleSeconds of user idle
(GetLastInputInfo, default 300) before it touches the mouse, and gives up
after -IdleTimeout: RESULT ABORTED (user busy), exit 3, with nothing driven.
Once it drives, any input without the injected flag (a person) aborts it:
RESULT ABORTED (user input), exit 3, which is not a test failure either.
-TimeoutSeconds counts from the moment the driver starts to drive
(driving.txt). Whatever happens, the driver's finally, its watchdog, or this
runner ends the drag (gui-guard.ps1), and only one of them does. A driver
pauses Daniel's installed Peel while it drives a dev build; this runner starts
it again at the end if it ran before.

Daniel has to be logged in at the console. If the session is locked, Explorer
may not be visible to UI Automation, and the run reports that rather than
guessing.
#>
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [ValidateSet('drag', 'render', 'editors', 'keyboard', 'abort-fail', 'abort-kill')]
    [string]$Test = 'drag',
    [string]$Source = 'sample.txt',
    [string]$Segment = 'MD',
    [string]$Expect = 'sample.md',
    [int]$TimeoutSeconds = 900,
    # editors only: the peel.exe to test against, when the installed engine is
    # older than the branch. The python beside it runs the pixel checks.
    [string]$Engine = '',
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 1800
)

$ErrorActionPreference = 'Stop'
# Pick the newest build by target framework version (e2e-hygiene.ps1), never
# the first folder alphabetically: a dev copy that still holds an old
# net8.0-windows build would otherwise launch that stale exe silently.
. (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')
. (Join-Path $PSScriptRoot 'gui-guard.ps1')
$app = Find-PeelApp $Root
# The abort proofs drag in Explorer only; every other test needs the shell.
if (-not $app -and $Test -notlike 'abort-*') { throw "build the shell first (dotnet build -c Release): no Peel.exe under shell\Peel\bin\Release\net8.0-windows*" }

if ($Test -eq 'drag') {
    $script = Join-Path $Root 'shell\tests\e2e-drag.ps1'
    $dir = 'C:\peel-dev\e2e'
    $arguments = '-Dir "{0}" -App "{1}" -Source "{2}" -Segment "{3}" -Expect "{4}"' -f $dir, $app, $Source, $Segment, $Expect
}
elseif ($Test -eq 'editors') {
    $script = Join-Path $Root 'shell\tests\e2e-editors.ps1'
    $dir = 'C:\peel-dev\e2e-editors'
    $check = Join-Path $Root 'shell\tests\e2e-editors-check.py'
    $arguments = '-Dir "{0}" -App "{1}" -Check "{2}"' -f $dir, $app, $check
    if ($Engine) { $arguments += ' -Peel "{0}"' -f $Engine }
}
elseif ($Test -eq 'keyboard') {
    $script = Join-Path $Root 'shell\tests\e2e-keyboard.ps1'
    $dir = 'C:\peel-dev\e2e-keyboard'
    $arguments = '-Dir "{0}" -App "{1}" -Source "{2}" -Segment "{3}" -Expect "{4}"' -f $dir, $app, $Source, $Segment, $Expect
}
elseif ($Test -like 'abort-*') {
    $script = Join-Path $Root 'shell\tests\e2e-drag-abort.ps1'
    $dir = 'C:\peel-dev\e2e-abort'
    $arguments = '-Dir "{0}" -Mode {1}' -f $dir, ($Test -replace '^abort-', '')
}
else {
    $script = Join-Path $Root 'shell\tests\e2e-wheel-render.ps1'
    $dir = 'C:\peel-dev\e2e-render'
    $fakeProject = Join-Path $Root 'shell\tests\FakeEngine'
    $fake = Join-Path $fakeProject 'bin\Release\net8.0\FakeEngine.exe'
    if (-not (Test-Path $fake)) {
        Write-Output 'building the fake engine'
        & dotnet build $fakeProject -c Release -v q --nologo | Out-Null
    }
    $options = Join-Path $Root 'shell\tests\fake-options.json'
    $arguments = '-Dir "{0}" -App "{1}" -Fake "{2}" -Options "{3}"' -f $dir, $app, $fake, $options
}
if (-not (Test-Path $script)) { throw "driver not found: $script" }
$arguments += ' -IdleSeconds {0} -IdleTimeout {1}' -f $IdleSeconds, $IdleTimeout

$resultPath = Join-Path $dir 'result.json'
New-Item -ItemType Directory -Force $dir | Out-Null

# Runs the driver in the desktop session and ends its drag whatever happens:
# the driver's finally, its watchdog, or a teardown from here (gui-guard.ps1).
# All of it inside gui.lock, which also covers stopping a test Peel the driver
# left and the restart of Daniel's Peel.
try {
    $run = Invoke-GuiDriver -Name 'PeelE2E' -Dir $dir -ResultPath $resultPath -App $app -TimeoutSeconds $TimeoutSeconds -IdleTimeout $IdleTimeout `
        -Arguments ('-NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f $script, $arguments)
}
catch {
    # gui.lock stayed held by another run: nothing was scheduled or driven.
    if (-not (Get-GuiAbort $_)) { throw }
    Write-Output "RESULT: ABORTED ($(Get-GuiAbort $_)), not a test failure: $_"
    exit 3
}
$timedOut = $run.TimedOut
$teardown = $run.Teardown

$driveLog = Join-Path $dir 'drive.log'
if (Test-Path $driveLog) {
    Write-Output '--- drive.log ---'
    Get-Content $driveLog | Select-Object -Last 40
}
if ($Test -eq 'abort-kill') {
    # The run under test is the kill: no result.json is expected, the drag
    # image and the button must be gone after the watchdog and the runner.
    $mid = Join-Path $run.RunDir 'shot-mid-drag.png'
    $holding = Test-Path (Join-Path $run.RunDir 'holding.txt')
    $watchdogPath = Join-Path $run.RunDir 'teardown-watchdog.json'
    $watchdog = if (Test-Path $watchdogPath) { Get-Content $watchdogPath -Raw | ConvertFrom-Json } else { $null }
    $state = Join-Path $run.RunDir 'guard-state.watchdog.json'
    $state = if (Test-Path $state) { Get-Content $state -Raw | ConvertFrom-Json } else { $null }
    $ok = $timedOut -and $holding -and (Test-Path $mid) -and $teardown -and $teardown.dragImagesLeft -eq 0 -and -not $teardown.stillDown -and
        $watchdog -and $watchdog.claimed -and -not $teardown.claimed -and $state -and $state.done -and @($state.owned).Count -eq 0
    Write-Output '--- checks ---'
    Write-Output ("{0} the driver was killed while it held the drag: timed out {1}, holding.txt {2}, {3}" -f $(if ($timedOut -and $holding -and (Test-Path $mid)) { 'PASS' } else { 'FAIL' }), $timedOut, $holding, $mid)
    if ($watchdog) {
        Write-Output ("{0} one owner tore down: the watchdog claimed the state ({1}), the runner did not ({2}); state done {3}, still owned '{4}'" -f $(if ($watchdog.claimed -and -not $teardown.claimed -and $state.done -and @($state.owned).Count -eq 0) { 'PASS' } else { 'FAIL' }), $watchdog.actions, $(if ($teardown) { $teardown.actions } else { 'no report' }), $state.done, (@($state.owned) -join ','))
    }
    if ($teardown) {
        Write-Output ("{0} no drag image and no button left after the kill: images left {1}, still down '{2}', screenshot {3}" -f $(if ($teardown.dragImagesLeft -eq 0 -and -not $teardown.stillDown) { 'PASS' } else { 'FAIL' }), $teardown.dragImagesLeft, $teardown.stillDown, $teardown.shot)
    }
    Write-Output ("RESULT: {0}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }))
    if ($ok) { exit 0 } else { exit 1 }
}
if (-not (Test-Path $resultPath)) {
    Write-Output 'RESULT: no result.json, the driver did not finish'
    exit 1
}
$result = Get-Content $resultPath -Raw | ConvertFrom-Json
Write-Output '--- checks ---'
foreach ($check in $result.checks) {
    Write-Output ("{0} {1}: {2}" -f $(if ($check.ok) { 'PASS' } else { 'FAIL' }), $check.name, $check.detail)
}
if ($null -ne $result.injections) {
    Write-Output ("guard: drove {0}, injections {1}, cleanup inputs {2}, inject.log in {3}" -f $result.drove, $result.injections, $result.cleanups, $result.runDir)
}
if ($result.aborted) {
    Write-Output "RESULT: ABORTED ($($result.aborted)), not a test failure: run it again when George is idle"
    exit 3
}
Write-Output ("RESULT: {0}" -f $(if ($result.ok) { 'PASS' } else { 'FAIL' }))
if (-not $result.ok) { exit 1 }
exit 0
