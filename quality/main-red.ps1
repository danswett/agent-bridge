#Requires -Version 7
[CmdletBinding()]
param([long]$RunId, [string]$Repository = $env:GITHUB_REPOSITORY)
$ErrorActionPreference = 'Stop'
# Reuse the log parser and checked gh wrapper, without running the rerun entry point.
. (Join-Path $PSScriptRoot 'rerun-known-flakes.ps1') -RunId $RunId -Repository $Repository

function Get-RedVerdict([string]$Conclusion) {
    if ($Conclusion -in 'failure','timed_out','startup_failure') { return 'red' }
    if ($Conclusion -eq 'success') { return 'green' }
    'none'
}

function Get-FailedJobs([long]$RunId, [string]$Repository) {
    $pages = Invoke-Gh api "repos/$Repository/actions/runs/$RunId/jobs?per_page=100" --paginate --slurp | ConvertFrom-Json
    foreach ($job in @($pages.jobs | Where-Object { $_.conclusion -in 'failure','timed_out' })) {
        $unreadable = $false; $tests = @()
        try {
            $log = (Invoke-Gh api "repos/$Repository/actions/jobs/$($job.id)/logs" --allow-escape-sequences) -join "`n"
            $tests = Get-FailedTests $log
        } catch {
            $unreadable = $true
            Write-Warning "Job $($job.id) log unreadable; detection will still publish the job."
        }
        [pscustomobject]@{ Name = $job.name; Tests = @($tests); LogUnreadable = $unreadable }
    }
}

function Get-MainRedIssue([string]$Repository) {
    Invoke-Gh issue list --repo $Repository --label main-red --author 'github-actions[bot]' --state open --limit 1 --json 'number,body' |
        ConvertFrom-Json | Select-Object -First 1
}

function Format-MainRedBody($Run, [string]$Repository, $Jobs) {
    # Never publish raw logs or exception text. Names are untrusted, even inside fences.
    function Safe-Name([string]$Text) {
        $text = $Text -replace '@', '[at]' -replace '`', "'" -replace '[\x00-\x1f\x7f]', ' '
        $text.Substring(0, [Math]::Min(500, $text.Length))
    }
    $lines = @("<!-- main-red run=$($Run.id) attempt=$($Run.run_attempt) -->"
        "CI on main is red: https://github.com/$Repository/actions/runs/$($Run.id)"
        "sha=$($Run.head_sha) attempt=$($Run.run_attempt)", '', '```')
    foreach ($job in @($Jobs)) {
        $lines += 'job: ' + (Safe-Name $job.Name)
        if ($job.LogUnreadable) { $lines += '  log unreadable; test names unavailable' }
        elseif ($job.Tests.Count -eq 0) { $lines += '  no test name found (build error, timeout or unrecognized failure)' }
        else { $lines += @($job.Tests | ForEach-Object { '  test: ' + (Safe-Name $_) }) }
    }
    if (@($Jobs).Count -eq 0) { $lines += 'no failed jobs reported' }
    $body = $lines -join "`n"
    if ($body.Length -gt 15000) { $body = $body.Substring(0, 15000) + "`n[truncated]" }
    $body + "`n" + '```'
}

function Invoke-MainRed([long]$RunId, [string]$Repository) {
    if ($RunId -le 0 -or $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw 'Give a positive -RunId and an owner/repository -Repository.'
    }
    $run = Invoke-Gh api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json
    if ($run.path -ne '.github/workflows/ci.yml' -or $run.event -ne 'push' -or $run.head_branch -ne 'main' -or
        $run.head_repository.full_name -ne $Repository -or $run.status -ne 'completed') { return }
    $verdict = Get-RedVerdict $run.conclusion
    if ($verdict -eq 'none') { return }
    # One completed-run lookup, never a polling loop. An in-flight newer run is not a verdict.
    $latest = (Invoke-Gh api "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&status=completed&per_page=1" |
        ConvertFrom-Json).workflow_runs
    if (@($latest).Count -ne 1 -or $latest[0].id -ne $RunId) { return }
    if ($run.head_sha -notmatch '^[a-fA-F0-9]{40}$' -or $run.run_attempt -lt 1 -or $run.id -ne $RunId) {
        throw 'Invalid CI run identity.'
    }
    $open = Get-MainRedIssue $Repository
    $marker = "<!-- main-red run=$RunId attempt=$($run.run_attempt) -->"
    $recorded = $open -and $open.body.StartsWith("$marker`n", [StringComparison]::Ordinal)
    if ($open -and -not $recorded) {
        $comments = (Invoke-Gh issue view $open.number --repo $Repository --json comments | ConvertFrom-Json).comments
        $recorded = @($comments | Where-Object {
            $_.author -and $_.author.login -in 'github-actions[bot]','github-actions','app/github-actions' -and
                $_.body.StartsWith("$marker`n", [StringComparison]::Ordinal)
        }).Count -gt 0
    }
    if ($verdict -eq 'green') {
        if ($open) {
            if (-not $recorded) {
                $body = "$marker`nmain-red: green again, run=$RunId attempt=$($run.run_attempt), sha=$($run.head_sha.Substring(0,8))"
                Invoke-Gh issue comment $open.number --repo $Repository --body $body | Out-Null
            }
            Invoke-Gh issue close $open.number --repo $Repository | Out-Null
        }
        return
    }
    if ($recorded) { return }
    $jobs = @(Get-FailedJobs -RunId $RunId -Repository $Repository)
    $body = Format-MainRedBody $run $Repository $jobs
    if ($open) { Invoke-Gh issue comment $open.number --repo $Repository --body $body | Out-Null }
    else {
        Invoke-Gh label create main-red --repo $Repository --color B60205 --description 'CI on main is red' --force | Out-Null
        Invoke-Gh issue create --repo $Repository --label main-red --title "Main red: CI failing on main ($($run.head_sha.Substring(0,8)))" --body $body | Out-Null
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
Invoke-MainRed -RunId $RunId -Repository $Repository
