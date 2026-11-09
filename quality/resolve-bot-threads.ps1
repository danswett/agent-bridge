<#
.SYNOPSIS
  Resolves a pull request's review threads that only the Copilot or Codex reviewer bots wrote.

.DESCRIPTION
  GitHub's ruleset needs 0 unresolved threads, so threads left only by the reviewer bots block
  a merge nobody is waiting on them for. This resolves ONLY threads whose every comment was
  written by those bots, and ONLY when the PR's head already has both independent-review
  receipts (independent-review and independent-review-2, each a success from a poster in
  quality/merge-rule.yml, naming models of different families). It refuses otherwise. A thread
  anyone else commented in is never touched.

  Dry run by default: it prints what it would resolve. Pass -Apply to resolve.
  Authenticate gh first, e.g. $env:GH_TOKEN = (gh auth token --user <login>); no token is printed.

.EXAMPLE
  pwsh quality/resolve-bot-threads.ps1 -Pr 1234
  pwsh quality/resolve-bot-threads.ps1 -Pr 1234 -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Pr,
    [string]$Repo = 'danswett/agent-bridge',
    [switch]$Apply,
    [string]$RuleFile = (Join-Path $PSScriptRoot 'merge-rule.yml'),
    [switch]$ParseRuleOnly
)

$ErrorActionPreference = 'Stop'
$owner, $name = $Repo -split '/', 2
$botLogins = @('copilot-pull-request-reviewer', 'copilot', 'chatgpt-codex-connector', 'codex')
$families = 'claude|gpt|gemini|grok|mai|llama|mistral|qwen|deepseek'

function Get-Family([string]$text) {
    [regex]::Matches($text, "(?<![a-z0-9])($families)(?=[-\s.\d]|$)", 'IgnoreCase') |
        ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Select-Object -Unique
}

# Each key must start its own line at the indent the rule file gives it, so a comment, another
# section's key, or a longer name (independent-review-2) can't stand in for the one wanted.
$rule = Get-Content $RuleFile -Raw
$section = { param($name) if ($rule -match "(?m)^${name}:[ \t]*\r?\n((?:[ \t]+[^\r\n]*\r?\n?|[ \t]*\r?\n)*)") { $Matches[1] } else { '' } }
$review = & $section 'independent_review'
if ($review -notmatch '(?m)^[ \t]+posters:[ \t]*\[([^\]\r\n]*)\][ \t]*(#.*)?\r?$') { throw 'quality/merge-rule.yml has no independent_review.posters.' }
$posters = $Matches[1] -split ',' | ForEach-Object { $_.Trim().Trim('"''') } | Where-Object { $_ }
if ($review -notmatch '(?m)^[ \t]+context:[ \t]*independent-review[ \t]*(#.*)?\r?$') { throw 'quality/merge-rule.yml has no independent_review.context of exactly independent-review.' }
$waiver = & $section 'copilot_waiver'
if ($waiver -notmatch '(?m)^[ \t]+second_review:[ \t]*independent-review-2[ \t]*(#.*)?\r?$') { throw 'quality/merge-rule.yml has no copilot_waiver.second_review of exactly independent-review-2.' }
$secondContext = 'independent-review-2'
if ($ParseRuleOnly) { [pscustomobject]@{ posters = $posters; second = $secondContext } | ConvertTo-Json -Compress; return }

$query = @'
query($owner:String!,$name:String!,$pr:Int!,$after:String){
  repository(owner:$owner,name:$name){ pullRequest(number:$pr){
    headRefOid
    reviewThreads(first:50,after:$after){
      pageInfo{hasNextPage endCursor}
      nodes{ id isResolved path
        comments(first:100){ pageInfo{hasNextPage} nodes{ author{ login __typename } } } } } } } }
'@

$head = $null
$threads = @()
$after = $null
do {
    $args = @('api', 'graphql', '-f', "query=$query", '-F', "owner=$owner", '-F', "name=$name", '-F', "pr=$Pr")
    if ($after) { $args += @('-F', "after=$after") }
    $data = (& gh @args | ConvertFrom-Json).data.repository.pullRequest
    if (-not $data) { throw "PR #$Pr not found in $Repo." }
    $head = $data.headRefOid
    $threads += $data.reviewThreads.nodes
    $after = if ($data.reviewThreads.pageInfo.hasNextPage) { $data.reviewThreads.pageInfo.endCursor } else { $null }
} while ($after)

# The statuses endpoint lists the whole history, newest first, with each status's creator.
$statuses = & gh api --paginate "repos/$Repo/commits/$head/statuses?per_page=100" | ConvertFrom-Json
function Get-Receipt([string]$context) {
    $statuses | Where-Object { $_.context -eq $context -and $posters -contains $_.creator.login } | Select-Object -First 1
}
$first = Get-Receipt 'independent-review'
$second = Get-Receipt $secondContext

$problems = @()
if (-not $first -or $first.state -ne 'success') { $problems += "no success independent-review receipt on $head" }
if (-not $second -or $second.state -ne 'success') { $problems += "no success $secondContext receipt on $head" }
if ($first -and $second -and $first.state -eq 'success' -and $second.state -eq 'success') {
    $a = @(Get-Family $first.description); $b = @(Get-Family $second.description)
    if ($a.Count -eq 0 -or $b.Count -eq 0 -or @($a | Where-Object { $b -contains $_ }).Count -gt 0) {
        $problems += 'the two receipts must each name a model, from different families'
    }
}
if ($problems) {
    Write-Error "Refusing to resolve threads on #$Pr ($head): $($problems -join '; ')."
    exit 1
}

$mine = @()
foreach ($t in $threads) {
    if ($t.isResolved) { continue }
    if ($t.comments.pageInfo.hasNextPage) { continue }
    $comments = @($t.comments.nodes)
    if ($comments.Count -eq 0) { continue }
    $botOnly = -not ($comments | Where-Object {
        -not ($_.author -and $_.author.__typename -eq 'Bot' -and $botLogins -contains ($_.author.login -replace '\[bot\]$', '').ToLowerInvariant())
    })
    if ($botOnly) { $mine += $t }
}

if (-not $mine) { Write-Host "#$Pr ($head): no unresolved bot-only threads."; exit 0 }

$mutation = 'mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ thread{ id isResolved } } }'
foreach ($t in $mine) {
    $label = "$($t.id) ($($t.path))"
    if ($Apply) {
        & gh api graphql -f "query=$mutation" -F "id=$($t.id)" | Out-Null
        Write-Host "Resolved $label"
    } else {
        Write-Host "Would resolve $label"
    }
}
if (-not $Apply) { Write-Host "Dry run: $($mine.Count) thread(s). Re-run with -Apply to resolve." }
