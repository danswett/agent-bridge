# Message delivery: envelope, lanes and journal (#2568)
Status: slice 3a standalone priority journal core and Phase 1 S3 source-only dispatch pump; delivery integration/live wiring held, 2026-11-22 PT. Inputs: #2568
rulings 6867525734, 6867631192, 6867667571, 6867669427; owner evidence 6867540072. **Change**: a departure, with ID-first addressing approved below.
## Owner decisions cited
[Owner decisions](https://github.com/danswett/slipgates-fleet/blob/main/docs/owner-decisions.md#nov-19), Nov 19, 2026 PT:
- **12:10:** "It should use GUIDs not names to message. Friendly name should be display only."
- **12:12:** "Perhaps you need two GUIDs: one for the unique session and one for its role (SRE, Chief, etc). That lets you not rely on friendly name which could easily misfire."
- **12:27:** the recorded decision says "The role-ID design is approved (12:27)." Person-signed assignments and per-successor approval are detailed in [#3334](https://github.com/danswett/agent-bridge/pull/3334) and [role-ids.md](design/role-ids.md).
**Nov 21, 2026, 17:05 PT:** the owner re-approved ID-first addressing, per Chief S9 relay to Engineering R6 / brief 313; not yet recorded on main's owner-decisions.md at this edit. This is relayed provenance, not a direct owner quote. Role-ID code remains paused (Nov 19, 17:58 PT); this doc claims no built role routing.
## Phase 1 path: owner decisions, Nov 22, 2026, 14:13 and 14:14 PT
Relayed by Engineering R7 from the recheck plan; not yet in owner-decisions.md. Building these slices is approved; enabling is not.
- **Flag:** `agentMessaging.laneDelivery`, default off in code, read afresh and fail closed (see host-config.md), and always ANDed with `liteKill.On`. Slice S1 adds the setting and nothing reads it yet.
- **Slices:** S1 flag, S2 composition behind the flag, S3 dispatch pump, S4 Lite wiring, S5 notices, S6 restart and shutdown. Each is at most 400 WHOLE and sensitive (Sol and Sonnet through Quality).
- **No agent urgent:** with the flag on, agent-sent urgent stays refused. A separate later slice and owner go would be needed.
- **No S8:** the disposable-VM acceptance slice is dropped. Once S1-S6 land through their gates, `laneDelivery` is enabled on the fleet directly. That enabling is a separate act; the default stays off in code. Load and stress tests still never run on live machines.
- **Priority, Option A:** authority lives in memory per host run. The journal is audit and subtract only. A restart voids every capability and the person re-issues it. Slice 3a is respun to this after #3721 lands.
- **Journal cannot open:** with the flag on, delivery refuses and fails closed. There is no legacy idle-only typing fallback.
## Inactive qualification source (A-ii-1)
The source-only producer remains unwired: no launch, reply, submit planner, hooks
verification or API invokes it. The later authorized controller/SRE control entry
(A-ii-2) and submit/delivery wiring (B) remain held. No live qualification was run.
`WorkerInjector.CaptureIdentityAsync` uses a read-only worker handshake with the
target process handle held and its PID/creation FILETIME checked. Its snapshot
contains target and injector image paths, file versions/SHA-256, console window
and owner-process incarnation, input mode and key-plan protocol. Missing evidence stays Unknown.
`QueueQualification` captures that snapshot at launch through its host-only seam.
A controller must supply a cursor-bound raw transcript reader for that same target
and an explicit challenge lifetime. Only the exact host-generated, one-shot nonce
in a main-target raw `user.message` with `data.delivery="queued"` while working,
before monotonic expiry and with unchanged identity before/after reading, yields
Verified. Replay, another host/target, stripped delivery, key-write success and
hooks/provider confirmation cannot qualify. No caller supplies a Verified flag.
The older design below does not authorize an idle-Enter fallback or capability table.

## Inactive qualification authority (A-ii-2a)
The host-only grant kernel accepts one approval id once, bound to exact SHA,
disposable VM/run/host identities, the complete `QueueTarget` snapshot and the
exact `QueueQualification` challenge nonce, for at most 60 minutes. Only trusted
owner approval or SRE may issue; only SRE CLI on that disposable run may consume by CAS.
Every target field and binding id must be present and well formed, or issue refuses. A nonce is
issued once: any journaled grant holding it, in any state and after restart, refuses a new approval id.
Consume and current-grant checks require that same target and nonce; a second consume or any
different target/challenge refuses. Expiry is journaled when first observed, so a wall-clock
rollback cannot revive it, including consumed replays and mismatched current-grant checks.
The journal durably advances a time high-water before checks; issue, consume, current and revoke
refuse an earlier clock even after restart, and every append atomically fences against that high-water.
Revocation requires SRE CLI with the grant's own binding and retries a competing transition's CAS.
Journal replay preserves binding, state and time high-water, not challenge object references.
Every transition requires an atomic append and durable flush through the Fleet
journal adapter; revocation never erases history. Missing adapters refuse.
This slice supplies only fake adapters in tests: no CLI verb, owner approval UI,
Fleet store writer, qualification keys or live run is enabled.

## Inactive qualification controller (A-ii-2b)
`qualification.start` has a source-only core requiring the grant id and host-owned
approved target/challenge; registration and trusted adapters remain absent, hence
default refusal. Each consumed grant permits that nonce once and at most one minute
of qualification/receipt validity, bounded by the grant. Final-effect fencing,
raw queued correlation and current
identity are mandatory; ambiguity stays Unknown. Explicit unsupported transport
is a negative result, never permission to fall back. Receipt fields contain only
the SHA, hashed bindings/nonce/snapshot, verdict, expiry and content hash, appended
through the durable Fleet boundary. Revocation, expiry or identity change removes
current capability; teardown/failure revokes. No host starts or live keys ran.
Consumers must ask the controller for current capability; a stored receipt alone
never authorizes delivery, and restart defaults to Unknown.

## Problem and cause
Copilot CLI joins steers pending mid-turn with `\n\n` into one user turn (6 of 692 turns in one transcript). The host types bare text (`HaReplies.cs:26`) and only Lite and `RetryEpisode` set
`RequireIdle` (`ReplyService.cs:422`), so replies land in a working session with no sender boundary
and only the agent's clock. Fix without CLI changes: self-describing deliveries, and no steered fyi.
## Overlap and dependencies, named before any code
- **PR 2546 (#2538, merged)** records the HA sender principal (`PrincipalOf`); reused. Rendering stays there.
- **Host-typing hotfix** (agent text via ctrl+q, default off: `KeyPlan.cs`, `CopilotReply.cs`, `Injector/`),
  then **PR 2469** (#2468, strict `queue|steer` mode, `Protocol/ReplyMode.cs`), then slice 2.
- **[#2574](https://github.com/danswett/agent-bridge/issues/2574):** sends target a SESSION ID or ROLE ID; names are display only and never route.
  A ROLE ID reaches its ONE verified live holder; none or two is refused. A stale or unknown ID fails loudly and holds: NO name, HA or substring fallback.
  Role claims and succession require person-signed approval per [#3334](https://github.com/danswett/agent-bridge/pull/3334) / [role-ids.md](design/role-ids.md); role-ID code stays paused, not built.
- **[#3607](https://github.com/danswett/agent-bridge/issues/3607):** sends and lookups target a live session ID; names are display only. A stale or unknown ID refuses loudly, naming the ID, and holds without name, substring or HA fallback.
  This supersedes #2574's name lookup, per the owner decisions of Nov 19, 2026, 12:10/12:12 PT and the [ID-first design](https://github.com/danswett/agent-bridge/pull/3606). Role-ID code and claims remain paused (#3334).
- **Protected shared store** (own dependency, in no slice estimate): partition identity, lease, fencing, CAS,
  migration. It holds protected queued payloads and cross-node state; if missing, hold (no local fallback).
  **Locality:** the host journal is node-bound injector evidence, not the cross-node payload, outbox or lease
  ledger. Host lost: its unresolved items stay `unconfirmed`; no node infers non-delivery or replays keys.
## Envelope (slice 1)
`[ab:from=<sender>|id=<id>|seq=<lane><n>|at=<time>|pri=<urgent|normal|fyi>|n=<chars>] <text>`, one line. No target field: delivery is to the session the host already resolved by ID.
- **One formatter and parser** in `AgentBridge.Protocol` (`DeliveryEnvelope`), shared by host and app,
  called by `ReplyService` and `LiteDeliver.Frame`. The formatter adds `id` (HA `rid` or request UUID).
- **`from`** displays the authenticated sender class, never a routing address: `owner` (his HA user or paired device), `system`
  (the host), or agent (agent token or Lite principal). **Owner-approved CHANGE, 14:49 PT Nov 21:**
  `from` carries `owner:<opaque-id>` or `agent:<opaque-id>` (id percent-encoded), optionally followed
  by ` (<display>)`. Display is `[A-Za-z0-9 ._-]{1,48}` only; anything else shows the id alone.
  Slice 1 carries the authenticated HA context user id into host-only `ReplyRequest.Sender`;
  unknown is `agent:unknown`. Payload sender/driver labels never supply this identity or intent.
  Sender and target IDs belong in journal/receipt records, not routing from envelope or body text; receipt names are display labels only. Envelope `id` is the delivery request ID, not a sender or target ID.
- **`at`** is the host's receipt time, Pacific, to the second with offset; it never changes, and any
  clock in the body is ignored. The time typed goes to the journal.
- **`seq`** is per session and lane (`u7`, `q12`), reserved by the flushed pre-key `handoff` record.
  **Change** (ruling: at enqueue): priority and drops reorder the queue; a crash can skip a number.
- **Escape:** the text typed is NFKC with format characters removed; any `[` before text that
  is `ab:` (any case) becomes `(`. **Owner-approved CHANGE, 14:49 PT Nov 21:** full UTS 39
  skeleton matching is deferred to [#3583](https://github.com/danswett/agent-bridge/issues/3583);
  no look-alike list is substituted.
  **`n`** counts UTF-16 units: `héllo 👍` is `n=8`.
- **Parser:** a header counts only at the start of a turn or right after `\n\n`. Its body is the next
  `n` units (the host types one collapsed line, so `n` is known). The rest, up to the next header, is
  `from=console` text kept verbatim, blank lines included: words typed at the machine, which the
  host never sees. **Change:** the ruling labels those `owner`; the host cannot see who typed them.
  If `n` does not match, the body runs to the next `\n\n` and the entry is flagged.
- **Trust:** a header is verified only if it matches this session's journal `handoff` (id, from, seq, at, n and body HMAC); the host checks, the app shows the verdict. Anything else, a pasted header
  included, stays `console`, unverified, and never confirms, dedupes or attributes a delivery.
  Body HMAC encoding uses strict UTF-8: invalid UTF-16 is refused, never replaced with U+FFFD during verification.
## Lanes and key path (slice 2)
Slice 2 is split: **2a** is the inert host-only lane scheduler, overflow notices and journal-backed
sequence reservation/high-water recovery; **2b1** supplies submit keys (`none`, `Enter`, `Queue`,
ctrl+q VK `Q` left-ctrl) and the per-terminal capability table; **2b2** owns production
offer/query by UUID, final fenced/flushed handoff, truthful states and restart classification/replay.
Engineering split 2b2 into P2-only **2b2a** and the host-only adapter **2b2b**, each <=400 WHOLE.
2b2b adds no entry point or second scheduler: trusted ingress must resolve the live target and sender,
then call `Offer(UUID, target, DeliverySender, body, capturedFence)` / `Query(UUID, target, sender)`.
Set `Dispatch` to ReplyService's callback on that same scheduler/gate; `DeliverNextAsync` holds
only lifecycle admission, not ReplyService's claim/typing slot. Its host-only `HandoffText` calls
`ReserveHandoff` in the final fenced typing path and returns the canonical flushed header/frame;
flush failure sends zero keys. Owner urgent may steer a known working empty box; agents wait idle.
The host owns memory-only pending payloads; no shared-store adapter means never `Queued`.
Node loss drops never-handed-off pending metadata; prior handoff stays `Unconfirmed`, never resubmitted.
Known UUIDs bind sender class/id, target and canonical body; re-offers neither renew authority nor
promote `Typed`/uncertainty. Post-key journal failure atomically freezes further admission.
The source-only [relay P2 follow-up](https://github.com/danswett/agent-bridge/issues/3717)
adds an optional explicit `Revoked` predicate: `Current=false` alone never drops.
Only positive incarnation (PID/start stamp), account change or explicit revocation
evidence permits a flushed `dropped: revoked authority` and notice before progress
past the stale item. Missing evidence stays pending. Authenticated node-history
queries retain the UUID's recorded full target without new routing or admission.
State stays node-bound injector evidence; protected cross-node recoverable payloads remain held.
The dispatcher must not hold ReplyService's shared claim/typing slot across its callback.
Phase 1's source-only dispatch pump wakes on every Offered event, filtered on lane.HasPending, and on session/idle changes, with one
flight and one 250 ms-to-2 s capped retry timer per pending session; drained sessions have no timer.
It refreshes the bounded-age screen before dispatch and retains the existing shared gate and fences.
An Unconfirmed result latches that session until host restart only; removal, re-offer, flag toggles
and pump recreation never clear the latch. Journal/dispatch uncertainty (notice.JournalUncertain or any
pump exception) stops the whole pump. A second pump on one ISessionSource throws: the host owns one.
Removing a session cancels its timers and drops its pending state.
BeginStop cancels retries and new wakes, never an admitted injector; S6 owns its shutdown wiring.
The laneDelivery flag stays default off; this adds no agent urgent or cross-node queued acceptance.
No 2a/2b1 production scheduler entry or live capability probe exists. All table entries remain
Unknown: queueable working sessions wait; positively idle/empty sessions may use Enter.
Worker protocol v2 refuses older key-plan headers; an older worker refuses v2 before any keys.
- **Urgent lane:** owner and system; agent-urgent from slice 3. Text, then Enter (steer), one at a time
  via `DeliveryGate`, by class (owner, system, agent), then arrival. Bound 10 per session: an owner item
  always enters, moving the newest agent (then system) urgent to the queueable lane, journaled and the
  sender told; an 11th owner item, or any other urgent at the bound, is refused, naming the target ID.
- Urgent (owner/system) bypasses `RequireIdle`: session state must be positively known (working or idle)
  AND the input box positively known EMPTY. Unknown state, unknown box or half-typed words = wait, no keys.
  Queueable never bypasses idle. A displaced urgent is a new queueable arrival: evict the oldest fyi;
  if 50 normals leave no fyi, drop the displaced item (`dropped`, reason `overflow`), sender told by a
  `from=system` drop notice naming the target ID (rule 3). Never hold it outside the 10/50 bounds.
- **Every lane** types only into an empty box; half-typed words at the machine make the item wait.
- **Queueable lane:** everything else, FIFO per session: envelope text, then ctrl+q, so the CLI queues it
  as its own turn. The host hands the CLI at most 2 and never sends ctrl+c, esc esc or Up. `KeySend.Enter`
  becomes a submit key (none, Enter, Queue); ctrl+q is a console key record (VK `Q`, left-ctrl).
- **Capability:** per terminal kind, default not capable. Not capable or unknown means fail closed: wait for idle, then Enter; queueable text never steers a working session (urgent does, by ruling).
  **Change:** no live probe on owner machines; a reviewable per-kind table filled by live test 4.
- `RequireIdle` is the default except on the urgent lane; nothing from an agent is urgent until slice 3.
  The HA ack gains `queued` and `pending` (rule 3); `reply-session.ps1` maps both to exit 6 (today: refused).
## Priority authority (slice 3) and hygiene (slice 4), as ruled
- **Priority:** `-Priority urgent -Reason '<text>'`; no reason is refused, naming the target ID; over 3 per principal
  per hour is demoted to normal (owner: no quota). Requests, grants and demotions are journaled and shown
  on the status surface, never paged; the ack says which. **Known limit:** every HA agent shares one
  token, so the quota is per token until host-signed principals exist (follow-up to be filed).
Slice 3a defines host-only `DeliveryPriorityRequest` wire fields `priority` (`normal|fyi|urgent`)
and `reason` (required for urgent, <=256 characters), and the standalone journal decision kernel.
An agent cannot self-grant: trusted ingress must first authenticate a person's issue and capture
the exact `GrantAudience` device/profile and agent principal. `IssuePriority`/`RevokePriority` are
internal trusted-person seams only; the compatible `GrantIssuer` is already authenticated, not a payload claim.
The separate `PriorityCapability` lives in the existing node-bound DeliveryJournal, not the fleet
GrantStore: it conveys no read/reply/answer/trust authority and is not a fleet launch/stop grant.
Request, issue, grant, consumption, refusal, demotion and revocation are flushed/MACed journal facts.
Priority facts require Priority state, MAC and previous-MAC verification. Relabeled or stripped policy
facts refuse their verified principal/receiver chain: no current authority, replacement capability or quota refund.
Unauthenticated linked/ambiguous facts quarantine all priority bindings and replay Unconfirmed; only MAC-covered fields prove attribution.
Zero-body legacy requires its MAC; unverifiable nonempty history is Unconfirmed/inert only when no priority record exists anywhere;
otherwise all bindings are quarantined regardless of record position. Unrelated handoffs stay Unconfirmed and need body-MAC evidence.
Known pre-activation limitation: genuine nonempty history in a journal with any priority record permanently quarantines on restart; whole-design review must resolve this with a body-free authenticated-history design ([#3720](https://github.com/danswett/agent-bridge/issues/3720)).
no missing-body guess, new replay-body resolver, UI, live adapter or migration is introduced.
Policy facts link their previous MAC; whole-file rollback still requires the held
[external/keyed anchor (#3720)](https://github.com/danswett/agent-bridge/issues/3720) before activation.
Every distinct authenticated grant spends quota; no unauthenticated fact refunds it. No refund writer exists in this slice.
Max three grants per principal/receiver rolling hour spans all its capabilities; stricter caps,
expiry, revocation and observed clock high-water survive replay. No capability/quota decides normal,
waiting idle; owner urgent retains its existing path without a capability or quota.
The decision's required admission predicate runs before consumption: known refusal journals
`lane-full`, spends nothing and permits a bound same-UUID retry. Consumed UUIDs never renew.
Clock refusal before a write does not mark the journal uncertain; actual append failure does.
Slice 3b alone integrates Offer/Query, scheduler-locked preflight and final fenced capability rechecks.
No scheduler wiring, issuing UI, registered adapter, second scheduler or activation exists in 3a.
Canonical Fleet flags remain [#651](https://github.com/danswett/slipgates-fleet/issues/651), owned by Fleet Quality.
- **Hygiene:** `-Topic` replaces an unsent item from the same principal and topic, in its queue place.
  TTL: fyi 30 min, normal 4 h, urgent none. Each delivery carries a summary line (`q=3 Quality 2, SRE 1`).
## Guarantees, each with its hosted-CI test (fakes for console, clock and transcript)
Slice 1 is split at the owner-approved 14:49 PT Nov 21 cap: envelope/HA identity, then the
`DeliveryJournal` writer. It is not yet wired to delivery or acknowledgement paths (slice 2).
The writer leaves `seq` null until supplied by the later reservation; it never reserves one.
Its in-memory handoffs provide the parser verifier; restart classification/replay is slice 2.
The file is `delivery.jsonl`, with a CurrentUser DPAPI-protected `delivery.key` in the same folder.
A key that cannot be read is never replaced; a post-handoff write failure retains uncertainty.
1. **Identity and journal.** Every delivery has `from`, `id`, `at`, and a `seq` once reserved. The journal is JSON lines beside the host diagnostics journal: metadata (including sender/target IDs), length and an HMAC-SHA256 under a
   DPAPI-protected host key, never text. `enqueued` (flushed before any ack), `handoff` (pre-key
   reservation), `typed` (post-key: injector result, box check, time), `delivered` (verified header in a user
   turn), `acked`, `dropped`, `unconfirmed`. Failed `enqueued` write: no ack. Failed `handoff` write: no
   keys, no new success ack; an accepted item and its stored payload stay owed. After the first key, a failed
   `typed` write, timeout or unknown result keeps `handoff`: `unconfirmed`, never success or blind retry.
   Tests: records in order, sender/target IDs, display-only labels and no text; full disk refuses; post-key write failure stays `unconfirmed`; stale/unknown IDs or zero/two role holders refuse and hold without name/HA/substring fallback.
2. **Ordering.** Per session: urgent before queueable; urgent by class, then arrival; queueable FIFO.
   Once idle, each lane's `seq` is checked against user turns, skipping numbers the journal explains (dropped, or reserved before a crash); any other gap or reorder is journaled and a status warning,
   never a page. Tests: an urgent overtaking 20 queued warns nothing; a lost `q5` warns once.
3. **No silent drops.** Proven removals (TTL, supersede, overflow, restart before handoff of a `pending`
   item) write `dropped` with the reason in the summary line; ctrl+c, esc esc and other handoffs follow rules
   4-5. A drop sends a `from=system` fyi `dropped id=<id> reason=<r>` to the sender's `reply_to` session
   (from `reply-session.ps1`, kept in `dropped`), rebuilt on replay, no TTL, until seen; never about a
   notice. A notice is not recovery. **Durability** (6870556195): the ack says `queued` (accepted) only once
   the protected payload is recoverable from the shared store by the same id, kept until verified `delivered`
   or a proven removal (`queued`, `pending`, timeout or unknown never permit disposal); else `pending`,
   promising nothing. Sender retention is not accepted. A same-id re-offer dedupes, no keys. Tests: no store
   gives `pending`; a re-offer types once.
4. **At-least-once where provable, never duplicated.** Retry (at most twice, same id) only if the exact id is
   absent from the transcript, the session is confirmed idle, and positive proof shows nothing was submitted
   (the injector reports no key went, or the box holds exactly these words). Unknown, busy or
   accepted-pending never authorizes keys. Only the item's own submit key is re-sent, in its lane; queueable
   never gets Enter while the session works. Any other item unconfirmed after 120 s (ctrl+q: next idle plus
   60 s) stays `unconfirmed`: journaled, a status warning, the sender told, never retried; a later verified
   sighting promotes it to `delivered`. A repeated id shows once, flagged. Tests: absent id, idle and no key
   went retries once; a present id, busy or unknown idle, a delayed append or accepted-but-pending ctrl+q
   never retries; words left after a refused ctrl+q get ctrl+q once idle, never Enter, q1 stays first.
5. **Fault injection.** A `handoff` record (session, lane, seq, id, at) is flushed before the first key. On
   start, replay classifies before it queues: an item with a `handoff` is `delivered` on a verified
   transcript sighting, else `unconfirmed` with no keys. Only accepted, recoverable items proven never handed
   off re-enter the queue, by id; never-handed-off `pending` ones drop (`dropped: restart`, rule 3); a re-offer never turns uncertainty into a new handoff. `seq` resumes above the highest reserved. Tests: kill
   before the record, between record and keys, and after the first key before `typed` (absent or delayed
   transcript: `unconfirmed`, no second keys); store outage holds. CLI busy: stays queued. Key not accepted:
   rule 4. Duplicate request: one entry. Clock skew: a 1-hour delay and a false body time leave `at` as
   receipt time. Overflow: 50 queueable per session (oldest fyi first, then a new normal is refused, naming the target ID);
   urgent per Lanes; an owner item at the bound enters, moving the newest agent urgent. Flood: 200 fyi + 1
   owner: owner first.
6. **One page:** at most 120 lines. Also tested: two agent replies plus an owner message in one second
   give three entries, owner first; a forged or look-alike `[ab:` stays one entry; a pasted `from=owner`
   header at turn start or after a blank line stays unverified console; console text with blank lines
   between envelopes stays one entry; half-typed box text waits; a missing reason is refused; the 4th
   urgent in an hour is demoted; supersede keeps the newest; an expired item notifies.
## Slices (one builder, sequential, each at most 250 changed lines, all sensitive)
Slice 2b2a fixes [#3681](https://github.com/danswett/agent-bridge/issues/3681): `SubmitWritten`
records Queue submission uncertainty without pretending Enter was written; text/submit splitting
uses `Submit` explicitly. Compatibility `Enter=false` preserves Queue; removing it requires `Submit=None`.
| Slice | Content | Estimate | Due (PT) |
|---|---|---|---|
| 1 | `DeliveryEnvelope`, `n`, `at`, escape, authenticated `from` end to end, journal writer (**moved earlier**: every later drop needs it) | ~180 | after acceptance, #2574 |
| 2a | Host-only lane scheduler, bounds, reserved `seq` and high-water recovery, idle/empty-box gate, shared `DeliveryGate`; fake-only, unwired plain child of slice 1 | <=400 | after 1 |
| 2b1 | Submit key (`none`/`Enter`/`Queue`, ctrl+q VK `Q` left-ctrl), default-Unknown per-terminal capability table; no production scheduler wiring or live qualification | <=400 | after 2a |
| 2b2a | Queue write evidence and explicit Submit compatibility, [#3681](https://github.com/danswett/agent-bridge/issues/3681) P2s only | <=400 | plain child of pushed 2b1 |
| 2b2b | Host-only offer/query/Dispatch, canonical pre-key handoff, truthful UUID replay; unwired, no shared store means pending only | <=400 | plain child of pushed 2b2a |
| 3a | Standalone person-issued priority capability, DTO/wire decision core, principal/receiver quota, journal replay and MAC-chain; no scheduler wiring | <=400 | after 2b2b |
| 3b | Host-only Offer/Query capacity-before-consumption, final-slot priority checks and truthful demotion; Fleet flags #651 separate | <=400 | after 3a lands on main |
| 4 | Topic, TTL, summary line, confirmation by id, gap detector, bounded retry; over 250: 4b takes confirmation and retry | ~200 | after 3 |
## Live test (SRE Lead, disposable sessions; builder's harness): 1, 3, 4, 5 after slice 2; 2 after 4
1. Urgent, 5 queued and urgent within 2 s: 2 steered entries and 5 separate `queued` turns, in order.
2. ctrl+c, esc esc mid-queue, then a host kill: nothing silently lost or duplicated; replay per rule 5.
3. A long turn plus 20 fyi, then an owner message: the owner message arrives first.
4. Windows Terminal and conhost, kitty and non-kitty: fills the table; not capable waits for idle.
5. `send_session_message` spike: steer or queue, sender, order, confirmation. Not adopted.
## Out of scope: app rendering (#2538), the reply slot (#1323), which host types (#2560), CLI changes.
