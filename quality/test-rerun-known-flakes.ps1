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
            @{ id = 100; status = $s.status; path = $s.path; event = $s.event; head_branch = $s.branch; head_repository = @{ full_name = $s.repository }
                conclusion = $s.conclusion; run_attempt = $s.attempt; created_at = '2026-11-09T05:00:00Z'; html_url = 'https://example/run/100'
                head_sha = ('abcdef01' * 5) } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/runs\?' { @{ workflow_runs = @($s.newer) } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/workflows/ci.yml/runs\?' { @{ workflow_runs = @($s.newer) } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/runs/100/attempts/1/jobs' {
            @{ jobs = @(@{ id = 9; name = 'original'; conclusion = 'failure' }) } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/runs/100/jobs' {
            $js = @(@{ id = 7; name = $s.jobName; labels = $s.labels; conclusion = $s.jobConclusion }) + @($s.extraJobs)
            if ($s.gate) { $js += @{ id = 8; name = 'quality-gate'; conclusion = 'failure' } }
            @{ jobs = $js } | ConvertTo-Json -Depth 4; return }
        '^api repos/r/actions/jobs/8/logs' { 'Error: Process completed with exit code 1.'; return }
        '^api repos/r/actions/jobs/7/logs' {
            if ($s.logFails) { $global:LASTEXITCODE = 1; return }
            if ($s.log.Contains([string][char]27) -and '--allow-escape-sequences' -notin $args) {
                $global:LASTEXITCODE = 1; return
            }
            $s.log; return }
        '^api repos/r/actions/jobs/9/logs' { $s.extraLog; return }
        '^issue list.*--label main-red' {
            if ($a -match '--state open' -and $s.incidentState -eq 'closed') { '[]'; return }
            @(@{ number = 40; body = $s.incidentBody
                comments = @($s.comments) }) | ConvertTo-Json -Depth 6 -AsArray; return }
        '^issue list' { ($issues | ConvertTo-Json -Depth 4 -AsArray); return }
        '^issue view' { @{ comments = @($s.comments) } | ConvertTo-Json -Depth 6; return }
        '^run rerun' { if ($s.dispatchFails) { $global:LASTEXITCODE = 1 }; return }
        '^issue comment' {
            $s.comments += @{ author = @{ login = 'github-actions[bot]' }; body = $args[([array]::IndexOf($args, '--body') + 1)] }; return }
        '^issue edit' { return }
        default { throw "Unexpected gh request: $a" }
    }
}
function Run-Scenario([hashtable]$Over, [switch]$DryRun, [switch]$AutoRerun, [switch]$Replay) {
    $script:Scenario = @{ conclusion = 'failure'; attempt = 1; newer = @(); jobConclusion = 'failure'; gate = $false
        jobName = 'product (windows-latest)'; labels = @('windows-latest'); extraJobs = @()
        extraLog = '  Failed A.B.C.Method [1 s]'; log = '  Failed A.B.C.Method [1 s]'
        path = '.github/workflows/ci.yml'; event = 'push'; branch = 'main'; repository = 'r'; comments = @()
        status = 'completed'; logFails = $false; dispatchFails = $false; incidentState = 'open'
        incidentBody = "<!-- main-red run=100 attempt=1 -->`nsha=$('abcdef01' * 5) attempt=1" } + @{}
    foreach ($k in $Over.Keys) { $script:Scenario[$k] = $Over[$k] }
    $script:Calls = @()
    $summary = [IO.Path]::GetTempFileName()
    $previous = $env:GITHUB_STEP_SUMMARY
    try {
        $env:GITHUB_STEP_SUMMARY = $summary
        $invoke = @{ RunId = 100; Repository = 'r'; DryRun = $DryRun }
        if ($AutoRerun) { $invoke.AutoRerun = $true }
        $r = Invoke-RerunKnownFlakes @invoke
        if ($Replay) { $null = Invoke-RerunKnownFlakes @invoke }
        $evidence = Get-Content -LiteralPath $summary -Raw
    } finally {
        $env:GITHUB_STEP_SUMMARY = $previous
        Remove-Item -LiteralPath $summary
    }
    [pscustomobject]@{ Result = $r; Summary = [string]$evidence; Calls = @($script:Calls)
        Requests = @($script:Calls | Where-Object { $_ -like 'run rerun*' })
        Reran = @($script:Calls | Where-Object { $_ -like 'run rerun*' }).Count; Comments = @($script:Calls | Where-Object { $_ -like 'issue comment*' }).Count }
}

$x = Run-Scenario @{}
Assert ($x.Result.Rerun -and $x.Reran -eq 1 -and $x.Comments -eq 1) 'a known flake is re-run once and logged on its issue'
$x = Run-Scenario @{ log = "2026-11-09T05:00:00Z   `e[31mFailed `e[1mA.B.C.Method`e[0m [1 s]" }
Assert ($x.Result.Rerun -and $x.Reran -eq 1 -and $x.Comments -eq 1) 'a known flake with ANSI escapes is read, re-run once and logged'
$x = Run-Scenario @{ log = "  Failed A.B.C.Method [1 s]`n  `e[31mFailed New.Real.Failure`e[0m [1 s]" }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0 -and $x.Comments -eq 0) 'an ANSI-escaped real failure beside a known flake stays red'
foreach ($escape in @('[38:5:196m', '[?25l')) {
    $x = Run-Scenario @{ log = "  Failed A.B.C.Method [1 s]`n  `e${escape}Failed New.Real.Failure`e[0m [1 s]" }
    Assert (-not $x.Result.Rerun -and $x.Reran -eq 0 -and $x.Comments -eq 0) "a remaining ESC$escape cannot hide an unfiled failure beside a known flake"
}
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
$x = Run-Scenario @{ jobConclusion = 'timed_out'; labels = @('ubuntu-latest') }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a Linux timeout stays red'

$x = Run-Scenario @{ jobName = 'product (ubuntu-latest)'; labels = @('ubuntu-latest') }
Assert ($x.Result.Rerun -and $x.Reran -eq 1 -and $x.Comments -eq 1) 'a recognized Linux flake keeps its existing retry and issue receipt'
foreach ($runner in 'windows-latest', 'windows-11-arm') {
    $x = Run-Scenario @{ jobConclusion = 'timed_out'; labels = @($runner); log = '' }
    Assert ($x.Result.Rerun -and $x.Reran -eq 1 -and $x.Comments -eq 0) "$runner timeout gets one rerun without inventing a flake issue"
    Assert ($x.Requests.Count -eq 1 -and $x.Requests[0] -eq 'run rerun 100 --repo r --failed') "$runner reruns failed jobs only, not the whole workflow"
    Assert ($x.Summary -match 'run 100' -and $x.Summary -match 'attempt=1' -and $x.Summary -match 'job=7' -and $x.Summary -match 'Windows timeout') "$runner retry records source run, attempt, job and reason in the run summary"
}
$x = Run-Scenario @{ conclusion = 'timed_out'; jobConclusion = 'timed_out'; log = '' }
Assert ($x.Result.Rerun -and $x.Reran -eq 1) 'a timeout-conclusion workflow reaches the Windows path'
$x = Run-Scenario @{ attempt = 2; jobConclusion = 'timed_out'; log = '' }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a second Windows timeout remains red'
$x = Run-Scenario @{ jobConclusion = 'timed_out'; log = '' } -DryRun
Assert ($x.Result.Rerun -and $x.Reran -eq 0 -and [string]::IsNullOrEmpty($x.Summary)) 'a timeout dry run makes no request or successful-retry record'
$linuxFlake = @{ id = 9; name = 'product (ubuntu-latest)'; labels = @('ubuntu-latest'); conclusion = 'failure' }
$x = Run-Scenario @{ jobConclusion = 'timed_out'; log = ''; extraJobs = @($linuxFlake) }
Assert ($x.Result.Rerun -and $x.Reran -eq 1 -and $x.Comments -eq 1) 'a recognized Linux flake and Windows timeout are both covered, with one rerun'
foreach ($badLog in '  Failed New.Real.Failure [1 s]', 'error CS1002: ; expected', '"failed": ["unrecognized WinUI check"]') {
    $x = Run-Scenario @{ jobConclusion = 'timed_out'; log = ''; extraJobs = @($linuxFlake); extraLog = $badLog }
    Assert (-not $x.Result.Rerun -and $x.Reran -eq 0 -and [string]::IsNullOrEmpty($x.Summary)) "an uncovered failure beside a Windows timeout refuses: $badLog"
}
$x = Run-Scenario @{ jobConclusion = 'timed_out'; extraJobs = @(@{ id = 9; name = 'cancelled'; conclusion = 'cancelled' }) }
Assert (-not $x.Result.Rerun -and $x.Reran -eq 0) 'a cancellation beside a Windows timeout refuses'
$workflow = Get-Content (Join-Path $PSScriptRoot '..\.github\workflows\rerun-known-flakes.yml') -Raw
Assert ($workflow -match "workflow_run.conclusion == 'timed_out'") 'the workflow also admits a timed-out CI conclusion'

foreach ($failureLog in '  Failed New.Real.Failure [1 s]', 'error CS1002: ; expected') {
    $x = Run-Scenario @{ log = $failureLog } -AutoRerun
    Assert ($x.Result.Rerun -and $x.Reran -eq 1) "auto mode retries an unfiled failure or build error: $failureLog"
}
$x = Run-Scenario @{ jobConclusion = 'timed_out'; labels = @('ubuntu-latest') } -AutoRerun
Assert ($x.Reran -eq 1) 'auto mode covers non-Windows timeouts too'
$x = Run-Scenario @{ logFails = $true } -AutoRerun
Assert ($x.Reran -eq 1) 'unavailable job logs cannot suppress an otherwise eligible automatic rerun'
$x = Run-Scenario @{} -AutoRerun -Replay
Assert ($x.Reran -eq 1) 'repeated first-attempt delivery cannot request a second rerun even before metadata advances'
Assert (@($x.Calls | Where-Object { $_ -like 'issue comment 1 *rerun-known-flakes: test=A.B.C.Method run=100 attempt=1 job=*' }).Count -eq 1) 'auto mode preserves the countable known-flake receipt'
$marker = "<!-- rerun-once run=100 attempt=1 sha=$('abcdef01' * 5) -->`n"
$x = Run-Scenario @{ comments = @(@{ author = @{ login = 'stranger' }; body = $marker }) } -AutoRerun
Assert ($x.Reran -eq 1) 'an untrusted comment cannot spoof a dispatch reservation'
$x = Run-Scenario @{ comments = @(@{ author = @{ login = 'github-actions[bot]' }; body = "job: $marker" }) } -AutoRerun
Assert ($x.Reran -eq 1) 'a marker embedded in a job name is not a reservation'
$s1Record = "<!-- main-red run=100 attempt=1 -->`nsha=$('abcdef01' * 5) attempt=1"
$x = Run-Scenario @{ incidentBody = 'an earlier run opened this incident'; comments = @(@{ author = @{ login = 'github-actions[bot]' }; body = $s1Record }) } -AutoRerun
Assert ($x.Reran -eq 1) 'S1 evidence on a reused incident binds the current run and SHA too'
try { $null = Run-Scenario @{ incidentBody = '<!-- main-red run=100 attempt=1 -->' } -AutoRerun; Assert $false 'an incident without the full matching SHA must refuse' }
catch { Assert (@($script:Calls | Where-Object { $_ -like 'run rerun*' }).Count -eq 0) 'an incident without the full matching SHA refuses before dispatch' }
try {
    $null = Run-Scenario @{ incidentBody = 'unrelated'; comments = @(@{ author = @{ login = 'stranger' }; body = $s1Record }) } -AutoRerun
    Assert $false 'untrusted S1 evidence must refuse'
} catch { Assert (@($script:Calls | Where-Object { $_ -like 'run rerun*' }).Count -eq 0) 'an untrusted S1 record cannot attach the rerun to an unrelated incident' }
try { $null = Run-Scenario @{ dispatchFails = $true } -AutoRerun; Assert $false 'a dispatch failure must surface' }
catch {
    Assert ($_.Exception.Message -like 'gh run rerun*failed*') 'a dispatch failure surfaces instead of reporting success'
    $before = @($script:Calls | Where-Object { $_ -like 'run rerun*' }).Count
    $null = Invoke-RerunKnownFlakes -RunId 100 -Repository r -AutoRerun
    Assert (@($script:Calls | Where-Object { $_ -like 'run rerun*' }).Count -eq $before) 'uncertain dispatch retains its reservation rather than risking a second request'
}
$x = Run-Scenario @{ attempt = 2; conclusion = 'success'; jobConclusion = 'success'; log = ''; incidentState = 'closed' } -AutoRerun -Replay
Assert ($x.Reran -eq 0) 'green attempt two never requests another rerun'
Assert (@($x.Calls | Where-Object { $_ -like 'issue edit 40 *--add-label flaky*' }).Count -eq 1) 'green attempt two labels the incident once'
Assert (@($x.Calls | Where-Object { $_ -like 'issue edit 1 *--add-label flaky*' }).Count -eq 1) 'green attempt two labels the matched flake issue'
Assert (@($x.Calls | Where-Object { $_ -like 'issue comment 40 *rerun-flake: run=100 attempt=2 passed; failed test(s)=A.B.C.Method*' }).Count -eq 1) 'green evidence uses original failed tests, even after S1 closes the incident'
Assert (@($x.Calls | Where-Object { $_ -like 'issue list*--state all*' }).Count -gt 0) 'attempt two can find the closed S1 incident'
$x = Run-Scenario @{ attempt = 2 } -AutoRerun -Replay
Assert ($x.Reran -eq 0 -and @($x.Calls | Where-Object { $_ -like 'issue comment 40 *rerun-failed: run=100 attempt=2 same test(s)=A.B.C.Method*' }).Count -eq 1) 'a repeated failure records its intersection once for mitigation, never attempt three'
$x = Run-Scenario @{ attempt = 2; log = '  Failed Different.Test.Failure [1 s]' } -AutoRerun
Assert (@($x.Calls | Where-Object { $_ -like 'issue comment 40 *same test(s)=none*' }).Count -eq 1) 'a different second failure is not misrepresented as repeatable evidence'
foreach ($over in @(
    @{ jobConclusion = 'cancelled' }, @{ attempt = 3 }, @{ branch = 'feature' }, @{ repository = 'fork/r' },
    @{ event = 'pull_request' }, @{ path = '.github/workflows/other.yml' }, @{ status = 'in_progress' },
    @{ newer = @(@{ id = 101; path = '.github/workflows/ci.yml'; created_at = '2026-11-09T05:10:00Z' }) },
    @{ newer = @(@{ id = 101; path = '.github/workflows/ci.yml'; created_at = '2026-11-09T05:00:00Z' }) }
)) {
    $x = Run-Scenario $over -AutoRerun
    Assert ($x.Reran -eq 0 -and $x.Comments -eq 0) "auto mode retains trust, cancellation, freshness and attempt guards: $($over.Keys -join ',')"
}
$x = Run-Scenario @{} -AutoRerun -DryRun
Assert ($x.Reran -eq 0 -and $x.Comments -eq 0) 'auto dry run leaves no reservation or issue writes'
if ($script:failures) { "$($script:failures) failure(s)"; exit 1 }
'rerun-known-flakes: all checks passed'
