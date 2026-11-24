# Host-native session watch (#4262)

Part of the fleet-tools-into-Slipgates work (#1232, #1238). Cites
[fleet-tools-to-host.md](https://github.com/danswett/slipgates-fleet/blob/main/docs/fleet-tools-to-host.md)
(rule 5: "anything on a timer is a candidate to be run by the host"; security section: closed refusal codes, role limits in the host) and
[fleet-tools-migration-plan.md](https://github.com/danswett/slipgates-fleet/blob/main/docs/fleet-tools-migration-plan.md)
(no HA fallback, one opt-in flag per tool, a cutover step, no tokens in moved tools).
**Status: design only.** No code, setting or script changes with this note.

## Summary in plain English

Today each coordinator runs its own 5-minute script that sweeps the roster and messages people. When nobody ran one, a lead sat 1 h 45 min
at a hidden prompt (#4241), builders idled 90-203 min, and about 150 finished sessions overloaded a VM (#4248). The host already knows the
signals. Each host watches its own sessions and tells the person (app and phone) and, when allowed, the agent that launched the session
(native relay `agent.send`). It never answers a prompt, never ends a session, and never touches Home Assistant.

## Placement

- One `SessionWatch` loop per host, a hand-rolled `PeriodicTimer` like `ProcessLoadSampler` (`Host.Core/Load/ProcessLoad.cs`). It reads
  `ISessionSource.Snapshot()`: `Status`, `StatusSince`, `Question`, `Finished`, `Owner`. It subscribes to `Changed`; the timer (60 s) covers
  time passing. It judges a session only while `ISessionSource.Held` is false (discovery held means absence decides nothing).
- Each host watches only its own sessions. No component is tied to a machine (owner rule, 2026-11-09).
- The decision is a pure `Evaluate(snapshot, now)` so it is tested without timers or a relay. Notice text carries durations, never
  timestamps, so clock skew between hosts does not matter.
- State comes from the roster (`Status`, `StatusSince`, `Question`), never from a transcript file's modification time. Just before
  sending, the host re-reads the session: same question id and still `Waiting` (R1), still `Idle` (R2), or the notice is dropped.
  After a host restart `StatusSince` restarts at the restore time; the ledger below keeps the first-seen time of an episode so a stall is
  not hidden for another N minutes.

## Who owns a session (durable owner record)

`RestoreLaunch.Launcher` is the agent CLI type, and `LaunchStatusLedger` keeps the launching principal only 15 min / 128 rows, so neither can
say who owns a session after 20 minutes. **New:** at launch the host writes one owner record from the AUTHENTICATED principal of the launch call
(the connection credential, never a message body, so a peer cannot forge it): `{session id, launcher node id and session id, launched-at, grant id}`.
It lives in the host data folder beside `PushStore`, is written before the launch reply, and is removed 7 days after the session ends.
If the record is missing (hand-started session, older launch, unreadable file) the session has **no owner**: R2 and R3 do not fire for it,
and R1, R4 and R5 go to the person only. The watch never guesses an owner.

## The five rules

| # | Rule | Fires when | Goes to | Default |
|---|---|---|---|---|
| R1 | At a prompt | `Status == Waiting` (a `Question` is pending) for N min | person; owner agent if any | 5 min |
| R2 | Idle with work | `Status == Idle` for N min, session has an owner record, `Finished` is not true | owner | 20 min |
| R3 | Missing | an owner record exists, the session is absent from the snapshot, `Held` is false, and no end was recorded | owner | next pass |
| R4 | Finished and idle | `Finished.Value == true` (the roster's finished evidence; unknown means no) and idle past the limit | person and owner (offer only) | 30 min |
| R5 | Host health | CPU over a limit, an update pending, or online-but-not-receiving | person | CPU 85% for 5 min; update pending 60 min |

`Attention` (for example a sign-in need) is not R1: it does not set `Waiting`, and it already has its own notice.
The watch never types into a session and never answers or approves a prompt (#1003 is the separate app path).

### R4 is offer-only

The host never ends anything. For a finished-idle session: the **person** gets a notice that opens the app's existing stop path, which shows
its own confirm; the person confirms there. The **owner agent** is told (in the digest below) and ends the session itself with
`fleet end` under its own session credential and grant (`fleet-grants-cli.md`). The watch adds no new way to stop a session and spends
no agent's grant.

### R5

- CPU: `ProcessLoadSampler` (`CpuPercent`, `RunQueue`, `AvailableMemoryMiB`); limits mirror `docs/fleet-load-limits.json`.
- Pending update: `IHostUpdates` (`Host/HostUpdates.cs`); one notice per pending version.
- Online but not receiving (#4248): no host-level signal exists today (only session-level `DeafSessionWatch`). **Default taken:** notify when
  the host's own delivery counters (`PushNotifier.NoteDeliveryFailure`, `ReplyService.Measured`) show 3 unconfirmed replies in 10 min (S5).

## Delivery

**Person:** `PushNotifier`. S2 reuses the existing `NeedsYou` kind with watch text instead of a new `NoticeKind` (a wire enum in
`Protocol/Push.cs`, a sensitive path): old clients then need no change. The existing question notice (2 s) and 15 min reminder stay;
R1 adds the "waiting 5+ min" escalation and replaces neither. Person notices are on by default.

**Owner agent:** `AgentLiteSender` needs a live session or script credential, and a receiver refuses a launcher it did not launch
(`notReplyTarget`). So there is **no new "host principal" kind** (that would change the signed envelope, a protocol and owner
decision, not taken here). **Default taken:** the watch sends under a person-issued script credential named `watch` (read+reply, `agent.send` only,
`script-credentials.md`). Until a person issues it in the app, agent delivery is off and the person alone is notified. The audit shows
`script:<machine>/watch`. The watch inherits the messaging switch, `RemoteEnabled` and the kill epoch: off, revoked or killed means no agent send.

**Person fallback:** a refusal (`notReplyTarget`, `off`), a launcher gone, or `busy` and `rate` still unresolved after the retries below
sends the notice to the person instead, saying why. No HA fallback, ever.

**Digest, caps and retry (storm control):** one message per owner per pass, listing session names or node ids and rule and duration, at most 10
lines then "and N more". The agent path is capped at 4 sends a minute per target (under `LiteDeliver.MaxPerMinute` 6 and the per-sender
`RateCap`); `busy` and `rate` retry after 60 s, up to 3 times, then fall back to the person. Text uses the launch name or node id only,
never a hand-started session's title (it can come from prompt text).

## Deduplication

- Key `(rule, session id, episode start)`. One notice per episode, then the same key waits at least 30 min (the #4262 rule).
- A still-open episode repeats every 30 min, at most 3 notices, then ONE final "still stuck, no further reminders" notice with the total
  duration, and then silence until the state changes. A new prompt is a new episode.
- The ledger (episode start, last sent, count) is in memory plus a small file in the host data folder. If the file is unreadable the watch
  starts empty and sends once; a duplicate beats a missed stall.
- Person path keeps `PushOptions.MaxPerMinute` (20); the agent path has its own cap above.

## When the watch itself fails

A loop that throws is restarted once; a second failure raises ONE person notice "session watch stopped" and the loop stays down until the
host restarts. The loop writes a last-pass time into host health, so the app shows a stale watch. The watch is host-local, so a dead or
wedged host (#4248) is seen by nobody on that host. **The coordinator scripts were the only off-host observer; they are not removed for that
case:** until a relay-side probe exists, each coordinator keeps one tiny check, "a machine in the relay roster is offline or has missing
sessions", and nothing else. If a node leaves, other nodes keep watching their own sessions; the leaving node's sessions are covered by that check.

## What it replaces, and the cutover

Replaces the per-coordinator roster watcher scripts (template `watch-template.ps1`, kept on VM4 only) and the disabled stall alarm
(slipgates-fleet #190, #197, #204, #744). **Departure from the fleet doc**, now an owner direction (Nov 23 about 18:00 PT, relayed by Chief S18 and
Engineering R10): `fleet-tools-to-host.md` lists `alarm/` as "Stays"; fleet PR #1306 moves that row to "Moves". Fleet-policy alarms
(budgets, PR stalls) stay in fleet.
**Cutover (no double wake-ups):** `sessionWatch.enabled` covers person notices (default on); `sessionWatch.agentDelivery` is a second flag, default
**off**. Scripts keep running until S3 evidence shows the digest reaching a launcher; then agent delivery is turned on per machine and that
machine's coordinator script is reduced to the offline check above. Making agent delivery default-on is a fleet-register decision for
Engineering, not taken here.

## Tests first (each fails before the change)

Pure `Evaluate` tests as sentences: a prompt under 5 min raises nothing; at 5 min one notice, none again within 30 min; a new prompt is a new
notice; after the cap one final notice then silence; idle with an owner notifies the owner, idle with no owner raises nothing;
`Finished.Value == true` is never R2; a session gone while `Held` raises nothing, gone with no end record raises R3, ended on purpose does
not; a queue-line or stale state is re-checked and dropped; a disabled flag sends nothing; the watch never calls a reply or answer API; ledger
corrupt starts empty; a restart keeps the episode start. Delivery tests with fake `IPushSender` and `ILiteSender`: 50 stalls under one owner
send one digest within the cap; `busy`, `rate` and `notReplyTarget` fall back to the person; agent delivery off or kill epoch set sends none.
**Mutation list per slice** (each mutant must fail a test): S1 threshold off by one, dedupe key without episode start, owner guessed when
missing; S2 text includes a title; S3 cap raised, fallback removed; S4 end called by the host; S5 counter window changed.

## Slices (each 400 whole lines or fewer, target 250)

| Slice | Content |
|---|---|
| S1 | Pure `Evaluate`, ledger, dedupe, R1 and R2, settings and flag |
| S1b | Durable owner record written at launch, and its reader |
| S2 | Person notices through `PushNotifier` (`NeedsYou` kind) |
| S3 | Agent digest over `agent.send` with the `watch` script credential, caps, retry, person fallback |
| S4 | R3 missing and R4 offer, loop failure notice and heartbeat |
| S5 | R5 host health |

## Defaults taken (no owner question raised)

Thresholds above; offer-only R4; person notices on and agent delivery off by default; no new principal kind (a person-issued script
credential instead); owner record written by the host at launch; digest and caps; reuse `NeedsYou`; coordinators keep an offline check until a
relay probe exists.

## Not in this note

Answering prompts from the app (#1003); ending sessions by the host; a new principal kind; a relay-side host probe; fleet policy alarms.
