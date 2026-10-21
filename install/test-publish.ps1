<#
.SYNOPSIS
  Tests for publish.ps1 and its loose JSON reader, against a scratch install
  root. The installed Peel in %LOCALAPPDATA%\Peel is never touched.

.DESCRIPTION
  publish.ps1 runs here with -InstallRoot in %TEMP%, -PrebuiltApp folders that
  hold a stand in Peel.exe (compiled below, no window, it only stays up or
  quits), and an -EngineBuilder that writes a marker instead of a pip venv.
  So each case checks which app runs and which engine venv is switched in
  after an upgrade that works, one whose build quits at startup, one whose
  build quits after 5 s, one whose engine build fails, and one that starts from
  an older install with a real venv folder. Nothing moves the mouse or the
  keyboard; the stand in starts in the signed in session through a scheduled
  task, so someone has to be signed in at George.

  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\test-publish.ps1"
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'LooseJson.ps1')

$failures = New-Object System.Collections.Generic.List[string]
$passes = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passes++; Write-Host "ok   $name" }
    else { $script:failures.Add("$name $detail"); Write-Host "FAIL $name $detail" }
}

# --- loose JSON ----------------------------------------------------------------
function Read-Loose([string]$text) {
    try { return @{ ok = $true; value = (ConvertFrom-LooseJson $text) } }
    catch { return @{ ok = $false } }
}

$accepted = @(
    @('line comment', "{ // note`n ""startWithWindows"": false }", $false),
    @('block comment', '{ /* note */ "startWithWindows": false }', $false),
    @('trailing comma', '{ "startWithWindows": false, }', $false),
    @('comma then comment then brace', "{ ""startWithWindows"": false, // x`n }", $false),
    @('comment between tokens', '{ "startWithWindows":/*x*/false }', $false),
    @('slashes inside a string', '{ "outputFolder": "C://a/*b*/", "startWithWindows": true }', $true)
)
foreach ($case in $accepted) {
    $read = Read-Loose $case[1]
    Check "json accepts: $($case[0])" ($read.ok -and $read.value.startWithWindows -eq $case[2])
}
$read = Read-Loose '{ "outputFolder": "C://a/*b*/" }'
Check 'json keeps a string with comment marks as it is' ($read.ok -and $read.value.outputFolder -eq 'C://a/*b*/')

# A comment inside a literal splits it, as it does for the app's reader.
$rejected = @(
    @('block comment inside false', '{ "startWithWindows": f/*x*/alse }'),
    @('block comment inside true', '{ "startWithWindows": tr/**/ue }'),
    @('line comment inside true', "{ ""startWithWindows"": tr//x`nue }"),
    @('block comment inside a number', '{ "n": 1/**/2 }'),
    @('block comment inside null', '{ "outputFolder": nu/* */ll }'),
    @('unterminated comment', '{ "startWithWindows": true /* }')
)
foreach ($case in $rejected) {
    Check "json rejects: $($case[0])" (-not (Read-Loose $case[1]).ok)
}

# --- scratch install root ------------------------------------------------------
$scratch = Join-Path $env:TEMP ("peel-publish-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$root = Join-Path $scratch 'root'
$builds = Join-Path $scratch 'builds'
$runKey = "HKCU:\Software\PeelTests\publish-$([guid]::NewGuid().ToString('N'))\Run"
$settings = Join-Path $scratch 'settings.json'
New-Item -ItemType Directory -Force -Path $root, $builds | Out-Null
Set-Content -LiteralPath $settings -Value '{ "startWithWindows": true }' -Encoding UTF8

# The stand in: mode.txt next to it says stay, exit (at once) or late (after 5 s).
$fake = Join-Path $scratch 'Peel.exe'
Add-Type -OutputType WindowsApplication -OutputAssembly $fake -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public static class FakePeel
{
    public static int Main()
    {
        var mode = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mode.txt")).Trim();
        if (mode == "exit") { return 1; }
        if (mode == "late") { Thread.Sleep(5000); return 1; }
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
'@
foreach ($build in @(@('good1', 'stay'), @('good2', 'stay'), @('quits', 'exit'), @('late', 'late'))) {
    $dir = Join-Path $builds $build[0]
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item $fake (Join-Path $dir 'Peel.exe')
    Set-Content -LiteralPath (Join-Path $dir 'mode.txt') -Value $build[1] -Encoding Ascii
    Set-Content -LiteralPath (Join-Path $dir 'build.txt') -Value $build[0] -Encoding Ascii
}

$builder = Join-Path $scratch 'engine-builder.ps1'
Set-Content -LiteralPath $builder -Encoding UTF8 -Value @'
param([string]$Venv, [string]$Source, [string]$Current)
if ($env:PEEL_TEST_ENGINE -eq 'fail') { throw 'engine build failed (test)' }
New-Item -ItemType Directory -Force -Path (Join-Path $Venv 'Scripts') | Out-Null
Set-Content -LiteralPath (Join-Path $Venv 'marker.txt') -Value $env:PEEL_TEST_ENGINE -Encoding Ascii
'@

$app = Join-Path $root 'app'
$venv = Join-Path $root 'venv'

function Publish([string]$build, [string]$engine) {
    $env:PEEL_TEST_ENGINE = $engine
    $log = Join-Path $scratch "publish-$build-$engine.log"
    # The child's own error text goes to the log, not into a stop here.
    $ErrorActionPreference = 'Continue'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'publish.ps1') `
        -InstallRoot $root -SettingsPath $settings -RunKey $runKey `
        -PrebuiltApp (Join-Path $builds $build) -EngineBuilder $builder *> $log
    $code = $LASTEXITCODE
    Get-Content $log | Where-Object { $_ -match 'peel-publish' } | ForEach-Object { Write-Host "     $_" }
    return $code
}

function Get-Running {
    @(Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)
    })
}

function Get-RunningBuild {
    $now = Get-Running
    if ($now.Count -ne 1) { return "($($now.Count) running)" }
    return (Get-Content (Join-Path (Split-Path -Parent $now[0].Path) 'build.txt')).Trim()
}

function Get-Engine {
    $marker = Join-Path $venv 'marker.txt'
    if (-not (Test-Path $marker)) { return '(none)' }
    return (Get-Content $marker).Trim()
}

function Test-IsLink([string]$path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    return ($null -ne $item) -and [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Check-Clean([string]$case) {
    $leftovers = @('app.old', 'app.new', 'venv.old') | Where-Object { Test-Path (Join-Path $root $_) }
    Check "$case leaves no app.old, app.new or venv.old" ($leftovers.Count -eq 0) ($leftovers -join ', ')
    $versions = @(Get-ChildItem -LiteralPath (Join-Path $root 'engines') -Directory -ErrorAction SilentlyContinue)
    $expected = if (Test-IsLink $venv) { 1 } else { 0 }
    Check "$case keeps only the engine in use" ($versions.Count -eq $expected) "($($versions.Count) in engines)"
}

try {
    # 1 first install
    $code = Publish 'good1' 'e1'
    Check 'first install exits 0' ($code -eq 0) "(exit $code)"
    Check 'first install runs good1' ((Get-RunningBuild) -eq 'good1') (Get-RunningBuild)
    Check 'first install switches in engine e1' ((Get-Engine) -eq 'e1') (Get-Engine)
    Check 'venv is a junction' (Test-IsLink $venv)
    $value = (Get-ItemProperty -Path $runKey -Name Peel -ErrorAction SilentlyContinue).Peel
    Check 'the Run value names the app in the root' ($value -eq ('"{0}"' -f (Join-Path $app 'Peel.exe'))) $value
    Check-Clean 'first install'

    # 2 good upgrade
    $code = Publish 'good2' 'e2'
    Check 'good upgrade exits 0' ($code -eq 0) "(exit $code)"
    Check 'good upgrade runs good2' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)
    Check 'good upgrade switches in engine e2' ((Get-Engine) -eq 'e2') (Get-Engine)
    Check-Clean 'good upgrade'

    # 3 a build that exits at startup (the bead's Definition of Done)
    $code = Publish 'quits' 'e3'
    Check 'a build that exits at startup exits 6' ($code -eq 6) "(exit $code)"
    Check 'a build that exits at startup leaves good2 running' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)
    Check 'a build that exits at startup puts engine e2 back' ((Get-Engine) -eq 'e2') (Get-Engine)
    Check-Clean 'a build that exits at startup'

    # 4 a build that quits after 5 s, inside the 10 s window
    $code = Publish 'late' 'e4'
    Check 'a build that quits after 5 s exits 6' ($code -eq 6) "(exit $code)"
    Check 'a build that quits after 5 s leaves good2 running' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)
    Check 'a build that quits after 5 s puts engine e2 back' ((Get-Engine) -eq 'e2') (Get-Engine)
    Check-Clean 'a build that quits after 5 s'

    # 5 an engine that does not build: Peel is never stopped
    $before = @(Get-Running | ForEach-Object { $_.Id })
    $code = Publish 'good1' 'fail'
    Check 'a failed engine build exits non zero' ($code -ne 0) "(exit $code)"
    $after = @(Get-Running | ForEach-Object { $_.Id })
    Check 'a failed engine build never stops Peel' (($before -join ',') -eq ($after -join ',') -and $after.Count -eq 1) "($($before -join ',') then $($after -join ','))"
    Check 'a failed engine build keeps engine e2' ((Get-Engine) -eq 'e2') (Get-Engine)
    Check-Clean 'a failed engine build'

    # 6 an older install: venv is a real folder, not a junction
    Get-Running | ForEach-Object { Stop-Process -Id $_.Id -Force; Wait-Process -Id $_.Id -Timeout 10 -ErrorAction SilentlyContinue }
    $target = @((Get-Item -LiteralPath $venv -Force).Target)[0]
    [IO.Directory]::Delete($venv, $false)
    Remove-Item -Recurse -Force (Split-Path -Parent $target)
    New-Item -ItemType Directory -Force -Path (Join-Path $venv 'Scripts') | Out-Null
    Set-Content -LiteralPath (Join-Path $venv 'marker.txt') -Value 'legacy' -Encoding Ascii
    Start-Process -FilePath (Join-Path $app 'Peel.exe')
    Start-Sleep -Seconds 1

    $code = Publish 'quits' 'e5'
    Check 'older install, failed build exits 6' ($code -eq 6) "(exit $code)"
    Check 'older install, failed build leaves good2 running' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)
    Check 'older install, failed build puts the venv folder back' ((-not (Test-IsLink $venv)) -and (Get-Engine) -eq 'legacy') (Get-Engine)
    Check-Clean 'older install, failed build'

    $code = Publish 'good1' 'e6'
    Check 'older install, good upgrade exits 0' ($code -eq 0) "(exit $code)"
    Check 'older install, good upgrade runs good1' ((Get-RunningBuild) -eq 'good1') (Get-RunningBuild)
    Check 'older install, good upgrade switches to a junction on e6' ((Test-IsLink $venv) -and (Get-Engine) -eq 'e6') (Get-Engine)
    Check-Clean 'older install, good upgrade'

    # 7 start with Windows off in Settings removes the Run value
    Set-Content -LiteralPath $settings -Value "{ ""startWithWindows"": false, // off`n }" -Encoding UTF8
    $code = Publish 'good2' 'e7'
    Check 'start with Windows off exits 0' ($code -eq 0) "(exit $code)"
    $value = (Get-ItemProperty -Path $runKey -Name Peel -ErrorAction SilentlyContinue).Peel
    Check 'start with Windows off removes the Run value' ($null -eq $value) $value
}
finally {
    Get-Running | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name Peel -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    Unregister-ScheduledTask -TaskName 'PeelStartScratch' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Split-Path -Parent $runKey) -ErrorAction SilentlyContinue
    if (Test-IsLink $venv) { [IO.Directory]::Delete($venv, $false) }
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
    $env:PEEL_TEST_ENGINE = $null
}

Write-Host ("{0} passed, {1} failed" -f $passes, $failures.Count)
if ($failures.Count -gt 0) { exit 1 }
exit 0
