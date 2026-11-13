# HA retirement gate counters

Design for [#2384](https://github.com/danswett/agent-bridge/issues/2384), S4-2 of
[#1396](https://github.com/danswett/agent-bridge/issues/1396). Proposed, not shipped.
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
Confirmed forced termination may count as one stop; it does not waive the
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

Proposed versioned metadata: host-generated `operation`, `run`, monotonic
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
otherwise invalidate the run, never retry a side effect to repair the count.
An append/flush failure before dispatch refuses the action. Failure after an
effect leaves uncertain evidence and blocks the gate, even if a client saw success.

At restart rebuild operation mappings, active run and totals from both streams,
checking supported versions, sequence/link continuity and the #1401 durable head.
A torn last line, missing tail, unclassified in-run command, unknown outcome,
unresolved intent or audit failure is incomplete evidence, never zero fallbacks.
Preserve all active-run records and anchors through rotation/archive. Do not
prune them into a passing suffix; if bounded retention cannot preserve them,
invalidate the run visibly and start again only after evidence health is restored.
The host's same-user filesystem trust limit remains: hashes cannot defeat a
same-user program able to rewrite all evidence and its anchors.
A6 continuity (slice 9) is required before starting a qualifying run. Missing
restart proof makes evidence incomplete; retained observations cannot earn credit.

## 5. Continuous run and reset

Run start, pause, reset and seal require a person-class authenticated channel and
host-side person authorization, never the local pipe or shared OS-user identity.
Agents and read-only audit clients cannot start, reset, repair, pause or seal it.
Record a host-generated run ID, fixed participating four Windows host/profile
identities, deployed instrumentation version and starting heads in audit markers.
Include the existing message recorder identity and starting source cursor/digest.
Pin membership from the fleet roster source the person approves at run start;
any roster membership/identity change mid-run voids the run.
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
On timeout, control loss or restart with an uncertain deadline, void the run and
resume admission. Retain every queued request for normal revalidation; drop none.
Restart is allowed only with verified complete replay and unchanged coverage;
missing/unreachable hosts or incomplete replay block the report.

Any fallback on any participant invalidates the whole run and resets eligible
message/launch/approval/stop credit to zero, not just that action's counter.
Keep the failed run's observed totals and fallback event for diagnosis.
No automatically chosen clean suffix can pass. A new run needs fresh all-host
begin markers after the fallback or coverage fault has been addressed.
Refusals and failures remain visible; they earn no success credit.

## 6. One operator view and verdict

Propose `agent-bridge-host audit gate --run <id> [--json]` on the existing audit
reader, after #2066. This is a new interface to build, not a command available now.
It obtains owner-authorized host snapshots over the normal authenticated
Slipgates channels; the relay cannot assert their contents. No pasted client
totals, cached roster counts or unsigned imported JSON may certify the gate.
Bind each host response to the requested run, membership, fresh reader challenge,
instrumentation version, durable heads and verified read-through sequence.
Reject duplicates, mismatched identities, replays, partial pages or stale replies.
An offline host or missing fresh-challenge response yields INCOMPLETE naming
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
zero legacy HA dispatches, and relay successes >= 500/50/30/20 respectively.
`FAIL` identifies fallback or a known invalidated run, with zero eligible credit.
`INCOMPLETE` names missing evidence/hosts or unmet thresholds; it is not a pass.
Exit codes: PASS = 0, FAIL = 1, INCOMPLETE = 2.
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
| 2 | Launch wiring, required | 210 | 1 + A3/A4; A5 attribution compatible, A6a reader; A6-rest for restart continuity |
| 3 | Approval wiring, required | 220 | 1 + A3/A5; A4 pattern, A6a reader; A6-rest for restart continuity |
| 4 | Stop wiring, required | 200 | 1 + A3/A4; A5 attribution compatible, A6a reader; A6-rest for restart continuity |
| 5 | Run prepare/reset, required | 220 | 2-4 + 9 + A3/A4/A5/A6a; person-class control, bounded pause |
| 6 | Run seal/fresh snapshot, required lifecycle continuation | 230 | 5 + 9 + A3/A4/A5/A6a; no client-supplied counts; bounded pause |
| 7 | Gate reducer + existing message-source adapter, required | 240 | 6 + 9 + A3/A4/A5/A6a; A6-rest required; stale claims fail |
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
