# Writes the receipt for an independent review (AGENTS.md rule 5) from what happened, never from
# what anyone says happened: the reviewer's own events in the Copilot CLI session log that ran it,
# GitHub's checks, files, threads and reviews on the exact head, and the head's own file contents.
# It prints the receipt; only with -Post does it post it, as one comment on the pull request and
# then the independent-review status on the head, linked to that comment (#929).
#
#   pwsh build/review-receipt.ps1 -Pr <n> -Head <sha> -EventLog <session folder>/events.jsonl `
#       -ReviewerAgentId <agentId> -Root <the reviewer's checkout> -Scope ENTIRE|FULL|DELTA `
#       [-Since <last CLEAN head>] [-Repository danswett/agent-bridge] [-DryRun | -Post]
#
# Without -Post it is a dry run: every check, and the receipt printed, but nothing posted. -Post
# refuses to run in GitHub Actions: a receipt is the coordinator's act, never CI's.
#
# The evidence, and what it refuses (it posts nothing unless every item holds):
#  - The reviewer, by exact identity: one subagent.started for -ReviewerAgentId, started by one
#    `task` tool call whose toolCallId it names, and whose first subagent.completed names both.
#    No subagent.failed, abort or model change of its own; no event of its own after that first
#    completion (an agent woken again by write_agent writes none, so a second run can't be told
#    apart); every tool call it started completed; timestamps in order.
#  - Its role: a code-review or security-review agent (AGENTS.md rule 5), that edited nothing,
#    from a session that edited nothing in the checkout it reviewed and wrote none of the pull
#    request's commits (no `Copilot-Session: <this session>` trailer in them).
#  - Its model: the model the task call asked for in its own `model` argument (a model only named
#    in the prompt is no requested model, and subagent.started must say `taskModelSource`
#    task_argument), which subagent.started, the first subagent.completed, any
#    subagent.configured, and every tool call and message of the reviewer must all name. The log
#    is the only source; nothing typed in stands in for it.
#  - CI, before any read: the coordinator's own capture of it in the same session, run as a
#    powershell tool call before it starts the reviewer, exactly (an optional leading
#    `$env:GH_TOKEN=(gh auth token --user <login>);` aside):
#      gh api repos/<repo>/pulls/<n> --jq .head.sha; gh pr checks <n> --repo <repo> --json 'name,state,bucket,startedAt,completedAt,workflow,link'; gh api repos/<repo>/pulls/<n> --jq .head.sha
#    The last such call started before the reviewer must have completed, succeeded (exit code 0)
#    before subagent.started, and printed -Head, the checks JSON, and -Head again: quality-gate
#    passed and every other check passed or was skipped (merge-rule and independent-review are
#    the receipts' own statuses, so they're left out). Its completion time is the capture's; the
#    reviewer must start after it, and every event of the reviewer come after it: no read
#    predates the capture of green CI, which no typed-in time stands in for. A capture that ran
#    only after the reviewer started is refused. Then `gh pr checks` is read again, between two
#    reads of the head: still green, and no check completed after the capture (a rerun or a new
#    check is a result the reads predate).
#  - Coverage, from the reviewer's own view calls (the read ledger, below), checked against the
#    head's contents from GitHub: each file the scope needs, read whole after the CI capture, and
#    the diff read.
#  - The verdict: the reviewer's last message ends in the line `VERDICT: CLEAN`, exactly (only
#    blank lines may follow it); `NOT CLEAN` and `NOT_CLEAN` appear nowhere in the message, in any
#    case; and no other line has the marker `VERDICT:` in any case, whatever markdown is around
#    it. Prose that only mentions the verdict, without that marker, is fine.
#  - The pull request: open, into the branch main's quality/merge-rule.yml names as its `base:`
#    (the only branch the merge rule judges, so a receipt for a pull request into any other can't
#    be carried to one into it), its head still -Head and its base still that branch when the
#    checks, threads and reviews are read, and every review thread resolved.
#  Before it posts the comment, and again before the status, it reads the pull request again; if
#  the head moved or the base changed after the comment was posted it posts no status, exits
#  nonzero and names the comment, so a half-done receipt is never taken for a whole one. The
#  comment and the status both name the base.
#
# The read ledger is replayed from the log, not supplied: every successful `view` tool call of the
# reviewer whose path is under -Root. A read counts for a changed file when the content it
# returned is the head's own content of that file (with CRLF taken as LF and trailing newlines
# ignored), whole or the lines of its view_range; a file is read whole when those reads together
# cover every line. A view the CLI refused as too large covers nothing. A view the CLI cut short
# (its content ends in "[Output truncated. Use view_range=[<n>, ...] to continue reading.") covers
# only the whole lines before the cut, less the last, which the cut may have split; each must be
# the head's, and later reads can cover the rest. A view whose content is neither the head's nor
# one of those fails the receipt, since the reviewer read another version.
# Each read is listed in the receipt with its path, lines, time and toolCallId.
#
# Scopes (Quality's coverage rule, #689): the diff and each file it leaves (not deleted) read whole.
#  ENTIRE and FULL  the files GitHub's compare of the merge-rule base (above) with the head lists,
#                   never the pull request's own base; the diff is `git diff <their merge base> <head>`.
#  DELTA            the files changed since -Since, whose latest independent-review status must
#                   be a success posted by an account in independent_review.posters as main's
#                   quality/merge-rule.yml has it (any case); the diff is `git diff <since> <head>`.
# The diff read is a successful powershell tool call of the reviewer, exit code 0, with output,
# whose command runs git diff with both full SHAs (A B, A..B or A...B). Its output may be cut
# short by the CLI, so the receipt proves the command, not that every line of it was read.
#
# The conventions this asks of the coordinator and a reviewer's prompt, then: capture CI as above
# once it's green, then start the reviewer with a task call that passes its `model` argument; read
# each file with `view` (in ranges when it's large); run that git diff with full SHAs; end the last
# message with the line `VERDICT: CLEAN`, with `VERDICT:`, NOT CLEAN and NOT_CLEAN nowhere else,
# or give the findings.
#
# Needs gh, signed in as an account in independent_review.posters (quality/merge-rule.yml) to post.
# Only events.jsonl schema version 1 (session.start's version, as Copilot CLI 1.0.105 writes it) is
# understood; any other is refused.
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$Pr,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Head,
    [Parameter(Mandatory)][string]$EventLog,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ReviewerAgentId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Root,
    [Parameter(Mandatory)][ValidateSet('ENTIRE', 'FULL', 'DELTA')][string]$Scope,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Since,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'danswett/agent-bridge',
    [switch]$DryRun,
    [switch]$Post
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The statuses quality/merge-rule.yml names: merge-rule's own and the receipt's.
$mergeRuleContext = 'merge-rule'
$receiptContext = 'independent-review'
$gateCheck = 'quality-gate'
$mergeRuleWorkflows = @('Merge rule', 'Merge rule review relay')
$reviewerTypes = @('code-review', 'security-review')
$writeTools = @('edit', 'create', 'apply_patch')
$tooLargeNotice = 'File too large to read at once'
$checkFields = 'name,state,bucket,startedAt,completedAt,workflow,link'
$Head = $Head.ToLowerInvariant()
$Scope = $Scope.ToUpperInvariant()
if ($Since) { $Since = $Since.ToLowerInvariant() }
$owner, $name = $Repository.Split('/')

function Stop-Receipt([string] $why) { throw "review-receipt refused, nothing posted: $why" }

if ($Post -and $DryRun) { Stop-Receipt '-DryRun and -Post together; choose one' }
if ($Post -and $env:GITHUB_ACTIONS -eq 'true') { Stop-Receipt '-Post never runs in GitHub Actions: posting a receipt is the coordinator''s act' }
if ($Scope -eq 'DELTA' -and -not $Since) { Stop-Receipt 'DELTA needs -Since, the head the last CLEAN receipt was for' }
if ($Scope -ne 'DELTA' -and $Since) { Stop-Receipt "-Since is for DELTA, not $Scope" }

function Invoke-Gh {
    # gh reports a failure on stderr and a nonzero exit; unchecked, it would read as no data.
    $output = & gh @args
    if ($LASTEXITCODE) { throw "review-receipt: gh $($args[0]) $($args[1]) failed ($LASTEXITCODE)" }
    $output
}

# A field of parsed JSON, or $null when any key on the way is missing, so strict mode never trips
# on an event that lacks one.
function Get-Field($value, [string[]] $path) {
    foreach ($key in $path) {
        if ($value -isnot [System.Collections.IDictionary] -or -not $value.Contains($key)) { return $null }
        $value = $value[$key]
    }
    if ($value -is [System.Collections.IList]) { return , $value }
    $value
}

function ConvertFrom-GhJson($lines, [string] $what) {
    $text = (@($lines) -join "`n").Trim()
    if (-not $text) { Stop-Receipt "GitHub returned nothing for $what" }
    $value = $null
    try { $value = $text | ConvertFrom-Json -AsHashtable -Depth 100 -NoEnumerate }
    catch { Stop-Receipt "GitHub's answer for $what isn't JSON" }
    if ($value -is [System.Collections.IList]) { return , $value }
    $value
}

# ConvertFrom-Json turns ISO times into DateTime already; a time without an offset is ambiguous,
# and gh's zero time means "not completed", so both are no time at all.
function Get-Instant($value) {
    $at = $null
    if ($value -is [datetime]) {
        if ($value.Kind -eq [DateTimeKind]::Unspecified) { return $null }
        $at = [DateTimeOffset]::new($value.ToUniversalTime())
    } elseif ($value -is [DateTimeOffset]) {
        $at = $value.ToUniversalTime()
    } elseif ($value -is [string] -and $value -match '(Z|[+-]\d\d:?\d\d)$') {
        $parsed = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $parsed)) { return $null }
        $at = $parsed.ToUniversalTime()
    } else {
        return $null
    }
    if ($at.Year -lt 2000) { return $null }
    $at
}

function Format-Instant($at) { $at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [Globalization.CultureInfo]::InvariantCulture) }

function Get-Normalized([string] $text) { ($text -replace "`r`n", "`n").TrimEnd("`n") }

# Copilot CLI 1.0.105 cuts a long view's output short, often mid-line, and appends a notice:
# "\n\n[Output truncated. Use view_range=[<n>, ...] to continue reading. ...". The whole lines
# before the cut, less the last one (which the cut may have split), are what the reviewer saw;
# $null when the text doesn't end in that notice.
function Get-ShownLines([string] $seen) {
    $at = $seen.LastIndexOf("`n`n[Output truncated. Use view_range=[", [StringComparison]::Ordinal)
    if ($at -lt 0 -or $seen.Substring($at) -cnotmatch '^\n\n\[Output truncated\. Use view_range=\[\d+, \.\.\.\] to continue reading\.') { return $null }
    $before = [string[]]$seen.Substring(0, $at).Split("`n")
    if ($before.Count -le 1) { return , [string[]]@() }
    return , [string[]]$before[0..($before.Count - 2)]
}

function ConvertTo-SlashPath([string] $path) { ($path -replace '\\', '/').TrimEnd('/') }
$rootPath = ConvertTo-SlashPath $Root
$rootPrefix = "$rootPath/"

# The repository path a reviewer's absolute path names under -Root, or $null.
function Get-RepoPath($path) {
    if ($path -isnot [string] -or -not $path) { return $null }
    $slashed = ConvertTo-SlashPath $path
    if (-not $slashed.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $relative = $slashed.Substring($rootPrefix.Length)
    # A path that steps through . or .. could name anything; it counts as no read.
    if (($relative -split '/') -contains '..' -or ($relative -split '/') -contains '.') { return $null }
    $relative
}

function Get-PullRequest {
    $pull = ConvertFrom-GhJson (Invoke-Gh api "repos/$Repository/pulls/$Pr" --jq '{sha: .head.sha, state: .state, base: .base.ref}') "pull request #$Pr"
    [pscustomobject]@{ Sha = [string](Get-Field $pull 'sha'); State = [string](Get-Field $pull 'state'); Base = [string](Get-Field $pull 'base') }
}

# Every read of the pull request, the last ones before each post included: open, on -Head, and
# into $targetBase, so a retarget is refused as a moved head is.
function Assert-Head([string] $when) {
    $pull = Get-PullRequest
    if ($pull.State -cne 'open') { Stop-Receipt "pull request #$Pr is $($pull.State), not open ($when)" }
    if ($pull.Sha -cne $Head) { Stop-Receipt "the head moved: #$Pr is at $($pull.Sha), not $Head ($when)" }
    if ($pull.Base -cne $targetBase) { Stop-Receipt "pull request #$Pr targets '$($pull.Base)', not $targetBase, the base main's quality/merge-rule.yml names ($when)" }
}

function Format-Cell([string] $text) { ($text -replace '\|', '\|' -replace "`r?`n", ' ').Trim() }

# JSON's true, not anything that merely compares equal to it.
function Test-True($value) { $value -is [bool] -and $value }

function Test-Integer($value) { $value -is [int] -or $value -is [long] }

# The checks `gh pr checks --json` printed that count, refused unless they're green, and when the
# last of them completed. The merge rule's own status and workflows wait on this receipt, so they
# aren't CI it can need.
function Get-GreenCi($checks, [string] $what) {
    if ($checks -isnot [System.Collections.IList]) { Stop-Receipt "$what aren't a list of checks" }
    $counted = @(@($checks) | Where-Object { (Get-Field $_ 'name') -cnotin $mergeRuleContext, $receiptContext -and (Get-Field $_ 'workflow') -cnotin $mergeRuleWorkflows })
    $greenAt = $null
    foreach ($check in $counted) {
        $checkName = [string](Get-Field $check 'name')
        $bucket = [string](Get-Field $check 'bucket')
        if ($bucket -cnotin 'pass', 'skipping') { Stop-Receipt "CI isn't green: $checkName is $bucket ($(Get-Field $check 'state')) in $what" }
        $done = Get-Instant (Get-Field $check 'completedAt')
        if ($bucket -ceq 'pass' -and -not $done) { Stop-Receipt "$checkName passed with no completion time in $what" }
        if ($done -and ($null -eq $greenAt -or $done -gt $greenAt)) { $greenAt = $done }
    }
    if (-not @($counted | Where-Object { (Get-Field $_ 'name') -ceq $gateCheck -and (Get-Field $_ 'bucket') -ceq 'pass' })) { Stop-Receipt "$gateCheck hasn't passed on $Head in $what (a draft's fast ring isn't it)" }
    [pscustomobject]@{ Considered = $counted; GreenAt = $greenAt }
}

# quality/merge-rule.yml as main has it, never the pull request's own copy, which the change under
# review could rewrite. Read once; both readers below take only the form main writes and refuse
# anything else rather than guess at it.
function Read-MainPolicy {
    $policy = @(Invoke-Gh api -H 'Accept: application/vnd.github.raw+json' "repos/$Repository/contents/quality/merge-rule.yml?ref=main") -join "`n"
    , [string[]](Get-Normalized $policy).Split("`n")
}

# The branch the merge rule judges pull requests into: the one top-level `base:` (MergeRule.cs's
# Config.Base, which it requires). A receipt is only for a pull request into it.
function Get-TargetBase([string[]] $policyLines) {
    $bases = @($policyLines | Where-Object { $_ -cmatch '^base:' })
    if ($bases.Count -ne 1) { Stop-Receipt "main's quality/merge-rule.yml has $($bases.Count) top-level base lines; exactly one is needed" }
    $value = [regex]::Match($bases[0], '^base:[ \t]*([''"]?)([A-Za-z0-9][A-Za-z0-9._/-]*)\1[ \t]*(#.*)?$')
    if (-not $value.Success -or $value.Groups[2].Value.Contains('..')) { Stop-Receipt "main's quality/merge-rule.yml names no base this understands" }
    $value.Groups[2].Value
}

# Who may post the receipt: independent_review.posters in main's policy; the merge rule
# (tools/AgentBridge.Quality/MergeRule.cs) takes a receipt only from them, matching logins in any
# case. Only one `independent_review:` block with its `context:` and a `posters: [a, b]` list is
# read.
function Get-ReceiptPosters([string[]] $policyLines) {
    $blocks = @(for ($i = 0; $i -lt $policyLines.Count; $i++) { if ($policyLines[$i] -cmatch '^independent_review:[ \t]*(#.*)?$') { $i } })
    if ($blocks.Count -ne 1) { Stop-Receipt "main's quality/merge-rule.yml has $($blocks.Count) independent_review blocks; exactly one is needed" }
    $contexts = [Collections.Generic.List[string]]::new()
    $lists = [Collections.Generic.List[string]]::new()
    for ($i = $blocks[0] + 1; $i -lt $policyLines.Count; $i++) {
        $line = $policyLines[$i]
        if ($line -match '^[ \t]*(#.*)?$') { continue }
        if ($line -notmatch '^[ \t]') { break }
        if ($line -cmatch '^[ \t]+context:(.*)$') { $contexts.Add($Matches[1]) }
        elseif ($line -cmatch '^[ \t]+posters:(.*)$') { $lists.Add($Matches[1]) }
    }
    if ($contexts.Count -ne 1 -or $contexts[0] -cnotmatch ('^[ \t]*([''"]?)' + [regex]::Escape($receiptContext) + '\1[ \t]*(#.*)?$')) {
        Stop-Receipt "main's quality/merge-rule.yml doesn't give independent_review the one context $receiptContext"
    }
    $list = $null
    if ($lists.Count -eq 1) { $list = [regex]::Match($lists[0], '^[ \t]*\[([^\[\]#]*)\][ \t]*(#.*)?$') }
    if ($null -eq $list -or -not $list.Success) { Stop-Receipt "main's quality/merge-rule.yml has no independent_review.posters list this understands" }
    $logins = @($list.Groups[1].Value.Split(',') | ForEach-Object { $_.Trim() -replace '^([''"])(.*)\1$', '$2' })
    foreach ($login in $logins) {
        if ($login -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]*$') { Stop-Receipt "main's independent_review.posters has '$login', which isn't a GitHub login" }
    }
    $logins
}

# --- The log, as bytes once: the hash covers exactly the lines read. A live session may be
# mid-write, so only whole lines count.
if (-not (Test-Path -LiteralPath $EventLog -PathType Leaf)) { Stop-Receipt "there's no event log at $EventLog" }
$logPath = (Resolve-Path -LiteralPath $EventLog).ProviderPath
$bytes = [IO.File]::ReadAllBytes($logPath)
$end = [Array]::LastIndexOf($bytes, [byte]10)
if ($end -lt 0) { Stop-Receipt 'the event log has no complete line' }
$used = [byte[]]::new($end + 1)
[Array]::Copy($bytes, $used, $end + 1)
$logHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($used)).ToLowerInvariant()
$text = $null
try { $text = [Text.UTF8Encoding]::new($false, $true).GetString($used) } catch { Stop-Receipt 'the event log isn''t UTF-8' }
$text = $text.TrimStart([char]0xFEFF)

$events = [Collections.Generic.List[object]]::new()
$lineNumber = 0
foreach ($line in $text.Split("`n")) {
    $lineNumber++
    if (-not $line.Trim()) { continue }
    $entry = $null
    try { $entry = $line | ConvertFrom-Json -AsHashtable -Depth 100 } catch { Stop-Receipt "line $lineNumber of the event log isn't JSON" }
    $type = Get-Field $entry 'type'
    $at = Get-Instant (Get-Field $entry 'timestamp')
    if ($type -isnot [string] -or -not $type) { Stop-Receipt "line $lineNumber of the event log has no type" }
    if (-not $at) { Stop-Receipt "line $lineNumber of the event log has no time with an offset" }
    $agent = Get-Field $entry 'agentId'
    if ($agent -isnot [string] -or -not $agent) { $agent = $null }
    $data = Get-Field $entry 'data'
    $events.Add([pscustomobject]@{ Line = $lineNumber; Type = $type; Agent = $agent; At = $at; Data = $data })
}

# --- The session.
$sessionStarts = @($events | Where-Object Type -EQ 'session.start')
if ($sessionStarts.Count -ne 1 -or $events[0].Type -ne 'session.start') { Stop-Receipt 'the event log must begin with its one session.start' }
$sessionId = Get-Field $sessionStarts[0].Data 'sessionId'
if ($sessionId -isnot [string] -or -not $sessionId) { Stop-Receipt 'session.start names no sessionId' }
# The schema this reads is session.start's version 1, as Copilot CLI 1.0.105 writes it.
$schema = Get-Field $sessionStarts[0].Data 'version'
if (-not (Test-Integer $schema) -or $schema -ne 1) { Stop-Receipt "the event log's schema is version '$schema'; only version 1 is understood" }
$cliVersion = [string](Get-Field $sessionStarts[0].Data 'copilotVersion')
$folder = Split-Path -Leaf (Split-Path -Parent $logPath)
if ($folder -match '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$' -and $folder -ne $sessionId) {
    Stop-Receipt "the log is in session $folder's folder but says it's session $sessionId"
}

# --- The reviewer, by exact identity.
$started = @($events | Where-Object { $_.Type -eq 'subagent.started' -and $_.Agent -ceq $ReviewerAgentId })
if ($started.Count -ne 1) { Stop-Receipt "the log has $($started.Count) subagent.started events for agent $ReviewerAgentId; exactly one is needed" }
$started = $started[0]
$callId = Get-Field $started.Data 'toolCallId'
if ($callId -isnot [string] -or -not $callId) { Stop-Receipt 'the reviewer''s subagent.started names no toolCallId' }
$role = Get-Field $started.Data 'agentType'
if ($role -isnot [string] -or $role -cnotin $reviewerTypes) { Stop-Receipt "agent $ReviewerAgentId is a '$role' agent; rule 5 takes a code-review or security-review agent" }

$taskStarts = @($events | Where-Object { $_.Type -eq 'tool.execution_start' -and (Get-Field $_.Data 'toolCallId') -ceq $callId })
if ($taskStarts.Count -ne 1 -or (Get-Field $taskStarts[0].Data 'toolName') -cne 'task') { Stop-Receipt "no single task tool call $callId started the reviewer" }
$task = $taskStarts[0]
if ($task.Line -gt $started.Line -or $task.At -gt $started.At) { Stop-Receipt 'the task call comes after the reviewer it started' }
if ($task.Agent -ceq $ReviewerAgentId) { Stop-Receipt 'the reviewer started itself' }
$taskArgs = Get-Field $task.Data 'arguments'
if ((Get-Field $taskArgs 'agent_type') -cne $role) { Stop-Receipt "the task call asked for a '$(Get-Field $taskArgs 'agent_type')' agent, but a '$role' agent started" }

$requested = Get-Field $taskArgs 'model'
if ($requested -isnot [string] -or -not $requested) { Stop-Receipt 'the task call that started the reviewer asked for no model; one inherited from the session is no requested model' }
if ((Get-Field $started.Data 'taskModelSource') -cne 'task_argument') { Stop-Receipt 'subagent.started doesn''t say its model came from the task call' }
if ((Get-Field $started.Data 'model') -cne $requested) { Stop-Receipt "the task call asked for $requested, but the reviewer started on $(Get-Field $started.Data 'model')" }

$ends = @($events | Where-Object { $_.Type -in 'subagent.completed', 'subagent.failed' -and ($_.Agent -ceq $ReviewerAgentId -or (Get-Field $_.Data 'toolCallId') -ceq $callId) })
foreach ($e in $ends) {
    if ($e.Type -eq 'subagent.failed') { Stop-Receipt "the reviewer failed (line $($e.Line))" }
    if ($e.Agent -cne $ReviewerAgentId -or (Get-Field $e.Data 'toolCallId') -cne $callId) { Stop-Receipt "line $($e.Line) completes the reviewer's agent or task call, but not both" }
    if ($e.Line -lt $started.Line) { Stop-Receipt 'the reviewer completed before it started' }
}
if ($ends.Count -eq 0) { Stop-Receipt 'the reviewer never completed' }
$completed = $ends[0]
if ($completed.At -le $started.At) { Stop-Receipt 'the reviewer completed no later than it started' }

$confirmed = Get-Field $completed.Data 'model'
if ($confirmed -isnot [string] -or -not $confirmed) { Stop-Receipt 'subagent.completed names no model' }
if ($confirmed -cne $requested) { Stop-Receipt "the task call asked for $requested, but the reviewer completed on $confirmed" }
$dispatched = Get-Field $completed.Data 'firstDispatchedModel'
if ($null -ne $dispatched -and $dispatched -cne $confirmed) { Stop-Receipt "the reviewer was first dispatched to $dispatched, not $confirmed" }

foreach ($e in $events) {
    $parent = Get-Field $e.Data 'parentToolCallId'
    if ($parent -ceq $callId -and $e.Agent -cne $ReviewerAgentId) { Stop-Receipt "line $($e.Line) belongs to the reviewer's task call but not to its agent" }
    if ($e.Agent -cne $ReviewerAgentId) { continue }
    if ($null -ne $parent -and $parent -cne $callId) { Stop-Receipt "line $($e.Line) is the reviewer's but names another task call" }
    if ($e.Type -in 'abort', 'session.model_change') { Stop-Receipt "the reviewer has a $($e.Type) (line $($e.Line))" }
    if ($e.Type -eq 'subagent.configured' -and (Get-Field $e.Data 'model') -cne $confirmed) { Stop-Receipt "the reviewer was configured for $(Get-Field $e.Data 'model'), not $confirmed" }
    if ($e.Line -lt $started.Line -and $e.Type -notin 'subagent.configured', 'subagent.selected') { Stop-Receipt "line $($e.Line) is the reviewer's before it started" }
    if ($e.Line -gt $completed.Line -and $e.Type -notin 'subagent.completed', 'session.shutdown') {
        Stop-Receipt "line $($e.Line) is the reviewer's after it completed: a second run can't be told apart from the first"
    }
}

# The reviewer's own run, in order.
$run = @($events | Where-Object { $_.Agent -ceq $ReviewerAgentId -and $_.Line -gt $started.Line -and $_.Line -lt $completed.Line })
$previous = $started.At
foreach ($e in $run) {
    if ($e.At -lt $previous) { Stop-Receipt "the reviewer's events are out of time order at line $($e.Line)" }
    $previous = $e.At
    if ($e.Type -in 'tool.execution_start', 'tool.execution_complete', 'assistant.message' -and (Get-Field $e.Data 'model') -cne $confirmed) {
        Stop-Receipt "the reviewer's $($e.Type) at line $($e.Line) ran on $(Get-Field $e.Data 'model'), not $confirmed"
    }
}
if ($completed.At -lt $previous) { Stop-Receipt 'the reviewer completed before its last event' }

$calls = [ordered]@{}
foreach ($e in $run) {
    if ($e.Type -notin 'tool.execution_start', 'tool.execution_complete') { continue }
    $id = Get-Field $e.Data 'toolCallId'
    if ($id -isnot [string] -or -not $id) { Stop-Receipt "line $($e.Line) is a tool event without a toolCallId" }
    if ($e.Type -eq 'tool.execution_start') {
        if ($calls.Contains($id)) { Stop-Receipt "tool call $id starts twice" }
        $calls[$id] = [pscustomobject]@{ Id = $id; Name = Get-Field $e.Data 'toolName'; Start = $e; Complete = $null }
    } else {
        if (-not $calls.Contains($id) -or $null -ne $calls[$id].Complete) { Stop-Receipt "tool call $id completes without one start" }
        $calls[$id].Complete = $e
    }
}
foreach ($call in $calls.Values) {
    if ($null -eq $call.Complete) { Stop-Receipt "the reviewer's $($call.Name) call $($call.Id) never completed" }
    if ($call.Name -cin $writeTools -or $null -ne (Get-Field $call.Complete.Data 'fileEdits')) { Stop-Receipt "the reviewer edited files ($($call.Name), $($call.Id)): a reviewer that edits isn't independent" }
}

# The session wrote nothing in the checkout it reviewed: no author shares its log.
foreach ($e in $events) {
    if ($e.Type -eq 'tool.execution_start' -and (Get-Field $e.Data 'toolName') -cin $writeTools) {
        # apply_patch's arguments are the patch itself, a string naming every file it touches.
        $toolArgs = Get-Field $e.Data 'arguments'
        $target = if ($toolArgs -is [string]) { $toolArgs } else { [string](Get-Field $toolArgs 'path') }
        if ("$(ConvertTo-SlashPath $target)/".IndexOf($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) { Stop-Receipt "this session edited the checkout it reviewed (line $($e.Line))" }
    }
    if ($e.Type -eq 'tool.execution_complete') {
        $edits = Get-Field $e.Data 'fileEdits'
        foreach ($edit in @($edits)) {
            if ($null -ne $edit -and (Get-RepoPath (Get-Field $edit 'path'))) { Stop-Receipt "this session edited the checkout it reviewed (line $($e.Line))" }
        }
    }
}

# The verdict, from the reviewer's last message.
$messages = @($run | Where-Object { $_.Type -eq 'assistant.message' -and ([string](Get-Field $_.Data 'content')).Trim() })
if ($messages.Count -eq 0) { Stop-Receipt 'the reviewer said nothing' }
$report = [string](Get-Field $messages[-1].Data 'content')
# Judged by the message's shape, not by guessing at markdown: its last non-empty line is exactly
# VERDICT: CLEAN; it says NOT CLEAN or NOT_CLEAN nowhere (any case); and no other line has the
# marker VERDICT: (any case, whatever markdown is around it).
$reportLines = $report -split '\r\n|\r|\n'
$last = $reportLines.Count - 1
while ($last -ge 0 -and [string]::IsNullOrWhiteSpace($reportLines[$last])) { $last-- }
if ($last -lt 0 -or $reportLines[$last] -cne 'VERDICT: CLEAN') { Stop-Receipt "the reviewer's last message doesn't end in the line VERDICT: CLEAN" }
if ($report -match 'NOT CLEAN|NOT_CLEAN') { Stop-Receipt "the reviewer's last message says NOT CLEAN" }
$verdictLines = @($reportLines | Where-Object { $_ -match 'VERDICT:' })
if ($verdictLines.Count -ne 1) { Stop-Receipt "the reviewer's last message has $($verdictLines.Count) lines with VERDICT:; only its last line may" }

# --- CI before the first read: the coordinator's own `gh pr checks` on the head, between two reads
# of the head, run as a powershell tool call in this session; the last one it started before the
# reviewer. The capture's completion is its time, so every read after it is after green CI was seen.
$captureCommand = "gh api repos/$Repository/pulls/$Pr --jq .head.sha; gh pr checks $Pr --repo $Repository --json '$checkFields'; gh api repos/$Repository/pulls/$Pr --jq .head.sha"
function Test-CaptureCommand($command) {
    if ($command -isnot [string]) { return $false }
    # Signing gh in for the one process prints nothing, so it may come first.
    $command = [regex]::Replace($command, '^\s*\$env:GH_TOKEN\s*=\s*\(\s*gh auth token --user [A-Za-z0-9-]+\s*\)\s*;', '', 'IgnoreCase')
    (($command -replace '\s+', ' ').Trim().TrimEnd(';').TrimEnd()) -ceq $captureCommand
}
$captures = [Collections.Generic.List[object]]::new()
foreach ($e in $events) {
    if ($null -ne $e.Agent -or $e.Type -ne 'tool.execution_start' -or (Get-Field $e.Data 'toolName') -cne 'powershell') { continue }
    if (-not (Test-CaptureCommand (Get-Field $e.Data 'arguments', 'command'))) { continue }
    $id = Get-Field $e.Data 'toolCallId'
    if ($id -isnot [string] -or -not $id) { Stop-Receipt "the CI capture at line $($e.Line) has no toolCallId" }
    $done = @($events | Where-Object { $_.Type -eq 'tool.execution_complete' -and (Get-Field $_.Data 'toolCallId') -ceq $id })
    if ($done.Count -gt 1) { Stop-Receipt "the CI capture $id completes $($done.Count) times" }
    $captures.Add([pscustomobject]@{ Id = $id; Start = $e; Complete = $(if ($done.Count -eq 1) { $done[0] } else { $null }) })
}
$before = @($captures | Where-Object { $_.Start.Line -lt $started.Line })
if ($before.Count -eq 0) {
    if ($captures.Count -gt 0) {
        Stop-Receipt "the reviewer started at $(Format-Instant $started.At), before the coordinator's first CI capture (line $($captures[0].Start.Line)): a read predates the CI capture"
    }
    Stop-Receipt "the coordinator captured no CI before starting the reviewer; run this in the same session first (AGENTS.md rule 5): $captureCommand"
}
$capture = $before[-1]
if ($null -eq $capture.Complete -or $capture.Complete.Line -gt $started.Line) { Stop-Receipt "the CI capture $($capture.Id) hadn't completed when the reviewer started: a read predates the CI capture" }
if ($null -ne $capture.Complete.Agent) { Stop-Receipt "the CI capture $($capture.Id) is completed by agent $($capture.Complete.Agent), not the coordinator" }
if ($capture.Complete.At -lt $capture.Start.At) { Stop-Receipt "the CI capture $($capture.Id) completed before it started" }
$captureExit = Get-Field $capture.Complete.Data 'shellExecution', 'exitCode'
if (-not (Test-True (Get-Field $capture.Complete.Data 'success')) -or -not (Test-Integer $captureExit) -or $captureExit -ne 0) { Stop-Receipt "the CI capture $($capture.Id) didn't succeed" }
$captureAt = $capture.Complete.At
$captureText = [regex]::Replace([string](Get-Field $capture.Complete.Data 'result', 'content'), '<shellId:[^<>]*>\s*$', '')
$captureLines = @($captureText -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($captureLines.Count -lt 3 -or $captureLines[0] -cne $Head -or $captureLines[-1] -cne $Head) {
    Stop-Receipt "the CI capture $($capture.Id) isn't between two reads of the head ${Head}: they may be another head's checks"
}
$capturedChecks = $null
try { $capturedChecks = ($captureLines[1..($captureLines.Count - 2)] -join "`n") | ConvertFrom-Json -AsHashtable -Depth 100 -NoEnumerate }
catch { Stop-Receipt "the CI capture $($capture.Id) printed no checks JSON between its reads of the head" }
$captured = Get-GreenCi $capturedChecks "the CI capture $($capture.Id)"
if ($captured.GreenAt -gt $captureAt) { Stop-Receipt "the CI capture $($capture.Id) shows a check completed at $(Format-Instant $captured.GreenAt), after the capture itself at $(Format-Instant $captureAt)" }
if ($started.At -le $captureAt) {
    Stop-Receipt "the reviewer started at $(Format-Instant $started.At), no later than the CI capture completed at $(Format-Instant $captureAt): a read predates the CI capture"
}
$early = @($run | Where-Object { $_.At -le $captureAt } | Select-Object -First 1)
if ($early) { Stop-Receipt "the reviewer's event at line $($early[0].Line) is at $(Format-Instant $early[0].At), no later than the CI capture at $(Format-Instant $captureAt): a read predates the CI capture" }

# --- GitHub, on the head.
# gh writes UTF-8; read as anything else, a file's non-ASCII lines would never match the reads.
try { [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false) } catch { Write-Host "Couldn't set the console to UTF-8: a file with non-ASCII text may not match its reads." }
$policyLines = Read-MainPolicy
$targetBase = Get-TargetBase $policyLines
Assert-Head 'before reading GitHub'

$commits = @(Invoke-Gh api "repos/$Repository/pulls/$Pr/commits?per_page=100" --paginate --jq '.[] | {sha, message: .commit.message}' |
        Where-Object { $_ } | ForEach-Object { ConvertFrom-GhJson $_ 'a commit' })
# GitHub lists at most 250 commits; past that an author's commit could be missing.
if ($commits.Count -eq 0 -or $commits.Count -ge 250) { Stop-Receipt "GitHub listed $($commits.Count) commits; between 1 and 249 are readable whole" }
if ((Get-Field $commits[-1] 'sha') -cne $Head) { Stop-Receipt "the last commit GitHub lists is $(Get-Field $commits[-1] 'sha'), not $Head" }
$trailer = '(?im)^[ \t]*Copilot-Session:[ \t]*' + [regex]::Escape($sessionId) + '[ \t]*$'
foreach ($c in $commits) {
    if ([string](Get-Field $c 'message') -match $trailer) { Stop-Receipt "commit $(Get-Field $c 'sha') was written in session $sessionId, the reviewer's: that's the author's session" }
}

$checksOutput = & gh pr checks $Pr --repo $Repository --json $checkFields
# gh exits 8 while a check is pending and 1 when one failed; both still print the checks, and
# are judged below. Anything else, or no JSON, is a failure to read them.
$checksExit = $LASTEXITCODE
$liveAt = [DateTimeOffset]::UtcNow
if ($checksExit -notin 0, 1, 8) { Stop-Receipt "gh pr checks failed ($checksExit)" }
$live = Get-GreenCi (ConvertFrom-GhJson $checksOutput "the checks on #$Pr") "the checks on #$Pr now"
$null = Assert-Head 'after reading the checks'
$considered = @($live.Considered)
if ($live.GreenAt -gt $liveAt) { Stop-Receipt 'a check completed after the checks were read' }
# Still green, and nothing completed since the capture: a check that ran again, or a new one, has a
# result the reviewer's reads predate.
if ($live.GreenAt -gt $captureAt) {
    Stop-Receipt "a check completed at $(Format-Instant $live.GreenAt), after the CI capture at $(Format-Instant $captureAt) the reviewer started from: its reads predate that result"
}

# --- What the scope needs read.
if ($Scope -eq 'DELTA') {
    # -Since must carry a CLEAN receipt: its latest independent-review status a success, posted by
    # an account main's merge rule lets post one. The latest alone is judged; an older status never
    # stands in for it.
    $posters = @(Get-ReceiptPosters $policyLines)
    $sinceStatuses = @(Invoke-Gh api "repos/$Repository/commits/$Since/statuses?per_page=100" --paginate --jq ".[] | select(.context == `"$receiptContext`") | {state, creator: .creator.login, created_at}" |
            Where-Object { $_ } | ForEach-Object { ConvertFrom-GhJson $_ "a $receiptContext status on $Since" })
    if ($sinceStatuses.Count -eq 0) { Stop-Receipt "$Since has no $receiptContext status to be a delta from" }
    $latestAt = $null
    foreach ($s in $sinceStatuses) {
        $statusAt = Get-Instant (Get-Field $s 'created_at')
        if (-not $statusAt) { Stop-Receipt "a $receiptContext status on $Since has no time" }
        if ($null -eq $latestAt -or $statusAt -gt $latestAt) { $latestAt = $statusAt }
    }
    $latest = @($sinceStatuses | Where-Object { (Get-Instant (Get-Field $_ 'created_at')) -eq $latestAt })
    if ($latest.Count -ne 1) { Stop-Receipt "$Since has $($latest.Count) $receiptContext statuses at $(Format-Instant $latestAt); which is the latest is ambiguous" }
    $sinceCreator = Get-Field $latest[0] 'creator'
    if ($sinceCreator -isnot [string] -or -not $sinceCreator) { Stop-Receipt "the latest $receiptContext status on $Since names no creator" }
    if (-not @($posters | Where-Object { [string]::Equals($_, $sinceCreator, [StringComparison]::OrdinalIgnoreCase) })) {
        Stop-Receipt "the latest $receiptContext status on $Since was posted by $sinceCreator, who isn't in independent_review.posters on main"
    }
    if ((Get-Field $latest[0] 'state') -cne 'success') { Stop-Receipt "the latest $receiptContext status on $Since is $(Get-Field $latest[0] 'state'), not success: no CLEAN receipt to be a delta from" }
    $compareFrom = $Since
} else {
    # Against the base branch main's merge rule names, never the pull request's own base: what
    # lands is what that branch doesn't have yet.
    $compareFrom = $targetBase
}
$compare = ConvertFrom-GhJson (Invoke-Gh api "repos/$Repository/compare/$compareFrom...$Head" --jq '{status, merge_base: .merge_base_commit.sha, files: [.files[] | {filename, status}]}') "$compareFrom...$Head"
$compareStatus = Get-Field $compare 'status'
$files = Get-Field $compare 'files'
$files = @($files)
# The compare API stops at 300 files.
if ($files.Count -ge 300) { Stop-Receipt "$compareFrom...$Head changes $($files.Count) files or more, more than GitHub lists" }
if ($Scope -eq 'DELTA') {
    if ($compareStatus -cne 'ahead') { Stop-Receipt "$Head isn't ahead of $Since ($compareStatus): no delta to read" }
    $diffBase = $Since
} else {
    if ($compareStatus -cne 'ahead' -and $compareStatus -cne 'diverged') { Stop-Receipt "$Head isn't ahead of $compareFrom ($compareStatus): nothing to land" }
    $diffBase = [string](Get-Field $compare 'merge_base')
    if ($diffBase -cnotmatch '^[0-9a-f]{40}$') { Stop-Receipt "no merge base for $compareFrom...$Head" }
}
$files = @($files | Where-Object { $null -ne $_ })
if ($files.Count -eq 0) { Stop-Receipt 'GitHub lists no changed file' }
$required = @($files | Where-Object { (Get-Field $_ 'status') -cne 'removed' } | ForEach-Object { [string](Get-Field $_ 'filename') } | Sort-Object -Unique -CaseSensitive)

$headLines = @{}
foreach ($path in $required) {
    $escaped = ($path -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
    $content = @(Invoke-Gh api -H 'Accept: application/vnd.github.raw+json' "repos/$Repository/contents/${escaped}?ref=$Head") -join "`n"
    if ($content.Contains([char]0)) { Stop-Receipt "$path is binary, which a reviewer can't read whole" }
    $normalized = Get-Normalized $content
    # Assigned, not the value of an if: an if would unroll a one-line file to a bare string.
    $fileLines = [string[]]@()
    if ($normalized.Length -gt 0) { $fileLines = $normalized.Split("`n") }
    $headLines[$path] = $fileLines
}

# --- The read ledger, replayed from the reviewer's view calls.
$ledger = [Collections.Generic.List[object]]::new()
$covered = @{}
foreach ($path in $required) { $covered[$path] = [Collections.Generic.List[object]]::new() }
foreach ($call in $calls.Values) {
    if ($call.Name -cne 'view') { continue }
    $relative = Get-RepoPath (Get-Field $call.Start.Data 'arguments', 'path')
    if (-not $relative) { continue }
    $path = $required | Where-Object { $_ -ieq $relative } | Select-Object -First 1
    if (-not $path) { continue }
    if (-not (Test-True (Get-Field $call.Complete.Data 'success'))) { continue }
    $seen = Get-Normalized ([string](Get-Field $call.Complete.Data 'result', 'content'))
    $lines = $headLines[$path]
    $range = Get-Field $call.Start.Data 'arguments', 'view_range'
    $shownLines = 'whole'
    if ($null -eq $range) {
        $from = 1; $to = $lines.Count
    } else {
        if ($range -isnot [System.Collections.IList] -or $range.Count -ne 2 -or -not (Test-Integer $range[0]) -or -not (Test-Integer $range[1])) { Stop-Receipt "the view of $path ($($call.Id)) has a view_range this doesn't understand" }
        $from = [int]$range[0]
        $to = if ([int]$range[1] -eq -1) { $lines.Count } else { [math]::Min([int]$range[1], $lines.Count) }
        if ($from -lt 1) { Stop-Receipt "the view of $path ($($call.Id)) starts at line $from" }
        $shownLines = "$from-$to"
    }
    $expected = ''
    if ($from -le $to) { $expected = Get-Normalized ($lines[($from - 1)..($to - 1)] -join "`n") }
    if ($seen -cne $expected) {
        if ($seen.StartsWith($tooLargeNotice, [StringComparison]::Ordinal)) { continue }
        $wrongVersion = "the reviewer's view of $path at $(Format-Instant $call.Start.At) ($($call.Id)) isn't the head's content: it read another version"
        $kept = Get-ShownLines $seen
        if ($null -eq $kept) { Stop-Receipt $wrongVersion }
        # A view cut short counts for the whole lines it showed, each the head's own, and no more.
        if ($kept.Count -gt $to - $from + 1) { Stop-Receipt $wrongVersion }
        if ($kept.Count -gt 0 -and ((@($lines[($from - 1)..($from + $kept.Count - 2)]) -join "`n") -cne ($kept -join "`n"))) { Stop-Receipt $wrongVersion }
        if ($kept.Count -eq 0) { continue }
        $to = $from + $kept.Count - 1
        $shownLines = "$from-$to (cut short)"
    }
    $covered[$path].Add([pscustomobject]@{ From = $from; To = $to })
    $ledger.Add([pscustomobject]@{ Path = $path; Lines = $shownLines; At = $call.Start.At; Id = $call.Id })
}
$unread = [Collections.Generic.List[string]]::new()
foreach ($path in $required) {
    $count = $headLines[$path].Count
    $reach = 0
    foreach ($span in @($covered[$path] | Sort-Object From)) {
        if ($span.From -gt $reach + 1) { break }
        $reach = [math]::Max($reach, $span.To)
    }
    $wasRead = $covered[$path].Count -gt 0
    if (-not $wasRead -or $reach -lt $count) {
        $unread.Add($(if ($wasRead) { "$path (lines $($reach + 1)-$count of $count)" } else { $path }))
    }
}
if ($unread.Count -gt 0) {
    $shown = ($unread | Select-Object -First 5) -join ', '
    if ($unread.Count -gt 5) { $shown += " and $($unread.Count - 5) more" }
    Stop-Receipt "$Scope needs every changed file read whole; the reviewer didn't read $shown"
}

$diffPattern = '(?i)\bgit\b[^\r\n]*\bdiff\b[^\r\n]*\b' + $diffBase + '(\.\.\.?|[ \t]+)' + $Head + '\b'
$diffRead = $null
foreach ($call in $calls.Values) {
    if ($call.Name -cne 'powershell') { continue }
    $command = Get-Field $call.Start.Data 'arguments', 'command'
    if ($command -isnot [string] -or $command -notmatch $diffPattern) { continue }
    $output = [regex]::Replace([string](Get-Field $call.Complete.Data 'result', 'content'), '<shellId:[^<>]*>\s*$', '').Trim()
    $exitCode = Get-Field $call.Complete.Data 'shellExecution', 'exitCode'
    if ((Test-True (Get-Field $call.Complete.Data 'success')) -and (Test-Integer $exitCode) -and $exitCode -eq 0 -and $output) {
        $diffRead = $call
        break
    }
}
if (-not $diffRead) { Stop-Receipt "the reviewer ran no successful git diff $diffBase $Head (full SHAs)" }

# --- Threads and reviews on the head, every page.
function Get-PullRequestNodes([string] $connection, [string] $fields) {
    $query = 'query($owner: String!, $name: String!, $number: Int!, $cursor: String) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { headRefOid baseRefName ' +
        $connection + '(first: 100, after: $cursor) { pageInfo { hasNextPage endCursor } nodes { ' + $fields + ' } } } } }'
    $nodes = [Collections.Generic.List[object]]::new()
    $cursor = $null
    for ($page = 1; ; $page++) {
        if ($page -gt 50) { Stop-Receipt "#$Pr has more than 5,000 $connection" }
        $ghArgs = @('api', 'graphql', '-f', "query=$query", '-f', "owner=$owner", '-f', "name=$name", '-F', "number=$Pr")
        if ($cursor) { $ghArgs += @('-f', "cursor=$cursor") }
        $answer = ConvertFrom-GhJson (Invoke-Gh @ghArgs) "the $connection of #$Pr"
        $pullNode = Get-Field $answer 'data', 'repository', 'pullRequest'
        if ($null -eq $pullNode) { Stop-Receipt "GitHub returned no pull request #$Pr" }
        if ((Get-Field $pullNode 'headRefOid') -cne $Head) { Stop-Receipt "the head moved to $(Get-Field $pullNode 'headRefOid') while reading the $connection" }
        if ((Get-Field $pullNode 'baseRefName') -cne $targetBase) { Stop-Receipt "#$Pr was retargeted to '$(Get-Field $pullNode 'baseRefName')', not $targetBase, while reading the $connection" }
        $connectionNode = Get-Field $pullNode $connection
        $pageNodes = Get-Field $connectionNode 'nodes'
        if ($pageNodes -isnot [System.Collections.IList]) { Stop-Receipt "page $page of the $connection has no nodes" }
        foreach ($node in $pageNodes) { if ($null -ne $node) { $nodes.Add($node) } }
        if (-not (Test-True (Get-Field $connectionNode 'pageInfo', 'hasNextPage'))) { break }
        $cursor = Get-Field $connectionNode 'pageInfo', 'endCursor'
        if ($cursor -isnot [string] -or -not $cursor) { Stop-Receipt "page $page of the $connection has no cursor to the next" }
    }
    , $nodes
}
$threads = Get-PullRequestNodes 'reviewThreads' 'isResolved isOutdated'
$threads = @($threads)
$open = @($threads | Where-Object { -not (Test-True (Get-Field $_ 'isResolved')) })
if ($open.Count -gt 0) { Stop-Receipt "$($open.Count) of $($threads.Count) review threads are unresolved: every finding is answered before a receipt" }
$reviews = Get-PullRequestNodes 'reviews' 'author { login } state submittedAt commit { oid }'
$reviews = @($reviews)
$onHead = @($reviews | Where-Object { (Get-Field $_ 'commit', 'oid') -ceq $Head } | ForEach-Object { "$(Get-Field $_ 'author', 'login') $(Get-Field $_ 'state')" })

# --- The receipt.
$passed = @($considered | Where-Object { (Get-Field $_ 'bucket') -ceq 'pass' }).Count
$skipped = $considered.Count - $passed
$scopeText = switch ($Scope) {
    'ENTIRE' { "ENTIRE: every resulting file read whole, and the diff from the merge base with $targetBase, after CI was green" }
    'FULL' { "FULL: the diff from the merge base with $targetBase plus every changed file read whole" }
    'DELTA' { "DELTA: the diff since $Since, plus every file changed since read whole" }
}
$body = [Collections.Generic.List[string]]::new()
$body.Add('<!-- agent-bridge review-receipt v1 -->')
$body.Add("## Independent review receipt: sec-$Pr CLEAN")
$body.Add('')
$body.Add('Machine-generated by `build/review-receipt.ps1` from the reviewer''s session log and GitHub''s state on the head; nothing in it is typed by hand. Times are UTC.')
$body.Add('')
$body.Add('| | |')
$body.Add('|---|---|')
$body.Add("| Pull request | #$Pr at ``$Head`` into ``$targetBase``, the base main's quality/merge-rule.yml names |")
$body.Add("| Scope | $scopeText |")
$body.Add("| Reviewer | a ``$role`` agent, ``$ReviewerAgentId``, started by task call ``$callId`` |")
$body.Add("| Model | requested ``$(Format-Cell $requested)``, confirmed ``$(Format-Cell $confirmed)`` by subagent.completed and every tool call and message of the reviewer |")
$body.Add("| Ran | $(Format-Instant $started.At) to $(Format-Instant $completed.At) |")
$body.Add("| Session log | session ``$(Format-Cell $sessionId)`` (Copilot CLI $(Format-Cell $cliVersion)), events.jsonl SHA-256 ``$logHash`` over $($used.Length) bytes, $($events.Count) events |")
$body.Add("| CI | green at $(Format-Instant $captured.GreenAt), captured on the head by the coordinator's ``$($capture.Id)`` at $(Format-Instant $captureAt) (line $($capture.Complete.Line)), before the reviewer started; read again at $(Format-Instant $liveAt) with nothing completed since: $passed passed, $skipped skipped |")
$body.Add("| Diff read | ``git diff $diffBase $Head`` at $(Format-Instant $diffRead.Start.At) (``$($diffRead.Id)``) |")
$body.Add("| Threads | $($threads.Count), all resolved |")
$body.Add("| Reviews of the head | $(if ($onHead) { Format-Cell ($onHead -join ', ') } else { 'none' }) |")
$body.Add('')
$body.Add("### Reads ($($ledger.Count))")
$body.Add('')
$body.Add('| File | Lines | Read at | Tool call |')
$body.Add('|---|---|---|---|')
foreach ($read in $ledger) { $body.Add("| ``$(Format-Cell $read.Path)`` | $($read.Lines) | $(Format-Instant $read.At) | ``$($read.Id)`` |") }
$body.Add('')
$body.Add('### Checks')
$body.Add('')
$body.Add('| Check | Result | Completed |')
$body.Add('|---|---|---|')
foreach ($check in @($considered | Sort-Object { [string](Get-Field $_ 'name') })) {
    $done = Get-Instant (Get-Field $check 'completedAt')
    $body.Add("| $(Format-Cell ([string](Get-Field $check 'name'))) | $(Get-Field $check 'bucket') | $(if ($done) { Format-Instant $done } else { '' }) |")
}
$body.Add('')
# GitHub takes up to 65,536 characters in a comment; the report gives way first.
$limit = 60000 - (($body -join "`n").Length)
if ($limit -lt 2000) { Stop-Receipt 'the receipt is too long for one comment' }
$shownReport = if ($report.Length -gt $limit) { $report.Substring(0, $limit) + "`n`n(cut at $limit of $($report.Length) characters)" } else { $report }
$body.Add('<details><summary>The reviewer''s report</summary>')
$body.Add('')
$body.Add($shownReport.Trim())
$body.Add('')
$body.Add('</details>')
$receipt = $body -join "`n"
$description = "sec-$Pr CLEAN: $Scope into $targetBase, $role, $confirmed"
if ($description.Length -gt 140) { $description = $description.Substring(0, 140) }

if (-not $Post) {
    Write-Host "Dry run: nothing posted. With -Post this comment goes on #$Pr, then $receiptContext=success on $Head ('$description')." -ForegroundColor DarkGray
    $receipt
    return
}

# --- Posting: the comment, then the status that links it, each after reading the head again.
$null = Assert-Head 'just before posting the comment'
$payload = @{ body = $receipt } | ConvertTo-Json -Compress
$posted = $payload | & gh api -X POST "repos/$Repository/issues/$Pr/comments" --input -
if ($LASTEXITCODE) { throw "review-receipt: posting the comment failed ($LASTEXITCODE); it may or may not be on #$Pr, and no status was posted" }
$url = $null
try { $url = Get-Field (ConvertFrom-GhJson $posted 'the posted comment') 'html_url' } catch { }
if ($url -isnot [string] -or $url -notmatch "^https://github\.com/$([regex]::Escape($Repository))/pull/$Pr#issuecomment-\d+$") {
    throw "review-receipt PARTIAL: a comment was posted on #$Pr but GitHub gave no URL for it; no status was posted"
}
try {
    $null = Assert-Head 'just before posting the status'
    $null = Invoke-Gh api -X POST "repos/$Repository/statuses/$Head" -f state=success -f "context=$receiptContext" -f "description=$description" -f "target_url=$url"
}
catch {
    $why = $_.Exception.Message -replace '^review-receipt refused, nothing posted: ', ''
    throw "review-receipt PARTIAL: the comment $url is posted, but no $receiptContext status: $why"
}
Write-Host "Posted $url and $receiptContext=success on $Head." -ForegroundColor Green
$url
