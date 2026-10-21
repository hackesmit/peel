# A unique task, the idle wait first, and a teardown of any drag if the
# driver is killed or dies before its own finally (gui-guard.ps1).
. (Join-Path $PSScriptRoot 'gui-guard.ps1')
$dir = 'C:\peel-dev\review2-5f8a-gui'
$run = Invoke-GuiTask -Name 'PeelReview2x5f8a' -Dir $dir -ResultPath (Join-Path $dir 'drive.log') -TimeoutSeconds 300 `
    -Arguments ('-NoProfile -ExecutionPolicy Bypass -File "{0}" -Dir "{1}"' -f (Join-Path $PSScriptRoot 'review-keyboard-round2-gui.ps1'), $dir)
Get-Process Peel -ErrorAction SilentlyContinue | Select-Object Id, Path
$drive = 'C:\peel-dev\review2-5f8a-gui\drive.log'
if (Test-Path $drive) { Get-Content $drive | Where-Object { $_ -match 'CHECK |DRIVER ERROR' } }
$fails = @(Get-Content $drive -ErrorAction SilentlyContinue | Where-Object { $_ -match 'CHECK .* FAIL|DRIVER ERROR' })
if ($fails.Count -gt 0) { 'REVIEW RESULT: FAIL'; exit 1 } else { 'REVIEW RESULT: PASS' }
