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
| Target offline | `target-offline`; nothing is queued and nothing is sent to HA |
| Source credential revoked or process gone | Refused before any send; nothing is sent |
| Grant revoked, kill switch on, scope or cap exceeded | Refused with `grant-revoked`, `policy`, `scope` as the receiver decides; counters do not move |
| Response lost after the send | `unconfirmed` (exit 7); resolved with `launches status --request <uuid> --json` on the target; never resent with a fresh id |
| Peer not an own-account host | Refused by the sender before any send |
| Same inputs over the HA transport (comparison) | Same `started` and `registered`, same refusals, same counters, no double launch |

## Pass bar

A run passes only if **all** of the following hold. Quality/SRE sets the number of requests and the
window; this document sets what each one must satisfy.

1. Every case above ends as expected, for N consecutive requests with no hidden retry (N and the window are
   Quality's number, recorded in the receipt).
2. Zero double launches: for each request id, at most one session exists on the target.
3. Zero HA presses made as a fallback on any Host-path request (the HA fallback counter stays at zero for
   Host-path requests).
4. For each request the Host path and the HA comparison agree on outcome, `registered` and refusal reason.
5. No token, key, credential or prompt text appears in any receipt, log or output the run keeps.
6. The receipts carry the request id, so any case can be read back with `launches status`.

A failure, or a result the run cannot classify, fails the gate. A new run starts from the beginning; a
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
