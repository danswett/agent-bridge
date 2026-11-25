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
query($cursor:String,$first:Boolean! = true) {
  rateLimit { remaining limit }
  repository(owner:"danswett",name:"agent-bridge") {
    main: ref(qualifiedName:"refs/heads/main") @include(if:$first) { target { oid } }
    rule: object(expression:"main:quality/merge-rule.yml") @include(if:$first) { ... on Blob { oid text } }
    pullRequests(states:OPEN,baseRefName:"main",first:100,after:$cursor,orderBy:{field:CREATED_AT,direction:ASC}) {
      pageInfo { hasNextPage endCursor }
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
$cursor = $null; $snapshot = $null; $prs = @()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
do {
    $argsForPage = @('api', 'graphql', '-f', "query=$query")
    if ($cursor) { $argsForPage += @('-F', 'first=false', '-f', "cursor=$cursor") }
    $answer = & gh @argsForPage | ConvertFrom-Json -AsHashtable
    $connection = $answer.data.repository.pullRequests
    if ($LASTEXITCODE -ne 0 -or $answer.errors -or $connection.pageInfo.hasNextPage -isnot [bool] -or
        $null -eq $connection.nodes) { throw 'GitHub read failed or open list incomplete; no plan.' }
    $rate = $answer.data.rateLimit
    if (-not $rate.limit -or $rate.remaining -lt ($rate.limit / 10)) { throw 'GitHub budget below 10 percent; no plan.' }
    if (-not $snapshot) { $snapshot = $answer.data.repository }
    $prs += $connection.nodes
    $cursor = $connection.pageInfo.endCursor
    if ($connection.pageInfo.hasNextPage -and (-not $cursor -or -not $seen.Add($cursor))) {
        throw 'Missing or repeated pagination cursor; no plan.'
    }
} while ($connection.pageInfo.hasNextPage)
$main = $snapshot.main.target.oid
if ($main -cnotmatch '^[a-f0-9]{40}$') { throw 'Missing main-tip proof; no plan.' }
$rule = $snapshot.rule.text
$posters = @(Inline (Section 'independent_review') 'posters')
$mainExemptLabels = @(Inline (Section 'main_green') 'exempt_labels')
$mainExemptPaths = @(Inline (Section 'main_green') 'exempt_paths')
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
$rows = @(foreach ($pr in $prs) {
    $why = @(); $head = $pr.headRefOid; $commit = $pr.commits.nodes[0].commit; $compare = $pr.headRef.compare
    if ($head -notmatch '^[a-f0-9]{40}$' -or $commit.oid -cne $head) { $why += 'Head/receipt snapshot mismatch' }
    if ($pr.state -cne 'OPEN' -or $pr.baseRefName -cne 'main' -or $pr.isDraft -or $pr.isCrossRepository -or
        $pr.mergeStateStatus -cnotin @('CLEAN', 'BEHIND')) { $why += 'Draft, fork, or not clean/behind' }
    if ($pr.labels.totalCount -ne @($pr.labels.nodes).Count -or
        @($pr.labels.nodes | Where-Object { $_.name -in $holds }).Count) { $why += 'Held or incomplete labels' }
    if ($pr.files.pageInfo.hasNextPage -or $pr.files.totalCount -ne @($pr.files.nodes).Count -or
        -not $pr.files.totalCount) { $why += 'Incomplete files' }
    if (-not $compare -or $compare.baseTarget.oid -cne $head -or $compare.headTarget.oid -cne $main -or
        $null -eq $compare.aheadBy -or $compare.aheadBy -lt 0) { $why += 'Unproved main comparison' }
    function Latest($context) {
        $commit.status.contexts | Where-Object { $_.context -ceq $context } |
            Sort-Object createdAt -Descending | Select-Object -First 1
    }
    $merge = Latest 'merge-rule'
    $first = Latest 'independent-review'; $second = Latest 'independent-review-2'
    if ($merge.state -cne 'SUCCESS' -or $merge.creator.login -notin @($poster)) { $why += 'No App merge-rule success on head' }
    foreach ($receipt in @($first, $second)) {
        if ($receipt -and $receipt.creator.login -notin $posters) { $why += 'Unapproved newest receipt creator' }
    }
    $mainExempt = @($pr.labels.nodes | Where-Object { $_.name -in $mainExemptLabels }).Count -gt 0 -and
        $pr.files.pageInfo.hasNextPage -ceq $false -and $pr.files.totalCount -eq @($pr.files.nodes).Count -and
        $pr.files.totalCount -gt 0 -and @($pr.files.nodes | Where-Object {
            $_.changeType -ceq 'RENAMED' -or -not (Matches $_.path $mainExemptPaths)
        }).Count -eq 0
    if (-not $mainExempt) { $why += 'Main-green unverified; App success alone is insufficient' }
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
    $update = $compare.aheadBy -gt 0 -or $pr.baseRefOid -cne $main -or $pr.mergeStateStatus -ceq 'BEHIND'
    [pscustomobject]@{
        url = $pr.url; head = $head; rulesBlob = $snapshot.rule.oid; eligible = $eligible; reason = $why -join '; '
        action = if (-not $eligible) { 'HOLD' } elseif ($update) { 'WOULD_UPDATE_BRANCH' } else { 'WOULD_MERGE' }
        command = if ($eligible -and -not $update) { "gh pr merge $($pr.number) --repo danswett/agent-bridge --squash --match-head-commit $head" } else { $null }
        afterUpdate = if ($eligible -and $update) { 'Quality 19:01 own-hunk carry proof and re-stamped exact-head receipts; new-head CI; re-plan. Overlap/conflict needs delta reads.' } else { $null }
        notBeforeMinutes = if ($eligible) { $slot; $slot += 4 } else { $null }
    }
})
ConvertTo-Json -InputObject $rows -Depth 4
