#Requires -Version 7
# Offline checks of tools/agents/reply-session.ps1 against a throwaway fake Home Assistant on localhost (#1634):
# it prints 'sent' (exit 0) only when the host said 'delivered' for its own request id, exits 3 when the host refused
# or said nothing, and three rapid sends to one node all arrive once, in order. Nothing here reaches a real Home Assistant.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'reply-session.ps1'
$node = 'agent_bridge_0123456789abcdef'
$failures = 0

function Assert([bool]$Ok, [string]$Name) {
    if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ }
}

$port = Get-Random -Minimum 20000 -Maximum 40000
$fake = [hashtable]::Synchronized(@{ Mode = 'deliver'; Slot = $null; Ack = $null; Delivered = [System.Collections.ArrayList]::new(); Stop = $false })
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$port/")
$listener.Start()

$server = Start-ThreadJob -ArgumentList $listener, $fake, $node -ScriptBlock {
    param($listener, $fake, $node)
    while ($listener.IsListening) {
        try { $ctx = $listener.GetContext() } catch { break }
        $path = $ctx.Request.Url.AbsolutePath -replace '^/api/', ''
        $body = [IO.StreamReader]::new($ctx.Request.InputStream).ReadToEnd()
        $out = @{}
        if ($path -eq 'services/script/agent_bridge_reply_payload') {
            $fake.Slot = ($body | ConvertFrom-Json).payload | ConvertFrom-Json
        }
        elseif ($path -eq 'services/button/press') {
            # The host takes whatever is in the single slot when Send is pressed.
            $slot = $fake.Slot
            if ($slot -and $fake.Mode -eq 'deliver') {
                [void]$fake.Delivered.Add($slot.text)
                $fake.Ack = @{ rid = $slot.rid; outcome = 'delivered'; at = [DateTime]::UtcNow.Ticks }
            }
            elseif ($slot -and $fake.Mode -eq 'refuse') {
                $fake.Ack = @{ rid = $slot.rid; outcome = 'refused'; at = [DateTime]::UtcNow.Ticks; error = 'the session is waiting on a question' }
            }
            elseif ($slot -and $fake.Mode -eq 'refuse-box') {
                $fake.Ack = @{ rid = $slot.rid; outcome = 'refused'; at = [DateTime]::UtcNow.Ticks; error = "something is already typed in Copilot's input box at the machine (364 characters, not shown here); send or clear it there first" }
            }
        }
        elseif ($path -eq "states/sensor.${node}_reply_payload") {
            $out = @{ state = 'x'; attributes = @{ at = $fake.Slot.at } }
        }
        elseif ($path -eq "states/sensor.${node}_activity") {
            $attrs = @{}
            if ($fake.Ack) { $attrs.reply_ack = $fake.Ack; if ($fake.Ack.error) { $attrs.error = $fake.Ack.error } }
            $out = @{ state = 'Done'; attributes = $attrs }
        }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($out | ConvertTo-Json -Depth 5 -Compress))
        $ctx.Response.ContentType = 'application/json'
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
}

$env:AGENT_HA_URL = "http://127.0.0.1:$port/api"
$env:AGENT_HA_AGENT_TOKEN = 'test-token-not-real'

function Send([string]$Text, [string]$Rid) {
    $a = @('-NoProfile', '-File', $script, '-Session', $node, '-Text', $Text, '-Rid', $Rid, '-AckSeconds', '3', '-SeenPolls', '4', '-Attempts', '1')
    $out = & pwsh @a 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $out }
}

try {
    $fake.Mode = 'deliver'
    $r = Send 'hello' 'r-ok'
    Assert ($r.Exit -eq 0 -and $r.Out -match "(?m)^sent to $node") 'a delivered message exits 0 and says sent'

    $fake.Mode = 'refuse'; $fake.Ack = $null
    $r = Send 'too soon' 'r-refused'
    Assert ($r.Exit -eq 3 -and $r.Out -match 'Reply NOT sent' -and $r.Out -match 'waiting on a question' -and $r.Out -notmatch "(?m)^sent to $node") 'a refused message exits 3 and is not reported as sent'

    $fake.Mode = 'silent'; $fake.Ack = $null
    $r = Send 'lost' 'r-lost'
    Assert ($r.Exit -eq 3 -and $r.Out -match 'Reply NOT confirmed' -and $r.Out -notmatch "(?m)^sent to $node") 'a message the host never answers exits 3 after the wait'

    # A reply refused because stale text sits in the input box (#150) is a loud, distinct failure, never exit 0.
    $fake.Mode = 'refuse-box'; $fake.Ack = $null
    $r = Send 'hello' 'r-box'
    Assert ($r.Exit -eq 4 -and $r.Out -match 'do not keep retrying' -and $r.Out -match '364 characters' -and $r.Out -notmatch "(?m)^sent to $node") 'a refusal for text left in the input box exits 4 and says so'

    # An ack for an earlier send is not this send's outcome.
    $fake.Mode = 'silent'; $fake.Ack = @{ rid = 'someone-else'; outcome = 'delivered' }
    $r = Send 'overwritten' 'r-over'
    Assert ($r.Exit -eq 3 -and $r.Out -notmatch "(?m)^sent to $node") "another sender's ack is not taken as this send's"

    # Retrying a refused rid must not read the old refusal as the retry's outcome.
    $fake.Mode = 'deliver'; $fake.Ack = @{ rid = 'r-retry'; outcome = 'refused'; at = 1; error = 'old refusal' }
    $r = Send 'retry' 'r-retry'
    Assert ($r.Exit -eq 0 -and $r.Out -match '(?m)^sent to') 'a retry with the same rid is judged on the new ack, not the old refusal'
    $fake.Mode = 'silent'; $fake.Ack = @{ rid = 'r-stale'; outcome = 'refused'; at = 1; error = 'old refusal' }
    $r = Send 'retry' 'r-stale'
    Assert ($r.Exit -eq 3 -and $r.Out -match 'NOT confirmed') 'an old refusal for the same rid is not taken as the retry''s outcome'

    # Three rapid sends to one node: each is delivered once, in the order sent.
    $fake.Mode = 'deliver'; $fake.Ack = $null; $fake.Delivered.Clear()
    $procs = foreach ($n in 1..3) {
        $p = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-File', $script, '-Session', $node, '-Text', "m$n", '-Rid', "rapid-$n", '-AckSeconds', '10', '-SeenPolls', '10', '-Attempts', '1')
        Start-Sleep -Milliseconds 800
        $p
    }
    $procs | Wait-Process -Timeout 60
    Assert (@($procs | Where-Object { $_.ExitCode -ne 0 }).Count -eq 0) 'three rapid sends all exit 0'
    Assert (($fake.Delivered -join ',') -eq 'm1,m2,m3') 'three rapid sends arrive once each, in order'
}
finally {
    $listener.Stop(); $listener.Close()
    Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\AGENT_HA_URL, Env:\AGENT_HA_AGENT_TOKEN -ErrorAction SilentlyContinue
}

if ($failures) { "$failures failure(s)"; exit 1 }
'reply-session: all checks passed'
