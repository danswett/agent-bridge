<#
.SYNOPSIS
DRAFT, SOURCE ONLY: the install smoke's verdict, from both roles' result.json and the lab
nightly's own verdict on the same run (#357 phases 2-4, #736).

.DESCRIPTION
The install smoke asks one narrow question of a nightly run (install-smoke-gate.ps1): on both
clean roles, did the cut's own verified installer install, and did the product it installed then
launch? This reads each role's result.json (#849's quality/lab/README.md §7, schema version 1)
from <ResultsDir>/lab-result-<role>/result.json, and the outcome nightly-verdict.ps1 (#854)
gave the same run, and returns:

- invalid when it can't count either way:
  - the nightly's outcome is missing, unknown or invalid: the run's relay, coverage and update
    checks are the nightly's, not repeated here, and a run it can't count can't count here;
  - a role's result is missing, isn't JSON, or doesn't describe this run, cut, commit, CI run,
    relay, role and native architecture, v1 and simulated agents, on its own lab device
    (lab-<role>-<runId>); its CI run isn't from refs/heads/main or a release tag; or its
    build.digests lack the role's installer the cut verified;
  - the role's run is invalid, or its outcome is unknown;
  - it has no single install step followed by a single launch step, either was skipped or has an
    unknown outcome, or the product launched after its installer failed;
  - the nightly passed the run, but a role's install smoke didn't pass; or it failed the run,
    but neither role's result says fail.
- fail when nothing is invalid and a role's installer failed, or the product it installed didn't
  launch. An installer that exited 0 is a passed install step, never a passed smoke.
- pass only when both roles installed and then launched.

A pass is v1 lab evidence about that one cut, scoped as its `scope` says: it is not the
nightly's cut verdict, a promotion, a channel move, v2 or Beta1 evidence, and it says nothing of
the rest of the scenario. Nothing here installs, launches, waives or retries anything, and it
reads nothing else: an audio-acceptance.json beside a result (§12) is separate, non-gating
evidence and never changes this verdict.

The device field and the install step are additions to result.json schema version 1 that
this scenario asks of #849's contract and #736's runner (install-smoke-gate.ps1's
contract-approval). Until a result carries them, every role is invalid.

.EXAMPLE
pwsh quality/lab/install-smoke-verdict.ps1 -ResultsDir results -RunId n00000jaqd8b -CutId v0.1.600 -Sha <commit> -Relay staging -CiRun <id> -X64Digest sha256:<hex> -Arm64Digest sha256:<hex> -NightlyOutcome pass
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultsDir,
    [Parameter(Mandatory)][string] $RunId,
    [Parameter(Mandatory)][string] $CutId,
    [Parameter(Mandatory)][string] $Sha,
    [Parameter(Mandatory)][string] $Relay,
    # What the nightly's cut verified: the CI run that built the cut, and each role's installer.
    [Parameter(Mandatory)][string] $CiRun,
    [Parameter(Mandatory)][string] $X64Digest,
    [Parameter(Mandatory)][string] $Arm64Digest,
    # nightly-verdict.ps1's outcome for this run: lab-nightly.yml's outcome output.
    [Parameter(Mandatory)][AllowEmptyString()][string] $NightlyOutcome
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Without what the run and its cut were there is nothing to hold the results to, so no verdict.
if ($RunId -cnotmatch '^n[0-9a-z]{11}\z') { throw "RunId must be the nightly's run id, not '$RunId'" }
if ($CutId -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+\z') { throw "CutId must be a release tag, not '$CutId'" }
if ($Sha -cnotmatch '^[0-9a-f]{40}\z') { throw "Sha must be the cut's 40-hex commit, not '$Sha'" }
if ($Relay -cnotin 'staging', 'ppe') { throw "Relay must be staging or ppe, not '$Relay'" }
if ($CiRun -cnotmatch '^[1-9][0-9]{0,15}\z') { throw "CiRun must be the verified CI run's id, not '$CiRun'" }
foreach ($digest in $X64Digest, $Arm64Digest) {
    if ($digest -cnotmatch '^sha256:[0-9a-f]{64}\z') { throw "a role's digest must be sha256:<64 lowercase hex>, not '$digest'" }
}
$CutDigests = @{ 'win-x64' = $X64Digest; 'win-arm64' = $Arm64Digest }
$Architectures = [ordered]@{ 'win-x64' = 'x64'; 'win-arm64' = 'arm64' }

# A field of parsed JSON, or $null when any step of the path is missing: under strict mode a
# missing property would throw, and a missing field is a reason, not a crash.
function Get-Field($object, [string[]]$path) {
    foreach ($name in $path) {
        if ($object -isnot [System.Management.Automation.PSCustomObject] -or -not $object.PSObject.Properties[$name]) { return $null }
        $object = $object.$name
    }
    $object
}

$invalid = [Collections.Generic.List[string]]::new()
$failed = [Collections.Generic.List[string]]::new()
$roles = [ordered]@{}

if ($NightlyOutcome -ceq '') { $invalid.Add("the nightly gave no outcome for ${CutId}: without its verdict on the run, nothing installed in it counts") }
elseif ($NightlyOutcome -ceq 'invalid') { $invalid.Add("the nightly judged $CutId invalid: nothing its run installed counts") }
elseif ($NightlyOutcome -cnotin 'pass', 'fail') { $invalid.Add("the nightly's outcome is '$NightlyOutcome', not pass, fail or invalid") }
$roleFailed = $false

foreach ($role in $Architectures.Keys) {
    $roles[$role] = 'invalid'
    $path = Join-Path $ResultsDir "lab-result-$role" 'result.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $roles[$role] = 'missing'; $invalid.Add("$role has no result.json: its run or VM was lost"); continue }
    try { $result = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -Depth 32 }
    catch { $invalid.Add("$role's result.json isn't JSON"); continue }

    $expected = [ordered]@{
        'schemaVersion'   = '1'
        'runId'           = $RunId
        'cutId'           = $CutId
        'role'            = $role
        'architecture'    = $Architectures[$role]
        'device'          = "lab-$role-$RunId"
        'build.sha'       = $Sha
        'source.run.id'   = $CiRun
        'coverageVersion' = 'v1'
        'agentMode'       = 'simulated'
        'relay.target'    = $Relay
    }
    $mismatch = $false
    foreach ($field in $expected.Keys) {
        $value = Get-Field $result ($field -split '\.')
        if ($null -eq $value -or "$value" -cne $expected[$field]) { $invalid.Add("$role's $field is '$value', not '$($expected[$field])'"); $mismatch = $true }
    }
    $ref = Get-Field $result @('source', 'run', 'ref')
    if ("$ref" -cnotmatch '^refs/(heads/main|tags/v[0-9]+\.[0-9]+\.[0-9]+)\z') {
        $invalid.Add("$role's source.run.ref is '$ref', not refs/heads/main or a release tag"); $mismatch = $true
    }
    $installed = @(Get-Field $result @('build', 'digests') | Where-Object { $_ -is [string] })
    if ($installed -cnotcontains $CutDigests[$role]) {
        $invalid.Add("$role's build.digests don't include the installer the cut verified, $($CutDigests[$role])"); $mismatch = $true
    }
    if ($mismatch) { continue }

    $outcome = Get-Field $result @('outcome')
    if ($outcome -ceq 'invalid') { $invalid.Add("$role's run is invalid"); continue }
    if ($outcome -cnotin 'pass', 'fail') { $invalid.Add("$role's outcome is '$outcome', not pass, fail or invalid"); continue }
    if ($outcome -ceq 'fail') { $roleFailed = $true }

    # The install step, then the launch of what it installed: one of each, in that order.
    $steps = @(Get-Field $result @('steps') | Where-Object { $null -ne $_ })
    $at = @{ install = @(); launch = @() }
    for ($i = 0; $i -lt $steps.Count; $i++) {
        $name = Get-Field $steps[$i] @('name')
        if ($name -is [string] -and $name -cin 'install', 'launch') { $at[$name] += $i }
    }
    $broken = $false
    foreach ($name in 'install', 'launch') {
        if ($at[$name].Count -ne 1) { $invalid.Add("$role has $($at[$name].Count) $name steps, not one"); $broken = $true }
    }
    if ($broken) { continue }
    $install = Get-Field $steps[$at.install[0]] @('outcome')
    $launch = Get-Field $steps[$at.launch[0]] @('outcome')
    if ($at.launch[0] -lt $at.install[0]) { $invalid.Add("$role launched before it installed"); continue }
    foreach ($step in @{ Name = 'install'; Outcome = $install }, @{ Name = 'launch'; Outcome = $launch }) {
        if ($step.Outcome -ceq 'skipped') { $invalid.Add("$role's $($step.Name) step was skipped: not exercised"); $broken = $true }
        elseif ($step.Outcome -cnotin 'pass', 'fail') { $invalid.Add("$role's $($step.Name) step's outcome is '$($step.Outcome)', not pass, fail or skipped"); $broken = $true }
    }
    if ($broken) { continue }

    if ($install -ceq 'fail') {
        if ($launch -ceq 'pass') { $invalid.Add("$role launched after its installer failed: what ran wasn't what this run installed"); continue }
        $smoke = 'fail'; $failed.Add("$role's installer failed")
    }
    elseif ($launch -ceq 'fail') {
        $smoke = 'fail'; $failed.Add("$role installed, but the installed product didn't launch: an installer's exit proves nothing by itself")
    }
    else { $smoke = 'pass' }

    if ($outcome -ceq 'pass' -and $smoke -cne 'pass') { $invalid.Add("$role claims pass, but its install smoke is $smoke"); continue }
    if ($NightlyOutcome -ceq 'pass' -and $smoke -cne 'pass') { $invalid.Add("the nightly passed $CutId, but $role's install smoke is $smoke"); continue }
    $roles[$role] = $smoke
}
# The nightly fails a cut only when a role failed: a fail neither result bears out isn't this run's.
if ($NightlyOutcome -ceq 'fail' -and -not $roleFailed -and -not $invalid.Count) {
    $invalid.Add("the nightly failed $CutId, but neither role's result says fail")
}

$verdict = if ($invalid.Count) { 'invalid' } elseif ($failed.Count) { 'fail' } else { 'pass' }
[pscustomobject]@{
    schemaVersion   = 1
    scenario        = 'install-smoke'
    scope           = 'v1, simulated agents: the cut''s verified installer installed and the installed product launched on clean win-x64 and native win-arm64 lab machines. Not the nightly''s cut verdict, a promotion, v2 or Beta1 evidence.'
    runId           = $RunId
    cutId           = $CutId
    coverageVersion = 'v1'
    outcome         = $verdict
    roles           = $roles
    reasons         = @($invalid) + @($failed)
}
