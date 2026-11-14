# quality-gate's time-boxed win-arm64 waiver (#2564, owner decision 2026-11-13). Reads the needs
# JSON and this run's jobs (GitHub's jobs API, paginated and slurped) and writes to -OutFile the ids
# of the needed jobs whose failures are only the three Arm64 legs named below; ci.yml then reads
# those as success. A matrix collapses to one result per job id in needs, so the legs can only be
# told apart through the API. Anything doubtful waives nothing: the switch not exactly "on", the
# expiry reached, an API answer of an unexpected shape, or any other job that did not succeed.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NeedsFile,
    [Parameter(Mandatory)][string]$JobsFile,
    [Parameter(Mandatory)][string]$OutFile,
    [string]$Setting = '',
    [string]$SummaryFile = '',
    [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow
)
$ErrorActionPreference = 'Stop'
# The only expiry, computed here so the waiver stops by itself; 2026-11-20 00:00 PT.
$expiry = [DateTimeOffset]::Parse('2026-11-20T08:00:00Z', [cultureinfo]::InvariantCulture)
$waivable = @('app (win-arm64)', 'app-installer (win-arm64)', 'product (windows-11-arm)')
$ids = @()
$lines = @()
try {
    if ($Setting -cne 'on') {
        $lines += 'ARM64_WAIVER is not on: nothing waived.'
    } elseif ($Now -ge $expiry) {
        $lines += 'The waiver ended 2026-11-20 00:00 PT (08:00 UTC): nothing waived.'
    } else {
        $needs = Get-Content -Raw -LiteralPath $NeedsFile | ConvertFrom-Json
        $pages = @(Get-Content -Raw -LiteralPath $JobsFile | ConvertFrom-Json)
        $jobs = @()
        foreach ($page in $pages) {
            if ($page.jobs -isnot [array] -or $null -eq $page.total_count) { throw 'unexpected shape from the jobs API' }
            $jobs += $page.jobs
        }
        # A short list would hide a failing leg, so it must be whole.
        if ($pages.Count -eq 0 -or [int]$pages[0].total_count -ne $jobs.Count) { throw 'the jobs API list is incomplete' }
        foreach ($job in $jobs) {
            if ($job.name -isnot [string] -or ($null -ne $job.conclusion -and $job.conclusion -isnot [string])) { throw 'unexpected shape from the jobs API' }
        }
        # Every job in the run, not only the legs of one needed job, so a leg renamed out of its
        # job's name still stops the waiver. Running jobs (no conclusion yet) say nothing.
        $stray = @($jobs | Where-Object {
            $null -ne $_.conclusion -and $_.conclusion -cnotin @('success', 'skipped', 'neutral') -and
            -not ($_.conclusion -ceq 'failure' -and $_.name -cin $waivable) })
        if ($stray.Count -gt 0) {
            $lines += 'Nothing waived, other jobs did not succeed: ' + (($stray | ForEach-Object { "$($_.name) ($($_.conclusion))" }) -join ', ')
        } else {
            foreach ($need in $needs.PSObject.Properties) {
                if ($need.Value.result -cne 'failure') { continue }
                $prefix = "$($need.Name) ("
                $hits = @($jobs | Where-Object { $_.conclusion -ceq 'failure' -and $_.name -cin $waivable -and $_.name.StartsWith($prefix, [StringComparison]::Ordinal) })
                if ($hits.Count -eq 0) { continue }
                # Every other leg of the job must have passed: a skipped, neutral or unfinished
                # sibling (an x64 leg that never ran) is not a pass.
                $unpassed = @($jobs | Where-Object { $_.name.StartsWith($prefix, [StringComparison]::Ordinal) -and -not ($_.conclusion -ceq 'success' -or ($_.conclusion -ceq 'failure' -and $_.name -cin $waivable)) })
                if ($unpassed.Count -gt 0) {
                    $lines += "Not waived, $($need.Name) has legs that did not succeed: " + (($unpassed | ForEach-Object { "$($_.name) ($($_.conclusion))" }) -join ', ')
                    continue
                }
                $ids += $need.Name
                foreach ($hit in $hits) { $lines += "Waived: $($hit.name) $($hit.html_url)" }
            }
            if ($ids.Count -eq 0) { $lines += 'No failed Arm64 leg to waive.' }
        }
    }
} catch {
    $ids = @()
    $lines += "Nothing waived, failing closed: $($_.Exception.Message)"
}
ConvertTo-Json -InputObject @($ids) -Compress | Set-Content -LiteralPath $OutFile
if ($ids.Count -gt 0) {
    $lines += 'The waiver ends 2026-11-20 00:00 PT (08:00 UTC); the Arm64 jobs still run and stay red.'
    foreach ($line in $lines | Where-Object { $_ -like 'Waived:*' }) { "::warning title=quality-gate win-arm64 waiver::$line" }
}
$lines
if ($SummaryFile) { Add-Content -LiteralPath $SummaryFile -Value (@('### quality-gate: win-arm64 waiver (#2564)') + ($lines | ForEach-Object { "- $_" })) }
