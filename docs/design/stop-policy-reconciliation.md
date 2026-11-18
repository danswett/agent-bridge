# S4 stop policy reconciliation (#2396)

**Status: docs-only reconciliation and proof design, 2026-11-12 PT.**
Parent: [#1396](https://github.com/danswett/agent-bridge/issues/1396).
This note records the conflict behind
[#2396](https://github.com/danswett/agent-bridge/issues/2396); it changes no contract,
permission, implementation or acceptance gate.

**No approved idle-only decision was found. [Product's ruling on #2396][ruling]
selects parity:** idle ends on one press; non-idle requires a second press
within 10 seconds, then `/exit`, then forced termination after 12 seconds.
Person and system stops follow the same state rule, without new caller authority.
Product's [second ruling][edge-ruling] confirms that a question, unready launch
or unreadable transcript takes the two-press path without `/exit`, including
when the host reports idle. The one-press implementation edge is a known gap.
After a confirmed stop, later busy state does not prevent forced termination;
the legacy-bridge guard still does.

The inventory's idle-only expectation was not a replacement host policy.
The host, parity section 5 and stop contract agree on confirmed working-session
stops. The conditional 180-to-250-line idle-only enforcement cut is cancelled;
this change contains no enforcement code.

## What the host does today

Observed at agent-bridge `9a0acf32e28fca6e03542c0b7b35a20498789581`.
[Host stop](../host-stop.md), [parity section 5](../parity.md#5-ending-a-session)
and the [stop contract](../protocol/v1-sessions.md#sessionstop) describe these
semantics; [StopService](../../src/AgentBridge.Host.Delivery/StopService.cs)
implements them.

The table assumes a valid request, a live identified process, supported platform,
no active legacy bridge, and no competing delivery or stop.

| State or condition | Current result |
|---|---|
| Idle, no background work, no question, launch ready | One press types `/exit`; the host waits up to 12 seconds, then terminates that process if it remains. |
| Working | First press returns `confirm` and a token, without typing or terminating. A second press with that token within 10 seconds may end the session. |
| Waiting on a question | The same confirmation rule applies. Once allowed to proceed, the host skips `/exit` and terminates without typing into the question. |
| Idle with background agents or commands | Treated as non-idle: confirmation can authorize ending it, rather than the host refusing every attempt. |
| Initially idle, then working/waiting/background work before the typing turn | An unconfirmed request is checked again and returns `confirm`. An already confirmed request bypasses this non-idle recheck. |
| Question found only by the last Copilot transcript check | The host skips `/exit` and terminates. That check does not itself require another confirmation. |
| Copilot transcript unreadable at that check | Treated as possibly asking: no `/exit`, but termination remains allowed. |
| Launch has not been seen ready, including possible folder trust | No `/exit`; termination remains allowed. Launch readiness is not an idle-only refusal. |
| Session already ending | `busy`; no second stop is started. |
| Session/process gone, or discovery's process identity no longer matches | `stale`; no replacement process is ended. For a non-idle session, an arm for an old discovery identity does not confirm the new process. |
| Repeated request UUID | The remembered outcome is returned without acting again, for the configured 15-minute retention. A new UUID is a new press, subject to the same checks. |

Process identity is checked by PID and start time. A process handle is held during
the actual stop so termination cannot reach a replacement that reuses the PID.
Elevated, other-user and host processes are refused. The per-session delivery
claim spans the stop, and the shared typing turn serializes console input.
The legacy-bridge guard is checked before admission, typing and termination.

Before effects, the host marks the session `ending`; after confirmed process exit,
it refreshes discovery to remove the session. A failed stop restores its own
reported status. A process that remains after the graceful wait is not checked
again for working/background/question state before forced termination.
The late-busy termination behavior is consistent with Product's first ruling.
There is a narrower observation that must not be hidden: if discovery
still reports idle, `Doing(session)` and `Doing(now)` do not include launch
readiness or the last Copilot transcript check. Those checks suppress typing
later, but do not themselves arm a confirmation (`StopService.cs:178-181`,
`:247-253`, `:280`). Product's second ruling settles the intended behavior:
even when reported idle, these states require two presses and no `/exit`.
**Immediate untyped termination without arming is a known parity gap, not an
approved exception.** Engineering owns the separate fix,
[#2411](https://github.com/danswett/agent-bridge/issues/2411); this docs-only cut
does not fix it or claim its proof passed.

## What the retirement inventory says

Source: slipgates-fleet
[`docs/ha-retirement-parity.md`, section 1, row 4][inventory],
read at `7dbb65ab382f21a1ec4c4f1ea43011fe6fab4b8c`:

> Owner on the HA card; all clients | Stop a session: `button.<session>_stop` |
> Relay stop command, same idle-only guard, audited | 20 stops across all 4
> Windows machines; a working session is refused every time; zero wrong target | S4

Section 1a row 4 at the same revision reads:
"All clients (idle-only rule enforced by the host, not the client)".

The inventory labels the retirement plan approved by the owner, but its idle-only
wording conflicts with the confirmed non-idle host contract. The
[client-protocol decisions](client-protocol.md#decisions-and-open-questions)
left that discrepancy open and authorized no policy change. Product's #2396
ruling now selects parity, not working-session refusal. The fleet inventory
owner must correct section 1 row 4 and section 1a to
**"idle: one press; not idle: two-press confirm"**. Its 20-stop evidence must show
correct targets and confirmation behavior, not refusal of every working stop.
The separate S4 evidence-matrix correction must use the same rule.

## All-client proof design

This is a design, not a run or parity receipt. Use only disposable sessions and
test-controlled processes, never the owner's active sessions. Exercise the same
host through each client; hiding a button does not prove host enforcement.
Native Android and Linux adopt these cases when shipped; browser coverage does
not establish native-client parity.

| Client/caller surface | Required coverage |
|---|---|
| PWA and Windows app | Full stop matrix against a capable Windows host, including a direct first press while working. |
| Apple clients (Mac and iOS) | The same matrix against a capable remote host. Record unavailable local-host stop separately; it is not a remote-client pass. |
| MCP/session-agent and host CLI | Establish whether a stop entry point is authorized and advertised. Existing agent stop exclusion must remain; absent operations are gaps or explicit exclusions, not invented authority or passed tests. |
| Optional HA client | The same matrix once its authenticated caller path is approved and available; do not substitute a service credential for a person's action. |
| Authorized person/system paths | Apply the same state/confirmation cases to each existing authorized path. Record absent system authority rather than borrowing a person credential. |

For the already-implemented cases below, run three repetitions per offered client
path per rehearsal stage. The known-gap cases are separate, blocked evidence.

1. **Idle:** record idle with no background work or question, capture PID/start
   time, press once, observe `ending`, shutdown and removal. Repeat with an agent
   that ignores `/exit` to prove the 12-second grace and same-process termination.
2. **Working, waiting and background work:** the first press returns `confirm`
   and changes no process or input. Within 10 seconds, send the returned token
   with a new request UUID: only the same PID/start time may end. Waiting on a
   question ends without typing or answering it. Cover background agents and
   background commands independently.
3. **Confirmation, replay and identity:** omit, expire, cross-session and replay
   tokens; resend a request UUID; replace the target process and reuse its PID.
   Invalid confirmation does not end a non-idle process; repeated UUIDs do not
   act again; no replacement process is typed into or terminated.
4. **Protected targets:** test elevated, other-user, unreadable-start-time, host
   itself and no-live-PID targets. Assert no typing or termination. Keep the
   existing distinction between `refused` for an unidentified/disallowed target
   and `stale` for a process/session already gone.
5. **Transitions and guards:** become busy while awaiting the typing turn and
   after a confirmed stop; verify confirmation and the permitted forced path,
   respectively. Start the legacy bridge before typing or termination and prove
   its guard still prevents those effects. Use isolated guard fixtures.

The idle-reported unreadable-transcript, unready-launch and transcript-only
question cases are excluded from this already-working proof design. Their fix
and proof belong to #2411. Preserve the current one-press observation as gap
evidence, never as a passing alternative or as evidence covered by an ordinary
waiting-state test.

Retain the client/version, host build, request and outcome, confirmation timing,
PID/start-time identity, transcript and host log for each case. Prove no input was
submitted to a question and no unrelated process was affected. Missing evidence,
unsupported surfaces and source/ruling mismatches remain explicit, never green.

The continuous-run evidence is **20 stops across all four Windows machines**,
with correct targets, idle one-press behavior and confirmed non-idle behavior;
zero wrong targets. Working first presses must not end anything, but a valid
second press is not expected to be refused. Retain all-client repetitions as
well; the aggregate count does not replace them.

## Remaining reconciliation and follow-ups

Engineering owns #2411, the separate bounded fix for the idle-reported unsafe-input
gap under Product's second ruling. Preserve the same-process and replay
protections; no enforcement code is part of this note.
Client-specific proof, inventory/matrix corrections and any required contract or
fixture changes remain separate bounded work, with Trust review for
protocol/signing changes and independent exact-head review.

Reconcile checklist E5-01/02/03/04 against the cited ruling, rather than relabeling
historical runs: E5-01 supplies idle evidence; E5-02 supplies expired-confirmation
evidence; E5-03 covers question safety after confirmation; E5-04 covers other
agents. None proves the whole client matrix or the idle-reported edge by itself.

## Read boundary

Read in full: `host-stop.md`, `StopService.cs` (including the unsupported-platform
handler), protocol `Stops.cs`, and the cited fleet retirement inventory. Also
read: parity section 5, the `session.stop` wire-contract section and dispatch,
client-protocol caller/operation tables and
decisions, and relevant historical design and host-test passages.

Not read in full: all linked issue/PR bodies, the original legacy implementation,
all client stop implementations/tests, or signing contracts.
The S4 evidence-matrix file was absent from the agent-bridge revision
above; Engineering supplied its stop-row summary. Neither those excerpts nor the
historical live claims establish idle-only authority or prove the known-gap fix.

[inventory]: https://github.com/danswett/slipgates-fleet/blob/7dbb65ab382f21a1ec4c4f1ea43011fe6fab4b8c/docs/ha-retirement-parity.md#1-parity-checklist
[ruling]: https://github.com/danswett/agent-bridge/issues/2396#issuecomment-6843557043
[edge-ruling]: https://github.com/danswett/agent-bridge/issues/2396#issuecomment-6843606276
