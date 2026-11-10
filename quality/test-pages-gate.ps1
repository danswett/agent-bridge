#Requires -Version 7
# quality/pages-gate.ps1: the newest green commit deploys; one already live, or older, does not (#1313).
$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot 'pages-gate.ps1'
$head = 'a' * 40
$live = 'b' * 40
$failures = 0
function Check([string]$name, [string]$expect, [hashtable]$gateArgs) {
    $got = (& $gate -Head $head @gateArgs)[0]
    if ($got -ne $expect) { Write-Host "FAIL: $name -> $got, expected $expect"; $script:failures++ }
}
Check 'a newer commit deploys' 'deploy' @{ Live = $live; Status = 'ahead' }
Check 'an older commit is skipped' 'skip' @{ Live = $live; Status = 'behind' }
Check 'the commit already live is skipped' 'skip' @{ Live = $head; Status = 'identical' }
Check 'the live commit alone is enough to skip the same commit' 'skip' @{ Live = $head }
Check 'a commit off main''s line deploys' 'deploy' @{ Live = $live; Status = 'diverged' }
Check 'a compare that failed deploys' 'deploy' @{ Live = $live }
Check 'an unreadable live site deploys' 'deploy' @{ Live = '' }
Check 'a live version that is not a commit deploys' 'deploy' @{ Live = 'dev'; Status = 'behind' }
if ($failures) { exit 1 }
Write-Host 'pages gate: ok'
