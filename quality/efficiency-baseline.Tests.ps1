#Requires -Version 7.4
param([ValidateSet('elapsed', 'exit', 'replacement', 'readonly', 'no-survivors', 'partial', 'ambiguous', 'metric-values', 'entrypoint',
    'memory-growth', 'memory-cache', 'memory-control', 'memory-spin', 'memory-steady', 'memory-boundary',
    'memory-missing', 'memory-replacement', 'memory-retry', 'memory-invalid')][string] $Scenario)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'efficiency-baseline.ps1')
function Assert($Condition, [string] $Reason) { if (-not $Condition) { throw $Reason } }
if ($Scenario -like 'memory-*') {
    function New-MemoryRun([double] $FirstMb = 100, [double] $LastMb = 104) {
        return @{
            processIdentity = '42/start'; controlIdentity = '43/start'
            samples = @(foreach ($i in 0..9) {
                $mb = if ($i -lt 3) { $FirstMb } else { $LastMb }
                @{ privateBytes = $mb * 1MB; workingSetBytes = $mb * 1MB; controlPrivateBytes = 10MB }
            })
        }
    }
    $args = @{
        Run = New-MemoryRun; Rerun = New-MemoryRun 104 108
        GrowthCeilingBytes = 3MB; SteadyPrivateCeilingBytes = 200MB
        SteadyWorkingSetCeilingBytes = 200MB; Margin = 0.25
    }
    switch ($Scenario) {
        'memory-growth' {
            # Outliers must not replace the first/last-three medians.
            $args.Run.samples[0].privateBytes = 180MB
            $args.Run.samples[9].privateBytes = 20MB
            $r = Get-EfficiencyMemoryVerdict @args
            Assert ($r.verdict -eq 'fail' -and $r.reason -eq 'growth' -and $r.growthBytes -eq 4MB -and $r.rerunUsed) 'Persistent median growth did not fail.'
        }
        'memory-cache' {
            $args.Rerun = New-MemoryRun 104 104
            $r = Get-EfficiencyMemoryVerdict @args
            Assert ($r.verdict -eq 'pass' -and $r.growthBytes -eq 0 -and $r.rerunUsed) 'One-time cache did not pass.'
        }
        'memory-control' {
            foreach ($phase in $args.Run, $args.Rerun) { $phase.samples[4].controlPrivateBytes = 12MB }
            $r = Get-EfficiencyMemoryVerdict @args
            Assert ($r.verdict -eq 'not measured' -and $r.reason -eq 'control' -and $r.rerunUsed) 'Moving control was accepted.'
        }
        'memory-spin' {
            $args.Run = New-MemoryRun 100 131
            $args.Rerun = New-MemoryRun 131 131
            $r = Get-EfficiencyMemoryVerdict @args
            Assert ($r.verdict -eq 'fail' -and $r.reason -eq 'growth' -and -not $r.rerunUsed) 'Extreme growth used a rerun.'
        }
        'memory-steady' {
            foreach ($metric in 'privateBytes', 'workingSetBytes') {
                $args.Run = New-MemoryRun 100 100; $args.Rerun = New-MemoryRun 100 100
                foreach ($phase in $args.Run, $args.Rerun) {
                    foreach ($sample in $phase.samples) { $sample[$metric] = 251MB }
                }
                $r = Get-EfficiencyMemoryVerdict @args
                Assert ($r.verdict -eq 'fail' -and $r.reason -eq 'steady') "Steady $metric above margin passed."
                $args.Rerun = New-MemoryRun 100 100
                Assert ((Get-EfficiencyMemoryVerdict @args).verdict -eq 'pass') 'Transient steady excess failed.'
            }
        }
        'memory-boundary' {
            $args.Run = New-MemoryRun 247 250; $args.Remove('Rerun')
            $args.Run.samples[9].controlPrivateBytes = 11MB
            $r = Get-EfficiencyMemoryVerdict @args
            Assert ($r.verdict -eq 'pass' -and -not $r.rerunUsed) 'Exact ceilings or control boundary failed.'
            $args.Run = New-MemoryRun 100 130
            $args.Rerun = New-MemoryRun 130 130
            Assert ((Get-EfficiencyMemoryVerdict @args).verdict -eq 'pass') 'Exactly ten times the ceiling failed without rerun.'
        }
        'memory-missing' {
            foreach ($bad in 'count', 'null', 'negative', 'nan', 'field', 'phase') {
                $phase = New-MemoryRun
                switch ($bad) {
                    'count' { $phase.samples = @($phase.samples | Select-Object -First 9) }
                    'null' { $phase.samples[4] = $null }
                    'negative' { $phase.samples[4].privateBytes = -1 }
                    'nan' { $phase.samples[4].privateBytes = [double]::NaN }
                    'field' { $phase.samples[4].Remove('workingSetBytes') }
                    'phase' { $phase.Remove('samples') }
                }
                $args.Run = $phase; $args.Rerun = $phase
                $r = Get-EfficiencyMemoryVerdict @args
                Assert ($r.verdict -eq 'not measured' -and $r.reason -eq 'samples') "Invalid $bad sample passed."
            }
        }
        'memory-replacement' {
            foreach ($identity in 'processIdentity', 'controlIdentity') {
                $args.Rerun = New-MemoryRun 104 104; $args.Rerun[$identity] = 'replacement/start'
                $r = Get-EfficiencyMemoryVerdict @args
                Assert ($r.verdict -eq 'not measured' -and $r.reason -eq 'identity' -and
                    -not $r.needsRerun -and $null -eq $r.growthBytes) 'A replacement process passed or retained stale results.'
                $args.Run = New-MemoryRun; $args.Run.Remove($identity); $args.Rerun = $args.Run
                Assert ((Get-EfficiencyMemoryVerdict @args).reason -eq 'identity') 'Missing identity was accepted.'
                $args.Run = New-MemoryRun
            }
        }
        'memory-retry' {
            $args.Remove('Rerun')
            $r = Get-EfficiencyMemoryVerdict @args
            Assert ($r.verdict -eq 'not measured' -and $r.reason -eq 'rerun required') 'Missing rerun passed.'
            $args.Run.samples[0] = $null; $args.Rerun = New-MemoryRun 104 104
            $r = Get-EfficiencyMemoryVerdict @args
            Assert ($r.verdict -eq 'pass' -and $r.rerunUsed) 'Valid retry of missing sample failed.'
            $args.Rerun = New-MemoryRun 100 131
            Assert ((Get-EfficiencyMemoryVerdict @args).verdict -eq 'fail') 'Growth on the only retry passed.'
        }
        'memory-invalid' {
            foreach ($value in -1, ([double]::NaN), ([double]::PositiveInfinity)) {
                $args.Margin = $value; $rejected = $false
                try { $null = Get-EfficiencyMemoryVerdict @args } catch [Management.Automation.ParameterBindingException] { $rejected = $true }
                Assert $rejected 'Invalid margin was not rejected.'
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
