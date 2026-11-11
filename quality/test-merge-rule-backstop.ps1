#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$workflow = Get-Content -Raw (Join-Path $PSScriptRoot '..' '.github' 'workflows' 'merge-rule.yml')
if ($workflow -notmatch '(?ms)^  evaluate:\r?\n\s+needs: backstop\r?\n') {
    throw 'Cron must check queued evaluations before entering the shared writer queue.'
}
. (Join-Path $PSScriptRoot 'merge-rule-backstop.ps1')

$now = [DateTimeOffset]'2026-11-11T20:00:00Z'
$script:calls = [System.Collections.Generic.List[string]]::new()
$script:replies = @{}
$script:failure = $null
function gh {
    $path = $args[1]
    $script:calls.Add($path)
    if ($script:failure -eq $path) { $global:LASTEXITCODE = 1; return }
    $global:LASTEXITCODE = 0
    if (-not $script:replies.ContainsKey($path)) { throw "Unexpected API read: $path" }
    $script:replies[$path]
}
function Check([string]$name, [bool]$expected, [hashtable[]]$runs, [hashtable]$jobs = @{}, [string]$bad = '') {
    $script:calls.Clear()
    $script:failure = $bad
    $script:replies = @{
        'repos/example/repo/actions/workflows/merge-rule.yml/runs?per_page=100' =
            (@(@{ workflow_runs = @($runs); total_count = $runs.Count }) | ConvertTo-Json -Depth 10 -AsArray -Compress)
    }
    foreach ($id in $jobs.Keys) {
        $script:replies["repos/example/repo/actions/runs/$id/jobs?filter=latest&per_page=100"] =
            (@(@{ jobs = @($jobs[$id]); total_count = @($jobs[$id]).Count }) | ConvertTo-Json -Depth 10 -AsArray -Compress)
    }
    $actual = Test-MergeRuleBackstop -Repo example/repo -RunId 99 -Now $now
    if ($actual -ne $expected) { throw "$name expected proceed=$expected, got $actual" }
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
Check 'a queued job with an unknown name visibly proceeds' $true @((Run 1)) @{ 1 = @((Job 'old-evaluate-name')) }
Check 'uncertainty takes precedence over a fresh queued evaluation' $true @((Run 1), (Run 2)) @{ 1 = @((Job)); 2 = @((Job 'old-evaluate-name')) }
Check 'a run with no queued evaluation does not suppress cron' $true @((Run 1 'pull_request_target' 'queued')) @{ 1 = @() }
Check 'an old queued evaluation cannot starve the backstop' $true @((Run 1 'status' 'in_progress' 31), (Run 2)) @{ 1 = @((Job)); 2 = @((Job)) }
Check 'the thirty minute boundary proceeds' $true @((Run 1 'status' 'queued' 30)) @{ 1 = @((Job)) }
Check 'an unreadable queue visibly proceeds' $true @() @{} 'repos/example/repo/actions/workflows/merge-rule.yml/runs?per_page=100'
Check 'unreadable jobs visibly proceed' $true @((Run 1)) @{ 1 = @((Job)) } 'repos/example/repo/actions/runs/1/jobs?filter=latest&per_page=100'

$script:replies['repos/example/repo/actions/workflows/merge-rule.yml/runs?per_page=100'] = '<html>bad gateway</html>'
if (-not (Test-MergeRuleBackstop -Repo example/repo -RunId 99 -Now $now)) {
    throw 'A malformed queue must proceed, never suppress evaluation.'
}
Write-Host 'PASS: malformed queue visibly proceeds'
