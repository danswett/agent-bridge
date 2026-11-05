# Checks build/publish-prerelease.ps1, and the delta base and build identity build/pack-app.ps1
# gives the package, against stand-ins for gh, vpk and dotnet: nothing is built, uploaded,
# published or deleted, and the folders are under out/test-publish-prerelease.
#
#   pwsh build/test-publish-prerelease.ps1
#
# What it guards: a merge publishes exactly one pre-release of its own version, never a second
# copy, never one older than what is out, never a dev build, and the pruning never reaches a full
# release or a tag.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publishScript = Join-Path $PSScriptRoot 'publish-prerelease.ps1'
$packScript = Join-Path $PSScriptRoot 'pack-app.ps1'
$scratch = Join-Path $PSScriptRoot '..' 'out' 'test-publish-prerelease'
$fixtures = Join-Path $PSScriptRoot '..' 'out' 'test-publish-prerelease-hosts'
$repo = 'danswett/agent-bridge'
$commit = '0123456789abcdef0123456789abcdef01234567'
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
        foreach ($r in $global:PpReleases) { $r | ConvertTo-Json -Compress }
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/git/matching-refs/tags/*") {
        $prefix = 'refs/tags/' + $a[1].Substring("repos/$repo/git/matching-refs/tags/".Length)
        $global:PpTags | Where-Object { $_.StartsWith($prefix) }
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'DELETE') {
        $id = [long]($a[3] -split '/')[-1]
        [void]$global:PpReleases.RemoveAll({ param($r) $r.id -eq $id })
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
        if ($global:PpPackMakes) {
            $channel = Arg '-c'
            $machine = if ((Arg '--runtime') -eq 'win-arm64') { 'arm64' } else { 'x64' }
            New-Item -ItemType Directory -Force (Arg '-o') | Out-Null
            New-Package (Join-Path (Arg '-o') (Get-PpFull $channel (Arg '-v'))) 'dev' $machine
            New-PpExe (Join-Path (Arg '-o') "AgentBridgeApp-$channel-Setup.exe") $machine
            New-PpPortable (Join-Path (Arg '-o') "AgentBridgeApp-$channel-Portable.zip") $machine
        }
    } else { throw "stand-in vpk: unexpected $($a -join ' ')" }
}

function global:dotnet {
    $a = @($args)
    $global:LASTEXITCODE = 0
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
    $global:PpDownload = $null; $global:PpPackSaw = $null; $global:PpPackArgs = $null; $global:PpIgnoreRelease = $false; $global:PpCompare = @()
    $global:PpBuildsFor = $null; $global:PpFeedExtra = $null; $global:PpPackMakes = $false; $global:PpTamper = $null; $global:PpSign = @{}
    $global:PpGeneratedNotes = "## What's Changed`n* A change (#200)`n`n**Full Changelog**: https://github.com/$repo/compare/v0.1.503...v0.1.600"
    $global:PpPublishedBody = $null
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
    foreach ($exe in 'AgentBridge.exe', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe', 'Squirrel.exe', 'Agent Bridge_ExecutionStub.exe', 'Microsoft.Runtime.dll') {
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
    foreach ($exe in 'Update.exe', 'Agent Bridge.exe') { New-PpExe (Join-Path $stage $exe) $machine }
    foreach ($exe in 'AgentBridge.exe', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe') {
        New-PpExe (Join-Path $stage 'current' $exe) $machine
    }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $path)
    Remove-Item $stage -Recurse -Force
}

# x64's folder is out/app/releases, as before Arm64 builds; Arm64's is beside it.
function Get-PpDir([string]$channel = 'win') { Join-Path $scratch $(if ($channel -eq 'win') { 'releases' } else { "releases-$channel" }) }

function New-Folder([string]$version, [string]$DeltaFrom, [string[]]$Skip = @(), [switch]$DevBuild, [string]$Channel = 'win', [string]$Machine) {
    # As vpk pack leaves it: with -DeltaFrom, a delta from that version and its full package beside.
    if (-not $Machine) { $Machine = if ($Channel -eq 'win') { 'x64' } else { 'arm64' } }
    $suffix = if ($Channel -eq 'win') { '' } else { "-$Channel" }
    $dir = Get-PpDir $Channel
    New-Item -ItemType Directory -Force $dir | Out-Null
    $assets = [Collections.Generic.List[object]]::new()
    $assets.Add([ordered]@{ RelativeFileName = "AgentBridgeApp-$Channel-Setup.exe"; Type = 'Installer' })
    $assets.Add([ordered]@{ RelativeFileName = "AgentBridgeApp-$Channel-Portable.zip"; Type = 'Portable' })
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
function Invoke-Publish([string]$version, [switch]$DryRun, [string]$DeltaFrom, [string[]]$Skip = @(), [switch]$DevBuild, [string[]]$Only = @('win', 'win-arm64'), [hashtable]$Machine = @{}, [switch]$Plan, [switch]$Sign) {
    $dirs = @(foreach ($channel in $Only) { New-Folder $version -DeltaFrom $DeltaFrom -Skip $Skip -DevBuild:$DevBuild -Channel $channel -Machine $Machine[$channel] })
    # -Sign: as CI publishes once APP_SIGNING is on.
    $signing = if ($Sign) { @{ RequireSignature = $true; SignerEku = $global:PpEku } } else { @{} }
    # -Plan: what it says it does, rather than nothing.
    if ($Plan) { & $publishScript -Version $version -ReleasesDir $dirs -Repository $repo -Commit $commit -DryRun:$DryRun @signing 6>&1 | Out-String }
    else { & $publishScript -Version $version -ReleasesDir $dirs -Repository $repo -Commit $commit -DryRun:$DryRun @signing 6>$null }
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

# --- Publishing.
Check 'A new version goes up as a draft, then is published as a pre-release of the commit with assets.win.json beside it' {
    Reset 3
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.503'
    Assert ($global:PpCalls -notcontains 'vpk delta generate') 'a delta from the latest release was rebuilt'
    Assert ((Get-Delta) -eq 'delta 0.1.503->0.1.600') "delta: $(Get-Delta)"
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
    Assert ([bool]($global:PpCalls | Where-Object { $_ -like 'gh release upload v0.1.600 *assets.win.json*' })) 'assets.win.json was not uploaded'
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
    # Every copy installed so far is x64 and reads releases.win.json: its channel, file names and
    # the upload that creates the release must not move.
    Reset 3
    $plan = Invoke-Publish '0.1.600' -DeltaFrom '0.1.503' -Plan
    Assert (@(Uploads)[0] -eq "vpk upload github --channel win --tag v0.1.600 --targetCommitish $commit pre=True publish=False merge=False") "upload: $(Uploads)"
    $x64 = 'AgentBridgeApp-win-Setup.exe, AgentBridgeApp-win-Portable.zip, AgentBridgeApp-0.1.600-full.nupkg, AgentBridgeApp-0.1.600-delta.nupkg, releases.win.json, RELEASES, assets.win.json'
    Assert ($plan -match "v0\.1\.600 win \(win-x64, release build\) from $commit, .*: $([regex]::Escape($x64))\r?\n") "plan: $plan"
    Assert (-not (@(Get-Asset 'win').RelativeFileName -match 'arm64')) "x64's assets: $(@(Get-Asset 'win').RelativeFileName)"
}

Check 'Arm64 joins the same draft in its own channel, and the release is published only once both are up' {
    Reset 3
    $plan = Invoke-Publish '0.1.600' -DeltaFrom '0.1.503' -Plan
    Assert (@(Uploads)[1] -eq "vpk upload github --channel win-arm64 --tag v0.1.600 --targetCommitish $commit pre=True publish=False merge=True") "upload: $(Uploads)"
    $arm64 = 'AgentBridgeApp-win-arm64-Setup.exe, AgentBridgeApp-win-arm64-Portable.zip, AgentBridgeApp-0.1.600-win-arm64-full.nupkg, AgentBridgeApp-0.1.600-win-arm64-delta.nupkg, releases.win-arm64.json, assets.win-arm64.json'
    Assert ($plan -match "v0\.1\.600 win-arm64 \(win-arm64, release build\) from $commit, .*: $([regex]::Escape($arm64))\r?\n") "plan: $plan"
    Assert ((Get-Delta 'win-arm64') -eq 'delta 0.1.503->0.1.600') "arm64 delta: $(Get-Delta 'win-arm64')"
    Assert (@($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600').Count -eq 1) 'not exactly one v0.1.600'
    $published = CallIndex 'gh api -X PATCH*'
    Assert ($published -gt (CallIndex '*--channel win-arm64*merge=True') -and $published -gt (CallIndex 'gh release upload v0.1.600 *assets.win-arm64.json*')) "calls: $($global:PpCalls -join '; ')"
    $notes = $global:PpPublishedBody
    Assert ($notes -like '*AgentBridgeApp-win-Setup.exe on an x64 PC*' -and $notes -like '*AgentBridgeApp-win-arm64-Setup.exe on a Windows on Arm PC*') "notes: $notes"
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
    Assert ($said -eq 'published=true') "a run that published said: '$said'"
    $said = Get-Said { New-PpRelease 'v0.1.600'; Invoke-Publish '0.1.600' }
    Assert ($said -eq '') "a re-run of a published version said: '$said'"
    $said = Get-Said { New-PpRelease 'v0.1.601'; Invoke-Publish '0.1.600' }
    Assert ($said -eq '') "an older version said: '$said'"
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
    try { Invoke-Publish '0.1.600' -Skip 'AgentBridgeApp-win-Setup.exe' } catch { $threw = $_.Exception.Message -like '*AgentBridgeApp-win-Setup.exe*' }
    Assert $threw 'the missing Setup.exe was not refused'
    Assert ($global:PpCalls.Count -eq 0) "calls: $($global:PpCalls -join '; ')"
}

Check "A folder of another version's packages is refused" {
    Reset 3
    $threw = $false
    $dir = New-Folder '0.1.599'
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dir -Repository $repo -Commit $commit 6>$null } catch { $threw = $true }
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
    & $publishScript -Version '0.1.600' -ReleasesDir (Get-PpDir 'win'), (Get-PpDir 'win-arm64') -Repository $repo -Commit $commit 6>$null
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
    Assert ($downloads[0] -like '*-p AgentBridgeApp-0.1.600-full.nupkg*-p AgentBridgeApp-win-Setup.exe*-p AgentBridgeApp-win-Portable.zip*-p AgentBridgeApp-0.1.600-delta.nupkg*-p releases.win.json*') "download: $($downloads[0])"
    Assert ((CallIndex 'gh release download*') -lt (CallIndex 'gh api -X PATCH*')) "calls: $($global:PpCalls -join '; ')"
    Assert ($global:PpPublishedBody -like '*Signed by CN=Agent Bridge Test Publisher with Azure Artifact Signing*SmartScreen may still warn*') "notes: $global:PpPublishedBody"
    Assert ($global:PpPublishedBody -notlike '*Unsigned*') "notes: $global:PpPublishedBody"
}

foreach ($case in @(
        @{ Name = 'an unsigned NativeAOT injector'; Sign = @{ 'agent-bridge-injector.exe' = 'unsigned' }; Says = '*agent-bridge-injector.exe: NotSigned*' }
        @{ Name = "Velopack's Update.exe signed by another publisher"; Sign = @{ 'Squirrel.exe' = 'other' }; Says = '*Squirrel.exe: signed by CN=Someone Else, not the pinned publisher*' }
        @{ Name = 'a Setup.exe without a timestamp'; Sign = @{ 'AgentBridgeApp-win-arm64-Setup.exe' = 'notimestamp' }; Says = '*AgentBridgeApp-win-arm64-Setup.exe: no timestamp*' }
        @{ Name = 'a launcher stub whose certificate is not Public Trust'; Sign = @{ 'Agent Bridge_ExecutionStub.exe' = 'nopublictrust' }; Says = '*Agent Bridge_ExecutionStub.exe: the signer has no 1.3.6.1.4.1.311.97.1.0 EKU*' }
        @{ Name = 'an unsigned runtime file'; Sign = @{ 'Microsoft.Runtime.dll' = 'unsigned' }; Says = '*Microsoft.Runtime.dll: NotSigned*' }
        @{ Name = 'a portable launcher signed by another publisher'; Sign = @{ 'Agent Bridge.exe' = 'other' }; Says = '*Portable.zip Agent Bridge.exe: signed by CN=Someone Else*' }
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
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -RequireSignature -SignerEku $global:PpEku 6>$null } catch { $threw = $_.Exception.Message -like "*has no lib$([IO.Path]::DirectorySeparatorChar)app$([IO.Path]::DirectorySeparatorChar)agent-bridge-host-hook.exe*" }
    Assert $threw 'a package without the hook passed'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

Check 'A portable zip without its launcher is refused' {
    Reset 3
    $global:PpSign = @{}
    $dirs = @(foreach ($channel in 'win', 'win-arm64') { New-Folder '0.1.600' -Channel $channel })
    $portable = Join-Path $dirs[1] 'AgentBridgeApp-win-arm64-Portable.zip'
    $zip = [IO.Compression.ZipFile]::Open($portable, 'Update')
    $zip.GetEntry('Agent Bridge.exe').Delete()
    $zip.Dispose()
    $threw = $false
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -RequireSignature -SignerEku $global:PpEku 6>$null } catch { $threw = $_.Exception.Message -like '*AgentBridgeApp-win-arm64-Portable.zip has no Agent Bridge.exe*' }
    Assert $threw 'a portable zip without its launcher passed'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
}

# What vpk 1.2.161 packs, exactly: a signed executable where it puts none is still refused.
foreach ($case in @(
        @{ Name = 'a portable zip with another executable at its root'; File = 'AgentBridgeApp-win-arm64-Portable.zip'; Entry = 'Helper.exe'; Says = '*AgentBridgeApp-win-arm64-Portable.zip has Helper.exe at its root, which vpk does not pack*' }
        @{ Name = 'a package with a second launcher stub'; File = 'AgentBridgeApp-0.1.600-win-arm64-full.nupkg'; Entry = 'lib/app/Other_ExecutionStub.exe'; Says = '*AgentBridgeApp-0.1.600-win-arm64-full.nupkg has a launcher stub Other_ExecutionStub.exe, not only Agent Bridge_ExecutionStub.exe*' }
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
        try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -RequireSignature -SignerEku $global:PpEku 6>$null } catch { $threw = $_.Exception.Message -like $case.Says; if (-not $threw) { throw "message: $($_.Exception.Message)" } }
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
    $zip.GetEntry('lib/app/Agent Bridge_ExecutionStub.exe').Delete()
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $extra, 'lib/app/AgentBridgeApp_ExecutionStub.exe')
    $zip.Dispose()
    $threw = $false
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -RequireSignature -SignerEku $global:PpEku 6>$null } catch { $threw = $_.Exception.Message -like "*has a launcher stub AgentBridgeApp_ExecutionStub.exe*has no lib$([IO.Path]::DirectorySeparatorChar)app$([IO.Path]::DirectorySeparatorChar)Agent Bridge_ExecutionStub.exe*"; if (-not $threw) { throw "message: $($_.Exception.Message)" } }
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
        try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit -RequireSignature -SignerEku $eku 6>$null } catch { $threw = $_.Exception.Message -like '*not an Artifact Signing identity validation EKU*' }
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
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
