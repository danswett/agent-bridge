<#
.SYNOPSIS
Reports observed CI waste from an offline GitHub workflow_runs snapshot; never calls GitHub.
.DESCRIPTION
Usage: pwsh tools/measure-ci-waste.ps1 -RunsFile snapshot.json
Optional jobs must be a complete job list (name,status,conclusion,started_at).
Optional plan must contain changed_files and long_jobs arrays from the existing plan.
Missing evidence is UNKNOWN, not zero. Supersession uses updated_at as a finish
proxy, within this snapshot only; failed-then-replaced is a separate signal.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunsFile, [ValidateRange(1, 60)][int]$Limit = 60)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$snapshot = Get-Content -LiteralPath $RunsFile -Raw | ConvertFrom-Json -AsHashtable
if ($null -eq $snapshot -or $snapshot['workflow_runs'] -isnot [array]) {
    throw 'Expected an object with a workflow_runs array.'
}
$runs = @($snapshot['workflow_runs'] | Where-Object { $_['event'] -eq 'pull_request' } |
    Sort-Object { [DateTimeOffset]$_['created_at'] } -Descending | Select-Object -First $Limit)
foreach ($run in $runs) {
    foreach ($field in 'id', 'run_attempt', 'head_sha', 'created_at', 'status') {
        if ([string]::IsNullOrWhiteSpace([string]$run[$field])) { throw "Run is missing $field." }
    }
    $run['_created'] = [DateTimeOffset]$run['created_at']
    $run['_finish'] = if ($run['status'] -eq 'completed') {
        [DateTimeOffset]$run['updated_at']
    } else { [DateTimeOffset]::MaxValue }
    $run['_prs'] = @(foreach ($pr in $run['pull_requests']) {
        $number = if ($pr -is [System.Collections.IDictionary]) { $pr['number'] } else { $pr }
        if ([int]$number -lt 1) { throw 'PR numbers must be positive.' }
        [int]$number
    })
}
$rows = @(foreach ($run in $runs) {
    $prs = $run['_prs']
    $later = @($runs | Where-Object {
        $_['_created'] -gt $run['_created'] -and $_['head_sha'] -ne $run['head_sha'] -and
        @($_['_prs'] | Where-Object { $prs -contains $_ }).Count -gt 0
    })
    $superseded = @($later | Where-Object { $_['_created'] -lt $run['_finish'] }).Count -gt 0
    $cancelled = $run['conclusion'] -eq 'cancelled'
    $jobsKnown = $null -ne $run['jobs']
    if ($jobsKnown -and $run['jobs'] -isnot [array]) { throw 'jobs must be a complete array or null.' }
    $longJobs = @(foreach ($job in $run['jobs']) {
        foreach ($field in 'name', 'status', 'conclusion', 'started_at') {
            if (-not $job.Contains($field)) { throw "Job evidence is missing $field." }
        }
        if ($job['name'] -match '^(pwa-ui \(chromium\)|app(?:-installer)?(?: \([^)]+\))?)$' -and
            $job['status'] -in @('in_progress', 'completed') -and $job['conclusion'] -ne 'skipped' -and
            -not [string]::IsNullOrWhiteSpace([string]$job['started_at'])) {
            [void][DateTimeOffset]::Parse($job['started_at'])
            $job['name']
        }
    })
    $plan = $run['plan']
    $planKnown = $null -ne $plan -and $null -ne $plan['changed_files'] -and $null -ne $plan['long_jobs']
    if ($planKnown -and ($plan['changed_files'] -isnot [array] -or $plan['long_jobs'] -isnot [array])) {
        throw 'plan.changed_files and plan.long_jobs must be arrays or absent.'
    }
    [pscustomobject]@{
        id = $run['id']; attempt = $run['run_attempt']; headSha = $run['head_sha']; prs = $prs
        cancelled = $cancelled; supersededObserved = $superseded
        failedThenReplacedObserved = $run['conclusion'] -eq 'failure' -and $later.Count -gt 0
        wastedObserved = $cancelled -or $superseded
        jobsKnown = $jobsKnown; longJobs = $longJobs
        planKnown = $planKnown; noLongJob = $planKnown -and $plan['long_jobs'].Count -eq 0
        changedFiles = if ($planKnown) { ,@($plan['changed_files']) } else { $null }
        pushKey = "$(($prs | Sort-Object) -join ',')/$($run['head_sha'])"
    }
})
$unknownPr = @($rows | Where-Object { $_.prs.Count -eq 0 }).Count
$unknownJobs = @($rows | Where-Object { -not $_.jobsKnown }).Count
$unknownPlan = @($rows | Where-Object { -not $_.planKnown }).Count
$burns = @(foreach ($row in $rows) {
    if ($row.wastedObserved) {
        foreach ($name in $row.longJobs) { @{ runId = $row.id; attempt = $row.attempt; name = $name } }
    }
})
$noLongPushes = @($rows | Where-Object { $_.noLongJob } | Group-Object pushKey | ForEach-Object {
    $row = $_.Group[0]
    @{ headSha = $row.headSha; prs = $row.prs; changedFiles = $row.changedFiles }
})
[ordered]@{
    sampledRuns = $runs.Count; requestedRuns = $Limit
    limitations = @('Supersession is a within-snapshot proxy using updated_at, not job-finish proof.',
        'Counts with missing PR associations are observed signals, not complete waste totals.',
        'head_sha is compared as supplied; retained snapshots may contain prefixes.',
        'Only supplied run attempts are measured; earlier attempts are not reconstructed.',
        'Job burn covers observed wasted runs only; UNKNOWN evidence is never an observed zero.')
    waste = @{
        status = if ($unknownPr) { 'PARTIAL' } else { 'PROXY' }
        cancelled = @($rows | Where-Object cancelled).Count
        supersededObserved = @($rows | Where-Object supersededObserved).Count
        failedThenReplacedObserved = @($rows | Where-Object failedThenReplacedObserved).Count
        observedWastedRuns = @($rows | Where-Object wastedObserved).Count
        unknownPrRuns = $unknownPr
    }
    longJobs = @{
        status = if ($unknownJobs) { 'UNKNOWN' } else { 'KNOWN' }
        unknownRuns = $unknownJobs; burnedInObservedWaste = $burns
    }
    noLongJobPushes = @{
        status = if ($unknownPlan) { 'UNKNOWN' } else { 'KNOWN' }
        unknownRuns = $unknownPlan; observed = $noLongPushes
    }
    runs = $rows
} | ConvertTo-Json -Depth 12
