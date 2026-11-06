#Requires -Version 7.2
<#
.SYNOPSIS
  Turns the host's reply confirmation lines into a distribution of how long each agent takes to
  record a reply, per agent and per state (idle or working), read-only (#1025).

.DESCRIPTION
  From #1025 on, the host logs one line for each reply that reached its confirmation, and one more
  for a reply that timed out unrecorded, once it is recorded after all or the watch is over:

    reply 0b7e3c1d to 99999999 confirm: copilot, working, typed 25, recorded 74210, timeout 60 s, delivered
    reply 0b7e3c1d to 99999999 confirm: copilot, working, typed 25, recorded never, timeout 60 s, unconfirmed
    reply 0b7e3c1d late: recorded 93120
    reply 0b7e3c1d late: not recorded within 300 s more

  Times are ms after the host received the request; "~" marks a time the host saw rather than the
  agent's own. The delay counted is recorded minus typed: how long the agent took to record the
  reply once every key had gone, using the late time for a reply recorded after its timeout.

  It only reads the log files it is given, by default the host's host.log and host.log.1 under
  every profile in %LOCALAPPDATA%\AgentBridge (~/.agent-bridge elsewhere). The lines hold ids,
  times and outcomes, never what a reply said.

.EXAMPLE
  ./ops/tools/reply-confirm-delays.ps1
.EXAMPLE
  ./ops/tools/reply-confirm-delays.ps1 -Path .\host.log, .\host.log.1 -AsJson
#>
[CmdletBinding()]
param(
    # Host log files to read. Default: every profile's host.log and host.log.1.
    [string[]] $Path,
    # The distribution as JSON instead of a table.
    [switch] $AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $root = if ($IsWindows) { Join-Path $env:LOCALAPPDATA 'AgentBridge' } else { Join-Path $HOME '.agent-bridge' }
    $Path = @(Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'logs/host.log.1'; Join-Path $_.FullName 'logs/host.log' } |
        Where-Object { Test-Path $_ })
    if (-not $Path) { throw "No host.log found under $root; pass -Path." }
}

$confirm = [regex]'reply (?<id>[0-9a-f]{8}) to \S+ confirm: (?<agent>\w+), (?<state>idle|working), typed (?<typed>\d+|\?), recorded (?<recorded>~?\d+|never), timeout (?<timeout>\d+) s, (?<outcome>\w+)$'
$late = [regex]'reply (?<id>[0-9a-f]{8}) late: (?:recorded (?<recorded>~?\d+)|not recorded within (?<watch>\d+) s more)$'

$replies = [ordered]@{}
$lates = @{}
foreach ($file in $Path) {
    foreach ($line in [IO.File]::ReadLines((Resolve-Path $file))) {
        if (($m = $confirm.Match($line)).Success) {
            $replies[$m.Groups['id'].Value] = [pscustomobject]@{
                Agent = $m.Groups['agent'].Value
                State = $m.Groups['state'].Value
                Typed = if ($m.Groups['typed'].Value -eq '?') { $null } else { [long]$m.Groups['typed'].Value }
                Recorded = $m.Groups['recorded'].Value
                Timeout = [int]$m.Groups['timeout'].Value
                Outcome = $m.Groups['outcome'].Value
            }
        }
        elseif (($m = $late.Match($line)).Success) {
            $lates[$m.Groups['id'].Value] = if ($m.Groups['recorded'].Success) { $m.Groups['recorded'].Value } else { 'never' }
        }
    }
}

function Percentile([long[]] $sorted, [double] $p) {
    if ($null -eq $sorted -or $sorted.Count -eq 0) { return $null }
    $sorted[[math]::Min($sorted.Count - 1, [math]::Floor($p * $sorted.Count))]
}

$rows = foreach ($group in ($replies.GetEnumerator() | ForEach-Object Value | Group-Object Agent, State | Sort-Object Name)) {
    $delays = [System.Collections.Generic.List[long]]::new()
    $lateDelays = [System.Collections.Generic.List[long]]::new()
    $never = 0
    foreach ($entry in $replies.GetEnumerator()) {
        $r = $entry.Value
        if ("$($r.Agent), $($r.State)" -ne $group.Name) { continue }
        $at = $r.Recorded
        $wasLate = $false
        if ($at -eq 'never') {
            $at = $lates[$entry.Key] ?? 'never'
            $wasLate = $at -ne 'never'
        }
        if ($at -eq 'never' -or $null -eq $r.Typed) { if ($at -eq 'never') { $never++ }; continue }
        $delay = [long]$at.TrimStart('~') - $r.Typed
        $delays.Add($delay)
        if ($wasLate) { $lateDelays.Add($delay) }
    }
    $sorted = [long[]]@($delays | Sort-Object)
    [pscustomobject]@{
        Agent = $group.Group[0].Agent
        State = $group.Group[0].State
        Replies = $group.Count
        Delivered = @($group.Group | Where-Object Outcome -eq 'delivered').Count
        Unconfirmed = @($group.Group | Where-Object Outcome -eq 'unconfirmed').Count
        RecordedLate = $lateDelays.Count
        NeverRecorded = $never
        P50Ms = Percentile $sorted 0.5
        P90Ms = Percentile $sorted 0.9
        P99Ms = Percentile $sorted 0.99
        MaxMs = if ($sorted.Count) { $sorted[-1] } else { $null }
        TimeoutS = ($group.Group | ForEach-Object Timeout | Sort-Object -Unique) -join '/'
    }
}

if ($AsJson) { $rows | ConvertTo-Json -Depth 3 } else { $rows | Format-Table -AutoSize }
