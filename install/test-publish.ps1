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
  and one that starts from an older install with real app and venv folders. Nothing moves the mouse or the
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

# The stand in: mode.txt next to it says stay, exit (at once), late (after
# 5 s), helper (starts sub\Helper.exe, which keeps its own image locked, then
# quits) or clobber (rewrites the file settings.txt names, then quits).
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
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
'@
$modes = @(@('good1', 'stay'), @('good2', 'stay'), @('quits', 'exit'), @('late', 'late'), @('locks', 'helper'), @('clobber', 'clobber'))
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
Set-Content -LiteralPath (Join-Path $builds 'clobber\settings.txt') -Value $settings -Encoding Ascii

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

function Check-Clean([string]$case) {
    $versions = @(Get-ChildItem -LiteralPath (Join-Path $root 'versions') -Directory -ErrorAction SilentlyContinue)
    Check "$case keeps only the pair in use" ($versions.Count -eq 1) "($($versions.Count) in versions)"
    $journal = Get-Journal
    Check "$case leaves no candidate in the journal" ($null -ne $journal -and $null -eq $journal.candidate -and $null -ne $journal.verified)
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
    Check-Clean 'after a killed run'
    $code = Publish 'good2' 'e13'
    Check 'after a killed run, an upgrade exits 0' ($code -eq 0) "(exit $code)"

    # 10 a build that rewrites settings.json and quits
    $settingsBytes = [IO.File]::ReadAllBytes($settings)
    $code = Publish 'clobber' 'e14'
    Check 'a build that rewrites settings exits 6' ($code -eq 6) "(exit $code)"
    $now = [IO.File]::ReadAllBytes($settings)
    Check 'a build that rewrites settings gets the old bytes back' ([Convert]::ToBase64String($now) -eq [Convert]::ToBase64String($settingsBytes)) ([Text.Encoding]::UTF8.GetString($now))
    Check 'a build that rewrites settings leaves good2 running' ((Get-RunningBuild) -eq 'good2') (Get-RunningBuild)

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
