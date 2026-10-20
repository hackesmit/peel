<#
.SYNOPSIS
  Install or upgrade Peel for the current user on George. No admin needed.
  Idempotent: re-run it any time to upgrade in place.

.DESCRIPTION
  1. Finishes a half done earlier upgrade: if app is missing but app.old is
     there, app.old goes back first, before anything is deleted.
  2. Copies the engine source to %LOCALAPPDATA%\Peel\engine and publishes the
     shell self-contained to %LOCALAPPDATA%\Peel\app.new, while the installed
     Peel keeps running.
  3. Stops the installed Peel, then installs the engine (not editable) into the
     venv install\setup.ps1 made, so the installed app no longer depends on a
     dev copy under C:\peel-dev and no running engine has its files replaced.
     If the venv is missing or `peel doctor` reports a missing backend, it runs
     setup.ps1 first.
  4. Swaps app.new in for app, keeping the old build until the swap has worked.
  5. Writes the start with Windows entry (HKCU Run, value "Peel", created if
     the Run key is missing), unless the user turned it off in Settings. A
     settings file it cannot read leaves the entry as it is.
  6. Starts Peel in the signed in desktop session and checks that it runs. If
     any step after the stop fails, it still starts whatever build is in app
     when Peel was running before, so a failed upgrade never leaves it stopped.

  Run it over ssh from hqbox, with the source synced to C:\peel-dev\<branch>:
    ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\publish.ps1"

  A process started over ssh lands in session 0, which has no desktop, so the
  start goes through a one shot scheduled task with an Interactive principal,
  the same way the GUI tests run. Daniel has to be signed in for it to start
  now; otherwise it starts at the next sign in.

.PARAMETER Source
  The repository root to install from. Defaults to the folder above install\.

.PARAMETER NoStart
  Install and write the Run entry, but leave Peel stopped (the smoke run starts
  its own copy).
#>
[CmdletBinding()]
param(
    [string]$Source = '',
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in a param default.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Source) { $Source = Split-Path -Parent $here }

function Write-Step([string]$message) {
    Write-Host "[peel-publish] $message"
}

$peelHome = Join-Path $env:LOCALAPPDATA 'Peel'
$app = Join-Path $peelHome 'app'
$staging = Join-Path $peelHome 'app.new'
$previous = Join-Path $peelHome 'app.old'
$engineCopy = Join-Path $peelHome 'engine'
$venvPy = Join-Path $peelHome 'venv\Scripts\python.exe'
$venvPeel = Join-Path $peelHome 'venv\Scripts\peel.exe'
$exe = Join-Path $app 'Peel.exe'
$buildLog = Join-Path $peelHome 'publish.log'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$settingsPath = Join-Path $env:APPDATA 'Peel\settings.json'

$project = Join-Path $Source 'shell\Peel\Peel.csproj'
$engineSource = Join-Path $Source 'engine'
if (-not (Test-Path $project)) { throw "no shell project at $project" }
if (-not (Test-Path (Join-Path $engineSource 'pyproject.toml'))) { throw "no engine at $engineSource" }
New-Item -ItemType Directory -Force -Path $peelHome | Out-Null

# --- recover ----------------------------------------------------------------
# A run that died between the two moves of the swap leaves app.old and no app.
# Put it back before anything else, so nothing below deletes the only build.
if (-not (Test-Path $app) -and (Test-Path $previous)) {
    Write-Step 'app is missing but app.old is there: restoring app.old first'
    Move-Item -LiteralPath $previous -Destination $app
}

# --- engine and shell, built while Peel still runs ---------------------------
function Copy-Engine {
    # /MIR keeps the copy exact, so a module deleted upstream goes here too.
    # Nothing runs from this folder (the venv holds a normal install of it).
    & robocopy $engineSource $engineCopy /MIR /NFL /NDL /NJH /NJS /NP `
        /XD tests __pycache__ .pytest_cache build '*.egg-info' .venv | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying the engine failed (robocopy exit $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
}

Copy-Engine
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
Write-Step "publishing the shell to $staging (log: $buildLog)"
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging --nologo -v q *> $buildLog
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $staging 'Peel.exe'))) {
    Get-Content $buildLog -Tail 20 | ForEach-Object { Write-Host $_ }
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

# --- stop --------------------------------------------------------------------
function Get-Installed {
    @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase)
    })
}

$running = Get-Installed
$wasRunning = $running.Count -gt 0
foreach ($process in $running) {
    Write-Step "stopping the installed Peel (pid $($process.Id))"
    # Peel holds its engines in kill on close job objects, so they go with it.
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}
foreach ($process in $running) {
    Wait-Process -Id $process.Id -Timeout 15 -ErrorAction SilentlyContinue
}
$stopped = @($running | ForEach-Object { $_.Id })
$others = @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $stopped -notcontains $_.Id })
if ($others.Count -gt 0) {
    # A dev build holds the per session single instance lock, so the installed
    # one would start, see it and quit. Say so instead of guessing.
    Write-Step ("note: another Peel.exe runs (" + (($others | ForEach-Object { "$($_.Id) $($_.Path)" }) -join '; ') + ")")
}

# --- start (used on success, and on failure when Peel was running) -----------
function Start-Installed {
    if (-not (Test-Path $exe)) {
        Write-Step "nothing to start: no $exe"
        return 5
    }
    if ([System.Diagnostics.Process]::GetCurrentProcess().SessionId -ne 0) {
        Start-Process -FilePath $exe
    }
    else {
        $task = 'PeelStart'
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument ('-NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath ''{0}''"' -f $exe)
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
        try {
            Start-ScheduledTask -TaskName $task
            $deadline = (Get-Date).AddSeconds(20)
            do {
                Start-Sleep -Milliseconds 500
                $state = (Get-ScheduledTask -TaskName $task).State
            } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        }
        finally {
            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
        }
    }

    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $now = @(Get-Installed | Where-Object { $_.SessionId -ne 0 })
    } while ($now.Count -eq 0 -and (Get-Date) -lt $deadline)
    if ($now.Count -eq 0) {
        Write-Step 'Peel did not start. Is anyone signed in at the console? It will start at the next sign in.'
        return 3
    }
    # Alive a few seconds later, not just launched: a second instance quits at once.
    Start-Sleep -Seconds 3
    $alive = @(Get-Installed | Where-Object { $_.SessionId -ne 0 })
    if ($alive.Count -eq 0) {
        Write-Step 'Peel started and then quit. See %LOCALAPPDATA%\Peel\logs\peel.log.'
        return 4
    }
    Write-Step ("Peel is running: pid {0}, session {1}" -f $alive[0].Id, $alive[0].SessionId)
    return 0
}

# --- settings ----------------------------------------------------------------
# The app reads settings.json with comments and trailing commas allowed;
# Windows PowerShell 5.1's ConvertFrom-Json takes neither. Strip both outside
# strings first, so a file the app reads is a file this reads too.
function ConvertFrom-LooseJson([string]$text) {
    $out = New-Object System.Text.StringBuilder
    $i = 0
    $n = $text.Length
    $inString = $false
    while ($i -lt $n) {
        $c = $text[$i]
        if ($inString) {
            [void]$out.Append($c)
            if ($c -eq '\' -and $i + 1 -lt $n) { [void]$out.Append($text[$i + 1]); $i += 2; continue }
            if ($c -eq '"') { $inString = $false }
            $i++
            continue
        }
        if ($c -eq '"') { $inString = $true; [void]$out.Append($c); $i++; continue }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '/') {
            while ($i -lt $n -and $text[$i] -ne "`n") { $i++ }
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '*') {
            $end = $text.IndexOf('*/', $i + 2)
            if ($end -lt 0) { throw 'unterminated comment' }
            $i = $end + 2
            continue
        }
        if ($c -eq ',') {
            $j = $i + 1
            while ($j -lt $n) {
                if ([char]::IsWhiteSpace($text[$j])) { $j++; continue }
                if ($text[$j] -eq '/' -and $j + 1 -lt $n -and $text[$j + 1] -eq '/') {
                    while ($j -lt $n -and $text[$j] -ne "`n") { $j++ }
                    continue
                }
                if ($text[$j] -eq '/' -and $j + 1 -lt $n -and $text[$j + 1] -eq '*') {
                    $end = $text.IndexOf('*/', $j + 2)
                    if ($end -lt 0) { break }
                    $j = $end + 2
                    continue
                }
                break
            }
            if ($j -lt $n -and ($text[$j] -eq '}' -or $text[$j] -eq ']')) { $i++; continue }
        }
        [void]$out.Append($c)
        $i++
    }
    return ($out.ToString() | ConvertFrom-Json)
}

# On, off, or $null when the file cannot be read: then the entry stays as it is.
function Get-StartWithWindows {
    if (-not (Test-Path $settingsPath)) { return $true }
    try {
        $settings = ConvertFrom-LooseJson (Get-Content $settingsPath -Raw -Encoding UTF8)
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

# --- install, swap, Run entry ------------------------------------------------
function Install-Engine {
    Write-Step "installing the engine from $engineCopy"
    & $venvPy -m pip install --no-deps --force-reinstall --quiet $engineCopy
    if ($LASTEXITCODE -ne 0) { throw 'installing the engine into the venv failed' }
}

function Get-Missing {
    $json = & $venvPeel doctor | Out-String
    if ($LASTEXITCODE -ne 0) { return @('peel doctor failed') }
    return @(($json | ConvertFrom-Json).missing)
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

$installed = $false
try {
    if (-not (Test-Path $venvPy)) {
        Write-Step 'no engine venv yet: running setup.ps1'
        & (Join-Path $here 'setup.ps1') -EnginePath $engineCopy
        if ($LASTEXITCODE -ne 0) { throw "setup.ps1 failed (exit $LASTEXITCODE)" }
    }
    Install-Engine
    $missing = Get-Missing
    if ($missing.Count -gt 0) {
        Write-Step ("peel doctor is not green (" + ($missing -join ', ') + "): running setup.ps1")
        & (Join-Path $here 'setup.ps1') -EnginePath $engineCopy
        Install-Engine
        $missing = Get-Missing
        if ($missing.Count -gt 0) { throw ("backends still missing after setup.ps1: " + ($missing -join ', ')) }
    }
    Write-Step 'peel doctor: all backends found'

    # app exists here (or this is a first install), so app.old is only an
    # earlier build that is safe to drop.
    if ((Test-Path $app) -and (Test-Path $previous)) { Remove-Item -Recurse -Force $previous }
    if (Test-Path $app) { Move-WithRetry $app $previous }
    try {
        Move-WithRetry $staging $app
    }
    catch {
        # Put the old build back so a failed upgrade still leaves a working Peel.
        if ((Test-Path $previous) -and -not (Test-Path $app)) { Move-Item -LiteralPath $previous -Destination $app }
        throw
    }
    if (Test-Path $previous) { Remove-Item -Recurse -Force $previous -ErrorAction SilentlyContinue }
    Write-Step "installed $exe"

    # --- start with Windows ---------------------------------------------------
    $startWithWindows = Get-StartWithWindows
    if ($null -eq $startWithWindows) {
        Write-Step "cannot read startWithWindows from ${settingsPath}, leaving the start with Windows entry as it is"
    }
    elseif ($startWithWindows) {
        if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
        Set-ItemProperty -Path $runKey -Name 'Peel' -Value ('"{0}"' -f $exe)
        Write-Step 'start with Windows: on (HKCU Run "Peel")'
    }
    else {
        if (Test-Path $runKey) { Remove-ItemProperty -Path $runKey -Name 'Peel' -ErrorAction SilentlyContinue }
        Write-Step 'start with Windows: off, as Settings says'
    }
    $installed = $true
}
finally {
    if (-not $installed -and $wasRunning) {
        Write-Step 'the upgrade failed after Peel was stopped: starting the build in app again'
        [void](Start-Installed)
    }
}

if ($NoStart) {
    Write-Step 'not starting Peel (-NoStart)'
    exit 0
}
exit (Start-Installed)
