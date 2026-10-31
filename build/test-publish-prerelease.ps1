# Checks build/publish-prerelease.ps1, and the delta base and build identity build/pack-app.ps1
# gives the package, against stand-ins for gh, vpk and dotnet: nothing is built, uploaded,
# published or deleted, and the folders are under out/test-publish-prerelease.
#
#   pwsh build/test-publish-prerelease.ps1
#
# What it guards: a merge publishes exactly one pre-release of its own version, never a second
# copy, never one older than what is out, never a dev build, and the pruning never reaches a full
# release or a tag.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publishScript = Join-Path $PSScriptRoot 'publish-prerelease.ps1'
$packScript = Join-Path $PSScriptRoot 'pack-app.ps1'
$scratch = Join-Path $PSScriptRoot '..' 'out' 'test-publish-prerelease'
$fixtures = Join-Path $PSScriptRoot '..' 'out' 'test-publish-prerelease-hosts'
$repo = 'danswett/agent-bridge'
$commit = '0123456789abcdef0123456789abcdef01234567'
$env:GH_TOKEN = 'stand-in-token'
Remove-Item Env:GITHUB_STEP_SUMMARY, Env:GITHUB_RUN_ID, Env:GITHUB_ACTIONS -ErrorAction SilentlyContinue

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
$global:PpPackSaw = $null

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
        foreach ($r in $global:PpReleases) { $r | ConvertTo-Json -Compress }
    } elseif ($a[0] -eq 'api' -and $a[1] -like "repos/$repo/git/matching-refs/tags/*") {
        $prefix = 'refs/tags/' + $a[1].Substring("repos/$repo/git/matching-refs/tags/".Length)
        $global:PpTags | Where-Object { $_.StartsWith($prefix) }
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'DELETE') {
        $id = [long]($a[3] -split '/')[-1]
        [void]$global:PpReleases.RemoveAll({ param($r) $r.id -eq $id })
    } elseif ($a[0] -eq 'api' -and $a[1] -eq '-X' -and $a[2] -eq 'PATCH') {
        $id = [long]($a[3] -split '/')[-1]
        $r = $global:PpReleases | Where-Object id -EQ $id
        if ($a -contains 'draft=false') { $r.draft = $false; $global:PpTags.Add("refs/tags/$($r.tag_name)") }
    } elseif ($a[0] -eq 'release' -and $a[1] -eq 'upload') {
    } else { throw "stand-in gh: unexpected $($a -join ' ')" }
}

function global:vpk {
    $a = @($args)
    $global:LASTEXITCODE = 0
    function Arg([string]$name) { $i = [array]::IndexOf($a, $name); if ($i -ge 0) { $a[$i + 1] } }
    if ($a[0] -eq 'upload' -and $a[1] -eq 'github') {
        $global:PpCalls.Add("vpk upload github --tag $(Arg '--tag') --targetCommitish $(Arg '--targetCommitish') pre=$($a -contains '--pre') publish=$($a -contains '--publish')")
        New-PpRelease (Arg '--tag') $true $true
    } elseif ($a[0] -eq 'upload' -and $a[1] -eq 'local') {
        $global:PpCalls.Add('vpk upload local')
        $assets = @(Get-Content (Join-Path (Arg '--outputDir') 'assets.win.json') -Raw | ConvertFrom-Json)
        $feed = @{ Assets = @($assets | Where-Object { $_.Type -in 'Full', 'Delta' } | ForEach-Object {
                    @{ Version = ($_.RelativeFileName -replace '^AgentBridgeApp-(.+)-(full|delta)\.nupkg$', '$1'); Type = $_.Type } }) }
        $feed | ConvertTo-Json -Depth 4 | Set-Content (Join-Path (Arg '--path') 'releases.win.json')
    } elseif ($a[0] -eq 'download' -and $a[1] -eq 'github') {
        $global:PpCalls.Add('vpk download github')
        if ($global:PpDownload -eq 'fail') { $global:LASTEXITCODE = 1; return }
        if ($global:PpDownload) {
            New-Item -ItemType Directory -Force (Arg '-o') | Out-Null
            Set-Content (Join-Path (Arg '-o') "AgentBridgeApp-$($global:PpDownload)-full.nupkg") "full $($global:PpDownload)"
        }
    } elseif ($a[0] -eq 'delta' -and $a[1] -eq 'generate') {
        # A stand-in delta names the two versions it lies between.
        $global:PpCalls.Add('vpk delta generate')
        $from = (Get-Content (Arg '--base')) -replace '^full ', ''
        $to = (Split-Path (Arg '--new') -Leaf) -replace '^AgentBridgeApp-(.+)-full\.nupkg$', '$1'
        Set-Content (Arg '--output') "delta $from->$to"
    } elseif ($a[0] -eq 'delta' -and $a[1] -eq 'patch') {
        # Rebuilds the new package, byte for byte, only from the base the delta was made from.
        $base = (Get-Content (Arg '--base')) -replace '^full ', ''
        if ((Get-Content (Arg '--patch')) -notmatch '^delta (.+)->(.+)$' -or $Matches[1] -ne $base) { $global:LASTEXITCODE = 1; return }
        Copy-Item (Join-Path (Split-Path (Arg '--patch')) "AgentBridgeApp-$($Matches[2])-full.nupkg") (Arg '--output')
    } elseif ($a[0] -eq 'pack') {
        $global:PpPackSaw = @(Get-ChildItem (Arg '-o') -Filter *.nupkg -ErrorAction SilentlyContinue | ForEach-Object Name)
    } else { throw "stand-in vpk: unexpected $($a -join ' ')" }
}

function global:dotnet {
    $a = @($args)
    $global:LASTEXITCODE = 0
    $out = $a[[array]::IndexOf($a, '-o') + 1]
    foreach ($f in 'AgentBridge.exe', 'AgentBridge.pri', 'agent-bridge-host.exe', 'agent-bridge-hostw.exe', 'agent-bridge-injector.exe', 'agent-bridge-host-hook.exe', 'Assets\AgentBridge.ico', 'Assets\AgentBridge.png') {
        $path = Join-Path $out $f
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        Set-Content $path 'stand-in'
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
    $global:PpReleases.Clear(); $global:PpTags.Clear(); $global:PpCalls.Clear()
    $global:PpDownload = $null; $global:PpPackSaw = $null; $global:PpIgnoreRelease = $false
    for ($i = 1; $i -le $prereleases; $i++) { New-PpRelease "v0.1.$(500 + $i)" }
    if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
}

function New-Package([string]$path, [string]$kind) {
    # A full package as vpk packs it, as far as host-identity.ps1 reads one: the host under lib/app.
    $stage = Join-Path $scratch 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $stage 'lib' 'app') | Out-Null
    Copy-Item $global:PpHost[$kind] (Join-Path $stage 'lib' 'app' 'agent-bridge-host.dll')
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $path)
    Remove-Item $stage -Recurse -Force
}

function New-Folder([string]$version, [string]$DeltaFrom, [string[]]$Skip = @(), [switch]$DevBuild) {
    # As vpk pack leaves it: with -DeltaFrom, a delta from that version and its full package beside.
    $dir = Join-Path $scratch 'releases'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $assets = [Collections.Generic.List[object]]::new()
    $assets.Add([ordered]@{ RelativeFileName = 'AgentBridgeApp-win-Setup.exe'; Type = 'Installer' })
    $assets.Add([ordered]@{ RelativeFileName = 'AgentBridgeApp-win-Portable.zip'; Type = 'Portable' })
    $assets.Add([ordered]@{ RelativeFileName = "AgentBridgeApp-$version-full.nupkg"; Type = 'Full' })
    if ($DeltaFrom) {
        $assets.Add([ordered]@{ RelativeFileName = "AgentBridgeApp-$version-delta.nupkg"; Type = 'Delta' })
        Set-Content (Join-Path $dir "AgentBridgeApp-$DeltaFrom-full.nupkg") "full $DeltaFrom"
    }
    foreach ($a in $assets) {
        $path = Join-Path $dir $a.RelativeFileName
        if ($a.RelativeFileName -in $Skip) { continue }
        switch ($a.Type) {
            'Full' { New-Package $path $(if ($DevBuild) { 'dev' } else { 'release' }) }
            'Delta' { Set-Content $path "delta $DeltaFrom->$version" }
            default { Set-Content $path 'stand-in' }
        }
    }
    ConvertTo-Json @($assets) -Compress | Set-Content (Join-Path $dir 'assets.win.json')
    $dir
}

function Invoke-Publish([string]$version, [switch]$DryRun, [string]$DeltaFrom, [string[]]$Skip = @(), [switch]$DevBuild) {
    $dir = New-Folder $version -DeltaFrom $DeltaFrom -Skip $Skip -DevBuild:$DevBuild
    & $publishScript -Version $version -ReleasesDir $dir -Repository $repo -Commit $commit -DryRun:$DryRun 6>$null
}

function Get-Asset { @(Get-Content (Join-Path $scratch 'releases' 'assets.win.json') -Raw | ConvertFrom-Json) }
function Get-Delta { Get-Content (Join-Path $scratch 'releases' 'AgentBridgeApp-0.1.600-delta.nupkg') -ErrorAction SilentlyContinue }

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Output "PASS $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Uploads { @($global:PpCalls | Where-Object { $_ -like 'vpk upload github*' }) }
function Deletes { @($global:PpCalls | Where-Object { $_ -like 'gh api -X DELETE*' }) }

# --- Publishing.
Check 'A new version goes up as a draft, then is published as a pre-release of the commit with assets.win.json beside it' {
    Reset 3
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.503'
    Assert ($global:PpCalls -notcontains 'vpk delta generate') 'a delta from the latest release was rebuilt'
    Assert ((Get-Delta) -eq 'delta 0.1.503->0.1.600') "delta: $(Get-Delta)"
    Assert (@(Uploads).Count -eq 1) "uploads: $(Uploads)"
    Assert (@(Uploads)[0] -eq "vpk upload github --tag v0.1.600 --targetCommitish $commit pre=True publish=False") "upload: $(Uploads)"
    Assert ([bool]($global:PpCalls | Where-Object { $_ -like 'gh release upload v0.1.600 *assets.win.json*' })) 'assets.win.json was not uploaded'
    $r = $global:PpReleases | Where-Object tag_name -EQ 'v0.1.600'
    Assert ($r -and -not $r.draft -and $r.prerelease) "release: $($r | ConvertTo-Json -Compress)"
    $patch = $global:PpCalls | Where-Object { $_ -like 'gh api -X PATCH*' }
    Assert ($patch -match [regex]::Escape($commit)) 'the notes do not name the commit'
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

Check 'A draft left by a failed attempt is deleted and the version uploaded again' {
    Reset 3
    New-PpRelease 'v0.1.600' $true $true
    $leftover = $global:PpReleases | Where-Object tag_name -EQ 'v0.1.600'
    Invoke-Publish '0.1.600'
    Assert (@(Deletes) -contains "gh api -X DELETE repos/$repo/releases/$($leftover.id)") "deletes: $(Deletes)"
    Assert (@(Uploads).Count -eq 1) "uploads: $(Uploads)"
    Assert (@($global:PpReleases | Where-Object tag_name -EQ 'v0.1.600').Count -eq 1) 'not exactly one v0.1.600'
}

Check 'A newer draft does not stop a version being published' {
    Reset 3
    New-PpRelease 'v0.1.601' $true $true
    Invoke-Publish '0.1.600'
    Assert (@(Uploads).Count -eq 1) "uploads: $(Uploads)"
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
    try { & $publishScript -Version '0.1.600' -ReleasesDir $dir -Repository $repo -Commit $commit 6>$null } catch { $threw = $true }
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
    Assert (@(Uploads).Count -eq 1) "uploads: $(Uploads)"
}

Check 'A delta that cannot be rebuilt is left out and the full package published alone' {
    Reset 3
    $global:PpDownload = 'fail'
    Invoke-Publish '0.1.600' -DeltaFrom '0.1.501'
    Assert ($null -eq (Get-Delta)) "delta: $(Get-Delta)"
    Assert (-not (Get-Asset | Where-Object Type -EQ 'Delta')) 'assets.win.json still lists the delta'
    Assert (@(Uploads).Count -eq 1) "uploads: $(Uploads)"
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

Check 'A dry run uploads, publishes and deletes nothing, and has vpk read the folder' {
    Reset 22
    Invoke-Publish '0.1.600' -DryRun -DeltaFrom '0.1.522'
    Assert (@(Uploads).Count -eq 0) "uploads: $(Uploads)"
    Assert (@(Deletes).Count -eq 0) "deletes: $(Deletes)"
    Assert (-not ($global:PpCalls | Where-Object { $_ -like 'gh api -X*' -or $_ -like 'gh release*' })) "writes: $($global:PpCalls -join '; ')"
    Assert ($global:PpCalls -contains 'vpk upload local') 'vpk did not read the folder'
    Assert ($global:PpReleases.Count -eq 22) 'the releases changed'
}

# --- The delta base pack-app.ps1 gives vpk pack.
function Invoke-Pack([string]$version) {
    & $packScript -Version $version -Out $scratch -DeltaFrom "https://github.com/$repo" 6>$null 3>$null
}

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

Check 'A package left in the output folder by an earlier pack is not a delta base' {
    Reset
    New-Item -ItemType Directory -Force (Join-Path $scratch 'releases') | Out-Null
    Set-Content (Join-Path $scratch 'releases' 'AgentBridgeApp-0.1.590-full.nupkg') 'full 0.1.590'
    $global:PpDownload = 'fail'
    Invoke-Pack '0.1.600'
    Assert ($null -ne $global:PpPackSaw -and @($global:PpPackSaw).Count -eq 0) "vpk pack saw: $($global:PpPackSaw)"
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
