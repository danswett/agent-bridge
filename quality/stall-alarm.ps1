#Requires -Version 7.4
[CmdletBinding()]
param([string]$Repository, [string]$Mentions)

function Read-StallAlarmConfig {
    $yaml = Get-Content (Join-Path $PSScriptRoot 'merge-rule.yml') -Raw
    $section = [regex]::Match($yaml, '(?m)^stall_alarm:\r?\n  clean_pr_threshold: ([1-9][0-9]*)\r?\n  minutes_without_merge: ([1-9][0-9]*)\s*$')
    if (-not $section.Success) { throw 'Missing or invalid stall_alarm thresholds in quality/merge-rule.yml.' }
    @{ clean_pr_threshold = [int]$section.Groups[1].Value; minutes_without_merge = [int]$section.Groups[2].Value }
}

function Test-MergeStall {
    [CmdletBinding()]
    param($Snapshot, [hashtable]$Config, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    try {
        if ($Config.clean_pr_threshold -lt 1 -or $Config.minutes_without_merge -lt 1) { throw 'Invalid thresholds.' }
        if (-not $Snapshot -or $Snapshot.errors -or -not $Snapshot.data.repository) { throw 'Partial or errored GraphQL result.' }
        $r = $Snapshot.data.repository
        foreach ($connection in @($r.queue, $r.merged, $r.alerts)) {
            if ($null -eq $connection.nodes -or $null -eq $connection.pageInfo.hasNextPage) { throw 'Missing connection data.' }
        }
        if ($r.queue.pageInfo.hasNextPage -or $r.alerts.pageInfo.hasNextPage -or
            $null -eq $r.queue.totalCount -or @($r.queue.nodes).Count -ne $r.queue.totalCount) { throw 'Truncated queue or alerts.' }
        $merges = @($r.merged.nodes)
        if (-not $merges.Count) { throw 'No last merge could be established.' }
        $times = @(foreach ($merge in $merges) {
            if (-not $merge.mergedAt -or -not $merge.updatedAt) { throw 'Missing merge timestamps.' }
            $at = [DateTimeOffset]::Parse($merge.mergedAt)
            $updated = [DateTimeOffset]::Parse($merge.updatedAt)
            if ($at -gt $Now -or $updated -lt $at -or $updated -gt $Now) { throw 'Invalid merge timestamps.' }
            $at
        })
        $lastMerge = $times | Sort-Object -Descending | Select-Object -First 1
        # UPDATED_AT is not merge order: an old PR can receive a new comment.
        if ($r.merged.pageInfo.hasNextPage -and [DateTimeOffset]::Parse($merges[-1].updatedAt) -gt $lastMerge) {
            throw 'The bounded merge history cannot prove the newest mergedAt.'
        }
        $clean = @(foreach ($pr in $r.queue.nodes) {
            if (-not $pr.number -or -not $pr.baseRefName -or $pr.isDraft -isnot [bool] -or
                $null -eq $pr.labels.nodes -or $pr.labels.pageInfo.hasNextPage -ne $false -or
                @($pr.commits.nodes).Count -ne 1 -or -not $pr.headRefOid -or
                $pr.commits.nodes[0].commit.oid -cne $pr.headRefOid) { throw 'Incomplete PR evidence.' }
            if (@($pr.labels.nodes | Where-Object { -not $_.name }).Count) { throw 'Incomplete labels.' }
            if ($pr.baseRefName -ne 'main' -or $pr.isDraft -or
                @($pr.labels.nodes | Where-Object name -In 'hold','do-not-merge').Count -or
                $pr.commits.nodes[0].commit.status.context.state -ne 'SUCCESS') { continue }
            if (-not $pr.createdAt) { throw 'Missing PR age.' }
            $created = [DateTimeOffset]::Parse($pr.createdAt)
            if ($created -gt $Now) { throw 'Future PR creation time.' }
            @{ Number = [long]$pr.number; AgeMinutes = [math]::Floor(($Now - $created).TotalMinutes) }
        })
        foreach ($issue in $r.alerts.nodes) {
            if (-not $issue.number -or -not $issue.title) { throw 'Incomplete alert issue.' }
        }
        $open = @($r.alerts.nodes | Where-Object title -CEQ 'Ops: review-clean merge queue stalled')
        if ($open.Count -gt 1) { throw 'Multiple matching alert issues need operator reconciliation.' }
        $age = [math]::Floor(($Now - $lastMerge).TotalMinutes)
        @{ Known = $true; Stalled = $clean.Count -gt $Config.clean_pr_threshold -and $age -ge $Config.minutes_without_merge
            PullRequests = $clean; MergeAgeMinutes = $age; Issue = $open | Select-Object -First 1 }
    } catch {
        Write-Warning "Stall alarm inconclusive: $($_.Exception.Message) No alert or close."
        @{ Known = $false; Stalled = $false }
    }
}

function Invoke-StallAlarm {
    [CmdletBinding()]
    param([string]$Repository, [hashtable]$Config, [string]$Mentions, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    $ErrorActionPreference = 'Stop'
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
    $owner, $name = $Repository.Split('/')
    $query = @'
query($owner: String!, $name: String!) {
  repository(owner: $owner, name: $name) {
    queue: pullRequests(baseRefName: "main", states: OPEN, first: 100) {
      totalCount pageInfo { hasNextPage }
      nodes { number baseRefName isDraft headRefOid createdAt
        labels(first: 100) { nodes { name } pageInfo { hasNextPage } }
        commits(last: 1) { nodes { commit { oid status { context(name: "merge-rule") { state } } } } }
      }
    }
    merged: pullRequests(baseRefName: "main", states: MERGED, first: 100, orderBy: {field: UPDATED_AT, direction: DESC}) {
      nodes { mergedAt updatedAt } pageInfo { hasNextPage }
    }
    alerts: issues(states: OPEN, labels: ["area:ops"], first: 100) {
      nodes { number title } pageInfo { hasNextPage }
    }
  }
}
'@
    try {
        $text = @(gh api graphql -f "query=$query" -f "owner=$owner" -f "name=$name")
        if ($LASTEXITCODE) { throw 'GraphQL read failed.' }
        $result = Test-MergeStall -Snapshot (($text -join "`n") | ConvertFrom-Json) -Config $Config -Now $Now
    } catch {
        Write-Warning 'Stall alarm read failed; no alert or close.'
        return
    }
    if (-not $result.Known -or (-not $result.Stalled -and -not $result.Issue)) { return }
    if ($Mentions -cnotmatch '^@[A-Za-z0-9-]+(?:/[A-Za-z0-9_-]+)? @[A-Za-z0-9-]+(?:/[A-Za-z0-9_-]+)?$') {
        throw 'Configure exactly the Main-Green and Chief @mentions; no other alert route is permitted.'
    }
    $body = (@($Mentions, "Last merge: $($result.MergeAgeMinutes) min ago.") +
        @($result.PullRequests | ForEach-Object { "#$($_.Number): $($_.AgeMinutes) min old." })) -join "`n"
    function Write-Issue([string[]]$Arguments) {
        gh api @Arguments | Out-Null
        if ($LASTEXITCODE) { throw 'Stall alarm issue write failed.' }
    }
    if ($result.Issue) {
        $path = "repos/$Repository/issues/$($result.Issue.number)"
        if (-not $result.Stalled) { $body = "Merge stall cleared.`n$body" }
        Write-Issue @('-X', 'POST', "$path/comments", '-f', "body=$body")
        if (-not $result.Stalled) { Write-Issue @('-X', 'PATCH', $path, '-f', 'state=closed') }
    } elseif ($result.Stalled) {
        Write-Issue @('-X', 'POST', "repos/$Repository/issues", '-f', 'title=Ops: review-clean merge queue stalled',
            '-f', 'labels[]=area:ops', '-f', "body=$body")
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-StallAlarm -Repository $Repository -Config (Read-StallAlarmConfig) -Mentions $Mentions
}
