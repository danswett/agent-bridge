# How long main's CI has been red: the number the owner's red-minutes target is measured by.
#
# Red is the same rule the main guardian uses (decision G1): a finished ci.yml push run on main that ended
# failure, timed_out or startup_failure. A red spell runs from the end of that run to the end of the next
# success. Cancelled, skipped and still-running runs say nothing, so they neither start nor end a spell.
#
#   pwsh quality/red-minutes.ps1                  # last 24 h, one digest line plus the spells
#   pwsh quality/red-minutes.ps1 -Json            # the same, as JSON
#   pwsh quality/red-minutes.ps1 -RunsFile runs.json -Now 2026-11-09T08:00:00Z   # offline (the test uses this)
#
# Reads: ci.yml's runs on main through `gh api`, with an ETag cache in -CacheFile so a poll that finds nothing new
# costs no rate limit (a 304 is free). Writes: only -CacheFile. Needs GH_TOKEN or a logged-in gh, unless -RunsFile.
[CmdletBinding()]
param(
    [string]$Repo = 'danswett/agent-bridge',
    [int]$Hours = 24,
    [string]$RunsFile,
    [string]$CacheFile = (Join-Path ([IO.Path]::GetTempPath()) 'red-minutes-cache.json'),
    [datetime]$Now = [datetime]::UtcNow,
    [switch]$Json
)
$ErrorActionPreference = 'Stop'

# The owner's target (2026-11-09): p90 of red minutes per hour at most 10 over a rolling 24 h, mean at most 3,
# no single red spell over 30 minutes, and every spell has a named cause.
$TargetP90 = 10; $TargetMean = 3; $TargetSpell = 30
$Red = 'failure', 'timed_out', 'startup_failure'

function Get-Pacific([datetime]$utc) {
    $tz = $null
    foreach ($id in 'Pacific Standard Time', 'America/Los_Angeles') {
        try { $tz = [TimeZoneInfo]::FindSystemTimeZoneById($id); break } catch { }
    }
    if (-not $tz) { return $utc.ToString('MM-dd HH:mm') + 'Z' }
    [TimeZoneInfo]::ConvertTimeFromUtc($utc, $tz).ToString('MM-dd HH:mm') + ' PT'
}

function Get-Page([string]$path, $cache) {
    $etag = if ($cache.ContainsKey($path)) { $cache[$path].etag } else { $null }
    $args2 = @('api', $path, '-i')
    if ($etag) { $args2 += @('-H', "If-None-Match: $etag") }
    $out = (& gh @args2 2>&1 | Out-String)
    if ($out -match '(?m)^HTTP/\S+ 304') { return $cache[$path].body }
    if ($out -notmatch '(?m)^HTTP/\S+ 200') { throw "gh api $path failed: $($out.Substring(0, [Math]::Min(300, $out.Length)))" }
    $parts = $out -split "(\r?\n){2}", 2
    $newEtag = [regex]::Match($parts[0], '(?im)^etag:\s*(.+?)\s*$').Groups[1].Value
    $body = $parts[-1]
    $cache[$path] = @{ etag = $newEtag; body = $body }
    $body
}

function Read-Runs {
    if ($RunsFile) { return @(Get-Content $RunsFile -Raw | ConvertFrom-Json) }
    $cache = @{}
    if (Test-Path $CacheFile) {
        (Get-Content $CacheFile -Raw | ConvertFrom-Json -AsHashtable).GetEnumerator() | ForEach-Object { $cache[$_.Key] = $_.Value }
    }
    $windowStart = $Now.AddHours(-$Hours).ToUniversalTime()
    $runs = @()
    foreach ($page in 1..6) {
        $body = Get-Page "repos/$Repo/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=100&page=$page" $cache
        $got = @(($body | ConvertFrom-Json).workflow_runs)
        $runs += $got
        $finished = @($got | Where-Object { $_.conclusion -eq 'success' -and ([datetime]$_.updated_at).ToUniversalTime() -lt $windowStart })
        if ($got.Count -lt 100 -or $finished.Count) { break }  # a success from before the window: the state at its start is known
    }
    $cache | ConvertTo-Json -Depth 4 | Set-Content $CacheFile
    $runs
}

$Now = $Now.ToUniversalTime()
$start = $Now.AddHours(-$Hours)
$done = @(Read-Runs | Where-Object { $_.conclusion -eq 'success' -or $_.conclusion -in $Red } |
    Sort-Object { ([datetime]$_.updated_at).ToUniversalTime() })

$spells = @(); $open = $null
foreach ($r in $done) {
    $t = ([datetime]$r.updated_at).ToUniversalTime()
    if ($t -gt $Now) { continue }
    if ($r.conclusion -in $Red) {
        if (-not $open) { $open = [pscustomobject]@{ from = $t; sha = $r.head_sha; url = $r.html_url; to = $null } }
    } elseif ($open) {
        $open.to = $t; $spells += $open; $open = $null
    }
}
if ($open) { $open.to = $Now; $spells += $open }

# Red minutes in each of the last $Hours hourly buckets, oldest first.
$hourly = foreach ($i in 0..($Hours - 1)) {
    $a = $start.AddHours($i); $b = $a.AddHours(1); $m = 0.0
    foreach ($s in $spells) {
        $lo = if ($s.from -gt $a) { $s.from } else { $a }
        $hi = if ($s.to -lt $b) { $s.to } else { $b }
        if ($hi -gt $lo) { $m += ($hi - $lo).TotalMinutes }
    }
    [math]::Round($m, 1)
}
$sorted = @($hourly | Sort-Object)
$p90 = $sorted[[int][math]::Ceiling(0.9 * $Hours) - 1]
$total = [math]::Round(($hourly | Measure-Object -Sum).Sum, 1)
$mean = [math]::Round($total / $Hours, 1)
$inWindow = @($spells | Where-Object { $_.to -gt $start })
$longest = if ($inWindow) { [math]::Round(($inWindow | ForEach-Object { ($_.to - $_.from).TotalMinutes } | Measure-Object -Maximum).Maximum, 0) } else { 0 }
$over = @()
if ($p90 -gt $TargetP90) { $over += 'p90' }
if ($mean -gt $TargetMean) { $over += 'mean' }
if ($longest -gt $TargetSpell) { $over += 'longest spell' }

$line = "Main red, last $Hours h: $([math]::Round($total)) of $($Hours * 60) min ($([math]::Round(100 * $total / ($Hours * 60)))%); per hour p90 $p90 min (target $TargetP90), mean $mean (target $TargetMean); longest spell $longest min (target $TargetSpell); $($inWindow.Count) spell(s). " +
    $(if ($over) { 'Over target: ' + ($over -join ', ') + '.' } else { 'Within target.' })

if ($Json) {
    [pscustomobject]@{
        hours = $Hours; red_minutes = $total; p90_per_hour = $p90; mean_per_hour = $mean; longest_spell_min = $longest
        spells = $inWindow.Count; over_target = $over; line = $line
        spell_list = @($inWindow | ForEach-Object { [pscustomobject]@{ from = $_.from.ToString('o'); to = $_.to.ToString('o'); minutes = [math]::Round(($_.to - $_.from).TotalMinutes); first_red_sha = $_.sha; run = $_.url } })
    } | ConvertTo-Json -Depth 5
} else {
    $line
    foreach ($s in $inWindow) {
        '  {0} to {1}  {2,4} min  first red {3}  cause: (name it)  {4}' -f (Get-Pacific $s.from), (Get-Pacific $s.to), [math]::Round(($s.to - $s.from).TotalMinutes), $s.sha.Substring(0, 8), $s.url
    }
}
