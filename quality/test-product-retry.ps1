#Requires -Version 7.4
# Offline: dotnet is a stand-in; no product tests, network or live services run.
$ErrorActionPreference = 'Stop'
$script:failures = 0
function Assert([bool]$Ok, [string]$Name) {
    if ($Ok) { Write-Host "PASS $Name" } else { Write-Host "FAIL $Name"; $script:failures++ }
}
$helper = Join-Path $PSScriptRoot 'product-test-retry.ps1'
if (-not (Test-Path $helper)) { throw 'Windows product failed-test retry helper is missing.' }
. $helper
$scratch = Join-Path ([IO.Path]::GetTempPath()) "ab-product-retry-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $scratch | Out-Null
$originalSummary = $env:GITHUB_STEP_SUMMARY
$script:cases = @(
    @{ Id = 'one'; Class = 'Example.Tests'; Method = 'Theory'; Name = 'Example.Tests.Theory(value: "a|b&(c)")' }
    @{ Id = 'two'; Class = 'Example.Tests'; Method = 'Theory'; Name = 'Example.Tests.Theory(value: "passed")' }
)
function Write-Trx([string]$Directory, [string]$Assembly, [string[]]$Outcomes, [string]$State = 'Failed') {
    New-Item -ItemType Directory -Force $Directory | Out-Null
    $results = for ($i = 0; $i -lt $Outcomes.Count; $i++) {
        $c = $script:cases[$i]
        $name = [Security.SecurityElement]::Escape($c.Name)
        "<UnitTestResult testId='$($c.Id)' testName='$name' outcome='$($Outcomes[$i])' />"
    }
    $definitions = foreach ($c in $script:cases) {
        "<UnitTest id='$($c.Id)' storage='$Assembly.dll'><TestMethod className='$($c.Class)' name='$($c.Method)' /></UnitTest>"
    }
    $failed = @($Outcomes | Where-Object { $_ -eq 'Failed' }).Count
    $passed = @($Outcomes | Where-Object { $_ -eq 'Passed' }).Count
    @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results>$($results -join '')</Results><TestDefinitions>$($definitions -join '')</TestDefinitions>
<ResultSummary outcome="$State"><Counters total="$($Outcomes.Count)" executed="$($Outcomes.Count)" passed="$passed" failed="$failed" error="0" aborted="0" timeout="0" /></ResultSummary>
</TestRun>
"@ | Set-Content (Join-Path $Directory "$Assembly.trx")
}
function dotnet {
    $script:calls.Add(@($args))
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'tool') {
        if ($script:scenario -ne 'no-trx') {
            Write-Trx $script:first 'Example.Tests' @('Failed', 'Passed')
            if ($script:scenario -ne 'missing-project') {
                $otherOutcome = if ($script:scenario -eq 'multiple') { 'Failed' } else { 'Passed' }
                Write-Trx $script:first 'Other.Tests' @($otherOutcome) 'Completed'
            }
        }
        switch ($script:scenario) {
            'green' { $global:LASTEXITCODE = 0; return }
            'bad-exit' { $global:LASTEXITCODE = 2; return }
            'no-failed-tests' { Write-Trx $script:first 'Example.Tests' @('Passed', 'Passed') 'Completed' }
            'ambiguous-case' {
                $path = Join-Path $script:first 'Example.Tests.trx'
                $old = [Security.SecurityElement]::Escape($script:cases[1].Name)
                $new = [Security.SecurityElement]::Escape($script:cases[0].Name)
                (Get-Content $path -Raw).Replace($old, $new) | Set-Content $path
            }
            'coverage-error' { Write-Output 'Error: Failed to collect coverage.' }
            'malformed' { Set-Content (Join-Path $script:first 'Other.Tests.trx') '<broken>' }
            'aborted' { Write-Trx $script:first 'Other.Tests' @('Passed') 'Aborted' }
            'run-error' {
                $path = Join-Path $script:first 'Other.Tests.trx'
                (Get-Content $path -Raw).Replace('</ResultSummary>', '<RunInfos><RunInfo outcome="Error" /></RunInfos></ResultSummary>') | Set-Content $path
            }
        }
        $global:LASTEXITCODE = 1
        return
    }
    $directory = $args[[array]::IndexOf($args, '--results-directory') + 1]
    if ($script:scenario -eq 'retry-no-trx') { return }
    $outcome = if ($script:scenario -eq 'twice-red') { 'Failed' } elseif ($script:scenario -eq 'retry-skipped') { 'NotExecuted' } else { 'Passed' }
    $assembly = [IO.Path]::GetFileNameWithoutExtension($args[1])
    Write-Trx $directory $assembly @($outcome) 'Completed'
    if ($script:scenario -eq 'broadened-retry') { Write-Trx $directory $assembly @('Passed', 'Passed') 'Completed' }
    if ($script:scenario -eq 'wrong-case') {
        $path = Join-Path $directory 'Example.Tests.trx'
        $old = [Security.SecurityElement]::Escape($script:cases[0].Name)
        $new = [Security.SecurityElement]::Escape($script:cases[1].Name)
        (Get-Content $path -Raw).Replace($old, $new) | Set-Content $path
    }
    if ($script:scenario -in @('twice-red', 'retry-crash')) { $global:LASTEXITCODE = 1 }
}
function Run-Case([string]$Name, [string]$Target = 'selected.slnf') {
    $script:scenario = $Name
    $script:calls = [Collections.Generic.List[object]]::new()
    $script:first = Join-Path $scratch "$Name-results"
    $env:GITHUB_STEP_SUMMARY = Join-Path $scratch "$Name-summary.md"
    $message = ''
    try { $code = Invoke-ProductTestRetry -Target $Target -ResultsDirectory $script:first -Filter 'Category!=Process' }
    catch { $code = 1; $message = $_.Exception.Message }
    [pscustomobject]@{
        Code = $code; Calls = $script:calls.ToArray(); Message = $message
        Summary = if (Test-Path $env:GITHUB_STEP_SUMMARY) { Get-Content $env:GITHUB_STEP_SUMMARY -Raw } else { '' }
    }
}
Push-Location $scratch
try {
    foreach ($project in 'Example.Tests', 'Other.Tests') {
        "<Project><ItemGroup><PackageReference Include='Microsoft.NET.Test.Sdk' /></ItemGroup></Project>" | Set-Content "$project.csproj"
    }
    '<Solution><Project Path="Example.Tests.csproj" /><Project Path="Other.Tests.csproj" /></Solution>' | Set-Content 'all.slnx'
    '{"solution":{"path":"all.slnx","projects":["Example.Tests.csproj","Other.Tests.csproj"]}}' | Set-Content 'selected.slnf'
    $r = Run-Case 'green'
    Assert ($r.Code -eq 0 -and $r.Calls.Count -eq 1 -and -not $r.Summary) 'a successful first attempt never retries'
    $r = Run-Case 'recovered'
    Assert ($r.Code -eq 0 -and $r.Calls.Count -eq 2) 'one failed case gets exactly one retry'
    $firstCommand = $r.Calls[0][-1]
    Assert ($firstCommand.Contains('--filter "Category!=Process"') -and $firstCommand.Contains('--blame-crash')) 'coverage preserves selection and crash diagnostics'
    $retry = $r.Calls[1]
    $filter = $retry[[array]::IndexOf($retry, '--filter') + 1]
    Assert ([IO.Path]::GetFileName($retry[1]) -eq 'Example.Tests.csproj' -and '--no-build' -in $retry) 'only the failed project runs again without a build'
    Assert ($filter.Contains('FullyQualifiedName=Example.Tests.Theory&DisplayName=Example.Tests.Theory\(value: "a\|b\&\(c\)"\)')) 'the filter selects the failed theory case and escapes filter metacharacters'
    Assert (-not $filter.Contains('passed')) 'a passed case of the same theory is not selected'
    Assert ($r.Summary.Contains([Net.WebUtility]::HtmlEncode($script:cases[0].Name)) -and $r.Summary.Contains('https://github.com/danswett/agent-bridge/issues/3191')) 'the summary names every retry and links its flake ledger'
    Assert (Test-Path (Join-Path $script:first 'Example.Tests.trx')) 'original failure evidence is retained'
    $r = Run-Case 'solution' 'all.slnx'
    Assert ($r.Code -eq 0 -and $r.Calls.Count -eq 2) 'whole-solution and selected-solution targets both retry'
    $r = Run-Case 'multiple'
    Assert ($r.Code -eq 0 -and $r.Calls.Count -eq 3 -and $r.Summary.Contains('Other.Tests:')) 'failures in two projects each get one serial retry and a ledger entry'
    foreach ($name in 'no-trx', 'bad-exit', 'no-failed-tests', 'ambiguous-case', 'coverage-error', 'malformed', 'aborted', 'run-error', 'missing-project') {
        $r = Run-Case $name
        Assert ($r.Code -ne 0 -and $r.Calls.Count -eq 1) "$name fails closed without a retry"
    }
    foreach ($name in 'twice-red', 'retry-no-trx', 'retry-crash', 'retry-skipped', 'wrong-case', 'broadened-retry') {
        $r = Run-Case $name
        Assert ($r.Code -ne 0 -and $r.Calls.Count -eq 2) "$name remains red without a third attempt"
    }
    $workflow = Get-Content -Raw (Join-Path $PSScriptRoot '..' '.github' 'workflows' 'ci.yml')
    Assert ($workflow.Contains("if: env.DOTNET_TARGET != '' && matrix.os == 'windows-latest'")) 'only windows-latest enters the retry step'
    Assert ($workflow.Contains("if: env.DOTNET_TARGET != '' && matrix.os != 'windows-latest'")) 'other product matrix legs retain the original coverage step'
    Assert ($workflow.Contains('exit (Invoke-ProductTestRetry -Target $env:DOTNET_TARGET -Filter $env:TEST_FILTER)')) 'the Windows step propagates the final retry exit'
}
finally {
    Pop-Location
    $env:GITHUB_STEP_SUMMARY = $originalSummary
    Remove-Item -LiteralPath $scratch -Recurse
}
if ($script:failures) { throw "$script:failures retry assertions failed." }
