# The alert half of the two-lane split (S2, #4104, quality/README.md section 4.7): one open issue per red FULL job, with a comment for each later commit it is red on.
# A red FULL lane never holds a merge; this is how someone hears of it.
#
#   pwsh quality/full-red.ps1 -RunId <the Full run's id> -Repository owner/name
#
# Reads a finished Full run of THIS repository on main and its latest attempt's jobs. A fork's run, a branch that is not main, and an event Full does not
# have (it runs after a CI run, or by hand: workflow_run or workflow_dispatch) are passed over, so nothing from outside can open an issue. For each red
# job (failure or timed out) it keeps ONE open issue, found by the hidden marker <!-- full-red-job:<name> --> in its body (never by label, which Quality may
# change), opening it on the first red commit and adding a comment on each later red commit (a re-run of the same commit adds nothing). When the job passes
# in a later run it comments once that it is green again and leaves the closing to a person. Each issue says whether the job is:
#   Blocking  the release train will not publish this commit (x64 anything, the Arm64 pack, any other job);
#   Waived    the Arm64 self-test, tests and installer (owner, #2454): recorded here, publish continues.
# The aggregates (trigger, full-gate) are never a cause. A run that failed with no failed job behind it (a startup failure, or a job that was skipped or
# cancelled) is red as "(whole run)". Names are cut down to a safe alphabet before they reach an issue, no log is read, and the only label is full-red.
# Only issues opened by the workflow's own token (github-actions[bot]) are found by that marker. Needs issues: write and actions: read (the workflow's token). Any API error fails the run; only "the label already exists" (422) is let through.
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
$WholeRun = '(whole run)'

function Invoke-Gh {
    $global:LASTEXITCODE = 0   # unset until a native program runs, and a stub script never sets it
    $out = & $GhCommand @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed: $((($out | Out-String).Trim()))" }
    ($out | Out-String)
}
# A name from a workflow file, cut to a safe alphabet and length: nothing in it can mention a person, open a tag or a link, or end a comment.
function Get-Safe([string]$name) { $s = $name -replace '[^A-Za-z0-9 ._()/-]', '?'; if ($s.Length -gt 80) { $s.Substring(0, 80) } else { $s } }

$run = Invoke-Gh api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json
$own = $run.head_repository -and $run.head_repository.full_name -ceq $Repository
if ($run.status -ne 'completed' -or $run.conclusion -in 'cancelled', 'skipped' -or -not $own -or $run.head_branch -cne 'main' -or $run.event -cnotin 'workflow_run', 'workflow_dispatch') {
    "Passed over: run $RunId is $($run.status)/$($run.conclusion), event $($run.event), on $($run.head_branch) of $($run.head_repository.full_name)."
    return
}
$sha = [string]$run.head_sha
if ($sha -notmatch '^[0-9a-f]{40}$') { throw "run $RunId has no commit sha: '$sha'" }
$short = $sha.Substring(0, 7)

$pages = Invoke-Gh api "repos/$Repository/actions/runs/$RunId/attempts/$($run.run_attempt)/jobs?per_page=100" --paginate --slurp | ConvertFrom-Json
$jobs = @($pages | ForEach-Object { $_.jobs })
$red = @($jobs | Where-Object { $_.conclusion -in 'failure', 'timed_out' -and $_.name -notin $Aggregates })
$causes = @($red | ForEach-Object { [pscustomobject]@{ Key = Get-Safe $_.name; Waived = $_.name -in $Waived; Url = $_.html_url; Result = $_.conclusion; Note = '' } })
if ($causes.Count -eq 0 -and $run.conclusion -in 'failure', 'timed_out', 'startup_failure') {
    $causes = @([pscustomobject]@{ Key = $WholeRun; Waived = $false; Url = $run.html_url; Result = $run.conclusion; Note = 'The run failed with no failed job behind it: a startup failure, or a job it needs was skipped or cancelled.' })
}
$green = @($jobs | Where-Object { $_.conclusion -eq 'success' } | ForEach-Object { Get-Safe $_.name })

# The open issues carrying a job marker, whatever their labels, not pull requests, and opened BY THE WORKFLOW'S OWN TOKEN (github-actions[bot], type Bot): a person's issue that happens to carry the marker
# is never found, so its body is never rewritten and it is never commented on (a new issue is opened instead).
$botLogin = 'github-actions[bot]'
$marker = [regex]'<!-- full-red-job:([^>]*?) -->'
$open = @(Invoke-Gh api "repos/$Repository/issues?state=open&per_page=100" --paginate --slurp | ConvertFrom-Json | ForEach-Object { $_ } |
    Where-Object { $_.body -and -not $_.PSObject.Properties['pull_request'] -and $_.PSObject.Properties['user'] -and $_.user -and $_.user.login -ceq $botLogin -and $_.user.type -ceq 'Bot' -and $marker.IsMatch($_.body) })
function Get-Issue([string]$key) { $open | Where-Object { $marker.Match($_.body).Groups[1].Value -ceq $key } | Select-Object -First 1 }
$summary = @()
$nl = [Environment]::NewLine
function New-Body($cause, [string]$state) {
    $how = if ($cause.Waived) { 'Waived: the Arm64 self-test, tests and installer (owner, #2454). Recorded here, and publish continues.' } else { 'Blocking: the release train will not publish a commit this is red on.' }
    $lines = @("<!-- full-red-job:$($cause.Key) -->", "<!-- full-red-last:$sha -->", "<!-- full-red-state:$state -->",
        "FULL lane job **$($cause.Key)** is red: [$($cause.Result)]($($cause.Url)) on commit [$short](https://github.com/$Repository/commit/$sha), run [$RunId]($($run.html_url)).", '', "**$how**")
    if ($cause.Note) { $lines += @('', $cause.Note) }
    $lines += @('', 'A red FULL lane never holds a merge. Each later commit it is red on is a comment here; close this when the cause is fixed.')
    ($lines -join $nl)
}
$labelReady = $false
foreach ($cause in $causes) {
    $issue = Get-Issue $cause.Key
    if ($issue) {
        if ($issue.body.Contains("<!-- full-red-last:$sha -->")) { $summary += "$($cause.Key): already recorded for $short."; continue }
        Invoke-Gh api -X PATCH "repos/$Repository/issues/$($issue.number)" -f "body=$(New-Body $cause 'red')" | Out-Null
        Invoke-Gh api -X POST "repos/$Repository/issues/$($issue.number)/comments" -f "body=Red again on [$short](https://github.com/$Repository/commit/$sha): [$($cause.Result)]($($cause.Url)), run [$RunId]($($run.html_url))." | Out-Null
        $summary += "$($cause.Key): commented on #$($issue.number)."
        continue
    }
    if (-not $labelReady) {
        # The label first; GitHub answers 422 when it exists, which is the usual case. Any other error fails the run.
        try { Invoke-Gh api -X POST "repos/$Repository/labels" -f "name=$Label" -f color=B60205 -f 'description=The FULL lane (full.yml) is red for a job' | Out-Null }
        catch { if ($_.Exception.Message -notmatch '422|already_exists') { throw } }
        $labelReady = $true
    }
    $title = if ($cause.Waived) { "FULL lane Arm64 result red (waived): $($cause.Key)" } else { "FULL lane red: $($cause.Key)" }
    Invoke-Gh api -X POST "repos/$Repository/issues" -f "title=$title" -f "body=$(New-Body $cause 'red')" -f "labels[]=$Label" | Out-Null
    $summary += "$($cause.Key): opened an issue."
}
# A job that passed this time: one comment on its open issue that it is green again, and a person closes it.
foreach ($issue in $open) {
    $key = $marker.Match($issue.body).Groups[1].Value
    if ($key -cin $green -and $key -cnotin @($causes | ForEach-Object { $_.Key }) -and $issue.body.Contains('<!-- full-red-state:red -->')) {
        Invoke-Gh api -X PATCH "repos/$Repository/issues/$($issue.number)" -f ('body=' + $issue.body.Replace('<!-- full-red-state:red -->', '<!-- full-red-state:green -->')) | Out-Null
        Invoke-Gh api -X POST "repos/$Repository/issues/$($issue.number)/comments" -f "body=Green again on [$short](https://github.com/$Repository/commit/$sha), run [$RunId]($($run.html_url)). Close this if the cause is fixed." | Out-Null
        $summary += "${key}: green again, commented on #$($issue.number)."
    }
}
if (-not $summary) { $summary = @("No red job in run $RunId.") }
$summary
if ($SummaryFile) { Add-Content -Path $SummaryFile -Value ($summary -join $nl) }
