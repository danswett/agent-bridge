# The Windows app's Velopack channels: one per architecture, each with its own feed,
# public downloads and stable package names. Dot-sourced by pack-app.ps1 and publish-prerelease.ps1.
# docs/app-windows.md, "Releases and updates".
#
# x64 stays on Velopack's default channel, `win`, as every copy installed before Arm64 builds
# existed was packed: an installed copy looks only for its own channel's releases.<channel>.json,
# so moving x64 to another channel would leave those copies on their last version for good. Arm64
# has `win-arm64`. vpk names a non-default channel's packages after it (DefaultName.cs in
# Velopack 1.2.161), so the two never share a file name in one GitHub release:
#
#   channel     feed                     full package                        installer
#   win         releases.win.json        AgentBridgeApp-<v>-full.nupkg       Slipgates-win-Setup.exe
#   win-arm64   releases.win-arm64.json  AgentBridgeApp-<v>-win-arm64-full.nupkg  Slipgates-win-arm64-Setup.exe
#Requires -Version 7

$AppPackId = 'AgentBridgeApp'

# The product's name and brand art as the app's project gives them (Product, AppIconFile,
# AppLogoFile in AgentBridge.App.csproj): the installer's title, the Start shortcut's and Apps &
# Settings' name, the setup icon and release titles name the app as the app itself does (#537).
# Unlike the pack id above, which installed copies find their updates by, these name only the
# displayed product and public downloads, not its packages, feeds or install folder.
$AppProject = [xml](Get-Content -Raw (Join-Path $PSScriptRoot '..' 'src' 'AgentBridge.App' 'AgentBridge.App.csproj'))
function Get-AppProjectProperty([string]$Name) {
    $value = @($AppProject.Project.PropertyGroup | ForEach-Object { $_.$Name } | Where-Object { $_ })
    if ($value.Count -ne 1) { throw "AgentBridge.App.csproj sets $Name $($value.Count) times, not once" }
    [string]$value[0]
}
$AppProductName = Get-AppProjectProperty 'Product'
$AppIconFile = Get-AppProjectProperty 'AppIconFile'
$AppLogoFile = Get-AppProjectProperty 'AppLogoFile'

# Runtime identifier, channel and the PE machine its executables carry.
$AppChannels = [ordered]@{
    'win-x64'   = [pscustomobject]@{ Runtime = 'win-x64'; Channel = 'win'; Machine = 'x64' }
    'win-arm64' = [pscustomobject]@{ Runtime = 'win-arm64'; Channel = 'win-arm64'; Machine = 'arm64' }
}

function Get-AppChannel([string]$Runtime) {
    if (-not $AppChannels.Contains($Runtime)) { throw "no channel for the runtime '$Runtime' (one of $($AppChannels.Keys -join ', '))" }
    $AppChannels[$Runtime]
}

function Get-AppChannelByName([string]$Channel) {
    $found = @($AppChannels.Values | Where-Object Channel -CEQ $Channel)
    if ($found.Count -ne 1) { throw "unknown channel '$Channel' (one of $(($AppChannels.Values | ForEach-Object Channel) -join ', '))" }
    $found[0]
}

# Packages retain Velopack's DefaultName and pack id. Public downloads use the product name;
# Set-AppDownloadNames changes vpk's output and upload index before either is consumed.
function Get-AppAssetName([string]$Channel, [string]$Type, [string]$Version) {
    $suffix = if ($Channel -ceq 'win') { '' } else { "-$Channel" }
    switch ($Type) {
        'Full' { "$AppPackId-$Version$suffix-full.nupkg" }
        'Delta' { "$AppPackId-$Version$suffix-delta.nupkg" }
        'Installer' { "$AppProductName-$Channel-Setup.exe" }
        'Portable' { "$AppProductName-$Channel-Portable.zip" }
        default { throw "unknown asset type '$Type'" }
    }
}

# vpk 1.2.161 derives these two names from PackId, with no separate filename option. Its upload
# reads assets.<channel>.json, whereas releases.<channel>.json lists only unchanged nupkgs.
function Set-AppDownloadNames([string]$Dir, [string]$Channel) {
    $null = Get-AppChannelByName $Channel
    $index = Join-Path $Dir "assets.$Channel.json"
    $assets = @(Get-Content -LiteralPath $index -Raw | ConvertFrom-Json)
    $renames = @(foreach ($type in 'Installer', 'Portable') {
        $entries = @($assets | Where-Object Type -CEQ $type)
        if ($entries.Count -ne 1) { throw "assets.$Channel.json has $($entries.Count) $type entries, not one" }
        $tail = if ($type -ceq 'Installer') { 'Setup.exe' } else { 'Portable.zip' }
        $packedName = "$AppPackId-$Channel-$tail"
        if ($entries[0].RelativeFileName -cne $packedName) {
            throw "assets.$Channel.json has unexpected $type name '$($entries[0].RelativeFileName)', not $packedName"
        }
        $source = Join-Path $Dir $packedName
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "missing packed download: $packedName" }
        $name = Get-AppAssetName $Channel $type ''
        if (Test-Path -LiteralPath (Join-Path $Dir $name) -PathType Container) { throw "packed download destination is a directory: $name" }
        [pscustomobject]@{ Entry = $entries[0]; Source = $source; Name = $name }
    })
    foreach ($rename in $renames) {
        Move-Item -LiteralPath $rename.Source -Destination (Join-Path $Dir $rename.Name) -Force
        $rename.Entry.RelativeFileName = $rename.Name
    }
    ConvertTo-Json -InputObject $assets -Depth 10 | Set-Content -LiteralPath $index -Encoding utf8NoBOM
}

# A channel's releases.<channel>.json against the packages in $Dir: one entry for each of $Types
# (Full, and Delta when there is one) of $Version, named as vpk names it, of this pack id, that
# version and that type, with each package's SHA256 and size, and nothing else. This, not
# Authenticode, is what an installed copy checks a full or delta package by; the pack id, version
# and type are what it picks one by. $Version is the [semver] publish-prerelease.ps1 was given,
# which vpk writes as given.
function Get-AppFeedProblem([string]$FeedPath, [string]$Channel, [string]$Version, [string]$Dir, [string[]]$Types) {
    $feed = Get-Content -LiteralPath $FeedPath -Raw | ConvertFrom-Json
    $feedName = Split-Path $FeedPath -Leaf
    $expected = @{}
    foreach ($type in $Types) { $expected[(Get-AppAssetName $Channel $type $Version)] = $type }
    $listed = @{}
    foreach ($entry in @($feed.Assets)) {
        # A field the feed leaves out reads as empty, so it is named as wrong rather than tripping strict mode.
        $asset = @{}
        foreach ($field in 'PackageId', 'FileName', 'Version', 'Type', 'SHA256', 'Size') {
            $property = $entry.PSObject.Properties[$field]
            $asset[$field] = if ($property) { [string]$property.Value } else { '' }
        }
        $name = $asset.FileName
        if (-not $expected.ContainsKey($name)) { "$feedName lists $name, which is not this release's"; continue }
        if ($listed.ContainsKey($name)) { "$feedName lists $name more than once"; continue }
        $listed[$name] = $true
        if ($asset.PackageId -cne $AppPackId) { "${feedName}: ${name}'s package id is '$($asset.PackageId)', not $AppPackId" }
        if ($asset.Version -cne $Version) { "${feedName}: ${name}'s version is '$($asset.Version)', not $Version" }
        if ($asset.Type -cne $expected[$name]) { "${feedName}: ${name}'s type is '$($asset.Type)', not $($expected[$name])" }
        $path = Join-Path $Dir $name
        if (-not (Test-Path -LiteralPath $path)) { "${feedName}: no $name beside it"; continue }
        $file = Get-Item -LiteralPath $path
        if ($asset.SHA256 -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { "${feedName}: ${name}'s SHA256 is not the file's" }
        if ($asset.Size -cne [string]$file.Length) { "${feedName}: ${name}'s size is not the file's" }
    }
    foreach ($name in $expected.Keys) { if (-not $listed.ContainsKey($name)) { "$feedName does not list $name" } }
}

# The version of one of this channel's full packages, from its file name, or nothing: another
# channel's package is not this one's (x64's pattern would otherwise read
# AgentBridgeApp-0.1.600-win-arm64-full.nupkg as the pre-release version 0.1.600-win-arm64).
function Get-AppFullVersion([string]$Channel, [string]$FileName) {
    $suffix = if ($Channel -ceq 'win') { '' } else { [regex]::Escape("-$Channel") }
    if ($FileName -match "^$AppPackId-(\d+\.\d+\.\d+)$suffix-full\.nupkg$") { [semver]$Matches[1] }
}

# The machine a PE image is built for, from its COFF header: x64, arm64, x86, or what it is not.
function Get-PeMachine([byte[]]$Bytes) {
    if ($Bytes.Length -lt 0x40 -or $Bytes[0] -ne 0x4D -or $Bytes[1] -ne 0x5A) { return 'not a PE image' }
    $pe = [BitConverter]::ToInt32($Bytes, 0x3C)
    if ($pe -lt 0 -or $pe + 6 -gt $Bytes.Length -or [BitConverter]::ToUInt32($Bytes, $pe) -ne 0x00004550) { return 'not a PE image' }
    switch ([BitConverter]::ToUInt16($Bytes, $pe + 4)) {
        0x8664 { 'x64' }
        0xAA64 { 'arm64' }
        0x014C { 'x86' }
        default { 'machine 0x{0:X4}' -f $_ }
    }
}

# The machine of the app's own executable inside a full package, as vpk packs it (lib/app/).
function Get-PackageMachine([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
    try {
        $entry = $zip.GetEntry('lib/app/AgentBridge.exe')
        if (-not $entry) { return 'no lib/app/AgentBridge.exe' }
        $memory = [IO.MemoryStream]::new()
        $stream = $entry.Open()
        try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
        Get-PeMachine $memory.ToArray()
    } finally { $zip.Dispose() }
}
