#Requires -Version 7
# Offline checks of quality/rerun-known-flakes.ps1 with a stand-in for gh: nothing reaches GitHub.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'rerun-known-flakes.ps1')
$script:failures = 0
function Assert([bool]$Ok, [string]$Name) { if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ } }

# --- Get-FailedTests ---
$log = @'
2026-11-09T05:00:00.0000000Z   Failed AgentBridge.Transport.Tests.ChannelTests.Revalidation_waits(outbound: True) [1 s]
2026-11-09T05:00:01.0000000Z   Failed AgentBridge.Core.Tests.Thing.Other [5 ms]
2026-11-09T05:00:02.0000000Z Failed!  - Failed:     2, Passed:    10, Skipped:     0
2026-11-09T05:00:03.0000000Z   Failed to load the dll
  1) [webkit] › history.spec.mjs:43:1 › it works
'@
$tests = Get-FailedTests $log
Assert (($tests -join '|') -eq 'AgentBridge.Core.Tests.Thing.Other|AgentBridge.Transport.Tests.ChannelTests.Revalidation_waits|history.spec.mjs:43') 'finds dotnet and Playwright failures, not summary lines'
$none = Get-FailedTests 'Build FAILED. error CS1002'
Assert ($none.Count -eq 0) 'a build error names no test'

# --- Find-FlakyIssue ---
$issues = @(
    [pscustomobject]@{ number = 1; title = 'flaky: A.B.C.Method'; body = ''; author = [pscustomobject]@{ login = 'danswett' } },
    [pscustomobject]@{ number = 2; title = 'flaky: file.spec.mjs:430'; body = ''; author = [pscustomobject]@{ login = 'app/github-actions' } },
    [pscustomobject]@{ number = 3; title = 'flaky: X.Y.Z'; body = ''; author = [pscustomobject]@{ login = 'stranger' } }
)
Assert ((Find-FlakyIssue 'A.B.C.Method' $issues).number -eq 1) 'matches a test named in a trusted issue'
Assert ($null -eq (Find-FlakyIssue 'A.B.C' $issues)) 'a prefix of a name does not match'
Assert ($null -eq (Find-FlakyIssue 'A.B.C.Method.More' $issues)) 'a longer name does not match'
Assert ($null -eq (Find-FlakyIssue 'file.spec.mjs:43' $issues)) 'file:43 does not match file:430'
$issues += [pscustomobject]@{ number = 4; title = 'flaky: reduced-motion.spec.mjs:20 and e2e/deep.spec.mjs:7'; body = ''; author = [pscustomobject]@{ login = 'danswett' } }
Assert ($null -eq (Find-FlakyIssue 'motion.spec.mjs:20' $issues)) 'motion.spec.mjs:20 does not match reduced-motion.spec.mjs:20'
Assert ($null -eq (Find-FlakyIssue 'deep.spec.mjs:7' $issues)) 'a bare file name does not match inside a path'
Assert ((Find-FlakyIssue 'reduced-motion.spec.mjs:20' $issues).number -eq 4) 'a hyphenated file name matches itself'
Assert ((Find-FlakyIssue 'file.spec.mjs:430' $issues).number -eq 2) 'the hunt bot counts as a trusted author'
Assert ($null -eq (Find-FlakyIssue 'X.Y.Z' $issues)) 'an issue from anyone else does not count'

# --- Get-RerunDecision ---
function Decide($map) { Get-RerunDecision $map $issues }
Assert ((Decide ([ordered]@{ w = @('A.B.C.Method') })).Rerun) 'one known flake: re-run'
Assert ((Decide ([ordered]@{ w = @('A.B.C.Method'); p = @('file.spec.mjs:430') })).Rerun) 'two jobs, both known: re-run'
Assert (-not (Decide ([ordered]@{ w = @('A.B.C.Method', 'New.Real.Failure') })).Rerun) 'one unknown test among known ones: stay red'
Assert (-not (Decide ([ordered]@{ w = @('A.B.C.Method'); b = @() })).Rerun) 'a failed job with no test failure: stay red'
Assert (-not (Decide ([ordered]@{})).Rerun) 'no failed job: nothing to re-run'

# --- Invoke-RerunKnownFlakes against a fake gh ---
$script:GhCommand = 'Fake-Gh'
$script:Scenario = @{}
function Fake-Gh {
    $a = $args -join ' '
    $script:Calls += , $a
    $s = $script:Scenario
    switch -Regex ($a) {
        '^api repos/r/actions/runs/100$' {
            @{ path = '.github/workflows/ci.yml'; event = 'push'; head_branch = 'main'; head_repository = @{ full_name = 'r' }
                conclusion = $s.conclusion; run_attempt = $s.attempt; created_at = '2026-11-09T05:00:00Z'; html_url = 'https://example/run/100'
                head_sha = 'abcdef0123456789' } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/runs\?' { @{ workflow_runs = @($s.newer) } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/runs/100/jobs' {
            $js = @(@{ id = 7; name = 'windows'; conclusion = $s.jobConclusion })
            if ($s.gate) { $js += @{ id = 8; name = 'quality-gate'; conclusion = 'failure' } }
            @{ jobs = $js } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/jobs/8/logs' { 'Error: Process completed with exit code 1.'; return }
        '^api repos/r/actions/jobs/7/logs' { $s.log; return }
        '^issue list' { ($issues | ConvertTo-Json -Depth 4 -AsArray); return }
        '^run rerun' { return }
        '^issue comment' { return }
    }
}
function Run-Scenario([hashtable]$Over, [switch]$DryRun) {
    $script:Scenario = @{ conclusion = 'failure'; attempt = 1; newer = @(); jobConclusion = 'failure'; gate = $false
        log = '  Failed A.B.C.Method [1 s]' } + @{}
    foreach ($k in $Over.Keys) { $script:Scenario[$k] = $Over[$k] }
    $script:Calls = @()
    $r = Invoke-RerunKnownFlakes -RunId 100 -Repository 'r' -DryRun:$DryRun
    [pscustomobject]@{ Result = $r; Reran = @($script:Calls | Where-Object { $_ -like 'run rerun*' }).Count; Comments = @($script:Calls | Where-Object { $_ -like 'issue comment*' }).Count }
}

$x = Run-Scenario @{}
Assert ($x.Result.Rerun -and $x.Reran -eq 1 -and $x.Comments -eq 1) 'a known flake is re-run once and logged on its issue'
$x = Run-Scenario @{} -DryRun
Assert ($x.Result.Rerun -and $x.Reran -eq 0 -and $x.Comments -eq 0) 'a dry run re-runs and comments on nothing'
$x = Run-Scenario @{ gate = $true }
Assert ($x.Result.Rerun -and $x.Reran -eq 1) "the red quality-gate job, which has no test of its own, does not stop a known flake's re-run"
$x = Run-Scenario @{ gate = $true; log = '  Failed New.Real.Failure [1 s]' }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'with the gate skipped, an unfiled failure still stays red'
$x = Run-Scenario @{ gate = $true; jobConclusion = 'success' }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a red gate with no other failed job is left alone'
$x = Run-Scenario @{ attempt = 2 }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a second attempt is never re-run'
$x = Run-Scenario @{ conclusion = 'cancelled' }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a cancelled run is left alone'
$x = Run-Scenario @{ newer = @(@{ id = 101; path = '.github/workflows/ci.yml'; created_at = '2026-11-09T05:10:00Z' }) }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a newer push run supersedes it'
$x = Run-Scenario @{ log = '  Failed New.Real.Failure [1 s]' }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0 -and $x.Comments -eq 0) 'an unfiled failure stays red'
$x = Run-Scenario @{ log = 'error CS1002: ; expected' }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a build error stays red'
$x = Run-Scenario @{ jobConclusion = 'timed_out' }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a timed-out job stays red'

if ($script:failures) { "$($script:failures) failure(s)"; exit 1 }
'rerun-known-flakes: all checks passed'
