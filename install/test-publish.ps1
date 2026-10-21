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
  build quits after 5 s, one whose engine build fails, one whose build leaves
  a file locked as it quits, one whose start throws, one killed between the
  two junctions, one whose build rewrites settings.json, two runs at once,
  and one that starts from an older install with real app and venv folders.
  The lucy round 2 cases (hq-plct.26): a failed first install that the next
  run must not call verified, a -NoStart run whose Run value must keep naming
  the verified build, a run killed after the app switched (the next run must
  start Peel again), a publisher killed in the watch window after its build
  rewrote settings.json, and a settings.json or install.json that cannot be
  read or written once Peel is stopped. Review round 1 (hq-plct.26): a
  failed first install must leave no Run value of ours, a rollback killed
  before Peel starts again must leave the restart to the next run, and a
  rollback that cannot put a read only settings.json back must keep the
  journal and settings.before, exit 7 and leave Peel stopped, with no
  start with Windows entry until a run can put settings.json back
  (hq-plct.29). With
  -RealEngine (hq-plct.28) it also runs publish.ps1's real pip build once,
  with -NoStart, from a scratch venv that lacks openpyxl: the new venv must
  have every module in modules.txt and a green peel doctor. That one needs
  the backends setup.ps1 installs and pip's network or cache, and takes a
  few minutes. Nothing moves the mouse or the
  keyboard; the stand in starts in the signed in session through a scheduled
  task, so someone has to be signed in at George.

  ssh george "powershell -NoProfile -ExecutionPolicy Bypass -File C:\peel-dev\<branch>\install\test-publish.ps1 [-RealEngine]"
#>
[CmdletBinding()]
param([switch]$RealEngine)

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

# The stand in: mode.txt next to it says stay, exit (at once), late (after
# 5 s), helper (starts sub\Helper.exe, which keeps its own image locked, then
# quits), clobber (rewrites the file settings.txt names, then quits) or
# rewrite (rewrites it and stays up).
$fake = Join-Path $scratch 'Peel.exe'
Add-Type -OutputType WindowsApplication -OutputAssembly $fake -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
public static class FakePeel
{
    public static int Main()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var mode = File.ReadAllText(Path.Combine(dir, "mode.txt")).Trim();
        if (mode == "exit") { return 1; }
        if (mode == "late") { Thread.Sleep(5000); return 1; }
        if (mode == "helper") { Process.Start(Path.Combine(dir, "sub", "Helper.exe")); return 1; }
        if (mode == "clobber")
        {
            File.WriteAllText(File.ReadAllText(Path.Combine(dir, "settings.txt")).Trim(), "{ \"migrated\": true }");
            return 1;
        }
        if (mode == "rewrite")
        {
            File.WriteAllText(File.ReadAllText(Path.Combine(dir, "settings.txt")).Trim(), "{ \"migrated\": true }");
        }
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
'@
$modes = @(@('good1', 'stay'), @('good2', 'stay'), @('quits', 'exit'), @('late', 'late'), @('locks', 'helper'), @('clobber', 'clobber'), @('rewrites', 'rewrite'))
foreach ($build in $modes) {
    $dir = Join-Path $builds $build[0]
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item $fake (Join-Path $dir 'Peel.exe')
    Set-Content -LiteralPath (Join-Path $dir 'mode.txt') -Value $build[1] -Encoding Ascii
    Set-Content -LiteralPath (Join-Path $dir 'build.txt') -Value $build[0] -Encoding Ascii
}
$helperDir = Join-Path $builds 'locks\sub'
New-Item -ItemType Directory -Force -Path $helperDir | Out-Null
Copy-Item $fake (Join-Path $helperDir 'Helper.exe')
Set-Content -LiteralPath (Join-Path $helperDir 'mode.txt') -Value 'stay' -Encoding Ascii
foreach ($name in @('clobber', 'rewrites')) {
    Set-Content -LiteralPath (Join-Path $builds "$name\settings.txt") -Value $settings -Encoding Ascii
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

function Get-PublishArgs([string]$build) {
    return @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'publish.ps1'),
        '-InstallRoot', $root, '-SettingsPath', $settings, '-RunKey', $runKey,
        '-PrebuiltApp', (Join-Path $builds $build), '-EngineBuilder', $builder)
}

function Publish([string]$build, [string]$engine, [string[]]$extra = @()) {
    $env:PEEL_TEST_ENGINE = $engine
    $log = Join-Path $scratch "publish-$build-$engine.log"
    # The child's own error text goes to the log, not into a stop here.
    $ErrorActionPreference = 'Continue'
    $publishArgs = @(Get-PublishArgs $build) + $extra
    & powershell @publishArgs *> $log
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

# Which build app points at, running or not.
function Get-AppBuild {
    $file = Join-Path $app 'build.txt'
    if (-not (Test-Path $file)) { return '(none)' }
    return (Get-Content $file).Trim()
}

function Get-Journal {
    return (Get-Content -LiteralPath (Join-Path $root 'install.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json)
}

function Stop-Scratch([string]$name) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue; Wait-Process -Id $_.Id -Timeout 10 -ErrorAction SilentlyContinue }
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

function Get-RunValue {
    return (Get-ItemProperty -Path $runKey -Name Peel -ErrorAction SilentlyContinue).Peel
}

function Get-Bytes([string]$path) {
    return [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
}

# A publisher whose build rewrites settings.json, killed in its watch window
# once the rewrite is on disk; returns whether the rewrite was seen.
function Invoke-KilledWatch([string]$engine) {
    $before = Get-Bytes $settings
    $env:PEEL_TEST_ENGINE = $engine
    $log = Join-Path $scratch "publish-killed-$engine.log"
    $killed = Start-Process -FilePath powershell -ArgumentList (@(Get-PublishArgs 'rewrites') + @('-StaySeconds', '120')) -PassThru -WindowStyle Hidden -RedirectStandardOutput $log
    $null = $killed.Handle
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Bytes $settings) -eq $before -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    $rewritten = (Get-Bytes $settings) -ne $before
    Stop-Process -Id $killed.Id -Force
    $killed.WaitForExit()
    return $rewritten
}

function Check-Clean([string]$case) {
    $versions = @(Get-ChildItem -LiteralPath (Join-Path $root 'versions') -Directory -ErrorAction SilentlyContinue)
    Check "$case keeps only the pair in use" ($versions.Count -eq 1) "($($versions.Count) in versions)"
    $journal = Get-Journal
    Check "$case leaves no candidate in the journal" ($null -ne $journal -and $null -eq $journal.candidate -and $null -ne $journal.verified)
}

try {
    # 0 a first install whose build quits: nothing verified it, so the next
    # run must not count it as verified (lucy round 2, finding 5)
    $code = Publish 'quits' 'e0'
    Check 'a failed first install exits 4' ($code -eq 4) "(exit $code)"
    Check 'a failed first install has no verified pair' ($null -eq (Get-Journal).verified)
    # Nothing verified, so log on must not start the candidate (review round 1, b).
    $value = Get-RunValue
    Check 'a failed first install registers no start with Windows entry' ($null -eq $value) $value
    $code = Publish 'quits' 'e0b'
    Check 'the next failed build after a failed first install exits 4, not 6' ($code -eq 4) "(exit $code)"
    Check 'the next run never marks the failed first install verified' ($null -eq (Get-Journal).verified) (ConvertTo-Json -Compress (Get-Journal))
    $value = Get-RunValue
    Check 'the next failed build after a failed first install registers no entry either' ($null -eq $value) $value
    foreach ($link in @($app, $venv)) { if (Test-IsLink $link) { [IO.Directory]::Delete($link, $false) } }
    Remove-Item -Recurse -Force (Join-Path $root 'versions'), (Join-Path $root 'install.json')
    Remove-ItemProperty -Path $runKey -Name Peel -ErrorAction SilentlyContinue

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

    # 2b -NoStart: nothing verifies the new build, so log on must still start
    # the verified one (lucy round 2, finding 1)
    $code = Publish 'good1' 'e2n' @('-NoStart')
    Check '-NoStart exits 0' ($code -eq 0) "(exit $code)"
    Check '-NoStart points app at the new build' ((Get-AppBuild) -eq 'good1') (Get-AppBuild)
    $value = Get-RunValue
    $pinned = ($value -replace '"', '')
    $pinnedBuild = '(none)'
    if ($pinned -and (Test-Path (Join-Path (Split-Path -Parent $pinned) 'build.txt'))) {
        $pinnedBuild = (Get-Content (Join-Path (Split-Path -Parent $pinned) 'build.txt')).Trim()
    }
    Check '-NoStart keeps the Run value on the verified build' ($pinnedBuild -eq 'good2' -and $pinned -ne (Join-Path $app 'Peel.exe')) $value
    $code = Publish 'good2' 'e2'
    Check 'after -NoStart, an upgrade exits 0' ($code -eq 0) "(exit $code)"
    $value = Get-RunValue
    Check 'after -NoStart, a verified upgrade names the app in the root again' ($value -eq ('"{0}"' -f (Join-Path $app 'Peel.exe'))) $value
    Check-Clean 'after -NoStart, an upgrade'

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

    # 6 a build that quits and leaves a file locked in its app folder (a
    # helper still running; a virus scan is the real analogue)
    $code = Publish 'locks' 'e8'
    Check 'a locked file in the failed build still exits 6' ($code -eq 6) "(exit $code)"
    Check 'a locked file in the failed build leaves good2 running' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)
    Check 'a locked file in the failed build points app at good2' ((Get-AppBuild) -eq 'good2') (Get-AppBuild)
    Check 'a locked file in the failed build puts engine e2 back' ((Get-Engine) -eq 'e2') (Get-Engine)

    # 7 the next run with a failed engine build still keeps the verified pair
    $before = @(Get-Running | ForEach-Object { $_.Id })
    $code = Publish 'good1' 'fail'
    Check 'after a locked rollback, a failed engine build exits non zero' ($code -ne 0) "(exit $code)"
    Check 'after a locked rollback, the verified pair is still good2 and e2' ((Get-AppBuild) -eq 'good2' -and (Get-Engine) -eq 'e2') "$(Get-AppBuild) $(Get-Engine)"
    Check 'after a locked rollback, Peel was never stopped' ((@(Get-Running | ForEach-Object { $_.Id }) -join ',') -eq ($before -join ',') -and $before.Count -eq 1)
    Stop-Scratch 'Helper'
    $code = Publish 'good2' 'e9'
    Check 'with the lock gone, an upgrade exits 0' ($code -eq 0) "(exit $code)"
    Check-Clean 'with the lock gone, an upgrade'

    # 8 the start itself throws (Task Scheduler refuses the task)
    $code = Publish 'good1' 'e10' @('-StartTask', 'Peel<bad>')
    Check 'a start that throws exits 6' ($code -eq 6) "(exit $code)"
    Check 'a start that throws points app at good2' ((Get-AppBuild) -eq 'good2') (Get-AppBuild)
    Check 'a start that throws puts engine e9 back' ((Get-Engine) -eq 'e9') (Get-Engine)
    Check-Clean 'a start that throws'
    $code = Publish 'good2' 'e11'
    Check 'after a start that threw, an upgrade exits 0' ($code -eq 0) "(exit $code)"

    # 9 a run killed after app is switched and before venv is
    $env:PEEL_PUBLISH_TEST_DIE = 'mid-switch'
    $code = Publish 'good1' 'e12'
    $env:PEEL_PUBLISH_TEST_DIE = $null
    Check 'the killed run died mid switch' ($code -eq 9) "(exit $code)"
    Check 'the killed run left app and engine from different pairs' ((Get-AppBuild) -eq 'good1' -and (Get-Engine) -eq 'e11') "$(Get-AppBuild) $(Get-Engine)"
    $code = Publish 'good1' 'fail'
    Check 'after a killed run, a failed engine build exits non zero' ($code -ne 0) "(exit $code)"
    Check 'after a killed run, the next run puts the verified pair back together' ((Get-AppBuild) -eq 'good2' -and (Get-Engine) -eq 'e11') "$(Get-AppBuild) $(Get-Engine)"
    # The killed run had stopped Peel (lucy round 2, finding 4).
    Check 'after a killed run, the next run starts the verified Peel again' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)
    Check-Clean 'after a killed run'
    $code = Publish 'good2' 'e13'
    Check 'after a killed run, an upgrade exits 0' ($code -eq 0) "(exit $code)"

    # 9b the recovery of a killed run is itself killed after its rollback and
    # before Peel starts again: the run after it must still start Peel
    # (review round 1, c)
    $env:PEEL_PUBLISH_TEST_DIE = 'mid-switch'
    $code = Publish 'good1' 'e13m'
    Check 'the first run died mid switch' ($code -eq 9) "(exit $code)"
    $env:PEEL_PUBLISH_TEST_DIE = 'before-restart'
    $code = Publish 'good1' 'fail'
    $env:PEEL_PUBLISH_TEST_DIE = $null
    Check 'the recovery died after its rollback with Peel stopped' ($code -eq 9 -and (Get-Running).Count -eq 0) "(exit $code, $((Get-Running).Count) running)"
    Check 'the killed recovery leaves the restart in the journal' ([bool](Get-Journal).restart) (ConvertTo-Json -Compress (Get-Journal))
    $code = Publish 'good1' 'fail'
    Check 'after a killed recovery, a failed engine build exits non zero' ($code -ne 0) "(exit $code)"
    Check 'after a killed recovery, the next run starts good2 on e13 again' ((Get-RunningBuild) -eq 'good2' -and (Get-Engine) -eq 'e13') "$(Get-RunningBuild) $(Get-Engine)"
    Check 'after a killed recovery, the restart flag is gone once Peel runs' (-not (Get-Journal).restart) (ConvertTo-Json -Compress (Get-Journal))
    Check-Clean 'after a killed recovery'

    # 10 a build that rewrites settings.json and quits
    $settingsBytes = [IO.File]::ReadAllBytes($settings)
    $code = Publish 'clobber' 'e14'
    Check 'a build that rewrites settings exits 6' ($code -eq 6) "(exit $code)"
    $now = [IO.File]::ReadAllBytes($settings)
    Check 'a build that rewrites settings gets the old bytes back' ([Convert]::ToBase64String($now) -eq [Convert]::ToBase64String($settingsBytes)) ([Text.Encoding]::UTF8.GetString($now))
    Check 'a build that rewrites settings leaves good2 running' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)

    # 10b the publisher is killed in the watch window, after its build has
    # rewritten settings.json: the next run puts the bytes back (lucy round 2,
    # finding 2)
    $settingsBytes = Get-Bytes $settings
    Check 'the killed watch: the new build rewrote settings first' (Invoke-KilledWatch 'e17')
    $code = Publish 'good1' 'fail'
    Check 'after a kill in the watch window, a failed engine build exits non zero' ($code -ne 0) "(exit $code)"
    Check 'after a kill in the watch window, the next run gets the old settings bytes back' ((Get-Bytes $settings) -eq $settingsBytes) ([IO.File]::ReadAllText($settings))
    Check 'after a kill in the watch window, good2 runs on engine e13' ((Get-RunningBuild) -eq 'good2' -and (Get-Engine) -eq 'e13') "$(Get-RunningBuild) $(Get-Engine)"
    Check-Clean 'after a kill in the watch window'

    # 10c as 10b, but settings.json is read only when the next run rolls
    # back: the rollback is not finished, so it keeps the journal and the
    # saved bytes, exits 7 and leaves Peel stopped (review round 1, a)
    $settingsBytes = Get-Bytes $settings
    Check 'the read only watch: the new build rewrote settings first' (Invoke-KilledWatch 'e17r')
    $settingsItem = Get-Item -LiteralPath $settings
    $settingsItem.IsReadOnly = $true
    $code = Publish 'good1' 'fail'
    $settingsItem.IsReadOnly = $false
    Check 'a settings file that cannot be put back exits 7' ($code -eq 7) "(exit $code)"
    $snapshot = Join-Path $root 'settings.before'
    Check 'a settings file that cannot be put back keeps settings.before' ((Test-Path $snapshot) -and (Get-Bytes $snapshot) -eq $settingsBytes)
    Check 'a settings file that cannot be put back keeps the candidate in the journal' ($null -ne (Get-Journal).candidate) (ConvertTo-Json -Compress (Get-Journal))
    Check 'a settings file that cannot be put back leaves Peel stopped' ((Get-Running).Count -eq 0) "($((Get-Running).Count) running)"
    # Log on must not start Peel on the candidate's settings (hq-plct.29).
    $value = Get-RunValue
    Check 'a settings file that cannot be put back drops the start with Windows entry' ($null -eq $value) $value
    Check 'a settings file that cannot be put back records the dropped entry' ([bool](Get-Journal).run) (ConvertTo-Json -Compress (Get-Journal))
    # A second run that still cannot write it keeps that record, with no entry of ours left to see.
    $settingsItem.IsReadOnly = $true
    $code = Publish 'good1' 'fail'
    $settingsItem.IsReadOnly = $false
    Check 'a second run with settings still read only exits 7' ($code -eq 7) "(exit $code)"
    $value = Get-RunValue
    Check 'a second run with settings still read only registers no entry' ($null -eq $value) $value
    Check 'a second run with settings still read only keeps the dropped entry record' ([bool](Get-Journal).run) (ConvertTo-Json -Compress (Get-Journal))
    $code = Publish 'good1' 'fail'
    Check 'once settings is writable, a failed engine build exits non zero' ($code -ne 0 -and $code -ne 7) "(exit $code)"
    Check 'once settings is writable, the old settings bytes are back' ((Get-Bytes $settings) -eq $settingsBytes) ([IO.File]::ReadAllText($settings))
    Check 'once settings is writable, good2 runs on engine e13' ((Get-RunningBuild) -eq 'good2' -and (Get-Engine) -eq 'e13') "$(Get-RunningBuild) $(Get-Engine)"
    $value = Get-RunValue
    Check 'once settings is writable, the start with Windows entry names the app in the root again' ($value -eq ('"{0}"' -f (Join-Path $app 'Peel.exe'))) $value
    Check 'once settings is writable, the dropped entry record is gone' (-not (Get-Journal).run) (ConvertTo-Json -Compress (Get-Journal))
    Check-Clean 'once settings is writable'

    # 11 two runs at once: the second one must not touch the install
    $env:PEEL_TEST_ENGINE = 'e15'
    $firstLog = Join-Path $scratch 'publish-first.log'
    $first = Start-Process -FilePath powershell -ArgumentList (Get-PublishArgs 'good1') -PassThru -WindowStyle Hidden -RedirectStandardOutput $firstLog
    $null = $first.Handle  # Windows PowerShell 5.1 has no ExitCode later without it
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Select-String -LiteralPath $firstLog -Pattern 'installed ' -Quiet -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    $code = Publish 'good2' 'e16'
    Check 'a second run while one runs exits 5' ($code -eq 5) "(exit $code)"
    $first.WaitForExit()
    Check 'the first run still finishes with good1 and e15' ($first.ExitCode -eq 0 -and (Get-RunningBuild) -eq 'good1' -and (Get-Engine) -eq 'e15') "(exit $($first.ExitCode), $(Get-RunningBuild), $(Get-Engine))"
    Check-Clean 'two runs at once'

    # 11b settings.json cannot be read once Peel is stopped (a folder there):
    # nothing switches and the stopped Peel starts again (lucy round 2,
    # finding 3)
    $savedSettings = $settings
    $settings = Join-Path $scratch 'settings-folder'
    New-Item -ItemType Directory -Force -Path $settings | Out-Null
    $code = Publish 'good2' 'e18'
    $settings = $savedSettings
    Check 'an unreadable settings file exits 1' ($code -eq 1) "(exit $code)"
    Check 'an unreadable settings file leaves good1 and e15 running' ((Get-RunningBuild) -eq 'good1' -and (Get-AppBuild) -eq 'good1' -and (Get-Engine) -eq 'e15') "$(Get-RunningBuild) $(Get-AppBuild) $(Get-Engine)"
    Check-Clean 'an unreadable settings file'

    # 11c install.json cannot be written once Peel is stopped
    $journalItem = Get-Item -LiteralPath (Join-Path $root 'install.json')
    $journalItem.IsReadOnly = $true
    $code = Publish 'good2' 'e19'
    $journalItem.IsReadOnly = $false
    Check 'a journal that cannot be written exits 1' ($code -eq 1) "(exit $code)"
    Check 'a journal that cannot be written leaves good1 and e15 running' ((Get-RunningBuild) -eq 'good1' -and (Get-AppBuild) -eq 'good1' -and (Get-Engine) -eq 'e15') "$(Get-RunningBuild) $(Get-AppBuild) $(Get-Engine)"
    Check-Clean 'a journal that cannot be written'

    # 12 an older install: app and venv are real folders, no journal
    Get-Running | ForEach-Object { Stop-Process -Id $_.Id -Force; Wait-Process -Id $_.Id -Timeout 10 -ErrorAction SilentlyContinue }
    [IO.Directory]::Delete($app, $false)
    [IO.Directory]::Delete($venv, $false)
    Remove-Item -Recurse -Force (Join-Path $root 'versions'), (Join-Path $root 'install.json')
    Copy-Item -LiteralPath (Join-Path $builds 'good2') -Destination $app -Recurse
    New-Item -ItemType Directory -Force -Path (Join-Path $venv 'Scripts') | Out-Null
    Set-Content -LiteralPath (Join-Path $venv 'marker.txt') -Value 'legacy' -Encoding Ascii
    Start-Process -FilePath (Join-Path $app 'Peel.exe')
    Start-Sleep -Seconds 1

    $code = Publish 'quits' 'e5'
    Check 'older install, failed build exits 6' ($code -eq 6) "(exit $code)"
    Check 'older install, failed build leaves good2 running' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)
    Check 'older install, failed build puts the old engine back' ((Get-Engine) -eq 'legacy') (Get-Engine)
    Check-Clean 'older install, failed build'

    $code = Publish 'good1' 'e6'
    Check 'older install, good upgrade exits 0' ($code -eq 0) "(exit $code)"
    Check 'older install, good upgrade runs good1' ((Get-RunningBuild) -eq 'good1') (Get-RunningBuild)
    Check 'older install, good upgrade switches to a junction on e6' ((Test-IsLink $venv) -and (Get-Engine) -eq 'e6') (Get-Engine)
    Check-Clean 'older install, good upgrade'

    # 13 start with Windows off in Settings removes the Run value
    Set-Content -LiteralPath $settings -Value "{ ""startWithWindows"": false, // off`n }" -Encoding UTF8
    $code = Publish 'good2' 'e7'
    Check 'start with Windows off exits 0' ($code -eq 0) "(exit $code)"
    $value = (Get-ItemProperty -Path $runKey -Name Peel -ErrorAction SilentlyContinue).Peel
    Check 'start with Windows off removes the Run value' ($null -eq $value) $value

    # 14 the real pip build: a current venv that lacks a pinned module gets
    # it in the new venv (hq-plct.28). Its own root, so the cases above keep
    # theirs; -NoStart, since the stand in app has no engine to talk to.
    if ($RealEngine) {
        $realRoot = Join-Path $scratch 'real'
        $realVenv = Join-Path $realRoot 'venv'
        $realPy = Join-Path $realVenv 'Scripts\python.exe'
        $ErrorActionPreference = 'Continue'
        & py -3 -m venv $realVenv *> (Join-Path $scratch 'real-venv.log')
        $lacking = Join-Path $scratch 'modules-without-openpyxl.txt'
        Get-Content (Join-Path $here 'modules.txt') | Where-Object { $_ -notmatch '^\s*openpyxl\b' } | Set-Content -LiteralPath $lacking -Encoding Ascii
        & $realPy -m pip install --quiet --disable-pip-version-check -r $lacking *> (Join-Path $scratch 'real-pip.log')
        & $realPy -m pip install --quiet --disable-pip-version-check (Join-Path (Split-Path -Parent $here) 'engine') *>> (Join-Path $scratch 'real-pip.log')
        $templateMissing = @((& (Join-Path $realVenv 'Scripts\peel.exe') doctor | Out-String | ConvertFrom-Json).missing)
        Check 'the real template venv lacks openpyxl' ($templateMissing -contains 'openpyxl') ($templateMissing -join ', ')

        $log = Join-Path $scratch 'publish-real.log'
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'publish.ps1') -InstallRoot $realRoot `
            -SettingsPath $settings -RunKey $runKey -PrebuiltApp (Join-Path $builds 'good1') -NoStart *> $log
        $code = $LASTEXITCODE
        Get-Content $log | Where-Object { $_ -match 'peel-publish' } | ForEach-Object { Write-Host "     $_" }
        Check 'the real publish exits 0' ($code -eq 0) "(exit $code)"
        Check 'the real publish switches venv to a junction' (Test-IsLink $realVenv)
        $newPy = Join-Path $realVenv 'Scripts\python.exe'
        $frozen = @(& $newPy -m pip freeze --disable-pip-version-check 2> $null)
        $pins = @(Get-Content (Join-Path $here 'modules.txt') | Where-Object { $_ -match '^[A-Za-z]' } | ForEach-Object { $_.Trim().ToLowerInvariant() -replace '_', '-' })
        $have = @($frozen | ForEach-Object { $_.Trim().ToLowerInvariant() -replace '_', '-' })
        $absent = @($pins | Where-Object { $have -notcontains $_ })
        Check 'the real publish installs every pinned module at its pin' ($absent.Count -eq 0) ($absent -join ', ')
        $newMissing = @((& (Join-Path $realVenv 'Scripts\peel.exe') doctor | Out-String | ConvertFrom-Json).missing)
        Check 'the real publish leaves peel doctor green' ($newMissing.Count -eq 0) ($newMissing -join ', ')
        $ErrorActionPreference = 'Stop'
        if (Test-IsLink $realVenv) { [IO.Directory]::Delete($realVenv, $false) }
        if (Test-IsLink (Join-Path $realRoot 'app')) { [IO.Directory]::Delete((Join-Path $realRoot 'app'), $false) }
    }
}
finally {
    Stop-Scratch 'Peel'
    Stop-Scratch 'Helper'
    Unregister-ScheduledTask -TaskName 'PeelStartScratch' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Split-Path -Parent $runKey) -ErrorAction SilentlyContinue
    foreach ($link in @($app, $venv)) { if (Test-IsLink $link) { [IO.Directory]::Delete($link, $false) } }
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
    $env:PEEL_TEST_ENGINE = $null
    $env:PEEL_PUBLISH_TEST_DIE = $null
}

Write-Host ("{0} passed, {1} failed" -f $passes, $failures.Count)
if ($failures.Count -gt 0) { exit 1 }
exit 0
