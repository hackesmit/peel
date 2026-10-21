# Runs review-elevated-up.ps1: Peel at medium integrity, the driver elevated,
# both in the interactive session. Takes gui.lock itself, pauses Daniel's
# installed Peel only once the driver drives, stops only the Peel it started,
# and starts Daniel's Peel again at the end (gui-guard.ps1, Invoke-GuiDriver).
#   ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\review-4500\shell\tests\run-review-elevated.ps1"
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$Dir = 'C:\peel-dev\review-e2e',
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
$driver = Join-Path $Root 'shell\tests\review-elevated-up.ps1'
New-Item -ItemType Directory -Force $Dir | Out-Null
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$peelLog = Join-Path $Dir 'peel.log'
$resultPath = Join-Path $Dir 'result.json'

$appTask = 'PeelReviewApp-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$appCmd = "`$env:PEEL_LOG='$peelLog'; `$env:PEEL_EXIT_AFTER='75'; & '$app'"
$appAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ("-NoProfile -WindowStyle Hidden -Command `"$appCmd`"")
Register-ScheduledTask -TaskName $appTask -Action $appAction -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited) -Force | Out-Null
try {
    # The driver waits for user idle first (gui-guard.ps1); Peel exits by
    # itself after 75 s, so start it only once the driver drives. Daniel's
    # Peel holds the single instance mutex, so pause it at that moment too.
    $run = Invoke-GuiDriver -Name 'PeelReviewDrive' -Dir $Dir -ResultPath $resultPath -App $app -RunLevel Highest `
        -TimeoutSeconds 120 -IdleTimeout $IdleTimeout `
        -Arguments ('-NoProfile -ExecutionPolicy Bypass -File "{0}" -Dir "{1}" -IdleSeconds {2} -IdleTimeout {3}' -f $driver, $Dir, $IdleSeconds, $IdleTimeout) `
        -OnDriving {
            foreach ($process in Get-InstalledPeel) {
                Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
                Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
            }
            Remove-Item $peelLog -ErrorAction SilentlyContinue
            Start-ScheduledTask -TaskName $appTask
        }
}
catch {
    if (-not (Get-GuiAbort $_)) { throw }
    Write-Output "RESULT: ABORTED ($(Get-GuiAbort $_)), not a test failure: $_"
    exit 3
}
finally {
    Stop-ScheduledTask -TaskName $appTask -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $appTask -Confirm:$false -ErrorAction SilentlyContinue
}
Write-Output '--- drive.log ---'
Get-Content (Join-Path $Dir 'drive.log') -ErrorAction SilentlyContinue | Select-Object -Last 30
Write-Output '--- peel.log (drag lines) ---'
Get-Content $peelLog -ErrorAction SilentlyContinue | Select-String 'drag|modifier|wheel|hook' | Select-Object -Last 25
