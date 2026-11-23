# Checks build/select-release-commit.ps1 against fixtures: nothing is read from GitHub.
#
#   pwsh build/test-select-release-commit.ps1
#
# What it guards: the release train publishes the newest main commit whose FAST lane passed (ci.yml's
# quality-gate and the x64 app build) AND whose FULL lane passed (full.yml: the x64 self-test, pack and
# installer; Arm64 may be red), that is a descendant of the latest release's commit, whose FULL run's
# packages for both architectures are unexpired and whose run number is above every published version;
# and publishes nothing when that commit is already out, or has no FULL run yet.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'select-release-commit.ps1')

$failures = 0
function Check([string]$name, $actual, $expected) {
    if ($actual -ceq $expected) { Write-Host "PASS $name" } else { $script:failures++; Write-Host "FAIL ${name}: expected '$expected', got '$actual'" }
}
function Run([string]$sha, [int]$number, [string]$gate = 'success', [string]$x64 = 'success', [string]$arm = 'success', [string]$conclusion = 'success', [bool]$fresh = $true, [hashtable]$more = @{}, [string]$relied = '', [bool]$noFull = $false, [string]$fullConclusion = 'success', [bool]$fullFresh = $true, [hashtable]$fullMore = @{}, [int]$fullNumber = 1) {
    $fullJobs = @{ 'full-gate' = 'success'; 'app-selftest (win-x64)' = 'success'; 'app-pack (win-x64)' = 'success'; 'app-installer (win-x64)' = 'success'; 'app-pack (win-arm64)' = 'success'; 'app-installer (win-arm64)' = 'success'; 'app-selftest (win-arm64)' = 'success'; 'pwa-ui (webkit 1/3)' = 'success'; 'spikes' = 'success' }
    foreach ($key in $fullMore.Keys) { if ($null -eq $fullMore[$key]) { $fullJobs.Remove($key) } else { $fullJobs[$key] = $fullMore[$key] } }
    $full = if ($noFull) { $null } else { [pscustomobject]@{ Number = $fullNumber; Conclusion = $fullConclusion; AllJobs = $fullJobs; ArtifactsUnexpired = ($fullFresh -and $fresh) } }
    $all = @{ 'quality-gate' = $gate; 'app (win-x64)' = $x64; 'app (win-arm64)' = $arm }
    foreach ($key in $more.Keys) { $all[$key] = $more[$key] }
    [pscustomobject]@{ Sha = $sha; Number = $number; Conclusion = $conclusion
        Jobs = @{ 'quality-gate' = $gate; 'app-x64' = $x64; 'app-arm64' = $arm }; ArtifactsUnexpired = $fresh; AllJobs = $all
        ReliedParent = $(if ($relied) { $relied } else { $null }); Full = $full }
}
$out = [pscustomobject]@{ Tag = 'v0.1.100'; Commit = 'sha100' }
$follows = { param($sha, $base) $sha -cne 'stray' }
function Pick($runs, $latest = $out) { $r = Select-ReleaseCommit -Runs $runs -Latest $latest -IsDescendant $follows; if ($r) { $r.Sha } else { $null } }

Check 'newest passing commit wins' (Pick @((Run 'sha103' 103), (Run 'sha102' 102))) 'sha103'
Check 'a red newest run is skipped for the next passing one' (Pick @((Run 'sha103' 103 -gate failure), (Run 'sha102' 102))) 'sha102'
Check 'an arm64 app job failure does not stop the pick when quality-gate and x64 passed' (Pick @((Run 'sha103' 103 -arm failure), (Run 'sha102' 102))) 'sha103'
Check 'a run with no arm64 job result at all is still picked' (Pick @(([pscustomobject]@{ Sha = 'sha103'; Number = 103; Conclusion = 'success'; Jobs = @{ 'quality-gate' = 'success'; 'app-x64' = 'success' }; ArtifactsUnexpired = $true; AllJobs = @{}; Full = [pscustomobject]@{ Conclusion = 'success'; AllJobs = @{ 'full-gate' = 'success'; 'app-selftest (win-x64)' = 'success'; 'app-pack (win-x64)' = 'success'; 'app-installer (win-x64)' = 'success'; 'app-pack (win-arm64)' = 'success'; 'app-installer (win-arm64)' = 'success' }; ArtifactsUnexpired = $true } }))) 'sha103'
# The FULL lane (full.yml, never a merge gate) gates the release: the newest commit whose FAST lane passed AND whose FULL
# run passed, its required jobs (the x64 self-test, pack and installer) green. Arm64 stays waived.
Check 'a commit with no FULL run yet is not picked, the next one with a FULL run is' (Pick @((Run 'sha103' 103 -noFull $true), (Run 'sha102' 102))) 'sha102'
Check 'a FULL run whose x64 self-test failed is not picked' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'app-selftest (win-x64)' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'a FULL run whose x64 installer failed is not picked' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'app-installer (win-x64)' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'a required FULL job that did not run at all is not a pass' (Pick @((Run 'sha103' 103 -fullMore @{ 'app-pack (win-x64)' = $null }), (Run 'sha102' 102))) 'sha102'
Check 'a required FULL job that was skipped is not a pass' (Pick @((Run 'sha103' 103 -fullMore @{ 'app-selftest (win-x64)' = 'skipped' }), (Run 'sha102' 102))) 'sha102'
Check 'a FULL run red only for Arm64 is publishable' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'app-selftest (win-arm64)' = 'failure'; 'product (windows-11-arm)' = 'failure' }), (Run 'sha102' 102))) 'sha103'
Check 'a FULL run red in WebKit is not picked (FULL gates the release, not only its Windows jobs)' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'pwa-ui (webkit 1/3)' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'a FULL run red in a spikes job is not picked' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'spikes' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'a FULL run still running is not picked' (Pick @((Run 'sha103' 103 -fullConclusion ''), (Run 'sha102' 102))) 'sha102'
Check 'a FULL run that timed out is not picked' (Pick @((Run 'sha103' 103 -fullConclusion timed_out), (Run 'sha102' 102))) 'sha102'
Check 'FULL packages that expired skip to the next commit, whatever FAST built' (Pick @((Run 'sha103' 103 -fullFresh $false), (Run 'sha102' 102))) 'sha102'
Check 'a green FAST lane is no pass without FULL' (Pick @((Run 'sha103' 103 -noFull $true))) $null
Check 'a FULL run with a FAST red lane is still no pass' (Pick @((Run 'sha103' 103 -gate failure), (Run 'sha102' 102))) 'sha102'
Check 'nothing picked says there is no FULL run' ((Select-ReleaseCommit -Runs @((Run 'sha103' 103 -noFull $true)) -Latest $out -IsDescendant $follows -Explain) -match 'sha103.*no FULL run') $true
Check 'nothing picked names the FULL job that failed' ((Select-ReleaseCommit -Runs @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'app-selftest (win-x64)' = 'failure' })) -Latest $out -IsDescendant $follows -Explain) -match 'sha103.*app-selftest \(win-x64\)') $true
Check 'a run with no Full property at all (an older record) is not a pass' (Pick @(([pscustomobject]@{ Sha = 'sha103'; Number = 103; Conclusion = 'success'; Jobs = @{ 'quality-gate' = 'success'; 'app-x64' = 'success'; 'app-arm64' = 'success' }; ArtifactsUnexpired = $true; AllJobs = @{ 'quality-gate' = 'success' } }), (Run 'sha102' 102))) 'sha102'
# Quality's conditions (4217): the Arm64 package is required, not optional; FULL's own run number is never the version; the FULL run is found by
# head_sha, never by branch; and a FULL run with a skipped lane (WebKit) is not selectable, which the aggregate full-gate shows.
Check 'an Arm64 pack that failed is not publishable: only the Arm64 self-test is waived' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'app-pack (win-arm64)' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'an Arm64 pack that did not run is not publishable' (Pick @((Run 'sha103' 103 -fullMore @{ 'app-pack (win-arm64)' = $null }), (Run 'sha102' 102))) 'sha102'
Check 'an Arm64 installer that did not run at all is still publishable (waived)' (Pick @((Run 'sha103' 103 -fullMore @{ 'app-installer (win-arm64)' = $null }), (Run 'sha102' 102))) 'sha103'
Check 'the x64 installer is not waived: failed, it is not publishable' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'app-installer (win-x64)' = 'failure' }), (Run 'sha102' 102))) 'sha102'
Check 'an Arm64 pack that was skipped is not publishable' (Pick @((Run 'sha103' 103 -fullMore @{ 'app-pack (win-arm64)' = 'skipped' }), (Run 'sha102' 102))) 'sha102'
Check 'an Arm64 installer that failed is still publishable: it installs and starts the app, the Arm64 self-test the owner waived (#4159)' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'app-installer (win-arm64)' = 'failure' }), (Run 'sha102' 102))) 'sha103'
Check 'a FULL run with no aggregate full-gate result is not selectable' (Pick @((Run 'sha103' 103 -fullMore @{ 'full-gate' = $null }), (Run 'sha102' 102))) 'sha102'
Check 'a FULL run whose full-gate failed (a WebKit job skipped) is not selectable' (Pick @((Run 'sha103' 103 -fullConclusion failure -fullMore @{ 'full-gate' = 'failure'; 'pwa-ui (webkit 1/3)' = 'skipped' }), (Run 'sha102' 102))) 'sha102'
Check 'a FULL run whose full-gate was skipped is not selectable' (Pick @((Run 'sha103' 103 -fullMore @{ 'full-gate' = 'skipped' }), (Run 'sha102' 102))) 'sha102'
Check 'the version floor is the FAST run number, not the FULL run''s own' (Pick @((Run 'sha099b' 99 -fullNumber 5000), (Run 'sha102' 102))) 'sha102'
Check 'the FULL run''s own small number does not lower it' (Pick @((Run 'sha103' 103 -fullNumber 1))) 'sha103'
function Names([string]$v, [string[]]$except = @()) { @("agent-bridge-windows-0.1.$v", "agent-bridge-windows-arm64-0.1.$v", "sbom-win-x64-0.1.$v", "sbom-win-arm64-0.1.$v") | Where-Object { $_ -cnotin $except } }
Check 'FULL packages complete: both packages and both SBOMs under the FAST run number' (Get-FullPackages (Names 103) 103).Complete $true
Check 'FULL packages: the version is 0.1.<the FAST run number>' (Get-FullPackages (Names 103) 103).Version '0.1.103'
Check 'FULL packages: no Arm64 SBOM is incomplete' (Get-FullPackages (Names 103 @('sbom-win-arm64-0.1.103')) 103).Complete $false
Check 'FULL packages: no Arm64 package is incomplete' (Get-FullPackages (Names 103 @('agent-bridge-windows-arm64-0.1.103')) 103).Complete $false
Check 'FULL packages: no x64 SBOM is incomplete' (Get-FullPackages (Names 103 @('sbom-win-x64-0.1.103')) 103).Complete $false
Check 'FULL packages: packages under FULL''s own run number are not the FAST run''s' (Get-FullPackages (Names 7) 103).Complete $false
Check 'FULL packages: none at all is incomplete and names what is missing' ((Get-FullPackages @() 103).Missing.Count -gt 0) $true
Check 'the FULL run is looked up by head_sha' ((Get-FullRunsPath 'o/r' ('a' * 40)) -match 'workflows/full\.yml/runs\?head_sha=a{40}') $true
Check 'the FULL run is never looked up by branch' ((Get-FullRunsPath 'o/r' ('a' * 40)) -match 'branch=') $false
Check 'a run with an Arm64 SBOM missing (packages incomplete) is not publishable' (Pick @((Run 'sha103' 103 -fullFresh $false), (Run 'sha102' 102))) 'sha102'
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

# A run that relied on an in-flight parent to test only its own change (item A, #2454) is publishable only if that parent, and what it
# relied on, passed the same checks: the commit it names holds the parent's change, which that run never tested.
Check 'a run that relied on a passing parent is picked' (Pick @((Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102))) 'sha103'
Check 'a run that relied on a parent that failed is skipped for an older run that relied on nothing' (Pick @((Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102 -gate failure), (Run 'sha101' 101))) 'sha101'
Check 'a failure further back the chain skips the run' (Pick @((Run 'sha104' 104 -relied 'sha103'), (Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102 -gate failure), (Run 'sha101' 101))) 'sha101'
Check 'a run that relied on a cancelled parent is skipped: the parent never tested its change (the parent itself still counts as a superseded run)' (Pick @((Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102 -conclusion cancelled), (Run 'sha101' 101))) 'sha102'
Check 'a run that relied on a parent still running is skipped' (Pick @((Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102 -conclusion ''), (Run 'sha101' 101))) 'sha101'
Check 'a run whose relied-on parent is not among the runs is skipped' (Pick @((Run 'sha103' 103 -relied 'shaGone'), (Run 'sha102' 102))) 'sha102'
Check 'a parent that passes, beside a green chain, is picked' (Pick @((Run 'sha104' 104 -relied 'sha103'), (Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102))) 'sha104'
Check 'a later run that relies on nothing covers the failed one and is picked' (Pick @((Run 'sha104' 104), (Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102 -gate failure))) 'sha104'
Check 'a reliance loop is not a pass' (Pick @((Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102 -relied 'sha103'))) $null
Check 'a skipped relied-on run says why' ((Select-ReleaseCommit -Runs @((Run 'sha103' 103 -relied 'sha102'), (Run 'sha102' 102 -gate failure)) -Latest $out -IsDescendant $follows -Explain) -match 'sha103.*relied on sha102') $true
Check 'the plan-base record of a run that relied on nothing reads as none' ((ConvertFrom-PlanBase ('base=' + ('a' * 40) + ' relied-parent=none')) -eq $null) $true
Check 'the plan-base record names the relied-on parent' (ConvertFrom-PlanBase ('base=' + ('a' * 40) + ' relied-parent=' + ('b' * 40))) ('b' * 40)
function Reads([string]$m) { try { ConvertFrom-PlanBase $m | Out-Null; 'read' } catch { 'refused' } }
Check 'a plan-base record not in the shape is an error, not a guess' (Reads 'base=x relied-parent=y') 'refused'
Check 'no plan-base record at all relied on nothing' ((ConvertFrom-PlanBase $null) -eq $null) $true

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
