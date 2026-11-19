#Requires -Version 7
# F5 guardian C4 (#3211): once a real test has kept main red for 15 minutes, open ONE revert PR for the single
# culprit merge, or ONE issue when the culprit is unknown or ambiguous. Red is red-minutes.ps1's rule; the failed
# tests come from the main-red issue (main-red.ps1). A failure that names no test (infra, setup or build) and a
# test with an open `quarantined` issue never trigger a revert. It pushes only its own revert-culprit/<sha>
# branch, never main, and never arms or merges: Quality gates and lands the revert like any other PR.
#
#   pwsh quality/revert-culprit.ps1 -Repository danswett/agent-bridge
#
# Needs a checkout of the repository (with enough main history to hold the culprit) that can push a branch,
# and GH_TOKEN with contents, pull-requests and issues write. State lives only on GitHub: the branch, PR and issue.
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
    # The commit that started the spell is the culprit only if the same test has failed since that first red run.
    $firstTests = @($entries | Where-Object { $_.Marker.StartsWith("main-red: run=$($firstRed.id) ") } | ForEach-Object { $_.Tests })

    $redSha = $firstRed.head_sha
    $short = $redSha.Substring(0, 12)
    $branch = "revert-culprit/$short"
    $why = $null; $pr = $null
    if ($green.Count -eq 0) { $why = 'no green main run precedes the red spell' }
    elseif (@($real | Where-Object { $_ -cin $firstTests }).Count -eq 0) { $why = 'no failing test has failed since the first red run' }
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
        if (@(Invoke-Gh pr list --repo $Repository --head $branch --state all --limit 1 --json 'number' | ConvertFrom-Json).Count) { return 'revert PR already exists' }
        Invoke-Git fetch --quiet origin '+refs/heads/main:refs/remotes/origin/main' | Out-Null
        # A shallow checkout makes the culprit a root commit, and reverting a root deletes the whole tree.
        $parent = try { Invoke-Git rev-parse --verify --quiet "$redSha^1" } catch { $global:LASTEXITCODE = 0 }
        if ("$parent" -ne $green[-1].head_sha) { throw "The culprit's parent is not in the local history; check out more of main." }
        $global:LASTEXITCODE = 0; & $script:GitCommand ls-remote --exit-code --heads origin $branch | Out-Null
        if ($LASTEXITCODE -eq 0) { $why = "branch $branch exists without a pull request" }
    }
    if (-not $why) {
        Invoke-Git switch --quiet -C $branch origin/main | Out-Null
        try { Invoke-Git -c 'user.name=github-actions[bot]' -c 'user.email=41898282+github-actions[bot]@users.noreply.github.com' revert --no-edit $redSha | Out-Null }
        catch {
            $why = 'the revert does not apply cleanly to main'
            try { Invoke-Git revert --abort | Out-Null } catch { $global:LASTEXITCODE = 0 }
        }
    }
    $mainRed = "https://github.com/$Repository/issues/$($issue.number)"
    $tests = ($real | Select-Object -First 10 | ForEach-Object { "- ``$_``" }) -join "`n"
    if (-not $why) {
        Invoke-Git push --quiet origin "HEAD:refs/heads/$branch" | Out-Null
        $body = "Reverts $($pr.html_url) (commit $short), the single culprit of main red: $mainRed`n`n" +
            "Main has been red for $([math]::Floor($redFor)) minutes with these failed tests:`n$tests`n`n" +
            "Opened by quality/revert-culprit.ps1, which never arms or merges it; Quality gates and lands it.`n`n" +
            "Builder: quality/revert-culprit.ps1 (F5 guardian C4, #3211)`nAuthor-model: none (deterministic script)"
        Invoke-Gh pr create --repo $Repository --base main --head $branch --title "Revert #$($pr.number): culprit of main red" --body $body | Out-Null
        return "opened a revert PR for #$($pr.number)"
    }
    $marker = "revert-culprit: first-red=$redSha"
    $existing = @(Invoke-Gh issue list --repo $Repository --label revert-culprit --state all --limit 100 --json 'number,body,author' | ConvertFrom-Json |
            Where-Object { $_.author.login -in $script:Bots -and ($_.body -split '\r?\n') -ccontains $marker })
    if ($existing.Count) { return 'culprit issue already exists' }
    Invoke-Gh label create revert-culprit --repo $Repository --color B60205 --description 'Main red with no single revertable culprit' --force | Out-Null
    $body = "$marker`nMain red: $mainRed`n`nNo revert PR was opened: $why. Failed tests:`n$tests"
    Invoke-Gh issue create --repo $Repository --title "Main red: no single culprit to revert ($short)" --label revert-culprit --body $body | Out-Null
    "opened an issue: $why"
}

if ($MyInvocation.InvocationName -eq '.') { return }
Invoke-RevertCulprit -Repository $Repository -Minutes $Minutes -Now $Now
