#Requires -Version 7.4
<#
.SYNOPSIS
  Once, when called: how the first ci.yml runs went after AB_CI_SELF_HOSTED is set again (#4104). Read-only.

.DESCRIPTION
  Finds the first same-repo pull_request run and the first push run on main created at or after -Since, and for each
  prints the product (windows) job's runner, its time against the median of hosted product jobs on main before -Since,
  and, if the job failed, the failed tests or the tests running when the test host was aborted (one log read).
  Never polls: call it again later if a run is still going. Reads with the caller's GH_TOKEN; writes nothing.

  GitHub reads per call: 2 REST run lists, 1 REST job list per candidate run (at most 3 each side), 1 GraphQL query for the hosted
  baseline, and 1 job log for each failed product job.

.EXAMPLE
  pwsh quality/ab-ci-watch.ps1 -Since '2026-11-24T18:00:00Z'
#>
[CmdletBinding()]
param(
    # When AB_CI_SELF_HOSTED went back on.
    [datetime] $Since,
    [string] $Repository = 'danswett/agent-bridge',
    # Hosted main runs before -Since that make up the baseline.
    [ValidateRange(3, 30)]
    [int] $Baseline = 15
)

$ErrorActionPreference = 'Stop'

# The first few same-repo PR runs and push runs on main at or after $Since, oldest first, from runs as GitHub lists them.
# A few, not one: a draft's run has no product job, so the next run is the one that tells.
function Select-AbCiFirstRuns([object[]] $Runs, [datetime] $Since, [string] $Repository, [int] $Take = 3)
{
    $after = @($Runs | Where-Object { [datetime]$_.created_at -ge $Since.ToUniversalTime() } | Sort-Object { [datetime]$_.created_at })
    [pscustomobject]@{
        PullRequest = @($after | Where-Object { $_.event -eq 'pull_request' -and $_.head_repository.full_name -eq $Repository } | Select-Object -First $Take)
        Main = @($after | Where-Object { $_.event -eq 'push' -and $_.head_branch -eq 'main' } | Select-Object -First $Take)
    }
}

# ab-ci is an x64 pool: the windows product job that is not arm64.
function Select-AbCiProductJob([object[]] $Jobs)
{
    @($Jobs | Where-Object { $_.name -like 'product (windows*' -and $_.name -notlike '*arm*' }) | Select-Object -First 1
}

function Get-AbCiMedian([double[]] $Seconds)
{
    if (-not $Seconds -or $Seconds.Count -eq 0) { return $null }
    $sorted = @($Seconds | Sort-Object)
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$mid] }
    ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

# From a job log: tests xunit marked [FAIL], and the tests vstest names as running when it aborted the test host.
function Get-AbCiTestFailures([string] $Log)
{
    $lines = $Log -split "`r?`n" | ForEach-Object { $_ -replace '\x1b\[[0-9;]*m', '' -replace '^\S+Z\s', '' }
    $failed = @($lines | Where-Object { $_ -match '^\s*\[xUnit\.net [^\]]+\]\s+(\S+)\s.*\[FAIL\]\s*$' } | ForEach-Object { $Matches[1] } | Select-Object -Unique)
    $running = [System.Collections.Generic.List[string]]::new()
    $inBlock = $false
    foreach ($line in $lines)
    {
        if ($line -match 'The test running when the crash occurred') { $inBlock = $true; continue }
        if (-not $inBlock) { continue }
        if ($line -match '^\s*$' -or $line -match 'may, or may not be the source') { $inBlock = $false; continue }
        $running.Add($line.Trim())
    }
    [pscustomobject]@{ Failed = $failed; Running = @($running | Select-Object -Unique) }
}

function Invoke-Gh([string[]] $Arguments)
{
    $out = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed ($LASTEXITCODE)" }
    $out -join "`n"
}

function Show-AbCiRun([string] $Label, [object[]] $Candidates, [Nullable[double]] $Median)
{
    if (-not $Candidates) { "${Label}: no run yet since $($Since.ToUniversalTime().ToString('u')); call again later."; return }
    $Run = $null; $product = $null
    foreach ($candidate in $Candidates)
    {
        $jobs = (Invoke-Gh @('api', "repos/$Repository/actions/runs/$($candidate.id)/jobs?per_page=100") | ConvertFrom-Json).jobs
        if ($product = Select-AbCiProductJob -Jobs $jobs) { $Run = $candidate; break }
        "${Label}: run $($candidate.id) has no x64 product job (a draft's fast ring, or skipped); looking at the next."
    }
    if (-not $product) { "${Label}: none of the first $(@($Candidates).Count) runs has an x64 product job yet; call again later."; return }
    $state = if ($product.status -ne 'completed') { "still $($product.status)" } else { $product.conclusion }
    $seconds = if ($product.completed_at) { ([datetime]$product.completed_at - [datetime]$product.started_at).TotalSeconds } else { $null }
    $versus = if ($null -ne $seconds -and $Median) { " ({0:+0;-0}% against the hosted median {1:0} s)" -f (100 * ($seconds - $Median) / $Median), $Median } else { '' }
    "${Label}: run $($Run.id) ($($Run.head_sha.Substring(0, 8))), product job $($product.id) on runner '$($product.runner_name)' [$($product.labels -join ', ')]: $state" +
        $(if ($null -ne $seconds) { ", {0:0} s{1}" -f $seconds, $versus } else { '' })
    if ($product.conclusion -eq 'failure')
    {
        $log = Invoke-Gh @('api', '--allow-escape-sequences', "repos/$Repository/actions/jobs/$($product.id)/logs")
        $named = Get-AbCiTestFailures -Log $log
        if ($named.Failed) { "  failed: $($named.Failed -join '; ')" }
        if ($named.Running) { "  running when the test host was aborted: $($named.Running -join '; ')" }
        if (-not $named.Failed -and -not $named.Running) { '  no failed or running test named in the log: a step other than the tests failed.' }
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $PSBoundParameters.ContainsKey('Since')) { throw 'Name -Since: when AB_CI_SELF_HOSTED went back on.' }

$sinceText = $Since.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$runs = (Invoke-Gh @('api', "repos/$Repository/actions/workflows/ci.yml/runs?created=%3E%3D$sinceText&per_page=50") | ConvertFrom-Json).workflow_runs
$first = Select-AbCiFirstRuns -Runs $runs -Since $Since -Repository $Repository

# The hosted baseline: product (windows) jobs of main's push runs before -Since, in one GraphQL query.
$before = (Invoke-Gh @('api', "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&status=success&created=%3C$sinceText&per_page=$Baseline") | ConvertFrom-Json).workflow_runs
$median = $null
if ($before)
{
    $owner, $name = $Repository -split '/'
    $fields = (0..(@($before).Count - 1) | ForEach-Object { "c${_}: object(expression: `"$(@($before)[$_].head_sha)`") { ... on Commit { checkSuites(first: 20) { nodes { checkRuns(first: 5, filterBy: { checkName: `"product (windows-latest)`" }) { nodes { startedAt completedAt conclusion } } } } } }" }) -join ' '
    $data = (Invoke-Gh @('api', 'graphql', '-f', "query=query { repository(owner: `"$owner`", name: `"$name`") { $fields } }") | ConvertFrom-Json).data.repository
    $seconds = foreach ($commit in $data.PSObject.Properties.Value)
    {
        foreach ($check in $commit.checkSuites.nodes.checkRuns.nodes | Where-Object { $_.conclusion -eq 'SUCCESS' -and $_.completedAt })
        {
            ([datetime]$check.completedAt - [datetime]$check.startedAt).TotalSeconds
        }
    }
    $median = Get-AbCiMedian -Seconds @($seconds)
}
"hosted baseline: " + $(if ($null -ne $median) { "median {0:0} s over {1} product jobs on main before {2}" -f $median, @($seconds).Count, $sinceText } else { 'none found' })
Show-AbCiRun -Label 'first same-repo PR run' -Candidates $first.PullRequest -Median $median
Show-AbCiRun -Label 'first main run' -Candidates $first.Main -Median $median
