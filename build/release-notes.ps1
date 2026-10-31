# Writes the release notes a package carries (vpk pack --releaseNotes): the commits since the latest
# published release, newest first, one line each. Installed copies show them in the toast after an
# update, so they say what changed.
#
#   pwsh build/release-notes.ps1 -Repository danswett/agent-bridge -Commit <sha> -Out out/app/notes.md
#
# Needs gh (GH_TOKEN). Never fails a build: notes it cannot read become the commit alone.
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$Out,
    [int]$Most = 15
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Line([string[]]$ghArgs) {
    $lines = & gh @ghArgs 2>$null
    if ($LASTEXITCODE) { return @() }
    @($lines | Where-Object { $_ })
}

$latest = @(Get-Line @('release', 'list', '-R', $Repository, '-L', '1', '--exclude-drafts', '--json', 'tagName', '--jq', '.[0].tagName')) | Select-Object -First 1
$subjects = @(if ($latest) {
    # Oldest first from GitHub; the newest matter most, and a toast has room for few.
    $since = @(Get-Line @('api', "repos/$Repository/compare/$latest...$Commit", '--jq', '.commits[].commit.message | split("\n")[0]'))
    if ($since.Count -gt 0) { $since[($since.Count - 1)..0] }
})
if (-not $subjects) { $subjects = @(Get-Line @('api', "repos/$Repository/commits/$Commit", '--jq', '.commit.message | split("\n")[0]')) }
if (-not $subjects) { $subjects = @("Built from $Commit.") }

$lines = @($subjects | Select-Object -First $Most | ForEach-Object { "- $_" })
if (@($subjects).Count -gt $Most) { $lines += "- and $(@($subjects).Count - $Most) more since $latest" }
New-Item -ItemType Directory -Force (Split-Path ([IO.Path]::GetFullPath($Out))) | Out-Null
Set-Content -Path $Out -Value ($lines -join "`n") -Encoding utf8NoBOM
Write-Host "release notes, $(@($subjects).Count) change(s)$(if ($latest) { " since $latest" }):"
$lines | ForEach-Object { Write-Host "  $_" }
