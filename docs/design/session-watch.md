# Host-native session watch (#4262)

Part of the fleet-tools-into-Slipgates work (#1232, #1238). Cites
[fleet-tools-to-host.md](https://github.com/danswett/slipgates-fleet/blob/main/docs/fleet-tools-to-host.md)
(rule 5: "anything on a timer is a candidate to be run by the host"; security section: closed refusal codes, role limits in the host) and
[fleet-tools-migration-plan.md](https://github.com/danswett/slipgates-fleet/blob/main/docs/fleet-tools-migration-plan.md)
(no HA fallback, one opt-in flag, no tokens in moved tools).
**Status: design only.** No code, setting or script changes with this note.

## Summary in plain English

Today each coordinator runs its own 5-minute script that sweeps the roster and messages people. When nobody ran one, a lead sat 1 h 45 min
at a hidden prompt (#4241), builders idled 90-203 min, and about 150 finished sessions overloaded a VM (#4248). The host already knows every
signal. This note has each host watch its own sessions and raise a short, deduplicated notice to the person (app and phone) and to the agent
that launched the session (native relay `agent.send`). It never answers a prompt and never touches Home Assistant.

## Placement

- One `SessionWatch` loop per host, a hand-rolled `PeriodicTimer` like `ProcessLoadSampler` (`Host.Core/Load/ProcessLoad.cs`) and the 5 s relay
  loop in `Host/Program.cs`. It reads `ISessionSource.Snapshot()` (`Host.Core/ISessionSource.cs`): `Session.Status`, `StatusSince`, `Question`,
  `Activity.Background`. It subscribes to `Changed` so a state change is seen at once; the timer (default 60 s) covers time passing.
- Each host watches only its own sessions. No component is tied to a machine (owner rule, 2026-11-09): any host with live sessions runs it, and
  no coordinator or VM is needed. A launcher on another machine is reached over the relay, not by reading another host's roster.
- The decision logic is a pure function `Evaluate(snapshot, now, settings, ledger) -> notices`, so it is tested without timers or a relay.
- Settings: one flag `sessionWatch.enabled` (default on; see defaults) and the thresholds below, in host settings.

## The five rules

Defaults are the numbers in #4262. A session is judged only while its host is online and its snapshot is fresh.

| # | Rule | Fires when | Goes to | Default |
|---|---|---|---|---|
| R1 | At a prompt | `Status == Waiting` (a `Question` or `Attention` is pending) for N min | person; launcher agent if any | 5 min |
| R2 | Idle with work | `Status == Idle` for N min, launched with assigned work (launch record has a launcher), not finished | launcher | 20 min |
| R3 | Missing | a launch record from agent X says `Registered` and the session id is absent from the snapshot with no end audit entry | launcher | next pass |
| R4 | Finished and idle | the session delivered and has been idle past a limit | person gets an "End it?" offer; an agent end grant may end it | 30 min, offer only |
| R5 | Host health | CPU over a limit, an update pending, or online-but-not-receiving | person | CPU 85% for 5 min; update pending 60 min |

Never answered: the watch reads state and sends notices. It never types into a session and never answers or approves a prompt (#1003 is the
separate app path for that).

### R1 to R4 details

- **R1** reuses the existing stuck-prompt plumbing instead of a second one: `PushNotifier` already has the folder-trust notice
  (`TrustQuestions`, `EnqueueTrust`, `TrustWaitAfter` = 2 min, "documented, no setting yet"). The watch adds a `Watch` notice built the same
  way (one host-built text, per device, latest wins in the outbox, OS tag collapses repeats). The A1 evidence (`FleetPublisher.Evidence`)
  is unchanged.
- **R2/R3 owner** is the launcher from the launch record (`RestoreLaunch.Launcher`, launch status rows). A session with no launcher (a person
  started it) gets R1, R4 and R5 to the person only. R2 and R3 apply to launched builders and reviewers.
- **R3** needs a "deliberately ended" test so a normal finish is not "missing": the end audit line (`fleet end`, stop service) clears it.
- **R4 auto-end** only through the existing grant path: `fleet end` with the agent's own session credential and a grant that covers that
  launch (`fleet-grants-cli.md`). Without a grant the host only offers "End" to the person. The watch adds no new way to stop a session.

### R5 host health

- CPU: `ProcessLoadSampler` already measures `CpuPercent`, `RunQueue` and `AvailableMemoryMiB`; limits mirror `docs/fleet-load-limits.json`.
- Pending update: `AutoUpdater.Pending` / `HostUpdates`; one notice per pending version.
- Online but not receiving (#4248): the host has no host-level signal today (only session-level `DeafSessionWatch`). **Default taken:** notify
  when the host's own delivery counters (`PushNotifier.NoteDeliveryFailure`, `ReplyService.Measured`) show N unconfirmed replies in a window
  (3 in 10 min). This is the last slice (S5) with its own tests. A relay-side "host stopped acking" probe is out of scope here.

## Deduplication

- Key: `(rule, session id or "host", state-since)`. A notice is sent once; the same key is not sent again within 30 min (the #4262 rule).
  If the condition is still true after 30 min it repeats once per 30 min, up to 3 times per episode, then stops until the state changes.
- The state-since part makes a new prompt a new episode even right after an old one.
- The ledger is in memory plus a small file in the host data folder (next to `PushStore`) so a restart does not re-send everything.
  **Default taken:** if the file is unreadable, send once and rebuild; a duplicate is better than a missed stall.
- A global cap reuses `PushOptions.MaxPerMinute` (20) so a bad pass cannot flood a phone or the relay.

## Delivery

- **Person:** `PushNotifier` (new `NoticeKind`, gated by `PushSubscriber.Wants`, suppressed while the person is active, as for other notices).
- **Agent:** the host sends through its own `ILiteSender.SendAsync` (`Host/Lite/AgentLiteSender.cs`), the native relay, to the launcher's node
  and session id, with the usual audit "attempted" and terminal lines. Text is at most 4096 characters, plain, one or two lines: session name,
  rule, time in state. **No HA fallback;** a failed send is audited and retried at the next dedupe window, never sent another way. If the
  launcher is gone, the notice goes to the person with "launcher gone".
- The sender identity is the host's own principal, so the audit shows the host raised it, not a script.

## What it replaces

The per-coordinator watcher scripts (template `watch-template.ps1`) and the disabled fleet stall alarm (slipgates-fleet #190, #197, #204, #744).
**Departure from the fleet doc:** `fleet-tools-to-host.md` section 2 lists `alarm/` as "Stays" (policy over the roster). #4262 and the owner's
Nov 23 words ("another thing that needs to be built into the host") move the roster-stall part into the host. This is reported to
Engineering as a change to that row, to be edited in a separate fleet PR; this note does not edit the fleet doc. Fleet-policy alarms (budgets,
PR stalls) stay in fleet.

## Tests first (each fails before the change)

Pure `Evaluate` tests, named as sentences: a prompt shorter than 5 min raises no notice; at 5 min it raises one, and not again within 30 min;
a new prompt after an old one raises a new notice; idle with a launcher notifies the launcher; idle with no launcher does not; a session gone
without an end entry is "missing" and one ended on purpose is not; a finished idle session gets an offer and no end without a grant; CPU
over the limit notices once per episode; a disabled flag sends nothing; the watch never calls a reply or answer API. Delivery tests use fake
`IPushSender` and `ILiteSender`.

## Slices (each 400 whole lines or fewer, target 250)

| Slice | Content |
|---|---|
| S1 | Pure `Evaluate`, ledger and dedupe, R1 and R2, settings and flag, tests |
| S2 | Person notices through `PushNotifier` for R1, plus the `Wants` toggle |
| S3 | Agent delivery over `agent.send` for R1 and R2, audit, retry |
| S4 | R3 missing and R4 finished-idle offer (and the grant-gated end) |
| S5 | R5 host health: CPU, pending update, delivery-failure signal |

## Defaults taken (no owner question raised)

Thresholds above; offer-only for R4; flag default on; in-memory plus small-file ledger; delivery-failure counter as the first
"not receiving" signal; no new HA or script path; the fleet `alarm/` row edit handled separately.

## Not in this note

Answering prompts from the app (#1003); ending sessions without a grant; cross-machine roster reads; fleet policy alarms.
