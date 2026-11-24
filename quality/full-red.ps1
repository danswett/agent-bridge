# Opens one issue per commit when the FULL lane (full.yml) has a red job: the alert half of the two-lane split (S2, #4104,
# quality/README.md section 4.7). A red FULL lane never holds a merge; this is how someone hears of it.
#
#   pwsh quality/full-red.ps1 -RunId <the Full run's id> -Repository owner/name
#
# Reads the finished Full run and its latest attempt's jobs, and for a run on main with a failed or timed-out job it opens an issue
# labelled full-red, or updates the commit's issue (matched by the marker <!-- full-red:<sha> --> in its body; a closed one is reopened
# with a comment), so a commit never has two. It lists each red job with its link and says which are:
#   Blocking  the release train will not publish this commit (x64 anything, the Arm64 pack, any other job);
#   Waived    the Arm64 self-test, tests and installer (owner, #2454): recorded here, publish continues.
# The aggregates (trigger, full-gate) are never a cause; a red full-gate with no failed job behind it (a job was skipped or cancelled) is
# reported as its own blocking line. Cancelled runs, other branches and runs with no red job are passed over. No log is read, so no
# log text reaches an issue. Needs issues: write and actions: read (the workflow's token).
[CmdletBinding()]
param(
    [Parameter(Mandatory)][long]$RunId,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$GhCommand = 'gh',
    [string]$SummaryFile = $env:GITHUB_STEP_SUMMARY
)
$ErrorActionPreference = 'Stop'
$Label = 'full-red'
# The Arm64 results the owner waived: what RUNS the app on Arm64. Its pack is not here: the package is always required.
$Waived = 'app (win-arm64)', 'app-selftest (win-arm64)', 'app-installer (win-arm64)', 'product (windows-11-arm)'
$Aggregates = 'trigger', 'full-gate'

function Invoke-Gh {
    $global:LASTEXITCODE = 0   # unset until a native program runs, and a stub script never sets it
    $out = & $GhCommand @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed: $((($out | Out-String).Trim()))" }
    ($out | Out-String)
}

$run = Invoke-Gh api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json
if ($run.status -ne 'completed' -or $run.conclusion -in 'cancelled', 'skipped' -or $run.head_branch -ne 'main') {
    "Passed over: run $RunId is $($run.status)/$($run.conclusion) on $($run.head_branch)."
    return
}
$sha = [string]$run.head_sha
if ($sha -notmatch '^[0-9a-f]{40}$') { throw "run $RunId has no commit sha: '$sha'" }

$pages = Invoke-Gh api "repos/$Repository/actions/runs/$RunId/attempts/$($run.run_attempt)/jobs?per_page=100" --paginate --slurp | ConvertFrom-Json
$red = @($pages | ForEach-Object { $_.jobs } | Where-Object { $_.conclusion -in 'failure', 'timed_out' })
$causes = @($red | Where-Object { $_.name -notin $Aggregates })
$blocking = @($causes | Where-Object { $_.name -notin $Waived })
$waived = @($causes | Where-Object { $_.name -in $Waived })
$gateOnly = $causes.Count -eq 0 -and @($red | Where-Object { $_.name -eq 'full-gate' }).Count -gt 0
if ($causes.Count -eq 0 -and -not $gateOnly) { "No red job in run $RunId."; return }

$short = $sha.Substring(0, 7)
$nl = [Environment]::NewLine
$lines = @("<!-- full-red:$sha -->",
    "The FULL lane (full.yml) is red for commit [$short](https://github.com/$Repository/commit/$sha): [run $RunId]($($run.html_url)), attempt $($run.run_attempt). A red FULL lane never holds a merge.", '')
if ($blocking.Count -gt 0 -or $gateOnly) {
    $lines += '**Blocking: the release train will not publish this commit**'
    $lines += @($blocking | ForEach-Object { "- [$($_.name)]($($_.html_url)): $($_.conclusion)" })
    if ($gateOnly) { $lines += '- full-gate failed with no failed job: a job it needs was skipped or cancelled' }
    $lines += ''
}
if ($waived.Count -gt 0) {
    $lines += '**Waived: the Arm64 self-test, tests and installer (owner, #2454). Recorded here, and publish continues**'
    $lines += @($waived | ForEach-Object { "- [$($_.name)]($($_.html_url)): $($_.conclusion)" })
}
$body = ($lines -join $nl).TrimEnd()
$names = (@($blocking | ForEach-Object { $_.name }) + $(if ($gateOnly) { 'full-gate' })) -join ', '
$title = if ($blocking.Count -gt 0 -or $gateOnly) { "FULL lane red on ${short}: $names" } else { "FULL lane Arm64 results waived on ${short}: $(@($waived | ForEach-Object { $_.name }) -join ', ')" }
if ($title.Length -gt 120) { $title = $title.Substring(0, 117) + '...' }

# The commit's issue, by its marker, among the label's issues, open or closed.
$known = @(Invoke-Gh api "repos/$Repository/issues?labels=$Label&state=all&per_page=100" --paginate --slurp | ConvertFrom-Json | ForEach-Object { $_ })
$issue = $known | Where-Object { $_.body -and $_.body.Contains("<!-- full-red:$sha -->") } | Select-Object -First 1
if ($issue) {
    $edit = @('-f', "title=$title", '-f', "body=$body")
    if ($issue.state -eq 'closed') { $edit += @('-f', 'state=open') }
    Invoke-Gh api -X PATCH "repos/$Repository/issues/$($issue.number)" @edit | Out-Null
    if ($issue.state -eq 'closed') {
        Invoke-Gh api -X POST "repos/$Repository/issues/$($issue.number)/comments" -f "body=Red again in [run $RunId]($($run.html_url)), attempt $($run.run_attempt)." | Out-Null
    }
    "Updated issue #$($issue.number) for $short."
}
else {
    # The label first; GitHub answers 422 when it exists, which is the usual case and no failure.
    try { Invoke-Gh api -X POST "repos/$Repository/labels" -f "name=$Label" -f color=B60205 -f 'description=The FULL lane (full.yml) is red for a commit' | Out-Null } catch { }
    Invoke-Gh api -X POST "repos/$Repository/issues" -f "title=$title" -f "body=$body" -f "labels[]=$Label" | Out-Null
    "Opened an issue for $short."
}
if ($SummaryFile) { Add-Content -Path $SummaryFile -Value $body }
