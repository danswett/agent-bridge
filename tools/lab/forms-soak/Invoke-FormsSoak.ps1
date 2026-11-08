#requires -Version 7.6
<#
.SYNOPSIS
  Forms soak (screen-only mode): draws Copilot CLI ask_user forms from a stub MCP server and
  answers them with the host's own screen reader and key typer, with no model quota.

.DESCRIPTION
  For every form shape x console size x Copilot CLI, it starts the CLI in a console of that
  size with the stub MCP server, waits for the form, then runs what the host runs:
  CopilotForm.Read, CopilotPlanner.Plan, CopilotFormScreen.Diagnose (the screen check before any
  key) and WorkerInjector.InjectAsync (the injector that types). The stub reports what the CLI
  was finally given, which is the ground truth for the result.

  This mode tests the screen reader and the injector. It does NOT go through session.answer,
  the host's transcript reading, its ledger or its gates: a form raised before the session's
  first model turn has no transcript, so the host lists no question for it. See
  Invoke-FullPathAnswer for the marked place a full-path mode would go.

  Result classes:
    delivered                  the CLI was given the answer that was typed
    refused-before-any-key     the host's own checks said no (or could not read the screen); nothing typed
    unconfirmed                keys went, and nothing proves what the CLI was given
    wedged                     keys went and the form is still waiting
    half-answered              the CLI was given something other than the answer (wrong or missing fields, declined)

  Exit code: 0 when no run was wedged or half-answered, 1 when one was, 2 when the rig could not
  start (a missing piece). Stop an unattended loop by creating a file named STOP in -OutDir.

  It starts only its own console, one per run, and ends only that process tree, by id. It
  never looks at, attaches to or ends any other process.
#>
[CmdletBinding()]
param(
    # Folder holding AgentBridge.Host.Delivery.dll, AgentBridge.Host.Core.dll, AgentBridge.Protocol.dll
    # and agent-bridge-injector.exe (a published host, or an installed app's folder).
    [string]$HostDir = $env:AGENT_BRIDGE_HOST_DIR,
    # One or more Copilot CLI executables. Each is one "CLI version" of the matrix.
    [string[]]$CopilotPath = @('copilot'),
    [string[]]$Shapes = @('choice', 'choice-text', 'choice-yesno-text', 'number', 'long-title', 'tall', 'other'),
    [string[]]$Sizes = @('120x30', '120x50', '200x50', '250x40'),
    # Passes over the whole matrix; 0 runs until a file named STOP appears in -OutDir.
    [int]$Iterations = 1,
    [int]$PauseBetweenPassesSeconds = 0,
    [string]$OutDir = (Join-Path (Get-Location) 'forms-soak-results'),
    [string]$NodePath = 'node',
    # How long to wait for the form to be drawn, and for the CLI to report after the keys.
    [int]$FormTimeoutSeconds = 45,
    # Run folders (CLI home and logs, config, stub log) kept under -OutDir\work; older ones are deleted.
    [int]$KeepRuns = 30,
    [int]$SettleSeconds = 20,
    # Extra arguments for every CLI start (for example a model-free or trusted-folder flag).
    [string[]]$CopilotArguments = @(),
    [ValidateSet('screen', 'full')][string]$Mode = 'screen'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$OutDir = [IO.Path]::GetFullPath($OutDir)
# powershell -File passes "a,b" as one value.
$Shapes = @($Shapes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Sizes = @($Sizes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# Per shape: the answer typed, and the fields the CLI should then have been given.
$answers = @{
    'choice'            = '{"choice":"second"}'
    'choice-text'       = '{"choice":"second","notes":"soak note"}'
    'choice-yesno-text' = '{"choice":"third","proceed":true,"notes":"soak note"}'
    'number'            = '{"count":7}'
    'long-title'        = '{"choice":"first"}'
    'tall'              = '{"choice":"second","proceed":true,"notes":"soak note"}'
    'other'             = '{"choice":{"other":"soak other"}}'
}

# --- the hook for the full path ------------------------------------------------------------
# FULL-PATH HOOK (not implemented). A full-path mode would answer through the host's local pipe
# (session.answer, docs/protocol/local-pipe.md) instead of calling the planner and injector here.
# It needs the host to list the form as a question, and the host reads pending asks from the
# session's transcript (events.jsonl), which exists only after a first model turn. So it needs
# either a real first turn before the stub raises the form (costs quota), or proof that an MCP
# elicitation is recorded in the transcript once a turn exists. Until then -Mode full stops.
function Invoke-FullPathAnswer {
    param($Run)
    throw 'Mode full is not implemented: it needs a model turn (quota) so the host has a transcript to read. See the comment above this function.'
}

. (Join-Path $PSScriptRoot 'Redaction.ps1')

function Read-Rows($injector, [int]$id) {
    $screen = $injector.ReadScreenAsync($id, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($screen.Rows) { return , [string[]]@($screen.Rows) }
    $script:LastScreenProblem = $screen.Problem
    return $null
}

function Test-Showing($rows, $form) {
    for ($i = 0; $i -lt $form.Fields.Count; $i++) {
        if ($null -eq [AgentBridge.Host.Delivery.Copilot.CopilotFormScreen]::Showing($rows, $form, $i)) { return $true }
    }
    $false
}

function Stop-Tree([int]$id) {
    # Only the console this run started, by id; /T takes its own children with it.
    & taskkill.exe /PID $id /T /F *> $null
}

function Invoke-OneRun {
    param($Run, $Injector)
    $result = [ordered]@{ Outcome = ''; Detail = ''; Rows = @() }
    $form = $null
    $console = $null
    $workDir = Join-Path $OutDir ("work\" + $Run.Id)
    New-Item -ItemType Directory -Force $workDir | Out-Null
    $stubLog = Join-Path $workDir 'stub.log'
    try {
        $argsJson = & $NodePath (Join-Path $here 'stub-mcp.js') --print $Run.Shape
        $elicit = [Text.Json.JsonDocument]::Parse(($argsJson -join '')).RootElement.Clone()
        $read = [AgentBridge.Host.Delivery.Copilot.CopilotForm]::Read($elicit)
        if ($null -eq $read.Item1) { $result.Outcome = 'refused-before-any-key'; $result.Detail = "form not readable: $($read.Item2)"; return $result }
        $form = $read.Item1
        if ([AgentBridge.Host.Delivery.Copilot.CopilotFormScreen]::Ambiguous($form) -as [string]) {
            $result.Outcome = 'refused-before-any-key'; $result.Detail = 'ambiguous form: ' + [AgentBridge.Host.Delivery.Copilot.CopilotFormScreen]::Ambiguous($form); return $result
        }

        $mcp = @{ mcpServers = @{ Copilot = @{ type = 'stdio'; command = $NodePath; args = @((Join-Path $here 'stub-mcp.js'), $Run.Shape, '3'); tools = @('*'); env = @{ FORMS_SOAK_STUB_LOG = $stubLog } } } }
        $mcpFile = Join-Path $workDir 'mcp.json'
        $mcp | ConvertTo-Json -Depth 8 | Set-Content $mcpFile -Encoding utf8
        $w, $h = $Run.Size -split 'x'
        $cliArgs = (@('--no-auto-update', '--log-dir', "`"$workDir\logs`"", '--additional-mcp-config', "`"@$mcpFile`"") + $CopilotArguments) -join ' '
        $line = "mode con: cols=$w lines=$h >nul && `"$($Run.Cli)`" $cliArgs"
        # A fresh CLI home per run, inside the run folder: the CLI keeps its config and session
        # state there, so a run never reads or writes the user's own, and it is pruned with the run.
        # The token, if any, comes from the environment the rig was started in.
        $previousHome = $env:COPILOT_HOME
        try {
            $env:COPILOT_HOME = Join-Path $workDir 'home'
            New-Item -ItemType Directory -Force $env:COPILOT_HOME | Out-Null
            # The folder is trusted up front, or the CLI asks about it before it draws anything else.
            @{ trustedFolders = @($workDir) } | ConvertTo-Json | Set-Content (Join-Path $env:COPILOT_HOME 'config.json') -Encoding utf8
            $console = Start-Process -FilePath $env:ComSpec -ArgumentList '/d', '/c', "`"$line`"" -WorkingDirectory $workDir -WindowStyle Hidden -PassThru
        }
        finally { $env:COPILOT_HOME = $previousHome }
        $consoleId = $console.Id

        $deadline = [DateTime]::UtcNow.AddSeconds($FormTimeoutSeconds)
        $rows = $null
        $why = 'the form was never drawn'
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 800
            if ($console.HasExited) { $why = 'the console ended before the form was drawn'; break }
            $rows = Read-Rows $Injector $consoleId
            if ($null -eq $rows) { $why = "screen not readable: $script:LastScreenProblem"; continue }
            $d = [AgentBridge.Host.Delivery.Copilot.CopilotFormScreen]::Diagnose($rows, $form, 0)
            if ($null -eq $d.Item1) { $why = $null; break }
            $why = "screen check $([AgentBridge.Host.Delivery.Copilot.CopilotFormScreen]::Name($d.Item2)): $($d.Item1)"
            # A form not yet drawn and one the check cannot place look the same until the timeout.
        }
        if ($why) {
            $result.Outcome = 'refused-before-any-key'; $result.Detail = $why
            if ($rows) { $result.Rows = $rows }
            return $result
        }

        $req = [AgentBridge.Protocol.AnswerRequest]::new()
        $req.Id = 'soak'; $req.Question = 'soak'; $req.Request = [guid]::NewGuid().ToString()
        $fields = [Collections.Generic.Dictionary[string, Text.Json.JsonElement]]::new()
        foreach ($p in ([Text.Json.JsonDocument]::Parse($answers[$Run.Shape]).RootElement.EnumerateObject())) { $fields[$p.Name] = $p.Value.Clone() }
        $req.Fields = $fields
        $plan = [AgentBridge.Host.Delivery.Copilot.CopilotPlanner]::Plan($form, $req)
        if ($null -eq $plan.Item1) { $result.Outcome = 'refused-before-any-key'; $result.Detail = "planner: $($plan.Item2)"; $result.Rows = $rows; return $result }
        # The host refuses "Other" before any key (host-answers.md), so the rig does too.
        if ([AgentBridge.Host.Delivery.Copilot.CopilotPlanner]::ThroughOther($form, $plan.Item1)) {
            $result.Outcome = 'refused-before-any-key'; $result.Detail = 'the answer goes through "Other", which the host does not send'; $result.Rows = $rows; return $result
        }

        $typed = $Injector.InjectAsync($consoleId, $plan.Item1.Keys, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($typed.Status -eq 'NothingTyped') { $result.Outcome = 'refused-before-any-key'; $result.Detail = "injector: $($typed.Detail)"; $result.Rows = Read-Rows $Injector $consoleId; return $result }

        $got = $null
        $settle = [DateTime]::UtcNow.AddSeconds($SettleSeconds)
        while ([DateTime]::UtcNow -lt $settle -and $null -eq $got) {
            Start-Sleep -Milliseconds 500
            if (Test-Path $stubLog) { $got = Select-String -Path $stubLog -Pattern '^\S+ answer (.*)$' | Select-Object -Last 1 }
        }
        $after = Read-Rows $Injector $consoleId
        if ($got) {
            $reply = $got.Matches[0].Groups[1].Value | ConvertFrom-Json
            $match = $reply.action -eq 'accept'
            if ($match) {
                foreach ($p in ([Text.Json.JsonDocument]::Parse($answers[$Run.Shape]).RootElement.EnumerateObject())) {
                    $want = if ($p.Value.ValueKind -eq 'Object') { $p.Value.GetProperty('other').GetString() } else { $p.Value.ToString() }
                    $have = $reply.content.PSObject.Properties[$p.Name]
                    if ($null -eq $have -or "$($have.Value)" -ne $want) { $match = $false }
                }
            }
            if ($match) { $result.Outcome = 'delivered'; $result.Detail = "typed $($typed.Status), CLI was given the answer" }
            else { $result.Outcome = 'half-answered'; $result.Detail = "CLI was given: $($got.Matches[0].Groups[1].Value)"; $result.Rows = $after }
        }
        elseif ($after -and (Test-Showing $after $form)) {
            $result.Outcome = if ($typed.Status -eq 'Interrupted') { 'half-answered' } else { 'wedged' }
            $result.Detail = "injector $($typed.Status) ($($typed.Detail)); the form is still on screen and the CLI was given nothing"
            $result.Rows = $after
        }
        else {
            $result.Outcome = 'unconfirmed'; $result.Detail = "injector $($typed.Status) ($($typed.Detail)); the CLI reported nothing and the form is gone or unreadable"; $result.Rows = $after
        }
        $result
    }
    catch {
        $result.Outcome = 'unconfirmed'; $result.Detail = "rig error: $($_.Exception.Message)"; $result
    }
    finally {
        if ($console) { Stop-Tree $console.Id }
    }
}

# --- setup -----------------------------------------------------------------------------------
if ($Mode -eq 'full') { Invoke-FullPathAnswer $null }
if (-not $HostDir -or -not (Test-Path (Join-Path $HostDir 'AgentBridge.Host.Delivery.dll'))) {
    Write-Error 'Set -HostDir (or AGENT_BRIDGE_HOST_DIR) to a folder with AgentBridge.Host.Delivery.dll, AgentBridge.Host.Core.dll, AgentBridge.Protocol.dll and agent-bridge-injector.exe.'
    exit 2
}
$injectorPath = Join-Path $HostDir 'agent-bridge-injector.exe'
if (-not (Test-Path $injectorPath)) { Write-Error "agent-bridge-injector.exe is not in $HostDir"; exit 2 }
foreach ($n in 'AgentBridge.Protocol', 'AgentBridge.Host.Core', 'AgentBridge.Host.Delivery') { [void][Reflection.Assembly]::LoadFrom((Join-Path $HostDir "$n.dll")) }
$injector = [AgentBridge.Host.Delivery.Windows.WorkerInjector]::new($injectorPath, $null)

New-Item -ItemType Directory -Force $OutDir | Out-Null
$csv = Join-Path $OutDir 'results.csv'
$md = Join-Path $OutDir 'results.md'
if (-not (Test-Path $csv)) { 'time_utc,cli_version,shape,size,pass,outcome,detail' | Set-Content $csv -Encoding utf8 }
if (-not (Test-Path $md)) { "# Forms soak results`n`nScreen-only mode: tests the host's screen reader and injector, not session.answer.`nScreen rows are shown for every run that was not delivered, with names, paths and tokens removed.`n" | Set-Content $md -Encoding utf8 }

$clis = foreach ($c in $CopilotPath) {
    $v = try { ((& $c --version 2>&1 | Select-Object -First 1) -replace '[^\d.]', '').Trim('.') } catch { 'unknown' }
    [pscustomobject]@{ Path = $c; Version = $v }
}

$bad = 0
$pass = 0
$stop = Join-Path $OutDir 'STOP'
while (($Iterations -eq 0 -or $pass -lt $Iterations) -and -not (Test-Path $stop)) {
    $pass++
    foreach ($cli in $clis) { foreach ($size in $Sizes) { foreach ($shape in $Shapes) {
        if (Test-Path $stop) { break }
        $run = @{ Id = ('{0:yyyyMMddTHHmmss}-{1}-{2}' -f [DateTime]::UtcNow, $shape, $size); Cli = $cli.Path; Shape = $shape; Size = $size }
        $r = Invoke-OneRun -Run $run -Injector $injector
        $r = @($r)[-1]
        $detail = (Get-Redacted ([string]$r.Detail)) -replace '[\r\n",]+', ' '
        ('{0:o},{1},{2},{3},{4},{5},"{6}"' -f [DateTime]::UtcNow, $cli.Version, $shape, $size, $pass, $r.Outcome, $detail) | Add-Content $csv -Encoding utf8
        Write-Host ("{0} {1} {2} pass {3}: {4}" -f $cli.Version, $shape, $size, $pass, $r.Outcome)
        Get-ChildItem (Join-Path $OutDir 'work') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -Skip $KeepRuns | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        if ($r.Outcome -ne 'delivered') {
            $section = "`n## $($cli.Version) / $shape / $size / pass ${pass}: $($r.Outcome)`n`n$detail`n"
            if ($r.Rows -and $r.Rows.Count) {
                $screen = ($r.Rows | ForEach-Object { Get-Redacted ([string]$_).TrimEnd() } | Where-Object { $_ }) -join "`n"
                $section += "`n``````text`n$screen`n```````n"
            }
            Add-Content $md $section -Encoding utf8
        }
        if ($r.Outcome -in 'wedged', 'half-answered') { $bad++ }
    } } }
    if ($PauseBetweenPassesSeconds -gt 0 -and ($Iterations -eq 0 -or $pass -lt $Iterations)) { Start-Sleep -Seconds $PauseBetweenPassesSeconds }
}
Write-Host "Done: $pass pass(es), $bad wedged or half-answered. Results in $OutDir"
exit ($bad -gt 0 ? 1 : 0)
