# Runs review-elevated-up.ps1: Peel at medium integrity, the driver elevated,
# both in the interactive session. Deletes both tasks and any Peel it started.
#   ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\review-4500\shell\tests\run-review-elevated.ps1"
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$Dir = 'C:\peel-dev\review-e2e'
)
$ErrorActionPreference = 'Stop'
# Match the target framework folder (it gained a Windows SDK version with the
# Share action), rather than naming one version.
# Pick the newest build by target framework version (e2e-hygiene.ps1), never
# the first folder alphabetically: a dev copy that still holds an old
# net8.0-windows build would otherwise launch that stale exe silently.
. (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')
. (Join-Path $PSScriptRoot 'gui-guard.ps1')
$app = Find-PeelApp $Root
if (-not $app) { throw "build the shell first (dotnet build -c Release): no Peel.exe under shell\Peel\bin\Release\net8.0-windows*" }
$driver = Join-Path $Root 'shell\tests\review-elevated-up.ps1'
New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item (Join-Path $Dir '*') -Recurse -Force -ErrorAction SilentlyContinue
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$peelLog = Join-Path $Dir 'peel.log'

$appCmd = "`$env:PEEL_LOG='$peelLog'; `$env:PEEL_EXIT_AFTER='75'; & '$app'"
$appAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ("-NoProfile -WindowStyle Hidden -Command `"$appCmd`"")
$driveAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -File "{0}" -Dir "{1}"' -f $driver, $Dir)
Register-ScheduledTask -TaskName 'PeelReviewApp' -Action $appAction -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited) -Force | Out-Null
Register-ScheduledTask -TaskName 'PeelReviewDrive' -Action $driveAction -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Highest) -Force | Out-Null
$died = $false
try {
    # The driver waits for user idle first (gui-guard.ps1); Peel exits by
    # itself after 75 s, so start it only once the driver drives.
    Start-ScheduledTask -TaskName 'PeelReviewDrive'
    $deadline = (Get-Date).AddSeconds(1920)
    do { Start-Sleep -Seconds 3; $state = (Get-ScheduledTask -TaskName 'PeelReviewDrive' -ErrorAction SilentlyContinue).State } `
        while ($state -eq 'Running' -and -not (Test-Path (Join-Path $Dir 'driving.txt')) -and (Get-Date) -lt $deadline)
    Start-ScheduledTask -TaskName 'PeelReviewApp'
    $deadline = (Get-Date).AddSeconds(120)
    do { Start-Sleep -Seconds 3; $state = (Get-ScheduledTask -TaskName 'PeelReviewDrive' -ErrorAction SilentlyContinue).State } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
    $died = $state -ne 'Ready'
}
finally {
    foreach ($t in 'PeelReviewDrive', 'PeelReviewApp') {
        Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
    }
    Get-Process Peel -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # A driver stopped mid drag never ran its finally: end the drag from here.
    $null = Complete-GuiRun -Dir $Dir -DriverDied ($died -or -not (Test-Path (Join-Path $Dir 'result.json')))
}
Write-Output '--- drive.log ---'
Get-Content (Join-Path $Dir 'drive.log') -ErrorAction SilentlyContinue | Select-Object -Last 30
Write-Output '--- peel.log (drag lines) ---'
Get-Content $peelLog -ErrorAction SilentlyContinue | Select-String 'drag|modifier|wheel|hook' | Select-Object -Last 25
