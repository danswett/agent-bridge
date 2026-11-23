# How fast main's CI is, measured from the API and not estimated (#4104).
#
# For the last -Runs finished or running ci.yml push runs on main:
#   gate_from_created_min  what a merger waits: run created_at -> the last end of the merge-gate jobs (queue included)
#   gate_from_started_min  the same from run_started_at (the run's own clock, after any queue before its first attempt)
#   full_min               run_started_at -> updated_at (the whole run)
#   queue_min              the longest wait of a gate job for a runner: its started_at - its created_at
# The merge gate is the jobs whose name matches -GatePattern: product and quality-gate until the fast lane is split out of
# ci.yml; then give the fast lane's job names. A run is "red" when it ended failure, timed_out or startup_failure (the same
# rule as red-minutes.ps1). Cancelled runs and runs still going are left out of every median and rate and counted apart.
#
#   pwsh quality/ci-speed.ps1                                   # last 100 runs: one summary line
#   pwsh quality/ci-speed.ps1 -Runs 200 -Csv runs.csv -DaysCsv days.csv
#   pwsh quality/ci-speed.ps1 -RunsFile runs.json -JobsFile jobs.json    # offline (the test uses this)
#
# Reads: ci.yml's runs on main (pages of 100) and each measured run's latest jobs (pages of 100), through `gh api`, several
# runs at a time, with an ETag cache in -CacheFile so a repeat finds nothing new for free (a 304 costs no rate limit). It
# stops fetching jobs, and says so, when x-ratelimit-remaining falls under -MinRemaining. Writes: only -CacheFile, -Csv and
# -DaysCsv. Needs GH_TOKEN or a logged-in gh, unless -RunsFile and -JobsFile are given.
[CmdletBinding()]
param(
    [string]$Repo = 'danswett/agent-bridge',
    [int]$Runs = 100,
    [string]$GatePattern = '^(product|quality-gate)( \(|$)',
    [string]$RunsFile,
    [string]$JobsFile,
    [string]$CacheFile = (Join-Path ([IO.Path]::GetTempPath()) 'ci-speed-cache.json'),
    [string]$Csv,
    [string]$DaysCsv,
    [int]$Throttle = 6,
    [int]$MinRemaining = 100
)
$ErrorActionPreference = 'Stop'
$Red = 'failure', 'timed_out', 'startup_failure'

function Get-PacificDay([datetime]$utc) {
    foreach ($id in 'Pacific Standard Time', 'America/Los_Angeles') {
        try { return [TimeZoneInfo]::ConvertTimeFromUtc($utc, [TimeZoneInfo]::FindSystemTimeZoneById($id)).ToString('MM-dd') } catch { }
    }
    $utc.ToString('MM-dd') + 'Z'
}

# Nearest rank: the smallest value with at least p percent of the values at or below it.
function Get-Percentile([double[]]$values, [double]$p) {
    if (-not $values -or $values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $sorted[[Math]::Max(0, [int][Math]::Ceiling($p / 100 * $sorted.Count) - 1)]
}

# ConvertFrom-Json turns ISO timestamps into local DateTime values: take those as they are, and parse a string as UTC.
function To-Utc($v) {
    if ($v -is [datetime]) { return [datetimeoffset]::new($v.ToUniversalTime(), [TimeSpan]::Zero) }
    [datetimeoffset]::Parse([string]$v, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
}

function Format-Minutes($value) { if ($null -eq $value) { 'n/a' } else { '{0:0.#}' -f $value } }

# One measured run: the gate jobs of its latest attempt, as minutes. $null where a gate job has not finished.
function Measure-Run($run, $jobs, [string]$pattern) {
    $created = (To-Utc $run.created_at)
    $started = (To-Utc $run.run_started_at)
    $updated = (To-Utc $run.updated_at)
    $gate = @($jobs | Where-Object { $_.name -match $pattern })
    $ended = @($gate | Where-Object { $_.completed_at } | ForEach-Object { (To-Utc $_.completed_at) })
    $last = if ($gate.Count -gt 0 -and $ended.Count -eq $gate.Count) { ($ended | Sort-Object)[-1] } else { $null }
    $waits = @($gate | Where-Object { $_.started_at -and $_.created_at } | ForEach-Object {
        ((To-Utc $_.started_at) - (To-Utc $_.created_at)).TotalMinutes })
    [pscustomobject]@{
        run_id = [long]$run.id
        created_at = $created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        day = Get-PacificDay $created.UtcDateTime
        conclusion = [string]$run.conclusion
        gate_jobs = $gate.Count
        gate_from_created_min = if ($last) { [Math]::Round(($last - $created).TotalMinutes, 1) } else { $null }
        gate_from_started_min = if ($last) { [Math]::Round(($last - $started).TotalMinutes, 1) } else { $null }
        full_min = [Math]::Round(($updated - $started).TotalMinutes, 1)
        queue_min = if ($waits.Count -gt 0) { [Math]::Round(($waits | Measure-Object -Maximum).Maximum, 1) } else { $null }
    }
}

# gh api with the ETag cache. 304 = the cached body, free. Returns the body and gh's remaining-rate header.
function Get-Body([string]$path, [hashtable]$cache) {
    $etag = if ($cache.ContainsKey($path)) { $cache[$path].etag } else { $null }
    $call = @('api', $path, '-i')
    if ($etag) { $call += @('-H', "If-None-Match: $etag") }
    $out = (& gh @call 2>&1 | Out-String)
    $remaining = [regex]::Match($out, '(?im)^x-ratelimit-remaining:\s*(\d+)').Groups[1].Value
    if ($out -match '(?m)^HTTP/\S+ 304') { return @{ body = $cache[$path].body; remaining = $remaining } }
    if ($out -notmatch '(?m)^HTTP/\S+ 200') { throw "gh api $path failed: $($out.Substring(0, [Math]::Min(300, $out.Length)))" }
    $parts = $out -split "(\r?\n){2}", 2
    $cache[$path] = @{ etag = [regex]::Match($parts[0], '(?im)^etag:\s*(.+?)\s*$').Groups[1].Value; body = $parts[-1] }
    @{ body = $parts[-1]; remaining = $remaining }
}

$cache = @{}
if (-not $RunsFile -and (Test-Path $CacheFile)) {
    try { (Get-Content $CacheFile -Raw | ConvertFrom-Json -AsHashtable).GetEnumerator() | ForEach-Object { $cache[$_.Key] = $_.Value } } catch { $cache = @{} }
}

# The runs: pages of 100 until -Runs are in hand (oldest pages last), failing loudly past 10 pages.
$all = @()
if ($RunsFile) { $all = @((Get-Content $RunsFile -Raw | ConvertFrom-Json).workflow_runs) }
else {
    for ($page = 1; $all.Count -lt $Runs; $page++) {
        if ($page -gt 10) { throw "more than 10 pages of runs asked for; lower -Runs" }
        $got = Get-Body "repos/$Repo/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=100&page=$page" $cache
        $batch = @((($got.body | ConvertFrom-Json).workflow_runs))
        if ($batch.Count -eq 0) { break }
        $all += $batch
        if ($batch.Count -lt 100) { break }
    }
}
$all = @($all | Sort-Object { (To-Utc $_.created_at) } -Descending | Select-Object -First $Runs)

$cancelled = @($all | Where-Object { $_.conclusion -eq 'cancelled' -or $_.conclusion -eq 'skipped' }).Count
$running = @($all | Where-Object { $_.status -ne 'completed' }).Count
$measured = @($all | Where-Object { $_.status -eq 'completed' -and $_.conclusion -notin 'cancelled', 'skipped' })

# Each measured run's latest jobs: several at a time, the cache read-only inside, merged after.
$jobsByRun = @{}
if ($JobsFile) {
    $map = Get-Content $JobsFile -Raw | ConvertFrom-Json -AsHashtable
    foreach ($run in $measured) { $jobsByRun[[string]$run.id] = @($map[[string]$run.id]) }
}
else {
    $stopped = $false
    $fetched = @($measured | ForEach-Object -ThrottleLimit $Throttle -Parallel {
        $run = $_; $repo = $using:Repo; $known = $using:cache; $jobs = @(); $remaining = $null; $fresh = @{}
        for ($page = 1; ; $page++) {
            $path = "repos/$repo/actions/runs/$($run.id)/jobs?filter=latest&per_page=100&page=$page"
            $call = @('api', $path, '-i')
            if ($known.ContainsKey($path)) { $call += @('-H', "If-None-Match: $($known[$path].etag)") }
            $out = (& gh @call 2>&1 | Out-String)
            $remaining = [regex]::Match($out, '(?im)^x-ratelimit-remaining:\s*(\d+)').Groups[1].Value
            if ($out -match '(?m)^HTTP/\S+ 304') { $body = $known[$path].body }
            elseif ($out -match '(?m)^HTTP/\S+ 200') {
                $parts = $out -split "(\r?\n){2}", 2; $body = $parts[-1]
                $fresh[$path] = @{ etag = [regex]::Match($parts[0], '(?im)^etag:\s*(.+?)\s*$').Groups[1].Value; body = $body }
            }
            else { return [pscustomobject]@{ id = $run.id; jobs = $null; error = "gh api $path failed"; remaining = $remaining; fresh = $fresh } }
            $doc = $body | ConvertFrom-Json
            $jobs += @($doc.jobs)
            if ($jobs.Count -ge [int]$doc.total_count -or @($doc.jobs).Count -eq 0) { break }
        }
        [pscustomobject]@{ id = $run.id; jobs = $jobs; error = $null; remaining = $remaining; fresh = $fresh }
    })
    $low = $null
    foreach ($f in $fetched) {
        foreach ($k in $f.fresh.Keys) { $cache[$k] = $f.fresh[$k] }
        if ($f.error) { Write-Warning $f.error; continue }
        $jobsByRun[[string]$f.id] = @($f.jobs)
        if ($f.remaining -and ([int]$f.remaining -lt $MinRemaining)) { $low = [int]$f.remaining }
    }
    if ($null -ne $low) { Write-Warning "x-ratelimit-remaining fell to $low (under $MinRemaining): the figures cover only the runs read" }
}

$rows = @($measured | Where-Object { $jobsByRun.ContainsKey([string]$_.id) } | ForEach-Object { Measure-Run $_ $jobsByRun[[string]$_.id] $GatePattern })
$unread = $measured.Count - $rows.Count
$ok = @($rows | Where-Object { $_.conclusion -eq 'success' })
$bad = @($rows | Where-Object { $_.conclusion -in $Red })

function Stat($name, $property) {
    $values = @($rows | Where-Object { $null -ne $_.$property } | ForEach-Object { [double]$_.$property })
    "$name median $(Format-Minutes (Get-Percentile $values 50)) p90 $(Format-Minutes (Get-Percentile $values 90)) min"
}
$days = $rows | Group-Object day | Sort-Object Name | ForEach-Object {
    $r = @($_.Group | Where-Object { $_.conclusion -in $Red }).Count; $s = @($_.Group | Where-Object { $_.conclusion -eq 'success' }).Count
    [pscustomobject]@{ day = $_.Name; success = $s; red = $r; red_pct = if ($r + $s -gt 0) { [Math]::Round(100 * $r / ($r + $s), 1) } else { $null } }
}
$redPct = if ($ok.Count + $bad.Count -gt 0) { '{0:0.#}' -f (100 * $bad.Count / ($ok.Count + $bad.Count)) } else { 'n/a' }
$perDay = ($days | ForEach-Object { "$($_.day) $(if ($null -ne $_.red_pct) { '{0:0.#}' -f $_.red_pct } else { 'n/a' })%" }) -join ', '
"ci-speed main push, last $($all.Count) runs ($($ok.Count) success, $($bad.Count) red, $cancelled cancelled left out, $running running left out$(if ($unread) { ", $unread unread" })): merge gate from created $(Stat '' gate_from_created_min); from started $(Stat '' gate_from_started_min); full run $(Stat '' full_min); gate queue $(Stat '' queue_min); red $redPct%; red per PT day: $perDay".Replace('  ', ' ')

if ($Csv) { $rows | Export-Csv -Path $Csv -NoTypeInformation }
if ($DaysCsv) { $days | Export-Csv -Path $DaysCsv -NoTypeInformation }
if (-not $RunsFile -and $cache.Count -gt 0) { try { $cache | ConvertTo-Json -Depth 5 -Compress | Set-Content $CacheFile } catch { Write-Warning "could not write the cache: $($_.Exception.Message)" } }
