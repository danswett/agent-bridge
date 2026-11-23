# HA retirement gate counters

Design for [#2384](https://github.com/danswett/agent-bridge/issues/2384), S4-2 of
[#1396](https://github.com/danswett/agent-bridge/issues/1396). Source instrumentation
is staged; qualifying runs remain held.
This measures item 2 of fleet `docs/ha-retirement-parity.md` section 3:
one continuous run with at least 500 messages, 50 launches, 30 approvals and
20 stops, and zero HA fallbacks. It does not approve retirement or replace
the other parity evidence, HA-off rehearsal or owner's sign-off.

## 1. Basis and dependencies

Read against agent-bridge main `e16dc3674e21f4974dabd51b8bdee3ebd6ad07b3`:
`src/AgentBridge.Host/AgentAuditLog.cs` 1-296 (EOF);
`LocalApi.cs` 1-195; `Lite/LiteAgentSend.cs` 1-70 (EOF);
`SessionService.cs` 418-455 and 627-696, under the same host directory;
`src/AgentBridge.HomeAssistant/HaConnector.Launch.cs` 1-190 and
`HaConnector.Stops.cs` 1-170; `src/AgentBridge.Protocol/Stops.cs` and `Answers.cs`
in full; `docs/host-config.md` 424-490; `docs/design/agent-launch-scope.md` 320-415.
Fleet parity was read in full, lines 1-175, at
`837f5e0ebb628cbc5c8affd1c2300c16225e1619`;
its `messenger/PairShadow.ps1` was also read in full, lines 1-75.

For #2064 / PR #2066, read local head
`fc47ed1cdd58418e2ef755506139c153f8026d74`: `AuditCommands.cs` 1-128
under `src/AgentBridge.Host`, plus that commit's changes to `Program.cs`,
`docs/host-config.md` and `tests/AgentBridge.Host.Tests/AuditCommandsTests.cs`.
Engineering supplied the full #2384/#1401 issues and #2066 body/both receipts;
those messages were read. No other PR review comments or unlisted source ranges
were read. Engineering reports #2066 unmerged with both receipts on that head.
These are source observations, not live verification or receipt claims.

#1401 supplies owner-only daily `agents-*.jsonl` and `launches-*.jsonl`,
serialized appends, versioned hash links and fail-closed attempt writes.
Main's outbound message record is `attempted`, not delivery; agent answer,
launch and stop refusals are not successful actions. Person paths need wiring.
PR #2066 reads filtered metadata, not chain integrity or gate totals, and its
row allow-list does not expose proposed gate fields. It is not a gate reader.
A3 #2009 supplies chain/folder-lock verification; A4 #2010 supplies launch/stop
wiring; A5 #2011 supplies answer/lookup wiring. A6a is #2064 / PR #2066,
not the remaining A6 durable-head/archive/pruning work. The gate must not claim
integrity before A3 lands. Existing hash links alone cannot prove no tail was lost.

### Status at main 0416b9d7b (S1 definition of the remaining #1401 and row 25 work)

Checked against source and commit subjects only; not a live run.

* **#1401 dependencies landed:** A3 #2009 (#2222), A4 #2010 (#2017), A5 #2011 (#2013) and
  A6a #2064 (#2066, merged; the "unmerged" statement above is out of date). Later audit
  work also landed: archive (#3162), person-signed rotation (#3566, #3580, #3551).
* **Counter source in code:** `src/AgentBridge.Host/GateAudit.cs` defines the `path`
  (`relay`, `local`, `legacy-ha`), `actorKind` and control phases (`gate.start`, `gate.pause`,
  `gate.reset`, `gate.seal`); launch, approval and stop wiring exist in `GateLaunchAudit.cs`,
  `PhoneApprovalEvidence.cs`, `StopAudit.cs` and `HaRunning.cs` (red tests #2570, #2588,
  #2596, #2629).
* **Landed state (source, not a live run):** present are the durable audit head and
  `AgentAuditLog.Continuity` (slice 9, with continuity and checkpoint-recovery tests),
  `GateAuditProjection.Read/Project`, used by the launch, approval and stop wiring, and,
  since #3813, the windowed `GateAuditProjection.ReadWindow` and the `GateVerdict` reducer.
  No writer of the `gate.start`, `gate.pause`, `gate.reset` and `gate.seal` control rows
  exists, and none is wanted (see the window decision below). Still not built: the
  message-source adapter and a command that exposes the verdict to a person or client (7b).
* **Confirmed (owner, relayed by Engineering, Nov 22):** the #1401 audit log is the counter
  source for launches, approvals and stops (S4). A counter is still not evidence until the
  windowed reader and verdict run. S8 stays: HA remains possible as an optional client, and
  the slice 11 dual run is still the gate.
* **Window, not a run (owner, Nov 22):** the retirement counters read the audit log over a
  time window: the last N days with HA off, from a start date kept in config or the parity
  checklist. A restart is a new start date. There is no run ID, no person-started run and no
  run-control op: section 5 and slices 5 and 6 below are superseded, and slice 5b (the
  person-class run entry) and the 5a writer (#3789) are dropped. Windowed S4 (#3813) reads
  operations whose first row is on or after the start date, any run, skips `grant.*` rows, and
  reduces them with the 50/30/20 and 500 message thresholds and zero HA dispatches. Trim
  detection is per stream (launches and agents each have their own retained floor): evidence
  trimmed by retention into the window is INCOMPLETE (`retention-trimmed-into-window`), not a
  pass, and the remedy is a later start date. A hard need for a run boundary would return only
  as a temporary op deleted with the HA connector (S7), as a new change.
* **Row 25 gap in fleet:** fleet `ops/ha-retirement` scanned PowerShell only, so live Python
  that talks to HA was invisible; `pump/fleet_pump/wake.py` is not an HA caller. Fleet slice S5
  scans live `.py` too and allowlists today's live findings (24: 19 PowerShell and 5 Python),
  defined in fleet `docs/ha-retirement.md`.

## 2. Authority and path

The destination host owns launch, approval and stop counts. Neither the
relay nor a client increments totals. The relay transports authenticated requests;
clients display an owner-authorized read of host evidence, never upload totals.
Source-host send attempts and relay acknowledgements are not delivery evidence.
Reuse fleet PairShadow's delivered-message counting and messenger records; do not
rebuild that counter or also add host send/receive records to its total.

At trusted ingress, the host assigns `path`: `relay`, `local` or `legacy-ha`,
and `actorKind`: `person-client`, `agent` or `ha-component`.
Use the authenticated channel/credential and the internal HA connector entry,
not a request's actor, transport, timestamp or success fields. Carry this context
unchanged through the shared action service; local pipe is never relabelled relay.
The host derives any agent principal/chain from the accepted credential.

A fallback is a command dispatched through the private HA connector/service path
instead of the normal Slipgates path, including explicitly selected HA rollback;
there need not have been a preceding relay failure. Record it before dispatch,
even if execution later fails or is unconfirmed. Stop confirmation commands count
as HA-path use too. A refused request that never reaches action dispatch is a
refusal, not a fallback. HA presence reads and shadow publication are not actions.
The future optional HA component using the normal authenticated relay protocol
has `actorKind: ha-component`, `path: relay`: it is not a fallback.
Actor identity alone must never be treated as proof of route.

## 3. Units: one successful logical action

| Counter | Credit only when the destination host confirms | Never extra credit |
|---|---|---|
| Message | One distinct delivered record from the existing trusted fleet message recorder | Source send, relay receipt, chunks, progress, attachment or duplicate acknowledgement |
| Launch | One fresh session started with a confirmed new session identity | Reservation, trust question, refusal, retry, result readback, resume or transfer |
| Approval | One current question answered and delivery confirmed | Each field, notification, dropdown, stale answer, refusal or unconfirmed typing |
| Stop | One session actually ended, with terminal `ended` | First confirmation press, busy/refused/failed result, retry or already-ended readback |

Replies and agent messages share the message unit; answers are approvals, not
messages as well. A multi-field form is one approval. Count a negative decision
when its answer is delivered: approval means a completed decision, not "Allow".
Confirmed forced termination counts as one stop; it does not waive the
separate working-session/idle-only rehearsal requirement. Local-path successes
are shown separately but do not earn relay replacement credit for item 2.
HA successes are shown for diagnosis and never earn gate credit.
Only distinct relay-path successes in the active run earn the four thresholds.

## 4. One audit store, no mutable counter database

Extend #1401's existing audit records; do not create counter files, client totals
or a second journal. Launch evidence stays in `launches-*.jsonl`; other actions
and run-control markers use `agents-*.jsonl`. Totals are a reproducible projection.
Keep existing audit semantics: `attempted` or `reserved` does not become success.
The existing fleet message evidence remains its named source, not a new store.

Versioned metadata: host-generated `operation`, `run`, monotonic
per-profile `sequence`, `phase` (intent/terminal/control), `path`, `actorKind`,
action, validated target/incarnation, outcome and bounded reason code.
Record a question identity for approval dedupe, never its title or fields.
Agent principal/chain come only from credentials, never a request. Person and
connector records use host-derived non-agent attribution; review the schema with
#1401 readers so neither is mislabelled as an agent or an agent as the owner.
Never add prompt/message/answer text, credentials, confirmation tokens, paths,
notification payloads or client-supplied free-form reasons. Existing hashes are
not needed for gate counting; gate records must not hash answer contents.

Use the audit folder's single writer/lock and durable flush before action
dispatch. Map authenticated principal, validated request and target incarnation
to one host operation; persist that mapping in the intent before side effects.
Retries/readbacks reuse it across restart. A conflicting request is refused.
Approvals additionally dedupe by question incarnation; stops by ended session
incarnation; launches by resulting session. Reused message records are deduped
by their retained source/run/pair/sequence identity, not text or a new caller ID.
No new counter protocol may turn an already-served request into another action.

The terminal record reuses the operation and is appended after the host-confirmed
effect. Replaying intent plus terminal yields at most one credit. Duplicate
identical terminals do not add credit; conflicting terminals invalidate the run.
An intent without a provable terminal after crash stays unresolved, not success.
Recovery may append a reconciled terminal only from authoritative action state;
otherwise report INCOMPLETE, never retry a side effect to repair the count.
An append/flush failure before dispatch refuses the action. Failure after an
effect leaves uncertain evidence and blocks the gate, even if a client saw success.

Slice A (#2521) wires app/host-CLI launches through the local handler. Without a
trusted run context, `run` is the empty UUID: `missing-run-context`, never credit.
Intent target is the request UUID; operation/incarnation use the reserved launch
UUID; terminal target is the registered session UUID. Slice B (#2587) restores
fresh-launch request bindings from these same records: confirmed readbacks reuse
the session, unresolved intents never start another process, and conflicts refuse.
The opaque launch binding reuses native launch-request hashing, never answer hashing.
Program's authorized relay entry and HaRunning assign relay/person-client and
legacy-ha/ha-component respectively; the pipe stays local. Only an explicitly
assigned control-writer run context supplies a run; there is no automatic provider.
The run-control writer remains a later slice; this adds only its nullable context seam.

Slice 3 (#2522) audits app/host-CLI answers on the local handler: empty run means
zero qualifying credit. A flushed intent follows native-question validation and
precedes keys; authoritative delivery (including denial) closes it once. Question
identity hashes only version/backend/session UUID/native call or generation;
permission display digests and all contents are excluded. Credentialed agents
remain refused: one terminal/refused row for a matching host question, no dispatched
intent and no credit or unresolved attempt. Other local callers, restart restoration
and relay context remain deferred; no automatic run provider is added.
HA context (#1401, HA deletion gate E4(f)): an answer pressed on a Home Assistant card is audited as its
launches and stops are: path `legacy-ha`, actorKind `ha-component`, principal `ha:connector`, the intent
flushed before typing and a terminal after the confirmed delivery (`HaAnswerAudit`), so the windowed gate
counts an HA approval as an HA dispatch; before this an HA approval left no row and could not read as a
fallback. **OWNER CHANGE, pending:** an HA answer whose intent cannot be written is refused, as a launch or
a stop from the card already is, so an HA user whose audit is unwritable cannot approve from the card.
Quality does not arm this change before the owner answers.
All three HA actions (launch, stop, answer) now carry the one principal `ha:connector`, composed in one place
(`HaRunning`), and a test per action drives that composed delegate to its audit rows and to the gate's HA count.
Launch rows written before this carry the principal `ha-component:local`; path `legacy-ha` and actorKind
`ha-component` are unchanged, so those earlier rows still read as the same connector and still count.

Slice 4 wires app/host-CLI, authenticated relay and internal HA stops. Empty run
still earns no qualifying credit. A flushed intent follows native stop guards
and precedes confirmation arming or dispatch; refused/busy/failed pre-dispatch
results have terminal-only evidence only when the process incarnation is known,
not HA fallback intents. Only `ended` credits the process incarnation. Replay
uses retained request/principal/incarnation bindings; changed incarnations and
uncertain cache misses after restart refuse effects. No confirmation token is
retained, and terminal-write failure leaves unresolved intent. A6 continuity
remains required.

At restart rebuild operation mappings, active run and totals from both streams,
checking supported versions, sequence/link continuity and the #1401 durable head.
A torn last line, missing tail, unknown outcome,
unresolved intent or audit failure is incomplete evidence, never zero fallbacks.
An established coverage break, unlike evidence not yet verifiable, voids the run.
Preserve all active-run records and anchors through rotation/archive. Do not
prune them into a passing suffix; if bounded retention cannot preserve them,
invalidate the run visibly and start again only after evidence health is restored.
The host's same-user filesystem trust limit remains: hashes cannot defeat a
same-user program able to rewrite all evidence and its anchors.
A6 continuity (slice 9) is required before starting a qualifying run. Missing
restart proof makes evidence incomplete; retained observations cannot earn credit.

### 4.1. Engineering clarification v1 (2026-11-14, #2524 / 9A)

A complete record missing only its final LF continues through existing framing
recovery, including gated appends, regardless of record age or actor. Keep
`audit-incomplete` visible across restart: no continuity certification or qualifying
credit. This does not change genuine partial-JSON ordinary crash recovery.
Deleted run-evidence days/records are established coverage loss: visibly INVALIDATE
the run with `coverage-break`, retain observed totals, and allow zero eligible
credit. Diagnostic appends may continue with monotonic sequences only if the
fault remains durably detectable across restart; actual append/flush failure
refuses the action. Never silently reanchor into a healthy suffix.
Reads change no bytes; permitted appends may extend files, not rewrite pre-existing
fault evidence. 9A detects/retains faults only: no truncation, erasure, recreation
of missing evidence, clean suffix, automatic reset/start, new store/journal or
repair command. Repair/reset execution remains outside 9A; reset alone does not
restore evidence health. Archive enumeration and active-run preservation remain
9B; neither cut completes #2524 alone or admits a qualifying run.

This explicitly clarifies previously unspecified append policy, based on section 4
lines 119-122 / 139-146 and section 6 lines 203-204 at frozen head
`020a2144705fce430a0d6a309373285f977ad3f6`, section 5's void-run/admission rules,
and section 6's INCOMPLETE torn-tail versus FAIL coverage-break table.

9A uses v1 checkpoint metadata in `audit\agents.head` / `audit\launches.head`:
retained root, flushed tail, highest sequence and sticky fault, never totals or
a second journal. Neither diagnostic appends nor framing recovery clear faults.

### 4.2. Corrective retention and upgrade rules (2026-11-15, Q12 / #2625)

Ordinary agents-file retention is the sole root-advance exception: under the writer
lock, verify the retained root and chain, then atomically checkpoint the successor root
and hash BEFORE deleting the exact old file. Optional v1 `retiringRoot` identifies
only that authorized deletion across a crash; `retainedThrough` persists the highest
removed gate sequence. Preserve tail/length; never lower sequence, advancing it if a
flushed retiring row was ahead of its checkpoint. A failed deletion may
leave that one old file present, not authorize losing any other file. Never advance
a root with coverage loss or an unverifiable chain. External loss remains `coverage-break`.
Physical continuity after authorized retention permits ordinary launches, including
sequence gaps at or below the retained watermark; duplicates and later gaps still
refuse. Gate run reports conservatively INVALIDATE after gated evidence is pruned,
retain observed totals and award no eligible credit. This is not active-run archival
or permission to certify a passing suffix; qualifying runs remain blocked on 9B.

Sticky `audit-incomplete` alone does not block retention and remains in advanced
heads. Coverage breaks or unverifiable roots/chains skip pruning with a host-log
warning; history may then exceed its retention target. Sticky `audit-incomplete`
still refuses launches until evidence health is repaired; 9A provides no repair.

Upgrade bootstrap is an explicit writer operation before launch preflight, also used
by gated append: only with NO heads, verify both existing chains before checkpointing
their present roots/tails and sequence cursors. It adopts retained history, cannot
prove pre-upgrade completeness or detect prior deletion, and earns no historical
qualifying proof. Existing heads/faults are never reset; partial head loss stays
incomplete. Recovered fragments or missing LF remain durably `audit-incomplete`.
The optional sticky `adopted` marker keeps run projections INCOMPLETE even when
physical replay is healthy; a later writer cannot silently certify old coverage.
Read-only inspection never bootstraps. Same-user removal of both heads is
indistinguishable from first adoption, within the existing filesystem trust limit.
Both lock opens retry sharing contention for about two seconds, then refuse.
A flushed extension with the anchored prefix intact is `audit-incomplete`, not
coverage loss; a shortened/changed prefix still fails. Replace stale `.head.new`
only under the writer lock; checkpoint replacement remains atomic and flushed.

## 5. Continuous run and reset

**Superseded (owner, Nov 22):** the counters are read over a time window from a start date,
not over a started run; there is no run-start, pause, reset or seal control. See the "Window,
not a run" status note in section 1. The text below is kept as the design record and for a
temporary run boundary should one be needed again.

The person-class control entry points are `start`, `pause`, `reset` and `seal`.
Each requires an authenticated person-class channel and host-side person
authorization, never the local pipe or shared OS-user identity.
Agents and read-only audit clients cannot start, reset, repair, pause or seal it.
Record a host-generated run ID and the host/profile identities of
all active hosts in the approved fleet roster, pinned at run start.
Record deployed instrumentation version and starting heads in audit markers.
Include the existing message recorder identity and starting source cursor/digest.
Membership is fixed for the run; any roster membership/identity change mid-run voids the run.
No host roles are assigned; coordinators can live anywhere.
All participants must attest coverage of relay, local and legacy HA action
entries before admission opens. Old daemon/private script bypasses must either
be covered or disabled by the person; unobserved routes make the gate unavailable.
No fallback inferred from the absence of an event is evidence.

Prepare every participant with action admission paused; install the same run
membership and begin markers everywhere before releasing admission. This barrier,
not client time or clock ordering, defines the beginning. All traffic, not only
test-tagged actions, is observed. Hosts cannot be silently removed from membership.
Only that person-class control may pause admission, for at most five minutes
per prepare/seal barrier; repeated calls cannot extend the host-enforced deadline.
Persist the UTC pause deadline and control timestamp in the audit control marker;
use a monotonic timer while live. An unreadable clock or one earlier than the
last recorded control timestamp makes the deadline uncertain and voids the run.
On timeout, control loss or restart with an uncertain deadline, void the run and
resume admission. Retain every queued request for normal revalidation; drop none.
Restart is allowed only with verified complete replay and unchanged coverage.
An offline pinned host or incomplete replay makes the report INCOMPLETE;
missing/unreachable hosts cannot be omitted.

Any fallback on any participant invalidates the whole run and resets eligible
message/launch/approval/stop credit to zero, not just that action's counter.
Keep the failed run's observed totals and fallback event for diagnosis.
No automatically chosen clean suffix can pass. A new run needs fresh all-host
begin markers after the fallback or coverage fault has been addressed.
`reset` voids the current run, issues a new run ID and requires fresh all-host
begin markers. It never zeroes an existing run in place or erases its evidence.
Refusals and failures remain visible; they earn no success credit.

## 6. One operator view and verdict

Windowed form (owner, Nov 22): read by `--since <start date>` in place of `--run <id>`; PASS,
FAIL and INCOMPLETE and the exit codes are unchanged, and the seal and pause steps below do
not apply. Windowed S4 (landed, #3813) supplies the projection and reducer
(`GateAuditProjection.ReadWindow`, `GateVerdict`). The command exists (HA deletion gate E4(f)):
`agent-bridge-host audit gate --since <ISO time with offset> [--profile <name>] [--messages <file>]
[--host <id>] [--json]`. It is read-only (no writer, lock or profile is created) and exits 0 PASS,
1 FAIL, 2 INCOMPLETE. `--messages` is `{"relayDelivered": n, "haFallbacks": n}` from the fleet's
send log; without it the answer is INCOMPLETE (`missing-message-evidence`), never zero fallbacks,
and a negative, malformed or incomplete file is refused. `--json` prints a closed schema-1 reading
for the fleet combiner, which takes one `<host>.json` per pinned host, applies the floors to the
fleet sums and names a host with no file INCOMPLETE: `schema`, `host` (the `--host` label, which
`--json` requires; the command never reads an identity), `since` (UTC), `kind` (`pass`, `fail`, `incomplete`),
`reasons` (closed codes only: `invalidated-window`, `ha-fallback`, `coverage-break`,
`retention-trimmed-into-window`, `unresolved-intent`, `audit-incomplete`, `sequence-break`,
`missing-window-evidence`, `missing-message-evidence`, `below-floor:<launch|approval|stop|message>`),
`relay` (`launch`, `approval`, `stop`), `messages` (`relayDelivered`, `haFallbacks`, or null),
`invalidated` and `legacyHaDispatches`. Per-host floors are never expected to be met alone, so
the combiner ignores `below-floor` codes. A `GateVerdict` PASS covers one host only and is never
retirement evidence by itself: the combiner must see every pinned host, and a missing or offline
host makes the whole answer INCOMPLETE. The readings are unsigned SRE-held files, so a fleet PASS is advisory.

Propose `agent-bridge-host audit gate --run <id> [--json]` on the existing audit
reader, after #2066. This is a new interface to build, not a command available now.
It obtains owner-authorized host snapshots over the normal authenticated
Slipgates channels; the relay cannot assert their contents. No pasted client
totals, cached roster counts or unsigned imported JSON may certify the gate.
Bind each host response to the requested run, membership, fresh reader challenge,
instrumentation version, durable heads and verified read-through sequence.
Reject duplicates, mismatched identities, replays, partial pages or stale replies.
An offline pinned host or missing fresh-challenge response yields INCOMPLETE naming
that host, exit 2; never pass using only the reachable participants.
The host verifies under its existing audit writer; the CLI read path must not
instantiate a writer, create locks/profiles or repair files to obtain a verdict.
Read PairShadow inputs from the enrolled trusted fleet recorder, not a caller's
summary. Seal its source range/digest with the same run and fresh read challenge;
recompute its existing delivered counts once, rejecting duplicate source identities
or missing ranges. This small gate adapter is not a replacement message counter.
PairShadow currently marks fallback only for `transport=ha, route=relay-first`.
The adapter must also reject/count HA-only rollback as fallback, including failed
HA dispatch from recorder events. Missing route/dispatch evidence gives
INCOMPLETE and exit 2, as with other missing evidence, never zero fallbacks.
Do not copy raw records' message/error text into the audit.

For a final verdict, quiesce admission on all participants, drain operations,
and append seal markers with final heads before collecting fresh snapshots.
Keep admission paused until all seals are collected or the bounded pause expires;
any HA dispatch still observed during sealing invalidates the run.
An incomplete seal is not a partial pass. Later traffic needs a new run;
a sealed report states its historical boundary and never certifies future traffic.
Readers cannot append evidence or supply the route/outcome used by the reducer.

One view shows run/membership and coverage, per-host and fleet-wide totals by
action, path and actor kind, unresolved/failed/refused counts, fallbacks and
reason codes, evidence heads, boundaries and verdict. Display dates in Pacific;
audit timestamps stay UTC. Do not expose session contents.
`PASS` requires all sealed evidence complete and A3-verified, no unresolved action,
zero legacy HA dispatches, and fleet-wide relay sums >= 500/50/30/20 respectively.
Per-host totals are displayed only; no host must individually meet those thresholds.
`FAIL` identifies fallback or a known invalidated run, with zero eligible credit.
`INCOMPLETE` names missing evidence/hosts or unmet thresholds; it is not a pass.
Exit codes: PASS = 0, FAIL = 1, INCOMPLETE = 2.
Faults take precedence over thresholds; where both fault classes occur, FAIL wins.

| Fault | Verdict / exit |
|---|---|
| HA fallback; conflicting terminal | FAIL / 1 |
| Roster membership/identity or established coverage break | FAIL / 1 |
| Pause timeout or another pause/deadline-uncertain void | FAIL / 1 |
| Crash-unresolved intent; missing or offline host | INCOMPLETE / 2 |
| Torn tail; stale or partial reply | INCOMPLETE / 2 |
| Missing route evidence; unmet thresholds | INCOMPLETE / 2 |

This view proves item 2 only. Quality retains gates and retirement sign-off.

## 7. Later slices and rehearsal dependencies

Estimates include focused failing tests; each PR must measure at most 250 lines.
Split further before implementation if an estimate does not fit. No code ships here.
Required for measurement: schema/reader, launch, approval, stop, run lifecycle,
gate view (including the existing-message-source adapter), and A6 continuity.
Implementation order: 1-4, then 9, then 5-7; 8/10/11 are later hardening.
No qualifying run starts before slice 9. Deferred work is not working today.
In every row below, A3 = #2009, A4 = #2010, A5 = #2011;
A6a = #2066, and A6-rest = the remaining #1401 work.

| Slice | Work / timing | Rough lines | #1401 dependencies / proof |
|---|---|---:|---|
| 1 | Schema/reader, required | 180 | A3 + A6a; consume A4/A5 outcomes; no A6-rest prerequisite for reading |
| 2 | Launch wiring, required | 210 | 1 + A3/A4; A5 attribution compatible, A6a reader; A6-rest is a proof dependency, not merge prerequisite |
| 3 | Approval wiring, required | 220 | 1 + A3/A5; A4 pattern, A6a reader; A6-rest is a proof dependency, not merge prerequisite |
| 4 | Stop wiring, required | 200 | 1 + A3/A4; A5 attribution compatible, A6a reader; A6-rest is a proof dependency, not merge prerequisite |
| 5 | Superseded by the window (owner, Nov 22): run prepare/reset | none | dropped, with 5a (#3789) and 5b |
| 6 | Superseded by the window (owner, Nov 22): run seal/fresh snapshot | none | dropped |
| S4 | Windowed projection and verdict reducer (replaces 6 and the reducer half of 7); landed, #3813 | 454 | 2-4 + 9 + A3/A4/A5/A6a; start date from config or the parity checklist; no run ID |
| 7b | `audit gate --since` command: landed, with the fleet combiner as its consumer (fleet `Get-HaFallbackGate`); message evidence comes from the fleet send log | 454 | S4; A6-rest required; stale claims fail |
| 8 | Shared durable operation-map hardening, later | 220 | 2-4 + A3/A4/A5/A6a; A6-rest required; core wiring already dedupes or invalidates uncertainty |
| 9 | A6 durable-head/archive continuity, required before 5 | 230 | A3/A4/A5 + A6a; completes A6-rest in separately measured cuts |
| 10 | Receiver/message provenance hardening, later | 160 | Reuse PairShadow; A3/A4/A5/A6a consumers unchanged; A6-rest for restart; no rebuilt counter |
| 11 | Extended rehearsal fixtures/instructions, later | 160 | 7; exercises A3/A4/A5/A6a and required A6-rest; required cases already in each core slice |

Parity rows 1/25/26 (messages), 6/7/22/23 (launches), 5/17/28
(decisions and phone path), 4 (stops), and 12/19 (audit attribution) depend on
these counters for item-2 evidence. The HA-off rehearsal in section 3 item 3
uses the same view to expose accidental HA routing while exercising each row.
Per-client phone delivery, away-from-home behavior, idle-only refusal, launch
scope/caps, and zero lost/wrong-target actions still need their own evidence;
aggregate success counts alone do not prove any of those acceptance criteria.
