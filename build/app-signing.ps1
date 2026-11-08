# Code signing for the Windows app's releases with Azure Artifact Signing (#734): the metadata vpk
# pack's --azureTrustedSignFile reads, and the checks that a release folder, or a release
# downloaded back, carries the pinned publisher's Authenticode signatures. Dot-sourced by
# pack-app.ps1, publish-prerelease.ps1 and CI. docs/app-windows.md, "Signing".
#
# Artifact Signing renews its certificates daily, so a thumbprint pins nothing for long. What
# stays is the identity validation's own EKU, 1.3.6.1.4.1.311.97.<n>.<n>.<n>.<n>, unique to the
# validated publisher: the pin is that EKU, with Public Trust's and code signing's beside it.
# Full and delta packages are zips, which Authenticode does not cover: their integrity is the
# SHA256 and size in releases.<channel>.json, a delta's byte-for-byte rebuild of its full package
# (publish-prerelease.ps1) and the attestations, not a signature.
#Requires -Version 7
. (Join-Path $PSScriptRoot 'app-channels.ps1')

$AppSigningPublicTrustEku = '1.3.6.1.4.1.311.97.1.0'
$AppSigningCodeSigningEku = '1.3.6.1.5.5.7.3.3'
# What vpk signs (Velopack's PE extensions), and so what is checked.
$AppSigningPeExtensions = '.exe', '.dll', '.node'
# This product's own executables and assemblies, and Velopack's Update.exe (packed as
# Squirrel.exe) and launcher stub: each must carry this publisher's signature. Anything else,
# a runtime file Microsoft already signed say, must carry a valid signature, whoever's.
$AppSigningOwnedPattern = '^(AgentBridge|agent-bridge).*\.(exe|dll)$|^(Squirrel|Update)\.exe$|_ExecutionStub\.exe$'
# The executables a package must hold: a check that found none of them would prove nothing.
$AppSigningAppExecutables = 'AgentBridge.exe', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe'

function Assert-AppSignerEku([string]$Eku) {
    # Public Trust's own EKU (97.1.0) is in every Artifact Signing certificate, anyone's; only the
    # identity validation's, with four more numbers, names this publisher.
    if ($Eku -notmatch '^1\.3\.6\.1\.4\.1\.311\.97(\.\d+){4}$') {
        throw "'$Eku' is not an Artifact Signing identity validation EKU (1.3.6.1.4.1.311.97.<n>.<n>.<n>.<n>); signing is required, so nothing is packed or published without the publisher it pins"
    }
}

# The metadata.json vpk pack hands Artifact Signing: the account and certificate profile, and only
# the Azure CLI's credential (azure/login's OIDC sign-in), so no other credential on the machine
# is ever tried. It holds no key, token or secret.
function New-AppSigningMetadata([string]$Path, [string]$Endpoint, [string]$Account, [string]$CertificateProfile) {
    if ($Endpoint -notmatch '^https://[a-z0-9]+\.codesigning\.azure\.net/?$') { throw "'$Endpoint' is not an Artifact Signing endpoint (https://<region>.codesigning.azure.net)" }
    foreach ($pair in @(@('account', $Account), @('certificate profile', $CertificateProfile))) {
        if ($pair[1] -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{1,98}[A-Za-z0-9]$') { throw "'$($pair[1])' is not an Artifact Signing $($pair[0]) name" }
    }
    $metadata = [ordered]@{
        Endpoint               = $Endpoint.TrimEnd('/')
        CodeSigningAccountName = $Account
        CertificateProfileName = $CertificateProfile
        ExcludeCredentials     = @('EnvironmentCredential', 'WorkloadIdentityCredential', 'ManagedIdentityCredential',
            'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential',
            'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
    }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $Path -NoNewline
}

function Get-AppCertificateEku($Certificate) {
    @($Certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
            ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object Value)
}

# What is wrong with one file's signature, or nothing. -Owned: it must be this publisher's.
function Get-AppSignatureProblem([string]$Path, [string]$Name, [string]$Eku, [switch]$Owned) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ([string]$signature.Status -ne 'Valid') { return "${Name}: $($signature.Status)" }
    $ekus = if ($signature.SignerCertificate) { Get-AppCertificateEku $signature.SignerCertificate } else { @() }
    $ours = $ekus -contains $Eku
    if ($Owned -and -not $ours) { return "${Name}: signed by $($signature.SignerCertificate.Subject), not the pinned publisher" }
    if ($ours) {
        # A certificate lives three days; without a timestamp the signature dies with it.
        if (-not $signature.TimeStamperCertificate) { return "${Name}: no timestamp" }
        foreach ($required in $AppSigningPublicTrustEku, $AppSigningCodeSigningEku) {
            if ($ekus -notcontains $required) { return "${Name}: the signer has no $required EKU" }
        }
    }
}

# Every PE file under $Root: valid, and this publisher's where it is ours. $Required are paths
# relative to $Root that must be there and be ours; $Owned, more that are ours by their place.
function Get-AppTreeProblem([string]$Root, [string]$Label, [string]$Eku, [string[]]$Required = @(), [string[]]$Owned = @()) {
    $rootPath = [IO.Path]::GetFullPath($Root)
    $seen = @{}
    foreach ($file in Get-ChildItem -LiteralPath $rootPath -Recurse -File | Where-Object { $_.Extension -in $AppSigningPeExtensions }) {
        $relative = [IO.Path]::GetRelativePath($rootPath, $file.FullName)
        $seen[$relative] = $true
        $isOwned = $file.Name -match $AppSigningOwnedPattern -or $relative -in $Required -or $relative -in $Owned
        Get-AppSignatureProblem $file.FullName "$Label $relative" $Eku -Owned:$isOwned
    }
    foreach ($r in $Required) { if (-not $seen.ContainsKey($r)) { "$Label has no $r" } }
}

# The signatures of one channel's release of $Version in $Dir (Setup.exe, the full package and the
# portable zip), expanded under $Work. Returns the problems, and the signer when there are none.
function Test-AppReleaseSignature([string]$Dir, [string]$Channel, [string]$Version, [string]$Eku, [string]$Work) {
    Assert-AppSignerEku $Eku
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $problems = [Collections.Generic.List[string]]::new()
    $names = @{}
    foreach ($type in 'Installer', 'Full', 'Portable') {
        $names[$type] = Get-AppAssetName $Channel $type $Version
        if (-not (Test-Path -LiteralPath (Join-Path $Dir $names[$type]))) { $problems.Add("missing: $($names[$type])") }
    }
    if ($problems.Count) { return [pscustomobject]@{ Problems = @($problems); Signer = $null } }

    $setup = Join-Path $Dir $names.Installer
    $problem = Get-AppSignatureProblem $setup $names.Installer $Eku -Owned
    if ($problem) { $problems.Add($problem) }

    if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
    try {
        # The package as an installed copy applies it: the app and Velopack's Update.exe, packed
        # as Squirrel.exe, and the launcher stub under lib/app.
        $full = Join-Path $Work 'full'
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $Dir $names.Full), $full)
        $app = Join-Path 'lib' 'app'
        # vpk names the launcher stub, and the portable zip's launcher, after --packTitle, which
        # pack-app.ps1 gives as $AppProductName (GetStubBaseName and GetStubFileName in Velopack
        # 1.2.161's WindowsPackCommandRunner): exactly that stub, and no other.
        $stub = "$($AppProductName)_ExecutionStub.exe"
        $required = @($AppSigningAppExecutables + 'Squirrel.exe' + $stub | ForEach-Object { Join-Path $app $_ })
        $stubs = @(Get-ChildItem -LiteralPath (Join-Path $full $app) -Filter '*_ExecutionStub.exe' -ErrorAction SilentlyContinue | Where-Object Name -CNE $stub)
        foreach ($s in $stubs) { $problems.Add("$($names.Full) has a launcher stub $($s.Name), not only $stub") }
        foreach ($p in @(Get-AppTreeProblem $full $names.Full $Eku -Required $required)) { if ($p) { $problems.Add($p) } }

        # The portable zip: exactly Update.exe and the launcher at its root, the app under current/.
        # A zip without the launcher has no way to start the app; one with another executable
        # there is not what vpk packs.
        $portable = Join-Path $Work 'portable'
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $Dir $names.Portable), $portable)
        $rootFiles = @('Update.exe', "$AppProductName.exe")
        foreach ($f in Get-ChildItem -LiteralPath $portable -File | Where-Object { $_.Extension -in $AppSigningPeExtensions -and $_.Name -cnotin $rootFiles }) {
            $problems.Add("$($names.Portable) has $($f.Name) at its root, which vpk does not pack")
        }
        $required = $rootFiles + @($AppSigningAppExecutables | ForEach-Object { Join-Path 'current' $_ })
        foreach ($p in @(Get-AppTreeProblem $portable $names.Portable $Eku -Required $required)) { if ($p) { $problems.Add($p) } }
    } finally {
        if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
    }
    $signer = if (-not $problems.Count) { (Get-AuthenticodeSignature -LiteralPath $setup).SignerCertificate.Subject }
    [pscustomobject]@{ Problems = @($problems); Signer = $signer }
}
