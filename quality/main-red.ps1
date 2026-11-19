#Requires -Version 7
[CmdletBinding()]
param([long]$RunId, [string]$Repository = $env:GITHUB_REPOSITORY)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'rerun-known-flakes.ps1') -RunId $RunId -Repository $Repository

function Get-RedVerdict([string]$Conclusion) {
    if ($Conclusion -in @('failure', 'timed_out', 'startup_failure')) { return 'red' }
    if ($Conclusion -eq 'success') { return 'green' }
    'none'
}

function Get-FailedJobs([long]$RunId, [string]$Repository) {
    $pages = Invoke-Gh api "repos/$Repository/actions/runs/$RunId/jobs?per_page=100" --paginate --slurp | ConvertFrom-Json
    foreach ($page in $pages) {
        foreach ($job in $page.jobs | Where-Object { $_.conclusion -in @('failure', 'timed_out') }) {
            $readable = $true; $tests = @()
            try {
                $log = (Invoke-Gh api "repos/$Repository/actions/jobs/$($job.id)/logs" --allow-escape-sequences) -join "`n"
            } catch {
                # Only a log read is best-effort; do not publish error text or raw log content.
                $readable = $false
                $global:LASTEXITCODE = 0
            }
            if ($readable) { $tests = Get-FailedTests $log }
            [pscustomobject]@{ Name = $job.name; Tests = $tests; LogReadable = $readable }
        }
    }
}

function Get-MainRedIssue([string]$Repository) {
    Invoke-Gh issue list --repo $Repository --state open --label main-red --limit 100 --json 'number,body,author' |
        ConvertFrom-Json | Where-Object { $_.author.login -in @('github-actions[bot]', 'app/github-actions', 'github-actions') } |
        Select-Object -First 1
}

function Format-MainRedBody($Run, [string]$Repository, $Jobs) {
    function Safe-Text([string]$Text) {
        $clean = $Text.Replace('@', '[at]').Replace('`', "'") -replace '\p{C}', ''
        if ($clean.Length -gt 300) { return $clean.Substring(0, 300) + ' [truncated]' }
        $clean
    }
    $sha = $Run.head_sha.Substring(0, 8)
    $lines = @("main-red: run=$($Run.id) attempt=$($Run.run_attempt) sha=$sha",
        "CI on main: https://github.com/$Repository/actions/runs/$($Run.id)", '', '```')
    foreach ($job in $Jobs) {
        $lines += "Job: $(Safe-Text $job.Name)"
        if (-not $job.LogReadable) { $lines += '  log was unreadable' }
        elseif ($job.Tests.Count -eq 0) { $lines += '  no test name was found' }
        else { foreach ($test in $job.Tests) { $lines += "  $(Safe-Text $test)" } }
    }
    if (@($Jobs).Count -eq 0) { $lines += 'No failed jobs were reported (workflow startup failure or unavailable job evidence).' }
    $text = $lines -join "`n"
    if ($text.Length -gt 11500) { $text = $text.Substring(0, 11500) + "`n[evidence truncated]" }
    $text + "`n" + '```'
}

function Invoke-MainRed([long]$RunId, [string]$Repository) {
    if ($RunId -le 0 -or $Repository -notmatch '^[\w.-]+/[\w.-]+$') { throw 'Give a positive RunId and owner/repository.' }
    $run = Invoke-Gh api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json
    if ($run.path -ne '.github/workflows/ci.yml' -or $run.event -ne 'push' -or $run.head_branch -ne 'main' -or
        $run.head_repository.full_name -ne $Repository -or $run.status -ne 'completed') { return }
    $verdict = Get-RedVerdict $run.conclusion
    if ($verdict -eq 'none') { return }
    # Filter completed runs server-side: a newer in-progress push says nothing about main's state.
    $latest = (Invoke-Gh api "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&status=completed&per_page=1" | ConvertFrom-Json).workflow_runs
    if (@($latest).Count -ne 1 -or $latest[0].id -ne $RunId -or $latest[0].run_attempt -ne $run.run_attempt) { return }
    $issue = Get-MainRedIssue $Repository
    $sha = $run.head_sha.Substring(0, 8)
    if ($verdict -eq 'green') {
        if ($issue) {
            Invoke-Gh issue comment $issue.number --repo $Repository --body "main-red: green again, run=$RunId attempt=$($run.run_attempt), sha=$sha" | Out-Null
            Invoke-Gh issue close $issue.number --repo $Repository | Out-Null
        }
        return
    }
    $marker = "main-red: run=$RunId attempt=$($run.run_attempt) sha=$sha"
    if ($issue) {
        if (($issue.body -split '\r?\n') -ccontains $marker) { return }
        $comments = (Invoke-Gh issue view $issue.number --repo $Repository --json comments | ConvertFrom-Json).comments
        if (@($comments | Where-Object { $_.author.login -in @('github-actions[bot]', 'app/github-actions', 'github-actions') -and
                    ($_.body -split '\r?\n') -ccontains $marker }).Count -gt 0) { return }
    }
    $body = Format-MainRedBody $run $Repository @(Get-FailedJobs $RunId $Repository)
    if ($issue) { Invoke-Gh issue comment $issue.number --repo $Repository --body $body | Out-Null }
    else {
        Invoke-Gh label create main-red --repo $Repository --color B60205 --description 'CI is red on main' --force | Out-Null
        Invoke-Gh issue create --repo $Repository --title "Main red: CI failing on main ($sha)" --label main-red --body $body | Out-Null
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
Invoke-MainRed -RunId $RunId -Repository $Repository
