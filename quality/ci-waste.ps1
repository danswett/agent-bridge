#Requires -Version 7.4
<#
.SYNOPSIS
  Read-only snapshot of the last 60 PR CI runs; replay with -RunsFile without network.
.DESCRIPTION
  Build tools/AgentBridge.Quality first. -SnapshotFile keeps the input for offline replay.
  Uses latest-attempt jobs, not billing data. Superseded means a newer distinct head of the
  same branch started before this attempt ended; it does not assert why GitHub cancelled it.
  Files are observed head-to-head deltas, not full PR diffs. Missing predecessors, reruns,
  diverged/truncated comparisons and ambiguous paths stay unknown, never "no long job".
#>
[CmdletBinding()]
param(
    [string]$Repo = 'danswett/agent-bridge',
    [ValidateRange(1, 100)][int]$Limit = 60,
    [string]$RunsFile,
    [string]$SnapshotFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools/AgentBridge.Quality/bin/Release/net10.0/agent-bridge-quality.dll'
if (-not (Test-Path $tool)) { throw 'Build tools/AgentBridge.Quality -c Release before measuring.' }

function Read-GitHub([string]$path) {
    $text = & gh api $path
    if ($LASTEXITCODE) { throw "GitHub read failed ($LASTEXITCODE): $path" }
    ($text -join "`n") | ConvertFrom-Json -AsHashtable
}
function Branch($run) { "$($run.head_repository.full_name):$($run.head_branch)" }
function Test-PushDelta($run, $previous, [string]$key) {
    $run.run_attempt -eq 1 -and $previous.ContainsKey($key) -and $previous[$key] -cne $run.head_sha
}
function Began($run) {
    if ($run['run_started_at']) { [datetimeoffset]$run.run_started_at } else { [datetimeoffset]$run.created_at }
}
function Long-Job([string]$name) {
    if ($name -match '^pwa-ui \(chromium(?:[ ,)]|$)') { return 'pwa-ui chromium' }
    if ($name -match '^(app(?:-installer)?) \(') { return $Matches[1] }
}

if ($RunsFile) {
    $snapshot = Get-Content $RunsFile -Raw | ConvertFrom-Json -AsHashtable
} else {
    $runs = @((Read-GitHub "repos/$Repo/actions/workflows/ci.yml/runs?event=pull_request&per_page=$Limit").workflow_runs)
    $now = [datetimeoffset]::UtcNow
    $previous = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($run in ($runs | Sort-Object { [datetimeoffset]$_.created_at })) {
        $id = $run.id
        $run.jobs = @((Read-GitHub "repos/$Repo/actions/runs/$id/jobs?filter=latest&per_page=100").jobs)
        if ($run.jobs.Count -ge 100) { throw "Run $id has potentially truncated jobs; refuse partial totals." }
        $run.files = $null
        $run.delta_base_sha = $null
        $key = Branch $run
        if (Test-PushDelta $run $previous $key) {
            $base = $previous[$key]
            $comparison = Read-GitHub "repos/$Repo/compare/$base...$($run.head_sha)?per_page=100"
            if ($comparison.status -eq 'ahead' -and @($comparison.files).Count -lt 100) {
                $run.delta_base_sha = $base
                $run.files = @($comparison.files | ForEach-Object {
                    $_.filename
                    if ($_['previous_filename']) { $_.previous_filename }
                } | Sort-Object -Unique)
            }
        }
        $previous[$key] = $run.head_sha
    }
    $snapshot = @{ captured_at = $now.ToString('O'); runs = $runs }
    if ($SnapshotFile) { $snapshot | ConvertTo-Json -Depth 30 | Set-Content $SnapshotFile }
}

$now = [datetimeoffset]$snapshot.captured_at
$runs = @($snapshot.runs)
# Legacy caches lack base provenance; reject refs with any case-colliding aliases.
$previous = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$refs = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.HashSet[string]]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($run in $runs) {
    $key = Branch $run
    if (-not $refs.ContainsKey($key)) { $refs[$key] = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
    [void]$refs[$key].Add($key)
}
foreach ($run in ($runs | Sort-Object { [datetimeoffset]$_.created_at })) {
    $key = Branch $run
    $base = if ($run.ContainsKey('delta_base_sha')) { $run.delta_base_sha } elseif ($refs[$key].Count -eq 1 -and $previous.ContainsKey($key)) { $previous[$key] }
    if (-not (Test-PushDelta $run $previous $key) -or $base -isnot [string] -or $base -cne $previous[$key] -or
        ($null -ne $run.files -and $run.files.Count -ge 100)) { $run.files = $null }
    $previous[$key] = $run.head_sha
}
$totals = @{}
foreach ($name in 'pwa-ui chromium', 'app', 'app-installer') {
    $totals[$name] = @{ job = $name; started = 0; minutes = 0.0; wasted_minutes = 0.0 }
}
$rows = @(foreach ($run in $runs) {
    $end = if ($run.status -eq 'completed') { [datetimeoffset]$run.updated_at } else { $now }
    $start = Began $run
    $key = Branch $run
    $superseded = @($runs | Where-Object {
        [string]::Equals((Branch $_), $key, [StringComparison]::Ordinal) -and $_.head_sha -cne $run.head_sha -and
        $_.run_attempt -eq 1 -and [datetimeoffset]$_.created_at -gt [datetimeoffset]$run.created_at -and
        (Began $_) -gt $start -and (Began $_) -lt $end
    }).Count -gt 0
    $cancelled = $run.conclusion -eq 'cancelled'
    $wasted = $cancelled -or $superseded
    $minutes = 0.0; $started = 0; $overFive = $false
    foreach ($job in $run.jobs) {
        if (-not $job.started_at -or $job.status -eq 'queued' -or $job.conclusion -eq 'skipped') { continue }
        if ([datetimeoffset]$job.started_at -gt $now) { continue }
        $stop = if ($job.completed_at) { [datetimeoffset]$job.completed_at } else { $now }
        if ($stop -gt $now) { $stop = $now }
        $duration = [math]::Max(0.0, ($stop - [datetimeoffset]$job.started_at).TotalMinutes)
        if ($duration -gt 5 -and $job.name -match '^(app|pwa-ui) \(') { $overFive = $true }
        $name = Long-Job $job.name
        if (-not $name) { continue }
        $started++; $minutes += $duration
        $totals[$name].started++
        $totals[$name].minutes += $duration
        if ($wasted) { $totals[$name].wasted_minutes += $duration }
    }
    $noLong = $null
    if ($null -ne $run.files -and @($run.files | Where-Object { $_ -match '[,\r\n]' }).Count -eq 0) {
        $noLong = $true
        if ($run.files.Count) {
            $json = & dotnet $tool select --repo $root --ring 1 --files ($run.files -join ',')
            if ($LASTEXITCODE) { throw "Plan replay failed for run $($run.id) ($LASTEXITCODE)." }
            $selection = ($json -join "`n") | ConvertFrom-Json -AsHashtable
            $jobs = $selection.jobs['ci.yml']
            $noLong = @($jobs.Keys | Where-Object { $_ -in 'pwa-ui', 'app', 'app-installer' }).Count -eq 0
        }
    }
    [ordered]@{
        run = $run.id; url = $run.html_url; head_sha = $run.head_sha
        cancelled = $cancelled; superseded = $superseded; wasted = $wasted
        long_jobs_started = $started; long_job_minutes = [math]::Round($minutes, 2)
        cancelled_over_five = $cancelled -and $overFive
        no_long_job_push = $noLong; files = $run.files
    }
})
$longJobs = @($totals.Values | Sort-Object job | ForEach-Object {
    $_.minutes = [math]::Round($_.minutes, 2)
    $_.wasted_minutes = [math]::Round($_.wasted_minutes, 2)
    $_
})
$noLongPushes = @($rows | Where-Object { $_.no_long_job_push -eq $true })
$plannerSha = git -C $root rev-parse HEAD
if ($LASTEXITCODE) { throw 'Reading the planner revision failed.' }
[ordered]@{
    captured_at = $snapshot.captured_at
    planner_sha = $plannerSha
    runs = $runs.Count
    cancelled = @($rows | Where-Object cancelled).Count
    superseded = @($rows | Where-Object superseded).Count
    cancelled_or_superseded = @($rows | Where-Object wasted).Count
    wasted_long_job_minutes = [math]::Round(($longJobs | Measure-Object wasted_minutes -Sum).Sum, 2)
    cancelled_runs_over_five_minutes = @($rows | Where-Object cancelled_over_five).Count
    no_long_job_pushes = $noLongPushes.Count
    no_long_job_pushes_that_started_long_jobs = @($noLongPushes | Where-Object { $_.long_jobs_started -gt 0 }).Count
    unknown_pushes = @($rows | Where-Object { $null -eq $_.no_long_job_push }).Count
    long_jobs = $longJobs
    no_long_job_push_list = $noLongPushes
    run_list = $rows
} | ConvertTo-Json -Depth 10
