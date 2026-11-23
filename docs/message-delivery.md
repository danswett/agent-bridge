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
- **Journal cannot open:** with the flag on, delivery refuses and fails closed. There is no legacy idle-only typing fallback for a lane target (Copilot). A Claude target is not on the lane and keeps its own busy-queue typing path behind `claudeBusyQueue` (owner option 2, see "Busy sends to Claude" below); the lane does not replace it.
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
Zero-body legacy requires its MAC (a missing MAC is unverifiable evidence, never an exception); a MAC-valid zero-body record linked to refused priority evidence replays Unconfirmed, not Dropped;
unverifiable nonempty history is Unconfirmed/inert only when no priority record exists anywhere,
except that a never-handed-off `Enqueued` or `Demoted` record replays as Dropped when no refused priority evidence links to it (a handoff or refusal link makes it Unconfirmed);
otherwise all bindings are quarantined regardless of record position. Unrelated handoffs stay Unconfirmed and need body-MAC evidence.
Known pre-activation limitation: genuine nonempty history in a journal with any priority record permanently quarantines on restart; whole-design review must resolve this with a body-free authenticated-history design ([#3720](https://github.com/danswett/agent-bridge/issues/3720)).
no missing-body guess, new replay-body resolver, UI, live adapter or migration is introduced.
Policy facts link their previous MAC. **Journal-only rollback anchor ([#3720](https://github.com/danswett/agent-bridge/issues/3720), owner-approved option A, [approval](https://github.com/danswett/agent-bridge/issues/3720#issuecomment-7092662644)):**
after every priority append the journal seals `{count, tail MAC}` of the authenticated chain in `delivery.head`, a DPAPI CurrentUser blob beside the journal.
On open, the chain must contain the sealed tail at index `count-1`; a missing head over existing priority records, a damaged or mismatching head,
or a journal that is shorter or older than the head quarantines priority, so agent priority is refused with no repair, reset, refund or revival (owner priority is unchanged).
A crash between append and seal leaves the journal ahead of the head: accepted and resealed. A failed seal marks the journal uncertain and grants nothing.
Journals that hold priority records but no head (pre-anchor) refuse. Where it runs, what happens when the node leaves, where state lives: the host node, whose
journal is already node-bound; the head is Windows user and machine bound, so if the node or its user is lost the head is gone and priority refuses (fail closed).
**Documented gap:** rewinding the journal and the head together (a VM snapshot revert, or a restore of both files) is not detected; a TPM or relay-held head (options B, C) stay held.
A quarantined journal never reseals the head, so appends on a refused chain cannot move the head onto it and a second restart stays refused.
**Documented gaps:** DPAPI CurrentUser has no per-process isolation, so any process running as the same Windows user can forge or delete `delivery.head` and can equally read the DPAPI-protected journal key: the head detects a journal-only rollback, not a hostile same-user process (a deleted head over existing priority records does refuse);
a malformed or out-of-range head quarantines rather than throwing. Cost: every priority append now adds one DPAPI protect and one fsynced file replace on top of the journal fsync; the `checked` coalescing (one per second per receiver) bounds the rate, and slice 3b owns any wider bound.
Agent priority stays unactivated, and the crash window above (one record appended but not yet sealed) can be rolled back unnoticed.
Every distinct authenticated grant spends quota; no unauthenticated fact refunds it. No refund writer exists in this slice.
Max three grants per principal/receiver rolling hour spans all its capabilities, and the smallest active `Hourly` limit governs; stricter caps,
expiry, revocation and observed clock high-water survive replay. No capability/quota decides normal,
waiting idle; owner urgent retains its existing path without a capability or quota.
The decision's required admission predicate runs before consumption: known refusal journals
`lane-full`, spends nothing (the predicate must not call back into the priority journal: any nested priority call throws, so the decision cannot be spent or revoked mid-admission) and permits a same-UUID retry only when it is unchanged: the `requested` fact stores a
keyed MAC-covered `priorityBinding` over target, sender, exact receiver and canonical body, so a changed body or receiver is refused.
The `requested` header stores the requested priority (`normal` for an invalid value) and `granted`/`consumed` store the effective `urgent`.
Every successful current-authority check appends a `checked` fact that advances the durable time high-water, at most one per second per receiver, so a rolled-back
clock is refused after replay too (a rollback inside the last unrecorded second is accepted); slice 3b still owns the wider record-growth bound. Consumed UUIDs never renew.
Clock refusal before a write does not mark the journal uncertain; actual append failure does.
Slice 3b alone integrates Offer/Query, scheduler-locked preflight and final fenced capability rechecks.
Offer takes an optional `DeliveryPriorityRequest` and receiver binding; only a non-owner explicit non-normal request reaches
`DecidePriority`, whose admission predicate checks the effective lane before any grant is consumed (a full lane refuses
`lane-full`, spends nothing and permits a same-UUID retry). Agent urgent exists only for a journaled grant; Query reports a
demotion and its reason. The scheduler rechecks `PriorityCurrent` at preflight and in the final fence: a lapsed grant is
journaled `demoted` and waits as normal, never typed urgent, evicting the oldest fyi if the normal lane is full. A priority append
failure latches the scheduler and the shared gate, so no later owner or lifecycle step proceeds; a clock refusal does not. The demotion
is a journal fact: Query keeps it after handoff, beside any delivery reason. Owner overflow demotes the newest agent, then system.
Nothing calls this yet: no wiring or activation.
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
## Busy sends to Claude: let Claude Code queue (Part of #2568; proposed, owner decision pending)
Status: DRAFT. The flag is proposed and defaults off; slice B (#3843) is merged and does not arm it. Goal: stop refusing a working Claude on the default transport, so a busy refusal no longer needs the HA fallback. Phase 1 lane delivery stays Copilot-only and its journal, scheduler and slices are untouched; this is the Lite/`AgentAsync` typing path Claude uses, and **it stays Claude's path with `laneDelivery` on** (owner option 2, #3901): the lane does not replace it. **Rule: the `claudeBusyQueue` flag stays OFF until all of these have landed: slice C (#3865), D2a (#3869), D2b (#3876 and the store, watcher and derived-slot pieces below) slice E with its exit-6 caller fixes (slipgates-fleet #1148), and the option-2 slice (#3901), because the fleet will run with `laneDelivery` on; arming is then a separate owner act.** Register it in the fleet flag registry ([#651](https://github.com/danswett/slipgates-fleet/issues/651)). Line numbers below are for `LiteDeliver.cs` and `ReplyService.cs` on main at the time of writing and must be re-checked before a slice cites them.
**Today.** `LiteDeliver` refuses a working Claude unless the flag is on: `Ready(s, busyQueue)` (LiteDeliver.cs:211) is `Question is null && (Status==Idle || busyQueue && Status==Working)`, and the request sets `RequireIdle = !busyQueue` (:130); `AgentAsync` rechecks (ReplyService.cs:715, :749). Lite also checks the composer (:113) and the rate limit. HA (`HaReplies.cs:26`) sets no `RequireIdle`, so it types into a working Claude. `ReplyMode {Steer,Queue}` is read by nothing; `QueueSubmit` (ctrl+q) is Copilot-only.
### Verified versus assumed
- **Verified:** a Claude prompt typed while it works is a `queue-operation` `enqueue` with that content (2.1.312, seen live; [host-replies.md](host-replies.md) line 15), which confirms by identity. A prompt typed while idle is a `user` entry with the same content. When Claude takes a queued prompt it records a `user` entry with `promptSource: "queued"` and the same words; the repo's Claude prompt parser (`AgentPrompts.cs`) documents this as seen live on 2.1.312 and already treats it as the enqueued prompt's consumption.
- **Assumed, UNPROVEN until a real-transcript fixture proves each:** the `dequeue`/`remove` (`absorbed_mid_turn`) rows (from a peer builder's read-only notes, not checked here), so **a `remove` does not free the held slot or end a watch as proof of removal until proven**; Claude Code's own queue cap and TTL; what Esc, a Claude restart or a session end does to a queued prompt (assumed: a restart drops the in-memory queue); Claude's suggested-prompt text (below).
### Owner change (the rule, for approval)
For a Claude target on the default transport, a **busy** session (`Working`, no pending `Question`) is not refused: Lite sends with `RequireIdle=false`, the relay types once and Claude Code queues it. Copilot, Codex, the urgent lane and the lane journal are unchanged. Flag `agentMessaging.claudeBusyQueue`, default off, own-account only, ANDed with `liteKill.On`, read afresh, fail closed. Kept: rate limit, pending `Question` refusal, unknown status refusal, `StillCurrent` fence before the first key, `KeysGoing`, and `KeySend.Expect` (the keys are held to the box's rows as read).
### Composer guard: owner decision (a), Nov 22, 2026 PT
Recorded by Chief S16 ([comment](https://github.com/danswett/agent-bridge/pull/3828#issuecomment-7094450173)) and relayed by Engineering R9; not yet in owner-decisions.md. Keep the `composerNotEmpty` guard, type past Claude's suggested-prompt placeholder, refuse if unsure, never type over a real draft. **Recognition is UNPROVEN:** the repo records that a screen read cannot tell Claude's suggestion from typed words (LiteDeliver.cs ~234, ReplyService.cs ~765, host-replies.md line 277), and today's Claude check refuses only a box starting with `/` or `!`. So the rule is: refuse unless the box is positively empty or shows exactly the known hint (`ClaudeComposer.QueuedHint`, `Press up to edit queued messages`). Slice C (#3865) implements this and proves that hint text from a real screen before the flag can be armed; until then busy sends to a non-empty box are refused.
### Risks
1. **Duplicate or ambiguous handoff.** Claude is not on the lane, so there is no `handoff` journal. After any key goes, an unknown result is never resent by the relay or `reply-session.ps1`, and never falls back to HA. A retry is allowed only for a refusal, which means nothing was typed ([reply-deduplication.md](design/reply-deduplication.md); [agent-lite.md](protocol/agent-lite.md)).
2. **Screen Expect.** The screen redraws while Claude works; confirmation is the transcript, not the screen.
3. **Ordering.** One relay message per node at a time. A send while another is in flight, Accepted or held is refused `queueHeld` with no keys; it does not wait behind it. No urgent overtaking.
4. **Prompt between check and keys.** The fence is taken before the first key; a question seen then aborts with nothing typed.
5. **Provenance and arming.** The text keeps the framed sender line and arrives as `origin` human. The flag is host config only; no message arms it. See "Provenance past 600 s and after a restart" below.
### States, terminal state and what the sender is told
- **Status store (R9 ruling; slice D2b):** a small host-owned store, separate from the lane journal and never shared with it, **in memory only** (the owner's decision D2 on [#3875](https://github.com/danswett/agent-bridge/pull/3875), relayed by Engineering R9; not yet in owner-decisions.md): nothing is written to disk. One record per send, created before the first key, holding ids, a state and times only: never message text, never the target's name. Fields use the receipt design's vocabulary ([#3875](https://github.com/danswett/agent-bridge/pull/3875), agent-lite.md), whose receipt answers `status: "receipt"` with the journaled outcome in `code`, or `status: "unknown"`, for the asked `request`; so its probe can read this store later: `request` (the signed request id), `principal` (the signed sender principal, kept as in the receipt journal so a probe from anyone else learns nothing), `state` (the acknowledgement kind as the receipt's `code` carries it: `in-flight`, `queued`, `delivered` or `unconfirmed`; `refused:<code>` and `unknown` are never written here, since a refusal before any key writes nothing and an absent record answers `unknown`), `reason` (null, or `noRow`, `watchEnded`, `claudeRestart` or `sessionEnded`; `removed` only once `remove` is proven), `at` (first offer, UTC), `until` (the Accepted time plus 120 s, set when the record becomes `queued`; before that the deadline is `at` plus 15 s) and `held` (true while the sent prompt may still sit in Claude's own queue: set when the keys go, cleared by its consumption, or by a Claude restart or session end, never by the 120 s report). Bounds: a record is kept 150 s from `at` (the receiver journal's window), but a `held` record is kept until `held` clears, and the store holds at most 4,096 records (its bound). A record that cannot be created (the store is full) refuses the send before any key, with the code `statusFull` (never `busy`); one that cannot be read answers `unknown`, never `delivered`. **A host restart forgets every record**, as it does the receipt journal (150 s, memory only): a lookup afterwards answers `unknown`, which for a send that was Accepted is reported `unconfirmed`. The host never re-types after a restart, because the store never held the text; nothing is retried. Surviving a restart would need its own owner decision.
- **Accepted:** the `enqueue` row for this send's content is seen (verified signal), within **15 s** of the last key. A `user` row with the same content, from a session that went idle between the check and the keys, counts the same, so a busy-to-idle flip is not read as `Unconfirmed`.
- **Delivered:** the idle `user` row above, or, for a send that was Accepted, the later `user` entry with `promptSource: "queued"` and the same words. A send is Accepted (`queued`) until the watcher (D2b) sees that entry; before then it is not claimed Delivered.
- **Unconfirmed, terminal for the report, not an expiry:** keys went but no `enqueue` or `user` row within 15 s (`noRow`); or Accepted and not consumed within an **absolute 120 s** of Accepted, whatever the turn is doing (`watchEnded`; no "next idle" wait, so a stuck turn ends the watch); or the Claude process restarts (`claudeRestart`) or the session ends (`sessionEnded`); or the host restarts (the store is memory only, so every pending `in-flight` or `queued` item is lost and is reported `unconfirmed` from then on). **At 120 s the host stops watching and the sender is told `unconfirmed`; the prompt is not removed.** The relay never sends Esc or ctrl+c, so Claude keeps it queued until it takes it, which can be long after (a 15-minute turn). The record stays `held`, and if Claude later takes the prompt (the `promptSource: "queued"` entry) the record moves from `unconfirmed` to `delivered`: a late delivery, which is exactly why a resend would duplicate it. Never retried, never re-typed, never over HA.
- **Told once, and the sender's own limit:** there is no push. The state is held in the status store; the sender asks for it by request id (pull, as the S5 owner decision says). An `unconfirmed` answer can later become `delivered` (a late consumption) and never goes back; no answer ever permits a resend. **The sender can only ask while it still holds the sealed envelope: at most 120 s, 64 entries (#3875).** After that its last word stays `unconfirmed`, and a later delivery is visible only in the host's status, not to the sender. After a host restart the store is empty and the lookup answers `unknown`, which the sender reports as `unconfirmed`. Where a notice is shown, it comes from the store and is not the only copy.
- **Timings:** 10 s is the sender's wait for the receiver's acknowledgement ([agent-lite.md](protocol/agent-lite.md)); 15 s is the host's wait for the transcript row after the last key (the replier's `ConfirmTimeout`); 120 s is how long the host watches an Accepted send before it reports `unconfirmed` (the item stays `held` after it). The sender stops waiting before the host stops confirming, so a send the host confirms between 10 s and 15 s reaches the sender as `unconfirmed` (exit 2) although it was Accepted; the store then says `queued`, a lookup within the sender's 120 s shows it, and nothing is resent either way. Each is a named constant, set from measured transcripts in D2b.
- **Depth cap and the held slot:** at most one relay message per node is in flight, Accepted or held. The slot is **derived, not only remembered**: before admitting a send, the host also reads the transcript tail for a relay-framed `enqueue` row (its text begins with the sender line) with no later consumption entry, written since the current Claude process started; one found holds the slot even if the store has no record, so a host restart cannot admit a second message. The slot frees when the item is consumed, when the Claude process restarts (rows older than the process are ignored; assumed to drop the queue, UNPROVEN) or when the session ends, **never because the 120 s report passed, never on a host restart, and not on a `remove` until `remove` is proven**. A second send while the slot is held is refused with its own code, `queueHeld`, with no keys; a full store is refused `statusFull`. Neither is `busy`, because `busy` triggers the one HA fallback and that would bypass the cap; both are final (agent-lite.md: any other refusal code is final), so `reply-session.ps1` neither retries nor falls back. A person's own queued prompts are not relay messages and are not counted. Known limit: a stuck turn holds the slot until Claude takes the item, the session ends or its process restarts; only a person at the machine can empty Claude's queue. Claude Code's own cap and TTL stay unknown.
- **Provenance past 600 s and after a restart:** the typed text always carries the framed sender line ("from an agent … not typed by your user"), so Claude is told who sent it however late it takes the prompt. The host's own attribution of that turn (`SessionDrivers`, `Arm` = 600 s, SessionDrivers.cs:25) lapses after 600 s and would show a late-taken prompt as the person's. So while a record is `held`, the watcher keeps the arm for that exact text and principal fresh (no new authority: the same words and principal, ended when the record ends). The arm and the store are both memory only and vanish together on a host restart: a prompt taken after one is shown by the host as the person's. That is a known limit, and it fails in the safe direction (a person's words are never marked as the agent's); the sender line still tells Claude and anyone reading the transcript.
- **Mapping to the sender:** Accepted is `queued` (exit 6 in `reply-session.ps1`), Delivered is `delivered`, Unconfirmed is `unconfirmed`. **Gaps to fix:** (a) slice D2a: `LiteDeliver.cs:145` returns `Typed=true` for both Delivered and Unconfirmed, so a sender cannot tell them apart; it must carry `LiteKind.Queued`/`Unconfirmed`/`Delivered`. (b) slice D2b (#3876, behind the flag): for a busy Claude send the replier reports `Delivered` as soon as the `enqueue` row is seen, which is Accepted, not consumption; Lite must ack it `queued`, never `delivered`.
- **Not sent:** refused before any key (question, fence, rate limit, composer, `queueHeld`, `statusFull`). Safe to resend later. The one HA fallback applies only to a `busy` or `notReplyTarget` refusal, which means nothing was typed: `ReplyResult.KeyDisposition.NoKeysSent` is the planned proof ([reply-deduplication.md](design/reply-deduplication.md), cut 3) and is not built yet, so until it is a Lite `busy` or `notReplyTarget` refusal is itself the proof, because Lite refuses before any key goes; once it exists the fallback requires it. Never after Accepted or Unconfirmed, and never for `queueHeld` or `statusFull`.
- **Coexistence with `laneDelivery`, settled (owner option 2, relayed by Engineering R9, recorded by Quality on #3866; not yet in owner-decisions.md):** Claude keeps the busy-queue typing path even with `agentMessaging.laneDelivery` on. The lane serves Copilot; it does not replace the Claude path. Before this, Lite refused every non-Copilot target while the lane was on (the gate at LiteDeliver.cs:94), so the flag did nothing then. Slice #3901 changes that gate: with `claudeBusyQueue` on, a Claude target skips the lane gate and lane block and is typed on the legacy path (its legacy text, guards and replier; Claude's words are not normalised by the lane journal). With the flag off a Claude is still refused `notReplyTarget` while the lane is on, as before. Copilot, Codex, the lane, its journal and scheduler are unchanged.
### Tests (RED first)
Busy Claude types once and is Accepted; busy Copilot still refuses; question, draft and ambiguous screen refuse with no key; flag off keeps Busy; busy-to-idle flip is not `Unconfirmed`; a keys-went send with no row in 15 s is `Unconfirmed` (`noRow`); Accepted not consumed at 120 s is reported `unconfirmed` (`watchEnded`) and stays held; a simulated host restart forgets the record, the lookup answers `unknown` and never Delivered, and nothing is typed again; after a host restart a relay-framed unconsumed `enqueue` in the transcript still holds the slot and a second send is refused `queueHeld`; rows older than the current Claude process do not hold it; Claude restart or session end ends the report and frees the slot; a `remove` row does not free the slot; the held slot survives the 120 s report; a second send while a message is held is refused `queueHeld` with no keys, is not retried by `reply-session.ps1`, and never falls back to HA; a full store is refused `statusFull`; a prompt taken after the 120 s report moves the record from `unconfirmed` to `delivered`; a prompt taken after 600 s is still attributed to the agent (the arm is kept fresh while held); a prompt taken after a host restart is shown as the person's while its text keeps the sender line; a send confirmed at 12 s reaches a 10 s sender as `unconfirmed` and the store says `queued`; a held record outlives the 150 s window until it clears; `enqueue` then `remove` is not Delivered; an unknown result is never resent; an Expect race (the box changes after Lite's read, before the keys) types nothing; with `laneDelivery` on, a busy Claude with the flag on is typed on the legacy path and the lane records nothing, with the flag off it is still refused `notReplyTarget`, Copilot stays on the lane, and Claude's typed words equal the no-lane path.
### Rollback
Turn `claudeBusyQueue` off: `RequireIdle=true` and the Busy refusal return. The status store is memory only and vanishes with the host run; nothing is replayed and there is no migration.
### Slices (each at most 250 changed lines, sensitive, after owner approval)
A this doc (#3828) and D1 (docs child: states, timings, depth cap, memory-only status store, reconciled agent-lite, local-pipe and reply-deduplication text). B flag, admission rule and tests, default off ([#3843](https://github.com/danswett/agent-bridge/pull/3843), merged); it does not touch the outcome mapping. C composer rule, empty box or the exact queued hint ([#3865](https://github.com/danswett/agent-bridge/pull/3865), about 70 lines). D2a the `LiteDeliver.cs:145` outcome mapping to `LiteKind` ([#3869](https://github.com/danswett/agent-bridge/pull/3869), about 15 lines). D2b is four slices, each a plain child: D2b-1 the `queued` stage and Lite's `queued` ack ([#3876](https://github.com/danswett/agent-bridge/pull/3876), about 40 lines); D2b-2a the memory-only status store with `held`, `reason`, the `statusFull` and `queueHeld` refusals and the depth cap (about 220 lines); D2b-2b the transcript watcher, the 15 s and 120 s deadlines and late `delivered` (about 240 lines); D2b-2c the transcript-derived slot after a restart and the arm kept fresh while held (about 200 lines). E `reply-session.ps1` result mapping with its exit-6 caller fixes (slipgates-fleet #1148, about 60 lines). Lane: owner option 2, a Claude keeps the busy-queue path with `laneDelivery` on ([#3901](https://github.com/danswett/agent-bridge/pull/3901), about 15 lines of code and 50 of tests). Retiring the HA fallback is not in these slices: it is an owner act after the held qualification (QUAL-1 witness) and an SRE live test. Coordinate with the Phase 1 builder (S2b, S3, S4, S6): no edits to their branches or the slice table above.
## Live test (SRE Lead, disposable sessions; builder's harness): 1, 3, 4, 5 after slice 2; 2 after 4
1. Urgent, 5 queued and urgent within 2 s: 2 steered entries and 5 separate `queued` turns, in order.
2. ctrl+c, esc esc mid-queue, then a host kill: nothing silently lost or duplicated; replay per rule 5.
3. A long turn plus 20 fyi, then an owner message: the owner message arrives first.
4. Windows Terminal and conhost, kitty and non-kitty: fills the table; not capable waits for idle.
5. `send_session_message` spike: steer or queue, sender, order, confirmation. Not adopted.
## Out of scope: app rendering (#2538), the reply slot (#1323), which host types (#2560), CLI changes.
