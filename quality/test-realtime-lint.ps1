# Offline fixtures for the warning-only real-time test lint.
$ErrorActionPreference = 'Stop'
$lint = Join-Path $PSScriptRoot 'realtime-lint.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("realtime-lint-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$failures = 0
function Check([string]$name, [bool]$ok) {
    if ($ok) { "ok   $name" } else { "FAIL $name"; $script:failures++ }
}
function Run-Lint([string]$path) {
    $output = @(& pwsh -NoProfile -File $lint -Path $path 2>&1)
    return @{ Output = ($output -join "`n"); Exit = $LASTEXITCODE }
}
try {
    $sleep = Join-Path $tmp 'SleepTests.cs'
    @'
Thread.Sleep(10);
await Task.Delay(20);
while (DateTime.UtcNow < deadline) { }
while (deadline > DateTimeOffset.UtcNow) { }
'@ | Set-Content -LiteralPath $sleep
    $result = Run-Lint $sleep
    Check 'sleep, delay and wall-clock polling each produce a warning' (
        $result.Output -match 'Thread\.Sleep' -and $result.Output -match 'Task\.Delay' -and
        $result.Output -match 'DateTime\.UtcNow' -and $result.Output -match 'DateTimeOffset\.UtcNow')
    Check 'findings do not fail the lint' ($result.Exit -eq 0)

    $powershell = Join-Path $tmp 'test-wait.ps1'
    'Start-Sleep -Milliseconds 10' | Set-Content -LiteralPath $powershell
    $result = Run-Lint $powershell
    Check 'PowerShell sleeps produce a warning' ($result.Output -match 'Start-Sleep')
    Check 'a PowerShell sleep does not fail the lint' ($result.Exit -eq 0)

    $clean = Join-Path $tmp 'CleanTests.cs'
    @'
// Thread.Sleep is intentionally not called here.
var now = DateTime.UtcNow;
Assert.True(clock.IsReady);
'@ | Set-Content -LiteralPath $clean
    $result = Run-Lint $clean
    Check 'clean code and full-line comments produce no warning' ($result.Exit -eq 0 -and -not $result.Output)

    $result = Run-Lint (Join-Path $tmp 'missing.cs')
    Check 'an unreadable path warns without failing' ($result.Exit -eq 0 -and $result.Output -match 'WARNING')

    $generated = Join-Path $tmp 'obj'
    New-Item -ItemType Directory $generated | Out-Null
    'Thread.Sleep(10);' | Set-Content -LiteralPath (Join-Path $generated 'Generated.cs')
    $result = Run-Lint $tmp
    Check 'directory scans exclude generated files' ($result.Exit -eq 0 -and $result.Output -notmatch 'Generated\.cs')

    # Stand in for the selector to exercise the runner's failure handling without NuGet.
    $quality = Join-Path $tmp 'quality'
    $bin = Join-Path $tmp 'bin'
    New-Item -ItemType Directory $quality, $bin | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run.ps1') -Destination $quality
    $broken = Join-Path $tmp 'broken-lint.ps1'
    'exit 17' | Set-Content -LiteralPath $broken
    $selection = @{
        platform = 'win-x64'; mode = 'named'; files = @(); unclaimed = @(); skipped = @()
        suites = @(@{ id = 'realtime-lint'; runner = 'local'; status = 'partial'
            command = 'pwsh'; runCommand = "pwsh -NoProfile -File '$broken'" })
    } | ConvertTo-Json -Depth 5 -Compress
    Set-Content -LiteralPath (Join-Path $bin 'selection.json') -Value $selection
    if ($IsWindows) {
        @'
@echo off
if "%~1"=="build" exit /b 0
type "%~dp0selection.json"
'@ | Set-Content -LiteralPath (Join-Path $bin 'dotnet.cmd')
    } else {
        @'
#!/bin/sh
if [ "$1" = build ]; then exit 0; fi
cat "$(dirname "$0")/selection.json"
'@ | Set-Content -LiteralPath (Join-Path $bin 'dotnet')
        & chmod +x (Join-Path $bin 'dotnet')
    }
    $oldPath = $env:PATH
    $oldMirror = $env:AGENT_BRIDGE_NUGET_SOURCE
    try {
        $env:PATH = "$bin$([IO.Path]::PathSeparator)$oldPath"
        $env:AGENT_BRIDGE_NUGET_SOURCE = $null
        $output = @(& pwsh -NoProfile -File (Join-Path $quality 'run.ps1') -Suite realtime-lint 2>&1) -join "`n"
        $runnerExit = $LASTEXITCODE
        $ok = $runnerExit -eq 0 -and $output -match 'realtime-lint could not complete' -and $output -match '\bwarning\b'
        Check 'a broken lint warns without failing the quality run' $ok
        if (-not $ok) { "runner exit=$runnerExit`n$output" }
    } finally {
        $env:PATH = $oldPath
        $env:AGENT_BRIDGE_NUGET_SOURCE = $oldMirror
    }
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force
}
if ($failures) { throw "$failures check(s) failed" }
'all realtime lint checks passed'
