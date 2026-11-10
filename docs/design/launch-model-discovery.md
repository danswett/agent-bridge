# Account-scoped launch model discovery (#1453)

**Status: proposed; not implementation or completed acceptance evidence.**
The [issue](https://github.com/danswett/agent-bridge/issues/1453) requires real
answers from four agents and a passing quality run. Both remain outstanding.
This documentation-only slice authorizes no probes, credential access, hidden
sessions, host changes or quota use. Engineering or the person must supply the
missing evidence under separately approved access.

## What is true now

[host-launch.md](../host-launch.md) describes the shipped host. It combines
configured or built-in tuning with models observed in live sessions; that is not
account discovery. `Launchers.cs` maps Agency to Copilot tuning, not proof that
every Agency profile uses the same executable, account or model catalogue.
`launch.options` must not label either pins or session observations `discovered`.

The protocol details and historical measurements below come from
[Engineering's source-reading report](https://github.com/danswett/agent-bridge/issues/1453#issuecomment-6734312045),
not fresh captures or an entitlement audit. Its source is the retired daemon's
`hooks/session-launch.ps1`. Installed CLI versions must be verified before an
adapter is enabled; an unsupported protocol fails explicitly to fallback.

## Asking each agent

| Agent | Account-list route reported by Engineering | Scope and fallback |
|---|---|---|
| Copilot | Headless stdio JSON-RPC: `connect`, inert `session.create`, `session.model.list`, `session.delete` | Per account, through the same sign-in and executable as launch. Requires a real hidden throwaway session. Until approved and verified, last good matching answer, then config, then built-in |
| Claude | Stream-JSON SDK `control_request` / `initialize`, with safe mode, no session persistence and strict empty MCP configuration | Account-scoped models and `supportedEffortLevels`. An older CLI without safe mode is unsupported, not retried with weaker isolation; same fallback |
| Codex | App-server JSON-RPC `model/list`, including `supportedReasoningEfforts` | Answer alone may be an offline built-in catalogue. Accept as discovered only with account-bound cache provenance as below; otherwise same fallback |
| Agency | No independent discovery call verified; candidate is its actual Copilot child | **Same as Copilot, unverified.** Do not reuse a direct Copilot answer unless executable, account and profile scope match; otherwise config, then built-in |

Reported process invocations, for adapter design only, **not an instruction to
run them on the fleet**:

```text
copilot --headless --no-auto-update --stdio
```

```text
claude -p --input-format stream-json --output-format stream-json --verbose --safe-mode --no-session-persistence --strict-mcp-config
```

```text
codex app-server
```

These are not complete probe scripts. Claude still needs an explicitly empty MCP
configuration; every adapter needs the isolation, handshake and cleanup below.
Copilot's session-less `models.list` omitted picker models in Engineering's
report, and `copilot help config` advertised a generic catalogue. Neither proves
account entitlement. Claude must not use `--bare`: Engineering reports that it
ignores subscription sign-in. Do not silently downgrade any safety flag.

For Codex, preserve the old provenance check: `models_cache.json` was fetched for
this sign-in within 300 seconds and contains every answered model. Age is only a
provenance bound, never a wait or evidence of account binding by itself. A cache
without a verifiable account association, an offline built-in response, malformed
data or incomplete pagination is not a successful discovery.

Efforts belong to individual models. Keep per-model effort sets; the launcher
must reject an unsupported pair rather than promote effort to another level.
Where a response supplies no effort metadata, use configured/built-in efforts
and label that provenance separately, never invent a discovered set.

No separate context-list protocol is verified. Keep contexts configured, else
built-in, and label them accordingly. Claude variants such as `sonnet[1m]` are
model identifiers, not permission to manufacture a context setting. Preserve
`auto`/empty-default semantics; neither is evidence of a paid entitlement.

## Capture ledger: acceptance remains open

| Agent | Real response from a machine | Needed before acceptance |
|---|---|---|
| Copilot | **Missing; no capture supplied** | Sanitized `session.model.list` response and successful cleanup evidence |
| Claude | **Missing; no capture supplied** | Sanitized initialization response with models and effort metadata |
| Codex | **Missing; no capture supplied** | Sanitized `model/list` response, pagination completion and cache/account provenance result |
| Agency | **Missing; no capture supplied** | Actual profile/child response or evidence that no supported ask exists, with explicit fallback |

Each receipt must name a non-sensitive machine alias, CLI version, capture time
in Pacific, exact operation, response fields, elapsed time, signed-in/offline
state and whether it is account-scoped or generic. Redact tokens, login names,
private paths and session contents. Keep enough model/effort data to verify the
parser. Do not insert illustrative JSON into this ledger as a real answer.
An unavailable capture is not proof that the CLI cannot be asked.

## Refresh, retention and launch behavior

The older Engineering report proposed six-hour caching and timed exponential
retry. The subsequent [owner rule #1511](https://github.com/danswett/agent-bridge/issues/1511)
forbids timer-driven waits/decisions. Proposed replacement: event-driven refresh,
subject to Engineering/owner agreement before implementation.

* **Start:** an approved adapter asks in the background when the host starts.
  Until it answers, options return immediately from fallback; no launch waits
  for the probe. No adapter is implicitly approved by merging this design.
* **Changes:** executable/version, sign-in identity, Agency profile or owner
  configuration changes invalidate the affected scope and request one refresh.
  An on-demand refresh also requests one; simultaneous requests coalesce.
* **Unknown model:** validation refuses it with a readable source/reason and
  schedules one refresh. Never start, silently substitute, or automatically
  replay the refused launch; the caller reads new options and submits a new id.
* **Failure:** report unsupported, signed-out, offline, malformed-response or
  cleanup faults. No timed retry loop. Retry on a new relevant event or explicit
  refresh, coalescing while one probe is active. Repeated reads of unchanged
  options do not start probes.
* **Retention:** keep the last good answer in host memory for that host run and
  unchanged account/executable/profile scope. A failed refresh marks it stale
  but keeps it usable. Restart has no last good answer until a new ask succeeds.
  Identity/sign-out changes discard it immediately; never expose or launch from
  another account's answer. There is no six-hour expiry or periodic timer.

With no explicit override, use last good matching discovery (even stale), then
the configured list, then the built-in list. A pin overrides discovery **only
when the owner explicitly sets that override at the machine**; neither an agent
request nor a remote options refresh can set it. Existing configured lists stay
fallback pins on migration, not silently elevated to explicit overrides.
The override must be separate from the fallback list in the future config.

Pins are policy, not entitlement: they can restrict or be wrong, and vendor
refusals remain visible. A stale allowed model may still fail at launch; the
failure invalidates the relevant result and requests refresh, never a substitute
model or a quota-spending trial. Live-session observations remain diagnostics;
they must not widen a discovered list or an explicit owner override.

### Options readback (proposed additive contract)

Keep all agents visible independently of the card's selected agent. Each model
list exposes source `discovered`, `pinned` or `built-in`, plus stale/pending state,
last successful observation time and a bounded failure reason. Distinguish an
explicit owner override from a fallback pin. Efforts and contexts each expose
their own source; a discovered model list does not make contexts discovered.
Preserve model-specific effort metadata and existing array/default compatibility.
No paths, credentials, login names or raw CLI output cross this readback.

Contract work must define exact fields in `docs/protocol/v1-launch.md` and the
agent-safe local-pipe view introduced by #1413 before implementation. This note
does not claim those fields exist today. Consumers display provenance and age,
and never interpret `pinned`/`built-in` as authoritative entitlement. The host
revalidates each launch against its current effective snapshot.

## Cost, isolation and machine lifetime

Engineering reports no prompt and no inference/premium request for these asks
(Copilot checked historically on 1.0.95), but this is **not current billing
verification or owner consent**. Reported elapsed times: Copilot 3-4 seconds,
up to 12 on cold start; Claude/Codex about 1 second; Agency unmeasured.
The old 20-second timeout is historical, not a readiness decision to copy.
A watchdog may report a stuck-probe fault; it must never declare success or
entitlement from elapsed time.

Every ask starts a CLI under the owner's sign-in and can contact the vendor.
Use only the already-approved launch identity; never read alternate credentials,
sign in, alter trust or fall back to another account. In particular, Copilot's
probe identity must match the launch account checks documented in host-launch.
Signed out: no login flow; discard that account's discovery and show fallback
with a signed-out reason. Fallback does not bypass launch authentication.

Use an empty scratch workspace, inert hooks/skills/config discovery, no user
prompt, no MCP servers, no telemetry/session persistence where supported, and
no user-workspace execution. Validate bounded output and protocol replies.
Copilot's necessary hidden session is an exception requiring explicit approval,
not a claim that discovery creates no session.

Register probe process identity and start time before session discovery can see
its `inuse.<pid>.lock`; exclude only that exact owned probe, not all headless
processes or a guessed PID. Delete its throwaway session, close the protocol and
observe process exit; report incomplete cleanup and suppress further probes
until reconciled. Never kill a process during credential rotation: the existing
`ProbeLauncher` deliberately avoids this because it can sign the machine out.
Cancellation/cleanup behavior needs Engineering review before any adapter runs.

**Where it runs:** the same discovery adapter on every participating host, with
no designated fleet VM, collector or scheduler. **When a node leaves:** its
offers become unavailable; other hosts keep discovering their own offers.
No machine's model answer is substituted for another's. **State:** transient
per-host memory keyed to the local executable/account/profile generation; on
restart it rebuilds. There is no durable machine-pinned coordination state.
The relay's existing protected transport carries readbacks, not credentials.

## Why machines disagree (#1142)

#1142 records Agency offering Opus on VM1/home but narrower lists on VM2-VM4.
The current configured/built-in/observed merge explains why a list can differ
without any entitlement difference: pins and which sessions happened to run
differ. Different accounts, profiles, executable versions or entitlements are
also possible, **not proven causes for those machines**. The report has no
per-machine pin-versus-account capture establishing which caused each omission.

Engineering must pair sanitized configured-list/override provenance with actual
account-scoped responses for the compared machines. Only then can readback say
"restricted by owner pin" or "not returned for this account". Until then say
"fallback list; entitlement unverified", never "Opus unavailable".

## Implementation slices and ordering

All budgets include tests/docs, are estimates capped at 400 changed lines per
PR, and must be split again if larger. New file names below are proposed.
Every row is **sensitive: yes**, requiring Quality/Engineering two-family,
exact-head receipts under AGENTS.md; no row grants probe or merge permission.

| Order | Slice and estimated budget | Files / focused evidence |
|---|---|---|
| 1 | Provenance contract and snapshot selection, <=350 | `src/AgentBridge.Protocol/Launch.cs`, new `Launch/ModelOptions.cs` under Host.Delivery, `docs/protocol/v1-launch.md`, protocol/delivery tests: owner override, fallback, stale and account invalidation |
| 2 | Codex adapter, <=350 | New `Launch/CodexModelDiscovery.cs`, `ProbeLauncher.cs` only if required, `tests/AgentBridge.Host.Delivery.Tests/CodexModelDiscoveryTests.cs`: offline cache, account mismatch, pagination, failure |
| 3 | Claude adapter, <=350 | New `Launch/ClaudeModelDiscovery.cs` and matching delivery tests: unsupported safe mode, signed out, per-model efforts, no prompt |
| 4 | Probe identity exclusion, <=300 | `src/AgentBridge.Host.Core/Discovery/Discovery.cs`, new probe identity registry, discovery tests, `docs/host-hooks.md`: no probe card, no exclusion of ordinary sessions/PID reuse |
| 5 | Copilot and Agency scope adapter, <=400 | New `Launch/CopilotModelDiscovery.cs`, `Launchers.cs`, matching delivery tests: inert session, deletion, cleanup failure, distinct Agency scope |
| 6 | Event integration, owner override and publication, <=400 | `LaunchService.cs`, `LaunchConfig.cs`, `src/AgentBridge.Host/HostConfigFile.cs`, `docs/host-config.md`, host/delivery tests: coalescing, options independent of selection, local-only override |

Order: approve this design/evidence first; coordinate slice 1 with launch 2c
(#1413) so it never reports pins as discovery. #1413 may expose honest fallback
provenance before adapters, but must not claim live discovery then. Codex/Claude
precede Copilot; slice 4 is mandatory before slice 5 can run. Wire the completed
adapters in slice 6. Fleet [#106](https://github.com/danswett/slipgates-fleet/issues/106)
consumes #1413's options/grant after its own dependencies; real-discovery
acceptance waits for these adapters and captures. Do not copy lists/caps into
the dispatcher. #1142 PR 2/3 remains running-session switching, not discovery;
it consumes this metadata where useful without moving its consent/audit work
here. Its one-minute update target needs a real change signal, not a timer
silently reintroduced by this note.

## Open decisions and exit gates

* **Engineering/person:** provide all four capture receipts above and establish
  Agency profile/account mapping; verify current quota cost and cleanup behavior.
* **Engineering/owner:** accept the event-driven retention/retry policy replacing
  the old timed proposal; identify real account/catalogue change signals capable
  of meeting #1142's freshness requirement. No timer exception is assumed.
* **Owner:** approve any future credential-context process/hidden-session use and
  its cost before rollout; decide explicit override UX at the machine. This
  supervised documentation assignment is not that approval.
* **Engineering/Quality:** run `pwsh quality/run.ps1` on the exact documentation
  head on a permitted machine, supply two independent model-family receipts,
  and clear findings. No builds/tests run on frozen VM1 for this slice.

Until captures, policy decisions and validation are supplied, #1453 stays open;
neither this note nor a review request is evidence that its gates passed.
