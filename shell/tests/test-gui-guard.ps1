<#
Tests for the GUI guard (gui-guard.ps1, gui-teardown.ps1) that never drive
George. Run over ssh:
  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\test-gui-guard.ps1"
  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\shell\tests\test-gui-guard.ps1 -Interactive"

busy      A real driver (e2e-drag-abort.ps1) on a user who is never idle
          (PEEL_GUARD_FAKE_IDLE_MS=0) and who holds the left button and Shift
          (PEEL_GUARD_FAKE_DOWN=0x01,0xA0), with a short -IdleTimeout. It must
          abort as 'user busy' with no injection and no cleanup input (the
          counters and inject.log), make no test folder, and delete nothing
          (a file in the old fixed testdir stays). With -Interactive it runs
          in Daniel's desktop session through a one shot Interactive task
          (inside gui.lock), and also checks that the cursor did not move and
          that GetLastInputInfo did not change: injected input updates it, so
          an unchanged tick means nothing was injected. A change made by a
          person at George makes that check inconclusive, and it says so.
refuse    No guard input works before the drive opens: every driving call
          throws PEEL_GUARD, and every cleanup call is a no-op.
own       (session 0 only, where injected input reaches no desktop) a run
          that pressed Shift releases Shift and nothing else, though the
          left button and Shift both look held; its state on disk follows.
claim     gui-teardown.ps1 takes a state only when it is not done and its
          owner is dead, and a run that never drove gets no input at all.
lock      gui.lock is exclusive, a second taker gives up as 'user busy', and
          it is gone when its holder lets go; Invoke-GuiTask refuses to run
          without it. A dead runner's lock is taken over only after its
          driver (named by a RUN line) is killed and its run torn down. A
          driver is named by pid and start time: a pid reused by another
          process is never killed, and the lock is kept (not taken over)
          while a live pid cannot be told apart or a kill does not take.
restart   Stop-GuiGuard starts the paused installed Peel with no PEEL_*
          variable, though the driver set PEEL_CLI and PEEL_EXIT_AFTER (a
          probe stands in for Peel.exe; Daniel's Peel is never touched).
press     (session 0 only) a press whose state cannot be written is refused:
          no injection, nothing owned, nothing in inject.log.
sheets    share sheets on screen at the start are never the run's, and
          Close-ShareSheets touches none after a person touched George or
          when the start was never noted, and stops at the first sign of a
          person before the next close or bring to front (stand in windows:
          no real window gets a message).
clipboard (session 0 only, its own clipboard) Clear-ClipboardIfOurs and
          Clear-ClipboardTextIfOurs empty the clipboard only while it holds
          exactly the run's files or text, never when the run put nothing
          there, and hold it open from the check to the empty.

Exit 0 when every check passes, 1 otherwise.
#>
param(
    [switch]$Interactive,
    [switch]$Inner,
    [string]$Dir = 'C:\peel-dev\guard-test'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'gui-guard.ps1')
$checks = [System.Collections.ArrayList]::new()
function Check($name, $ok, $detail) {
    $null = $checks.Add([pscustomobject]@{ name = $name; ok = [bool]$ok; detail = "$detail" })
}
function Finish-Checks([string]$resultPath) {
    $failed = @($checks | Where-Object { -not $_.ok })
    if ($resultPath) {
        [pscustomobject]@{ ok = ($failed.Count -eq 0); checks = $checks } | ConvertTo-Json -Depth 4 | Set-Content $resultPath -Encoding UTF8
    }
    foreach ($c in $checks) { Write-Output ("{0} {1}: {2}" -f $(if ($c.ok) { 'PASS' } else { 'FAIL' }), $c.name, $c.detail) }
    Write-Output ("RESULT: {0} ({1} of {2} checks passed)" -f $(if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }), ($checks.Count - $failed.Count), $checks.Count)
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}

# ---- busy: a real driver that must never drive -------------------------------
function Test-Busy([string]$base, [bool]$desktop) {
    $d = Join-Path $base ('busy-{0:HHmmss}' -f (Get-Date))
    $old = Join-Path $d 'testdir'
    New-Item -ItemType Directory -Force $old | Out-Null
    $keep = Join-Path $old 'keep.txt'
    Set-Content $keep 'must survive a busy run'
    $env:PEEL_GUARD_FAKE_IDLE_MS = '0'
    $env:PEEL_GUARD_FAKE_DOWN = '0x01,0xA0'
    Add-Type -AssemblyName System.Windows.Forms
    $cursorBefore = [System.Windows.Forms.Cursor]::Position
    $tickBefore = [PeelGuard]::LastInputTick()
    $started = Get-Date
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'e2e-drag-abort.ps1') -Dir $d -Mode fail -IdleSeconds 2 -IdleTimeout 3 | Out-Null
    $code = $LASTEXITCODE
    $tickAfter = [PeelGuard]::LastInputTick()
    $cursorAfter = [System.Windows.Forms.Cursor]::Position
    Remove-Item Env:PEEL_GUARD_FAKE_IDLE_MS, Env:PEEL_GUARD_FAKE_DOWN
    $resultPath = Join-Path $d 'result.json'
    $result = if (Test-Path $resultPath) { Get-Content $resultPath -Raw | ConvertFrom-Json } else { $null }
    Check 'busy: the driver wrote its result' ($null -ne $result) "exit $code, $resultPath"
    if (-not $result) { return }
    Check 'busy: the run aborted as user busy' ($result.aborted -eq 'user busy') "aborted '$($result.aborted)', exit $code"
    Check 'busy: the run never opened the drive' (-not $result.drove) "drove $($result.drove)"
    Check 'busy: no driving injection' ($result.injections -eq 0) "injections $($result.injections)"
    Check 'busy: no cleanup input (no Escape, no release, no cursor restore)' ($result.cleanups -eq 0) "cleanups $($result.cleanups); $($result.teardown)"
    $log = Join-Path $result.runDir 'inject.log'
    Check 'busy: inject.log was never written' (-not (Test-Path $log)) $log
    $state = Get-ChildItem $result.runDir -Filter 'guard-state*.json' | Select-Object -First 1
    $s = if ($state) { Get-Content $state.FullName -Raw | ConvertFrom-Json } else { $null }
    Check 'busy: its state says done, never drove, owns nothing' ($s -and $s.done -and -not $s.drove -and @($s.owned).Count -eq 0) $(if ($state) { "$($state.Name): $(Get-Content $state.FullName -Raw)" } else { 'no state file' })
    Check 'busy: the teardown says nothing was driven' ($result.teardown -match 'nothing driven') $result.teardown
    Check 'busy: no scratch folder was made' (-not (Test-Path (Join-Path $result.runDir 'testdir'))) (Join-Path $result.runDir 'testdir')
    Check 'busy: nothing outside the run was deleted' (Test-Path $keep) $keep
    Check 'busy: the wait gave up within the -IdleTimeout' (((Get-Date) - $started).TotalSeconds -lt 60) ("{0:0.0} s" -f ((Get-Date) - $started).TotalSeconds)
    if ($desktop) {
        if ($tickAfter -eq $tickBefore) {
            Check 'busy (desktop): GetLastInputInfo did not change, so no input was injected' $true "tick $tickBefore"
            Check 'busy (desktop): the cursor did not move' ($cursorAfter -eq $cursorBefore) "before $cursorBefore, after $cursorAfter"
        }
        else {
            # Someone used George during the run; the counters above still hold.
            Check 'busy (desktop): GetLastInputInfo changed during the run (a person at George?), cursor check inconclusive' $true `
                "tick $tickBefore -> $tickAfter; cursor $cursorBefore -> $cursorAfter"
        }
    }
    Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Inner) {
    # In the desktop session, from the Interactive task below.
    New-Item -ItemType Directory -Force $Dir | Out-Null
    Test-Busy $Dir $true
    Finish-Checks (Join-Path $Dir 'interactive-result.json')
}

if ($Interactive) {
    # gui.lock, so no other run's injected input lands in the tick check.
    try { Enter-GuiLock 600 }
    catch {
        if (-not (Get-GuiAbort $_)) { throw }
        Write-Output "RESULT: ABORTED ($(Get-GuiAbort $_)): $_"; exit 3
    }
    try {
        New-Item -ItemType Directory -Force $Dir | Out-Null
        $resultPath = Join-Path $Dir 'interactive-result.json'
        Remove-Item $resultPath -ErrorAction SilentlyContinue
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $task = 'PeelGuardTest-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -Inner -Dir "{1}"' -f $MyInvocation.MyCommand.Path, $Dir)
        Register-ScheduledTask -TaskName $task -Action $action -Principal (New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited) -Force | Out-Null
        try {
            Start-ScheduledTask -TaskName $task
            $deadline = (Get-Date).AddSeconds(120)
            do { Start-Sleep -Seconds 2 } while (-not (Test-Path $resultPath) -and (Get-Date) -lt $deadline)
            Start-Sleep -Milliseconds 500
        }
        finally { Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue }
        if (-not (Test-Path $resultPath)) { Write-Output 'RESULT: FAIL (the interactive run wrote no result; is Daniel signed in?)'; exit 1 }
        $r = Get-Content $resultPath -Raw | ConvertFrom-Json
        foreach ($c in $r.checks) { Check "$($c.name)" $c.ok $c.detail }
    }
    finally { Exit-GuiLock }
    Finish-Checks ''
}

New-Item -ItemType Directory -Force $Dir | Out-Null
$session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId

# ---- busy, from here --------------------------------------------------------
Test-Busy $Dir $false

# ---- refuse: nothing before the drive opens ----------------------------------
$run = Join-Path $Dir ('refuse-{0:HHmmss}' -f (Get-Date))
New-Item -ItemType Directory -Force $run | Out-Null
[PeelGuard]::Open($run)
$refused = @()
foreach ($call in @({ [PeelGuard]::Move(10, 10) }, { [PeelGuard]::Place(10, 10) }, { [PeelGuard]::Down() },
        { [PeelGuard]::Tap([byte]0x1B) }, { [PeelGuard]::Key([byte]0xA0, $true) }, { [PeelGuard]::Hotkey() }, { [void][PeelGuard]::Front([long]0) })) {
    try { & $call; $refused += "ran: $call" } catch { if ("$_" -match 'PEEL_GUARD refused') { $refused += 'refused' } else { $refused += "other error: $_" } }
}
Check 'refuse: every driving call throws PEEL_GUARD before the drive' (@($refused | Where-Object { $_ -ne 'refused' }).Count -eq 0) ($refused -join '; ')
$noops = @([PeelGuard]::CleanupEscape(), [PeelGuard]::CleanupNudge(), [PeelGuard]::Release(1), [PeelGuard]::CleanupTap([byte]0x1B), [PeelGuard]::RestoreCursor(10, 10))
Check 'refuse: every cleanup call is a no-op before the drive' (@($noops | Where-Object { $_ }).Count -eq 0) ($noops -join ',')
$t = Invoke-GuiTeardown
Check 'refuse: the teardown of a run that never drove sends nothing' ($t.Cleanups -eq 0 -and [PeelGuard]::Injections -eq 0 -and $t.Actions -match 'nothing driven') $t.Text
Check 'refuse: no inject.log' (-not (Test-Path (Join-Path $run 'inject.log'))) $run

# ---- own: release only what the run pressed (session 0 only) ----------------
if ($session -eq 0) {
    $run = Join-Path $Dir ('own-{0:HHmmss}' -f (Get-Date))
    New-Item -ItemType Directory -Force $run | Out-Null
    [PeelGuard]::Open($run)
    [PeelGuard]::BeginDrive()
    [PeelGuard]::Key([byte]0xA0, $true)
    $state = Get-Content (Join-Path $run 'guard-state.json') -Raw | ConvertFrom-Json
    Check 'own: the state on disk records the pressed Shift' ((@($state.owned) -join ',') -eq '160' -and $state.driving) (Get-Content (Join-Path $run 'guard-state.json') -Raw)
    Check 'own: the injection was counted and logged' ([PeelGuard]::Injections -eq 1 -and (Get-Content (Join-Path $run 'inject.log')) -match 'drive key 0xa0 down') ((Get-Content (Join-Path $run 'inject.log')) -join ' | ')
    [PeelGuard]::EndDrive()
    $claim = Request-GuiState $run 'driver'
    [PeelGuard]::MovedTo($claim.Path)
    $t = Invoke-GuiTeardown
    [PeelGuard]::Finish()
    $log = @(Get-Content (Join-Path $run 'inject.log'))
    Check 'own: the teardown released Shift and nothing else' (($log | Where-Object { $_ -match 'cleanup' }) -join '|' -match '^[^|]*cleanup release 0xa0$') ($log -join ' | ')
    Check 'own: no Escape and no button release without an owned button' (-not ($log -match 'escape|nudge|release 0x01')) $t.Text
    $final = Get-Content $claim.Path -Raw | ConvertFrom-Json
    Check 'own: the claimed state is done and owns nothing' ($final.done -and @($final.owned).Count -eq 0 -and -not (Test-Path (Join-Path $run 'guard-state.json'))) (Get-Content $claim.Path -Raw)
}
else { Check 'own: skipped outside session 0 (injected input would reach the desktop)' $true "session $session" }

# ---- claim: one owner, and only for a dead owner's unfinished state ---------
function New-State([string]$name, [int]$ownerPid, [bool]$drove, [bool]$done, [int[]]$owned) {
    $d = Join-Path $Dir ('claim-{0}-{1:HHmmssfff}' -f $name, (Get-Date))
    New-Item -ItemType Directory -Force $d | Out-Null
    [pscustomobject]@{ pid = $ownerPid; driving = $false; drove = $drove; done = $done; owned = $owned; injections = 0; cleanups = 0 } |
        ConvertTo-Json | Set-Content (Join-Path $d 'guard-state.json')
    return $d
}
$dead = 999999
$teardown = Join-Path $here 'gui-teardown.ps1'
function Run-Teardown([string]$d) {
    $report = Join-Path $d 'report.json'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $teardown -RunDir $d -Report $report | Out-Null
    return (Get-Content $report -Raw | ConvertFrom-Json)
}
$d = New-State 'never' $dead $false $false @()
$r = Run-Teardown $d
Check 'claim: a dead run that never drove is claimed and gets no input' ($r.claimed -and $r.cleanups -eq 0 -and -not (Test-Path (Join-Path $d 'inject.log'))) "$($r.text)"
$d = New-State 'live' $PID $true $false @(1)
$r = Run-Teardown $d
Check 'claim: a state whose owner is alive is left to its owner' (-not $r.claimed -and (Test-Path (Join-Path $d 'guard-state.json'))) "$($r.text)"
$d = New-State 'done' $dead $true $true @()
$r = Run-Teardown $d
Check 'claim: a finished state is not torn down again' (-not $r.claimed -and $r.cleanups -eq 0) "$($r.text)"
if ($session -eq 0) {
    $d = New-State 'held' $dead $true $false @(0xA0)
    $r = Run-Teardown $d
    $r2 = Run-Teardown $d
    $log = @(Get-Content (Join-Path $d 'inject.log') -ErrorAction SilentlyContinue)
    Check 'claim: a dead run that held Shift gets exactly one release, once' ($r.claimed -and -not $r2.claimed -and @($log).Count -eq 1 -and $log[0] -match 'cleanup release 0xa0') ("first: $($r.text) | second: $($r2.text) | log: $($log -join ' | ')")
}

# ---- restart: Daniel's Peel gets none of the driver's PEEL_* variables ------
$run = Join-Path $Dir ('restart-{0:HHmmss}' -f (Get-Date))
New-Item -ItemType Directory -Force $run | Out-Null
$envDump = Join-Path $run 'probe-env.txt'
$env:PEEL_CLI = 'C:\fake\peel.exe'; $env:PEEL_EXIT_AFTER = '240'; $env:PEEL_LOG = (Join-Path $run 'x.log')
$savedInstalled = $script:InstalledPeel
try {
    [PeelGuard]::Open($run)
    $script:InstalledPeel = Join-Path $run 'not-installed\Peel.exe'
    $script:GuiGuardPaused = (Get-Command powershell.exe).Source
    $script:GuiGuardPausedFor = Join-Path $run 'not-under-test\Peel.exe'
    $script:GuiGuardRestartArgs = '-NoProfile -Command "Get-ChildItem Env: | ForEach-Object {{ $_.Name }} | Set-Content ''{0}''"' -f $envDump
    $t = Stop-GuiGuard
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path $envDump) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Start-Sleep -Milliseconds 300
    $names = @(Get-Content $envDump -ErrorAction SilentlyContinue)
    $peelVars = @($names | Where-Object { $_ -like 'PEEL_*' })
    Check 'restart: Stop-GuiGuard restarted the paused Peel' ($t.Text -match 'restarted the installed Peel') $t.Text
    Check 'restart: the restarted process has no PEEL_* variable' ($names.Count -gt 0 -and $peelVars.Count -eq 0) ("{0} variables, PEEL_*: {1}" -f $names.Count, ($peelVars -join ','))
    Check 'restart: the driver itself still has its PEEL_* variables' ($env:PEEL_CLI -and $env:PEEL_EXIT_AFTER) "PEEL_CLI=$env:PEEL_CLI"
    Check 'restart: the restart sent no input' ($t.Cleanups -eq 0 -and -not (Test-Path (Join-Path $run 'inject.log'))) $t.Text
}
finally {
    $script:InstalledPeel = $savedInstalled; $script:GuiGuardPaused = $null; $script:GuiGuardRestartArgs = ''
    Remove-Item Env:PEEL_CLI, Env:PEEL_EXIT_AFTER, Env:PEEL_LOG -ErrorAction SilentlyContinue
}

# ---- press: no record, no press (session 0 only) ----------------------------
if ($session -eq 0) {
    $run = Join-Path $Dir ('press-{0:HHmmss}' -f (Get-Date))
    New-Item -ItemType Directory -Force $run | Out-Null
    [PeelGuard]::Open($run)
    [PeelGuard]::BeginDrive()
    # A folder where Save writes its temp file: the state write fails.
    New-Item -ItemType Directory -Force (Join-Path $run 'guard-state.json.tmp') | Out-Null
    $before = [PeelGuard]::Injections
    try { [PeelGuard]::Key([byte]0xA0, $true); $pressed = 'pressed' } catch { $pressed = "$_" }
    $log = @(Get-Content (Join-Path $run 'inject.log') -ErrorAction SilentlyContinue)
    Check 'press: a key press whose state cannot be written is refused' ($pressed -match 'PEEL_GUARD refused key 0xa0 down: guard-state.json could not be written') $pressed
    Check 'press: the refused press was not injected, counted or logged' ([PeelGuard]::Injections -eq $before -and -not ($log -match 'drive key 0xa0')) ("injections {0} -> {1}; log: {2}" -f $before, [PeelGuard]::Injections, ($log -join ' | '))
    Check 'press: the refused press is not owned' (@([PeelGuard]::Owned()).Count -eq 0) (@([PeelGuard]::Owned()) -join ',')
    try { [PeelGuard]::Down(); $down = 'pressed' } catch { $down = "$_" }
    Check 'press: a button press whose state cannot be written is refused' ($down -match 'PEEL_GUARD refused button 0x01 down' -and @([PeelGuard]::Owned()).Count -eq 0) $down
    Remove-Item (Join-Path $run 'guard-state.json.tmp') -Recurse -Force
    [PeelGuard]::Key([byte]0xA0, $true)
    $state = Get-Content (Join-Path $run 'guard-state.json') -Raw | ConvertFrom-Json
    Check 'press: once the state can be written the press is on record' ((@($state.owned) -join ',') -eq '160') (Get-Content (Join-Path $run 'guard-state.json') -Raw)
    [PeelGuard]::Key([byte]0xA0, $false)
    [PeelGuard]::EndDrive()
    [PeelGuard]::Finish()
}
else { Check 'press: skipped outside session 0 (injected input would reach the desktop)' $true "session $session" }

# ---- sheets: only the run's own, and not after a person ---------------------
. (Join-Path $here 'e2e-hygiene.ps1')
$own = @(Select-OwnShareSheets @(11, 22, 33) @(22))
Check 'sheets: a sheet on screen at the start is not the run''s' (($own -join ',') -eq '11,33') ($own -join ',')
Check 'sheets: no sheet is the run''s when nothing is new' (@(Select-OwnShareSheets @(22) @(22)).Count -eq 0) ''
function Run-Sheets([string]$name, [string]$body, [hashtable]$vars) {
    $d = Join-Path $Dir ('sheets-{0}-{1:HHmmssfff}' -f $name, (Get-Date))
    New-Item -ItemType Directory -Force $d | Out-Null
    foreach ($k in $vars.Keys) { Set-Item "Env:$k" $vars[$k] }
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command (". '{0}'; . '{1}'; [PeelGuard]::Open('{2}'); [PeelGuard]::BeginDrive(); {3}; (Close-ShareSheets).Text; [PeelGuard]::Finish()" -f
        (Join-Path $here 'gui-guard.ps1'), (Join-Path $here 'e2e-hygiene.ps1'), $d, $body)
    foreach ($k in $vars.Keys) { Remove-Item "Env:$k" }
    return [pscustomobject]@{ Text = "$out"; Injected = (Test-Path (Join-Path $d 'inject.log')) }
}
$r = Run-Sheets 'person' 'Register-ShareSheets | Out-Null' @{ PEEL_GUARD_FAKE_REAL = '1' }
Check 'sheets: none touched once a person touched George' ($r.Text -match 'not touched: a person touched George' -and -not $r.Injected) $r.Text
$r = Run-Sheets 'unnoted' '$null' @{}
Check 'sheets: none touched when the start was never noted' ($r.Text -match 'not touched: the sheets on screen at the start were not noted' -and -not $r.Injected) $r.Text
# Stand in windows (handles 101, 102): the finder, the close and the bring to
# front are replaced, so no real window gets a message. $script:at counts the
# finder's calls (1 Register, 2 the run's sheets, 3 those that stayed).
# Each body is one line with no double quotes (it goes through a native
# command line).
$fakes = '$script:at = 0; $script:person = $false; ' +
    'function Get-ShareSheetHandles { $script:at++; if ($script:at -eq 1) { @() } else { @(101, 102) } }; ' +
    'function Test-PersonTouched { $script:person }; ' +
    'function Show-ShareSheet([long]$Handle) { Write-Host (''FRONT '' + $Handle) }; ' +
    'Register-ShareSheets | Out-Null; '
$r = Run-Sheets 'midclose' ($fakes + 'function Send-ShareSheetClose([long]$Handle) { Write-Host (''CLOSE '' + $Handle); $script:person = $true }') @{}
Check 'sheets: a person seen after one close stops the next close' ($r.Text -match 'CLOSE 101' -and $r.Text -notmatch 'CLOSE 102|FRONT' -and $r.Text -match 'stopped: a person touched George' -and -not $r.Injected) $r.Text
$r = Run-Sheets 'midfront' ($fakes + 'function Send-ShareSheetClose([long]$Handle) { Write-Host (''CLOSE '' + $Handle); if ($Handle -eq 102) { $script:person = $true } }') @{}
Check 'sheets: a person seen after the closes stops the bring to front' ($r.Text -match 'CLOSE 101.*CLOSE 102' -and $r.Text -notmatch 'FRONT' -and $r.Text -match 'stopped: a person touched George' -and -not $r.Injected) $r.Text

# ---- clipboard: only the run's own payload (session 0 only) ------------------
if ($session -eq 0) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $mine = Join-Path $Dir 'clip-sample.txt'
        Set-Content $mine 'x'
        $list = New-Object System.Collections.Specialized.StringCollection
        [void]$list.Add($mine)
        [System.Windows.Forms.Clipboard]::SetFileDropList($list)
        $t1 = Clear-ClipboardIfOurs @()
        Check 'clipboard: a run that put nothing there leaves it alone' ($t1 -match 'not touched' -and [System.Windows.Forms.Clipboard]::ContainsFileDropList()) $t1
        [System.Windows.Forms.Clipboard]::SetText('Daniel copied this')
        $t2 = Clear-ClipboardIfOurs @($mine)
        Check 'clipboard: something copied since is left alone' ($t2 -match 'no longer holds' -and [System.Windows.Forms.Clipboard]::GetText() -eq 'Daniel copied this') $t2
        [System.Windows.Forms.Clipboard]::SetFileDropList($list)
        $t3 = Clear-ClipboardIfOurs @($mine.ToUpperInvariant())
        Check 'clipboard: the run''s own file list is cleared' ($t3 -match 'cleared' -and -not [System.Windows.Forms.Clipboard]::ContainsFileDropList()) $t3
        # Between the check and the empty nobody else can open the clipboard,
        # so nothing copied there in between is emptied.
        [System.Windows.Forms.Clipboard]::SetFileDropList($list)
        $script:otherOpened = $null
        [PeelClipboard]::AfterCheck = [Action]{ $script:otherOpened = [PeelClipboard]::OtherThreadCanOpen() }
        try { $t4 = Clear-ClipboardIfOurs @($mine) } finally { [PeelClipboard]::AfterCheck = $null }
        Check 'clipboard: it stays open from the check to the empty' ($t4 -match 'cleared' -and $script:otherOpened -eq $false -and [PeelClipboard]::OtherThreadCanOpen()) "$t4; another thread opened it in between: $($script:otherOpened)"
        $payload = '"C:\peel-dev\smoke\testdir\sample.txt"'
        [System.Windows.Forms.Clipboard]::SetText('Daniel copied this')
        $t5 = Clear-ClipboardTextIfOurs $payload
        Check 'clipboard: text copied since is left alone' ($t5 -match 'no longer holds' -and [System.Windows.Forms.Clipboard]::GetText() -eq 'Daniel copied this') $t5
        $t6 = Clear-ClipboardTextIfOurs ''
        Check 'clipboard: a run that put no text there leaves it alone' ($t6 -match 'put nothing' -and [System.Windows.Forms.Clipboard]::GetText() -eq 'Daniel copied this') $t6
        [System.Windows.Forms.Clipboard]::SetText($payload)
        $t7 = Clear-ClipboardTextIfOurs $payload
        Check 'clipboard: the run''s own text is cleared' ($t7 -match 'cleared' -and -not [System.Windows.Forms.Clipboard]::ContainsText()) $t7
        [System.Windows.Forms.Clipboard]::Clear()
    }
    catch { Check 'clipboard: skipped, no clipboard in this session' $true "$_" }
}
else { Check 'clipboard: skipped outside session 0 (it is Daniel''s clipboard)' $true "session $session" }

# ---- lock -------------------------------------------------------------------
$lockFree = -not (Test-Path $script:GuiLockPath)
if ($lockFree) {
    $script:GuiLockPath = Join-Path $Dir 'test-gui.lock'
    Remove-Item $script:GuiLockPath -ErrorAction SilentlyContinue
    try { Invoke-GuiTask -Name 'PeelGuardTest' -Arguments '-Command exit' -Dir $Dir -ResultPath (Join-Path $Dir 'none.json'); $without = 'ran' }
    catch { $without = "$_" }
    Check 'lock: Invoke-GuiTask refuses to run without gui.lock' ($without -match 'gui.lock first') $without
    Enter-GuiLock 5
    $other = & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ". '$(Join-Path $here 'gui-guard.ps1')'; `$script:GuiLockPath = '$($script:GuiLockPath)'; try { Enter-GuiLock 1; 'TAKEN' } catch { `"`$_`" }"
    Check 'lock: a second taker gives up as user busy' ("$other" -match 'PEEL_ABORT user busy') "$other"
    Check 'lock: the lock file names its holder' ((Get-Content $script:GuiLockPath -Raw) -match "pid $PID") (Get-Content $script:GuiLockPath -Raw)
    Exit-GuiLock
    Check 'lock: the file is gone once released' (-not (Test-Path $script:GuiLockPath)) $script:GuiLockPath
    Set-Content $script:GuiLockPath 'LOCKED by C:\gone.ps1 pid 999999 at 2026-10-21T00:00:00'
    Enter-GuiLock 5
    Check 'lock: a lock left by a dead pid is taken over' ((Get-Content $script:GuiLockPath -Raw) -match "pid $PID") (Get-Content $script:GuiLockPath -Raw)
    Exit-GuiLock
    # A dead runner's lock that names a run whose driver still runs: the
    # driver is killed and its run torn down (here, not in the desktop)
    # before the lock is taken over.
    function Invoke-InteractiveTeardown([string]$RunDir, [string]$Report, [string]$Shot) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $teardown -RunDir $RunDir -Report $Report | Out-Null
        if (Test-Path $Report) { Get-Content $Report -Raw | ConvertFrom-Json }
    }
    $lockedAt = (Get-Date).AddSeconds(-5)
    $pidFile = Join-Path $Dir 'self-driver.pid'
    Write-DriverPid $pidFile
    $self = Read-DriverPid $pidFile
    Check 'lock: driver.pid names the driver by pid and start time' ($self.Pid -eq $PID -and (Get-DriverProcess $self).Id -eq $PID) (Get-Content $pidFile -Raw)
    Remove-Item $pidFile
    function Set-DriverPid([string]$RunDir, $Process, [long]$Shift = 0) {
        Set-Content (Join-Path $RunDir 'driver.pid') ('{0} {1}' -f $Process.Id, ($Process.StartTime.ToFileTimeUtc() + $Shift))
    }
    $sleeper = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 120'
    $d = New-State 'reclaim' $dead $false $false @()
    Set-DriverPid $d $sleeper
    Set-Content $script:GuiLockPath ("LOCKED by C:\gone.ps1 pid 999999 at {0:o}`r`nRUN {1}" -f $lockedAt, $d)
    Enter-GuiLock 10
    Start-Sleep -Milliseconds 300
    Check 'lock: a dead runner''s live driver is killed before the takeover' (-not (Get-Process -Id $sleeper.Id -ErrorAction SilentlyContinue)) "driver pid $($sleeper.Id)"
    $claimed = Join-Path $d 'guard-state.runner.json'
    Check 'lock: the dead runner''s run was torn down (state claimed, done)' ((Test-Path $claimed) -and (Get-Content $claimed -Raw | ConvertFrom-Json).done -and -not (Test-Path (Join-Path $d 'inject.log'))) $(if (Test-Path $claimed) { Get-Content $claimed -Raw } else { 'no runner state' })
    Check 'lock: then the lock is taken over' ((Get-Content $script:GuiLockPath -Raw) -match "pid $PID") (Get-Content $script:GuiLockPath -Raw)
    Add-GuiLockRun 'C:\peel-dev\x\runs\r1'
    Check 'lock: a run folder is noted in the held lock' ((Get-Content $script:GuiLockPath -Raw) -match 'RUN C:\\peel-dev\\x\\runs\\r1') (Get-Content $script:GuiLockPath -Raw)
    Exit-GuiLock
    # A live pid with another start time is not the dead runner's driver: it
    # is never killed, and the driver is gone, so the lock is taken over.
    $other = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 120'
    $d = New-State 'reused' $dead $false $false @()
    Set-DriverPid $d $other -1
    Set-Content $script:GuiLockPath ("LOCKED by C:\gone.ps1 pid 999999 at {0:o}`r`nRUN {1}" -f (Get-Date), $d)
    Enter-GuiLock 10
    Check 'lock: a reused pid (another start time) is never killed' ([bool](Get-Process -Id $other.Id -ErrorAction SilentlyContinue)) "pid $($other.Id)"
    Check 'lock: and the lock is taken over (its driver is gone)' ((Get-Content $script:GuiLockPath -Raw) -match "pid $PID") (Get-Content $script:GuiLockPath -Raw)
    Exit-GuiLock
    # A live pid that cannot be told apart (an old pid only driver.pid): not
    # killed, and the lock is kept.
    $d = New-State 'pidonly' $dead $false $false @()
    Set-Content (Join-Path $d 'driver.pid') $other.Id
    $deadLock = "LOCKED by C:\gone.ps1 pid 999999 at {0:o}`r`nRUN {1}" -f (Get-Date), $d
    Set-Content $script:GuiLockPath $deadLock
    try { Enter-GuiLock 1; $kept = 'TAKEN' } catch { $kept = "$_" }
    Check 'lock: a live pid with no start time is not killed and the lock is kept' ($kept -match 'PEEL_ABORT user busy' -and (Get-Process -Id $other.Id -ErrorAction SilentlyContinue) -and (Get-Content $script:GuiLockPath -Raw) -match 'pid 999999' -and -not (Test-Path (Join-Path $d 'guard-state.runner.json'))) $kept
    # A driver the kill does not stop (Stop-Process stood in for): the lock is kept.
    Set-DriverPid $d $other
    function Stop-Process { }
    try { Enter-GuiLock 1; $kept = 'TAKEN' } catch { $kept = "$_" }
    finally { Remove-Item Function:\Stop-Process }
    Check 'lock: a driver still alive after the kill keeps the lock' ($kept -match 'PEEL_ABORT user busy' -and (Get-Process -Id $other.Id -ErrorAction SilentlyContinue) -and (Get-Content $script:GuiLockPath -Raw) -match 'pid 999999' -and -not (Test-Path (Join-Path $d 'guard-state.runner.json'))) $kept
    Remove-Item $script:GuiLockPath
    Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue
    Set-Content $script:GuiLockPath 'taken by hand'
    try { Enter-GuiLock 1; $manual = 'TAKEN' } catch { $manual = "$_" }
    Check 'lock: a lock of unknown form is waited on, never removed' ($manual -match 'PEEL_ABORT user busy' -and (Test-Path $script:GuiLockPath)) $manual
    Remove-Item $script:GuiLockPath
}
else { Check 'lock: skipped, C:\peel-dev\gui.lock is held by another run right now' $true (Get-Content 'C:\peel-dev\gui.lock' -Raw -ErrorAction SilentlyContinue) }

Get-ChildItem $Dir -Directory | Where-Object { $_.Name -match '^(refuse|own|claim|restart|press|sheets)-' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Finish-Checks ''
