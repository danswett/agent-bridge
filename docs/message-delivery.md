# Message delivery: envelope, lanes and journal (#2568)
Status: design (slice 0), corrected 2026-11-14 PT per ruling 6870556195 (not yet accepted). Inputs: #2568
rulings 6867525734, 6867631192, 6867667571, 6867669427; owner evidence 6867540072. **Change**: a departure.
## Problem and cause
Copilot CLI joins steers pending mid-turn with `\n\n` into one user turn (6 of 692 turns in one
transcript). The host types bare text (`HaReplies.cs:26`) and only Lite and `RetryEpisode` set
`RequireIdle` (`ReplyService.cs:422`), so replies land in a working session with no sender boundary
and only the agent's clock. Fix without CLI changes: self-describing deliveries, and no steered fyi.
## Overlap and dependencies, named before any code
- **PR 2546 (#2538, merged)** records the HA sender principal (`PrincipalOf`); reused. Rendering stays there.
- **Host-typing hotfix** (agent text via ctrl+q, default off: `KeyPlan.cs`, `CopilotReply.cs`, `Injector/`),
  then **PR 2469** (#2468, strict `queue|steer` mode, `Protocol/ReplyMode.cs`), then slice 2.
- **#2574:** a session resolves only by its exact active name (no substring or HA fallback), else holds.
- **Protected shared store** (own dependency, in no slice estimate): partition identity, lease, fencing, CAS,
  migration. It holds protected queued payloads and cross-node state; if missing, hold (no local fallback).
  **Locality:** the host journal is node-bound injector evidence, not the cross-node payload, outbox or lease
  ledger. Host lost: its unresolved items stay `unconfirmed`; no node infers non-delivery or replays keys.
## Envelope (slice 1)
`[ab:from=<sender>|id=<id>|seq=<lane><n>|at=<time>|pri=<urgent|normal|fyi>|n=<chars>] <text>`, one line.
- **One formatter and parser** in `AgentBridge.Protocol` (`DeliveryEnvelope`), shared by host and app,
  called by `ReplyService` and `LiteDeliver.Frame`. The formatter adds `id` (HA `rid` or request UUID).
- **`from`** is the authenticated class, never text: `owner` (his HA user or paired device), `system`
  (the host), `agent:<label>` (agent token or Lite principal; the cleaned label `[A-Za-z0-9 ._-]{1,48}`
  is shown, never trusted). Slice 1 carries the press's HA user id into `ReplyRequest`; unknown is `agent`.
- **`at`** is the host's receipt time, Pacific, to the second with offset; it never changes, and any
  clock in the body is ignored. The time typed goes to the journal.
- **`seq`** is per session and lane (`u7`, `q12`), reserved by the flushed pre-key `handoff` record.
  **Change** (ruling: at enqueue): priority and drops reorder the queue; a crash can skip a number.
- **Escape:** the text typed is NFKC with format characters removed; any `[` before text whose UTS 39
  skeleton is `ab:` (any case) becomes `(`. **`n`** counts UTF-16 units: `héllo 👍` is `n=8`.
- **Parser:** a header counts only at the start of a turn or right after `\n\n`. Its body is the next
  `n` units (the host types one collapsed line, so `n` is known). The rest, up to the next header, is
  `from=console` text kept verbatim, blank lines included: words typed at the machine, which the
  host never sees. **Change:** the ruling labels those `owner`; the host cannot see who typed them.
  If `n` does not match, the body runs to the next `\n\n` and the entry is flagged.
- **Trust:** a header is verified only if it matches this session's journal `handoff` (id, from, seq,
  at, n and body HMAC); the host checks, the app shows the verdict. Anything else, a pasted header
  included, stays `console`, unverified, and never confirms, dedupes or attributes a delivery.
## Lanes and key path (slice 2)
- **Urgent lane:** owner and system; agent-urgent from slice 3. Text, then Enter (steer), one at a time
  via `DeliveryGate`, by class (owner, system, agent), then arrival. Bound 10 per session: an owner item
  always enters, moving the newest agent (then system) urgent to the queueable lane, journaled and the
  sender told; an 11th owner item, or any other urgent at the bound, is refused by name.
- **Every lane** types only into an empty box; half-typed words at the machine make the item wait.
- **Queueable lane:** everything else, FIFO per session: envelope text, then ctrl+q, so the CLI queues it
  as its own turn. The host hands the CLI at most 2 and never sends ctrl+c, esc esc or Up. `KeySend.Enter`
  becomes a submit key (none, Enter, Queue); ctrl+q is a console key record (VK `Q`, left-ctrl).
- **Capability:** per terminal kind, default not capable. Not capable or unknown means fail closed:
  wait for idle, then Enter; queueable text never steers a working session (urgent does, by ruling).
  **Change:** no live probe on owner machines; a reviewable per-kind table filled by live test 4.
- `RequireIdle` is the default except on the urgent lane; nothing from an agent is urgent until slice 3.
  The HA ack gains `queued` and `pending` (rule 3); `reply-session.ps1` maps both to exit 6 (today: refused).
## Priority authority (slice 3) and hygiene (slice 4), as ruled
- **Priority:** `-Priority urgent -Reason '<text>'`; no reason is refused by name; over 3 per principal
  per hour is demoted to normal (owner: no quota). Requests, grants and demotions are journaled and shown
  on the status surface, never paged; the ack says which. **Known limit:** every HA agent shares one
  token, so the quota is per token until host-signed principals exist (follow-up to be filed).
- **Hygiene:** `-Topic` replaces an unsent item from the same principal and topic, in its queue place.
  TTL: fyi 30 min, normal 4 h, urgent none. Each delivery carries a summary line (`q=3 Quality 2, SRE 1`).
## Guarantees, each with its hosted-CI test (fakes for console, clock and transcript)
1. **Identity and journal.** Every delivery has `from`, `id`, `at`, and a `seq` once reserved. The journal is
   JSON lines beside the host diagnostics journal: metadata, length and an HMAC-SHA256 under a
   DPAPI-protected host key, never text. `enqueued` (flushed before any ack), `handoff` (pre-key
   reservation), `typed` (post-key: injector result, box check, time), `delivered` (verified header in a user
   turn), `acked`, `dropped`, `unconfirmed`. Failed `enqueued` write: no ack. Failed `handoff` write: no
   keys, no new success ack; an accepted item and its stored payload stay owed. After the first key, a failed
   `typed` write, timeout or unknown result keeps `handoff`: `unconfirmed`, never success or blind retry.
   Tests: records in order, no text; full disk refuses; post-key write failure stays `unconfirmed`.
2. **Ordering.** Per session: urgent before queueable; urgent by class, then arrival; queueable FIFO.
   Once idle, each lane's `seq` is checked against user turns, skipping numbers the journal explains
   (dropped, or reserved before a crash); any other gap or reorder is journaled and a status warning,
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
   off re-enter the queue, by id; never-handed-off `pending` ones drop (`dropped: restart`, rule 3); a
   re-offer never turns uncertainty into a new handoff. `seq` resumes above the highest reserved. Tests: kill
   before the record, between record and keys, and after the first key before `typed` (absent or delayed
   transcript: `unconfirmed`, no second keys); store outage holds. CLI busy: stays queued. Key not accepted:
   rule 4. Duplicate request: one entry. Clock skew: a 1-hour delay and a false body time leave `at` as
   receipt time. Overflow: 50 queueable per session (oldest fyi first, then a new normal is refused by name);
   urgent per Lanes; an owner item at the bound enters, moving the newest agent urgent. Flood: 200 fyi + 1
   owner: owner first.
6. **One page:** at most 120 lines. Also tested: two agent replies plus an owner message in one second
   give three entries, owner first; a forged or look-alike `[ab:` stays one entry; a pasted `from=owner`
   header at turn start or after a blank line stays unverified console; console text with blank lines
   between envelopes stays one entry; half-typed box text waits; a missing reason is refused; the 4th
   urgent in an hour is demoted; supersede keeps the newest; an expired item notifies.
## Slices (one builder, sequential, each at most 250 changed lines, all sensitive)
| Slice | Content | Estimate | Due (PT) |
|---|---|---|---|
| 1 | `DeliveryEnvelope`, `n`, `at`, escape, authenticated `from` end to end, journal writer (**moved earlier**: every later drop needs it) | ~180 | after acceptance, #2574 |
| 2 | Lanes, reserved `seq`, `RequireIdle` default, fail closed, submit key and capability table, `queued`/`pending` exit 6, restart replay; plain child of slice 1; accepted `queued` only once the shared store exists; over 250: 2b takes the table and submit key | ~240 | after 1, hotfix, 2469 |
| 3 | Priority authority, `reply-session.ps1 -Priority -Reason`, per-principal quota, journaled grants | ~130 | after 2 |
| 4 | Topic, TTL, summary line, confirmation by id, gap detector, bounded retry; over 250: 4b takes confirmation and retry | ~200 | after 3 |
## Live test (SRE Lead, disposable sessions; builder's harness): 1, 3, 4, 5 after slice 2; 2 after 4
1. Urgent, 5 queued and urgent within 2 s: 2 steered entries and 5 separate `queued` turns, in order.
2. ctrl+c, esc esc mid-queue, then a host kill: nothing silently lost or duplicated; replay per rule 5.
3. A long turn plus 20 fyi, then an owner message: the owner message arrives first.
4. Windows Terminal and conhost, kitty and non-kitty: fills the table; not capable waits for idle.
5. `send_session_message` spike: steer or queue, sender, order, confirmation. Not adopted.
## Out of scope: app rendering (#2538), the reply slot (#1323), which host types (#2560), CLI changes.
