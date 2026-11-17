# Offline wrapper contract: intercept before process launch, log reads or WER configuration.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$wrapper = Join-Path $PSScriptRoot 'app-self-test.ps1'
$originalActions = $env:GITHUB_ACTIONS
$capture = [pscustomobject]@{ Arguments = $null }
function New-Item { }
function Start-Process {
    param($FilePath, $ArgumentList, $RedirectStandardError, [switch]$PassThru)
    $capture.Arguments = $ArgumentList
    throw 'intercepted launch'
}
$passed = 0
$failed = 0
try {
    $env:GITHUB_ACTIONS = 'false'
    foreach ($group in '', 'history', 'wheel', 'unknown') {
        $capture.Arguments = $null
        $parameters = @{ Exe = 'never-launched.exe'; Result = (Join-Path $PSScriptRoot 'path with spaces\result.json') }
        if ($group) { $parameters.Group = $group }
        $reason = $null
        try { & $wrapper @parameters }
        catch { $reason = $_.Exception.Message }
        $expected = @('--self-test', "`"$($parameters.Result)`"")
        if ($group -and $group -ne 'unknown') { $expected += '--self-test-group', $group }
        $ok = if ($group -eq 'unknown') {
            $null -eq $capture.Arguments -and $reason -like '*ValidateSet*'
        } else {
            $reason -eq 'intercepted launch' -and
                ($capture.Arguments -join '|') -ceq ($expected -join '|')
        }
        if ($ok) { $passed++; Write-Host "PASS: wrapper group '$group'" }
        else { $failed++; Write-Host "FAIL: wrapper group '$group': $reason; actual [$($capture.Arguments -join '|')]; expected [$($expected -join '|')]" }
    }
} finally {
    $env:GITHUB_ACTIONS = $originalActions
}
Write-Host "wrapper selector: $passed passed, $failed failed"
if ($failed) { throw 'Wrapper selector contract failed.' }
