# S4 MCP evidence: implemented surface and remaining cuts

Reconciliation for [#2401](https://github.com/danswett/agent-bridge/issues/2401),
parent [#1396](https://github.com/danswett/agent-bridge/issues/1396), November 12,
2026 (Pacific). Source baseline: main
`9a0acf32e28fca6e03542c0b7b35a20498789581`.
This is a static code inventory, not live parity, a permission grant, or authority
to retire HA. No runtime, signing, approval, stop or notification behavior changes.

## Reconcile the records first

Engineering supplied the complete #2401 and
[#45](https://github.com/danswett/agent-bridge/issues/45) bodies, with no comments.
#45's reconciliation says #1125 shipped the stdio server. Its older body still
says there is no server, that HTTP should have full tools, and that the legacy
HA connector lets MCP follow cutover. Those statements are not current evidence:
the server exists; the supplied Q8 decision allows no HTTP agent principal or
acting tools; HA retirement cannot rely on the retiring connector.
[Agent-principals section 3.6](agent-principals.md#36-how-45s-mcp-server-fits)
describes the intended stdio/HTTP distinction, not proof all its APIs are built.

The [#2383 matrix at its inspected head](https://github.com/danswett/agent-bridge/blob/75001259346486b56eb53bee4a722c9291c77132/docs/design/ha-s4-evidence-matrix.md)
provides the nine row numbers and the separate live-evidence contract.
Its idle-only stop wording is superseded by Engineering's supplied Product ruling
on [#2396](https://github.com/danswett/agent-bridge/issues/2396): person and system
stops use one press when idle, two-press confirmation otherwise. That does **not**
decide whether an agent caller may stop another session. Ask Product through
Engineering before planning such authority.
The later owner order also replaces the matrix's 250-line PR cap with 400 changed
lines including tests; it does not waive reviews or Quality's admission of each push.

## Complete current MCP tool inventory

Evidence keys used below:

- **S**: [server schemas, capabilities and dispatch](../../mcp/src/server.mjs#L17-L171).
- **T**: [all tool implementations](../../mcp/src/tools.mjs#L1-L140).
- **P**: [pipe framing, event handling and authentication](../../mcp/src/pipe.mjs#L1-L172).
- **H**: [host capability shaping and action refusals](../../src/AgentBridge.Host/LocalApi.cs#L20-L124).
- **F**: [native fleet reads and lookup](../../src/AgentBridge.Host/Fleet/FleetList.cs#L133-L242).
- **L**: [local-pipe contract](../protocol/local-pipe.md).

| Tool schema in S | Implementation / host operation | Actual limitation |
|---|---|---|
| `list_agent_sessions` | T `listSessions` -> `sessions.list` | Local host; paginated, status/metadata by default, activity/question only with `include: "content"`. |
| `read_agent_session` | T `readSession` -> `session.get` | Local snapshot or bounded polling, at most 600 seconds requested through S; question data is returned, not answered. |
| `reply_to_agent_session` | T `replyToSession` -> `session.get`, `session.reply` | Schema/adapter present; needs principal and advertised `sessions.reply`. H does not advertise this to agent descendants and refuses their person reply path. |
| `launch_agent_session` | T `launchSession` -> `session.launch` | Schema/adapter present; same capability/host refusal distinction for `sessions.launch`. A `permissions` argument is not authority. |
| `read_launch_result` | T `LaunchLedger.read` | Process-memory ledger of this MCP server's launches, bounded to 200 entries; not a durable host result lookup. Also gated on `sessions.launch`. |
| `get_launch_options` | T `getLaunchOptions` -> `launch.options` | Also gated on `sessions.launch`; not offered to current read-only agent connections despite the host permitting the read operation. |

These are **all six** tool definitions and dispatches, not a sample.
S starts stdio only, and offers no tools when connection establishment fails.
P sends `hello`, then `agent.hello` only when `AGENT_BRIDGE_TOKEN` is supplied.
Without an accepted principal S offers/dispatches only its two read tools.
H advertises only `sessions.read` and `usage` to agent descendants; accepting a
credential does not add reply/launch capabilities. F's `fleet.list`/`fleet.find`
and the native `agent.send` entry point are not wired into these MCP tools.
`machine` on launch/options/result schemas is ignored, never remote routing.
`ask_via_home_assistant`, native elicitation and HTTP transport are absent.

The MCP tests use a fake host, including one advertising acting capabilities.
Their successful reply/launch cases prove adapters, not permission on a real host.
The current 16 KiB prompt/reply limit is not the legacy unlimited payload contract.
`since` hashes response plus turn count. With `since`, `done` requires a changed
marker and a status other than `working`; without it, an old non-working snapshot
can already be `done`. This is not a durable finished-turn event receipt.
P discards event frames, so polling cannot be called a push subscription.
Current metadata includes titles/paths; the design's future non-tainting typed
projection and provenance machinery are not certified by the word "typed".

## Nine S4 rows: static status, not live qualification

**Present** means an exposed, permitted mechanism for the row, not just a schema.
**Partial** names an observable subset; **absent** means no MCP implementation
for that mechanism in S/T/P. No row below has a new live receipt.

| Row | MCP status and exact evidence | Missing mechanism / disposition |
|---|---|---|
| 4 Stop | **Absent; agent stop authority undecided, ask Product.** S has no stop tool; H `Acts` currently refuses agent `session.stop`. | Current refusal is not an authority ruling. C1 first; neither a system-stop rule nor a person's confirmation grants an agent stop rights. |
| 5 Approvals | **Absent; refusal-only.** S has no answer/trust tool. H refuses agent `session.answer`; the [folder-trust contract](../protocol/v1-launch.md#launchtrustanswer) is person-device only. | Person-only, never an agent tool. Readable question content does not authorize submission. C2 refusal evidence only. |
| 8 Owner-only updates | **Absent; refusal-only.** S has no update tool; H permits acting `host.*` management only to its authorized person callers. | No agent Check/Install grant. C2; the person's update workflow remains outside this MCP surface. |
| 15 Forget machine | **Absent; refusal-only.** No forget or roster-management schema/dispatch in S/T. | Person-only confirmed forget must not become an agent tool. C2; fleet reads are not roster mutation. |
| 16 Finished-turn/question push | **Partial.** T `readSession` returns response/question snapshots and polls; P `#dispatch` drops `ev` frames. | No subscription, background delivery, withdrawal or reconnect/dedup receipt. C3/C4; snapshots are not push proof. |
| 17 Native forms | **Partial: question data only.** T list-with-content/read return `question`; S has no elicitation/ask alias or form submission. | No MCP native form interaction. C5; f1-f12 were HA per-field dropdowns, not keyboard function keys. No agent approval/permission submission. |
| 27 Dashboard equivalents | **Partial.** T local list/detail/status only. S lacks fleet lookup, usage and machine-view tools; reply/launch schemas are presently capability-blocked. | C6/C7/C8; F already implements native reads, but no MCP wrapper or verified remote detail routing exists here. |
| 28 Push opens question | **Absent.** No notice/deep-link tool in S/T; P drops events. A manually read `question` is not a notice's exact-target open. | C9 defines the MCP inspect equivalent before implementation; it must never submit or answer by navigation. |
| 29 Images in replies | **Absent.** S reply accepts text only; T sends no attachment reference. H `Acts` refuses agent attachment start/chunk/discard. | C10 first; no person credential, local path or text encoding may stand in for an authorized image-reply mechanism. |

Approvals, updates and forget remain person-only. The ordinary form row does not
permit answering another session's trust or permission confirmation.
No missing person action is repaired by changing the caller to "person".

## Bounded follow-ups, not implementation grants

Each numbered cut is independently capped at **400 changed lines including tests**.
These are first mechanisms, not promises that an entire S4 row fits one PR.
Engineering must confirm existing ownership and dependencies before opening work;
unread issue titles or this inventory do not prove a prerequisite is complete.

| Cut / rows | One bounded deliverable and dependency | Required focused evidence |
|---|---|---|
| C1 / 4 | Record Product's agent-stop decision and reconcile #2396/#1146 with the MCP caller contract. No stop implementation before that decision. | Distinguish person/system stop semantics from agent authorization; retain current host refusal evidence without declaring agent stop policy settled. |
| C2 / 5, 8, 15 | Add MCP negative contract cases for unknown answer/trust/update/forget tool names, with no pipe action. Depends on existing H/L person boundaries; any missing person-client mechanism belongs to that client's track. | An advertised fake acting capability still cannot create these schemas; observe zero acting requests. This establishes refusal-only coverage, not positive parity. |
| C3 / 16 | Add opt-in pipe event demultiplexing with listener disposal, leaving request matching unchanged. Depends on the approved sessions event contract, not Web Push. | Interleaved event/response frames cannot complete the wrong request; malformed/closed connections report failure. |
| C4 / 16 | Add one MCP read subscription lifecycle over C3, with explicit gap/reconnect behavior and bounded cancellation. Depends on approved read scope/provenance and #736's acceptance definition. | Fresh question/turn identity, no stale replay as new, withdrawal and lost connection remain distinguishable. Further background/platform proof is separate. |
| C5 / 17 | Reconcile the missing stdio ask alias/elicitation and named-field contract in #45/#2400; first cut is the contract plus mock-person form cases, not `session.answer`. | Choice, boolean, Unicode text, number and multi-select preserve field identity; trust/permission confirmations cannot be answered through the alias. Actual client form delivery remains a follow-up. |
| C6 / 27 | Expose existing `fleet.list` as an explicit credentialed read, not broaden local list implicitly. Depends on F, audit #1401 and the #55 account/roster contract. | Opt-in names, account-scoped machine/session projection, offline/known-only records and explicit missing-scope/audit failure. |
| C7 / 27 | Expose existing `fleet.find` with typed outcomes. Depends on C6's connection boundary, F and the signing/verification contract; no new signer in this cut. | Preserve incomplete/unverified/ambiguous/offline/notFound; never turn unsigned matches into routable `found` results or infer a model. |
| C8 / 27 | Add one missing permitted read projection, usage first, with capability filtering. Other machine/launcher views and remote session detail are later cuts under #45. | Real field/unknown-capability fixtures; remote content waits on approved signed routing/provenance, never ignored `machine` arguments. Reply/launch authority wiring stays a separate AP1 follow-up. |
| C9 / 28 | Define and test read-only notice-target inspection keyed to current host/session/question, without generating phone alerts. Depends on Product's MCP equivalent ruling, C3/C4, account scope and #736. | Stale, answered, wrong-account and offline targets never navigate as current or submit anything. No notice or live delivery is claimed by this contract cut. |
| C10 / 29 | Reconcile the agent image-reply scope/attachment contract in #45 before exposing it; first code cut would explicitly reject unsupported image arguments without staging or send. | No silent image drop or text/path substitution; zero attachment requests. If separately authorized, protected staging/chunk limits, reply binding and cleanup each need further bounded cuts and #55/#1401 dependencies. |

No signing or protocol contract is changed here. A later cut touching those
contracts needs Trust pairing; sensitive PRs require both reader families.
Code cuts require tests first and Quality-directed hosted red/green proof.
The docs-only first slice has no hosted-red claim and runs no local build or
`quality/run.ps1`, per Engineering's brief.

## Reading depth and remaining evidence

Read **whole**, directly: `mcp/README.md`, `mcp/package.json`, all three
`mcp/src/*.mjs` files (server, tools, pipe), `mcp/test/mcp.test.mjs`,
`docs/protocol/local-pipe.md`, `docs/protocol/agent-operations.md`,
`src/AgentBridge.Host/Fleet/FleetList.cs`, `src/AgentBridge.Protocol/AgentPrincipals.cs`,
`src/AgentBridge.Host/AgentLaunchGate.cs`,
`tests/AgentBridge.Host.Tests/LocalApiLaunchStopAuditTests.cs` and
`tests/AgentBridge.Host.Tests/AgentAnswerLookupAuditTests.cs`.
Read the complete matrix from the cited **local Git object**, not main or GitHub.
Read #2401/#45 bodies and zero-comment state as supplied by Engineering, not by
an independent GitHub read.

Read **excerpts only**: H lines 1-210; `LocalApiTests.cs` lines 801-842;
`agent-principals.md` sections 1 through the start of 3.3, section 3.6 and Q8;
`v1-launch.md` lines 160-240; the retirement decision in `design.md`, current
`status.md` opening sections, relevant `parity.md` sections, and quality docs.
Required contributor/onboarding/worktree instructions were read separately.

**Unread**: full bodies/comments for #55, #736, #1146, #1401, #1949, #50, #161,
#1419 and #1396; the complete #2396 Product thread beyond Engineering's supplied
ruling; remaining agent-principals/signing/provenance design and host/relay/client
source outside the stated reads; the legacy HA MCP implementation and tests in
full; #45's registration/install path. These remain unverified dependencies, not
closed issues. `mcp/package-lock.json` was not audited.

Existing fake-host tests and inspected host refusal tests were **read, not run**.
No installed-client, Windows/Ubuntu hosted, locked/background, load, three-repeat
rehearsal or HA-off run was performed. [#2394](https://github.com/danswett/agent-bridge/issues/2394)
still needs exact permitted MCP outcomes/refusals and missing-path gaps, correlated
with real host/transcript receipts. #1949's load proof, #1396's all-client evidence
and owner-approved retirement gate remain separate and unsatisfied by this note.
