<#
.SYNOPSIS
DRAFT, HELD: the last job of .github/workflows/lab-release-verify.yml. Holds both roles' lab
results, and the lab nightly's own outcome, to the one release asked for (#357, #736).

.DESCRIPTION
The lab nightly (#854) verified the cut and judged it: quality/lab/nightly-verdict.ps1 bound each
result to the cut's commit, CI run and installer digests, and to one unchanged relay. This script
doesn't judge that again. It checks what the nightly's single outcome can't show on its own: that
the outcome is about the release this verification was asked for, and that the results bear it out.
So a pass for another cut, a pass with a role missing or not passing, or two roles from different
lab runs, commits or CI runs is invalid, never a pass.

The outcome, with the reasons:
- invalid: the nightly gave no outcome (it was refused, cancelled or broke before its verdict) or
  one that isn't pass, fail or invalid; it judged the cut invalid; a role's result is missing or
  isn't JSON; a result isn't schema version 1, v1, simulated agents, for this release, or the role
  it's filed under; the roles' run id, commit (build.sha) or CI run (source.run.id) differ; or the
  nightly's outcome and the results disagree.
- fail: the nightly failed the release, and a role's result says so.
- pass: the nightly passed it, and both roles' results are this release's and passed.

A pass is v1 evidence about one release only. It promotes nothing and moves no channel
(quality/README.md §7), and it never counts toward Beta 1, which needs v2 and three consecutive
qualifying releases (#736). An audio report beside the results (#849 §12) is never read.

.EXAMPLE
pwsh quality/lab/release-verify.ps1 -Release v0.1.600 -NightlyOutcome pass -ResultsDir results
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Release,
    # The nightly's workflow_call output: empty when it was refused or never reached its verdict.
    [Parameter(Mandatory)][AllowEmptyString()][string] $NightlyOutcome,
    # Holds lab-result-win-x64/result.json and lab-result-win-arm64/result.json.
    [Parameter(Mandatory)][string] $ResultsDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Without the release asked for there is nothing to hold the results to, so no verdict at all.
if ($Release -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+\z') { throw "Release must be a release tag such as v0.1.600, not '$Release'" }

$Architectures = [ordered]@{ 'win-x64' = 'x64'; 'win-arm64' = 'arm64' }
# What one lab run of one cut shares across both roles.
$Shared = 'runId', 'build.sha', 'source.run.id'

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
$seen = [ordered]@{}
foreach ($field in $Shared) { $seen[$field] = [ordered]@{} }

if ($NightlyOutcome -ceq '') { $invalid.Add('the nightly gave no outcome: it was refused, cancelled or broke before its verdict') }
elseif ($NightlyOutcome -cnotin 'pass', 'fail', 'invalid') { $invalid.Add("the nightly's outcome is '$NightlyOutcome', not pass, fail or invalid") }
elseif ($NightlyOutcome -ceq 'invalid') { $invalid.Add("the nightly judged $Release invalid") }

foreach ($role in $Architectures.Keys) {
    $path = Join-Path $ResultsDir "lab-result-$role" 'result.json'
    $roles[$role] = 'missing'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $invalid.Add("$role has no result.json"); continue }
    try { $result = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -Depth 32 }
    catch { $invalid.Add("$role's result.json isn't JSON"); continue }

    $expected = [ordered]@{
        'schemaVersion'   = '1'
        'cutId'           = $Release
        'role'            = $role
        'architecture'    = $Architectures[$role]
        'coverageVersion' = 'v1'
        'agentMode'       = 'simulated'
    }
    foreach ($field in $expected.Keys) {
        $value = Get-Field $result ($field -split '\.')
        if ($null -eq $value -or "$value" -cne $expected[$field]) { $invalid.Add("$role's $field is '$value', not '$($expected[$field])'") }
    }
    foreach ($field in $Shared) {
        $value = Get-Field $result ($field -split '\.')
        if ($null -eq $value -or "$value" -ceq '') { $invalid.Add("$role's result has no $field") }
        else { $seen[$field][$role] = "$value" }
    }
    $outcome = Get-Field $result @('outcome')
    if ($outcome -cin 'pass', 'fail', 'invalid') { $roles[$role] = $outcome }
    else { $roles[$role] = 'unknown'; $invalid.Add("$role's outcome is '$outcome', not pass, fail or invalid") }
}

foreach ($field in $Shared) {
    $values = @($seen[$field].Values)
    if ($values.Count -eq 2 -and $values[0] -cne $values[1]) {
        $invalid.Add("the roles' $field differ ($($values -join ', ')): not one lab run of one cut")
    }
}

# The nightly's outcome must be what its results say, or neither can be trusted.
switch -CaseSensitive ($NightlyOutcome) {
    'pass' {
        foreach ($role in $Architectures.Keys) {
            if ($roles[$role] -cne 'pass') { $invalid.Add("the nightly passed $Release, but $role's result is $($roles[$role])") }
        }
    }
    'fail' {
        $failing = @($Architectures.Keys | Where-Object { $roles[$_] -ceq 'fail' })
        if ($failing.Count) { $failed.Add("the nightly failed $Release ($($failing -join ', '))") }
        else { $invalid.Add("the nightly failed $Release, but neither role's result says fail") }
    }
}

$outcome = if ($invalid.Count) { 'invalid' } elseif ($failed.Count) { 'fail' } else { 'pass' }
[pscustomobject]@{
    schemaVersion   = 1
    release         = $Release
    outcome         = $outcome
    coverageVersion = 'v1'
    roles           = $roles
    reasons         = @($invalid) + @($failed)
}
