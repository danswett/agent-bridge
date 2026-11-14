# Message delivery: envelope, lanes and journal (#2568)

Status: design (slice 0), 2026-11-13 PT. Inputs: Engineering's rulings on #2568 (6867525734, 6867631192,
6867667571, 6867669427) and owner evidence (6867540072). **Change** marks a departure from them.

## Problem and cause
Copilot CLI joins steers pending mid-turn with `\n\n` into one user turn (6 of 692 turns in one
transcript). The host types bare text (`HaReplies.cs:26`) and only Lite and `RetryEpisode` set
`RequireIdle` (`ReplyService.cs:422`), so replies land in a working session with no sender boundary
and only the agent's clock. Fix without CLI changes: self-describing deliveries, and no steered fyi.

## Overlap, named before any code
- **PR 2546 (#2538, merged)** records the HA sender principal (`PrincipalOf`); reused. Rendering stays there.
- **#2468 / PR 2469 (open)** adds a strict `queue|steer` request mode (`Protocol/ReplyMode.cs`).
  Slice 2 uses that type and defines none of its own. Order: hotfix, then 2469, then slice 2.
- **Host-typing hotfix (Host Typing Hotfix GPT):** agent text via ctrl+q, default off, in `KeyPlan.cs`,
  `CopilotReply.cs` and `Injector/Program.cs`; slice 2 lands after it and reuses its ctrl+q key.

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
  The HA ack gains `queued`, which `reply-session.ps1` maps to exit 6 (today it would read refused).

## Priority authority (slice 3) and hygiene (slice 4), as ruled
- **Priority:** `-Priority urgent -Reason '<text>'`; no reason is refused by name; over 3 per principal
  per hour is demoted to normal (owner: no quota). Requests, grants and demotions are journaled and shown
  on the status surface, never paged; the ack says which. **Known limit:** every HA agent shares one
  token, so the quota is per token until host-signed principals exist (follow-up to be filed).
- **Hygiene:** `-Topic` replaces an unsent item from the same principal and topic, in its queue place.
  TTL: fyi 30 min, normal 4 h, urgent none. Each delivery carries a summary line (`q=3 Quality 2, SRE 1`).

## Guarantees, each with its hosted-CI test (fakes for console, clock and transcript)
1. **Identity and journal.** Every delivery has `from`, `id`, `at`, and a `seq` once reserved. The
   journal is JSON lines beside the host diagnostics journal: `enqueued` (flushed before any ack),
   `handoff` (pre-key reservation), `typed` (post-key: injector result, box check, time), `delivered`
   (verified header in a user turn), `acked`, `dropped`, `unconfirmed`. It holds metadata, length and
   an HMAC-SHA256 under a DPAPI-protected host key, never text. A failed journal write means no ack and
   no keys. Tests: records in order, no text; a full disk refuses.
2. **Ordering.** Per session: urgent before queueable; urgent by class, then arrival; queueable FIFO.
   Once idle, each lane's `seq` is checked against user turns, skipping numbers the journal explains
   (dropped, or reserved before a crash); any other gap or reorder is journaled and a status warning,
   never a page. Tests: an urgent overtaking 20 queued warns nothing; a lost `q5` warns once.
3. **No silent drops.** Proven removals (TTL, supersede, overflow, restart before handoff) write
   `dropped` with the reason in the summary line; ctrl+c, esc esc and other handoffs follow rules 4-5. A
   TTL or restart drop tells the sender: `reply-session.ps1` sends its session as `reply_to`, kept in
   `dropped`; the host types a `from=system` fyi `dropped id=<id> reason=<r>` there, rebuilt on replay,
   with no TTL, until seen (never about a notice). **Change** (owner: no lost delivery): the `queued`
   ack says it is not durable; follow-up (a): DPAPI text at rest.
4. **At-least-once where provable, never duplicated.** Retry (at most twice, same id) only on proof that
   nothing reached the CLI: the injector reports no key went, or the box still holds exactly these
   words. Only the item's own submit key is re-sent, in its lane; queueable never gets Enter while the
   session works. Any other item unconfirmed after 120 s (ctrl+q: next idle plus 60 s) stays
   `unconfirmed`: journaled, a status warning, the sender told, never retried; a later verified sighting
   promotes it to `delivered`. A repeated id shows once, flagged. Tests: no key went retries once; words
   left after a refused ctrl+q get ctrl+q, never Enter, and q1 stays first; a delayed append, stale idle
   or accepted-but-pending ctrl+q never retries; a repeat shows once.
5. **Fault injection.** A `handoff` record (session, lane, seq, id, at) is flushed before the first key.
   On start, replay marks never-handed-off items `dropped: restart` (with notice, rule 3), checks
   handed-off ones against the transcript (`delivered`, else `unconfirmed`), and resumes `seq` above the
   highest reserved. Tests: kill before the record, between record and keys, and after the keys. CLI
   busy: stays queued. Key not accepted: rule 4. Duplicate request: one entry. Clock skew: a 1-hour
   delay and a false body time leave `at` as receipt time. Overflow: 50 queueable per session (oldest
   fyi first, then a new normal is refused by name); urgent per Lanes, and an owner item at the bound
   enters and moves the newest agent urgent. Flood: 200 fyi plus 1 owner message, and the owner message
   is typed first.
6. **One page:** at most 120 lines. Also tested: two agent replies plus an owner message in one second
   give three entries, owner first; a forged or look-alike `[ab:` stays one entry; a pasted `from=owner`
   header at turn start or after a blank line stays unverified console; console text with blank lines
   between envelopes stays one entry; half-typed box text waits; a missing reason is refused; the 4th
   urgent in an hour is demoted; supersede keeps the newest; an expired item notifies.

## Slices (one builder, sequential, each at most 250 changed lines, all sensitive)
| Slice | Content | Estimate | Due (PT) |
|---|---|---|---|
| 1 | `DeliveryEnvelope`, `n`, `at`, escape, authenticated `from` end to end, journal writer (**moved earlier**: every later drop needs it) | ~180 | packet Nov 14 12:00 |
| 2 | Lanes, reserved `seq`, `RequireIdle` default, fail closed, submit key and capability table, `queued` exit 6, restart replay; one plain child of slice 1; over 250: 2b takes the table and submit key | ~240 | head Nov 14 18:00; live test Nov 15 |
| 3 | Priority authority, `reply-session.ps1 -Priority -Reason`, per-principal quota, journaled grants | ~130 | after 2 |
| 4 | Topic, TTL, summary line, confirmation by id, gap detector, bounded retry; over 250: 4b takes confirmation and retry | ~200 | after 3 |

## Live test (SRE Lead, disposable sessions; builder's harness): 1, 3, 4, 6 after slice 2; 2, 5 after 4
1. Urgent, 5 queued and urgent within 2 s: 2 steered entries and 5 separate `queued` turns, in order.
2. ctrl+c and esc esc mid-queue: nothing lost silently; every unconfirmed item journaled.
3. A long turn plus 20 fyi, then an owner message: the owner message arrives first.
4. Windows Terminal and conhost, kitty and non-kitty: fills the table; not capable waits for idle.
5. Kill the host with both queues non-empty: unsent items drop with a by-id notice; no duplicate.
6. `send_session_message` spike: steer or queue, sender, order, confirmation. Not adopted.

## Out of scope: app rendering (#2538), the reply slot (#1323), which host types (#2560), CLI changes.
