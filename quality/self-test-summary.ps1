param([string]$Result, [string]$Summary = $env:GITHUB_STEP_SUMMARY)
$ErrorActionPreference = 'Stop'

function ConvertTo-SelfTestSummary($Data) {
    if ($null -eq $Data.timings -or $null -eq $Data.timings.totalMs) { return 'Self-test timings unavailable (no timed result).' }
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $seconds = ([double]$Data.timings.totalMs / 1000).ToString('F3', $culture)
    $lines = @("### Self-test timings", "", "Total self-test: $seconds s.", "",
        "Check (time since previous check, including waits) | Seconds", "--- | ---:")
    foreach ($check in @($Data.timings.checks | Sort-Object { [double]$_.ms } -Descending | Select-Object -First 10)) {
        $name = [Net.WebUtility]::HtmlEncode([string]$check.name).Replace('|', '&#124;') -replace '[\r\n]', ' '
        $seconds = ([double]$check.ms / 1000).ToString('F3', $culture)
        $lines += "<code>$name</code> | $seconds"
    }
    $lines -join "`n"
}

if ($MyInvocation.InvocationName -eq '.') { return }
try {
    $text = if (Test-Path -LiteralPath $Result -PathType Leaf) {
        ConvertTo-SelfTestSummary (Get-Content -LiteralPath $Result -Raw | ConvertFrom-Json)
    } else {
        Write-Warning 'Self-test timings unavailable: result.json was not produced.'
        'Self-test timings unavailable: result.json was not produced.'
    }
    Add-Content -LiteralPath $Summary -Value $text -Encoding utf8
} catch { Write-Warning "Self-test timing summary unavailable: $($_.Exception.Message)" }
exit 0
