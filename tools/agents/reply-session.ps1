# reply-session.ps1: send one whole message (up to 60 KB) to a bridge session through Home Assistant, as the non-admin agent,
# and report what the host did with it (#1634).
# Usage: reply-session.ps1 -Session <full session id | agent_bridge_<16 hex> | session name such as 'SG SRE Lead'> -Text '<message>'
# Needs AGENT_HA_URL (the Home Assistant API address, ending /api) and AGENT_HA_AGENT_TOKEN in the environment.
# Each message carries a request id (rid). The host acknowledges it in the node's activity attributes (reply_ack), and this script
# prints 'sent' only once the host says 'delivered' for its own rid. There is one reply slot per node, so a send that is overwritten
# by another never gets its ack and is reported, never printed as sent. Nothing is queued: a refusal is final until the sender retries.
# Exit codes: 0 delivered; 1 not delivered (bad input, no token, Home Assistant unreachable); 2 the host could not confirm
# the typing (it MAY have been delivered: check before resending); 3 the host refused it or gave no outcome in time (retry).
# The token is read from the environment and never printed.
param(
  [Parameter(Mandatory)][string]$Session, [Parameter(Mandatory)][string]$Text, [int]$Attempts = 5,
  # How long to wait for the host's outcome for this message.
  [int]$AckSeconds = 20,
  # Retry with the rid of an earlier send that timed out: the host drops a rid it already delivered.
  [string]$Rid,
  [string]$Sender = 'reply-session',
  # How long to wait for the payload to show on the reply sensor before sending it again.
  [int]$SeenPolls = 20)
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

# The host's outcome for this rid, or $null while it has said nothing about it.
function Get-Ack {
  try {
    $a = (Get-Json "states/sensor.${node}_activity").attributes
    # An ack already on the node before this send (a refused earlier try with the same rid) is not this send's outcome.
    if ($a.reply_ack -and $a.reply_ack.rid -eq $Rid -and "$($a.reply_ack.at)" -ne $script:staleAt) { return [pscustomobject]@{ Outcome = [string]$a.reply_ack.outcome; Error = [string]$a.error } }
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
  $last = 'unknown'; $pressed = $false
  for ($a = 1; $a -le $Attempts -and -not $pressed; $a++) {
    try {
      $at = [DateTime]::UtcNow.ToString('o')
      $payload = @{ at = $at; text = $Text; driver = 'agent'; rid = $Rid; sender = $Sender } | ConvertTo-Json -Compress
      if ([Text.Encoding]::UTF8.GetByteCount($payload) -gt 60000) { [Console]::Error.WriteLine('Message is over the 60 KB limit; not sent.'); exit 1 }
      Post 'services/script/agent_bridge_reply_payload' @{ node = $node; payload = $payload }
      $seen = $false
      for ($i = 0; $i -lt $SeenPolls -and -not $seen; $i++) {
        Start-Sleep -Milliseconds 700
        $got = (Get-Json "states/sensor.${node}_reply_payload").attributes.at
        if ($got -is [DateTime]) { $got = $got.ToUniversalTime().ToString('o') }
        $seen = ($got -eq $at)
      }
      if (-not $seen) { $last = "the payload did not reach sensor.${node}_reply_payload (the single reply slot was overwritten or is slow)"; throw $last }
      Post 'services/button/press' @{ entity_id = "button.${node}_submit" }
      $pressed = $true
    } catch {
      $last = $_.Exception.Message
      if ($a -lt $Attempts) { Start-Sleep -Milliseconds (2000 + (Get-Random -Minimum 0 -Maximum 3000)) }
    }
  }
  if (-not $pressed) { [Console]::Error.WriteLine("Not delivered to $node after $Attempts attempts: $last"); exit 1 }

  # Held under the send lock, so a second send from this machine cannot overwrite the slot before the host has taken this one.
  $deadline = [DateTime]::UtcNow.AddSeconds($AckSeconds)
  do {
    $ack = Get-Ack
    if ($ack) {
      switch ($ack.Outcome) {
        'delivered' { "sent to $node"; exit 0 }
        'unconfirmed' { [Console]::Error.WriteLine("The host typed the message into $node but could not confirm it. It MAY have been delivered; check before resending (rid $Rid)."); exit 2 }
        default {
          $why = if ($ack.Error) { $ack.Error } else { 'the host refused it' }
          [Console]::Error.WriteLine("Reply NOT sent to ${node}: $why. Nothing is queued; retry later (rid $Rid).")
          exit 3
        }
      }
    }
    Start-Sleep -Milliseconds 300
  } while ([DateTime]::UtcNow -lt $deadline)
  [Console]::Error.WriteLine("Reply NOT confirmed for ${node}: the host gave no outcome for rid $Rid within $AckSeconds s (another sender may have overwritten the slot, or the host is busy). It may still arrive; retry with -Rid $Rid so the host drops a duplicate.")
  exit 3
} finally { if ($have) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
