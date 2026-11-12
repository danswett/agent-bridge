#Requires -Version 7
# quality/pages-gate.ps1: the newest green commit deploys; one already live, or older, does not (#1313).
$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot 'pages-gate.ps1'
$candidate = Join-Path $PSScriptRoot 'pages-candidate.ps1'
$head = 'a' * 40
$live = 'b' * 40
$failures = 0
$resumed = if ((Get-Command $gate).Parameters.ContainsKey('RollbackStatus')) { @{ RollbackStatus = 'ahead' } } else { @{} }
function Check([string]$name, [string]$expect, [hashtable]$gateArgs) {
    $got = (& $gate -Head $head -MainStatus ahead -Files @('pwa/index.html') @resumed @gateArgs)[0]
    if ($got -ne $expect) { Write-Host "FAIL: $name -> $got, expected $expect"; $script:failures++ }
}
Check 'a newer commit deploys' 'deploy' @{ Live = $live; Status = 'ahead' }
Check 'an older commit is skipped' 'skip' @{ Live = $live; Status = 'behind' }
Check 'the commit already live is skipped' 'skip' @{ Live = $head; Status = 'identical' }
Check 'the live commit alone is enough to skip the same commit' 'skip' @{ Live = $head }
Check 'a diverged commit is skipped' 'skip' @{ Live = $live; Status = 'diverged' }
Check 'a compare that failed is skipped' 'skip' @{ Live = $live }
Check 'an unreadable live site is skipped' 'skip' @{ Live = '' }
Check 'a live version that is not a commit is skipped' 'skip' @{ Live = 'dev'; Status = 'behind' }
foreach ($mainStatus in @('identical', 'ahead', 'behind', 'diverged', '')) {
    $got = (& $gate -Head $head -Live $live -Status ahead -MainStatus $mainStatus -Files @('pwa/index.html') @resumed)[0]
    if (($got -eq 'deploy') -ne ($mainStatus -in @('identical', 'ahead'))) { throw 'main ancestry decision failed' }
}
foreach ($files in @(@('docs/status.md'), @('src/AgentBridge.Host/Program.cs'), @())) {
    if ((& $gate -Head $head -Live $live -Status ahead -MainStatus ahead -Files $files @resumed)[0] -ne 'skip') { throw 'non-Pages change deployed' }
}
function Run([int]$number, [string]$sha) {
    @{ run_number = $number; head_sha = $sha; event = 'push'; head_branch = 'main'; jobs = @(
        Get-Content -Raw (Join-Path $PSScriptRoot 'pages-jobs.json') | ConvertFrom-Json | ForEach-Object {
            @{ name = $_; head_sha = $sha; status = 'completed'; conclusion = 'success' }
        }) }
}
foreach ($fault in @('none', 'installer-red', 'failure', 'cancelled', 'skipped', 'missing', 'running', 'wrong-sha', 'docs-only', 'extra-shard')) {
    $run = Run 2 $head
    switch ($fault) {
        'installer-red' { $run.jobs += @{ name = 'product (windows-11-arm)'; conclusion = 'failure' }; $run.jobs += @{ name = 'app-installer'; conclusion = 'failure' } }
        'missing' { $run.jobs = $run.jobs | Where-Object name -ne 'pwa-ui (chromium 1/3)' }
        'extra-shard' { $run.jobs += @{ name = 'pwa-ui (webkit 4/4)'; head_sha = $head; status = 'completed'; conclusion = 'success' } }
        'running' { $run.jobs[1].status = 'in_progress' }
        'wrong-sha' { $run.jobs[1].head_sha = $live }
        'docs-only' { $run.jobs[-1].conclusion = 'skipped' }
        { $_ -in @('failure', 'cancelled', 'skipped') } { $run.jobs[1].conclusion = $fault }
    }
    $got = & $candidate -Runs @($run)
    $expected = if ($fault -in @('none', 'installer-red')) { $head } else { '' }
    if ($got.sha -ne $expected) { throw "candidate fixture failed: $fault" }
}
$new = Run 2 $head; $old = Run 1 $live
if ((& $candidate -Runs @($old, $new)).sha -ne $head) { throw 'newest SHA did not win' }
if ((& $candidate -Runs @((Run 1 $head), (Run 2 $live))).sha -ne $live) { throw 'SHA lexical order replaced run order' }
$new.jobs[1].conclusion = 'cancelled'
if ((& $candidate -Runs @($new, $old)).sha -ne $live) { throw 'newest eligible SHA did not win' }
$new = Run 3 $live; $new.jobs[1].conclusion = 'skipped'
if ((& $candidate -Runs @($old, $new)).sha) { throw 'older evidence replaced the latest run for a SHA' }
$root = Split-Path -Parent $PSScriptRoot
$workflow = Get-Content -Raw (Join-Path $root '.github' 'workflows' 'pages.yml')
$block = [regex]::Match($workflow, '(?ms)^  gate:\r?\n.*?^        run: \|\r?\n(.*?)(?=^  build:)')
if (-not $block.Success) { throw 'the actual Pages gate shell block was not found' }
function WorkflowGate(
    $runs, [string]$relation = 'identical', [string]$sha = $head, [switch]$Unreadable,
    [int]$TotalCount = 0, [switch]$AncestryError, [string]$Trigger = 'schedule',
    [switch]$NoCandidate, [string]$MainStatus = 'ahead', [switch]$MainError, [string]$AppAtRoot = 'true'
) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) "pages-gate-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $dir | Out-Null
    $saved = @{}; foreach ($key in @('GITHUB_EVENT_NAME', 'GITHUB_REPOSITORY', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY', 'GITHUB_RUN_ID', 'APP_AT_ROOT')) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }
    $ci = Run 2 $sha; $jobs = $ci.jobs; $ci.Remove('jobs'); $ci.id = 1; $siteVersion = $live; $fencePath = "compare/$head...$sha"
    if ($NoCandidate) { $jobs[1].conclusion = 'failure' }
    $configUri = if ($AppAtRoot -eq 'true') { 'https://pages.invalid/config.js?gate=1' } else { 'https://pages.invalid/app/config.js?gate=1' }
    $historyPath = 'actions/workflows/pages.yml/runs?branch=main&event=workflow_dispatch&per_page=100'
    $replies = @{
        'actions/workflows/ci.yml/runs?branch=main&event=push&per_page=20' = @{ workflow_runs = @($ci) }
        'actions/runs/1/jobs?filter=latest&per_page=100' = @{ jobs = $jobs }
        $historyPath = @{ workflow_runs = @($runs); total_count = [Math]::Max($TotalCount, @($runs).Count) }
        $fencePath = @{ status = $relation }
        "compare/$sha...main" = @{ status = $MainStatus }
        "compare/$live...$sha" = @{ status = 'ahead'; files = @(@{ filename = 'pwa/index.html' }) }
        'pages' = @{ html_url = 'https://pages.invalid/' }
    }
    $calls = [System.Collections.Generic.List[string]]::new()
    function gh {
        $path = "$($args[1])" -replace '^repos/example/repo/', ''
        $calls.Add($path)
        if ($args.Count -ne 2 -or $args[0] -ne 'api' -or -not $replies.ContainsKey($path)) { throw "unexpected API call: $args" }
        $global:LASTEXITCODE = 0
        if (($Unreadable -and $path -eq $historyPath) -or ($AncestryError -and $path -eq $fencePath) -or ($MainError -and $path -eq "compare/$sha...main")) { $global:LASTEXITCODE = 1; return }
        $replies[$path] | ConvertTo-Json -Depth 10 -Compress
    }
    function Invoke-RestMethod($Uri, $Headers) { if ($Uri -ne $configUri) { throw "unexpected site request: $Uri" }; 'APP_VERSION = "' + $siteVersion + '"' }
    Push-Location $root
    try {
        $env:GITHUB_EVENT_NAME = $Trigger; $env:GITHUB_REPOSITORY = 'example/repo'; $env:GITHUB_RUN_ID = '1'; $env:APP_AT_ROOT = $AppAtRoot
        $env:GITHUB_OUTPUT = Join-Path $dir 'output'; $env:GITHUB_STEP_SUMMARY = Join-Path $dir 'summary'
        New-Item -ItemType File -Path @($env:GITHUB_OUTPUT, $env:GITHUB_STEP_SUMMARY) | Out-Null
        $step = Join-Path $dir 'gate.ps1'; Set-Content -LiteralPath $step -Value ($block.Groups[1].Value -replace '(?m)^          ', '')
        & $step
        $output = @(Get-Content $env:GITHUB_OUTPUT); $decision = @($output | Where-Object { $_ -match '^deploy=(true|false)$' })
        if ($decision.Count -ne 1 -or @($calls | Where-Object { -not $replies.ContainsKey($_) }).Count) { throw 'the gate must emit exactly one decision using only mocked APIs' }
        $summary = @(Get-Content $env:GITHUB_STEP_SUMMARY)
        if ($Trigger -eq 'workflow_dispatch' -or $NoCandidate) {
            $expectedCalls = if ($Trigger -eq 'workflow_dispatch') { 0 } else { 2 }
            if ($output.Count -ne 1 -or $calls.Count -ne $expectedCalls -or $summary.Count) { throw 'manual dispatch and no-candidate exits must not emit a tested SHA or read deployment evidence' }
        } else {
            if ($output -notcontains "sha=$sha" -or @($calls | Where-Object { $_ -eq $historyPath }).Count -ne 1) { throw 'an automatic gate must emit its tested SHA after one bounded history read' }
            if ($summary.Count -ne ($jobs.Count + 1) -or $summary[0] -notlike "### Pages candidate $sha (*)" -or (Compare-Object @($jobs | ForEach-Object { "$($_.name): $($_.conclusion)" }) @($summary | Select-Object -Skip 1))) { throw 'the summary must identify the tested SHA and every named job conclusion' }
        }
        if ($decision[0] -eq 'deploy=true') { 'deploy' } else { 'skip' }
    } finally {
        Pop-Location
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
        Remove-Item -LiteralPath $dir -Recurse
    }
}
$manual = @{ head_sha = $head; display_title = 'Pages rollback'; status = 'completed'; conclusion = 'success'; event = 'workflow_dispatch'; head_branch = 'main'; created_at = '2026-11-11T18:00:00Z'; run_number = 90 }
foreach ($tick in 1..2) {
    if ((WorkflowGate $manual) -ne 'skip') { throw 'cron replayed bad B after rollback to good A, without new CI' }
}
foreach ($relation in @('behind', 'diverged', '')) {
    if ((WorkflowGate $manual $relation) -ne 'skip') { throw 'an unproven rollback fence was cleared' }
}
if ((WorkflowGate $manual 'ahead' ('c' * 40)) -ne 'deploy') { throw 'newer green C did not clear the B rollback fence' }
$resume = $manual.Clone(); $resume.display_title = 'Pages resume'; $resume.created_at = '2026-11-11T19:00:00Z'; $resume.run_number = 1
foreach ($outcome in @('failure', 'cancelled', $null)) {
    $resume.conclusion = $outcome
    if ((WorkflowGate @($manual, $resume)) -ne 'skip') { throw 'an unsuccessful resume cleared the rollback fence' }
}
$resume.conclusion = 'success'
if ((WorkflowGate @($manual, $resume)) -ne 'deploy') { throw 'explicit successful resume did not clear the rollback fence' }
$tie = $resume.Clone(); $tie.created_at = $manual.created_at
if ((WorkflowGate @($manual, $tie)) -ne 'skip') { throw 'equal-time rollback and resume must retain the fence' }
$resume.status = 'in_progress'
if ((WorkflowGate @($manual, $resume)) -ne 'skip') { throw 'pending resume cleared the rollback fence' }
$manual.status = 'queued'; $manual.conclusion = $null
if ((WorkflowGate $manual) -ne 'skip') { throw 'pending rollback did not hold the fence' }
if ((WorkflowGate @() -Unreadable) -ne 'skip') { throw 'unreadable history cleared the rollback fence' }
if ((WorkflowGate $manual -AncestryError) -ne 'skip') { throw 'failed ancestry lookup cleared the rollback fence' }
if ((WorkflowGate @($resume) -TotalCount 101) -ne 'skip') { throw 'truncated history cleared the rollback fence' }
if ((WorkflowGate @()) -ne 'skip') { throw 'expired history cleared the rollback fence' }
foreach ($trigger in @('schedule', 'workflow_run')) {
    if ((WorkflowGate $manual -Trigger $trigger) -ne 'skip') { throw "$trigger bypassed the rollback fence" }
    if ((WorkflowGate @() -Trigger $trigger -NoCandidate) -ne 'skip') { throw "$trigger deployed without an eligible candidate" }
    foreach ($mainRelation in @('ahead', 'identical', 'behind', 'diverged', '')) {
        $got = WorkflowGate $manual 'ahead' ('c' * 40) -Trigger $trigger -MainStatus $mainRelation
        if (($got -eq 'deploy') -ne ($mainRelation -in @('ahead', 'identical'))) { throw "$trigger ignored main ancestry: $mainRelation" }
    }
    $refused = $false
    try { WorkflowGate $manual 'ahead' ('c' * 40) -Trigger $trigger -MainError | Out-Null } catch { $refused = $_.Exception.Message -eq 'pages-api-unavailable' }
    if (-not $refused) { throw "$trigger did not fail closed on main API error" }
    if ((WorkflowGate $manual 'ahead' ('c' * 40) -Trigger $trigger -AppAtRoot '') -ne 'deploy') { throw "$trigger did not read the /app live version" }
}
if ((WorkflowGate @() -Trigger workflow_dispatch -NoCandidate) -ne 'deploy') { throw 'manual dispatch must remain available without automatic CI evidence' }
if ($failures) { exit 1 }
Write-Host 'pages gate: ok'
