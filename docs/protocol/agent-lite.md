# Own-agents messaging (AP5-lite): runbook

A temporary mode (#728, owner decision of 14:06 PT on 2026-11-08) that lets the owner's agents on the owner's
own machines message each other host to host over the relay, with Home Assistant as the automatic fallback,
until the signed roster (#55) exists. It is off by default and is removed or folded into the roster path
when that lands. It is built by slices L1 to L7 (#1349 to #1355, #1369, #1371). The own-peer command name is still to be confirmed by its slice.

## What it trusts, and what it does not

It trusts four things, all checked on the receiving machine: the relay's account authentication (the relay
forwards only within one account, and the channel names the peer's device id); the **pinned own-peer list**
(the machines a person at this machine chose to trust, each with its agent signing key); **signed envelopes**
(signature, 120 second life, text bound by hash, markers that can never verify in the roster path); and the
**agent credential** (AP1), which ties a message to one agent session on the sender.

It does **not** defend against a dishonest relay or a stolen relay-account login. There is no roster, so
nothing records which device a person approved or revoked. That is why the mode is temporary and
own-agents-only.

## The plain limits

- Own machines and own agents only. A message from any other account is never accepted, whatever it carries.
- Every pinned machine is trusted until a person removes it at that machine. No per-device approval or
  revocation by the signed roster until the roster lands.
- Text only, at most 4096 characters. Delivery uses the same typing routine the Home Assistant reply path
  uses, and text goes only into an idle session with an empty input box. Delivered text starts with one fixed
  prefix line naming the sending machine and session.
- allow-all behaviour: pending owner decision
- Rate: the receiver allows at most 6 messages a minute and 60 an hour from one sending session to one
  receiving session. The sender side allows 6 a minute per sender-receiver pair, in the messenger script (fleet #56), covering both the relay and Home Assistant routes. Beyond that is refused as `rate`, which is final.
- The duplicate memory covers the relay route only. The relay and Home Assistant keep separate in-memory stores, so after an `unconfirmed` relay send the Home Assistant fallback can deliver the same message twice. This is rare and a known limit of the first version; a shared store comes after the cut-over.
  The duplicate memory is in memory only, so a host restart inside the 2 minute window can also deliver one message twice.
- A message to an offline machine fails at once and falls back to Home Assistant. Nothing is queued.
- "Delivered" means typed and acknowledged, not read.
- **Not in the first version; each is required before anyone beyond the owner uses it:** the signed roster, a
  named allow-all list (L5b), durable duplicate memory, a full audit log (today there is one delivery log
  line per message), and pushing the kill to connected hosts (it follows the cut-over).
  The owner accepted these skips. Until the roster lands, anyone with access to the owner's relay account
  could message his sessions, and any message from a pinned own machine is typed into any session there.

## Turn it on (a person at each machine, in this order: VM3, VM1, VM4, VM2)

1. Set `agentMessaging.remote` to `own-account` ([host-config](../host-config.md)); changes need no restart.
   For `agentMessaging.allowAllTargets`, see the allow-all line under the limits.
2. Pin the other machines with the local own-peer command (L2b, #1369; expected
   `agent-bridge-host agents lite peer add|list|remove`, **to be confirmed by the L2b PR** before merge). It shows each peer's device id and fingerprint; check both against that machine before
   confirming. Pinning runs only at the machine, never from a remote device or an agent session.
3. After each machine, send one message each way with the machines already on, then do the next.

## Turn it off

1. Fleet: set the repo variable `FLEET_RELAY_MSG` to `off` in the private fleet repository. The messenger script (fleet issues #36 and #56) reads it at least every 30 seconds, so messengers are back on Home Assistant within about
   a minute. This alone restores messaging.
2. Then on each machine run `agent-bridge-host agents lite kill` (L6). It turns that
   machine off at once, and a higher kill number seen from a pinned peer turns another host off at its next
   exchange. The kill is not pushed to connected hosts in this version, so a killed machine's peers do not
   learn of it until then; run it on every machine.
3. One machine only: set `agentMessaging.remote` to `off`; it holds from the next message.
4. Turning back on takes a person at that machine. Nothing remote can.

**Roll back at once** on any one of: more than 1 percent of messages late or lost; p99 delivery over 250 ms for
10 minutes; the relay down for more than 2 minutes; or any message delivered to the wrong session. Then tell the
owner which one tripped.

## When it fails

Receiver refusals, in the order they are checked:

| Code | Meaning |
| --- | --- |
| `off` | the mode is off or killed on the target |
| `notSameAccount` | the sender is not a host of this account |
| `notPeer` | the sender is not pinned here, or its key does not match its device id |
| envelope codes | unsigned, bad signature, expired, from the future, another account, not the reply action, lacks the lite markers, or the text is not bound or too long (each has its own code) |
| `replay` | this request was already seen |
| `rate` | the pair went over its limit |
| `notReplyTarget` | the target is not a session this host can type into |
| `busy` | the target is not idle, or the duplicate memory is full |
| `composerNotEmpty` | someone has typed in the target's input box; nothing was typed |
| `noSuchSession` | the target session does not exist |

What the sender gets, and what the messenger does:

| Outcome | The messenger |
| --- | --- |
| `delivered` | success; the only success |
| `off`, `undeliverable` (machine offline, nothing queued), `unconfirmed` (no acknowledgement in 10 seconds, or the mode went off while waiting) | falls back to Home Assistant once; after `unconfirmed` the first message may still have been typed, so the fallback can deliver it twice |
| `refused` with `busy` or `notReplyTarget` | falls back to Home Assistant |
| `refused` with `rate` | final failure shown to the sender; never sent over Home Assistant |
| any other `refused` (other account, unpinned peer, replay, size, and so on) | final failure shown to the sender |

## Acceptance tests (tick before cut-over)

Run on staging, then production, with VM3 and VM1 first and VM4 and VM2 joining after.

- [ ] 12 ordered pairs of the four machines, 50 messages each: all delivered once and acknowledged within 30 seconds; fallbacks counted, and more than about 2 percent on a healthy relay fails.
- [ ] Relay duplicates: the same message id sent twice over the relay inside 2 minutes is delivered once; 20 races from two machines at once lose nothing and duplicate nothing on the relay route. Not tested for once-only across relay and Home Assistant (known limit above).
- [ ] Relay unreachable: messages arrive through Home Assistant. Target offline: a fast, clear failure.
- [ ] Busy target and non-empty input box: refused, nothing typed. Other account and unpinned peer: refused, nothing typed.
- [ ] A fresh install has the mode off. A 60 KB message is refused at the cap; a one-character message is delivered.
- [ ] The 7th message in a minute from one pair is refused as `rate` and not retried over Home Assistant; another pair is unaffected.
- [ ] A deliberate refusal case, a host-down fallback to Home Assistant, and a coordinator and a builder session on each machine each receive a message.
- [ ] Delivered text carries the prefix line. Allow-all sessions: pending owner decision.
- [ ] Fleet switch off: every messenger is back on Home Assistant within 60 seconds. Per-machine kill: that machine stops sending and receiving at once.

## Home Assistant retirement checklist (tick after the 24 hour soak of all four machines)

- [ ] Every fleet coordinator machine runs this mode with a passing one-message check each way.
- [ ] The fleet switch, the per-machine kill and the fallback have each been exercised once.
- [ ] Allow-all targets: pending owner decision.
- [ ] No sender still writes the Home Assistant reply payload.
- [ ] A week has passed with no Home Assistant reply-path use.
- [ ] Before any rollout beyond the owner: the roster, allow-all list, durable duplicate memory, full audit log and kill push are built.
