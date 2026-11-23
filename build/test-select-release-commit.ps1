# Checks build/select-release-commit.ps1 against fixtures: nothing is read from GitHub.
#
#   pwsh build/test-select-release-commit.ps1
#
# What it guards: the release train publishes the newest main commit whose CI passed whole
# (quality-gate and both app jobs), that is a descendant of the latest release's commit, whose
# artifacts for both architectures are unexpired and whose run number is above every published
# version; and publishes nothing when that commit is already out.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'select-release-commit.ps1')

$failures = 0
function Check([string]$name, $actual, $expected) {
    if ($actual -ceq $expected) { Write-Host "PASS $name" } else { $script:failures++; Write-Host "FAIL ${name}: expected '$expected', got '$actual'" }
}
function Run([string]$sha, [int]$number, [string]$gate = 'success', [string]$x64 = 'success', [string]$arm = 'success', [string]$conclusion = 'success', [bool]$fresh = $true, [hashtable]$more = @{}) {
    $all = @{ 'quality-gate' = $gate; 'app (win-x64)' = $x64; 'app (win-arm64)' = $arm }
    foreach ($key in $more.Keys) { $all[$key] = $more[$key] }
    [pscustomobject]@{ Sha = $sha; Number = $number; Conclusion = $conclusion
        Jobs = @{ 'quality-gate' = $gate; 'app-x64' = $x64; 'app-arm64' = $arm }; ArtifactsUnexpired = $fresh; AllJobs = $all }
}
$out = [pscustomobject]@{ Tag = 'v0.1.100'; Commit = 'sha100' }
$follows = { param($sha, $base) $sha -cne 'stray' }
function Pick($runs, $latest = $out) { $r = Select-ReleaseCommit -Runs $runs -Latest $latest -IsDescendant $follows; if ($r) { $r.Sha } else { $null } }

Check 'newest passing commit wins' (Pick @((Run 'sha103' 103), (Run 'sha102' 102))) 'sha103'
Check 'a red newest run is skipped for the next passing one' (Pick @((Run 'sha103' 103 -gate failure), (Run 'sha102' 102))) 'sha102'
Check 'an arm64 app job failure does not stop the pick when quality-gate and x64 passed' (Pick @((Run 'sha103' 103 -arm failure), (Run 'sha102' 102))) 'sha103'
Check 'a run with no arm64 job result at all is still picked' (Pick @(([pscustomobject]@{ Sha = 'sha103'; Number = 103; Conclusion = 'success'; Jobs = @{ 'quality-gate' = 'success'; 'app-x64' = 'success' }; ArtifactsUnexpired = $true; AllJobs = @{} }))) 'sha103'
Check 'a failed x64 app job is not a pass' (Pick @((Run 'sha103' 103 -x64 failure), (Run 'sha102' 102))) 'sha102'
Check 'a cancelled run counts when quality-gate and both apps succeeded' (Pick @((Run 'sha103' 103 -conclusion cancelled))) 'sha103'
Check 'an arm64-only failed run (conclusion failure, gate and x64 green) is publishable' (Pick @((Run 'sha103' 103 -arm failure -conclusion failure), (Run 'sha102' 102))) 'sha103'
Check 'a failed run with quality-gate red is not publishable' (Pick @((Run 'sha103' 103 -gate failure -conclusion failure), (Run 'sha102' 102))) 'sha102'
Check 'a run whose only failed jobs are Arm64 jobs is publishable' (Pick @((Run 'sha103' 103 -conclusion failure -more @{ 'app (win-arm64)' = 'failure'; 'product (windows-11-arm)' = 'failure' }), (Run 'sha102' 102))) 'sha103'
Check 'a failed non-Arm64 job (relay-image-sarif) beside green gate and x64 is not publishable' (Pick @((Run 'sha103' 103 -conclusion failure -more @{ 'app (win-arm64)' = 'failure'; 'relay-image-sarif' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'a failed publish job is not publishable' (Pick @((Run 'sha103' 103 -conclusion failure -more @{ 'publish' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'a cancelled run with a failed relay-image-sarif is not publishable' (Pick @((Run 'sha103' 103 -conclusion cancelled -more @{ 'relay-image-sarif' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'a run whose job results are unknown is not publishable' (Pick @(([pscustomobject]@{ Sha = 'sha103'; Number = 103; Conclusion = 'success'; Jobs = @{ 'quality-gate' = 'success'; 'app-x64' = 'success' }; ArtifactsUnexpired = $true }), (Run 'sha102' 102))) 'sha102'
Check 'a failed run with a cancelled non-Arm64 delivery job is not publishable' (Pick @((Run 'sha103' 103 -conclusion failure -more @{ 'sign' = 'cancelled' }), (Run 'sha102' 102))) 'sha102'
Check 'a superseded (cancelled) run with a cancelled delivery job still counts' (Pick @((Run 'sha103' 103 -conclusion cancelled -more @{ 'sign' = 'cancelled' }))) 'sha103'
Check 'a failed non-Arm64 delivery job (attest) is not publishable' (Pick @((Run 'sha103' 103 -conclusion failure -more @{ 'attest' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'a timed-out run is not eligible' (Pick @((Run 'sha103' 103 -conclusion timed_out))) $null
Check 'an unfinished run is not eligible' (Pick @((Run 'sha103' 103 -conclusion ''))) $null
Check 'a cancelled run with an app job cancelled does not count' (Pick @((Run 'sha103' 103 -x64 cancelled -conclusion cancelled))) $null
Check 'expired artifacts skip to the next commit' (Pick @((Run 'sha103' 103 -fresh $false), (Run 'sha102' 102))) 'sha102'
Check 'a commit already released publishes nothing' (Pick @((Run 'sha100' 100), (Run 'sha099' 99))) $null
Check 'nothing newer than the latest release publishes nothing' (Pick @((Run 'sha100' 100))) $null
Check 'a commit that does not follow the latest release is skipped' (Pick @((Run 'stray' 104), (Run 'sha102' 102))) 'sha102'
Check 'a run number not above every published version is skipped' (Pick @((Run 'sha099b' 99), (Run 'sha102' 102))) 'sha102'
Check 'no release yet takes the newest passing run' (Pick @((Run 'sha001' 1)) $null) 'sha001'
Check 'nothing selected says why' ((Select-ReleaseCommit -Runs @((Run 'sha103' 103 -gate failure)) -Latest $out -IsDescendant $follows -Explain) -match 'sha103.*quality-gate') $true

function Signed([string]$v) { try { Assert-TrainMayPublish $v; "published" } catch { "refused" } }
function Stops { try { Select-ReleaseCommit -Runs @((Run "sha103" 103)) -Latest $out -IsDescendant { throw "gh failed" } | Out-Null; "went on" } catch { "stopped" } }
Check "a throwing descendant check stops the selection" (Stops) "stopped"
Check "signing on refuses" (Signed "true") "refused"
Check "a run missing a job key is not a pass" (Pick @(([pscustomobject]@{ Sha = "sha103"; Number = 103; Conclusion = "success"; Jobs = @{ "quality-gate" = "success"; "app-arm64" = "success" }; ArtifactsUnexpired = $true }), (Run "sha102" 102))) "sha102"
Check "a truthy non-boolean descendant answer is not a yes" ((Select-ReleaseCommit -Runs @((Run "sha103" 103)) -Latest $out -IsDescendant { "ahead" })) $null
Check "a null descendant answer is not a yes" ((Select-ReleaseCommit -Runs @((Run "sha103" 103)) -Latest $out -IsDescendant { $null })) $null
Check "signing unset may publish" (Signed "") "published"
foreach ($v in "True", "TRUE", " true", "true ", "yes", "1") { Check "signing $v refuses" (Signed $v) "refused" }
foreach ($v in "false", "False", " FALSE ", $null) { Check "signing $v may publish" (Signed $v) "published" }

if ($failures) { Write-Host "$failures failed"; exit 1 }
Write-Host 'all passed'
