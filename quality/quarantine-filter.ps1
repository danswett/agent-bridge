[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Discovery', 'Gate', 'Report')][string]$Mode,
    [string]$Manifest,
    [string]$Evidence,
    [string]$Name,
    [int]$TestExit,
    [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$approved = 'AnswerFlowTests.A_form_stopped_after_every_field_is_resumed_at_most_three_times_and_then_left_with_no_Esc'
$approvedExpiry = '2026-11-16T07:59:00Z'

switch ($Mode) {
    'Plan' {
        $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText((Resolve-Path -LiteralPath $Manifest)))
        try {
            $root = $document.RootElement
            if (@($root.EnumerateObject()).Count -ne 1 -or $root.GetProperty('tests').GetArrayLength() -ne 1) {
                throw 'Expected exactly one named quarantine row.'
            }
            $row = $root.GetProperty('tests')[0]
            if (@($row.EnumerateObject()).Count -ne 4 -or
                $row.GetProperty('fullyQualifiedName').GetString() -cne $approved -or
                $row.GetProperty('issue').GetInt32() -ne 2670 -or
                $row.GetProperty('owner').GetString() -cne 'Engineering Lead' -or
                $row.GetProperty('expires').GetString() -cne $approvedExpiry) {
                throw 'Quarantine row does not match the authorized test.'
            }
            $expires = [DateTimeOffset]::ParseExact($row.GetProperty('expires').GetString(), "yyyy-MM-dd'T'HH:mm:ss'Z'",
                [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
            [pscustomobject]@{ name = $approved; active = $Now -lt $expires
                gateFilter = "FullyQualifiedName!=$approved"; reportFilter = "FullyQualifiedName=$approved" } | ConvertTo-Json -Compress
        } finally { $document.Dispose() }
    }
    'Discovery' {
        if ($Name -cne $approved) { throw 'Unexpected quarantine test name.' }
        $matches = @(Get-Content -LiteralPath $Evidence | Where-Object { $_.Trim() -ceq $Name })
        if ($matches.Count -ne 1) { throw "Expected one discovered quarantine test, found $($matches.Count)." }
    }
    'Gate' {
        if (-not (Test-Path -LiteralPath $Evidence -PathType Container)) { throw 'Gate test results are missing.' }
        $files = @(Get-ChildItem -LiteralPath $Evidence -Filter '*.trx' -File -Recurse)
        if ($files.Count -eq 0) { throw 'Gate test results are missing.' }
        $hostExecuted = 0
        foreach ($file in $files) {
            try { $report = [xml](Get-Content -LiteralPath $file.FullName -Raw) }
            catch { throw "Gate test result is invalid: $($file.Name)." }
            $counters = $report.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
            $count = 0
            if ($null -eq $counters -or -not [int]::TryParse($counters.GetAttribute('executed'), [ref]$count) -or $count -lt 0) {
                throw "Gate test result has invalid counters: $($file.Name)."
            }
            if ($count -eq 0) { continue }
            $hostIds = @{}
            foreach ($definition in $report.SelectNodes("//*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")) {
                $storage = $definition.GetAttribute('storage') -replace '\\', '/'
                $id = $definition.GetAttribute('id')
                if ($id -and ($storage -split '/')[-1] -ieq 'AgentBridge.Host.Tests.dll') {
                    $hostIds[$id] = $true
                }
            }
            foreach ($result in $report.SelectNodes("//*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
                if ($hostIds.ContainsKey($result.GetAttribute('testId')) -and $result.GetAttribute('outcome') -in @('Passed', 'Failed')) {
                    $hostExecuted++
                }
            }
        }
        if ($hostExecuted -eq 0) { throw 'Gate has no executed Host.Tests results.' }
    }
    'Report' {
        if ($Name -cne $approved) { throw 'Unexpected quarantine test name.' }
        if (-not (Test-Path -LiteralPath $Evidence -PathType Leaf)) { throw 'Quarantine report is missing.' }
        try { $report = [xml](Get-Content -LiteralPath $Evidence -Raw) }
        catch { throw "Quarantine report is invalid: $($_.Exception.Message)" }
        $results = @($report.SelectNodes("//*[local-name()='UnitTestResult']"))
        $summary = $report.SelectSingleNode("//*[local-name()='ResultSummary']")
        $counters = $report.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
        $errors = @($report.SelectNodes("//*[local-name()='RunInfo' and (@outcome='Error' or @outcome='Failed')]"))
        if ($results.Count -ne 1 -or $null -eq $summary -or $null -eq $counters -or $errors.Count -ne 0 -or
            $results[0].GetAttribute('testName') -cne $Name -or $counters.GetAttribute('total') -ne '1' -or
            $counters.GetAttribute('executed') -ne '1') { throw 'Quarantine report is incomplete or mismatched.' }
        $outcome = $results[0].GetAttribute('outcome')
        if ($outcome -eq 'Passed' -and $TestExit -eq 0 -and $counters.GetAttribute('passed') -eq '1' -and
            $counters.GetAttribute('failed') -eq '0' -and $summary.GetAttribute('outcome') -eq 'Completed') {
            Write-Output 'The quarantined test passed.'
        } elseif ($outcome -eq 'Failed' -and $TestExit -eq 1 -and $counters.GetAttribute('failed') -eq '1' -and
            $counters.GetAttribute('passed') -eq '0' -and $summary.GetAttribute('outcome') -eq 'Failed') {
            Write-Warning 'The quarantined test failed; its console output and TRX remain available.'
        } else { throw "Quarantine report outcome and test exit disagree: $outcome / $TestExit." }
    }
}
