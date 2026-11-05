# Checks build/review-receipt.ps1 against a stand-in for gh and the committed synthetic fixtures in
# build/test-review-receipt: nothing reaches GitHub, nothing is posted, and every call the script
# makes goes to the stand-in, which refuses any it doesn't know.
#
#   pwsh build/test-review-receipt.ps1
#
# The fixtures mirror what Copilot CLI 1.0.105 writes (events.jsonl schema version 1) and what
# `gh pr checks --json` prints, with made-up identities: events.jsonl is a session whose coordinator
# captures green CI on the head (AGENTS.md rule 5) and then starts a code-review agent, which reads
# two changed files and the diff and ends `VERDICT: CLEAN`; checks.json is the pull request's checks
# as read again afterwards; ledger.json is the read ledger the script must replay from that log.
# Each case changes one thing in memory and expects the script to refuse it.
#
# What it guards (#929): a receipt only from a complete, identified reviewer run on the confirmed
# model, every read after the captured green CI, on the exact head, with every changed file read
# whole; anything less is refused with nothing posted, and a post that half-happens says so.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tool = Join-Path $PSScriptRoot 'review-receipt.ps1'
$fixtures = Join-Path $PSScriptRoot 'test-review-receipt'
$scratch = Join-Path $PSScriptRoot '..' 'out' 'test-review-receipt'
$log = Join-Path $scratch 'events.jsonl'
$global:RrRepo = 'danswett/agent-bridge'
$global:RrHead = 'a929a929a929a929a929a929a929a929a929a929'
$global:RrMergeBase = 'b929b929b929b929b929b929b929b929b929b929'
$global:RrBase = 'd929d929d929d929d929d929d929d929d929d929'
$earlier = 'e929e929e929e929e929e929e929e929e929e929'
$moved = 'f929f929f929f929f929f929f929f929f929f929'
$agent = 'AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE'
$session = '11111111-2222-4333-8444-555555555555'
$root = 'C:\checkouts\agent-bridge'
$model = 'claude-opus-5.5'
$env:GH_TOKEN = 'stand-in-token'
Remove-Item Env:GITHUB_ACTIONS -ErrorAction SilentlyContinue

$events = [IO.File]::ReadAllText((Join-Path $fixtures 'events.jsonl')) -replace "`r`n", "`n"
$checks = [IO.File]::ReadAllText((Join-Path $fixtures 'checks.json')).Trim()
$ledger = @(Get-Content (Join-Path $fixtures 'ledger.json') -Raw | ConvertFrom-Json)

# --- The stand-in. Functions come before programs, so the script under test finds this one; what
# it records is global, since $script: in it would be the script under test's.
function global:gh {
    $a = @($args | ForEach-Object { [string]$_ })
    $global:RrCalls.Add("gh $($a -join ' ')")
    $global:LASTEXITCODE = 0
    $repo = $global:RrRepo
    if ($a[0] -eq 'api' -and $a[1] -eq "repos/$repo/pulls/929") {
        $global:RrHeadReads++
        $sha = if ($global:RrHeadReads -gt $global:RrMoveAfter) { $global:RrMovedTo } else { $global:RrHead }
        @{ sha = $sha; state = 'open'; base = $global:RrBase } | ConvertTo-Json -Compress
    } elseif ($a[0] -eq 'api' -and $a[1] -eq "repos/$repo/pulls/929/commits?per_page=100") {
        foreach ($c in $global:RrCommits) { $c | ConvertTo-Json -Compress }
    } elseif ($a[0] -eq 'pr' -and $a[1] -eq 'checks') {
        $global:LASTEXITCODE = $global:RrChecksExit
        $global:RrChecks
    } elseif ($a[0] -eq 'api' -and $a[1] -eq "repos/$repo/pulls/929/files?per_page=100") {
        foreach ($f in $global:RrFiles) { $f | ConvertTo-Json -Compress }
    } elseif ($a[0] -eq 'api' -and $a[1] -eq "repos/$repo/compare/$($global:RrBase)...$($global:RrHead)") {
        $global:RrMergeBase
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-H' -and $a[2] -eq 'Accept: application/vnd.github.raw+json' -and $a[3] -like "repos/$repo/contents/*") {
        $path = [uri]::UnescapeDataString(($a[3] -replace "^repos/$repo/contents/", '' -replace '\?ref=.*$', ''))
        if ($a[3] -notlike "*?ref=$($global:RrHead)") { throw "stand-in gh: contents not at the head: $($a[3])" }
        if (-not $global:RrContents.Contains($path)) { throw "stand-in gh: no contents for $path" }
        $global:RrContents[$path] -split "`n"
    } elseif ($a[0] -eq 'api' -and $a[1] -eq 'graphql') {
        $query = ($a | Where-Object { $_ -like 'query=*' }) -join ''
        $cursor = ($a | Where-Object { $_ -like 'cursor=*' }) -join ''
        $connection = if ($query -like '*reviewThreads(*') { 'reviewThreads' } else { 'reviews' }
        $pages = @(if ($connection -eq 'reviewThreads') { $global:RrThreadPages } else { $global:RrReviewPages })
        $index = if ($cursor) { [int]$cursor.Substring('cursor=page'.Length) } else { 0 }
        $hasNext = $index + 1 -lt $pages.Count
        $connectionValue = @{ pageInfo = @{ hasNextPage = $hasNext; endCursor = $(if ($hasNext) { "page$($index + 1)" } else { $null }) }; nodes = @($pages[$index]) }
        @{ data = @{ repository = @{ pullRequest = @{ headRefOid = $global:RrHead; $connection = $connectionValue } } } } | ConvertTo-Json -Depth 10 -Compress
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'POST' -and $a[3] -eq "repos/$repo/issues/929/comments" -and $a[4] -eq '--input' -and $a[5] -eq '-') {
        $global:RrComment = ($input | Out-String | ConvertFrom-Json).body
        '{"html_url":"https://github.com/danswett/agent-bridge/pull/929#issuecomment-1001","id":1001}'
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'POST' -and $a[3] -eq "repos/$repo/statuses/$($global:RrHead)") {
        $global:RrStatus = $a[4..($a.Count - 1)] -join ' '
        '{"state":"success"}'
    } else { throw "stand-in gh: unexpected $($a -join ' ')" }
}
if ((Get-Command gh).CommandType -ne 'Function') { throw 'the gh stand-in is not the gh the script would find' }

function Reset([string]$text = $events) {
    $global:RrCalls = [Collections.Generic.List[string]]::new()
    $global:RrHeadReads = 0
    $global:RrMoveAfter = [int]::MaxValue
    $global:RrMovedTo = $moved
    $global:RrChecks = $checks
    $global:RrChecksExit = 0
    $global:RrCommits = @(
        @{ sha = $earlier; message = "Start the change`n`nCo-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>" },
        @{ sha = $global:RrHead; message = 'Finish the change' })
    $global:RrFiles = @(
        @{ filename = 'src/Example.cs'; status = 'modified' },
        @{ filename = 'docs/notes.md'; status = 'added' },
        @{ filename = 'old.txt'; status = 'removed' })
    $global:RrContents = @{
        'src/Example.cs' = "namespace Example;`n`npublic static class Greeting { }`n"
        'docs/notes.md' = "# Notes`nFirst line.`nSecond line.`nThird line.`n"
    }
    # Two pages of threads, so the second is only seen by following the cursor.
    $global:RrThreadPages = @(@(@{ isResolved = $true; isOutdated = $false }), @(@{ isResolved = $true; isOutdated = $true }))
    $global:RrReviewPages = @(, @(@{ author = @{ login = 'a-reviewer' }; state = 'COMMENTED'; submittedAt = '2025-06-01T10:34:00Z'; commit = @{ oid = $global:RrHead } }))
    $global:RrComment = $null
    $global:RrStatus = $null
    if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
    New-Item -ItemType Directory -Force $scratch | Out-Null
    [IO.File]::WriteAllText($log, $text, [Text.UTF8Encoding]::new($false))
}

function Invoke-Receipt([switch]$Post) {
    (& $tool -Pr 929 -Head $global:RrHead -EventLog $log -ReviewerAgentId $agent -Root $root -Scope FULL -Repository $global:RrRepo -Post:$Post 6>$null) -join "`n"
}

# The message the script refused with, or $null when it didn't.
function Get-Refusal([switch]$Post) {
    try { $null = Invoke-Receipt -Post:$Post; $null } catch { $_.Exception.Message }
}

function Format-Read($at) {
    if ($at -is [datetime]) { return $at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [Globalization.CultureInfo]::InvariantCulture) }
    [string]$at
}

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Output "PASS $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Posts { @($global:RrCalls | Where-Object { $_ -like 'gh api -X POST*' }) }
function Assert-Refused([string]$refusal, [string]$like) {
    Assert ($refusal -like 'review-receipt refused, nothing posted: *') "not refused: $refusal"
    Assert ($refusal -like $like) "refused for another reason: $refusal"
    Assert (@(Posts).Count -eq 0) "posted anyway: $(Posts)"
}

# --- A receipt.
Check 'A complete review prints a receipt naming the confirmed model, the head, CI and every replayed read, and posts nothing' {
    Reset
    $receipt = Invoke-Receipt
    Assert ($receipt.StartsWith('<!-- agent-bridge review-receipt v1 -->')) "receipt: $receipt"
    Assert ($receipt.Contains("sec-929 CLEAN")) 'no verdict line'
    Assert ($receipt.Contains("#929 at ``$($global:RrHead)``")) 'the head is not named'
    Assert ($receipt.Contains("requested ``$model``, confirmed ``$model``")) 'the model is not confirmed'
    Assert ($receipt.Contains("a ``code-review`` agent, ``$agent``")) 'the role is not named'
    Assert ($receipt.Contains("session ``$session``")) 'the session is not named'
    Assert ($receipt.Contains('| CI | green at 2025-06-01T10:20:00.000Z, captured on the head by the coordinator''s `toolu_synthetic_capture_0001` at 2025-06-01T10:25:05.000Z (line 3), before the reviewer started; read again at ')) 'CI is not the capture before the reviewer started'
    Assert ($receipt.Contains("``git diff $($global:RrMergeBase) $($global:RrHead)``")) 'the diff read is not named'
    Assert ($receipt.Contains('| Threads | 2, all resolved |')) 'the second page of threads was not read'
    Assert ($receipt.Contains('a-reviewer COMMENTED')) 'the review of the head is not listed'
    foreach ($read in $ledger) {
        $row = "| ``$($read.path)`` | $($read.lines) | $(Format-Read $read.read) | ``$($read.toolCallId)`` |"
        Assert ($receipt.Contains($row)) "the ledger has no row $row"
    }
    Assert ($receipt.Contains("### Reads ($($ledger.Count))")) 'the ledger has reads the fixture does not'
    Assert (@(Posts).Count -eq 0) "a dry run posted: $(Posts)"
}

Check 'With -Post it reads the head, posts the comment, reads the head again and posts the status linking that comment' {
    Reset
    $url = Invoke-Receipt -Post
    Assert ($url -eq 'https://github.com/danswett/agent-bridge/pull/929#issuecomment-1001') "url: $url"
    Assert ($global:RrComment -like '<!-- agent-bridge review-receipt v1 -->*') 'the comment is not the receipt'
    Assert ($global:RrStatus -like "*state=success*context=independent-review*description=sec-929 CLEAN: FULL, code-review, $model*target_url=$url*") "status: $($global:RrStatus)"
    $comment = $global:RrCalls.FindIndex({ param($c) $c -like '*issues/929/comments*' })
    $status = $global:RrCalls.FindIndex({ param($c) $c -like '*statuses/*' })
    Assert ($comment -lt $status) 'the status went first'
    Assert ($global:RrCalls[$status - 1] -eq "gh api repos/$($global:RrRepo)/pulls/929 --jq {sha: .head.sha, state: .state, base: .base.sha}") 'the head was not read just before the status'
}

# --- The three refusals #929's validation route names.
Check 'A read that predates the CI capture is refused, though CI completed before the read' {
    # CI completed at 10:20 and the reviewer started at 10:30:01, but the coordinator captured the
    # checks only at 10:31:05, after the reviewer's first read.
    $lines = $events -split "`n"
    $capture = @($lines | Where-Object { $_.Contains('toolu_synthetic_capture_0001') })
    $late = [Collections.Generic.List[string]]::new()
    foreach ($line in $lines) {
        if ($line.Contains('toolu_synthetic_capture_0001')) { continue }
        $late.Add($line)
        if ($line.Contains('"type":"tool.execution_complete"') -and $line.Contains('toolu_synthetic_view_0001')) {
            foreach ($c in $capture) { $late.Add($c.Replace('"timestamp":"2025-06-01T10:25:0', '"timestamp":"2025-06-01T10:31:0')) }
        }
    }
    Reset ($late -join "`n")
    Assert-Refused (Get-Refusal) '*started at 2025-06-01T10:30:01.000Z, before the coordinator''s first CI capture*a read predates the CI capture*'
    # The capture where it was, but completed after the reviewer started: its time decides, not its line.
    Reset ($events.Replace('"timestamp":"2025-06-01T10:25:05.000Z"', '"timestamp":"2025-06-01T10:30:30.000Z"'))
    Assert-Refused (Get-Refusal) '*no later than the CI capture completed at 2025-06-01T10:30:30.000Z: a read predates the CI capture*'
}

Check 'A check that completes after the CI capture is refused: the reviewer''s reads predate its result' {
    Reset
    $global:RrChecks = $checks.Replace('"completedAt":"2025-06-01T10:20:00Z"', '"completedAt":"2025-06-01T10:31:05Z"')
    Assert-Refused (Get-Refusal) '*a check completed at 2025-06-01T10:31:05.000Z, after the CI capture at 2025-06-01T10:25:05.000Z*'
}

Check 'A head that moves while the checks are read is refused' {
    Reset
    $global:RrMoveAfter = 1
    Assert-Refused (Get-Refusal) "*the head moved: #929 is at $moved*after reading the checks*"
}

Check 'A task call that asked for no model is refused: a model the session inherited is not a requested one' {
    Reset ($events.Replace('"name":"reviewer","model":"claude-opus-5.5",', '"name":"reviewer",'))
    Assert-Refused (Get-Refusal) '*asked for no model*'
}

Check 'A reviewer whose completion confirms another model is refused' {
    Reset ($events.Replace('"agentName":"reviewer","model":"claude-opus-5.5","firstDispatchedModel"', '"agentName":"reviewer","model":"claude-haiku-4.5","firstDispatchedModel"'))
    Assert-Refused (Get-Refusal) '*completed on claude-haiku-4.5*'
}

Check 'Any reviewer role but code-review or security-review, general-purpose included, or no role, is refused' {
    foreach ($other in 'general-purpose', 'explore', 'task') {
        Reset ($events.Replace('"agent_type":"code-review"', "`"agent_type`":`"$other`"").Replace('"agentType":"code-review"', "`"agentType`":`"$other`""))
        Assert-Refused (Get-Refusal) "*is a '$other' agent; rule 5 takes a code-review or security-review agent*"
    }
    Reset ($events.Replace('"agentType":"code-review",', ''))
    Assert-Refused (Get-Refusal) "*is a '' agent*"
}

Check 'No CI capture before the reviewer, or a capture of another head, is refused' {
    Reset (($events -split "`n" | Where-Object { -not $_.Contains('toolu_synthetic_capture_0001') }) -join "`n")
    Assert-Refused (Get-Refusal) '*the coordinator captured no CI before starting the reviewer*'
    Reset ($events.Replace("\n$($global:RrHead)\n<shellId", "\n$moved\n<shellId"))
    Assert-Refused (Get-Refusal) '*isn''t between two reads of the head*another head''s checks*'
}

# --- More it must not take.
Check 'A head that moves after the comment is posted posts no status and says the receipt is partial' {
    Reset
    $global:RrMoveAfter = 3
    $refusal = Get-Refusal -Post
    Assert ($refusal -like 'review-receipt PARTIAL: the comment https://github.com/danswett/agent-bridge/pull/929#issuecomment-1001 is posted, but no independent-review status*the head moved*') "refusal: $refusal"
    Assert ($null -ne $global:RrComment -and $null -eq $global:RrStatus) 'the status was posted, or the comment was not'
}

Check 'A reviewer that never completed is refused' {
    Reset (($events -split "`n" | Where-Object { $_ -notlike '*"type":"subagent.completed"*' }) -join "`n")
    Assert-Refused (Get-Refusal) '*never completed*'
}

Check 'A reviewer from the session that wrote a commit of the pull request is refused' {
    Reset
    $global:RrCommits[0].message += "`nCopilot-Session: $session"
    Assert-Refused (Get-Refusal) "*commit $earlier was written in session $session*"
}

Check 'A view that read another version of a file is refused' {
    Reset ($events.Replace('"content":"Second line.\nThird line."', '"content":"Second line.\nAn older third line."'))
    Assert-Refused (Get-Refusal) '*view of docs/notes.md*isn''t the head''s content*'
}

Check 'A view the CLI refused as too large reads nothing, so the file is unread' {
    Reset ($events.Replace('"content":"namespace Example;\n\npublic static class Greeting { }\n"', '"content":"File too large to read at once (synthetic)."'))
    Assert-Refused (Get-Refusal) '*didn''t read src/Example.cs*'
}

Check 'An unresolved thread on the second page is found and refused' {
    Reset
    $global:RrThreadPages = @(@(@{ isResolved = $true; isOutdated = $false }), @(@{ isResolved = $false; isOutdated = $false }))
    Assert-Refused (Get-Refusal) '*1 of 2 review threads are unresolved*'
    Assert ([bool]($global:RrCalls | Where-Object { $_ -like '*cursor=page1*' })) 'the second page was never asked for'
}

Check 'Pending CI is refused' {
    Reset
    $global:RrChecks = $checks.Replace('"bucket":"pass","completedAt":"2025-06-01T10:19:00Z"', '"bucket":"pending","completedAt":"0001-01-01T00:00:00Z"')
    $global:RrChecksExit = 8
    Assert-Refused (Get-Refusal) '*CI isn''t green: light is pending*'
}

Check '-Post refuses to run in GitHub Actions, before reading anything' {
    Reset
    $env:GITHUB_ACTIONS = 'true'
    try { $refusal = Get-Refusal -Post } finally { Remove-Item Env:GITHUB_ACTIONS -ErrorAction SilentlyContinue }
    Assert-Refused $refusal '*-Post never runs in GitHub Actions*'
    Assert ($global:RrCalls.Count -eq 0) "it called gh: $($global:RrCalls)"
}

if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
Remove-Item Env:GH_TOKEN
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
