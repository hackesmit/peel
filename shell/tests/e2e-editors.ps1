<#
Image editor end to end test: open each editor FROM THE WHEEL with a real
Explorer drag, drive it with UI Automation, apply, and check what the engine
wrote.

This has to run in the interactive session (session 1), so run it through
run-e2e.ps1 -Test editors, which schedules it with an Interactive principal.

One scenario per editor, each on its own source file so no two outputs can
collide:

  crop.png   Crop      drag a box with the mouse, then type the exact numbers,
                       and expect a 200 by 150 output cut from the green
                       quarter of the picture.
  light.png  Edit      rotate 90 and set brightness to 2 on a flat colour, and
                       expect a 300 by 400 output whose every pixel doubled.
  draw.png   Annotate  drag a thick green box over the red quarter, and expect
                       the ink on its edge and the red untouched inside it.
  hide.png   Redact    drag a region over the red quarter with mode box, and
                       expect black there and the grey quarter untouched.
  turned.jpg Crop      a photo stored turned, with an EXIF Orientation tag.
                       Its preview has to come from the engine and the box has
                       to be in the upright 300 by 400, not the stored 400 by
                       300.
  tags.jpg   Metadata  expect the form to arrive filled from the file's own
                       tags (the engine's action=view), type new ones with a
                       location, and expect them in the output's EXIF.

Then one more: open Crop again and press Escape, and expect nothing written.

Nothing about the registry is hard coded. The tools wheel's own log line lists
its segments clockwise from twelve o'clock and the angle of the one to aim at
is worked out from that, so the run keeps working as families land. The
drawing areas are found the same way: a WPF Canvas has no UI Automation peer,
so each editor logs where its canvas is in device pixels and what image size it
stands for, and the driver aims a real mouse at an image pixel from that.

Every drop lands inside C:\peel-dev\e2e-editors\testdir. Nothing is ever
dropped on Explorer's navigation pane.
#>
param(
    [string]$Dir = 'C:\peel-dev\e2e-editors',
    [string]$App = 'C:\peel-dev\peel-6f29\shell\Peel\bin\Release\net8.0-windows\Peel.exe',
    # The engine to test against, and the python next to it for the checks.
    # The installed engine is the default, but a developer machine's install
    # can be older than the branch (it was: C:\peel-dev\main had no image
    # family and the tools wheel was empty for a PNG), so pass -Peel the
    # peel.exe of a venv holding this branch's engine. Peel itself is pointed
    # at the same one through PEEL_CLI, so the shell and the checks never
    # disagree about which engine they are talking about.
    [string]$Peel = "$env:LOCALAPPDATA\Peel\venv\Scripts\peel.exe",
    [string]$Check = 'C:\peel-dev\peel-6f29\shell\tests\e2e-editors-check.py',
    # gui-guard.ps1: user idle to wait for before driving, and how long to wait.
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 1800
)
$Python = Join-Path (Split-Path -Parent $Peel) 'python.exe'

$ErrorActionPreference = 'Stop'
$test = Join-Path $Dir 'testdir'
$driveLog = Join-Path $Dir 'drive.log'
$peelLog = Join-Path $Dir 'peel.log'
$resultPath = Join-Path $Dir 'result.json'
$checks = [System.Collections.ArrayList]::new()
$script:GuiAbort = $null
$shots = [System.Collections.ArrayList]::new()
$script:cursor = @(0, 0)

New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item (Join-Path $Dir '*.png'), $driveLog, $peelLog, $resultPath -ErrorAction SilentlyContinue
Remove-Item $test -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $test | Out-Null

function L($message) { Add-Content $driveLog ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $message) }

function Check($name, $ok, $detail) {
    $null = $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = "$detail" })
    L ("{0} {1}: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

function PeelLog() {
    if (Test-Path $peelLog) { return (Get-Content $peelLog -Raw -ErrorAction SilentlyContinue) }
    return ''
}

function CountLog($pattern) { return [regex]::Matches((PeelLog), $pattern).Count }

function WaitLog($pattern, $timeoutMs, $atLeast = 1) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $found = [regex]::Matches((PeelLog), $pattern)
        if ($found.Count -ge $atLeast) { return $found[$found.Count - 1] }
        Assert-NoUserInput
        Start-Sleep -Milliseconds 100
    }
    return $null
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class InEd {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  // SetCursorPos does not reach a low level hook. Absolute SendInput moves do.
  public static void Move(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    int ax = (int)(((x - vx) * 65535.0) / (vw - 1)), ay = (int)(((y - vy) * 65535.0) / (vh - 1));
    mouse_event(0x0001 | 0x8000 | 0x4000, ax, ay, 0, UIntPtr.Zero);
  }
  public static void Place(int x, int y) { SetCursorPos(x, y); }
  public static void Down() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
  public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
  public static void Tap(byte vk) {
    keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); keybd_event(vk, 0, 2, UIntPtr.Zero);
  }
}
'@
[void][InEd]::SetProcessDPIAware()
# Idle wait, real input abort, and the teardown that ends any drag.
. (Join-Path $PSScriptRoot 'gui-guard.ps1')

$VkCtrl = [byte]0xA2
$VkEsc = [byte]0x1B
$UIA = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

function MoveTo($x, $y) {
    Assert-NoUserInput
    [InEd]::Move([int]$x, [int]$y)
    $script:cursor = @([int]$x, [int]$y)
}

function GlideTo($x, $y, $steps = 8) {
    $from = $script:cursor
    for ($i = 1; $i -le $steps; $i++) {
        MoveTo ($from[0] + (($x - $from[0]) * $i / $steps)) ($from[1] + (($y - $from[1]) * $i / $steps))
        Start-Sleep -Milliseconds 35
    }
}

function CloseTestExplorer() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $_.LocationURL -like '*peel-dev/e2e-editors/testdir*' } | ForEach-Object { $_.Quit() }
    } catch { }
}

function FindItem($name) {
    $root = $UIA::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $withExt = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    $withoutExt = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $stem)
    $isItem = New-Object System.Windows.Automation.PropertyCondition(
        $UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $cond = New-Object System.Windows.Automation.AndCondition(
        $isItem, (New-Object System.Windows.Automation.OrCondition($withExt, $withoutExt)))
    for ($i = 0; $i -lt 25; $i++) {
        $item = $root.FindFirst($Tree::Descendants, $cond)
        if ($item) { return $item }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function StartDragFrom($item) {
    $r = $item.Current.BoundingRectangle
    $x = [int]($r.X + 24); $y = [int]($r.Y + $r.Height / 2)
    L "grab at $x,$y"
    [void][InEd]::Place($x, $y); Start-Sleep -Milliseconds 250
    MoveTo $x $y; Start-Sleep -Milliseconds 120
    [InEd]::Down(); Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 14; $i++) {
        MoveTo ($x + $i * 14) ($y + $i * 7)
        Start-Sleep -Milliseconds 30
    }
}

function ApproachRing($centre) {
    # The hub is a hole, so a pointer that never leaves it never enters the
    # overlay and the wheel never gets the drag data.
    $here = $script:cursor
    GlideTo ($centre[0]) ($centre[1] - 111) 6
    Start-Sleep -Milliseconds 250
    GlideTo $here[0] $here[1] 4
}

function SegmentPoint($centre, $labels, $label) {
    $index = [array]::IndexOf($labels, $label)
    if ($index -lt 0) { return $null }
    $bearing = ($index * (360.0 / $labels.Count)) * [Math]::PI / 180.0
    return @([int]($centre[0] + [Math]::Sin($bearing) * 111.0),
             [int]($centre[1] - [Math]::Cos($bearing) * 111.0))
}

function Win($title, $timeoutMs = 20000) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $title)
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $window = $UIA::RootElement.FindFirst($Tree::Children, $cond)
        if ($window) { return $window }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function El($window, $id, $timeoutMs = 6000) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $element = $window.FindFirst($Tree::Descendants, $cond)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 150
    }
    throw "no element with the automation id $id"
}

function SetText($element, $text) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue([string]$text)
    Start-Sleep -Milliseconds 120
}

function GetText($element) {
    try {
        return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    } catch {
        return $element.Current.Name
    }
}

function Press($element) {
    # A Button answers Invoke and a RadioButton answers SelectionItem. Toggle
    # is last and deliberately so: WPF implements it with OnToggle, which
    # changes IsChecked without raising Click, so a window that listened for
    # Click would look like it ignored the press.
    try {
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    catch {
        try {
            $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        }
        catch {
            $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        }
    }
    Start-Sleep -Milliseconds 200
}

function SetRange($element, $value) {
    $element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue([double]$value)
    Start-Sleep -Milliseconds 250
}

function ShootWindow($window, $name) {
    $r = $window.Current.BoundingRectangle
    $path = Join-Path $Dir $name
    $bitmap = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    L "screenshot $path ($([int]$r.Width)x$([int]$r.Height))"
    $null = $shots.Add($path)
    return $path
}

function ReadShot($path, $name) {
    # Read the screenshot back: it has to be a real window, not a blank rect.
    $bitmap = New-Object System.Drawing.Bitmap $path
    try {
        $seen = New-Object 'System.Collections.Generic.HashSet[int]'
        for ($y = 0; $y -lt $bitmap.Height; $y += 7) {
            for ($x = 0; $x -lt $bitmap.Width; $x += 7) {
                $null = $seen.Add($bitmap.GetPixel($x, $y).ToArgb())
            }
        }
        $ok = $bitmap.Width -gt 300 -and $bitmap.Height -gt 200 -and $seen.Count -gt 20
        Check "$name screenshot read back" $ok "$($bitmap.Width)x$($bitmap.Height), $($seen.Count) colours"
    }
    finally { $bitmap.Dispose() }
}

function CanvasRect($id, $timeoutMs = 8000, $atLeast = 1) {
    $line = WaitLog "editor canvas $([regex]::Escape($id)) at (\d+),(\d+) size (\d+)x(\d+) image (\d+)x(\d+)" $timeoutMs $atLeast
    if (-not $line) { throw "the $id editor never said where its canvas is" }
    return @{
        x = [int]$line.Groups[1].Value; y = [int]$line.Groups[2].Value
        w = [int]$line.Groups[3].Value; h = [int]$line.Groups[4].Value
        iw = [int]$line.Groups[5].Value; ih = [int]$line.Groups[6].Value
    }
}

function CanvasPoint($rect, $imageX, $imageY) {
    return @([int]($rect.x + ($imageX + 0.5) * $rect.w / $rect.iw),
             [int]($rect.y + ($imageY + 0.5) * $rect.h / $rect.ih))
}

function DragOnCanvas($rect, $fromX, $fromY, $toX, $toY) {
    $from = CanvasPoint $rect $fromX $fromY
    $to = CanvasPoint $rect $toX $toY
    L "canvas drag from image $fromX,$fromY ($($from[0]),$($from[1])) to $toX,$toY ($($to[0]),$($to[1]))"
    MoveTo $from[0] $from[1]
    Start-Sleep -Milliseconds 150
    [InEd]::Down()
    Start-Sleep -Milliseconds 120
    GlideTo $to[0] $to[1] 10
    Start-Sleep -Milliseconds 150
    [InEd]::Up()
    Start-Sleep -Milliseconds 250
}

function Ask($arguments) {
    $raw = & $Python $Check @arguments 2>&1 | Out-String
    try { return $raw | ConvertFrom-Json }
    catch { throw "the check script said: $raw" }
}

function Near($rgb, $want, $slack = 12) {
    for ($i = 0; $i -lt 3; $i++) {
        if ([Math]::Abs([int]$rgb[$i] - [int]$want[$i]) -gt $slack) { return $false }
    }
    return $true
}

function OpenEditorFrom($source, $label) {
    # One drag, one modifier tap, one drop on the named tool segment.
    $item = FindItem $source
    if (-not $item) { throw "$source is not visible in Explorer" }
    $drags = CountLog 'drag start at'
    StartDragFrom $item
    if (-not (WaitLog 'drag start at' 5000 ($drags + 1))) { throw "the hook did not see the drag of $source" }

    # Count every line to be waited for BEFORE the action that writes it. The
    # wheel draws its segments within milliseconds of opening, so a count taken
    # after the pointer has reached the ring is already too late and the wait
    # is for a line that will never come.
    $shown = CountLog 'wheel shown at'
    $drawn = CountLog 'wheel tool page 0: '
    [InEd]::Tap($VkCtrl)
    $line = WaitLog 'wheel shown at (\d+),(\d+)' 5000 ($shown + 1)
    if (-not $line) { throw 'the tools wheel never opened' }
    $centre = @([int]$line.Groups[1].Value, [int]$line.Groups[2].Value)
    ApproachRing $centre

    $tools = WaitLog 'wheel tool page 0: (.+)' 6000 ($drawn + 1)
    if (-not $tools) { throw 'the tools wheel never drew its segments' }
    $labels = @($tools.Groups[1].Value.Trim() -split ',')
    $target = SegmentPoint $centre $labels $label
    if (-not $target) { throw "segment $label is not on the tools wheel: $($labels -join ',')" }
    L "aiming at $label, index $([array]::IndexOf($labels, $label)) of $($labels.Count)"
    GlideTo $target[0] $target[1]
    Start-Sleep -Milliseconds 400
    [InEd]::Up()
    $drop = WaitLog "drop on $label with (\d+) file" 6000
    Check "$label opens from the wheel" ($drop -ne $null) "$($drop.Value)"
    return $labels
}

function WaitForOutput($path, $seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not (Test-Path $path) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    return (Test-Path $path)
}

$peelProcess = $null
$originalCursor = [System.Windows.Forms.Cursor]::Position
try {
    if (-not (Test-Path $App)) { throw "build the shell first: $App" }
    if (-not (Test-Path $Peel)) { throw "the engine is not there: $Peel" }
    if (-not (Test-Path $Python)) { throw "the python next to the engine is not there: $Python" }
    $made = Ask @('make', $test)
    Check 'fixtures made' ($made.files -ne $null) "$($made.folder): $(($made.files | Get-Member -MemberType NoteProperty).Name -join ',')"
    $red = $made.red; $green = $made.green; $grey = $made.grey; $flat = $made.flat

    # The wheel can only offer what the engine offers. Read it, so this run
    # keeps telling the truth as the registry grows.
    $offered = @((& $Peel options (Join-Path $test 'crop.png') | ConvertFrom-Json).wheels |
        Where-Object { $_.family -eq 'tool' } | ForEach-Object { $_.groups } |
        ForEach-Object { $_.entries } | Where-Object { $_.needs_editor } | ForEach-Object { $_.label })
    Check 'the engine offers the five image editors' ($offered.Count -eq 5) `
        "needs_editor tools for a PNG from $Peel : $($offered -join ',')"
    if ($offered.Count -ne 5) { throw "this engine has no image editors to test: $Peel" }

    Start-GuiGuard -Dir $Dir -IdleSeconds $IdleSeconds -IdleTimeout $IdleTimeout -Log { param($m) L $m }
    $originalCursor = [System.Windows.Forms.Cursor]::Position

    $env:PEEL_LOG = $peelLog
    $env:PEEL_EXIT_AFTER = '900'
    $env:PEEL_CLI = $Peel
    $peelProcess = Start-Process $App -PassThru
    L "started $App as pid $($peelProcess.Id)"
    Check 'app starts' ((WaitLog 'peel shell start' 15000) -ne $null) 'peel shell start in the log'
    Check 'registry preloads' ((WaitLog 'registry preloaded' 20000) -ne $null) 'registry preloaded in the log'

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3

    # --- Crop -----------------------------------------------------------------
    $canvasLines = CountLog 'editor canvas image\.crop at'
    $null = OpenEditorFrom 'crop.png' 'Crop'
    Check 'the drop did not run the job itself' ((WaitLog 'image\.crop: editor queued' 4000) -ne $null) `
        'the editor is queued off the OLE callback, not opened inside it'
    $crop = Win 'Crop crop.png'
    Check 'the Crop window opens' ($crop -ne $null) 'found by its title'
    if (-not $crop) { throw 'no Crop window' }
    $rect = CanvasRect 'image.crop' 12000 ($canvasLines + 1)
    L "crop canvas $($rect.x),$($rect.y) $($rect.w)x$($rect.h) for $($rect.iw)x$($rect.ih)"
    # A real mouse drag first: this is the handle path, which no test can reach
    # through UI Automation because a Canvas has no automation peer.
    DragOnCanvas $rect 40 40 240 200
    $dragged = (El $crop 'CropHint').Current.Name
    Check 'dragging on the picture moves the crop box' ($dragged -match 'Cropping to \d+ by \d+ at \d+,\d+') $dragged
    # Then the numeric fields, which is what the bead asks for.
    SetText (El $crop 'CropW') '200'
    SetText (El $crop 'CropH') '150'
    SetText (El $crop 'CropX') '200'
    SetText (El $crop 'CropY') '0'
    $said = (El $crop 'CropHint').Current.Name
    Check 'the window reports the box it will send' ($said -match 'Cropping to 200 by 150 at 200,0') $said
    ReadShot (ShootWindow $crop 'shot-crop.png') 'Crop'
    Press (El $crop 'CropApply')
    Check 'Crop sends the engine the numbers from the fields' `
        ((WaitLog 'image\.crop: editor applied h=150 w=200 x=200 y=0' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'crop (Crop).png'
    Check 'Crop wrote its output' (WaitForOutput $out) $out
    $size = Ask @('info', $out)
    Check 'the output is the size that was asked for' (($size.width -eq 200) -and ($size.height -eq 150)) `
        "$($size.width)x$($size.height)"
    $middle = Ask @('pixel', $out, '100', '75')
    Check 'the output is the part of the picture that was asked for' (Near $middle.rgb $green) `
        "centre is $($middle.rgb -join ',') and the green quarter is $($green -join ',')"

    # --- Edit -----------------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $null = OpenEditorFrom 'light.png' 'Edit'
    $edit = Win 'Edit light.png'
    Check 'the Edit window opens' ($edit -ne $null) 'found by its title'
    if (-not $edit) { throw 'no Edit window' }
    Press (El $edit 'EditRotate90')
    SetRange (El $edit 'EditBrightness') 2.0
    Start-Sleep -Milliseconds 400
    $readout = (El $edit 'EditBrightnessValue').Current.Name
    Check 'the brightness readout follows the slider' ($readout -eq '2.00') $readout
    ReadShot (ShootWindow $edit 'shot-edit.png') 'Edit'
    Press (El $edit 'EditApply')
    Check 'Edit sends only what was changed' `
        ((WaitLog 'image\.edit: editor applied brightness=2 rotate=90' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'light (Edit).png'
    Check 'Edit wrote its output' (WaitForOutput $out) $out
    $size = Ask @('info', $out)
    Check 'the output is turned a quarter turn' (($size.width -eq 300) -and ($size.height -eq 400)) `
        "$($size.width)x$($size.height) from 400x300"
    $lit = Ask @('pixel', $out, '150', '200')
    # One at a time: a comma binds tighter than a multiplication in
    # PowerShell, so the obvious one liner builds nested arrays instead.
    $doubled = @()
    foreach ($channel in $flat) { $doubled += ([int]$channel * 2) }
    Check 'every channel doubled, which is what brightness 2 means' (Near $lit.rgb $doubled 2) `
        "$($lit.rgb -join ',') against $($doubled -join ',')"

    # --- Annotate -------------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $null = OpenEditorFrom 'draw.png' 'Annotate'
    $draw = Win 'Annotate draw.png'
    Check 'the Annotate window opens' ($draw -ne $null) 'found by its title'
    if (-not $draw) { throw 'no Annotate window' }
    $rect = CanvasRect 'image.annotate'
    Press (El $draw 'AnnotateToolBox')
    Press (El $draw 'AnnotateInkGreen')
    Press (El $draw 'AnnotateWidthThick')
    DragOnCanvas $rect 40 40 160 120
    $drawn = (El $draw 'AnnotateHint').Current.Name
    Check 'the box is drawn' ($drawn -match '^1 shape') $drawn
    # Then a caption, which is the other half of the tool: type the words and
    # click where they go. It is also the one path that makes WPF measure text
    # in a font of its own, which is where the font cache bites.
    SetText (El $draw 'AnnotateText') 'Vineyard'
    Press (El $draw 'AnnotateToolText')
    $where = CanvasPoint $rect 220 200
    MoveTo $where[0] $where[1]
    Start-Sleep -Milliseconds 150
    [InEd]::Down(); Start-Sleep -Milliseconds 120; [InEd]::Up()
    Start-Sleep -Milliseconds 300
    $both = (El $draw 'AnnotateHint').Current.Name
    Check 'the caption is placed where it was clicked' ($both -match '^2 shapes') $both
    ReadShot (ShootWindow $draw 'shot-annotate.png') 'Annotate'
    Press (El $draw 'AnnotateApply')
    Check 'Annotate sends a shape list the engine parses' `
        ((WaitLog 'image\.annotate: editor applied shapes=\[\{"type":"rect"' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'draw (Annotate).png'
    Check 'Annotate wrote its output' (WaitForOutput $out) $out
    Check 'the engine drew both shapes' `
        ((WaitLog 'job end image\.annotate ok=True outputs=1 Drew 2 shapes' 10000) -ne $null) 'from the log'
    $edge = Ask @('pixel', $out, '100', '42')
    Check 'the ink landed on the edge of the box' (Near $edge.rgb @(52, 199, 89) 24) `
        "$($edge.rgb -join ',') against the green ink 52,199,89"
    $inside = Ask @('pixel', $out, '100', '80')
    Check 'the inside of the box is untouched' (Near $inside.rgb $red) `
        "$($inside.rgb -join ',') against the red quarter $($red -join ',')"

    # --- Redact ---------------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $null = OpenEditorFrom 'hide.png' 'Redact'
    $hide = Win 'Redact hide.png'
    Check 'the Redact window opens' ($hide -ne $null) 'found by its title'
    if (-not $hide) { throw 'no Redact window' }
    $rect = CanvasRect 'image.redact'
    Press (El $hide 'RedactModeBox')
    DragOnCanvas $rect 20 20 170 120
    $hidden = (El $hide 'RedactHint').Current.Name
    Check 'the region is drawn' ($hidden -match '^1 region, box') $hidden
    # Round 1: a drag that runs past the bottom right corner must reach the
    # last column and row, not stop one pixel short of them.
    DragOnCanvas $rect 340 240 ($rect.iw + 30) ($rect.ih + 30)
    $hidden = (El $hide 'RedactHint').Current.Name
    Check 'a drag past the corner is a second region' ($hidden -match '^2 regions, box') $hidden
    ReadShot (ShootWindow $hide 'shot-redact.png') 'Redact'
    Press (El $hide 'RedactApply')
    Check 'Redact sends the region and the way of hiding it' `
        ((WaitLog 'image\.redact: editor applied mode=box regions=\[\{"x":20' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'hide (Redact).png'
    Check 'Redact wrote its output' (WaitForOutput $out) $out
    $gone = Ask @('pixel', $out, '95', '70')
    Check 'the region is a black box' (Near $gone.rgb @(0, 0, 0) 6) "$($gone.rgb -join ',')"
    $kept = Ask @('pixel', $out, '300', '250')
    Check 'the rest of the picture is untouched' (Near $kept.rgb $grey) `
        "$($kept.rgb -join ',') against the grey quarter $($grey -join ',')"
    foreach ($corner in @(@(399, 299), @(399, 245), @(345, 299))) {
        $edge = Ask @('pixel', $out, "$($corner[0])", "$($corner[1])")
        Check "the edge pixel $($corner[0]),$($corner[1]) is hidden" (Near $edge.rgb @(0, 0, 0) 6) `
            "$($edge.rgb -join ',')"
    }

    # --- Metadata -------------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $null = OpenEditorFrom 'tags.jpg' 'Metadata'
    $meta = Win 'Metadata of tags.jpg'
    Check 'the Metadata window opens' ($meta -ne $null) 'found by its title'
    if (-not $meta) { throw 'no Metadata window' }
    $was = GetText (El $meta 'MetaTitle')
    Check 'the form arrives filled from the file' ($was -eq 'the old title') `
        "the Title field holds '$was', which the engine's action=view read"
    $tag = (El $meta 'MetaTagArtist').Current.Name
    Check 'the tags in the file are listed' ($tag -eq 'Artist') "the list holds a row named '$tag'"
    SetText (El $meta 'MetaTitle') 'Vineyard at dusk'
    SetText (El $meta 'MetaAuthor') 'Daniel'
    SetText (El $meta 'MetaLatitude') '32.4567'
    ReadShot (ShootWindow $meta 'shot-metadata.png') 'Metadata'
    Press (El $meta 'MetaApply')
    $problem = (El $meta 'MetaProblem').Current.Name
    Check 'half a location is refused in the window, not by a failed job' ($problem -match 'both') $problem
    SetText (El $meta 'MetaLongitude') '-116.6123'
    Press (El $meta 'MetaApply')
    Check 'Metadata sends set with the new fields' `
        ((WaitLog 'image\.metadata: editor applied action=set' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'tags (Metadata).jpg'
    Check 'Metadata wrote its output' (WaitForOutput $out) $out
    $tags = Ask @('tags', $out)
    Check 'the new title and author are in the output' `
        (($tags.title -eq 'Vineyard at dusk') -and ($tags.author -eq 'Daniel')) `
        "title '$($tags.title)', author '$($tags.author)'"
    Check 'the location is in the output' `
        (([Math]::Abs([double]$tags.latitude - 32.4567) -lt 0.001) -and
         ([Math]::Abs([double]$tags.longitude + 116.6123) -lt 0.001)) `
        "$($tags.latitude), $($tags.longitude)"

    # Round 1: emptying both location boxes removes the location and keeps
    # the rest. set alone keeps every tag it is not given, so the window
    # sends clear_location.
    Start-Sleep -Milliseconds 800
    $null = OpenEditorFrom 'tags (Metadata).jpg' 'Metadata'
    $again = Win 'Metadata of tags (Metadata).jpg'
    Check 'the Metadata window opens on the tagged output' ($again -ne $null) 'found by its title'
    if (-not $again) { throw 'no second Metadata window' }
    $lat = GetText (El $again 'MetaLatitude')
    Check 'the form shows the location it is about to remove' ($lat -match '^32\.45') "latitude '$lat'"
    SetText (El $again 'MetaLatitude') ''
    SetText (El $again 'MetaLongitude') ''
    SetText (El $again 'MetaLatitude') 'NaN'
    SetText (El $again 'MetaLongitude') '0'
    Press (El $again 'MetaApply')
    $problem = (El $again 'MetaProblem').Current.Name
    Check 'NaN is refused in the window' ($problem -match 'NaN') $problem
    SetText (El $again 'MetaLatitude') ''
    SetText (El $again 'MetaLongitude') ''
    Press (El $again 'MetaApply')
    Check 'emptied location boxes send clear_location' `
        ((WaitLog 'image\.metadata: editor applied action=set .*clear_location=true' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'tags (Metadata) (Metadata).jpg'
    Check 'the cleared copy was written' (WaitForOutput $out) $out
    $tags = Ask @('tags', $out)
    Check 'the location is gone and the title and author stay' `
        (($null -eq $tags.latitude) -and ($null -eq $tags.longitude) -and
         ($tags.title -eq 'Vineyard at dusk') -and ($tags.author -eq 'Daniel')) `
        "latitude '$($tags.latitude)', longitude '$($tags.longitude)', title '$($tags.title)', author '$($tags.author)'"

    # --- a photo stored turned ------------------------------------------------
    # The engine opens every image upright (image.load applies and drops the
    # EXIF Orientation tag) and a crop box means what the editor showed. WPF
    # does not apply the tag, so this file's preview has to come from the
    # engine, and the editor has to work in the upright 300 by 400, not the
    # stored 400 by 300.
    Start-Sleep -Milliseconds 800
    $turnedCanvas = CountLog 'editor canvas image\.crop at'
    $null = OpenEditorFrom 'turned.jpg' 'Crop'
    $turned = Win 'Crop turned.jpg'
    Check 'the Crop window opens for a photo stored turned' ($turned -ne $null) 'found by its title'
    if (-not $turned) { throw 'no Crop window for turned.jpg' }
    Check 'a rotated photo is rasterised by the engine, not by WPF' `
        ((WaitLog 'image\.crop: preview 300x400 via engine raster' 8000) -ne $null) 'from the log'
    $rect = CanvasRect 'image.crop' 12000 ($turnedCanvas + 1)
    Check 'the editor works in the upright size' (($rect.iw -eq 300) -and ($rect.ih -eq 400)) `
        "the canvas stands for $($rect.iw)x$($rect.ih), and the file on disk is 400x300"
    SetText (El $turned 'CropW') '150'
    SetText (El $turned 'CropH') '200'
    SetText (El $turned 'CropX') '150'
    SetText (El $turned 'CropY') '0'
    $said = (El $turned 'CropHint').Current.Name
    Check 'the upright box is the one it will send' ($said -match 'Cropping to 150 by 200 at 150,0') $said
    ReadShot (ShootWindow $turned 'shot-crop-turned.png') 'Crop of a turned photo'
    Press (El $turned 'CropApply')
    $out = Join-Path $test 'turned (Crop).jpg'
    Check 'the turned photo was cropped' (WaitForOutput $out) $out
    $size = Ask @('info', $out)
    Check 'the output is the upright box, not the stored one' `
        (($size.width -eq 150) -and ($size.height -eq 200)) "$($size.width)x$($size.height)"
    $quarter = Ask @('pixel', $out, '75', '100')
    Check 'the crop came from the upright top right quarter' (Near $quarter.rgb $green 24) `
        "centre is $($quarter.rgb -join ',') and the green quarter is $($green -join ',')"

    # --- Escape writes nothing ------------------------------------------------
    Start-Sleep -Milliseconds 800
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $null = OpenEditorFrom 'crop.png' 'Crop'
    $again = Win 'Crop crop.png'
    Check 'the Crop window opens a second time' ($again -ne $null) 'found by its title'
    if ($again) {
        $null = CanvasRect 'image.crop' 12000 ($turnedCanvas + 2)
        [InEd]::Tap($VkEsc)
        Check 'Escape closes the editor' ((WaitLog 'image\.crop: editor cancelled' 5000) -ne $null) 'from the log'
    }
    Start-Sleep -Seconds 2
    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'a cancelled editor writes nothing' ($new.Count -eq 0) "new files: $($new -join ',')"

    # --- the run as a whole ---------------------------------------------------
    $worst = [regex]::Matches((PeelLog), 'worst hook call ([\d.]+)ms')
    $ms = if ($worst.Count -gt 0) { [double]$worst[$worst.Count - 1].Groups[1].Value } else { 0 }
    Check 'the hooks stayed far under their budget' ($ms -lt 10) "worst hook call ${ms}ms against 10ms"
    Check 'no unhandled exceptions' (-not ((PeelLog) -match 'unhandled')) 'the log has no unhandled exception'
    Check 'every editor opened from the wheel' ((CountLog ': editor open') -ge 7) `
        "$(CountLog ': editor open') editor windows opened"
}
catch {
    $script:GuiAbort = Get-GuiAbort $_
    if ($script:GuiAbort) { L "ABORTED: $_" } else { Check 'driver completed' $false "$_" }
}
finally {
    # First: end any drag and let go of every button and key (gui-guard.ps1).
    try { L (Stop-GuiGuard).Text } catch { L "teardown failed: $_" }
    [System.Windows.Forms.Cursor]::Position = $originalCursor
    if ($peelProcess) { Stop-Process -Id $peelProcess.Id -ErrorAction SilentlyContinue }
    CloseTestExplorer
    $failed = @($checks | Where-Object { -not $_.ok })
    [pscustomobject]@{ ok = ($failed.Count -eq 0 -and -not $script:GuiAbort); aborted = $script:GuiAbort; checks = $checks; shots = $shots; log = $peelLog } |
        ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
    L ("done, {0} of {1} checks passed" -f ($checks.Count - $failed.Count), $checks.Count)
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}
