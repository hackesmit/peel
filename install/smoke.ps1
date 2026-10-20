<#
.SYNOPSIS
  Smoke test the INSTALLED Peel (%LOCALAPPDATA%\Peel\app\Peel.exe) on George.

.DESCRIPTION
  Run after publish.ps1. From hqbox:
    ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\smoke.ps1"

  Over ssh this lands in session 0, which has no desktop, so it re-runs itself
  with -Inner through a scheduled task with an Interactive principal (the way
  shell\tests\run-e2e.ps1 does) and prints the checks. Daniel has to be signed
  in. The real mouse and keyboard are driven: take C:\peel-dev\gui.lock first.

  What the inner run does, all against the installed build:
    1. Stops the installed Peel, so the test copy can hold the single instance
       lock, and puts it back running at the end.
    2. Conversion by drag: shell\tests\e2e-drag.ps1 with -App pointed at the
       installed exe (TXT to MD, plus its cancel and fall through scenarios).
    3. Settings window: starts Peel, starts it again (which opens the running
       Peel's settings), screenshots the window with PrintWindow and reads its
       controls back through UI Automation, then cancels.
    4. Settings take effect: the test Peel runs on a settings file with the
       tools and actions keys swapped and an output folder, so the tool (Zip,
       opened with Alt) must land in that folder, and the action (Copy Path,
       opened with Ctrl) must put the path on the clipboard.
    5. Soak: 100 hotkey presses and 100 drags with a wheel opened and
       cancelled, sampling Peel's handle, GDI, USER and thread counts.

  The Alt wheel's own six actions are bead hq-plct.9; Copy Path is the action
  the engine already ships, so it stands in for "one action" here.

  Evidence: C:\peel-dev\smoke\ (result.json, drive.log, peel.log, settings.png,
  drag\ for the drag driver's own result).
#>
param(
    [switch]$Inner,
    [string]$Dir = 'C:\peel-dev\smoke',
    [int]$Presses = 100,
    [int]$Drags = 100,
    [int]$TimeoutSeconds = 1500
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$app = Join-Path $env:LOCALAPPDATA 'Peel\app\Peel.exe'
$peel = Join-Path $env:LOCALAPPDATA 'Peel\venv\Scripts\peel.exe'
$resultPath = Join-Path $Dir 'result.json'
$driveLog = Join-Path $Dir 'drive.log'

# --- outer: schedule the inner run in the desktop session --------------------
if (-not $Inner) {
    if (-not (Test-Path $app)) { throw "Peel is not installed: run install\publish.ps1 first ($app)" }
    New-Item -ItemType Directory -Force $Dir | Out-Null
    Remove-Item $resultPath -ErrorAction SilentlyContinue
    $task = 'PeelSmoke'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Inner -Dir "{1}" -Presses {2} -Drags {3}' -f `
        $MyInvocation.MyCommand.Path, $Dir, $Presses, $Drags
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSeconds)
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    try {
        Start-ScheduledTask -TaskName $task
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            Start-Sleep -Seconds 5
            $state = (Get-ScheduledTask -TaskName $task).State
        } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        if ($state -eq 'Running') {
            Stop-ScheduledTask -TaskName $task
            Write-Output "TIMEOUT after $TimeoutSeconds seconds"
        }
    }
    finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path $resultPath)) {
        Write-Output 'RESULT: no result.json, the driver did not finish'
        if (Test-Path $driveLog) { Get-Content $driveLog -Tail 30 }
        exit 1
    }
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    foreach ($check in $result.checks) {
        Write-Output ("{0} {1}: {2}" -f $(if ($check.ok) { 'PASS' } else { 'FAIL' }), $check.name, $check.detail)
    }
    Write-Output ("RESULT: {0}" -f $(if ($result.ok) { 'PASS' } else { 'FAIL' }))
    if (-not $result.ok) { exit 1 }
    exit 0
}

# --- inner: runs in the signed in session --------------------------------------
$test = Join-Path $Dir 'testdir'
$outDir = Join-Path $Dir 'out'
$peelLog = Join-Path $Dir 'peel.log'
$settingsFile = Join-Path $Dir 'settings.json'
$defaultsFile = Join-Path $Dir 'defaults.json'
$shot = Join-Path $Dir 'settings.png'
$checks = [System.Collections.ArrayList]::new()
$samples = [System.Collections.ArrayList]::new()

New-Item -ItemType Directory -Force $Dir | Out-Null
Remove-Item $driveLog, $peelLog, $shot, $resultPath -ErrorAction SilentlyContinue
foreach ($folder in @($test, $outDir)) {
    Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $folder | Out-Null
}

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

function Highlight() {
    $m = [regex]::Matches((PeelLog), 'keyboard highlight (.+)')
    if ($m.Count -gt 0) { return $m[$m.Count - 1].Groups[1].Value.Trim() }
    return $null
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class In {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static void Move(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    int ax = (int)(((x - vx) * 65535.0) / (vw - 1)), ay = (int)(((y - vy) * 65535.0) / (vh - 1));
    mouse_event(0x0001 | 0x8000 | 0x4000, ax, ay, 0, UIntPtr.Zero);
  }
  public static void Place(int x, int y) { SetCursorPos(x, y); }
  public static void Down() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
  public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
  public static void TapEx(byte vk, bool ext) {
    uint f = ext ? 0x0001u : 0u;
    keybd_event(vk, 0, f, UIntPtr.Zero);
    System.Threading.Thread.Sleep(45);
    keybd_event(vk, 0, f | 0x0002u, UIntPtr.Zero);
  }
  public static void Tap(byte vk) { TapEx(vk, false); }
  public static void Key(byte vk, bool down) { keybd_event(vk, 0, down ? 0u : 0x0002u, UIntPtr.Zero); }
  public static void Chord(byte key) {
    Key(0x11, true);  System.Threading.Thread.Sleep(40);
    Key(0x12, true);  System.Threading.Thread.Sleep(40);
    Key(key, true);   System.Threading.Thread.Sleep(60);
    Key(key, false);  System.Threading.Thread.Sleep(40);
    Key(0x12, false); System.Threading.Thread.Sleep(40);
    Key(0x11, false);
  }
  public static void Shot(IntPtr hwnd, string path) {
    RECT r; GetWindowRect(hwnd, out r);
    using (var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top)) {
      using (var g = System.Drawing.Graphics.FromImage(bmp)) {
        IntPtr hdc = g.GetHdc();
        PrintWindow(hwnd, hdc, 2);
        g.ReleaseHdc(hdc);
      }
      bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
  }
}
'@ -ReferencedAssemblies System.Drawing
[void][In]::SetProcessDPIAware()

$VkRight = [byte]0x27
$VkEnter = [byte]0x0D
$VkEsc = [byte]0x1B
$VkP = [byte]0x50
$keys = @{ 'Shift' = [byte]0xA0; 'Ctrl' = [byte]0xA2; 'Alt' = [byte]0xA4 }
$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function Installed() {
    @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path -ieq $app
    })
}

function StartPeel([string]$settings) {
    $env:PEEL_LOG = $peelLog
    $env:PEEL_SETTINGS = $settings
    $env:PEEL_EXIT_AFTER = '1500'
    $env:PEEL_KEYBOARD_IDLE_MS = '20000'
    $process = Start-Process $app -PassThru
    L "started $app as pid $($process.Id) with settings $settings"
    return $process
}

function StopPeel($process) {
    if ($process) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function Sample($process, $label) {
    $p = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if (-not $p) { return }
    $p.Refresh()
    $row = [pscustomobject]@{
        at      = $label
        handles = $p.HandleCount
        gdi     = [In]::GetGuiResources($p.Handle, 0)
        user    = [In]::GetGuiResources($p.Handle, 1)
        threads = $p.Threads.Count
        mb      = [math]::Round($p.WorkingSet64 / 1MB, 1)
    }
    $null = $samples.Add($row)
    L ("sample {0}: handles {1} gdi {2} user {3} threads {4} ws {5} MB" -f $row.at, $row.handles, $row.gdi, $row.user, $row.threads, $row.mb)
}

function CloseTestExplorer() {
    try {
        (New-Object -ComObject Shell.Application).Windows() |
            Where-Object { $_.LocationURL -like '*peel-dev/smoke/*' } |
            ForEach-Object { $_.Quit() }
    } catch { }
}

function FindItem($name) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $withExt = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    $withoutExt = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $stem)
    $isItem = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $cond = New-Object System.Windows.Automation.AndCondition($isItem,
        (New-Object System.Windows.Automation.OrCondition($withExt, $withoutExt)))
    for ($i = 0; $i -lt 25; $i++) {
        $item = $UIA::RootElement.FindFirst($Scope::Descendants, $cond)
        if ($item) { return $item }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function ItemPoint($item) {
    $r = $item.Current.BoundingRectangle
    return @([int]($r.X + 24), [int]($r.Y + $r.Height / 2))
}

function SelectItem($item) {
    $p = ItemPoint $item
    [void][In]::Place($p[0], $p[1]); Start-Sleep -Milliseconds 150
    [In]::Down(); Start-Sleep -Milliseconds 70; [In]::Up()
    Start-Sleep -Milliseconds 350
}

function AimAt($target, $count) {
    for ($i = 0; $i -lt ($count + 2); $i++) {
        if ((Highlight) -eq $target) { return }
        $seen = CountLog 'keyboard highlight '
        [In]::TapEx($VkRight, $true)
        WaitLog 'keyboard highlight ' 3000 ($seen + 1) | Out-Null
    }
}

# Open the keyboard wheel on the selected file, switch with a key, run a label.
function RunByKeyboard($item, $switchKey, $family, $label) {
    SelectItem $item
    $shownSeen = CountLog 'keyboard wheel shown at'
    [In]::Chord($VkP)
    $shown = WaitLog 'keyboard wheel shown at' 6000 ($shownSeen + 1)
    if (-not $shown) { return "the hotkey did not open the wheel" }
    WaitLog 'keyboard wheel convert page 0: ' 6000 | Out-Null
    $drawnSeen = CountLog "keyboard wheel $family page 0: "
    [In]::Tap($keys[$switchKey])
    $drawn = WaitLog "keyboard wheel $family page 0: (.+)" 6000 ($drawnSeen + 1)
    if (-not $drawn) { return "$switchKey did not switch to the $family wheel" }
    $labels = @($drawn.Groups[1].Value.Trim() -split ',')
    if ($labels -notcontains $label) { return "$label is not on the $family wheel: $($labels -join ',')" }
    AimAt $label $labels.Count
    if ((Highlight) -ne $label) { return "the highlight never reached $label" }
    $runs = CountLog 'keyboard run '
    [In]::Tap($VkEnter)
    $ran = WaitLog 'keyboard run (\S+) on' 6000 ($runs + 1)
    if (-not $ran) { return "Enter ran nothing" }
    return "ran $($ran.Groups[1].Value)"
}

$smokePeel = $null
$wasRunning = (Installed).Count -gt 0
$originalCursor = [System.Windows.Forms.Cursor]::Position
try {
    if (-not (Test-Path $app)) { throw "not installed: $app" }
    foreach ($process in Installed) {
        L "stopping the installed Peel pid $($process.Id) for the run"
        StopPeel $process
    }

    # Defaults for the drag driver, so a setting Daniel changed cannot move
    # its output; the smoke settings swap the tools and actions keys, use an
    # output folder and a different hotkey, so they prove the settings work.
    [System.IO.File]::WriteAllText($defaultsFile, '{}')
    $smokeSettings = [ordered]@{
        modifiers        = [ordered]@{ Shift = 'Shift'; Ctrl = 'Alt'; Alt = 'Ctrl' }
        hotkey           = 'Ctrl+Alt+P'
        output           = 'folder'
        outputFolder     = $outDir
        quality          = 'smaller'
        startWithWindows = $true
    }
    [System.IO.File]::WriteAllText($settingsFile, ($smokeSettings | ConvertTo-Json))

    # 1. Conversion by drag, with the drag driver the shell bead proved.
    $dragDir = Join-Path $Dir 'drag'
    $env:PEEL_SETTINGS = $defaultsFile
    $dragScript = Join-Path $root 'shell\tests\e2e-drag.ps1'
    L "running $dragScript against $app"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dragScript -Dir $dragDir -App $app -Peel $peel | Out-Null
    $drag = Get-Content (Join-Path $dragDir 'result.json') -Raw | ConvertFrom-Json
    $failed = @($drag.checks | Where-Object { -not $_.ok } | ForEach-Object { $_.name })
    Check 'conversion by drag (installed build)' $drag.ok ("{0} checks, failed: {1}" -f @($drag.checks).Count, ($failed -join '; '))

    # 2. Settings window, opened by starting Peel a second time.
    $smokePeel = StartPeel $settingsFile
    Check 'installed Peel starts' ((WaitLog 'peel shell start' 15000) -ne $null) 'peel shell start'
    Check 'settings file is read' ((WaitLog 'settings: loaded .*hotkey Ctrl\+Alt\+P' 5000) -ne $null) "$((WaitLog 'settings: .*' 100).Value)"
    Check 'the hotkey comes from the settings' ((WaitLog 'keyboard hotkey registered: Ctrl\+Alt\+P' 10000) -ne $null) 'Ctrl+Alt+P registered'
    Check 'registry preloads' ((WaitLog 'registry preloaded' 20000) -ne $null) 'registry preloaded'
    Sample $smokePeel 'start'

    $second = Start-Process $app -PassThru
    $second.WaitForExit(10000) | Out-Null
    Check 'a second start exits and opens the settings' (((WaitLog 'settings window shown, (\d) wheel' 8000) -ne $null) -and $second.HasExited) "second pid $($second.Id) exited=$($second.HasExited)"
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, 'Peel settings')
    $window = $null
    for ($i = 0; $i -lt 20 -and -not $window; $i++) {
        $window = $UIA::RootElement.FindFirst($Scope::Children, $nameCond)
        if (-not $window) { Start-Sleep -Milliseconds 250 }
    }
    if ($window) {
        Start-Sleep -Milliseconds 600
        [In]::Shot([IntPtr]$window.Current.NativeWindowHandle, $shot)
        $texts = @($window.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } |
            ForEach-Object { $_.Current.Name })
        $combos = @($window.FindAll($Scope::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ComboBox))) | ForEach-Object {
            $pattern = $_.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
            $picked = @($pattern.Current.GetSelection() | ForEach-Object { $_.Current.Name }) -join '/'
            "$($_.Current.Name)=$picked"
        })
        $edits = @($window.FindAll($Scope::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit))) | ForEach-Object {
            "$($_.Current.Name)=" + $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        })
        L ("settings texts: " + ($texts -join ' | '))
        L ("settings combos: " + ($combos -join ' | '))
        L ("settings edits: " + ($edits -join ' | '))
        Check 'settings window screenshot' (Test-Path $shot) $shot
        Check 'settings show the swapped keys' ((($combos -join ';') -match 'Tools wheel key=Alt') -and (($combos -join ';') -match 'Actions wheel key=Ctrl')) ($combos -join '; ')
        Check 'settings show the hotkey and folder' ((($edits -join ';') -match 'Keyboard shortcut=Ctrl\+Alt\+P') -and (($edits -join ';') -match [regex]::Escape($outDir))) ($edits -join '; ')
        $cancel = $window.FindFirst($Scope::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, 'Cancel')))
        $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
        Check 'Cancel closes the settings' (-not $UIA::RootElement.FindFirst($Scope::Children, $nameCond)) 'window gone'
    }
    else {
        Check 'settings window found' $false 'no top level window named Peel settings'
    }

    # 3. Tool and action, through the keyboard path, with the keys swapped.
    $source = Join-Path $test 'sample.txt'
    Set-Content -Path $source -Value "Peel smoke`r`nline two`r`n" -Encoding UTF8
    CloseTestExplorer
    Start-Process explorer.exe $test | Out-Null
    Start-Sleep -Seconds 3
    $item = FindItem 'sample.txt'
    if (-not $item) { throw 'sample.txt is not visible in Explorer' }

    $tool = RunByKeyboard $item 'Alt' 'tool' 'Zip'
    $zip = Join-Path $outDir 'sample.zip'
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path $zip) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Check 'tool: Zip via Alt (remapped) writes to the output folder' (Test-Path $zip) "$tool; $zip; next to source: $(Test-Path (Join-Path $test 'sample.zip'))"

    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.Clipboard]::Clear()
    $item = FindItem 'sample.txt'
    $action = RunByKeyboard $item 'Ctrl' 'action' 'Copy Path'
    $clip = ''
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $clip = [System.Windows.Forms.Clipboard]::GetText()
        if ($clip) { break }
        Start-Sleep -Milliseconds 200
    }
    Check 'action: Copy Path via Ctrl (remapped) fills the clipboard' ($clip.Trim() -ieq $source) "$action; clipboard=$clip"

    # 4. Soak: hotkey presses.
    Sample $smokePeel 'before presses'
    $missed = 0
    $item = FindItem 'sample.txt'
    SelectItem $item
    for ($i = 1; $i -le $Presses; $i++) {
        $shownSeen = CountLog 'keyboard wheel shown at'
        $hiddenSeen = CountLog 'keyboard wheel hidden'
        [In]::Chord($VkP)
        if (-not (WaitLog 'keyboard wheel shown at' 4000 ($shownSeen + 1))) {
            $missed++
            $item = FindItem 'sample.txt'
            if ($item) { SelectItem $item }
            continue
        }
        [In]::Tap($VkEsc)
        WaitLog 'keyboard wheel hidden' 4000 ($hiddenSeen + 1) | Out-Null
        Start-Sleep -Milliseconds 150
        if ($i % 25 -eq 0) { Sample $smokePeel "after $i presses" }
    }
    Check "soak: $Presses hotkey presses open the wheel" ($missed -le 2) "missed $missed"

    # 5. Soak: drags with a wheel opened and cancelled, dropped back on the file.
    $missedDrags = 0
    for ($i = 1; $i -le $Drags; $i++) {
        $item = FindItem 'sample.txt'
        if (-not $item) { throw 'sample.txt vanished during the drag soak' }
        $start = ItemPoint $item
        $shownSeen = CountLog '(?<!keyboard )wheel shown at'
        [void][In]::Place($start[0], $start[1]); Start-Sleep -Milliseconds 120
        [In]::Move($start[0], $start[1]); Start-Sleep -Milliseconds 80
        [In]::Down(); Start-Sleep -Milliseconds 100
        for ($s = 1; $s -le 10; $s++) { [In]::Move($start[0] + $s * 12, $start[1] + $s * 6); Start-Sleep -Milliseconds 25 }
        [In]::Tap($keys['Shift'])
        if (-not (WaitLog '(?<!keyboard )wheel shown at' 3000 ($shownSeen + 1))) { $missedDrags++ }
        [In]::Tap($VkEsc)
        Start-Sleep -Milliseconds 150
        for ($s = 9; $s -ge 0; $s--) { [In]::Move($start[0] + $s * 12, $start[1] + $s * 6); Start-Sleep -Milliseconds 20 }
        [In]::Up()
        Start-Sleep -Milliseconds 300
        if ($i % 25 -eq 0) { Sample $smokePeel "after $i drags" }
    }
    Check "soak: $Drags drags open the wheel" ($missedDrags -le 2) "missed $missedDrags"
    $files = @(Get-ChildItem $test -File | ForEach-Object { $_.Name })
    Check 'soak wrote and moved nothing' (($files.Count -eq 1) -and ($files[0] -eq 'sample.txt')) ($files -join ',')

    # Lag or leak. An idle minute does not run the GC, so handles a finalizer
    # would free (released COM objects, disposed-late WPF sources) stay put.
    # dotnet-gcdump forces a full GC; the second one also collects what the
    # first one's finalizers released. A lagging count drops, a leak stays.
    Start-Sleep -Seconds 60
    Sample $smokePeel 'idle 60 s'
    $gcdump = Get-Command dotnet-gcdump -ErrorAction SilentlyContinue
    if (-not $gcdump) {
        $candidate = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-gcdump.exe'
        if (Test-Path $candidate) { $gcdump = Get-Item $candidate }
    }
    if ($gcdump) {
        foreach ($pass in 1, 2) {
            $dump = Join-Path $Dir "gc$pass.gcdump"
            # It warns on stderr, which Windows PowerShell 5.1 would turn
            # into a terminating error under Stop.
            $ErrorActionPreference = 'Continue'
            & $gcdump.Source collect -p $smokePeel.Id -o $dump 2>&1 | Out-Null
            $ErrorActionPreference = 'Stop'
            Remove-Item $dump -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
        }
        Sample $smokePeel 'after forced GC'
    }
    else {
        L 'dotnet-gcdump not installed (dotnet tool install -g dotnet-gcdump): no forced GC sample'
    }
    $first = $samples | Where-Object { $_.at -eq 'before presses' } | Select-Object -First 1
    $pressed = $samples | Where-Object { $_.at -eq "after $Presses presses" } | Select-Object -First 1
    $last = $samples[$samples.Count - 1]
    Check 'soak: handles return to the start level' (($last.handles - $first.handles) -lt 100) ("handles {0} before, {1} after the presses, {2} at '{3}'; gdi {4} to {5}, user {6} to {7}, threads {8} to {9}" -f `
        $first.handles, $pressed.handles, $last.handles, $last.at, $first.gdi, $last.gdi, $first.user, $last.user, $first.threads, $last.threads)
    Check 'soak: GDI and USER objects do not grow' ((($last.gdi - $first.gdi) -lt 50) -and (($last.user - $first.user) -lt 50)) "gdi +$($last.gdi - $first.gdi), user +$($last.user - $first.user)"

    $text = PeelLog
    Check 'no unhandled exceptions' (-not ($text -match 'unhandled')) 'peel.log has no unhandled exception'
}
catch {
    Check 'driver completed' $false "$_"
}
finally {
    [System.Windows.Forms.Cursor]::Position = $originalCursor
    StopPeel $smokePeel
    CloseTestExplorer
    Remove-Item Env:PEEL_LOG, Env:PEEL_SETTINGS, Env:PEEL_EXIT_AFTER, Env:PEEL_KEYBOARD_IDLE_MS -ErrorAction SilentlyContinue
    # Leave Daniel's Peel the way it was found: running, on his own settings.
    # Wait for the test copy to be gone first: while it holds the single
    # instance lock a new start only asks it for its settings and quits.
    if ($wasRunning) {
        $deadline = (Get-Date).AddSeconds(15)
        while (@(Installed).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        Start-Process $app | Out-Null
        Start-Sleep -Seconds 4
        $back = @(Installed).Count
        L "restarted the installed Peel: $back running"
        Check 'the installed Peel is running again' ($back -gt 0) "$back running"
    }
    $failed = @($checks | Where-Object { -not $_.ok })
    [pscustomobject]@{
        ok      = ($failed.Count -eq 0)
        checks  = $checks
        samples = $samples
    } | ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
}
