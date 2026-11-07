# Converts authenticated GitHub release metadata into the site's static download contract.
# docs/beta-release-manifest.md. Offline unless -Publish is explicitly supplied.
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseJson,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string]$Repository,
    [Parameter(Mandatory)][string]$Out,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'app-channels.ps1')

$release = Get-Content -LiteralPath $ReleaseJson -Raw | ConvertFrom-Json -AsHashtable
if ($release -isnot [Collections.IDictionary]) { throw 'release metadata must be one object' }
if ($release['draft'] -isnot [bool] -or $release['draft']) { throw 'a draft is not a published Beta' }
if ($release['prerelease'] -isnot [bool] -or -not $release['prerelease']) { throw 'Beta metadata must describe a pre-release' }
if ($release['tag_name'] -isnot [string] -or $release['tag_name'] -cnotmatch '^v((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$') {
    throw 'Beta tag must be v<major>.<minor>.<patch>'
}
$version = ([semver]$Matches[1]).ToString()
$tag = $release['tag_name']
$releaseUrl = "https://github.com/$Repository/releases/tag/$tag"
if ($release['html_url'] -isnot [string] -or $release['html_url'] -cne $releaseUrl) {
    throw 'release notes URL does not belong to this repository and tag'
}

$date = $release['published_at']
# PowerShell versions differ on whether ConvertFrom-Json turns ISO dates into DateTime.
if ($date -is [datetime] -or $date -is [datetimeoffset]) {
    $publishedAt = [datetimeoffset]$date
} elseif ($date -is [string] -and $date -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$') {
    $publishedAt = [datetimeoffset]::Parse($date, [Globalization.CultureInfo]::InvariantCulture)
} else {
    throw 'published_at must be an ISO timestamp for a published release'
}
if ($release['assets'] -isnot [array]) { throw 'release assets must be an array' }
$downloads = [ordered]@{}
foreach ($target in $AppChannels.Values) {
    $name = Get-AppAssetName $target.Channel 'Installer' $version
    $found = @($release['assets'] | Where-Object {
        $_ -is [Collections.IDictionary] -and $_['name'] -ceq $name
    })
    if ($found.Count -ne 1) { throw "expected exactly one installer $name, found $($found.Count)" }
    $asset = $found[0]
    if ($asset['state'] -cne 'uploaded') { throw "installer $name is not uploaded" }
    if (($asset['size'] -isnot [long] -and $asset['size'] -isnot [int]) -or $asset['size'] -le 0) {
        throw "installer $name must have a positive integer size"
    }
    $url = "https://github.com/$Repository/releases/download/$tag/$name"
    if ($asset['browser_download_url'] -isnot [string] -or $asset['browser_download_url'] -cne $url) {
        throw "installer $name URL does not belong to this repository and tag"
    }
    if ($asset['digest'] -isnot [string] -or $asset['digest'] -cnotmatch '^sha256:[0-9a-f]{64}$') {
        throw "installer $name must have GitHub's uploaded-asset SHA-256 digest"
    }
    $downloads[$target.Runtime] = [ordered]@{
        fileName = $name
        url = $url
        sizeBytes = $asset['size']
        sha256 = $asset['digest'].Substring(7)
    }
}
$manifest = [ordered]@{
    schemaVersion = 1
    channel = 'beta'
    version = $version
    tag = $tag
    publishedAt = $publishedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    releaseNotesUrl = $releaseUrl
    downloads = $downloads
}
$path = [IO.Path]::GetFullPath($Out)
if ($Publish -and [IO.Path]::GetFileName($path) -cne 'beta-release.json') {
    throw 'publishing requires an output file named beta-release.json'
}
New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
Write-Host "Beta download manifest: $tag, $($downloads.Keys -join ', ') -> $path"
if ($Publish) {
    $existing = @($release['assets'] | Where-Object {
        $_ -is [Collections.IDictionary] -and $_['name'] -ceq 'beta-release.json'
    })
    if ($existing.Count -gt 1) { throw 'release has multiple beta-release.json assets; nothing was replaced' }
    if ($existing.Count -eq 1) {
        if ($existing[0]['state'] -cne 'uploaded') { throw 'existing beta-release.json is not uploaded; nothing was replaced' }
        $checkDir = Join-Path (Split-Path $path) "beta-manifest-$([guid]::NewGuid().ToString('N'))"
        $checkPath = Join-Path $checkDir 'beta-release.json'
        New-Item -ItemType Directory $checkDir | Out-Null
        try {
            & gh release download $tag --repo $Repository --pattern beta-release.json --dir $checkDir
            if ($LASTEXITCODE) { throw "existing Beta manifest download failed ($LASTEXITCODE); nothing was replaced" }
            if ((Get-FileHash -LiteralPath $checkPath -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) {
                throw 'existing Beta manifest does not match the published release metadata; nothing was replaced'
            }
            Write-Host 'Existing Beta manifest matches; retained without uploading'
        } finally {
            if (Test-Path -LiteralPath $checkPath) { Remove-Item -LiteralPath $checkPath }
            Remove-Item -LiteralPath $checkDir
        }
    } else {
        # Without --clobber, even an asset added after the metadata read cannot be deleted.
        & gh release upload $tag $path --repo $Repository
        if ($LASTEXITCODE) { throw "Beta manifest upload failed ($LASTEXITCODE); no existing asset was replaced" }
    }
}
