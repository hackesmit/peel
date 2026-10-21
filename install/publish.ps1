<#
.SYNOPSIS
  Install or upgrade Peel for the current user on George. No admin needed.
  Idempotent: re-run it any time to upgrade in place.

.DESCRIPTION
  The app and the engine are one pair: the new build only replaces the old
  one once it has started and stayed up, and a build that fails puts the
  previous app AND the previous engine back.

  1. Finishes a half done earlier run: app.old goes back when app is missing,
     venv.old when venv is missing, before anything is deleted.
  2. While the installed Peel keeps running: copies the engine source to
     engines\<stamp>\src, builds a new venv at engines\<stamp>\venv (the same
     pinned modules the current venv has, plus this engine, not editable),
     checks it with `peel doctor`, and publishes the shell self-contained to
     app.new. Nothing the running Peel uses is touched yet, so a failure here
     leaves it as it was. With no venv at all (first install) it runs
     setup.ps1 first for the backends.
  3. Stops the installed Peel and switches the pair: app becomes app.old and
     app.new becomes app; venv (a junction, which is where the app looks for
     the engine) is pointed at the new engines\<stamp>\venv. A venv that is
     still a real folder from an older install moves aside to venv.old.
  4. Writes the start with Windows entry (HKCU Run, value "Peel", created if
     the Run key is missing), unless the user turned it off in Settings. A
     settings file it cannot read leaves the entry as it is.
  5. Starts Peel in the signed in desktop session and watches it for
     -StaySeconds (10). Up the whole time: app.old, venv.old and the older
     engines\* go. Quit or never showed up: it stops the new build, puts the
     previous app and engine back, starts them and exits 6. If nobody is signed
     in it cannot tell, so it keeps the previous build next to the new one and
     exits 3; the next run drops it.

  Run it over ssh from hqbox, with the source synced to C:\peel-dev\<branch>:
    ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\publish.ps1"

  A process started over ssh lands in session 0, which has no desktop, so the
  start goes through a one shot scheduled task with an Interactive principal,
  the same way the GUI tests run.

  Exit codes: 0 installed and running (or -NoStart); 3 installed, not
  verified (nobody signed in); 4 the new build did not stay up and there was
  no previous build to go back to; 6 the new build did not stay up and the
  previous one is back; anything else (an error before the switch) leaves the
  installed Peel as it was.

.PARAMETER Source
  The repository root to install from. Defaults to the folder above install\.

.PARAMETER NoStart
  Install and write the Run entry, but leave Peel stopped (the smoke run starts
  its own copy). The previous build stays next to the new one, since nothing
  checked that the new one starts; the next run drops it.

.PARAMETER InstallRoot, SettingsPath, RunKey, PrebuiltApp, EngineBuilder
  For install\test-publish.ps1, which runs this against a scratch root. The
  installed app looks for its engine under %LOCALAPPDATA%\Peel only, so a
  real install always uses the defaults. PrebuiltApp is a folder used as the
  new build instead of dotnet publish. EngineBuilder is a script run as
  `& $EngineBuilder -Venv <dir> -Source <dir> -Current <venv or ''>` instead
  of the pip build and doctor check.
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
    [int]$StaySeconds = 10
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
$staging = Join-Path $peelHome 'app.new'
$previous = Join-Path $peelHome 'app.old'
$venv = Join-Path $peelHome 'venv'
$venvAside = Join-Path $peelHome 'venv.old'
$engines = Join-Path $peelHome 'engines'
$legacyEngineCopy = Join-Path $peelHome 'engine'
$exe = Join-Path $app 'Peel.exe'
$buildLog = Join-Path $peelHome 'publish.log'
$startTask = if ($peelHome -eq $defaultRoot) { 'PeelStart' } else { 'PeelStartScratch' }

$project = Join-Path $Source 'shell\Peel\Peel.csproj'
$engineSource = Join-Path $Source 'engine'
if (-not $PrebuiltApp -and -not (Test-Path $project)) { throw "no shell project at $project" }
if (-not $EngineBuilder -and -not (Test-Path (Join-Path $engineSource 'pyproject.toml'))) { throw "no engine at $engineSource" }
New-Item -ItemType Directory -Force -Path $peelHome, $engines | Out-Null

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

# --- recover -----------------------------------------------------------------
# A run that died in the middle of a switch leaves app.old with no app, or
# venv.old with no venv. Put them back before anything else, so nothing below
# deletes the only build.
if (-not (Test-Path $app) -and (Test-Path $previous)) {
    Write-Step 'app is missing but app.old is there: restoring app.old first'
    Move-Item -LiteralPath $previous -Destination $app
}
if (-not (Test-Path $venv) -and (Test-Path $venvAside)) {
    Write-Step 'venv is missing but venv.old is there: restoring venv.old first'
    Remove-Link $venv
    Move-Item -LiteralPath $venvAside -Destination $venv
}

# An earlier run that could not watch its build start kept the previous pair.
# app exists here and venv is whole, so those are safe to drop now.
$current = Get-LinkTarget $venv
if ((Test-Path $app) -and (Test-Path $previous)) { Remove-Item -Recurse -Force $previous }
if ((Test-Path $venv) -and (Test-Path $venvAside)) { Remove-Item -Recurse -Force $venvAside }
foreach ($old in @(Get-ChildItem -LiteralPath $engines -Directory -ErrorAction SilentlyContinue)) {
    if ($current -and $current.StartsWith($old.FullName + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
    Write-Step "dropping an older engine: $($old.FullName)"
    Remove-Item -Recurse -Force -LiteralPath $old.FullName
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

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$version = Join-Path $engines $stamp
for ($k = 2; Test-Path $version; $k++) { $version = Join-Path $engines "$stamp-$k" }
$versionSrc = Join-Path $version 'src'
$versionVenv = Join-Path $version 'venv'
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

    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    if ($PrebuiltApp) {
        Write-Step "using the prebuilt app in $PrebuiltApp"
        Copy-Item -LiteralPath $PrebuiltApp -Destination $staging -Recurse
    }
    else {
        Write-Step "publishing the shell to $staging (log: $buildLog)"
        Invoke-Native { & dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging --nologo -v q *> $buildLog }
        if ($LASTEXITCODE -ne 0) {
            Get-Content $buildLog -Tail 20 | ForEach-Object { Write-Host $_ }
            throw "dotnet publish failed (exit $LASTEXITCODE)"
        }
    }
    if (-not (Test-Path (Join-Path $staging 'Peel.exe'))) { throw "no Peel.exe in $staging" }
    $built = $true
}
finally {
    if (-not $built) {
        Write-Step 'the build failed before Peel was touched: dropping it'
        Remove-Item -Recurse -Force -LiteralPath $version -ErrorAction SilentlyContinue
        if (Test-Path $staging) { Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue }
    }
}

# --- stop and start ----------------------------------------------------------
function Get-Installed {
    @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase)
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
        Register-ScheduledTask -TaskName $startTask -Action $action -Principal $principal -Force | Out-Null
        try {
            Start-ScheduledTask -TaskName $startTask
            $deadline = (Get-Date).AddSeconds(20)
            do {
                Start-Sleep -Milliseconds 500
                $state = (Get-ScheduledTask -TaskName $startTask).State
            } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        }
        finally {
            Unregister-ScheduledTask -TaskName $startTask -Confirm:$false -ErrorAction SilentlyContinue
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

$running = Stop-Installed
$wasRunning = $running.Count -gt 0
$stopped = @($running | ForEach-Object { $_.Id })
$others = @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $stopped -notcontains $_.Id })
if ($others.Count -gt 0) {
    # A dev build holds the per session single instance lock, so the installed
    # one would start, see it and quit. Say so instead of guessing.
    Write-Step ("note: another Peel.exe runs (" + (($others | ForEach-Object { "$($_.Id) $($_.Path)" }) -join '; ') + ")")
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

# --- switch the pair, and switch it back ---------------------------------------
# What venv was before the switch: a junction target, a real folder (moved to
# venv.old), or nothing.
$hadApp = Test-Path $app
$oldTarget = Get-LinkTarget $venv
$oldWasFolder = (Test-Path $venv) -and -not $oldTarget

function Restore-Previous {
    [void](Stop-Installed)
    if (-not $hadApp) {
        Write-Step 'there is no previous build to go back to'
        return $false
    }
    if (Test-Path $previous) {
        if (Test-Path $app) { Remove-Item -Recurse -Force -LiteralPath $app }
        Move-WithRetry $previous $app
    }
    if ($oldTarget) {
        Set-Link $venv $oldTarget
    }
    elseif ($oldWasFolder -and (Test-Path $venvAside)) {
        Remove-Link $venv
        Move-WithRetry $venvAside $venv
    }
    Remove-Item -Recurse -Force -LiteralPath $version -ErrorAction SilentlyContinue
    Write-Step 'the previous app and engine are back'
    return $true
}

$switched = $false
try {
    if ($hadApp) { Move-WithRetry $app $previous }
    Move-WithRetry $staging $app
    if ($oldWasFolder) { Move-WithRetry $venv $venvAside }
    Set-Link $venv $versionVenv
    Write-Step "installed $exe with the engine in $versionVenv"

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
finally {
    if (-not $switched) {
        Write-Step 'the switch failed after Peel was stopped: going back to the previous build'
        if ((Restore-Previous) -and $wasRunning) { [void](Start-Installed) }
    }
}

if ($NoStart) {
    Write-Step 'not starting Peel (-NoStart); the previous build stays until the next run'
    exit 0
}
$result = Start-Installed
if ($result -eq 3) {
    Write-Step 'kept the previous build next to the new one, since nothing saw the new one start'
    exit 3
}
if ($result -ne 0) {
    Write-Step 'the new build did not stay up: going back to the previous one'
    if (-not (Restore-Previous)) { exit 4 }
    [void](Start-Installed)
    exit 6
}

# Up and stayed up: the previous pair is no longer needed.
if (Test-Path $previous) { Remove-Item -Recurse -Force $previous -ErrorAction SilentlyContinue }
if (Test-Path $venvAside) { Remove-Item -Recurse -Force $venvAside -ErrorAction SilentlyContinue }
# Only a folder under engines\ is this script's to drop; a junction someone
# pointed elsewhere keeps its target.
if ($oldTarget) {
    $oldVersion = Split-Path -Parent $oldTarget
    if ($oldVersion -ne $version -and (Split-Path -Parent $oldVersion) -eq $engines) {
        Remove-Item -Recurse -Force -LiteralPath $oldVersion -ErrorAction SilentlyContinue
    }
}
# Before engines\ there was one source copy here; nothing installs from it now.
if (Test-Path $legacyEngineCopy) { Remove-Item -Recurse -Force $legacyEngineCopy -ErrorAction SilentlyContinue }
exit 0
