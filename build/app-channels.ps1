# The Windows app's Velopack channels: one per architecture, each with its own feed, and the file
# names vpk gives each channel's assets. Dot-sourced by pack-app.ps1 and publish-prerelease.ps1.
# docs/app-windows.md, "Releases and updates".
#
# x64 stays on Velopack's default channel, `win`, as every copy installed before Arm64 builds
# existed was packed: an installed copy looks only for its own channel's releases.<channel>.json,
# so moving x64 to another channel would leave those copies on their last version for good. Arm64
# has `win-arm64`. vpk names a non-default channel's packages after it (DefaultName.cs in
# Velopack 1.2.161), so the two never share a file name in one GitHub release:
#
#   channel     feed                     full package                        installer
#   win         releases.win.json        AgentBridgeApp-<v>-full.nupkg       AgentBridgeApp-win-Setup.exe
#   win-arm64   releases.win-arm64.json  AgentBridgeApp-<v>-win-arm64-full.nupkg  AgentBridgeApp-win-arm64-Setup.exe
#Requires -Version 7

$AppPackId = 'AgentBridgeApp'

# The product's name and brand art as the app's project gives them (Product, AppIconFile,
# AppLogoFile in AgentBridge.App.csproj): the installer's title, the Start shortcut's and Apps &
# Settings' name, the setup icon and release titles name the app as the app itself does (#537).
# Unlike the pack id above, which installed copies find their updates by, these are display only.
$AppProject = [xml](Get-Content -Raw (Join-Path $PSScriptRoot '..\src\AgentBridge.App\AgentBridge.App.csproj'))
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

# What vpk pack names each asset of a channel (Velopack's DefaultName): packages carry the channel
# only when it is not the default; the installer and portable zip always do.
function Get-AppAssetName([string]$Channel, [string]$Type, [string]$Version) {
    $suffix = if ($Channel -ceq 'win') { '' } else { "-$Channel" }
    switch ($Type) {
        'Full' { "$AppPackId-$Version$suffix-full.nupkg" }
        'Delta' { "$AppPackId-$Version$suffix-delta.nupkg" }
        'Installer' { "$AppPackId-$Channel-Setup.exe" }
        'Portable' { "$AppPackId-$Channel-Portable.zip" }
        default { throw "unknown asset type '$Type'" }
    }
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
