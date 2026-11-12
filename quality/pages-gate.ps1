#Requires -Version 7
# Decides whether a tested main commit should be deployed to Pages (pages.yml, gate job).
# The newest green commit deploys: a run is skipped only when its commit is the one already live,
# or older than it. "Is main's tip" would rarely hold, since main moves faster than CI finishes.
#   -Head     the commit CI tested
#   -Live     the commit the live site was built from (config.js APP_VERSION); unknown blocks
#   -Status   GitHub's compare status of Live...Head (ahead, identical, behind, diverged); empty if unknown
# Prints "deploy" or "skip" and the reason on the next line.
param(
    [Parameter(Mandatory)][string]$Head,
    [string]$Live = '',
    [string]$Status = '',
    [string]$MainStatus = '',
    [string[]]$Files = @()
)
$ErrorActionPreference = 'Stop'

if ($MainStatus -notin @('ahead', 'identical')) { 'skip'; 'main-ancestry-unproven'; return }
if ($Live -notmatch '^[0-9a-f]{40}$') { 'skip'; 'live-sha-unavailable'; return }
if ($Live -eq $Head -or $Status -eq 'identical') { 'skip'; 'already-live'; return }
if ($Status -ne 'ahead') { 'skip'; 'live-ancestry-unproven'; return }
if (-not @($Files | Where-Object { $_ -match '^(pwa/|design/fonts/|spikes/.*/web/|\.github/workflows/pages\.yml$)' }).Count) { 'skip'; 'no-pages-change'; return }
'deploy'; 'newer-tested-sha'
