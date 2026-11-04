# Builds the Windows app's Velopack release: the app, the host, its windowless launcher, the
# injector and the agents' hook, published into one folder with one version, then packed.
# docs/app-windows.md, docs/host-hooks.md.
#
#   pwsh build/pack-app.ps1 -Version 0.2.0            # out/app/publish and out/app/releases
#   pwsh build/pack-app.ps1 -Version 0.2.0 -NoPack    # the folder only
#   pwsh build/pack-app.ps1 -Version 0.2.0 -DeltaFrom https://github.com/danswett/agent-bridge
#                                                     # and a delta from the latest release
#   pwsh build/pack-app.ps1 -Version 0.2.0 -Runtime win-arm64
#                                                     # Arm64, in out/app-arm64 (build/app-channels.ps1)
#
# Needs the .NET 10 SDK, vpk 1.2.161 (dotnet tool install -g vpk --version 1.2.161) and, for
# the NativeAOT injector and hook, the MSVC linker (Visual Studio's C++ build tools, with the
# ARM64 build tools for -Runtime win-arm64 on an x64 machine; CI builds Arm64 on Arm64). Unsigned
# unless -SignParams or -AzureTrustedSignFile is given (docs/app-windows.md, Signing). With
# -RequireSignature -SignerEku, it refuses to start without a signing hook, and fails unless every
# file packed carries the pinned publisher's timestamped signature (build/app-signing.ps1).
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    # The architecture, and so the Velopack channel, the package is for (build/app-channels.ps1).
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    # out/app for x64, as before Arm64 builds; out/app-arm64 for Arm64.
    [string]$Out,
    # A release build: the host reports itself as released rather than dev (hello's machine.dev).
    [switch]$Release,
    [switch]$NoPack,
    # A GitHub repository whose latest release (pre-releases included) the new package gets a
    # delta from. Each full package is about 100 MB; a delta is what an installed copy downloads.
    # Uses GH_TOKEN when set, for the API's rate limit. Empties the releases folder first.
    [string]$DeltaFrom,
    # The signing hook, passed to vpk pack as they are: signtool arguments, or an Azure Trusted
    # Signing metadata.json. vpk signs every executable in the package and Setup.exe.
    [string]$SignParams,
    [string]$AzureTrustedSignFile,
    # Signing is expected (CI's APP_SIGNING): vpk only warns when it signs nothing, so this checks
    # what it packed against the publisher's Artifact Signing identity EKU, and fails if any is not.
    [switch]$RequireSignature,
    [string]$SignerEku,
    # Markdown the package carries as its release notes (build/release-notes.ps1): what an
    # installed copy's toast says changed after it updates.
    [string]$ReleaseNotes
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'app-channels.ps1')
if ($RequireSignature) {
    # Before building anything: a signed release that cannot be signed must not become an unsigned one.
    . (Join-Path $PSScriptRoot 'app-signing.ps1')
    Assert-AppSignerEku $SignerEku
    if ($NoPack) { throw '-RequireSignature checks the package, and -NoPack packs none' }
    if (-not $SignParams -and -not $AzureTrustedSignFile) { throw 'signing is required, but neither -AzureTrustedSignFile nor -SignParams was given' }
    if ($AzureTrustedSignFile -and -not (Test-Path -LiteralPath $AzureTrustedSignFile -PathType Leaf)) { throw "signing is required, but there is no $AzureTrustedSignFile" }
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target = Get-AppChannel $Runtime
if (-not $Out) { $Out = Join-Path $root 'out' $(if ($Runtime -eq 'win-x64') { 'app' } else { 'app-arm64' }) }
$Out = [IO.Path]::GetFullPath($Out)
$publish = Join-Path $Out 'publish'
$releases = Join-Path $Out 'releases'
$packId = $AppPackId
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

function Publish([string]$project, [string[]]$extra) {
    & dotnet publish (Join-Path $root $project) -c Release -r $Runtime -o $publish --nologo "-p:Version=$Version" @extra
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
$required = 'AgentBridge.exe', 'AgentBridge.pri', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe', $AppIconFile, $AppLogoFile
# The bundled fonts and their licences (Design/DesignKeys.cs FontFiles): the app names each font
# by its file, so one left out would quietly fall back.
$keys = Get-Content (Join-Path $root 'src' 'AgentBridge.App' 'Design' 'DesignKeys.cs') -Raw
$required += [regex]::Matches($keys, '"(Assets/Fonts/[^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '/', '\' }
if (-not ($required -like 'Assets\Fonts\*.ttc')) { throw 'Design/DesignKeys.cs lists no font files' }
$missing = $required | Where-Object { -not (Test-Path (Join-Path $publish $_)) }
if ($missing) { throw "missing from the publish folder: $($missing -join ', ')" }
# Every executable for the architecture asked for: an x64 one in an Arm64 package would run
# emulated, if at all, and the injector's console calls are where emulation is least trusted.
$foreign = foreach ($exe in $required -like '*.exe') {
    $machine = Get-PeMachine ([IO.File]::ReadAllBytes((Join-Path $publish $exe)))
    if ($machine -ne $target.Machine) { "$exe ($machine)" }
}
if ($foreign) { throw "not built for $($target.Machine): $($foreign -join ', ')" }
# The build identity decides whether the host's Home Assistant connector may write to a real
# Home Assistant (G6). It must be what was asked for, whatever the build cache held.
$identity = & (Join-Path $PSScriptRoot 'host-identity.ps1') (Join-Path $publish 'agent-bridge-host.dll')
$wanted = if ($Release) { 'release' } else { 'dev' }
if ($identity -ne $wanted) { throw "the host was built as a $identity build, not a $wanted build" }
Write-Host "published $Version ($identity build, $Runtime) to $publish"

if ($NoPack) { return }

if ($DeltaFrom) {
    # vpk pack builds a delta from the newest full package in the output folder that is older
    # than the new one, so the previous release's goes there first. A delta is an optimisation:
    # when the download fails, or the latest release is not older (a pull request built before a
    # newer merge published), this packs the full package alone rather than failing the build.
    # vpk would also stop at a prompt, which in CI is a failure, over a same-or-newer package.
    # Only the package checked here may be a base: one left in the output folder by an earlier
    # pack would be taken just the same, so the folder starts empty.
    if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
    $previous = Join-Path $Out 'previous'
    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
    # Only from this package's own channel: the other architecture's package is no base for any
    # copy that would download this delta.
    $download = @('download', 'github', '--repoUrl', $DeltaFrom, '--pre', '--channel', $target.Channel, '-o', $previous)
    if ($env:GH_TOKEN) { $download += @('--token', $env:GH_TOKEN) }
    & vpk @download
    $base = if ($LASTEXITCODE -eq 0 -and (Test-Path $previous)) {
        Get-ChildItem $previous -Filter "$packId-*-full.nupkg" | ForEach-Object {
            $v = Get-AppFullVersion $target.Channel $_.Name
            if ($v) { [pscustomobject]@{ File = $_; Version = $v } }
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

$more = @()
if ($SignParams) { $more += @('--signParams', $SignParams) }
if ($AzureTrustedSignFile) { $more += @('--azureTrustedSignFile', $AzureTrustedSignFile) }
if ($ReleaseNotes) { $more += @('--releaseNotes', [IO.Path]::GetFullPath($ReleaseNotes)) }
# The channel decides, for good, which feed an installed copy reads (build/app-channels.ps1). x64
# is packed exactly as before Arm64 builds: the default channel and no --runtime, so its
# Update.exe and Setup.exe stay the ones installed copies already have. Arm64 names its runtime,
# so vpk gives it Velopack's Arm64 Update.exe, Setup.exe and stub, and the package records the
# machine it is for.
if ($Runtime -ne 'win-x64') { $more += @('--runtime', $Runtime) }
& vpk pack -u $packId -v $Version -c $target.Channel -p $publish -e AgentBridge.exe -o $releases --packTitle $AppProductName --icon (Join-Path $publish $AppIconFile) @more
if ($LASTEXITCODE) { throw 'vpk pack failed' }
if ($RequireSignature) {
    $check = Test-AppReleaseSignature $releases $target.Channel $Version $SignerEku (Join-Path $Out 'signature-check')
    if ($check.Problems) { throw "signing is required, but the packed release is not signed as it must be:`n  $($check.Problems -join "`n  ")" }
    Write-Host "every file packed is signed by $($check.Signer), with a timestamp"
}
Write-Host "packed $Version for the $($target.Channel) channel to $releases"