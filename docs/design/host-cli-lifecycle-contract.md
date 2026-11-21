# Host CLI turn and question lifecycle contract (C8)

Contract and review-only event vectors for [#2496][issue], C8 in the
[S4 host CLI inventory][inventory], under [#1396][retirement].
Docs only: no notification implementation, new wire schema, executable verb,
person credential, focus grant, paging or alerting work.
Source baseline: `66811f055aae45052298dffda762fe417398843b`.

## Three distinct surfaces

| Surface | Existing exposure or required boundary |
| --- | --- |
| Local watch feed | [Program][cli] starts a local `SessionSource` and subscription. `watch --json` writes `{ev,data}` lines for snapshot and live events until cancellation; it does not write the channel's `seq` or accept a resume cursor. It is not a push subscription or display receipt. |
| Foreground terminal client | May render the feed's current sessions, questions and replies while attached. Printed bytes, a visible terminal or a detached watch process establish neither a person's attention nor background/locked delivery. No CLI notifying-client lifecycle is established. |
| Notifying person client | Needs its existing authorized person-device route, notification ingress, actual display, withdrawal and exact-target navigation. Host sends and stdout are not device display receipts. C8 specifies the gap, not a new CLI route or an exemption. |

The CLI reads local sources directly, not through a person-authenticated pipe
session. File access is not person authority or an anti-agent security boundary.
The [C1 contract][person] retains caller and operation restrictions; C8 grants
no answer, approval, launch, stop, focus or host-management capability.

## Identity and evidence

Use the full selected machine/session/question identity, never a title, list
position, question text or notification tag as a substitute.
The feed has local source context, not a new authenticated machine identity.
A future person client must establish its selected profile/machine through its
accepted transport; unknown or changed scope requires explicit re-resolution.

The existing [Activity contract][activity] distinguishes:

- `activity.turn`: the current prompt's turn, to which its question belongs.
- `activity.responseTurn`: the main agent reply's turn; it can remain the previous
  turn while the current prompt has not replied. Missing means unknown.
- `activity.turnCount`: completed user prompts, not assistant tool-call rounds
  or a globally unique turn ID. A delegate's text is not the main agent's reply;
  preserve `source.response`/`own.response` attribution.

Correlate a question with the selected machine, full session ID and `question.id`;
retain its known turn context without inventing a missing turn ID.
An answer continues that turn, not a new completed turn.
Current status, `updatedAt`, repeated response text or an idle snapshot alone
cannot prove a new successful turn completed.

Existing [push data][push] carries machine/session/question and kind, not a
finished-turn ID. `data.at` is send time, not turn identity. A notification
tag/topic collapses a session's notices; it is not question identity, proof of
display or an answer token. Do not add a C8 wire field to fill those gaps.

## Lifecycle requirements

**Bootstrap and reconnect.** The [source subscription][source] sends current
`session.upsert` records followed by `sessions.synced`, then live changes.
Build a new current-state view through that sync boundary. Snapshot content is
not a backlog of newly completed turns or displayed notices.
Duplicates and coalesced latest state are not additional lifecycle receipts.
Discovery-held state means incomplete discovery, not proof a question was answered
or a missing session ended.

The CLI exposes no channel sequence-gap or durable replay/resume contract.
After loss or restart, rebuild the view from a fresh subscription; do not pretend
missed transitions were observed. A future channel client follows the existing
[subscribe/resync rules][subscribe], including its own sequence/run boundaries,
not an invented stdout resume token. An existing host may notify an already-pending
question at startup under its own policy; that is not new CLI delivery evidence.

**Question replacement and withdrawal.** Repeated Q1 upserts update the same
question. When Q1 is answered elsewhere, remove its actionable terminal state.
A notifying client must reconcile its notice through the existing `resolved` or
replacement lifecycle, with actual device evidence still required.
If Q2 replaces Q1, a delayed Q1 notice or withdrawal must not authorize, answer or
erase Q2 as though it were Q1. Re-read current state before exact-question opening;
report answered, stale/replaced, missing, offline or unknown distinctly.
If only absence is known, report no longer pending/unknown, not a confirmed answer.
Opening is navigation only, never submission or implicit consent.

**Finished turns.** A main-agent completion must be attributable to its turn and
the observed transition, not a delegate, old response, failed sign-in or error.
Keep the existing [notification policy][notifications], including silent Done,
collapse, replacement and in-use suppression. C8 neither changes that policy nor
equates suppressed, queued, sent or replaced-before-display with displayed.

**Presence and focus.** An agent must never assert the person's focus, open
question or recent input to suppress notifications. Running a genuine CLI,
reading a question or having a host identity supplies no exception.
A person's terminal is not an implemented focus adapter either; any future
adapter must satisfy the existing [person-device focus contract][focus].

## Acceptance event vectors (requirements, not executed receipts)

M1/M2, S1, Q1/Q2 and T1/T2 are review labels, not new wire IDs.
Each sequence below is checked by document review; no product behavior is
implemented or executable RED invented by these vectors.

| Vector | Input sequence / condition | Required observation and evidence limit |
| --- | --- | --- |
| V1 | Initial S1 idle/T1 reply or pending Q1, then `sessions.synced` | Render current state, not a newly finished turn or a notice display. A host's startup-question policy remains separate. |
| V2 | Live S1/Q1 upsert, then repeated/coalesced Q1 upserts | One current question identity; no extra completion or display count from repeated lines. |
| V3 | S1/Q1 answered on another device; upsert no longer has Q1 | Terminal drops Q1 controls; notifying client must prove withdrawal/replacement separately. No inferred local answer receipt. |
| V4 | Q1 replaced by Q2; late notice or `resolved` still names Q1 | Q1 is stale; reconcile current Q2 without answering, opening it as Q1 or treating Q1's withdrawal as Q2's resolution. |
| V5 | Target S1 removed, or current lookup returns missing | Explicit missing/ended target, no fallback to another session or stale cached controls. |
| V6 | Notice names M2, another account, or an unverified scope | No automatic retarget, focus or action; require the authorized exact scope. |
| V7 | Disconnect between Q1 arrival and answer; fresh snapshot has no Q1 | Reconcile through new sync, retire stale controls, do not replay Q1 as new or claim the missed answer was observed. |
| V8 | Discovery held, incomplete snapshot or channel-client sequence gap | State remains incomplete; no inferred answer/removal. Channel clients resync under their existing contract, not stdout sequence assumptions. |
| V9 | Host run/profile/machine changes on reconnect | Prior currentness is invalid; explicitly re-resolve scope. No retained selection becomes permission. |
| V10 | T2 begins with `responseTurn=T1` and T1's response still present | Do not attribute T1's reply to T2 or infer T2 completion from idle alone. Unknown turn attribution stays unknown. |
| V11 | Observed main-agent T2 completes with its own reply | Terminal can show the attributable reply; stdout still proves no OS notice, background delivery or push-policy eligibility. |
| V12 | Delegate finishes, error/sign-in ends a turn, or only tool rounds advance | No successful main-agent finished-turn receipt from those signals; retain attribution and failure state. |
| V13 | Terminal hidden/detached/backgrounded or screen locked; watch later exits | No background/locked receipt. Once the feed closes it provides no continuing coverage or delivery guarantee. |
| V14 | Host send, collapse, suppression, queued push or replacement before display | Not a displayed notice. A synthetic event is not OS delivery proof. Preserve existing silent/collapse rules. |
| V15 | Notifying client actually displays, then Q1 answered elsewhere | Later proof must correlate client/OS display, host evidence and withdrawal; this docs vector supplies none of them. |
| V16 | Foreground/background/locked tap arrives before sync, or after Q2 replaced Q1 | Wait for authoritative current scope; open only exact pending Q1, otherwise show the explicit stale/answered/unavailable result; submit nothing. |
| V17 | Agent or agent-child CLI attempts a focus/presence assertion | No person-focus assertion, notification suppression, borrowed host/App identity or approval authority. |
| V18 | Finished push lacks turn ID; timestamps/text/tag match an old notice | Do not manufacture a unique turn identity or duplicate display evidence; retain the correlation limitation. |

## C9 read-only exact-question validation

| Command | Result and boundary |
| --- | --- |
| `question-target <session-id> <question-id>` | Refreshes the local source and checks both full IDs exactly. JSON identifies the selected tuple, `outcome` and known `turn`; only `pending` exits 0. No answer, focus assertion, notification registration or delivery occurs. |

`stale` means a different session/question was returned; `missing` means the
session is absent. `answered` requires explicit Answered/Approved/Denied evidence
for that question in the newest bounded history page, followed by a fresh target
check. Absence alone is `no-longer-pending`, never proof of an answer.
Discovery holds or inconsistent pending state report `unknown`; read failures
report `unavailable`. These exit 1 with the outcome on stderr; bad syntax exits 2.
The existing isolated `--read-root <absolute-home>` read seam is supported.
Scope is the local source, not an authenticated machine/person selection.
This validates a snapshot, not continuing currentness, answerability, notice
opening, background delivery or completion of row 28.

## Dispositions and retained work

[Engineering's disposition][engineering] retains row 16 background/locked
finished-turn and question delivery, and row 28 notice ingress, withdrawal and
exact-question opening as separate implementation/live-proof work.
[Product's own disposition][product] agrees with that retained-gap ruling as its
separate line, not joint acceptance.
Joint C8 acceptance/readiness is not claimed here; it remains pending the
responsible leads' acceptance. This document grants no later slice a ready state.

Keep those gaps in the S4 matrix and [CLI live-proof lane #2395][live].
C9's future read-only target validation is not notice ingress, withdrawal,
background delivery or live opening proof. Unsupported notification is a retained
gap, never a client exemption, completed row, parity claim or HA retirement grant.
The owner signs retirement; neither separate disposition nor this cut does.

No long-running component or new store is added. Watch and any terminal view run
only where invoked; losing that process/node ends the feed. Its view is local
and disposable, reconstructed from source rather than a designated fleet node.
Any future notifying client retains its existing device/host state contracts;
C8 creates no daemon, shared delivery ledger or machine-specific home.

Review checks V1-V18 and the three surface boundaries, not runtime delivery.
The existing hosted `docs-links` suite checks links; no executable test is
specified for this docs-only cut. Independent review, hosted verification and
later live evidence remain separate from this contract.

[issue]: https://github.com/danswett/agent-bridge/issues/2496
[inventory]: host-cli-s4-evidence.md
[retirement]: https://github.com/danswett/agent-bridge/issues/1396
[cli]: ../../src/AgentBridge.Host/Program.cs
[source]: ../../src/AgentBridge.Host.Core/ISessionSource.cs
[person]: host-cli-person-contract.md
[activity]: ../protocol/v1-sessions.md#activity
[subscribe]: ../protocol/v1-sessions.md#sessionssubscribe
[notifications]: ../protocol/v1-sessions.md#notifications
[focus]: ../protocol/v1-sessions.md#sessionsfocus
[push]: ../../src/AgentBridge.Protocol/Push.cs
[engineering]: https://github.com/danswett/agent-bridge/issues/2496#issuecomment-6871665164
[product]: https://github.com/danswett/agent-bridge/issues/2496#issuecomment-6908372247
[live]: https://github.com/danswett/agent-bridge/issues/2395
