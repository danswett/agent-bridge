# Own-agents messaging (AP5-lite): runbook

A temporary mode (#728, owner decision of 14:06 PT on 2026-11-08) that lets the owner's agents on the owner's
own machines message each other host to host over the relay, with Home Assistant as the automatic fallback,
until the signed roster (#55) exists. It is off by default and is removed or folded into the roster path
when that lands. It is built by slices L1 to L7 (#1349 to #1355, #1369, #1371). Text marked **Engineering to
confirm** describes a slice that had not merged when this page was written.

## What it trusts, and what it does not

It trusts four things, all checked on the receiving machine: the relay's account authentication (the relay
forwards only within one account, and the channel names the peer's device id); the **pinned own-peer list**
(the machines a person at this machine chose to trust, each with its agent signing key); **signed envelopes**
(a lite envelope: signature, 120 second life, text bound by hash, markers that can never verify in the
roster path); and the **agent credential** (AP1), which ties a message to one agent session on the sender.

It does **not** defend against a dishonest relay or a stolen relay-account login. There is no roster, so
nothing records which device a person approved or revoked. That is why the mode is temporary and
own-agents-only.

## The plain limits

- Own machines and own agents only. A message from any other account is never accepted, whatever it carries.
- Every pinned machine is trusted until a person removes it at that machine. No per-device approval or
  revocation by the signed roster until the roster lands.
- Text only, at most 4096 characters. Delivery uses the same routine the Home Assistant reply path uses, so
  allow-all sessions work as they do today, and text goes only into an idle session.
- **Not built yet.** There is no allow-all list, no provenance line and no rate limit. They come after the
  cut-over and before any rollout beyond the owner. The owner accepted these skips: any message from a pinned
  own machine is typed into any session there; no provenance line; no loop limit (the kill switch is the
  stop); the duplicate memory is in memory only, so a host restart inside 2 minutes can deliver a message
  twice; and with no roster, anyone with access to the owner's relay account could message his sessions.
- A message to an offline machine fails at once and falls back to Home Assistant. Nothing is queued.
- "Delivered" means typed and acknowledged, not read.

## Turn it on (a person at each machine, in this order: VM3, VM1, VM4, VM2)

1. Set `agentMessaging.remote` to `own-account` ([host-config](../host-config.md)). Leave
   `agentMessaging.allowAllTargets` as it is. Changes need no restart.
2. Pin the other machines with the local own-peer command (L2b, #1369; **Engineering to confirm** the exact
   command). It shows each peer's device id and fingerprint; check both against that machine before
   confirming. Pinning runs only at the machine, never from a remote device or an agent session.
3. After each machine, send one message each way with the machines already on, then do the next.

## Turn it off

- One machine: set `agentMessaging.remote` to `off`. It holds from the next message.
- The whole fleet: at any one machine run `agent-bridge-host agents lite kill` (L6; **Engineering to
  confirm**). It turns that machine off at once and tells every connected own machine; one that was offline
  turns off at its next lite exchange. A peer can only ever turn the mode off, never on.
- After a kill or switch-off, senders get `off` and use Home Assistant on their own, with no restart.
- Turning it back on takes a person at that machine. Nothing remote can.

## When it fails

Receiver refusals, in the order they are checked:

| Code | Meaning |
| --- | --- |
| `off` | the mode is off or killed on the target |
| `notSameAccount` | the sender is not a host of this account |
| `notPeer` | the sender is not pinned here, or its key does not match its device id |
| envelope codes | unsigned, bad signature, expired, from the future, another account, not the reply action, lacks the lite markers, or the text is not bound (each has its own code) |
| `replay` | this request was already seen |
| `notReplyTarget` | the target is not a session this host can type into |
| `busy` | the target is not idle, or the duplicate memory is full |
| `composerNotEmpty` | someone has typed in the target's input box; nothing was typed |
| `noSuchSession` | the target session does not exist |

What the sender gets, and what the caller does:

| Outcome | Meaning | The caller |
| --- | --- | --- |
| `delivered` | typed and acknowledged | done |
| `refused` | the receiver sent a code above | `notReplyTarget` or `busy`: use Home Assistant; others: fix the cause, do not resend blindly |
| `undeliverable` | the machine is offline; nothing was queued | use Home Assistant |
| `unconfirmed` | no acknowledgement in 10 seconds, or the mode went off while waiting; it may or may not have arrived | use Home Assistant (the duplicate rule drops a repeat) |
| `off` | the mode is off, killed, or the peer is not pinned | use Home Assistant |

## Acceptance tests (tick before cut-over)

The Migration Lead pastes the fleet plan's list here; **Engineering to confirm** it matches.

- [ ] One message delivered between every pair of machines VM1 to VM4, both directions.
- [ ] Each machine rolled back to Home Assistant within a minute, then turned on again.
- [ ] The fleet kill switch run once; every sender fell back with no restart.

## Home Assistant retirement checklist (tick after the soak)

- [ ] Every fleet coordinator machine runs this mode with a passing one-message check each way.
- [ ] The kill switch and the fallback have each been exercised once.
- [ ] Allow-all targets are reachable (allow-all list built), or the owner accepts they stay on Home Assistant.
- [ ] The allow-all list, provenance line and rate limit are built before any rollout beyond the owner.
- [ ] No sender still writes the Home Assistant reply payload.
- [ ] A week has passed with no Home Assistant reply-path use in the audit log.
