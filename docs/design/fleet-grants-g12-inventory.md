# G12 deletion inventory: what the grants stack retires

Part of #3237 and the Home Assistant (HA) retirement (#1396). This is the design-first
inventory for G12 in [fleet-grants.md](fleet-grants.md) ("G9 wire and cutover rows", rows
G9 to G12). **It deletes nothing.** Each later G12 deletion PR names the rows it removes
from this table and the gate that unlocked them. Wrapper rules come from
[fleet-grants-cli.md](fleet-grants-cli.md) ("Wrapper mapping").

Slice names used below are defined here, because the docs above name only G9 to G12:

- **G9b2**: the receiving host's remote launch (the `fleet.launch` envelope check, replay cache and grant gate, in `FleetRemoteLaunch`).
- **G9c2**: the wire op that serves `fleet.launch` to host peers over the relay, and the sender's relay link.
- **G11 A / G11 B**: the two wrapper slices. A makes `launch-remote.ps1` call `fleet launch`; B makes the end wrappers call `fleet end`, one request per session. Both are policy-only and remove no wrapper.
- **Slice 11** (owner gate, see E4) is a different thing: the 24 h session-registry dual run, not G11.

## Why a table, and the order it enforces

fleet-grants.md fixes the order: G9 lands remote launch, the default flips to the Host
transport, the dispatcher stops its HA calls, G10 and G11 follow for end and wrappers, and
G12 removes the HA launch path "after approved cutover evidence". The owner answered F1 to
F9 on #1396 that no HA-only path survives (S8, HA as an optional client, is kept). That answer is the **permission** to remove the
connector once its gate has passed; it is never an unlock by itself. What the table adds is *when*: a row is deletable only after the slice that replaces it has landed
**and** its evidence gate has passed. Deleting a path before its replacement is proven is how
a person ends up with neither.

## Evidence gates

Quality/SRE owns the evidence on disposable VMs, never a builder and never this doc alone.

| Gate | Unlocks when |
|---|---|
| **E1 Host launch proof** | G9 (envelope, receiver, sender, `fleet.launch` op) is merged, and a disposable-VM run shows a remote launch over the Host transport that starts a registered session, with the receiver's own caps, kill switch and counters |
| **E2 Launch dual run** (not slice 11) | Over an agreed window, every launch is issued on the Host transport while the HA path is still present, and the two paths' outcomes are compared per request: same `started` and `registered`, same refusals, no double launch, no HA press made as a fallback. The window and pass count are Quality's to set |
| **E3 End proof** | G10 (remote stop) is merged and a disposable-VM run shows `fleet end` per session with a fresh request id each, matching what the HA stop press did |
| **E4 Connector-removal gate** | **All** of: (a) **slice 11**, the 24 h session-registry dual run, which is an **owner gate** before any HA connector removal, passed and accepted by the owner; (b) the load proof (#1949, fleet #314); (c) the HA-off rehearsal: the fleet runs with HA switched off and launch, end, and messaging all work; (d) #1396's required parity-row evidence recorded for every required row and client; (e) the owner's sign-off, in writing, after E1 to E3 and (a) to (d) have Quality/SRE receipts. The older HA-fallback count gate and the S8-as-prerequisite are superseded by (a) to (e) in the agent-bridge docs, but fleet `ha-retirement.md` section 3 still lists them, so the Quality/SRE receipt states which of them it also required. The owner's F1 to F9 answer is the permission to remove the connector, not a gate that unlocks it; removal goes **last** |

E2 is a launch comparison only. It is not slice 11 and does not stand in for it. Rows that remove a
fleet tool's HA press (section A) need the gate named in the row. Every row that removes the HA
connector's launch surface (sections B and C) needs **E4**, whole, never a part of it.

A row names the *earliest* gate that unlocks it. A deletion PR cites the receipt, not this doc.

## Inventory

"Slice" is the stack slice that makes the row redundant. Sizes are current line counts.

### A. Fleet tools (the wrappers and their HA presses; every path here is in the Fleet repository, not this one)

| # | Item | What it does over HA | Replaced by (slice) | Unlocked by |
|---|---|---|---|---|
| A1 | (fleet repo) `launch-remote.ps1` HA transport: the `Call button press` on the machine's `new_session` button, the `new_prompt_payload` write, and the pre-launch snapshot of the HA `new_session_result` and `sessions` entities | Submits a launch as an HA payload plus a button press | `fleet launch --target` (G9c2, then G11 A) | E2 |
| A2 | (fleet repo) `launch-remote.ps1` HA lease reads (the HA server date, the foreign launch lease wait) and the three-minute foreign-lease wait | Guards one HA launch at a time per target | Receiver-side dedupe and counters (G5, G9b2) | E2 |
| A3 | (fleet repo) `launch-remote.ps1` HA end path: the `button.<node>_stop` press and its observe-after-press loop | Ends an idle session by one press | `fleet end` (G10, G11 B) | E3 |
| A4 | (fleet repo) `end-sessions.ps1`: its `button/press` of `button.<target>_stop` and its `AGENT_HA_AGENT_TOKEN` read. `end-one.ps1`, `end-many.ps1` and `end-remote.ps1` are already thin wrappers with no HA press of their own; they change only in G11 B | Ending press over HA | One `fleet end` per session (G10, G11 B) | E3 |
| A6 | (fleet repo) The launch mutex/duplicate/counter logic inside `launch-remote.ps1` (not the `FleetLaunchLedger.psm1` module, see "not retired") | Fleet-side duplicate and counter bookkeeping | Receiver durable counters and request dedupe (G5) | E2, **and** only after A3 has stopped reading the ledger (E3) |
| A7 | (fleet repo) The Fleet dispatcher's `FleetLaunch` and its three HA calls | Dispatch a launch through HA | The host verb (cutover row 2 in fleet-grants.md) | E1, then E2 |
| A8 | (fleet repo) `launch-remote.ps1 -Transport` value `HA` and its default | Selects HA | Default `Host` first (cutover row 1); the `HA` value is removed last | Default flip: E1. Value removed: E4, before or with B2/B4/B5. The `-Transport` default `HA` of `end-one.ps1`, `end-many.ps1`, `end-remote.ps1` and `end-sessions.ps1` flips with G11 B; the HA-only modes `-List`, `-Options`, `-Resolve`, `-EndWhenIdle` need a replacement first |

Other allowlisted HA call sites that touch launch but are **not** retired by the G9 to G11 stack. They
are listed so no deletion PR assumes they are covered; each belongs to its own fleet slice and issue
(the allowlist in fleet `ops/ha-retirement`, tracked in fleet #106, #107, #104 and #267, and agent-bridge #1414), and a
PR that removes one names the issue and the gate that issue's slice sets:

| # | Item | Why it is listed |
|---|---|---|
| A9 | (fleet repo) `dispatcher/Invoke-DispatcherCycle.ps1` reads of `launch_options` and `new_workspace` | The dispatcher still reads HA launch entities; A7 moves only its `FleetLaunch` calls. These reads must be replaced before B2 can go, so B2 also waits on this row |
| A10 | (fleet repo) `messenger/FleetMessenger.psm1` and its tests, and `tools/handover-updaters/waiting-list-watch.ps1` | HA token reads for messaging; messaging has separate umbrella slices (see "not retired") |
| A11 | (fleet repo) `tools/find-nodes.ps1` and the roster/lookup scripts | Lookup over HA; separate slice, and G9 `--target` resolves from relay presence, not from them |

### B. Host (agent-bridge): the HA connector's launch surface

| # | Item | What it is | Replaced by (slice) | Unlocked by |
|---|---|---|---|---|
| B1 | `src/AgentBridge.HomeAssistant/HaConnector.Launch.cs` (1024 lines): `LaunchAsync`, `LaunchControlsAsync`, `LaunchPressAsync`, `LaunchFromCardAsync`, `LaunchesDoneAsync`, `OutcomeAsync` and the prompt-payload clearing | Publishes the launch card controls and acts on the press | Host verbs (G9, G11) | E4 |
| B2 | `HaConnector.Launch.cs` `LaunchOptionsSensorAsync` and the `launch_options` sensor it publishes | Read-only list of every agent's launchable options | `fleet launch` and `launch options` on the Host (G9) | E4 |
| B3 | `HaContract.Launch.cs` (170 lines) and the launch entries in `HaContract.cs`: the card controls, `new_session_result`, `launch_options` discovery | The discovery contract for the launch entities | Nothing; the entities go | E4 |
| B4 | `new_session_result` sensor and its `recent` attribute, including the expiry read in `ExpireLaunchNoteAsync` | Published launch outcomes (the old read of an outcome) | `launches status --request --json` (#3533) | E4 |
| B6 | The `HaLauncher` wiring: its parameter on `HaConnector` (`HaConnector.cs`), the construction and hand-over in `Program.cs`, and `src/AgentBridge.Host/HaRunning.cs` where it is held | The seam that gives the HA connector the host's launcher | Nothing; the seam goes with B1 | E4 |
| B5 | Dispatcher-side HA prompt payload (`newsession/promptpayload`, `newsession/outcome` retained topics) | Carries the launch prompt past HA's 255-character limit | The signed envelope's payload hash and the host launch request | E4 |

B1 to B6 are one deletion PR family: B3 cannot go before B1 (it would not compile), and B1
cannot go before the dispatcher (A7) has stopped calling it.

### C. Tests, fixtures and docs that go with them

| # | Item | Goes with | Unlocked by |
|---|---|---|---|
| C1 | `tests/AgentBridge.HomeAssistant.Tests/LaunchTests.cs` (891 lines) | B1, B2 | E4 |
| C2 | `tests/AgentBridge.HomeAssistant.Tests/Fixtures/ha-launch.json` and the launch parts of `ha-contract.json` | B3 | E4 |
| C3 | Launch cases in `UpkeepTests.cs` and `LiveHomeAssistantTests.cs` | B1, B3 | E4 |
| C4 | `docs/ha-connector.md` launch sections (`launch_options`, `new_session_result`, the launch card, the purple-edge note for a launched session) and the launch rows in `docs/parity.md` | B1 to B4 | E4 |
| C5 | (fleet repo) Fleet tests that exercise the HA press paths (`Test-LaunchEvidence.ps1` HA cases) | A1 to A4 | The row that unlocks the code |
| C6 | `tests/AgentBridge.Host.Tests/LaunchFlowTests.HomeAssistant.cs` (launch flow over the HA connector) | B1, B6 | E4 |
| C7 | `docs/design/ha-gate-counters.md` HA launch parts, and `docs/protocol/local-pipe.md` / `docs/parity.md` lines naming the HA launch entities | B1 to B4, B6 | E4 |

Each deletion PR removes its tests in the same PR, so the suite never tests a path that no
longer exists and never loses coverage of one that does.

## What is **not** retired

These stay. A deletion PR that touches one is wrong.

- **Host grant launch.** `FleetGrantLaunch`, `FleetLaunchGrant`, `FleetLaunchCommands`,
  `FleetStopCommands`, `FleetEndCommands`, `FleetRemoteLaunch` and `FleetRemoteLaunchSender` (in open PRs #3772 and #3848 until they merge),
  `FleetActionEnvelope`, `Fleet/FleetLaunchNames` and `AgentPipeLaunch`: they are the
  replacement.
- **`FleetLifecycle.ps1` pressing audit (formerly A5).** `Write-FleetEndAudit ... 'pressing'` is the
  Slipgates `session.stop` audit, not an HA path. It stays; only the HA press call next to it goes with A3/A4.
- **`FleetLaunchLedger.psm1` (formerly part of A6).** It is used by `FleetLifecycle.ps1` and the end-ownership
  check, so it is not an HA target. It may change only in a PR that shows those users no longer read it.
- **Person gates.** Every phone and person-device approval, the roster and signer gates, and
  person-required confirmations. No row removes or bypasses one.
- **Messaging.** `reply-session.ps1`, its HA fallback, lookup scripts and the outbox are
  separate umbrella slices (fleet-grants.md, "Do not remove `reply-session`...").
- **MCP `get_launch_options`.** `mcp/src/server.mjs` maps it to the host's own
  `sessions.launch` operation, not an HA entity. It stays; a deletion PR verifies this
  before touching `mcp/`.
- **The `HaConnector` outside launch**, until the umbrella's own slices remove it.

## Deletion order

1. **Default flip** (A8, value left in place): after E1, in its own small Fleet PR.
2. **Dispatcher** (A7): after E2, the same gate as step 3 (E1 alone is not enough); it stops the three HA calls and calls the host verb.
3. **Wrapper bodies** (A1, A2): after E2, replaced by the G11 A thin wrapper.
4. **End path** (A3, A4): after E3, replaced by the G11 B thin wrappers.
5. **Launch bookkeeping** (A6): only after step 4, once A3 no longer reads the ledger.
6. **Host HA launch surface** (B1 to B6 with C1 to C7; B2 also needs A9 replaced): after E4, last, one family of PRs in
   the order B1/B6/C1/C3/C6, B2/B4, then B3/C2, then docs C4/C7.
7. **`-Transport HA`** (A8 value): before or with step 6 (the HA mode reads the launch entities B2, B4 and B5 remove), and only once `-List`, `-Options`, `-Resolve` and `-EndWhenIdle`, which only the HA transport serves, have a replacement.

Each PR is at most 400 lines against its approved parent (target 250; a large file deletion is judged under the cap rule),
tests first where code changes, and carries the gate receipt it relies on.

## Open items for Quality/SRE (not decided here)

- The E2 window length and pass count.
- The slice 11 (24 h session-registry dual run) window and owner acceptance format.
- Which existing HA entities a phone dashboard still reads; the owner's F1 to F9 answer says
  none survive, so this is a check to run before E4, not an unlock.

## Doc status

New file; `fleet-grants.md` and `fleet-grants-cli.md` are unchanged because they already own
the order and the wrapper rules, and this file only enumerates what each step removes.
