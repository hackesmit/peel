<#
Run the Alt wheel action drag test (e2e-actions-drag.ps1) in the interactive
desktop session and report the result. A drag and the share sheet need a real
desktop; a process started over ssh lands in session 0, which has none. This
schedules a task with an Interactive principal, the same way run-e2e.ps1 does.

Daniel has to be logged in at the console. The caller takes C:\peel-dev\gui.lock
before running this (three peel workers share the real mouse) and removes it
after.

From hqbox:
  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\run-actions-drag.ps1"
#>
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    # The drive budget, counted from the end of the idle wait (driving.txt).
    [int]$TimeoutSeconds = 300,
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
if (-not $app) { throw "build the shell first (dotnet build -c Release): no Peel.exe under shell\Peel\bin\Release\net8.0-windows*" }

$script = Join-Path $Root 'shell\tests\e2e-actions-drag.ps1'
if (-not (Test-Path $script)) { throw "driver not found: $script" }
$dir = 'C:\peel-dev\e2e-actions'
$resultPath = Join-Path $dir 'result.json'
New-Item -ItemType Directory -Force $dir | Out-Null
Remove-Item $resultPath -ErrorAction SilentlyContinue

# The dev engine built in engine\.venv (pip install --no-deps -e .), which has
# the five new shell actions. Falls back to the installed engine if absent.
$engine = Join-Path $Root 'engine\.venv\Scripts\peel.exe'
$engineArg = if (Test-Path $engine) { ' -Engine "{0}"' -f $engine } else { '' }
$arguments = ('-Dir "{0}" -App "{1}" -IdleSeconds {2} -IdleTimeout {3}' -f $dir, $app, $IdleSeconds, $IdleTimeout) + $engineArg
# A unique task, and a teardown of any drag if the driver is killed or dies
# before its own finally (gui-guard.ps1).
$run = Invoke-GuiTask -Name 'PeelActionsDrag' -Dir $dir -ResultPath $resultPath -TimeoutSeconds $TimeoutSeconds -IdleTimeout $IdleTimeout `
    -Arguments ('-NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f $script, $arguments)

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
if ($result.aborted) {
    Write-Output "RESULT: ABORTED ($($result.aborted)), not a test failure: run it again when George is idle"
    exit 3
}
Write-Output ("RESULT: {0}" -f $(if ($result.ok) { 'PASS' } else { 'FAIL' }))
if (-not $result.ok) { exit 1 }
exit 0
