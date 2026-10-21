<#
Proof for the GUI guard (gui-guard.ps1): a driver that dies in the middle of a
real Explorer drag must leave no drag image and no button held.

It needs no Peel: it starts an Explorer drag of a file in a scratch folder it
makes inside its own run folder once the drive starts, with SendInput, holds it, checks that Explorer shows its drag image
(SysDragImage), screenshots it, and then:

-Mode fail  throws, as a failing check would. The finally runs the guard
            teardown, and the driver checks that the image is gone, no button
            is down, and the file has not moved; then screenshots the desktop.
-Mode kill  holds the drag until the runner kills it (run-e2e.ps1 -Test
            abort-kill, a short -TimeoutSeconds). Its finally never runs; the
            watchdog and the runner have to clean up, and the runner checks.

Run it through run-e2e.ps1 (-Test abort-fail or abort-kill), never by hand
over ssh: a drag needs the desktop session.
#>
param(
    [string]$Dir = 'C:\peel-dev\e2e-abort',
    [ValidateSet('fail', 'kill')]
    [string]$Mode = 'fail',
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 1800,
    # The run folder a runner made for this run (gui-guard.ps1, Invoke-GuiTask).
    [string]$GuardDir = ''
)

$ErrorActionPreference = 'Stop'
# This run's own scratch folder, made inside its fresh run folder once the
# drive starts: nothing is created or deleted while George may be busy, and
# the run deletes only the folder it made.
$test = $null
$driveLog = Join-Path $Dir 'drive.log'
$resultPath = Join-Path $Dir 'result.json'
$checks = [System.Collections.ArrayList]::new()
$script:cursor = @(0, 0)
$script:GuiAbort = $null

New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item $driveLog, $resultPath -ErrorAction SilentlyContinue

function L($message) { Add-Content $driveLog ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $message) }

function Check($name, $ok, $detail) {
    $null = $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = "$detail" })
    L ("{0} {1}: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class InAb {
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
'@
. (Join-Path $PSScriptRoot 'gui-guard.ps1')

function MoveTo($x, $y) {
    Assert-NoUserInput
    [PeelGuard]::Move([int]$x, [int]$y)
    $script:cursor = @([int]$x, [int]$y)
}

function Screenshot($path) {
    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($screen.X, $screen.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    L "screenshot $path"
}

function CloseTestExplorer() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $test -and $_.LocationURL -like (([uri]$test).AbsoluteUri + '*') } |
            ForEach-Object { $_.Quit() }
    } catch { }
}

function FindItem($name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $isItem = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $named = New-Object System.Windows.Automation.OrCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $stem)))
    $cond = New-Object System.Windows.Automation.AndCondition($isItem, $named)
    for ($i = 0; $i -lt 25; $i++) {
        $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($item) { return $item }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

$source = $null
$evidence = $Dir
$teardown = $null
$originalCursor = [System.Windows.Forms.Cursor]::Position
try {
    Start-GuiGuard -Dir $Dir -RunDir $GuardDir -IdleSeconds $IdleSeconds -IdleTimeout $IdleTimeout -Log { param($m) L $m }
    $originalCursor = [System.Windows.Forms.Cursor]::Position
    $evidence = [PeelGuard]::RunDir
    $test = Join-Path $evidence 'testdir'
    New-Item -ItemType Directory $test | Out-Null
    $source = Join-Path $test 'held.txt'
    Set-Content -Path $source -Value 'held mid drag' -Encoding UTF8
    L "scratch folder $test"

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3
    $item = FindItem 'held.txt'
    if (-not $item) { throw 'held.txt is not visible in Explorer' }
    $r = $item.Current.BoundingRectangle
    $x = [int]($r.X + 24); $y = [int]($r.Y + $r.Height / 2)
    L "grab at $x,$y"
    [void][PeelGuard]::Place($x, $y); Start-Sleep -Milliseconds 250
    MoveTo $x $y; Start-Sleep -Milliseconds 120
    [PeelGuard]::Down(); Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 14; $i++) { MoveTo ($x + $i * 14) ($y + $i * 7); Start-Sleep -Milliseconds 30 }
    Start-Sleep -Milliseconds 800

    $images = ([PeelGuard]::DragImages()).Count
    Check 'Explorer shows a drag image mid drag' ($images -gt 0) "visible SysDragImage windows: $images"
    Check 'the left button is held mid drag' ([PeelGuard]::IsDown(1)) 'GetAsyncKeyState(VK_LBUTTON)'
    Screenshot (Join-Path $evidence 'shot-mid-drag.png')

    if ($Mode -eq 'kill') {
        # A person who touches George ends the hold at once (Wait-Gui), and a
        # runner that never kills this driver gets a failure, not a drag held
        # for good.
        L 'holding the drag until the runner kills this driver'
        Set-Content (Join-Path $evidence 'holding.txt') (Get-Date).ToString('o')
        Wait-Gui 120000
        throw 'nobody killed the driver within 120 s of holding the drag'
    }
    throw 'a check failed mid drag (synthetic, -Mode fail)'
}
catch {
    $script:GuiAbort = Get-GuiAbort $_
    if ($script:GuiAbort) { L "ABORTED: $_" }
    elseif ("$_" -notmatch 'synthetic, -Mode fail') { Check 'driver completed' $false "$_" }
    else { L "the failure under test: $_" }
}
finally {
    try { $teardown = Stop-GuiGuard; L $teardown.Text } catch { Check 'the guard teardown ran' $false "$_" }
    if (-not $script:GuiAbort -and $Mode -eq 'fail') {
        Start-Sleep -Milliseconds 500
        $left = ([PeelGuard]::DragImages()).Count
        Check 'no drag image is left after the failure' ($left -eq 0) "visible SysDragImage windows: $left"
        $down = @([PeelGuard]::Buttons + [PeelGuard]::Modifiers | Where-Object { [PeelGuard]::IsDown($_) })
        Check 'no button or modifier is left down' ($down.Count -eq 0) "still down: $(($down | ForEach-Object { '0x{0:x2}' -f $_ }) -join ',')"
        $files = if ($test -and (Test-Path $test)) { @(Get-ChildItem -LiteralPath $test -File -Recurse | ForEach-Object { $_.Name }) } else { @() }
        Check 'the cancelled drag moved nothing' ($source -and (Test-Path $source) -and $files.Count -eq 1) "files in the test folder: $($files -join ',')"
        try { Screenshot (Join-Path $evidence 'shot-after-teardown.png') } catch { L "screenshot failed: $_" }
    }
    Restore-GuiCursor $originalCursor
    CloseTestExplorer
    if ($test -and (Test-Path $test)) { Remove-Item -LiteralPath $test -Recurse -Force -ErrorAction SilentlyContinue }
    $failed = @($checks | Where-Object { -not $_.ok })
    [pscustomobject]@{
        ok = ($failed.Count -eq 0 -and -not $script:GuiAbort); aborted = $script:GuiAbort; checks = $checks
        runDir = [PeelGuard]::RunDir; drove = [PeelGuard]::Drove; injections = [PeelGuard]::Injections; cleanups = [PeelGuard]::Cleanups
        teardown = $(if ($teardown) { $teardown.Text } else { '' })
    } |
        ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
    L ("done, {0} of {1} checks passed" -f ($checks.Count - $failed.Count), $checks.Count)
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}
