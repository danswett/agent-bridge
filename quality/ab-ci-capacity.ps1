#Requires -Version 7.4
<#
.SYNOPSIS
  The ab-ci pool's capacity check (quality/README.md, "CI: speed, runners and what gates main", capacity first; #2454).

.DESCRIPTION
  Set AB_CI_SELF_HOSTED to true only while this exits 0, and run it again in a busy hour while it is set. It reads the
  repository's runners and the jobs of its queued and in-progress runs, then runs the Quality tool's ab-ci-capacity
  check (built beforehand: dotnet build tools/AgentBridge.Quality -c Release). Listing runners needs administration
  read on the repository, so SRE or the owner runs it with gh signed in as themselves; it only reads. Exit 0: the pool
  keeps up. Exit 1: too few runners online, too many ab-ci jobs queued, or one waiting too long. Exit 2: it couldn't read.
#>
param(
    [string] $Repository = 'danswett/agent-bridge',
    [int] $MaxQueued = 0,
    [int] $MaxWait = 60,
    [int] $MinOnline = 1,
    # Runs read per status (queued, in progress): enough for a busy hour, few enough calls.
    [ValidateRange(1, 100)][int] $Runs = 30
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools/AgentBridge.Quality/bin/Release/net10.0/agent-bridge-quality.dll'
if (-not (Test-Path $tool)) { Write-Error "Build the Quality tool first: dotnet build tools/AgentBridge.Quality -c Release"; exit 2 }
$dir = Join-Path ([IO.Path]::GetTempPath()) "ab-ci-capacity-$PID"
New-Item -ItemType Directory -Force (Join-Path $dir 'jobs') | Out-Null
try {
    $runners = gh api "repos/$Repository/actions/runners?per_page=100"
    if ($LASTEXITCODE) { Write-Error "couldn't list the runners ($LASTEXITCODE): the token needs administration read"; exit 2 }
    # One page only: past 100 runners this would undercount, so it refuses instead.
    if (($runners | ConvertFrom-Json).total_count -gt 100) { Write-Error 'more than 100 runners: page through them before trusting this check'; exit 2 }
    $runners | Set-Content (Join-Path $dir 'runners.json') -Encoding utf8NoBOM
    foreach ($status in 'queued', 'in_progress') {
        $ids = gh api "repos/$Repository/actions/runs?status=$status&per_page=$Runs" --jq '.workflow_runs[].id'
        if ($LASTEXITCODE) { Write-Error "couldn't list $status runs ($LASTEXITCODE)"; exit 2 }
        foreach ($id in $ids) {
            $jobs = gh api "repos/$Repository/actions/runs/$id/jobs?filter=latest&per_page=100"
            if ($LASTEXITCODE) { Write-Error "couldn't read run $id's jobs ($LASTEXITCODE)"; exit 2 }
            $jobs | Set-Content (Join-Path $dir "jobs/$id.json") -Encoding utf8NoBOM
        }
    }
    dotnet $tool ab-ci-capacity --runners (Join-Path $dir 'runners.json') --jobs (Join-Path $dir 'jobs') --max-queued $MaxQueued --max-wait $MaxWait --min-online $MinOnline
    exit $LASTEXITCODE
}
finally { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue }
