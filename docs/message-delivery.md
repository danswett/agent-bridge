# Message delivery: envelope, lanes and journal (#2568)

Status: design (slice 0); no code until Engineering accepts it. Author: the #2568 builder
(claude-opus-5.5), 2026-11-13 PT. Inputs: Engineering's rulings on #2568 (comments 6867525734,
6867631192, 6867667571, 6867669427) and owner evidence (6867540072). **Change** marks a departure.

## Problem and cause
Copilot CLI records Enter pressed mid-turn as `steering` and joins pending steers with `\n\n` into
one user turn (6 of 692 turns in the Engineering Lead transcript; the owner's sentences in the
Chief's). The host types every delivery as bare text (`HaReplies.cs:26`), and only Lite and
`RetryEpisode` set `RequireIdle` (`ReplyService.cs:422`), so phone, HA and `reply-session.ps1`
replies go into a working session, nothing marks where a sender ends, and the only clock is the
one the agent typed. We cannot change the CLI: every delivery becomes self-describing, and
non-urgent text is no longer steered into a working session.

## Overlap, named before any code
- **#2538 / PR 2546 (merged)** records the sender principal on the HA reply (`DriverOf`,
  `PrincipalOf`); reused as the sender source. App rendering stays with #2538.
- **#2468 / PR 2469 (open)** adds a strict `queue|steer` request mode (`Protocol/ReplyMode.cs`).
  Slice 2 uses that type and defines none of its own. **Ask:** whether 2469 lands first (preferred).
- **Host-typing hotfix (SG Builder 2568 Host Typing Hotfix GPT):** agent messages sent with ctrl+q
  behind a default-off setting, in `KeyPlan.cs`, `CopilotReply.cs`, `Injector/Program.cs`. Slice 1
  waits for it if it touches `ReplyService.cs` or `HaReplies.cs`; slice 2 reuses its ctrl+q key.
- **#1323** (one HA reply slot) and **#2560** (which host types) stay separate.


## Envelope (slice 1)
`[ab:from=<sender>|id=<id>|seq=<lane><n>|at=<time>|pri=<urgent|normal|fyi>|n=<chars>] <text>`, one line.
- **One formatter and parser** in `AgentBridge.Protocol` (`DeliveryEnvelope`), shared by host and app,
  called by `ReplyService` and `LiteDeliver.Frame`. The formatter adds `id` (HA `rid` or request UUID).
- **`from`** is the authenticated sender class, never text: `owner` (the owner's HA user, or a device
  paired to him), `system` (the host itself), `agent:<label>` (agent token or Lite principal; the
  cleaned `sender` label, `[A-Za-z0-9 ._-]{1,48}`, is shown, never trusted). Today `HaReply` drops
  the Send press's HA user id; slice 1 carries it into `ReplyRequest`. An unknown id is `agent`.
- **`at`** is the host's receipt time, Pacific, to the second with offset; it never changes, and any
  clock in the body is ignored. The time typed goes to the journal.
- **`seq`** is per session and per lane (`u7`, `q12`), reserved by the flushed pre-key `handoff` record. **Change** (ruling:
  at enqueue): class priority and drops reorder enqueued items; a crash can leave an unused number.
- **Escape:** after NFKC and removing format characters, `[ab:` in the text (any case) becomes `(ab:`.
- **Parser:** a header starts the turn or follows `\n\n`. Its body is the next `n` characters (the
  host types one collapsed line, so `n` is known). The rest, up to the next header, is
  `from=console` text kept verbatim, blank lines included: words typed at the machine, which the
  host never sees. **Change:** the ruling labels those `owner`; the host cannot see who typed them.
  If `n` does not match, the body runs to the next `\n\n` and the entry is flagged.

## Lanes and key path (slice 2)
- **Urgent lane:** owner and system by default; agent-urgent from slice 3. Text, then Enter (steer),
  one at a time through `DeliveryGate`. Order: by class (owner, system, agent), then arrival.
- **Queueable lane:** everything else, FIFO per session: envelope text, then ctrl+q, so the CLI
  queues it as its own turn. The host keeps the lane queue, hands the CLI at most 2, and never sends
  ctrl+c, esc esc or Up. `KeySend.Enter` becomes a submit key (none, Enter, Queue); the injector
  writes ctrl+q as a console key record (VK `Q`, left-ctrl).
- **Capability:** per terminal kind, default not capable. Not capable or unknown means fail closed:
  wait for idle and an empty box, then Enter; never type into a working session. **Change:** no live
  probe on production hosts. A per-kind table in code is filled from SRE live test 4, because a probe
  would launch sessions on owner machines, and a table is reviewable and fails closed.
- `RequireIdle` becomes the default except for the urgent lane. Until slice 3, nothing from an agent
  is urgent. The HA ack gains `queued`; `reply-session.ps1` maps it to exit 6 (otherwise it reads it
  as a refusal).

## Priority authority (slice 3) and hygiene (slice 4), as ruled
- **Priority:** `-Priority urgent -Reason '<text>'`; class decides. An agent's urgent without a reason is
  refused by name; over 3 per principal per hour is demoted to normal. Each request, grant and
  demotion is journaled with sender, reason and time, and the ack says which. The quota keys on
  the authenticated principal, never the label. **Ask:** on the HA path every agent shares one agent
  token, so its quota is per token until host-signed principals (#519) name the session.
- **Hygiene:** `-Topic` supersedes an unsent item from the same principal and topic. TTL: fyi 30 minutes,
  normal 4 hours, urgent none. Each delivery carries one summary line (`q=3 Quality 2, SRE 1`).

## Guarantees, each with its hosted-CI test (fakes for console, clock and transcript)
1. **Identity and journal.** Every delivery has `from`, `id`, `at`, and a `seq` once reserved. The journal
   is JSON lines beside the host diagnostics journal: `enqueued` (flushed before any ack), `handoff`
   (the pre-key reservation), `delivered` (id seen in a user turn), `acked`, `dropped`, `unconfirmed`;
   metadata, hash and length only. Test: one reply writes them in order, and its text is in none.
2. **Ordering.** Per session: urgent before queueable; the urgent lane by class, then arrival;
   queueable FIFO. The gap detector compares each lane's reserved `seq` with user turns once the session
   is idle. A missing or out-of-order number writes a journal line and a status warning, never a
   page or owner alert. Tests: an urgent overtaking 20 queued gives no warning; a lost `q5` gives one.
3. **No silent drops.** Proven removals (TTL, supersede, overflow, restart before handoff) write
   `dropped` with the reason in the summary line; ctrl+c, esc esc and other handoffs follow rules 4-5.
4. **At-least-once where provable, never duplicated.** Retry (at most twice, same id) only on positive
   evidence that nothing reached the CLI: the injector reports no key went, or the box still holds
   exactly these words (then only Enter is re-sent). Every other unconfirmed item (120 s; ctrl+q:
   next idle plus 60 s) stays `unconfirmed`: journaled, a status warning, the sender told, never
   retried. The parser shows a repeated id once and flags it. Tests: no key went retries once; a
   delayed transcript append, stale idle or pending ctrl+q handoff never retries; a repeat shows once.
5. **Fault injection.** Before the first key, a `handoff` record (session, lane, seq, id, at) is flushed,
   which also reserves the seq. On start, the journal is replayed: never handed off becomes
   `dropped: restart`; handed off is checked against the transcript (`delivered` if found, else
   `unconfirmed`); seq resumes above the highest reserved. No text on disk (it can hold secrets).
   Tests: kill before the record, between record and keys, and after the keys. CLI busy: stays
   queued. Key not accepted: rule 4. Duplicate request: one entry. Clock skew: a 1-hour queue
   delay and a false body time leave `at` as receipt time. Overflow: per session, 50 queueable
   (oldest fyi first, then a new normal is refused by name) and 10 urgent. Flood: 200 fyi plus 1
   owner message, and the owner message is typed first.
6. **One page:** at most 120 lines. Also tested: two agent replies plus an owner message in one
second give three entries, owner first; a forged `[ab:` stays one entry; console text with blank
lines between two envelopes stays one entry; a missing reason is refused; the 4th urgent in an hour
is demoted; supersede keeps the newest; the summary counts by sender; an expired item is journaled.

## Slices (one builder, sequential, each at most 250 changed lines, all sensitive)
| Slice | Content | Estimate | Due (PT) |
|---|---|---|---|
| 1 | `DeliveryEnvelope`, `n`, `at`, escape, authenticated `from` end to end, journal writer (**moved earlier**: every later drop needs it) | ~180 | packet Nov 14 12:00 |
| 2 | Lanes, reserved `seq`, `RequireIdle` default, fail closed, submit key and capability table, `queued` exit 6, restart replay; one plain child of slice 1 | ~240 | head Nov 14 18:00 |
| 3 | Priority authority, `reply-session.ps1 -Priority -Reason`, per-principal quota, journaled grants | ~130 | after 2 |
| 4 | Topic, TTL, summary line, confirmation by id, gap detector, bounded retry | ~200 | after 3 |

Over 250 splits first (2b: capability table and submit key; 4b: confirmation and retry).

## Live test (SRE Lead, disposable sessions only; the builder writes the harness)
After slice 2 (target Nov 15): 1, 3, 4 and 6. After slice 4: 2 and 5.
1. Urgent, 5 queued and urgent within 2 s: 2 steered entries and 5 separate `queued` turns, in order.
2. ctrl+c and esc esc mid-queue: nothing lost silently; every unconfirmed item journaled.
3. A long turn plus 20 fyi, then an owner message: the owner message arrives first.
4. Windows Terminal and conhost, kitty and non-kitty: fills the table; not capable waits for idle.
5. Kill the host with a non-empty host queue and CLI queue: only never-handed-off items drop; others follow rule 5; no duplicate.
6. `send_session_message` spike: steer or queue, sender, order, confirmation. Not adopted.

## Out of scope: app rendering (#2538), the reply slot (#1323), which host types (#2560), CLI changes.
