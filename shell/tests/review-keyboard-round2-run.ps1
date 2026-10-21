# gui.lock, a unique task, the idle wait first, a teardown of any drag if the
# driver is killed or dies before its own finally, and Daniel's Peel back at
# the end (gui-guard.ps1, Invoke-GuiDriver). The driver writes result.json in
# its finally, so a driver that died is told apart from one that finished.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'e2e-hygiene.ps1')
. (Join-Path $PSScriptRoot 'gui-guard.ps1')
$dir = 'C:\peel-dev\review2-5f8a-gui'
$app = Find-PeelApp (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (-not $app) { throw 'build the shell first (dotnet build -c Release): no Peel.exe under shell\Peel\bin\Release' }
$resultPath = Join-Path $dir 'result.json'
New-Item -ItemType Directory -Force $dir | Out-Null
try {
    $run = Invoke-GuiDriver -Name 'PeelReview2x5f8a' -Dir $dir -ResultPath $resultPath -App $app -TimeoutSeconds 300 `
        -Arguments ('-NoProfile -ExecutionPolicy Bypass -File "{0}" -Dir "{1}" -App "{2}"' -f (Join-Path $PSScriptRoot 'review-keyboard-round2-gui.ps1'), $dir, $app)
}
catch {
    if (-not (Get-GuiAbort $_)) { throw }
    "REVIEW RESULT: ABORTED ($(Get-GuiAbort $_)): $_"; exit 3
}
Get-Process Peel -ErrorAction SilentlyContinue | Select-Object Id, Path
$drive = Join-Path $dir 'drive.log'
if (Test-Path $drive) { Get-Content $drive | Where-Object { $_ -match 'CHECK |DRIVER ERROR|ABORTED' } }
if (-not (Test-Path $resultPath)) { 'REVIEW RESULT: FAIL (no result.json, the driver did not finish)'; exit 1 }
$result = Get-Content $resultPath -Raw | ConvertFrom-Json
if ($result.aborted) { "REVIEW RESULT: ABORTED ($($result.aborted)), not a test failure"; exit 3 }
if (-not $result.ok) { 'REVIEW RESULT: FAIL'; exit 1 } else { 'REVIEW RESULT: PASS' }
