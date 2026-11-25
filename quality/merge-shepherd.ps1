#Requires -Version 7.4
<#
.SYNOPSIS
Prints a one-shot, read-only landing plan. No apply mode, polling, or GitHub mutations.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
function Section($name) {
    $m = [regex]::Match($rule, "(?m)^${name}:[ \t]*\r?\n((?:[ \t]+[^\r\n]*\r?\n?|[ \t]*\r?\n)*)")
    if (-not $m.Success) { throw "Missing rule section: $name" }
    $m.Groups[1].Value
}
function Inline($text, $key) {
    $m = [regex]::Match($text, "(?m)^[ \t]*${key}:[ \t]*\[([^\]\r\n]+)\]")
    if (-not $m.Success) { throw "Unsupported rule list: $key" }
    $m.Groups[1].Value -split ',' | ForEach-Object { $_.Trim().Trim('"''') }
}
function Matches($path, $globs) {
    foreach ($g in $globs) {
        $rx = [regex]::Escape($g).Replace('\*\*/', '(?:.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
        if ([regex]::IsMatch($path, "\A$rx\z", 'IgnoreCase,Singleline')) { return $true }
    }
    $false
}
function Family($text) {
    @([regex]::Matches("$text", '(?i)(?<![a-z0-9])(claude|gpt|gemini|grok|mai|llama|mistral|qwen|deepseek)(?=[-\s.\d]|$)') |
        ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Select-Object -Unique)
}
$query = @'
query {
  rateLimit { remaining limit }
  repository(owner:"danswett",name:"agent-bridge") {
    rule: object(expression:"main:quality/merge-rule.yml") { ... on Blob { oid text } }
    pullRequests(states:OPEN,baseRefName:"main",first:100,orderBy:{field:CREATED_AT,direction:ASC}) {
      pageInfo { hasNextPage }
      nodes { number url title state isDraft isCrossRepository baseRefName baseRefOid headRefOid mergeStateStatus
        headRef { compare(headRef:"main") { aheadBy baseTarget { oid } headTarget { oid } } }
        labels(first:100) { totalCount nodes { name } }
        files(first:100) { totalCount pageInfo { hasNextPage } nodes { path changeType } }
        commits(last:1) { nodes { commit { oid status { contexts {
          context state description createdAt targetUrl creator { login }
        } } } } }
      }
    }
  }
}
'@
$answer = & gh api graphql -f "query=$query" | ConvertFrom-Json -AsHashtable
if ($LASTEXITCODE -ne 0 -or $answer.errors -or -not $answer.data.repository.pullRequests -or
    $answer.data.repository.pullRequests.pageInfo.hasNextPage) { throw 'GitHub read failed or open list incomplete; no plan.' }
$rate = $answer.data.rateLimit
if (-not $rate.limit -or $rate.remaining -lt ($rate.limit / 10)) { throw 'GitHub budget below 10 percent; no plan.' }
$rule = $answer.data.repository.rule.text
$posters = @(Inline (Section 'independent_review') 'posters')
$holds = @(Inline $rule 'hold_labels'); $security = Section 'security_marks'
$securityLabels = @(Inline $security 'labels')
$prefix = [regex]::Match($security, '(?m)^  title_prefix: ([^\r\n]+)').Groups[1].Value.Trim()
$poster = [regex]::Match($rule, '(?m)^status_poster: ([a-z0-9-]+)\r?$').Groups[1].Value
$patterns = @([regex]::Matches((Section 'sensitive_paths'), '(?m)^  - ([^#\r\n]+)') |
    ForEach-Object { $_.Groups[1].Value.Trim().Trim('"''') })
$single = @([regex]::Matches((Section 'copilot_waiver'), '(?m)^    - ([^#\r\n]+)') |
    ForEach-Object { $_.Groups[1].Value.Trim().Trim('"''') })
if (-not $poster -or -not $prefix -or -not $patterns.Count -or -not $single.Count -or
    (Section 'independent_review') -notmatch '(?m)^  context: independent-review\r?$' -or
    (Section 'copilot_waiver') -notmatch '(?m)^  second_review: independent-review-2\r?$') { throw 'Unsupported merge-rule configuration.' }
$slot = 0
$rows = @(foreach ($pr in $answer.data.repository.pullRequests.nodes) {
    $why = @(); $head = $pr.headRefOid; $commit = $pr.commits.nodes[0].commit; $compare = $pr.headRef.compare
    if ($head -notmatch '^[a-f0-9]{40}$' -or $commit.oid -cne $head) { $why += 'Head/receipt snapshot mismatch' }
    if ($pr.state -cne 'OPEN' -or $pr.baseRefName -cne 'main' -or $pr.isDraft -or $pr.isCrossRepository -or
        $pr.mergeStateStatus -cnotin @('CLEAN', 'BEHIND')) { $why += 'Draft, fork, or not clean/behind' }
    if ($pr.labels.totalCount -ne @($pr.labels.nodes).Count -or
        @($pr.labels.nodes | Where-Object { $_.name -in $holds }).Count) { $why += 'Held or incomplete labels' }
    if ($pr.files.pageInfo.hasNextPage -or $pr.files.totalCount -ne @($pr.files.nodes).Count -or
        -not $pr.files.totalCount) { $why += 'Incomplete files' }
    if (-not $compare -or $compare.baseTarget.oid -cne $head -or $compare.headTarget.oid -cne $pr.baseRefOid -or
        $null -eq $compare.aheadBy -or $compare.aheadBy -lt 0) { $why += 'Unproved main comparison' }
    function Latest($context, $allowed) {
        $commit.status.contexts | Where-Object { $_.context -ceq $context -and $_.creator.login -in $allowed } |
            Sort-Object createdAt -Descending | Select-Object -First 1
    }
    $merge = Latest 'merge-rule' @($poster)
    $first = Latest 'independent-review' $posters; $second = Latest 'independent-review-2' $posters
    if ($merge.state -cne 'SUCCESS') { $why += 'No App merge-rule success on head' }
    $a = @(Family $first.description); $b = @(Family $second.description)
    if ($first.state -cne 'SUCCESS' -or -not $first.targetUrl -or -not $a.Count -or
        ($second -and $second.state -cne 'SUCCESS')) { $why += 'Missing or unsuccessful exact-head receipt' }
    $needsTwo = $pr.title.TrimStart().StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        @($pr.labels.nodes | Where-Object { $_.name -in $securityLabels }).Count -gt 0
    foreach ($file in $pr.files.nodes) {
        if ($file.changeType -ceq 'RENAMED' -or $file.path -match '[\p{Cc}\uFFFD]' -or
            ((Matches $file.path $patterns) -and -not (Matches $file.path $single))) { $needsTwo = $true }
    }
    if ($needsTwo -and ($second.state -cne 'SUCCESS' -or -not $second.targetUrl -or -not $b.Count -or
        @($a | Where-Object { $_ -in $b }).Count)) { $why += 'Two independent families required; Copilot never counts' }
    $eligible = $why.Count -eq 0
    $update = $compare.aheadBy -gt 0 -or $pr.mergeStateStatus -ceq 'BEHIND'
    [pscustomobject]@{
        url = $pr.url; head = $head; rulesBlob = $answer.data.repository.rule.oid; eligible = $eligible; reason = $why -join '; '
        action = if (-not $eligible) { 'HOLD' } elseif ($update) { 'WOULD_UPDATE_BRANCH' } else { 'WOULD_MERGE' }
        command = if ($eligible -and -not $update) { "gh pr merge $($pr.number) --repo danswett/agent-bridge --squash --match-head-commit $head" } else { $null }
        afterUpdate = if ($eligible -and $update) { 'Quality 19:01 own-hunk carry proof and re-stamped exact-head receipts; new-head CI; re-plan. Overlap/conflict needs delta reads.' } else { $null }
        notBeforeMinutes = if ($eligible) { $slot; $slot += 4 } else { $null }
    }
})
ConvertTo-Json -InputObject $rows -Depth 4
