# Publishes the Windows app's Velopack release folder (build/pack-app.ps1) as a GitHub
# pre-release, which installed copies update from, then keeps only the newest pre-releases.
# CI's publish job runs it for every push to main; the app job runs it with -DryRun on every
# build, which reads the repository's releases but uploads, publishes and deletes nothing.
# docs/app-windows.md, "Releases and updates".
#
#   pwsh build/publish-prerelease.ps1 -Version 0.1.600 -ReleasesDir out/app/releases `
#       -Repository danswett/agent-bridge -Commit <sha> [-DryRun]
#
# Needs gh and vpk 1.2.161, and GH_TOKEN with contents: write unless -DryRun.
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ReleasesDir,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Commit,
    # How many pre-releases stay. Velopack's GitHub source reads the newest ten releases' feeds,
    # so deltas never need more; the rest are for going back by hand.
    [ValidateRange(1, 1000)][int]$Keep = 20,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packId = 'AgentBridgeApp'
$tag = "v$Version"
$semver = [semver]$Version
$ReleasesDir = [IO.Path]::GetFullPath($ReleasesDir)
$work = Join-Path (Split-Path $ReleasesDir) 'publish-work'
$mode = if ($DryRun) { 'dry run: ' } else { '' }

function Invoke-Gh {
    # gh reports a failure on stderr and a nonzero exit; unchecked, it would read as no releases.
    $output = & gh @args
    if ($LASTEXITCODE) { throw "gh $($args[0]) $($args[1]) failed ($LASTEXITCODE)" }
    $output
}

function Write-Plan([string]$line, [switch]$Warn) {
    if ($Warn -and $env:GITHUB_ACTIONS) { Write-Host "::warning::$mode$line" } else { Write-Host "$mode$line" }
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "* $mode$line" }
}

function Get-Release {
    @(Invoke-Gh api "repos/$Repository/releases?per_page=100" --paginate --jq '.[] | {id, tag_name, draft, prerelease}' |
            Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Get-TagVersion([string]$tagName) {
    if ($tagName -match '^v(\d+\.\d+\.\d+)$') { [semver]$Matches[1] }
}

function Get-Megabyte([string]$path) { '{0:N1} MB' -f ((Get-Item $path).Length / 1MB) }

function Reset-Work {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force $work | Out-Null
}

# --- The folder is what vpk pack wrote: exactly one version's packages, named as clients expect.
$assetsFile = Join-Path $ReleasesDir 'assets.win.json'
if (-not (Test-Path $assetsFile)) { throw "no assets.win.json in ${ReleasesDir}: not the output of vpk pack" }
$assets = [Collections.Generic.List[object]]@(Get-Content $assetsFile -Raw | ConvertFrom-Json)
$expected = [ordered]@{ Full = "$packId-$Version-full.nupkg"; Installer = "$packId-win-Setup.exe"; Portable = "$packId-win-Portable.zip"; Delta = "$packId-$Version-delta.nupkg" }
foreach ($asset in $assets) {
    if (-not $expected.Contains([string]$asset.Type) -or $asset.RelativeFileName -ne $expected[[string]$asset.Type]) {
        throw "unexpected asset in assets.win.json: $($asset.Type) $($asset.RelativeFileName)"
    }
    if (-not (Test-Path (Join-Path $ReleasesDir $asset.RelativeFileName))) { throw "missing from ${ReleasesDir}: $($asset.RelativeFileName)" }
}
foreach ($type in 'Full', 'Installer', 'Portable') {
    if (-not ($assets | Where-Object Type -EQ $type)) { throw "assets.win.json has no $type ($($expected[$type]))" }
}
$fullPath = Join-Path $ReleasesDir $expected.Full
$deltaPath = Join-Path $ReleasesDir $expected.Delta

# --- Whether to publish: once per version, and never a version older than one already out.
$releases = Get-Release
$tagged = @(Invoke-Gh api "repos/$Repository/git/matching-refs/tags/$tag" --jq '.[].ref') -contains "refs/tags/$tag"
$published = $tagged -or [bool]($releases | Where-Object { -not $_.draft -and $_.tag_name -eq $tag })
$released = @($releases | Where-Object { -not $_.draft } | ForEach-Object { Get-TagVersion $_.tag_name } | Sort-Object -Descending)
$newer = @($released | Where-Object { $_ -gt $semver })
$publishing = -not $published -and -not $newer
if ($published) {
    Write-Plan "$tag is already published; nothing to upload"
} elseif ($newer) {
    # Two merges close together: the later one's build published first. Installed copies take
    # the highest version, so this one would only be noise, and a delta base nobody has.
    Write-Plan "$tag is older than the published v$($newer[0]); not publishing it"
}

function Test-Delta([string]$basePath) {
    # Right only if it rebuilds this version's full package, byte for byte, from the package of
    # the release installed copies are on; vpk delta patch reproduces it exactly.
    $patched = Join-Path $work 'patched.nupkg'
    Remove-Item $patched -ErrorAction SilentlyContinue
    & vpk delta patch --base $basePath --patch $deltaPath --output $patched | Out-Host
    $LASTEXITCODE -eq 0 -and (Test-Path $patched) -and (Get-FileHash $patched).Hash -eq (Get-FileHash $fullPath).Hash
}

function Update-Delta {
    # The app job built its delta from whatever release was latest when it packed. Another merge
    # may have published since (this runs under the publish lock, so that one is now final), and a
    # delta from an older base would fail on every copy that took it. Check it against the latest
    # release, rebuild it from that one if it is wrong or missing, and report why when neither works.
    Reset-Work
    $latest = $released | Select-Object -First 1
    if (-not $latest) { return 'there is no earlier release' }
    $basePath = Join-Path $ReleasesDir "$packId-$latest-full.nupkg"
    if (-not (Test-Path $basePath)) {
        $download = @('download', 'github', '--repoUrl', "https://github.com/$Repository", '--pre', '--channel', 'win', '-o', $work)
        if ($env:GH_TOKEN) { $download += @('--token', $env:GH_TOKEN) }
        & vpk @download | Out-Host
        $basePath = Join-Path $work "$packId-$latest-full.nupkg"
        if ($LASTEXITCODE -or -not (Test-Path $basePath)) { return "v$latest's full package could not be downloaded" }
    }
    if ((Test-Path $deltaPath) -and (Test-Delta $basePath)) { return "@v$latest" }
    Write-Plan "rebuilding the delta from v$latest"
    Remove-Item $deltaPath -ErrorAction SilentlyContinue
    & vpk delta generate --base $basePath --new $fullPath --output $deltaPath | Out-Host
    if ($LASTEXITCODE -or -not (Test-Path $deltaPath) -or -not (Test-Delta $basePath)) { return "no delta from v$latest rebuilds the full package" }
    "@v$latest"
}

if ($publishing) {
    $deltaResult = Update-Delta
    $deltaEntry = $assets | Where-Object Type -EQ 'Delta'
    if ($deltaResult -like '@*') {
        if (-not $deltaEntry) { $assets.Add([pscustomobject]@{ RelativeFileName = $expected.Delta; Type = 'Delta' }) }
        $size = "a $(Get-Megabyte $deltaPath) delta from $($deltaResult.Substring(1)) beside the $(Get-Megabyte $fullPath) full package"
    } else {
        # A wrong delta costs every installed copy a failed patch before the full download; none
        # costs only the download.
        if ($deltaEntry) { [void]$assets.Remove($deltaEntry) }
        Remove-Item $deltaPath -ErrorAction SilentlyContinue
        Write-Plan "publishing the full package alone: $deltaResult" -Warn
        $size = "the $(Get-Megabyte $fullPath) full package only, no delta"
    }
    ConvertTo-Json @($assets) -Compress | Set-Content $assetsFile -NoNewline
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }

    # vpk upload github sends the build's assets, a releases.win.json and RELEASES of this
    # version alone; assets.win.json goes up too, as on the first release published by hand.
    $uploads = @($assets.RelativeFileName) + 'releases.win.json', 'RELEASES', 'assets.win.json'
    Write-Plan "$tag from ${Commit}, ${size}: $($uploads -join ', ')"
}

if ($DryRun) {
    # vpk itself reads the folder, through the same build-assets file the GitHub upload reads,
    # into a scratch feed beside it: the nearest a pull request can come to uploading.
    Reset-Work
    & vpk upload local --outputDir $ReleasesDir --channel win --path $work | Out-Host
    if ($LASTEXITCODE) { throw 'vpk upload local failed: vpk cannot upload this folder' }
    $feed = Get-Content (Join-Path $work 'releases.win.json') -Raw | ConvertFrom-Json
    if (-not ($feed.Assets | Where-Object { $_.Version -eq $Version -and $_.Type -eq 'Full' })) { throw "vpk's feed has no full package of $Version" }
    if ((Test-Path $deltaPath) -and -not ($feed.Assets | Where-Object { $_.Version -eq $Version -and $_.Type -eq 'Delta' })) { throw "vpk's feed has no delta of $Version" }
    Remove-Item $work -Recurse -Force
}

if ($publishing) {
    foreach ($leftover in @($releases | Where-Object { $_.draft -and $_.tag_name -eq $tag })) {
        # A draft is all a failed attempt leaves (it is published only after every upload), and
        # vpk refuses a tag that has one.
        Write-Plan "deleting the draft $tag left by an earlier attempt"
        if (-not $DryRun) { Invoke-Gh api -X DELETE "repos/$Repository/releases/$($leftover.id)" | Out-Null }
    }
    if (-not $DryRun) {
        if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is needed to publish' }
        # As a draft first: clients never see a release that is missing its notes or a file.
        & vpk upload github --outputDir $ReleasesDir --channel win --repoUrl "https://github.com/$Repository" --token $env:GH_TOKEN --pre --releaseName "Agent Bridge $Version (pre-release)" --tag $tag --targetCommitish $Commit | Out-Host
        if ($LASTEXITCODE) { throw 'vpk upload github failed' }
        $draft = @(Get-Release | Where-Object { $_.draft -and $_.tag_name -eq $tag })
        if ($draft.Count -ne 1) { throw "expected one draft $tag after the upload, found $($draft.Count)" }
        Invoke-Gh release upload $tag $assetsFile -R $Repository --clobber | Out-Null
        $run = if ($env:GITHUB_RUN_ID) { " (CI run $env:GITHUB_SERVER_URL/$Repository/actions/runs/$env:GITHUB_RUN_ID)" } else { '' }
        $hasDelta = Test-Path $deltaPath
        $notes = @(
            "Pre-release of the Agent Bridge Windows app and host, built from main at $Commit$run."
            ''
            "Unsigned: Windows SmartScreen warns on first install (More info, then Run anyway). To install, run $packId-win-Setup.exe. Installed copies update themselves from this repository's releases, pre-releases included$(if ($hasDelta) { ', downloading only the delta from the previous one' })."
        ) -join "`n"
        Invoke-Gh api -X PATCH "repos/$Repository/releases/$($draft[0].id)" -f "body=$notes" -F draft=false -F prerelease=true | Out-Null
    }
    Write-Plan "$(if ($DryRun) { 'would publish' } else { 'published' }) $tag as a pre-release"
}

# --- Keep the newest pre-releases. Deleting a release deletes its assets but not its tag; full
# releases, drafts and anything not tagged v<major>.<minor>.<patch> are never touched.
$current = if ($DryRun) { $releases } else { Get-Release }
$pre = @($current | Where-Object { $_.prerelease -and -not $_.draft } | ForEach-Object {
        $v = Get-TagVersion $_.tag_name
        if ($v) { [pscustomobject]@{ Id = $_.id; Tag = $_.tag_name; Version = $v } }
    })
if ($DryRun -and $publishing) { $pre += [pscustomobject]@{ Id = $null; Tag = $tag; Version = $semver } }
$old = @($pre | Sort-Object Version -Descending | Select-Object -Skip $Keep)
foreach ($o in $old) {
    Write-Plan "deleting the pre-release $($o.Tag) and its assets (the tag stays)"
    if (-not $DryRun) { Invoke-Gh api -X DELETE "repos/$Repository/releases/$($o.Id)" | Out-Null }
}
Write-Plan "$([Math]::Min($pre.Count, $Keep)) pre-release(s) kept, $($old.Count) deleted"
