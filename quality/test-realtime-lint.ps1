# Checks quality/realtime-lint.ps1 on a made-up tree under out/test-realtime-lint: which files it
# reads, which lines it warns about, that its output is the same lines in the same order, and that
# it only ever warns.
#
#   pwsh quality/test-realtime-lint.ps1
#
# This file is itself a test the lint reads, so the APIs it looks for are spelled in pieces here.
#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$lint = Join-Path $PSScriptRoot 'realtime-lint.ps1'
$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $repo 'out' 'test-realtime-lint'
. $lint

$delay = 'Task' + '.Delay'
$utcNow = 'DateTime' + '.UtcNow'
$watch = 'Stop' + 'watch'
$psUtcNow = '[DateTime]' + '::UtcNow'
$psDelay = '[System.Threading.Tasks.Task]' + '::Delay'

function Write-Fixture([string]$relative, [string[]]$lines) {
    $path = Join-Path $scratch $relative
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}

if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
Write-Fixture 'tests/Demo.Tests/WaitTests.cs' @(
    'using System.Diagnostics;',
    "// await $delay(10); in a comment",
    "await $delay(10); var at = $utcNow;",
    "var w = $watch.StartNew();",
    'var later = DateTimeOffset.UtcNow;'
)
Write-Fixture 'tests/Demo.Tests/aLower.cs' @("    await $delay(1);")
Write-Fixture 'tests/Demo.Tests/New.cs' @("var w = $watch.StartNew();")
Write-Fixture 'tests/Demo.Tests/obj/Generated.cs' @("await $delay(1);")
Write-Fixture 'tests/Demo.Tests/View.g.cs' @("await $delay(1);")
Write-Fixture 'tests/AgentBridge.App.Tests/Speed.cs' @("var clock = $watch.StartNew();")
Write-Fixture 'tests/AgentBridge.PerfHook/Program.cs' @("var watch = $watch.StartNew();")
Write-Fixture 'src/Demo/Clock.cs' @("var now = $utcNow;")
Write-Fixture 'build/test-demo.ps1' @("`$at = $psUtcNow", "# $psUtcNow in a comment", "$psDelay(5).Wait()")
Write-Fixture 'build/demo.ps1' @("`$at = $psUtcNow")
Write-Fixture 'pwa/test/unit/demo.test.mjs' @("await $delay(1);")
# What git would track: everything but New.cs, which a change has only just added.
$tracked = @(
    'tests/Demo.Tests/WaitTests.cs', 'tests/Demo.Tests/aLower.cs', 'tests/Demo.Tests/obj/Generated.cs',
    'tests/Demo.Tests/View.g.cs', 'tests/AgentBridge.App.Tests/Speed.cs', 'tests/AgentBridge.PerfHook/Program.cs',
    'src/Demo/Clock.cs', 'build/test-demo.ps1', 'build/demo.ps1', 'pwa/test/unit/demo.test.mjs'
)
$advice = 'in a test; use a fake clock (TimeProvider) instead (#929)'

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Output "PASS $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Assert-Lines([string[]]$actual, [string[]]$expected) {
    Assert (($actual -join "`n") -ceq ($expected -join "`n")) "expected:`n$($expected -join "`n")`nactual:`n$($actual -join "`n")"
}

Check 'Every use in a test file, path:line, sorted by ordinal path, line and kind; comments, allowed files, production, generated and JS files are not read' {
    $lines = @(Get-RealTimeTestWarning -Root $scratch -Path $tracked)
    Assert-Lines $lines @(
        "warning: build/test-demo.ps1:1: DateTime.UtcNow $advice",
        "warning: build/test-demo.ps1:3: Task.Delay $advice",
        "warning: tests/Demo.Tests/WaitTests.cs:3: DateTime.UtcNow $advice",
        "warning: tests/Demo.Tests/WaitTests.cs:3: Task.Delay $advice",
        "warning: tests/Demo.Tests/WaitTests.cs:4: $watch $advice",
        "warning: tests/Demo.Tests/aLower.cs:1: Task.Delay $advice",
        "6 real-time use(s) in 3 test file(s); warnings only until Connection's fake-clock sweep lands (#929)."
    )
}

Check 'The same tree, listed in another order, prints the same lines' {
    $reversed = @($tracked)
    [array]::Reverse($reversed)
    Assert-Lines @(Get-RealTimeTestWarning -Root $scratch -Path $reversed) @(Get-RealTimeTestWarning -Root $scratch -Path $tracked)
}

Check 'With -Changed, warnings for the changed test files (a new, untracked one too), and one line counting the rest' {
    $changed = @('tests\Demo.Tests\WaitTests.cs', 'tests/Demo.Tests/New.cs', 'src/Demo/Clock.cs', 'tests/Demo.Tests/Deleted.cs')
    $lines = @(Get-RealTimeTestWarning -Root $scratch -Path $tracked -Changed $changed)
    Assert-Lines $lines @(
        "warning: tests/Demo.Tests/New.cs:1: $watch $advice",
        "warning: tests/Demo.Tests/WaitTests.cs:3: DateTime.UtcNow $advice",
        "warning: tests/Demo.Tests/WaitTests.cs:3: Task.Delay $advice",
        "warning: tests/Demo.Tests/WaitTests.cs:4: $watch $advice",
        "4 real-time use(s) in the changed test files, 3 elsewhere in the tests; warnings only until Connection's fake-clock sweep lands (#929). pwsh quality/realtime-lint.ps1 lists them all."
    )
}

Check 'A change that reaches no test with a use prints nothing' {
    Assert-Lines @(Get-RealTimeTestWarning -Root $scratch -Path @('src/Demo/Clock.cs', 'tests/AgentBridge.App.Tests/Speed.cs') -Changed @('src/Demo/Clock.cs')) @()
}

Check 'Without git it throws, for quality/run.ps1 to report rather than fail on' {
    $refusal = $null
    try { $null = Get-RealTimeTestWarning -Root (Join-Path $scratch 'no-such-folder') 2>$null } catch { $refusal = $_.Exception.Message }
    Assert ($refusal -like 'git ls-files failed in *') "didn't throw as expected: $refusal"
}

Check 'Every allowed path exists in this repository and says why' {
    foreach ($entry in (Get-RealTimeTestAllowed).GetEnumerator()) {
        Assert (Test-Path (Join-Path $repo $entry.Key)) "$($entry.Key) is allowed but doesn't exist"
        Assert ("$($entry.Value)".Length -gt 20) "$($entry.Key) is allowed without a reason"
    }
}

Check 'Run on its own, it lists this repository''s uses as warnings and a total, none from an allowed file' {
    $lines = @(& $lint)
    $warnings = @($lines | Select-Object -SkipLast 1)
    foreach ($line in $warnings) {
        Assert ($line -cmatch ('^warning: [^:]+:\d+: (Task\.Delay|DateTime\.UtcNow|' + $watch + ') in a test; ')) "not a warning: $line"
        Assert (-not (Test-RealTimeTestAllowed ($line -replace '^warning: ([^:]+):.*$', '$1'))) "an allowed file warned: $line"
    }
    if ($lines.Count -gt 0) {
        Assert ($lines[-1] -cmatch "^$($warnings.Count) real-time use\(s\) in \d+ test file\(s\); ") "not the total: $($lines[-1])"
    }
}

if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
