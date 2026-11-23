#Requires -Version 7
# Offline only: every GitHub request must match this stand-in.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'merge-rule-sweep.ps1') -Repository 'o/r'
$script:GhCommand = 'Fake-Gh'
$script:failures = 0
$script:checks = 0
$now = [DateTimeOffset]::new(2026, 11, 23, 12, 0, 0, [TimeSpan]::Zero)
function Assert([bool]$Ok, [string]$Name) {
    $script:checks++
    if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ }
}
function Reset-Scenario {
    # $s.runs: status -> runs; each run's age in minutes. $s.refuse: run ids whose plain cancel answers a 409 conflict;
# $s.completed: run ids GitHub says already completed (409 on either cancel); $s.ghost: never queued (409 on either).
    $script:s = @{ runs = @{}; refuse = @(); cancelled = @(); forced = @(); calls = @(); listFails = $false; forceFails = @(); ghost = @(); completed = @(); remaining = 900
        commits = @{}; prQueryFails = $false; jobs = @{}; jobsFail = @(); rateHeader = 'X-Ratelimit-Remaining: {0}' }
}
function Run([long]$Id, [double]$AgeMinutes, [string]$Path = '.github/workflows/merge-rule.yml', [int]$Pr = 1, [string]$Head = '', [double]$RunningMinutes = -1, [string]$PrHead = '', [switch]$JudgeQueued, [int]$Attempt = 1, [double]$AttemptMinutes = -1, [string]$Branch = '', [double]$UpdatedMinutes = -1) {
    # By default every run is for pull request 1, so a case's young run 99 (Keeper) is the newest one kept and the old
    # runs are cancellable; -Head with -Pr 0 makes a run with no pull request, keyed by its head.
    # As live (40 of 40 on Nov 23): run_started_at equals created_at even for a run that waited in the group. When its
    # judge (the evaluate job, rendered merge-rule-scoped-v1 or merge-rule-sweep-v1) started is only in the jobs API:
    # -RunningMinutes ago (default: the run's whole age), or still queued with -JudgeQueued.
    $created = $now.AddMinutes(-$AgeMinutes).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    $started = $now.AddMinutes(-$(if ($RunningMinutes -ge 0) { $RunningMinutes } else { $AgeMinutes })).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    $script:s.jobs[[long]$Id] = @(
        @{ id = $Id * 10; run_id = $Id; name = 'backstop'; status = 'completed'; conclusion = 'skipped'; created_at = $created; started_at = $created; completed_at = $created }
        @{ id = $Id * 10 + 1; run_id = $Id; name = 'merge-rule-scoped-v1'; status = $(if ($JudgeQueued) { 'queued' } else { 'in_progress' }); conclusion = $null
            created_at = $created; started_at = $(if ($JudgeQueued) { $created } else { $started }); completed_at = $null })
    # A re-run (-Attempt 2) keeps created_at; run_started_at (-AttemptMinutes) and updated_at (-UpdatedMinutes, default the
    # same) move. Live shape: pull_requests is empty once the pull request is merged or closed (-Pr 0), and its head.sha is
    # the pull request's CURRENT head (-PrHead), not the run's.
    $attemptStart = if ($AttemptMinutes -ge 0) { $now.AddMinutes(-$AttemptMinutes).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") } else { $created }
    $updated = if ($UpdatedMinutes -ge 0) { $now.AddMinutes(-$UpdatedMinutes).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") } else { $attemptStart }
    $sha = if ($Head) { $Head } else { "head$Id" }
    @{ id = $Id; path = $Path; created_at = $created; run_started_at = $attemptStart; updated_at = $updated; run_attempt = $Attempt
        head_sha = $sha; head_branch = $(if ($Branch) { $Branch } else { "br$Id" })
        pull_requests = @(if ($Pr) { @{ number = $Pr; head = @{ sha = $(if ($PrHead) { $PrHead } else { $sha }); ref = $(if ($Branch) { $Branch } else { "br$Id" }) } } })
        html_url = "https://github.com/o/r/actions/runs/$Id" }
}
function Keeper { Run 99 1 }
function Fake-Gh {
    $a = $args -join ' '
    $s.calls += $a
    switch -Regex ($a) {
        '^api (-i )?repos/o/r/actions/workflows/merge-rule\.yml/runs\?status=(\w+)&per_page=100&page=(\d+)$' {
            if ($s.listFails) { $global:LASTEXITCODE = 1; return }
            $every = @($s.runs[$Matches[2]] | Where-Object { $_ })
            $runs = @($every | Select-Object -Skip (([int]$Matches[3] - 1) * 100) -First 100)
            $global:LASTEXITCODE = 0
            # gh api -i prints the status line and headers, a blank line, then the body, as the real one does.
            if ($Matches[1]) {
                'HTTP/2.0 200 OK'; 'Content-Type: application/json; charset=utf-8'; 'X-Ratelimit-Limit: 1000'
                if ($null -ne $s.rateHeader) { $s.rateHeader -f $s.remaining }
                'X-Ratelimit-Resource: core'; ''
            }
            @{ total_count = $every.Count; workflow_runs = $runs } | ConvertTo-Json -Depth 6; return
        }
        '^api -X POST repos/o/r/actions/runs/(\d+)/cancel$' {
            if ([long]$Matches[1] -in $s.ghost) { $global:LASTEXITCODE = 1; 'gh: Cannot cancel a workflow run that has not been queued yet. (HTTP 409)'; return }
            if ([long]$Matches[1] -in $s.completed) { $global:LASTEXITCODE = 1; 'gh: Cannot cancel a workflow run that is completed. (HTTP 409)'; return }
            if ([long]$Matches[1] -in $s.refuse) { $global:LASTEXITCODE = 1; 'gh: Conflict (HTTP 409)'; return }
            $global:LASTEXITCODE = 0; $s.cancelled += [long]$Matches[1]; return
        }
        '^api -X POST repos/o/r/actions/runs/(\d+)/force-cancel$' {
            if ([long]$Matches[1] -in $s.ghost) { $global:LASTEXITCODE = 1; 'gh: Cannot cancel a workflow run that has not been queued yet. (HTTP 409)'; return }
            if ([long]$Matches[1] -in $s.completed) { $global:LASTEXITCODE = 1; 'gh: Cannot cancel a workflow run that is completed. (HTTP 409)'; return }
            if ([long]$Matches[1] -in $s.forceFails) { $global:LASTEXITCODE = 1; 'gh: HTTP 500'; return }
            $global:LASTEXITCODE = 0; $s.forced += [long]$Matches[1]; return
        }
        '^api repos/o/r/actions/runs/(\d+)/jobs\?filter=latest&per_page=20$' {
            # The real shape: { total_count, jobs: [ { id, run_id, name, status, conclusion, created_at, started_at, ... } ] }.
            if ([long]$Matches[1] -in $s.jobsFail) { $global:LASTEXITCODE = 1; 'gh: HTTP 502'; return }
            $jobs = @($s.jobs[[long]$Matches[1]] | Where-Object { $_ })
            $global:LASTEXITCODE = 0
            @{ total_count = $jobs.Count; jobs = $jobs } | ConvertTo-Json -Depth 5; return
        }
        '^api graphql -f query=' {
            # One query for every waiting run's head commit and its associated pull requests, as GitHub answers it: an
            # unknown commit is null. $s.commits: sha -> nodes, 'null' (answered null) or 'omit' (left out of the answer).
            if ($s.prQueryFails) { $global:LASTEXITCODE = 1; return }
            $repo = [ordered]@{}
            foreach ($m in [regex]::Matches($a, '(c\d+): object\(oid: "(\w+)"\) \{ \.\.\. on Commit \{ associatedPullRequests\(first: 5\) \{ nodes \{ number state headRefOid headRefName \} \} \} \}')) {
                $c = $s.commits[$m.Groups[2].Value]
                if ($c -eq 'omit') { continue }
                $repo[$m.Groups[1].Value] = if ($null -eq $c -or $c -eq 'null') { $null } else { @{ associatedPullRequests = @{ nodes = @($c) } } }
            }
            $global:LASTEXITCODE = 0
            @{ data = @{ repository = $repo } } | ConvertTo-Json -Depth 8; return
        }
        default { throw "Unexpected fake gh request: $a" }
    }
}
function Sweep([switch]$DryRun, [int]$MaxPages = 5, [string]$GhostFile = '') { Invoke-MergeRuleSweep -Repository 'o/r' -WaitingMaxMinutes 30 -RunningMaxMinutes 15 -Now $now -DryRun:$DryRun -MaxPages $MaxPages -GhostFile $GhostFile }
function PR([int]$Number, [string]$State, [string]$HeadOid, [string]$Branch) { @{ number = $Number; state = $State; headRefOid = $HeadOid; headRefName = $Branch } }

Reset-Scenario
$s.runs = @{ in_progress = @((Run 1 40), (Run 2 200)); queued = @((Run 3 5), (Run 4 45)); pending = @((Run 5 31)) }
$out = Sweep
Assert ((($s.cancelled | Sort-Object) -join ',') -eq '4,5') 'a plain cancel for queued and pending runs past 30 minutes'
Assert ((($s.forced | Sort-Object) -join ',') -eq '1,2') 'in_progress runs past twice their 15-minute limit (zombies) are force-cancelled at once'
Assert ($s.cancelled -notcontains 3 -and $s.forced -notcontains 3) 'a run younger than the limit is left alone'
Assert ($out.Failed -eq 0 -and $out.Acted -eq 4) 'the sweep reports what it did'

Reset-Scenario
$s.runs = @{ in_progress = @((Run 7 50), (Keeper)) }; $s.refuse = @(7)
$out = Sweep
Assert (($s.forced -join ',') -eq '7' -and $out.Failed -eq 0) 'a run that refuses a plain cancel is force-cancelled'

Reset-Scenario
$s.runs = @{ in_progress = @((Run 8 50), (Keeper)) }; $s.refuse = @(8); $s.forceFails = @(8)
$out = Sweep
Assert ($out.Failed -eq 1) 'a run neither cancel can stop is counted as a failure, not hidden'

Reset-Scenario
$s.runs = @{ in_progress = @((Run 9 50), (Run 10 50 '.github/workflows/ci.yml'), (Keeper)) }
$null = Sweep
Assert ((($s.cancelled + $s.forced) -join ',') -eq '9') 'a run of any other workflow is never touched, even if the API returned it'

Reset-Scenario
$s.runs = @{ in_progress = @((Run 11 300)); queued = @((Run 12 60), (Keeper)) }
$null = Sweep -DryRun
Assert ($s.cancelled.Count -eq 0 -and $s.forced.Count -eq 0) 'a dry run cancels nothing'

Reset-Scenario
$s.listFails = $true; $failed = $null
try { $null = Sweep } catch { $failed = $_.Exception.Message }
Assert ($failed -match 'could not list') 'a listing that fails stops the sweep loudly, never reads as nothing to do'

Reset-Scenario
$null = Sweep
Assert (@($s.calls | Where-Object { $_ -match '/runs\?status=' }).Count -eq 5 -and @($s.calls | Where-Object { $_ -match 'POST' }).Count -eq 0) 'one bounded read per unfinished status, and nothing to cancel writes nothing'

# R8b: the 'zombies' were waiting in the queue: max group, not running; age decides whatever the status, but the newest
# run for each pull request (or, with none, each head) is never cancelled, however old.
Reset-Scenario
$s.runs = @{ queued = @((Run 20 90 -Pr 7), (Run 21 60 -Pr 7)); in_progress = @((Run 22 120 -Pr 7)) }
$null = Sweep
Assert ((($s.cancelled + $s.forced | Sort-Object) -join ',') -eq '20,22') 'of three stale runs for one pull request, every one but the newest is cancelled, whatever their status'
Reset-Scenario
$s.runs = @{ pending = @((Run 23 100 -Pr 8)); in_progress = @((Run 27 40 -Pr 9)) }
$null = Sweep
Assert ($s.cancelled.Count -eq 0 -and $s.forced.Count -eq 0) 'the newest run for a pull request is kept while it waits under 2 h or runs under 45 min'
Reset-Scenario
$s.runs = @{ queued = @((Run 24 50 -Pr 0 -Head 'abc'), (Run 25 40 -Pr 0 -Head 'abc'), (Run 26 45 -Pr 0 -Head 'def')) }
$null = Sweep
Assert ((($s.cancelled | Sort-Object) -join ',') -eq '24') 'with no pull request, the newest run per head is kept'

# Quality's spec: queued/pending past 30 minutes, in_progress past 15 minutes of running (the judge times out at 10).
Reset-Scenario
$s.runs = @{ in_progress = @((Run 30 20), (Run 31 40 -RunningMinutes 10), (Keeper)); queued = @((Run 32 20)) }
$null = Sweep
Assert ((($s.cancelled + $s.forced | Sort-Object) -join ',') -eq '30') 'in_progress past 15 minutes of running is cancelled; one that waited, then started 10 minutes ago, is not; queued under 30 minutes is not'
Reset-Scenario
$s.runs = @{ in_progress = @((Run 33 35), (Keeper)) }
$null = Sweep
Assert (($s.forced -join ',') -eq '33') 'an in_progress run past twice its limit (30 minutes running) is force-cancelled'

# Quality: a stuck newest run must not hold the group forever: past 45 min running or 2 h waiting, even it goes.
Reset-Scenario
$s.runs = @{ in_progress = @((Run 40 50 -Pr 11)); pending = @((Run 41 130 -Pr 12)) }
$out = Sweep
Assert ((($s.cancelled + $s.forced | Sort-Object) -join ',') -eq '40,41' -and @($out.Lines | Where-Object { $_ -match 'newest' }).Count -eq 2) 'even the newest run goes past 45 min running or 2 h waiting, and says it was the newest'

# The Nov 11 ghosts: 409 'has not been queued yet' is logged and skipped, never retried or forced, never a failure.
Reset-Scenario
$s.runs = @{ queued = @((Run 42 45), (Keeper)) }; $s.ghost = @(42)
$out = Sweep
Assert (@($s.calls | Where-Object { $_ -match '/runs/42/' }).Count -eq 1 -and $out.Failed -eq 0 -and @($out.Lines | Where-Object { $_ -match 'run 42 .*never queued' }).Count -eq 1) 'a ghost run is logged and skipped after one call, and fails nothing'

# The dry run lists only what it would cancel, and why: id, age, newest or not.
Reset-Scenario
$s.runs = @{ queued = @((Run 43 45), (Keeper), (Run 44 10 -Pr 13)) }
$out = Sweep -DryRun
Assert (@($out.Lines).Count -eq 1 -and $out.Lines[0] -match '^would cancel run 43 \(queued, 45 min, newest: no, over 30 min waiting\)') 'a dry run lists only what it would cancel, with id, age, newest and why'

# A bounded budget: at most 15 runs acted on and 20 jobs read a sweep: 5 lists + 1 PR query + 20 jobs reads + 2 x 15
# posts = 56 calls a sweep at most, 224 an hour at 4 sweeps, of the token's 1000.
Reset-Scenario
$s.runs = @{ queued = @(@(1..25 | ForEach-Object { Run (100 + $_) 45 }) + @(Keeper)) }
$out = Sweep
Assert ($s.cancelled.Count -eq 15 -and @($s.calls).Count -le 56 -and @($out.Lines | Where-Object { $_ -match 'budget' }).Count -eq 1) 'a sweep acts on at most 15 runs and says the rest wait for the next sweep'

# Dead weight (Quality did 4 by hand on Nov 23), live shape (B421): the pull request is resolved from the run's head_sha
# and head_branch; a merged or closed pull request's runs have EMPTY pull_requests, and an open one's shows its CURRENT
# head. A waiting run for a merged or closed pull request, or for a head it has moved past, is plainly cancelled at any age.
Reset-Scenario
$s.commits = @{ 'a050' = (PR 21 'MERGED' 'a050' 'b50'); 'a051' = (PR 22 'OPEN' 'a0ff' 'b51'); 'a052' = (PR 23 'OPEN' 'a052' 'b52'); 'a053' = (PR 21 'MERGED' 'a053' 'b53') }
$s.runs = @{ pending = @((Run 50 2 -Pr 0 -Head 'a050' -Branch 'b50'), (Run 51 3 -Pr 22 -Head 'a051' -Branch 'b51' -PrHead 'a0ff'), (Run 52 1 -Pr 23 -Head 'a052' -Branch 'b52')); in_progress = @((Run 53 5 -Pr 0 -Head 'a053' -Branch 'b53')) }
$out = Sweep
Assert ((($s.cancelled | Sort-Object) -join ',') -eq '50,51' -and $s.forced.Count -eq 0) 'young waiting runs for a merged PR (empty pull_requests) or a superseded head are plainly cancelled; the current head, and a running one, are not'
Assert (@($s.calls | Where-Object { $_ -match '^api graphql' }).Count -eq 1 -and @($out.Lines | Where-Object { $_ -match 'pull request merged \(#21\)|head superseded \(#22\)' }).Count -eq 2) 'one query resolves every head, and the log says why each was dead weight'
Reset-Scenario
$s.commits = @{ 'a054' = (PR 24 'CLOSED' 'a054' 'b54') }; $s.runs = @{ queued = @((Run 54 2 -Pr 0 -Head 'a054' -Branch 'b54')) }; $s.refuse = @(54)
$out = Sweep
Assert ($s.forced.Count -eq 0 -and $out.Failed -eq 0) 'a dead-weight run that refuses a plain cancel is logged, never forced, never a failure'
Reset-Scenario
$s.prQueryFails = $true; $s.commits = @{ 'a055' = (PR 25 'MERGED' 'a055' 'b55') }; $s.runs = @{ queued = @((Run 55 2 -Pr 0 -Head 'a055' -Branch 'b55')) }
$out = Sweep
Assert ($s.cancelled.Count -eq 0 -and $out.Failed -eq 0 -and @($out.Lines | Where-Object { $_ -match 'pull requests could not be read' }).Count -eq 1) 'an unreadable PR query skips only the dead-weight rule, and says so'
Reset-Scenario
# A main commit's merged pull request is never this run's: main's runs aren't even asked about.
$s.commits = @{ 'a056' = (PR 26 'MERGED' 'a0aa' 'feature') }; $s.runs = @{ queued = @((Run 56 2 -Pr 0 -Head 'a056' -Branch 'main'), (Run 57 2 -Pr 0 -Head 'a057' -Branch 'other')) }
$s.commits['a057'] = (PR 27 'MERGED' 'a057' 'feature')
$null = Sweep
Assert ($s.cancelled.Count -eq 0 -and @($s.calls | Where-Object { $_ -match 'a056' }).Count -eq 0) 'a run on main, or one whose commit belongs to another branch''s pull request, is never dead weight'

# Schedule pin: GitHub drops schedules on :00/:15/:30/:45 under load, so the sweep runs at off-minutes, every 15 minutes.
$yml = Get-Content -Raw (Join-Path $PSScriptRoot '..' '.github' 'workflows' 'merge-rule-sweep.yml')
$crons = @([regex]::Matches($yml, "(?m)^\s*-\s*cron:\s*'([^']+)'") | ForEach-Object { $_.Groups[1].Value })
$minutes = @(if ($crons.Count -eq 1) { ($crons[0] -split ' ')[0] -split ',' | ForEach-Object { [int]$_ } })
Assert ($crons.Count -eq 1 -and $crons[0] -eq '7,22,37,52 * * * *' -and -not @($minutes | Where-Object { $_ % 15 -eq 0 }).Count) 'the sweep is scheduled at off-minutes 7,22,37,52, never on a quarter hour'

# B406 (P1): a run that waited in the group has run_started_at == created_at, so its running age comes from its judge's
# own started_at, read from the jobs API (one call per in_progress run).
Reset-Scenario
$s.runs = @{ in_progress = @((Run 60 25 -RunningMinutes 5), (Run 61 20 -JudgeQueued), (Run 62 35 -JudgeQueued), (Keeper)) }
$out = Sweep
Assert ((($s.cancelled + $s.forced | Sort-Object) -join ',') -eq '62') 'a judge that started 5 min ago after 20 min waiting is kept; a queued judge goes only past 30 min waiting'
Assert (@($s.calls | Where-Object { $_ -match '/runs/\d+/jobs\?' }).Count -eq 4) 'one jobs read per in_progress run'
Reset-Scenario
$s.runs = @{ in_progress = @((Run 63 50), (Keeper)) }; $s.jobsFail = @(63)
$out = Sweep
Assert ($s.cancelled.Count -eq 0 -and $s.forced.Count -eq 0 -and $out.Failed -eq 0 -and @($out.Lines | Where-Object { $_ -match 'run 63.*jobs could not be read' }).Count -eq 1) 'a run whose jobs cannot be read is skipped and logged, never cancelled blind'

# P2: an already-completed run is a warning, never forced, never a failure; a run older than 7 days (the Nov 11 ghosts)
# is skipped outright, with no call at all; only other errors fail the sweep.
Reset-Scenario
$s.runs = @{ queued = @((Run 70 45), (Run 71 (8 * 1440)), (Keeper)) }; $s.completed = @(70)
$out = Sweep
Assert ($s.forced.Count -eq 0 -and $out.Failed -eq 0 -and @($s.calls | Where-Object { $_ -match '/runs/70/' }).Count -eq 1 -and @($out.Lines | Where-Object { $_ -match 'warning: run 70 .*already completed' }).Count -eq 1) 'an already-completed run is one call and a warning, not a failure'
Assert (@($s.calls | Where-Object { $_ -match '/runs/71/' }).Count -eq 0 -and @($out.Lines | Where-Object { $_ -match 'older than 7 days' }).Count -eq 1) 'a run older than 7 days is skipped outright, with no call'

# The workflow itself (B406 P2.4): nothing a fork can start, the least token, its own group, main's own script.
$norm = $yml -replace "`r`n", "`n"
$onBlock = [regex]::Match($norm, '(?ms)^on:\n(.*?)^\S').Groups[1].Value
$triggers = @([regex]::Matches($onBlock, '(?m)^  (\w+):') | ForEach-Object { $_.Groups[1].Value })
Assert (($triggers -join ',') -eq 'schedule,workflow_dispatch') 'only schedule and workflow_dispatch start the sweep'
$jobPerms = [regex]::Match($norm, '(?ms)^    permissions:\n((?:      [^\n]*\n)+)').Groups[1].Value
$granted = @([regex]::Matches($jobPerms, '(?m)^      ([\w-]+): (\w+)') | ForEach-Object { "$($_.Groups[1].Value)=$($_.Groups[2].Value)" })
Assert ($norm -match '(?m)^permissions: \{\}$' -and ($granted -join ',') -eq 'actions=write,contents=read,pull-requests=read') 'permissions: {} at the top; the job gets actions: write, contents: read, pull-requests: read only'
Assert ($norm -match '(?m)^  group: merge-rule-sweep\n  cancel-in-progress: false$' -and $norm -match '(?m)^          ref: main$' -and $norm -match '(?m)^          persist-credentials: false$' -and $norm -notmatch 'secrets\.') 'its own group, never cancelling a sweep in flight; main''s own checkout, no credentials kept, no secrets'

# Never starve the merge-rule judge: under 150 calls left this hour (x-ratelimit-remaining on the sweep's first call),
# the sweep does nothing but say so.
Reset-Scenario
$s.runs = @{ queued = @((Run 80 45), (Keeper)); in_progress = @((Run 81 50)) }; $s.remaining = 149
$out = Sweep
Assert (@($s.calls).Count -eq 1 -and $s.calls[0] -match '^api -i ' -and $s.cancelled.Count -eq 0 -and $s.forced.Count -eq 0 -and $out.Failed -eq 0 -and @($out.Lines).Count -eq 1 -and $out.Lines[0] -match '^warning: .*149 .*rate limit') 'under 150 calls left this hour, one read and one warning line, nothing else'
Reset-Scenario
$s.runs = @{ queued = @((Run 82 45), (Keeper)) }; $s.remaining = 150
$null = Sweep
Assert (($s.cancelled -join ',') -eq '82') 'at 150 left, the sweep goes on'

# B412 (P1): under StrictMode a commit missing from the GraphQL answer, or null, or a pull request without its fields, must
# not kill the sweep: its state is unknown, so that rule doesn't cancel it; every other rule and run still goes.
Reset-Scenario
$s.commits = @{ 'a090' = (PR 30 'MERGED' 'a090' 'br90'); 'a091' = 'omit'; 'a092' = 'null'; 'a093' = @{ headRefOid = 'a093'; headRefName = 'br93' } }
$s.runs = @{ pending = @((Run 90 2 -Pr 0 -Head 'a090'), (Run 91 2 -Pr 31 -Head 'a091'), (Run 92 2 -Pr 32 -Head 'a092'), (Run 93 2 -Pr 33 -Head 'a093')); queued = @((Run 94 45 -Pr 31), (Run 95 1 -Pr 31)) }
$out = Sweep
Assert ((($s.cancelled | Sort-Object) -join ',') -eq '90,94' -and $out.Failed -eq 0) 'a commit omitted from the answer, null, or a pull request without a state is unknown: not cancelled as dead weight, and the sweep goes on'

# B412 P3.1: jobs are read oldest first, so with more than 20 running the stuck (old) ones are never starved.
Reset-Scenario
$s.runs = @{ in_progress = @(@(1..21 | ForEach-Object { Run (300 + $_) (1 + $_ / 10) -Pr (100 + $_) }) + @(Run 399 200 -Pr 99 -RunningMinutes 50)) }
$out = Sweep
Assert ((($s.cancelled + $s.forced) -join ',') -eq '399' -and @($s.calls | Where-Object { $_ -match '/runs/\d+/jobs\?' }).Count -eq 20 -and @($s.calls | Where-Object { $_ -match '/runs/399/jobs\?' }).Count -eq 1) 'with 22 running, the 20 jobs reads go to the oldest first, and the stuck one is cancelled'

# B412 P3.3: a re-run keeps its first created_at; it is aged from its own attempt's start.
Reset-Scenario
$s.runs = @{ queued = @((Run 400 (3 * 1440) -Attempt 2 -AttemptMinutes 5), (Run 401 (3 * 1440) -Attempt 2 -AttemptMinutes 45), (Keeper)) }
$out = Sweep
Assert (($s.cancelled -join ',') -eq '401' -and $s.forced.Count -eq 0) 'a re-run started 5 min ago is kept though created 3 days ago; one waiting 45 min is plainly cancelled'

# B421 (3): a missing or garbled x-ratelimit-remaining header is a warning and a stop, as is too few left.
foreach ($h in @($null, 'X-Ratelimit-Remaining: lots', 'X-Ratelimit-Remaining: ')) {
    Reset-Scenario
    $s.runs = @{ queued = @((Run 83 45), (Keeper)) }; $s.rateHeader = $h
    $out = Sweep
    Assert (@($s.calls).Count -eq 1 -and $s.cancelled.Count -eq 0 -and $out.Failed -eq 1 -and @($out.Lines).Count -eq 1 -and $out.Lines[0] -match '^warning: no readable x-ratelimit-remaining') "a rate header that is $(if ($null -eq $h) { 'missing' } else { "'$h'" }) stops the sweep after one warning, and fails the run"
}

# B421 (2): oldest first, and every page: the stuck ones are the old ones, and a flood never hides them.
Reset-Scenario
# Listed as GitHub lists them, newest first.
$s.runs = @{ queued = @(@(Keeper) + @(1..20 | ForEach-Object { Run (500 + $_) (30 + $_) })) }
$out = Sweep
Assert ((($s.cancelled | Sort-Object) -join ',') -eq ((506..520) -join ',')) 'with 20 stuck and a budget of 15, the 15 oldest go first'
Reset-Scenario
$s.runs = @{ queued = @(@(1..150 | ForEach-Object { Run (1000 + $_) 2 }) + @(Run 999 200 -Pr 2)) }
$null = Sweep
Assert (@($s.calls | Where-Object { $_ -match 'status=queued&per_page=100&page=' }).Count -eq 2 -and ($s.cancelled -join ',') -eq '' -and ($s.forced -join ',') -eq '999') 'past 100 runs a second page is read, and the stuck one on it goes'
Reset-Scenario
$s.runs = @{ queued = @(1..101 | ForEach-Object { Run (2000 + $_) 2 }) }; $failed = $null
try { $null = Sweep -MaxPages 1 } catch { $failed = $_.Exception.Message }
Assert ($failed -match 'truncated' -and $s.cancelled.Count -eq 0) 'a listing longer than the page cap fails loudly, never sweeps half'

# B421 (4): a re-run keeps created_at; it is aged by updated_at, and is never force-cancelled younger than its limit.
Reset-Scenario
$s.runs = @{ queued = @((Run 410 (3 * 1440) -Attempt 2 -UpdatedMinutes 10), (Keeper)) }; $s.refuse = @(410)
$null = Sweep
Assert (@($s.calls | Where-Object { $_ -match '/runs/410/' }).Count -eq 0) 'a re-run updated 10 min ago, created 3 days ago, gets no cancel and no force'

# B421 P3: the caps hold for dead weight and for jobs reads.
Reset-Scenario
$heads = 1..20 | ForEach-Object { 'a6{0:d2}' -f $_ }
foreach ($h in $heads) { $s.commits[$h] = (PR 40 'CLOSED' $h "b$h") }
$s.runs = @{ pending = @($heads | ForEach-Object { Run (600 + [int]$_.Substring(2)) 1 -Pr 0 -Head $_ -Branch "b$_" }) }
$out = Sweep
Assert ($s.cancelled.Count -eq 15 -and @($out.Lines | Where-Object { $_ -match '^budget: 5 more' }).Count -eq 1) 'dead weight counts against the 15-run cap; the rest wait'
Reset-Scenario
$s.runs = @{ in_progress = @(1..25 | ForEach-Object { Run (700 + $_) 5 -Pr (200 + $_) }) }
$out = Sweep
Assert (@($s.calls | Where-Object { $_ -match '/jobs\?' }).Count -eq 20 -and @($out.Lines | Where-Object { $_ -match '^budget: 5 more' }).Count -eq 1) 'at most 20 jobs reads a sweep; the rest wait'

# B421 P3: a ghost is remembered per run id, never called again while GitHub lists it, and forgotten once it doesn't.
$ghostFile = Join-Path ([IO.Path]::GetTempPath()) "sweep-ghosts-$PID.txt"
Remove-Item -LiteralPath $ghostFile -ErrorAction SilentlyContinue
try {
    Reset-Scenario
    $s.runs = @{ queued = @((Run 42 45), (Keeper)) }; $s.ghost = @(42)
    $null = Sweep -GhostFile $ghostFile
    $first = @(Get-Content -LiteralPath $ghostFile)
    Reset-Scenario
    $s.runs = @{ queued = @((Run 42 60), (Keeper)) }; $s.ghost = @(42)
    $out = Sweep -GhostFile $ghostFile
    Assert (($first -join ',') -eq '42' -and @($s.calls | Where-Object { $_ -match '/runs/42/' }).Count -eq 0 -and @($out.Lines | Where-Object { $_ -match 'never queued \(no call\)' }).Count -eq 1) 'a ghost is remembered and not called again'
    Reset-Scenario
    $s.runs = @{ queued = @((Keeper)) }
    $null = Sweep -GhostFile $ghostFile
    Assert (@(Get-Content -LiteralPath $ghostFile | Where-Object { $_ }).Count -eq 0) 'a ghost GitHub no longer lists is forgotten'
} finally { Remove-Item -LiteralPath $ghostFile -ErrorAction SilentlyContinue }

# B421 P3: the judge pattern is tied to merge-rule.yml's evaluate job, whose names it must match.
$mr = (Get-Content -Raw (Join-Path $PSScriptRoot '..' '.github' 'workflows' 'merge-rule.yml')) -replace "`r`n", "`n"
$evaluate = [regex]::Match($mr, '(?ms)^  evaluate:\n(.*?)(?=^  \S|\z)').Groups[1].Value
$names = @([regex]::Matches([regex]::Match($evaluate, '(?ms)^    name: >-\n(.*?)(?=^    \S)').Groups[1].Value, "'(merge-rule-[a-z]+-v\d+)'") | ForEach-Object { $_.Groups[1].Value })
Assert ($names.Count -ge 2 -and -not @($names | Where-Object { $_ -notmatch $script:JudgePattern }).Count -and 'backstop' -notmatch $script:JudgePattern) "the judge pattern matches every name merge-rule.yml's evaluate job renders ($($names -join ', ')), and not the backstop"

# The ghost cache: the same pinned actions/cache as the rest of the repository, keyed to this workflow only.
Assert (@([regex]::Matches($norm, 'uses: actions/cache/(restore|save)@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6\.1\.0')).Count -eq 2 -and
    $norm -match '(?m)^          key: merge-rule-sweep-ghosts-\n          restore-keys: merge-rule-sweep-ghosts-$' -and
    $norm -match "(?m)^          key: merge-rule-sweep-ghosts-\$\{\{ hashFiles\('\.sweep/ghosts\.txt'\) \}\}$" -and $norm -notmatch 'run_id') 'the ghost list is cached with the pinned actions/cache: restored by prefix, saved under a key of its contents, never one per run'

# B433 (P1): two pull requests on one branch at the same head, #1 closed and #2 open: the run is live for #2, in any
# order GitHub lists them. Only when no open one is at the run's head does a closed, merged or moved-on one make it dead.
foreach ($order in @('closed-first', 'open-first')) {
    Reset-Scenario
    $closed = PR 1 'CLOSED' 'a0e0' 'shared'; $open = PR 2 'OPEN' 'a0e0' 'shared'
    $s.commits = @{ 'a0e0' = $(if ($order -eq 'closed-first') { @($closed, $open) } else { @($open, $closed) }); 'a0e1' = @((PR 3 'CLOSED' 'a0e1' 'two'), (PR 4 'OPEN' 'a0ff' 'two')) }
    $s.runs = @{ pending = @((Run 800 2 -Pr 2 -Head 'a0e0' -Branch 'shared'), (Run 801 2 -Pr 4 -Head 'a0e1' -Branch 'two' -PrHead 'a0ff')) }
    $out = Sweep
    Assert (($s.cancelled -join ',') -eq '801' -and @($out.Lines | Where-Object { $_ -match 'run 801 .*head superseded \(#4\)' }).Count -eq 1) "a run live for an open pull request is kept though a closed one shares its branch and head ($order); one whose open pull request moved on goes"
}

# B433 P3: a run that moves between statuses while the five lists are read appears twice; it is acted on once.
Reset-Scenario
$twice = Run 810 45
$s.runs = @{ queued = @($twice, (Keeper)); pending = @($twice) }
$null = Sweep
Assert (@($s.calls | Where-Object { $_ -match '/runs/810/' }).Count -eq 1 -and ($s.cancelled -join ',') -eq '810') 'a run listed under two statuses is acted on once'

if ($script:failures) { "$script:failures of $script:checks checks failed"; exit 1 }
"merge-rule-sweep: $script:checks checks passed"
