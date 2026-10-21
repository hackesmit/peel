<#
End any drag a GUI driver left behind, in the interactive session
(gui-guard.ps1, Invoke-GuiTeardown), and report what it found.

Two callers:
  - the watchdog a driver starts (Start-GuiGuard): -WaitPid <driver pid>. It
    waits for the driver to exit. A driver that reaches its finally stops the
    watchdog first, so the teardown here only runs when the driver was killed
    (Stop-ScheduledTask, a timeout) or crashed before its own.
  - a runner in session 0 (Invoke-InteractiveTeardown), through a one shot
    Interactive task, after a driver timed out or left no result.

-Report  JSON with what was done and what is left (DragImagesLeft, StillDown).
-Shot    a screenshot of the whole desktop after the teardown.
#>
param(
    [int]$WaitPid = 0,
    [string]$Report = '',
    [string]$Shot = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'gui-guard.ps1')

if ($WaitPid -gt 0) {
    try { (Get-Process -Id $WaitPid -ErrorAction Stop).WaitForExit() } catch { }
}

$result = Invoke-GuiTeardown
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
    actions         = $result.Actions
    dragImagesFound = $result.DragImagesFound
    dragImagesLeft  = $result.DragImagesLeft
    stillDown       = $result.StillDown
    clean           = $result.Clean
    shot            = $(if ($shotTaken) { $Shot } else { '' })
    text            = $result.Text
    when            = (Get-Date).ToString('o')
}
if ($Report) { $out | ConvertTo-Json | Set-Content $Report -Encoding UTF8 }
else { $out | ConvertTo-Json }
