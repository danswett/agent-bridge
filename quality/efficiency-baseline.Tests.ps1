#Requires -Version 7.4
param([ValidateSet('elapsed', 'exit', 'replacement', 'readonly', 'no-survivors', 'partial', 'ambiguous', 'metric-values', 'entrypoint', 'cpu-spin', 'cpu-rerun', 'cpu-control')][string] $Scenario)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'efficiency-baseline.ps1')
function Assert($Condition, [string] $Reason) { if (-not $Condition) { throw $Reason } }
if ($Scenario -like 'cpu-*') {
    function Samples([double[]] $Values) {
        foreach ($value in $Values) { @{ status = 'available'; cpuSecondsPerMinute = $value } }
    }
    $idle = @(Samples @(0.01, 0.02, 0.03))
    $borderline = @(Samples @(0.7, 0.8, 0.9))
    $options = @{ Samples = $borderline; ControlSamples = $idle; Budget = 0.3
        MarginFraction = 0.5; MarginFloor = 0.15; SpinMultiple = 20; BudgetsApproved = $false }
    switch ($Scenario) {
        'cpu-spin' {
            $options.RerunSamples = $borderline; $options.RerunControlSamples = $idle
            $result = Get-EfficiencyCpuVerdict @options
            Assert ($result.verdict -eq 'pass' -and $result.budgetComparison -eq 'over margin') 'Proposed budget caused a failure or lost its comparison.'
            $options.Samples = @(Samples @(6.02, 6.02, 6.02))
            Assert ((Get-EfficiencyCpuVerdict @options).verdict -eq 'pass') 'Exact spin ceiling failed.'
            $options.Samples = @(Samples @(6.03, 60, 6.04))
            $result = Get-EfficiencyCpuVerdict @options
            Assert ($result.verdict -eq 'fail' -and $result.attempts -eq 1) 'Spin did not fail immediately using the median less control.'
            $options.Budget = 0; $options.Samples = @(Samples @(3.03, 3.04, 3.05))
            Assert ((Get-EfficiencyCpuVerdict @options).verdict -eq 'fail') 'Zero budget lost the margin-floor spin ceiling.'
        }
        'cpu-rerun' {
            $options.BudgetsApproved = $true
            $result = Get-EfficiencyCpuVerdict @options
            Assert ($result.verdict -eq 'not measured' -and $result.rerunRequired -and $result.attempts -eq 1) 'Borderline did not request exactly one rerun.'
            $options.RerunSamples = $borderline; $options.RerunControlSamples = $idle
            $result = Get-EfficiencyCpuVerdict @options
            Assert ($result.verdict -eq 'fail' -and -not $result.rerunRequired -and $result.attempts -eq 2) 'Confirmed borderline did not fail after one rerun.'
            $options.RerunSamples = @(Samples @(0.46, 0.47, 0.48))
            $result = Get-EfficiencyCpuVerdict @options
            Assert ($result.verdict -eq 'pass' -and $result.budgetComparison -eq 'within margin') 'Rerun at the inclusive margin did not pass.'
            $options.Samples = @(Samples @(0.1, 0.2, 20))
            $result = Get-EfficiencyCpuVerdict @options
            Assert ($result.verdict -eq 'pass' -and $result.attempts -eq 1 -and $result.budgetComparison -eq 'within budget') 'Passing median unnecessarily used the rerun.'
        }
        'cpu-control' {
            $options.ControlSamples = @(Samples @(0.11, 0.12, 0.13))
            $result = Get-EfficiencyCpuVerdict @options
            Assert ($result.verdict -eq 'not measured' -and $result.rerunRequired) 'Noisy control looked measured.'
            $options.RerunSamples = $borderline; $options.RerunControlSamples = $options.ControlSamples
            $result = Get-EfficiencyCpuVerdict @options
            Assert ($result.verdict -eq 'not measured' -and -not $result.rerunRequired -and $result.attempts -eq 2 -and $result.reason -like '*control*') 'Persistent noise did not stop as not measured.'
            $options.RerunControlSamples = $idle
            Assert ((Get-EfficiencyCpuVerdict @options).verdict -eq 'pass') 'Clean rerun could not recover.'
            $options.QuietCheckPassed = $false; $options.RerunQuietCheckPassed = $false
            Assert ((Get-EfficiencyCpuVerdict @options).verdict -eq 'not measured') 'Failed quiet checks looked measured.'
            $options.QuietCheckPassed = $true; $options.RerunQuietCheckPassed = $true
            foreach ($bad in @(@{ status = 'partial'; cpuSecondsPerMinute = 0 }), @(@{ status = 'available'; cpuSecondsPerMinute = $null }), @(Samples @(0.1, [double]::NaN, 0.1))) {
                $options.RerunSamples = $bad
                Assert ((Get-EfficiencyCpuVerdict @options).verdict -eq 'not measured') 'Missing or invalid samples looked measured.'
            }
        }
    }
    Write-Output "PASS: $Scenario"
    return
}
function Test-ReadOnlyCommands($Ast) {
    return -not @($Ast.FindAll({ param($a) $a -is [Management.Automation.Language.CommandAst] }, $true) | Where-Object {
        $_.GetCommandName() -match '^(Stop-Process|taskkill|Restart-.*|Debug-Process|Enter-PSHostProcess|Invoke-Expression|iex)$' -or
        ($_.InvocationOperator -eq [Management.Automation.Language.TokenKind]::Ampersand -and $_.CommandElements[0] -isnot [Management.Automation.Language.StringConstantExpressionAst])
    }).Count
}
$state = @{ seconds = 0.0; index = 0 }
$fixture = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\fixtures\efficiency\process.json') -Raw | ConvertFrom-Json -AsHashtable
$options = @{
    DurationSeconds = 10; SampleIntervalSeconds = 3
    Targets = @{ host = @{ processId = 42; unavailable = $null }
        desktop = @{ processId = $null; unavailable = 'No desktop fixture.' }; pwa = @{ processId = $null; unavailable = 'No browser collector.' } }
    Elapsed = { $state.seconds }
    Wait = { param($seconds) $state.seconds += $seconds + 1 }
    ReadSnapshot = { param($processId) $fixture[$state.index++] }
    UtcNow = { [datetime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc).AddSeconds($state.seconds) }
}
switch ($Scenario) {
    'entrypoint' {
        $root = Join-Path ([IO.Path]::GetTempPath()) ("efficiency-" + [guid]::NewGuid().ToString('N')); $script = Join-Path $PSScriptRoot 'efficiency-baseline.ps1'
        $cli = @{ DurationSeconds = 0.01; SampleIntervalSeconds = 0.01; HostProcessId = 2147483647; DesktopProcessId = 2147483647 }
        try {
            $path = Join-Path $root 'nested\baseline.json'
            & $script @cli -OutputPath $path
            Assert (@(Get-ChildItem -LiteralPath $root -Recurse -File -Force).Count -eq 1) 'The entry point did not write exactly one file.'
            Assert (Test-Json -Json (Get-Content -LiteralPath $path -Raw) -SchemaFile (Join-Path $PSScriptRoot 'efficiency-baseline.schema.json')) 'The requested file is not baseline JSON.'
            $lock = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None); $cli.DurationSeconds = 30
            try {
                function Start-Sleep { throw 'Sampling started before OutputPath validation.' }
                foreach ($invalid in $root, $path) {
                    $rejected = $false
                    try { & $script @cli -OutputPath $invalid } catch { $failure = $_.Exception.GetBaseException(); $rejected = $failure -is [IO.IOException] -or $failure -is [UnauthorizedAccessException] }
                    Assert $rejected 'Directory or unwritable OutputPath was not rejected before sampling.'
                }
            } finally { $lock.Dispose(); Remove-Item -LiteralPath Function:\Start-Sleep }
        } finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }
    'exit' { $options.ReadSnapshot = { param($processId) @{ unavailable = @{ process = 'Process exited during observation.' } } } }
    'replacement' { $fixture[-1].identity = '42/reused' }
    'no-survivors' { foreach ($thread in $fixture[-1].threads) { $thread.identity += '/new' } }
    'partial' { $options.ReadSnapshot = { param($processId) $i = $state.index++; if ($i -eq 1) { @{ unavailable = @{ process = 'Process temporarily unreadable.' } } } else { $fixture[$i] } } }
    'ambiguous' {
        $find = { param($name) foreach ($id in 1, 2) { [pscustomobject]@{ Id = $id } | Add-Member ScriptMethod Dispose {} -PassThru } }
        $options.Targets.desktop = Resolve-EfficiencyProcess 0 'AgentBridge' -Find $find
        Assert ($options.Targets.desktop.unavailable -like '*-DesktopProcessId*') 'Missing explicit desktop ID hint.'
        Assert ((Resolve-EfficiencyProcess 0 'agent-bridge-host' -Find $find).unavailable -like '*-HostProcessId*') 'Missing explicit host ID hint.'
        Assert ((Resolve-EfficiencyProcess 0 'AgentBridge' -Find { throw [ComponentModel.Win32Exception]::new(5) }).unavailable -like '*-DesktopProcessId*') 'Unreadable discovery aborted or lost its ID hint.'
    }
    'readonly' {
        $tokens = $null; $errors = $null
        $script = Join-Path $PSScriptRoot 'efficiency-baseline.ps1'
        $ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
        Assert ($errors.Count -eq 0) 'Invalid PowerShell syntax.'
        Assert (Test-ReadOnlyCommands $ast) 'Unsafe command or dynamic call operator.'
        foreach ($example in 'Invoke-Expression "Get-Process"', '& $command') {
            $badAst = [Management.Automation.Language.Parser]::ParseInput($example, [ref]$tokens, [ref]$errors)
            Assert (-not (Test-ReadOnlyCommands $badAst)) 'The read-only guard accepted dynamic execution.'
        }
        Assert (-not ((Get-Content $script -Raw) -match '\.(Kill|Terminate|Suspend|Attach|Start)\(')) 'Mutating process API.'
    }
}
$result = Invoke-EfficiencyBaseline @options
$json = $result | ConvertTo-Json -Depth 20
Assert (Test-Json -Json $json -SchemaFile (Join-Path $PSScriptRoot 'efficiency-baseline.schema.json')) 'Invalid schema.'
if ($Scenario -eq 'metric-values') {
    $future = $json | ConvertFrom-Json -AsHashtable
    $future.targets.host.gcAllocationBytesPerSecond = 12.5
    $future.targets.host.network = @{ endpoints = @(@{ endpoint = 'relay'; bytesInPerMinute = 20; bytesOutPerMinute = 10; requestsPerMinute = 1 }) }
    Assert (Test-Json -Json ($future | ConvertTo-Json -Depth 20) -SchemaFile (Join-Path $PSScriptRoot 'efficiency-baseline.schema.json')) 'Version 1 rejects additive metric values.'
    $future.targets.host.network.endpoints[0].unexpected = 1
    Assert (-not (Test-Json -Json ($future | ConvertTo-Json -Depth 20) -SchemaFile (Join-Path $PSScriptRoot 'efficiency-baseline.schema.json') -ErrorAction SilentlyContinue)) 'Unknown endpoint field accepted.'
}
Assert ($result.endUtc -ge $result.startUtc) 'UTC interval reversed.'
foreach ($name in 'host', 'desktop', 'pwa') {
    foreach ($metric in 'gcAllocationBytesPerSecond', 'network') {
        Assert ($null -eq $result.targets[$name][$metric]) 'Unsupported metric invented a value.'
        Assert (-not [string]::IsNullOrWhiteSpace($result.targets[$name].unavailable[$metric])) 'Missing unavailable reason.'
    }
}
$hostResult = $result.targets.host
if ($Scenario -in 'exit', 'replacement', 'partial') {
    Assert ($null -eq $hostResult.cpuSecondsPerMinute) 'An exited or replaced process looked idle.'
    Assert (-not [string]::IsNullOrWhiteSpace($hostResult.unavailable.process)) 'Missing process failure reason.'
    if ($Scenario -eq 'partial') { Assert ($hostResult.status -eq 'partial' -and $hostResult.sampleCount -eq 3) 'Lost partial-read evidence.' }
} else {
    Assert ($result.sampleCount -eq 4 -and $result.elapsedSeconds -eq 11) 'Sampling counted ticks instead of elapsed time.'
    Assert ([math]::Abs($hostResult.cpuSecondsPerMinute - 180.0 / 11) -lt 0.000001) 'Wrong CPU rate.'
    Assert ($hostResult.workingSetBytes -eq 400 -and $hostResult.privateBytes -eq 800) 'Wrong memory values.'
    if ($Scenario -eq 'no-survivors') { Assert ($hostResult.topThreads.Count -eq 0) 'Invented a surviving thread.' }
    else { Assert ($hostResult.topThreads.Count -eq 1 -and $hostResult.topThreads[0].id -eq 8 -and $hostResult.topThreads[0].cpuSeconds -eq 2) 'Thread reuse or sorting error.' }
}
foreach ($name in 'desktop', 'pwa') { Assert ($result.targets[$name].status -eq 'unavailable') 'Missing target not reported.' }
if ($Scenario -eq 'ambiguous') { Assert ($result.targets.desktop.unavailable.process -like '*Multiple AgentBridge*-DesktopProcessId*') 'Ambiguity lost its target-specific reason.' }
Write-Output "PASS: $Scenario"
