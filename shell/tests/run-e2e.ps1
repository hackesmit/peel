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

Daniel has to be logged in at the console. If the session is locked, Explorer
may not be visible to UI Automation, and the run reports that rather than
guessing.
#>
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [ValidateSet('drag', 'render', 'editors', 'keyboard')]
    [string]$Test = 'drag',
    [string]$Source = 'sample.txt',
    [string]$Segment = 'MD',
    [string]$Expect = 'sample.md',
    [int]$TimeoutSeconds = 900,
    # editors only: the peel.exe to test against, when the installed engine is
    # older than the branch. The python beside it runs the pixel checks.
    [string]$Engine = ''
)

$ErrorActionPreference = 'Stop'
# Pick the newest build by target framework version (e2e-hygiene.ps1), never
# the first folder alphabetically: a dev copy that still holds an old
# net8.0-windows build would otherwise launch that stale exe silently.
. (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')
$app = Find-PeelApp $Root
if (-not $app) { throw "build the shell first (dotnet build -c Release): no Peel.exe under shell\Peel\bin\Release\net8.0-windows*" }

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

$taskName = 'PeelE2E'
$resultPath = Join-Path $dir 'result.json'
New-Item -ItemType Directory -Force $dir | Out-Null
Remove-Item $resultPath -ErrorAction SilentlyContinue

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f $script, $arguments)
# $env:USERDOMAIN is WORKGROUP on George, which is not an account domain.
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Seconds 3
        $state = (Get-ScheduledTask -TaskName $taskName).State
    } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
    if ($state -eq 'Running') {
        Stop-ScheduledTask -TaskName $taskName
        Write-Output "TIMEOUT after $TimeoutSeconds seconds"
    }
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
}

$driveLog = Join-Path $dir 'drive.log'
if (Test-Path $driveLog) {
    Write-Output '--- drive.log ---'
    Get-Content $driveLog | Select-Object -Last 40
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
Write-Output ("RESULT: {0}" -f $(if ($result.ok) { 'PASS' } else { 'FAIL' }))
if (-not $result.ok) { exit 1 }
exit 0
