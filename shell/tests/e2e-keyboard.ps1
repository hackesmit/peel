<#
Peel keyboard-path end to end test: select a file in Explorer, press the global
hotkey, steer the wheel's highlight with the arrow keys, press Enter, and check
that the engine wrote the file.

This has to run in the interactive session (session 1): a process started over
ssh lands in session 0, where there is no desktop and no foreground Explorer, so
run it through run-e2e.ps1 -Test keyboard, which schedules it with an
Interactive principal.

Nothing about the registry is hard coded. The wheel's own log line
("keyboard wheel convert page 0: MD,PDF,...") lists the segments clockwise from
twelve o'clock, and "keyboard highlight <label>" says where the highlight rests;
the script taps an arrow key until the highlight is the segment it wants, then
Enter. When a family lands in the engine the same script covers it.

Scenario A: select the source, press the hotkey, arrow to the named segment,
            Enter, expect the output next to the source.
Scenario B: select again, press the hotkey, press Escape, expect the wheel gone
            and nothing written.

The only mouse action is one click to select the file and give Explorer focus;
there is no drag, so nothing is ever dropped.
#>
param(
    [string]$Dir = 'C:\peel-dev\e2e-keyboard',
    [string]$App = 'C:\peel-dev\peel-5f8a-hq-plct-8\shell\Peel\bin\Release\net8.0-windows\Peel.exe',
    [string]$Peel = "$env:LOCALAPPDATA\Peel\venv\Scripts\peel.exe",
    [string]$Source = 'sample.txt',
    [string]$Segment = 'MD',
    [string]$Expect = 'sample.md'
)

$ErrorActionPreference = 'Stop'
$test = Join-Path $Dir 'testdir'
$driveLog = Join-Path $Dir 'drive.log'
$peelLog = Join-Path $Dir 'peel.log'
$resultPath = Join-Path $Dir 'result.json'
$checks = [System.Collections.ArrayList]::new()

New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item $driveLog, $peelLog, $resultPath -ErrorAction SilentlyContinue
Remove-Item $test -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $test | Out-Null

function L($message) {
    Add-Content $driveLog ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $message)
}

function Check($name, $ok, $detail) {
    $null = $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = "$detail" })
    L ("{0} {1}: {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

function PeelLog() {
    if (Test-Path $peelLog) { return (Get-Content $peelLog -Raw -ErrorAction SilentlyContinue) }
    return ''
}

function CountLog($pattern) {
    return [regex]::Matches((PeelLog), $pattern).Count
}

function WaitLog($pattern, $timeoutMs, $atLeast = 1) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $found = [regex]::Matches((PeelLog), $pattern)
        if ($found.Count -ge $atLeast) { return $found[$found.Count - 1] }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Highlight() {
    $m = [regex]::Matches((PeelLog), 'keyboard highlight (.+)')
    if ($m.Count -gt 0) { return $m[$m.Count - 1].Groups[1].Value.Trim() }
    return $null
}

Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class In {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public static void Place(int x, int y) { SetCursorPos(x, y); }
  public static void Down() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
  public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
  // down then up for one key, with the extended flag for the arrow keys.
  public static void TapEx(byte vk, bool ext) {
    uint f = ext ? 0x0001u : 0u;
    keybd_event(vk, 0, f, UIntPtr.Zero);
    System.Threading.Thread.Sleep(45);
    keybd_event(vk, 0, f | 0x0002u, UIntPtr.Zero);
  }
  public static void Tap(byte vk) { TapEx(vk, false); }
  public static void Key(byte vk, bool down) { keybd_event(vk, 0, down ? 0u : 0x0002u, UIntPtr.Zero); }
  // Ctrl+Alt+Enter, pressed as a chord and released cleanly.
  public static void Hotkey() {
    Key(0x11, true);  System.Threading.Thread.Sleep(40);
    Key(0x12, true);  System.Threading.Thread.Sleep(40);
    Key(0x0D, true);  System.Threading.Thread.Sleep(60);
    Key(0x0D, false); System.Threading.Thread.Sleep(40);
    Key(0x12, false); System.Threading.Thread.Sleep(40);
    Key(0x11, false);
  }
}
'@
[void][In]::SetProcessDPIAware()

$VkRight = [byte]0x27
$VkEnter = [byte]0x0D
$VkEsc = [byte]0x1B

function CloseTestExplorer() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $_.LocationURL -like '*peel-dev/e2e-keyboard/testdir*' } |
            ForEach-Object { $_.Quit() }
    } catch { }
}

function FindItem($name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $withExt = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $withoutExt = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $stem)
    $isItem = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $cond = New-Object System.Windows.Automation.AndCondition(
        $isItem, (New-Object System.Windows.Automation.OrCondition($withExt, $withoutExt)))
    for ($i = 0; $i -lt 25; $i++) {
        $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($item) { return $item }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# One click: select the file and make its Explorer window the foreground one,
# which is the window the hotkey reads the selection from. No drag.
function SelectItem($item) {
    $r = $item.Current.BoundingRectangle
    $x = [int]($r.X + 24); $y = [int]($r.Y + $r.Height / 2)
    L "click to select at $x,$y"
    [void][In]::Place($x, $y); Start-Sleep -Milliseconds 150
    [In]::Down(); Start-Sleep -Milliseconds 70; [In]::Up()
    Start-Sleep -Milliseconds 350
}

# Tap an arrow until the highlight is the wanted segment. Each wait is for a
# highlight line this loop produced, never one an earlier step left behind.
function AimAt($target, $labels) {
    for ($i = 0; $i -lt ($labels.Count + 2); $i++) {
        if ((Highlight) -eq $target) { return }
        $seen = CountLog 'keyboard highlight '
        [In]::TapEx($VkRight, $true)
        WaitLog 'keyboard highlight ' 3000 ($seen + 1) | Out-Null
    }
}

$peelProcess = $null
$originalCursor = [System.Windows.Forms.Cursor]::Position
try {
    Set-Content -Path (Join-Path $test $Source) -Value "Peel keyboard test`r`nline two`r`n" -Encoding UTF8
    L "source ready: $test\$Source"

    # The wheel can only offer what the engine offers; read it so this test
    # keeps telling the truth as the registry grows.
    $options = & $Peel options (Join-Path $test $Source) | ConvertFrom-Json
    $convert = $options.wheels | Where-Object { $_.family -eq 'convert' }
    $offered = @($convert.groups | ForEach-Object { $_.entries } | ForEach-Object { $_.label })
    Check 'engine offers the segment' ($offered -contains $Segment) "peel options convert wheel: $($offered -join ',')"
    if ($offered -notcontains $Segment) { throw "the engine does not offer a $Segment conversion for $Source" }

    $env:PEEL_LOG = $peelLog
    $env:PEEL_EXIT_AFTER = '240'
    $peelProcess = Start-Process $App -PassThru
    L "started $App as pid $($peelProcess.Id)"
    Check 'app starts' ((WaitLog 'peel shell start' 15000) -ne $null) 'peel shell start in the log'
    Check 'hotkey registers' ((WaitLog 'keyboard hotkey registered' 15000) -ne $null) 'Ctrl+Alt+Enter registered'
    Check 'registry preloads' ((WaitLog 'registry preloaded' 20000) -ne $null) 'registry preloaded in the log'

    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3
    $item = FindItem $Source
    if (-not $item) { throw "$Source is not visible in Explorer" }
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })

    # Scenario A: select, hotkey, arrow to the segment, Enter.
    SelectItem $item
    [In]::Hotkey()
    $shown = WaitLog 'keyboard wheel shown at (\d+),(\d+)' 6000
    Check 'hotkey opens the wheel' ($shown -ne $null) "$($shown.Value)"
    if (-not $shown) { throw 'the keyboard wheel never opened' }

    $drawn = WaitLog 'keyboard wheel convert page 0: (.+)' 6000
    Check 'conversion wheel is drawn from the engine' ($drawn -ne $null) "$($drawn.Value)"
    if (-not $drawn) { throw 'the conversion wheel never drew its segments' }
    $labels = @($drawn.Groups[1].Value.Trim() -split ',')
    if ($labels -notcontains $Segment) { throw "segment $Segment is not on the wheel: $($labels -join ',')" }

    # An arrow key reaches the focused wheel and drives the highlight. On a
    # one-segment wheel the highlight wraps back to the same segment; the point
    # here is that the key is plumbed through to the navigator (the movement
    # logic itself is pinned by KeyboardNavigatorTests). A busier registry makes
    # AimAt below step across real segments.
    $seen = CountLog 'keyboard highlight '
    [In]::TapEx($VkRight, $true)
    $moved = WaitLog 'keyboard highlight ' 3000 ($seen + 1)
    Check 'an arrow key drives the highlight' ($moved -ne $null) "highlight after Right: $($moved.Value)"

    AimAt $Segment $labels
    Check 'arrow keys reach the segment' ((Highlight) -eq $Segment) "highlight=$(Highlight), target=$Segment"

    [In]::Tap($VkEnter)
    $ran = WaitLog 'keyboard run (\S+) on (\d+) file' 6000
    Check 'Enter runs the highlighted segment' ($ran -ne $null) "$($ran.Value)"

    $expected = Join-Path $test $Expect
    $deadline = (Get-Date).AddSeconds(40)
    while (-not (Test-Path $expected) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Check 'the engine wrote the output' (Test-Path $expected) $expected

    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'exactly one new file' ($new.Count -eq 1) "new files: $($new -join ',')"

    # Scenario B: open again, Escape, nothing runs.
    Start-Sleep -Milliseconds 800
    $item = FindItem $Source
    if (-not $item) { throw "$Source vanished from Explorer" }
    $before = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $runsBefore = CountLog 'keyboard run '
    $shownSeen = CountLog 'keyboard wheel shown at'
    SelectItem $item
    [In]::Hotkey()
    $shown = WaitLog 'keyboard wheel shown at (\d+),(\d+)' 6000 ($shownSeen + 1)
    Check 'hotkey opens the wheel again' ($shown -ne $null) "$($shown.Value)"
    WaitLog 'keyboard wheel convert page 0: (.+)' 6000 | Out-Null
    [In]::Tap($VkEsc)
    $cancelled = WaitLog 'keyboard wheel hidden \(escape\)' 4000
    Check 'Escape closes the wheel' ($cancelled -ne $null) "$($cancelled.Value)"
    Start-Sleep -Seconds 2
    Check 'Escape runs nothing' ((CountLog 'keyboard run ') -eq $runsBefore) "runs seen: $(CountLog 'keyboard run ') (was $runsBefore)"
    $after = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    $new = @($after | Where-Object { $before -notcontains $_ })
    Check 'a cancelled hotkey writes nothing' ($new.Count -eq 0) "new files: $($new -join ',')"

    $text = PeelLog
    Check 'no unhandled exceptions' (-not ($text -match 'unhandled')) 'log has no unhandled exception'
}
catch {
    Check 'driver completed' $false "$_"
}
finally {
    [System.Windows.Forms.Cursor]::Position = $originalCursor
    if ($peelProcess) { Stop-Process -Id $peelProcess.Id -ErrorAction SilentlyContinue }
    CloseTestExplorer
    $failed = @($checks | Where-Object { -not $_.ok })
    $summary = [pscustomobject]@{
        ok     = ($failed.Count -eq 0)
        checks = $checks
        log    = $peelLog
        when   = (Get-Date).ToString('o')
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
    L ("done, {0} of {1} checks passed" -f ($checks.Count - $failed.Count), $checks.Count)
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}
