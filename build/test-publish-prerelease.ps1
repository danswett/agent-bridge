# Checks build/publish-prerelease.ps1, and the delta base and build identity build/pack-app.ps1
# gives the package, against stand-ins for gh, vpk and dotnet: nothing is built, uploaded,
# published or deleted, and the folders are under out/test-publish-prerelease.
#
#   pwsh build/test-publish-prerelease.ps1
#
# What it guards: a merge publishes exactly one pre-release of its own version, never a second
# copy, never one older than what is out or from a commit behind it, never a dev build, and the
# pruning never reaches a full release or a tag.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publishScript = Join-Path $PSScriptRoot 'publish-prerelease.ps1'
$packScript = Join-Path $PSScriptRoot 'pack-app.ps1'
$scratch = Join-Path $PSScriptRoot '..' 'out' 'test-publish-prerelease'
$fixtures = Join-Path $PSScriptRoot '..' 'out' 'test-publish-prerelease-hosts'
$repo = 'danswett/agent-bridge'
$commit = '0123456789abcdef0123456789abcdef01234567'
# Main's line around the commit being built, oldest first; a release's tag is on its ancestor
# unless a test puts it elsewhere.
$ancestor = 'fedcba9876543210fedcba9876543210fedcba98'
$descendant = '89abcdef0123456789abcdef0123456789abcdef'
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
# GitHub failing calls (#907): the next N listings or DELETEs fail; a DELETE that "goes through"
# deletes before it fails, as a 500 after the fact does.
$global:PpListFailures = 0
$global:PpListings = 0
$global:PpFailListAfter = -1
$global:PpDeleteFailures = 0
$global:PpDeleteGoesThrough = $false
$global:PpSleeps = [Collections.Generic.List[int]]::new()
function global:Start-Sleep { [CmdletBinding()] param([int]$Seconds, [int]$Milliseconds) $global:PpSleeps.Add($Seconds) }

$global:PpMain = @($ancestor, $commit, $descendant)
$global:PpTagSha = @{}
$global:PpTagType = 'commit'
$global:PpTagRef = $null
$global:PpAncestry = $null
# What main's selector, at main's head, says of the commits after the one publishing: its JSON,
# or 'fail' for a nonzero exit.
$global:PpSelect = $null
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
        if ($global:PpListFailures -gt 0) { $global:PpListFailures--; $global:LASTEXITCODE = 1; return }
        $global:PpListings++
        if ($global:PpFailListAfter -ge 0 -and $global:PpListings -gt $global:PpFailListAfter) { $global:LASTEXITCODE = 1; return }
        foreach ($r in $global:PpReleases) { $r | ConvertTo-Json -Compress }
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/git/matching-refs/tags/*") {
        $prefix = 'refs/tags/' + $a[1].Substring("repos/$repo/git/matching-refs/tags/".Length)
        $global:PpTags | Where-Object { $_.StartsWith($prefix) }
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'DELETE') {
        $id = [long]($a[3] -split '/')[-1]
        if ($global:PpDeleteFailures -gt 0) {
            $global:PpDeleteFailures--
            if ($global:PpDeleteGoesThrough) { [void]$global:PpReleases.RemoveAll({ param($r) $r.id -eq $id }) }
            $global:LASTEXITCODE = 1; return
        }
        # As GitHub answers a release that isn't there: 404.
        if (-not $global:PpReleases.RemoveAll({ param($r) $r.id -eq $id })) { $global:LASTEXITCODE = 1 }
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
    } elseif ($a[0] -eq 'release' -and $a[1] -eq 'list') {
        $global:PpReleases | Where-Object { -not $_.draft } | Select-Object -Last 1 | ForEach-Object tag_name
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/git/ref/tags/*") {
        # A published release's tag: the commit it is on, its tag's sha unless a test says.
        $name = $a[1].Substring("repos/$repo/git/ref/tags/".Length)
        if ($global:PpTagRef -eq 'fail' -or $global:PpTags -notcontains "refs/tags/$name") { $global:LASTEXITCODE = 1; return }
        $global:PpTagType
        if ($global:PpTagSha.ContainsKey($name)) { $global:PpTagSha[$name] } else { $global:PpMain[0] }
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/compare/*" -and $a -contains '.status') {
        # Where head is from base on main's line, as GitHub's compare says it.
        if ($global:PpAncestry -eq 'fail') { $global:LASTEXITCODE = 1; return }
        if ($global:PpAncestry) { return $global:PpAncestry }
        $base, $head = $a[1].Substring("repos/$repo/compare/".Length) -split '\.\.\.'
        $from = [array]::IndexOf($global:PpMain, $base); $to = [array]::IndexOf($global:PpMain, $head)
        if ($from -lt 0 -or $to -lt 0) { 'diverged' } elseif ($to -gt $from) { 'ahead' } elseif ($to -eq $from) { 'identical' } else { 'behind' }
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
    } elseif ($a[0] -eq 'upload' -and $a[1] -eq 'local') {
        $channel = Arg '--channel'
        $global:PpCalls.Add("vpk upload local --channel $channel")
        $assets = @(Get-Content (Join-Path (Arg '--outputDir') "assets.$channel.json") -Raw | ConvertFrom-Json)
        $entries = @($assets | Where-Object { $_.Type -in 'Full', 'Delta' } | ForEach-Object {
                @{ Version = ($_.RelativeFileName -replace '^AgentBridgeApp-(\d+\.\d+\.\d+).*-(full|delta)\.nupkg$', '$1'); Type = $_.Type; FileName = $_.RelativeFileName } })
        if ($global:PpFeedExtra) { $entries += @{ Version = '0.1.600'; Type = 'Full'; FileName = $global:PpFeedExtra } }
        @{ Assets = $entries } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path (Arg '--path') "releases.$channel.json")
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
    } else { throw "stand-in vpk: unexpected $($a -join ' ')" }
}

function global:dotnet {
    $a = @($args)
    $global:LASTEXITCODE = 0
    if ($a[0] -like '*agent-bridge-quality.dll') {
        $global:PpCalls.Add("selector $($a[1..($a.Count - 1)] -join ' ')")
        if ($global:PpSelect -eq 'fail') { $global:LASTEXITCODE = 1; return }
        return $global:PpSelect
    }
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
    $global:PpBuildsFor = $null; $global:PpFeedExtra = $null
    $global:PpGeneratedNotes = "## What's Changed`n* A change (#200)`n`n**Full Changelog**: https://github.com/$repo/compare/v0.1.503...v0.1.600"
    $global:PpPublishedBody = $null
    $global:PpListFailures = 0; $global:PpListings = 0; $global:PpFailListAfter = -1; $global:PpDeleteFailures = 0; $global:PpDeleteGoesThrough = $false; $global:PpSleeps.Clear()
    $global:PpMain = @($ancestor, $commit, $descendant); $global:PpTagSha = @{}; $global:PpTagType = 'commit'; $global:PpTagRef = $null; $global:PpAncestry = $null
    $global:PpSelect = $null
    for ($i = 1; $i -le $prereleases; $i++) { New-PpRelease "v0.1.$(500 + $i)" }
    if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
}

function New-Package([string]$path, [string]$kind, [string]$machine) {
    # A full package as vpk packs it, as far as host-identity.ps1 and publishing read one: the
    # host and the app's executable under lib/app.
    $stage = Join-Path $scratch 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $stage 'lib' 'app') | Out-Null
    Copy-Item $global:PpHost[$kind] (Join-Path $stage 'lib' 'app' 'agent-bridge-host.dll')
    New-PpExe (Join-Path $stage 'lib' 'app' 'AgentBridge.exe') $machine
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
            default { Set-Content $path 'stand-in' }
        }
    }
    ConvertTo-Json @($assets) -Compress | Set-Content (Join-Path $dir "assets.$Channel.json")
    $dir
}

# Both architectures' folders, as the publish job downloads them, unless -Only names one.
function Invoke-Publish([string]$version, [switch]$DryRun, [string]$DeltaFrom, [string[]]$Skip = @(), [switch]$DevBuild, [string[]]$Only = @('win', 'win-arm64'), [hashtable]$Machine = @{}, [switch]$Plan, [string]$Head = $commit) {
    $dirs = @(foreach ($channel in $Only) { New-Folder $version -DeltaFrom $DeltaFrom -Skip $Skip -DevBuild:$DevBuild -Channel $channel -Machine $Machine[$channel] })
    # -Plan: what it says it does, rather than nothing. -Head: main's head as publish reads it.
    if ($Plan) { & $publishScript -Version $version -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $Head -MainRepo $scratch -DryRun:$DryRun 6>&1 | Out-String }
    else { & $publishScript -Version $version -ReleasesDir $dirs -Repository $repo -Commit $commit -MainHead $Head -MainRepo $scratch -DryRun:$DryRun 6>$null }
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

. (Join-Path $PSScriptRoot 'validate-halt-list.ps1')
foreach ($invalid in @(
    @{ Name = 'A string schema version is refused'; Json = '{"v":"1","halted":[]}' },
    @{ Name = 'A decimal schema version is refused'; Json = '{"v":1.0,"halted":[]}' },
    @{ Name = 'An extra halt policy property is refused'; Json = '{"v":1,"halted":[],"extra":false}' },
    @{ Name = 'A halted version with a trailing newline is refused'; Json = '{"v":1,"halted":["0.1.600\n"]}' },
    @{ Name = 'A halted version above Int64 MaxValue is refused'; Json = '{"v":1,"halted":["0.1.9223372036854775808"]}' }
)) {
    Check $invalid.Name {
        New-Item -ItemType Directory -Force $scratch | Out-Null
        $file = Join-Path $scratch 'invalid-halts.json'
        [IO.File]::WriteAllText($file, $invalid.Json)
        $threw = $false
        try { Assert-ReleaseHaltList $file } catch { $threw = $true }
        Assert $threw 'publisher accepted a policy that the client rejects'
    }
}

# --- Publishing.
Check 'A new version goes up as a draft, then is published as a pre-release of the commit with assets.win.json beside it' {
    Reset 3
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.503'
    Assert ($global:PpCalls -notcontains 'vpk delta generate') 'a delta from the latest release was rebuilt'
    Assert ((Get-Delta) -eq 'delta 0.1.503->0.1.600') "delta: $(Get-Delta)"
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
    Assert ([bool]($global:PpCalls | Where-Object { $_ -like 'gh release upload v0.1.600 *assets.win.json*' })) 'assets.win.json was not uploaded'
    $haltUpload = CallIndex 'gh release upload v0.1.600 *halts.json*'
    Assert ($haltUpload -ge 0 -and $haltUpload -lt (CallIndex 'gh api -X PATCH*')) 'the shared halt policy must be uploaded before publication'
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

Check "A higher version built from an ancestor of the latest release's commit is not published: its run came later" {
    # Reversed order: the descendant's run (0.1.503) published first; the ancestor's run, created
    # after it, has the higher number. Under the lock, compare says which commit holds the other.
    Reset 3
    $global:PpTagSha['v0.1.503'] = $descendant
    $plan = Invoke-Publish '0.1.600' -Plan
    Assert ($global:PpCalls -contains "gh api repos/$repo/git/ref/tags/v0.1.503 --jq .object.type, .object.sha") "calls: $($global:PpCalls -join '; ')"
    Assert ($global:PpCalls -contains "gh api repos/$repo/compare/$descendant...$commit --jq .status") "calls: $($global:PpCalls -join '; ')"
    Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) "uploads: $(Uploads); deletes: $(Deletes)"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "writes: $($global:PpCalls -join '; ')"
    Assert (-not ($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600')) 'an ancestor was released over its descendant'
    Assert ($plan -like "*$commit is behind v0.1.503's commit $descendant*") "plan: $plan"
}

Check "A version from the latest release's commit or a descendant of it is published" {
    foreach ($tagged in $ancestor, $commit) {
        Reset 3
        $global:PpTagSha['v0.1.503'] = $tagged
        Invoke-Publish '0.1.600'
        Assert (@(Uploads).Count -eq 2) "tagged on $tagged; uploads: $(Uploads)"
        $read = CallIndex "gh api repos/$repo/compare/$tagged...$commit*"
        Assert ($read -ge 0 -and $read -lt (CallIndex 'vpk upload github*')) "ancestry wasn't read before uploading: $($global:PpCalls -join '; ')"
    }
}

Check "A commit off the latest release's line, or a tag or comparison that can't be read, is not published" {
    foreach ($case in @(
        @{ Name = 'diverged'; Set = { $global:PpTagSha['v0.1.503'] = 'abababababababababababababababababababab' } },
        @{ Name = 'an unknown comparison'; Set = { $global:PpAncestry = 'unknown' } },
        @{ Name = 'an unreadable comparison'; Set = { $global:PpAncestry = 'fail' } },
        @{ Name = 'an unreadable tag'; Set = { $global:PpTagRef = 'fail' } },
        @{ Name = 'an annotated tag'; Set = { $global:PpTagType = 'tag' } },
        @{ Name = 'a tag on no commit'; Set = { $global:PpTagSha['v0.1.503'] = '' } }
    )) {
        Reset 3
        New-PpRelease 'v0.1.600' $true $true
        & $case.Set
        $threw = $false
        try { Invoke-Publish '0.1.600' } catch { $threw = $true }
        Assert $threw "$($case.Name) was published over"
        Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) "$($case.Name): uploads: $(Uploads); deletes: $(Deletes)"
        Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "$($case.Name): writes: $($global:PpCalls -join '; ')"
    }
}

$appTail = '{"mode":"partial","jobs":{"ci.yml":{"app":["app-windows"],"product":["product"]}}}'
Check "An ancestor whose run publishes first stands aside when main has changed the app since, and the descendant's lower version publishes both" {
    # S -> A -> B: B's run was created first (0.1.600), A's after it (0.1.601). Were A out first,
    # B's lower version would be refused and B's app changes never released (#938's review).
    Reset 3
    $global:PpSelect = $appTail
    $plan = Invoke-Publish '0.1.601' -Head $descendant -Plan
    Assert ($global:PpCalls -contains "gh api repos/$repo/compare/$commit...$descendant --jq .status") "calls: $($global:PpCalls -join '; ')"
    Assert ($global:PpCalls -contains "selector select --repo $scratch --ring 1 --base $commit --head $descendant") "calls: $($global:PpCalls -join '; ')"
    Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) "uploads: $(Uploads); deletes: $(Deletes)"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "writes: $($global:PpCalls -join '; ')"
    Assert ($plan -like "*main has moved on to $descendant, which changes the app since $commit*") "plan: $plan"
    # Then B's run, main's head, publishes its lower version over S's release.
    Remove-Item (Get-PpDir 'win'), (Get-PpDir 'win-arm64') -Recurse -Force
    $dirs = @(New-Folder '0.1.600'; New-Folder '0.1.600' -Channel 'win-arm64')
    & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $descendant -MainHead $descendant -MainRepo $scratch 6>$null
    Assert (@(Uploads).Count -eq 2 -and -not (Uploads | Where-Object { $_ -notlike "*--tag v0.1.600 --targetCommitish $descendant *" })) "uploads: $(Uploads)"
    Assert (-not ($global:PpReleases | Where-Object tag_name -EQ 'v0.1.601')) 'the ancestor was released'
}

Check "A commit whose later commits on main leave the app alone, docs only say, still publishes" {
    foreach ($select in '{"mode":"partial","jobs":{"ci.yml":{"light":["docs"]}}}', '{"mode":"docs-only","jobs":{}}') {
        Reset 3
        $global:PpSelect = $select
        Invoke-Publish '0.1.600' -Head $descendant
        Assert (@(Uploads).Count -eq 2) "$select; uploads: $(Uploads)"
        $read = CallIndex 'selector select*'
        Assert ($read -ge 0 -and $read -lt (CallIndex 'vpk upload github*')) "$select; main's head wasn't read before uploading: $($global:PpCalls -join '; ')"
    }
    # And main's head being the commit itself needs no selector.
    Reset 3
    $global:PpSelect = $appTail
    Invoke-Publish '0.1.600'
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'selector*' -or $_ -like "gh api repos/$repo/compare/$commit...*" })) "calls: $($global:PpCalls -join '; ')"
}

Check "A main head that isn't after the commit, or a selector answer that can't be read, stops publishing" {
    # With no release (0), only main's head is compared: the release ancestry check has nothing to read.
    foreach ($case in @(
        @{ Name = 'a diverged head'; Head = 'abababababababababababababababababababab'; Releases = 3; Selects = $false; Set = {} },
        @{ Name = 'a head behind the commit'; Head = $ancestor; Releases = 3; Selects = $false; Set = {} },
        @{ Name = 'an unknown head comparison'; Head = $descendant; Releases = 0; Selects = $false; Set = { $global:PpAncestry = 'unknown' } },
        @{ Name = 'an unreadable head comparison'; Head = $descendant; Releases = 0; Selects = $false; Set = { $global:PpAncestry = 'fail' } },
        @{ Name = 'a failing selector'; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = 'fail' } },
        @{ Name = 'no selector output'; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = $null } },
        @{ Name = 'malformed selector output'; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = '{"jobs":' } },
        @{ Name = 'selector output without jobs'; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = '{"mode":"partial"}' } },
        @{ Name = "selector output whose ci.yml jobs aren't a map"; Head = $descendant; Releases = 3; Selects = $true; Set = { $global:PpSelect = '{"jobs":{"ci.yml":["app"]}}' } }
    )) {
        Reset $case.Releases
        $global:PpSelect = $appTail
        & $case.Set
        $threw = $false
        try { Invoke-Publish '0.1.600' -Head $case.Head } catch { $threw = $_.Exception.Message -like '*not publishing v0.1.600*' }
        Assert $threw "$($case.Name) was published past"
        Assert (@(Uploads).Count -eq 0 -and @(Deletes).Count -eq 0) "$($case.Name): uploads: $(Uploads); deletes: $(Deletes)"
        Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "$($case.Name): writes: $($global:PpCalls -join '; ')"
        Assert ($case.Selects -eq [bool]($global:PpCalls | Where-Object { $_ -like 'selector*' })) "$($case.Name): calls: $($global:PpCalls -join '; ')"
    }
}

Check "Publishing without main's head, or with one that isn't a commit, is refused before anything is read" {
    foreach ($case in @(@{ Name = 'no head'; Args = @{ MainRepo = $scratch } }, @{ Name = 'a branch name'; Args = @{ MainHead = 'main'; MainRepo = $scratch } }, @{ Name = 'no clone'; Args = @{ MainHead = $descendant } })) {
        Reset 3
        $dirs = @(New-Folder '0.1.600'; New-Folder '0.1.600' -Channel 'win-arm64')
        $named = $case.Args
        $threw = $false
        try { & $publishScript -Version '0.1.600' -ReleasesDir $dirs -Repository $repo -Commit $commit @named 6>$null } catch { $threw = $_.Exception.Message -like '*publishing needs -MainHead*' }
        Assert $threw "$($case.Name) was not refused"
        Assert ($global:PpCalls.Count -eq 0) "$($case.Name): calls: $($global:PpCalls -join '; ')"
    }
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
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dir -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch 6>$null } catch { $threw = $true }
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
    & $publishScript -Version '0.1.600' -ReleasesDir (Get-PpDir 'win'), (Get-PpDir 'win-arm64') -Repository $repo -Commit $commit -MainHead $commit -MainRepo $scratch 6>$null
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

# --- GitHub failing a call (#907): twice on 2026-11-05 that alone turned main red.
Check 'A prune that GitHub fails once is tried again and the old pre-release is deleted' {
    Reset 20
    $global:PpDeleteFailures = 1
    Invoke-Publish '0.1.600'
    Assert ('v0.1.501' -notin @($global:PpReleases | ForEach-Object tag_name)) 'v0.1.501 was kept'
    Assert (@(Deletes).Count -eq 2) "deletes: $(Deletes)"
    Assert (@($global:PpSleeps) -join ',' -eq '2') "waits: $($global:PpSleeps -join ',')"
}

Check 'A prune DELETE that failed but went through counts as done' {
    Reset 20
    $global:PpDeleteFailures = 1; $global:PpDeleteGoesThrough = $true
    Invoke-Publish '0.1.600'
    Assert ('v0.1.501' -notin @($global:PpReleases | ForEach-Object tag_name)) 'v0.1.501 was kept'
}

Check 'A prune that keeps failing after a successful publish is a warning, not a failed publish' {
    Reset 20
    $global:PpDeleteFailures = 99
    $plan = Invoke-Publish '0.1.600' -Plan
    Assert ($global:LASTEXITCODE -eq 0) "the step would exit $global:LASTEXITCODE"
    $r = $global:PpReleases | Where-Object tag_name -EQ 'v0.1.600'
    Assert ($r -and -not $r.draft) 'v0.1.600 was not published'
    Assert ('v0.1.501' -in @($global:PpReleases | ForEach-Object tag_name)) 'v0.1.501 is gone'
    Assert ($plan -like '*v0.1.501 was not deleted*') "plan: $plan"
    Assert ($plan -like '*1 left for the next publish*') "plan: $plan"
}

Check 'Listing releases to prune that keeps failing after a successful publish is a warning, not a failed publish' {
    # How many listings a publish makes; the last is pruning's. Every one from that on fails.
    Reset 21
    Invoke-Publish '0.1.600'
    $listings = @($global:PpCalls | Where-Object { $_ -like "gh api repos/$repo/releases?*" }).Count
    Reset 21
    $global:PpFailListAfter = $listings - 1
    $plan = Invoke-Publish '0.1.600' -Plan
    Assert ($global:LASTEXITCODE -eq 0) "the step would exit $global:LASTEXITCODE"
    $r = $global:PpReleases | Where-Object tag_name -EQ 'v0.1.600'
    Assert ($r -and -not $r.draft) 'v0.1.600 was not published'
    Assert ($plan -like '*old pre-releases were not pruned*') "plan: $plan"
    Assert (@(Deletes).Count -eq 0) "deletes: $(Deletes)"
}

Check 'A leftover draft whose DELETE failed but went through does not stop the version being published' {
    Reset 3
    New-PpRelease 'v0.1.600' $true $true
    $global:PpDeleteFailures = 1; $global:PpDeleteGoesThrough = $true
    Invoke-Publish '0.1.600'
    Assert (@(Uploads).Count -eq 2) "uploads: $(Uploads)"
    Assert (@($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600').Count -eq 1) 'not exactly one v0.1.600'
}

Check 'A leftover draft that cannot be deleted still stops the publish before anything is uploaded' {
    Reset 3
    New-PpRelease 'v0.1.600' $true $true
    $global:PpDeleteFailures = 99
    $threw = $false
    try { Invoke-Publish '0.1.600' } catch { $threw = $true }
    Assert $threw 'the publish went on past a draft it could not delete'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
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
    try { Invoke-Publish '0.1.600' -DryRun -Only 'win' } catch { $threw = $_.Exception.Message -like "*win feed lists what is not this channel's*win-arm64-full.nupkg*" }
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

if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
if (Test-Path $fixtures) { Remove-Item $fixtures -Recurse -Force }
Remove-Item Env:GH_TOKEN
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
