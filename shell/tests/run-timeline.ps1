<#
Run the timeline and page editor end to end test (e2e-timeline.ps1) in the
interactive desktop session and report the result.

From hqbox:
  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\run-timeline.ps1 -Engine C:\peel-dev\<venv>\Scripts\peel.exe"

The driver waits for 5 minutes with no user input on George before it moves
the mouse, so this can take that long to start. It pauses Daniel's installed
Peel for the run and starts it again at the end. If the driver is killed
before its own cleanup (timeout), this script puts the installed Peel back:
the driver leaves installed-paused.txt while Peel is paused.

-Engine is a peel.exe whose venv holds this branch's engine and the module
backends (pillow, pymupdf); its python runs the checks. The shell is the
newest Release build under shell\Peel\bin (e2e-hygiene.ps1).
#>
param(
    [string]$Engine = 'C:\peel-dev\venv-64ec\Scripts\peel.exe',
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 3600,
    # The drive budget, counted from the end of the idle wait (driving.txt).
    [int]$TimeoutSeconds = 5400
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent (Split-Path -Parent $here)
. (Join-Path $here 'e2e-hygiene.ps1')
. (Join-Path $here 'gui-guard.ps1')
$app = Find-PeelApp $Root
if (-not $app) { throw 'build the shell first (dotnet build -c Release): no Peel.exe under shell\Peel\bin\Release' }

$dir = 'C:\peel-dev\e2e-timeline'
$script = Join-Path $here 'e2e-timeline.ps1'
$resultPath = Join-Path $dir 'result.json'
$pausedMarker = Join-Path $dir 'installed-paused.txt'
$installedApp = Join-Path $env:LOCALAPPDATA 'Peel\app\Peel.exe'
New-Item -ItemType Directory -Force $dir | Out-Null

$arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Dir "{1}" -App "{2}" -Peel "{3}" -IdleSeconds {4} -IdleTimeout {5}' -f `
    $script, $dir, $app, $Engine, $IdleSeconds, $IdleTimeout
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
# gui.lock for the whole run, the restart of Daniel's Peel included; it is
# created exclusively and goes when this process ends (gui-guard.ps1).
try { Enter-GuiLock $IdleTimeout }
catch {
    if (-not (Get-GuiAbort $_)) { throw }
    Write-Output "RESULT: ABORTED ($(Get-GuiAbort $_)), not a test failure: $_"
    exit 3
}
Remove-Item $pausedMarker -ErrorAction SilentlyContinue
try {
    # A unique task, and a teardown of any drag if the driver is killed or
    # dies before its own finally (gui-guard.ps1).
    $run = Invoke-GuiTask -Name 'PeelE2ETimeline' -Arguments $arguments -Dir $dir -ResultPath $resultPath `
        -TimeoutSeconds $TimeoutSeconds -IdleTimeout $IdleTimeout
}
finally {
    if (Test-Path $pausedMarker) {
        # The driver died with Daniel's Peel paused. Stop any test copy it
        # left, then start the installed one in the desktop session.
        Get-Process -Name Peel -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path -ieq $app } |
            ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        if (@(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $installedApp }).Count -eq 0) {
            $restore = 'PeelE2ETimelineRestore-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
                -Argument ('-NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath ''{0}''"' -f $installedApp)
            Register-ScheduledTask -TaskName $restore -Action $action -Principal $principal -Force | Out-Null
            try {
                Start-ScheduledTask -TaskName $restore
                Start-Sleep -Seconds 5
            }
            finally {
                Unregister-ScheduledTask -TaskName $restore -Confirm:$false -ErrorAction SilentlyContinue
            }
        }
        Remove-Item $pausedMarker -ErrorAction SilentlyContinue
        Write-Output 'RECOVERY: the driver did not finish; restarted the installed Peel'
    }
    Exit-GuiLock
}

$driveLog = Join-Path $dir 'drive.log'
if (Test-Path $driveLog) {
    Write-Output '--- drive.log (tail) ---'
    Get-Content $driveLog | Select-Object -Last 15
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
