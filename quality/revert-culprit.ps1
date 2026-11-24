#Requires -Version 7
# F5 guardian C4 (#3211): once a real test has kept main red for 15 minutes, open ONE revert PR for the single
# culprit merge, or ONE issue when the culprit is unknown or ambiguous. Red is red-minutes.ps1's rule; the failed
# tests come from the main-red issue (main-red.ps1); the culprit needs one test failing in every red run since the
# first. A failure that names no test (infra, setup or build) and a
# test with an open `quarantined` issue never trigger a revert. It pushes only its own revert-culprit/<sha>
# branch, never main, and never arms or merges: Quality gates and lands the revert like any other PR.
#
#   pwsh quality/revert-culprit.ps1 -Repository danswett/agent-bridge
#
# Needs a checkout of the repository with enough main history to hold the culprit (when it holds too little, one issue
# says so), and GH_TOKEN with contents,
# pull-requests and issues write; the push authenticates from GH_TOKEN. State lives only on GitHub: the branch, PR and issue.
[CmdletBinding()]
param([string]$Repository = $env:GITHUB_REPOSITORY, [int]$Minutes = 15, [datetime]$Now = [datetime]::UtcNow)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'main-red.ps1') -Repository $Repository
$script:GitCommand = 'git'
$script:Bots = @('github-actions[bot]', 'app/github-actions', 'github-actions')

function Invoke-Git {
    $global:LASTEXITCODE = 0
    $out = & $script:GitCommand @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE" }
    $out
}

# A null value must remove the variable: PowerShell turns a plain $null argument into an empty string, which stays set.
function Set-ProcessEnv([string]$Name, $Value) {
    if ($null -eq $Value) { [Environment]::SetEnvironmentVariable($Name, [NullString]::Value) }
    else { [Environment]::SetEnvironmentVariable($Name, [string]$Value) }
}

# The checkout keeps no credentials (persist-credentials: false), so the push authenticates itself from GH_TOKEN.
# The header reaches only this one git process through GIT_CONFIG_* variables: never argv, a config file, the
# remote URL or any message, and git's output is masked before it is reported.
function Invoke-GitPush([string]$Branch) {
    $token = $env:GH_TOKEN
    if (-not $token) { throw 'GH_TOKEN is not set, so the revert branch cannot be pushed.' }
    $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$token"))
    # Appended after the caller's own GIT_CONFIG_* entries. extraheader is a list, so an empty entry first drops any
    # inherited Authorization header, and Trace2 must not log config or env values for this one process.
    $n = 0; $null = [int]::TryParse("$env:GIT_CONFIG_COUNT", [ref]$n)
    $key = 'http.https://github.com/.extraheader'
    $set = [ordered]@{ GIT_CONFIG_COUNT = "$($n + 2)"; "GIT_CONFIG_KEY_$n" = $key; "GIT_CONFIG_VALUE_$n" = ''
        "GIT_CONFIG_KEY_$($n + 1)" = $key; "GIT_CONFIG_VALUE_$($n + 1)" = "AUTHORIZATION: basic $basic"
        GIT_TRACE2_CONFIG_PARAMS = $null; GIT_TRACE2_ENV_VARS = $null }
    $saved = @{}; foreach ($k in $set.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k) }
    try {
        foreach ($k in $set.Keys) { Set-ProcessEnv $k $set[$k] }
        $global:LASTEXITCODE = 0
        $out = & $script:GitCommand push --quiet origin "HEAD:refs/heads/$Branch" 2>&1
        $code = $LASTEXITCODE
    } catch { $out = $_.Exception.Message; $code = -1 }
    finally { foreach ($k in $saved.Keys) { Set-ProcessEnv $k $saved[$k] } }
    if ($code -ne 0) {
        throw ("git push failed with exit code ${code}: " + (@($out | Select-Object -Last 3) -join ' ')).Replace($basic, '***').Replace($token, '***')
    }
}

# Every main-red entry (issue body or bot comment): its marker line and the tests it lists.
function Get-MainRedEntries($Issue, [string]$Repository) {
    $comments = @((Invoke-Gh issue view $Issue.number --repo $Repository --json 'comments' | ConvertFrom-Json).comments |
            Where-Object { $_.author.login -in $script:Bots } | ForEach-Object { $_.body })
    foreach ($text in @(@($Issue.body) + $comments | Where-Object { "$_".StartsWith('main-red: run=') })) {
        $lines = $text -split '\r?\n'
        $tests = @($lines | Where-Object { $_ -match '^  \S' } | ForEach-Object { $_.Trim() } |
                Where-Object { $_ -notin @('no test name was found', 'log was unreadable') })
        [pscustomobject]@{ Marker = $lines[0]; Tests = $tests }
    }
}

function Invoke-RevertCulprit([string]$Repository, [int]$Minutes, [datetime]$Now) {
    if ($Repository -notmatch '^[\w.-]+/[\w.-]+$') { throw 'Give owner/repository.' }
    $Now = $Now.ToUniversalTime()
    $runs = @((Invoke-Gh api "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&status=completed&per_page=100" |
            ConvertFrom-Json).workflow_runs | Where-Object { $_.conclusion -in @('success', 'failure', 'timed_out', 'startup_failure') } |
            Sort-Object { ([datetime]$_.updated_at).ToUniversalTime() })
    if ($runs.Count -eq 0 -or $runs[-1].conclusion -eq 'success') { return 'main is not red' }
    $file = New-TemporaryFile
    try {
        $runs | Select-Object conclusion, updated_at, head_sha, html_url | ConvertTo-Json -AsArray | Set-Content $file
        $spell = @((& (Join-Path $PSScriptRoot 'red-minutes.ps1') -RunsFile $file -Now $Now -Json | ConvertFrom-Json).spell_list)[-1]
    } finally { Remove-Item $file }
    $redFor = ($Now - ([datetime]$spell.from).ToUniversalTime()).TotalMinutes
    if ($redFor -lt $Minutes) { return "red for $([math]::Floor($redFor)) of $Minutes minutes" }

    $newest = $runs[-1]
    $green = @($runs | Where-Object { $_.conclusion -eq 'success' })
    $firstRed = if ($green.Count) { $runs[[array]::IndexOf($runs, $green[-1]) + 1] } else { $runs[0] }
    $issue = Get-MainRedIssue $Repository
    $entries = @(if ($issue) { Get-MainRedEntries $issue $Repository })
    # Act only on evidence for main's newest finished run; main-red.ps1 catches up on its next run.
    $latest = @($entries | Where-Object { $_.Marker.StartsWith("main-red: run=$($newest.id) attempt=$($newest.run_attempt) ") })
    if ($latest.Count -eq 0) { return 'no main-red entry for the newest run' }
    $quarantined = @(Invoke-Gh issue list --repo $Repository --label quarantined --state open --limit 200 --json 'number,title,body,author' | ConvertFrom-Json)
    $real = @($latest[-1].Tests | Where-Object { $null -eq (Find-FlakyIssue $_ $quarantined) })
    if ($real.Count -eq 0) { return 'no failed test that is neither infra nor quarantined' }

    $redSha = $firstRed.head_sha
    $short = $redSha.Substring(0, 12)
    $branch = "revert-culprit/$short"
    $marker = "revert-culprit: first-red=$redSha"
    $mainRed = "https://github.com/$Repository/issues/$($issue.number)"
    # One shot per red spell (one main-red issue): a PR or an issue from any earlier tick ends it, whichever path
    # this tick would take, even after a re-run of the first red run moves the spell's start to another commit.
    $link = "$([regex]::Escape($mainRed))(?!\d)"
    $mine = { $_.author.login -in $script:Bots -and ($_.body -match $link -or ($_.body -split '\r?\n') -ccontains $marker) }
    if (@(Invoke-Gh pr list --repo $Repository --head $branch --state all --limit 1 --json 'number' | ConvertFrom-Json).Count -or
        @(Invoke-Gh pr list --repo $Repository --label revert-culprit --state all --limit 100 --json 'body,author' | ConvertFrom-Json |
                Where-Object -FilterScript $mine).Count) { return 'revert PR already exists' }
    $existing = @(Invoke-Gh issue list --repo $Repository --label revert-culprit --state all --limit 100 --json 'number,body,author' | ConvertFrom-Json |
            Where-Object -FilterScript $mine)
    if ($existing.Count) { return 'culprit issue already exists' }
    if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is not set, so no revert branch can be pushed; nothing was opened.' }
    # The first red commit is the culprit only if one test failed in the current attempt of EVERY red run since;
    # a test that passed in between (or a run with no entry) proves nothing about that commit.
    $common = $real; $missing = $null
    foreach ($run in @($runs[[array]::IndexOf($runs, $firstRed)..($runs.Count - 1)])) {
        $entry = @($entries | Where-Object { $_.Marker.StartsWith("main-red: run=$($run.id) attempt=$($run.run_attempt) ") })
        if ($entry.Count -eq 0) { $missing = $run.id; break }
        $common = @($common | Where-Object { $_ -cin $entry[-1].Tests })
    }
    $why = $null; $pr = $null
    if ($green.Count -eq 0) { $why = 'no green main run precedes the red spell' }
    elseif ($missing) { $why = "run $missing has no main-red entry, so the culprit is unknown" }
    elseif ($common.Count -eq 0) { $why = 'no failing test has failed in every red run since the first' }
    else {
        $commits = @((Invoke-Gh api "repos/$Repository/compare/$($green[-1].head_sha)...$redSha" | ConvertFrom-Json).commits)
        if ($commits.Count -ne 1 -or @($commits[0].parents).Count -ne 1) { $why = "$($commits.Count) commits (or a merge commit) landed between the last green run and the first red run" }
        else {
            $prs = @(Invoke-Gh api "repos/$Repository/commits/$redSha/pulls" | ConvertFrom-Json |
                    Where-Object { $_.merged_at -and $_.base.ref -eq 'main' -and $_.merge_commit_sha -eq $redSha })
            if ($prs.Count -eq 1) { $pr = $prs[0] } else { $why = "the culprit commit has $($prs.Count) merged pull requests" }
        }
    }
    if (-not $why) {
        Invoke-Git fetch --quiet origin '+refs/heads/main:refs/remotes/origin/main' | Out-Null
        # A shallow checkout makes the culprit a root commit, and reverting a root deletes the whole tree.
        $parent = try { Invoke-Git rev-parse --verify --quiet "$redSha^1" } catch { $global:LASTEXITCODE = 0 }
        if ("$parent" -ne $green[-1].head_sha) { $why = "the culprit's parent is not in the local history, so the checkout holds too little of main to revert it" }
        else {
            $global:LASTEXITCODE = 0; & $script:GitCommand ls-remote --exit-code --heads origin $branch | Out-Null
            if ($LASTEXITCODE -eq 0) { $why = "branch $branch exists without a pull request" }
        }
    }
    if (-not $why) {
        Invoke-Git switch --quiet -C $branch origin/main | Out-Null
        try { Invoke-Git -c 'user.name=github-actions[bot]' -c 'user.email=41898282+github-actions[bot]@users.noreply.github.com' revert --no-edit $redSha | Out-Null }
        catch {
            $why = 'the revert does not apply cleanly to main'
            try { Invoke-Git revert --abort | Out-Null } catch { $global:LASTEXITCODE = 0 }
        }
    }
    $tests = (@(if ($common.Count) { $common } else { $real }) | Select-Object -First 10 | ForEach-Object { "- ``$_``" }) -join "`n"
    # Both artifacts carry the label so a later tick finds either one.
    Invoke-Gh label create revert-culprit --repo $Repository --color B60205 --description 'Main red: an automatic revert or why none was opened' --force | Out-Null
    if (-not $why) {
        try { Invoke-GitPush $branch } catch { $why = "the revert branch could not be pushed ($($_.Exception.Message))" }
    }
    if (-not $why) {
        $body = "Reverts $($pr.html_url) (commit $short), the single culprit of main red: $mainRed`n`n" +
            "Main has been red for $([math]::Floor($redFor)) minutes with these failed tests:`n$tests`n`n" +
            "Opened by quality/revert-culprit.ps1, which never arms or merges it; Quality gates and lands it.`n`n" +
            "Builder: quality/revert-culprit.ps1 (F5 guardian C4, #3211)`nAuthor-model: none (deterministic script)"
        Invoke-Gh pr create --repo $Repository --base main --head $branch --label revert-culprit --title "Revert #$($pr.number): culprit of main red" --body $body | Out-Null
        return "opened a revert PR for #$($pr.number)"
    }
    $body = "$marker`nMain red: $mainRed`n`nNo revert PR was opened: $why. Failed tests:`n$tests"
    Invoke-Gh issue create --repo $Repository --title "Main red: no single culprit to revert ($short)" --label revert-culprit --body $body | Out-Null
    "opened an issue: $why"
}

if ($MyInvocation.InvocationName -eq '.') { return }
Invoke-RevertCulprit -Repository $Repository -Minutes $Minutes -Now $Now
