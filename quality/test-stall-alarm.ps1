#Requires -Version 7.4
param([Parameter(Mandatory)][ValidateRange(1,8)][int]$Case)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'stall-alarm.ps1')
$now = [DateTimeOffset]'2026-11-18T07:50:00Z'
$config = @{ clean_pr_threshold = 5; minutes_without_merge = 30 }
$script:calls = [Collections.Generic.List[object]]::new()
$script:reply = $null
$script:failRead = $false
function gh {
    $script:calls.Add(@($args))
    $global:LASTEXITCODE = 0
    if ($args[1] -eq 'graphql') {
        if ($script:failRead) { $global:LASTEXITCODE = 1; return }
        return ($script:reply | ConvertTo-Json -Depth 30 -Compress)
    }
    if ($args[0] -ne 'api' -or $args[1] -ne '-X' -or $args[2] -notin 'POST','PATCH') {
        throw 'Only the single GraphQL read and REST writes are allowed.'
    }
    '{"number":42}'
}
function Pr([int]$number) {
    @{ number = $number; baseRefName = 'main'; isDraft = $false; headRefOid = "head$number"
        createdAt = $now.AddHours(-2).ToString('o')
        labels = @{ nodes = @(); pageInfo = @{ hasNextPage = $false } }
        commits = @{ nodes = @(@{ commit = @{ oid = "head$number"; status = @{ context = @{ state = 'SUCCESS' } } } }) } }
}
function Snapshot([int]$count = 6, [int]$minutes = 31, [switch]$Open) {
    $prs = @(1..$count | ForEach-Object { Pr $_ })
    @{ data = @{ repository = @{
        queue = @{ nodes = $prs; totalCount = $count; pageInfo = @{ hasNextPage = $false } }
        merged = @{ nodes = @(@{ mergedAt = $now.AddMinutes(-$minutes).ToString('o')
            updatedAt = $now.AddMinutes(-$minutes).ToString('o') }); pageInfo = @{ hasNextPage = $false } }
        alerts = @{ nodes = @(if ($Open) { @{ number = 42; title = 'Ops: review-clean merge queue stalled' } })
            pageInfo = @{ hasNextPage = $false } }
    } } }
}
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Evaluate($snapshot) { Test-MergeStall -Snapshot $snapshot -Config $config -Now $now }
function Publish {
    [CmdletBinding()]
    param($snapshot)
    $script:reply = $snapshot
    Invoke-StallAlarm -Repository example/repo -Config $config -Now $now -Mentions '@main-green @chief'
}
switch ($Case) {
    1 {
        $result = Evaluate (Snapshot)
        Check ($result.Known -and $result.Stalled -and $result.PullRequests.Count -eq 6) 'Six clean PRs and 31 minutes must alarm.'
        $config.clean_pr_threshold = 6
        Check (-not (Evaluate (Snapshot)).Stalled) 'The PR threshold must come from configuration.'
        $config.clean_pr_threshold = 5; $config.minutes_without_merge = 32
        Check (-not (Evaluate (Snapshot)).Stalled) 'The time threshold must come from configuration.'
    }
    2 { Check (-not (Evaluate (Snapshot 5)).Stalled) 'Exactly five PRs must not alarm.' }
    3 {
        Check (-not (Evaluate (Snapshot 6 29)).Stalled) 'A merge 29 minutes ago must not alarm.'
        Check ((Evaluate (Snapshot 6 30)).Stalled) 'Thirty minutes without a merge meets the time threshold.'
    }
    4 {
        $s = Snapshot 6
        $s.data.repository.queue.nodes[0].isDraft = $true
        $s.data.repository.queue.nodes[1].labels.nodes = @(@{ name = 'hold' })
        $s.data.repository.queue.nodes[2].commits.nodes[0].commit.status.context.state = 'PENDING'
        $s.data.repository.queue.nodes[3].commits.nodes[0].commit.status.context.state = 'FAILURE'
        $s.data.repository.queue.nodes[4].baseRefName = 'other'
        $s.data.repository.queue.nodes[5].labels.nodes = @(@{ name = 'do-not-merge' })
        $result = Evaluate $s
        Check ($result.Known -and -not $result.Stalled -and $result.PullRequests.Count -eq 0) 'Ineligible PRs must not count.'
    }
    5 {
        Publish (Snapshot 6 2 -Open)
        Check ($script:calls.Count -eq 3) 'Recovery must comment and close.'
        Check ($script:calls[1][2] -eq 'POST' -and $script:calls[1][3] -eq 'repos/example/repo/issues/42/comments') 'Recovery needs a comment.'
        Check ($script:calls[2][2] -eq 'PATCH' -and ($script:calls[2] -join ' ') -match 'state=closed') 'Recovery must close the same issue.'
    }
    6 {
        $partial = Snapshot -Open; $partial.data.repository.queue.pageInfo.hasNextPage = $true
        $errored = Snapshot -Open; $errored.errors = @(@{ message = 'partial' })
        $mismatch = Snapshot -Open; $mismatch.data.repository.queue.nodes[0].commits.nodes[0].commit.oid = 'old'
        $history = Snapshot -Open; $history.data.repository.merged.pageInfo.hasNextPage = $true
        $history.data.repository.merged.nodes[0].updatedAt = $now.ToString('o')
        foreach ($s in @($partial, $errored, $mismatch, $history, @{ data = $null })) {
            $script:calls.Clear(); $warnings = @()
            Publish $s -WarningVariable warnings
            Check ($warnings.Count -gt 0 -and $script:calls.Count -eq 1) 'Unknown data must warn, with no alert or close.'
        }
        $script:calls.Clear(); $script:failRead = $true; $warnings = @()
        Invoke-StallAlarm -Repository example/repo -Config $config -Now $now -Mentions '@main-green @chief' -WarningVariable warnings
        Check ($warnings.Count -gt 0 -and $script:calls.Count -eq 1) 'A failed read must warn without writes.'
    }
    7 {
        Publish (Snapshot -Open)
        Check ($script:calls.Count -eq 2 -and $script:calls[1][3] -eq 'repos/example/repo/issues/42/comments') 'Repeated stalls update the one issue.'
        Check (($script:calls[1] -join ' ') -match '@main-green @chief') 'Both intended recipients must be mentioned.'
    }
    8 {
        Publish (Snapshot)
        Check ($script:calls.Count -eq 2 -and $script:calls[0][1] -eq 'graphql') 'Exactly one read and one create are allowed.'
        Check ($script:calls[1][3] -eq 'repos/example/repo/issues') 'The first stall opens one ops issue.'
        $query = $script:calls[0] -join ' '
        Check ($query -match 'baseRefName: "main"' -and $query -match 'mergedAt' -and $query -match 'merge-rule') 'The single read must contain queue and merge evidence.'
    }
}
Write-Host "PASS: stall alarm case $Case"
