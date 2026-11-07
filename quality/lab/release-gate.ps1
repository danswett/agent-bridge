<#
.SYNOPSIS
DRAFT, HELD: the first job of .github/workflows/lab-release-verify.yml, which refuses every
release verification on the shared clean-VM lab until the lab may run it (#357, #736).

.DESCRIPTION
Nothing in lab-release-verify.yml calls the lab nightly (#854), which is what resolves the cut,
asks for an OIDC token and reaches the lab environment, unless this script returns. It refuses,
listing every reason at once, when:

- the run isn't a dispatch from main of danswett/agent-bridge, on its first attempt;
- the release isn't a release tag (there is no default: a verification names its release), or the
  relay isn't `staging` or `ppe` exactly;
- the nightly, its scripts, the provisioner or the shared runner isn't in the checkout;
- any prerequisite in $Prerequisites below is unmet.

Today all of them are unmet, and #849 and #854 aren't on main, so it always refuses.

A prerequisite is marked met only by the reviewed change that delivers its receipt, never by a
parameter, an environment variable, a repository variable or a configuration default: this
script has no input that can clear it. The nightly's own gate (quality/lab/nightly-gate.ps1)
keeps the lab's prerequisites and refuses again by itself; nightly-cleared below repeats them
here so that nothing is even called first.

.EXAMPLE
pwsh quality/lab/release-gate.ps1 -Release v0.1.600 -Relay staging -EventName workflow_dispatch -Ref refs/heads/main -Repository danswett/agent-bridge -RunAttempt 1
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string] $Release,
    [Parameter(Mandatory)][AllowEmptyString()][string] $Relay,
    [Parameter(Mandatory)][string] $EventName,
    [Parameter(Mandatory)][string] $Ref,
    [Parameter(Mandatory)][string] $Repository,
    [Parameter(Mandatory)][string] $RunAttempt,
    # The checkout the run would use: only where the files it depends on are looked for.
    [string] $Root = (Join-Path $PSScriptRoot '..' '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# What a release verification needs before it may call the nightly. Met only by the reviewed
# change that delivers each receipt.
$Prerequisites = @(
    [pscustomobject]@{ Id = 'contract-approval'; Met = $false; Owner = 'owner, Quality'
        Need = 'The #357 lab contract and its provisioning draft (#849) approved and merged.' }
    [pscustomobject]@{ Id = 'nightly-landed'; Met = $false; Owner = 'Platform, Quality'
        Need = 'The lab nightly (#854) merged, and this workflow''s call re-pointed from its pinned #854 commit to main''s own ./.github/workflows/lab-nightly.yml, so the workflow and the scripts its jobs run come from one commit; with proof that a call still runs one lab run at a time.' }
    [pscustomobject]@{ Id = 'nightly-cleared'; Met = $false; Owner = 'owner, Platform, SRE'
        Need = 'Every prerequisite of quality/lab/nightly-gate.ps1 met in reviewed source: the owner''s authorization of execution and spend, the bootstrap, S0 snapshots, native Arm64 feasibility, run registration, the expiry sweeper, cost alerts, the lab token and the v1 scenario (#357 B1-B3, #736).' }
    [pscustomobject]@{ Id = 'release-consumer'; Met = $false; Owner = 'SRE, owner'
        Need = 'A reviewed consumer of the verification (SRE''s #590 cut and promotion path, quality/README.md §7): which channel, if any, a verified release may feed. Until then a verification has nowhere to go, and it is never a promotion, a channel move or a Beta 1 count.' }
)

$refusals = [Collections.Generic.List[string]]::new()

if ($Repository -cne 'danswett/agent-bridge') { $refusals.Add("runs only in danswett/agent-bridge, not $Repository") }
if ($Ref -cne 'refs/heads/main') { $refusals.Add("runs only from refs/heads/main, not $Ref") }
if ($EventName -cne 'workflow_dispatch') { $refusals.Add("runs only by a dispatch naming its release, not $EventName") }
if ($RunAttempt -cne '1') { $refusals.Add("re-runs are refused (attempt $RunAttempt): a retry is a new run") }

if ($Release -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+\z') { $refusals.Add("the release must be a release tag such as v0.1.600, never empty or the newest by default, not '$Release'") }
if ($Relay -cnotin 'staging', 'ppe') { $refusals.Add("the relay must be staging or ppe, never production or a default, not '$Relay'") }

foreach ($dependency in @(
        @{ Path = '.github/workflows/lab-nightly.yml'; From = 'the lab nightly, #854, not on main' }
        @{ Path = 'quality/lab/nightly-gate.ps1'; From = 'the lab nightly, #854, not on main' }
        @{ Path = 'quality/lab/nightly-verdict.ps1'; From = 'the lab nightly, #854, not on main' }
        @{ Path = 'quality/lab/provision.ps1'; From = 'the provisioning draft, #849, not on main' }
        @{ Path = 'quality/lab/run.ps1'; From = 'the shared clean-VM runner, #736, unbuilt' })) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $dependency.Path) -PathType Leaf)) {
        $refusals.Add("$($dependency.Path) is missing: $($dependency.From)")
    }
}

foreach ($item in @($Prerequisites | Where-Object { -not $_.Met })) { $refusals.Add("$($item.Id) ($($item.Owner)): $($item.Need)") }

if ($refusals.Count) {
    throw "The lab release verification is refused before the nightly is called:`n" + (($refusals | ForEach-Object { "- $_" }) -join "`n")
}

[pscustomobject]@{
    Release = $Release
    Relay   = $Relay
}
