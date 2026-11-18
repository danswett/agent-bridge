[CmdletBinding()]
param(
    [ValidatePattern('^[\w.-]+/[\w.-]+$')][string]$Repo = 'danswett/agent-bridge',
    [ValidateRange(1,100)][int]$Count = 10,
    [switch]$Json
)
$ErrorActionPreference = 'Stop'

function Get-MainQueueMeasurement($Run, $Jobs) {
    if (-not $Run.run_started_at) { throw 'Attempt one has no run_started_at' }
    $start = [datetimeoffset]$Run.run_started_at
    $first = @($Jobs | Where-Object { $_.run_attempt -eq 1 })
    $plan = $first | Where-Object { $_.name -eq 'plan' -and $_.started_at } | Select-Object -First 1
    $planStart = if ($plan) { [datetimeoffset]$plan.started_at } else { $null }
    $end = if ($first.Count -gt 0 -and @($first | Where-Object { -not $_.completed_at }).Count -eq 0) {
        $first | ForEach-Object { [datetimeoffset]$_.completed_at } | Sort-Object -Descending | Select-Object -First 1
    } else { $null }
    [pscustomobject]@{
        run = $Run.id; runStart = $start; planStart = $planStart
        waitMinutes = if ($planStart) { [math]::Round(($planStart - $start).TotalMinutes, 3) } else { $null }
        totalMinutes = if ($end) { [math]::Round(($end - $start).TotalMinutes, 3) } else { $null }
    }
}

function Read-MainQueueApi([string]$Path) {
    $body = & gh api $Path
    if ($LASTEXITCODE -ne 0) { throw "GitHub queue measurement read failed: $Path" }
    $body | ConvertFrom-Json
}

if ($MyInvocation.InvocationName -eq '.') { return }
$runs = (Read-MainQueueApi "repos/$Repo/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=$Count").workflow_runs
$rows = @(foreach ($run in $runs) {
    # A rerun's list entry has different timestamps: always read attempt one's metadata and jobs.
    $attempt = Read-MainQueueApi "repos/$Repo/actions/runs/$($run.id)/attempts/1"
    $jobs = @()
    $page = 1
    do {
        $batch = (Read-MainQueueApi "repos/$Repo/actions/runs/$($run.id)/attempts/1/jobs?per_page=100&page=$page").jobs
        $jobs += @($batch)
        $page++
    } while (@($batch).Count -eq 100)
    Get-MainQueueMeasurement $attempt $jobs
})
if ($Json) { ConvertTo-Json -InputObject $rows -Depth 4 }
else {
    $pacific = [TimeZoneInfo]::FindSystemTimeZoneById('America/Los_Angeles')
    $rows | Format-Table run,
        @{ Label = 'Run start (PT)'; Expression = { [TimeZoneInfo]::ConvertTime($_.runStart, $pacific).ToString('MM-dd HH:mm:ss') } },
        @{ Label = 'Plan start (PT)'; Expression = { if ($_.planStart) { [TimeZoneInfo]::ConvertTime($_.planStart, $pacific).ToString('MM-dd HH:mm:ss') } } },
        waitMinutes, totalMinutes -AutoSize
}
