# Host transport evidence: the gate for the launch default flip

Part of #3237 and the Home Assistant (HA) retirement (#1396). Design-first, docs only. This is the
evidence that must exist before the Fleet `launch-remote.ps1` default changes from the `HA`
transport to the `Host` transport and before the Fleet dispatcher's `FleetLaunch` stops its HA
calls. Those are cutover rows 1 and 2 in [fleet-grants.md](fleet-grants.md) ("G9 wire and cutover
rows"); the remote launch it proves is described there and in [fleet-grants-cli.md](fleet-grants-cli.md)
("Remote authority", "Refusal codes", "Uncertain outcomes: never auto-retry").

## What this gate is, and is not

- It gates a **switch**: the default becomes `Host` and the dispatcher calls the host verb. The HA code
  stays in place and selectable, so the switch is reversible.
- It is the evidence named **E1** (Host launch proof) in the G12 inventory, plus the per-request
  comparison named **E2**, both in `fleet-grants-g12-inventory.md` (#3864). It is **not** slice 11
  (the 24 h session-registry dual run) and **not** E4 (the connector-removal gate). Nothing is deleted
  on the strength of this document.
- It adds no code and asks the owner for nothing in this document. The run itself needs a few **person steps at run time**,
  named in "Person steps" below, and nothing else from the owner.

## Preconditions

1. The G9 stack is merged on main: the envelope, the receiver (`fleet.launch` on a host peer) and the
   sender with its relay link.
2. Both hosts run a build that contains it, are signed in to the same account, and are own-account
   peers that trust each other (the existing own-peer trust).
3. The target host holds a receiver-side grant for the source agent session, with caps and a kill switch the run can
   read back. The grant is **person-issued** for the run; no builder or script creates, widens or reuses one. Any
   person action the run needs (the grant, an induced restart or reconnect) is approved by that person **at run time**.
4. Disposable hosts and a disposable HA only. No run uses a person's own grant, a person's session, a machine in daily
   use, or the owner's real Home Assistant.

## What runs

Each case is one `fleet launch --target <node>` from an agent on the source host, driven by a script
that records the request id, the receipt and both hosts' own logs. The HA transport is run for the same
inputs in the same window, for comparison only. **The HA transport in this run is a disposable HA instance made for
the run, never the owner's real Home Assistant** (AGENTS.md, first rule): no HA comparison press, payload or entity
write goes to the real one, and the receipt names the disposable HA used.

| Case | Expected on the Host path |
|---|---|
| Granted launch | One session starts on the target and registers; the receipt's `outcome` is `started` and `registered` is true; the receiver's counters rise by exactly one |
| Same request id again | The recorded outcome comes back; no second session starts |
| Replay of the sealed envelope | Refused (`policy`); nothing starts |
| First 2.5 minutes after the target restarts | Refused (`policy`, the warm-up); nothing starts; a fresh request after the window starts one session |
| Target offline | `target-offline` (exit 5, only when delivery provably did not happen); nothing is queued and nothing is sent to HA |
| Source credential revoked or process gone | Refused `policy` (exit 5) by the sender before any send; nothing is sent |
| Grant missing or expired | Refused `grant-missing` or `grant-expired` (exit 5); counters do not move |
| Grant revoked | Refused `grant-revoked` (exit 5); counters do not move |
| Kill switch on (`launch.enabled` off or equivalent) | Refused `kill-switch` (exit 5); never another route |
| Scope outside the grant or host policy | Refused `scope` (exit 5) |
| Cap would be exceeded | Refused `grant-exhausted` (exit 5); counters do not move |
| Another receiving-host safety gate (including replay and the warm-up) | Refused `policy` (exit 5) |
| Response lost after the send | `unconfirmed` (exit 7); resolved with `launches status --request <uuid> --json` on the target; never resent with a fresh id |
| Peer not a trusted own-account host | Refused `peer-not-trusted` (exit 5) by the sender before any send; never another route |
| Same inputs over the HA transport (comparison) | Same `started` and `registered`, same refusals, same counters, no double launch |

### What HA launch does today (the comparison baseline)

So the comparison is fair, this is what the HA transport of `launch-remote.ps1` does for a launch now, and
what the Host path is therefore measured against:

- It checks the requested options against the target's HA `launch_options` and checks a new workspace, before it sends.
- It takes a per-target launch lock (a mutex on the sending machine) and honours an HA launch lease: it reads the lease
  and the HA server date and waits up to three minutes for a foreign lease. It writes the prompt as an HA payload (which lifts
  the 255 character limit; prompts run from short up to about 60 KB), takes a snapshot of the target's HA entities,
  and presses the target's `new_session` button once.
- It reads the result from `new_session_result`. If the host is busy it waits and resubmits, up to 5 times, 30 s apart.
- Concurrent launches to one target are serialized by that lock and lease.
- An uncertain press is reported as uncertain and is not repeated.

The Host path has no lock or lease, no payload entity and no resubmit loop: the receiver's own caps, counters, dedupe
and refusals decide, and a busy or uncertain result is exit 7 or a refusal, never an automatic resend.

### Flip scope: launch only

The flip changes the **launch** default only. Under `-Transport Host` today, `-List`, `-Options`, `-Resolve`, `-EndWhenIdle`
and `-EndStaleShell` throw and send nothing, so they stay on HA (or Slipgates for `-EndStaleShell`). Plain `-End` is different: it
**runs `fleet end`** on the Host transport (`launch-remote.ps1`, the `-Transport Host` branch). A bare default flip would therefore move
ends to Host before G10 and G11 B are proven, so **the flip PR must route plain `-End` to HA explicitly** (an end keeps the HA
default until its own gate, E3). Host launches also need `-TargetNode` (omit for local), `-Grant`, and `-Permissions 'Allow all'`
(the Host branch refuses anything else), and never `-Machine`. The `HA` value stays selectable for everything not launch;
the replacement is G10 and G11 B and removal is E4.

### Which cases have an HA twin, and how a pair is compared

**With an HA twin** (compared, counted in the 100): granted launch, same request id again, response lost, long prompts,
and the concurrent bursts. Each HA twin is the same inputs sent over the HA transport to the same target in the same
window, with its own request id. The three less obvious twins are:

- **Burst twin:** the same K launches issued at the same moment over HA to one target (HA serializes them by its lock
  and lease). A burst passes on **invariants**, not timing or order: new sessions equal accepted requests, each request has
  exactly one session, the counter rise equals the started count, and there is no double and no unexplained session.
- **Same-id twin:** the HA wrapper makes a fresh request per call, so a small **harness** is built for the run (and listed in
  the run plan) that sends the same HA request id twice to the disposable HA. HA must show one session and the recorded
  outcome, as Host does. If the harness cannot be built, this twin is dropped and the run is INCONCLUSIVE.
- **Lost-response twin:** an HA press whose result is unreadable (reported uncertain, not repeated), resolved by reading
  what the target actually started; it matches a Host exit 7 resolved by `launches status`. **Induced** by a fault after the
  request is sent, never before: on the Host side the disposable target's relay connection is dropped before its reply returns;
  on the HA side the response read to the disposable HA is cut after the press. The fault is injected by a person at run time
  (see "Person steps") and the run plan records when it was injected relative to the send.

**Host only, no twin** (counted against their own floors, not the 100): replay of the sealed envelope, the warm-up
refusal, grant revoked, kill switch, scope or cap exceeded, peer not trusted or not own-account, target offline, and
source credential revoked. HA has no equivalent of these, so they are judged against the table above, not a twin.

**Pairing.** A Host request and its HA twin are paired by a run-plan row that records both request ids, the inputs
hash, the source and target, and the time. A request without its twin is not compared and is not counted.

**Outcome mapping** (a pair matches only when both sides land in the same row):

| Host receipt (`fleet launch --json`) | Exit | HA outcome it must match |
|---|---|---|
| `outcome` `started`, `registered` true | 0 | `started` and the session registered |
| `outcome` `refused` or `failed`, with a `reason` | 5 | the same refusal or failure; the Host reason is a documented refusal code |
| host, pipe or audit not usable | 6 | HA reports the target unavailable |
| `accepted` or `unconfirmed` (may have started) | 7 | HA `unconfirmed`; both are resolved with `launches status` before they are counted |
| receiver busy (Host) / host busy (HA) | 5, `refused`, with the busy reason | HA: the wrapper waits and resubmits up to 5 times 30 s apart. The pair matches on the **final** outcome and reason; every HA resubmit that starts a session is counted by the double check, and a Host busy is never resent automatically |

**Denominators.** The 100 is compared pairs. A pair that ends exit 7 is counted only after it is resolved to one of the
other rows. A mismatch is any pair that lands in different rows, or in the same row with a different reason. Pairs
dropped for a run fault (a host down for a reason outside the case) are listed in the receipt and replaced; they are
never silently excluded, and more than the plan's stated number dropped makes the run INCONCLUSIVE.

**Counting doubles.** `launches list --json` has no request id and the store keeps one row per principal and request,
so a second session for the same request would not show as a second row. Doubles are therefore counted from
**sessions**, never from rows. Every request in the run plan carries its own unique launch name. At the end of each
request, and again after the 5 minute late wait, for each target compare three counts for that launch name:

1. **every new session** that appears on the target during the request's window, read from the host's `sessions.list`
   verb before and after (each entry carries the launch name in `Title` and the start time in `StartedAt`; `fleet.list`
   carries the name only, so it is not used for the time match), not only the sessions with the request's launch name:
   a stray or misnamed second session is exactly what this check exists to find. The ones carrying the launch name are
   matched to the request; any other new session in the window is an unexplained session and fails the run;
2. the **started rows** in `launches list --json` for the same request;
3. the **receiver's counter rise** for the grant (hourly, daily and live usage), read before and after.

All three must be exactly one per Host request (zero when the request was refused), and the three must agree. Any
count above one, any disagreement between the three, or a session that appears only after the late wait is a double
launch (or a missing record) and fails the run. A count that cannot be read makes the run INCONCLUSIVE.

## Pass bar

A run passes only if **all** of the following hold. Quality/SRE sets the numbers and writes them into the
**run plan before the first request is sent**; a number chosen or changed after a request is sent voids the run.
Quality sets these floors:

1. **N = 50 consecutive compared launches**: 50 launches, each issued on the Host path and compared with the same
   inputs on the HA transport, with no hidden retry and no gaps. Fewer than 50 compared launches is
   **INCONCLUSIVE**, not a pass, and the run is repeated.
   The run holds at least **100 compared requests** in all, each with an HA twin; the 50 consecutive count is
   inside that total, never in place of it.

2. **Coverage floors**, each also INCONCLUSIVE if missed:

   - **Refusal cases (Host only, no HA twin)**: 5 of each Host-only case in the table above (the cases with no HA twin), and 3 for the warm-up case.
   - **Lost response**: 3 cases, each resolved with `launches status`.
   - **Concurrent bursts**: 3, with launches issued at the same time; the double-launch and counter checks cover them.
   - **Long prompts**: 5, including one near 60 KB.
   - **Hosts**: at least 3 disposable hosts, with 3 source-target pairs, each pair run in both directions.
   - **Window**: at least 6 h across at least 4 distinct hours, including one induced host restart and one relay
     reconnect. The 3 warm-up restarts are induced events too: each is person-only and needs the owner's approval **at run time**; this document grants none.
   - **Late second session**: after each launch, wait 5 minutes and confirm no second session appeared for that request (its launch name).
3. **0 double launches**: for each request (its unique launch name), at most one session exists on the target, and the live-session, started-row and counter-rise counts agree.
4. **0 HA fallbacks**: the HA fallback counter stays at zero. Source: the owner-only #1401 audit log (`launches-*.jsonl`, `ha-gate-counters.md`, #3813), whose rows carry a `path` of `relay`, `local` or `legacy-ha`. Reader: Quality/SRE's windowed reader over those files (the run adds no counter of its own). The `legacy-ha` rows are attributed by request id: a row whose id is one of the plan's HA twin ids is the comparison and is expected; every other `legacy-ha` row in the run window, for a target and name of the plan, is a fallback and fails the run. It is read before and after each request and for the whole window. Unreadable is INCONCLUSIVE. (It counts zero for
   Host-path requests.)
5. **0 mismatches**: for each request the Host path and the HA comparison agree on outcome, `registered` and refusal reason.
6. No token, key, credential or prompt text appears in any receipt, log or output the run keeps.
7. Every case can be recomputed from the raw per-request records SRE keeps (see "Witness and receipt"); the 15 minute launch status store is never relied on after the run.

A failure fails the gate. A run **below any floor** (fewer than 50 compared launches, a case that never ran, a counter
that was not read) is **INCONCLUSIVE** and is repeated from the start; silence or no activity is never a pass. A result
the run cannot classify is also a failure. A new run starts from the beginning; a
failed request is never excused as load or retried until it passes.

## Witness and receipt

- **SRE runs the evidence and keeps the raw records off-repo; Quality reads them against the bars and attests.** A
  builder who wrote the code never witnesses it, and neither role writes the run plan numbers after the first request.
- The receipt **copies the run plan's numbers unchanged** (N, every floor, the window, the hosts and pairs) and attaches the
  plan, then records the **actual** count and denominator against each one. It also names the source and target build heads,
  the date, each case's result and the request ids. It carries no secret, token, URL, path, machine name or prompt text.
- **Raw records and hashes.** SRE keeps every raw per-request record off the repository (command outputs, receipts,
  session lists, counter reads, host logs). The receipt gives the **evidence path** and a sha256 for each record. The launch
  status store keeps an outcome only 15 minutes, so each `launches status` and `launches list` output is copied at request
  time, not read later.
- **Failed and INCONCLUSIVE runs are kept** with the same records and are never overwritten; a repeat run is a new run.
- **The owner decides any deletion** of the records or the receipt (G12 E4 (e)); nothing here deletes them.
- **Quality attests; the lead approves the flip** from the attested receipt. The flip is a separate small Fleet PR that
  cites the receipt; this document does not authorize it.

## Rollback

Rollback is **HA kept**: nothing on this gate deletes HA code (deletion needs E4 in the G12 inventory, design.md P7).

- **Launch default:** set the `launch-remote.ps1` default back to `HA` (a one-line Fleet change); the HA path, which
  this gate leaves untouched, works as before.
- **Dispatcher:** revert the dispatcher's switch in its own small Fleet PR so `FleetLaunch` makes its three HA calls
  again; those calls stay in the code until E4, so this is a revert, not a rewrite. The dispatcher is a headless caller, and
  the host verb accepts only a live agent session credential, so the dispatcher's evidence is produced **through that same
  headless caller class**, never through a human CLI, the app or a script, or the evidence does not cover the dispatcher.
- **Per-call rollback:** any single call can pass `-Transport HA` explicitly and use the unchanged HA path at once.
- **Rollback triggers:** a launch that lands on the wrong target, a double launch, a person-guard bypass, or any row that
  fails twice rolls the default back. The lead decides, and the receipt records which trigger fired.
- This gate also covers the G11 A wrapper, which depends on the same Host launch path.
- If a Host-path request is `unconfirmed`, rolling back does not resend it: it is resolved by `launches status` first.
## Person steps

The run needs a person at run time; an agent, a script or a builder does none of these (guard 67).

- **Grant:** the person who owns the disposable hosts' account issues the grant for the run and confirms it on the host's
  own person surface (the app), after seeing its scope and caps. Nothing issues, widens or confirms it for them.
- **Induced host restart:** a host restart is person-only. The person restarts the disposable target host from that host's own
  control (the app's host Restart), once, at the time the run plan names.
- **Induced relay reconnect and the lost-response faults:** the person drops and restores the disposable host's relay
  connection (and the disposable HA response read) at the times the plan names, using the same host control.
- **What they press** is recorded per host in the run plan (the control, the host, the time); Quality/SRE writes the exact
  control names there, and the receipt records each press with the time. The owner approves each induced event at run time
  before it happens, and this document grants none of them.

## Names, and ending the sessions the run starts

A run starts at least 100 Host sessions and as many HA-twin sessions (200 or more in all), so the names and the
live count are planned, not left to chance.

- **Names.** Each request's unique launch name is taken from the fixed `SG Builder <n>` / `SG Worker <n>` list that the
  existing name gate (`FleetLaunchNames`, a format and collision rule) accepts; the run plan assigns every name before
  the first request. A name is **held while its session is live** and is reused only after that session has ended,
  because a live name collides (`name-gate`). The plan therefore never needs more names than the cap below.
- **Cap.** The run plan states a bound on simultaneously live disposable sessions per target (Quality/SRE's number,
  written before the first request). The run issues requests in batches no larger than the cap, ends the batch, and only
  then starts the next; a batch that would exceed the cap is not started. The concurrent bursts are inside the cap too.
- **Ending.** Each batch is ended by the same operator that ran it, by list (the names in the plan), through the existing
  end path for the disposable hosts (`fleet end` once G10 lands, otherwise the current end wrappers on those hosts only).
  Never a raw kill, and never anything outside the plan's list. After the run an **end-by-list** sweep ends whatever the
  plan's names still show live and records each result.
- **Check.** After the sweep the session list on every disposable host shows none of the plan's names. A name still
  live after the sweep is a finding in the receipt, not something to leave running. The receipt records the cap used,
  the batch sizes, and the sweep result, with the session lists hashed as raw records.

## Out of scope

The end path (G10, G11 B), the session-registry dual run (slice 11), the load proof, the HA-off rehearsal and the parity rows
belong to E3 and E4 in the G12 inventory, not here. So does the **windowed** HA-fallback count of E4 (f), which reads the audit log
over a longer window (ha-gate-counters.md). This run only reads the fallback counter for its own Host-path requests, as defined above.

## Doc status

New file. `fleet-grants.md` and `fleet-grants-cli.md` are unchanged: they own the cutover order and the
exit codes this document relies on.
