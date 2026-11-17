#Requires -Version 7.4
function Add-EfficiencyBrowserEvent($Browser, $Event) {
    if (-not $Event.ContainsKey('method')) { return }
    $p = $Event.params
    switch ($Event.method) {
        'Network.requestWillBeSent' {
            $Browser.requests++
            if ($p.ContainsKey('redirectResponse')) { $Browser.bytesIn += $p.redirectResponse.encodedDataLength }
            if ($p.request.ContainsKey('postDataEntries') -and $p.request.postDataEntries.Count -gt 0) {
                foreach ($entry in $p.request.postDataEntries) {
                    if ($entry.ContainsKey('bytes') -and $null -ne $entry.bytes) { $Browser.bytesOut += [Convert]::FromBase64String($entry.bytes).Length }
                    else { $Browser.networkUnavailable = 'An upload body was not exposed by CDP.' }
                }
            } elseif ($p.request.ContainsKey('postData')) {
                $Browser.bytesOut += [Text.Encoding]::UTF8.GetByteCount($p.request.postData)
            } elseif ($p.request.ContainsKey('hasPostData') -and $p.request.hasPostData) {
                $Browser.networkUnavailable = 'An upload body was not exposed by CDP.'
            }
        }
        'Network.loadingFinished' { $Browser.bytesIn += $p.encodedDataLength }
        'Network.loadingFailed' { $Browser.networkUnavailable = 'A request failed; complete transfer bytes are unavailable.' }
        { $_ -in 'Network.webSocketFrameSent', 'Network.webSocketFrameReceived' } {
            $frame = $p.response
            $bytes = if ($frame.opcode -eq 1) { [Text.Encoding]::UTF8.GetByteCount($frame.payloadData) }
                else { [Convert]::FromBase64String($frame.payloadData).Length }
            if ($Event.method -eq 'Network.webSocketFrameSent') { $Browser.bytesOut += $bytes } else { $Browser.bytesIn += $bytes }
            $Browser.messages++
        }
    }
}

function Invoke-EfficiencyCdp($Browser, [string] $Method) {
    $id = ++$Browser.commandId
    $bytes = [Text.Encoding]::UTF8.GetBytes((@{ id = $id; method = $Method } | ConvertTo-Json -Compress))
    $timeout = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds(5))
    try {
        $null = $Browser.socket.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $timeout.Token).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(65536)
        while ($true) {
            $message = [IO.MemoryStream]::new()
            try {
                do {
                    $part = $Browser.socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $timeout.Token).GetAwaiter().GetResult()
                    if ($part.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) { throw [IO.IOException]::new('CDP disconnected or sent a non-text message.') }
                    $message.Write($buffer, 0, $part.Count)
                    if ($message.Length -gt 16777216) { throw [IO.IOException]::new('CDP message exceeded the observation limit.') }
                } until ($part.EndOfMessage)
                $event = [Text.Encoding]::UTF8.GetString($message.ToArray()) | ConvertFrom-Json -AsHashtable
            } finally { $message.Dispose() }
            if ($event.ContainsKey('id') -and $event.id -eq $id) {
                if ($event.ContainsKey('error')) { throw [IO.IOException]::new('CDP rejected the observation command.') }
                return $event.result
            }
            Add-EfficiencyBrowserEvent $Browser $event
        }
    } finally { $timeout.Dispose() }
}

function Open-EfficiencyBrowser([string] $Endpoint) {
    $target = @{ processId = $null; unavailable = 'No browser page WebSocket supplied; use -PwaCdpWebSocket for an already-running PWA.' }
    if ([string]::IsNullOrWhiteSpace($Endpoint)) { return $target }
    $uri = $null
    if (-not [uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin 'ws', 'wss') {
        throw [ArgumentException]::new('-PwaCdpWebSocket must be a ws:// or wss:// page endpoint.')
    }
    $browser = @{ socket = [Net.WebSockets.ClientWebSocket]::new(); commandId = 0
        bytesIn = 0L; bytesOut = 0L; requests = 0L; messages = 0L; networkUnavailable = $null }
    $timeout = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds(5))
    try {
        $null = $browser.socket.ConnectAsync($uri, $timeout.Token).GetAwaiter().GetResult()
        $null = Invoke-EfficiencyCdp $browser 'Performance.enable'
        $null = Invoke-EfficiencyCdp $browser 'Network.enable'
        $target.browser = $browser
        $target.unavailable = $null
    } catch [Net.WebSockets.WebSocketException], [OperationCanceledException], [IO.IOException], [Management.Automation.MethodInvocationException] {
        $failure = $_.Exception.GetBaseException()
        if ($failure -isnot [Net.WebSockets.WebSocketException] -and $failure -isnot [Net.Sockets.SocketException] -and $failure -isnot [OperationCanceledException] -and $failure -isnot [IO.IOException]) { throw }
        $browser.socket.Dispose()
        $target.unavailable = 'Browser CDP connection unavailable, timed out, or refused the observation commands.'
    } finally { $timeout.Dispose() }
    return $target
}

function Read-EfficiencyBrowser($Browser) {
    try {
        $metrics = @{}
        foreach ($metric in (Invoke-EfficiencyCdp $Browser 'Performance.getMetrics').metrics) { $metrics[$metric.name] = $metric.value }
        if (-not $metrics.ContainsKey('TaskDuration') -or -not $metrics.ContainsKey('JSHeapUsedSize')) { throw [IO.IOException]::new('Required CDP metrics absent.') }
        $unavailable = @{
            buildVersion = 'Not collected by page-only attachment.'
            topThreads = 'CDP measures renderer main-thread task time, not OS threads.'
            workingSetBytes = 'CDP exposes JS heap, not process working set.'
            privateBytes = 'CDP exposes JS heap, not process private bytes.'
        }
        if ($Browser.networkUnavailable) { $unavailable.network = $Browser.networkUnavailable }
        return @{ identity = 'page'; cpuSeconds = $metrics.TaskDuration; jsHeapUsedBytes = [long]$metrics.JSHeapUsedSize
            workingSetBytes = $null; privateBytes = $null; threads = $null; buildVersion = $null; unavailable = $unavailable
            network = @{ bytesIn = $Browser.bytesIn; bytesOut = $Browser.bytesOut; requests = $Browser.requests; messages = $Browser.messages } }
    } catch [Net.WebSockets.WebSocketException], [OperationCanceledException], [IO.IOException], [Management.Automation.MethodInvocationException] {
        $failure = $_.Exception.GetBaseException()
        if ($failure -isnot [Net.WebSockets.WebSocketException] -and $failure -isnot [Net.Sockets.SocketException] -and $failure -isnot [OperationCanceledException] -and $failure -isnot [IO.IOException]) { throw }
        return @{ unavailable = @{ process = 'Browser disconnected, timed out, or required CDP metrics became unavailable.' } }
    }
}
