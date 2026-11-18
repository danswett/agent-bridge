#Requires -Version 7.4
. (Join-Path $PSScriptRoot 'dotnet-test-command.ps1')

function Read-ProductTrx([string]$Directory) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter *.trx -File)
    if (-not $files.Count) { throw "No TRX evidence in $Directory." }
    foreach ($file in $files) {
        $xml = [xml](Get-Content -LiteralPath $file.FullName -Raw)
        $run = $xml.TestRun
        $summary = $run.ResultSummary
        $rows = @($run.Results.UnitTestResult | Where-Object { $_ })
        $c = $summary.Counters
        if ($summary.outcome -notin @('Completed', 'Passed', 'Failed') -or
            @($summary.RunInfos.RunInfo | Where-Object outcome -EQ 'Error').Count -or
            @($rows | Where-Object outcome -NotIn @('Passed', 'Failed', 'NotExecuted')).Count -or
            [int]$c.total -ne $rows.Count -or [int]$c.failed -ne @($rows | Where-Object outcome -EQ 'Failed').Count -or
            [int]$c.passed -ne @($rows | Where-Object outcome -EQ 'Passed').Count -or
            [int]$c.executed -ne ([int]$c.passed + [int]$c.failed) -or
            [int]$c.error -or [int]$c.aborted -or [int]$c.timeout) {
            throw "Incomplete or non-test failure in $($file.Name); refusing recovery."
        }
        foreach ($row in $rows) {
            $definitions = @($run.TestDefinitions.UnitTest | Where-Object id -EQ $row.testId)
            if ($definitions.Count -ne 1) { throw "Missing or ambiguous TRX definition for $($row.testName)." }
            $definition = $definitions[0]
            $method = $definition.TestMethod
            if (-not $method.className -or -not $method.name -or -not $row.testName -or -not $definition.storage) {
                throw 'TRX does not identify a test case and its assembly.'
            }
            [pscustomobject]@{
                Assembly = [IO.Path]::GetFileNameWithoutExtension($definition.storage)
                FullyQualifiedName = "$($method.className).$($method.name)"
                Name = [string]$row.testName
                Outcome = [string]$row.outcome
            }
        }
    }
}

function Invoke-ProductTestRetry {
    param(
        [Parameter(Mandatory)][string]$Target,
        [string]$Filter = '',
        [string]$ResultsDirectory = 'TestResults'
    )
    $ErrorActionPreference = 'Stop'
    # A nonzero test exit is evidence to classify, not a PowerShell terminating error.
    $PSNativeCommandUseErrorActionPreference = $false
    $projects = @{}
    $solution = $Target
    if ([IO.Path]::GetExtension($Target) -eq '.slnf') {
        $selection = (Get-Content -LiteralPath $Target -Raw | ConvertFrom-Json).solution
        $solution = Join-Path (Split-Path (Resolve-Path $Target)) $selection.path
        $paths = @($selection.projects)
    } else {
        $paths = @(([xml](Get-Content -LiteralPath $solution -Raw)).SelectNodes('//Project') | ForEach-Object { $_.Path })
    }
    foreach ($path in $paths) {
        $project = Join-Path (Split-Path (Resolve-Path $solution)) $path
        $xml = [xml](Get-Content -LiteralPath $project -Raw)
        if (-not $xml.SelectSingleNode('//PackageReference[@Include="Microsoft.NET.Test.Sdk"]')) { continue }
        $assembly = $xml.SelectSingleNode('//AssemblyName')
        $name = if ($assembly) { $assembly.InnerText } else { [IO.Path]::GetFileNameWithoutExtension($project) }
        $projects[$name] = $project
    }
    if (-not $projects.Count) { throw 'No expected test projects; refusing an unbounded retry.' }
    if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Retry requires a fresh results directory.' }
    $filterArgument = if ($Filter) { " --filter `"$Filter`"" } else { '' }
    $command = Add-DotnetCrashBlame "dotnet test `"$Target`" -c Release --no-build --nologo --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none --results-directory `"$ResultsDirectory`" --logger trx$filterArgument"
    dotnet tool run dotnet-coverage collect --nologo -s build/coverage.settings.xml -f cobertura -o "$ResultsDirectory/coverage.cobertura.xml" $command 2>&1 | Tee-Object -Variable firstOutput | Out-Host
    $firstExit = $LASTEXITCODE
    if ($firstExit -eq 0) { return 0 }
    if ($firstExit -ne 1) { throw "Test/coverage command failed with exit $firstExit; not a retryable test exit." }
    if (($firstOutput -join "`n") -match '(?im)^\s*(Error:|.*:\s*error(?:\s|:)|Test Run Aborted)') {
        throw 'Test/coverage command reported a non-test error; refusing recovery.'
    }
    $rows = @(Read-ProductTrx $ResultsDirectory)
    foreach ($name in $projects.Keys) {
        if ($name -notin $rows.Assembly) { throw "No complete TRX for $name; refusing recovery." }
    }
    if (@($rows | Where-Object { -not $projects.ContainsKey($_.Assembly) }).Count) { throw 'Unexpected test assembly in TRX.' }
    $failed = @($rows | Where-Object Outcome -EQ 'Failed')
    if (-not $failed.Count) { throw 'Command failed without failed test cases; refusing recovery.' }
    foreach ($test in $failed) {
        $same = @($rows | Where-Object {
            $_.Assembly -eq $test.Assembly -and $_.FullyQualifiedName -eq $test.FullyQualifiedName -and $_.Name -eq $test.Name
        })
        if ($same.Count -ne 1) { throw 'A failed case has an ambiguous filter identity; refusing a broader retry.' }
    }
    if (-not $env:GITHUB_STEP_SUMMARY) { throw 'GITHUB_STEP_SUMMARY is required to record retries.' }
    @(
        '### Windows product: failed tests retried once'
        'Flake ledger: https://github.com/danswett/agent-bridge/issues/3191'
    ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    $retryExit = 0
    foreach ($group in $failed | Group-Object Assembly) {
        $group.Group | ForEach-Object { '- <code>' + [Net.WebUtility]::HtmlEncode("$($_.Assembly): $($_.Name)") + '</code>' } |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
        # DisplayName keeps passed data rows of the same theory out of the retry.
        $clauses = foreach ($test in $group.Group) {
            $fqn = $test.FullyQualifiedName -replace '([\\()&|=!~])', '\$1'
            $display = $test.Name -replace '([\\()&|=!~])', '\$1'
            "(FullyQualifiedName=$fqn&DisplayName=$display)"
        }
        $retryDirectory = Join-Path $ResultsDirectory "retry-$($group.Name)"
        dotnet test $projects[$group.Name] -c Release --no-build --nologo --blame-crash --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none --results-directory $retryDirectory --logger trx --filter ($clauses -join '|') | Out-Host
        $code = $LASTEXITCODE
        if ($code -ne 0) { $retryExit = 1; continue }
        $retried = @(Read-ProductTrx $retryDirectory)
        if ($retried.Count -ne $group.Count) { $retryExit = 1 }
        foreach ($test in $group.Group) {
            $matches = @($retried | Where-Object {
                $_.Assembly -eq $test.Assembly -and $_.FullyQualifiedName -ceq $test.FullyQualifiedName -and $_.Name -ceq $test.Name
            })
            if ($matches.Count -ne 1 -or $matches[0].Outcome -ne 'Passed') { $retryExit = 1 }
        }
        if (@($retried | Where-Object Outcome -NE 'Passed').Count) { $retryExit = 1 }
    }
    $outcome = if ($retryExit) { '**Retry failed; job remains failed.**' } else { '**Retry passed; original failed TRX retained.**' }
    $outcome | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    return $retryExit
}
