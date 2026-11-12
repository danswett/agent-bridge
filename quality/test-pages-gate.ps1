#Requires -Version 7
# quality/pages-gate.ps1: the newest green commit deploys; one already live, or older, does not (#1313).
$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot 'pages-gate.ps1'
$candidate = Join-Path $PSScriptRoot 'pages-candidate.ps1'
$head = 'a' * 40
$live = 'b' * 40
$failures = 0
function Check([string]$name, [string]$expect, [hashtable]$gateArgs) {
    $got = (& $gate -Head $head -MainStatus ahead -Files @('pwa/index.html') @gateArgs)[0]
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
    $got = (& $gate -Head $head -Live $live -Status ahead -MainStatus $mainStatus -Files @('pwa/index.html'))[0]
    if (($got -eq 'deploy') -ne ($mainStatus -in @('identical', 'ahead'))) { throw 'main ancestry decision failed' }
}
foreach ($files in @(@('docs/status.md'), @('src/AgentBridge.Host/Program.cs'), @())) {
    if ((& $gate -Head $head -Live $live -Status ahead -MainStatus ahead -Files $files)[0] -ne 'skip') { throw 'non-Pages change deployed' }
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
if ($failures) { exit 1 }
Write-Host 'pages gate: ok'
