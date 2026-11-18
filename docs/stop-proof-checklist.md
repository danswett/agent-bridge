# S4 stop proof checklist

Execution supplement to [matrix row 4](design/ha-s4-evidence-matrix.md#row-4-stop-idle-one-press-non-idle-confirmation),
not a completed proof or a new stop policy. The [reconciliation note](design/stop-policy-reconciliation.md)
records the two Product rulings on #2396; [host-stop.md](host-stop.md) gives the host ordering.
No runs are claimed here. Historical screenshots, host unit tests and this checklist
do not establish a current client/stage pass.

## Run cells and prerequisites

Create a separate evidence folder for each client/version, rehearsal stage,
repetition and attempt. Repeat the required cases **three times per required
client/stage**, following the stages in the [evidence matrix](design/ha-s4-evidence-matrix.md).
The matrix owns inclusion and retirement-blocking status; this checklist grants
neither an exemption nor a new blocking dependency.
Label migration-side and rollback-side observations separately, including the
restore/repeat boundary. Positive Slipgates stop cases require an authorized
delivering stage. Where the legacy guard is active, retain refusal/no-effect
evidence as such, never as a successful stop or a reason to disable the guard.

| Client cell | Route to exercise; keep a gap if unavailable |
|---|---|
| PWA desktop browser / installed | Actual End session UI; record browser and installation mode separately |
| PWA Android | Actual Android browser/installed PWA run; never a native Android claim |
| PWA iOS/iPadOS | Actual browser/installed PWA run; never evidence for the native Apple client |
| Windows | Actual Windows app End session UI |
| Mac / iOS/iPadOS native | Separate remote-client runs against a supported disposable host; preserve the matrix's parked status |
| MCP agent | No stop tool; host currently refuses agent stops. Keep I-MCP/L-MCP open, not a successful person-stop substitution |
| Host CLI | Entry point unestablished; keep I-CLI/L-CLI open until Engineering identifies a shipped route |
| Optional HA | Proposed runtime is absent; keep HA/STOP open. Legacy connector evidence stays separately labeled |
| Android / Linux native | Unshipped, not passed; add client-specific runs when shipped |

Use only authorized disposable targets and the existing rehearsal setup, not
the owner's host, prompts, HA or broker. The runner must already have the required
caller rights and test fixtures. Do not impersonate a person or change trust,
permissions, credentials or host setup to make an unavailable route work.
Exercise person and existing authorized system routes separately where present;
shared stop semantics do not authorize an MCP stop tool.

Record client/build, protocol version, full host implementation SHA, agent CLI
(Copilot, Claude or Codex), disposable machine alias, stage, repetition and attempt.
Record the missing route or fixture as a gap, with its issue, instead of skipping
its cell. Keep #2411's guarded-idle cases open until Engineering/Quality confirms
the fix landed and the installed build includes it. They must pass before the
20-stop retirement gate run; PR #2431's review is not evidence of landing.

## Evidence procedure

Use the existing `agent-bridge-rehearsal` binary and its `steps` JSONL, marks and
screenshots. Do not reuse a folder: its report selects the latest attempt per
step, not three independent repetitions. Keep every attempt's full output.
The following is a template for an approved disposable run, not an instruction
to start a local build or a live stop from this builder session.

Set `$fixtureHome` and `$fixtureProfile` to the approved disposable fixture,
`$evidence` to a new run-cell/attempt folder, `$target` to its exact session id,
`$witness` to a second disposable session that must survive, and `$checklist`
to this file's absolute path. Never select by newest PID or title.
For example, the plain-idle case starts with:

```powershell
agent-bridge-rehearsal host --home $fixtureHome --profile $fixtureProfile --evidence $evidence --step S4-01a --expect-delivering
agent-bridge-rehearsal sessions --home $fixtureHome --profile $fixtureProfile --evidence $evidence --step S4-01b --id $target --status idle --expect-count 1
agent-bridge-rehearsal mark $target --home $fixtureHome --profile $fixtureProfile --evidence $evidence --step S4-01c
```

Inspect the captured state and fixture record: plain idle also requires no
background work, no question, a readable transcript and a ready launch. An
`idle` label alone is insufficient. Now perform the case through the named
client, preserving each press's request UUID, host-relative timing and outcome.
Capture confirmation receipt/correlation without publishing raw tokens.
Keep typing/termination instrumentation that identifies the original PID and
start time; a disappearing card alone does not prove the correct target stopped.

After an expected successful stop, check removal and the surviving witness:

```powershell
agent-bridge-rehearsal sessions --home $fixtureHome --profile $fixtureProfile --evidence $evidence --step S4-01d --id $target --expect-gone --timeout 30
agent-bridge-rehearsal sessions --home $fixtureHome --profile $fixtureProfile --evidence $evidence --step S4-01e --id $witness --expect-count 1
```

For a graceful stop, also check the post-mark transcript:

```powershell
agent-bridge-rehearsal expect shutdown $target --home $fixtureHome --profile $fixtureProfile --evidence $evidence --step S4-01f --timeout 30
```

Do not demand a graceful transcript event for an intentional no-typing or forced
termination. Those cases need host/fixture typing and process-handle evidence,
including **zero `/exit` writes** where specified. A failed/missing transcript
read is not proof of zero typing. For an unreadable-transcript fixture, retain
independent fixture instrumentation; do not turn `mark` failure into a pass.
For refusal or re-arm cases, verify both target and witness remain alive, with
no typing or termination. Keep unfiltered client outcomes and host/fixture logs.

## Cases per run cell

Each row needs its own outcome and evidence; use a fresh target after a stop.
A second press uses a fresh request UUID and the first press's confirmation
within the host's 10-second window. Never reuse the first request as confirmation.

| Case | Action and required observation |
|---|---|
| **S4-01** | Plain idle: one press ends only the target; `/exit` precedes normal graceful exit |
| **S4-02** | Working: first press returns `confirm`, no effects; valid second press ends the target |
| **S4-03** | Waiting: first press returns `confirm`, no effects; valid second press ends the target |
| **S4-04** | Idle with background agents/commands: first press returns `confirm`, no effects; valid second press ends the target |
| **S4-05** | Late second press: after the host's 10-second window, re-arm without effects; only a subsequent valid confirmation ends the target |
| **S4-06** | Replay the first request UUID: cached `confirm`, no effects, no extension of its original window; replay is not a second press |
| **S4-07** | Open question: first press arms; valid confirmation ends without typing `/exit` into the question |
| **S4-08** | Idle-reported unreadable transcript: arm, then confirmed termination with zero `/exit` writes; known #2411 gap until landed and rerun |
| **S4-09** | Idle-reported launch not ready: arm, then confirmed termination with zero `/exit` writes; known #2411 gap until landed and rerun |
| **S4-10** | Idle-reported transcript-only question: arm, then confirmed termination with zero `/exit` writes; known #2411 gap until landed and rerun |
| **S4-11** | Elevated target fixture: `refused` before arming, no typing/termination |
| **S4-12** | Other-user target fixture: `refused` before arming, no typing/termination |
| **S4-13** | Unreadable start-time fixture: `refused` before arming, no typing/termination |
| **S4-14** | Host-process target fixture: `refused` before arming, host and witness remain alive |
| **S4-15** | No live PID: capture the contract's `refused`/`stale` distinction; unidentified targets are refused, already-gone sessions are stale; neither arms or acts |
| **S4-16** | Process replaced after arming: confirmation must not type into or terminate the replacement; preserve old/new start-time receipts |
| **S4-17** | Target ignores `/exit`: after a confirmed stop, force only the held process after the 12-second grace; becoming busy does not cancel confirmation |
| **S4-18** | Replay an ended request: same cached outcome and no additional typing/termination; do not count it as another stop |

Use pre-provisioned disposable fixtures for protected-target and PID-race cases.
If a client cannot address the fixture, record that client-specific gap; a host
test is supporting evidence, not proof that this client delivered the request.
The existing legacy-bridge guard still applies, including before force; never
start or restart the live bridge to exercise it.

Record a case's observed verdict with `note`, citing the output, screenshot and
fixture receipts. Use `fail` for a mismatch, `manual` for unavailable/unrun proof,
and `pass` only after all observations hold. For example, an unrun edge stays:

```powershell
agent-bridge-rehearsal note --home $fixtureHome --profile $fixtureProfile --evidence $evidence --step S4-08 --result manual --detail "Known #2411 gap; installed fix and client proof not established"
agent-bridge-rehearsal report --home $fixtureHome --profile $fixtureProfile --evidence $evidence --checklist $checklist
```

Tool exit 2 (uncheckable), exit 3 (manual), an absent case, or a gap is not a pass.
The report is an evidence index, **not an automatic retirement-gate verdict**.

## Three repetitions and the 20-stop ledger

Maintain a client/stage/case index linking r1, r2 and r3 to their separate attempt
folders and actual verdicts. Preserve failures and corrections, not just the
latest successful receipt. No inheritance between clients, caller routes,
agents, builds or stages; identify any unrun combination explicitly.

Separately number at least **20 distinct, successful correct-target stops across
all four Windows machines**. Each ledger entry links the run cell, case, request
correlation, target PID/start time, witness-survival receipt, result and evidence
folder. Use disposable aliases M1-M4, not fleet node ids. List each machine's
subtotal and reconcile them with the distinct-stop total.
First presses, refusals, stale results, retries/replays of one operation and
fixture-only tests do not inflate that total. A valid stop may contribute to
both its run cell and the aggregate, but never substitutes for another cell's
missing repetitions. Do not begin the retirement gate before the #2411 fix has
landed and passed the guarded-idle cases.

Send Quality the complete index, all attempt outputs, gaps and 20-stop ledger;
Engineering/Quality assess the matrix's current client/stage requirements and
receipts. This checklist neither grants retirement nor closes #2396.
