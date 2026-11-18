#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
function Invoke-TesthostStartup([string]$Target, [string]$Filter, [string]$Results = 'TestResults') {
    $PSNativeCommandUseErrorActionPreference = $false
    $Results = Join-Path $Results ([guid]::NewGuid().ToString('N'))
    $testArgs = @('-c', 'Release', '--no-build', '--nologo', '-m:2', '--blame-crash', '--blame-hang', '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'none', '--logger', 'trx')
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    dotnet test $Target @testArgs --results-directory $Results 2>&1 | Tee-Object -Variable log | Out-Host
    $code = $LASTEXITCODE
    if ($code -eq 0) { return 0 }
    if (-not (Test-Path $Results)) { return $code }
    $runs = @(Get-ChildItem $Results -Recurse -Filter *.trx | ForEach-Object { [xml](Get-Content -LiteralPath $_.FullName -Raw) })
    $retry = @()
    foreach ($run in $runs) {
        $summary = $run.TestRun.ResultSummary
        if ($summary.Counters.failed -ne '0') { return $code }
        if ($summary.outcome -eq 'Completed') { continue }
        if ($summary.Counters.executed -ne '0' -or $summary.InnerText -notmatch "Testhost process for source\(s\) '([^']+)[\\/]bin[\\/][^']+\.dll' exited with error:") { return $code }
        $retry += $Matches[1]
    }
    if (-not $retry.Count -or ($log -join "`n") -match '(?im)\berror (?:[A-Z]+\d+\s*)?:') { return $code }
    foreach ($project in $retry | Select-Object -Unique) {
        Write-Information "::warning::Testhost startup aborted with zero executed/failed tests: $project. Retrying this assembly ONCE (1/1); original TRX retained." -InformationAction Continue
        dotnet test $project @testArgs --results-directory (Join-Path $Results 'startup-retry') | Out-Host
        if ($LASTEXITCODE -ne 0) { return $LASTEXITCODE }
    }
    return 0
}
if ($MyInvocation.InvocationName -ne '.') { exit (Invoke-TesthostStartup -Target $env:DOTNET_TARGET -Filter $env:TEST_FILTER) }
