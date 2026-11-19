#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$subject = Join-Path $PSScriptRoot 'merge-shepherd.ps1'
$script:passed = 0; $script:failed = 0
$script:deployedRule = Get-Content -Raw (Join-Path $PSScriptRoot 'merge-rule.yml')
function Get-Content { $global:MergeShepherdFake.localRule }
function gh {
    $fake = $global:MergeShepherdFake
    if ($args.Count -notin @(4, 8) -or $args[0] -cne 'api' -or $args[1] -cne 'graphql' -or
        $args[2] -cne '-f' -or $args[3] -notmatch '^query=query\b' -or $args[3] -match '\bmutation\b') {
        throw 'Only read-only GraphQL pages are allowed.'
    }
    if ($fake.calls -gt 0 -and ($args[4] -cne '-F' -or $args[5] -cne 'first=false' -or
        $args[6] -cne '-f' -or $args[7] -cne "cursor=$($fake.pages[$fake.calls - 1].data.repository.pullRequests.pageInfo.endCursor)")) {
        throw 'Subsequent pages must use the previous cursor and omit repeated rules.'
    }
    if ($fake.calls -ge $fake.pages.Count) { throw 'Unexpected extra read.' }
    $page = $fake.pages[$fake.calls++]
    $global:LASTEXITCODE = $fake.exitCode
    $page | ConvertTo-Json -Depth 30 -Compress
}
function Status($context, $creator, $description = 'gpt-6.1-sol CLEAN', $state = 'SUCCESS') {
    @{ context = $context; creator = @{ login = $creator }; description = $description; state = $state
        createdAt = '2026-11-18T19:00:00Z'; targetUrl = 'https://github.com/danswett/agent-bridge/pull/1#issuecomment-1' }
}
function Case($name, [scriptblock]$change, $eligible = $false, $action = 'HOLD', [switch]$Reject) {
    $h = 'a' * 40; $m = 'b' * 40
    $script:pr = @{
        number = 1; url = 'https://github.com/danswett/agent-bridge/pull/1'; title = 'Ordinary change'
        state = 'OPEN'; isDraft = $false; isCrossRepository = $false; baseRefName = 'main'
        headRefOid = $h; baseRefOid = $m; mergeStateStatus = 'CLEAN'
        headRef = @{ compare = @{ aheadBy = 0; baseTarget = @{ oid = $h }; headTarget = @{ oid = $m } } }
        labels = @{ totalCount = 1; nodes = @(@{ name = 'fixes-main' }) }
        files = @{ totalCount = 1; pageInfo = @{ hasNextPage = $false }; nodes = @(@{ path = 'README.md'; changeType = 'MODIFIED' }) }
        commits = @{ nodes = @(@{ commit = @{ oid = $h; status = @{ contexts = @(
            (Status 'merge-rule' 'agent-bridge-merge-rule'), (Status 'independent-review' 'danswett')
        ) } } }) }
    }
    $script:response = @{ data = @{ rateLimit = @{ remaining = 4800; limit = 5000 }
        repository = @{ main = @{ target = @{ oid = $m } }; rule = @{ text = $script:deployedRule; oid = 'd' * 40 }
            pullRequests = @{ pageInfo = @{ hasNextPage = $false; endCursor = 'page-1' }; nodes = @($script:pr) } } } }
    $script:exitCode = 0; $script:localRule = $script:deployedRule
    $script:nextPages = @()
    try {
        & $change
        $pages = @($script:response) + $script:nextPages
        $global:MergeShepherdFake = @{ calls = 0; exitCode = $script:exitCode; pages = $pages; localRule = $script:localRule }
        $rows = @(& $subject | ConvertFrom-Json)
        if ($Reject) { throw 'Expected a read failure, not a plan.' }
        $expectedRows = @($pages | ForEach-Object { $_.data.repository.pullRequests.nodes }).Count
        if ($rows.Count -ne $expectedRows -or $rows[0].eligible -ne $eligible -or $rows[0].action -cne $action -or
            $global:MergeShepherdFake.calls -ne $pages.Count) {
            throw "Unexpected plan: $($rows | ConvertTo-Json -Compress)"
        }
        if ($eligible -and $action -eq 'WOULD_MERGE' -and $rows[0].command -cne
            "gh pr merge 1 --repo danswett/agent-bridge --squash --match-head-commit $h") { throw 'Merge must pin the full head and retain branches.' }
        if ($action -eq 'WOULD_UPDATE_BRANCH' -and $rows[0].afterUpdate -notmatch '19:01.*own.*hunk.*CI') {
            throw 'An update must invalidate merge readiness and require own-hunk carry proof plus CI.'
        }
        if ($rows.Count -gt 1 -and $rows[1].notBeforeMinutes -ne 4) { throw 'Plans must space eligible PRs four minutes apart.' }
        $script:passed++; "PASS $name"
    } catch {
        if ($Reject -and $_ -notmatch 'Expected a read failure') { $script:passed++; "PASS $name" }
        else { $script:failed++; "FAIL ${name}: $_" }
    }
}
Case 'a main-exempt exact-head receipt and App verdict can enter the queue' {} $true 'WOULD_MERGE'
Case 'a behind head proposes only an update and revalidation' { $pr.headRef.compare.aheadBy = 2 } $true 'WOULD_UPDATE_BRANCH'
Case 'a required BEHIND state cannot propose a merge' { $pr.mergeStateStatus = 'BEHIND'; $pr.headRef.compare.aheadBy = 1 } $true 'WOULD_UPDATE_BRANCH'
Case 'drafts hold' { $pr.isDraft = $true }
Case 'hold labels win over stale green' { $pr.labels.nodes = @(@{ name = 'hold' }); $pr.labels.totalCount = 1 }
Case 'unread labels hold' { $pr.labels.totalCount = 2 }
Case 'unresolved or blocked state holds' { $pr.mergeStateStatus = 'BLOCKED' }
Case 'conflicts hold' { $pr.mergeStateStatus = 'DIRTY' }
Case 'unknown merge state holds' { $pr.mergeStateStatus = 'UNKNOWN' }
Case 'fork comparisons cannot prove target-main ancestry' { $pr.isCrossRepository = $true }
Case 'a stale head receipt is never carried implicitly' { $pr.commits.nodes[0].commit.oid = 'c' * 40 }
Case 'missing receipts hold' { $pr.commits.nodes[0].commit.status.contexts = @((Status 'merge-rule' 'agent-bridge-merge-rule')) }
Case 'an unapproved poster cannot supply a receipt' { $pr.commits.nodes[0].commit.status.contexts[1].creator.login = 'stranger' }
Case 'a non-App merge-rule success cannot authorize a plan' { $pr.commits.nodes[0].commit.status.contexts[0].creator.login = 'danswett' }
Case 'a later failed receipt replaces an old success' {
    $s = Status 'independent-review' 'danswett' 'gpt-6.1-sol P1' 'FAILURE'; $s.createdAt = '2026-11-18T20:00:00Z'
    $pr.commits.nodes[0].commit.status.contexts += $s
}
Case 'a pending merge-rule holds' { $pr.commits.nodes[0].commit.status.contexts[0].state = 'PENDING' }
Case 'sensitive files require a second family even with green merge-rule' { $pr.files.nodes[0].path = 'quality/run.ps1' }
Case 'untrusted local rules cannot remove the second reader' { $pr.files.nodes[0].path = 'quality/run.ps1'; $script:localRule = $script:deployedRule.Replace('  - quality/**', '') }
Case 'the configured status-row exception still permits one reader' { $pr.files.nodes[0].path = 'docs/status.md' } $true 'WOULD_MERGE'
Case 'a security title requires two families' { $pr.title = 'Security: ordinary path' }
Case 'a security label requires two families' { $pr.labels.nodes = @(@{ name = 'security' }); $pr.labels.totalCount = 1 }
Case 'a failed second receipt also holds ordinary changes' { $pr.commits.nodes[0].commit.status.contexts += Status 'independent-review-2' 'danswett' 'claude-sonnet-5.5 P1' 'FAILURE' }
Case 'sensitive two-family exact-head receipts pass' {
    $pr.files.nodes[0].path = 'quality/run.ps1'
    $pr.commits.nodes[0].commit.status.contexts += Status 'independent-review-2' 'agent-bridge-receipts' 'claude-sonnet-5.5 CLEAN'
} $true 'WOULD_MERGE'
Case 'same-family receipts cannot supply two readers' {
    $pr.files.nodes[0].path = 'quality/run.ps1'; $pr.commits.nodes[0].commit.status.contexts += Status 'independent-review-2' 'danswett'
}
Case 'renames conservatively require two families' { $pr.files.nodes[0].changeType = 'RENAMED' }
Case 'incomplete file lists hold' { $pr.files.pageInfo.hasNextPage = $true }
Case 'a moving main invalidates the comparison' { $pr.headRef.compare.headTarget.oid = 'c' * 40 }
Case 'a missing comparison holds' { $pr.headRef.compare = $null }
Case 'eligible PRs get distinct four-minute slots' {
    $other = $pr | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable; $other.number = 2
    $response.data.repository.pullRequests.nodes += $other
} $true 'WOULD_MERGE'
Case 'a partial open list fails closed' { $response.data.repository.pullRequests.pageInfo.hasNextPage = $true } -Reject
Case 'GraphQL partial errors fail closed' { $response.errors = @(@{ message = 'unavailable' }) } -Reject
Case 'an exhausted budget fails closed' { $response.data.rateLimit.remaining = 499 } -Reject
Case 'gh failure is not an empty queue' { $script:exitCode = 1 } -Reject
Case 'missing deployed rules cannot fall back to local rules' { $response.data.repository.rule.text = $null } -Reject
Case 'a lagging PR base still plans an update against the real main tip' {
    $pr.baseRefOid = 'c' * 40; $pr.headRef.compare.aheadBy = 3
} $true 'WOULD_UPDATE_BRANCH'
Case 'an old comparison cannot impersonate the actual main tip' { $response.data.repository.main.target.oid = 'e' * 40 }
Case 'missing main-tip proof fails closed' { $response.data.repository.main = $null } -Reject
Case '149 open PRs produce a complete plan across two pages' {
    $other = $response | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
    $response.data.repository.pullRequests.nodes = @(1..100 | ForEach-Object { $pr })
    $response.data.repository.pullRequests.pageInfo.hasNextPage = $true
    $other.data.repository.pullRequests.nodes = @(101..149 | ForEach-Object { $pr })
    $other.data.repository.Remove('rule'); $other.data.repository.Remove('main'); $script:nextPages = @($other)
} $true 'WOULD_MERGE'
Case 'a broken second page cannot return a partial plan' {
    $response.data.repository.pullRequests.pageInfo.hasNextPage = $true
    $script:nextPages = @(@{ errors = @(@{ message = 'page unavailable' }) })
} -Reject
Case 'a repeated pagination cursor fails closed' {
    $response.data.repository.pullRequests.pageInfo.hasNextPage = $true
    $script:nextPages = @($response)
} -Reject
Case 'an unapproved newest second receipt cannot hide an authorized failure' {
    $pr.commits.nodes[0].commit.status.contexts += Status 'independent-review-2' 'stranger'
}
Case 'an unapproved newest first receipt holds' { $pr.commits.nodes[0].commit.status.contexts[1].creator.login = 'stranger' }
Case 'a stale App success cannot prove main green for an ordinary PR' {
    $pr.labels = @{ totalCount = 0; nodes = @() }
}
Case 'the deployed path-only main-green exception permits a test-only PR' {
    $pr.labels = @{ totalCount = 0; nodes = @() }; $pr.files.nodes[0].path = 'pwa/test/example.test.js'
} $true 'WOULD_MERGE'
Case 'one nonexempt file keeps main green unverified' {
    $pr.labels = @{ totalCount = 0; nodes = @() }
    $pr.files.nodes += @{ path = 'pwa/test/example.test.js'; changeType = 'MODIFIED' }; $pr.files.totalCount = 2
}
"Tests: $script:passed passed, $script:failed failed"
if ($script:failed) { throw "$script:failed merge-shepherd checks failed" }
