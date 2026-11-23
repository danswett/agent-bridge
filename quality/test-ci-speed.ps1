#Requires -Version 7
# Offline only: a fixed run list and its jobs; nothing reaches GitHub. The figures below are worked out by hand.
$ErrorActionPreference = 'Stop'
$script:failures = 0
$script:checks = 0
function Assert([bool]$Ok, [string]$Name) {
    $script:checks++
    if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ }
}
$work = Join-Path ([IO.Path]::GetTempPath()) ("ci-speed-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function At([string]$day, [string]$time) { "2026-11-$($day)T$($time):00Z" }
function Run([long]$id, [string]$status, [string]$conclusion, [string]$created, [string]$started, [string]$updated) {
    @{ id = $id; status = $status; conclusion = $conclusion; created_at = $created; run_started_at = $started; updated_at = $updated }
}
function Job([string]$name, [string]$created, [string]$started, $completed) {
    @{ name = $name; created_at = $created; started_at = $started; completed_at = $completed }
}

# A: success. product waits 3 min and 1 min; quality-gate ends 10:33; app (win-x64) is no gate job and ends last.
# B: red. product waited 10 min; quality-gate ended 11:16. C: cancelled, D: still running: both left out.
# E: success, the next Pacific day (09:00Z is 01:00 PT on Nov 24).
$runs = @(
    (Run 1 'completed' 'success' (At 23 '10:00') (At 23 '10:02') (At 23 '10:40')),
    (Run 2 'completed' 'failure' (At 23 '11:00') (At 23 '11:00') (At 23 '11:20')),
    (Run 3 'completed' 'cancelled' (At 23 '12:00') (At 23 '12:00') (At 23 '12:05')),
    (Run 4 'in_progress' $null (At 23 '13:00') (At 23 '13:00') (At 23 '13:03')),
    (Run 5 'completed' 'success' (At 24 '09:00') (At 24 '09:00') (At 24 '09:30')))
$jobs = @{
    '1' = @((Job 'product (ubuntu-latest)' (At 23 '10:02') (At 23 '10:05') (At 23 '10:25')), (Job 'product (windows-latest)' (At 23 '10:02') (At 23 '10:03') (At 23 '10:30')),
        (Job 'quality-gate' (At 23 '10:31') (At 23 '10:31') (At 23 '10:33')), (Job 'app (win-x64)' (At 23 '10:05') (At 23 '10:06') (At 23 '10:40')))
    '2' = @((Job 'product (ubuntu-latest)' (At 23 '11:00') (At 23 '11:10') (At 23 '11:15')), (Job 'quality-gate' (At 23 '11:16') (At 23 '11:16') (At 23 '11:16')))
    '3' = @((Job 'product (ubuntu-latest)' (At 23 '12:00') (At 23 '12:01') $null))
    '5' = @((Job 'product (ubuntu-latest)' (At 24 '09:00') (At 24 '09:00') (At 24 '09:20')), (Job 'quality-gate' (At 24 '09:21') (At 24 '09:21') (At 24 '09:22')))
}
$runsFile = Join-Path $work 'runs.json'; $jobsFile = Join-Path $work 'jobs.json'
@{ workflow_runs = $runs } | ConvertTo-Json -Depth 6 | Set-Content $runsFile
$jobs | ConvertTo-Json -Depth 6 | Set-Content $jobsFile
$script = Join-Path $PSScriptRoot 'ci-speed.ps1'

try {
    $csv = Join-Path $work 'runs.csv'; $daysCsv = Join-Path $work 'days.csv'
    $line = (& pwsh -NoProfile -File $script -RunsFile $runsFile -JobsFile $jobsFile -Csv $csv -DaysCsv $daysCsv | Out-String).Trim()
    Assert ($line.Split("`n").Count -eq 1) 'the summary is one line'
    Assert ($line -match '5 runs \(2 success, 1 red, 1 cancelled left out, 1 running left out\)') 'cancelled and running runs are counted apart, not measured'
    # Gate from created: A 33.0, B 16.0, E 22.0 -> median 22, p90 33.  From started: A 31.0, B 16.0, E 22.0 -> 22, 31.
    Assert ($line -match 'merge gate from created median 22 p90 33 min') 'the merge gate from created_at, queue included'
    Assert ($line -match 'from started median 22 p90 31 min') 'the merge gate from run_started_at, labelled apart'
    # Full run: A 38, B 20, E 30 -> 30, 38. Gate queue (the longest job wait): A 3, B 10, E 0 -> 3, 10.
    Assert ($line -match 'full run median 30 p90 38 min') 'the full run is run_started_at to updated_at'
    Assert ($line -match 'gate queue median 3 p90 10 min') 'the queue wait is the longest gate-job wait for a runner'
    Assert ($line -match 'red 33\.3%') 'the red rate counts success and red only'
    Assert ($line -match 'red per PT day: 11-23 50%, 11-24 0%') 'the red rate per Pacific day'

    $rows = @(Import-Csv $csv)
    Assert ($rows.Count -eq 3) 'the CSV has the three measured runs'
    $a = $rows | Where-Object { $_.run_id -eq '1' }
    Assert ($a.gate_from_created_min -eq '33' -and $a.gate_from_started_min -eq '31' -and $a.full_min -eq '38' -and $a.queue_min -eq '3' -and $a.gate_jobs -eq '3') 'run A: 33, 31, 38, 3 and three gate jobs (app is not one)'
    Assert ($a.created_at -eq '2026-11-23T10:00:00Z' -and $a.day -eq '11-23') 'created_at is the API value in UTC, not shifted by the local zone'
    $b = $rows | Where-Object { $_.run_id -eq '2' }
    Assert ($b.conclusion -eq 'failure' -and $b.gate_from_created_min -eq '16' -and $b.queue_min -eq '10') 'run B: red, 16, queue 10'
    $days = @(Import-Csv $daysCsv)
    Assert ($days.Count -eq 2 -and $days[0].day -eq '11-23' -and $days[0].red -eq '1' -and $days[1].day -eq '11-24' -and $days[1].red -eq '0') 'the per-day CSV'

    # The gate is a pattern: product alone ends at 10:30, 11:15, 09:20 -> 30, 15, 20 from created -> median 20, p90 30.
    $product = (& pwsh -NoProfile -File $script -RunsFile $runsFile -JobsFile $jobsFile -GatePattern '^product' | Out-String).Trim()
    Assert ($product -match 'merge gate from created median 20 p90 30 min') 'another gate pattern measures another gate'

    # A gate job that never finished says nothing: no figure, and the run drops out of the medians (it does not count as 0).
    $partial = @{ '1' = $jobs['1']; '2' = $jobs['2']; '5' = @((Job 'product (ubuntu-latest)' (At 24 '09:00') (At 24 '09:00') $null)) }
    $partialFile = Join-Path $work 'partial.json'; $partial | ConvertTo-Json -Depth 6 | Set-Content $partialFile
    $open = (& pwsh -NoProfile -File $script -RunsFile $runsFile -JobsFile $partialFile | Out-String).Trim()
    Assert ($open -match 'merge gate from created median 16 p90 33 min') 'an unfinished gate job is left out of the gate figures'

    # No measured runs at all: a line that says n/a, not an error.
    @{ workflow_runs = @((Run 9 'completed' 'cancelled' (At 23 '10:00') (At 23 '10:00') (At 23 '10:05'))) } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $work 'none.json')
    '{}' | Set-Content (Join-Path $work 'nojobs.json')
    $none = (& pwsh -NoProfile -File $script -RunsFile (Join-Path $work 'none.json') -JobsFile (Join-Path $work 'nojobs.json') | Out-String).Trim()
    Assert ($none -match '1 cancelled left out' -and $none -match 'median n/a' -and $none -match 'red n/a%') 'nothing measured says n/a'

    # -Runs keeps the newest N.
    $two = (& pwsh -NoProfile -File $script -RunsFile $runsFile -JobsFile $jobsFile -Runs 2 | Out-String).Trim()
    Assert ($two -match 'last 2 runs' -and $two -match '1 success, 0 red, 0 cancelled left out, 1 running left out') '-Runs keeps the newest runs'
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }

"$script:checks checks, $script:failures failed"
if ($script:failures -gt 0) { exit 1 }
