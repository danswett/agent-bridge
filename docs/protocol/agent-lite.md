# Own-agents messaging (AP5-lite): runbook

A temporary mode (#728, owner decision of 14:06 PT on 2026-11-08) that lets the owner's agents on the owner's
own machines message each other host to host over the relay until the signed roster (#55) exists.
Legacy fleet messenger fallback is described below; the host does not perform it.
Since the owner's decision of Nov 19, 2026, 22:07 PT it is **on by default**
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

The signed roster does not gate own-account lite messaging or its local `fleet.find`/`fleet.list` routing: a fresh present host row carries `trust: "own-account"` (not roster verification), even with no pin or an unreadable pin and with `security.roster` off or observe. This trust is for messaging only, never launches, stop or remote manage; those retain their existing authorization gates. An explicit messaging off or kill refuses this routing as `off`. The actual send still requires the same account and a channel key matching the device id.

## The plain limits

### Queueable relay adapter (source-only, #3574)

The host-only `LiteDeliver` lane seam uses the existing slice-2b2b
`DeliveryLaneScheduler.Offer`/`Query` and dispatcher; it is **not enabled** in
`Program` or `LiteComposition`. The legacy live-path limits below remain unchanged.
Its contract follows [message-delivery](../message-delivery.md#lanes-and-key-path-slice-2):
a verified ordinary agent offer to a working session waits in the queueable lane,
without typing or holding ReplyService's claim/typing slot. The authenticated
sender, resolved target and captured launch/grant/process fence stay bound to it.
An ended ingress request does not revoke that authority; each dispatch rechecks it.

Without recoverable protected shared payload storage the outcome is `pending`,
not durable acceptance; this adapter never invents `queued` or `delivered`.
The receiver and sender preserve `pending`, `queued` and `unconfirmed` separately.
Same-ID signed reoffers query existing bound state, never enqueue again or bypass
channel, signature, body or target checks. Restarted handoffs remain unconfirmed
without new keys; memory-only pending payloads follow the kernel's restart drop.
The canonical frame and sequence come from the flushed pre-key handoff API.
Leading attachment tokens use `(at)` so journal normalization cannot restore `@`.
The lane normalizes the canonical body before escaping, including supplementary
format characters that could otherwise disappear and expose an attachment token.
Known node-addressed UUIDs use the authenticated sender/recorded full-target
binding for history and body-HMAC checks even after discovery loses the target.
This never resolves a new recipient, renews authority or admits another offer.
An optional positive-revocation predicate distinguishes an identified PID/start
stamp or account change from a temporarily false current fence. Proven revocation
flushes `dropped: revoked authority` before removing the item and notifying through
the existing notice seam. Busy, questions, holds and missing evidence stay pending.
This source cut adds no dispatcher, shared store, CLI/helper rollout or live probe.

- ID-first host selection ([#3607](https://github.com/danswett/agent-bridge/issues/3607)): `machine` is an exact present host device ID (32 hex digits; publishers use canonical lowercase and comparisons are exact). Names are display only; names, fragments and a name-reusing successor never retarget. Unknown or offline IDs refuse loudly with the ID, before signing or opening a channel; the host never retries or falls back to HA.
- Sender boundary: this cut acquires no session roster. The pipe still takes the receiver's exact session node, and the receiver retains its existing full-ID/node resolution and final-effect fences below. No role IDs or claims are introduced.
- Own machines and own agents only. A message from any other account is never accepted, whatever it carries.
- Same machine means local delivery through the same signed receiver checks (owner-approved 14:34 PT Nov 21, 2026). A target device id equal to the sending host's own id routes locally, even without relay presence for itself. The host passes the signed envelope through the same receiver, replay/rate limits and reply-target gates, bound to its own device key and current signed-in account; it never opens a remote channel to itself. Unknown other host IDs refuse loudly as `noSuchMachine`. A busy local target is refused as `busy`, not `offline`; queueing is a separate change.
- Every host signed in to the account and present on the relay is trusted; there is no pin, and no per-device approval or
  revocation by the signed roster until the roster lands.
- Text only, at most 4096 characters. Delivery uses the same typing routine the Home Assistant reply path
  uses, and text goes only into an idle session with its input box on screen (Copilot: empty; Claude: see `notReplyTarget` below, as its suggested prompt reads as words). Delivered text starts with one fixed
  prefix line naming the sending machine and session.
- Allow-all sessions: with explicit `remote: "own-account"`, a session receives only if this host launched it in allow-all mode and either the existing `agentMessaging.allowAllTargets` plus exact `agentMessaging.allowAllTargetSessions` list grants it, or the owner has enabled `agentMessaging.allowAllLaunchedTargets` on this machine. The new boolean switch defaults false, invalid/non-boolean is false, and it is read afresh per message. It is independent of the unchanged list path; display names and wildcards never grant access.
- Target: the full session id or the node id `agent_bridge_<16 lowercase hex>` the roster and `fleet.find` publish (#3399). A node id is used only when exactly one live session here maps to it and session discovery is not held (a held pass may miss a second one); none or several is refused as `noSuchSession`, and a hold as `sessionResolutionIncomplete` (the session may be live but not listed yet: retry), never guessed. A full session id is not affected by a hold. Gates, the allow-all list, rate and typing all use the resolved full id; the log keeps the target as sent.
- Which sessions receive: only sessions this host launched in forced-ask mode, or allow-all mode with the grants above. On `SessionStarted`, the host atomically saves session id, permission mode, pid and exact process start time to device-key-authenticated, same-user-only `lite-launches.bin` beside `config.json`. After discovery at host startup, only entries still matching the live session's pid and process start time count; dead or changed entries are pruned, also during serving. An unreadable, corrupt, tampered or oversized file loads empty (safe); a failed save grants no recorded target. Sessions started by hand never qualify, even if listed. A host restart alone no longer requires relaunching an otherwise live, recorded session.
- Rate: the receiver allows at most 6 messages a minute and 60 an hour from one sending session to one
  receiving session. The sender side allows 6 a minute per sender-receiver pair, in the messenger script (fleet #56), covering both the relay and Home Assistant routes. Beyond that is refused as `rate`, which is final.
- The duplicate memory covers the relay route only. The relay and Home Assistant keep separate in-memory stores, so after an `unconfirmed` relay send the Home Assistant fallback can deliver the same message twice. This is rare and a known limit of the first version; a shared store comes after the cut-over.
  The duplicate memory is in memory only, so a host restart inside the 2 minute window can also deliver one message twice.
- A remote-host device ID absent from presence fails at once as `refused: noSuchMachine`, naming the ID. Nothing is signed or queued, and identity refusals hold without HA fallback.
- "Delivered" means typed and acknowledged, not read.
- **Not in the first version; each is required before anyone beyond the owner uses it:** the signed roster, durable duplicate memory, and a full audit log (today delivery outcomes and each evaluated gate's decision and reason are logged without message text).
  The owner accepted these skips. Until the roster lands, anyone with access to the owner's relay account
   could message his sessions, and any message from a present own host is typed into a session there that is idle, has its input box on screen (empty for Copilot; see Claude under `notReplyTarget`), and is host-launched or allow-all (see the next limit).

## Receipt lookup for an unconfirmed send (design, not implemented)

When a send ends `unconfirmed` (no acknowledgement in 10 s, the channel lost, or the mode switched off while waiting) the sender
cannot say whether the text was typed, and `reply-session` prints "Delivery is uncertain; Receipt lookup is unavailable; do not
resend automatically". On one machine that was 190 of 1,384 sends in about twelve hours (14%). This section designs the lookup. It
changes no code and no wire format until its slices land.

**What already holds.** The envelope id is the request id. The receiver remembers `peer/request` for 150 s (in memory, at most 4,096
ids, a restart forgets them), and a same-id offer from the same peer never types a second time: it is refused `replay`. An envelope
lives 120 s. What is missing is an answer: `replay` does not say what the first attempt became, and the sender keeps no copy of what it sent.

**The design.**

1. **Receiver journal.** Beside the replay id the receiver records the first attempt's outcome and nothing else: `in-flight`, `delivered`
   (typed), `refused` with its code, or the lane's `pending`/`queued`/`unconfirmed`. The same 150 s and 4,096 bounds; no text, no name. A
   same-id offer from the same peer (the channel's static key, as today) is answered with that outcome as a `receipt`, still without typing.
2. **A probe that never types.** A lookup is a re-offer of the same sealed envelope carrying a probe marker inside the signed body (a new,
   versioned lite marker). A probe of an id the receiver has no record of is answered `unknown` and nothing is typed; without the marker
   an unseen id is a first offer and would deliver (a late resend, which this design does not do).
3. **Sender keeps the sealed envelope.** For a send that ends `unconfirmed`, the sender's host keeps the sealed bytes in memory until the
   envelope expires (at most 120 s, at most 64 entries, dropped on expiry or restart) and serves them to a new agent pipe op,
   `agent.receipt { request }`, which returns `delivered`, `refused:<code>`, `pending`, `queued`, `in-flight` (ask again shortly), or
   `unknown` (the target has no record, the id is too old, or the host restarted). It never returns more than that.
4. **Fleet tool.** After `unconfirmed`, `reply-session` asks `agent.receipt` up to three times, 5 s apart, and prints one of: delivered
   (confirmed by receipt), not delivered with the refusal code, or still unknown with the sentence it prints today. It never resends the text.

**Limits that stay.** The journal is memory only (a restart forgets it, so a lookup after one is `unknown`); beyond the envelope's 120 s
a lookup cannot be made; the Home Assistant fallback keeps its own separate memory, so a double delivery across the two routes remains
the known rare limit above; a lookup is rate-limited with the pair's other offers and refused when the mode is off or killed.

**Who can ask.** Only the peer that sent the request, on the channel that authenticates it. The reply carries the request id, a state and
a refusal code, so it exposes nothing about the target's sessions or text, and it grants no authority to type.

**Slices** (each at most 400 lines, tests first): (1) receiver journal and `receipt` answer, with the probe marker; (2) the sender's
sealed-envelope memory and `agent.receipt`; (3) `reply-session`.

**Decided (owner; recorded by the Chief on #3875).** (a) A **strict probe**: a lookup only reads the outcome and never types; an
unseen id answers `unknown`. Exactly-once resend (a re-offer that delivers an unseen id late) is not part of this design. (b) The
journal is **memory only, 150 s**: a restart forgets it, and a lookup after one is `unknown`.

## Turn it on (a person at each machine, in the agreed order)

1. Nothing to set for the mode itself: a signed-in host with no `agentMessaging.remote` key is `own-account`
   ([host-config](../host-config.md)). A machine that was killed or set to `"off"` needs a person there to set
   `agentMessaging.remote` to `own-account`; changes need no restart.
   On an owner-approved receiving machine, set `agentMessaging.remote` to `own-account` explicitly. For the existing list path, set `agentMessaging.allowAllTargets` to `true` and list exact session ids in `agentMessaging.allowAllTargetSessions`. Alternatively, have the person at that machine set `agentMessaging.allowAllLaunchedTargets` to `true` to reach all live allow-all launches recorded by this host without maintaining ids. The new switch defaults false and never admits hand-started sessions. Setting it back to `false` leaves only the existing master-plus-list grants; disabling both paths refuses all allow-all targets. Neither changes launch authority, trust or person-only prompt answers.
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
| `notReplyTarget` | the target is not a session this host can type into. Lite types into Copilot and Claude Code sessions only (**owner-approved CHANGE, ~15:20 PT Nov 22**: Claude was refused before); Codex and unknown agents stay refused, and the lane (`laneDelivery`) still takes Copilot targets only. A Claude session is held to the same gates, idle, rate, signed account and process-incarnation fences as Copilot, and the fence is asked again before the first key and before each Enter pressed again when Claude took the first as part of the paste (a lost grant, kill switch or account stops the keys); its input box is read as the replier reads it (on screen, no `/` or `!` command waiting). Claude shows a suggested prompt in an empty box that a screen read cannot tell from typed words, so only a box that is off screen or holds a command is `composerNotEmpty` for Claude |
| `busy` | the target is not idle (a working Claude too, unless `agentMessaging.claudeBusyQueue` is on, default off), or the duplicate memory is full |
| `composerNotEmpty` | someone has typed in the target's input box; nothing was typed |
| `noSuchSession` | the target session does not exist, or the target is a node id that no live session or more than one maps to, (discovery held is `sessionResolutionIncomplete`, below). A fresh Copilot session with no `events.jsonl` yet also fails closed at the typing fence (including a full id); retry after its first transcript event |
| `sessionResolutionIncomplete` | the target is a node id and session discovery is held, so a live session may not be listed yet. Nothing was typed; retry after a few seconds. Final for the sender's HA fallback, as `noSuchSession` is (docs/design/reply-deduplication.md; #1396) |

What the sender gets, and what the legacy fleet messenger does (report-only, outside this host cut).
These historical fallback rows describe external scripts, not a host fallback or permission to
resolve names. The ID-first refusal contract above takes precedence for identity failures.

| Outcome | The messenger |
| --- | --- |
| `delivered` | success; the only success |
| `refused` with `badMachine` or `noSuchMachine` | final identity failure naming the device ID; no name, fragment or HA fallback |
| `off` (including the mode turned off while the signing key was made; nothing sent), `undeliverable` (machine offline, nothing queued; a device id matches only the present host with that id, never a host name), `unconfirmed` (no acknowledgement in 10 seconds, or the mode went off while waiting) | falls back to Home Assistant once; after `unconfirmed` the first message may still have been typed, so the fallback can deliver it twice |
| `refused` with `busy` or `notReplyTarget` | falls back to Home Assistant, once, only when nothing was typed (`NoKeysSent`); never after Accepted or Unconfirmed |
| `refused` with `rate` | final failure shown to the sender; never sent over Home Assistant |
| any other `refused` (other account, replay, size, and so on) | final failure shown to the sender |

## Acceptance tests (tick before cut-over)

Run on staging, then production, with two approved machines first and the other two joining after.

- [ ] 12 ordered pairs of the four machines, 50 messages each: all delivered once and acknowledged within 30 seconds; fallbacks counted, and more than about 2 percent on a healthy relay fails.
- [ ] Relay duplicates: the same message id sent twice over the relay inside 2 minutes is delivered once; 20 races from two machines at once lose nothing and duplicate nothing on the relay route. Not tested for once-only across relay and Home Assistant (known limit above).
- [ ] Relay unreachable: messages arrive through Home Assistant. Target offline: a fast, clear failure.
- [ ] Busy target and non-empty input box: refused, nothing typed. Other account, absent host and wrong static key: refused, nothing typed.
- [ ] A fresh install has the mode on only when signed in with a present same-account peer; signed out it sends and receives nothing; after a kill it stays off across restarts; a malformed or unreadable setting is off. A 60 KB message is refused at the cap; a one-character message is delivered.
- [ ] The 7th message in a minute from one pair is refused as `rate` and not retried over Home Assistant; another pair is unaffected.
- [ ] A deliberate refusal case, a host-down fallback to Home Assistant, and a coordinator and a builder session on each machine each receive a message. A host restart keeps recorded forced-ask targets; a changed pid, reused pid with another start time, or corrupt launch file refuses them. Hand-started sessions are always `notReplyTarget`.
- [ ] Delivered text carries the prefix line. A live recorded allow-all session receives only when idle, explicit remote is on, and the existing master-plus-list grant or the owner-set all-launched switch is on. Restart preserves it; default/invalid/off preserves the list path. The receiver rechecks its kill switch, remote setting, grant, process generation and signed-in account (the envelope's account) on admission, after waiting for the shared typing slot and immediately before injection; remote off, a revoked grant, kill, or an account switch meanwhile, or no account at all (including during a same-host send), types nothing, and the account case is refused as `notSameAccount`.
- [ ] Fleet switch off: every messenger is back on Home Assistant within 60 seconds. Per-machine kill: that machine stops using the relay at once (it refuses with `off`) and messengers fall back to Home Assistant.

## Home Assistant retirement checklist (tick after the 24 hour soak of all four machines)

- [ ] Every fleet coordinator machine runs this mode with a passing one-message check each way.
- [ ] The fleet switch, the per-machine kill and the fallback have each been exercised once.
- [ ] Only owner-approved machines enable either allow-all path: the existing master plus exact ids, or the explicit owner-set all-launched messaging switch.
- [ ] No sender still writes the Home Assistant reply payload.
- [ ] A week has passed with no Home Assistant reply-path use.
- [ ] Before any rollout beyond the owner: the roster, durable duplicate memory and full audit log are built.
