# Audit rows: a host-derived client field (S4 evidence, rows 4 and 5)

**Design only.** Part of [#1396](https://github.com/danswett/agent-bridge/issues/1396); extends [ha-gate-counters.md](ha-gate-counters.md)
and the [S4 evidence matrix](ha-s4-evidence-matrix.md). No code, no host behavior and no audit row changes here; the code is a later,
sensitive slice (Quality and an independent read).

## The gap

Rows 4 (stop) and 5 (approvals) of the retirement inventory need their evidence split per client: 20 correct-target stops and 30 real
approvals "spread across the shipped clients". The host audit `Row` carries the time, the `Principal` (`person:<deviceId>` for a relay
answer or stop), the action, the outcome and `Gate` metadata (`Path`, `ActorKind`, `Phase`, `Operation`, `Incarnation`, `Question`).
It does not say which client product the device is. `PhoneApprovalEvidence` therefore groups by device id and needs the caller to say
which id is which client. That is the only thing missing for the per-client split.

## Proposal: two optional fields, written by the host

`GateAuditMetadata` gains `ClientKind` (`app`, `pwa` or `phone`) and `ClientPlatform` (`windows`, `macos`, `linux`, `ios`, `ipados` or
`android`), the closed sets of [devices.md](../protocol/devices.md). Both or neither; valid only on `person-client` rows of
`session.answer` and `session.stop`.

* **Source: the host's own verified roster, never the client.** At write time the host looks up the row's principal device in its
  verifier (`PinnedDevices.Query` returns each active, not held person device with `id`, `kind`, `platform` and a `quarantined` flag; the host uses a device that is not quarantined). The roster entry is the
  admin-approved, hash-chained record made when the device was paired. The fields are not read from the hello, the presence `kind`
  (which the client declares), or any request payload. A test pins it the way the forged-principal test does: a request that carries
  its own `client`, `kind` or `platform` changes nothing.
* **No roster hit: omit both fields.** An unknown, quarantined, held or revoked device is written as before, unattributed. The host
  never guesses a client.
* **Open check for the code PR:** devices.md signs `kind` in the member record; confirm that `platform` is covered by the signed,
  hash-chained entry too. If it is not, write `ClientKind` only and say so.

## Caveat: no roster on the host yet

The host does not have a person-device roster today. `Program.cs` wires `NoPersonRoster` ("None until Trust's roster lands"), so `IPersonDeviceRoster.Find` returns nothing and **real rows omit these fields until Trust's #55 roster (slice A, with #972 slice 2) lands** and `personRoster` is replaced. The code ([#4062](https://github.com/danswett/agent-bridge/pull/4062)) is the writer, the validation and the lookup, tested against a fake roster; it changes no row written today. Until then the per-client split cannot be read from real logs, and every row is unattributed.

## Compatibility

Following G6b3 ([#4027](https://github.com/danswett/agent-bridge/pull/4027)): the fields are omitted when absent, so every row written
before is byte-identical and still valid; a previous host reads a new row and ignores the fields; the chain hash covers the raw line;
E4(f) readers (`audit gate`) see no change and need no change. Tests pin all three.

A window that spans old and new rows is read honestly: rows without the fields are *unattributed*, and per-client evidence that needs
them is INCOMPLETE for those rows, never a guess and never a pass. The floors (20 stops, 30 approvals) are still read by `audit gate`.

## Reading it

`PhoneApprovalEvidence` is extended, not duplicated: it groups by `(ClientKind, ClientPlatform)` as well as by device, and the
expected clients become client labels (PWA, Windows, Mac, iOS, Android) beside the device ids it takes today. A PASS stays advisory.

## Expected target and wrong target (owner decision: allowed, client-reported, never a decision input)

R9 asked whether the host can derive an expected target from the card it issued. It can, but the result proves nothing: the `Question`
on an answer row is a host-derived identity of the issued card, the row `target` is the session the host resolved, and `Incarnation` is
that session's process instance. The host computes the "expected" and the "actual" target from the same lookup, so they agree by
construction. What the host cannot see is which session the person's card *showed* when it was tapped.

**Owner decision (about 08:35 PT, November 23, relayed by R9):** a **client-reported card target** is ALLOWED, labelled client-reported,
and **never used for decisions**. This note records it; the code is a separate, sensitive slice stacked on the client-field code.

* **What:** an optional `cardTarget` on `session.answer` (the session id the card showed, bounded and charset-checked) and an optional
  `ClientReportedCardTarget` on the answer's audit rows, written exactly as received once it passes the shape check, omitted when absent.
  Rows written without it stay byte-identical; a previous host ignores the field (the answer reader already ignores unknown request
  fields, pinned by a test).
* **Evidence only.** Comparing it with the row `target` shows a client that sent an answer for a different session than its card
  displayed. It is a claim by the client: a client that lies can hide a mismatch, so it supports the live run's transcript check and
  never replaces it.
* **Never a decision input.** No gate, grant, policy, routing, staleness or projection (`audit gate`, `PhoneApprovalEvidence`) reads it:
  an answer is routed, checked and counted exactly as without it. The code slice pins this with a source test that fails if any file
  outside the request type, the row type and the one writer names the field, and with tests that an answer with a wrong or hostile
  value is delivered, refused and counted exactly as one without it.
* **Not the client field.** `ClientKind` and `ClientPlatform` stay host-derived from the verified roster and are unchanged.
## Departure check against the audit design

| Audit design rule | This note |
|---|---|
| Extend #1401's existing records; no counter files, no client totals | Extends the record; adds identity labels, not totals; totals are still read from rows by the host |
| Nothing client-supplied; no free-form reasons | The client field comes from the verified roster; closed sets; nothing from the request. The one client-supplied value, the card target, is owner-approved evidence labelled client-reported and never a decision input |
| `ActorKind` is `person-client`, `agent` or `ha-component` | Unchanged; the client label is a separate field |
| Old rows still read | Byte-identical and valid; unattributed in a mixed window |
| Privacy | Adds `kind` and `platform` only; no device name, and no id beyond the principal already there |

No departure, so no owner CHANGE is needed for the client field. The card target is a departure from "nothing client-supplied" that the owner approved (above), on the terms stated there.

## Next slices (not in this note)

1. Code, tests first, at most 400 lines: the writer, `Valid`, the verifier lookup, the old-row and forged-request tests.
2. `PhoneApprovalEvidence` grouping by client, and the fleet ledger checker that consumes it (consume the host's own rows or `audit
   gate --json`; reuse the shared reading checks; a PASS stays advisory).