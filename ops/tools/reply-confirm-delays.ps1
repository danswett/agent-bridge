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

  A reply is counted NeverRecorded only once its late watch is known to be over: its `late: not
  recorded within` line is there, or the same log has a line stamped at least 300 s after the
  reply's confirm line (the watch is five minutes). A reply whose watch may still be running (the
  log ends sooner), whose transcript could not be read, or whose host stopped and restarted
  before its watch ended (the later activity is then a restart's: look at those by hand if the
  host was restarted) is not counted as never recorded. It goes in PendingOrNotObservable, so the
  never-recorded count is only what was watched to the end. Read a live log again later and
  pending replies move to their bucket.

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
$late = [regex]'reply (?<id>[0-9a-f]{8}) late: (?:recorded (?<recorded>~?\d+)|not recorded within (?<watch>\d+) s more|(?<unreadable>its transcript could not be read.*))$'
$stamp = [regex]'^(?<at>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) '
# The host's late watch (ReplyOptions.LateWatch, set in Program.cs).
$watchSeconds = 300

$replies = [ordered]@{}
$lates = @{}
$logEnd = @{}
foreach ($file in $Path) {
    $logEnd[$file] = $null
    foreach ($line in [IO.File]::ReadLines((Resolve-Path $file))) {
        $stampedAt = $null
        if (($s = $stamp.Match($line)).Success) {
            $stampedAt = [datetime]::ParseExact($s.Groups['at'].Value, 'yyyy-MM-dd HH:mm:ss', [cultureinfo]::InvariantCulture)
            if ($null -eq $logEnd[$file] -or $stampedAt -gt $logEnd[$file]) { $logEnd[$file] = $stampedAt }
        }
        if (($m = $confirm.Match($line)).Success) {
            $replies[$m.Groups['id'].Value] = [pscustomobject]@{
                Agent = $m.Groups['agent'].Value
                State = $m.Groups['state'].Value
                Typed = if ($m.Groups['typed'].Value -eq '?') { $null } else { [long]$m.Groups['typed'].Value }
                Recorded = $m.Groups['recorded'].Value
                Timeout = [int]$m.Groups['timeout'].Value
                Outcome = $m.Groups['outcome'].Value
                File = $file
                At = $stampedAt
            }
        }
        elseif (($m = $late.Match($line)).Success) {
            $lates[$m.Groups['id'].Value] =
                if ($m.Groups['recorded'].Success) { $m.Groups['recorded'].Value }
                elseif ($m.Groups['unreadable'].Success) { 'unobservable' }
                else { 'never' }
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
    $pending = 0
    foreach ($entry in $replies.GetEnumerator()) {
        $r = $entry.Value
        if ("$($r.Agent), $($r.State)" -ne $group.Name) { continue }
        $at = $r.Recorded
        $wasLate = $false
        if ($at -eq 'never') {
            $at = $lates[$entry.Key] ?? 'pending'
            if ($at -eq 'pending' -and $null -ne $r.At -and $null -ne $logEnd[$r.File] -and $logEnd[$r.File] -ge $r.At.AddSeconds($watchSeconds)) { $at = 'never' }
            $wasLate = $at -notin 'never', 'pending', 'unobservable'
        }
        if ($at -in 'never', 'pending', 'unobservable') {
            if ($at -eq 'never') { $never++ } else { $pending++ }
            continue
        }
        if ($null -eq $r.Typed) { continue }
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
        PendingOrNotObservable = $pending
        P50Ms = Percentile $sorted 0.5
        P90Ms = Percentile $sorted 0.9
        P99Ms = Percentile $sorted 0.99
        MaxMs = if ($sorted.Count) { $sorted[-1] } else { $null }
        TimeoutS = ($group.Group | ForEach-Object Timeout | Sort-Object -Unique) -join '/'
    }
}

if ($AsJson) { $rows | ConvertTo-Json -Depth 3 }
else {
    $rows | Format-Table -AutoSize
    Write-Host "NeverRecorded counts only replies whose $watchSeconds s late watch is known to be over; PendingOrNotObservable are replies still watched (or not observable: transcript unreadable), so they are in neither NeverRecorded nor the delays."
}
