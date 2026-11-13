#Requires -Version 7
param([object[]]$Runs)
$required = @(Get-Content -Raw (Join-Path $PSScriptRoot 'pages-jobs.json') | ConvertFrom-Json)
$latest = $Runs | Sort-Object run_number -Descending | Group-Object head_sha | ForEach-Object { $_.Group[0] }
foreach ($run in ($latest | Sort-Object run_number -Descending)) {
    if ($run.event -ne 'push' -or $run.head_branch -ne 'main' -or $run.head_sha -notmatch '^[0-9a-f]{40}$') { continue }
    $jobs = @($run.jobs | Where-Object { $_.name -in $required -or $_.name -like 'pwa-ui*' })
    if ($jobs.Count -ne $required.Count -or @(Compare-Object $required @($jobs.name)).Count) { continue }
    if (@($jobs | Where-Object { $_.head_sha -ne $run.head_sha -or $_.status -ne 'completed' -or $_.conclusion -ne 'success' }).Count) { continue }
    return @{ sha = $run.head_sha; jobs = $jobs; reason = 'named-jobs-success' }
}
@{ sha = ''; jobs = @(); reason = 'named-jobs-not-success' }
