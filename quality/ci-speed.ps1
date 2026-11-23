# How fast main's CI is, measured from the API and not estimated (#4104).
#
# For the last -Runs ci.yml push runs on main:
#   gate_from_created_min  what a merger waits: run created_at -> the last end of the merge-gate jobs (queue included)
#   gate_from_started_min  the same from run_started_at (the run's own clock, after any queue before its first attempt)
#   full_min               run_started_at -> updated_at (the whole run)
#   queue_min              the longest wait of a gate job for a runner: its started_at - its created_at
# The merge gate is the jobs whose name matches -GatePattern: product and quality-gate until the fast lane is split out of
# ci.yml (quality-gate needs every other job, so its end is the gate's end in practice); then give the fast lane's job names.
# The summary line names the gate jobs it matched. A run is "red" when it ended failure, timed_out or startup_failure (the same
# rule as red-minutes.ps1). Cancelled and skipped runs, and runs still going, are left out of every median and rate and counted
# apart; other conclusions (neutral, stale, action_required) are counted as "other". A median is the nearest-rank (lower) one.
# A re-run keeps its first created_at but moves run_started_at, so its gate-from-created figure includes the earlier attempt,
# and the first failed attempt is invisible to the red rate: re-runs are counted on the line and in the CSV (attempt).
#
#   pwsh quality/ci-speed.ps1                                   # last 100 runs: one summary line
#   pwsh quality/ci-speed.ps1 -Runs 200 -Csv runs.csv -DaysCsv days.csv
#   pwsh quality/ci-speed.ps1 -RunsFile runs.json -JobsFile jobs.json    # offline (the test uses this)
#
# Reads: ci.yml's runs on main (pages of 100) and each measured run's latest jobs, through `gh api`, up to -Throttle runs at a
# time, with an ETag cache in -CacheFile so a repeat finds nothing new for free (a 304 costs no rate limit). It FAILS CLOSED
# on the rate limit: it reads the budget (`gh api rate_limit`, free) before the first call, and again from every response's
# x-ratelimit headers before each page and each batch, and it makes only as many calls as keep the remaining budget above
# -MinPercent (10) of the limit. A header that is missing, not a number or zero stops it. It says so on the summary line and
# measures what it has read. Writes: only -CacheFile, -Csv and -DaysCsv. Needs GH_TOKEN or a logged-in gh, unless -RunsFile
# and -JobsFile are given.
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
    [int]$MinPercent = 10,
    [string]$GhCommand = 'gh'
)
$ErrorActionPreference = 'Stop'
$Red = 'failure', 'timed_out', 'startup_failure'
$script:Limit = 0
$script:Remaining = 0
$script:Stop = $null

# ConvertFrom-Json turns ISO timestamps into local DateTime values: take those as they are, and parse a string as UTC.
function To-Utc($v) {
    if ($v -is [datetime]) { return [datetimeoffset]::new($v.ToUniversalTime(), [TimeSpan]::Zero) }
    [datetimeoffset]::Parse([string]$v, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
}

function Get-PacificDay([datetime]$utc) {
    foreach ($id in 'Pacific Standard Time', 'America/Los_Angeles') {
        try { return [TimeZoneInfo]::ConvertTimeFromUtc($utc, [TimeZoneInfo]::FindSystemTimeZoneById($id)).ToString('yyyy-MM-dd') } catch { }
    }
    $utc.ToString('yyyy-MM-dd') + 'Z'
}

# Nearest rank: the smallest value with at least p percent of the values at or below it.
function Get-Percentile([double[]]$values, [double]$p) {
    $sorted = $values | Sort-Object
    $sorted[[Math]::Max(0, [int][Math]::Ceiling($p / 100 * $sorted.Count) - 1)]
}

# A header value as a whole number, or $null when it is missing or not one.
function Get-Count($text) {
    if ($null -ne $text -and ([string]$text) -match '^\s*(\d{1,9})\s*$') { return [int]$Matches[1] }
    $null
}

# Takes the limit and remaining from a response. Missing, not a number or a zero limit: stop.
function Update-Budget($limit, $remaining) {
    $l = Get-Count $limit
    $r = Get-Count $remaining
    if ($null -eq $l -or $null -eq $r -or $l -le 0) { if (-not $script:Stop) { $script:Stop = 'the rate-limit headers are missing or unreadable' }; return }
    $script:Limit = $l
    $script:Remaining = $r
}

# How many more calls keep the budget above -MinPercent of the limit. 0 (and a reason) when none, or when it is already stopped.
function Get-Allowed {
    if ($script:Stop) { return 0 }
    $floor = [Math]::Ceiling($script:Limit * $MinPercent / 100)
    $allowed = $script:Remaining - $floor
    if ($allowed -le 0) { $script:Stop = "x-ratelimit-remaining $($script:Remaining) of $($script:Limit) is at or under $MinPercent%"; return 0 }
    $allowed
}

function Format-Minutes($value) { '{0:0.#}' -f $value }

# One measured run: the gate jobs of its latest attempt, as minutes. $null where a gate job has not finished.
function Measure-Run($run, $jobs, [string]$pattern) {
    $created = To-Utc $run.created_at
    $started = To-Utc $run.run_started_at
    $updated = To-Utc $run.updated_at
    $gate = @($jobs | Where-Object { $_.name -match $pattern })
    $ended = @($gate | Where-Object { $_.completed_at } | ForEach-Object { To-Utc $_.completed_at })
    $last = if ($gate.Count -gt 0 -and $ended.Count -eq $gate.Count) { ($ended | Sort-Object)[-1] } else { $null }
    $waits = @($gate | Where-Object { $_.started_at -and $_.created_at } | ForEach-Object { ((To-Utc $_.started_at) - (To-Utc $_.created_at)).TotalMinutes })
    $attempt = Get-Count $run.run_attempt
    [pscustomobject]@{
        run_id = [long]$run.id
        created_at = $created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        day = Get-PacificDay $created.UtcDateTime
        conclusion = [string]$run.conclusion
        attempt = if ($attempt) { $attempt } else { 1 }
        gate_jobs = $gate.Count
        gate_names = (@($gate | ForEach-Object { ($_.name -replace ' \(.*$', '') } | Sort-Object -Unique) -join '|')
        gate_from_created_min = if ($last) { [Math]::Round(($last - $created).TotalMinutes, 1) } else { $null }
        gate_from_started_min = if ($last) { [Math]::Round(($last - $started).TotalMinutes, 1) } else { $null }
        full_min = [Math]::Round(($updated - $started).TotalMinutes, 1)
        queue_min = if ($waits.Count -gt 0) { [Math]::Round(($waits | Measure-Object -Maximum).Maximum, 1) } else { $null }
    }
}

# gh api with the ETag cache. 304 = the cached body, free. Returns the body and the response's rate-limit headers as text.
function Get-Body([string]$path, [hashtable]$cache) {
    $etag = if ($cache.ContainsKey($path)) { $cache[$path].etag } else { $null }
    $call = @('api', $path, '-i')
    if ($etag) { $call += @('-H', "If-None-Match: $etag") }
    $out = (& $GhCommand @call 2>&1 | Out-String)
    $limit = [regex]::Match($out, '(?im)^x-ratelimit-limit:\s*(\S+)').Groups[1].Value
    $remaining = [regex]::Match($out, '(?im)^x-ratelimit-remaining:\s*(\S+)').Groups[1].Value
    if ($out -match '(?m)^HTTP/\S+ 304') { return @{ body = $cache[$path].body; limit = $limit; remaining = $remaining } }
    if ($out -notmatch '(?m)^HTTP/\S+ 200') { throw "gh api $path failed: $($out.Substring(0, [Math]::Min(300, $out.Length)))" }
    $parts = $out -split "(\r?\n){2}", 2
    $cache[$path] = @{ etag = [regex]::Match($parts[0], '(?im)^etag:\s*(.+?)\s*$').Groups[1].Value; body = $parts[-1] }
    @{ body = $parts[-1]; limit = $limit; remaining = $remaining }
}

$cache = @{}
if (-not $RunsFile -and (Test-Path $CacheFile)) {
    try { (Get-Content $CacheFile -Raw | ConvertFrom-Json -AsHashtable).GetEnumerator() | ForEach-Object { $cache[$_.Key] = $_.Value } } catch { $cache = @{} }
}

$live = -not $RunsFile
if ($live) {
    # The budget before any call: the rate_limit endpoint is free. Unreadable means stop, never carry on.
    try {
        $core = ((& $GhCommand api rate_limit 2>&1 | Out-String) | ConvertFrom-Json).resources.core
        Update-Budget $core.limit $core.remaining
    }
    catch { $script:Stop = 'the rate limit could not be read' }
}

# The runs: pages of 100 until -Runs are in hand, the budget checked before each page.
$all = @()
if ($RunsFile) { $all = @((Get-Content $RunsFile -Raw | ConvertFrom-Json).workflow_runs) }
else {
    for ($page = 1; $all.Count -lt $Runs; $page++) {
        if ($page -gt 10) { if (-not $script:Stop) { $script:Stop = 'more than 10 pages of runs: lower -Runs' }; break }
        if ((Get-Allowed) -lt 1) { break }
        $got = Get-Body "repos/$Repo/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=100&page=$page" $cache
        Update-Budget $got.limit $got.remaining
        $batch = @((($got.body | ConvertFrom-Json).workflow_runs))
        if ($batch.Count -eq 0) { break }
        $all += $batch
        if ($batch.Count -lt 100) { break }
    }
}
$all = @($all | Sort-Object { To-Utc $_.created_at } -Descending | Select-Object -First $Runs)

$cancelled = @($all | Where-Object { $_.conclusion -in 'cancelled', 'skipped' }).Count
$running = @($all | Where-Object { $_.status -ne 'completed' }).Count
$measured = @($all | Where-Object { $_.status -eq 'completed' -and $_.conclusion -notin 'cancelled', 'skipped' })

# Each measured run's latest jobs, in batches no bigger than the budget allows and than -Throttle.
$jobsByRun = @{}
if ($JobsFile) {
    $map = Get-Content $JobsFile -Raw | ConvertFrom-Json -AsHashtable
    foreach ($run in $measured) { $jobsByRun[[string]$run.id] = @($map[[string]$run.id]) }
}
else {
    $next = 0
    while ($next -lt $measured.Count) {
        $allowed = Get-Allowed
        if ($allowed -lt 1) { break }
        $size = [Math]::Min([Math]::Min($Throttle, $allowed), $measured.Count - $next)
        $group = @($measured[$next..($next + $size - 1)])
        $next += $size
        $fetched = @($group | ForEach-Object -ThrottleLimit $Throttle -Parallel {
            $run = $_; $repo = $using:Repo; $known = $using:cache; $gh = $using:GhCommand; $jobs = @(); $fresh = @{}; $limit = $null; $remaining = $null
            for ($page = 1; $page -le 3; $page++) {
                $path = "repos/$repo/actions/runs/$($run.id)/jobs?filter=latest&per_page=100&page=$page"
                $call = @('api', $path, '-i')
                if ($known.ContainsKey($path)) { $call += @('-H', "If-None-Match: $($known[$path].etag)") }
                $out = (& $gh @call 2>&1 | Out-String)
                $limit = [regex]::Match($out, '(?im)^x-ratelimit-limit:\s*(\S+)').Groups[1].Value
                $remaining = [regex]::Match($out, '(?im)^x-ratelimit-remaining:\s*(\S+)').Groups[1].Value
                if ($out -match '(?m)^HTTP/\S+ 304') { $body = $known[$path].body }
                elseif ($out -match '(?m)^HTTP/\S+ 200') {
                    $parts = $out -split "(\r?\n){2}", 2; $body = $parts[-1]
                    $fresh[$path] = @{ etag = [regex]::Match($parts[0], '(?im)^etag:\s*(.+?)\s*$').Groups[1].Value; body = $body }
                }
                else { return [pscustomobject]@{ id = $run.id; jobs = $null; error = "gh api $path failed"; limit = $limit; remaining = $remaining; fresh = $fresh } }
                $doc = $body | ConvertFrom-Json
                $jobs += @($doc.jobs)
                if ($jobs.Count -ge [int]$doc.total_count -or @($doc.jobs).Count -eq 0) { break }
            }
            [pscustomobject]@{ id = $run.id; jobs = $jobs; error = $null; limit = $limit; remaining = $remaining; fresh = $fresh }
        })
        $lowest = $null; $unreadable = $false
        foreach ($f in $fetched) {
            foreach ($k in $f.fresh.Keys) { $cache[$k] = $f.fresh[$k] }
            if ($null -eq (Get-Count $f.remaining) -or $null -eq (Get-Count $f.limit)) { $unreadable = $true }
            elseif ($null -eq $lowest -or (Get-Count $f.remaining) -lt (Get-Count $lowest.remaining)) { $lowest = $f }
            if ($f.error) { Write-Warning $f.error; continue }
            $jobsByRun[[string]$f.id] = @($f.jobs)
        }
        # The lowest figure of the batch is the truest; an unreadable one stops everything after it.
        if ($unreadable -or $null -eq $lowest) { Update-Budget $null $null } else { Update-Budget $lowest.limit $lowest.remaining }
    }
}

$rows = @($measured | Where-Object { $jobsByRun.ContainsKey([string]$_.id) } | ForEach-Object { Measure-Run $_ $jobsByRun[[string]$_.id] $GatePattern })
$unread = $measured.Count - $rows.Count
$ok = @($rows | Where-Object { $_.conclusion -eq 'success' })
$bad = @($rows | Where-Object { $_.conclusion -in $Red })
$other = $rows.Count - $ok.Count - $bad.Count
$reruns = @($rows | Where-Object { $_.attempt -gt 1 }).Count

function Stat($label, $property) {
    $values = @($rows | Where-Object { $null -ne $_.$property } | ForEach-Object { [double]$_.$property })
    if ($values.Count -eq 0) { return "$label no data" }
    "$label median $(Format-Minutes (Get-Percentile $values 50)) p90 $(Format-Minutes (Get-Percentile $values 90)) min"
}
$days = $rows | Group-Object day | Sort-Object Name | ForEach-Object {
    $r = @($_.Group | Where-Object { $_.conclusion -in $Red }).Count; $s = @($_.Group | Where-Object { $_.conclusion -eq 'success' }).Count
    [pscustomobject]@{ day = $_.Name; success = $s; red = $r; red_pct = if ($r + $s -gt 0) { [Math]::Round(100 * $r / ($r + $s), 1) } else { $null } }
}
$redText = if ($ok.Count + $bad.Count -gt 0) { '{0:0.#}%' -f (100 * $bad.Count / ($ok.Count + $bad.Count)) } else { 'no data' }
$perDay = if ($days) { ($days | ForEach-Object { "$($_.day) $(if ($null -ne $_.red_pct) { '{0:0.#}%' -f $_.red_pct } else { 'no data' })" }) -join ', ' } else { 'no data' }
$names = @($rows | ForEach-Object { $_.gate_names -split '\|' } | Where-Object { $_ } | Sort-Object -Unique)
$gateText = if ($names.Count -gt 0) { $names -join ', ' } else { "none matched $GatePattern" }
$notes = @("$($ok.Count) success", "$($bad.Count) red", "$cancelled cancelled left out", "$running running left out")
if ($other) { $notes += "$other other" }
if ($reruns) { $notes += "$reruns re-run(s): their gate-from-created includes the earlier attempt" }
if ($unread) { $notes += "$unread unread" }
$line = "ci-speed main push, last $($all.Count) runs ($($notes -join ', ')): $(Stat 'merge gate from created' gate_from_created_min); $(Stat 'from started' gate_from_started_min); $(Stat 'full run' full_min); $(Stat 'gate queue' queue_min); red $redText; red per PT day: $perDay; gate jobs: $gateText"
if ($script:Stop) { $line += "; STOPPED EARLY, $($script:Stop): the figures cover only the runs read" }
$line

if ($Csv) { $rows | Export-Csv -Path $Csv -NoTypeInformation }
if ($DaysCsv) { $days | Export-Csv -Path $DaysCsv -NoTypeInformation }
if ($live -and $cache.Count -gt 0) { try { $cache | ConvertTo-Json -Depth 5 -Compress | Set-Content $CacheFile } catch { Write-Warning "could not write the cache: $($_.Exception.Message)" } }
