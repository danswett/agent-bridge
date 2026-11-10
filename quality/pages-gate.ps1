#Requires -Version 7
# Decides whether a green main CI run's commit should be deployed to Pages (pages.yml, gate job).
# The newest green commit deploys: a run is skipped only when its commit is the one already live,
# or older than it. "Is main's tip" would rarely hold, since main moves faster than CI finishes.
#   -Head     the commit CI tested
#   -Live     the commit the live site was built from (config.js APP_VERSION); empty if unreadable
#   -Status   GitHub's compare status of Live...Head (ahead, identical, behind, diverged); empty if unknown
# Prints "deploy" or "skip" and the reason on the next line.
param(
    [Parameter(Mandatory)][string]$Head,
    [string]$Live = '',
    [string]$Status = ''
)
$ErrorActionPreference = 'Stop'

if (-not $Live -or $Live -notmatch '^[0-9a-f]{40}$') { 'deploy'; 'the live commit is unknown (first deploy, or not a commit)'; return }
if ($Live -eq $Head -or $Status -eq 'identical') { 'skip'; "$Head is already live"; return }
if ($Status -eq 'behind') { 'skip'; "$Head is older than the live $Live"; return }
'deploy'; "$Head is newer than, or not comparable with, the live $Live"
