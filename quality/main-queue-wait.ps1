param(
    [ValidateRange(1, 100)][int]$Limit = 10,
    [string]$Repository = 'danswett/agent-bridge',
    [string]$RunsFile,
    [switch]$Json
)
$ErrorActionPreference = 'Stop'
function Read-Api([string]$Path, [switch]$Pages) {
    $options = if ($Pages) { @('--paginate', '--slurp') } else { @() }
    $response = gh api $Path @options
    if ($LASTEXITCODE) { throw "GitHub read failed: $Path" }
    ($response -join "`n") | ConvertFrom-Json -AsHashtable
}
$snapshot = if ($RunsFile) {
    Get-Content -LiteralPath $RunsFile -Raw | ConvertFrom-Json -AsHashtable
} else {
    Read-Api "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=$Limit"
}
if ($snapshot['workflow_runs'] -isnot [array]) { throw 'Expected a workflow_runs array.' }
$runs = $snapshot['workflow_runs']
$rows = @(foreach ($run in ($runs | Where-Object { $_['event'] -eq 'push' -and $_['head_branch'] -eq 'main' } | Select-Object -First $Limit)) {
    # A rerun's list entry has overwritten attempt-one timestamps; read the original attempt.
    $attempt = if ($RunsFile) {
        if ($run['run_attempt'] -eq 1) { $run } else { $run['attempt1'] }
    } else { Read-Api "repos/$Repository/actions/runs/$($run['id'])/attempts/1" }
    if (-not $attempt['run_started_at']) { throw 'Attempt-one run start is missing.' }
    $jobs = if ($RunsFile) { $run['jobs'] } else {
        @(Read-Api "repos/$Repository/actions/runs/$($run['id'])/attempts/1/jobs?per_page=100" -Pages |
            ForEach-Object { $_['jobs'] })
    }
    $start = [DateTimeOffset]$attempt['run_started_at']
    $plan = $jobs | Where-Object { $_['name'] -eq 'plan' -and $_['started_at'] -and $_['conclusion'] -ne 'skipped' } | Select-Object -First 1
    $end = $jobs | Where-Object { $_['completed_at'] } |
        ForEach-Object { [DateTimeOffset]$_['completed_at'] } | Sort-Object -Descending | Select-Object -First 1
    [pscustomobject]@{
        RunId = $run['id']; RunStart = $start.ToString('o')
        PlanStart = if ($plan) { ([DateTimeOffset]$plan['started_at']).ToString('o') } else { $null }
        WaitMinutes = if ($plan) { ([DateTimeOffset]$plan['started_at'] - $start).TotalMinutes } else { $null }
        TotalMinutes = if ($attempt['status'] -eq 'completed' -and $end) { ($end - $start).TotalMinutes } else { $null }
    }
})
if ($Json) { ConvertTo-Json -InputObject $rows -Depth 4 }
else { $rows | Format-Table -AutoSize; 'Blank durations mean unavailable attempt-one evidence, not zero.' }
