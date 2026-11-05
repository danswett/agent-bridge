<#
.SYNOPSIS
DRAFT, SOURCE ONLY, HELD: the gate before the clean-VM install smoke installs anything on a lab
machine (#357 phases 2-4, #736). It refuses every install today.

.DESCRIPTION
The install smoke is one scenario of the lab nightly: on each role's machine, freshly restored
from S0, install the cut's own installer, then show the installed product working. It adds no
lab, workflow, provisioner, runner or cut of its own. It runs inside a nightly run that
lab-nightly.yml's gate (#854) cleared, through the shared runner (quality/lab/run.ps1, #736),
on machines the provisioner (quality/lab/provision.ps1, #849) creates, and
install-smoke-verdict.ps1 judges what it recorded.

Nothing may install, start an installer, touch a VM's OS or call Azure for the install smoke
unless this script returns. It refuses, listing every reason at once, when:

- the role isn't win-x64 or win-arm64, the cut isn't a release tag, the run id isn't the
  nightly's ('n' and 11 lowercase base-36 digits), or the relay isn't staging or ppe exactly;
- the installer isn't named by a sha256 digest, or the CI run that built it isn't a protected
  run (an id, from refs/heads/main or a release tag). There is no installer path parameter: the
  runner may only install the artifact the cut verified by that digest, never a file it is
  handed;
- the provisioner (#849), the runner (#736) or the nightly's gate and verdict (#854) aren't in
  the checkout;
- any prerequisite in $Prerequisites below is unmet.

Today all of them are unmet and none of those scripts is on main, so it always refuses.

A prerequisite is marked met only by the reviewed change that delivers its receipt, never by a
parameter, an environment variable, a repository variable or a configuration default: this
script has no input that can clear one. The lab's own prerequisites (owner authorization,
bootstrap, S0, Arm64 feasibility, registration, the sweeper, cost alerts, the lab token and
PPE) stay in nightly-gate.ps1 and provision.ps1; this gate doesn't copy them, it waits for them
through nightly-cleared.

Its return is a go-ahead, not evidence: no installer has run, and a later installer exit is
never a pass by itself (install-smoke-verdict.ps1).

.EXAMPLE
pwsh quality/lab/install-smoke-gate.ps1 -Role win-x64 -CutId v0.1.600 -RunId n00000jaqd8b -Relay staging -InstallerDigest sha256:<hex> -CiRun 41973345451 -SourceRef refs/heads/main
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string] $Role,
    [Parameter(Mandatory)][AllowEmptyString()][string] $CutId,
    [Parameter(Mandatory)][AllowEmptyString()][string] $RunId,
    [Parameter(Mandatory)][AllowEmptyString()][string] $Relay,
    # What the nightly's cut verified: this role's installer, and the CI run and ref it came from.
    [Parameter(Mandatory)][AllowEmptyString()][string] $InstallerDigest,
    [Parameter(Mandatory)][AllowEmptyString()][string] $CiRun,
    [Parameter(Mandatory)][AllowEmptyString()][string] $SourceRef,
    # The checkout the run would use: only where the scripts this depends on are looked for.
    [string] $Root = (Join-Path $PSScriptRoot '..' '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# What the install smoke needs beyond the nightly itself. Met only by the reviewed change that
# delivers each receipt.
$Prerequisites = @(
    [pscustomobject]@{ Id = 'contract-approval'; Met = $false; Owner = 'owner, Quality'
        Need = 'The #357 lab contract (#849) approved and merged, with the result.json schema version 1 additions this scenario reads: a device field, lab-<role>-<runId>, and an install step before launch.' }
    [pscustomobject]@{ Id = 'nightly-cleared'; Met = $false; Owner = 'owner, Platform, SRE'
        Need = 'lab-nightly.yml (#854) merged, and every prerequisite its gate (nightly-gate.ps1) and provision.ps1 hold met in reviewed source. The install smoke runs only inside a nightly run that gate cleared, never on its own.' }
    [pscustomobject]@{ Id = 'installer-attestation'; Met = $false; Owner = 'Platform, SRE'
        Need = 'The nightly''s cut verifies each role''s installer against its build provenance attestation from a protected CI run before handing its digest on (#590), and the runner refuses any file whose digest isn''t that one.' }
    [pscustomobject]@{ Id = 'install-smoke-scenario'; Met = $false; Owner = 'Platform'
        Need = 'The shared runner (#736) implements -Scenario install-smoke: on a machine restored from S0, installs only the verified artifact, as the signed-in desktop user, then launches the installed product and records both as steps, with the device it signed in as.' }
    [pscustomobject]@{ Id = 'owner-authorization'; Met = $false; Owner = 'owner'
        Need = 'The owner authorizes installing on lab machines. Approving the contract, this draft or the nightly authorizes nothing.' }
)

$refusals = [Collections.Generic.List[string]]::new()

if ($Role -cnotin 'win-x64', 'win-arm64') { $refusals.Add("the role must be win-x64 or win-arm64 (native Arm64, never emulation or Linux), not '$Role'") }
if ($CutId -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+\z') { $refusals.Add("the cut must be a release tag such as v0.1.600, not '$CutId'") }
if ($RunId -cnotmatch '^n[0-9a-z]{11}\z') { $refusals.Add("the run id must be the nightly's, 'n' and 11 lowercase base-36 digits, not '$RunId'") }
if ($Relay -cnotin 'staging', 'ppe') { $refusals.Add("the relay must be staging or ppe, never production or a default, not '$Relay'") }
if ($InstallerDigest -cnotmatch '^sha256:[0-9a-f]{64}\z') { $refusals.Add("the installer must be named by its verified digest, sha256:<64 lowercase hex>, not '$InstallerDigest'") }
if ($CiRun -cnotmatch '^[1-9][0-9]{0,15}\z') { $refusals.Add("the CI run must be the verified run's id, not '$CiRun'") }
if ($SourceRef -cnotmatch '^refs/(heads/main|tags/v[0-9]+\.[0-9]+\.[0-9]+)\z') { $refusals.Add("the installer must come from a protected ref, refs/heads/main or a release tag, not '$SourceRef'") }

foreach ($dependency in @(
        @{ Path = 'quality/lab/provision.ps1'; From = 'the provisioning draft, #849, not on main' }
        @{ Path = 'quality/lab/run.ps1'; From = 'the shared clean-VM runner, #736, unbuilt' }
        @{ Path = 'quality/lab/nightly-gate.ps1'; From = 'the lab nightly''s gate, #854, not on main' }
        @{ Path = 'quality/lab/nightly-verdict.ps1'; From = 'the lab nightly''s cut verdict, #854, not on main' })) {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $dependency.Path) -PathType Leaf)) {
        $refusals.Add("$($dependency.Path) is missing: $($dependency.From)")
    }
}

foreach ($item in @($Prerequisites | Where-Object { -not $_.Met })) { $refusals.Add("$($item.Id) ($($item.Owner)): $($item.Need)") }

if ($refusals.Count) {
    throw "The install smoke is refused before any installer, VM, OS or Azure operation:`n" + (($refusals | ForEach-Object { "- $_" }) -join "`n")
}

[pscustomobject]@{
    Scenario        = 'install-smoke'
    Role            = $Role
    Architecture    = if ($Role -ceq 'win-x64') { 'x64' } else { 'arm64' }
    CutId           = $CutId
    RunId           = $RunId
    Device          = "lab-$Role-$RunId"
    Relay           = $Relay
    InstallerDigest = $InstallerDigest
    CiRun           = $CiRun
    SourceRef       = $SourceRef
}
