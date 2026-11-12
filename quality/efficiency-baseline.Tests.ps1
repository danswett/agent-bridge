#Requires -Version 7.4
param([ValidateSet('elapsed', 'exit', 'replacement', 'readonly', 'no-survivors', 'partial')][string] $Scenario)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'efficiency-baseline.ps1')
function Assert($Condition, [string] $Reason) { if (-not $Condition) { throw $Reason } }
$state = @{ seconds = 0.0; index = 0 }
$fixture = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\fixtures\efficiency\process.json') -Raw | ConvertFrom-Json -AsHashtable
$options = @{
    DurationSeconds = 10; SampleIntervalSeconds = 3
    Targets = @{ host = 42; desktop = $null; pwa = $null }
    Elapsed = { $state.seconds }
    Wait = { param($seconds) $state.seconds += $seconds + 1 }
    ReadSnapshot = { param($processId) $fixture[$state.index++] }
    UtcNow = { [datetime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc).AddSeconds($state.seconds) }
}
switch ($Scenario) {
    'exit' { $options.ReadSnapshot = { param($processId) @{ unavailable = @{ process = 'Process exited during observation.' } } } }
    'replacement' { $fixture[-1].identity = '42/reused' }
    'no-survivors' { foreach ($thread in $fixture[-1].threads) { $thread.identity += '/new' } }
    'partial' { $options.ReadSnapshot = { param($processId) $i = $state.index++; if ($i -eq 1) { @{ unavailable = @{ process = 'Process temporarily unreadable.' } } } else { $fixture[$i] } } }
    'readonly' {
        $tokens = $null; $errors = $null
        $script = Join-Path $PSScriptRoot 'efficiency-baseline.ps1'
        $ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
        Assert ($errors.Count -eq 0) 'Invalid PowerShell syntax.'
        $commands = $ast.FindAll({ param($a) $a -is [Management.Automation.Language.CommandAst] }, $true)
        Assert (-not ($commands.GetCommandName() -match '^(Stop-Process|taskkill|Restart-.*|Debug-Process|Enter-PSHostProcess)$')) 'Mutating process command.'
        Assert (-not ((Get-Content $script -Raw) -match '\.(Kill|Terminate|Suspend|Attach|Start)\(')) 'Mutating process API.'
    }
}
$result = Invoke-EfficiencyBaseline @options
$json = $result | ConvertTo-Json -Depth 20
Assert (Test-Json -Json $json -SchemaFile (Join-Path $PSScriptRoot 'efficiency-baseline.schema.json')) 'Invalid schema.'
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
Write-Output "PASS: $Scenario"
