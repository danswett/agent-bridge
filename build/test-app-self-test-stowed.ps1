# Wrapper contract only: no app, XAML, registry, event-log reads or live resize runs.
#Requires -Version 7
[CmdletBinding()]
param([switch]$Child, [string]$CaseDir, [int]$SimulatedExit = -1073741189)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Child) {
    $env:GITHUB_ACTIONS = 'false'
    $env:LOCALAPPDATA = Join-Path $CaseDir 'local'
    $global:simulatedAppExit = $SimulatedExit
    function global:Start-Process {
        param($FilePath, $ArgumentList, $RedirectStandardError, [switch]$PassThru)
        Set-Content -LiteralPath $RedirectStandardError -Value 'synthetic app stderr'
        $process = [pscustomobject]@{ Id = 4242; Handle = 1; ExitCode = $global:simulatedAppExit }
        $process | Add-Member ScriptMethod WaitForExit { param($Milliseconds) return $true }
        return $process
    }
    function global:Get-WinEvent {
        [CmdletBinding()]
        param($FilterHashtable)
        $event = [pscustomobject]@{
            ProviderName = 'Application Error'; Id = 1000; RecordId = 1
            TimeCreated = Get-Date; Message = 'fake.exe: synthetic native failure'
        }
        $event | Add-Member ScriptMethod ToXml {
            return '<Event><EventData><Data Name="ProcessId">4242</Data></EventData></Event>'
        }
        return $event
    }
    $appLogs = Join-Path $env:LOCALAPPDATA 'AgentBridge\app'
    New-Item -ItemType Directory -Path $appLogs -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $appLogs 'app.log') -Value 'synthetic app log'
    & (Join-Path $PSScriptRoot 'app-self-test.ps1') -Exe (Join-Path $CaseDir 'fake.exe') -Result (Join-Path $CaseDir 'result.json')
    exit $LASTEXITCODE
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('ab-stowed-contract-' + [guid]::NewGuid().ToString('N'))
$failures = [Collections.Generic.List[string]]::new()
$passed = 0
try {
    foreach ($case in @('stowed', 'native-only', 'unfinished-zero', 'stale-state')) {
        $dir = Join-Path $root $case
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $state = @{
            stage = 'live resize'; requestedTheme = 'Default'; actualTheme = 'Light'
            highContrast = $false; windowWidth = 1440; windowHeight = 1000
            layoutUpdates = 12; pendingLayout = 'not exposed by WinUI'
        }
        @{
            passed = @(); failed = @(); unfinished = 'live resize'
            resizeDiagnostics = @()
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dir 'result.json')
        @{ processId = $(if ($case -eq 'stale-state') { 4243 } else { 4242 }); states = @($state) } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dir 'result.resize-state.json')
        if ($case -eq 'stowed') {
            @{
                processId = 4242; stage = 'live resize'; hresult = '0x80004005'
                xamlMessage = 'synthetic XAML E_FAIL'
                exception = "System.Runtime.InteropServices.COMException: E_FAIL`n   at Synthetic.Measure()"
                lastState = $state
            } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dir 'result.xaml-failure.json')
        }
        $code = if ($case -eq 'unfinished-zero') { 0 } else { -1073741189 }
        $output = & (Get-Process -Id $PID).Path -NoProfile -File $PSCommandPath -Child -CaseDir $dir -SimulatedExit $code 2>&1
        $actualExit = $LASTEXITCODE
        try {
            $evidence = Get-Content -LiteralPath (Join-Path $dir 'app-exit-cause.json') -Raw | ConvertFrom-Json
            if ($actualExit -eq 0 -or -not $evidence.failed) { throw 'a crash or unfinished result must remain a failure' }
            if ($evidence.exitCode -ne $code) { throw 'the actual app exit code must be retained' }
            if ($null -eq $evidence.PSObject.Properties['selfTest']) { throw 'failure evidence lost the last self-test resize state' }
            if ($case -eq 'stale-state') {
                if ($evidence.selfTest.resizeDiagnostics.Count -ne 0 -or
                    -not ($evidence.captureErrors -match 'does not belong to the app process')) {
                    throw 'another process must not supply this crash state, and the evidence gap must be explicit'
                }
            }
            elseif ($evidence.selfTest.unfinished -ne 'live resize' -or
                $evidence.selfTest.resizeDiagnostics[0].windowWidth -ne 1440 -or
                $evidence.selfTest.resizeDiagnostics[0].pendingLayout -ne 'not exposed by WinUI') {
                throw 'failure evidence changed the stage, window size or pending-layout observation'
            }
            if ($case -eq 'stowed') {
                if ($evidence.xamlFailure.hresult -ne '0x80004005' -or
                    $evidence.xamlFailure.xamlMessage -ne 'synthetic XAML E_FAIL' -or
                    $evidence.xamlFailure.exception -notmatch 'Synthetic\.Measure') {
                    throw 'failure evidence lost the original XAML message, HRESULT or exception stack'
                }
            }
            elseif ($null -ne $evidence.xamlFailure) { throw 'a native-only failure must not invent a managed exception' }
            $passed++
            Write-Output "PASS $case"
        }
        catch {
            $failures.Add("$case`: $($_.Exception.Message)")
            Write-Output "FAIL $($failures[-1])"
            $output | Write-Verbose
        }
    }
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
Write-Output "Wrapper diagnostics: $passed passed, $($failures.Count) failed; synthetic contracts, not an app-crash reproduction."
if ($failures.Count -gt 0) { exit 1 }
