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
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
# The output folder carries the Windows SDK version (net8.0-windows10.0.x since
# the Share action's WinRT need). Pick the most recently built Peel.exe so a
# stale net8.0-windows folder from an earlier TFM never shadows the real build.
$app = Get-ChildItem -Path (Join-Path $Root 'shell\Peel\bin\Release') -Filter 'net8.0-windows*' -Directory |
    ForEach-Object { Join-Path $_.FullName 'Peel.exe' } | Where-Object { Test-Path $_ } |
    Sort-Object { (Get-Item $_).LastWriteTime } -Descending | Select-Object -First 1
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
$arguments = ('-Dir "{0}" -App "{1}"' -f $dir, $app) + $engineArg
$taskName = 'PeelActionsDrag'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -File "{0}" {1}' -f $script, $arguments)
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
