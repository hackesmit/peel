<#
.SYNOPSIS
  Tests for setup.ps1 when venv is a junction (publish.ps1's layout), against
  a scratch install root. %LOCALAPPDATA%\Peel and the user PATH are never
  touched.

.DESCRIPTION
  The junction points at a stand in venv: Scripts\python.exe writes
  called.txt next to itself if anything runs it (pip would), and
  Scripts\peel.exe prints a green doctor report. Each case checks that every
  file under the target keeps its size, time and bytes, and that venv is
  still the same junction: a plain -SkipWinget run, a -RecreateVenv run
  (refused), and a junction whose target has no python.exe (refused, nothing
  created in it).

  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\test-setup.ps1"
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$failures = New-Object System.Collections.Generic.List[string]
$passes = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passes++; Write-Host "ok   $name" }
    else { $script:failures.Add("$name $detail"); Write-Host "FAIL $name $detail" }
}

$scratch = Join-Path $env:TEMP ("peel-setup-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$root = Join-Path $scratch 'root'
$target = Join-Path $scratch 'versions\20261021-000000\venv'
$venv = Join-Path $root 'venv'
New-Item -ItemType Directory -Force -Path $root, (Join-Path $target 'Scripts') | Out-Null

$fake = Join-Path $scratch 'fake.exe'
Add-Type -OutputType ConsoleApplication -OutputAssembly $fake -TypeDefinition @'
using System;
using System.IO;
public static class FakeVenvTool
{
    public static int Main()
    {
        var self = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (Path.GetFileName(self).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(self), "called.txt"), Environment.CommandLine);
            return 0;
        }
        Console.WriteLine("{\"ok\": true, \"missing\": []}");
        return 0;
    }
}
'@
Copy-Item $fake (Join-Path $target 'Scripts\python.exe')
Copy-Item $fake (Join-Path $target 'Scripts\peel.exe')
Set-Content -LiteralPath (Join-Path $target 'pyvenv.cfg') -Value 'home = C:\Python' -Encoding Ascii
New-Item -ItemType Junction -Path $venv -Target $target | Out-Null

function Get-Tree([string]$dir) {
    $lines = @(Get-ChildItem -LiteralPath $dir -Recurse -Force | Sort-Object FullName | ForEach-Object {
        if ($_.PSIsContainer) { "d $($_.FullName)" }
        else { "f $($_.FullName) $($_.Length) $($_.LastWriteTimeUtc.Ticks) $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    })
    return ($lines -join "`n")
}

function Get-LinkTarget([string]$path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return '(not a junction)' }
    return @($item.Target)[0]
}

function Setup([string[]]$extra = @()) {
    $log = Join-Path $scratch ("setup-" + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.log')
    $ErrorActionPreference = 'Continue'
    $setupArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'setup.ps1'),
        '-SkipWinget', '-InstallRoot', $root, '-EnginePath', (Join-Path $scratch 'no-engine')) + $extra
    & powershell @setupArgs *> $log
    $code = $LASTEXITCODE
    Get-Content $log | Where-Object { $_ -match 'peel-setup' } | ForEach-Object { Write-Host "     $_" }
    return $code
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
try {
    # 1 a plain rerun on a published venv
    $before = Get-Tree $target
    $code = Setup
    Check 'a junction venv: setup exits 0' ($code -eq 0) "(exit $code)"
    Check 'a junction venv: nothing ran python.exe in it' (-not (Test-Path (Join-Path $target 'Scripts\called.txt')))
    Check 'a junction venv: the target is as it was' ((Get-Tree $target) -eq $before)
    Check 'a junction venv: venv is still the same junction' ((Get-LinkTarget $venv) -eq $target) (Get-LinkTarget $venv)
    Check 'a junction venv: the shim is written in the root' (Test-Path (Join-Path $root 'bin\peel.cmd'))

    # 2 -RecreateVenv refuses
    $code = Setup @('-RecreateVenv')
    Check 'a junction venv: -RecreateVenv exits 1' ($code -eq 1) "(exit $code)"
    Check 'a junction venv: -RecreateVenv leaves the target as it was' ((Get-Tree $target) -eq $before)
    Check 'a junction venv: -RecreateVenv leaves the junction' ((Get-LinkTarget $venv) -eq $target) (Get-LinkTarget $venv)

    # 3 a junction to a venv with no python.exe: no venv is made inside it
    $empty = Join-Path $scratch 'versions\20261021-000001\venv'
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    [IO.Directory]::Delete($venv, $false)
    New-Item -ItemType Junction -Path $venv -Target $empty | Out-Null
    $code = Setup
    Check 'a junction to an empty venv exits 1' ($code -eq 1) "(exit $code)"
    Check 'a junction to an empty venv stays empty' (@(Get-ChildItem -LiteralPath $empty -Force).Count -eq 0)
    Check 'a junction to an empty venv: the first target is as it was' ((Get-Tree $target) -eq $before)

    Check 'the user PATH is as it was' ([Environment]::GetEnvironmentVariable('Path', 'User') -eq $userPath)
}
finally {
    if ((Get-LinkTarget $venv) -ne '(not a junction)') { [IO.Directory]::Delete($venv, $false) }
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}

Write-Host ("{0} passed, {1} failed" -f $passes, $failures.Count)
if ($failures.Count -gt 0) { exit 1 }
exit 0
