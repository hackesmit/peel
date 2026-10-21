<#
End the drag a dead GUI driver left behind, in the interactive session
(gui-guard.ps1, Invoke-GuiTeardown), and report what it found.

Two callers:
  - the watchdog a driver starts (Start-GuiGuard): -WaitPid <driver pid>. It
    waits for the driver to exit. A driver that reaches its finally stops the
    watchdog first, so the teardown here only runs when the driver was killed
    (Stop-ScheduledTask, a timeout) or crashed before its own.
  - a runner in session 0 (Invoke-InteractiveTeardown), through a one shot
    Interactive task, after a driver timed out or left no result.

Either one acts only on the run's own state (-RunDir, guard-state.json): it
claims a state that is not done and whose owner process is gone, and then
releases only the buttons and keys that state says the run pressed. A run that
never drove, or a state another owner already finished, gets no input at all.

-Report  JSON with what was done and what is left (DragImagesLeft, OwnedLeft).
-Shot    a screenshot of the whole desktop after the teardown.
#>
param(
    [int]$WaitPid = 0,
    [Parameter(Mandatory)] [string]$RunDir,
    [string]$Report = '',
    [string]$Shot = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'gui-guard.ps1')

if ($WaitPid -gt 0) {
    try { (Get-Process -Id $WaitPid -ErrorAction Stop).WaitForExit() } catch { }
}

$by = if ($WaitPid -gt 0) { 'watchdog' } else { 'runner' }
$claim = Request-GuiState $RunDir $by
if ($claim) {
    [PeelGuard]::Adopt($RunDir, $claim.Path, [bool]$claim.State.drove, [int[]]@($claim.State.owned | Where-Object { $null -ne $_ }), [int]$claim.State.injections)
    $result = Invoke-GuiTeardown
    [PeelGuard]::Finish()
    # A killed driver never closed the Explorer windows it opened on its own
    # scratch folder (inside the run folder); close those, and no other.
    $prefix = ([uri]((Resolve-Path $RunDir).Path.TrimEnd('\') + '\')).AbsoluteUri
    $closed = 0
    try {
        foreach ($w in @((New-Object -ComObject Shell.Application).Windows())) {
            try { if ("$($w.LocationURL)/" -like "$prefix*") { $w.Quit(); $closed++ } } catch { }
        }
    } catch { }
    if ($closed -gt 0) { $result.Actions += ", closed $closed Explorer window(s) on the run folder"; $result.Text += "; closed $closed Explorer window(s) on the run folder" }
}
else {
    $result = [pscustomobject]@{
        Actions = 'nothing to tear down: no unfinished state of a dead owner'; Drove = $false; Injections = 0; Cleanups = 0
        DragImagesFound = ([PeelGuard]::DragImages()).Count; DragImagesLeft = ([PeelGuard]::DragImages()).Count
        OwnedLeft = ''; StillDown = ''; Clean = $true; Text = ''
    }
    $result.Text = "teardown ($by): $($result.Actions); drag images on screen $($result.DragImagesLeft)"
}
$shotTaken = $false
if ($Shot) {
    try {
        Add-Type -AssemblyName System.Drawing, System.Windows.Forms
        $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($screen.X, $screen.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose(); $bitmap.Dispose()
        $shotTaken = $true
    }
    catch { $result.Text += "; screenshot failed: $($_.Exception.Message)" }
}

$out = [pscustomobject]@{
    by              = $(if ($WaitPid -gt 0) { "watchdog of pid $WaitPid" } else { 'runner' })
    claimed         = [bool]$claim
    actions         = $result.Actions
    drove           = $result.Drove
    injections      = $result.Injections
    cleanups        = $result.Cleanups
    dragImagesFound = $result.DragImagesFound
    dragImagesLeft  = $result.DragImagesLeft
    ownedLeft       = $result.OwnedLeft
    stillDown       = $result.StillDown
    clean           = $result.Clean
    shot            = $(if ($shotTaken) { $Shot } else { '' })
    text            = $result.Text
    when            = (Get-Date).ToString('o')
}
if ($Report) { $out | ConvertTo-Json | Set-Content $Report -Encoding UTF8 }
else { $out | ConvertTo-Json }
