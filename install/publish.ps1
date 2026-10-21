<#
.SYNOPSIS
  Install or upgrade Peel for the current user on George. No admin needed.
  Idempotent: re-run it any time to upgrade in place.

.DESCRIPTION
  The app and the engine are one pair: versions\<stamp>\app and
  versions\<stamp>\venv. app and venv in the install root are junctions to
  the pair in use, which is where the Run entry and the app look. Switching
  pairs, and switching back, only moves those two junctions, so a file that
  stays locked in a failed build never blocks going back.

  install.json records the last verified pair (it stayed up for -StaySeconds)
  and the candidate (switched in, not verified yet). A verified pair is never
  deleted until a newer one is verified. A named mutex lets one publish run at
  a time on an install root (exit 5 if another one runs).

  1. Recovers first: a candidate left by an earlier run (killed in the middle
     of the switch, -NoStart, or nobody signed in) goes back to the verified
     pair, both junctions together, before anything is deleted. Then it drops
     the versions\* that neither pair uses.
  2. While the installed Peel keeps running: copies the engine source to
     versions\<stamp>\src, builds a new venv at versions\<stamp>\venv (the
     same pinned modules the current venv has, plus this engine, not
     editable), checks it with `peel doctor`, and publishes the shell
     self-contained to versions\<stamp>\app. Nothing the running Peel uses is
     touched yet, so a failure here leaves it as it was. With no venv at all
     (first install) it runs setup.ps1 first for the backends.
  3. Stops the installed Peel, saves settings.json as it is, records the
     candidate, and points app and venv at it. An install from before
     versions\ (app and venv real folders) moves into versions\ once as the
     verified pair.
  4. Writes the start with Windows entry (HKCU Run, value "Peel", created if
     the Run key is missing), unless the user turned it off in Settings. A
     settings file it cannot read leaves the entry as it is.
  5. Starts Peel in the signed in desktop session and watches it for
     -StaySeconds (10). Up the whole time: the candidate becomes the verified
     pair and the older versions go. Quit, never showed up, or the start
     itself failed: it stops the new build, puts settings.json back as it
     was, points app and venv at the verified pair, starts it and exits 6. If
     nobody is signed in it cannot tell, so it exits 3 and the next run goes
     back to the verified pair before it installs.

  Run it over ssh from hqbox, with the source synced to C:\peel-dev\<branch>:
    ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\publish.ps1"

  A process started over ssh lands in session 0, which has no desktop, so the
  start goes through a one shot scheduled task with an Interactive principal,
  the same way the GUI tests run.

  Exit codes: 0 installed and running (or -NoStart); 3 installed, not
  verified (nobody signed in); 4 the new build did not stay up and there was
  no previous build to go back to; 5 another publish runs on this install
  root; 6 the new build did not stay up and the previous one is back;
  anything else (an error before the switch) leaves the installed Peel as it
  was.

.PARAMETER Source
  The repository root to install from. Defaults to the folder above install\.

.PARAMETER NoStart
  Install and write the Run entry, but leave Peel stopped (the smoke run starts
  its own copy). Nothing checked that the new build starts, so it stays a
  candidate and the next run goes back to the verified pair first.

.PARAMETER InstallRoot, SettingsPath, RunKey, PrebuiltApp, EngineBuilder, StartTask
  For install\test-publish.ps1, which runs this against a scratch root. The
  installed app looks for its engine under %LOCALAPPDATA%\Peel only, so a
  real install always uses the defaults. PrebuiltApp is a folder used as the
  new build instead of dotnet publish. EngineBuilder is a script run as
  `& $EngineBuilder -Venv <dir> -Source <dir> -Current <venv or ''>` instead
  of the pip build and doctor check. StartTask names the scheduled task that
  starts Peel from session 0. The environment variable
  PEEL_PUBLISH_TEST_DIE=mid-switch kills the run after app is switched and
  before venv is, as a crash there would.
#>
[CmdletBinding()]
param(
    [string]$Source = '',
    [switch]$NoStart,
    [string]$InstallRoot = '',
    [string]$SettingsPath = '',
    [string]$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    [string]$PrebuiltApp = '',
    [string]$EngineBuilder = '',
    [int]$StaySeconds = 10,
    [string]$StartTask = ''
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in a param default.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Source) { $Source = Split-Path -Parent $here }
$defaultRoot = Join-Path $env:LOCALAPPDATA 'Peel'
if (-not $InstallRoot) { $InstallRoot = $defaultRoot }
if (-not $SettingsPath) { $SettingsPath = Join-Path $env:APPDATA 'Peel\settings.json' }
. (Join-Path $here 'LooseJson.ps1')

function Write-Step([string]$message) {
    Write-Host "[peel-publish] $message"
}

$peelHome = $InstallRoot
$app = Join-Path $peelHome 'app'
$venv = Join-Path $peelHome 'venv'
$versions = Join-Path $peelHome 'versions'
$journalPath = Join-Path $peelHome 'install.json'
$legacyEngineCopy = Join-Path $peelHome 'engine'
$exe = Join-Path $app 'Peel.exe'
$buildLog = Join-Path $peelHome 'publish.log'
if (-not $StartTask) { $StartTask = if ($peelHome -eq $defaultRoot) { 'PeelStart' } else { 'PeelStartScratch' } }

$project = Join-Path $Source 'shell\Peel\Peel.csproj'
$engineSource = Join-Path $Source 'engine'
if (-not $PrebuiltApp -and -not (Test-Path $project)) { throw "no shell project at $project" }
if (-not $EngineBuilder -and -not (Test-Path (Join-Path $engineSource 'pyproject.toml'))) { throw "no engine at $engineSource" }
New-Item -ItemType Directory -Force -Path $peelHome, $versions | Out-Null

# --- one publish at a time ---------------------------------------------------
# Global, so a run from another ssh session sees it too. A run that died
# leaves the mutex abandoned, which counts as free.
$sha = New-Object System.Security.Cryptography.SHA1Managed
$rootId = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($peelHome.ToLowerInvariant()))).Replace('-', '')
$installLock = New-Object System.Threading.Mutex($false, "Global\PeelPublish-$rootId")
try { $owned = $installLock.WaitOne(0) }
catch {
    $inner = $_.Exception.InnerException
    if (-not ($_.Exception -is [Threading.AbandonedMutexException] -or $inner -is [Threading.AbandonedMutexException])) { throw }
    $owned = $true
}
if (-not $owned) {
    Write-Step "another publish.ps1 runs on $peelHome; try again when it ends"
    exit 5
}

# --- links -------------------------------------------------------------------
function Test-Link([string]$path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    return ($null -ne $item) -and [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Get-LinkTarget([string]$path) {
    if (-not (Test-Link $path)) { return $null }
    return @((Get-Item -LiteralPath $path -Force).Target)[0]
}

# Directory.Delete without recursion removes the junction, never its target.
function Remove-Link([string]$path) {
    if (Test-Link $path) { [IO.Directory]::Delete($path, $false) }
}

function Set-Link([string]$path, [string]$target) {
    Remove-Link $path
    New-Item -ItemType Junction -Path $path -Target $target | Out-Null
}

function Move-WithRetry([string]$from, [string]$to) {
    for ($try = 1; $try -le 10; $try++) {
        try {
            Move-Item -LiteralPath $from -Destination $to -ErrorAction Stop
            return
        }
        catch {
            if ($try -eq 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

# A folder that will not go (a file in it still open) stays for the next run.
function Remove-Quietly([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    try { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop }
    catch { Write-Step "cannot remove $path yet ($($_.Exception.Message)); the next run tries again" }
}

# --- pairs and the journal ---------------------------------------------------
function New-Pair([string]$app, [string]$venv) {
    return [pscustomobject]@{ app = $app; venv = $venv }
}

function Read-Journal {
    if (-not (Test-Path $journalPath)) { return [pscustomobject]@{ verified = $null; candidate = $null } }
    return (Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json)
}

# Written to a side file and swapped in, so a crash leaves the old journal or
# the new one, never half of one.
function Write-Journal($verified, $candidate) {
    $text = ConvertTo-Json -Depth 3 ([pscustomobject]@{ verified = $verified; candidate = $candidate })
    $temp = "$journalPath.new"
    [IO.File]::WriteAllText($temp, $text)
    if (Test-Path $journalPath) { [IO.File]::Replace($temp, $journalPath, [NullString]::Value) }
    else { [IO.File]::Move($temp, $journalPath) }
}

# The pair app and venv point at now. A real folder there is an install from
# before versions\: it is named as the folder it moves to under versions\.
function Get-CurrentPair([string]$legacy) {
    if (-not (Test-Path $app)) { return $null }
    $appTarget = Get-LinkTarget $app
    if (-not $appTarget) { $appTarget = Join-Path $legacy 'app' }
    $venvTarget = Get-LinkTarget $venv
    if (-not $venvTarget) { $venvTarget = Join-Path $legacy 'venv' }
    return (New-Pair $appTarget $venvTarget)
}

# Points app and venv at a pair. A real folder still at app or venv moves to
# the pair's folder first (the one time move of an install from before
# versions\), so this is safe to run again after a crash.
function Use-Pair($pair) {
    foreach ($link in @(@($app, $pair.app), @($venv, $pair.venv))) {
        $path = $link[0]
        $target = $link[1]
        if ((Test-Path $path) -and -not (Test-Link $path)) {
            if (Test-Path $target) { throw "$path is a folder and $target is there already" }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Move-WithRetry $path $target
        }
        if ((Test-Path $target) -and (Get-LinkTarget $path) -ne $target) { Set-Link $path $target }
    }
}

# Drops each versions\* folder that neither the journal nor a junction uses.
function Remove-Unused {
    $journal = Read-Journal
    $keep = @(@($journal.verified, $journal.candidate, (New-Pair (Get-LinkTarget $app) (Get-LinkTarget $venv))) |
        Where-Object { $_ } | ForEach-Object { $_.app; $_.venv } | Where-Object { $_ } | ForEach-Object { Split-Path -Parent $_ })
    foreach ($old in @(Get-ChildItem -LiteralPath $versions -Directory -ErrorAction SilentlyContinue)) {
        if ($keep -contains $old.FullName) { continue }
        Write-Step "dropping an unused version: $($old.FullName)"
        Remove-Quietly $old.FullName
    }
}

# --- engine and shell, built while Peel still runs ---------------------------
# A native tool's stderr (pip warns there) must not stop the script.
function Invoke-Native([scriptblock]$block) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $block } finally { $ErrorActionPreference = $saved }
}

function Get-Missing([string]$peelExe) {
    $json = Invoke-Native { & $peelExe doctor | Out-String }
    if ($LASTEXITCODE -ne 0) { return @('peel doctor failed') }
    return @(($json | ConvertFrom-Json).missing)
}

# The new venv gets exactly the modules the current one has (pip freeze,
# without the engine itself), from the same base Python, then this engine.
function Build-EngineVenv([string]$target, [string]$src, [string]$from) {
    $currentPy = Join-Path $from 'Scripts\python.exe'
    $homeLine = Get-Content (Join-Path $from 'pyvenv.cfg') | Where-Object { $_ -match '^\s*home\s*=' } | Select-Object -First 1
    if (-not $homeLine) { throw "no home line in $from\pyvenv.cfg" }
    $basePy = Join-Path ($homeLine -replace '^\s*home\s*=\s*', '').Trim() 'python.exe'
    Write-Step "building the engine venv at $target (base $basePy)"
    Invoke-Native { & $basePy -m venv $target }
    $newPy = Join-Path $target 'Scripts\python.exe'
    if (-not (Test-Path $newPy)) { throw "venv creation failed at $target" }

    $frozen = Invoke-Native { & $currentPy -m pip freeze --exclude-editable }
    if ($LASTEXITCODE -ne 0) { throw "pip freeze failed in $from" }
    $requirements = Join-Path (Split-Path -Parent $target) 'requirements.txt'
    @($frozen | Where-Object { $_ -and $_ -notmatch '^\s*(#|-e\s)' -and $_ -notmatch '^peel[-_.]engine\b' }) |
        Set-Content -LiteralPath $requirements -Encoding Ascii
    Invoke-Native { & $newPy -m pip install --quiet --disable-pip-version-check -r $requirements }
    if ($LASTEXITCODE -ne 0) { throw 'installing the modules into the new venv failed' }
    Invoke-Native { & $newPy -m pip install --quiet --disable-pip-version-check --no-deps $src }
    if ($LASTEXITCODE -ne 0) { throw 'installing the engine into the new venv failed' }

    $missing = Get-Missing (Join-Path $target 'Scripts\peel.exe')
    if ($missing.Count -gt 0) {
        throw ("peel doctor on the new engine is not green (" + ($missing -join ', ') + "): run install\setup.ps1, then publish again")
    }
    Write-Step 'peel doctor: all backends found'
}

# --- stop and start ----------------------------------------------------------
function Get-Installed {
    @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ($_.Path.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $_.Path.StartsWith($versions + '\', [StringComparison]::OrdinalIgnoreCase))
    })
}

function Stop-Installed {
    $running = Get-Installed
    foreach ($process in $running) {
        Write-Step "stopping the installed Peel (pid $($process.Id))"
        # Peel holds its engines in kill on close job objects, so they go with it.
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    foreach ($process in $running) {
        Wait-Process -Id $process.Id -Timeout 15 -ErrorAction SilentlyContinue
    }
    return $running
}

function Test-SignedIn {
    return @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }).Count -gt 0
}

# 0 up for StaySeconds; 3 cannot tell (nobody signed in); 4 quit or never came up.
function Start-Installed {
    if (-not (Test-Path $exe)) {
        Write-Step "nothing to start: no $exe"
        return 4
    }
    if ([System.Diagnostics.Process]::GetCurrentProcess().SessionId -ne 0) {
        Start-Process -FilePath $exe
    }
    else {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument ('-NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath ''{0}''"' -f $exe)
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $StartTask -Action $action -Principal $principal -Force | Out-Null
        try {
            Start-ScheduledTask -TaskName $StartTask
            $deadline = (Get-Date).AddSeconds(20)
            do {
                Start-Sleep -Milliseconds 500
                $state = (Get-ScheduledTask -TaskName $StartTask).State
            } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        }
        finally {
            Unregister-ScheduledTask -TaskName $StartTask -Confirm:$false -ErrorAction SilentlyContinue
        }
    }

    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $now = @(Get-Installed | Where-Object { $_.SessionId -ne 0 })
    } while ($now.Count -eq 0 -and (Get-Date) -lt $deadline)
    if ($now.Count -eq 0) {
        if (-not (Test-SignedIn)) {
            Write-Step 'Peel did not start and nobody is signed in at the console. It will start at the next sign in.'
            return 3
        }
        Write-Step 'Peel did not start. See %LOCALAPPDATA%\Peel\logs\peel.log.'
        return 4
    }
    # Up for the whole window, not just launched: a build that crashes while
    # it loads, or a second instance, quits within it.
    $deadline = (Get-Date).AddSeconds($StaySeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (@(Get-Installed | Where-Object { $_.SessionId -ne 0 }).Count -eq 0) {
            Write-Step 'Peel started and then quit. See %LOCALAPPDATA%\Peel\logs\peel.log.'
            return 4
        }
    }
    $alive = @(Get-Installed | Where-Object { $_.SessionId -ne 0 })
    Write-Step ("Peel is running: pid {0}, session {1}, up {2} s" -f $alive[0].Id, $alive[0].SessionId, $StaySeconds)
    return 0
}

# --- settings ----------------------------------------------------------------
# On, off, or $null when the file cannot be read: then the entry stays as it is.
function Get-StartWithWindows {
    if (-not (Test-Path $SettingsPath)) { return $true }
    try {
        $settings = ConvertFrom-LooseJson (Get-Content $SettingsPath -Raw -Encoding UTF8)
    }
    catch {
        return $null
    }
    if ($settings -isnot [System.Management.Automation.PSCustomObject]) { return $null }
    $property = $settings.PSObject.Properties | Where-Object { $_.Name -eq 'startWithWindows' } | Select-Object -First 1
    if ($null -eq $property) { return $true }
    if ($property.Value -is [bool]) { return $property.Value }
    return $null
}

# The start failing (Task Scheduler refusing the task, say) counts as a build
# that did not come up, so it goes back like one.
function Invoke-Start {
    try { return (Start-Installed) }
    catch {
        Write-Step "starting Peel failed: $($_.Exception.Message)"
        return 4
    }
}

# Back to the verified pair: Peel stopped, both junctions on the verified
# pair, the journal says so, then the candidate goes if it can.
function Restore-Verified($journal) {
    [void](Stop-Installed)
    Use-Pair $journal.verified
    Write-Journal $journal.verified $null
    if ($journal.candidate) { Remove-Quietly (Split-Path -Parent $journal.candidate.app) }
    Write-Step "the verified app and engine are back ($($journal.verified.app))"
}

# --- recover -----------------------------------------------------------------
# A candidate here was never verified: an earlier run died in the middle of
# the switch (app and venv may be from different pairs), ran with -NoStart, or
# found nobody signed in. Go back to the verified pair, both junctions
# together, before anything is deleted. With no verified pair the candidate
# is all there is, so it stays.
$journal = Read-Journal
if ($journal.candidate) {
    if ($journal.verified) {
        Write-Step 'an earlier run left a build that was never verified: going back to the verified pair first'
        $wasUp = (Get-Installed).Count -gt 0
        Restore-Verified $journal
        if ($wasUp) { [void](Invoke-Start) }
    }
    else {
        Use-Pair $journal.candidate
        Write-Journal $journal.candidate $null
    }
}
Remove-Unused

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$version = Join-Path $versions $stamp
for ($k = 2; Test-Path $version; $k++) { $version = Join-Path $versions "$stamp-$k" }
$versionSrc = Join-Path $version 'src'
$versionVenv = Join-Path $version 'venv'
$versionApp = Join-Path $version 'app'
New-Item -ItemType Directory -Force -Path $version | Out-Null

$built = $false
try {
    if (-not $EngineBuilder) {
        & robocopy $engineSource $versionSrc /MIR /NFL /NDL /NJH /NJS /NP `
            /XD tests __pycache__ .pytest_cache build '*.egg-info' .venv | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "copying the engine failed (robocopy exit $LASTEXITCODE)" }
        $global:LASTEXITCODE = 0
        if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) {
            # First install: setup.ps1 installs the backends and makes a venv
            # (it only ever writes %LOCALAPPDATA%\Peel). That venv is the
            # template for the versioned one and moves aside at the switch.
            Write-Step 'no engine venv yet: running setup.ps1'
            & (Join-Path $here 'setup.ps1') -EnginePath $versionSrc
            if ($LASTEXITCODE -ne 0) { throw "setup.ps1 failed (exit $LASTEXITCODE)" }
        }
        Build-EngineVenv $versionVenv $versionSrc $venv
    }
    else {
        $from = if (Test-Path $venv) { $venv } else { '' }
        & $EngineBuilder -Venv $versionVenv -Source $engineSource -Current $from
    }

    if ($PrebuiltApp) {
        Write-Step "using the prebuilt app in $PrebuiltApp"
        Copy-Item -LiteralPath $PrebuiltApp -Destination $versionApp -Recurse
    }
    else {
        Write-Step "publishing the shell to $versionApp (log: $buildLog)"
        Invoke-Native { & dotnet publish $project -c Release -r win-x64 --self-contained true -o $versionApp --nologo -v q *> $buildLog }
        if ($LASTEXITCODE -ne 0) {
            Get-Content $buildLog -Tail 20 | ForEach-Object { Write-Host $_ }
            throw "dotnet publish failed (exit $LASTEXITCODE)"
        }
    }
    if (-not (Test-Path (Join-Path $versionApp 'Peel.exe'))) { throw "no Peel.exe in $versionApp" }
    $built = $true
}
finally {
    if (-not $built) {
        Write-Step 'the build failed before Peel was touched: dropping it'
        Remove-Quietly $version
    }
}

# --- switch the pair -----------------------------------------------------------
$running = Stop-Installed
$wasRunning = $running.Count -gt 0
$stopped = @($running | ForEach-Object { $_.Id })
$others = @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $stopped -notcontains $_.Id })
if ($others.Count -gt 0) {
    # A dev build holds the per session single instance lock, so the installed
    # one would start, see it and quit. Say so instead of guessing.
    Write-Step ("note: another Peel.exe runs (" + (($others | ForEach-Object { "$($_.Id) $($_.Path)" }) -join '; ') + ")")
}

# The new build may rewrite settings.json as it starts; a rollback puts the
# bytes back, or removes a file that was not there.
$settingsBefore = $null
if (Test-Path -LiteralPath $SettingsPath) { $settingsBefore = [IO.File]::ReadAllBytes($SettingsPath) }
function Restore-Settings {
    if ($null -ne $settingsBefore) { [IO.File]::WriteAllBytes($SettingsPath, $settingsBefore) }
    elseif (Test-Path -LiteralPath $SettingsPath) { Remove-Item -LiteralPath $SettingsPath -Force }
}

# The verified pair is what a failure goes back to. With no journal yet the
# installed pair counts as verified: it is the one that has been running.
$verified = (Read-Journal).verified
if (-not $verified) { $verified = Get-CurrentPair (Join-Path $versions "$(Split-Path -Leaf $version)-previous") }
$candidate = New-Pair $versionApp $versionVenv
Write-Journal $verified $candidate

$switched = $false
try {
    if ($verified) { Use-Pair $verified }
    # First install: the venv setup.ps1 made was only the template.
    if ((Test-Path $venv) -and -not (Test-Link $venv)) { Move-WithRetry $venv (Join-Path $version 'template-venv') }
    Set-Link $app $versionApp
    if ($env:PEEL_PUBLISH_TEST_DIE -eq 'mid-switch') { [Environment]::Exit(9) }
    Set-Link $venv $versionVenv
    Write-Step "installed $exe from $version"

    # --- start with Windows ---------------------------------------------------
    $startWithWindows = Get-StartWithWindows
    if ($null -eq $startWithWindows) {
        Write-Step "cannot read startWithWindows from ${SettingsPath}, leaving the start with Windows entry as it is"
    }
    elseif ($startWithWindows) {
        if (-not (Test-Path $RunKey)) { New-Item -Path $RunKey -Force | Out-Null }
        Set-ItemProperty -Path $RunKey -Name 'Peel' -Value ('"{0}"' -f $exe)
        Write-Step 'start with Windows: on (HKCU Run "Peel")'
    }
    else {
        if (Test-Path $RunKey) { Remove-ItemProperty -Path $RunKey -Name 'Peel' -ErrorAction SilentlyContinue }
        Write-Step 'start with Windows: off, as Settings says'
    }
    $switched = $true
}
catch {
    Write-Step "the switch failed after Peel was stopped: $($_.Exception.Message)"
}

if ($switched -and $NoStart) {
    Write-Step 'not starting Peel (-NoStart), so nothing verified this build yet'
    exit 0
}
$result = if ($switched) { Invoke-Start } else { 4 }
if ($result -eq 3) {
    Write-Step 'nothing saw the new build start, so the verified pair stays next to it'
    exit 3
}
if ($result -ne 0) {
    if (-not $verified) {
        Write-Step 'the new build did not stay up and there is no previous build to go back to'
        exit 4
    }
    Write-Step 'the new build did not stay up: going back to the verified pair'
    [void](Stop-Installed)
    Restore-Settings
    Restore-Verified (Read-Journal)
    if ($wasRunning -or $switched) { [void](Invoke-Start) }
    exit 6
}

# Up and stayed up: this pair is the verified one now, and the older go.
Write-Journal $candidate $null
Remove-Unused
# Before versions\ there was one source copy here; nothing installs from it now.
Remove-Quietly $legacyEngineCopy
exit 0
