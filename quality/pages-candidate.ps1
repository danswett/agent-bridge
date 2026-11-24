#Requires -Version 7
# The main commit Pages may deploy: the newest whose plan, product (ubuntu-latest) and PWA jobs (pages-jobs.json) all passed (#2143).
# Runs are main's push CI runs, each with its jobs. Under the two lanes (S2, #4104) the PWA jobs move from ci.yml (FAST) to full.yml (FULL), and a Pages
# deploy is a release, so it waits for FULL: when a run lacks some of those jobs, -GetFull is asked for the same commit's FULL run (by head_sha, as the
# release train finds it) and the jobs are taken from whichever lane holds them. A FAST run that lacks them, with no FULL run yet, never deploys. Only the named
# jobs count, so a FULL run red elsewhere (spikes, Arm64) does not hold Pages. -GetFull is not called for a commit with a red or running FAST job, and a job in
# both lanes fails the exact-set check below. Without -GetFull, one run must hold every job, as before.
param([object[]]$Runs, [scriptblock]$GetFull)
$families = @(Get-Content -Raw (Join-Path $PSScriptRoot 'pages-jobs.json') | ConvertFrom-Json)
if ($families.Count -and $families[0] -is [string]) { $families = @(,$families) }
$required = @($families | ForEach-Object { $_ } | Sort-Object -Unique)
function Select-Named($jobs) { @($jobs | Where-Object { $_.name -in $required -or $_.name -like 'pwa-ui*' }) }
function Test-Complete($jobs) {
    $matched = 0
    foreach ($family in $families) {
        if ($jobs.Count -eq $family.Count -and -not @(Compare-Object $family @($jobs.name)).Count) { $matched++ }
    }
    $matched -eq 1
}
$latest = $Runs | Sort-Object run_number -Descending | Group-Object head_sha | ForEach-Object { $_.Group[0] }
foreach ($run in ($latest | Sort-Object run_number -Descending)) {
    if ($run.event -ne 'push' -or $run.head_branch -ne 'main' -or $run.head_sha -notmatch '^[0-9a-f]{40}$') { continue }
    $jobs = Select-Named $run.jobs
    $passed = { param($job) $job.head_sha -eq $run.head_sha -and $job.status -eq 'completed' -and $job.conclusion -eq 'success' }
    if ($GetFull -and -not (Test-Complete $jobs) -and -not @($jobs | Where-Object { -not (& $passed $_) }).Count) {
        $full = & $GetFull $run.head_sha
        if ($full) { $jobs = @($jobs) + @(Select-Named $full.jobs) }
    }
    if (-not (Test-Complete $jobs)) { continue }
    if (@($jobs | Where-Object { -not (& $passed $_) }).Count) { continue }
    return @{ sha = $run.head_sha; jobs = $jobs; reason = 'named-jobs-success' }
}
@{ sha = ''; jobs = @(); reason = 'named-jobs-not-success' }
