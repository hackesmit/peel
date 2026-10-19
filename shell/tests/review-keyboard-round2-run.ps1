$task = 'PeelReview2x5f8a'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f (Join-Path $PSScriptRoot 'review-keyboard-round2-gui.ps1'))
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
try {
  Start-ScheduledTask -TaskName $task
  $deadline = (Get-Date).AddSeconds(600)
  do { Start-Sleep 3; $s = (Get-ScheduledTask -TaskName $task).State } while ($s -eq 'Running' -and (Get-Date) -lt $deadline)
  if ($s -eq 'Running') { Stop-ScheduledTask -TaskName $task; 'TIMEOUT' }
} finally { Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue }
Get-Process Peel -ErrorAction SilentlyContinue | Select-Object Id, Path
$drive = 'C:\peel-dev\review2-5f8a-gui\drive.log'
if (Test-Path $drive) { Get-Content $drive | Where-Object { $_ -match 'CHECK |DRIVER ERROR' } }
$fails = @(Get-Content $drive -ErrorAction SilentlyContinue | Where-Object { $_ -match 'CHECK .* FAIL|DRIVER ERROR' })
if ($fails.Count -gt 0) { 'REVIEW RESULT: FAIL'; exit 1 } else { 'REVIEW RESULT: PASS' }
