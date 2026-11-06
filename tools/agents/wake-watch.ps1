# wake-watch.ps1: exits as soon as there is something for this team to act on, which wakes the session.
# See docs/agents/onboarding.md §5. Most sessions share one GitHub account and its GraphQL budget, so this polls over REST only.
# Run it with the powershell tool in mode "async" (attached). Restart it after handling each wake.
param(
  [Parameter(Mandatory)][string]$Team,           # e.g. quality, engineering, cx, connection, daily, sre, trust, platform, migration, product, creative, tpm
  [int[]]$Prs = @(),                             # PRs whose CI to watch
  [string]$Since = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'),
  [int]$CommentSeconds = 60,
  [int]$CiSeconds = 180,
  [int]$MaxMinutes = 120,
  [long[]]$IgnoreIds = @()                       # comment ids to skip, e.g. a rolling status comment edited in place
)
$ErrorActionPreference = 'Stop'
$repo = 'danswett/agent-bridge'
$tags = @("[to:$($Team.ToLower())]", '[to:all]')
$ignore = '[' + (($IgnoreIds | % { "$_" }) -join ',') + ']'
$jq = '.[] | select(.body != null) | select(.id as $i | ' + $ignore + ' | index($i) | not) | select((.body | ascii_downcase) as $b | ' + (($tags | % { '($b | contains("' + $_ + '"))' }) -join ' or ') + ') | "WAKE comment \(.html_url) by \(.user.login) at \(.updated_at)"'
# CI also comes over REST (check-runs on the head), never `gh pr checks`, which spends the shared GraphQL budget.
function Get-CiState([string]$sha) {
  $runs = gh api "repos/$repo/commits/$sha/check-runs?per_page=100" --jq '[.check_runs[] | {name, status, conclusion}]' 2>$null | ConvertFrom-Json
  if ($LASTEXITCODE) { throw "check-runs for $sha unreadable" }
  $ci = @($runs | ? { $_.name -notin 'merge-rule','evaluate','independent-review' })
  if (-not $ci.Count) { return @{ pending = $true; failed = @() } }
  @{ pending = [bool]($ci | ? status -ne 'completed'); failed = @($ci | ? conclusion -in 'failure','cancelled','timed_out','action_required' | % name) }
}
# Wake on CI only when it goes from running to finished, never just because it is already finished.
$heads = @{}; $running = @{}
# Head and state come over REST, which has its own budget: `gh pr view` is GraphQL, and once the
# shared GraphQL budget ran out it returned nothing, which read as "not open" and woke falsely (SRE, 6673064421).
function Get-PrState([int]$p) {
  $r = gh api "repos/$repo/pulls/$p" --jq '"\(.head.sha) \(if .merged then "MERGED" elif .state == "closed" then "CLOSED" else "OPEN" end)"' 2>$null
  if ($LASTEXITCODE -or -not $r) { throw "pr #$p state unreadable" }
  $sha, $state = "$r" -split ' '
  @{ sha = $sha; state = $state }
}
foreach ($p in $Prs) { $heads[$p] = (Get-PrState $p).sha; $running[$p] = (Get-CiState $heads[$p]).pending }
$deadline = (Get-Date).AddMinutes($MaxMinutes)
$nextCi = Get-Date
while ((Get-Date) -lt $deadline) {
  try {
    $hits = @(gh api "repos/$repo/issues/comments?since=$Since&per_page=100" --jq $jq)
    if ($hits.Count) { $hits; exit 0 }
    if ($Prs.Count -and (Get-Date) -ge $nextCi) {
      $nextCi = (Get-Date).AddSeconds($CiSeconds)
      foreach ($p in $Prs) {
        $ps = Get-PrState $p; $sha = $ps.sha; $state = $ps.state
        if ($state -ne 'OPEN') { "WAKE pr #$p is $state"; exit 0 }
        if ($sha -ne $heads[$p]) { "WAKE pr #$p head moved $($heads[$p].Substring(0,8)) -> $($sha.Substring(0,8))"; exit 0 }
        $st = Get-CiState $sha
        if ($running[$p] -and -not $st.pending) {
          "WAKE ci #$p done at $($sha.Substring(0,8)): " + $(if ($st.failed.Count) { "FAILED: $($st.failed -join ', ')" } else { 'all passed or skipped' })
          exit 0
        }
        $running[$p] = $st.pending
      }
    }
  } catch { Write-Host "watch error (continuing): $($_.Exception.Message)" }
  Start-Sleep -Seconds $CommentSeconds
}
"WAKE timeout after $MaxMinutes min (restart the watcher)"
