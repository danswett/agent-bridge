# Publishes a release the spacing deferred while main is idle (docs/app-windows.md, "Spacing"):
# without it, a green run refused inside the 15 minutes would wait for main's next push. CI's
# publish-pending.yml runs it every 5 minutes. It takes main's newest finished CI run whose
# quality-gate and publish jobs succeeded (a waived red Arm64 leg included), and once no newer run
# is still going (for under 3 hours), that run's version is above every release and untagged, its
# commit follows the latest release's, and the newest release is 15 minutes old, re-runs that
# publish job (4 times at most): the same job, in the same publish-prerelease concurrency group,
# which checks everything again under the publish lock. It reads, and re-runs at most one job.
# Anything it can't read publishes nothing and is tried again on the next tick.
#
#   pwsh build/publish-pending.ps1 -Repository danswett/agent-bridge
#
# Needs gh, and GH_TOKEN with actions: write.
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [string]$Workflow = 'ci.yml',
    # As ci.yml's APP_VERSION: 0.1.<run number>.
    [string]$VersionPrefix = '0.1.'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'release-spacing.ps1')

function Read-Gh {
    $output = & gh @args
    if ($LASTEXITCODE) { throw "gh $($args[0]) $($args[1]) failed ($LASTEXITCODE)" }
    @($output | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Get-Pending {
    $now = [DateTimeOffset](Get-Date)
    $runs = Read-Gh api "repos/$Repository/actions/workflows/$Workflow/runs?branch=main&event=push&per_page=30" --jq '.workflow_runs[] | {id, run_number, run_attempt, head_sha, status, conclusion, created_at}'
    # Main's newest finished run whose quality-gate and publish jobs succeeded, of the newest 5. Not
    # the run's conclusion: the Arm64 waiver (#2454) leaves a waived run red, its publish having run.
    $green = $null
    foreach ($run in @($runs | Where-Object status -EQ 'completed' | Sort-Object run_number -Descending | Select-Object -First 5)) {
        $jobs = Read-Gh api "repos/$Repository/actions/runs/$($run.id)/jobs?filter=latest&per_page=100" --jq '.jobs[] | {id, name, conclusion}'
        $gate = @($jobs | Where-Object { $_.name -ceq 'quality-gate' -and $_.conclusion -eq 'success' })
        $publish = @($jobs | Where-Object { $_.name -ceq 'publish' -and $_.conclusion -eq 'success' })
        if ($gate.Count -eq 1 -and $publish.Count -eq 1) { $green = $run; break }
    }
    if (-not $green) { return "no finished run of main's newest 5 built, checked and handed its app to publish" }
    # A newer run may publish first; one going for 3 hours is stuck, and holds nothing.
    $going = @($runs | Where-Object { $_.run_number -gt $green.run_number -and $_.status -ne 'completed' -and $now - [DateTimeOffset]$_.created_at -lt [TimeSpan]::FromHours(3) })
    if ($going) { return "run $($going[0].run_number) is still going and may publish first" }
    $version = [semver]"$VersionPrefix$($green.run_number)"
    # Bounded: a version whose tag survives its deleted release is published as far as publish
    # reads it, and re-running it would loop; and no publish job is re-run more than 4 times.
    $tagged = @(& gh api "repos/$Repository/git/matching-refs/tags/v$version" --jq '.[].ref')
    if ($LASTEXITCODE) { throw "couldn't read v$version's tag" }
    if ($tagged -contains "refs/tags/v$version") { return "v$version is tagged already; nothing to re-run" }
    if ($green.run_attempt -ge 5) { return "::warning::run $($green.run_number) was run $($green.run_attempt) times; not re-running its publish again (v$version stays unpublished until a later run, or Quality, publishes)" }
    $releases = Read-Gh api "repos/$Repository/releases?per_page=100" --paginate --jq '.[] | {id, tag_name, draft, prerelease, published_at}'
    $released = @($releases | Where-Object { -not $_.draft -and $_.tag_name -match '^v(\d+\.\d+\.\d+)$' } | ForEach-Object { [semver]$_.tag_name.Substring(1) } | Sort-Object -Descending)
    if ($released.Count -and $released[0] -ge $version) { return "v$version, main's newest green run's, is released or behind v$($released[0])" }
    if ($released.Count) {
        $ref = @(& gh api "repos/$Repository/git/ref/tags/v$($released[0])" --jq '.object.type, .object.sha')
        if ($LASTEXITCODE -or $ref.Count -ne 2 -or $ref[0] -cne 'commit' -or $ref[1] -cnotmatch '^[0-9a-f]{40}$') { throw "couldn't read v$($released[0])'s commit" }
        $ancestry = [string](& gh api "repos/$Repository/compare/$($ref[1])...$($green.head_sha)" --jq '.status')
        if ($LASTEXITCODE) { throw "couldn't compare $($green.head_sha) with v$($released[0])'s commit" }
        if ($ancestry -cne 'ahead') { return "$($green.head_sha) doesn't follow v$($released[0])'s commit ($ancestry)" }
    }
    $newest = Get-NewestRelease $releases $now
    if ($newest -and $newest.Age -lt $ReleaseSpacing) { return "v$version waits: $($newest.Tag) was published $('{0:N1}' -f $newest.Age.TotalMinutes) minutes ago" }
    & gh api -X POST "repos/$Repository/actions/jobs/$($publish[0].id)/rerun" | Out-Null
    if ($LASTEXITCODE) { throw "re-running run $($green.run_number)'s publish job failed" }
    "re-ran run $($green.run_number)'s publish job for v$version"
}

try { Write-Host (Get-Pending) }
catch { Write-Host "::warning::nothing re-run this time: $($_.Exception.Message); the next check tries again" }
# A failed read is a warning, tried again on the next tick, not a red run.
$global:LASTEXITCODE = 0
