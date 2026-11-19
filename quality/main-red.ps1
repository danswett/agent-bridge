#Requires -Version 7
[CmdletBinding()]
param([long]$RunId, [string]$Repository = $env:GITHUB_REPOSITORY)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'rerun-known-flakes.ps1') -RunId $RunId -Repository $Repository
$script:CatalogPath = Join-Path $PSScriptRoot 'catalog.yml'
# The S3b generator (PR 3078), called by path only: until it is on main a request is just recorded.
$script:QuarantineScript = Join-Path $PSScriptRoot 'quarantine-pr.ps1'

# Tiered gating (#3210): the post-merge tier is the tier_jobs flow list of each tier: post-merge
# suite in catalog.yml. Anything this minimal read misses stays blocking, so a miss only alerts louder.
function Get-PostMergeJobs([string]$Path) {
    $jobs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return , $jobs }
    $tier = $null; $names = @()
    foreach ($line in @(Get-Content -LiteralPath $Path) + 'end:') {
        if ($line -match '^(\S|  - )') {
            if ($tier -eq 'post-merge') { foreach ($name in $names) { [void]$jobs.Add($name) } }
            $tier = $null; $names = @()
        }
        if ($line -match '^    tier:\s*([\w-]+)\s*(#.*)?$') { $tier = $Matches[1] }
        elseif ($line -match '^    tier_jobs:\s*\[([^\]]*)\]\s*(#.*)?$') {
            # Quoted names may hold commas; splitting them would invent names that match blocking jobs.
            $names = @(foreach ($m in [regex]::Matches($Matches[1], '''([^'']*)''|"([^"]*)"|([^,''"]+)')) {
                    foreach ($g in 1..3) { if ($m.Groups[$g].Success -and $m.Groups[$g].Value.Trim()) { $m.Groups[$g].Value.Trim() } }
                })
        }
    }
    , $jobs
}

function Get-RedVerdict([string]$Conclusion) {
    if ($Conclusion -in @('failure', 'timed_out', 'startup_failure')) { return 'red' }
    if ($Conclusion -eq 'success') { return 'green' }
    'none'
}

function Get-FailedJobs([long]$RunId, [string]$Repository, [Collections.Generic.HashSet[string]]$PostMergeJobs) {
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
            [pscustomobject]@{ Name = $job.name; Tests = $tests; LogReadable = $readable
                PostMerge = $null -ne $PostMergeJobs -and $PostMergeJobs.Contains([string]$job.name) }
        }
    }
}

function Get-MainRedIssue([string]$Repository) {
    Invoke-Gh issue list --repo $Repository --state open --label main-red --limit 100 --json 'number,body,author' |
        ConvertFrom-Json | Where-Object { $_.author.login -in @('github-actions[bot]', 'app/github-actions', 'github-actions') } |
        Select-Object -First 1
}

function Format-MainRedBody($Run, [string]$Repository, $Jobs, [bool]$PostMergeOnly) {
    function Safe-Text([string]$Text) {
        $clean = $Text.Replace('@', '[at]').Replace('`', "'") -replace '\p{C}', ''
        # B3b compares names across entries: a changed name must not read as another, real one.
        if (-not [string]::Equals($clean, $Text, [StringComparison]::Ordinal)) { $clean += ' [sanitized]' }
        if ($clean.Length -gt 300) { return $clean.Substring(0, 300) + ' [truncated]' }
        $clean
    }
    $sha = $Run.head_sha.Substring(0, 8)
    $lines = @("main-red: run=$($Run.id) attempt=$($Run.run_attempt) sha=$sha",
        "CI on main: https://github.com/$Repository/actions/runs/$($Run.id)")
    if ($PostMergeOnly) {
        $lines += "post-merge tier only: an alert; a pull request's merge verdict never counts these jobs."
    }
    $lines += '', '```'
    foreach ($job in $Jobs) {
        $tier = if ($job.PSObject.Properties['PostMerge'] -and $job.PostMerge) { ' (post-merge-tier)' } else { '' }
        $lines += "Job$($tier): $(Safe-Text $job.Name)"
        if (-not $job.LogReadable) { $lines += '  log was unreadable' }
        elseif ($job.Tests.Count -eq 0) { $lines += '  no test name was found' }
        else { foreach ($test in $job.Tests) { $lines += "  $(Safe-Text $test)" } }
    }
    if (@($Jobs).Count -eq 0) { $lines += 'No failed jobs were reported (workflow startup failure or unavailable job evidence).' }
    $text = $lines -join "`n"
    if ($text.Length -gt 11500) { $text = $text.Substring(0, 11500) + "`n[evidence truncated]" }
    $text + "`n" + '```'
}

# B3b (#3210): the one test an entry names when every failed job but quality-gate is post-merge tier and
# names only that test; otherwise $null. Entries are parsed, not jobs, so the previous run reads the same way.
function Get-QuarantineTest([string]$Entry) {
    if ($Entry.Contains('[evidence truncated]')) { return $null }
    $perJob = @(); $kind = $null
    foreach ($line in $Entry -split '\r?\n') {
        if ($line -cmatch '^Job( \(post-merge-tier\))?: (.*)$') {
            $kind = if ($Matches[2] -ceq 'quality-gate') { 'gate' } elseif ($Matches[1]) { 'post' } else { return $null }
            if ($kind -eq 'post') { $perJob += , [Collections.Generic.List[string]]::new() }
        } elseif ($kind -eq 'post' -and $line -cmatch '^  (\S.*)$') { $perJob[-1].Add($Matches[1]) }
    }
    # Ordinal throughout: PowerShell's -ceq and -Unique compare culture-aware and ignore zero-width characters.
    $tests = [Collections.Generic.HashSet[string]]::new([string[]]@(foreach ($list in $perJob) { if ($list.Count -eq 1) { $list[0] } else { '' } }),
        [StringComparer]::Ordinal)
    if ($tests.Count -ne 1) { return $null }
    $test = @($tests)[0]
    # The generator's own supported-name shape, so a sanitized, truncated or sentinel line never matches.
    if ($test -cnotmatch '^(?:[A-Za-z_]\w*(?:\.[A-Za-z_]\w*){2,}|(?:[\w-]+/)*[\w-]+\.spec\.mjs:[1-9]\d*)$') { return $null }
    $test
}

function Request-Quarantine([long]$RunId, [string]$Test, [string]$Repository) {
    # Default off: the generator writes a branch and a draft PR, so it runs only while Quality has
    # turned MAIN_RED_QUARANTINE on (it rechecks the repository variable before each write).
    if (-not [string]::Equals($env:MAIN_RED_QUARANTINE, 'on', [StringComparison]::Ordinal)) { Write-Warning "Quarantine of $Test recorded only: MAIN_RED_QUARANTINE is not on."; return }
    if (-not (Test-Path -LiteralPath $script:QuarantineScript -PathType Leaf)) { Write-Warning "Quarantine of $Test recorded only: no generator."; return }
    try { & $script:QuarantineScript -RunId $RunId -TestName $Test -Repository $Repository -Hours 24 | ForEach-Object { Write-Host "quarantine-pr: $_" } }
    catch { Write-Warning "Quarantine generator refused $($Test): $($_.Exception.Message)" }
    finally { $global:LASTEXITCODE = 0 }
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
    $comments = @()
    if ($issue) {
        if (($issue.body -split '\r?\n') -ccontains $marker) { return }
        $comments = @((Invoke-Gh issue view $issue.number --repo $Repository --json comments | ConvertFrom-Json).comments |
                Where-Object { $_.author.login -in @('github-actions[bot]', 'app/github-actions', 'github-actions') })
        if (@($comments | Where-Object { ($_.body -split '\r?\n') -ccontains $marker }).Count -gt 0) { return }
    }
    $jobs = @(Get-FailedJobs $RunId $Repository (Get-PostMergeJobs $script:CatalogPath))
    # The PR verdict's main-green skips post-merge tier jobs (#3219); say so, so this isn't read as a hold.
    # quality-gate only sums the other ci.yml jobs and main-green never lists it, so it echoes them.
    $own = @($jobs | Where-Object { $_.Name -cne 'quality-gate' })
    $postMergeOnly = $own.Count -gt 0 -and @($own | Where-Object { -not $_.PostMerge }).Count -eq 0
    $body = Format-MainRedBody $run $Repository $jobs $postMergeOnly
    # B3b: the same single post-merge test failing in two entries in a row proposes one quarantine, once
    # per open issue. It is a line in this alert and a generator call, never a status, check or label.
    $test = Get-QuarantineTest $body; $request = $false
    if ($test -and $issue) {
        $entries = @(@($issue.body) + @($comments | ForEach-Object { $_.body }) | Where-Object { $_ -clike 'main-red: *' })
        $flag = "quarantine-request: test=$test"
        $request = $entries.Count -gt 0 -and [string]::Equals((Get-QuarantineTest $entries[-1]), $test, [StringComparison]::Ordinal) -and
            @($entries | Where-Object { [Array]::IndexOf([string[]]($_ -split '\r?\n'), $flag) -ge 0 }).Count -eq 0
        if ($request) {
            $body += "`n$flag`nSecond failure in a row: one draft fixes-main quarantine, 24 h expiry, refused unless " +
                "MAIN_RED_QUARANTINE is on: ``pwsh -NoProfile -File quality/quarantine-pr.ps1 -RunId $RunId -TestName $test -Repository $Repository -Hours 24``"
        }
    }
    if ($issue) { Invoke-Gh issue comment $issue.number --repo $Repository --body $body | Out-Null }
    else {
        $title = if ($postMergeOnly) { "Main red, post-merge tier only ($sha)" } else { "Main red: CI failing on main ($sha)" }
        Invoke-Gh label create main-red --repo $Repository --color B60205 --description 'CI is red on main' --force | Out-Null
        Invoke-Gh issue create --repo $Repository --title $title --label main-red --body $body | Out-Null
    }
    if ($request) { Request-Quarantine $RunId $test $Repository }
}

if ($MyInvocation.InvocationName -eq '.') { return }
Invoke-MainRed -RunId $RunId -Repository $Repository
