# A simulated agent for the Beta acceptance run (#1185; the lab contract's AgentSim in its smallest
# form). It stands in for the agent only: it writes a Copilot-format session transcript the host
# reads, and drives the product's own hook (agent-bridge-host-hook) the way Copilot does, with
# the event on stdin. It never fakes the app, the host, the relay or the PWA.
#
#   pwsh quality/lab/agent-sim.ps1 -Hook <agent-bridge-host-hook.exe> -CopilotHome <scratch> -Question "Proceed?"
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Hook,
    # Copilot's home: the transcript goes to <Home>\session-state\<id>\events.jsonl.
    [Parameter(Mandatory)][string]$CopilotHome,
    [string]$Question = 'Acceptance question: proceed?',
    [string]$Prompt = 'Acceptance run: say hello.',
    [string]$SessionId = [guid]::NewGuid().ToString()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dir = Join-Path (Join-Path $CopilotHome 'session-state') $SessionId
New-Item -ItemType Directory -Force $dir | Out-Null
$file = Join-Path $dir 'events.jsonl'
$parent = $null
function Add-Event([string]$type, [hashtable]$data) {
    $id = [guid]::NewGuid().ToString()
    $line = [ordered]@{ type = $type; data = $data; id = $id; timestamp = (Get-Date).ToUniversalTime().ToString('o'); parentId = $script:parent } | ConvertTo-Json -Depth 8 -Compress
    Add-Content -Path $file -Value $line -Encoding utf8
    $script:parent = $id
}
function Invoke-Hook([string]$name, [hashtable]$event) {
    $json = $event | ConvertTo-Json -Depth 6 -Compress
    # A batch file cannot be started directly without the shell; the real hook is an .exe.
    if ($Hook -match '\.(cmd|bat)$') {
        if ($Hook -match '[&|<>^%!"()]') { throw "a batch hook path must not contain cmd metacharacters: $Hook" }
        $psi = [Diagnostics.ProcessStartInfo]::new($env:ComSpec)
        $psi.Arguments = "/d /s /c `"`"$Hook`" copilot $name`""
    }
    else {
        $psi = [Diagnostics.ProcessStartInfo]::new($Hook)
        foreach ($a in 'copilot', $name) { $psi.ArgumentList.Add($a) }
    }
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write($json); $p.StandardInput.Close()
    $out = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(15000)) { $p.Kill(); throw "hook $name did not end in 15 s" }
    [pscustomobject]@{ Hook = $name; Exit = $p.ExitCode; Output = $out.Trim() }
}

$cwd = $CopilotHome
Add-Event 'session.start' @{ sessionId = $SessionId; version = 1; producer = 'copilot-agent'; copilotVersion = '1.0.105'; startTime = (Get-Date).ToUniversalTime().ToString('o'); selectedModel = 'acceptance-sim'; context = @{ cwd = $cwd } }
Add-Event 'user.message' @{ content = $Prompt; transformedContent = $Prompt; attachments = @() }
Add-Event 'assistant.turn_start' @{ turnId = '1' }
$call = 'call_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
Add-Event 'assistant.message' @{ messageId = [guid]::NewGuid().ToString(); content = ''; toolRequests = @(@{ toolCallId = $call; name = 'ask_user'; arguments = @{ question = $Question }; type = 'function' }) }
Add-Event 'tool.execution_start' @{ toolCallId = $call; toolName = 'ask_user'; arguments = @{ question = $Question } }

# The hook's contract (HookProcessTests): ask_user is let through at once; agent_stop replies {}.
$results = @(
    Invoke-Hook 'ask_user' @{ sessionId = $SessionId; toolName = 'ask_user'; toolArgs = @{ question = $Question }; cwd = $cwd }
    Invoke-Hook 'agent_stop' @{ sessionId = $SessionId; cwd = $cwd }
)
[pscustomobject]@{ SessionId = $SessionId; Transcript = $file; Question = $Question; Hooks = $results }
