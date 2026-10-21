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
          without it.

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
    Set-Content $script:GuiLockPath 'taken by hand'
    try { Enter-GuiLock 1; $manual = 'TAKEN' } catch { $manual = "$_" }
    Check 'lock: a lock of unknown form is waited on, never removed' ($manual -match 'PEEL_ABORT user busy' -and (Test-Path $script:GuiLockPath)) $manual
    Remove-Item $script:GuiLockPath
}
else { Check 'lock: skipped, C:\peel-dev\gui.lock is held by another run right now' $true (Get-Content 'C:\peel-dev\gui.lock' -Raw -ErrorAction SilentlyContinue) }

Get-ChildItem $Dir -Directory | Where-Object { $_.Name -match '^(refuse|own|claim)-' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Finish-Checks ''
