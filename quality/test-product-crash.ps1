#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workflow = Get-Content -Raw (Join-Path $root '.github' 'workflows' 'ci.yml')
function Require([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }
$step = [regex]::Match($workflow, '(?ms)^      - name: dotnet test, with coverage and per-test times\r?\n.*?^        run: \|\r?\n(.*?)^        timeout-minutes:')
Require $step.Success 'The product coverage test step must exist.'
$target = $env:DOTNET_TARGET
$filter = $env:TEST_FILTER
function dotnet { $script:collected = @($args) }
Push-Location $root
try {
    $env:DOTNET_TARGET = 'selected.slnf'
    $env:TEST_FILTER = 'Category!=Process&Category!=Network'
    & ([scriptblock]::Create(($step.Groups[1].Value -replace '(?m)^          ', '')))
    $command = $script:collected[-1]
    Require ($command -match '(?:^|\s)--blame-crash(?:\s|$)') 'Product crashes must record the active test with --blame-crash.'
    Require ($command -match '--blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none') 'The existing hang watchdog must stay unchanged, with hang dumps off.'
    Require ($command.Contains('--filter "Category!=Process&Category!=Network"')) 'Crash diagnostics must preserve the test filter.'
    Require ($command.Contains('dotnet test selected.slnf -c Release --no-build')) 'Crash diagnostics must preserve the selected target and build mode.'
    $env:DOTNET_TARGET = 'AgentBridge.slnx'
    $env:TEST_FILTER = ''
    & ([scriptblock]::Create(($step.Groups[1].Value -replace '(?m)^          ', '')))
    Require ($script:collected[-1] -match '^dotnet test AgentBridge.slnx .* --blame-crash$') 'The unfiltered whole solution must also record crashes.'
    Require (-not $script:collected[-1].Contains('--filter')) 'Whole-solution runs must not gain a test filter.'
    Require ($workflow.Contains('public-hang-sequences/sequence-*.xml')) 'Public diagnostics must remain execution sequences only.'
    . (Join-Path $PSScriptRoot 'dotnet-test-command.ps1')
    foreach ($inputCommand in 'dotnet test tests/Example -c Release', 'dotnet test selected.slnf --blame-crash --filter "Category!=Process"') {
        $actual = Add-DotnetCrashBlame $inputCommand
        Require ($actual.StartsWith($inputCommand)) 'Existing test arguments must remain unchanged.'
        Require ([regex]::Matches($actual, '(?:^|\s)--blame-crash(?:\s|$)').Count -eq 1) 'Crash blame must be added exactly once.'
    }
    Require ((Add-DotnetCrashBlame 'dotnet build Example') -eq 'dotnet build Example') 'Non-test commands must stay unchanged.'
    $runner = Get-Content -Raw (Join-Path $PSScriptRoot 'run.ps1')
    Require ($runner.Contains('$command = Add-DotnetCrashBlame $s.runCommand')) 'The local quality runner must use the shared crash command builder.'
    Write-Host 'PASS: product and local quality crash diagnostics preserve test selection and hang settings.'
}
finally {
    $env:DOTNET_TARGET = $target
    $env:TEST_FILTER = $filter
    Pop-Location
}
