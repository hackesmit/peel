<#
Check the Alt wheel file actions on George, in the logged on desktop session.

Processes started over ssh live in session 0, which has no desktop, so the
clipboard, the share sheet and the float windows are not reachable there. This
registers a scheduled task with an Interactive principal, the same way
run-e2e.ps1 does, so Peel.ActionProbe runs where Daniel is logged in and can
touch the clipboard and show windows.

It drives the real action classes directly (no mouse, no Explorer drag), so it
needs no gui.lock and drops nothing on the desktop. Everything it creates lives
under C:\peel-dev\actions-check and is removed at the end.

From hqbox:
  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\run-actions.ps1"
#>
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$probeProject = Join-Path $Root 'shell\tests\ActionProbe'
$probe = Join-Path $probeProject 'bin\Release\net8.0-windows10.0.19041.0\Peel.ActionProbe.exe'
if (-not (Test-Path $probe)) {
    Write-Output 'building the action probe'
    & dotnet build $probeProject -c Release -v q --nologo | Out-Null
}
if (-not (Test-Path $probe)) { throw "the probe did not build: $probe" }

$dir = 'C:\peel-dev\actions-check'
$fixtures = Join-Path $dir 'fixtures'
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory -Force $fixtures | Out-Null

# A shared, non-destructive selection: an image, a plain file and a folder, so
# every action is exercised on a file, a folder and a mix at once.
function New-Png($path) {
    $bmp = New-Object System.Drawing.Bitmap 96, 96
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::CornflowerBlue)
    $g.FillEllipse([System.Drawing.Brushes]::Orange, 16, 16, 64, 64)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$img = Join-Path $fixtures 'photo.png'
$txt = Join-Path $fixtures 'notes.txt'
$folder = Join-Path $fixtures 'album'
New-Png $img
Set-Content -Path $txt -Value 'peel action check'
New-Item -ItemType Directory -Force $folder | Out-Null
Set-Content -Path (Join-Path $folder 'inside.txt') -Value 'x'
$shared = @($img, $txt, $folder) -join ';'

$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$results = @{}

function Invoke-Probe($name, $argline) {
    $task = "PeelActions_$name"
    $out = Join-Path $dir "$name.txt"
    Remove-Item $out -ErrorAction SilentlyContinue
    $action = New-ScheduledTaskAction -Execute $probe -Argument $argline
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 700
            $state = (Get-ScheduledTask -TaskName $task).State
        } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        if ($state -eq 'Running') { Stop-ScheduledTask -TaskName $task }
    }
    finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }
    if (Test-Path $out) { $results[$name] = Get-Content $out -Raw } else { $results[$name] = "FAIL ${name}: no result written" }
}

try {
    Invoke-Probe 'copy'     ('copy --out "{0}\copy.txt" --files "{1}"' -f $dir, $shared)
    Invoke-Probe 'terminal' ('terminal --out "{0}\terminal.txt" --files "{1}"' -f $dir, $img)
    Invoke-Probe 'float'    ('float --out "{0}\float.txt" --files "{1}" --shot "{0}\float.png" --wait 2500' -f $dir, $shared)
    Invoke-Probe 'share'    ('share --out "{0}\share.txt" --files "{1}" --shot "{0}\share.png" --wait 3500' -f $dir, $shared)

    # Move destroys its sources, so it gets its own fresh selection.
    $moveSrc = Join-Path $fixtures 'move-src'
    $moveDst = Join-Path $dir 'move-dst'
    New-Item -ItemType Directory -Force $moveSrc | Out-Null
    $mImg = Join-Path $moveSrc 'clip.png'; New-Png $mImg
    $mDir = Join-Path $moveSrc 'docs'; New-Item -ItemType Directory -Force $mDir | Out-Null
    Set-Content -Path (Join-Path $mDir 'a.txt') -Value 'y'
    Invoke-Probe 'move' ('move --out "{0}\move.txt" --files "{1}" --dest "{2}"' -f $dir, (@($mImg, $mDir) -join ';'), $moveDst)
}
finally {
    foreach ($leftover in @('WindowsTerminal', 'Peel.ActionProbe')) {
        Get-Process -Name $leftover -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

Write-Output '=== action checks ==='
$overall = $true
foreach ($name in 'copy', 'terminal', 'move', 'float', 'share') {
    Write-Output "--- $name ---"
    $text = $results[$name]
    Write-Output $text
    if ($text -notmatch 'PASS' -or $text -match 'FAIL') { $overall = $false }
}
Write-Output ("RESULT: {0}" -f $(if ($overall) { 'PASS' } else { 'FAIL' }))
Write-Output "screenshots and results under $dir"
if (-not $overall) { exit 1 }
exit 0
