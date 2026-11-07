<#
.SYNOPSIS
DRAFT, HELD: the cut verdict of .github/workflows/lab-nightly.yml, from both roles' result.json
(#357, #736).

.DESCRIPTION
Both roles of a nightly run test one cut, against one relay digest (#849's quality/lab/README.md
§6-§8). This reads each role's result.json (schema version 1) from
<ResultsDir>/lab-result-<role>/result.json and returns the cut's outcome:

- invalid when it can't count either way: a role's result is missing (the run or its VM was
  lost) or doesn't describe this run, cut, build, relay and v1 coverage; it names another CI
  run than the one cut verified (source.run.id), or its build.digests lack the role's installer
  digest cut verified; a role's run is
  invalid; the relay's digest changed during a run or differs between the roles; or a run
  claims pass without each v1 step (launch, reply, answer, stop, update) exactly once and passing,
  or without an update between two versions. Invalid is never
  admission or promotion evidence; a cut gets at most one diagnosed retry, as a new run.
- fail when no role is invalid and either failed.
- pass only when both roles passed.

It reads nothing else. An audio-acceptance.json beside a result (§12) is separate,
non-gating evidence and never changes this verdict, and nothing here waives or retries.

.EXAMPLE
pwsh quality/lab/nightly-verdict.ps1 -ResultsDir results -RunId n00000jaqd8b -CutId v0.1.600 -Sha <commit> -Relay staging -CiRun <id> -X64Digest sha256:<hex> -Arm64Digest sha256:<hex>
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultsDir,
    [Parameter(Mandatory)][string] $RunId,
    [Parameter(Mandatory)][string] $CutId,
    [Parameter(Mandatory)][string] $Sha,
    [Parameter(Mandatory)][string] $Relay,
    # What the cut job verified: the CI run that built the cut, and each role's installer digest.
    [Parameter(Mandatory)][string] $CiRun,
    [Parameter(Mandatory)][string] $X64Digest,
    [Parameter(Mandatory)][string] $Arm64Digest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Without what cut verified there is nothing to hold the results to, so no verdict at all.
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
$digests = [ordered]@{}

foreach ($role in $Architectures.Keys) {
    $path = Join-Path $ResultsDir "lab-result-$role" 'result.json'
    $roles[$role] = 'missing'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $invalid.Add("$role has no result.json: its run or VM was lost"); continue }
    try { $result = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -Depth 32 }
    catch { $invalid.Add("$role's result.json isn't JSON"); continue }

    # This nightly is v1 only: a result claiming v2 or real agents isn't this run's.
    $expected = [ordered]@{
        'schemaVersion'   = '1'
        'runId'           = $RunId
        'cutId'           = $CutId
        'role'            = $role
        'architecture'    = $Architectures[$role]
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
    # The role's own installer, as cut verified it, must be among what the run installed.
    $installed = @(Get-Field $result @('build', 'digests') | Where-Object { $_ -is [string] })
    if ($installed -cnotcontains $CutDigests[$role]) {
        $invalid.Add("$role's build.digests don't include the installer the cut verified, $($CutDigests[$role])"); $mismatch = $true
    }
    $outcome = Get-Field $result @('outcome')
    $roles[$role] = if ($outcome -cin 'pass', 'fail', 'invalid') { $outcome } else { 'unknown' }
    if ($mismatch) { continue }

    $start = Get-Field $result @('relay', 'digestAtStart')
    $end = Get-Field $result @('relay', 'digestAtEnd')
    if ([string]::IsNullOrEmpty($start) -or "$start" -cne "$end") { $invalid.Add("$role's relay digest changed during the run, or wasn't recorded ($start, $end)") }
    else { $digests[$role] = "$start" }

    $steps = @(Get-Field $result @('steps') | Where-Object { $null -ne $_ })
    switch -CaseSensitive ($roles[$role]) {
        'invalid' { $invalid.Add("$role's run is invalid") }
        'fail' {
            $names = @($steps | Where-Object { (Get-Field $_ @('outcome')) -cne 'pass' } | ForEach-Object { Get-Field $_ @('name') })
            $failed.Add("$role failed ($($names -join ', '))")
        }
        'pass' {
            $notPassed = @($steps | Where-Object { (Get-Field $_ @('outcome')) -cne 'pass' } | ForEach-Object { Get-Field $_ @('name') })
            $wrongCount = @(foreach ($name in 'launch', 'reply', 'answer', 'stop', 'update') {
                $occurrences = @($steps | Where-Object {
                    $stepName = Get-Field $_ @('name')
                    $stepName -is [string] -and $stepName -ceq $name
                })
                if ($occurrences.Count -ne 1) { $name }
            })
            $from = Get-Field $result @('updatePair', 'from', 'version')
            $to = Get-Field $result @('updatePair', 'to', 'version')
            if ($wrongCount.Count) { $invalid.Add("$role claims pass without each mandatory v1 step exactly once ($($wrongCount -join ', '))") }
            elseif ($notPassed.Count) { $invalid.Add("$role claims pass, but not every step passed ($($notPassed -join ', '))") }
            elseif ([string]::IsNullOrEmpty($from) -or [string]::IsNullOrEmpty($to) -or "$from" -ceq "$to") {
                $invalid.Add("$role claims pass without an update between two versions ($from to $to)")
            }
        }
        default { $invalid.Add("$role's outcome is '$outcome', not pass, fail or invalid") }
    }
}

if ($digests.Count -eq 2 -and $digests['win-x64'] -cne $digests['win-arm64']) {
    $invalid.Add("the roles ran against different relay digests ($($digests['win-x64']), $($digests['win-arm64'])), not one")
}

$cutOutcome = if ($invalid.Count) { 'invalid' } elseif ($failed.Count) { 'fail' } else { 'pass' }
[pscustomobject]@{
    schemaVersion = 1
    runId         = $RunId
    cutId         = $CutId
    outcome       = $cutOutcome
    roles         = $roles
    reasons       = @($invalid) + @($failed)
}
