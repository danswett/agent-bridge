# Same-run installer provenance; the large release files use app's existing artifact.
#Requires -Version 7
. (Join-Path $PSScriptRoot 'app-channels.ps1')
. (Join-Path $PSScriptRoot 'app-notices.ps1')

function Get-AppInstallerReleaseHashes([string]$Releases, [string]$Version, [string]$Runtime) {
    $channel = (Get-AppChannel $Runtime).Channel
    foreach ($kind in 'Installer', 'Full', 'Portable') {
        $name = Get-AppAssetName $channel $kind $Version
        $path = Get-AppNoticePath $Releases $name
        @{ name = $name; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}

function Write-AppInstallerInput(
    [string]$Publish, [string]$Releases, [string]$Out,
    [string]$Version, [ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [ValidatePattern('^[1-9][0-9]*$')][string]$RunId
) {
    $ErrorActionPreference = 'Stop'
    $notices = Get-AppNotices $Publish $Version $Runtime
    if ($notices.commit -cne $Commit) { throw 'installer source notice commit does not match the checked-out commit' }
    $files = @(Get-AppInstallerReleaseHashes $Releases $Version $Runtime)
    if (Test-Path -LiteralPath $Out) { throw "installer input destination already exists: $Out" }
    foreach ($file in @($notices.files.path) + 'ThirdPartyNotices/manifest.json') {
        $source = Get-AppNoticePath $Publish $file
        $destination = Join-Path $Out "publish/$file"
        New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
    # Run, not attempt: re-running only a failed installer must accept app's successful input.
    @{
        schemaVersion = 1; version = $Version; runtime = $Runtime; commit = $Commit; runId = $RunId
        files = $files
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Out 'installer-input.json') -Encoding utf8NoBOM
}

function Assert-AppInstallerInput(
    [string]$InputPath, [string]$Releases,
    [string]$Version, [ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
    [ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [ValidatePattern('^[1-9][0-9]*$')][string]$RunId
) {
    $ErrorActionPreference = 'Stop'
    $path = Get-AppNoticePath $InputPath 'installer-input.json'
    $inputIdentity = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if ($inputIdentity.schemaVersion -ne 1) { throw 'unsupported installer input schema' }
    foreach ($field in @{ version = $Version; runtime = $Runtime; commit = $Commit; runId = $RunId }.GetEnumerator()) {
        if ($inputIdentity[$field.Key] -cne $field.Value) { throw "installer input identity mismatch: $($field.Key)" }
    }
    $notices = Get-AppNotices (Join-Path $InputPath 'publish') $Version $Runtime
    if ($notices.commit -cne $Commit) { throw 'installer input notice commit mismatch' }
    $files = @(Get-AppInstallerReleaseHashes $Releases $Version $Runtime)
    if (@($inputIdentity.files).Count -ne $files.Count) { throw 'installer input release inventory mismatch' }
    foreach ($file in $files) {
        $matches = @($inputIdentity.files | Where-Object name -CEQ $file.name)
        if ($matches.Count -ne 1 -or $matches[0].sha256 -cne $file.sha256) {
            throw "installer input release hash mismatch: $($file.name)"
        }
    }
    Write-Output "Installer input: run=$RunId commit=$Commit runtime=$Runtime version=$Version"
    $files | ForEach-Object { Write-Output "$($_.name): sha256=$($_.sha256)" }
}
