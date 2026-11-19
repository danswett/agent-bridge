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

function Get-FailedJobs([long]$RunId, [string]$Repository, [Collections.Generic.HashSet[string]]$PostMergeJobs, [int]$Attempt) {
    $pages = Invoke-Gh api "repos/$Repository/actions/runs/$RunId/attempts/$Attempt/jobs?per_page=100" --paginate --slurp | ConvertFrom-Json
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

function Get-MainRedIssue([string]$Repository, [switch]$IncludeClosed) {
    $state = if ($IncludeClosed) { 'all' } else { 'open' }
    $fields = if ($IncludeClosed) { 'number,body,author,state' } else { 'number,body,author' }
    Invoke-Gh issue list --repo $Repository --state $state --label main-red --limit 100 --json $fields |
        ConvertFrom-Json | Where-Object { $_.author.login -in @('github-actions[bot]', 'app/github-actions', 'github-actions') } |
        Sort-Object -Stable { if ($IncludeClosed) { $_.state -eq 'CLOSED' } else { $false } } |
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
    # A rerun can mutate the run API before this detector starts; the completed event cannot.
    $run = if ($env:GITHUB_EVENT_NAME -eq 'workflow_run') {
        (Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json).workflow_run
    } else { Invoke-Gh api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json }
    if ($run.id -ne $RunId) { throw 'The completed event does not match RunId.' }
    if ($run.path -ne '.github/workflows/ci.yml' -or $run.event -ne 'push' -or $run.head_branch -ne 'main' -or
        $run.head_repository.full_name -ne $Repository) { return }
    # Reconcile the newest completed verdict even if an older event replaced its pending detector.
    $recent = (Invoke-Gh api "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=100" | ConvertFrom-Json).workflow_runs
    $latest = $recent | Where-Object { $_.status -eq 'completed' -or $_.run_attempt -gt 1 } |
        Sort-Object run_number -Descending | Select-Object -First 1
    if ($latest -and $latest.status -ne 'completed') {
        $latest = Invoke-Gh api "repos/$Repository/actions/runs/$($latest.id)/attempts/$($latest.run_attempt - 1)" | ConvertFrom-Json
    }
    $completed = @(@($run) + @($latest) | Where-Object { $null -ne $_ -and $_.status -eq 'completed' } | Sort-Object run_number -Descending)
    if ($completed.Count -eq 0) { return }
    foreach ($candidate in $completed | Where-Object { $_.run_number -eq $completed[0].run_number } | Sort-Object run_attempt -Unique) {
        if ($candidate.path -ne '.github/workflows/ci.yml' -or $candidate.event -ne 'push' -or $candidate.head_branch -ne 'main' -or
            $candidate.head_repository.full_name -ne $Repository -or $candidate.run_attempt -lt 1) { throw 'Invalid completed main CI run.' }
        Publish-MainRed $candidate $Repository
    }
}

function Publish-MainRed($run, [string]$Repository) {
    $RunId = $run.id
    $verdict = Get-RedVerdict $run.conclusion
    if ($verdict -eq 'none') { return }
    $issue = Get-MainRedIssue $Repository -IncludeClosed:($verdict -eq 'red')
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
        $entries = @($issue.body) + @($comments | ForEach-Object { $_.body })
        if (@($entries | Where-Object { $_ -cmatch "(?m)^main-red: run=$RunId attempt=(\d+) sha=" -and
                    [long]$Matches[1] -gt $run.run_attempt }).Count -gt 0) { return }
        if ($issue.state -eq 'CLOSED') { $issue = $null; $comments = @() }
    }
    $jobs = @(Get-FailedJobs $RunId $Repository (Get-PostMergeJobs $script:CatalogPath) $run.run_attempt)
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
