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
$complete = $null -ne $observed.exitCode -and
    -not [string]::IsNullOrWhiteSpace($observed.stderr) -and
    @($observed.events | Where-Object {
        $_.processId -eq $observed.processId -and
        -not [string]::IsNullOrWhiteSpace($_.message)
    }).Count -gt 0 -and @($observed.captureErrors).Count -eq 0
if (-not $complete) {
    throw 'ASSERTION FAILED: the failed installed self-test lacks app stderr, actual exit code, or app-process event-log cause; inspect app-exit-cause.json and app.stderr.log. Capture failures are evidence gaps, not a diagnosed cause.'
}
Write-Output 'The failed installed self-test has stderr, exit code and app-process event evidence; cause still requires review.'
