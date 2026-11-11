#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$workflow = Get-Content -Raw (Join-Path $PSScriptRoot '..' '.github' 'workflows' 'merge-rule.yml')
if ($workflow -notmatch '(?ms)^  evaluate:\r?\n\s+needs: backstop\r?\n') {
    throw 'Cron must check queued evaluations before entering the shared writer queue.'
}
$evaluate = [regex]::Match($workflow, '(?ms)^  evaluate:\r?\n(.*?)(?=^  \w|\z)').Groups[1].Value
$condition = [regex]::Match($evaluate, '(?m)^    if: >-\r?\n((?:^      .*\r?\n)+)').Groups[1].Value
if ($condition -notmatch '!cancelled\(\)') { throw 'Event evaluation must retain !cancelled() when backstop is skipped.' }
. (Join-Path $PSScriptRoot 'merge-rule-backstop.ps1')

$now = [DateTimeOffset]'2026-11-11T20:00:00Z'
$script:calls = [System.Collections.Generic.List[string]]::new()
$script:replies = @{}
$script:failure = $null
function gh {
    $path = $args[-1]
    $history = $path -eq 'repos/example/repo/actions/workflows/merge-rule.yml/runs?event=schedule&status=completed&per_page=10'
    if ($args[0] -ne 'api' -or ($history -and $args.Count -ne 2) -or
        (-not $history -and ($args[1] -ne '--paginate' -or $args[2] -ne '--slurp'))) {
        throw 'Queue reads must retain every page.'
    }
    $script:calls.Add($path)
    if ($script:failure -eq $path) { $global:LASTEXITCODE = 1; return }
    $global:LASTEXITCODE = 0
    if (-not $script:replies.ContainsKey($path)) { throw "Unexpected API read: $path" }
    $script:replies[$path]
}
function Check([string]$name, [bool]$expected, [hashtable[]]$runs, [hashtable]$jobs = @{}, [string]$bad = '',
    [switch]$Warn, [DateTimeOffset]$At = $now, [string]$WriterConclusion = 'success', [switch]$NoWriter) {
    $script:calls.Clear()
    $script:failure = $bad
    $script:replies = @{}
    foreach ($status in 'queued', 'pending', 'waiting', 'in_progress') {
        $selected = @($runs | Where-Object status -eq $status)
        $script:replies["repos/example/repo/actions/workflows/merge-rule.yml/runs?status=$status&per_page=100"] =
            (@(@{ workflow_runs = $selected; total_count = $selected.Count }) | ConvertTo-Json -Depth 10 -AsArray -Compress)
    }
    foreach ($id in $jobs.Keys) {
        $script:replies["repos/example/repo/actions/runs/$id/jobs?filter=latest&per_page=100"] =
            (@(@{ jobs = @($jobs[$id]); total_count = @($jobs[$id]).Count }) | ConvertTo-Json -Depth 10 -AsArray -Compress)
    }
    $history = @(if (-not $NoWriter) { Run 80 'schedule' 'completed' })
    $script:replies['repos/example/repo/actions/workflows/merge-rule.yml/runs?event=schedule&status=completed&per_page=10'] =
        (@{ workflow_runs = $history; total_count = $history.Count } | ConvertTo-Json -Depth 10 -Compress)
    $writer = @{ name = 'merge-rule-sweep-v1'; status = 'completed'; conclusion = $WriterConclusion; completed_at = $now.AddMinutes(-1).ToString('o') }
    $script:replies['repos/example/repo/actions/runs/80/jobs?filter=latest&per_page=100'] =
        (@(@{ jobs = @($writer); total_count = 1 }) | ConvertTo-Json -Depth 10 -AsArray -Compress)
    $warnings = @()
    $information = @()
    $actual = Test-MergeRuleBackstop -Repo example/repo -RunId 99 -Now $At -WarningVariable warnings -InformationVariable information
    if ($actual -ne $expected) { throw "$name expected proceed=$expected, got $actual" }
    if ($Warn -and -not $warnings.Count) { throw "$name must warn visibly" }
    if ($Warn -and -not @($information | Where-Object { "$($_.MessageData)" -like '::warning::*' }).Count) {
        throw "$name must emit a GitHub warning annotation"
    }
    Write-Host "PASS: $name"
}
function Run([long]$id, [string]$event = 'status', [string]$status = 'in_progress', [int]$minutes = 1) {
    @{ id = $id; event = $event; status = $status; created_at = $now.AddMinutes(-$minutes).ToString('o') }
}
function Job([string]$name = 'merge-rule-scoped-v1', [string]$status = 'queued') {
    @{ name = $name; status = $status }
}

Check 'an empty queue proceeds' $true @()
Check 'a queued scoped evaluation suppresses cron' $false @((Run 1)) @{ 1 = @((Job)) }
Check 'a pending scoped evaluation suppresses cron' $false @((Run 1 'status' 'pending')) @{ 1 = @((Job 'merge-rule-scoped-v1' 'pending')) }
Check 'a waiting scoped evaluation suppresses cron' $false @((Run 1 'workflow_run' 'waiting')) @{ 1 = @((Job 'merge-rule-scoped-v1' 'waiting')) }
Check 'the current run does not suppress itself' $true @((Run 99))
Check 'another cron or dispatch does not suppress cron' $true @((Run 1 'schedule'), (Run 2 'workflow_dispatch'))
Check 'skipped or cancelled runs do not suppress cron' $true @((Run 1 'status' 'completed'), (Run 2 'status' 'cancelled'))
Check 'an ineligible skipped job does not suppress cron' $true @((Run 1)) @{ 1 = @((Job 'merge-rule-scoped-v1' 'completed')) }
Check 'a main sweep does not suppress cron' $true @((Run 1 'workflow_run')) @{ 1 = @((Job 'merge-rule-sweep-v1')) }
Check 'a running writer does not suppress cron' $true @((Run 1)) @{ 1 = @((Job 'merge-rule-scoped-v1' 'in_progress')) }
Check 'a captured legacy queued job name visibly proceeds' $true @((Run 1)) @{ 1 = @((Job 'evaluate' 'pending')) } -Warn
Check 'uncertainty takes precedence over a fresh queued evaluation' $true @((Run 1), (Run 2)) @{ 1 = @((Job)); 2 = @((Job 'evaluate' 'pending')) } -Warn
Check 'a run with no queued evaluation does not suppress cron' $true @((Run 1 'pull_request_target' 'queued')) @{ 1 = @() }
foreach ($elapsed in 0, 10, 30) {
    $fresh = Run 1
    $fresh.created_at = $now.AddMinutes($elapsed - 1).ToString('o')
    Check "fresh replacements cannot defer cron forever at tick $elapsed" ($elapsed -eq 30) @($fresh) @{ 1 = @((Job)) } -At $now.AddMinutes($elapsed) -Warn:($elapsed -eq 30)
}
Check 'a recent failed cron writer still allows deferral' $false @((Run 1)) @{ 1 = @((Job)) } -WriterConclusion failure
Check 'a skipped cron writer does not renew the bound' $true @((Run 1)) @{ 1 = @((Job)) } -WriterConclusion skipped -Warn
Check 'a cancelled cron writer does not renew the bound' $true @((Run 1)) @{ 1 = @((Job)) } -WriterConclusion cancelled -Warn
Check 'no completed cron writer visibly proceeds' $true @((Run 1)) @{ 1 = @((Job)) } -NoWriter -Warn
Check 'unreadable cron history visibly proceeds' $true @((Run 1)) @{ 1 = @((Job)) } 'repos/example/repo/actions/workflows/merge-rule.yml/runs?event=schedule&status=completed&per_page=10' -Warn
Check 'an old queued evaluation cannot starve the backstop' $true @((Run 1 'status' 'in_progress' 31), (Run 2)) @{ 1 = @((Job)); 2 = @((Job)) } -Warn
Check 'the thirty minute boundary proceeds' $true @((Run 1 'status' 'queued' 30)) @{ 1 = @((Job)) } -Warn
Check 'an unreadable queue visibly proceeds' $true @() @{} 'repos/example/repo/actions/workflows/merge-rule.yml/runs?status=queued&per_page=100' -Warn
Check 'unreadable jobs visibly proceed' $true @((Run 1)) @{ 1 = @((Job)) } 'repos/example/repo/actions/runs/1/jobs?filter=latest&per_page=100' -Warn

$script:replies['repos/example/repo/actions/workflows/merge-rule.yml/runs?status=queued&per_page=100'] = '<html>bad gateway</html>'
if (-not (Test-MergeRuleBackstop -Repo example/repo -RunId 99 -Now $now)) {
    throw 'A malformed queue must proceed, never suppress evaluation.'
}
Write-Host 'PASS: malformed queue visibly proceeds'

Check 'pagination fixture starts with a queued evaluation' $false @((Run 1)) @{ 1 = @((Job)) }
$path = 'repos/example/repo/actions/runs/1/jobs?filter=latest&per_page=100'
$script:replies[$path] = @(
    @{ total_count = 2; jobs = @((Job 'merge-rule-sweep-v1')) },
    @{ total_count = 2; jobs = @((Job)) }
) | ConvertTo-Json -Depth 10 -Compress
if (Test-MergeRuleBackstop -Repo example/repo -RunId 99 -Now $now) {
    throw 'A scoped evaluation on a later jobs page must be found.'
}
Write-Host 'PASS: a later jobs page identifies queued scoped work'
$script:replies[$path] = @(@{ total_count = 2; jobs = @((Job)) }) | ConvertTo-Json -Depth 10 -AsArray -Compress
$warnings = @()
if (-not (Test-MergeRuleBackstop -Repo example/repo -RunId 99 -Now $now -WarningVariable warnings) -or -not $warnings.Count) {
    throw 'Incomplete pagination must warn and proceed, not suppress cron.'
}
Write-Host 'PASS: incomplete pagination visibly proceeds'

Check 'history budget fixture starts with a recent writer' $false @((Run 1)) @{ 1 = @((Job)) }
$history = @(80..89 | ForEach-Object { Run $_ 'schedule' 'completed' })
$script:replies['repos/example/repo/actions/workflows/merge-rule.yml/runs?event=schedule&status=completed&per_page=10'] =
    (@{ workflow_runs = $history; total_count = 800 } | ConvertTo-Json -Depth 10 -Compress)
foreach ($run in $history) {
    $writer = @{ name = 'merge-rule-sweep-v1'; status = 'completed'; conclusion = 'success'; completed_at = $now.AddMinutes(-31).ToString('o') }
    $script:replies["repos/example/repo/actions/runs/$($run.id)/jobs?filter=latest&per_page=100"] =
        (@(@{ jobs = @($writer); total_count = 1 }) | ConvertTo-Json -Depth 10 -AsArray -Compress)
}
$script:calls.Clear()
if (-not (Test-MergeRuleBackstop -Repo example/repo -RunId 99 -Now $now) -or $script:calls.Count -ne 16) {
    throw 'Old history must proceed after at most ten historical job reads, not scan all 800 runs.'
}
Write-Host 'PASS: old cron history has a bounded lookup budget'
