<#
.SYNOPSIS
DRAFT, NOT EXECUTABLE: plans the Azure resources one clean-VM lab run would own (#357, #736).

.DESCRIPTION
quality/lab/README.md is the lab's contract; this is its provisioning piece, in draft. It
returns the plan for one run of one role: the run's OS disk made from the role's S0 snapshot, a
NIC with no public address, and the VM. Each is named after the run and tagged with an expiry
no more than 4 hours out, inside rg-agent-bridge-lab and nowhere else. The plan also holds the
exact deletes that remove them.

Planning makes no Azure call at all: no az, no sign-in, no subscription lookup.

-Apply refuses, before any Azure call, while any prerequisite in $Prerequisites below is unmet,
and lists each one. Today none is met: the lab's resource group, `lab` environment, OIDC
principal and bootstrap receipt don't exist, no S0 snapshot exists, native Windows Arm64 is
unproven, and no durable expiry sweeper exists. The sweeper matters because this script's own
cleanup on failure is best effort only: a cancelled or killed run never reaches it, so nothing
may be created until something independent deletes expired run-owned resources. A
prerequisite is marked met only by the reviewed change that delivers its receipt, never by a
parameter, an environment variable or a configuration default.

Every Azure call names -Subscription, which has no default: the CLI's default subscription
never decides where anything goes. Nothing here creates or changes a resource group, network,
identity, role assignment, public IP, NSG or credential, and nothing lists or deletes by
pattern: every name is derived from -RunId.

.PARAMETER Subscription
The owner-approved Visual Studio Enterprise subscription, by id. Required on every call; never
read from `az account`.

.PARAMETER Role
win-x64 or win-arm64. win-arm64 is a native Arm64 VM (a ps_v5 size, an Arm64 disk); never x64
emulation or a Linux stand-in.

.PARAMETER RunId
6 to 12 lowercase letters and digits, unique to the run. Every resource name ends with it.

.PARAMETER ExpiryHours
Hours until the run's resources expire and the sweeper may delete them: 1 to 4.

.PARAMETER Apply
Create the resources. Refused in this draft (see the description).

.EXAMPLE
./quality/lab/provision.ps1 -Subscription <id> -Role win-arm64 -RunId 20261103a | ConvertTo-Json -Depth 5
#>
#Requires -Version 7
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')]
    [string] $Subscription,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Role,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{6,12}$')]
    [string] $RunId,

    [ValidateRange(1, 4)]
    [int] $ExpiryHours = 4,

    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ValidatePattern and ValidateSet ignore case; the names and tags below don't.
$Subscription = $Subscription.ToLowerInvariant()
if ($Subscription -eq '00000000-0000-0000-0000-000000000000') { throw 'Subscription must be the owner-approved subscription id, not an all-zero placeholder' }
if ($Role -cnotin 'win-x64', 'win-arm64') { throw "Role must be written win-x64 or win-arm64, not $Role" }
if ($RunId -cnotmatch '^[a-z0-9]{6,12}$') { throw "RunId must be 6 to 12 lowercase letters and digits, not $RunId" }

# The one resource group the lab may use. The bootstrap (prerequisite below) creates it, its
# network and the OIDC principal scoped to it; this script never creates or changes any of them.
$ResourceGroup = 'rg-agent-bridge-lab'
$VnetName = 'vnet-agent-bridge-lab'
$SubnetName = 'runs'

# Per role: the size priced in the owner's B1 decision (#357, 2026-11-04) and the disk's architecture.
# Only the S0 snapshots persist between runs: a deallocated VM's disk still costs money, so each
# run's OS disk is created from S0 and deleted with the run.
$Roles = @{
    'win-x64'   = @{ Code = 'x64'; Size = 'Standard_D4s_v5'; Architecture = 'x64' }
    'win-arm64' = @{ Code = 'a64'; Size = 'Standard_D4ps_v5'; Architecture = 'Arm64' }
}

# What -Apply needs before it may create anything. Met only by the reviewed change that delivers
# each receipt (quality/lab/README.md §10), never from a parameter or the environment.
$Prerequisites = @(
    [pscustomobject]@{ Id = 'owner-authorization'; Roles = @('win-x64', 'win-arm64'); Met = $false; Owner = 'owner'
        Need = 'The owner authorizes lab execution and spend. Accepting the contract and this draft authorized neither.' }
    [pscustomobject]@{ Id = 'bootstrap'; Roles = @('win-x64', 'win-arm64'); Met = $false; Owner = 'owner, Platform'
        Need = "A bootstrap receipt for $ResourceGroup in the named subscription: the group tagged purpose=agent-bridge-lab, $VnetName/$SubnetName with no inbound from the Internet, the GitHub environment lab, and an OIDC principal whose only role is scoped to $ResourceGroup (#357 B2)." }
    [pscustomobject]@{ Id = 's0-snapshot'; Roles = @('win-x64', 'win-arm64'); Met = $false; Owner = 'Platform'
        Need = 'The role''s S0 snapshot (snap-lab-<code>-s0: Trusted Launch, Windows client licence) built from a clean image with no reusable credential, and a restore proof that a run from it has no previous identity, hooks, settings or processes.' }
    [pscustomobject]@{ Id = 'arm64-feasibility'; Roles = @('win-arm64'); Met = $false; Owner = 'Platform, owner'
        Need = 'A feasibility receipt for native Windows Arm64 in this subscription and region: the image, Standard_D4ps_v5 (or the size chosen) unrestricted, Trusted Launch and licence billing (#357 B1). Until then win-arm64 is unproven; x64 emulation and Linux are no substitute.' }
    [pscustomobject]@{ Id = 'expiry-sweeper'; Roles = @('win-x64', 'win-arm64'); Met = $false; Owner = 'Platform'
        Need = "A durable sweeper, independent of this script, that deletes expired run-owned resources in $ResourceGroup by exact name from a trusted inventory of runs, and forgets their lab devices. This script's own cleanup is best effort: a cancelled run never reaches it." }
    [pscustomobject]@{ Id = 'cost-alerts'; Roles = @('win-x64', 'win-arm64'); Met = $false; Owner = 'SRE'
        Need = 'Lab cost and resource-age alerts (infra/modules/monitoring.bicep) against the owner''s $75/month budget, with nightly runs of both roles (#357 B1).' }
)

$spec = $Roles[$Role]
$code = $spec.Code
$createdAt = [DateTime]::UtcNow
$expiresAt = $createdAt.AddHours($ExpiryHours)
$groupId = "/subscriptions/$Subscription/resourceGroups/$ResourceGroup"
$snapshotId = "$groupId/providers/Microsoft.Compute/snapshots/snap-lab-$code-s0"
$subnetId = "$groupId/providers/Microsoft.Network/virtualNetworks/$VnetName/subnets/$SubnetName"
$names = [ordered]@{
    disk = "disk-lab-$code-$RunId"
    nic  = "nic-lab-$code-$RunId"
    vm   = "vm-lab-$code-$RunId"
}
# Tags label a resource for people and the sweeper's report; they are not what makes it the
# run's. The sweeper deletes from its trusted inventory of runs, by exact name.
$tags = @(
    'purpose=agent-bridge-lab'
    "lab-run=$RunId"
    "lab-role=$Role"
    "created-at=$($createdAt.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    "expires-at=$($expiresAt.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    'created-by=quality/lab/provision.ps1'
)
$scope = @('--subscription', $Subscription, '-g', $ResourceGroup)

# No public IP and no NSG of its own: the NIC is made alone on the lab subnet, whose NSG
# (bootstrap) denies inbound from the Internet, and the VM takes that NIC. The OS disk is a
# specialized copy of S0, so no admin user or password is passed anywhere.
$create = @(
    , (@('disk', 'create') + $scope + @('-n', $names.disk, '--source', $snapshotId, '--architecture', $spec.Architecture,
            '--sku', 'StandardSSD_LRS', '--tags') + $tags + @('-o', 'none'))
    , (@('network', 'nic', 'create') + $scope + @('-n', $names.nic, '--subnet', $subnetId, '--tags') + $tags + @('-o', 'none'))
    , (@('vm', 'create') + $scope + @('-n', $names.vm, '--attach-os-disk', $names.disk, '--os-type', 'Windows',
            '--size', $spec.Size, '--nics', $names.nic, '--security-type', 'TrustedLaunch', '--license-type', 'Windows_Client',
            '--tags') + $tags + @('-o', 'none'))
)
$cleanup = @(
    , (@('vm', 'delete') + $scope + @('-n', $names.vm, '--yes', '-o', 'none'))
    , (@('network', 'nic', 'delete') + $scope + @('-n', $names.nic, '-o', 'none'))
    , (@('disk', 'delete') + $scope + @('-n', $names.disk, '--yes', '-o', 'none'))
)
$shows = @(
    , (@('disk', 'show') + $scope + @('-n', $names.disk))
    , (@('network', 'nic', 'show') + $scope + @('-n', $names.nic))
    , (@('vm', 'show') + $scope + @('-n', $names.vm))
)

# Every command, whatever edits come later: the one subscription, the one group, a run-owned
# name, and nothing that opens the VM, grants anything or reaches beyond the run.
$forbidden = 'public-ip-address', 'nsg', 'nsg-rule', 'admin-username', 'admin-password', 'assign-identity', 'role', 'scope', 'ids', 'custom-data', 'user-data', 'secrets'
foreach ($argv in $create + $cleanup + $shows) {
    $line = $argv -join ' '
    if ($argv[0] -in 'account', 'login', 'role', 'group', 'resource', 'identity', 'keyvault') { throw "az $($argv[0]) has no place in a lab run: $line" }
    if (@($argv | Where-Object { $_ -eq '--subscription' }).Count -ne 1 -or $argv[[array]::IndexOf($argv, '--subscription') + 1] -cne $Subscription) { throw "not scoped to the one subscription: $line" }
    if ($argv[[array]::IndexOf($argv, '-g') + 1] -cne $ResourceGroup) { throw "not scoped to ${ResourceGroup}: $line" }
    if ($argv[[array]::IndexOf($argv, '-n') + 1] -cnotin @($names.Values)) { throw "not a run-owned name: $line" }
    foreach ($word in $forbidden) { if ($argv -contains "--$word") { throw "--$word has no place in a lab run: $line" } }
}

$unmet = @($Prerequisites | Where-Object { -not $_.Met -and $Role -in $_.Roles })
$plan = [pscustomobject][ordered]@{
    schemaVersion = 1
    draft         = $true
    mode          = if ($Apply) { 'apply' } else { 'plan' }
    subscription  = $Subscription
    resourceGroup = $ResourceGroup
    role          = $Role
    runId         = $RunId
    size          = $spec.Size
    architecture  = $spec.Architecture
    source        = $snapshotId
    createdAt     = $createdAt.ToString('yyyy-MM-ddTHH:mm:ssZ')
    expiresAt     = $expiresAt.ToString('yyyy-MM-ddTHH:mm:ssZ')
    resources     = @($names.Values)
    create        = @($create | ForEach-Object { , (@('az') + $_) })
    cleanup       = @($cleanup | ForEach-Object { , (@('az') + $_) })
    unmet         = @($unmet | ForEach-Object { "$($_.Id) ($($_.Owner)): $($_.Need)" })
}

if (-not $Apply) { return $plan }

if ($unmet.Count) {
    throw "-Apply refused before any Azure call: this is a draft and $($unmet.Count) prerequisite(s) are unmet.`n- $($plan.unmet -join "`n- ")"
}

# --- Not reachable until every prerequisite above is met by a reviewed change. -------------

function Invoke-LabAz([string[]] $Argv) {
    # The plan's arguments, checked above. az is a batch file on Windows, so none may carry cmd
    # metacharacters, and none is a secret.
    $out = & az @Argv --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($Argv[0..1] -join ' ') failed: $(($out | Out-String).Trim())" }
}

function Test-LabResource([string[]] $ShowArgs) {
    $out = & az @ShowArgs --only-show-errors -o none 2>&1
    if ($LASTEXITCODE -eq 0) { return $true }
    # Only a definite "not found" is absence; throttling or a denied read must not look like it.
    $text = ($out | Out-String)
    if ($text -match 'ResourceNotFound|NotFound|could not be found|was not found') { return $false }
    throw "az $($ShowArgs -join ' ') failed: $($text.Trim())"
}

$purpose = & az group show -n $ResourceGroup --subscription $Subscription --query 'tags.purpose' -o tsv --only-show-errors 2>&1
if ($LASTEXITCODE -ne 0 -or "$purpose".Trim() -cne 'agent-bridge-lab') { throw "$ResourceGroup in the named subscription is not the bootstrapped lab group" }
# A run id is used once: a create over an existing resource would adopt it.
foreach ($argv in $shows) { if (Test-LabResource $argv) { throw "$($argv[[array]::IndexOf($argv, '-n') + 1]) already exists; a run id is used once" } }

if (-not $PSCmdlet.ShouldProcess("$ResourceGroup ($($names.Values -join ', '))", "Create the lab run's resources, expiring $($plan.expiresAt)")) { return $plan }

try {
    foreach ($argv in $create) { Invoke-LabAz $argv }
    return $plan
} catch {
    # Best effort only, by exact name: the sweeper, not this, is what guarantees removal.
    foreach ($argv in $cleanup) { try { Invoke-LabAz $argv } catch { Write-Warning "$_" } }
    throw
}
