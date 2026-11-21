# Own-agents messaging (AP5-lite): runbook

A temporary mode (#728, owner decision of 14:06 PT on 2026-11-08) that lets the owner's agents on the owner's
own machines message each other host to host over the relay, with Home Assistant as the automatic fallback,
until the signed roster (#55) exists. Since the owner's decision of Nov 19, 2026, 22:07 PT it is **on by default**
for a host signed in to the account: with no `agentMessaging.remote` key it is `own-account`, and it does anything
only with a present same-account peer, by the trust rule below. A host with no signed-in account sends and receives
nothing. An explicit `"off"`, an unreadable setting or a kill keeps it off; the kill switch stays. It is removed or
folded into the roster path when that lands. It is built by slices L1 to L7 (#1349 to #1355, #1369, #1371).

## What it trusts, and what it does not

It trusts four things, all checked on the receiving machine: the relay's account authentication (the relay
forwards only within one account, and the channel names the peer's device id); **relay presence** (the sender
must be a host the relay lists as present on this signed-in account, on a channel its device static key
authenticates; no pin, owner decision of 2026-11-19; the sender's agent signing key arrives on that channel); **signed envelopes**
(signature, 120 second life, text bound by hash, markers that can never verify in the roster path); and the
**agent credential** (AP1), which ties a message to one agent session on the sender.

It does **not** defend against a dishonest relay or a stolen relay-account login. There is no roster, so
nothing records which device a person approved or revoked. That is why the mode is temporary and
own-agents-only.

## The plain limits

- Own machines and own agents only. A message from any other account is never accepted, whatever it carries.
- Every host signed in to the account and present on the relay is trusted; there is no pin, and no per-device approval or
  revocation by the signed roster until the roster lands.
- Text only, at most 4096 characters. Delivery uses the same typing routine the Home Assistant reply path
  uses, and text goes only into an idle session with an empty input box. Delivered text starts with one fixed
  prefix line naming the sending machine and session.
- Allow-all sessions: `agentMessaging.allowAllTargets` is the per-machine master switch, on only for the cut-over machines and off everywhere else. Even when it is on, a session receives only if its exact session id is in `agentMessaging.allowAllTargetSessions` and this host launched it in allow-all mode since its last restart (the launch record is in memory). A missing or empty list denies every allow-all session; display names and wildcards never grant access.
- Target: the full session id or the node id `agent_bridge_<16 lowercase hex>` the roster and `fleet.find` publish (#3399). A node id is used only when exactly one live session here maps to it and session discovery is not held (a held pass may miss a second one); none, several or a hold is refused as `noSuchSession`, never guessed. A full session id is not affected by a hold. Gates, the allow-all list, rate and typing all use the resolved full id; the log keeps the target as sent.
- Which sessions receive: the relay types only into sessions this host launched since its last restart in forced-ask mode, or in allow-all mode with both the master switch and an exact listed session id. A session started by hand or launched before the host's last restart, even if listed, is refused as `notReplyTarget` and falls back to Home Assistant until relaunched by this host.
- Rate: the receiver allows at most 6 messages a minute and 60 an hour from one sending session to one
  receiving session. The sender side allows 6 a minute per sender-receiver pair, in the messenger script (fleet #56), covering both the relay and Home Assistant routes. Beyond that is refused as `rate`, which is final.
- The duplicate memory covers the relay route only. The relay and Home Assistant keep separate in-memory stores, so after an `unconfirmed` relay send the Home Assistant fallback can deliver the same message twice. This is rare and a known limit of the first version; a shared store comes after the cut-over.
  The duplicate memory is in memory only, so a host restart inside the 2 minute window can also deliver one message twice.
- A message to an offline machine fails at once and falls back to Home Assistant. Nothing is queued.
- "Delivered" means typed and acknowledged, not read.
- **Not in the first version; each is required before anyone beyond the owner uses it:** the signed roster, durable duplicate memory, and a full audit log (today delivery outcomes and each evaluated gate's decision and reason are logged without message text).
  The owner accepted these skips. Until the roster lands, anyone with access to the owner's relay account
   could message his sessions, and any message from a present own host is typed into a session there that is idle, has an empty input box, and is host-launched or allow-all (see the next limit).

## Turn it on (a person at each machine, in the agreed order)

1. Nothing to set for the mode itself: a signed-in host with no `agentMessaging.remote` key is `own-account`
   ([host-config](../host-config.md)). A machine that was killed or set to `"off"` needs a person there to set
   `agentMessaging.remote` to `own-account`; changes need no restart.
   On the four approved coordinator machines only, also set `agentMessaging.remote` to `own-account` explicitly and `agentMessaging.allowAllTargets` to `true` (the default never opens allow-all); leave it off on every other machine. On each receiving machine, list the exact session ids approved for allow-all delivery in `agentMessaging.allowAllTargetSessions`. An empty or missing list denies all; a display name or wildcard does not grant access. Setting the master switch back to `false` refuses all allow-all targets.
2. Nothing to pin: every other host signed in to the same account and present on the relay is a peer. The legacy
   `peers` commands and `own-peers.json` no longer decide who may send or kill.
3. After each machine, send one message each way with the machines already on, then do the next.

## Turn it off

1. Fleet: set the repo variable `FLEET_RELAY_MSG` to `off` in the private fleet repository. The messenger script (fleet issues #36 and #56) reads it at least every 30 seconds, so messengers are back on Home Assistant within about
   a minute. This alone restores messaging.
2. Then run `agent-bridge-host agents lite kill [--profile <name>]` (L6) at a machine's own command line; it is
   refused inside an agent session. It turns that machine off at once: it saves a new kill epoch, sets
   `agentMessaging.remote` to `off` and cancels the sends in flight there (they end `unconfirmed`). If the setting
   cannot be written (another part of `config.json` is invalid), the command exits 1 and the kill record holds the
   machine off, across restarts, until a start can write the setting off. Within about 5 seconds the running host
   tells each host the relay lists as present on this account, which turns off too; it retries a host that
   answers with an error or has not taken the epoch. A kill is believed only from such a host whose channel key
   matches its device id (no pin), through the push, the epoch on a lite send, or an ack; it only ever turns the
   mode off. A machine offline at the kill learns it at its next lite send, so run the kill there as well.
3. One machine only: set `agentMessaging.remote` to `off`; it holds from the next message, and the default never
   overrides an explicit `"off"`.
4. Turning back on takes a person at that machine setting `agentMessaging.remote` to `own-account`. Nothing remote
   can, and after a recorded kill removing the key (`config unset`) does not either.

**Roll back at once** on any one of: more than 1 percent of messages late or lost; p99 delivery over 250 ms for
10 minutes; the relay down for more than 2 minutes; or any message delivered to the wrong session. Then tell the
owner which one tripped.

## When it fails

Receiver refusals, in the order they are checked:

| Code | Meaning |
| --- | --- |
| `off` | the mode is off or killed on the target, or the request carried a newer kill epoch |
| `notSameAccount` | the sender is not a host the relay lists as present on this signed-in account |
| `notPeer` | the channel's static key does not hash to the sender's device id |
| `malformed`, envelope codes | no `signPoint` (the sender's agent signing key); unsigned, bad signature, expired, from the future, another account, not the reply action, lacks the lite markers, or the text is not bound or too long (each has its own code) |
| `replay` | this request was already seen |
| `rate` | the pair went over its limit |
| `notReplyTarget` | the target is not a session this host can type into |
| `busy` | the target is not idle, or the duplicate memory is full |
| `composerNotEmpty` | someone has typed in the target's input box; nothing was typed |
| `noSuchSession` | the target session does not exist, or the target is a node id that no live session or more than one maps to, or discovery is held |

What the sender gets, and what the messenger does:

| Outcome | The messenger |
| --- | --- |
| `delivered` | success; the only success |
| `off` (including the mode turned off while the signing key was made; nothing sent), `undeliverable` (machine offline, nothing queued; a device id matches only the present host with that id, never a host name), `unconfirmed` (no acknowledgement in 10 seconds, or the mode went off while waiting) | falls back to Home Assistant once; after `unconfirmed` the first message may still have been typed, so the fallback can deliver it twice |
| `refused` with `busy` or `notReplyTarget` | falls back to Home Assistant |
| `refused` with `rate` | final failure shown to the sender; never sent over Home Assistant |
| any other `refused` (other account, `ambiguousMachine` (a name shared by two present hosts), replay, size, and so on) | final failure shown to the sender |

## Acceptance tests (tick before cut-over)

Run on staging, then production, with two approved machines first and the other two joining after.

- [ ] 12 ordered pairs of the four machines, 50 messages each: all delivered once and acknowledged within 30 seconds; fallbacks counted, and more than about 2 percent on a healthy relay fails.
- [ ] Relay duplicates: the same message id sent twice over the relay inside 2 minutes is delivered once; 20 races from two machines at once lose nothing and duplicate nothing on the relay route. Not tested for once-only across relay and Home Assistant (known limit above).
- [ ] Relay unreachable: messages arrive through Home Assistant. Target offline: a fast, clear failure.
- [ ] Busy target and non-empty input box: refused, nothing typed. Other account, absent host and wrong static key: refused, nothing typed.
- [ ] A fresh install has the mode on only when signed in with a present same-account peer; signed out it sends and receives nothing; after a kill it stays off across restarts; a malformed or unreadable setting is off. A 60 KB message is refused at the cap; a one-character message is delivered.
- [ ] The 7th message in a minute from one pair is refused as `rate` and not retried over Home Assistant; another pair is unaffected.
- [ ] A deliberate refusal case, a host-down fallback to Home Assistant, and a coordinator and a builder session on each machine each receive a message. Receiving sessions must have been launched by this host since its last restart in forced-ask mode, or allow-all mode with the switch on and exact id listed; listed sessions started by hand or before a host restart are refused as `notReplyTarget`.
- [ ] Delivered text carries the prefix line. An allow-all session receives only when idle, the master switch is on, its exact id is listed, and this host launched it in allow-all mode since its last restart; unlisted, hand-started or pre-restart sessions are refused as `notReplyTarget`.
- [ ] Fleet switch off: every messenger is back on Home Assistant within 60 seconds. Per-machine kill: that machine stops using the relay at once (it refuses with `off`) and messengers fall back to Home Assistant.

## Home Assistant retirement checklist (tick after the 24 hour soak of all four machines)

- [ ] Every fleet coordinator machine runs this mode with a passing one-message check each way.
- [ ] The fleet switch, the per-machine kill and the fallback have each been exercised once.
- [ ] `allowAllTargets` is true on the four approved machines only, and false on all other machines; each enabled machine lists only the exact allow-all session ids it approves.
- [ ] No sender still writes the Home Assistant reply payload.
- [ ] A week has passed with no Home Assistant reply-path use.
- [ ] Before any rollout beyond the owner: the roster, durable duplicate memory and full audit log are built.
