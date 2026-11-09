# Re-runs the failed jobs of a red push run of ci.yml on main, once, when every test that failed
# is already filed as a flake (owner decision, 2026-11-08; quality/README.md §5). A real failure
# stays red: one failed test with no open `flaky` issue, a failed job with no test failure in
# its log (a build error, a hang, a timeout), a re-run already made (attempt 2 or later) or a
# newer push run on main all leave the run as it is.
#
# Each re-run is logged as a comment on every flake issue it concerned, one line per test in a
# fixed shape, so the flips of a test are countable from its issue:
#   rerun-known-flakes: test=<name> run=<id> attempt=1 job=<job>
#
#   pwsh quality/rerun-known-flakes.ps1 -RunId <id> -Repository danswett/agent-bridge [-DryRun]
#
# Run by .github/workflows/rerun-known-flakes.yml from main's copy, with GH_TOKEN the workflow's
# token (actions: write, issues: write). Nothing here checks out or runs the code of a run.
#Requires -Version 7
[CmdletBinding()]
param(
    [long]$RunId,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Whose issues count as a test's flake record, as in the nightly hunt (#542): the repository's
# owner and the hunt's own bot. The `flaky` label alone is not enough.
$script:TrustedAuthors = @('danswett', 'app/github-actions', 'github-actions', 'github-actions[bot]')
$script:GhCommand = 'gh'

function Invoke-Gh {
    $global:LASTEXITCODE = 0
    $out = & $script:GhCommand @args
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed with exit code $LASTEXITCODE" }
    $out
}

# The tests a job's log says failed: dotnet's `  Failed Class.Method(args) [n ms]` lines
# (Class.Method, without the arguments) and Playwright's `  1) [webkit] › file.spec.mjs:43:1 › title`
# (file:line). A log line may start with the runner's timestamp.
function Get-FailedTests([string]$Log) {
    $names = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($m in [regex]::Matches($Log, '(?m)^(?:\S+Z)?[ \t]*Failed[ \t]+(?<n>[A-Za-z_][\w+]*(?:\.[\w+]+)+)')) { [void]$names.Add($m.Groups['n'].Value) }
    foreach ($m in [regex]::Matches($Log, '(?m)^(?:\S+Z)?[ \t]*\d+\) \[[^\]\r\n]+\] › (?<f>[^\s:]+):(?<l>\d+)')) { [void]$names.Add("$($m.Groups['f'].Value):$($m.Groups['l'].Value)") }
    , [string[]]@($names)
}

# The open flake issue that names the test, or $null. The name must stand alone in the issue's
# title or body: Class.Method.Other and file:430 are not Class.Method and file:43.
function Find-FlakyIssue([string]$Test, $Issues) {
    $pattern = '(?<![\w.:])' + [regex]::Escape($Test) + '(?!\w|\.\w)'
    foreach ($issue in $Issues) {
        if ($issue.author.login -notin $script:TrustedAuthors) { continue }
        if ("$($issue.title)`n$($issue.body)" -match $pattern) { return $issue }
    }
    $null
}

# $FailedByJob maps a failed job's name to the tests its log shows failed. Re-run only when
# there is at least one failed job, every one has at least one failed test, and every failed
# test has a flake issue.
function Get-RerunDecision($FailedByJob, $Issues) {
    $matched = [System.Collections.Generic.List[object]]::new()
    $why = $null
    if ($FailedByJob.Count -eq 0) { $why = 'no failed job' }
    foreach ($job in $FailedByJob.Keys) {
        $tests = @($FailedByJob[$job])
        if ($tests.Count -eq 0) { $why = "job '$job' failed with no failed test in its log"; continue }
        foreach ($test in $tests) {
            $issue = Find-FlakyIssue $test $Issues
            if ($null -eq $issue) { $why = "'$test' (job '$job') has no open flake issue"; continue }
            $matched.Add([pscustomobject]@{ Job = $job; Test = $test; Issue = [int]$issue.number })
        }
    }
    [pscustomobject]@{ Rerun = ($null -eq $why); Reason = $why; Matched = @($matched) }
}

function Get-Pacific([datetime]$Utc) {
    try { [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId($Utc, 'America/Los_Angeles').ToString('yyyy-MM-dd HH:mm') + ' PT' }
    catch { $Utc.ToString('yyyy-MM-dd HH:mm') + ' UTC' }
}

function Invoke-RerunKnownFlakes([long]$RunId, [string]$Repository, [switch]$DryRun) {
    $run = Invoke-Gh api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json
    if ($run.path -ne '.github/workflows/ci.yml' -or $run.event -ne 'push' -or $run.head_branch -ne 'main' -or $run.head_repository.full_name -ne $Repository) {
        return [pscustomobject]@{ Rerun = $false; Reason = 'not a push run of ci.yml on main'; Matched = @() }
    }
    if ($run.conclusion -ne 'failure') { return [pscustomobject]@{ Rerun = $false; Reason = "the run ended $($run.conclusion), not failure"; Matched = @() } }
    if ($run.run_attempt -ne 1) { return [pscustomobject]@{ Rerun = $false; Reason = "attempt $($run.run_attempt): a run is re-run once"; Matched = @() } }

    $newer = (Invoke-Gh api "repos/$Repository/actions/runs?branch=main&event=push&per_page=10" | ConvertFrom-Json).workflow_runs |
        Where-Object { $_.path -eq '.github/workflows/ci.yml' -and $_.id -ne $RunId -and [datetime]$_.created_at -gt [datetime]$run.created_at }
    if (@($newer).Count -gt 0) { return [pscustomobject]@{ Rerun = $false; Reason = 'a newer push run of ci.yml exists on main'; Matched = @() } }

    $jobs = (Invoke-Gh api "repos/$Repository/actions/runs/$RunId/jobs?per_page=100" | ConvertFrom-Json).jobs
    if (@($jobs | Where-Object { $_.conclusion -eq 'timed_out' }).Count -gt 0) {
        return [pscustomobject]@{ Rerun = $false; Reason = 'a job timed out'; Matched = @() }
    }
    $failedByJob = [ordered]@{}
    foreach ($job in @($jobs | Where-Object { $_.conclusion -eq 'failure' })) {
        $log = (Invoke-Gh api "repos/$Repository/actions/jobs/$($job.id)/logs") -join "`n"
        $failedByJob[$job.name] = Get-FailedTests $log
    }
    $issues = Invoke-Gh issue list --repo $Repository --label flaky --state open --limit 200 --json 'number,title,body,author' | ConvertFrom-Json
    $decision = Get-RerunDecision $failedByJob $issues
    if (-not $decision.Rerun -or $DryRun) { return $decision }

    Invoke-Gh run rerun $RunId --repo $Repository --failed | Out-Null
    $when = Get-Pacific ([datetime]::UtcNow)
    foreach ($group in $decision.Matched | Group-Object Issue) {
        $lines = $group.Group | ForEach-Object { "rerun-known-flakes: test=$($_.Test) run=$RunId attempt=1 job=$($_.Job)" }
        $body = "Main's CI run $($run.html_url) (``$($run.head_sha.Substring(0, 8))``) failed on this known flake at $when. Its failed jobs were re-run once, automatically (quality/rerun-known-flakes.ps1). A real failure would have stayed red.`n`n" + ($lines -join "`n")
        Invoke-Gh issue comment $group.Name --repo $Repository --body $body | Out-Null
    }
    $decision
}

# Dot-sourced by the test, which calls the functions above.
if ($MyInvocation.InvocationName -eq '.') { return }

if (-not $RunId -or -not $Repository) { throw 'Give -RunId and -Repository (or run in Actions, where GITHUB_REPOSITORY is set).' }
$result = Invoke-RerunKnownFlakes -RunId $RunId -Repository $Repository -DryRun:$DryRun
$summary = if ($result.Rerun) {
    "Known flake: re-run the failed jobs of run $RunId once. " + (($result.Matched | ForEach-Object { "$($_.Test) (issue #$($_.Issue))" }) -join '; ') + $(if ($DryRun) { ' (dry run: nothing re-run)' } else { '' })
} else { "Left as it is: $($result.Reason)." }
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary }
