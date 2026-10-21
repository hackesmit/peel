<#
.SYNOPSIS
  Peel setup for George (Windows 11). Idempotent: safe to run repeatedly.

.DESCRIPTION
  Installs the external backends Peel's engine shells out to (via winget,
  exact ids), creates a dedicated Python venv, installs the engine plus its
  module backends into it, and drops a `peel` shim on the user PATH. Finishes
  by running `peel doctor` so you can see what was found.

  Once publish.ps1 has run, venv is a junction to the engine venv of the
  installed pair (versions\<stamp>\venv), which publish.ps1 owns. Then this
  script leaves that venv exactly as it is: no pip upgrade, no editable
  engine, no module installs, and -RecreateVenv refuses. Backends, the shim
  and the doctor run still happen; to change the engine, run publish.ps1.

  Run it over ssh from hqbox:
    ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\main\install\setup.ps1"

.PARAMETER EnginePath
  The engine source to install editable. Defaults to C:\peel-dev\main\engine.

.PARAMETER SkipWinget
  Skip the winget backend installs (venv and shim only). Useful when the
  backends are already present and you only touched the engine.

.PARAMETER InstallRoot
  Defaults to %LOCALAPPDATA%\Peel. Another root is a scratch one
  (install\test-setup.ps1, or publish.ps1 run with -InstallRoot): the venv
  and shim go there and the user PATH is left as it is.
#>
[CmdletBinding()]
param(
    [string]$EnginePath = 'C:\peel-dev\main\engine',
    [switch]$SkipWinget,
    # Python for the venv, as the py launcher spells it: '3' (newest), '3.12'.
    [string]$PythonVersion = '3',
    # Delete and rebuild the venv, for example after changing -PythonVersion.
    [switch]$RecreateVenv,
    [string]$InstallRoot = ''
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in a param default.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step([string]$message) {
    Write-Host "[peel-setup] $message"
}

# --- backends via winget --------------------------------------------------
# Exact package ids (winget -e --id). The engine's backend lookup
# (peel_engine/backends.py) knows the standard install locations for each, so
# they do not all need to be on PATH; libvips is the exception and is handled
# below.
$packages = @(
    'Gyan.FFmpeg'                       # ffmpeg + ffprobe (audio/video)
    'ImageMagick.ImageMagick'           # image conversion fallback
    'libvips.libvips'                   # fast image conversion
    'TheDocumentFoundation.LibreOffice' # documents, slides, spreadsheets
    'JohnMacFarlane.Pandoc'             # document conversion
    '7zip.7zip'                         # archives
    'Microsoft.DotNet.SDK.8'            # builds the WPF shell (shell bead)
)

function Test-WingetInstalled([string]$id) {
    $out = winget list --id $id -e --accept-source-agreements 2>$null | Out-String
    return ($out -match [regex]::Escape($id))
}

function Install-WingetPackage([string]$id) {
    if (Test-WingetInstalled $id) {
        Write-Step "$id already installed"
        return
    }
    Write-Step "installing $id"
    winget install --id $id -e --silent --disable-interactivity `
        --accept-package-agreements --accept-source-agreements | Out-Null
    $code = $LASTEXITCODE
    if (-not (Test-WingetInstalled $id)) {
        throw "winget install $id failed (exit $code)"
    }
}

if (-not $SkipWinget) {
    foreach ($id in $packages) { Install-WingetPackage $id }
} else {
    Write-Step 'skipping winget installs (-SkipWinget)'
}

# Pull freshly installed PATH entries into this session so later steps and the
# doctor run can see the new executables.
function Sync-Path {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($machine, $user) | Where-Object { $_ }) -join ';'
}
Sync-Path

# libvips ships as a winget "portable" zip: its bin is not placed on PATH by
# the installer. Find it under the WinGet Packages tree and add it to the user
# PATH so the engine's `vips` lookup (shutil.which) resolves it.
function Ensure-VipsOnPath {
    if (Get-Command vips -ErrorAction SilentlyContinue) { return }
    $root = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (-not (Test-Path $root)) { return }
    $vipsExe = Get-ChildItem -Path $root -Recurse -Filter 'vips.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $vipsExe) { return }
    $vipsBin = Split-Path $vipsExe.FullName -Parent
    Add-ToUserPath $vipsBin
    Write-Step "added libvips bin to user PATH: $vipsBin"
}

function Add-ToUserPath([string]$dir) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @()
    if ($userPath) { $entries = $userPath -split ';' | Where-Object { $_ } }
    if ($entries -notcontains $dir) {
        $new = (@($entries + $dir) -join ';')
        [Environment]::SetEnvironmentVariable('Path', $new, 'User')
    }
    Sync-Path
}

if (-not $SkipWinget) { Ensure-VipsOnPath }

# --- Python venv and engine -----------------------------------------------
$defaultRoot = Join-Path $env:LOCALAPPDATA 'Peel'
$peelHome = if ($InstallRoot) { $InstallRoot } else { $defaultRoot }
$venv = Join-Path $peelHome 'venv'
$binDir = Join-Path $peelHome 'bin'
$venvPy = Join-Path $venv 'Scripts\python.exe'
$venvPeel = Join-Path $venv 'Scripts\peel.exe'

New-Item -ItemType Directory -Force -Path $peelHome, $binDir | Out-Null

# A junction here is publish.ps1's: the venv of the installed pair. Writing
# into it would change the running engine under publish.ps1's journal, and
# Remove-Item -Recurse on a junction can empty its target in Windows
# PowerShell 5.1, so nothing below touches it.
$venvItem = Get-Item -LiteralPath $venv -Force -ErrorAction SilentlyContinue
$published = ($null -ne $venvItem) -and [bool]($venvItem.Attributes -band [IO.FileAttributes]::ReparsePoint)
if ($published) {
    $target = @($venvItem.Target)[0]
    if ($RecreateVenv) {
        Write-Step "$venv is a junction to $target, which publish.ps1 owns: not recreating it. Run install\publish.ps1 to build a fresh app and engine pair."
        exit 1
    }
    if (-not (Test-Path $venvPy)) {
        Write-Step "$venv is a junction to $target, which has no Scripts\python.exe. Run install\publish.ps1."
        exit 1
    }
    Write-Step "venv is a junction to $target (publish.ps1 owns it): leaving the engine and modules as they are"
}

if ($RecreateVenv -and (Test-Path $venv)) {
    Write-Step "removing venv at $venv (-RecreateVenv)"
    Remove-Item -Recurse -Force $venv
}
if (-not (Test-Path $venvPy)) {
    Write-Step "creating venv at $venv with py -$PythonVersion"
    & py "-$PythonVersion" -m venv $venv
    if (-not (Test-Path $venvPy)) { throw "venv creation failed at $venv" }
} else {
    Write-Step "venv already present at $venv"
}

if (-not $published) {
    Write-Step 'upgrading pip'
    & $venvPy -m pip install --upgrade pip --quiet
    if ($LASTEXITCODE -ne 0) { throw 'pip upgrade failed' }

    Write-Step "installing engine (editable) from $EnginePath"
    & $venvPy -m pip install -e $EnginePath --quiet
    if ($LASTEXITCODE -ne 0) { throw "engine install failed from $EnginePath" }

    # Module backends are intentionally not engine dependencies (the engine
    # runs with whatever is present); install them explicitly here. pytest
    # runs the core test suite.
    # The pinned list is shared with publish.ps1 (modules.txt).
    Write-Step 'installing pinned module backends and pytest'
    & $venvPy -m pip install -r (Join-Path $here 'modules.txt') --quiet
    if ($LASTEXITCODE -ne 0) { throw 'module backend install failed' }
}

# --- peel shim on PATH -----------------------------------------------------
$shim = Join-Path $binDir 'peel.cmd'
$shimBody = "@echo off`r`n`"$venvPeel`" %*`r`n"
Set-Content -Path $shim -Value $shimBody -Encoding Ascii -NoNewline
Write-Step "wrote shim $shim"
if ($peelHome -eq $defaultRoot) { Add-ToUserPath $binDir }
else { Write-Step "scratch root: leaving the user PATH as it is" }

# --- doctor ----------------------------------------------------------------
Write-Step 'running peel doctor'
$doctorJson = & $venvPeel doctor
$doctorExit = $LASTEXITCODE
$doctorJson
if ($doctorExit -ne 0) { exit $doctorExit }
# doctor reports ok even with gaps; fail the setup honestly when one is missing.
$report = $doctorJson | Out-String | ConvertFrom-Json
$missing = @($report.missing)
if ($missing.Count -gt 0) {
    Write-Step ("missing backends: " + ($missing -join ', '))
    exit 2
}
exit 0
