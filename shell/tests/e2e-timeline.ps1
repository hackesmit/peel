<#
Timeline and page editor end to end test: open each video, audio and PDF
editor FROM THE WHEEL with a real Explorer drag, drive it with the real mouse
and UI Automation, apply, and check what the engine wrote with ffprobe,
ffmpeg and PyMuPDF (e2e-timeline-check.py).

Run it through run-timeline.ps1, which schedules it in the interactive
session (session 1) and puts Daniel's installed Peel back if this run dies.

Before the first input it waits until George has had no user input for
-IdleSeconds (default 300, GetLastInputInfo), so it never moves the mouse
under someone who is using the machine, and gives up after -IdleTimeout.
Then it pauses the installed Peel (its single instance mutex would make the
test copy exit at once) and starts it again at the end, without this run's
PEEL_* variables.

One scenario per editor, each on its own source file:

  trim.mp4    Trim      drag the start handle on the bar with the mouse, play,
                        then type 1 and 2.5: a 1.5 s output.
  split.mp4   Split     double click the bar for a cut, click and Cut here for
                        another, then type 1, 2.5: three parts of 1, 1.5, 1.5 s.
  redact.mp4  Redact    drag a box on the engine's frame and a range on the
                        bar, then type exact numbers: the box is blurred from
                        1 s to 2 s and sharp after, and the rest is sharp.
  crop.mp4    Crop      drag a box, type 80,60 160x120: a 160 by 120 output.
  quiet.mp3   Bleep     drag a range over silence: a tone from 1 s to 2 s only.
  clip.mp3    Trim      type 1 and 3: a 2 s output.
  song.mp3    Metadata  the form arrives filled from the file's tags; an
                        unchanged form is refused; new title and album land.
  pages.pdf   Organize  drag page 3 before page 1 with the mouse, turn it,
                        delete page 2: PAGE 3 (turned 90) then PAGE 1.
  parts.pdf   Split     cut after page 2: two PDFs of two pages.

Then Escape on a Trim writes nothing, and no temporary frame or page folder
is left in %TEMP%\Peel.

Like e2e-editors.ps1, nothing about the wheel is hard coded: the segment is
aimed at from the wheel's own log line, a canvas from the editor's "editor
canvas" line, the bar from its "editor timeline" line, and a page card from
its UI Automation bounds.
#>
param(
    [string]$Dir = 'C:\peel-dev\e2e-timeline',
    [string]$App = '',
    [string]$Peel = 'C:\peel-dev\venv-64ec\Scripts\peel.exe',
    [string]$Check = '',
    [int]$IdleSeconds = 300,
    [int]$IdleTimeout = 3600
)
$Python = Join-Path (Split-Path -Parent $Peel) 'python.exe'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Check) { $Check = Join-Path $here 'e2e-timeline-check.py' }

$ErrorActionPreference = 'Stop'
$test = Join-Path $Dir 'testdir'
$driveLog = Join-Path $Dir 'drive.log'
$peelLog = Join-Path $Dir 'peel.log'
$resultPath = Join-Path $Dir 'result.json'
$pausedMarker = Join-Path $Dir 'installed-paused.txt'
$installedApp = Join-Path $env:LOCALAPPDATA 'Peel\app\Peel.exe'
$checks = [System.Collections.ArrayList]::new()
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
        Start-Sleep -Milliseconds 100
    }
    return $null
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class InTl {
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
  public static uint IdleMs() {
    var info = new LASTINPUTINFO(); info.cbSize = 8;
    GetLastInputInfo(ref info);
    return (uint)Environment.TickCount - info.dwTime;
  }
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
[void][InTl]::SetProcessDPIAware()

$VkCtrl = [byte]0xA2
$VkEsc = [byte]0x1B
$UIA = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

function MoveTo($x, $y) {
    [InTl]::Move([int]$x, [int]$y)
    $script:cursor = @([int]$x, [int]$y)
}

function GlideTo($x, $y, $steps = 8) {
    $from = $script:cursor
    for ($i = 1; $i -le $steps; $i++) {
        MoveTo ($from[0] + (($x - $from[0]) * $i / $steps)) ($from[1] + (($y - $from[1]) * $i / $steps))
        Start-Sleep -Milliseconds 35
    }
}

function ClickAt($x, $y) {
    MoveTo $x $y
    Start-Sleep -Milliseconds 120
    [InTl]::Down(); Start-Sleep -Milliseconds 80; [InTl]::Up()
    Start-Sleep -Milliseconds 250
}

function DoubleClickAt($x, $y) {
    MoveTo $x $y
    Start-Sleep -Milliseconds 120
    [InTl]::Down(); Start-Sleep -Milliseconds 40; [InTl]::Up()
    Start-Sleep -Milliseconds 60
    [InTl]::Down(); Start-Sleep -Milliseconds 40; [InTl]::Up()
    Start-Sleep -Milliseconds 300
}

function DragFromTo($from, $to) {
    MoveTo $from[0] $from[1]
    Start-Sleep -Milliseconds 150
    [InTl]::Down()
    Start-Sleep -Milliseconds 120
    GlideTo $to[0] $to[1] 12
    Start-Sleep -Milliseconds 150
    [InTl]::Up()
    Start-Sleep -Milliseconds 300
}

function TestWindows() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $_.LocationURL -like '*peel-dev/e2e-timeline/testdir*' }
    } catch { @() }
}

function CloseTestExplorer() {
    foreach ($window in @(TestWindows)) { try { $window.Quit() } catch { } }
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
    [void][InTl]::Place($x, $y); Start-Sleep -Milliseconds 250
    MoveTo $x $y; Start-Sleep -Milliseconds 120
    [InTl]::Down(); Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le 14; $i++) {
        MoveTo ($x + $i * 14) ($y + $i * 7)
        Start-Sleep -Milliseconds 30
    }
}

function ApproachRing($centre) {
    # The hub is a hole, so a pointer that never leaves it never enters the
    # overlay and the wheel never gets the drag data.
    $back = $script:cursor
    GlideTo ($centre[0]) ($centre[1] - 111) 6
    Start-Sleep -Milliseconds 250
    GlideTo $back[0] $back[1] 4
}

function SegmentPoint($centre, $labels, $label) {
    $index = [array]::IndexOf($labels, $label)
    if ($index -lt 0) { return $null }
    $bearing = ($index * (360.0 / $labels.Count)) * [Math]::PI / 180.0
    return @([int]($centre[0] + [Math]::Sin($bearing) * 111.0),
             [int]($centre[1] - [Math]::Cos($bearing) * 111.0))
}

function Win($title, $timeoutMs = 30000) {
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
    Start-Sleep -Milliseconds 150
}

function GetText($element) {
    try {
        return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    } catch {
        return $element.Current.Name
    }
}

function Hint($window, $id) { return (El $window $id).Current.Name }

function Press($element) {
    # Invoke for a Button, SelectionItem for a RadioButton, Toggle last
    # (a ToggleButton's Toggle sets IsChecked without a Click).
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

function CanvasRect($id, $timeoutMs = 20000, $atLeast = 1) {
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

function BarRect($id, $timeoutMs = 20000, $atLeast = 1) {
    $line = WaitLog "editor timeline $([regex]::Escape($id)) at (\d+),(\d+) size (\d+)x(\d+) duration (\d+)ms" $timeoutMs $atLeast
    if (-not $line) { throw "the $id editor never said where its timeline is" }
    return @{
        x = [int]$line.Groups[1].Value; y = [int]$line.Groups[2].Value
        w = [int]$line.Groups[3].Value; h = [int]$line.Groups[4].Value
        seconds = [int]$line.Groups[5].Value / 1000.0
    }
}

function BarPoint($bar, $seconds) {
    return @([int]($bar.x + [Math]::Min($bar.w - 1, $seconds / $bar.seconds * $bar.w)),
             [int]($bar.y + $bar.h / 2))
}

function Ask($arguments) {
    $raw = & $Python $Check @arguments 2>&1 | Out-String
    try { return $raw | ConvertFrom-Json }
    catch { throw "the check script said: $raw" }
}

function Near($value, $want, $slack) { return [Math]::Abs([double]$value - [double]$want) -le $slack }

function OpenEditorFrom($source, $label) {
    # One drag, one modifier tap, one drop on the named tool segment.
    $item = FindItem $source
    if (-not $item) { throw "$source is not visible in Explorer" }
    $drags = CountLog 'drag start at'
    StartDragFrom $item
    if (-not (WaitLog 'drag start at' 5000 ($drags + 1))) { throw "the hook did not see the drag of $source" }
    $shown = CountLog 'wheel shown at'
    $drawn = CountLog 'wheel tool page 0: '
    [InTl]::Tap($VkCtrl)
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
    $drops = CountLog "drop on $label with"
    [InTl]::Up()
    $drop = WaitLog "drop on $label with (\d+) file" 6000 ($drops + 1)
    Check "$label opens from the wheel for $source" ($drop -ne $null) "$($drop.Value)"
}

function WaitForOutput($path, $seconds = 90) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not (Test-Path $path) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    # The engine fills its claim in place: wait for the job line, not the name.
    return (Test-Path $path)
}

function JobsDone($id) { return (CountLog "job end $([regex]::Escape($id)) ok=True") }

function WaitJob($id, $before, $seconds = 90) {
    # $before is JobsDone taken BEFORE Apply: a job can end in 200 ms, before
    # a count taken after Apply.
    return (WaitLog "job end $([regex]::Escape($id)) ok=True" ($seconds * 1000) ($before + 1))
}

function WaitForIdle() {
    $deadline = (Get-Date).AddSeconds($IdleTimeout)
    $started = Get-Date
    while ([InTl]::IdleMs() -lt ($IdleSeconds * 1000)) {
        if ((Get-Date) -gt $deadline) {
            throw "George was in use: no $IdleSeconds s of user idle within $IdleTimeout s, so nothing was driven"
        }
        Start-Sleep -Seconds 10
    }
    L ("George idle for {0:0} s after waiting {1:0} s" -f ([InTl]::IdleMs() / 1000), ((Get-Date) - $started).TotalSeconds)
}

$peelProcess = $null
$paused = $false
$originalCursor = [System.Windows.Forms.Cursor]::Position
try {
    if (-not $App -or -not (Test-Path $App)) { throw "build the shell first: $App" }
    if (-not (Test-Path $Peel)) { throw "the engine is not there: $Peel" }
    if (-not (Test-Path $Python)) { throw "the python next to the engine is not there: $Python" }
    $made = Ask @('make', $test)
    Check 'fixtures made' ($made.files -ne $null) "$($made.folder): $(($made.files | Get-Member -MemberType NoteProperty).Name -join ',')"

    foreach ($pair in @(@('trim.mp4', 4), @('song.mp3', 3), @('pages.pdf', 2))) {
        $offered = @((& $Peel options (Join-Path $test $pair[0]) | ConvertFrom-Json).wheels |
            Where-Object { $_.family -eq 'tool' } | ForEach-Object { $_.groups } |
            ForEach-Object { $_.entries } | Where-Object { $_.needs_editor } | ForEach-Object { $_.id })
        Check "the engine offers the $($pair[0]) editors" ($offered.Count -eq $pair[1]) ($offered -join ',')
    }

    WaitForIdle
    $originalCursor = [System.Windows.Forms.Cursor]::Position

    # Daniel's Peel holds the single instance mutex; pause it for the run.
    $installed = @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $installedApp })
    if ($installed.Count -gt 0) {
        Set-Content $pausedMarker $installedApp
        $paused = $true
        foreach ($process in $installed) {
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
        L "paused the installed Peel ($($installed.Count) process)"
    }

    $env:PEEL_LOG = $peelLog
    $env:PEEL_EXIT_AFTER = '1500'
    $env:PEEL_CLI = $Peel
    $peelProcess = Start-Process $App -PassThru
    L "started $App as pid $($peelProcess.Id)"
    Check 'app starts' ((WaitLog 'peel shell start' 15000) -ne $null) 'peel shell start in the log'
    Check 'registry preloads' ((WaitLog 'registry preloaded' 20000) -ne $null) 'registry preloaded in the log'

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3

    # --- video Trim -----------------------------------------------------------
    $bars = CountLog 'editor timeline av\.video_trim at'
    OpenEditorFrom 'trim.mp4' 'Trim'
    $win = Win 'Trim trim.mp4'
    Check 'the Trim window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no Trim window' }
    $bar = BarRect 'av.video_trim' 20000 ($bars + 1)
    Check 'the bar stands for the whole file' (Near $bar.seconds 4 0.1) "$($bar.seconds) s on $($bar.w) px"
    Check 'Media Foundation plays the MP4' ((WaitLog 'av\.video_trim: media player opened' 10000) -ne $null) 'from the log'
    Check 'the frame strip comes from the engine' ((WaitLog 'av\.video_trim: frame strip of [1-9]' 30000) -ne $null) 'from the log'
    DragFromTo (BarPoint $bar 0) (BarPoint $bar 1.0)
    $said = Hint $win 'TimelineHint'
    Check 'dragging the start handle moves the start' ($said -match 'Keeping 0:0[01]\.\d{3} to 0:04\.000') $said
    Press (El $win 'TimelinePlay')
    Start-Sleep -Milliseconds 900
    $playhead = (El $win 'TimelinePlayhead').Current.Name
    Press (El $win 'TimelinePlay')
    Check 'Play moves the playhead from the start' ($playhead -match '^0:0[12]\.\d{3} of 0:04\.000') $playhead
    SetText (El $win 'TimelineStart') '1'
    SetText (El $win 'TimelineEnd') '2.5'
    $said = Hint $win 'TimelineHint'
    Check 'the window reports the span it will keep' ($said -match 'Keeping 0:01\.000 to 0:02\.500 \(1\.500 s\)') $said
    ReadShot (ShootWindow $win 'shot-trim.png') 'Trim'
    $jobs = JobsDone 'av.video_trim'
    Press (El $win 'TimelineApply')
    Check 'Trim sends the typed start and end' ((WaitLog 'av\.video_trim: editor applied end=2\.5 start=1\r?\n' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'trim (Trim).mp4'
    Check 'Trim wrote its output' ((WaitJob 'av.video_trim' $jobs) -ne $null -and (Test-Path $out)) $out
    $info = Ask @('probe', $out)
    Check 'the output is 1.5 s long' (Near $info.duration 1.5 0.15) "$($info.duration) s"

    # --- video Split ----------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $bars = CountLog 'editor timeline av\.video_split at'
    OpenEditorFrom 'split.mp4' 'Split'
    $win = Win 'Split split.mp4'
    Check 'the Split window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no Split window' }
    $bar = BarRect 'av.video_split' 20000 ($bars + 1)
    $p = BarPoint $bar 1.0
    DoubleClickAt $p[0] $p[1]
    $said = Hint $win 'TimelineHint'
    Check 'a double click on the bar adds a cut' ($said -match '^1 cut at 0:0[01]\.\d{3}, 2 parts') $said
    $p = BarPoint $bar 2.5
    ClickAt $p[0] $p[1]
    Press (El $win 'TimelineCutHere')
    $said = Hint $win 'TimelineHint'
    Check 'Cut here adds a cut at the playhead' ($said -match '^2 cuts, 3 parts') $said
    SetText (El $win 'TimelineCuts') '1, 2.5'
    $said = Hint $win 'TimelineHint'
    Check 'typed cuts replace the clicked ones' ($said -match '^2 cuts, 3 parts') "$said / $(GetText (El $win 'TimelineCuts'))"
    ReadShot (ShootWindow $win 'shot-split.png') 'Split'
    $jobs = JobsDone 'av.video_split'
    Press (El $win 'TimelineApply')
    Check 'Split sends the cut list' ((WaitLog 'av\.video_split: editor applied at=1,2\.5\r?\n' 5000) -ne $null) 'from the log'
    Check 'Split ran' ((WaitJob 'av.video_split' $jobs) -ne $null) 'job end in the log'
    $lengths = @()
    foreach ($index in 1..3) {
        $part = Join-Path $test "split (Split $index).mp4"
        $lengths += $(if (Test-Path $part) { (Ask @('probe', $part)).duration } else { -1 })
    }
    Check 'three parts of 1, 1.5 and 1.5 s' `
        ((Near $lengths[0] 1 0.15) -and (Near $lengths[1] 1.5 0.15) -and (Near $lengths[2] 1.5 0.15)) ($lengths -join ', ')

    # --- video Redact ---------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $canvases = CountLog 'editor canvas av\.video_redact at'
    $bars = CountLog 'editor timeline av\.video_redact at'
    OpenEditorFrom 'redact.mp4' 'Redact'
    $win = Win 'Redact redact.mp4'
    Check 'the Redact window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no Redact window' }
    $rect = CanvasRect 'av.video_redact' 20000 ($canvases + 1)
    Check 'the frame is the engine frame size' (($rect.iw -eq 320) -and ($rect.ih -eq 240)) "$($rect.iw)x$($rect.ih)"
    $bar = BarRect 'av.video_redact' 20000 ($bars + 1)
    DragFromTo (CanvasPoint $rect 40 40) (CanvasPoint $rect 139 119)
    $said = Hint $win 'TimelineHint'
    Check 'dragging on the frame draws the box' ($said -match 'box \d+ by \d+ at \d+,\d+') $said
    DragFromTo (BarPoint $bar 1.0) (BarPoint $bar 2.0)
    $said = Hint $win 'TimelineHint'
    Check 'dragging on the bar sets the range' ($said -match '^Blurred from 0:0[01]\.\d{3} to 0:0[12]\.\d{3}') $said
    SetText (El $win 'TimelineStart') '1'
    SetText (El $win 'TimelineEnd') '2'
    SetText (El $win 'RegionX') '40'
    SetText (El $win 'RegionY') '40'
    SetText (El $win 'RegionW') '100'
    SetText (El $win 'RegionH') '80'
    $said = Hint $win 'TimelineHint'
    Check 'the window reports the range and the box' `
        ($said -match 'Blurred from 0:01\.000 to 0:02\.000, box 100 by 80 at 40,40') $said
    ReadShot (ShootWindow $win 'shot-redact.png') 'Redact'
    $jobs = JobsDone 'av.video_redact'
    Press (El $win 'TimelineApply')
    Check 'Redact sends the box and the range' `
        ((WaitLog 'av\.video_redact: editor applied end=2 h=80 start=1 w=100 x=40 y=40\r?\n' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'redact (Redact).mp4'
    Check 'Redact wrote its output' ((WaitJob 'av.video_redact' $jobs) -ne $null -and (Test-Path $out)) $out
    $src = Join-Path $test 'redact.mp4'
    $before = Ask @('detail', $src, '1.5', '48', '48', '84', '64')
    $inside = Ask @('detail', $out, '1.5', '48', '48', '84', '64')
    $after = Ask @('detail', $out, '3.0', '48', '48', '84', '64')
    $beside = Ask @('detail', $out, '1.5', '200', '150', '80', '60')
    Check 'the box is blurred inside the range' (($before.edges -gt 40) -and ($inside.edges -lt 20)) `
        "edges $($before.edges) in the source, $($inside.edges) in the output at 1.5 s"
    Check 'the box is sharp after the range' ($after.edges -gt 40) "edges $($after.edges) at 3 s"
    Check 'the rest of the frame stays sharp' ($beside.edges -gt 40) "edges $($beside.edges) beside the box"

    # --- video Crop -----------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $canvases = CountLog 'editor canvas av\.video_crop at'
    OpenEditorFrom 'crop.mp4' 'Crop'
    $win = Win 'Crop crop.mp4'
    Check 'the Crop window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no Crop window' }
    $rect = CanvasRect 'av.video_crop' 20000 ($canvases + 1)
    DragFromTo (CanvasPoint $rect 0 0) (CanvasPoint $rect 159 119)
    $said = Hint $win 'TimelineHint'
    Check 'dragging on the frame sets the crop' ($said -match 'Cropping to \d+ by \d+ at \d+,\d+') $said
    SetText (El $win 'RegionX') '80'
    SetText (El $win 'RegionY') '60'
    SetText (El $win 'RegionW') '160'
    SetText (El $win 'RegionH') '120'
    $said = Hint $win 'TimelineHint'
    Check 'the window reports the crop it will send' ($said -match 'Cropping to 160 by 120 at 80,60') $said
    ReadShot (ShootWindow $win 'shot-crop.png') 'Video crop'
    $jobs = JobsDone 'av.video_crop'
    Press (El $win 'TimelineApply')
    Check 'Crop sends the box' ((WaitLog 'av\.video_crop: editor applied h=120 w=160 x=80 y=60\r?\n' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'crop (Crop).mp4'
    Check 'Crop wrote its output' ((WaitJob 'av.video_crop' $jobs) -ne $null -and (Test-Path $out)) $out
    $info = Ask @('probe', $out)
    Check 'the output is 160 by 120' (($info.width -eq 160) -and ($info.height -eq 120)) "$($info.width)x$($info.height)"

    # --- audio Bleep ----------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $bars = CountLog 'editor timeline av\.audio_bleep at'
    OpenEditorFrom 'quiet.mp3' 'Bleep'
    $win = Win 'Bleep quiet.mp3'
    Check 'the Bleep window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no Bleep window' }
    $bar = BarRect 'av.audio_bleep' 20000 ($bars + 1)
    DragFromTo (BarPoint $bar 1.0) (BarPoint $bar 2.0)
    $said = Hint $win 'TimelineHint'
    Check 'dragging on the bar adds a range' ($said -match '^1 range bleeped') $said
    SetText (El $win 'TimelineRanges') '1-2'
    $said = Hint $win 'TimelineHint'
    Check 'the typed range replaces the dragged one' ($said -match '^1 range bleeped \(1\.000 s\)') $said
    ReadShot (ShootWindow $win 'shot-bleep.png') 'Bleep'
    $jobs = JobsDone 'av.audio_bleep'
    Press (El $win 'TimelineApply')
    Check 'Bleep sends the range' ((WaitLog 'av\.audio_bleep: editor applied ranges=1-2\r?\n' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'quiet (Bleep).mp3'
    Check 'Bleep wrote its output' ((WaitJob 'av.audio_bleep' $jobs) -ne $null -and (Test-Path $out)) $out
    $tone = Ask @('level', $out, '1.2', '1.8')
    $quiet = Ask @('level', $out, '2.5', '3.5')
    Check 'the tone is inside the range and nowhere else' (($tone.rms -gt 0.05) -and ($quiet.rms -lt 0.005)) `
        "rms $($tone.rms) inside, $($quiet.rms) after"

    # --- audio Trim -----------------------------------------------------------
    Start-Sleep -Milliseconds 800
    $bars = CountLog 'editor timeline av\.audio_trim at'
    OpenEditorFrom 'clip.mp3' 'Trim'
    $win = Win 'Trim clip.mp3'
    Check 'the audio Trim window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no audio Trim window' }
    $null = BarRect 'av.audio_trim' 20000 ($bars + 1)
    Check 'Media Foundation plays the MP3' ((WaitLog 'av\.audio_trim: media player opened' 10000) -ne $null) 'from the log'
    SetText (El $win 'TimelineStart') '1'
    SetText (El $win 'TimelineEnd') '3'
    ReadShot (ShootWindow $win 'shot-audio-trim.png') 'Audio trim'
    $jobs = JobsDone 'av.audio_trim'
    Press (El $win 'TimelineApply')
    Check 'audio Trim sends the span' ((WaitLog 'av\.audio_trim: editor applied end=3 start=1\r?\n' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'clip (Trim).mp3'
    Check 'audio Trim wrote its output' ((WaitJob 'av.audio_trim' $jobs) -ne $null -and (Test-Path $out)) $out
    $info = Ask @('probe', $out)
    Check 'the output is 2 s long' (Near $info.duration 2 0.12) "$($info.duration) s"

    # --- audio Metadata -------------------------------------------------------
    Start-Sleep -Milliseconds 800
    OpenEditorFrom 'song.mp3' 'Metadata'
    $win = Win 'Metadata of song.mp3'
    Check 'the audio Metadata window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no audio Metadata window' }
    $was = GetText (El $win 'AudioMetaTitle')
    Check 'the form arrives filled from the file' ($was -eq 'Old title') "title '$was'"
    Press (El $win 'AudioMetaApply')
    $problem = (El $win 'AudioMetaProblem').Current.Name
    Check 'an unchanged form is refused in the window' ($problem -match 'Change a field first') $problem
    SetText (El $win 'AudioMetaTitle') 'New title'
    SetText (El $win 'AudioMetaAlbum') 'Peel album'
    ReadShot (ShootWindow $win 'shot-audio-metadata.png') 'Audio metadata'
    $jobs = JobsDone 'av.audio_metadata'
    Press (El $win 'AudioMetaApply')
    Check 'Metadata sends only the changed fields' `
        ((WaitLog 'av\.audio_metadata: editor applied album=Peel album title=New title\r?\n' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'song (Metadata).mp3'
    Check 'Metadata wrote its output' ((WaitJob 'av.audio_metadata' $jobs) -ne $null -and (Test-Path $out)) $out
    $info = Ask @('probe', $out)
    Check 'the new tags are in the output and the artist stays' `
        (($info.title -eq 'New title') -and ($info.album -eq 'Peel album') -and ($info.artist -eq 'Old artist')) `
        "title '$($info.title)', album '$($info.album)', artist '$($info.artist)'"

    # --- PDF Organize ---------------------------------------------------------
    Start-Sleep -Milliseconds 800
    OpenEditorFrom 'pages.pdf' 'Organize'
    $win = Win 'Organize pages.pdf'
    Check 'the Organize window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no Organize window' }
    Start-Sleep -Milliseconds 600
    $one = (El $win 'Page1').Current.BoundingRectangle
    $three = (El $win 'Page3').Current.BoundingRectangle
    DragFromTo @([int]($three.X + $three.Width / 2), [int]($three.Y + $three.Height / 2)) `
        @([int]($one.X + 12), [int]($one.Y + $one.Height / 2))
    $said = Hint $win 'PagesHint'
    Check 'dragging page 3 before page 1 reorders the pages' ($said -match 'in the order 3, 1, 2') $said
    Press (El $win 'Page3')
    Press (El $win 'PagesTurnRight')
    Press (El $win 'Page2')
    Press (El $win 'PagesDelete')
    $said = Hint $win 'PagesHint'
    Check 'the window reports order, deletion and turn' ($said -match 'Keeping 2 of 3 pages in the order 3, 1, 1 turned') $said
    ReadShot (ShootWindow $win 'shot-organize.png') 'Organize'
    $jobs = JobsDone 'pdf.organize'
    Press (El $win 'PagesApply')
    Check 'Organize sends the order and the turn' `
        ((WaitLog 'pdf\.organize: editor applied pages=3,1 rotate=\{"3":90\}\r?\n' 5000) -ne $null) 'from the log'
    $out = Join-Path $test 'pages (Organize).pdf'
    Check 'Organize wrote its output' ((WaitJob 'pdf.organize' $jobs) -ne $null -and (Test-Path $out)) $out
    $doc = Ask @('pdf', $out)
    Check 'the output is page 3 turned, then page 1' `
        (($doc.pages -eq 2) -and ($doc.texts[0] -eq 'PAGE 3') -and ($doc.texts[1] -eq 'PAGE 1') -and
         ($doc.rotations[0] -eq 90) -and ($doc.rotations[1] -eq 0)) `
        "$($doc.pages) pages: $($doc.texts -join ' | '), rotations $($doc.rotations -join ',')"

    # --- PDF Split ------------------------------------------------------------
    Start-Sleep -Milliseconds 800
    OpenEditorFrom 'parts.pdf' 'Split'
    $win = Win 'Split parts.pdf'
    Check 'the PDF Split window opens' ($win -ne $null) 'found by its title'
    if (-not $win) { throw 'no PDF Split window' }
    $said = Hint $win 'PagesHint'
    Check 'with no cut it says one file per page' ($said -match 'One file per page, 4 files') $said
    Press (El $win 'PagesCutAfter2')
    $said = Hint $win 'PagesHint'
    $ranges = GetText (El $win 'PagesRanges')
    Check 'a cut after page 2 makes two ranges' (($said -match '2 files: 1-2,3-4') -and ($ranges -eq '1-2,3-4')) "$said / '$ranges'"
    ReadShot (ShootWindow $win 'shot-pdf-split.png') 'PDF split'
    $jobs = JobsDone 'pdf.split'
    Press (El $win 'PagesApply')
    Check 'Split sends the ranges' ((WaitLog 'pdf\.split: editor applied ranges=1-2,3-4\r?\n' 5000) -ne $null) 'from the log'
    Check 'PDF Split ran' ((WaitJob 'pdf.split' $jobs) -ne $null) 'job end in the log'
    $first = Ask @('pdf', (Join-Path $test 'parts (Split 1-2).pdf'))
    $second = Ask @('pdf', (Join-Path $test 'parts (Split 3-4).pdf'))
    Check 'two PDFs of two pages each, in order' `
        (($first.texts -join '|') -eq 'PAGE 1|PAGE 2' -and ($second.texts -join '|') -eq 'PAGE 3|PAGE 4') `
        "$($first.texts -join '|') and $($second.texts -join '|')"

    # --- Escape writes nothing ------------------------------------------------
    Start-Sleep -Milliseconds 800
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $bars = CountLog 'editor timeline av\.video_trim at'
    OpenEditorFrom 'keep.mp4' 'Trim'
    $win = Win 'Trim keep.mp4'
    Check 'the Trim window opens on another file' ($win -ne $null) 'found by its title'
    if ($win) {
        $null = BarRect 'av.video_trim' 20000 ($bars + 1)
        $null = WaitLog 'av\.video_trim: frame strip of' 30000 2
        [InTl]::Tap($VkEsc)
        Check 'Escape closes the editor' ((WaitLog 'av\.video_trim: editor cancelled' 5000) -ne $null) 'from the log'
    }
    Start-Sleep -Seconds 2
    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'a cancelled editor writes nothing' ($new.Count -eq 0) "new files: $($new -join ',')"

    # --- the run as a whole ---------------------------------------------------
    $left = @(Get-ChildItem (Join-Path $env:TEMP 'Peel') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'frames-*' -or $_.Name -like 'pages-*' })
    Check 'no frame or page folder is left in the temp folder' ($left.Count -eq 0) ($left.Name -join ',')
    $worst = [regex]::Matches((PeelLog), 'worst hook call ([\d.]+)ms')
    $ms = if ($worst.Count -gt 0) { [double]$worst[$worst.Count - 1].Groups[1].Value } else { 0 }
    Check 'the hooks stayed far under their budget' ($ms -lt 10) "worst hook call ${ms}ms against 10ms"
    Check 'no unhandled exceptions' (-not ((PeelLog) -match 'unhandled')) 'the log has no unhandled exception'
    Check 'every editor opened from the wheel' ((CountLog ': editor open') -ge 10) "$(CountLog ': editor open') editor windows opened"
}
catch {
    Check 'driver completed' $false "$_"
}
finally {
    try { [InTl]::Up() } catch { }
    [System.Windows.Forms.Cursor]::Position = $originalCursor
    if ($peelProcess) {
        # Its editor windows go with it.
        Stop-Process -Id $peelProcess.Id -ErrorAction SilentlyContinue
        Wait-Process -Id $peelProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    CloseTestExplorer
    Remove-Item Env:PEEL_LOG, Env:PEEL_EXIT_AFTER, Env:PEEL_CLI -ErrorAction SilentlyContinue
    if ($paused) {
        if (@(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $installedApp }).Count -eq 0) {
            Start-Process -FilePath $installedApp
            L 'restarted the installed Peel'
        }
        Remove-Item $pausedMarker -ErrorAction SilentlyContinue
    }
    $failed = @($checks | Where-Object { -not $_.ok })
    [pscustomobject]@{ ok = ($failed.Count -eq 0); checks = $checks; shots = $shots; log = $peelLog } |
        ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
    L ("done, {0} of {1} checks passed" -f ($checks.Count - $failed.Count), $checks.Count)
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}
