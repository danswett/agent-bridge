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
- It adds no code and asks the owner to type or press nothing.

## Preconditions

1. The G9 stack is merged on main: the envelope, the receiver (`fleet.launch` on a host peer) and the
   sender with its relay link.
2. Both hosts run a build that contains it, are signed in to the same account, and are own-account
   peers that trust each other (the existing own-peer trust).
3. The target host holds a receiver-side grant for the source agent session, with caps and a kill switch
   the run can read back.
4. Disposable hosts only. No run uses a person's grant, a person's session or a machine in daily use.

## What runs

Each case is one `fleet launch --target <node>` from an agent on the source host, driven by a script
that records the request id, the receipt and both hosts' own logs. The HA transport is run for the same
inputs in the same window, for comparison only.

| Case | Expected on the Host path |
|---|---|
| Granted launch | One session starts on the target and registers; the receipt's `outcome` is `started` and `registered` is true; the receiver's counters rise by exactly one |
| Same request id again | The recorded outcome comes back; no second session starts |
| Replay of the sealed envelope | Refused (`policy`); nothing starts |
| First 2.5 minutes after the target restarts | Refused (`policy`, the warm-up); nothing starts; a fresh request after the window starts one session |
| Target offline | `target-offline` (exit 5, only when delivery provably did not happen); nothing is queued and nothing is sent to HA |
| Source credential revoked or process gone | Refused before any send; nothing is sent |
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
- It takes a per-target launch lock and a launch lease, writes the prompt as an HA payload (which lifts the
  255 character limit; prompts run from short up to about 60 KB), takes a snapshot of the target's HA entities,
  and presses the target's `new_session` button once.
- It reads the result from `new_session_result`. If the host is busy it waits and resubmits, up to 5 times, 30 s apart.
- Concurrent launches to one target are serialized by that lock and lease.
- An uncertain press is reported as uncertain and is not repeated.

The Host path has no lock or lease, no payload entity and no resubmit loop: the receiver's own caps, counters, dedupe
and refusals decide, and a busy or uncertain result is exit 7 or a refusal, never an automatic resend.

### Flip scope: launch only

The flip changes the **launch** default only. `-End`, `-List`, `-Options`, `-Resolve` and `-EndWhenIdle` stay on
HA (the Host transport refuses them and throws), so they are not part of this gate and the `HA` value stays
selectable for them. Their replacement is G10 and G11 B, and removal is E4.

### Which cases have an HA twin, and how a pair is compared

**With an HA twin** (compared, counted in the 100): granted launch, same request id again, response lost, long prompts,
and the concurrent bursts. Each HA twin is the same inputs sent over the HA transport to the same target in the same
window, with its own request id.

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

**Denominators.** The 100 is compared pairs. A pair that ends exit 7 is counted only after it is resolved to one of the
other rows. A mismatch is any pair that lands in different rows, or in the same row with a different reason. Pairs
dropped for a run fault (a host down for a reason outside the case) are listed in the receipt and replaced; they are
never silently excluded, and more than the plan's stated number dropped makes the run INCONCLUSIVE.

**Counting doubles.** At the end of each request, and again after the 5 minute late wait, list the target's launches
with `launches list --json` and count entries by request id (`rid`). More than one started session for a request id,
or a session that appears only after the late wait, is a double launch and fails the run.

## Pass bar

A run passes only if **all** of the following hold. Quality/SRE sets the numbers and writes them into the
**run plan before the first request is sent**; a number chosen or changed after a request is sent voids the run.
Quality sets these floors:

1. **N = 50 consecutive compared launches**: 50 launches, each issued on the Host path and compared with the same
   inputs on the HA transport, with no hidden retry and no gaps. Fewer than 50 compared launches is
   **INCONCLUSIVE**, not a pass, and the run is repeated.
   The run holds at least **100 compared requests** in all, each with an HA twin; the 50 consecutive count is
   inside that total, never in place of it.

**Coverage floors**, each also INCONCLUSIVE if missed:

- **Refusal cases (Host only, no HA twin)**: 5 of each case in the table above, and 3 for the warm-up case.
- **Lost response**: 3 cases, each resolved with `launches status`.
- **Concurrent bursts**: 3, with launches issued at the same time; the double-launch and counter checks cover them.
- **Long prompts**: 5, including one near 60 KB.
- **Hosts**: at least 3 disposable hosts, with 3 source-target pairs, each pair run in both directions.
- **Window**: at least 6 h across at least 4 distinct hours, including one induced host restart and one relay
  reconnect. Each induced event needs the owner's approval **at run time**; this document grants none.
- **Late second session**: after each launch, wait 5 minutes and confirm no second session appeared for that request id.
2. **0 double launches**: for each request id, at most one session exists on the target.
3. **0 HA fallbacks**: zero HA presses made as a fallback on any Host-path request (the HA fallback counter stays at zero for
   Host-path requests).
4. **0 mismatches**: for each request the Host path and the HA comparison agree on outcome, `registered` and refusal reason.
5. No token, key, credential or prompt text appears in any receipt, log or output the run keeps.
6. The receipts carry the request id, so any case can be read back with `launches status`.

A failure fails the gate. A run **below any floor** (fewer than 50 compared launches, a case that never ran, a counter
that was not read) is **INCONCLUSIVE** and is repeated from the start; silence or no activity is never a pass. A result
the run cannot classify is also a failure. A new run starts from the beginning; a
failed request is never excused as load or retried until it passes.

## Witness and receipt

- **Quality/SRE runs the evidence and signs a receipt.** A builder who wrote the code never witnesses it.
- The receipt names: the source and target build heads, the date and window, N, each case's result, the
  request ids, and where the raw logs are kept. It carries no secret.
- The lead approves the flip from the receipt. The flip is a separate small Fleet PR that cites the receipt;
  this document does not authorize it.

## Rollback

Rollback is **HA kept**: set the default back to `HA` (a one-line Fleet change) and the HA path, which
this gate leaves untouched, works as before. Nothing on this gate deletes HA code; deletion needs E4 in
the G12 inventory (design.md P7). If a Host-path request is `unconfirmed`, rolling back does not resend
it: the request is resolved by `launches status` first.

## Out of scope

The end path (G10, G11 B), the session-registry dual run (slice 11), the load proof, the HA-off rehearsal,
parity rows and the windowed HA-fallback count belong to E3 and E4 in the G12 inventory, not here.

## Doc status

New file. `fleet-grants.md` and `fleet-grants-cli.md` are unchanged: they own the cutover order and the
exit codes this document relies on.
