<#
.SYNOPSIS
DRAFT, HELD: the first job of .github/workflows/lab-nightly.yml, which refuses every nightly
clean-VM lab run until the lab may run (#357, #736).

.DESCRIPTION
Nothing in lab-nightly.yml signs in to Azure, asks for an OIDC token, reads the `lab`
environment or resolves a cut unless this script returns. It refuses, listing every reason at
once, when:

- the run isn't from main of danswett/agent-bridge, by its schedule or a dispatch, on its first
  attempt (a re-run would reuse the run's resource names; a retry is a new run);
- the relay isn't `staging` or `ppe` exactly, or the cut isn't empty or a release tag;
- the provisioner (quality/lab/provision.ps1, #849) or the shared runner (quality/lab/run.ps1,
  #736) isn't in the checkout;
- any prerequisite in $Prerequisites below is unmet.

The provisioner and nonexecuting runner interface have landed. Execution prerequisites remain
unmet, so the nightly still always refuses; source presence is not execution permission.

A prerequisite is marked met only by the reviewed change that delivers its receipt, never by a
parameter, an environment variable, a repository variable or a configuration default: this
script has no input that can clear it. provision.ps1 keeps its own prerequisites and refuses
-Apply by itself; this gate repeats the lab's so that nothing even signs in first.

On success it returns the lab run id both roles share: 'n' and the GitHub run id in base 36,
12 lowercase letters and digits as provision.ps1 requires.

.EXAMPLE
pwsh quality/lab/nightly-gate.ps1 -Relay staging -EventName schedule -Ref refs/heads/main -Repository danswett/agent-bridge -GitHubRunId 41973345451 -RunAttempt 1
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string] $Relay,
    [AllowEmptyString()][string] $Cut = '',
    [Parameter(Mandatory)][string] $EventName,
    [Parameter(Mandatory)][string] $Ref,
    [Parameter(Mandatory)][string] $Repository,
    [Parameter(Mandatory)][string] $GitHubRunId,
    [Parameter(Mandatory)][string] $RunAttempt,
    # The checkout the run would use: only where the provisioner and runner are looked for.
    [string] $Root = (Join-Path $PSScriptRoot '..' '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# What the nightly needs before it may sign in anywhere (#849's quality/lab/README.md §10 is
# the contract). Met only by the reviewed change that delivers each receipt.
$Prerequisites = @(
    [pscustomobject]@{ Id = 'contract-approval'; Met = $false; Owner = 'owner, Quality'
        Need = 'Owner approval of the #357 lab contract. The merged nonexecuting drafts (#849, #1115) are not execution approval.' }
    [pscustomobject]@{ Id = 'owner-authorization'; Met = $false; Owner = 'owner'
        Need = 'The owner authorizes this nightly''s execution and spend. Approving the contract, this workflow or a separate manual proof authorizes neither.' }
    [pscustomobject]@{ Id = 'bootstrap'; Met = $false; Owner = 'owner, Platform'
        Need = 'The bootstrap receipt (#357 B2): rg-agent-bridge-lab and its network, an OIDC principal scoped to that group only and trusting only environment lab, and the lab environment limited to main, holding LAB_AZURE_CLIENT_ID, LAB_AZURE_TENANT_ID and LAB_SUBSCRIPTION_ID (the owner-approved subscription, never a default).' }
    [pscustomobject]@{ Id = 's0-snapshot'; Met = $false; Owner = 'owner'
        Need = 'Each role''s S0 snapshot, with the proof that a run restored from it has no previous identity, hooks, settings or processes.' }
    [pscustomobject]@{ Id = 'arm64-feasibility'; Met = $false; Owner = 'Platform, owner'
        Need = 'A receipt that native Windows 11 Arm64 runs in this subscription and region: image, size, Trusted Launch and licence billing (#357 B1). The nightly runs both roles or neither: x64 emulation and Linux are no substitute.' }
    [pscustomobject]@{ Id = 'run-registration'; Met = $false; Owner = 'Platform, SRE'
        Need = 'provision.ps1 registers each run''s exact resource names and expiry in the sweeper''s trusted inventory, and checks the receipt, before its first create.' }
    [pscustomobject]@{ Id = 'expiry-sweeper'; Met = $false; Owner = 'SRE'
        Need = 'A durable sweeper, independent of this workflow, that deletes expired run-owned resources by exact name from that inventory. A cancelled run never reaches its own cleanup.' }
    [pscustomobject]@{ Id = 'cost-alerts'; Met = $false; Owner = 'SRE'
        Need = 'Lab cost and resource-age alerts against the owner''s $75/month for both roles nightly (#357 B1), with the cost measured.' }
    [pscustomobject]@{ Id = 'owner-device-flow'; Met = $false; Owner = 'owner, Platform'
        Need = 'Owner-only Slipgates-lab-bot device flow on each role''s fresh VM in every run within its original expiry, with credential, relay and device fences proved (#357 B3). No lab token, CI/vault secret or signed-in image.' }
    [pscustomobject]@{ Id = 'v1-scenario'; Met = $false; Owner = 'Platform'
        Need = 'The shared runner''s v1 scenario, nightly-v1 (#736): launch, reply, answer, stop and an explicit update pair, writing result.json schema version 1.' }
)
# Only when the run asks for PPE (#590, owner decision B4).
$PpeRelay = [pscustomobject]@{ Id = 'ppe-relay'; Met = $false; Owner = 'SRE, owner'
    Need = 'PPE enrolled, keyed, in DNS and deployed (#590). Until then the relay is staging.' }

$refusals = [Collections.Generic.List[string]]::new()

if ($Repository -cne 'danswett/agent-bridge') { $refusals.Add("runs only in danswett/agent-bridge, not $Repository") }
if ($Ref -cne 'refs/heads/main') { $refusals.Add("runs only from refs/heads/main, not $Ref") }
if ($EventName -cnotin 'schedule', 'workflow_dispatch') { $refusals.Add("runs only by its schedule or a dispatch, not $EventName") }
if ($GitHubRunId -cnotmatch '^[1-9][0-9]{0,15}\z') { $refusals.Add("the GitHub run id must be a number, not '$GitHubRunId'") }
if ($RunAttempt -cne '1') { $refusals.Add("re-runs are refused (attempt $RunAttempt): they would reuse this run's resource names; retry a cut as a new run") }

if ($Relay -cnotin 'staging', 'ppe') { $refusals.Add("the relay must be staging or ppe, never production or a default, not '$Relay'") }
if ($Cut -cnotmatch '^(v[0-9]+\.[0-9]+\.[0-9]+)?\z') { $refusals.Add("the cut must be empty (the newest main pre-release) or a release tag such as v0.1.600, not '$Cut'") }

foreach ($dependency in @(
        @{ Path = 'quality/lab/provision.ps1'; From = 'the sole nonexecuting provisioning draft, #849' }
        @{ Path = 'quality/lab/run.ps1'; From = 'the shared nonexecuting runner interface, #1115, part of #736' })) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $dependency.Path) -PathType Leaf)) {
        $refusals.Add("$($dependency.Path) is missing: $($dependency.From)")
    }
}

$unmet = @($Prerequisites | Where-Object { -not $_.Met })
if ($Relay -ceq 'ppe' -and -not $PpeRelay.Met) { $unmet += $PpeRelay }
foreach ($item in $unmet) { $refusals.Add("$($item.Id) ($($item.Owner)): $($item.Need)") }

if ($refusals.Count) {
    throw "The lab nightly is refused before any sign-in, cut or Azure call:`n" + (($refusals | ForEach-Object { "- $_" }) -join "`n")
}

$digits = '0123456789abcdefghijklmnopqrstuvwxyz'
[long] $n = $GitHubRunId
[long] $remainder = 0
$base36 = ''
do {
    $n = [Math]::DivRem($n, [long]36, [ref]$remainder)
    $base36 = "$($digits[[int]$remainder])$base36"
} while ($n -gt 0)

[pscustomobject]@{
    RunId = 'n' + $base36.PadLeft(11, '0')
    Relay = $Relay
    Cut   = $Cut
}
