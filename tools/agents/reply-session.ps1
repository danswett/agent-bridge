# reply-session.ps1: send one whole message (up to 60 KB) to a bridge session through Home Assistant, as the non-admin agent,
# and report what the host did with it (#1634).
# Usage: reply-session.ps1 -Session <full session id | agent_bridge_<16 hex> | session name such as 'SG SRE Lead'> -Text '<message>'
# Needs AGENT_HA_URL (the Home Assistant API address, ending /api) and AGENT_HA_AGENT_TOKEN in the environment.
# Each message carries a request id (rid). The host acknowledges it in the node's activity attributes (reply_ack), and this script
# prints 'sent' only once the host says 'delivered' for its own rid. There is one reply slot per node, so a send that is overwritten
# by another never gets its ack and is reported, never printed as sent. Nothing is queued: a refusal is final until the sender retries.
# Exit codes: 0 delivered; 1 not delivered (bad input, no token, Home Assistant unreachable); 2 the host could not confirm
# the typing (it MAY have been delivered: check before resending), or the rid repeats one it already handled; 3 the host refused it or gave no outcome in time (retry);
# 4 the reply is stuck in the session's input box (#150): the host refused because text is already typed there, or it typed
# this reply but its Enter never went, so its words are left there. Every later reply is refused until someone sends or
# clears it at the machine, so retrying cannot work.
# 5 the host said delivered, but the session's status did not change within -StatusSeconds (#150): treat it as NOT delivered.
# A 'delivered' ack alone is not enough for exit 0: the session's status must then move (a new turn), unless it was
# already working, when no status change can show the reply and the ack stands.
# Unconfirmed outcomes get one automatic retry with the same rid; -Attempts still bounds pre-submit payload attempts.
# Named outcomes and liveness requirements: docs/agents/comms-lanes-sender.md.
# The token is read from the environment and never printed.
param(
  [Parameter(Mandatory)][string]$Session, [Parameter(Mandatory)][string]$Text, [int]$Attempts = 5,
  # How long to wait for the host's outcome for this message.
  [int]$AckSeconds = 20,
  # Retry with the rid of an earlier send that timed out: the host drops a rid it already delivered.
  [string]$Rid,
  [string]$Sender = 'reply-session',
  # How long to wait for the payload to show on the reply sensor before sending it again.
  [int]$SeenPolls = 20,
  # How long after a 'delivered' ack to wait for the session's status to move before calling it not delivered (#150).
  [int]$StatusSeconds = 30)
$ErrorActionPreference = 'Stop'
$tok = [Environment]::GetEnvironmentVariable('AGENT_HA_AGENT_TOKEN', 'User'); if (-not $tok) { $tok = $env:AGENT_HA_AGENT_TOKEN }
if (-not $tok) { [Console]::Error.WriteLine('AGENT_HA_AGENT_TOKEN is not set for this user. Ask the owner to run set-agent-ha-token.ps1.'); exit 1 }
if (-not $env:AGENT_HA_URL) { [Console]::Error.WriteLine('AGENT_HA_URL is not set (the Home Assistant API address, ending /api); not sent.'); exit 1 }
if ($Rid -and $Rid -notmatch '^[A-Za-z0-9._-]{1,64}$') { [Console]::Error.WriteLine('-Rid must be 1 to 64 letters, digits, - _ or .; not sent.'); exit 1 }
if (-not $Rid) { $Rid = 'rs-' + [Guid]::NewGuid().ToString('N') }
$h = @{ Authorization = "Bearer $tok" }
$u = $env:AGENT_HA_URL.TrimEnd('/')
function Get-Json($path) { Invoke-RestMethod "$u/$path" -Headers $h -TimeoutSec 20 }
function Post($path, $body) { Invoke-RestMethod -Method Post "$u/$path" -Headers $h -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 20 | Out-Null }
function Complete-Reply([string]$Outcome, [int]$Code, [string]$Message) {
  $line = "$Message [outcome=$Outcome; rid=$Rid]"
  if ($Code -eq 0) { $line } else { [Console]::Error.WriteLine($line) }
  exit $Code
}
function Complete-RetryUnconfirmed([string]$Message) { Complete-Reply 'unconfirmed' 2 "The first submission may have been delivered; $Message" }

$node = $null
if ($Session -match '^agent_bridge_[0-9a-f]{16}$') { $node = $Session }
elseif (($Session -replace '-', '') -match '^[0-9a-fA-F]{16,32}$') { $node = 'agent_bridge_' + ($Session -replace '-', '').Substring(0, 16).ToLower() }
else {
  # Resolve a session name through the live status sensors: "Copilot: SG SRE Lead Status" -> its node.
  $want = ($Session -replace '\s+', ' ').Trim().ToLower()
  $found = @()
  foreach ($s in (Get-Json 'states')) {
    if ($s.entity_id -match '^sensor\.(agent_bridge_[0-9a-f]{16})_status$') {
      $n = $Matches[1]; $fn = [string]$s.attributes.friendly_name
      $name = (($fn -replace '^[^:]*:\s*', '') -replace '\s+Status$', '').Trim().ToLower()
      if ($name -eq $want -or $name -eq "sg $want") { $found += $n }
    }
  }
  $found = @($found | Select-Object -Unique)
  if ($found.Count -eq 1) { $node = $found[0] }
  elseif ($found.Count -gt 1) { [Console]::Error.WriteLine("The name '$Session' matches $($found.Count) sessions; use the agent_bridge_<16 hex> node."); exit 1 }
  else { [Console]::Error.WriteLine("No session named '$Session' was found. Use its full session id or agent_bridge_<16 hex> node."); exit 1 }
}

function Assert-NodeLive([switch]$Retry) {
  try { $states = Get-Json 'states' }
  catch {
    if ($Retry) { Complete-Reply 'unconfirmed' 2 'The first submission may have been delivered; receiver liveness could not be read, so the automatic retry was skipped.' }
    Complete-Reply 'unconfirmed' 1 'Not delivered: receiver liveness could not be read; nothing submitted.'
  }
  $lists = @($states | Where-Object { $_.entity_id -match '^sensor\.agent_bridge_.+_sessions$' })
  $owners = @($lists | Where-Object { @($_.attributes.sessions | Where-Object { $_.node -eq $node }).Count -gt 0 })
  if ($lists.Count -gt 0 -and $owners.Count -eq 0) {
    if ($Retry) { Complete-Reply 'unconfirmed' 2 "The first submission may have been delivered; $node is no longer listed by a host, so the automatic retry was skipped." }
    Complete-Reply 'node-gone' 1 "Not delivered: $node is absent from the host session lists."
  }
  if ($owners.Count -eq 1 -and $owners[0].state -notin 'unavailable', 'unknown') {
    $onlineId = $owners[0].entity_id -replace '^sensor\.', 'binary_sensor.' -replace '_sessions$', '_online'
    $online = @($states | Where-Object { $_.entity_id -eq $onlineId })
    if ($online.Count -eq 1 -and $online[0].state -eq 'on') {
      $instance = [string]$owners[0].attributes.instance
      if ($Retry -and [string]::IsNullOrWhiteSpace($instance)) {
        Complete-Reply 'unconfirmed' 2 'The first submission may have been delivered; the receiver instance is unknown, so the automatic retry was skipped.'
      }
      # This is machine.instance from the host, stable only for this process run.
      return $instance
    }
  }
  if ($Retry) { Complete-Reply 'unconfirmed' 2 "The first submission may have been delivered; $node has no unique online host session-list membership, so the automatic retry was skipped." }
  Complete-Reply 'unconfirmed' 1 "Not delivered: $node has no unique online host session-list membership; registration is not liveness."
}

# The session's status sensor as state and last change, or $null when it cannot be read. last_changed moves even when a
# short turn has gone back to idle by the next read, so a turn that came and went is still seen.
function Get-Status {
  try {
    $s = Get-Json "states/sensor.${node}_status"
    $changed = $s.last_changed; if ($changed -is [DateTime]) { $changed = $changed.ToUniversalTime().ToString('o') }
    return [pscustomobject]@{ State = [string]$s.state; Changed = [string]$changed }
  } catch { return $null }
}

# The host's outcome for this rid, or $null while it has said nothing about it.
function Get-Ack {
  try {
    $a = (Get-Json "states/sensor.${node}_activity").attributes
    # An ack already on the node before this send (a refused earlier try with the same rid) is not this send's outcome.
    if ($a.reply_ack -and $a.reply_ack.rid -eq $Rid -and "$($a.reply_ack.at)" -ne $script:staleAt) { return [pscustomobject]@{ Outcome = [string]$a.reply_ack.outcome; Error = [string]$a.error; Duplicate = ($a.reply_ack.duplicate -eq $true); At = "$($a.reply_ack.at)" } }
  } catch { }
  return $null
}

$mutex = New-Object System.Threading.Mutex($false, 'Global\agent-bridge-reply-session')
$have = $false
try {
  try { $have = $mutex.WaitOne([TimeSpan]::FromSeconds(60)) } catch [System.Threading.AbandonedMutexException] { $have = $true }
  if (-not $have) { [Console]::Error.WriteLine('Another reply on this machine held the send lock for over a minute; not sent.'); exit 1 }
  $script:staleAt = $null
  try { $before = (Get-Json "states/sensor.${node}_activity").attributes.reply_ack; if ($before -and $before.rid -eq $Rid) { $script:staleAt = "$($before.at)" } } catch { }
  $firstPayload = $null; $firstAt = $null; $firstReceiverInstance = $null; $firstAttemptUnconfirmed = $false
  for ($deliveryAttempt = 1; $deliveryAttempt -le 2; $deliveryAttempt++) {
    $last = 'unknown'; $pressed = $false
    for ($a = 1; $a -le $Attempts -and -not $pressed; $a++) {
      $receiverInstance = Assert-NodeLive -Retry:($deliveryAttempt -gt 1)
      if ($deliveryAttempt -gt 1 -and $receiverInstance -cne $firstReceiverInstance) {
        Complete-Reply 'unconfirmed' 2 'The first submission may have been delivered; the receiver instance changed, so the automatic retry was skipped.'
      }
      try {
        if ($deliveryAttempt -eq 1) {
          $at = [DateTime]::UtcNow.ToString('o')
          $payload = @{ at = $at; text = $Text; driver = 'agent'; rid = $Rid; sender = $Sender } | ConvertTo-Json -Compress
        } else { $payload = $firstPayload }
        if ([Text.Encoding]::UTF8.GetByteCount($payload) -gt 60000) { Complete-Reply 'not-delivered' 1 'Message is over the 60 KB limit; not sent.' }
        Post 'services/script/agent_bridge_reply_payload' @{ node = $node; payload = $payload }
        $seen = $false
        for ($i = 0; $i -lt $SeenPolls -and -not $seen; $i++) {
          Start-Sleep -Milliseconds 700
          $got = (Get-Json "states/sensor.${node}_reply_payload").attributes.at
          if ($got -is [DateTime]) { $got = $got.ToUniversalTime().ToString('o') }
          $expectedAt = if ($deliveryAttempt -eq 1) { $at } else { $firstAt }
          if ($expectedAt -is [DateTime]) { $expectedAt = $expectedAt.ToUniversalTime().ToString('o') }
          $seen = ($got -eq $expectedAt)
        }
        if (-not $seen) { $last = "the payload did not reach sensor.${node}_reply_payload (the single reply slot was overwritten or is slow)"; throw $last }
        if ($deliveryAttempt -eq 1) {
          $firstPayload = $payload; $firstAt = $at; $firstReceiverInstance = $receiverInstance; $statusBefore = Get-Status
        } else {
          $receiverInstance = Assert-NodeLive -Retry
          if ($receiverInstance -cne $firstReceiverInstance) {
            Complete-Reply 'unconfirmed' 2 'The first submission may have been delivered; the receiver instance changed before retry submission, so the automatic retry was skipped.'
          }
        }
        # A lost HTTP response may still have pressed Send: wait for its ack before any same-rid retry.
        $pressed = $true
        Post 'services/button/press' @{ entity_id = "button.${node}_submit" }
      } catch {
        $last = $_.Exception.Message
        if (-not $pressed -and $a -lt $Attempts) { Start-Sleep -Milliseconds (2000 + (Get-Random -Minimum 0 -Maximum 3000)) }
      }
    }
    if (-not $pressed) {
      if ($deliveryAttempt -gt 1) { Complete-Reply 'unconfirmed' 2 "The first submission may have been delivered; automatic retry preparation failed: $last" }
      Complete-Reply 'not-delivered' 1 "Not delivered to $node after $Attempts attempts: $last"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($AckSeconds)
    $delivered = $false; $unconfirmed = $false
    do {
      $ack = Get-Ack
      if ($ack) {
        if ($ack.Duplicate) {
          if ($deliveryAttempt -gt 1 -and $firstAttemptUnconfirmed) { Complete-RetryUnconfirmed "the retry received a duplicate acknowledgement ($($ack.Outcome)); check the session before resending." }
          Complete-Reply 'already-handled' 2 "The host already handled rid $Rid for $node (outcome then: $($ack.Outcome)) and typed nothing this time. Check the session before resending."
        }
        switch ($ack.Outcome) {
          'delivered' { $delivered = $true }
          'unconfirmed' {
            if ($deliveryAttempt -eq 1) { $firstAttemptUnconfirmed = $true }
            if ($ack.Error -match 'not sent and its words are still in the box') {
              if ($deliveryAttempt -gt 1 -and $firstAttemptUnconfirmed) { Complete-RetryUnconfirmed "the retry left text in the input box: $($ack.Error)" }
              Complete-Reply 'input-full' 4 "Reply NOT sent to ${node}: $($ack.Error). Send or clear those words at the machine; do not keep retrying."
            }
            $unconfirmed = $true
          }
          default {
            $why = if ($ack.Error) { $ack.Error } else { 'the host refused it' }
            if ($deliveryAttempt -gt 1 -and $firstAttemptUnconfirmed) { Complete-RetryUnconfirmed "the retry was refused: $why" }
            if ($why -match 'already typed in .*input box') {
              Complete-Reply 'input-full' 4 "Reply NOT sent to ${node}: $why. Send or clear that text at the machine; do not keep retrying."
            }
            Complete-Reply 'not-delivered' 3 "Reply NOT sent to ${node}: $why. Nothing is queued; retry later."
          }
        }
        if ($delivered -or $unconfirmed) { break }
      }
      Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $delivered) {
      if ($deliveryAttempt -eq 1) {
        if ($ack) { $script:staleAt = $ack.At }
        [Console]::Error.WriteLine("Unconfirmed reply to $node; retrying exactly once with the same rid $Rid.")
        $firstAttemptUnconfirmed = $true
        continue
      }
      if ($unconfirmed) {
        if ($deliveryAttempt -gt 1 -and $firstAttemptUnconfirmed) { Complete-RetryUnconfirmed "the retry could not be confirmed ($($ack.Error))." }
        Complete-Reply 'unconfirmed' 2 "The host could not confirm the message into $node ($($ack.Error)). It MAY have been delivered; check before resending."
      }
      if ($deliveryAttempt -gt 1 -and $firstAttemptUnconfirmed) { Complete-RetryUnconfirmed "the host gave no outcome for rid $Rid within $AckSeconds s after the same-rid retry." }
      Complete-Reply 'unconfirmed' 3 "Reply NOT confirmed for ${node}: the host gave no outcome for rid $Rid within $AckSeconds s after one same-rid retry. It may still arrive."
    }
    if ($statusBefore -and $statusBefore.State -eq 'working') { Complete-Reply 'sent' 0 "sent to $node (the session was already working; the host confirmed it)" }
    $statusDeadline = [DateTime]::UtcNow.AddSeconds($StatusSeconds)
    $readable = $false
    do {
      $now = Get-Status
      if ($now) {
        $readable = $true; $after = $now.State
        if ($now.State -eq 'working' -or ($statusBefore -and $now.State -eq $statusBefore.State -and $now.Changed -ne $statusBefore.Changed)) {
          Complete-Reply 'sent' 0 "sent to $node"
        }
      }
      Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $statusDeadline)
    $why = if (-not $readable) { "its status could not be read from Home Assistant for $StatusSeconds s" } else { "the session's status did not show a turn within $StatusSeconds s (it was $(if ($statusBefore) { "'$($statusBefore.State)'" } else { 'unreadable' }) before Send, '$after' after)" }
    if ($deliveryAttempt -gt 1 -and $firstAttemptUnconfirmed) { Complete-RetryUnconfirmed "the retry's delivery was not confirmed: $why." }
    Complete-Reply 'not-delivered' 5 "Reply NOT delivered to ${node}: the host said delivered, but $why. Check the session at the machine before resending; a blind resend may send it twice."
  }
} finally { if ($have) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
