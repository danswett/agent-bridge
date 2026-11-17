#Requires -Version 7.4
param([ValidateSet('elapsed', 'exit', 'replacement', 'readonly', 'no-survivors', 'partial', 'ambiguous', 'metric-values', 'entrypoint', 'pwa-metrics', 'pwa-missing', 'pwa-exit', 'pwa-network', 'pwa-cdp')][string] $Scenario)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'efficiency-baseline.ps1')
function Assert($Condition, [string] $Reason) { if (-not $Condition) { throw $Reason } }
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
if ($Scenario -like 'pwa-*') {
    $browser = @{ bytesIn = 0L; bytesOut = 0L; requests = 0L; messages = 0L; networkUnavailable = $null }
    if ($Scenario -eq 'pwa-cdp') {
        $socket = [pscustomobject]@{ calls = [Collections.Generic.List[string]]::new(); pending = [Collections.Generic.Queue[string]]::new(); reject = $false }
        $socket | Add-Member ScriptMethod SendAsync {
            param($data, $type, $end, $token)
            $command = [Text.Encoding]::UTF8.GetString($data.Array) | ConvertFrom-Json -AsHashtable
            $this.calls.Add($command.method)
            $reply = @{ id = $command.id; result = @{ metrics = @(@{ name = 'TaskDuration'; value = 12.5 }, @{ name = 'JSHeapUsedSize'; value = 4096 }) } }
            if ($this.reject) { $reply = @{ id = $command.id; error = @{ message = 'not supported' } } }
            $this.pending.Enqueue('{"method":"Network.loadingFinished","params":{"encodedDataLength":123}}')
            $json = $reply | ConvertTo-Json -Depth 6 -Compress
            $this.pending.Enqueue($json.Substring(0, 10)); $this.pending.Enqueue($json.Substring(10))
            return [Threading.Tasks.Task]::CompletedTask
        }
        $socket | Add-Member ScriptMethod ReceiveAsync {
            param($buffer, $token)
            $text = $this.pending.Dequeue(); $bytes = [Text.Encoding]::UTF8.GetBytes($text)
            [Array]::Copy($bytes, 0, $buffer.Array, $buffer.Offset, $bytes.Length)
            $part = [Net.WebSockets.WebSocketReceiveResult]::new($bytes.Length, [Net.WebSockets.WebSocketMessageType]::Text, ($text.Length -ne 10))
            return [Threading.Tasks.Task[Net.WebSockets.WebSocketReceiveResult]]::FromResult($part)
        }
        $browser.socket = $socket; $browser.commandId = 0
        $snapshot = Read-EfficiencyBrowser $browser
        Assert ($snapshot.cpuSeconds -eq 12.5 -and $snapshot.jsHeapUsedBytes -eq 4096 -and $snapshot.network.bytesIn -eq 123) 'CDP transport lost interleaved events, fragments or metrics.'
        Assert ($socket.calls.Count -eq 1 -and $socket.calls[0] -eq 'Performance.getMetrics') 'Unexpected browser mutation command.'
        $socket.reject = $true
        Assert ((Read-EfficiencyBrowser $browser).unavailable.ContainsKey('process')) 'Rejected CDP command looked like idle.'
    }
    if ($Scenario -eq 'pwa-network') {
        $text = [string][char]0xe9
        Add-EfficiencyBrowserEvent $browser @{ method = 'Network.requestWillBeSent'; params = @{ request = @{ hasPostData = $true; postData = $text } } }
        Add-EfficiencyBrowserEvent $browser @{ method = 'Network.loadingFinished'; params = @{ encodedDataLength = 100 } }
        Add-EfficiencyBrowserEvent $browser @{ method = 'Network.webSocketFrameSent'; params = @{ response = @{ opcode = 1; payloadData = $text } } }
        Add-EfficiencyBrowserEvent $browser @{ method = 'Network.webSocketFrameReceived'; params = @{ response = @{ opcode = 2; payloadData = 'AQID' } } }
        Assert ($browser.bytesIn -eq 103 -and $browser.bytesOut -eq 4 -and $browser.requests -eq 1 -and $browser.messages -eq 2) 'CDP bytes, binary frames or request counts are wrong.'
        Add-EfficiencyBrowserEvent $browser @{ method = 'Network.requestWillBeSent'; params = @{ request = @{ hasPostData = $true } } }
        Assert (-not [string]::IsNullOrWhiteSpace($browser.networkUnavailable)) 'Unobservable upload was silently counted as zero.'
        $browser.networkUnavailable = $null
        Add-EfficiencyBrowserEvent $browser @{ method = 'Network.requestWillBeSent'; params = @{ request = @{ hasPostData = $true; postDataEntries = @() } } }
        Assert (-not [string]::IsNullOrWhiteSpace($browser.networkUnavailable)) 'Empty upload entries were silently counted as zero.'
    }
    $options.Targets.host = @{ processId = $null; unavailable = 'No host fixture.' }
    $options.Targets.pwa = @{ processId = $null; browser = $browser; unavailable = $null }
    $options.ReadBrowserSnapshot = {
        param($connection)
        $i = $state.index++
        if ($Scenario -eq 'pwa-exit' -and $i -eq 1) { return @{ unavailable = @{ process = 'Browser disconnected.' } } }
        @{ identity = 'page'; cpuSeconds = 10 + $i; jsHeapUsedBytes = 1000L + $i
            workingSetBytes = $null; privateBytes = $null; threads = $null; buildVersion = $null
            unavailable = $(if ($connection.networkUnavailable) { @{ network = $connection.networkUnavailable } } else { @{} })
            network = @{ bytesIn = 100L * $i; bytesOut = 20L * $i; requests = $i; messages = 2 * $i } }
    }
    if ($Scenario -eq 'pwa-missing') {
        $options.Targets.pwa = Open-EfficiencyBrowser ''
        Assert (-not [string]::IsNullOrWhiteSpace($options.Targets.pwa.unavailable)) 'Absent browser has no reason.'
        $rejected = $false
        try { Open-EfficiencyBrowser 'https://example.invalid' } catch [ArgumentException] { $rejected = $true }
        Assert $rejected 'Non-WebSocket CDP endpoint was accepted.'
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
        $missing = Open-EfficiencyBrowser "ws://127.0.0.1:$port/devtools/page/absent"
        Assert (-not $missing.ContainsKey('browser') -and -not [string]::IsNullOrWhiteSpace($missing.unavailable)) 'Absent debug browser failed without an unavailable reason.'
    }
    $result = Invoke-EfficiencyBaseline @options
    Assert (Test-Json -Json ($result | ConvertTo-Json -Depth 20) -SchemaFile (Join-Path $PSScriptRoot 'efficiency-baseline.schema.json')) 'PWA output violates the one-file schema.'
    $pwa = $result.targets.pwa
    if ($Scenario -in 'pwa-missing', 'pwa-exit') {
        Assert ($null -eq $pwa.cpuSecondsPerMinute -and $null -eq $pwa.network) 'Absent or disconnected browser looked idle.'
        Assert ($pwa.Contains('jsHeapUsedBytes') -and $null -eq $pwa.jsHeapUsedBytes -and $pwa.unavailable.ContainsKey('jsHeapUsedBytes')) 'Absent browser heap lost its explicit null and reason.'
        Assert (-not [string]::IsNullOrWhiteSpace($pwa.unavailable.process)) 'Browser failure lost its reason.'
        if ($Scenario -eq 'pwa-exit') { Assert ($pwa.status -eq 'partial' -and $pwa.sampleCount -eq 3) 'Lost partial browser evidence.' }
    } else {
        Assert ($pwa.status -eq 'available' -and $pwa.processId -eq $null) 'Browser is unavailable or mislabeled as a process.'
        Assert ([math]::Abs($pwa.cpuSecondsPerMinute - 180.0 / 11) -lt 0.000001 -and $pwa.jsHeapUsedBytes -eq 1003) 'Wrong main-thread CPU rate or heap bytes.'
        if ($Scenario -eq 'pwa-network') {
            Assert ($null -eq $pwa.network -and -not [string]::IsNullOrWhiteSpace($pwa.unavailable.network)) 'Incomplete CDP network looked like zero traffic.'
        } else {
            $net = $pwa.network.endpoints[0]
            Assert ([math]::Abs($net.bytesInPerMinute - 18000.0 / 11) -lt 0.000001 -and [math]::Abs($net.bytesOutPerMinute - 3600.0 / 11) -lt 0.000001) 'Wrong network byte rates.'
            Assert ([math]::Abs($net.requestsPerMinute - 180.0 / 11) -lt 0.000001 -and [math]::Abs($net.messagesPerMinute - 360.0 / 11) -lt 0.000001) 'Wrong network request/frame rates.'
        }
        Assert ($null -eq $pwa.workingSetBytes -and $null -eq $pwa.privateBytes -and $null -eq $pwa.gcAllocationBytesPerSecond) 'Browser invented process or GC metrics.'
    }
    foreach ($name in 'host', 'desktop') {
        Assert ($null -eq $result.targets[$name].network -and $null -eq $result.targets[$name].gcAllocationBytesPerSecond) 'Browser collection changed process-only metrics.'
    }
    Write-Output "PASS: $Scenario"
    exit
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
        foreach ($file in 'efficiency-baseline.ps1', 'efficiency-baseline.cdp.ps1') {
            $script = Join-Path $PSScriptRoot $file
            $ast = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
            Assert ($errors.Count -eq 0) 'Invalid PowerShell syntax.'
            Assert (Test-ReadOnlyCommands $ast) 'Unsafe command or dynamic call operator.'
            Assert (-not ((Get-Content $script -Raw) -match '\.(Kill|Terminate|Suspend|Attach|Start)\(')) 'Mutating process API.'
        }
        foreach ($example in 'Invoke-Expression "Get-Process"', '& $command') {
            $badAst = [Management.Automation.Language.Parser]::ParseInput($example, [ref]$tokens, [ref]$errors)
            Assert (-not (Test-ReadOnlyCommands $badAst)) 'The read-only guard accepted dynamic execution.'
        }
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
