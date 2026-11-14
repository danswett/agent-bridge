# S4 host CLI: exposure, person boundaries and missing mechanisms

Inventory for [#2402](https://github.com/danswett/agent-bridge/issues/2402),
under [#1396](https://github.com/danswett/agent-bridge/issues/1396).
**Sensitive:** approval, stop, attachment delivery and push boundaries.
This is a static inventory, not implementation authorization or live qualification.

Builder: SG Builder 2402 host CLI evidence
Author-model: gpt-6.1-sol

## Baselines and scope

Host source and tests: `518a7d2826604a82cab0333ebcb97d4869c4463e`.
The nine-row [S4 matrix][matrix] was read from its immutable local Git object,
`75001259346486b56eb53bee4a722c9291c77132`; it is not present at the host baseline.
[MCP's sibling inventory][mcp] also identifies that matrix source.
Cross-repository context: `danswett/slipgates-fleet`, `docs/ha-retirement-parity.md`
at `837f5e0ebb628cbc5c8affd1c2300c16225e1619`, read whole locally.
No GitHub API read was used.

Rows are **4, 5, 8, 15, 16, 17, 27, 28 and 29**. Partial below means actual CLI
read exposure exists, not that the row's person-facing mechanism is complete.
Absent means the required mechanism has no command in the inspected host dispatch.
No row is complete. These observations supersede the earlier incomplete host CLI
read in [row 17's reconciliation][forms], not another client's evidence.

## What a person can actually run

Use the installed host executable, not an operation name from the protocol.
For example, in a person's PowerShell terminal:

```powershell
$hostExe = "$env:LOCALAPPDATA\AgentBridgeApp\current\agent-bridge-host.exe"
& $hostExe sessions --compact
& $hostExe get '<session-id>'
& $hostExe watch --json
& $hostExe usage --profile default
```

These are source-derived examples, **not commands exercised by this inventory**.
The installed executable/path and version must be recorded in later live evidence.
Do not run `usage` as an offline fixture check: its collectors can contact providers
and start probes. `watch` remains attached until cancellation.

| Actual command | Dispatch and output | Boundary |
| --- | --- | --- |
| `sessions [--compact]` | [Program lines 155-161][cli]: refreshes a local `SessionSource`; emits `SessionList` JSON including revision and holds. | Current OS user's readable roots; not a remote fleet/person credential. |
| `get <session-id>` | [Lines 162-169][cli]: refreshes the same source; emits session JSON, or `{"missing":true}` with exit 1. | Local current snapshot; no machine selector or question-identity argument. |
| `watch [--json]` | [Lines 192-209][cli]: starts a local source and subscription; JSON events are `{ev,data}`. Text formatting is in `Describe`, lines 1226-1233. | Local session changes; neither OS push registration nor a person's foreground/focus assertion. |
| `usage [--profile <name>]` | [Lines 228-258][cli]: validates the profile, respects publish-off and collects usage, including additional accounts. | Local provider/profile access; neither an all-machine dashboard nor an update command. |

These read paths instantiate the source directly; they do not pass through the
running host's pipe caller guard. They do not prove an authenticated person's
identity, nor an agent-principal visibility restriction. OS file access still
applies; do not describe local transcript access as an anti-agent security boundary.

The remaining [top-level dispatch/help][cli] was inspected through its default
case (lines 259-363). There is no stop, answer, update, forget, form-submit,
notice-open or image-reply verb. An unknown verb reaches usage and exit 2.
`serve` starts a server; accepting an operation there does not expose a CLI client.
`sign-in` signs in the host, and `whoami` describes its identity, not a person
device for updates or Forget. `rename` is a narrow guarded pipe command, not a
generic protocol-call escape hatch.

The [injector entry point][injector] is an internal key-plan worker, not a
question-bound person approval/reply client. Do not substitute direct injection,
an HA helper, or the [rehearsal parser's tests][rehearsal] for a product CLI verb.

## Nine-row inventory

Evidence labels below distinguish executable dispatch (**D**), underlying unit
test bodies (**T**) and permission/security contracts (**B**).
None is a newly executed test or a real CLI-to-transcript receipt.

| Row / mechanism | Real CLI exposure and status | Permitted-person boundary and retained gap | Evidence / proposed cuts |
| --- | --- | --- | --- |
| 4: stop | **Absent.** `get` can inspect a target, but no command stops it. | Person action only; no agent permission follows from a host stop handler. Apply the current reconciliation, not the matrix's historical idle-only rule. | D1; B1, B2; C1, C2. **Sensitive.** |
| 5: approvals / decisions | **Partial, read only.** `get`/JSON `watch` expose the session's question data; no approval renderer or answer command. | Reading a question grants no consent. Ordinary person answers must bind the current session/question/request and preserve host refusals. Escaped approval is not available on the local pipe. | D1; T1, T3; B1, B3; C1, C3, C4. **Sensitive.** |
| 8: owner updates | **Absent.** `usage`, `sign-in` and migration are not check/install/update-result controls. | Owner-person intent is required. `update.*` is refused on the pipe even for the app; app update reporting is app-only. Host credentials must not become person credentials. | D1; T4; B1; C6. **Sensitive.** |
| 15: Forget / upkeep | **Absent.** No account roster or Forget command. | Relay permits Forget only for person device kinds `app`, `pwa`, `phone`, not `host`, agent or test identity. Same account, offline target and two-press confirmation remain required. | D1; B4; C7. **Sensitive.** |
| 16: finished-turn / question push | **Partial, raw local changes only.** `watch --json` is an event feed, not a notification lifecycle or delivery receipt. | Agents cannot assert the person's focus to suppress pushes. A foreground terminal stream proves neither background/locked delivery nor withdrawal after another device answers. | D1; T1, T3; B1, B3; C8. **Sensitive.** |
| 17: native named-field forms | **Partial, read only.** Question JSON can describe fields; no editor/typed named-field submission command. | A text reply, positional choices or hand-built raw pipe call is not a native form. Preserve current-question, type, required, capability and at-machine checks; no agent approval authority. | D1; T1; B3; row-17 contract; C1, C5. **Sensitive.** |
| 27: dashboard views | **Partial.** Local sessions, current session/activity/question and local usage are exposed separately as JSON. | No all-machine person view or dashboard action parity is established. Local file reads cannot stand in for account-scoped fleet membership; writes retain their own boundaries. | D1; T1, T2; C10 plus action cuts. Read-only inventory is not dashboard acceptance. |
| 28: notice opens exact question | **Absent.** `get <session-id>` opens neither a notice nor a pinned question; the current question may have changed. | Never answer or focus a replacement question using stale notice identity. No CLI notice-target resolver, stale/answered treatment or authenticated presence/focus behavior is exposed. | D1; T3; B1, B3; C8, C9. **Sensitive.** |
| 29: reply images | **Absent.** No text-reply command, attachment upload/stage command or image delivery result. | Person reply only, not a means to answer a prompt. Preserve device-owned attachment IDs, hashes/limits, composer/question guards and confirmed/unconfirmed outcomes; never replay ambiguous typing. | D1; B1, B3, B5; C1, C11, C12. **Sensitive.** |

### Stop and form reconciliation

[Stop policy reconciliation][stop-policy] records idle one-press, non-idle
two-press confirmation within 10 seconds, graceful `/exit`, then forced termination
of the same process after 12 seconds. A question, unready launch or unreadable
transcript requires two presses and no `/exit`, including a reported-idle session.
Its documented #2411 late-transcript gap remains a gap, not an approved exception.
There is still no person CLI entry point to those mechanics.

Row 17's `f1` through `f12` are HA per-field dropdowns, **not keyboard function
keys**. Keep the [native five-field contract][forms]: named choice, boolean,
Unicode text, number and multi-select together, without Other in the positive
case. JSON inspection alone does not satisfy it.

## Evidence index and limits

| Label | Inspected evidence | What it establishes, and what it does not |
| --- | --- | --- |
| D1 | [Program][cli], lines 75-363, 380-388 and 1226-1233. | CLI configuration, complete dispatch/help, person-only refusal helper and watch formatting; not live delivery. |
| T1 | [SourceTests][source-tests]: `Subscribing_sends_every_live_session_then_synced_and_then_live_changes` (207); `Watching_picks_up_appended_lines_without_an_explicit_refresh` (395); `A_full_scan_and_watch_cycle_never_writes_under_the_agent_and_bridge_roots` (491). | Underlying snapshot/live events and read-only scan/watch fixture assertions. They do not launch the product CLI executable. |
| T2 | [UsageTests][usage-tests], `The_first_pass_asks_every_agent_and_announces_what_they_said` (553), and non-finite reading case (574). | Collector/report fixture assertions, including explicit per-agent failure; not fleet view or installed CLI proof. |
| T3 | [CallerGuardTests][caller-tests] and [LocalApiTests][pipe-tests], lines 804-953. [LocalApi][pipe-code], lines 20-126. | HostCli ancestry classification; agent answer/history/focus refusal; narrow CLI restart exception. These are pipe guards, not read-command guards or a person CLI action surface. |
| T4 | [LocalApiTests][pipe-tests], `Only_the_app_reports_this_machines_updates_and_the_pipe_asks_for_none` (1025); [LocalApi][pipe-code], lines 65-80. | Local update refusal and app-only reporting. No update CLI or all-machine installation receipt. |
| B1 | [Local pipe contract][pipe], read whole. | Caller distinction, agent refusals, local push/update limits. Same-user ancestry checks are safety rails, not a malware boundary. |
| B2 | [Host stop][host-stop] and [stop reconciliation][stop-policy], read whole. | Existing stop contract and its known gap, not command exposure or a new grant to agents. |
| B3 | [Host answers][answers], [v1 sessions][sessions] and [client protocol design][clients], read whole. | Approval/form, exact-question, attachment and push/focus contracts. Contract presence is not CLI implementation. |
| B4 | [Roster contract][roster], read whole. | Person-device allow-list, same-account offline Forget, confirmation and unavailable/disabled outcomes. |
| B5 | [Host replies][replies], read whole. | Reply/composer/question fences, file staging and transcript confirmation; no CLI wrapper. |

The cited test bodies were read, **not run**. No installed CLI command, host action,
build, quality harness or hosted CI was executed/read for this cut.
No process-level host CLI receipt was established by the inspected tests.
The rehearsal CLI tests exercise a different tool's parser.

This is not a complete audit of every linked security design: in particular the
full agent-principals, devices, launch and update designs were not audited here.
Future person-client authentication or update work must read and reconcile its
whole subject contracts before implementation; this inventory authorizes none.

## Issue-ready bounded follow-ups

Engineering will file/reconcile these proposed issues; **C numbers are local
proposal labels, not filed issue numbers**. Each size includes its tests/docs.
The [adopted matrix][matrix] requires every follow-up to be recut before exceeding
250 changed lines, including tests and docs. This packet grants no override.
Do not waive guards or acceptance to fit; retain remaining work as dependent cuts.
Dependencies between proposals mean the resulting filed issues.
These are first bounded cuts, not promises that an entire matrix row fits one PR.
Do not extend existing person privileges to agents or turn on host settings.
Product/Engineering must decide unresolved CLI interaction equivalence before
implementation; an inability to notify is a gap, not an automatic row exemption.

### C0 - Establish process-level evidence for the existing host CLI reads

Scope: fixture-isolated product executable checks for `sessions` and missing
`get` first. Reuse an existing process fixture if available; do not run provider
collectors or live roots.
Size: at most 250 changed lines. Sensitive: yes (read-root seam). Depends-on: #2402.
Acceptance: capture stdout/stderr/exit and unchanged fixture roots; distinguish
product host executable tests from the rehearsal parser's tests.
Remaining: JSON `watch` startup/cancellation and unknown-verb exit proof in
dependent process-evidence cuts, each within the same 250-line ceiling.

The #2488 process fixture starts `agent-bridge-host.dll`, not the rehearsal parser.
Its `sessions`/`get`-only `--read-root` derives agent, registration and probe roots;
profile intake is skipped so migration state cannot redirect reads outside it.
Invalid or conflicting roots are refused before discovery; defaults stay unchanged.
Fixture fingerprints check directory entries, file content and write times.

### C1 - Reconcile the person CLI action transport and caller contract

Scope: docs/contract vectors for person invocation, fresh complete ancestry,
stable target/request identity and explicit refusals. Inventory reusable pipe
client code; no general arbitrary-operation command or new credential grant.
Size: at most 200 lines. Sensitive: yes. Depends-on: #2402.
Acceptance: person/agent-child/unknown-ancestry cases and allowed operation list;
separate pipe-capable actions from relay-only person-device operations.

### C2 - Expose one local person CLI stop path without changing stop policy

Scope: one target on this machine through the existing guarded service; render
arming/refusal/result, preserve confirmation expiry and process identity.
Size: at most 250 lines. Sensitive: yes. Depends-on: C0, C1, stop reconciliation.
Acceptance: tests first for idle, busy confirmation and stale confirmation; agent
invocation refused. Retain the #2411 dependency for affected safety/live cases.
Remaining: #2411-affected qualification and multi-machine CLI stop rehearsal.
Additional adapter/renderer work must be recut before exceeding 250 lines.

### C3 - Expose ordinary single-choice person approval, with refusal fences

Scope: one supported ordinary, non-escaped approval; display its actual text and
bind session/question/request on submission. No trust, escaped approval or forms.
Size: at most 250 lines. Sensitive: yes. Depends-on: C0, C1.
Acceptance: a real command parser/transport test plus current-question success,
stale/at-machine/truncated/escaped refusal and agent-child no-send assertions.
This does not qualify every agent's approval adapter or the full row.
Remaining: other agents' adapter qualification, escaped/trust (C4), forms (C5)
and live approvals, in separately bounded cuts.

### C4 - Decide the CLI boundary for escaped approval and folder trust

Scope: docs and refusal vectors only; reconcile person-device verification with
local-pipe exclusions. No raw confirmation forwarding or terminal trust bypass.
Size: at most 200 lines. Sensitive: yes. Depends-on: C1 and whole subject designs.
Acceptance: approved route or explicit retained gap per approval kind; ordinary
host sign-in and agent credentials cannot stand in for verified person intent.

### C5 - Specify native CLI named-field forms before an editor cut

Scope: one usable named-field interaction contract and five-field vectors,
including unsent draft and answer-elsewhere behavior. Do not call JSON output,
five unrelated prompts or a free-text reply a native editor.
Size: at most 250 lines. Sensitive: yes. Depends-on: C1, #2400.
Acceptance: preserve names/types, validation and refusal semantics from row 17;
name the subsequent editor/submission cut rather than closing the row on design.

### C6 - Specify owner update commands without borrowing the host identity

Scope: read the full update/device contracts; define check/install/result commands,
person authentication and target binding. Docs/vectors only, no local-pipe bypass.
Size: at most 250 lines. Sensitive: yes. Depends-on: C1.
Acceptance: app-only reporting and pipe refusals remain; identify the supported
owner-person route and split its implementation before any all-machine rehearsal.

### C7 - Specify person CLI Forget against the actual roster allow-list

Scope: docs/vectors for a permitted person-device route, offline machine identity,
five-second two-press arm and account/lastSeen/reconnect invalidation.
Size: at most 250 lines. Sensitive: yes. Depends-on: C1.
Acceptance: host/agent/test devices remain refused; preserve online, unavailable,
disabled and account-isolated outcomes. No deletion of a local profile as Forget.

### C8 - Define CLI turn/question lifecycle evidence, not fake push parity

Scope: docs and event vectors separating the local watch feed, a terminal client
and a notifying client. Define question/turn identity, withdrawal and reconnect
behavior; record background/locked limitations. No owner paging or alerting work.
Size: at most 250 lines. Sensitive: yes. Depends-on: #2402.
Acceptance: no claimed background receipt from stdout, no agent focus assertion,
and an explicit Engineering/Product disposition for remaining notification gaps.

### C9 - Add read-only exact-question target validation for a person CLI

Scope: after a target contract is accepted, inspect the pinned session/question,
refuse a stale/replaced target and report already-answered/missing explicitly.
No answer, focus suppression, OS notification registration or delivery.
Size: at most 250 lines. Sensitive: yes. Depends-on: C0, C8.
Acceptance: exact identity success and changed/answered/missing refusal fixtures;
plain `get <session-id>` is not accepted as that evidence.
Remaining: notice ingress/navigation implementation, withdrawal lifecycle and live
opening proof after C8's contract; identity validation alone delivers none of them.

### C10 - Map CLI dashboard views to local versus account-scoped reads

Scope: docs/output vectors for machine/session/question/usage views; identify
existing reusable readers and missing account-scoped person CLI transport.
No dashboard write implementation, credential changes or provider probing.
Size: at most 200 lines. Sensitive: yes (account/person boundary). Depends-on: #2402.
Acceptance: offline/unknown/stale distinguished; raw local JSON and agent fleet
metadata are not presented as full person dashboard/action parity.

### C11 - Add one guarded person CLI text-reply path before attachments

Scope: local text reply through the existing service and its delivery receipt;
keep composer/question fences and do not implement a second injector.
Size: at most 250 lines. Sensitive: yes. Depends-on: C0, C1.
Acceptance: delivered/refused/unconfirmed outputs; agent-child no-send,
question-open no-send and no automatic retry after ambiguous typing.
Remaining: each agent's real CLI-to-transcript qualification and image support
(C12); any further CLI/adapter work must be recut within 250 lines.

### C12 - Add a bounded image-reply path using the person reply contract

Scope: one image up to 256 KiB through existing attachment start/chunk and reply;
preserve device ownership, SHA-256, limits and failure cleanup. No blob expansion.
Size: at most 250 lines. Sensitive: yes. Depends-on: C11.
Acceptance: command-to-request fixture, hash/foreign-ID refusal, image transcript
receipt and unconfirmed handling.
Remaining: files over 256 KiB, relay blob transport and broader agent/format
qualification, each in separately sized cuts within the 250-line ceiling.

## What remains before S4 acceptance

The [CLI live-proof lane #2395][live] still needs documented real entry points,
the actual client/output and host/transcript receipts, three runs per row per
rehearsal stage. Row 17 needs the named five-field values together, not a schema
dump. Keep refusal and wrong/stale-target evidence as well as positive delivery.
No inventory label waives the matrix's retained per-client acceptance.

The epic's continuous-run counters remain separate: 500 messages, 50 launches,
30 real approvals, 20 stops and zero HA fallbacks; counter work is #2384 and
load proof is #1949. This cut provides none of those results and retires no HA path.
Validation here is document review and offline Git whitespace/stat checks only.
Sensitive independent exact-head receipts and hosted proof remain Quality's work.

[matrix]: https://github.com/danswett/agent-bridge/blob/75001259346486b56eb53bee4a722c9291c77132/docs/design/ha-s4-evidence-matrix.md
[mcp]: mcp-s4-evidence.md
[forms]: ha-row17-native-forms.md
[cli]: ../../src/AgentBridge.Host/Program.cs
[injector]: ../../src/AgentBridge.Injector/Program.cs
[rehearsal]: ../../tests/AgentBridge.Rehearsal.Tests/CliAndEvidenceTests.cs
[source-tests]: ../../tests/AgentBridge.Host.Core.Tests/SourceTests.cs
[usage-tests]: ../../tests/AgentBridge.Host.Core.Tests/UsageTests.cs
[caller-tests]: ../../tests/AgentBridge.Host.Tests/CallerGuardTests.cs
[pipe-tests]: ../../tests/AgentBridge.Host.Tests/LocalApiTests.cs
[pipe-code]: ../../src/AgentBridge.Host/LocalApi.cs
[pipe]: ../protocol/local-pipe.md
[host-stop]: ../host-stop.md
[stop-policy]: stop-policy-reconciliation.md
[answers]: ../host-answers.md
[sessions]: ../protocol/v1-sessions.md
[clients]: client-protocol.md
[roster]: ../protocol/roster.md
[replies]: ../host-replies.md
[live]: https://github.com/danswett/agent-bridge/issues/2395
