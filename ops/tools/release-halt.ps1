#Requires -Version 7.2
<#
.SYNOPSIS
  Owner only: halts (withdraws) Windows releases, so no installed copy installs them, by adding
  them to the release halt list, rollout.json on the repository's pinned `rollout` release.

.DESCRIPTION
  The release kill switch (#580, ops/plan.md §6.4, ops/runbooks/windows-release.md). The app and
  the host read rollout.json before they download or install anything, and again just before
  installing a release already downloaded; a halted version is never installed, and a list that
  cannot be read installs nothing (src/AgentBridge.Host.Core/Updates/ReleaseRollout.cs).

  There is no rollback, by the owner's decision on #580: nothing is ever downgraded, not even from
  a halted build. Copies already on a halted version stay on it until a newer release (the revert,
  or the fix) is published, which they take by the ordinary idle rules. Halting is the stop;
  publishing that newer release is the recovery.

  -Version adds versions to the list and uploads it, then marks each one's release notes as
  withdrawn. -Initialize creates the `rollout` release with an empty list, once, before the first
  release that reads it ships (until then such copies install nothing). -DryRun reads everything
  and changes nothing.

  Uses gh's own sign-in (gh auth login, or GH_TOKEN), checked with gh auth status; no token is
  read into this script or printed. Needs contents: write on the repository. Production change:
  the owner's to run (ops/README.md); agent sessions prepare it, or run it with -DryRun.

.PARAMETER Version
  The version(s) to halt, as Velopack and the release tags name them: 0.1.1200 or v0.1.1200.
  Each must be a published release of the repository.

.PARAMETER Reason
  Why, in a sentence, for the withdrawn notice on each release's notes. Required with -Version.
  No identities, addresses or tokens (ops/README.md, "No identities").

.PARAMETER Initialize
  Creates the `rollout` pre-release (never marked latest) holding {"halt":[]}, or uploads that
  list to an existing `rollout` release that has none. Refuses to replace a list that is there.

.PARAMETER Repository
  owner/name. Default danswett/agent-bridge.

.PARAMETER DryRun
  Reads the releases and the current list, says what would change, and changes nothing.

.EXAMPLE
  pwsh ops/tools/release-halt.ps1 -Initialize -DryRun
.EXAMPLE
  pwsh ops/tools/release-halt.ps1 -Initialize
.EXAMPLE
  pwsh ops/tools/release-halt.ps1 -Version 0.1.1200 -Reason "Host crashes at start on Arm64 (#1234)" -DryRun
.EXAMPLE
  pwsh ops/tools/release-halt.ps1 -Version 0.1.1200 -Reason "Host crashes at start on Arm64 (#1234)"

.OUTPUTS
  Exit code 0 when done (or, with -DryRun, when it would be); a terminating error otherwise.
#>
[CmdletBinding(DefaultParameterSetName = 'Halt')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Halt')][string[]]$Version,
    [Parameter(Mandatory, ParameterSetName = 'Halt')][ValidateNotNullOrEmpty()][string]$Reason,
    [Parameter(Mandatory, ParameterSetName = 'Initialize')][switch]$Initialize,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'danswett/agent-bridge',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# What the app reads (ReleaseRollout): the asset's name and the release's tag.
$FileName = 'rollout.json'
$Tag = 'rollout'
# The same shape ReleaseRollout accepts: Velopack's version, an optional leading v.
$VersionPattern = '^[vV]?(?<v>[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?)$'
# Marks a release's notes as withdrawn; found again, it is not added twice.
$Withdrawn = '<!-- agent-bridge: withdrawn -->'
$mode = if ($DryRun) { 'dry run: ' } else { '' }

function Write-Plan([string]$line) { Write-Host "$mode$line" }

function Invoke-Gh {
    # gh reports a failure on stderr and a nonzero exit; unchecked, it would read as success.
    $output = & gh @args
    if ($LASTEXITCODE) { throw "gh $($args[0]) $($args[1]) failed ($LASTEXITCODE)" }
    $output
}

function ConvertTo-HaltVersion([string]$value) {
    $m = [regex]::Match($value.Trim(), $VersionPattern)
    if (-not $m.Success) { throw "'$value' is not a version (0.1.1200 or v0.1.1200)" }
    $m.Groups['v'].Value.ToLowerInvariant()
}

# The list as clients read it, or a terminating error: never a guess at what it meant.
function Read-HaltList([string]$json) {
    try { $doc = $json.TrimStart([char]0xFEFF) | ConvertFrom-Json -AsHashtable -NoEnumerate }
    catch { throw "$FileName is not JSON: $($_.Exception.Message); fix it by hand before halting" }
    if ($doc -isnot [System.Collections.IDictionary]) { throw "$FileName is not a JSON object; fix it by hand before halting" }
    # -ccontains: clients read "halt" exactly, and a hashtable would take "Halt" for it.
    if (-not (@($doc.Keys) -ccontains 'halt') -or $doc['halt'] -isnot [System.Collections.IList]) { throw "$FileName has no `"halt`" array; fix it by hand before halting" }
    $doc['halt'] = @($doc['halt'] | ForEach-Object {
            if ($_ -isnot [string]) { throw "$FileName names something that is not a version: $_" }
            ConvertTo-HaltVersion $_
        })
    $doc
}

function Format-HaltList($doc) {
    $doc['halt'] = @($doc['halt'] | Sort-Object -Unique { [semver]($_ -replace '\+.*$', '') }, { $_ })
    $json = $doc | ConvertTo-Json -Depth 10
    # What is uploaded must read back as what was meant.
    $null = Read-HaltList $json
    $json
}

# A scratch folder for the asset, beside this repository's other build output (out/ is ignored).
$work = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'out/release-halt'
function Reset-Work {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force $work | Out-Null
}

function Get-Release([string]$tag) {
    # A 404 means there is no such release, an answer; any other failure (network, sign-in,
    # permissions) stops here, rather than being taken for one.
    $output = & gh api "repos/$Repository/releases/tags/$tag" 2>&1
    if (-not $LASTEXITCODE) { return ($output | Where-Object { $_ -is [string] }) | ConvertFrom-Json }
    if (($output | Out-String) -match 'HTTP 404') { return $null }
    throw "gh api could not read the release $tag of $Repository ($LASTEXITCODE)"
}

function Get-HaltList {
    Reset-Work
    Invoke-Gh release download $Tag -R $Repository -p $FileName -D $work --clobber | Out-Null
    $file = Join-Path $work $FileName
    if (-not (Test-Path $file)) { throw "the $Tag release has no $FileName" }
    Read-HaltList (Get-Content $file -Raw)
}

function Send-HaltList([string]$json) {
    Reset-Work
    $file = Join-Path $work $FileName
    [IO.File]::WriteAllText($file, $json + "`n")
    Invoke-Gh release upload $Tag $file -R $Repository --clobber | Out-Null
    # Read back from the release as it now is, not as it was written.
    $back = Get-HaltList
    $back
}

# --- gh, signed in. Only the exit code: gh auth status masks its token, and nothing is printed.
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) is needed: https://cli.github.com' }
& gh auth status --hostname github.com *> $null
if ($LASTEXITCODE) { throw 'gh is not signed in to github.com: run gh auth login (or set GH_TOKEN), then run this again' }

$rollout = Get-Release $Tag

if ($Initialize) {
    $empty = Format-HaltList ([ordered]@{ halt = @() })
    if ($rollout) {
        $assets = @($rollout.assets | Where-Object name -CEQ $FileName)
        if ($assets) {
            $current = Get-HaltList
            Write-Plan "the $Tag release already holds $FileName, halting $(if ($current['halt'].Count) { $current['halt'] -join ', ' } else { 'nothing' }); left as it is"
            return
        }
        Write-Plan "uploading an empty $FileName to the existing $Tag release"
        if (-not $DryRun) { $null = Send-HaltList $empty }
    } else {
        Write-Plan "creating the $Tag pre-release (never latest) holding an empty $FileName"
        if (-not $DryRun) {
            Reset-Work
            $file = Join-Path $work $FileName
            [IO.File]::WriteAllText($file, $empty + "`n")
            $notes = @(
                "The Windows app's release halt list (#580): $FileName, read by every installed copy before it downloads or installs a release."
                ''
                'Maintained only by ops/tools/release-halt.ps1. Do not delete this release or its asset: copies that cannot read the list install nothing.'
            ) -join "`n"
            Invoke-Gh release create $Tag $file -R $Repository --prerelease --latest=false --title 'Release halt list' --notes $notes | Out-Null
            $null = Get-HaltList
        }
    }
    Write-Plan "$(if ($DryRun) { 'would be' } else { 'is' }) ready: https://github.com/$Repository/releases/download/$Tag/$FileName"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    return
}

# --- Halting.
$versions = @($Version | ForEach-Object { ConvertTo-HaltVersion $_ } | Sort-Object -Unique)
if (-not $rollout) { throw "the $Tag release does not exist yet: run this with -Initialize first" }
$releases = foreach ($v in $versions) {
    $release = Get-Release "v$v"
    if (-not $release) { throw "there is no release v$v in $Repository; nothing was changed" }
    $release
}

$list = Get-HaltList
$adding = @($versions | Where-Object { $_ -notin $list['halt'] })
if ($adding) {
    $list['halt'] = @($list['halt']) + $adding
    $json = Format-HaltList $list
    Write-Plan "halting $($adding -join ', '): $FileName becomes $($json -replace '\s+', ' ')"
    if (-not $DryRun) {
        $back = Send-HaltList $json
        $missing = @($adding | Where-Object { $_ -notin $back['halt'] })
        if ($missing) { throw "$FileName was uploaded, but reads back without $($missing -join ', ')" }
    }
} else {
    Write-Plan "$($versions -join ', ') already halted"
}

# --- The notes, after the list: the list is what stops installs.
$pacific = [TimeZoneInfo]::FindSystemTimeZoneById('America/Los_Angeles')
$when = [TimeZoneInfo]::ConvertTime([DateTimeOffset]::UtcNow, $pacific).ToString('yyyy-MM-dd HH:mm') + ' PT'
foreach ($r in $releases) {
    $body = if ($r.body) { [string]$r.body } else { '' }
    if ($body.Contains($Withdrawn)) {
        Write-Plan "$($r.tag_name)'s notes already say it is withdrawn"
        continue
    }
    $notice = @(
        $Withdrawn
        '> [!CAUTION]'
        "> **Withdrawn (halted) $when.** $Reason"
        '>'
        '> Installed copies do not install this release. Copies already on it are not rolled back: they stay on it until a newer release, which they take by themselves.'
        ''
        ''
    ) -join "`n"
    Write-Plan "marking $($r.tag_name)'s notes as withdrawn"
    if (-not $DryRun) {
        # Through stdin: release notes can exceed Windows' command-line limit.
        @{ body = $notice + $body } | ConvertTo-Json -Compress | gh api -X PATCH "repos/$Repository/releases/$($r.id)" --input - | Out-Null
        if ($LASTEXITCODE) { throw "$FileName halts it, but $($r.tag_name)'s notes could not be updated ($LASTEXITCODE)" }
    }
}
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
Write-Plan "done. Recovery is a newer release (the revert or the fix): publish it; copies take it by the ordinary idle rules. Nothing is downgraded."
