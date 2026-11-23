#Requires -Version 7
<#
.SYNOPSIS
  Cancels Merge rule runs stuck past a limit, so its queue: max group can't jam (#2454 follow-up b).

.DESCRIPTION
  merge-rule.yml's runs wait in one group with queue: max, and its judge (the evaluate job) times out at 10 minutes only
  once a runner takes it, so a run waiting in the group never times out (Nov 23: five, hours old, held every later
  run). This reads merge-rule.yml's unfinished runs (queued, in_progress, waiting, pending, requested; every page up to
  5 of 100 each, failing loudly past that) and, oldest first, cancels:
  - a waiting run (queued, pending, waiting, requested, or in_progress with its judge not yet started) over
    -WaitingMaxMinutes (30) since its attempt began: created_at for a first attempt; a re-run keeps its first
    created_at, so a later attempt goes by its updated_at;
  - a judging run over -RunningMaxMinutes (15) since its judge started, read from the jobs API (one read per
    in_progress run, oldest first, at most 20): run_started_at equals created_at for a run that waited (B406);
  - but never the newest run per pull request, or per head for a run with none, unless it is stuck itself: judging
    over 45 minutes or waiting over 2 hours. A run with no pull request is keyed by its own head: a workflow_run,
    status or check_suite run judges that one head (merge-rule-scoped-v1). Only runs on main's head (schedule, main's
    own events) share one key, on purpose: each judges every open pull request (merge-rule-sweep-v1), so the newest
    stands for them all;
  - dead weight at any age, the newest too: a waiting run whose head's pull request is merged or closed, or has moved
    to a newer head. The pull request is resolved from the run's head_sha and head_branch in one GraphQL read (a run's
    pull_requests is empty once merged or closed, and shows the current head, never the run's); plain cancel only.
  A run past twice its limit, or one that refuses a plain cancel, is force-cancelled; a re-run younger than its limit
  (aged by its own attempt) is never acted on at all. A run GitHub says was never queued (the Nov 11 ghosts) is remembered in -GhostFile and never called again;
  that and an already-completed run are warnings, never retried or failed; a run older than 7 days is skipped with no
  call. Only other errors fail the sweep. The first read's x-ratelimit-remaining must say at least 150: fewer, and the
  sweep stops after one warning line (exit 0) so it never starves the merge-rule judge; missing or garbled, it stops the
  same way but the run fails, so an unreadable header is never a silent pass. Budget: at most 15 runs
  acted on and 20 jobs read a sweep; 5 lists + 1 query + 20 jobs reads + 30 posts = 56 calls usually, 76 if every
  status needs 5 pages (304 an hour at 4 sweeps, of the token's 1000). It cancels, never deletes, and never touches
  another workflow's runs. A listing it can't read stops it with an error: unread is never "nothing to do". -DryRun
  lists only what it would cancel, and why.
#>
[CmdletBinding()]
param([string]$Repository = $env:GITHUB_REPOSITORY, [ValidateRange(15, 360)][int]$WaitingMaxMinutes = 30,
    [ValidateRange(11, 360)][int]$RunningMaxMinutes = 15, [string]$GhostFile = '', [switch]$DryRun)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:GhCommand = 'gh'
$script:Workflow = '.github/workflows/merge-rule.yml'
# The judge's rendered names (merge-rule.yml's evaluate job); a test ties this to that file.
$script:JudgePattern = '^merge-rule-(scoped|sweep)-v\d+$'

# ConvertFrom-Json already reads an ISO time as a UTC DateTime; re-parsing its text would apply the local offset.
function ConvertTo-SweepTime($Value) {
    if ($Value -is [datetime]) { return [DateTimeOffset]::new($Value.ToUniversalTime()) }
    [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
}

# StrictMode: a field that's absent reads as $null, never throws.
function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object -or $Object -isnot [psobject]) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { $p.Value } else { $null }
}

# A refusal that means there is nothing left to stop: a warning, not a failure.
function Get-SweepMoot($Call) {
    $text = $Call.Output -join ' '
    if ($text -match 'has not been queued yet') { return 'GitHub says it was never queued' }
    if ($text -match 'is completed|already completed') { return 'GitHub says it already completed' }
    ''
}

function Invoke-SweepGh { $out = & $script:GhCommand @args; [pscustomobject]@{ Ok = $LASTEXITCODE -eq 0; Output = $out } }

function Invoke-MergeRuleSweep([string]$Repository, [int]$WaitingMaxMinutes, [int]$RunningMaxMinutes, [DateTimeOffset]$Now, [switch]$DryRun,
    [int]$NewestWaitingMaxMinutes = 120, [int]$NewestRunningMaxMinutes = 45, [int]$MaxActions = 15, [int]$MaxJobReads = 20,
    [int]$IgnoreOlderThanDays = 7, [int]$MinRateRemaining = 150, [int]$MaxPages = 5, [string]$GhostFile = '') {
    if ($Repository -notmatch '^[\w.-]+/[\w.-]+$') { throw 'Give owner/repository.' }
    $acted = 0; $failed = 0; $skipped = 0; $deferred = 0; $jobReads = 0; $old = 0; $known = 0; $lines = @()
    # Ghosts from earlier sweeps: run ids GitHub answered 'never queued'. Never called again.
    $ghosts = [Collections.Generic.HashSet[long]]::new()
    if ($GhostFile -and (Test-Path -LiteralPath $GhostFile -PathType Leaf)) {
        foreach ($l in Get-Content -LiteralPath $GhostFile) { if ($l -match '^\s*(\d{1,19})\s*$') { [void]$ghosts.Add([long]$Matches[1]) } }
    }
    $listed = [Collections.Generic.HashSet[long]]::new()
    # Every unfinished run first (all of them are read before anything is cancelled), so the newest run per pull
    # request, or per head when it has none, is known across statuses.
    $all = @()
    foreach ($status in 'queued', 'in_progress', 'waiting', 'pending', 'requested') {
        $seen = 0
        for ($pageNo = 1; ; $pageNo++) {
            $url = "repos/$Repository/actions/workflows/merge-rule.yml/runs?status=$status&per_page=100&page=$pageNo"
            # The very first read also returns the token's rate headers (gh api -i): the sweep never starves the judge.
            $first = $status -eq 'queued' -and $pageNo -eq 1
            $read = if ($first) { Invoke-SweepGh api -i $url } else { Invoke-SweepGh api $url }
            if (-not $read.Ok) { throw "could not list $status Merge rule runs; nothing was cancelled" }
            $body = @($read.Output)
            if ($first) {
                $blank = [Array]::FindIndex([string[]]$body, [Predicate[string]] { param($l) $l -eq '' })
                $headers = if ($blank -ge 0) { @($body[0..$blank]) } else { @() }
                $body = if ($blank -ge 0 -and $blank + 1 -lt $body.Count) { @($body[($blank + 1)..($body.Count - 1)]) } else { $body }
                $raw = @($headers | Where-Object { $_ -match '^x-ratelimit-remaining:' } | ForEach-Object { ($_ -replace '^x-ratelimit-remaining:', '').Trim() })
                $remaining = if ($raw.Count -eq 1 -and $raw[0] -match '^\d{1,9}$') { [int]$raw[0] } else { $null }
                if ($null -eq $remaining -or $remaining -lt $MinRateRemaining) {
                    $said = if ($null -eq $remaining) { 'no readable x-ratelimit-remaining header' } else { "only $remaining API calls left this hour" }
                    # A real low count is a quiet skip (exit 0); an unreadable header is a visible failure, never a silent pass (B433 P2).
                    return [pscustomobject]@{ Acted = 0; Failed = $(if ($null -eq $remaining) { 1 } else { 0 }); Lines = @("warning: $said (rate limit; under $MinRateRemaining or unknown): the sweep did nothing, so the merge-rule judge keeps them") }
                }
            }
            $page = ($body -join "`n") | ConvertFrom-Json
            $runs = @(Get-Field $page 'workflow_runs' | Where-Object { $_ })
            $total = [int](Get-Field $page 'total_count')
            $seen += $runs.Count
            foreach ($run in $runs) {
                if ((Get-Field $run 'path') -cne $script:Workflow) { continue }
                # A run that moves between statuses while the five lists are read shows up twice: the first listing wins.
                if (-not $listed.Add([long]$run.id)) { continue }
                if ($ghosts.Contains([long]$run.id)) { $known++; continue }
                # A re-run keeps its first created_at; its attempt goes by updated_at (never earlier than its start).
                $attempt = if (Get-Field $run 'run_attempt') { [int]$run.run_attempt } else { 1 }
                $since = ConvertTo-SweepTime $run.created_at
                if ($attempt -gt 1) {
                    foreach ($f in 'run_started_at', 'updated_at') { if (Get-Field $run $f) { $t = ConvertTo-SweepTime (Get-Field $run $f); if ($t -gt $since) { $since = $t } } }
                }
                # Anything older than 7 days is past every limit here and is skipped outright, with no call.
                if (($Now - $since).TotalDays -gt $IgnoreOlderThanDays) { $old++; continue }
                $prList = @(Get-Field $run 'pull_requests' | Where-Object { $_ })
                $prNo = if ($prList.Count) { [int](Get-Field $prList[0] 'number') } else { 0 }
                $key = if ($prNo) { "pr:$prNo" } else { "head:$($run.head_sha)" }
                $all += [pscustomobject]@{ Run = $run; Status = $status; Since = $since; Created = $since; Key = $key; Attempt = $attempt
                    Branch = [string](Get-Field $run 'head_branch'); Head = [string](Get-Field $run 'head_sha')
                    Running = $false; Limit = $WaitingMaxMinutes; Unread = ($status -eq 'in_progress') }
            }
            if ($runs.Count -lt 100 -or $seen -ge $total) { break }
            if ($pageNo -ge $MaxPages) { throw "more than $($MaxPages * 100) $status Merge rule runs; the listing is truncated, so nothing was cancelled" }
        }
    }
    # A running one is aged by when its judge started: its 10-minute timeout counts from there. One jobs read per
    # in_progress run, oldest first (the stuck ones are old), at most MaxJobReads; a judge not yet started is still
    # waiting. A run whose jobs aren't read is never cancelled blind, but still counts for the newest per key.
    foreach ($entry in @($all | Where-Object Unread | Sort-Object Since, { [long]$_.Run.id })) {
        if ($jobReads -ge $MaxJobReads) { $deferred++; continue }
        $jobReads++
        $jobsRead = Invoke-SweepGh api "repos/$Repository/actions/runs/$($entry.Run.id)/jobs?filter=latest&per_page=20"
        if (-not $jobsRead.Ok) { $lines += "skipped run $($entry.Run.id) (in_progress): its jobs could not be read; never cancelled blind"; continue }
        $jobs = try { @(Get-Field (($jobsRead.Output -join "`n") | ConvertFrom-Json) 'jobs') } catch { @() }
        $judge = $jobs | Where-Object { $_ -and [string](Get-Field $_ 'name') -match $script:JudgePattern } | Select-Object -First 1
        $entry.Unread = $false
        if ($judge -and (Get-Field $judge 'status') -eq 'in_progress' -and (Get-Field $judge 'started_at')) {
            $entry.Since = ConvertTo-SweepTime $judge.started_at; $entry.Running = $true; $entry.Limit = $RunningMaxMinutes
        }
    }
    # Dead weight (Quality cancelled 4 by hand on Nov 23): a waiting run whose head's pull request is merged or closed,
    # or has moved past that head. Resolved from the run's own head_sha and head_branch, never from pull_requests (empty
    # once merged or closed; shows the current head). One GraphQL read; unreadable or unknown skips only this rule.
    $dead = @{}
    # Runs on main are never asked: main's commits belong to merged pull requests. That includes a pull_request_target
    # run, whose head_branch is the base branch (main): it is skipped by this rule, which is safe (age still applies).
    $candidates = @($all | Where-Object { $_.Status -ne 'in_progress' -and $_.Head -match '^[0-9a-f]{4,40}$' -and $_.Branch -and $_.Branch -ne 'main' })
    $heads = @($candidates | ForEach-Object { $_.Head } | Sort-Object -Unique | Select-Object -First 50)
    if ($heads.Count) {
        $owner, $name = $Repository -split '/', 2
        $alias = @{}; $i = 0
        $fields = ($heads | ForEach-Object { $alias[$_] = "c$i"; $i++; "$($alias[$_]): object(oid: `"$_`") { ... on Commit { associatedPullRequests(first: 5) { nodes { number state headRefOid headRefName } } } }" }) -join ' '
        $query = Invoke-SweepGh api graphql -f "query=query { repository(owner: `"$owner`", name: `"$name`") { $fields } }"
        $repo = if ($query.Ok) { try { Get-Field (Get-Field (($query.Output -join "`n") | ConvertFrom-Json) 'data') 'repository' } catch { $null } }
        if (-not $query.Ok -or $null -eq $repo -or $repo -isnot [psobject]) { $lines += 'warning: pull requests could not be read; the dead-weight rule is skipped this sweep' }
        else {
            foreach ($entry in $candidates) {
                if (-not $alias.ContainsKey($entry.Head)) { continue }
                $nodes = @(Get-Field (Get-Field (Get-Field $repo $alias[$entry.Head]) 'associatedPullRequests') 'nodes' | Where-Object { $_ })
                # Only pull requests whose branch this run is for; a main commit's merged pull request never counts. Every
                # such pull request is weighed, in no order (B433): one OPEN at the run's own head makes it live; any
                # unreadable one makes it unknown; otherwise an open one that moved on, then a merged, then a closed one.
                $prs = @($nodes | Where-Object { [string](Get-Field $_ 'headRefName') -ceq $entry.Branch })
                if (-not $prs.Count -or @($prs | Where-Object { -not (Get-Field $_ 'state') -or -not (Get-Field $_ 'number') }).Count) { continue }
                if (@($prs | Where-Object { $_.state -ceq 'OPEN' -and [string](Get-Field $_ 'headRefOid') -ceq $entry.Head }).Count) { continue }
                $moved = @($prs | Where-Object { $_.state -ceq 'OPEN' -and [string](Get-Field $_ 'headRefOid') }) | Select-Object -First 1
                $merged = @($prs | Where-Object { $_.state -ceq 'MERGED' }) | Select-Object -First 1
                $closed = @($prs | Where-Object { $_.state -ceq 'CLOSED' }) | Select-Object -First 1
                if ($moved) { $dead[$entry.Run.id] = "head superseded (#$($moved.number))" }
                elseif ($merged) { $dead[$entry.Run.id] = "pull request merged (#$($merged.number))" }
                elseif ($closed) { $dead[$entry.Run.id] = "pull request closed (#$($closed.number))" }
            }
        }
    }
    $newest = @{}
    foreach ($group in $all | Group-Object Key) {
        $newest[$group.Name] = ($group.Group | Sort-Object Created, { [long]$_.Run.id } | Select-Object -Last 1).Run.id
    }
    $newGhosts = @()
    # Oldest first: the stuck ones are the old ones, so the budget goes to them.
    foreach ($entry in @($all | Sort-Object Since, { [long]$_.Run.id })) {
        $run = $entry.Run; $status = $entry.Status
        if ($entry.Unread) { continue }
        $age = ($Now - $entry.Since).TotalMinutes
        if ($dead.ContainsKey($run.id)) {
            # Dead weight: a plain cancel at any age, the newest too; a refusal is logged, never forced, never a failure.
            $what = "run $($run.id) ($status, $([int]$age) min, dead weight: $($dead[$run.id]))"
            if ($DryRun) { $lines += "would cancel $what"; continue }
            if ($acted + $failed + $skipped -ge $MaxActions) { $deferred++; continue }
            $call = Invoke-SweepGh api -X POST "repos/$Repository/actions/runs/$($run.id)/cancel"
            if ($call.Ok) { $acted++; $lines += "cancelled $what"; continue }
            $moot = Get-SweepMoot $call
            if ($moot -match 'never queued') { $newGhosts += [long]$run.id }
            $skipped++; $lines += "warning: $($what): $(if ($moot) { $moot } else { 'the plain cancel was refused' }); skipped"
            continue
        }
        if ($age -le $entry.Limit) { continue }
        # The newest run per pull request (or head) is kept, unless it is stuck itself: past 45 minutes running or 2 hours
        # waiting it would otherwise hold the group forever.
        $isNewest = $newest[$entry.Key] -eq $run.id
        $stuckLimit = if ($entry.Running) { $NewestRunningMaxMinutes } else { $NewestWaitingMaxMinutes }
        if ($isNewest -and $age -le $stuckLimit) { continue }
        $why = if ($isNewest) { "newest: yes, stuck over $stuckLimit min" } else { "newest: no, over $($entry.Limit) min $(if ($entry.Running) { 'running' } else { 'waiting' })" }
        $what = "run $($run.id) ($status, $([int]$age) min, $why)"
        if ($DryRun) { $lines += "would cancel $what"; continue }
        # A bounded budget: at most MaxActions runs acted on per sweep; the rest wait for the next sweep.
        if ($acted + $failed + $skipped -ge $MaxActions) { $deferred++; continue }
        # A run past twice its limit, or one that refuses a plain cancel, is force-cancelled (only runs past their limit,
        # re-runs aged by their own attempt, get here). Never queued (the Nov 11 ghosts) or already completed: a warning.
        $done = $false; $moot = ''
        if ($age -le 2 * $entry.Limit) {
            $call = Invoke-SweepGh api -X POST "repos/$Repository/actions/runs/$($run.id)/cancel"
            $done = $call.Ok; if (-not $done) { $moot = Get-SweepMoot $call }
            if ($done) { $lines += "cancelled $what" }
        }
        if (-not $done -and -not $moot) {
            $call = Invoke-SweepGh api -X POST "repos/$Repository/actions/runs/$($run.id)/force-cancel"
            $done = $call.Ok; if (-not $done) { $moot = Get-SweepMoot $call }
            if ($done) { $lines += "force-cancelled $what" }
        }
        if ($moot) {
            if ($moot -match 'never queued') { $newGhosts += [long]$run.id }
            $skipped++; $lines += "warning: $($what): $moot; skipped"; continue
        }
        if ($done) { $acted++ } else { $failed++; $lines += "FAILED to stop $what" }
    }
    # Remember the ghosts still listed, so the file stays as small as GitHub's own list of them.
    if ($GhostFile -and -not $DryRun) {
        $keep = @(@($ghosts | Where-Object { $listed.Contains($_) }) + $newGhosts | Sort-Object -Unique)
        Set-Content -LiteralPath $GhostFile -Value ($keep | ForEach-Object { [string]$_ }) -Encoding utf8NoBOM
    }
    if ($known) { $lines += "skipped $known run(s) GitHub said earlier were never queued (no call)" }
    if ($old) { $lines += "skipped $old run(s) older than $IgnoreOlderThanDays days outright (ghosts GitHub still lists)" }
    if ($deferred) { $lines += "budget: $deferred more run(s) left for the next sweep (at most $MaxActions acted on and $MaxJobReads jobs read per sweep)" }
    [pscustomobject]@{ Acted = $acted; Failed = $failed; Lines = $lines }
}

if ($MyInvocation.InvocationName -eq '.') { return }
$result = Invoke-MergeRuleSweep -Repository $Repository -WaitingMaxMinutes $WaitingMaxMinutes -RunningMaxMinutes $RunningMaxMinutes -Now ([DateTimeOffset]::UtcNow) -DryRun:$DryRun -GhostFile $GhostFile
$summary = @("### Merge rule sweep (waiting over $WaitingMaxMinutes min, judging over $RunningMaxMinutes min)") + $(if ($result.Lines) { $result.Lines | ForEach-Object { "- $_" } } else { '- nothing stuck' })
$summary | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
if ($result.Failed -gt 0) { Write-Host "::error title=merge-rule sweep::$($result.Failed) stuck Merge rule run(s) could not be stopped"; exit 1 }
