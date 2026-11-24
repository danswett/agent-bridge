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
$oldLegs = Run 2 $head
$newLegs = Run 2 $head
$newLegs.jobs = @($newLegs.jobs | ForEach-Object {
    $_.name = $_.name -replace 'webkit ([123])/3', 'webkit $1/5'; $_
}) + @(4..5 | ForEach-Object { @{ name = "pwa-ui (webkit $_/5)"; head_sha = $head; status = 'completed'; conclusion = 'success' } })
$manifests = @(@($oldLegs.jobs.name), @($newLegs.jobs.name))
function FamilyCandidate($run) {
    function Get-Content { ConvertTo-Json -InputObject $manifests -Compress }
    & $candidate -Runs @($run)
}
foreach ($run in @($oldLegs, $newLegs)) {
    if ((FamilyCandidate $run).sha -ne $head) { throw 'an exact old or new Pages manifest was refused' }
    $missingLeg = $run.Clone()
    $missingLeg.jobs = @($run.jobs | Where-Object name -ne 'pwa-ui (chromium 1/3)')
    if ((FamilyCandidate $missingLeg).sha) { throw 'a missing leg passed an alternative Pages manifest' }
    $run.jobs = @($run.jobs) + @(@{ name = 'pwa-ui (webkit 6/6)'; head_sha = $head; status = 'completed'; conclusion = 'success' })
    if ((FamilyCandidate $run).sha) { throw 'an extra shard passed an alternative Pages manifest' }
}
$new = Run 2 $head; $old = Run 1 $live
if ((& $candidate -Runs @($old, $new)).sha -ne $head) { throw 'newest SHA did not win' }
if ((& $candidate -Runs @((Run 1 $head), (Run 2 $live))).sha -ne $live) { throw 'SHA lexical order replaced run order' }
$new.jobs[1].conclusion = 'cancelled'
if ((& $candidate -Runs @($new, $old)).sha -ne $live) { throw 'newest eligible SHA did not win' }
$new = Run 3 $live; $new.jobs[1].conclusion = 'skipped'
if ((& $candidate -Runs @($old, $new)).sha) { throw 'older evidence replaced the latest run for a SHA' }
# The two lanes (S2, #4104): the PWA jobs are in the FAST run today and move to the FULL run (full.yml, found by the commit's head_sha). A commit
# deploys when plan, product (ubuntu-latest) and every PWA job passed, each in whichever lane holds it. Pages never deploys on a FAST run that lacks them.
$fastNames = @('plan', 'product (ubuntu-latest)')
$pwaNames = @(Get-Content -Raw (Join-Path $PSScriptRoot 'pages-jobs.json') | ConvertFrom-Json | ForEach-Object { $_ } | Where-Object { $_ -cnotin $fastNames })
function LaneJobs([string]$sha, [string[]]$names) { @($names | ForEach-Object { @{ name = $_; head_sha = $sha; status = 'completed'; conclusion = 'success' } }) }
function LaneRun([int]$number, [string]$sha, [string[]]$names) { @{ run_number = $number; head_sha = $sha; event = 'push'; head_branch = 'main'; jobs = (LaneJobs $sha $names) } }
$script:fullCalls = @()
# A GetFull that answers from a table (sha -> the FULL run: Id, Conclusion, jobs; or $null) and records what it was asked.
function Candidate($runs, [hashtable]$fullBySha) {
    foreach ($key in $fullReplies.Keys) { $replies[$key] = $fullReplies[$key] }
    $calls = [System.Collections.Generic.List[string]]::new()
    $getFull = { param($sha) $calls.Add($sha); $fullBySha[$sha] }.GetNewClosure()
    $result = & $candidate -Runs @($runs) -GetFull $getFull
    $script:fullCalls = @($calls)
    $result
}
function FullRun([string]$sha, [string[]]$names, [string]$conclusion = 'success') { [pscustomobject]@{ Id = 77; Conclusion = $conclusion; jobs = (LaneJobs $sha $names) } }

$moved = LaneRun 2 $head $fastNames
$got = Candidate @($moved) @{ $head = (FullRun $head $pwaNames) }
if ($got.sha -ne $head -or @($got.jobs).Count -ne $fastNames.Count + $pwaNames.Count -or ($script:fullCalls -join ',') -ne $head) { throw 'a FAST run with the PWA jobs in the same commit''s FULL run was refused' }
$got = Candidate @((LaneRun 2 $head ($fastNames + $pwaNames))) @{}
if ($got.sha -ne $head -or $script:fullCalls.Count) { throw 'a FAST run that holds every job needed a FULL lookup' }
$got = Candidate @($moved) @{ $head = $null }
if ($got.sha -or $got.reason -ne 'named-jobs-not-success' -or $script:fullCalls.Count -ne 1) { throw 'Pages deployed on a FAST run alone' }
$got = Candidate @($moved) @{}
if ($got.sha) { throw 'Pages deployed with no FULL run to read' }
$got = Candidate @((LaneRun 3 $live $fastNames), (LaneRun 2 $head $fastNames)) @{ $head = (FullRun $head $pwaNames) }
if ($got.sha -ne $head -or ($script:fullCalls -join ',') -ne "$live,$head") { throw 'the newest commit with a FULL run did not win over a newer one without' }
$got = Candidate @((LaneRun 2 $head ($fastNames + @($pwaNames | Where-Object { $_ -like 'pwa-ui*' })))) @{ $head = (FullRun $head @('pwa-e2e')) }
if ($got.sha -ne $head) { throw 'PWA jobs split between the lanes (pwa-ui in FAST, pwa-e2e in FULL, as when only one has moved) were refused' }
$got = Candidate @((LaneRun 2 $head ($fastNames + @('pwa-e2e')))) @{ $head = (FullRun $head @($pwaNames | Where-Object { $_ -like 'pwa-ui*' })) }
if ($got.sha -ne $head) { throw 'PWA jobs split between the lanes (pwa-e2e in FAST, pwa-ui in FULL) were refused' }
$redFast = LaneRun 2 $head $fastNames; $redFast.jobs[1].conclusion = 'failure'
$got = Candidate @($redFast) @{ $head = (FullRun $head $pwaNames) }
if ($got.sha -or $script:fullCalls.Count) { throw 'a red FAST job was read past, or cost a FULL lookup' }
$got = Candidate @($moved) @{ $head = (FullRun $head $pwaNames 'failure') }
if ($got.sha -ne $head) { throw 'a FULL run red elsewhere (spikes, Arm64) held the Pages deploy: only the named PWA jobs count' }
foreach ($fault in @('red-leg', 'cancelled-leg', 'missing-leg', 'extra-shard', 'running', 'wrong-sha', 'duplicate')) {
    $fullJobs = LaneJobs $head $pwaNames
    switch ($fault) {
        'red-leg' { $fullJobs[0].conclusion = 'failure' }
        'cancelled-leg' { $fullJobs[2].conclusion = 'cancelled' }
        'missing-leg' { $fullJobs = @($fullJobs | Where-Object { $_.name -ne 'pwa-ui (webkit 3/3)' }) }
        'extra-shard' { $fullJobs += @{ name = 'pwa-ui (webkit 4/4)'; head_sha = $head; status = 'completed'; conclusion = 'success' } }
        'running' { $fullJobs[1].status = 'in_progress'; $fullJobs[1].conclusion = $null }
        'wrong-sha' { $fullJobs[1].head_sha = $live }
        'duplicate' { $run = LaneRun 2 $head ($fastNames + @('pwa-e2e')) }
    }
    $fastRun = if ($fault -eq 'duplicate') { $run } else { $moved }
    $got = Candidate @($fastRun) @{ $head = [pscustomobject]@{ Id = 77; Conclusion = 'success'; jobs = $fullJobs } }
    if ($got.sha) { throw "Pages deployed on a FULL run with a $fault" }
}
$legacy = & $candidate -Runs @($moved)
if ($legacy.sha) { throw 'without a FULL lookup, a run lacking the PWA jobs deployed' }
$root = Split-Path -Parent $PSScriptRoot
$workflow = Get-Content -Raw (Join-Path $root '.github' 'workflows' 'pages.yml')
$block = [regex]::Match($workflow, '(?ms)^  gate:\r?\n.*?^        run: \|\r?\n(.*?)(?=^  build:)')
if (-not $block.Success) { throw 'the actual Pages gate shell block was not found' }
function WorkflowGate(
    $runs, [string]$relation = 'identical', [string]$sha = $head, [switch]$Unreadable,
    [int]$TotalCount = 0, [switch]$AncestryError, [string]$Trigger = 'schedule',
    [switch]$NoCandidate, [string]$MainStatus = 'ahead', [switch]$MainError, [string]$AppAtRoot = 'true',
    [switch]$FullLane, [switch]$NoFullRun
) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) "pages-gate-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $dir | Out-Null
    $saved = @{}; foreach ($key in @('GITHUB_EVENT_NAME', 'GITHUB_REPOSITORY', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY', 'GITHUB_RUN_ID', 'APP_AT_ROOT')) { $saved[$key] = [Environment]::GetEnvironmentVariable($key) }
    $ci = Run 2 $sha; $jobs = $ci.jobs; $ci.Remove('jobs'); $ci.id = 1; $siteVersion = $live; $fencePath = "compare/$head...$sha"
    if ($NoCandidate) { $jobs[1].conclusion = 'failure' }
    # The two lanes: the push run holds only plan and product; the PWA jobs are in the same commit's FULL run, found by head_sha (none with -NoFullRun).
    $fastJobs = $jobs; $fullReplies = @{}
    if ($FullLane -or $NoFullRun) {
        $fastJobs = @($jobs | Where-Object { $_.name -in @('plan', 'product (ubuntu-latest)') })
        $fullRuns = if ($NoFullRun) { @() } else { @(@{ id = 77; conclusion = 'success'; head_sha = $sha; event = 'workflow_run'; head_branch = 'main' }) }
        $fullReplies = @{ "actions/workflows/full.yml/runs?head_sha=$sha&per_page=20" = @{ workflow_runs = $fullRuns }
            'actions/runs/77/jobs?filter=latest&per_page=100' = @{ jobs = @($jobs | Where-Object { $_.name -notin @('plan', 'product (ubuntu-latest)') }) } }
    }
    $configUri = if ($AppAtRoot -eq 'true') { 'https://pages.invalid/config.js?gate=1' } else { 'https://pages.invalid/app/config.js?gate=1' }
    $historyPath = 'actions/workflows/pages.yml/runs?branch=main&event=workflow_dispatch&per_page=100'
    $replies = @{
        'actions/workflows/ci.yml/runs?branch=main&event=push&per_page=20' = @{ workflow_runs = @($ci) }
        'actions/runs/1/jobs?filter=latest&per_page=100' = @{ jobs = $fastJobs }
        $historyPath = @{ workflow_runs = @($runs); total_count = [Math]::Max($TotalCount, @($runs).Count) }
        $fencePath = @{ status = $relation }
        "compare/$sha...main" = @{ status = $MainStatus }
        "compare/$live...$sha" = @{ status = 'ahead'; files = @(@{ filename = 'pwa/index.html' }) }
        'pages' = @{ html_url = 'https://pages.invalid/' }
    }
    foreach ($key in $fullReplies.Keys) { $replies[$key] = $fullReplies[$key] }
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
        if ($Trigger -eq 'workflow_dispatch' -or $NoCandidate -or $NoFullRun) {
            $expectedCalls = if ($Trigger -eq 'workflow_dispatch') { 0 } elseif ($NoFullRun) { 3 } else { 2 }
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
if ((WorkflowGate @()) -ne 'deploy') { throw 'a history with no rollback in it held the deploy (no rollback was ever made, #4369)' }
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
if ((WorkflowGate $manual 'ahead' ('c' * 40) -FullLane) -ne 'deploy') { throw 'a commit whose PWA jobs passed in its FULL run did not deploy' }
if ((WorkflowGate @() -NoFullRun) -ne 'skip') { throw 'Pages deployed on the FAST run alone' }
if ((WorkflowGate @() -Trigger workflow_dispatch -NoCandidate) -ne 'deploy') { throw 'manual dispatch must remain available without automatic CI evidence' }
# The rollback fence itself (#4369): held only while a rollback has no later successful resume; an unreadable or truncated history holds, and says so loudly.
$rollbackScript = Join-Path $PSScriptRoot 'pages-rollback.ps1'
function Fence($runs, [int]$total = -1, [switch]$Unreadable) {
    if ($Unreadable) { return (& $rollbackScript -Unreadable) }
    & $rollbackScript -Runs @($runs) -TotalCount $(if ($total -ge 0) { $total } else { @($runs).Count })
}
function ExpectFence($name, $state, $held, $reason, $loud) {
    if ($state.held -ne $held -or $state.reason -ne $reason -or $state.loud -ne $loud) { Write-Host "FAIL: $name -> held $($state.held), $($state.reason), loud $($state.loud)"; $script:failures++ }
}
$old = @{ head_sha = $head; display_title = 'Pages'; status = 'completed'; conclusion = 'success'; event = 'workflow_dispatch'; head_branch = 'main'; created_at = '2026-11-07T10:50:00Z' }
$rollbackRun = @{ head_sha = $head; display_title = 'Pages rollback'; status = 'completed'; conclusion = 'success'; event = 'workflow_dispatch'; head_branch = 'main'; created_at = '2026-11-11T18:00:00Z' }
$resumeRun = $rollbackRun.Clone(); $resumeRun.display_title = 'Pages resume'; $resumeRun.created_at = '2026-11-11T19:00:00Z'
ExpectFence 'empty history' (Fence @()) $false 'no-rollback' $false
ExpectFence 'only manual runs from before the rollback run names' (Fence @($old, $old)) $false 'no-rollback' $false
ExpectFence 'a rollback without a resume' (Fence @($rollbackRun)) $true 'rollback-active' $false
ExpectFence 'a rollback then a resume' (Fence @($rollbackRun, $resumeRun)) $false 'rollback-resumed' $false
ExpectFence 'a resume then a newer rollback' (Fence @($resumeRun, ($rollbackRun.Clone() | ForEach-Object { $_.created_at = '2026-11-12T00:00:00Z'; $_ }))) $true 'rollback-active' $false
ExpectFence 'unreadable history' (Fence @() -Unreadable) $true 'rollback-history-unavailable' $true
ExpectFence 'truncated history with no rollback in what was read' (Fence @($old) 101) $true 'rollback-history-truncated' $true
if ((Fence @($rollbackRun) 101).reason -ne 'rollback-active') { Write-Host 'FAIL: a rollback in a truncated history is still an active rollback'; $failures++ }
$missingTotal = & $rollbackScript -Runs @()
if (-not ($missingTotal.held -eq $true -and $missingTotal.loud -eq $true -and $missingTotal.reason -ceq 'rollback-history-unavailable')) { Write-Host "FAIL: a missing TotalCount must hold loudly -> held $($missingTotal.held), $($missingTotal.reason), loud $($missingTotal.loud)"; $failures++ }

# An explicit TotalCount that is no count of runs holds loudly too (#4425): it used to coerce to 0 and open the fence.
foreach ($bad in @($null, '', -1, 'abc', 1.5)) {
    $state = try { & $rollbackScript -Runs @() -TotalCount $bad 3>$null } catch { @{ held = 'threw'; reason = $_.Exception.Message; loud = $null } }
    ExpectFence "TotalCount $(if ($null -eq $bad) { 'null' } else { "'$bad'" })" $state $true 'rollback-history-unavailable' $true
}
ExpectFence 'a TotalCount read from JSON (a long)' (& $rollbackScript -Runs @() -TotalCount ([long]0) 3>$null) $false 'no-rollback' $false
# No rollback is no warning on every 5-minute gate run, nor a line in the gate's summary (#4425): the gate prints the reason once.
$summaryFile = New-TemporaryFile; $savedSummary = $env:GITHUB_STEP_SUMMARY; $env:GITHUB_STEP_SUMMARY = $summaryFile.FullName
try { $streams = @(& $rollbackScript -Runs @() -TotalCount 0 3>&1) } finally { $env:GITHUB_STEP_SUMMARY = $savedSummary }
$warned = @($streams | Where-Object { $_ -is [System.Management.Automation.WarningRecord] }); $noted = @(Get-Content $summaryFile.FullName); Remove-Item $summaryFile.FullName
if ($warned.Count -or $noted.Count) { Write-Host "FAIL: no rollback must print nothing itself -> warnings $($warned.Count), summary '$($noted -join ' | ')'"; $failures++ }
Write-Host $(if ($failures) { "pages gate: $failures failed" } else { 'pages gate: ok' })
# Last, always (PagesGateScriptTests pins it): #4370's first head lost this tail, and the file then passed whatever it found.
if ($failures) { exit 1 }
