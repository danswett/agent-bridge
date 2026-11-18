param([Parameter(Mandatory)][string]$Result, [string]$Summary = $env:GITHUB_STEP_SUMMARY)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Result)) { Write-Warning 'Self-test result unavailable; no timing summary.'; return }
$data = Get-Content -LiteralPath $Result -Raw | ConvertFrom-Json
if ($null -eq $data.durationMs -or $null -eq $data.checkDurations) {
    Write-Warning 'Self-test timings unavailable in this result.'; return
}
$text = @(
    '### Self-test timings'
    ('Total self-test: {0} s. Check intervals include work/waits since the preceding check.' -f
        ($data.durationMs / 1000).ToString('F3', [Globalization.CultureInfo]::InvariantCulture))
    ''
    '| Check | Duration (ms) |'
    '|---|---:|'
    foreach ($check in ($data.checkDurations | Sort-Object durationMs -Descending | Select-Object -First 10)) {
        $name = [Net.WebUtility]::HtmlEncode([string]$check.name).Replace('|', '&#124;').Replace('`', '&#96;') -replace '[\r\n]', ' '
        '| {0} | {1} |' -f $name, ([double]$check.durationMs).ToString('F3', [Globalization.CultureInfo]::InvariantCulture)
    }
)
$text | Add-Content -LiteralPath $Summary -Encoding utf8
