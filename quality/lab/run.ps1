<#
.SYNOPSIS
Nonexecuting runner-interface draft. Part of #736; not a scenario runner.

.DESCRIPTION
Validates a nightly-v1 request and returns an explicitly unverified plan, using
provision.ps1 for the resource plan. Makes no resource, Azure, network or sign-in call,
creates no output directory, and never writes result.json or reports an outcome.

-Apply always refuses. Provisioning prerequisites, build provenance, scenario execution,
AgentSim, transport/evidence and per-run owner device flow need separate reviewed delivery.
No parameter, environment variable, -Confirm or -WhatIf clears that refusal.

.PARAMETER Build
A release tag pinned to the installer's digest: v0.1.N@sha256:<64 lowercase hex>.
This draft checks syntax only, not provenance. URLs, paths and mutable tags are refused.

.PARAMETER Scenario
nightly-v1 only. Its launch, reply, answer, stop and update steps are not implemented here.

.PARAMETER Relay
staging or ppe only, explicitly named. No URL override; PPE remains unavailable.

.PARAMETER Out
The intended evidence directory. Planning neither creates it nor reads or changes its files.

.EXAMPLE
./quality/lab/run.ps1 -Subscription <id> -Role win-x64 -Build v0.1.N@sha256:<digest> -Scenario nightly-v1 -Relay staging
#>
#Requires -Version 7
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string] $Subscription,
    [Parameter(Mandatory)][string] $Role,
    [Parameter(Mandatory)][string] $Build,
    [Parameter(Mandatory)][string] $Scenario,
    [Parameter(Mandatory)][string] $Relay,
    [string] $RunId = [Guid]::NewGuid().ToString('N').Substring(0, 12),
    [string] $Out,
    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Scenario -cne 'nightly-v1') { throw 'Scenario must be nightly-v1; other coverage is not implemented' }
if ($Relay -cnotin 'staging', 'ppe') { throw 'Relay must be staging or ppe; URLs and production are refused' }
if ($Build -cnotmatch '^v0[.]1[.][0-9]+@sha256:[0-9a-f]{64}\z') {
    throw 'Build must be v0.1.N@sha256:<64 lowercase hex>; a URL, path or unpinned tag is not accepted'
}
if (Test-Path Env:LAB_GITHUB_TOKEN) {
    throw 'LAB_GITHUB_TOKEN is refused; the owner completes device flow on each fresh VM/run, never through a lab-token secret'
}

# Reuse the sole provisioner's validation and planning; never forward -Apply.
$provision = & (Join-Path $PSScriptRoot 'provision.ps1') -Subscription $Subscription -Role $Role -RunId $RunId
$buildParts = $Build -csplit '@', 2
$unmet = @($provision.unmet) + @(
    'build-provenance (Platform): Protected main CI/release provenance and installer bytes must be verified; syntax is not verification.'
    'scenario-execution (Platform): The real interactive nightly-v1 steps and AgentSim are unbuilt; planning is not coverage.'
    'transport-evidence (Platform): Guest transport, sanitized evidence and result.json production are unbuilt.'
    'owner-device-flow (owner): Each fresh VM/run needs owner-only Slipgates-lab-bot device flow within its original expiry; no saved signed-in image.'
)
if ($Relay -ceq 'ppe') { $unmet += 'ppe (SRE): PPE is unavailable; never substitute staging or production.' }

$plan = [pscustomobject][ordered]@{
    planVersion      = 1
    draft            = $true
    mode             = 'plan'
    executionAllowed = $false
    subscription     = $provision.subscription
    runId            = $provision.runId
    role             = $provision.role
    scenario         = $Scenario
    requestedBuild   = [pscustomobject]@{ tag = $buildParts[0]; digest = $buildParts[1]; provenanceVerified = $false }
    relay            = $Relay
    out              = if ($PSBoundParameters.ContainsKey('Out')) { $Out } else { Join-Path 'lab-results' "$RunId-$Role" }
    provision        = $provision
    unmet            = $unmet
}

if ($Apply) {
    throw "Run refused before any resource, Azure or sign-in call: this runner is a nonexecuting draft.`n- $($unmet -join "`n- ")"
}

$plan
