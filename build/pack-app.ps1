# Builds the Windows app's Velopack release: the app, the host, its windowless launcher and
# the injector, published into one folder with one version, then packed. docs/app-windows.md.
#
#   pwsh build/pack-app.ps1 -Version 0.2.0            # out/app/publish and out/app/releases
#   pwsh build/pack-app.ps1 -Version 0.2.0 -NoPack    # the folder only
#
# Needs the .NET 10 SDK, vpk 1.2.161 (dotnet tool install -g vpk --version 1.2.161) and, for
# the NativeAOT injector, the MSVC linker (Visual Studio's C++ build tools). Nothing is signed:
# signing is a release step that needs a certificate (docs/app-windows.md, Signing).
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Out = (Join-Path $PSScriptRoot '..' 'out' 'app'),
    # A release build: the host reports itself as released rather than dev (hello's machine.dev).
    [switch]$Release,
    [switch]$NoPack
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Out = [IO.Path]::GetFullPath($Out)
$publish = Join-Path $Out 'publish'
$releases = Join-Path $Out 'releases'
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
Publish 'src/AgentBridge.App/AgentBridge.App.csproj' @()

# The .pri carries the compiled XAML: without it the installed app fails at startup with
# nothing but 0xC000027B (spike S3), while the build output runs fine.
$required = 'AgentBridge.exe', 'AgentBridge.pri', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'Assets\AgentBridge.ico', 'Assets\AgentBridge.png'
$missing = $required | Where-Object { -not (Test-Path (Join-Path $publish $_)) }
if ($missing) { throw "missing from the publish folder: $($missing -join ', ')" }
Write-Host "published $Version to $publish"

if ($NoPack) { return }
& vpk pack -u AgentBridgeApp -v $Version -p $publish -e AgentBridge.exe -o $releases --packTitle 'Agent Bridge' --icon (Join-Path $publish 'Assets\AgentBridge.ico')
if ($LASTEXITCODE) { throw 'vpk pack failed' }
Write-Host "packed $Version to $releases"
