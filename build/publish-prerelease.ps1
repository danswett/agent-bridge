# Publishes the Windows app's Velopack release folders (build/pack-app.ps1), one per architecture,
# as one GitHub pre-release, which installed copies update from, then keeps only the newest
# pre-releases. CI's publish job runs it for every push to main; the app jobs run it with -DryRun
# on every build, each for its own folder, which reads the repository's releases but uploads,
# publishes and deletes nothing. docs/app-windows.md, "Releases and updates".
#
#   pwsh build/publish-prerelease.ps1 -Version 0.1.600 -ReleasesDir out/app/releases, out/app-arm64/releases `
#       -Repository danswett/agent-bridge -Commit <sha> -MainHead <sha> -MainRepo <dir> [-DryRun]
#
# -MainHead is main's head as the publish job read it, and -MainRepo a clone of main at it with
# the selector built (tools/AgentBridge.Quality): a commit main has since moved past with app
# changes isn't published, since main's later run publishes those (#938). A dry run needs neither.
#
# Each folder is one Velopack channel, read from the assets.<channel>.json vpk pack wrote there:
# x64's `win`, the channel every copy installed before Arm64 builds reads, and Arm64's
# `win-arm64` (build/app-channels.ps1). A release carries both, so publishing needs both.
#
# Needs gh and vpk 1.2.161, and GH_TOKEN with contents: write unless -DryRun.
#
# -RequireSignature -SignerEku <identity EKU> (CI's APP_SIGNING, docs/app-windows.md, Signing):
# every folder must carry the pinned publisher's timestamped signatures before anything else, and
# the draft release is downloaded back and checked again (its feeds' hashes too) before it is
# published; a failure leaves it a draft, which installed copies never see. Without it, releases
# are unsigned as before.
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string[]]$ReleasesDir,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Commit,
    # How many pre-releases stay. Velopack's GitHub source reads the newest ten releases' feeds,
    # so deltas never need more; the rest are for going back by hand.
    [ValidateRange(1, 1000)][int]$Keep = 20,
    [string]$MainHead,
    [string]$MainRepo,
    [switch]$DryRun,
    [switch]$RequireSignature,
    [string]$SignerEku
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $DryRun -and ($MainHead -cnotmatch '^[0-9a-f]{40}$' -or -not $MainRepo)) {
    throw "publishing needs -MainHead, main's head commit ('$MainHead'), and -MainRepo, a clone of main at it"
}
. (Join-Path $PSScriptRoot 'app-channels.ps1')
if ($RequireSignature) {
    . (Join-Path $PSScriptRoot 'app-signing.ps1')
    Assert-AppSignerEku $SignerEku
}

$packId = $AppPackId
$tag = "v$Version"
$semver = [semver]$Version
$ReleasesDir = @($ReleasesDir | ForEach-Object { [IO.Path]::GetFullPath($_) })
$work = Join-Path (Split-Path $ReleasesDir[0]) 'publish-work'
$mode = if ($DryRun) { 'dry run: ' } else { '' }
$haltsPath = Join-Path $PSScriptRoot '..' 'ops' 'halts.json'
. (Join-Path $PSScriptRoot 'validate-halt-list.ps1')
Assert-ReleaseHaltList $haltsPath

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

# GitHub's API now and then fails a single call (HTTP 500, an empty response) and answers the next
# one. Twice on 2026-11-05 that alone turned main red, after every build and test had passed
# (#907). Only listing and deleting releases go through this: both are safe to repeat.
$ghRetryDelays = @(2, 8)
function Invoke-GhRetry {
    for ($attempt = 0; ; $attempt++) {
        try { return Invoke-Gh @args }
        catch {
            if ($attempt -ge $ghRetryDelays.Count) { throw }
            Write-Plan "$($_.Exception.Message); trying again in $($ghRetryDelays[$attempt]) s"
            Start-Sleep -Seconds $ghRetryDelays[$attempt]
        }
    }
}

function Get-Release {
    @(Invoke-GhRetry api "repos/$Repository/releases?per_page=100" --paginate --jq '.[] | {id, tag_name, draft, prerelease}' |
            Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Remove-Release([long]$id) {
    try { Invoke-GhRetry api -X DELETE "repos/$Repository/releases/$id" | Out-Null }
    catch {
        # A DELETE that failed may still have gone through, and its retries then fail as "not
        # found": the release being gone is what counts.
        if (@(Get-Release | Where-Object id -EQ $id).Count) { throw }
    }
}

function Get-TagVersion([string]$tagName) {
    if ($tagName -match '^v(\d+\.\d+\.\d+)$') { [semver]$Matches[1] }
}

function Get-Megabyte([string]$path) { '{0:N1} MB' -f ((Get-Item $path).Length / 1MB) }

function Reset-Work {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force $work | Out-Null
}

# --- Each folder is what vpk pack wrote: exactly one channel's packages of one version, named as
# that channel's clients expect, and built for that channel's architecture.
$builds = foreach ($dir in $ReleasesDir) {
    $assetFiles = @(Get-ChildItem $dir -Filter 'assets.*.json' -ErrorAction SilentlyContinue)
    if ($assetFiles.Count -eq 0) { throw "no assets.win.json or other assets.<channel>.json in ${dir}: not the output of vpk pack" }
    if ($assetFiles.Count -gt 1) { throw "more than one channel in ${dir}: $($assetFiles.Name -join ', ')" }
    $assetsName = $assetFiles[0].Name
    $target = Get-AppChannelByName ($assetsName -replace '^assets\.(.+)\.json$', '$1')
    $channel = $target.Channel
    $assets = [Collections.Generic.List[object]]@(Get-Content $assetFiles[0].FullName -Raw | ConvertFrom-Json)
    $expected = [ordered]@{}
    foreach ($type in 'Full', 'Installer', 'Portable', 'Delta') { $expected[$type] = Get-AppAssetName $channel $type $Version }
    foreach ($asset in $assets) {
        if (-not $expected.Contains([string]$asset.Type) -or $asset.RelativeFileName -ne $expected[[string]$asset.Type]) {
            throw "unexpected asset in ${assetsName}: $($asset.Type) $($asset.RelativeFileName)"
        }
        if (-not (Test-Path (Join-Path $dir $asset.RelativeFileName))) { throw "missing from ${dir}: $($asset.RelativeFileName)" }
    }
    foreach ($type in 'Full', 'Installer', 'Portable') {
        if (-not ($assets | Where-Object Type -EQ $type)) { throw "$assetsName has no $type ($($expected[$type]))" }
    }
    [pscustomobject]@{
        Dir = $dir; Channel = $channel; Machine = $target.Machine; Runtime = $target.Runtime
        AssetsName = $assetsName; AssetsFile = $assetFiles[0].FullName; Assets = $assets; Expected = $expected
        FullPath = Join-Path $dir $expected.Full; DeltaPath = Join-Path $dir $expected.Delta; Identity = $null; Size = $null
    }
}
$builds = @($builds)
$duplicate = @($builds | Group-Object Channel | Where-Object Count -GT 1)
if ($duplicate) { throw "the $($duplicate[0].Name) channel is given twice" }
# x64 first: its upload creates the draft the others join, and it is the one existing copies read.
$builds = @($AppChannels.Values | ForEach-Object { $c = $_.Channel; $builds | Where-Object Channel -CEQ $c })
if (-not $DryRun) {
    # A release without one architecture would leave that architecture's copies a version behind,
    # and its next delta without a base.
    $absent = @($AppChannels.Values | Where-Object { $_.Channel -cnotin $builds.Channel } | ForEach-Object { "$($_.Channel) ($($_.Runtime))" })
    if ($absent) { throw "a release carries every architecture; no folder for $($absent -join ', ')" }
}
foreach ($b in $builds) {
    # A channel's feed is all its installed copies read, so a package for another machine in it
    # would be installed over every one of them.
    $machine = Get-PackageMachine $b.FullPath
    if ($machine -ne $b.Machine) { throw "$($b.Expected.Full) holds an app built for $machine; the $($b.Channel) channel is $($b.Machine)'s" }
    # Only a release build may go out: installed copies take it, and a dev build's Home Assistant
    # connector refuses a real Home Assistant (G6). A pull request's dry run is a dev build, as it
    # should be, and only says so.
    $identity = & (Join-Path $PSScriptRoot 'host-identity.ps1') $b.FullPath
    if ($identity -ne 'release') {
        if (-not $DryRun) { throw "$($b.Expected.Full) holds a $identity build of the host; only a release build (pack-app.ps1 -Release) is published" }
        Write-Plan "the host is a $identity build in the $($b.Channel) package; the publish path packs a release build (pack-app.ps1 -Release)"
    }
    $b.Identity = $identity
}

# --- Signed, when signing is required: by the pinned publisher, every file, before anything is
# read from or written to the repository.
$signer = $null
if ($RequireSignature) {
    foreach ($b in $builds) {
        $check = Test-AppReleaseSignature $b.Dir $b.Channel $Version $SignerEku (Join-Path $work 'signatures')
        if ($check.Problems) { throw "the $($b.Channel) folder is not signed as it must be; nothing was published:`n  $($check.Problems -join "`n  ")" }
        if ($signer -and $check.Signer -ne $signer) { throw "the $($b.Channel) folder is signed by $($check.Signer), another folder by $signer; nothing was published" }
        $signer = $check.Signer
        Write-Plan "the $($b.Channel) release is signed by $signer, with a timestamp"
    }
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
}
# --- Whether to publish: once per version, never a version older than one already out, never
# a commit the latest release's commit isn't part of, and never one main has since changed the app past.
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
if ($publishing -and $released.Count -and -not $DryRun) {
    # A run's number, and so its version, says nothing of its commit's place on main: an
    # ancestor's run created after its descendant's gets the higher one. Installed copies take the
    # highest version, so a commit that doesn't hold the latest release's would take them back to
    # older code. Read here, under the publish lock; anything unreadable or off that line stops.
    $latestTag = "v$($released[0])"
    $ref = @(Invoke-Gh api "repos/$Repository/git/ref/tags/$latestTag" --jq '.object.type, .object.sha')
    if ($ref.Count -ne 2 -or $ref[0] -cne 'commit' -or $ref[1] -cnotmatch '^[0-9a-f]{40}$') {
        throw "couldn't read the commit $latestTag is tagged on ($($ref -join ' ')); not publishing $tag"
    }
    $latestCommit = $ref[1]
    $ancestry = [string](Invoke-Gh api "repos/$Repository/compare/$latestCommit...$Commit" --jq '.status')
    if ($ancestry -ceq 'behind') {
        # The same race as an older version, the other way round: the newer commit is out already.
        Write-Plan "$Commit is behind $latestTag's commit $latestCommit; not publishing $tag over it"
        $publishing = $false
    } elseif ($ancestry -cnotin 'ahead', 'identical') {
        throw "$Commit doesn't follow $latestTag's commit $latestCommit (compare: '$ancestry'); not publishing $tag"
    }
}
if ($publishing -and -not $DryRun -and $MainHead -cne $Commit) {
    # And the other way round from that: a descendant's run created before this one has the lower
    # version, so once this is out the checks above refuse it, and its app changes would never be
    # released (#938's review). Its commit was pushed before this run was made, so it's in main's
    # head as the publish job read it, under this lock. When main's selector and catalog, at that
    # head, pick the app job for what came after this commit, main's later run builds the app and
    # publishes it, this commit's changes with it; this one stands aside. A tail that leaves the
    # app alone, docs only say, still publishes this, so main's head needn't be this commit.
    # Anything that can't be read or isn't on main's line stops.
    $tail = [string](Invoke-Gh api "repos/$Repository/compare/$Commit...$MainHead" --jq '.status')
    if ($tail -cne 'ahead') { throw "main's head $MainHead doesn't follow $Commit (compare: '$tail'); not publishing $tag" }
    $selector = Join-Path $MainRepo 'tools/AgentBridge.Quality/bin/Release/net10.0/agent-bridge-quality.dll'
    $json = (& dotnet $selector select --repo $MainRepo --ring 1 --base $Commit --head $MainHead) -join "`n"
    if ($LASTEXITCODE) { throw "the selector failed (exit $LASTEXITCODE) on $Commit..$MainHead; not publishing $tag" }
    $selection = try { $json | ConvertFrom-Json -AsHashtable } catch { $null }
    $jobs = if ($selection -is [Collections.IDictionary]) { $selection['jobs'] }
    $ciJobs = if ($jobs -is [Collections.IDictionary]) { $jobs['ci.yml'] }
    if ($jobs -isnot [Collections.IDictionary] -or ($null -ne $ciJobs -and $ciJobs -isnot [Collections.IDictionary])) {
        throw "couldn't read the selector's jobs for $Commit..$MainHead; not publishing $tag"
    }
    if ($ciJobs -and $ciJobs.Contains('app')) {
        Write-Plan "main has moved on to $MainHead, which changes the app since $Commit; its run publishes both, not $tag"
        $publishing = $false
    }
}

function Test-Delta($b, [string]$basePath) {
    # Right only if it rebuilds this version's full package, byte for byte, from the package of
    # the release installed copies are on; vpk delta patch reproduces it exactly.
    $patched = Join-Path $work 'patched.nupkg'
    Remove-Item $patched -ErrorAction SilentlyContinue
    & vpk delta patch --base $basePath --patch $b.DeltaPath --output $patched | Out-Host
    $LASTEXITCODE -eq 0 -and (Test-Path $patched) -and (Get-FileHash $patched).Hash -eq (Get-FileHash $b.FullPath).Hash
}

function Update-Delta($b) {
    # The app job built its delta from whatever release was latest when it packed. Another merge
    # may have published since (this runs under the publish lock, so that one is now final), and a
    # delta from an older base would fail on every copy that took it. Check it against the latest
    # release's package of the same channel, rebuild it from that one if it is wrong or missing,
    # and report why when neither works.
    Reset-Work
    $latest = $released | Select-Object -First 1
    if (-not $latest) { return 'there is no earlier release' }
    $baseName = Get-AppAssetName $b.Channel 'Full' $latest
    $basePath = Join-Path $b.Dir $baseName
    if (-not (Test-Path $basePath)) {
        $download = @('download', 'github', '--repoUrl', "https://github.com/$Repository", '--pre', '--channel', $b.Channel, '-o', $work)
        if ($env:GH_TOKEN) { $download += @('--token', $env:GH_TOKEN) }
        & vpk @download | Out-Host
        $basePath = Join-Path $work $baseName
        if ($LASTEXITCODE -or -not (Test-Path $basePath)) { return "v$latest's $($b.Channel) full package could not be downloaded" }
    }
    if ((Test-Path $b.DeltaPath) -and (Test-Delta $b $basePath)) { return "@v$latest" }
    Write-Plan "rebuilding the $($b.Channel) delta from v$latest"
    Remove-Item $b.DeltaPath -ErrorAction SilentlyContinue
    & vpk delta generate --base $basePath --new $b.FullPath --output $b.DeltaPath | Out-Host
    if ($LASTEXITCODE -or -not (Test-Path $b.DeltaPath) -or -not (Test-Delta $b $basePath)) { return "no delta from v$latest rebuilds the full package" }
    "@v$latest"
}

if ($publishing) {
    foreach ($b in $builds) {
        $deltaResult = Update-Delta $b
        $deltaEntry = $b.Assets | Where-Object Type -EQ 'Delta'
        if ($deltaResult -like '@*') {
            if (-not $deltaEntry) { $b.Assets.Add([pscustomobject]@{ RelativeFileName = $b.Expected.Delta; Type = 'Delta' }) }
            $b.Size = "a $(Get-Megabyte $b.DeltaPath) delta from $($deltaResult.Substring(1)) beside the $(Get-Megabyte $b.FullPath) full package"
        } else {
            # A wrong delta costs every installed copy a failed patch before the full download; none
            # costs only the download.
            if ($deltaEntry) { [void]$b.Assets.Remove($deltaEntry) }
            Remove-Item $b.DeltaPath -ErrorAction SilentlyContinue
            Write-Plan "publishing the $($b.Channel) full package alone: $deltaResult" -Warn
            $b.Size = "the $(Get-Megabyte $b.FullPath) full package only, no delta"
        }
        ConvertTo-Json @($b.Assets) -Compress | Set-Content $b.AssetsFile -NoNewline
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }

        # vpk upload github sends the build's assets and a releases.<channel>.json of this version
        # alone, and for x64's `win` a RELEASES too; assets.<channel>.json goes up as well, as on the
        # first release published by hand.
        $uploads = @($b.Assets.RelativeFileName) + "releases.$($b.Channel).json"
        if ($b.Channel -ceq 'win') { $uploads += 'RELEASES' }
        $uploads += $b.AssetsName
        Write-Plan "$tag $($b.Channel) ($($b.Runtime), $($b.Identity) build) from ${Commit}, $($b.Size): $($uploads -join ', ')"
    }
}

if ($DryRun) {
    # vpk itself reads each folder, through the same build-assets file the GitHub upload reads,
    # into a scratch feed beside it: the nearest a pull request can come to uploading. Each
    # channel's feed must list its own packages, as an installed copy picks and checks them, and
    # nothing else: it is all its copies read.
    foreach ($b in $builds) {
        Reset-Work
        & vpk upload local --outputDir $b.Dir --channel $b.Channel --path $work | Out-Host
        if ($LASTEXITCODE) { throw "vpk upload local failed: vpk cannot upload the $($b.Channel) folder" }
        $feedPath = Join-Path $work "releases.$($b.Channel).json"
        $types = @('Full') + @(if (Test-Path $b.DeltaPath) { 'Delta' })
        $problems = @(Get-AppFeedProblem $feedPath $b.Channel $Version $b.Dir $types | Where-Object { $_ })
        if ($problems) { throw "vpk's $($b.Channel) feed is not this channel's $Version`:`n  $($problems -join "`n  ")" }
        Write-Plan "the $($b.Channel) feed lists $(@((Get-Content $feedPath -Raw | ConvertFrom-Json).Assets.FileName) -join ', ')"
    }
    Remove-Item $work -Recurse -Force
}

if ($publishing) {
    if (-not $DryRun) {
        # Generate under the publish lock, against the release actually preceding this one:
        # packing may have happened before another release, or several merges may not have shipped.
        $generate = @('api', '-X', 'POST', "repos/$Repository/releases/generate-notes",
            '-f', "tag_name=$tag", '-f', "target_commitish=$Commit")
        $previous = $released | Select-Object -First 1
        if ($previous) { $generate += @('-f', "previous_tag_name=v$previous") }
        $generated = Invoke-Gh @generate | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($generated.body)) { throw 'GitHub generated no release notes; the release was not published' }
        $changeNotes = $generated.body
    }
    foreach ($leftover in @($releases | Where-Object { $_.draft -and $_.tag_name -eq $tag })) {
        # A draft is all a failed attempt leaves (it is published only after every upload), and
        # vpk refuses a tag that has one.
        Write-Plan "deleting the draft $tag left by an earlier attempt"
        # This one must go before anything is uploaded, so failing to delete it still fails.
        if (-not $DryRun) { Remove-Release $leftover.id }
    }
    if (-not $DryRun) {
        if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is needed to publish' }
        # As a draft first: clients never see a release that is missing its notes or a file. x64's
        # upload creates the draft; each other channel's joins it (--merge), adding its own feed.
        $first = $true
        foreach ($b in $builds) {
            $upload = @('upload', 'github', '--outputDir', $b.Dir, '--channel', $b.Channel, '--repoUrl', "https://github.com/$Repository", '--token', $env:GH_TOKEN, '--pre', '--releaseName', "$AppProductName $Version (pre-release)", '--tag', $tag, '--targetCommitish', $Commit)
            if (-not $first) { $upload += '--merge' }
            & vpk @upload | Out-Host
            if ($LASTEXITCODE) { throw "vpk upload github failed for the $($b.Channel) channel" }
            $first = $false
        }
        $draft = @(Get-Release | Where-Object { $_.draft -and $_.tag_name -eq $tag })
        if ($draft.Count -ne 1) { throw "expected one draft $tag after the upload, found $($draft.Count)" }
        foreach ($b in $builds) { Invoke-Gh release upload $tag $b.AssetsFile -R $Repository --clobber | Out-Null }
        # The same reviewed policy for every channel; upload before exposing the draft.
        # Clients verify GitHub's asset digest and never treat a missing policy as empty.
        Invoke-Gh release upload $tag $haltsPath -R $Repository --clobber | Out-Null
        if ($RequireSignature) {
            # What clients would download, read back from the draft: each file the one checked
            # above, still signed, and each feed's hashes those of the packages beside it.
            Reset-Work
            $problems = foreach ($b in $builds) {
                $dir = Join-Path $work "draft-$($b.Channel)"
                $names = @($b.Expected.Full, $b.Expected.Installer, $b.Expected.Portable)
                if (Test-Path $b.DeltaPath) { $names += $b.Expected.Delta }
                $feedName = "releases.$($b.Channel).json"
                $patterns = @($names + $feedName | ForEach-Object { '-p'; $_ })
                Invoke-Gh release download $tag -R $Repository -D $dir @patterns | Out-Null
                foreach ($name in $names) {
                    $downloaded = Join-Path $dir $name
                    if (-not (Test-Path -LiteralPath $downloaded)) { "the draft has no $name"; continue }
                    if ((Get-FileHash -LiteralPath $downloaded).Hash -ne (Get-FileHash -LiteralPath (Join-Path $b.Dir $name)).Hash) { "the draft's $name is not the file checked" }
                }
                $types = @('Full') + @(if (Test-Path $b.DeltaPath) { 'Delta' })
                if (Test-Path (Join-Path $dir $feedName)) { Get-AppFeedProblem (Join-Path $dir $feedName) $b.Channel $Version $dir $types } else { "the draft has no $feedName" }
                (Test-AppReleaseSignature $dir $b.Channel $Version $SignerEku (Join-Path $work 'draft-signatures')).Problems
            }
            $problems = @($problems | Where-Object { $_ })
            if ($problems) { throw "the draft $tag, downloaded back, is not what was checked; it was left a draft, not published:`n  $($problems -join "`n  ")" }
            Remove-Item $work -Recurse -Force
            Write-Plan "the draft $tag, downloaded back, is the release checked: signed by $signer, its feeds' hashes its packages'"
        }
        $run = if ($env:GITHUB_RUN_ID) { " (CI run $env:GITHUB_SERVER_URL/$Repository/actions/runs/$env:GITHUB_RUN_ID)" } else { '' }
        $hasDelta = [bool]($builds | Where-Object { Test-Path $_.DeltaPath })
        $installers = @($builds | ForEach-Object { "$($_.Expected.Installer) on $(if ($_.Machine -eq 'x64') { 'an x64 PC' } else { 'a Windows on Arm PC' })" })
        $notes = @(
            "Pre-release of the $AppProductName Windows app and host, built from main at $Commit$run."
            ''
            $changeNotes
            ''
            '## Installation and updates'
            ''
            $(if ($RequireSignature) {
                    "Signed by $signer with Azure Artifact Signing: Setup.exe, the app and every executable in it. Windows SmartScreen may still warn on first install until the publisher's reputation builds (More info, then Run anyway). To install, run $($installers -join ', or ')."
                } else {
                    "Unsigned: Windows SmartScreen warns on first install (More info, then Run anyway). To install, run $($installers -join ', or ')."
                })
            ''
            "An installed copy installs it by itself once its host is idle, or at once from Settings, Check for updates (pre-releases included)$(if ($hasDelta) { ', downloading only the delta from the previous one' }), and only ever for its own architecture."
        ) -join "`n"
        # Full changelogs can exceed Windows' command-line limit; send the body through stdin.
        $payload = @{ body = $notes; draft = $false; prerelease = $true } | ConvertTo-Json -Compress
        $payload | gh api -X PATCH "repos/$Repository/releases/$($draft[0].id)" --input - | Out-Null
        if ($LASTEXITCODE) { throw "gh release publication failed ($LASTEXITCODE)" }
        # For CI's attest job: these assets are this run's, so its SBOMs describe them. A run that
        # uploads nothing (a re-run, or an older version) leaves it unset and attests nothing.
        if ($env:GITHUB_OUTPUT) { Add-Content -Path $env:GITHUB_OUTPUT -Value 'published=true' }
    }
    Write-Plan "$(if ($DryRun) { 'would publish' } else { 'published' }) $tag as a pre-release ($($builds.Channel -join ', '))"
}

# --- Keep the newest pre-releases. Deleting a release deletes its assets but not its tag; full
# releases, drafts and anything not tagged v<major>.<minor>.<patch> are never touched.
# Pruning is housekeeping: by now the release is out (or wasn't due), so a GitHub failure here,
# after its retries, is a warning rather than a failed publish. The next publish prunes again.
try { $current = if ($DryRun) { $releases } else { Get-Release } }
catch {
    Write-Plan -Warn "old pre-releases were not pruned: listing the releases failed ($($_.Exception.Message)); the next publish prunes them"
    # GitHub's pwsh step ends with `exit $LASTEXITCODE`: the failed gh call's code would turn this red.
    $global:LASTEXITCODE = 0
    return
}
$pre = @($current | Where-Object { $_.prerelease -and -not $_.draft } | ForEach-Object {
        $v = Get-TagVersion $_.tag_name
        if ($v) { [pscustomobject]@{ Id = $_.id; Tag = $_.tag_name; Version = $v } }
    })
if ($DryRun -and $publishing) { $pre += [pscustomobject]@{ Id = $null; Tag = $tag; Version = $semver } }
$old = @($pre | Sort-Object Version -Descending | Select-Object -Skip $Keep)
$failed = 0
foreach ($o in $old) {
    Write-Plan "deleting the pre-release $($o.Tag) and its assets (the tag stays)"
    if ($DryRun) { continue }
    try { Remove-Release $o.Id }
    catch {
        $failed++
        Write-Plan -Warn "the pre-release $($o.Tag) was not deleted ($($_.Exception.Message)); the next publish deletes it"
    }
}
Write-Plan "$([Math]::Min($pre.Count, $Keep)) pre-release(s) kept, $($old.Count - $failed) deleted$(if ($failed) { ", $failed left for the next publish" })"
# A pruning failure above is a warning; see the comment at the listing.
$global:LASTEXITCODE = 0
