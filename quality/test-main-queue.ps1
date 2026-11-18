$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/main-queue.ps1"
$run = [pscustomobject]@{ id = 7; run_started_at = '2026-11-18T07:00:00Z' }
$jobs = @(
    [pscustomobject]@{ name = 'plan'; run_attempt = 1; started_at = '2026-11-18T07:02:30Z'; completed_at = '2026-11-18T07:03:00Z' },
    [pscustomobject]@{ name = 'quality-gate'; run_attempt = 1; started_at = '2026-11-18T07:10:00Z'; completed_at = '2026-11-18T07:12:00Z' },
    [pscustomobject]@{ name = 'plan'; run_attempt = 2; started_at = '2026-11-18T08:00:00Z'; completed_at = '2026-11-18T08:10:00Z' })
$row = Get-MainQueueMeasurement $run $jobs
if ($row.waitMinutes -ne 2.5 -or $row.totalMinutes -ne 12 -or
    $row.planStart -ne [datetimeoffset]'2026-11-18T07:02:30Z') { throw 'Attempt-one queue and total minutes differ' }
$row = Get-MainQueueMeasurement $run @($jobs[1])
if ($null -ne $row.waitMinutes -or $null -ne $row.planStart) { throw 'Missing plan must remain unknown' }
$running = [pscustomobject]@{ name = 'app'; run_attempt = 1; started_at = '2026-11-18T07:03:00Z'; completed_at = $null }
$row = Get-MainQueueMeasurement $run @($jobs[0], $running)
if ($null -ne $row.totalMinutes) { throw 'An unfinished attempt must not report a finished total' }
'main queue canned-data checks passed'
