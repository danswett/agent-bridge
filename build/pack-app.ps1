# Builds the Windows app's Velopack release: the app, the host, its windowless launcher, the
# injector and the agents' hook, published into one folder with one version, then packed.
# docs/app-windows.md, docs/host-hooks.md.
#
#   pwsh build/pack-app.ps1 -Version 0.2.0            # out/app/publish and out/app/releases
#   pwsh build/pack-app.ps1 -Version 0.2.0 -NoPack    # the folder only
#   pwsh build/pack-app.ps1 -Version 0.2.0 -DeltaFrom https://github.com/danswett/agent-bridge
#                                                     # and a delta from the latest release
#
# Needs the .NET 10 SDK, vpk 1.2.161 (dotnet tool install -g vpk --version 1.2.161) and, for
# the NativeAOT injector and hook, the MSVC linker (Visual Studio's C++ build tools). Unsigned
# unless -SignParams or -AzureTrustedSignFile is given (docs/app-windows.md, Signing).
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Out = (Join-Path $PSScriptRoot '..' 'out' 'app'),
    # A release build: the host reports itself as released rather than dev (hello's machine.dev).
    [switch]$Release,
    [switch]$NoPack,
    # A GitHub repository whose latest release (pre-releases included) the new package gets a
    # delta from. Each full package is about 100 MB; a delta is what an installed copy downloads.
    # Uses GH_TOKEN when set, for the API's rate limit.
    [string]$DeltaFrom,
    # The signing hook, passed to vpk pack as they are: signtool arguments, or an Azure Trusted
    # Signing metadata.json. vpk signs every executable in the package and Setup.exe.
    [string]$SignParams,
    [string]$AzureTrustedSignFile
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Out = [IO.Path]::GetFullPath($Out)
$publish = Join-Path $Out 'publish'
$releases = Join-Path $Out 'releases'
$packId = 'AgentBridgeApp'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

function Publish([string]$project, [string[]]$extra) {
    & dotnet publish (Join-Path $root $project) -c Release -r win-x64 -o $publish --nologo "-p:Version=$Version" @extra
    if ($LASTEXITCODE) { throw "publishing $project failed" }
}

# One folder: the host runs from beside the app, so an app update is a host update. The
# self-contained runtime files the four share are the same SDK's, so whichever lands last is
# identical to the others; the app goes last, as the one with the most.
$hostFlags = @('--self-contained')
if ($Release) { $hostFlags += '-p:AgentBridgeRelease=true' }
Publish 'src/AgentBridge.Host/AgentBridge.Host.csproj' $hostFlags
Publish 'src/AgentBridge.HostLauncher/AgentBridge.HostLauncher.csproj' @('--self-contained')
Publish 'src/AgentBridge.Injector/AgentBridge.Injector.csproj' @()
Publish 'src/AgentBridge.Hook/AgentBridge.Hook.csproj' @()
Publish 'src/AgentBridge.App/AgentBridge.App.csproj' @()

# The .pri carries the compiled XAML: without it the installed app fails at startup with
# nothing but 0xC000027B (spike S3), while the build output runs fine.
$required = 'AgentBridge.exe', 'AgentBridge.pri', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe', 'Assets\AgentBridge.ico', 'Assets\AgentBridge.png'
$missing = $required | Where-Object { -not (Test-Path (Join-Path $publish $_)) }
if ($missing) { throw "missing from the publish folder: $($missing -join ', ')" }
Write-Host "published $Version to $publish"

if ($NoPack) { return }

if ($DeltaFrom) {
    # vpk pack builds a delta from the newest full package in the output folder that is older
    # than the new one, so the previous release's goes there first. A delta is an optimisation:
    # when the download fails, or the latest release is not older (a pull request built before a
    # newer merge published), this packs the full package alone rather than failing the build.
    # vpk would also stop at a prompt, which in CI is a failure, over a same-or-newer package.
    $previous = Join-Path $Out 'previous'
    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
    $download = @('download', 'github', '--repoUrl', $DeltaFrom, '--pre', '--channel', 'win', '-o', $previous)
    if ($env:GH_TOKEN) { $download += @('--token', $env:GH_TOKEN) }
    & vpk @download
    $base = if ($LASTEXITCODE -eq 0 -and (Test-Path $previous)) {
        Get-ChildItem $previous -Filter "$packId-*-full.nupkg" | ForEach-Object {
            if ($_.Name -match "^$packId-(.+)-full\.nupkg$") { [pscustomobject]@{ File = $_; Version = [semver]$Matches[1] } }
        } | Sort-Object Version -Descending | Select-Object -First 1
    }
    if (-not $base) {
        Write-Warning "no release to build a delta from in $DeltaFrom; packing the full package only"
    } elseif ($base.Version -ge [semver]$Version) {
        Write-Warning "the latest release, $($base.Version), is not older than $Version; packing the full package only"
    } else {
        New-Item -ItemType Directory -Force $releases | Out-Null
        Copy-Item $base.File.FullName $releases -Force
        Write-Host "building a delta from $($base.Version)"
    }
}

$pack = @('pack', '-u', $packId, '-v', $Version, '-p', $publish, '-e', 'AgentBridge.exe', '-o', $releases, '--packTitle', 'Agent Bridge', '--icon', (Join-Path $publish 'Assets\AgentBridge.ico'))
if ($SignParams) { $pack += @('--signParams', $SignParams) }
if ($AzureTrustedSignFile) { $pack += @('--azureTrustedSignFile', $AzureTrustedSignFile) }
& vpk @pack
if ($LASTEXITCODE) { throw 'vpk pack failed' }
Write-Host "packed $Version to $releases"