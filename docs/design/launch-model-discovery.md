# Account-scoped launch discovery (#1453)

**Design only; acceptance remains incomplete.** This is an independent successor
proposal to [draft #1784](https://github.com/danswett/agent-bridge/pull/1784);
that branch is untouched.
Four real captures are still required; none is invented below. No adapter, probe,
credential access, hidden session, trust answer or rollout is authorized here.
Quality owns gates and landing; this sensitive design needs two independent
model-family receipts on its exact head.

## Current behavior and evidence

[host-launch](../host-launch.md) describes the shipped implementation.
`LaunchService.Tuning` takes the configured model list when present; otherwise it
combines built-in models with models observed in live sessions. The configured
default is offered too. `Launchers.cs` maps Agency to Copilot tuning. None of that
proves account entitlement, and session observations must not be called discovery.

The routes below are historical proposals from
[Engineering's source report](https://github.com/danswett/agent-bridge/issues/1453#issuecomment-6734312045)
and readback of `hooks/session-launch.ps1` in `danswett/agent-ha-bridge`.
They are not verified protocol responses or account-entitlement evidence.
[SRE's capture](https://github.com/danswett/agent-bridge/pull/1784#issuecomment-6804440873)
contains help/version output for Agency, Copilot and Claude; Codex was absent.
It neither validates nor disproves the proposed protocols.
An unsupported version must report a fault and use labeled fallback, never weaken
isolation flags or substitute a generic catalogue as an account answer.

## Per-agent ask

Per [Engineering's ruling](https://github.com/danswett/agent-bridge/issues/1453#issuecomment-6804452821),
discovery cannot use a CLI flag: it requires a protocol request inside a started
session (`model/list` in Codex app-server, Claude's initialize `control_request`,
or Copilot's `session.model.list`). **All three protocol calls, their response
fields and account claims, including Codex's sign-in/cache semantics, remain
unverified until captured.** The CLI flags below only start the proposed process.

| Agent | Route and answer | Account scope and fallback |
|---|---|---|
| Copilot | **Unverified proposed route:** `copilot --headless --no-auto-update --stdio`; JSON-RPC `connect`, inert `session.create`, `session.model.list`, then `session.delete` | **Unverified account claim:** account-scoped through the launch executable, home and sign-in, using a hidden session. If verified, last good matching answer, then pinned list, then built-in |
| Claude | **Unverified proposed route:** `claude -p --input-format stream-json --output-format stream-json --verbose --safe-mode --no-session-persistence --strict-mcp-config`; SDK `control_request` with `request.subtype: initialize` | **Unverified response/account claims:** account models and per-model `supportedEffortLevels`. Unsupported safe mode must fail to the same fallback, not a weaker invocation |
| Codex | **Unverified proposed route:** `codex -c features.plugins=false app-server`; JSON-RPC `initialize`, `initialized`, `account/read`, then paginated `model/list` | **Unverified response/account claims:** per-account list and entries with `supportedReasoningEfforts`. Sign-in/cache provenance must be established before discovery; otherwise use the same labeled fallback |
| Agency | No separate model-list API verified; candidate is its actual Copilot child in the selected profile | **Same as Copilot, unverified.** Until executable, home, account and profile mapping are captured, pinned then built-in; never borrow another scope's discovery |

These are unverified protocol proposals, not complete scripts to run on the fleet.
Copilot's session-less `models.list` omitted picker models in the source report;
`copilot help config` prints a generic catalogue, not an entitlement list.
Claude must retain subscription sign-in: do not use `--bare`. Safe mode and an
explicitly empty MCP configuration must exclude hooks, plugins, skills and project
instructions without changing the owner's stored settings.
The source-reported, unverified Codex route asks `account/read` without requesting
credential refresh and starts no thread. API-key or signed-out responses must not
be treated as verified ChatGPT-plan discovery.

The unverified legacy behavior says Codex may answer `model/list` from its built-in
list when offline. The proposed acceptance check must preserve the
old `models_cache.json` evidence: fetched before the answer, less than 300 seconds
old, and containing every returned model. **That is necessary, not sufficient.**
The old check accepts a nonempty cache identity without comparing it to the active
account or CLI version; its own comment admits a cross-sign-in/version race.
The adapter must also establish that binding across the ask or fail to fallback.
Cache age is a provenance bound, never a refresh timer or a reason to wait.
Missing or future-dated cache, mismatched identity/version, malformed output,
repeated pagination cursor or an unfinished page sequence fails explicitly.

Efforts are per-model sets, not a union that licenses every model/effort pair.
Unsupported pairs are refused, never promoted to a higher effort. If an API lacks
effort metadata, retain configured/built-in efforts with separate provenance.
No independent context-list API is verified for these agents. Contexts remain
configured, else built-in, not `discovered`. Claude's `sonnet[1m]`-style identifiers
are model variants, not permission to invent a context axis. Preserve existing
empty/default and `auto` semantics; a default is not an entitlement claim.

## Real capture ledger: unmet acceptance

| Agent | Real response | Required evidence |
|---|---|---|
| Copilot | **Missing** | Sanitized `session.model.list` response; inert settings and deletion/process-exit evidence |
| Claude | **Missing** | Sanitized initialize response with models and per-model efforts; safe-mode version |
| Codex | **Missing** | Sanitized complete `model/list`, `account/read` scope result and cache account/version binding result |
| Agency | **Missing** | Actual profile/child model response, or a captured unsupported ask with the explicit fallback |

Platform or SRE records the captures on #1784 and links them here before design
acceptance. Each record needs a machine alias, CLI version, capture time in Pacific,
exact operation, elapsed time, signed-in/offline state, response fields and whether
the list is account-scoped or generic. Keep model/effort values useful for parser
fixtures. Exclude credentials, account names, private paths and session contents.
A source comment, configured list or illustrative JSON is not a real response;
an absent capture is not proof that the CLI has no API.

## Refresh and selection

Adopt [Engineering's refresh ruling](https://github.com/danswett/agent-bridge/issues/1453#issuecomment-6793287305)
under [#1511](https://github.com/danswett/agent-bridge/issues/1511).
The historical six-hour expiry and 60-second exponential retry are rejected.

| Event | Decision |
|---|---|
| Host starts | Start with an empty in-memory cache; the first options read or launch supplies the missing-cache event. No eager credential-context process is required at startup |
| Options read or launch | If missing, invalidated, previously failed, or the CLI version differs, request one background ask; return the current effective snapshot without waiting |
| Explicit refresh | Request an ask for that authorized scope even if the answer is present |
| Executable/home, account, profile or relevant owner configuration changes | Invalidate the affected scope; reject old-generation completions. A new options/launch event or explicit refresh asks again |
| Unknown-model refusal or confirmed vendor rejection | Refuse visibly, invalidate the answer and request refresh; never silently substitute or replay the launch |
| Ask fails | Keep the last good answer only in the same still-verified scope, mark it stale, show its age and reason; the next options/launch event retries |

One ask per scope is in flight. Concurrent events coalesce; a changed generation
requests one follow-up, not a probe per reader. There is no timer-driven refresh,
retry, expiry or arbitrary sleep. A last-resort watchdog reports a probe fault
through existing fault reporting; it never declares success or freshness. This
slice adds no owner paging or alerting.

A successful answer is retained for that host run until invalidation, with its
observation time displayed. Age alone never changes eligibility. Restart loses it.
Signed-out, changed or unverifiable identity discards the old account answer;
version/profile/home changes do not reuse another generation's answer.

Effective model selection is: explicit owner override, otherwise last good
matching discovery (including stale), otherwise configured fallback pin, otherwise
built-in. Only an owner action at the machine may establish or remove an override.
Existing configured lists migrate as fallback pins, not silently as overrides.
No agent call or remote refresh changes that policy. Pins are not entitlement and
never bypass authentication, quota, permissions, workspace or trust checks.
Live-session observations may remain diagnostics but cannot widen a discovered
list or owner override. Defaults must be validated against the effective snapshot.

### Options readback

The additive contract work must update [v1-launch](../protocol/v1-launch.md) and
the agent-safe local-pipe view in #1413 before implementation is enabled.
The proposed metadata is not shipped today. Preserve existing arrays/defaults;
all installed agents remain independent of the selected launch-card agent.

Each axis exposes source `discovered`, `pinned` or `built-in`, observation time
where known, stale/pending state and a bounded failure reason. Distinguish an owner
override from a fallback pin. Models carry per-model effort sets; effort/context
provenance is independent of model provenance. A signed-out or unsupported fallback
is visible, not success-shaped discovery. Never return credentials, account
identifiers, local paths or raw protocol output. The host revalidates every launch
against its current effective snapshot, not the client's cached display.

## Cost, isolation and cleanup

Historical source evidence says no prompt is submitted and no model inference or
premium request is used (Copilot checked then on 1.0.95). Reported elapsed times
were Copilot 3-4 seconds, up to 12 cold; Claude/Codex about 1 second; Agency unknown.
These are not current measurements or a billing guarantee; captures must establish
current behavior. Every ask starts a process under the owner's sign-in and may
contact the vendor. The unverified Copilot proposal includes a hidden throwaway
session. Engineering's ruling requires Chief of Staff and Quality approval for a
signed-in probe, one CLI at a time; Codex must be installed before its capture.
This slice installs nothing. Merging this note is not execution approval.

Use the same approved executable, agent home and account as launch, including
Copilot's launch-account checks in host-launch. Never search for alternate tokens,
sign in, alter permissions, accept a trust prompt or change user settings.
Signed-out is a visible failure with labeled fallback, not a login flow.
Use an owned empty scratch folder, no user prompt, inert hooks/skills/project
discovery and no MCP servers. Bound protocol/output sizes and validate every reply.
The successful path closes the protocol and observes process exit; Copilot also
deletes only its own throwaway session. Cleanup failures are reported and block
another probe in that scope until process/session state is reconciled.

Register exact probe process identity plus start time before session discovery
can observe it; exclude only that owned probe (including Codex's app-server), not
all headless agents or a recycled PID. Copilot's `inuse.<pid>.lock` must not create
a session card. Record the exclusion rule in [host-hooks](../host-hooks.md).
Do not copy the legacy forced-kill cleanup: current `ProbeLauncher.cs` deliberately
does not kill on disposal because credential rotation can leave an account signed
out. Engineering must specify graceful shutdown and retained-process reconciliation
before enabling an adapter. No broad session-folder cleanup or timestamp rollback
over concurrent sessions is permitted.

**Where it runs:** the same adapter on each participating host, no designated VM.
**If a node leaves:** its offers disappear; another host never substitutes its
account list. **State:** transient per-host memory keyed to the local
executable/version/home/account/profile generation; restart rebuilds from events.
This is not fleet coordination state or a durable cache pinned to one machine.
Existing protected transport carries readbacks, never credentials.

## Why machines disagree (#1142)

#1142 reports Opus on VM1/home but narrower Agency lists on VM2-VM4. The current
configured-versus-built-in/live-observation code can explain differing lists
without an entitlement difference. Accounts, versions and Agency profiles can also
differ; no paired pin/account captures establish the cause on each named machine.
Report "fallback list; entitlement unverified", not "Opus unavailable". Engineering
must compare sanitized configuration provenance with actual account responses to
attribute a restriction to an owner pin or to the account's returned catalogue.

## Bounded implementation slices and order

All rows are **sensitive: yes**, include tests/docs, and have a hard 400-line cap;
budgets are estimates, not exceptions. Paths below are proposed files. Engineering
splits a row again if needed. **Implementation slices M1 to M4 are blocked; nothing
is built on an unverified protocol.** No code slice starts until the four captures
and this event-driven design are accepted; all adapters stay unwired until
integration. Until then the host keeps its current list, labeled not discovered.

| Order | Slice and budget | Files and failing-first evidence |
|---|---|---|
| M1 | Codex adapter, <=350 | New `Launch/CodexModelDiscovery.cs` under Host.Delivery and matching delivery tests: signed-out/offline, account/version race, incomplete pages, per-model efforts |
| M2 | Claude adapter, <=300 | New `Launch/ClaudeModelDiscovery.cs` and matching delivery tests: initialize parsing, unsupported safe mode, no prompt/MCP, signed-out |
| M3a | Probe identity exclusion, <=300 | Host.Core discovery and an owned-probe registry, discovery tests, `docs/host-hooks.md`: no card, ordinary session/PID-reuse still visible |
| M3b | Copilot adapter, <=400 | New `Launch/CopilotModelDiscovery.cs` and delivery tests: inert session, deletion, graceful exit/cleanup failure. Agency scope remains unverified until its own capture |
| M4a | Shared cache/refresh, <=350 | New `Launch/ModelOptions.cs`, delivery tests: precedence, stale failure/retry event, coalescing, account/version invalidation and late completion |
| M4b | Contract and launch integration, <=400 | Protocol `Launch.cs`, `LaunchService.cs`, `LaunchConfig.cs`, host config adapter, `docs/protocol/v1-launch.md`, `docs/host-config.md` and corresponding tests: truthful axis provenance, local-only override, selection-independent options |

M1 then M2 then M3a/M3b then M4 follows Engineering's re-cut; M3a is mandatory
before M3b runs beside discovery. #1413 may expose honest fallback before adapters,
but its options integration must coordinate M4 and never call pins discovered.
Fleet [#106](https://github.com/danswett/slipgates-fleet/issues/106) follows its launch
scope/readback/grant prerequisites; real-discovery acceptance also waits for M4.
The dispatcher consumes host readback, not copied model lists or cap numbers.
#1142 PR 2/3 remains running-session reporting/switching, with its authorization,
cost confirmation and audit; it consumes allowed values without moving that work
here. Its one-minute freshness target requires a real change signal, not a timer.

## Remaining decisions and exit evidence

Engineering/Platform/SRE: supply all four captures, verify Agency profile/account
mapping, current cost and safe process reconciliation. Engineering: identify the
real account/catalogue change signals and finalize additive field/budget choices.
The refresh ruling above is settled, not an outstanding timer-policy question.
Owner: approve future credential-context execution and the explicit local override
UX before rollout; no owner command or permission answer is delegated to an agent.
Quality/Engineering: require full-document two-family exact-head reviews, the
quality run and hosted checks. Until captures and validation exist, #1453 stays
open; the note is a reviewable design slice, not completed parity evidence.
