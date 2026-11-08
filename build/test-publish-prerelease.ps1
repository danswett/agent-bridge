# Checks build/publish-prerelease.ps1, and the delta base and build identity build/pack-app.ps1
# gives the package, against stand-ins for gh, vpk and dotnet: nothing is built, uploaded,
# published or deleted, and the folders are under out/test-publish-prerelease.
#
#   pwsh build/test-publish-prerelease.ps1
#
# What it guards: a merge publishes exactly one pre-release of its own version, never a second
# copy, never one older than what is out or from a commit behind it, never a dev build, and the
# pruning never reaches a full release or a tag.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publishScript = Join-Path $PSScriptRoot 'publish-prerelease.ps1'
$packScript = Join-Path $PSScriptRoot 'pack-app.ps1'
$scratch = Join-Path $PSScriptRoot '..' 'out' 'test-publish-prerelease'
$fixtures = Join-Path $PSScriptRoot '..' 'out' 'test-publish-prerelease-hosts'
$repo = 'danswett/agent-bridge'
$commit = '0123456789abcdef0123456789abcdef01234567'
# Main's line around the commit being built, oldest first; a release's tag is on its ancestor
# unless a test puts it elsewhere.
$ancestor = 'fedcba9876543210fedcba9876543210fedcba98'
$descendant = '89abcdef0123456789abcdef0123456789abcdef'
$env:GH_TOKEN = 'stand-in-token'
Remove-Item Env:GITHUB_STEP_SUMMARY, Env:GITHUB_RUN_ID, Env:GITHUB_ACTIONS, Env:GITHUB_OUTPUT -ErrorAction SilentlyContinue

# Two real assemblies standing in for the host: one with the release metadata a
# -p:AgentBridgeRelease=true build carries, one without, as host-identity.ps1 reads them.
if (Test-Path $fixtures) { Remove-Item $fixtures -Recurse -Force }
$global:PpHost = @{}
foreach ($kind in 'dev', 'release') {
    $dir = Join-Path $fixtures $kind
    New-Item -ItemType Directory -Force $dir | Out-Null
    $global:PpHost[$kind] = Join-Path $dir 'agent-bridge-host.dll'
    $metadata = if ($kind -eq 'release') { '[assembly: System.Reflection.AssemblyMetadata("AgentBridgeRelease", "true")]' } else { '[assembly: System.Reflection.AssemblyMetadata("Other", "true")]' }
    Add-Type -TypeDefinition "$metadata public class StandInHost$kind {}" -OutputAssembly $global:PpHost[$kind]
}
$global:PpIgnoreRelease = $false
Add-Type -AssemblyName System.IO.Compression.FileSystem
$originalNugetPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = Join-Path $fixtures 'nuget'
foreach ($package in @(
        @{ Id = 'Notice.Expression'; License = '<license type="expression">MIT</license>'; File = 'LICENSE.txt' }
        @{ Id = 'Notice.File'; License = '<license type="file">legal/terms.txt</license>'; File = 'legal\terms.txt' }
    )) {
    $folder = Join-Path $env:NUGET_PACKAGES $package.Id.ToLowerInvariant() '1.0.0'
    $file = Join-Path $folder $package.File
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    Set-Content -LiteralPath $file "Verbatim notice fixture for $($package.Id): copyright, terms and disclaimer."
    Set-Content -LiteralPath (Join-Path $folder "$($package.Id.ToLowerInvariant()).nuspec") "<package><metadata><id>$($package.Id)</id><version>1.0.0</version>$($package.License)</metadata></package>"
}

# --- Stand-ins. Functions come before programs, so the scripts under test find these. What they
# record is global: $script: in them would be the script under test's.
$global:PpReleases = [Collections.Generic.List[object]]::new()
$global:PpTags = [Collections.Generic.List[string]]::new()
$global:PpCalls = [Collections.Generic.List[string]]::new()
$global:PpNextId = 1000
$global:PpDownload = $null
$global:PpCompare = @()
$global:PpPackSaw = $null
$global:PpPackArgs = $null
$global:PpPackHashes = @{}
$global:PpPublishRids = [Collections.Generic.List[string]]::new()
$global:PpBuildsFor = $null
$global:PpFeedExtra = $null
$global:PpGeneratedNotes = $null
$global:PpPublishedBody = $null
$global:PpPackMakes = $false
$global:PpTamper = $null

# --- Authenticode, as Get-AuthenticodeSignature reports it, for the signing checks
# (build/app-signing.ps1). Each file is signed by this publisher unless $global:PpSign names its
# leaf: 'unsigned', 'other' (another Artifact Signing publisher), 'notimestamp', 'nopublictrust' or
# 'microsoft'. The certificates are made in memory for these checks and never written anywhere.
$global:PpEku = '1.3.6.1.4.1.311.97.123456789.12345.67890.1'
$global:PpSign = @{}
function global:New-PpCertificate([string]$subject, [string[]]$ekus) {
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($subject, $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $oids = [Security.Cryptography.OidCollection]::new()
    foreach ($e in $ekus) { [void]$oids.Add([Security.Cryptography.Oid]::new($e)) }
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))
    $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(2))
}
$global:PpCertificates = @{
    ours          = New-PpCertificate 'CN=Agent Bridge Test Publisher' @($global:PpEku, '1.3.6.1.4.1.311.97.1.0', '1.3.6.1.5.5.7.3.3')
    nopublictrust = New-PpCertificate 'CN=Agent Bridge Test Publisher' @($global:PpEku, '1.3.6.1.5.5.7.3.3')
    other         = New-PpCertificate 'CN=Someone Else' @('1.3.6.1.4.1.311.97.987654321.1.2.3', '1.3.6.1.4.1.311.97.1.0', '1.3.6.1.5.5.7.3.3')
    microsoft     = New-PpCertificate 'CN=Microsoft Corporation' @('1.3.6.1.5.5.7.3.3')
    timestamp     = New-PpCertificate 'CN=Test Timestamping' @('1.3.6.1.5.5.7.3.8')
}
function global:Get-AuthenticodeSignature {
    param([string]$FilePath, [string]$LiteralPath)
    $leaf = Split-Path $(if ($LiteralPath) { $LiteralPath } else { $FilePath }) -Leaf
    $state = if ($global:PpSign.ContainsKey($leaf)) { $global:PpSign[$leaf] } else { 'ours' }
    if ($state -eq 'unsigned') { return [pscustomobject]@{ Status = 'NotSigned'; SignerCertificate = $null; TimeStamperCertificate = $null } }
    $certificate = $global:PpCertificates[$(if ($state -eq 'notimestamp') { 'ours' } else { $state })]
    [pscustomobject]@{ Status = 'Valid'; SignerCertificate = $certificate; TimeStamperCertificate = $(if ($state -ne 'notimestamp') { $global:PpCertificates.timestamp }) }
}

# GitHub failing calls (#907): the next N listings or DELETEs fail; a DELETE that "goes through"
# deletes before it fails, as a 500 after the fact does.
$global:PpListFailures = 0
$global:PpListings = 0
$global:PpFailListAfter = -1
$global:PpDeleteFailures = 0
$global:PpDeleteGoesThrough = $false
$global:PpSleeps = [Collections.Generic.List[int]]::new()
function global:Start-Sleep { [CmdletBinding()] param([int]$Seconds, [int]$Milliseconds) $global:PpSleeps.Add($Seconds) }

$global:PpMain = @($ancestor, $commit, $descendant)
$global:PpTagSha = @{}
$global:PpTagType = 'commit'
$global:PpTagRef = $null
$global:PpAncestry = $null
# What main's selector, at main's head, says of the commits after the one publishing: its JSON,
# or 'fail' for a nonzero exit.
$global:PpSelect = $null
# What vpk names a channel's full package (build/app-channels.ps1): x64's `win` without a suffix.
function global:Get-PpFull([string]$channel, [string]$version) {
    "AgentBridgeApp-$version$(if ($channel -ne 'win') { "-$channel" })-full.nupkg"
}

# The smallest PE image Get-PeMachine reads: an MZ header pointing at a COFF header for the machine.
function global:New-PpExe([string]$path, [string]$machine) {
    $bytes = [byte[]]::new(0x48)
    $bytes[0] = 0x4D; $bytes[1] = 0x5A
    [BitConverter]::GetBytes([int]0x40).CopyTo($bytes, 0x3C)
    [BitConverter]::GetBytes([uint32]0x4550).CopyTo($bytes, 0x40)
    [BitConverter]::GetBytes([uint16]@{ x64 = 0x8664; arm64 = 0xAA64 }[$machine]).CopyTo($bytes, 0x44)
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($path), $bytes)
}

function global:New-PpRelease([string]$tag, [bool]$prerelease = $true, [bool]$draft = $false) {
    $global:PpNextId++
    $global:PpReleases.Add([pscustomobject]@{ id = $global:PpNextId; tag_name = $tag; draft = $draft; prerelease = $prerelease })
    if (-not $draft) { $global:PpTags.Add("refs/tags/$tag") }
}

function global:gh {
    $a = @($args)
    $global:PpCalls.Add("gh $($a -join ' ')")
    $global:LASTEXITCODE = 0
    if ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/releases?*") {
        if ($global:PpListFailures -gt 0) { $global:PpListFailures--; $global:LASTEXITCODE = 1; return }
        $global:PpListings++
        if ($global:PpFailListAfter -ge 0 -and $global:PpListings -gt $global:PpFailListAfter) { $global:LASTEXITCODE = 1; return }
        foreach ($r in $global:PpReleases) { $r | ConvertTo-Json -Compress }
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/git/matching-refs/tags/*") {
        $prefix = 'refs/tags/' + $a[1].Substring("repos/$repo/git/matching-refs/tags/".Length)
        $global:PpTags | Where-Object { $_.StartsWith($prefix) }
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'DELETE') {
        $id = [long]($a[3] -split '/')[-1]
        if ($global:PpDeleteFailures -gt 0) {
            $global:PpDeleteFailures--
            if ($global:PpDeleteGoesThrough) { [void]$global:PpReleases.RemoveAll({ param($r) $r.id -eq $id }) }
            $global:LASTEXITCODE = 1; return
        }
        # As GitHub answers a release that isn't there: 404.
        if (-not $global:PpReleases.RemoveAll({ param($r) $r.id -eq $id })) { $global:LASTEXITCODE = 1 }
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'POST' -and $a[3] -eq "repos/$repo/releases/generate-notes") {
        if ($global:PpGeneratedNotes -eq 'fail') { $global:LASTEXITCODE = 1; return }
        @{ body = $global:PpGeneratedNotes } | ConvertTo-Json -Compress
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'PATCH') {
        $id = [long]($a[3] -split '/')[-1]
        $r = $global:PpReleases | Where-Object id -EQ $id
        $request = ($input -join "`n") | ConvertFrom-Json
        if ($request.draft -eq $false) {
            $global:PpPublishedBody = $request.body
            $r.draft = $false; $global:PpTags.Add("refs/tags/$($r.tag_name)")
        }
    } elseif ($a[0] -eq 'release' -and $a[1] -eq 'upload') {
    } elseif ($a[0] -eq 'release' -and $a[1] -eq 'download') {
        # What vpk upload github put up for the tag; as gh does, a pattern matching nothing fails.
        $store = Join-Path $scratch 'uploaded' $a[2]
        $dest = $a[[array]::IndexOf($a, '-D') + 1]
        New-Item -ItemType Directory -Force $dest | Out-Null
        for ($i = 0; $i -lt $a.Count; $i++) {
            if ($a[$i] -ne '-p') { continue }
            $file = Join-Path $store $a[$i + 1]
            if (-not (Test-Path -LiteralPath $file)) { $global:LASTEXITCODE = 1; return }
            Copy-Item -LiteralPath $file $dest -Force
        }
    } elseif ($a[0] -eq 'release' -and $a[1] -eq 'list') {
        $global:PpReleases | Where-Object { -not $_.draft } | Select-Object -Last 1 | ForEach-Object tag_name
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/git/ref/tags/*") {
        # A published release's tag: the commit it is on, its tag's sha unless a test says.
        $name = $a[1].Substring("repos/$repo/git/ref/tags/".Length)
        if ($global:PpTagRef -eq 'fail' -or $global:PpTags -notcontains "refs/tags/$name") { $global:LASTEXITCODE = 1; return }
        $global:PpTagType
        if ($global:PpTagSha.ContainsKey($name)) { $global:PpTagSha[$name] } else { $global:PpMain[0] }
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/compare/*" -and $a -contains '.status') {
        # Where head is from base on main's line, as GitHub's compare says it.
        if ($global:PpAncestry -eq 'fail') { $global:LASTEXITCODE = 1; return }
        if ($global:PpAncestry) { return $global:PpAncestry }
        $base, $head = $a[1].Substring("repos/$repo/compare/".Length) -split '\.\.\.'
        $from = [array]::IndexOf($global:PpMain, $base); $to = [array]::IndexOf($global:PpMain, $head)
        if ($from -lt 0 -or $to -lt 0) { 'diverged' } elseif ($to -gt $from) { 'ahead' } elseif ($to -eq $from) { 'identical' } else { 'behind' }
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/compare/*") {
        if ($global:PpCompare -eq 'fail') { $global:LASTEXITCODE = 1; return }
        $global:PpCompare
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/commits/*") {
        'The commit being built (#200)'
    } else { throw "stand-in gh: unexpected $($a -join ' ')" }
}

function global:vpk {
    $a = @($args)
    $global:LASTEXITCODE = 0
    function Arg([string]$name) { $i = [array]::IndexOf($a, $name); if ($i -ge 0) { $a[$i + 1] } }
    if ($a[0] -eq 'upload' -and $a[1] -eq 'github') {
        $merge = $a -contains '--merge'
        $global:PpCalls.Add("vpk upload github --channel $(Arg '--channel') --tag $(Arg '--tag') --targetCommitish $(Arg '--targetCommitish') pre=$($a -contains '--pre') publish=$($a -contains '--publish') merge=$merge")
        # As vpk does: without --merge a release already tagged is refused; with it, joined.
        $existing = $global:PpReleases | Where-Object tag_name -EQ (Arg '--tag')
        if ($existing -and -not $merge) { $global:LASTEXITCODE = 1; return }
        if (-not $existing) { New-PpRelease (Arg '--tag') $true $true }
        # The assets go up as vpk sends them, with a releases.<channel>.json of their versions,
        # types, hashes and sizes, as Velopack's VelopackAsset writes them.
        $channel = Arg '--channel'
        $store = Join-Path $scratch 'uploaded' (Arg '--tag')
        New-Item -ItemType Directory -Force $store | Out-Null
        $entries = foreach ($asset in @(Get-Content (Join-Path (Arg '--outputDir') "assets.$channel.json") -Raw | ConvertFrom-Json)) {
            Copy-Item -LiteralPath (Join-Path (Arg '--outputDir') $asset.RelativeFileName) $store -Force
            if ($asset.Type -in 'Full', 'Delta') {
                $file = Get-Item -LiteralPath (Join-Path $store $asset.RelativeFileName)
                [ordered]@{ PackageId = 'AgentBridgeApp'; Version = ($file.Name -replace '^AgentBridgeApp-(\d+\.\d+\.\d+).*-(full|delta)\.nupkg$', '$1'); Type = $asset.Type; FileName = $file.Name; SHA256 = (Get-FileHash -LiteralPath $file.FullName).Hash; Size = $file.Length }
            }
        }
        @{ Assets = @($entries) } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $store "releases.$channel.json")
        if ($global:PpTamper) { & $global:PpTamper $store $channel }
    } elseif ($a[0] -eq 'upload' -and $a[1] -eq 'local') {
        $channel = Arg '--channel'
        $global:PpCalls.Add("vpk upload local --channel $channel")
        $assets = @(Get-Content (Join-Path (Arg '--outputDir') "assets.$channel.json") -Raw | ConvertFrom-Json)
        $entries = @($assets | Where-Object { $_.Type -in 'Full', 'Delta' } | ForEach-Object {
                $file = Get-Item -LiteralPath (Join-Path (Arg '--outputDir') $_.RelativeFileName)
                [ordered]@{ PackageId = 'AgentBridgeApp'; Version = ($_.RelativeFileName -replace '^AgentBridgeApp-(\d+\.\d+\.\d+).*-(full|delta)\.nupkg$', '$1'); Type = $_.Type; FileName = $_.RelativeFileName; SHA256 = (Get-FileHash -LiteralPath $file.FullName).Hash; Size = $file.Length } })
        if ($global:PpFeedExtra) { $entries += @{ Version = '0.1.600'; Type = 'Full'; FileName = $global:PpFeedExtra } }
        @{ Assets = $entries } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path (Arg '--path') "releases.$channel.json")
        if ($global:PpTamper) { & $global:PpTamper (Arg '--path') $channel }
    } elseif ($a[0] -eq 'download' -and $a[1] -eq 'github') {
        $global:PpCalls.Add("vpk download github --channel $(Arg '--channel')")
        if ($global:PpDownload -eq 'fail') { $global:LASTEXITCODE = 1; return }
        if ($global:PpDownload) {
            New-Item -ItemType Directory -Force (Arg '-o') | Out-Null
            Set-Content (Join-Path (Arg '-o') (Get-PpFull (Arg '--channel') $global:PpDownload)) "full $($global:PpDownload)"
        }
    } elseif ($a[0] -eq 'delta' -and $a[1] -eq 'generate') {
        # A stand-in delta names the two versions it lies between.
        $global:PpCalls.Add('vpk delta generate')
        $from = (Get-Content (Arg '--base')) -replace '^full ', ''
        $to = (Split-Path (Arg '--new') -Leaf) -replace '^AgentBridgeApp-(\d+\.\d+\.\d+).*-full\.nupkg$', '$1'
        Set-Content (Arg '--output') "delta $from->$to"
    } elseif ($a[0] -eq 'delta' -and $a[1] -eq 'patch') {
        # Rebuilds the new package, byte for byte, only from the base the delta was made from.
        $base = (Get-Content (Arg '--base')) -replace '^full ', ''
        if ((Get-Content (Arg '--patch')) -notmatch '^delta (.+)->(.+)$' -or $Matches[1] -ne $base) { $global:LASTEXITCODE = 1; return }
        Copy-Item ((Arg '--patch') -replace '-delta\.nupkg$', '-full.nupkg') (Arg '--output')
    } elseif ($a[0] -eq 'pack') {
        $global:PpPackArgs = $a
        $global:PpPackSaw = @(Get-ChildItem (Arg '-o') -Filter *.nupkg -ErrorAction SilentlyContinue | ForEach-Object Name)
        $channel = Arg '-c'
        New-Item -ItemType Directory -Force (Arg '-o') | Out-Null
        # The pinned vpk emits pack-id download names, even with --packTitle Slipgates.
        $packedAssets = @(
            @{ RelativeFileName = "AgentBridgeApp-$channel-Setup.exe"; Type = 'Installer' }
            @{ RelativeFileName = "AgentBridgeApp-$channel-Portable.zip"; Type = 'Portable' }
            @{ RelativeFileName = (Get-PpFull $channel (Arg '-v')); Type = 'Full' }
        )
        ConvertTo-Json -InputObject $packedAssets | Set-Content (Join-Path (Arg '-o') "assets.$channel.json")
        if ($global:PpPackMakes) {
            $machine = if ((Arg '--runtime') -eq 'win-arm64') { 'arm64' } else { 'x64' }
            New-Package (Join-Path (Arg '-o') (Get-PpFull $channel (Arg '-v'))) 'dev' $machine
            New-PpExe (Join-Path (Arg '-o') "AgentBridgeApp-$channel-Setup.exe") $machine
            New-PpPortable (Join-Path (Arg '-o') "AgentBridgeApp-$channel-Portable.zip") $machine
        } else {
            foreach ($asset in $packedAssets | Where-Object Type -NE 'Full') {
                Set-Content (Join-Path (Arg '-o') $asset.RelativeFileName) "packed $($asset.Type)"
            }
        }
        foreach ($asset in $packedAssets | Where-Object Type -NE 'Full') {
            $global:PpPackHashes[$asset.Type] = (Get-FileHash (Join-Path (Arg '-o') $asset.RelativeFileName)).Hash
        }
    } else { throw "stand-in vpk: unexpected $($a -join ' ')" }
}

function global:dotnet {
    $a = @($args)
    $global:LASTEXITCODE = 0
    if ($a[0] -like '*agent-bridge-quality.dll') {
        $global:PpCalls.Add("selector $($a[1..($a.Count - 1)] -join ' ')")
        if ($global:PpSelect -eq 'fail') { $global:LASTEXITCODE = 1; return }
        return $global:PpSelect
    }
    $out = $a[[array]::IndexOf($a, '-o') + 1]
    $rid = $a[[array]::IndexOf($a, '-r') + 1]
    $global:PpPublishRids.Add($rid)
    # Executables for the runtime asked for, unless a test stands in for a build that ignored it.
    $machine = if ($global:PpBuildsFor) { $global:PpBuildsFor } else { @{ 'win-x64' = 'x64'; 'win-arm64' = 'arm64' }[$rid] }
    # And the bundled monospace and its licence, as the app's project copies them (Design/DesignKeys.cs).
    $fonts = [regex]::Matches((Get-Content (Join-Path $PSScriptRoot '..' 'src' 'AgentBridge.App' 'Design' 'DesignKeys.cs') -Raw), '"(Assets/Fonts/[^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '/', '\' }
    foreach ($f in @('AgentBridge.exe', 'AgentBridge.pri', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe', 'Assets\AgentBridge.ico', 'Assets\AgentBridge.png') + $fonts) {
        $path = Join-Path $out $f
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        if ($f -like '*.exe') { New-PpExe $path $machine } else { Set-Content $path 'stand-in' }
    }
    @{ libraries = @{
            'Notice.Expression/1.0.0' = @{ type = 'package' }
            'Notice.File/1.0.0' = @{ type = 'package' }
            'AgentBridge.App/0.1.600' = @{ type = 'project' }
        } } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'AgentBridge.deps.json')
    # The host project's build carries the release metadata exactly when asked for, unless a test
    # stands in for a build cache that ignored the property.
    if ($a[1] -like '*AgentBridge.Host.csproj') {
        $release = $a -contains '-p:AgentBridgeRelease=true' -and -not $global:PpIgnoreRelease
        Copy-Item $global:PpHost[$(if ($release) { 'release' } else { 'dev' })] (Join-Path $out 'agent-bridge-host.dll') -Force
    }
}

# --- Fixtures.
function Reset([int]$prereleases = 0) {
    $global:PpReleases.Clear(); $global:PpTags.Clear(); $global:PpCalls.Clear(); $global:PpPublishRids.Clear()
    $global:PpDownload = $null; $global:PpPackSaw = $null; $global:PpPackArgs = $null; $global:PpPackHashes = @{}; $global:PpIgnoreRelease = $false; $global:PpCompare = @()
    $global:PpBuildsFor = $null; $global:PpFeedExtra = $null; $global:PpPackMakes = $false; $global:PpTamper = $null; $global:PpSign = @{}
    $global:PpGeneratedNotes = "## What's Changed`n* A change (#200)`n`n**Full Changelog**: https://github.com/$repo/compare/v0.1.503...v0.1.600"
    $global:PpPublishedBody = $null
    $global:PpListFailures = 0; $global:PpListings = 0; $global:PpFailListAfter = -1; $global:PpDeleteFailures = 0; $global:PpDeleteGoesThrough = $false; $global:PpSleeps.Clear()
    $global:PpMain = @($ancestor, $commit, $descendant); $global:PpTagSha = @{}; $global:PpTagType = 'commit'; $global:PpTagRef = $null; $global:PpAncestry = $null
    $global:PpSelect = $null
    for ($i = 1; $i -le $prereleases; $i++) { New-PpRelease "v0.1.$(500 + $i)" }
    if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
}

function New-Package([string]$path, [string]$kind, [string]$machine) {
    # A full package as vpk packs it, as far as host-identity.ps1, publishing and the signing
    # checks read one: the host, the app's executables, Velopack's Update.exe (as Squirrel.exe)
    # and launcher stub, and a runtime file someone else signed, under lib/app.
    $stage = Join-Path $scratch 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    $app = Join-Path $stage 'lib' 'app'
    New-Item -ItemType Directory -Force $app | Out-Null
    Copy-Item $global:PpHost[$kind] (Join-Path $app 'agent-bridge-host.dll')
    foreach ($exe in 'AgentBridge.exe', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe', 'Squirrel.exe', 'Slipgates_ExecutionStub.exe', 'Microsoft.Runtime.dll') {
        New-PpExe (Join-Path $app $exe) $machine
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $path)
    Remove-Item $stage -Recurse -Force
}

function New-PpPortable([string]$path, [string]$machine) {
    # Velopack's portable zip: Update.exe and the launcher at its root, the app under current/.
    $stage = Join-Path $scratch 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $stage 'current') | Out-Null
    foreach ($exe in 'Update.exe', 'Slipgates.exe') { New-PpExe (Join-Path $stage $exe) $machine }
    foreach ($exe in 'AgentBridge.exe', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe') {
        New-PpExe (Join-Path $stage 'current' $exe) $machine
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $path)
    Remove-Item $stage -Recurse -Force
}

# x64's folder is out/app/releases, as before Arm64 builds; Arm64's is beside it.
function Get-PpDir([string]$channel = 'win') { Join-Path $scratch $(if ($channel -eq 'win') { 'releases' } else { "releases-$channel" }) }

function New-Folder([string]$version, [string]$DeltaFrom, [string[]]$Skip = @(), [switch]$DevBuild, [string]$Channel = 'win', [string]$Machine) {
    # As pack-app leaves it after normalizing public names: with -DeltaFrom, a delta and its base.
    if (-not $Machine) { $Machine = if ($Channel -eq 'win') { 'x64' } else { 'arm64' } }
    $suffix = if ($Channel -eq 'win') { '' } else { "-$Channel" }
    $dir = Get-PpDir $Channel
    New-Item -ItemType Directory -Force $dir | Out-Null
    $assets = [Collections.Generic.List[object]]::new()
    $assets.Add([ordered]@{ RelativeFileName = "Slipgates-$Channel-Setup.exe"; Type = 'Installer' })
    $assets.Add([ordered]@{ RelativeFileName = "Slipgates-$Channel-Portable.zip"; Type = 'Portable' })
    $assets.Add([ordered]@{ RelativeFileName = "AgentBridgeApp-$version$suffix-full.nupkg"; Type = 'Full' })
    if ($DeltaFrom) {
        $assets.Add([ordered]@{ RelativeFileName = "AgentBridgeApp-$version$suffix-delta.nupkg"; Type = 'Delta' })
        Set-Content (Join-Path $dir (Get-PpFull $Channel $DeltaFrom)) "full $DeltaFrom"
    }
    foreach ($a in $assets) {
        $path = Join-Path $dir $a.RelativeFileName
        if ($a.RelativeFileName -in $Skip) { continue }
        switch ($a.Type) {
            'Full' { New-Package $path $(if ($DevBuild) { 'dev' } else { 'release' }) $Machine }
            'Delta' { Set-Content $path "delta $DeltaFrom->$version" }
            'Installer' { New-PpExe $path $Machine }
            'Portable' { New-PpPortable $path $Machine }
            default { Set-Content $path 'stand-in' }
        }
    }
    ConvertTo-Json @($assets) -Compress | Set-Content (Join-Path $dir "assets.$Channel.json")
    $dir
}

# Both architectures' folders, as the publish job downloads them, unless -Only names one.
function Invoke-Publish([string]$version, [switch]$DryRun, [string]$DeltaFrom, [string[]]$Skip = @(), [switch]$DevBuild, [string[]]$Only = @('win', 'win-arm64'), [hashtable]$Machine = @{}, [switch]$Plan, [switch]$Sign, [string]$Head = $commit) {
    $dirs = @(foreach ($channel in $Only) { New-Folder $version -DeltaFrom $DeltaFrom -Skip $Skip -DevBuild:$DevBuild -Channel $channel -Machine $Machine[$channel] })
    # -Sign: as CI publishes once APP_SIGNING is on.
    $signing = if ($Sign) { @{ RequireSignature = $true; SignerEku = $global:PpEku } } else { @{} }
    # -Plan: what it says it does, rather than nothing. -Head: main's head as publish reads it.
    if ($Plan) { & $publishScript -Version $version -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $Head -MainRepo $scratch -DryRun:$DryRun @signing 6>&1 | Out-String }
    else { & $publishScript -Version $version -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $Head -MainRepo $scratch -DryRun:$DryRun @signing 6>$null }
}

function Get-Asset([string]$channel = 'win') { @(Get-Content (Join-Path (Get-PpDir $channel) "assets.$channel.json") -Raw | ConvertFrom-Json) }
function Get-Delta([string]$channel = 'win') {
    $name = "AgentBridgeApp-0.1.600$(if ($channel -ne 'win') { "-$channel" })-delta.nupkg"
    Get-Content (Join-Path (Get-PpDir $channel) $name) -ErrorAction SilentlyContinue
}

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Output "PASS $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Uploads { @($global:PpCalls | Where-Object { $_ -like 'vpk upload github*' }) }
function Deletes { @($global:PpCalls | Where-Object { $_ -like 'gh api -X DELETE*' }) }
function CallIndex([string]$like) { $global:PpCalls.FindIndex({ param($c) $c -like $like }) }

. (Join-Path $PSScriptRoot 'validate-halt-list.ps1')
foreach ($invalid in @(
    @{ Name = 'A string schema version is refused'; Json = '{"v":"1","halted":[]}' },
    @{ Name = 'A decimal schema version is refused'; Json = '{"v":1.0,"halted":[]}' },
    @{ Name = 'An extra halt policy property is refused'; Json = '{"v":1,"halted":[],"extra":false}' },
    @{ Name = 'A halted version with a trailing newline is refused'; Json = '{"v":1,"halted":["0.1.600\n"]}' },
    @{ Name = 'A halted version above Int64 MaxValue is refused'; Json = '{"v":1,"halted":["0.1.9223372036854775808"]}' }
)) {
    Check $invalid.Name {
        New-Item -ItemType Directory -Force $scratch | Out-Null
        $file = Join-Path $scratch 'invalid-halts.json'
        [IO.File]::WriteAllText($file, $invalid.Json)
        $threw = $false
        try { Assert-ReleaseHaltList $file } catch { $threw = $true }
        Assert $threw 'publisher accepted a policy that the client rejects'
    }
}

# --- Publishing.
Check 'A new version goes up as a draft, then is published as a pre-release of the commit with assets.win.json beside it' {
    Reset 3
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.503'
    Assert ($global:PpCalls -notcontains 'vpk delta generate') 'a delta from the latest release was rebuilt'
    Assert ((Get-Delta) -eq 'delta 0.1.503->0.1.600') "delta: $(Get-Delta)"
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
    Assert ([bool]($global:PpCalls | Where-Object { $_ -like 'gh release upload v0.1.600 *assets.win.json*' })) 'assets.win.json was not uploaded'
    $haltUpload = CallIndex 'gh release upload v0.1.600 *halts.json*'
    Assert ($haltUpload -ge 0 -and $haltUpload -lt (CallIndex 'gh api -X PATCH*')) 'the shared halt policy must be uploaded before publication'
    $r = $global:PpReleases | Where-Object tag_name -EQ 'v0.1.600'
    Assert ($r -and -not $r.draft -and $r.prerelease) "release: $($r | ConvertTo-Json -Compress)"
    Assert ($global:PpPublishedBody.Contains($commit)) 'the notes do not name the commit'
}

Check 'Every generated change is published even when the notes exceed the Windows command-line limit' {
    Reset 3
    $global:PpGeneratedNotes = "## What's Changed`n" + ((1..250 | ForEach-Object { "* Change $_ $('a useful change ' * 10) (#$_)" }) -join "`n") +
        "`n`n**Full Changelog**: https://github.com/$repo/compare/v0.1.503...v0.1.600"
    Invoke-Publish '0.1.600'
    Assert ($global:PpPublishedBody.Contains($global:PpGeneratedNotes)) 'the complete generated notes were not published'
    Assert ($global:PpPublishedBody -like '*## Installation and updates*') 'installation guidance was lost'
    Assert ((CallIndex '*releases/generate-notes*') -lt (CallIndex 'vpk upload github*')) 'notes were generated after uploading'
}

Check 'Release notes compare the previous published version to this exact commit, not a draft or an older release' {
    Reset 3
    New-PpRelease 'v0.1.100' $false
    New-PpRelease 'v0.1.601' $true $true
    Invoke-Publish '0.1.600'
    $generate = $global:PpCalls | Where-Object { $_ -like '*releases/generate-notes*' }
    Assert ($generate -like "*tag_name=v0.1.600 *target_commitish=$commit *previous_tag_name=v0.1.503") "generation: $generate"
    Reset
    Invoke-Publish '0.1.600'
    $generate = $global:PpCalls | Where-Object { $_ -like '*releases/generate-notes*' }
    Assert ($generate -notlike '*previous_tag_name*') "the first release had an invented predecessor: $generate"
}

Check 'Failed or empty generated notes leave releases unpublished and existing drafts untouched' {
    foreach ($result in 'fail', '') {
        Reset 3
        New-PpRelease 'v0.1.600' $true $true
        $global:PpGeneratedNotes = $result
        $threw = $false
        try { Invoke-Publish '0.1.600' } catch { $threw = $true }
        Assert $threw 'failed notes were replaced by installation boilerplate'
        Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) 'a draft was uploaded or deleted before notes were available'
        Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X PATCH*' })) 'a release was published without notes'
    }
}

Check 'x64 goes up exactly as before Arm64 builds: the win channel, creating the draft, under the names installed copies read' {
    # Existing x64 copies read releases.win.json: its channel, package names and the upload
    # that creates the release must not move. Only public Setup/Portable names change.
    Reset 3
    $plan = Invoke-Publish '0.1.600' -DeltaFrom '0.1.503' -Plan
    Assert (@(Uploads)[0] -eq "vpk upload github --channel win --tag v0.1.600 --targetCommitish $commit pre=True publish=False merge=False") "upload: $(Uploads)"
    $x64 = 'Slipgates-win-Setup.exe, Slipgates-win-Portable.zip, AgentBridgeApp-0.1.600-full.nupkg, AgentBridgeApp-0.1.600-delta.nupkg, releases.win.json, RELEASES, assets.win.json'
    Assert ($plan -match "v0\.1\.600 win \(win-x64, release build\) from $commit, .*: $([regex]::Escape($x64))\r?\n") "plan: $plan"
    Assert (-not (@(Get-Asset 'win').RelativeFileName -match 'arm64')) "x64's assets: $(@(Get-Asset 'win').RelativeFileName)"
}

Check 'Arm64 joins the same draft in its own channel, and the release is published only once both are up' {
    Reset 3
    $plan = Invoke-Publish '0.1.600' -DeltaFrom '0.1.503' -Plan
    Assert (@(Uploads)[1] -eq "vpk upload github --channel win-arm64 --tag v0.1.600 --targetCommitish $commit pre=True publish=False merge=True") "upload: $(Uploads)"
    $arm64 = 'Slipgates-win-arm64-Setup.exe, Slipgates-win-arm64-Portable.zip, AgentBridgeApp-0.1.600-win-arm64-full.nupkg, AgentBridgeApp-0.1.600-win-arm64-delta.nupkg, releases.win-arm64.json, assets.win-arm64.json'
    Assert ($plan -match "v0\.1\.600 win-arm64 \(win-arm64, release build\) from $commit, .*: $([regex]::Escape($arm64))\r?\n") "plan: $plan"
    Assert ((Get-Delta 'win-arm64') -eq 'delta 0.1.503->0.1.600') "arm64 delta: $(Get-Delta 'win-arm64')"
    Assert (@($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600').Count -eq 1) 'not exactly one v0.1.600'
    $published = CallIndex 'gh api -X PATCH*'
    Assert ($published -gt (CallIndex '*--channel win-arm64*merge=True') -and $published -gt (CallIndex 'gh release upload v0.1.600 *assets.win-arm64.json*')) "calls: $($global:PpCalls -join '; ')"
    $notes = $global:PpPublishedBody
    Assert ($notes -like '*Slipgates-win-Setup.exe on an x64 PC*' -and $notes -like '*Slipgates-win-arm64-Setup.exe on a Windows on Arm PC*') "notes: $notes"
}

Check 'A release without the Arm64 folder is refused before anything is uploaded' {
    Reset 3
    $threw = $false
    try { Invoke-Publish '0.1.600' -Only 'win' } catch { $threw = $_.Exception.Message -like '*no folder for win-arm64*' }
    Assert $threw 'a release without Arm64 was not refused'
    Assert ($global:PpCalls.Count -eq 0) "calls: $($global:PpCalls -join '; ')"
}

Check "An Arm64 package in x64's channel is refused before anything is uploaded" {
    Reset 3
    $threw = $false
    try { Invoke-Publish '0.1.600' -Machine @{ win = 'arm64' } } catch { $threw = $_.Exception.Message -like '*built for arm64; the win channel is x64*' }
    Assert $threw 'an Arm64 package in the win channel was not refused'
    Assert ($global:PpCalls.Count -eq 0) "calls: $($global:PpCalls -join '; ')"
    Reset 3
    $threw = $false
    try { Invoke-Publish '0.1.600' -DryRun -Only 'win-arm64' -Machine @{ 'win-arm64' = 'x64' } } catch { $threw = $_.Exception.Message -like '*built for x64; the win-arm64 channel is arm64*' }
    Assert $threw 'an x64 package in the win-arm64 channel was not refused, even in a dry run'
}

Check 'A version that is already tagged is not uploaded again' {
    Reset 3
    New-PpRelease 'v0.1.600'
    Invoke-Publish '0.1.600'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

Check 'A version older than a published release is not uploaded' {
    Reset 3
    New-PpRelease 'v0.1.601'
    Invoke-Publish '0.1.600'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
    Assert (-not ($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600')) 'an older release was created'
}

Check "A higher version built from an ancestor of the latest release's commit is not published: its run came later" {
    # Reversed order: the descendant's run (0.1.503) published first; the ancestor's run, created
    # after it, has the higher number. Under the lock, compare says which commit holds the other.
    Reset 3
    $global:PpTagSha['v0.1.503'] = $descendant
    $plan = Invoke-Publish '0.1.600' -Plan
    Assert ($global:PpCalls -contains "gh api repos/$repo/git/ref/tags/v0.1.503 --jq .object.type, .object.sha") "calls: $($global:PpCalls -join '; ')"
    Assert ($global:PpCalls -contains "gh api repos/$repo/compare/$descendant...$commit --jq .status") "calls: $($global:PpCalls -join '; ')"
    Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) "uploads: $(Uploads); deletes: $(Deletes)"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "writes: $($global:PpCalls -join '; ')"
    Assert (-not ($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600')) 'an ancestor was released over its descendant'
    Assert ($plan -like "*$commit is behind v0.1.503's commit $descendant*") "plan: $plan"
}

Check "A version from the latest release's commit or a descendant of it is published" {
    foreach ($tagged in $ancestor, $commit) {
        Reset 3
        $global:PpTagSha['v0.1.503'] = $tagged
        Invoke-Publish '0.1.600'
        Assert (@(Uploads).Count -eq 2) "tagged on $tagged; uploads: $(Uploads)"
        $read = CallIndex "gh api repos/$repo/compare/$tagged...$commit*"
        Assert ($read -ge 0 -and $read -lt (CallIndex 'vpk upload github*')) "ancestry wasn't read before uploading: $($global:PpCalls -join '; ')"
    }
}

Check "A commit off the latest release's line, or a tag or comparison that can't be read, is not published" {
    foreach ($case in @(
        @{ Name = 'diverged'; Set = { $global:PpTagSha['v0.1.503'] = 'abababababababababababababababababababab' } },
        @{ Name = 'an unknown comparison'; Set = { $global:PpAncestry = 'unknown' } },
        @{ Name = 'an unreadable comparison'; Set = { $global:PpAncestry = 'fail' } },
        @{ Name = 'an unreadable tag'; Set = { $global:PpTagRef = 'fail' } },
        @{ Name = 'an annotated tag'; Set = { $global:PpTagType = 'tag' } },
        @{ Name = 'a tag on no commit'; Set = { $global:PpTagSha['v0.1.503'] = '' } }
    )) {
        Reset 3
        New-PpRelease 'v0.1.600' $true $true
        & $case.Set
        $threw = $false
        try { Invoke-Publish '0.1.600' } catch { $threw = $true }
        Assert $threw "$($case.Name) was published over"
        Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) "$($case.Name): uploads: $(Uploads); deletes: $(Deletes)"
        Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "$($case.Name): writes: $($global:PpCalls -join '; ')"
    }
}

$appTail = '{"mode":"partial","jobs":{"ci.yml":{"app":["app-windows"],"product":["product"]}}}'
Check "An ancestor whose run publishes first stands aside when main has changed the app since, and the descendant's lower version publishes both" {
    # S -> A -> B: B's run was created first (0.1.600), A's after it (0.1.601). Were A out first,
    # B's lower version would be refused and B's app changes never released (#938's review).
    Reset 3
    $global:PpSelect = $appTail
    $plan = Invoke-Publish '0.1.601' -Head $descendant -Plan
    Assert ($global:PpCalls -contains "gh api repos/$repo/compare/$commit...$descendant --jq .status") "calls: $($global:PpCalls -join '; ')"
    Assert ($global:PpCalls -contains "selector select --repo $scratch --ring 1 --base $commit --head $descendant") "calls: $($global:PpCalls -join '; ')"
    Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) "uploads: $(Uploads); deletes: $(Deletes)"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "writes: $($global:PpCalls -join '; ')"
    Assert ($plan -like "*main has moved on to $descendant, which changes the app since $commit*") "plan: $plan"
    # Then B's run, main's head, publishes its lower version over S's release.
    Remove-Item (Get-PpDir 'win'), (Get-PpDir 'win-arm64') -Recurse -Force
    $dirs = @(New-Folder '0.1.600'; New-Folder '0.1.600' -Channel 'win-arm64')
    & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $descendant -MainHead $descendant -MainRepo $scratch 6>$null
    Assert (@(Uploads).Count -eq 2 -and -not (Uploads | Where-Object { $_ -notlike "*--tag v0.1.600 --targetCommitish $descendant *" })) "uploads: $(Uploads)"
    Assert (-not ($global:PpReleases | Where-Object tag_name -EQ 'v0.1.601')) 'the ancestor was released'
}

Check "A commit whose later commits on main leave the app alone, docs only say, still publishes" {
    foreach ($select in '{"mode":"partial","jobs":{"ci.yml":{"light":["docs"]}}}', '{"mode":"docs-only","jobs":{}}') {
        Reset 3
        $global:PpSelect = $select
        Invoke-Publish '0.1.600' -Head $descendant
        Assert (@(Uploads).Count -eq 2) "$select; uploads: $(Uploads)"
        $read = CallIndex 'selector select*'
        Assert ($read -ge 0 -and $read -lt (CallIndex 'vpk upload github*')) "$select; main's head wasn't read before uploading: $($global:PpCalls -join '; ')"
    }
    # And main's head being the commit itself needs no selector.
    Reset 3
    $global:PpSelect = $appTail
    Invoke-Publish '0.1.600'
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'selector*' -or $_ -like "gh api repos/$repo/compare/$commit...*" })) "calls: $($global:PpCalls -join '; ')"
}

Check "A main head that isn't after the commit, or a selector answer that can't be read, stops publishing" {
    # With no release (0), only main's head is compared: the release ancestry check has nothing to read.
    # A compare that can't be read is refused by Invoke-Gh, as gh's nonzero exit, naming that read.
    $unreadable = "gh api repos/$repo/compare/$commit...$descendant failed (1)"
    foreach ($case in @(
        @{ Name = 'a diverged head'; Head = 'abababababababababababababababababababab'; Releases = 3; Selects = $false; Set = {} },
        @{ Name = 'a head behind the commit'; Head = $ancestor; Releases = 3; Selects = $false; Set = {} },
        @{ Name = 'an unknown head comparison'; Head = $descendant; Releases = 0; Selects = $false; Set = { $global:PpAncestry = 'unknown' } },
        @{ Name = 'an unreadable head comparison'; Head = $descendant; Releases = 0; Selects = $false; Set = { $global:PpAncestry = 'fail' }; Says = $unreadable },
        @{ Name = 'a failing selector'; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = 'fail' } },
        @{ Name = 'no selector output'; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = $null } },
        @{ Name = 'malformed selector output'; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = '{"jobs":' } },
        @{ Name = 'selector output without jobs'; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = '{"mode":"partial"}' } },
        @{ Name = "selector output whose ci.yml jobs aren't a map"; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = '{"jobs":{"ci.yml":["app"]}}' } }
    )) {
        Reset $case.Releases
        $global:PpSelect = $appTail
        & $case.Set
        $said = $null
        try { Invoke-Publish '0.1.600' -Head $case.Head } catch { $said = $_.Exception.Message }
        $says = if ($case.Contains('Says')) { $case.Says } else { '*not publishing v0.1.600*' }
        Assert ($said -clike $says) "$($case.Name) was published past (said: '$said')"
        Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) "$($case.Name): uploads: $(Uploads); deletes: $(Deletes)"
        Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "$($case.Name): writes: $($global:PpCalls -join '; ')"
        Assert ($case.Selects -eq [bool]($global:PpCalls | Where-Object { $_ -like 'selector*' })) "$($case.Name): calls: $($global:PpCalls -join '; ')"
    }
}

Check "Publishing without main's head, or with one that isn't a commit, is refused before anything is read" {
    foreach ($case in @(@{ Name = 'no head'; Args = @{ MainRepo = $scratch } }, @{ Name = 'a branch name'; Args = @{ MainHead = 'main'; MainRepo = $scratch } }, @{ Name = 'no clone'; Args = @{ MainHead = $descendant } })) {
        Reset 3
        $dirs = @(New-Folder '0.1.600'; New-Folder '0.1.600' -Channel 'win-arm64')
        $named = $case.Args
        $threw = $false
        try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit @named 6>$null } catch { $threw = $_.Exception.Message -like '*publishing needs -MainHead*' }
        Assert $threw "$($case.Name) was not refused"
        Assert ($global:PpCalls.Count -eq 0) "$($case.Name): calls: $($global:PpCalls -join '; ')"
    }
}

Check "Only a run that uploads the release tells CI's attest job so, never a re-run or a dry run" {
    # attest signs the release's assets with this run's SBOMs; after a re-run rebuilt the app,
    # those would not describe what is out.
    $out = Join-Path $scratch '..' 'test-publish-prerelease-output'
    function Get-Said([scriptblock]$publish) {
        Reset 3
        Set-Content $out ''
        $env:GITHUB_OUTPUT = $out
        try { & $publish } finally { Remove-Item Env:GITHUB_OUTPUT }
        (Get-Content $out -Raw).Trim()
    }
    $said = Get-Said { Invoke-Publish '0.1.600' }
    Assert (($said -replace "`r", '') -ceq "published=true`nmanifest-tag=v0.1.600") "a run that published said: '$said'"
    $said = Get-Said { New-PpRelease 'v0.1.600'; Invoke-Publish '0.1.600' }
    Assert ($said -eq 'manifest-tag=v0.1.600') "a re-run must repair the manifest without attesting rebuilt packages: '$said'"
    $said = Get-Said { New-PpRelease 'v0.1.600'; Invoke-Publish '0.1.600' -DryRun }
    Assert ($said -eq '') "a dry run of a published version must not request a manifest upload: '$said'"
    $said = Get-Said { New-PpRelease 'v0.1.601'; Invoke-Publish '0.1.600' }
    Assert ($said -eq '') "an older version said: '$said'"
    $said = Get-Said { New-PpRelease 'v0.1.600' $false; Invoke-Publish '0.1.600' }
    Assert ($said -eq '') "a stable release must not become a Beta manifest: '$said'"
    $said = Get-Said { $global:PpTags.Add('refs/tags/v0.1.600'); Invoke-Publish '0.1.600' }
    Assert ($said -eq '') "a tag without a published release must not request a manifest: '$said'"
    $said = Get-Said { Invoke-Publish '0.1.600' -DryRun }
    Assert ($said -eq '') "a dry run said: '$said'"
    Remove-Item $out
}

Check 'A draft left by a failed attempt is deleted and the version uploaded again' {
    Reset 3
    New-PpRelease 'v0.1.600' $true $true
    $leftover = $global:PpReleases | Where-Object tag_name -EQ 'v0.1.600'
    Invoke-Publish '0.1.600'
    Assert (@(Deletes) -contains "gh api -X DELETE repos/$repo/releases/$($leftover.id)") "deletes: $(Deletes)"
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
    Assert (@($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600').Count -eq 1) 'not exactly one v0.1.600'
}

Check 'A newer draft does not stop a version being published' {
    Reset 3
    New-PpRelease 'v0.1.601' $true $true
    Invoke-Publish '0.1.600'
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
}

Check 'A folder without its Setup.exe is refused before anything is uploaded' {
    Reset 3
    $threw = $false
    try { Invoke-Publish '0.1.600' -Skip 'Slipgates-win-Setup.exe' } catch { $threw = $_.Exception.Message -like '*Slipgates-win-Setup.exe*' }
    Assert $threw 'the missing Setup.exe was not refused'
    Assert ($global:PpCalls.Count -eq 0) "calls: $($global:PpCalls -join '; ')"
}

Check "A folder of another version's packages is refused" {
    Reset 3
    $threw = $false
    $dir = New-Folder '0.1.599'
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dir -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch 6>$null } catch { $threw = $true }
    Assert $threw 'a 0.1.599 folder was published as 0.1.600'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

# --- Only a release build goes out.
Check 'A dev build of the host is refused before anything is uploaded' {
    Reset 3
    $threw = $false
    try { Invoke-Publish '0.1.600' -DevBuild } catch { $threw = $_.Exception.Message -like '*dev build*' }
    Assert $threw 'a dev build was not refused'
    Assert ($global:PpCalls.Count -eq 0) "calls: $($global:PpCalls -join '; ')"
}

Check "A pull request's dry run of a dev build says so and writes nothing" {
    Reset 3
    $dir = New-Folder '0.1.600' -DevBuild -DeltaFrom '0.1.503'
    $plan = & $publishScript -Version '0.1.600' -ReleasesDir $dir -Repository $repo -Commit $commit -DryRun 6>&1 | Out-String
    Assert ($plan -match 'the host is a dev build') "plan: $plan"
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

# --- The delta: from the release installed copies are on, or none.
Check 'A delta from an older release than the latest is rebuilt from the latest before upload' {
    Reset 3
    $global:PpDownload = '0.1.503'
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.501'
    Assert ((Get-Delta) -eq 'delta 0.1.503->0.1.600') "delta: $(Get-Delta)"
    Assert ([bool](Get-Asset | Where-Object Type -EQ 'Delta')) 'the delta is not in assets.win.json'
    Assert ((Get-Delta 'win-arm64') -eq 'delta 0.1.503->0.1.600') "arm64 delta: $(Get-Delta 'win-arm64')"
    Assert ([bool](Get-Asset 'win-arm64' | Where-Object Type -EQ 'Delta')) 'the delta is not in assets.win-arm64.json'
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
}

Check "Each architecture's delta base is the latest release's package of its own channel" {
    Reset 3
    $global:PpDownload = '0.1.503'
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.501'
    $downloads = @($global:PpCalls | Where-Object { $_ -like 'vpk download github*' })
    Assert (($downloads -join '; ') -eq 'vpk download github --channel win; vpk download github --channel win-arm64') "downloads: $($downloads -join '; ')"
}

Check "An Arm64 delta is left out when the latest release has no Arm64 package, and x64's is unaffected" {
    # The first release with Arm64 in it: the one before has only x64's.
    Reset 3
    New-Folder '0.1.600' -DeltaFrom '0.1.503' | Out-Null
    New-Folder '0.1.600' -Channel 'win-arm64' | Out-Null
    $global:PpDownload = 'fail'
    & $publishScript -Version '0.1.600' -ReleasesDir (Get-PpDir 'win'), (Get-PpDir 'win-arm64') -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch 6>$null
    Assert ((Get-Delta) -eq 'delta 0.1.503->0.1.600') "x64 delta: $(Get-Delta)"
    Assert ($null -eq (Get-Delta 'win-arm64')) "arm64 delta: $(Get-Delta 'win-arm64')"
    Assert (-not (Get-Asset 'win-arm64' | Where-Object Type -EQ 'Delta')) 'assets.win-arm64.json lists a delta'
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
}

Check 'A delta that cannot be rebuilt is left out and the full package published alone' {
    Reset 3
    $global:PpDownload = 'fail'
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.501'
    Assert ($null -eq (Get-Delta)) "delta: $(Get-Delta)"
    Assert (-not (Get-Asset | Where-Object Type -EQ 'Delta')) 'assets.win.json still lists the delta'
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
}

Check 'A package packed without a delta gets one from the latest release' {
    Reset 3
    $global:PpDownload = '0.1.503'
    Invoke-Publish '0.1.600'
    Assert ((Get-Delta) -eq 'delta 0.1.503->0.1.600') "delta: $(Get-Delta)"
    Assert ([bool](Get-Asset | Where-Object Type -EQ 'Delta')) 'the delta is not in assets.win.json'
}

# --- Keeping the newest twenty.
Check 'Only pre-releases beyond the newest twenty are deleted, never a full release, a draft or a tag' {
    Reset 22
    New-PpRelease 'v0.1.100' $false
    New-PpRelease 'v0.1.400' $true $true
    New-PpRelease 'nightly' $true
    $tagsBefore = @($global:PpTags)
    Invoke-Publish '0.1.600'
    $left = @($global:PpReleases | ForEach-Object tag_name)
    Assert (@(Deletes).Count -eq 3) "deletes: $(Deletes)"
    foreach ($gone in 'v0.1.501', 'v0.1.502', 'v0.1.503') { Assert ($gone -notin $left) "$gone was kept" }
    foreach ($kept in 'v0.1.504', 'v0.1.522', 'v0.1.600', 'v0.1.100', 'v0.1.400', 'nightly') { Assert ($kept -in $left) "$kept was deleted" }
    foreach ($t in $tagsBefore) { Assert ($t -in $global:PpTags) "the tag $t was deleted" }
    Assert (-not ($global:PpCalls | Where-Object { $_ -like '*git/refs*' })) 'a ref was changed'
}

# --- GitHub failing a call (#907): twice on 2026-11-05 that alone turned main red.
Check 'A prune that GitHub fails once is tried again and the old pre-release is deleted' {
    Reset 20
    $global:PpDeleteFailures = 1
    Invoke-Publish '0.1.600'
    Assert ('v0.1.501' -notin @($global:PpReleases | ForEach-Object tag_name)) 'v0.1.501 was kept'
    Assert (@(Deletes).Count -eq 2) "deletes: $(Deletes)"
    Assert (@($global:PpSleeps) -join ',' -eq '2') "waits: $($global:PpSleeps -join ',')"
}

Check 'A prune DELETE that failed but went through counts as done' {
    Reset 20
    $global:PpDeleteFailures = 1; $global:PpDeleteGoesThrough = $true
    Invoke-Publish '0.1.600'
    Assert ('v0.1.501' -notin @($global:PpReleases | ForEach-Object tag_name)) 'v0.1.501 was kept'
}

Check 'A prune that keeps failing after a successful publish is a warning, not a failed publish' {
    Reset 20
    $global:PpDeleteFailures = 99
    $plan = Invoke-Publish '0.1.600' -Plan
    Assert ($global:LASTEXITCODE -eq 0) "the step would exit $global:LASTEXITCODE"
    $r = $global:PpReleases | Where-Object tag_name -EQ 'v0.1.600'
    Assert ($r -and -not $r.draft) 'v0.1.600 was not published'
    Assert ('v0.1.501' -in @($global:PpReleases | ForEach-Object tag_name)) 'v0.1.501 is gone'
    Assert ($plan -like '*v0.1.501 was not deleted*') "plan: $plan"
    Assert ($plan -like '*1 left for the next publish*') "plan: $plan"
}

Check 'Listing releases to prune that keeps failing after a successful publish is a warning, not a failed publish' {
    # How many listings a publish makes; the last is pruning's. Every one from that on fails.
    Reset 21
    Invoke-Publish '0.1.600'
    $listings = @($global:PpCalls | Where-Object { $_ -like "gh api repos/$repo/releases?*" }).Count
    Reset 21
    $global:PpFailListAfter = $listings - 1
    $plan = Invoke-Publish '0.1.600' -Plan
    Assert ($global:LASTEXITCODE -eq 0) "the step would exit $global:LASTEXITCODE"
    $r = $global:PpReleases | Where-Object tag_name -EQ 'v0.1.600'
    Assert ($r -and -not $r.draft) 'v0.1.600 was not published'
    Assert ($plan -like '*old pre-releases were not pruned*') "plan: $plan"
    Assert (@(Deletes).Count -eq 0) "deletes: $(Deletes)"
}

Check 'A leftover draft whose DELETE failed but went through does not stop the version being published' {
    Reset 3
    New-PpRelease 'v0.1.600' $true $true
    $global:PpDeleteFailures = 1; $global:PpDeleteGoesThrough = $true
    Invoke-Publish '0.1.600'
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
    Assert (@($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600').Count -eq 1) 'not exactly one v0.1.600'
}

Check 'A leftover draft that cannot be deleted still stops the publish before anything is uploaded' {
    Reset 3
    New-PpRelease 'v0.1.600' $true $true
    $global:PpDeleteFailures = 99
    $threw = $false
    try { Invoke-Publish '0.1.600' } catch { $threw = $true }
    Assert $threw 'the publish went on past a draft it could not delete'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

Check 'A dry run uploads, publishes and deletes nothing, and has vpk read the folder' {
    Reset 22
    Invoke-Publish '0.1.600' -DryRun -DeltaFrom '0.1.522'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
    Assert (@(Deletes).Count -eq 0) "deletes: $(Deletes)"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "writes: $($global:PpCalls -join '; ')"
    Assert ($global:PpCalls -contains 'vpk upload local --channel win') 'vpk did not read the x64 folder'
    Assert ($global:PpCalls -contains 'vpk upload local --channel win-arm64') 'vpk did not read the Arm64 folder'
    Assert ($global:PpReleases.Count -eq 22) 'the releases changed'
}

Check "A dry run fails when vpk's feed for a channel lists another channel's package" {
    Reset 3
    $global:PpFeedExtra = 'AgentBridgeApp-0.1.600-win-arm64-full.nupkg'
    $threw = $false
    try { Invoke-Publish '0.1.600' -DryRun -Only 'win' } catch { $threw = $_.Exception.Message -like "*win feed is not this channel's 0.1.600*releases.win.json lists AgentBridgeApp-0.1.600-win-arm64-full.nupkg, which is not this release's*" }
    Assert $threw 'an Arm64 package in the x64 feed was not caught'
}

Check 'Each app job dry-runs only its own folder' {
    Reset 3
    foreach ($channel in 'win', 'win-arm64') {
        $global:PpCalls.Clear()
        Invoke-Publish '0.1.600' -DryRun -Only $channel
        Assert ((@($global:PpCalls | Where-Object { $_ -like 'vpk upload local*' }) -join '; ') -eq "vpk upload local --channel $channel") "calls: $($global:PpCalls -join '; ')"
    }
}

# --- Signing (#734): with -RequireSignature, every file the pinned publisher's, before anything
# goes up and again in the draft, downloaded back, before it is published.
function Published { [bool]($global:PpReleases | Where-Object { $_.tag_name -eq 'v0.1.600' -and -not $_.draft }) }

Check 'A signed release is checked, uploaded as a draft, downloaded back and checked again, then published naming its signer' {
    Reset 3
    $global:PpSign = @{ 'Microsoft.Runtime.dll' = 'microsoft' }
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.503' -Sign
    Assert (Published) 'not published'
    $downloads = @($global:PpCalls | Where-Object { $_ -like 'gh release download v0.1.600 *' })
    Assert ($downloads.Count -eq 2) "downloads: $($downloads -join '; ')"
    Assert ($downloads[0] -like '*-p AgentBridgeApp-0.1.600-full.nupkg*-p Slipgates-win-Setup.exe*-p Slipgates-win-Portable.zip*-p AgentBridgeApp-0.1.600-delta.nupkg*-p releases.win.json*') "download: $($downloads[0])"
    Assert ((CallIndex 'gh release download*') -lt (CallIndex 'gh api -X PATCH*')) "calls: $($global:PpCalls -join '; ')"
    Assert ($global:PpPublishedBody -like '*Signed by CN=Agent Bridge Test Publisher with Azure Artifact Signing*SmartScreen may still warn*') "notes: $global:PpPublishedBody"
    Assert ($global:PpPublishedBody -notlike '*Unsigned*') "notes: $global:PpPublishedBody"
}

foreach ($case in @(
        @{ Name = 'an unsigned NativeAOT injector'; Sign = @{ 'agent-bridge-injector.exe' = 'unsigned' }; Says = '*agent-bridge-injector.exe: NotSigned*' }
        @{ Name = "Velopack's Update.exe signed by another publisher"; Sign = @{ 'Squirrel.exe' = 'other' }; Says = '*Squirrel.exe: signed by CN=Someone Else, not the pinned publisher*' }
        @{ Name = 'a Setup.exe without a timestamp'; Sign = @{ 'Slipgates-win-arm64-Setup.exe' = 'notimestamp' }; Says = '*Slipgates-win-arm64-Setup.exe: no timestamp*' }
        @{ Name = 'a launcher stub whose certificate is not Public Trust'; Sign = @{ 'Slipgates_ExecutionStub.exe' = 'nopublictrust' }; Says = '*Slipgates_ExecutionStub.exe: the signer has no 1.3.6.1.4.1.311.97.1.0 EKU*' }
        @{ Name = 'an unsigned runtime file'; Sign = @{ 'Microsoft.Runtime.dll' = 'unsigned' }; Says = '*Microsoft.Runtime.dll: NotSigned*' }
        @{ Name = 'a portable launcher signed by another publisher'; Sign = @{ 'Slipgates.exe' = 'other' }; Says = '*Portable.zip Slipgates.exe: signed by CN=Someone Else*' }
    )) {
    Check "A release with $($case.Name) uploads nothing when signing is required" {
        Reset 3
        $global:PpSign = $case.Sign
        $threw = $false
        try { Invoke-Publish '0.1.600' -Sign } catch { $threw = $_.Exception.Message -like "*not signed as it must be; nothing was published*$($case.Says.TrimStart('*'))" ; if (-not $threw) { throw "message: $($_.Exception.Message)" } }
        Assert $threw 'it did not refuse'
        Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
        Assert (-not (Published)) 'published'
    }
}

Check 'A package missing an executable is refused, not passed for having nothing to check' {
    Reset 3
    $global:PpSign = @{}
    $dirs = @(foreach ($channel in 'win', 'win-arm64') { New-Folder '0.1.600' -Channel $channel })
    # Repack x64's full package without the hook.
    $full = Join-Path $dirs[0] (Get-PpFull 'win' '0.1.600')
    $zip = [IO.Compression.ZipFile]::Open($full, 'Update')
    $zip.GetEntry('lib/app/agent-bridge-host-hook.exe').Delete()
    $zip.Dispose()
    $threw = $false
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch -RequireSignature -SignerEku $global:PpEku 6>$null } catch { $threw = $_.Exception.Message -like "*has no lib$([IO.Path]::DirectorySeparatorChar)app$([IO.Path]::DirectorySeparatorChar)agent-bridge-host-hook.exe*" }
    Assert $threw 'a package without the hook passed'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

Check 'A portable zip without its launcher is refused' {
    Reset 3
    $global:PpSign = @{}
    $dirs = @(foreach ($channel in 'win', 'win-arm64') { New-Folder '0.1.600' -Channel $channel })
    $portable = Join-Path $dirs[1] 'Slipgates-win-arm64-Portable.zip'
    $zip = [IO.Compression.ZipFile]::Open($portable, 'Update')
    $zip.GetEntry('Slipgates.exe').Delete()
    $zip.Dispose()
    $threw = $false
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch -RequireSignature -SignerEku $global:PpEku 6>$null } catch { $threw = $_.Exception.Message -like '*Slipgates-win-arm64-Portable.zip has no Slipgates.exe*' }
    Assert $threw 'a portable zip without its launcher passed'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

# What vpk 1.2.161 packs, exactly: a signed executable where it puts none is still refused.
foreach ($case in @(
        @{ Name = 'a portable zip with another executable at its root'; File = 'Slipgates-win-arm64-Portable.zip'; Entry = 'Helper.exe'; Says = '*Slipgates-win-arm64-Portable.zip has Helper.exe at its root, which vpk does not pack*' }
        @{ Name = 'a package with a second launcher stub'; File = 'AgentBridgeApp-0.1.600-win-arm64-full.nupkg'; Entry = 'lib/app/Other_ExecutionStub.exe'; Says = '*AgentBridgeApp-0.1.600-win-arm64-full.nupkg has a launcher stub Other_ExecutionStub.exe, not only Slipgates_ExecutionStub.exe*' }
    )) {
    Check "$($case.Name) is refused" {
        Reset 3
        $global:PpSign = @{}
        $dirs = @(foreach ($channel in 'win', 'win-arm64') { New-Folder '0.1.600' -Channel $channel })
        $extra = Join-Path $scratch 'extra.exe'
        New-PpExe $extra 'arm64'
        $zip = [IO.Compression.ZipFile]::Open((Join-Path $dirs[1] $case.File), 'Update')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $extra, $case.Entry)
        $zip.Dispose()
        $threw = $false
        try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch -RequireSignature -SignerEku $global:PpEku 6>$null } catch { $threw = $_.Exception.Message -like $case.Says; if (-not $threw) { throw "message: $($_.Exception.Message)" } }
        Assert $threw 'it passed'
        Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
    }
}

Check 'A package whose launcher stub is not named after the pack title is refused' {
    Reset 3
    $global:PpSign = @{}
    $dirs = @(foreach ($channel in 'win', 'win-arm64') { New-Folder '0.1.600' -Channel $channel })
    $extra = Join-Path $scratch 'extra.exe'
    New-PpExe $extra 'x64'
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $dirs[0] 'AgentBridgeApp-0.1.600-full.nupkg'), 'Update')
    $zip.GetEntry('lib/app/Slipgates_ExecutionStub.exe').Delete()
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $extra, 'lib/app/AgentBridgeApp_ExecutionStub.exe')
    $zip.Dispose()
    $threw = $false
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch -RequireSignature -SignerEku $global:PpEku 6>$null } catch { $threw = $_.Exception.Message -like "*has a launcher stub AgentBridgeApp_ExecutionStub.exe*has no lib$([IO.Path]::DirectorySeparatorChar)app$([IO.Path]::DirectorySeparatorChar)Slipgates_ExecutionStub.exe*"; if (-not $threw) { throw "message: $($_.Exception.Message)" } }
    Assert $threw 'a wrongly named stub passed'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

Check "A pull request's dry run checks the signatures too when signing is required" {
    Reset 3
    $global:PpSign = @{ 'agent-bridge-host-hook.exe' = 'unsigned' }
    $threw = $false
    try { Invoke-Publish '0.1.600' -DryRun -Only 'win' -Sign } catch { $threw = $_.Exception.Message -like '*agent-bridge-host-hook.exe: NotSigned*' }
    Assert $threw 'the dry run passed an unsigned hook'
}

Check 'A draft whose package changed after it was checked stays a draft' {
    Reset 3
    $global:PpTamper = { param($store, $channel) if ($channel -eq 'win-arm64') { Add-Content -LiteralPath (Join-Path $store (Get-PpFull 'win-arm64' '0.1.600')) 'x' } }
    $threw = $false
    try { Invoke-Publish '0.1.600' -Sign } catch { $threw = $_.Exception.Message -like "*left a draft, not published*the draft's AgentBridgeApp-0.1.600-win-arm64-full.nupkg is not the file checked*releases.win-arm64.json: AgentBridgeApp-0.1.600-win-arm64-full.nupkg's SHA256 is not the file's*" }
    Assert $threw 'a changed package was published'
    Assert (-not (Published)) 'published'
    Assert ((CallIndex 'gh api -X PATCH*') -lt 0) "calls: $($global:PpCalls -join '; ')"
}

Check "A draft whose feed lists another package stays a draft" {
    Reset 3
    $global:PpTamper = {
        param($store, $channel)
        $feed = Join-Path $store "releases.$channel.json"
        $json = Get-Content $feed -Raw | ConvertFrom-Json
        $json.Assets += [pscustomobject]@{ FileName = 'AgentBridgeApp-0.1.599-full.nupkg'; Type = 'Full'; SHA256 = '00'; Size = 1 }
        $json | ConvertTo-Json -Depth 4 | Set-Content $feed
    }
    $threw = $false
    try { Invoke-Publish '0.1.600' -Sign } catch { $threw = $_.Exception.Message -like '*releases.win.json lists AgentBridgeApp-0.1.599-full.nupkg, which is not this release*' }
    Assert $threw 'a feed with a stranger was published'
    Assert (-not (Published)) 'published'
}

# An installed copy picks a package by its feed entry's pack id, version and type, so the hash and
# size of the right file under the wrong ones is no use to it. Checked before anything goes up
# (the dry run's vpk upload local) and in the draft, downloaded back.
function Get-PpFullEntry($json) { @($json.Assets | Where-Object FileName -EQ 'AgentBridgeApp-0.1.600-full.nupkg')[0] }
foreach ($case in @(
        @{ Name = 'a package with no version'; Edit = { param($j) (Get-PpFullEntry $j).PSObject.Properties.Remove('Version') }; Says = "AgentBridgeApp-0.1.600-full.nupkg's version is '', not 0.1.600" }
        @{ Name = 'a package another version'; Edit = { param($j) (Get-PpFullEntry $j).Version = '0.1.599' }; Says = "AgentBridgeApp-0.1.600-full.nupkg's version is '0.1.599', not 0.1.600" }
        @{ Name = 'a full package typed as a delta'; Edit = { param($j) (Get-PpFullEntry $j).Type = 'Delta' }; Says = "AgentBridgeApp-0.1.600-full.nupkg's type is 'Delta', not Full" }
        @{ Name = "a package another app's id"; Edit = { param($j) (Get-PpFullEntry $j).PackageId = 'SomeoneElsesApp' }; Says = "AgentBridgeApp-0.1.600-full.nupkg's package id is 'SomeoneElsesApp', not AgentBridgeApp" }
        @{ Name = 'a package twice'; Edit = { param($j) $j.Assets += (Get-PpFullEntry $j).PSObject.Copy() }; Says = 'lists AgentBridgeApp-0.1.600-full.nupkg more than once' }
    )) {
    Check "A feed that gives $($case.Name) fails the dry run and leaves the draft a draft" {
        $global:PpFeedEdit = $case.Edit
        $tamper = {
            param($store, $channel)
            if ($channel -ne 'win') { return }
            $feed = Join-Path $store "releases.$channel.json"
            $json = Get-Content $feed -Raw | ConvertFrom-Json
            & $global:PpFeedEdit $json
            $json | ConvertTo-Json -Depth 4 | Set-Content $feed
        }
        Reset 3
        $global:PpTamper = $tamper
        $threw = $false
        try { Invoke-Publish '0.1.600' -DryRun -Only 'win' } catch { $threw = $_.Exception.Message -like "*vpk's win feed is not this channel's 0.1.600*releases.win.json*$($case.Says)*"; if (-not $threw) { throw "dry run: $($_.Exception.Message)" } }
        Assert $threw 'the dry run passed it'
        Reset 3
        $global:PpTamper = $tamper
        $threw = $false
        try { Invoke-Publish '0.1.600' -Sign } catch { $threw = $_.Exception.Message -like "*left a draft, not published*releases.win.json*$($case.Says)*"; if (-not $threw) { throw "draft: $($_.Exception.Message)" } }
        Assert $threw 'it was published'
        Assert (-not (Published)) 'published'
    }
}

Check 'Signing required with no publisher to pin refuses before reading the repository' {
    foreach ($eku in '', '1.3.6.1.4.1.311.97.1.0', '1.3.6.1.5.5.7.3.3') {
        Reset 3
        $dirs = @(foreach ($channel in 'win', 'win-arm64') { New-Folder '0.1.600' -Channel $channel })
        $threw = $false
        try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch -RequireSignature -SignerEku $eku 6>$null } catch { $threw = $_.Exception.Message -like '*not an Artifact Signing identity validation EKU*' }
        Assert $threw "EKU '$eku' was taken"
        Assert ($global:PpCalls.Count -eq 0) "calls: $($global:PpCalls -join '; ')"
    }
}

Check 'Without signing, a release is published as before: unsigned, said so, and never downloaded back' {
    Reset 3
    $global:PpSign = @{ 'agent-bridge-injector.exe' = 'unsigned' }
    Invoke-Publish '0.1.600'
    Assert (Published) 'not published'
    Assert ($global:PpPublishedBody -like '*Unsigned: Windows SmartScreen warns on first install*') "notes: $global:PpPublishedBody"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh release download*' })) "calls: $($global:PpCalls -join '; ')"
}

# --- The delta base pack-app.ps1 gives vpk pack.
function Invoke-Pack([string]$version, [string]$Runtime = 'win-x64') {
    & $packScript -Version $version -Runtime $Runtime -Out $scratch -DeltaFrom "https://github.com/$repo" 6>$null 3>$null
}
function PackArg([string]$name) { $i = [array]::IndexOf($global:PpPackArgs, $name); if ($i -ge 0) { $global:PpPackArgs[$i + 1] } }

Check 'Both architectures get branded downloads and matching upload indexes without changing package identity' {
    foreach ($runtime in 'win-x64', 'win-arm64') {
        Reset
        Invoke-Pack '0.1.600' -Runtime $runtime
        $channel = if ($runtime -eq 'win-x64') { 'win' } else { 'win-arm64' }
        $dir = Join-Path $scratch 'releases'
        $assets = @(Get-Content (Join-Path $dir "assets.$channel.json") -Raw | ConvertFrom-Json)
        Assert ((PackArg '-u') -ceq 'AgentBridgeApp') "pack id: $(PackArg '-u')"
        Assert (($assets | Where-Object Type -EQ 'Full').RelativeFileName -ceq (Get-PpFull $channel '0.1.600')) 'the full package identity moved'
        foreach ($type in 'Installer', 'Portable') {
            $tail = if ($type -eq 'Installer') { 'Setup.exe' } else { 'Portable.zip' }
            $name = "Slipgates-$channel-$tail"
            Assert (($assets | Where-Object Type -EQ $type).RelativeFileName -ceq $name) "upload index does not name $name"
            Assert ((Get-FileHash (Join-Path $dir $name)).Hash -ceq $global:PpPackHashes[$type]) "renaming changed $name's bytes"
            Assert (-not (Test-Path (Join-Path $dir "AgentBridgeApp-$channel-$tail"))) 'a legacy public name was left behind'
            Set-Content (Join-Path $dir $name) 'stale previous pack'
        }
        & $packScript -Version '0.1.600' -Runtime $runtime -Out $scratch -PackOnly 6>$null 3>$null
        Assert ((Get-Content (Join-Path $dir "Slipgates-$channel-Setup.exe")) -ceq 'packed Installer') 'a repeat pack retained the old public installer'
        Assert ((Get-Content (Join-Path $dir "Slipgates-$channel-Portable.zip")) -ceq 'packed Portable') 'a repeat pack retained the old public portable'
    }
}

Check 'Incomplete or unexpected packed download metadata is refused before either file or index is renamed' {
    . (Join-Path $PSScriptRoot 'app-channels.ps1')
    foreach ($case in 'missing-portable', 'duplicate-installer', 'foreign-name', 'blocked-destination') {
        Reset
        $dir = New-Item -ItemType Directory -Force (Join-Path $scratch 'releases')
        $assets = @(
            @{ Type = 'Installer'; RelativeFileName = 'AgentBridgeApp-win-Setup.exe' }
            @{ Type = 'Portable'; RelativeFileName = 'AgentBridgeApp-win-Portable.zip' }
        )
        Set-Content (Join-Path $dir 'AgentBridgeApp-win-Setup.exe') 'packed installer'
        if ($case -ne 'missing-portable') { Set-Content (Join-Path $dir 'AgentBridgeApp-win-Portable.zip') 'packed portable' }
        if ($case -eq 'duplicate-installer') { $assets += $assets[0] }
        if ($case -eq 'foreign-name') { $assets[1].RelativeFileName = '..\foreign.zip' }
        if ($case -eq 'blocked-destination') { New-Item -ItemType Directory (Join-Path $dir 'Slipgates-win-Portable.zip') | Out-Null }
        $index = Join-Path $dir 'assets.win.json'
        ConvertTo-Json -InputObject $assets | Set-Content $index
        $before = (Get-FileHash $index).Hash
        $threw = $false
        try { Set-AppDownloadNames $dir 'win' } catch {
            $threw = $_.Exception.Message -like '*missing packed download*' -or
                $_.Exception.Message -like '*entries, not one*' -or $_.Exception.Message -like '*unexpected Portable name*' -or
                $_.Exception.Message -like '*destination is a directory*'
        }
        Assert $threw "not refused: $case"
        Assert (Test-Path (Join-Path $dir 'AgentBridgeApp-win-Setup.exe')) "installer moved before validation: $case"
        Assert (-not (Test-Path (Join-Path $dir 'Slipgates-win-Setup.exe'))) "branded installer created before validation: $case"
        Assert ((Get-FileHash $index).Hash -ceq $before) "upload index changed before validation: $case"
    }
}

Check 'Legacy-only public download names are refused before any repository call' {
    Reset
    $dir = New-Folder '0.1.600'
    $assets = @(Get-Asset)
    foreach ($asset in $assets | Where-Object Type -IN @('Installer', 'Portable')) {
        $old = $asset.RelativeFileName
        $asset.RelativeFileName = $old -creplace '^Slipgates-', 'AgentBridgeApp-'
        Move-Item -LiteralPath (Join-Path $dir $old) -Destination (Join-Path $dir $asset.RelativeFileName)
    }
    ConvertTo-Json -InputObject $assets | Set-Content (Join-Path $dir 'assets.win.json')
    $threw = $false
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dir -Repository $repo -Commit $commit -DryRun 6>$null } catch {
        $threw = $_.Exception.Message -like '*unexpected asset*AgentBridgeApp-win-Setup.exe*'
    }
    Assert $threw 'legacy public downloads were admitted'
    Assert ($global:PpCalls.Count -eq 0) 'legacy public downloads reached the repository'
}

Check 'An older release is put beside the new package for vpk to build a delta from' {
    Reset
    $global:PpDownload = '0.1.583'
    Invoke-Pack '0.1.600'
    Assert ($global:PpPackSaw -contains 'AgentBridgeApp-0.1.583-full.nupkg') "vpk pack saw: $($global:PpPackSaw)"
}

Check 'A release that is not older than the new package is not a delta base' {
    Reset
    $global:PpDownload = '0.1.601'
    Invoke-Pack '0.1.600'
    Assert (@($global:PpPackSaw).Count -eq 0) "vpk pack saw: $($global:PpPackSaw)"
}

Check 'A failed download packs the full package alone' {
    Reset
    $global:PpDownload = 'fail'
    Invoke-Pack '0.1.600'
    Assert ($null -ne $global:PpPackSaw -and @($global:PpPackSaw).Count -eq 0) "vpk pack saw: $($global:PpPackSaw)"
}

Check 'An x64 pack is published for win-x64 and packed into the win channel exactly as before Arm64 builds' {
    # No --runtime: vpk then gives x64 the Update.exe and Setup.exe installed copies already have.
    Reset
    $global:PpDownload = '0.1.583'
    Invoke-Pack '0.1.600'
    Assert (@($global:PpPublishRids | Select-Object -Unique) -join ',' -eq 'win-x64') "published for: $($global:PpPublishRids)"
    Assert ((PackArg '-c') -ceq 'win') "channel: $(PackArg '-c')"
    Assert ($global:PpPackArgs -notcontains '--runtime') "vpk pack $($global:PpPackArgs -join ' ')"
    Assert ($global:PpCalls -contains 'vpk download github --channel win') "calls: $($global:PpCalls -join '; ')"
}

Check 'An Arm64 pack is published for win-arm64 and packed into its own channel, with its delta base from that channel alone' {
    Reset
    $global:PpDownload = '0.1.583'
    Invoke-Pack '0.1.600' -Runtime 'win-arm64'
    Assert (@($global:PpPublishRids | Select-Object -Unique) -join ',' -eq 'win-arm64') "published for: $($global:PpPublishRids)"
    Assert ((PackArg '-c') -ceq 'win-arm64') "channel: $(PackArg '-c')"
    Assert ((PackArg '--runtime') -ceq 'win-arm64') "vpk pack $($global:PpPackArgs -join ' ')"
    Assert ($global:PpCalls -contains 'vpk download github --channel win-arm64') "calls: $($global:PpCalls -join '; ')"
    Assert ($global:PpPackSaw -contains 'AgentBridgeApp-0.1.583-win-arm64-full.nupkg') "vpk pack saw: $($global:PpPackSaw)"
}

Check 'A pack whose executables are for another architecture than asked for stops before packing' {
    Reset
    $global:PpBuildsFor = 'x64'
    $threw = $false
    try { Invoke-Pack '0.1.600' -Runtime 'win-arm64' } catch { $threw = $_.Exception.Message -like '*not built for arm64*AgentBridge.exe (x64)*' }
    Assert $threw 'x64 executables were packed for Arm64'
    Assert ($null -eq $global:PpPackSaw) 'vpk pack ran'
}

Check 'A package left in the output folder by an earlier pack is not a delta base' {
    Reset
    New-Item -ItemType Directory -Force (Join-Path $scratch 'releases') | Out-Null
    Set-Content (Join-Path $scratch 'releases' 'AgentBridgeApp-0.1.590-full.nupkg') 'full 0.1.590'
    $global:PpDownload = 'fail'
    Invoke-Pack '0.1.600'
    Assert ($null -ne $global:PpPackSaw -and @($global:PpPackSaw).Count -eq 0) "vpk pack saw: $($global:PpPackSaw)"
}

# --- The release notes a package carries, which an installed copy's toast shows after it updates.
Check 'Release notes list the commits since the latest release, newest first' {
    Reset 3
    $global:PpCompare = @('Older change (#198)', 'Newer change (#199)')
    $notes = Join-Path $scratch 'notes.md'
    & (Join-Path $PSScriptRoot 'release-notes.ps1') -Repository $repo -Commit $commit -Out $notes 6>$null
    Assert (((Get-Content $notes) -join "`n") -eq "- Newer change (#199)`n- Older change (#198)") "notes: $(Get-Content $notes -Raw)"
}

Check 'Release notes that cannot be compared with the latest release name the commit itself' {
    Reset 3
    $global:PpCompare = 'fail'
    $notes = Join-Path $scratch 'notes.md'
    & (Join-Path $PSScriptRoot 'release-notes.ps1') -Repository $repo -Commit $commit -Out $notes 6>$null
    Assert (((Get-Content $notes) -join "`n") -eq '- The commit being built (#200)') "notes: $(Get-Content $notes -Raw)"
}

# --- The build identity pack-app.ps1 gives the host.
Check 'A pack without -Release, as a pull request packs, holds a dev host' {
    Reset
    Invoke-Pack '0.1.600'
    $identity = & (Join-Path $PSScriptRoot 'host-identity.ps1') (Join-Path $scratch 'publish' 'agent-bridge-host.dll')
    Assert ($identity -eq 'dev') "identity: $identity"
}

Check 'A pack with -Release, as the publish path packs, holds a release host' {
    Reset
    & $packScript -Version '0.1.600' -Out $scratch -Release 6>$null 3>$null
    $identity = & (Join-Path $PSScriptRoot 'host-identity.ps1') (Join-Path $scratch 'publish' 'agent-bridge-host.dll')
    Assert ($identity -eq 'release') "identity: $identity"
}

Check 'A pack whose host is not the build asked for stops before packing' {
    Reset
    $global:PpIgnoreRelease = $true
    $threw = $false
    try { & $packScript -Version '0.1.600' -Out $scratch -Release 6>$null 3>$null } catch { $threw = $_.Exception.Message -like '*dev build, not a release build*' }
    Assert $threw 'a dev host was packed as a release'
    Assert ($null -eq $global:PpPackSaw) 'vpk pack ran'
}

# --- Signing in pack-app.ps1 (#734): vpk signs through Artifact Signing's metadata, and the pack
# fails unless what it packed is the pinned publisher's.
. (Join-Path $PSScriptRoot 'app-signing.ps1')
function New-PpMetadata { $path = Join-Path $scratch 'signing' 'metadata.json'; New-AppSigningMetadata -Path $path -Endpoint 'https://eus.codesigning.azure.net/' -Account 'agent-bridge' -CertificateProfile 'agent-bridge-public'; $path }

Check 'A signing pack hands vpk the Artifact Signing metadata and checks every file it packed' {
    foreach ($runtime in 'win-x64', 'win-arm64') {
        Reset
        $global:PpPackMakes = $true
        $global:PpSign = @{ 'Microsoft.Runtime.dll' = 'microsoft' }
        $metadata = New-PpMetadata
        & $packScript -Version '0.1.600' -Runtime $runtime -Out $scratch -Release -AzureTrustedSignFile $metadata -RequireSignature -SignerEku $global:PpEku 6>$null 3>$null
        Assert ((PackArg '--azureTrustedSignFile') -eq $metadata) "vpk pack $($global:PpPackArgs -join ' ')"
        Assert (-not (Test-Path (Join-Path $scratch 'signature-check'))) 'the expanded packages were left behind'
    }
}

Check 'A signing pack with an unsigned NativeAOT hook fails' {
    Reset
    $global:PpPackMakes = $true
    $global:PpSign = @{ 'agent-bridge-host-hook.exe' = 'unsigned' }
    $threw = $false
    try { & $packScript -Version '0.1.600' -Out $scratch -Release -AzureTrustedSignFile (New-PpMetadata) -RequireSignature -SignerEku $global:PpEku 6>$null 3>$null } catch { $threw = $_.Exception.Message -like '*packed release is not signed as it must be*agent-bridge-host-hook.exe: NotSigned*' }
    Assert $threw 'an unsigned hook was packed'
}

Check 'A pack required to sign, without the means to, builds nothing' {
    foreach ($case in @(
            @{ Args = @{ SignerEku = $global:PpEku }; Says = '*neither -AzureTrustedSignFile nor -SignParams*' }
            @{ Args = @{ SignerEku = $global:PpEku; AzureTrustedSignFile = 'no-such-metadata.json' }; Says = '*there is no no-such-metadata.json*' }
            @{ Args = @{ SignerEku = '1.3.6.1.4.1.311.97.1.0'; SignParams = '/n x' }; Says = '*not an Artifact Signing identity validation EKU*' }
            @{ Args = @{ SignerEku = $global:PpEku; SignParams = '/n x'; NoPack = $true }; Says = '*-NoPack packs none*' }
        )) {
        Reset
        $a = $case.Args
        $threw = $false
        try { & $packScript -Version '0.1.600' -Out $scratch -Release -RequireSignature @a 6>$null 3>$null } catch { $threw = $_.Exception.Message -like $case.Says }
        Assert $threw "not refused: $($case.Says)"
        Assert ($global:PpPublishRids.Count -eq 0) "dotnet publish ran: $($case.Says)"
    }
}

# --- CI's signing job (#734): it packs and signs the folder an app job published, building nothing,
# so no build or test holds the signing identity.
function New-PpPublished([string]$Runtime = 'win-x64', [switch]$Release) {
    & $packScript -Version '0.1.600' -Runtime $Runtime -Out $scratch -Release:$Release -NoPack 6>$null 3>$null
    $global:PpPublishRids.Clear()
}

Check 'Both architectures carry verbatim project and package notices bound to their build source' {
    foreach ($runtime in 'win-x64', 'win-arm64') {
        Reset
        New-PpPublished $runtime
        . (Join-Path $PSScriptRoot 'app-notices.ps1')
        $publish = Join-Path $scratch 'publish'
        $notice = Get-AppNotices $publish '0.1.600' $runtime
        Assert ($notice.version -ceq '0.1.600') 'a dependency version replaced the build version'
        Assert ($notice.packages.Count -eq 2) 'package notices missing'
        Assert ((Get-FileHash (Join-Path $publish 'LICENSE.txt')).Hash -ceq (Get-FileHash (Join-Path $PSScriptRoot '..' 'LICENSE')).Hash) 'project licence changed'
        $source = Get-Content (Join-Path $publish 'SOURCE.txt') -Raw
        Assert ($source.Contains("/tree/$($notice.commit)") -and $source.Contains("/blob/$($notice.commit)/docs/app-windows.md")) 'source/build-install links are not exact-commit links'
        foreach ($package in $notice.packages) {
            $path = Get-AppNoticePath $publish $package.files[0]
            Assert ((Get-Content $path -Raw).Contains("Verbatim notice fixture for $($package.id)")) 'notice text changed'
        }
    }
}

Check 'Pack-only refuses missing or altered notices without rebuilding them' {
    foreach ($edit in @(
            { Remove-Item -LiteralPath (Join-Path $scratch 'publish' 'LICENSE.txt') }
            { Add-Content -LiteralPath (Join-Path $scratch 'publish' 'SOURCE.txt') 'changed' }
        )) {
        Reset
        New-PpPublished
        & $edit
        $threw = $false
        try { & $packScript -Version '0.1.600' -Out $scratch -PackOnly 6>$null 3>$null }
        catch { $threw = $_.Exception.Message -like '*notice*' }
        Assert $threw 'changed notices were accepted'
        Assert ($global:PpPublishRids.Count -eq 0 -and $null -eq $global:PpPackSaw) 'pack-only rebuilt notices or packed them'
    }
}

Check 'Packages cannot substitute metadata, omit declared terms or escape their restored folder' {
    foreach ($case in @(
            @{ From = 'legal/terms.txt'; To = '../outside.txt'; Says = '*notice path escapes its folder*' }
            @{ From = 'legal/terms.txt'; To = 'legal/missing.txt'; Says = '*missing notice file*' }
            @{ From = '<id>Notice.File</id>'; To = '<id>Other</id>'; Says = '*metadata does not identify Notice.File/1.0.0*' }
        )) {
        Reset
        $nuspec = Join-Path $env:NUGET_PACKAGES 'notice.file' '1.0.0' 'notice.file.nuspec'
        $original = Get-Content $nuspec -Raw
        try {
            Set-Content -LiteralPath $nuspec ($original.Replace($case.From, $case.To))
            $threw = $false
            try { New-PpPublished } catch { $threw = $_.Exception.Message -like $case.Says }
            Assert $threw "package error was accepted: $($case.To)"
            Assert ($null -eq $global:PpPackSaw) 'vpk pack ran'
        } finally { Set-Content -LiteralPath $nuspec $original }
    }
}

Check 'Offline upstream notices preserve exact bytes and refuse unpinned packages, metadata or notice material' {
    foreach ($case in 'valid', 'archive', 'metadata', 'notice', 'version', 'unreviewed',
        'url-valid', 'url-metadata', 'url-source', 'url-copyright', 'url-expression') {
        Reset
        . (Join-Path $PSScriptRoot 'app-notices.ps1')
        $publish = Join-Path $scratch 'publish'
        $repositoryRoot = Join-Path $scratch 'notice-repository'
        $packageRoot = Join-Path $scratch 'notice-packages'
        $folder = Join-Path $packageRoot 'notice.curated' '1.0.0'
        $upstream = Join-Path $repositoryRoot 'build' 'notices' 'notice.curated' '1.0.0'
        foreach ($directory in $folder, $upstream, (Join-Path $publish 'Assets' 'Fonts')) {
            New-Item -ItemType Directory -Force $directory | Out-Null
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'LICENSE') -Destination (Join-Path $repositoryRoot 'LICENSE')
        @{ libraries = @{ 'Notice.Curated/1.0.0' = @{ type = 'package' } } } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $publish 'app.deps.json')
        $nuspec = Join-Path $folder 'notice.curated.nuspec'
        $archive = Join-Path $folder 'notice.curated.1.0.0.nupkg'
        $urlOnly = $case.StartsWith('url-')
        $noticeName = if ($urlOnly) { 'license.rtf' } else { 'license.txt' }
        $notice = Join-Path $upstream $noticeName
        $provenancePath = Join-Path $upstream 'provenance.json'
        $declaration = if ($urlOnly) {
            '<licenseUrl>https://example.test/SDK-LICENSE.rtf</licenseUrl><copyright>Copyright fixture</copyright>'
        } else {
            "<license type=`"expression`">BSD-2-Clause</license><repository type=`"git`" url=`"https://github.com/example/notice`" commit=`"$commit`" />"
        }
        $metadata = "<package><metadata><id>Notice.Curated</id><version>1.0.0</version>$declaration</metadata></package>"
        Set-Content -LiteralPath $nuspec $metadata
        Set-Content -LiteralPath $archive 'Exact archive fixture bytes, hashed but never unpacked or executed.'
        $noticeText = if ($urlOnly) { "{\rtf1\ansi Copyright fixture\par Verbatim terms and disclaimer. }`n" } else { "Copyright fixture`nVerbatim terms and disclaimer. `n" }
        [IO.File]::WriteAllBytes($notice, [Text.Encoding]::UTF8.GetBytes($noticeText))
        $provenance = @{
            schemaVersion = 1; packageId = 'Notice.Curated'; packageVersion = '1.0.0'
            packageSha512 = (Get-FileHash $archive -Algorithm SHA512).Hash.ToLowerInvariant()
            noticeFile = $noticeName
            noticeSha256 = (Get-FileHash $notice -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        if ($urlOnly) {
            $provenance.licenseUrl = 'https://example.test/SDK-LICENSE.rtf'
            $provenance.noticeSourceUrl = $provenance.licenseUrl
            $provenance.copyright = 'Copyright fixture'
        } else {
            $provenance.licenseExpression = 'BSD-2-Clause'
            $provenance.repositoryUrl = 'https://github.com/example/notice'
            $provenance.repositoryCommit = $commit
            $provenance.noticeSourceUrl = "https://github.com/example/notice/blob/$commit/license.txt"
        }
        $expectedNotice = [Convert]::ToBase64String([IO.File]::ReadAllBytes($notice))
        switch ($case) {
            'archive' { Add-Content -LiteralPath $archive 'changed' }
            'metadata' { Set-Content -LiteralPath $nuspec ($metadata.Replace('BSD-2-Clause', 'MIT')) }
            'notice' { Add-Content -LiteralPath $notice 'changed' }
            'version' { $provenance.packageVersion = '1.0.1' }
            'url-metadata' { Set-Content -LiteralPath $nuspec ($metadata.Replace('SDK-LICENSE.rtf', 'OTHER-LICENSE.rtf')) }
            'url-source' { $provenance.noticeSourceUrl = 'https://example.test/OTHER-LICENSE.rtf' }
            'url-copyright' { $provenance.copyright = 'Different copyright' }
            'url-expression' { Set-Content -LiteralPath $nuspec ($metadata.Replace('<licenseUrl>', '<license type="expression">MIT</license><licenseUrl>')) }
        }
        $provenance | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $provenancePath
        if ($case -eq 'unreviewed') { Remove-Item -LiteralPath $provenancePath }
        $failure = $null
        try { New-AppNotices $repositoryRoot $publish '0.1.600' 'win-x64' $commit $packageRoot }
        catch { $failure = $_.Exception.Message }
        if ($case -in 'valid', 'url-valid') {
            Assert ($null -eq $failure) "reviewed upstream notice refused: $failure"
            $manifest = Get-AppNotices $publish '0.1.600' 'win-x64'
            $bundled = Join-Path $publish 'ThirdPartyNotices' 'packages' 'Notice.Curated' '1.0.0'
            Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $bundled $noticeName))) -ceq $expectedNotice) 'upstream notice bytes changed'
            Assert ((Get-FileHash (Join-Path $bundled 'provenance.json')).Hash -ceq (Get-FileHash $provenancePath).Hash) 'upstream provenance bytes changed'
            Assert ($manifest.packages[0].files.Count -eq 2) 'upstream notice or provenance omitted from the hashed inventory'
            if ($urlOnly) {
                Assert ($manifest.packages[0].licenseUrl -ceq $provenance.licenseUrl) 'package-declared licence URL lost'
                Assert (-not $manifest.packages[0].licenseType -and -not $manifest.packages[0].licenseValue) 'URL-only terms were relabelled as a declared expression or file'
            }
        } else {
            $expected = switch ($case) {
                'archive' { '*upstream notice package hash mismatch*' }
                'metadata' { '*upstream notice provenance does not identify*' }
                'notice' { '*upstream notice hash mismatch*' }
                'version' { '*upstream notice provenance does not identify*' }
                'unreviewed' { '*has no restored notice files*' }
                { $_ -in 'url-metadata', 'url-source', 'url-copyright', 'url-expression' } { '*upstream notice provenance does not identify*' }
            }
            Assert ($failure -like $expected) "wrong or missing upstream refusal for $case`: $failure"
        }
    }
}

Check 'SBOM licences preserve custom terms and existing fields without guessing unrelated licences' {
    Reset
    New-PpPublished
    . (Join-Path $PSScriptRoot 'app-notices.ps1')
    $sbom = Join-Path $scratch 'sbom.json'
    @{ bomFormat = 'CycloneDX'; components = @(
            @{ name = 'Notice.Expression'; version = '1.0.0'; purl = 'pkg:nuget/Notice.Expression@1.0.0' }
            @{ name = 'Notice.File'; version = '1.0.0' }
            @{ name = 'Notice.Expression'; version = '1.0.0'; licenses = @(@{ expression = 'BSD-2-Clause' }) }
            @{ name = 'Unknown'; version = '1.0.0' }
            @{ name = 'Notice.Expression'; version = '1.0.0'; purl = 'pkg:npm/Notice.Expression@1.0.0' }
            @{ name = 'AgentBridge.App'; version = '0.1.600' }
        ) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $sbom
    Add-AppSbomLicences (Join-Path $scratch 'publish') '0.1.600' 'win-x64' $sbom
    $components = (Get-Content $sbom -Raw | ConvertFrom-Json -AsHashtable).components
    Assert ($components[0].licenses[0].expression -ceq 'MIT') 'declared expression lost'
    $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($components[1].licenses[0].license.text.content))
    Assert ($text.Contains('Verbatim notice fixture for Notice.File')) 'custom licence text lost'
    Assert ($components[2].licenses[0].expression -ceq 'BSD-2-Clause') 'existing licence overwritten'
    Assert (-not $components[3].ContainsKey('licenses') -and -not $components[4].ContainsKey('licenses')) 'unrelated licences guessed'
    Assert ($components[5].licenses[0].license.text.content -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $scratch 'publish' 'LICENSE.txt')))) 'project licence text missing'
}

Check 'SBOM file licences accept Windows casing and normalized relative paths without changing their bytes' {
    foreach ($declared in 'LEGAL/TERMS.TXT', './legal/terms.txt') {
        Reset
        New-PpPublished
        . (Join-Path $PSScriptRoot 'app-notices.ps1')
        $publish = Join-Path $scratch 'publish'
        $manifestPath = Join-Path $publish 'ThirdPartyNotices' 'manifest.json'
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        $package = @($manifest.packages | Where-Object { $_.id -ceq 'Notice.File' })[0]
        $package.licenseValue = $declared
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath
        $sbom = Join-Path $scratch 'sbom.json'
        @{ bomFormat = 'CycloneDX'; components = @(
                @{ name = 'Notice.File'; version = '1.0.0'; purl = 'pkg:nuget/Notice.File@1.0.0' }
            ) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $sbom
        Add-AppSbomLicences $publish '0.1.600' 'win-x64' $sbom
        $content = (Get-Content $sbom -Raw | ConvertFrom-Json -AsHashtable).components[0].licenses[0].license.text.content
        $path = Join-Path $publish 'ThirdPartyNotices' 'packages' 'Notice.File' '1.0.0' 'legal' 'terms.txt'
        Assert ($content -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) "declared licence bytes changed: $declared"
    }
}

Check 'Distribution evidence checks portable, full and installed notices against their actual hashes' {
    Reset
    New-PpPublished
    . (Join-Path $PSScriptRoot 'app-channels.ps1')
    . (Join-Path $PSScriptRoot 'app-notices.ps1')
    $publish = Join-Path $scratch 'publish'
    $releases = Join-Path $scratch 'releases'
    New-Item -ItemType Directory -Path $releases | Out-Null
    foreach ($kind in 'Portable', 'Full') {
        $stage = Join-Path $scratch "notices-$kind"
        $inner = if ($kind -eq 'Portable') { Join-Path $stage 'current' } else { Join-Path $stage 'lib' 'app' }
        New-Item -ItemType Directory -Force (Split-Path $inner) | Out-Null
        Copy-Item -LiteralPath $publish -Destination $inner -Recurse
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, (Join-Path $releases (Get-AppAssetName 'win' $kind '0.1.600')))
    }
    $setup = Join-Path $releases (Get-AppAssetName 'win' 'Installer' '0.1.600')
    New-PpExe $setup 'x64'
    $installed = Join-Path $scratch 'installed'
    Copy-Item -LiteralPath $publish -Destination $installed -Recurse
    $out = Join-Path $scratch 'distribution-evidence.json'
    Write-AppDistributionEvidence $publish $releases '0.1.600' 'win-x64' $installed $out
    $evidence = Get-Content $out -Raw | ConvertFrom-Json -AsHashtable
    Assert ($evidence.archives.Count -eq 2 -and $evidence.installedFiles.Count -gt 3) 'file inventories missing'
    Assert ($evidence.installer.sha256 -ceq (Get-FileHash $setup).Hash.ToLowerInvariant()) 'installer hash is not the inspected file'
    $portable = Join-Path $releases (Get-AppAssetName 'win' 'Portable' '0.1.600')
    $zip = [IO.Compression.ZipFile]::Open($portable, [IO.Compression.ZipArchiveMode]::Update)
    try { $zip.GetEntry('current/LICENSE.txt').Delete() } finally { $zip.Dispose() }
    $threw = $false
    try { Write-AppDistributionEvidence $publish $releases '0.1.600' 'win-x64' $installed $out }
    catch { $threw = $_.Exception.Message -like '*missing or duplicate notice: LICENSE.txt*' }
    Assert $threw 'a portable missing its project licence passed'
    Add-Content -LiteralPath (Join-Path $installed 'SOURCE.txt') 'changed'
    $threw = $false
    try { Write-AppDistributionEvidence $publish $releases '0.1.600' 'win-x64' $installed $out }
    catch { $threw = $_.Exception.Message -like '*distribution notice hash mismatch: SOURCE.txt*' }
    Assert $threw 'an installed copy with changed source material passed'
    Assert ($global:PpCalls.Count -eq 0) 'notice evidence made a GitHub or vpk call'
}

Check 'A pack-only signing pack signs the folder already published, for either architecture, publishing nothing' {
    foreach ($runtime in 'win-x64', 'win-arm64') {
        Reset
        New-PpPublished $runtime -Release
        $global:PpPackMakes = $true
        $global:PpSign = @{ 'Microsoft.Runtime.dll' = 'microsoft' }
        $metadata = New-PpMetadata
        & $packScript -Version '0.1.600' -Runtime $runtime -Out $scratch -Release -PackOnly -AzureTrustedSignFile $metadata -RequireSignature -SignerEku $global:PpEku 6>$null 3>$null
        Assert ($global:PpPublishRids.Count -eq 0) "dotnet publish ran for: $($global:PpPublishRids)"
        Assert ((PackArg '-p') -eq [IO.Path]::GetFullPath((Join-Path $scratch 'publish'))) "vpk pack $($global:PpPackArgs -join ' ')"
        Assert ((PackArg '--azureTrustedSignFile') -eq $metadata) "vpk pack $($global:PpPackArgs -join ' ')"
        Assert ((PackArg '-c') -ceq @{ 'win-x64' = 'win'; 'win-arm64' = 'win-arm64' }[$runtime]) "channel: $(PackArg '-c')"
    }
}

Check 'A pack-only pack still checks the folder it is given, and packs nothing it refuses' {
    foreach ($case in @(
            @{ Setup = {}; Args = @{}; Says = '*-PackOnly packs*and there is none*' }
            @{ Setup = { New-PpPublished }; Args = @{ Release = $true }; Says = '*dev build, not a release build*' }
            @{ Setup = { New-PpPublished -Release }; Args = @{ Release = $true; Runtime = 'win-arm64' }; Says = '*not built for arm64*AgentBridge.exe (x64)*' }
            @{ Setup = { New-PpPublished -Release; Remove-Item (Join-Path $scratch 'publish' 'agent-bridge-injector.exe') }; Args = @{ Release = $true }; Says = '*missing from the publish folder: agent-bridge-injector.exe*' }
            @{ Setup = { New-PpPublished -Release }; Args = @{ Release = $true; NoPack = $true }; Says = '*-PackOnly packs the folder already there, and -NoPack packs nothing*' }
            @{ Setup = { New-PpPublished -Release }; Args = @{ Release = $true; RequireSignature = $true; SignerEku = $global:PpEku }; Says = '*neither -AzureTrustedSignFile nor -SignParams*' }
        )) {
        Reset
        & $case.Setup
        $a = $case.Args
        $threw = $false
        try { & $packScript -Version '0.1.600' -Out $scratch -PackOnly @a 6>$null 3>$null } catch { $threw = $_.Exception.Message -like $case.Says }
        Assert $threw "not refused: $($case.Says)"
        Assert ($global:PpPublishRids.Count -eq 0) "dotnet publish ran: $($case.Says)"
        Assert ($null -eq $global:PpPackSaw) "vpk pack ran: $($case.Says)"
    }
}

Check 'The Artifact Signing metadata names the account and profile, the Azure CLI sign-in alone, and no secret' {
    Reset
    $metadata = Get-Content (New-PpMetadata) -Raw | ConvertFrom-Json
    Assert ($metadata.Endpoint -eq 'https://eus.codesigning.azure.net' -and $metadata.CodeSigningAccountName -eq 'agent-bridge' -and $metadata.CertificateProfileName -eq 'agent-bridge-public') "metadata: $($metadata | ConvertTo-Json -Compress)"
    Assert ((@($metadata.PSObject.Properties.Name) | Sort-Object) -join ',' -eq 'CertificateProfileName,CodeSigningAccountName,Endpoint,ExcludeCredentials') "fields: $($metadata.PSObject.Properties.Name)"
    Assert ($metadata.ExcludeCredentials -notcontains 'AzureCliCredential' -and $metadata.ExcludeCredentials -contains 'EnvironmentCredential' -and $metadata.ExcludeCredentials -contains 'ManagedIdentityCredential') "excluded: $($metadata.ExcludeCredentials)"
    foreach ($bad in @(@('http://eus.codesigning.azure.net', 'a-b-c', 'p-q-r'), @('https://evil.example.net', 'a-b-c', 'p-q-r'), @('https://eus.codesigning.azure.net', '', 'p-q-r'), @('https://eus.codesigning.azure.net', 'a-b-c', 'p q'))) {
        $threw = $false
        try { New-AppSigningMetadata -Path (Join-Path $scratch 'bad.json') -Endpoint $bad[0] -Account $bad[1] -CertificateProfile $bad[2] } catch { $threw = $true }
        Assert $threw "taken: $($bad -join ', ')"
    }
}

if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
if (Test-Path $fixtures) { Remove-Item $fixtures -Recurse -Force }
Remove-Item Env:GH_TOKEN
if ($null -eq $originalNugetPackages) { Remove-Item Env:NUGET_PACKAGES }
else { $env:NUGET_PACKAGES = $originalNugetPackages }
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
