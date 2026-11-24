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
    try { $lines = @(Get-Content -LiteralPath $Path -ErrorAction Stop) }
    catch [IO.IOException], [UnauthorizedAccessException] {
        Write-Warning 'Cannot read the tier catalog; every failed job stays blocking.'
        return , $jobs
    }
    $tier = $null; $names = @()
    foreach ($line in $lines + 'end:') {
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
    $fields = if ($IncludeClosed) { 'number,body,author,state,title' } else { 'number,body,author,title' }
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
    if (-not [string]::Equals($env:MAIN_RED_QUARANTINE, 'on', [StringComparison]::Ordinal)) { Write-Warning "Quarantine of $Test recorded only: MAIN_RED_QUARANTINE is not on."; return $false }
    if (-not (Test-Path -LiteralPath $script:QuarantineScript -PathType Leaf)) { Write-Warning "Quarantine of $Test recorded only: no generator."; return $false }
    try {
        $global:LASTEXITCODE = 0
        & $script:QuarantineScript -RunId $RunId -TestName $Test -Repository $Repository -Hours 24 | ForEach-Object { Write-Host "quarantine-pr: $_" }
        if ($LASTEXITCODE -ne 0) { throw "generator exited $LASTEXITCODE" }
        return $true
    }
    catch { Write-Warning "Quarantine generator refused $($Test): $($_.Exception.Message)"; return $false }
    finally { $global:LASTEXITCODE = 0 }
}

function Invoke-MainRed([long]$RunId, [string]$Repository) {
    if ($RunId -le 0 -or $Repository -notmatch '^[\w.-]+/[\w.-]+$') { throw 'Give a positive RunId and owner/repository.' }
    # A rerun can mutate the run API before this detector starts; the completed event cannot.
    $run = if ($env:GITHUB_EVENT_NAME -eq 'workflow_run') {
        (Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json).workflow_run
    } else { Invoke-Gh api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json }
    if ($run.id -ne $RunId) { throw 'The completed event does not match RunId.' }
    if (-not (Test-MainPush $run $Repository)) { return }
    # Pending detectors replace each other, so every detector applies the newest decisive verdict, whichever
    # event (stale, delayed or redelivered) started it, and every order converges on the same incident state.
    $recent = @((Invoke-Gh api "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=100" |
            ConvertFrom-Json).workflow_runs | Where-Object { Test-MainPush $_ $Repository })
    $known = @{}
    foreach ($r in $recent + @($run)) { $known["$($r.id)/$($r.run_attempt)"] = $r }
    function Get-Attempt($Id, [int]$N) {
        if (-not $known.ContainsKey("$Id/$N")) {
            $a = Invoke-Gh api "repos/$Repository/actions/runs/$Id/attempts/$N" | ConvertFrom-Json
            if ($a.id -ne $Id -or $a.run_attempt -ne $N) { throw 'CI attempt does not match the requested run and attempt.' }
            $known["$Id/$N"] = $a
        }
        $known["$Id/$N"]
    }
    # Newest run, then newest attempt, first. Cancelled, skipped and running attempts are not verdicts. A running
    # attempt never holds back a finished one: its own completed event reconciles again when it ends.
    $verdict = $null
    $ordered = @($recent + @($run) | Group-Object id | ForEach-Object { $_.Group | Sort-Object run_attempt | Select-Object -Last 1 } |
            Sort-Object run_number -Descending)
    :search foreach ($r in $ordered) {
        for ($n = $r.run_attempt; $n -ge 1; $n--) {
            $a = Get-Attempt $r.id $n
            if ($a.status -eq 'completed' -and (Get-RedVerdict $a.conclusion) -ne 'none') { $verdict = $a; break search }
        }
    }
    if (-not $verdict) { return }
    # A success event may arrive after the next failure. Recover its boundary for quarantine counting,
    # without closing the incident over a newer red verdict.
    $recovery = if ($verdict.conclusion -eq 'success') { $verdict } else { $null }
    if (-not $recovery) {
        :recovery foreach ($r in @($ordered | Where-Object { $_.run_number -le $verdict.run_number })) {
            $start = if ($r.id -eq $verdict.id) { $verdict.run_attempt - 1 } else { $r.run_attempt }
            for ($n = $start; $n -ge 1; $n--) {
                $a = Get-Attempt $r.id $n
                if ($a.status -eq 'completed' -and $a.conclusion -eq 'success') { $recovery = $a; break recovery }
            }
        }
    }
    # The completed event's own failure stays evidence when a retry of the same run already decided main's state.
    if ($run.id -eq $verdict.id -and $run.run_attempt -lt $verdict.run_attempt -and (Get-RedVerdict $run.conclusion) -eq 'red') {
        Publish-MainRed $run $Repository -Recovery $recovery
    } elseif ($run.id -ne $verdict.id -and (Get-RedVerdict $run.conclusion) -eq 'red') {
        Write-Host "Red evidence for run=$($run.id) attempt=$($run.run_attempt) is not backfilled: newer decisive run=$($verdict.id) attempt=$($verdict.run_attempt) determines main. A missing old-run entry keeps auto-revert fail-safe."
    }
    Publish-MainRed $verdict $Repository -Recovery $recovery
}

function Test-MainPush($Run, [string]$Repository) {
    $Run.path -eq '.github/workflows/ci.yml' -and $Run.event -eq 'push' -and $Run.head_branch -eq 'main' -and
    $Run.head_repository.full_name -eq $Repository -and $Run.run_attempt -ge 1
}

# The unrecovered streak is every attempt after the newest successful one (#3347); none means the whole issue.
function Test-AfterRecovery([long]$Id, [long]$N, $Recovery) {
    -not $Recovery -or $Id -gt $Recovery.id -or ($Id -eq $Recovery.id -and $N -gt $Recovery.run_attempt)
}

function Get-CommentTime($Comment) {
    $at = if ($Comment.PSObject.Properties['createdAt']) { $Comment.createdAt }
    if ($at -is [datetime] -and $at.Kind -eq 'Utc') { return $at }
    $t = [datetime]::MinValue
    if ($at -is [string] -and [datetime]::TryParseExact($at, "yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]'AssumeUniversal,AdjustToUniversal', [ref]$t)) { return $t }
    throw 'Refusing quarantine: a main-red comment has no valid createdAt to place a run-less quarantine-completed line.'
}

# A legacy completion names no run, so it applies to the streak open when GitHub recorded it, ending at the next
# recovery: the entry (or green-again record) just before it decides. Entries only move forward, and the issue
# body is older than every comment. A tie across a recovery is ambiguous, so it refuses rather than guess.
function Test-LegacyInStreak($Issue, $Comments, [datetime]$At, $Recovery) {
    $marks = @(foreach ($c in $Comments) {
            if ($c.body -cmatch '^main-red: (green again, )?run=(\d+) attempt=(\d+),? sha=') {
                $open = -not $Matches[1] -and (Test-AfterRecovery $Matches[2] $Matches[3] $Recovery)
                [pscustomobject]@{ At = Get-CommentTime $c; Open = $open }
            }
        })
    $earlier = @($marks | Where-Object { $_.At -lt $At })
    $last = if ($earlier.Count) { ($earlier | Measure-Object -Property At -Maximum).Maximum }
    $open = @(@($marks | Where-Object { $_.At -eq $At -or ($earlier.Count -and $_.At -eq $last) }) | ForEach-Object Open)
    if (-not $earlier.Count) {
        if ($Issue.body -cnotmatch '^main-red: run=(\d+) attempt=(\d+) sha=') { throw 'Refusing quarantine: the main-red issue body cannot place a run-less quarantine-completed line.' }
        $open += Test-AfterRecovery $Matches[1] $Matches[2] $Recovery
    }
    if (@($open | Sort-Object -Unique).Count -ne 1) { throw "Refusing quarantine: a run-less quarantine-completed line recorded at $($At.ToString('o')) shares its time with another streak." }
    $open[0]
}

# Standalone completion comments for $Test in the current streak. New ones name the requesting run and attempt;
# anything else unplaceable refuses this entry before its write or any generator call, never a global answer.
function Test-QuarantineCompleted($Issue, $Comments, [string]$Test, $Recovery) {
    $done = $false
    foreach ($c in @($Comments | Where-Object { $_.body -cnotlike 'main-red: *' })) {
        foreach ($line in @($c.body -split '\r?\n' | Where-Object { $_ -clike 'quarantine-completed:*' })) {
            if ($line -cmatch '^quarantine-completed: test=(\S+) run=(\d+) attempt=(\d+)$') {
                $name = $Matches[1]; $inStreak = Test-AfterRecovery $Matches[2] $Matches[3] $Recovery
            } elseif ($line -cmatch '^quarantine-completed: test=(\S+)$') {
                $name = $Matches[1]
                $inStreak = [string]::Equals($name, $Test, [StringComparison]::Ordinal) -and
                    (Test-LegacyInStreak $Issue $Comments (Get-CommentTime $c) $Recovery)
            } else { throw "Refusing quarantine: malformed quarantine-completed line '$line'." }
            if ($inStreak -and [string]::Equals($name, $Test, [StringComparison]::Ordinal)) { $done = $true }
        }
    }
    $done
}

function Publish-MainRed($run, [string]$Repository, $Recovery) {
    $RunId = $run.id
    $verdict = Get-RedVerdict $run.conclusion
    if ($verdict -eq 'none') { return }
    $issue = Get-MainRedIssue $Repository -IncludeClosed:($verdict -eq 'red')
    $sha = $run.head_sha.Substring(0, 8)
    $comments = @(); $greenRecorded = $false
    if ($issue) {
        $comments = @((Invoke-Gh issue view $issue.number --repo $Repository --json comments | ConvertFrom-Json).comments |
                Where-Object { $_.author.login -in @('github-actions[bot]', 'app/github-actions', 'github-actions') })
        # Writes only move forward: nothing at or before the newest recorded red or green attempt is written again.
        # Run ids grow with run numbers, so they order runs the same way.
        $recorded = (@($issue.body) + @($comments | ForEach-Object { $_.body })) -join "`n"
        foreach ($m in [regex]::Matches($recorded, '(?m)^main-red: (green again, )?run=(\d+) attempt=(\d+),? sha=')) {
            $id = [long]$m.Groups[2].Value; $n = [long]$m.Groups[3].Value
            # Only the comment is a duplicate: if the close after this attempt's own recovery comment failed,
            # the issue is still open and a redelivery must finish closing it, or main stays reported red.
            if ($verdict -eq 'green' -and $m.Groups[1].Success -and $id -eq $RunId -and $n -eq $run.run_attempt) { $greenRecorded = $true }
            elseif ($id -gt $RunId -or ($id -eq $RunId -and $n -ge $run.run_attempt)) { return }
        }
    }
    if ($verdict -eq 'green') {
        if ($issue) {
            if (-not $greenRecorded) {
                Invoke-Gh issue comment $issue.number --repo $Repository --body "main-red: green again, run=$RunId attempt=$($run.run_attempt), sha=$sha" | Out-Null
            }
            Invoke-Gh issue close $issue.number --repo $Repository | Out-Null
        }
        return
    }
    if ($issue -and $issue.state -eq 'CLOSED') { $issue = $null; $comments = @() }
    $jobs = @(Get-FailedJobs $RunId $Repository (Get-PostMergeJobs $script:CatalogPath) $run.run_attempt)
    # The PR verdict's main-green skips post-merge tier jobs (#3219); say so, so this isn't read as a hold.
    # quality-gate only sums the other ci.yml jobs and main-green never lists it, so it echoes them.
    $own = @($jobs | Where-Object { $_.Name -cne 'quality-gate' })
    $postMergeOnly = $own.Count -gt 0 -and @($own | Where-Object { -not $_.PostMerge }).Count -eq 0
    $body = Format-MainRedBody $run $Repository $jobs $postMergeOnly
    # B3b: the same single post-merge test failing in two entries in a row proposes one quarantine, once
    # per unrecovered streak. It is a line in this alert and a generator call, never a status, check or label.
    $test = Get-QuarantineTest $body
    $generate = $false
    if ($test -and $issue) {
        $entries = @(@($issue.body) + @($comments | ForEach-Object { $_.body }) | Where-Object { $_ -clike 'main-red: *' })
        if ($Recovery) {
            $entries = @($entries | Where-Object {
                    $_ -cmatch '^main-red: run=(\d+) attempt=(\d+) sha=' -and (Test-AfterRecovery $Matches[1] $Matches[2] $Recovery)
                })
        }
        $lines = @($entries -split '\r?\n')
        $intent = @($lines | Where-Object { $_ -clike 'quarantine-request: test=*' } | Select-Object -First 1)
        $eligible = $entries.Count -gt 0 -and [string]::Equals((Get-QuarantineTest $entries[-1]), $test, [StringComparison]::Ordinal)
        $flag = if ($intent.Count) { $intent[0] } else { "quarantine-request: test=$test" }
        if ($eligible -and -not $intent.Count) {
            $body += "`n$flag`nSecond failure in a row: one draft fixes-main quarantine, 24 h expiry, refused unless " +
                "MAIN_RED_QUARANTINE is on: ``pwsh -NoProfile -File quality/quarantine-pr.ps1 -RunId $RunId -TestName $test -Repository $Repository -Hours 24``"
        }
        $completed = "quarantine-completed: test=$test"
        # A refusal leaves the streak's one intent pending for a later eligible attempt. A completion line inside
        # an entry belongs to that entry's streak; a standalone completion comment is placed by its own metadata.
        $generate = $eligible -and [string]::Equals($flag, "quarantine-request: test=$test", [StringComparison]::Ordinal) -and
            [Array]::IndexOf([string[]]$lines, $completed) -lt 0 -and -not (Test-QuarantineCompleted $issue $comments $test $Recovery)
    }
    $title = if ($postMergeOnly) { "Main red, post-merge tier only ($sha)" } else { "Main red: CI failing on main ($sha)" }
    if ($issue) {
        # A blocking failure must not stay filed under a title that says it never blocks.
        if (-not $postMergeOnly -and $issue.title -clike 'Main red, post-merge tier only (*)') {
            Invoke-Gh issue edit $issue.number --repo $Repository --title $title | Out-Null
        }
        Invoke-Gh issue comment $issue.number --repo $Repository --body $body | Out-Null
    } else {
        Invoke-Gh label create main-red --repo $Repository --color B60205 --description 'CI is red on main' --force | Out-Null
        Invoke-Gh issue create --repo $Repository --title $title --label main-red --body $body | Out-Null
    }
    # The intent is stored first: a failed write must not leave a generator run that no later tick can see.
    # Completion is its own write, so a failure there only retries the generator on a later attempt. It names
    # the requesting run and attempt so it never suppresses a later streak after a recovery (#3268).
    if ($generate -and (Request-Quarantine $RunId $test $Repository)) {
        Invoke-Gh issue comment $issue.number --repo $Repository --body "$completed run=$RunId attempt=$($run.run_attempt)" | Out-Null
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
Invoke-MainRed -RunId $RunId -Repository $Repository
