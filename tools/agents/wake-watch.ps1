# wake-watch.ps1: exits as soon as there is something for this team to act on, which wakes the session.
# See docs/agents/onboarding.md §5. Run it with the powershell tool in mode "async" (attached), and
# restart it after handling each wake.
#
# Budget: most sessions share one GitHub account (5,000 REST calls an hour between them). A dozen
# watchers polling plainly used it all up on 11-05, twice. So every read is a conditional request
# (If-None-Match), and an unchanged answer (304) doesn't count against the limit. A failed read is
# logged and retried, never treated as a wake: an error body once read as a new comment.
param(
  [Parameter(Mandatory)][string]$Team,           # e.g. quality, engineering, cx, connection, daily, sre, trust, platform, apple, product, creative, tpm
  [int[]]$Prs = @(),                             # PRs whose head, state and CI to watch
  [string]$Since = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'),
  [int]$CommentSeconds = 60,
  [int]$CiSeconds = 180,
  [int]$MaxMinutes = 120,
  [long[]]$IgnoreIds = @()                       # comment ids to skip, e.g. a rolling status comment edited in place
)
$ErrorActionPreference = 'Stop'
$repo = 'danswett/agent-bridge'
$tags = @("[to:$($Team.ToLower())]", '[to:all]')
$cache = @{}

function Get-Api([string]$path) {
  $c = $cache[$path]
  $call = @('api', '-i', $path)
  if ($c) { $call += @('-H', "If-None-Match: $($c.etag)") }
  $raw = (& gh @call 2>$null) -join "`n"
  $status = if ($raw -match '^HTTP/\S+\s+(\d{3})') { [int]$Matches[1] } else { 0 }
  if ($status -eq 304 -and $c) { return $c.body }
  if ($status -ne 200) { throw "GET $path answered HTTP $status" }
  $head, $text = $raw -split "\r?\n\r?\n", 2
  $body = $text | ConvertFrom-Json
  if ($head -match '(?mi)^etag:\s*(.+?)\s*$') { $cache[$path] = @{ etag = $Matches[1]; body = $body } }
  $body
}
# Oldest first, so a new comment past the first 100 changed since $Since is on a later page. Each page
# URL keeps its own ETag; a 304 on a page is "unchanged". Any page failing throws, so it is never a wake.
function Get-Comments([string]$since) {
  $all = @()
  for ($page = 1; ; $page++) {
    $items = @(Get-Api "repos/$repo/issues/comments?since=$since&per_page=100&page=$page")
    $all += $items
    if ($items.Count -lt 100) { break }
  }
  $all
}
function Invoke-Retry([scriptblock]$Do) {
  for ($i = 1; ; $i++) {
    try { return (& $Do) } catch {
      if ($i -ge 3) { throw }
      Write-Host "start-up read failed (retrying): $($_.Exception.Message)"; Start-Sleep -Seconds 5
    }
  }
}
function Test-Tagged([string]$body) {
  $b = $body.ToLower()
  [bool]@($tags | ? { $b.Contains($_) }).Count
}
function Get-PrState([int]$p) {
  $pr = Get-Api "repos/$repo/pulls/$p"
  if (-not $pr.head.sha) { throw "pr #$p state unreadable" }
  @{ sha = $pr.head.sha; state = $(if ($pr.merged) { 'MERGED' } elseif ($pr.state -eq 'closed') { 'CLOSED' } else { 'OPEN' }) }
}
function Get-CiState([string]$sha) {
  $runs = @((Get-Api "repos/$repo/commits/$sha/check-runs?per_page=100").check_runs)
  $ci = @($runs | ? { $_.name -notin 'merge-rule', 'evaluate', 'independent-review' })
  if (-not $ci.Count) { return @{ pending = $true; failed = @() } }
  @{ pending = [bool]($ci | ? status -ne 'completed'); failed = @($ci | ? conclusion -in 'failure', 'cancelled', 'timed_out', 'action_required' | % name) }
}

# Wake on CI only when it goes from running to finished, never just because it is already finished.
$heads = @{}; $running = @{}
"watching comments since $Since"
# A transient error here must not exit the script (that wakes the session): retry, then treat CI as not running.
foreach ($p in $Prs) {
  try {
    $heads[$p] = (Invoke-Retry { Get-PrState $p }).sha
    $running[$p] = (Invoke-Retry { Get-CiState $heads[$p] }).pending
  } catch {
    Write-Host "start-up read for #$p failed (continuing, CI treated as not running): $($_.Exception.Message)"
    if (-not $heads[$p]) { $heads[$p] = $null }
    $running[$p] = $false
  }
}
$deadline = (Get-Date).AddMinutes($MaxMinutes)
$nextCi = Get-Date
while ((Get-Date) -lt $deadline) {
  try {
    $hits = @(Get-Comments $Since | ? {
        $_.body -and $_.id -notin $IgnoreIds -and (Test-Tagged $_.body) })
    if ($hits.Count) { $hits | % { "WAKE comment $($_.html_url) by $($_.user.login) at $($_.updated_at)" }; exit 0 }
    if ($Prs.Count -and (Get-Date) -ge $nextCi) {
      $nextCi = (Get-Date).AddSeconds($CiSeconds)
      foreach ($p in $Prs) {
        $ps = Get-PrState $p
        if ($ps.state -ne 'OPEN') { "WAKE pr #$p is $($ps.state)"; exit 0 }
        if (-not $heads[$p]) { $heads[$p] = $ps.sha }
        if ($ps.sha -ne $heads[$p]) { "WAKE pr #$p head moved $($heads[$p].Substring(0,8)) -> $($ps.sha.Substring(0,8))"; exit 0 }
        $st = Get-CiState $ps.sha
        if ($running[$p] -and -not $st.pending) {
          "WAKE ci #$p done at $($ps.sha.Substring(0,8)): " + $(if ($st.failed.Count) { "FAILED: $($st.failed -join ', ')" } else { 'all passed or skipped' })
          exit 0
        }
        $running[$p] = $st.pending
      }
    }
  } catch { Write-Host "watch error (continuing): $($_.Exception.Message)"; Start-Sleep -Seconds 60 }
  Start-Sleep -Seconds $CommentSeconds
}
"WAKE timeout after $MaxMinutes min (restart the watcher)"
