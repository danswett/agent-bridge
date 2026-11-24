# Asserts against the actual installed app's wrapper evidence, never a fixture or a rerun.
#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Evidence)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Evidence -PathType Leaf)) {
    throw 'Diagnostic capture did not produce evidence; this is not behavioral RED.'
}
$observed = Get-Content -LiteralPath $Evidence -Raw | ConvertFrom-Json
if (-not $observed.failed) {
    Write-Output 'The installed self-test passed; no failing-app RED was observed.'
    exit 0
}
# A crash leaves stderr and a Windows event. A self-test that failed a check and exited normally with
# code 1 leaves neither (#3743's main run, job 137418151743): its cause is the failed checks the app
# reported in result.json, which app-self-test.ps1 copies into the evidence.
$crash = $null -ne $observed.exitCode -and
    -not [string]::IsNullOrWhiteSpace($observed.stderr) -and
    @($observed.events | Where-Object {
        $_.processId -eq $observed.processId -and
        -not [string]::IsNullOrWhiteSpace($_.message)
    }).Count -gt 0
$failedChecks = @($observed.PSObject.Properties['failedChecks'] ? $observed.failedChecks : @())
$checks = $observed.exitCode -eq 1 -and $failedChecks.Count -gt 0
$complete = ($crash -or $checks) -and @($observed.captureErrors).Count -eq 0
if (-not $complete) {
    throw 'ASSERTION FAILED: the failed installed self-test lacks app stderr, actual exit code, or app-process event-log cause; inspect app-exit-cause.json and app.stderr.log. Capture failures are evidence gaps, not a diagnosed cause.'
}
Write-Output $(if ($checks -and -not $crash) { "The installed self-test exited normally with $($failedChecks.Count) failed check(s): $($failedChecks -join ' | '); cause still requires review." }
    else { 'The failed installed self-test has stderr, exit code and app-process event evidence; cause still requires review.' })
