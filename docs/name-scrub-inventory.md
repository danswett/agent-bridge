# Old-name inventory and compatibility plan

Issue [#1838](https://github.com/danswett/agent-bridge/issues/1838), Product slice 1.
Snapshot: `6f4ab661736d6f9af651c89b188724f4b8a76af6` (2026-11-10, Pacific). This document changes no names.
Targets below are proposals for Product and Engineering sign-off, not new supported
settings or permission to migrate installed machines. Every old name keeps working.
The inventory is repository-wide: tracked text and paths, including hidden files,
tests, Apple, MCP, infrastructure and historical spikes. External consumers outside
this checkout require a later audit, including `danswett/slipgates-fleet`'s roster
reader from PR #277, which must keep using the current token name in this slice.
Verified fleet consumers at `b80bef62aaa8ed0885c44516e99480f956b5ec90`: `messenger/FleetRelay.ps1` (token, profile, folder and pipe) and `meter/gh.cs`
(session marker), plus their tests. This is not a full external-repository inventory.

## Rules and order

Land user-visible copy and docs first (Creative reviews wording), then environment
aliases, then executables/folders through installer migration, and the repository
last. Do not mix renames into the fleet #277 roster change or the name-lookup chain.
Quality decides sensitivity from the actual file list. Later env/token changes
need two model-family receipts on their exact head; this is one ordinary docs file.

**Break** means a bare rename would break an existing consumer, not that this doc
breaks anything. Rules below are requirements for future slices, not implemented
behavior. An asterisk in an old-name cell preserves the suffix; it is a named
family (namespace, generated key or temporary resource), not an omission.

| Rule | Required compatibility behavior |
|---|---|
| E | Read both environment names for at least one release. New wins when present; invalid/empty new values fail as required by that setting, never fall back to old. Warn once without values when old is used. Keep accepting old for running sessions. Emit both names with the same value to children until old-host/old-child overlap is over. Preserve scrubbing, inheritance, expiry and principal binding; variable names never prove identity. |
| I | Installer/host performs the migration, with no person command. Keep old launch entry points and discovery working for at least one release; move tasks, hooks, shortcuts and folders transactionally, preserving ACLs, credentials and rollback. Never start a second host or silently create a fresh profile. Test upgrade, interrupted upgrade, mixed versions and rollback before removal. |
| S | Persistent names are compatibility contracts. Dual-read old/new state with new winning, migrate atomically with rollback and preserved access controls, and keep old readers viable for the overlap release. Cryptographic labels are NOT cosmetic: retain their bytes until a separately versioned protocol/key migration is approved. Never regenerate identity to change spelling. |
| D | Provision new service/DNS/resource names before moving clients. Keep old endpoints and release feeds usable through the supported mixed-version/rollback window; no deploy, DNS, credential or infrastructure change is authorized here. Do not rename a live resource in place or remove a hostname used by installed clients. |
| T | Display copy/docs can say Slipgates without changing identifiers, flags, links or quoted protocol values. Keep historical receipts, copyright notices and external links truthful. Generated copy must change at its source, not only generated output. |
| B | Internal build/test names have no product alias requirement; change references and callers together in a later slice. Shipped assembly, bundle, package or artifact names instead require I/S. Keep fixtures that deliberately test old-name compatibility. |
| H | HA-retirement references stay intact until their retirement gate. Do not rename the legacy daemon, tasks, entity ids, hook files or coexistence probes as a rebrand. |
| R | Repository rename is last. Preserve GitHub redirects, old clone/release URLs, update discovery and integrations; audit workflows, scripts, docs and external callers before changing them. |

This adds no running component: the inventory lives in Git, can be checked on any checkout, and does not depend on a particular machine remaining online.

## Environment spellings and lookalikes

All actual `AGENT_BRIDGE_*` names found by the check are listed individually.
Consumers are source paths relative to the repository; tests/docs also count. The three HA test settings below are not excluded `AGENT_HA_*` names.

| Old name | Proposed new name | Consumers / meaning | Break | Rule |
|---|---|---|---|---|
| `AGENT_BRIDGE_TOKEN` | `SLIPGATES_TOKEN` | `src/AgentBridge.Host.Delivery/Launch/Launchers.cs`, `mcp/src/server.mjs`; fleet `messenger/FleetRelay.ps1` and tests/roster reader (#277); session credential, not an account/HA token | Yes | E; never log either value |
| `AGENT_BRIDGE_SESSION` | `SLIPGATES_SESSION` | `Launchers.cs`, `src/AgentBridge.Local/AgentAncestry.cs`, host guards, `build/measure-update-gap.ps1`, adoption/launch/guard tests; fleet `meter/gh.cs` and Test-Meter.ps1 | Yes | E; preserve refusal guards, not authentication |
| `AGENT_BRIDGE_PIPE` | `SLIPGATES_PIPE` | `mcp/src/pipe.mjs`, `mcp/test/mcp.test.mjs`, local-pipe doc; explicit endpoint override | Yes | E; preserve endpoint precedence |
| `AGENT_BRIDGE_ROOT` | `SLIPGATES_ROOT` | `mcp/src/pipe.mjs`, local-pipe doc; profile root used to derive pipe | Yes | E + I; do not hash a different folder prematurely |
| `AGENT_BRIDGE_PROFILE` | `SLIPGATES_PROFILE` | `mcp/src/pipe.mjs`, local-pipe doc; fleet `messenger/FleetRelay.ps1` pipe derivation | Yes | E; same profile and endpoint |
| `AGENT_BRIDGE_RELAY` | `SLIPGATES_RELAY` | `src/AgentBridge.Host/Program.cs`, `build/shadow-week.ps1`, shadow-week tests/docs | Yes | E; saved credential's relay binding still wins where specified |
| `AGENT_BRIDGE_HOSTW` | `SLIPGATES_HOSTW` | `src/AgentBridge.Local/HostLauncherContract.cs`, host and launcher; also a compile symbol in HostLauncher.csproj, Product.cs, DesignBrand.cs, ProfileFolders.cs | Yes | E for runtime; B for compile symbol; clear both before child launch |
| `AGENT_BRIDGE_HOSTW_CHECK_STOP` | `SLIPGATES_HOSTW_CHECK_STOP` | `HostLauncherContract.cs`, launcher/host stop-marker handshake | Yes | E; clear both; old Stop remains effective |
| `AGENT_BRIDGE_NUGET_SOURCE` | `SLIPGATES_NUGET_SOURCE` | `quality/run.ps1`, `quality/nuget-mirror.ps1`, mirror tests, AGENTS/quality docs | Yes | E; preserve restore and audit; no feed/token logging |
| `AGENT_BRIDGE_RECEIPTS_KEY_PATH` | `SLIPGATES_RECEIPTS_KEY_PATH` | `quality/post-receipt.ps1`; merge-rule App key-file lookup | Yes | E; key identity/permissions unchanged |
| `AGENT_BRIDGE_HOST_DIR` | `SLIPGATES_HOST_DIR` | `tools/lab/forms-soak/Invoke-FormsSoak.ps1` and README; lab binaries | Yes | E; explicit parameter precedence unchanged |
| `AGENT_BRIDGE_HOOK` | `SLIPGATES_HOOK` | `.github/workflows/ci.yml`, HookProcessTests.cs, `tests/AgentBridge.PerfHook/Program.cs`; published hook under test | Yes | E; retain exact shipped-binary test |
| `AGENT_BRIDGE_INJECTOR` | `SLIPGATES_INJECTOR` | `.github/workflows/ci.yml`, InjectorIntegrationTests.cs; published injector under test | Yes | E; retain shipped-binary selection |
| `AGENT_BRIDGE_REHEARSAL_EVIDENCE` | `SLIPGATES_REHEARSAL_EVIDENCE` | `tools/AgentBridge.Rehearsal/Program.cs`, parity-checklist.md; evidence root | Yes | E; do not move/delete retained evidence |
| `AGENT_BRIDGE_HA_TEST_URL` | `SLIPGATES_HA_TEST_URL` | `tests/AgentBridge.HomeAssistant.Tests/LiveHomeAssistantTests.cs`, ha-connector.md | Yes | E; loopback/disposable checks stay mandatory |
| `AGENT_BRIDGE_HA_TEST_TOKEN` | `SLIPGATES_HA_TEST_TOKEN` | `LiveHomeAssistantTests.cs`; disposable test credential | Yes | E; never read the live owner's token |
| `AGENT_BRIDGE_HA_TEST_DISPOSABLE` | `SLIPGATES_HA_TEST_DISPOSABLE` | `LiveHomeAssistantTests.cs`, ha-connector.md; explicit disposable opt-in | Yes | E; never infer opt-in from an alias default |
| `AGENT_BRIDGE_PROBE_TEST` | `SLIPGATES_PROBE_TEST` | `tests/AgentBridge.Host.Delivery.Tests/LaunchPieceTests.cs`; synthetic env-scrub probe | No | B; keep testing removal of inherited variables |
| `AGENT_BRIDGE_TEST_INHERITED` | `SLIPGATES_TEST_INHERITED` | `LaunchPieceTests.cs`; synthetic inheritance probe | No | B |
| `AGENT_BRIDGE_TEST_REMOVED_*` | `SLIPGATES_TEST_REMOVED_*` | `LaunchPieceTests.cs`; randomized synthetic removal probe | No | B; suffix preserved |
| `AGENT_BRIDGE_VERIFY_*` | `SLIPGATES_VERIFY_*` | `src/AgentBridge.Host/AgentVerificationService.cs`, AgentVerificationTests.cs; challenge text, NOT an environment setting | Yes | S; accept an outstanding old challenge until expiry, preserve verification |
| `AGENT_BRIDGE_` | `SLIPGATES_` | `docs/design/agent-principals.md` environment scrub-prefix requirement, brand docs; NOT an additional setting | Yes | E; audit prefix-based removal as well as exact-name reads |

## Host commands, executables and installed state

| Old name | Proposed new name | Consumers / meaning | Break | Rule |
|---|---|---|---|---|
| `agent-bridge-host`, `agent-bridge-host.exe`, `agent-bridge-host.dll` | `slipgates-host` + same extension | Host.csproj, HostManager/HostProcess, CLI docs, installers, scripts, hooks, launcher | Yes | I |
| `agent-bridge-hostw`, `agent-bridge-hostw.exe` | `slipgates-hostw` + extension | HostLauncher.csproj, App.Core HostManager, startup task, update/rollback scripts | Yes | I; preserve job/stop handshake |
| `AgentBridge.exe`, `AgentBridge.deps.json`, `AgentBridge.pri` | `Slipgates` + extension | App.csproj, app publish/package/self-test, update discovery | Yes | I |
| `agent-bridge-host-hook`, `agent-bridge-host-hook.exe` | `slipgates-host-hook` + extension | Hook.csproj, StandaloneHooks, hook config, CI shipped-hook test | Yes | I; already-installed hooks keep resolving |
| `agent-bridge-hook`, `agent-bridge-hook.exe` | `slipgates-hook` + extension | historical Hooks tool, hook docs/tests, managed launch config | Yes | I; distinguish old and current hook binaries |
| `agent-bridge-injector`, `agent-bridge-injector.exe` | `slipgates-injector` + extension | Injector.csproj, WorkerInjector, forms-soak, CI | Yes | I |
| `agent-bridge-rehearsal`, `agent-bridge-rehearsal.exe` | `slipgates-rehearsal` + extension | Rehearsal.csproj, lab/quality/build scripts, parity checklist | Yes | I; evidence still readable |
| `agent-bridge-quality`, `agent-bridge-quality.dll` | `slipgates-quality` + extension | Quality.csproj, quality/run.ps1, CI and merge-rule tool callers | Yes | I; old gate invocations still work |
| `agent-bridge-console-echo`, `agent-bridge-console-echo.exe` | `slipgates-console-echo` + extension | ConsoleEcho.csproj, delivery/console tests | No | B; update tests with artifact |
| `AgentBridge` | `Slipgates` | `%LOCALAPPDATA%\AgentBridge`, ProfileFolders.cs, MCP pipe derivation; also namespace root | Yes | I for folder; B for namespace |
| `.agent-bridge` | `.slipgates` | ProfileFolders.cs, Identity.cs, Unix/macOS profile and socket roots | Yes | I; same account/profile/key material |
| `AgentBridgeApp`, `AgentBridgeApp.*`, `AgentBridgeApp_ExecutionStub.exe` | `SlipgatesApp` + suffix | Platform.cs pack id, Program.cs single-instance mutex, Velopack install root/stubs | Yes | I + S; preserve package update identity until migration exists |
| `velopack.AgentBridgeApp`, `velopack_AgentBridgeApp.log` | `velopack.SlipgatesApp`, `velopack_SlipgatesApp.log` | Toasts.cs AUMID, activation, packaging and installer logs | Yes | I + S; old toast activation/shortcuts work |
| `AgentBridgeHost`, `AgentBridgeHost_*` | `SlipgatesHost` + suffix | App.Core/HostManager.cs scheduled task name (profile + SID hash), host mutexes | Yes | I; replace owned task only; preserve stopped state |
| `AgentBridgeHostWorktree_*` | `SlipgatesHostWorktree_*` | Host.Delivery/Launch/Worktrees.cs cleanup mutex | Yes | S; mixed hosts must share one lock |
| `AgentBridge.Host.*` | `Slipgates.Host.*` only after endpoint migration | `src/AgentBridge.Local/LocalEndpoint.cs`, MCP pipe.mjs, fleet FleetRelay.ps1; Windows named pipe derived from SID and profile folder hash; also host namespaces | Yes | I + S for pipe; B for namespaces; retain old pipe and folder-hash discovery |
| `agent-bridge-*.sock` | `slipgates-*.sock` | LocalEndpoint.cs fallback Unix socket basename (profile-folder hash); source has the literal `agent-bridge-` prefix | Yes | I + S; retain old socket discovery during folder/name migration |
| `.agent-bridge-host-cleanup-*`, `agent-bridge-host-cleanup-*` | `.slipgates-host-cleanup-*`, `slipgates-host-cleanup-*` | Worktrees.cs staged cleanup folders and tests | Yes | I; recognize old staged ownership, never sweep roots |
| `agent-bridge-owner`, `agent-bridge-owner.json`, `agent-bridge-host-created`, `agent-bridge-created`, `agent-bridge-managed`, `agent-bridge-withdrawn`, `agent-bridge-updated` | same suffixes with `slipgates` | Worktrees/Launch ownership markers, managed-hook/instruction markers | Yes | S; retain provenance and old marker recognition |
| `.agent-bridge-*`, `agent-bridge-import-*` | `.slipgates-*`, `slipgates-import-*` | Migration/Migrator.cs temp/backup suffixes, import scratch | Yes | I; preserve pending rollback records |
| `agent-bridge-host-intake.lock` | `slipgates-host-intake.lock` | hook spool intake synchronization | Yes | S; one shared lock during overlap |
| `agent-bridge-spool`, `agent-bridge-hook.log`, `agent-bridge-hook.log.lock` | `slipgates` + same suffix | Hooks spool/log paths, intake and hook tooling | Yes | I + S; do not lose queued events |
| `agent-bridge.json`, `agent-bridge.instructions.md` | `slipgates.json`, `slipgates.instructions.md` | StandaloneHooks.cs, managed Copilot hooks/instructions and cleanup | Yes | I; migrate only owned files; never overwrite user configuration |
| `.agent-bridge-receipts`, `agent-bridge-receipts` | `.slipgates-receipts`, `slipgates-receipts` | quality/post-receipt.ps1 key lookup and receipt tooling | Yes | I + S; no key rotation implied |
| `agent-bridge.open` | `slipgates.open` | `pwa/sw.js` postMessage, `pwa/js/main.js` receiver, notification/e2e/unit tests | Yes | S; old service workers and open clients must interoperate |
| `agentbridge`, `agentbridge.*`, `.agentbridge`, `_agentbridge._tcp` | `slipgates` + suffix | Apple URL scheme, UserDefaults/diagnostics labels, Bonjour service | Yes | I + S; retain old discovery/activation aliases |
| `dev.agentbridge`, `dev.agentbridge.*` | `dev.slipgates` + suffix (subject to signing ownership) | Apple bundle ids, entitlements, app groups and socket/test labels | Yes | I + S; never silently lose Keychain/container access |
| `agent-bridge.app.*` | `slipgates.app.*` | PWA localStorage preferences: account, inspector, installFailures, lastHost, launchHost, notify, order, panes, textSize | Yes | S; migrate existing preferences, no reset |
| `agent-bridge.worktrees` | `slipgates.worktrees` for future example paths | `docs/protocol/vectors/launch.json`, ProtocolVectors.cs; fixture checkout folder, NOT a settings key | No | B; preserve compatibility fixtures where deliberate |
| `agent-bridge.connector-read` | retain v1 format until versioned migration | `docs/protocol/connector-read-v1.json` format discriminator | Yes | S; not display copy |
| `agent-bridge-directory-v1`, `agent-bridge-link-v1` | retain v1 bytes; branded name only in a future version | transport/directory/link framing and interoperability vectors | Yes | S; never search-replace protocol labels |
| `agent-bridge-agent-sign-key-v1`, `agent-bridge-device-key-v1`, `agent-bridge-roster-signing-key-v1`, `agent-bridge-ha-token-v1` | retain v1 protection labels | agent/device/roster/HA protected-key storage | Yes | S; old ciphertext must remain decryptable |
| `agent-bridge-keys`, `agent-bridge-device-*`, `agent-bridge-app-*` | `slipgates` + suffix | key/identity/device/app state and test fixtures | Yes | S; distinguish persisted keys from disposable fixtures |

## Product copy, services, repositories and build surfaces

| Old name | Proposed new name | Consumers / meaning | Break | Rule |
|---|---|---|---|---|
| `Agent Bridge` | `Slipgates` | README/docs, CLI help/errors, Apple strings, HTML titles, manifests, design/spikes; Product.cs already supplies branded current copy | No | T; descriptive text only, not identifiers embedded in it |
| `agent-bridge`, `agent-bridge.git` | `slipgates`, `slipgates.git` (repository target needs owner confirmation) | GitHub clone/release/API/workflow links, CONTRIBUTING/AGENTS, package metadata | Yes | R; do not assume future repo URL exists |
| `agent-bridge-relay`, `agent-bridge-relay-staging`, `agent-bridge-relay.*.azurecontainerapps.io`, `agent-bridge-relay-staging.*.azurecontainerapps.io`, `agent-bridge-relay--*` | branded endpoints `relay.slipgates.com` / staging; new resource label `slipgates-relay` | infra deploy/Bicep, relay URLs in clients/docs/tests, container revisions | Yes | D; old hostnames remain reachable |
| `rg-agent-bridge-relay`, `cae-agent-bridge-relay-wus2`, `log-agent-bridge-relay-wus2`, `appi-agent-bridge-relay-wus2`, `id-agent-bridge-relay`, `id-agent-bridge-relay-staging` | replace `agent-bridge` segment with `slipgates` | infra resource groups, Container Apps environment, logs, telemetry, managed identities | Yes | D; identities/access bindings are not display text |
| `ag-agent-bridge-ops`, `agent-bridge-ops`, `agent-bridge-ops-probe`, `budget-agent-bridge-relay`, `logic-agent-bridge-github-watch` | replace `agent-bridge` segment with `slipgates` | infra/ops monitoring names and docs | Yes | D; inventory only, no paging/alerting changes |
| `rg-agent-bridge-feedback`, `id-agent-bridge-feedback-reader-staging` | replace segment with `slipgates` | feedback infrastructure and reader identity | Yes | D + S |
| `rg-agent-bridge-lab`, `rg-agent-bridge-rehearsal`, `vnet-agent-bridge-lab`, `agent-bridge-lab` | replace segment with `slipgates` | quality lab templates, rehearsal scripts/docs | Yes | D |
| `agent-bridge-pwa`, `agent-bridge-push`, `agent-bridge-release-feed`, `agent-bridge-blob-*`, `agent-bridge-upload-*` | `slipgates` + suffix | PWA/push/feed/blob build and deployment/config names | Yes | D + S; retain feed and registration compatibility |
| `agent-bridge-merge-rule`, `agent-bridge-merge-rule-danswett` | `slipgates-merge-rule` + suffix | GitHub App, check issuer, ruleset integration, quality docs | Yes | S; an App identity cannot be renamed by editing a string |
| `agent-bridge-stamp` | `slipgates-stamp` | `infra/main.bicep` resource tag | Yes if automation queries tag | D; preserve old tag while consumers transition |
| `agent-bridge-windows`, `agent-bridge-windows-*`, `agent-bridge-windows-arm64`, `agent-bridge-windows-arm64-*`, `agent-bridge-windows-signed`, `agent-bridge-windows-signed-*` | `slipgates` + same artifact suffix | CI publish/sign/package, update and release tooling | Yes | I; artifact consumers need coordinated transition |
| `agent-bridge-app`, `agent-bridge-app-dev`, `agent-bridge-0.2.0-full.nupkg` | `slipgates-app`, `slipgates-app-dev`, branded package after migration | Velopack/release examples, app build, test packages | Yes | I; existing pack/feed identity retained meanwhile |
| `AgentBridge.*`, `AgentBridge.slnx`, `AgentBridge.ico`, `AgentBridge.png` | `Slipgates.*` only in a later coordinated build slice | src/tests/tools namespaces, assemblies, csproj/solution refs, stack traces, generated assets | Yes for shipped names; no for source-only | B + I; includes descendants and extension suffixes |
| `AgentBridgeApple*`, `AgentBridgeClientState*`, `AgentBridgeCore*`, `AgentBridgeMac`, `AgentBridgeMobile`, `AgentBridgeApp.swift` | `Slipgates` + same suffix | Apple project/package/target/module/test names | Yes for installed bundles; no for source-only | B + I; Apple hold is not lifted |
| `window.agentBridge*`, `...window.agentBridgeDemo*`, `agentBridgeDemo.*`, `agentBridge.timings`, `agentBridgeMigrationCheck`, `AgentBridgeCodeCopied`, `AgentBridgeIdCopied`, `AgentBridgeRowMoved`, `AgentBridgeUsageCard` | same names with `slipgates` / `Slipgates` root | PWA debug/demo/test hooks, Apple notification symbols, migration JS | No for test-only; yes for external hooks | B + S; retain explicit old client contracts |
| `the-hook-agent-bridge-host-hook` | new branded heading anchor, keeping old anchor alias | docs/protocol/v1-update.md link to docs/host-hooks.md | Yes | T; old deep links still resolve |
| `agent-bridge-session-card`, `agent-bridge-activity-card`, `agent-bridge-reply-card.js`, `agent-bridge-publication-policy.js` | `slipgates` + suffix | HA cards / publication adapter scripts and docs | Yes | H for deployed HA consumers; retire rather than silently rename |
| `agent-bridge-standalone-app-design.md`, `agent-bridge-studio` | branded doc/tool label | historical design pointers and contributor tooling | No | T; preserve valid historical links |
| `agent-bridge-roster-sign`, `agent-bridge-roster-sign-*` | `slipgates-roster-sign` + suffix | roster signing tool/tests and temporary evidence | Yes for CLI; no for temp fixtures | I + B; no key regeneration |
| `agent-bridge-shadow`, `AgentBridgeShadow`, `AgentBridgeShadowHost_*`, `AgentBridgeShadowRun_*` | `slipgates-shadow`, `SlipgatesShadow` + suffix | shadow CLI, host/run mutexes, shadow-week tooling | Yes | I + S; mixed-version locks remain shared |
| `agent-bridge-usage-live`, `agent-bridge-q-perf`, `agent-bridge-size`, `agent-bridge-soak-transcripts`, `AgentBridgeSoakDriver`, `AgentBridgeSoakTunnel`, `AgentBridgeRehearsal`, `AgentBridgeSignIn`, `AgentBridgeLiteKill`, `AgentBridgeMove`, `AgentBridgeRelease` | corresponding `slipgates` / `Slipgates` roots | lab/perf/rehearsal driver names, scratch dirs, test processes | No for disposable fixtures | B; installed tool references use I |
| `agent-bridge-verify-mac`, `agent-bridge-verify-mobile` | `slipgates-verify-mac`, `slipgates-verify-mobile` | `apple/scripts/verify.sh` disposable Xcode derived-data folders | No | B; no installer data involved |
| `AgentBridgeMono` | `SlipgatesMono` (already migrated by #557) | historical font/file references in brand design; the OFL copyright line naming Agent Bridge Mono stays verbatim | No | T; copyright notices are not rebrand copy |
| `agent-bridge-spike-s2`, `agent-bridge-spike-s4`, `agent-bridge-s2-live`, `agent-bridge-s2-vectors`, `agent-bridge-s4`, `agent-bridge-s4-relay-proof`, `agent-bridge-s6`, `agent-bridge-u6-app`, `AgentBridgeS3*`, `AgentBridgeS4`, `AgentBridgeS3Data`, `velopack.AgentBridgeS3` | retain archived spellings; brand any revived product separately | historical spikes, prototype bundles and fixture packages | No | B + T; do not imply prototypes are current installs |
| `agent-bridge-relay-early-proof`, `agent-bridge-relay-ppe`, `agent-bridge-relay-s4`, `agent-bridge-relay-deploy-*`, `cae-agent-bridge`, `agentbridgerelay59731` | retain historical evidence; use `slipgates` in new disposable names | relay proofs, deployment scripts, staging fixtures | Yes if live; no for recorded fixtures | D + B |
| `agent-bridge-`, `agent-bridge-7`, `agent-bridge-a-worktree-with-a-long-name`, `agent-bridge-other`, `agent-bridge-test-*`, `agent-bridge-tests-*`, `agent-bridge-demo-*`, `agent-bridge-own-peers-*`, `agent-bridge-invalid-stream-*`, `agentbridgetes`, `agentbridgetestoperations`, `AGENT-BRIDGE-PLAINTEXT-MARKER` | `slipgates` roots in future fixtures | tests/spikes temporary files, fake ids and negative-test plaintext | No | B; no production alias needed for disposable data |

## HA-retirement references, not rebrand migrations

The grep removes `AGENT_HA_*` and `agent-ha-bridge` names before matching, as the
issue requires. Related old spellings below still match and are classified, not
silently skipped. Their retirement must preserve coexistence and rollback.

| Old name | Proposed new name | Consumers / meaning | Break | Rule |
|---|---|---|---|---|
| `agent_bridge_*` | no rebrand; retire with HA | HomeAssistant connector/entity ids, MQTT discovery topics, cards, fixtures and docs | Yes | H; no concrete session/node ids belong in this inventory |
| `AgentBridgeDaemon`, `AgentBridgeDaemon_*`, `AgentBridgeDevBoxKeepAwake`, `AgentBridgeDevBoxKeepAwake_*`, `AgentBridgeHaConnector_*` | retain until HA retirement | AgentHaBridgeProbe.cs, migration task discovery/rollback, keep-awake handoff, connector locks | Yes | H; never rename live legacy tasks |
| `agent-bridge-daemon.ps1`, `agent-bridge-supervisor.ps1`, `agent-bridge-devbox-keepawake.ps1`, `agent-bridge-launch.vbs`, `agent-bridge-daemon-state.json`, `agent-bridge-daemon.heartbeat` | retain until HA retirement | migration discovery, legacy process/launch probes and contract fixtures | Yes | H |
| `agent-bridge-home-assistant`, `agent-bridge-claude`, `agent-bridge-codex`, `agent-bridge-env`, `agent-bridge-py`, `agent-bridge-reply-session`, `known_hosts_agentbridge` | retain existing compatibility names; replace only when consumer retires/migrates | HA tests, hook launch/settings, legacy tooling and rehearsal SSH state | Yes | H for HA; I for surviving host hooks |
| `agent-bridge-public`, `agent-bridge-host-launch` | `slipgates` + suffix for surviving tooling | HA/publication/host-launch fixtures and compatibility docs | Yes if externally consumed | H + I |

## Reproducible coverage check

Run this PowerShell block from the repository root. It is read-only and needs Git,
not a build, token or live host. It checks every distinct spelling from text AND
tracked filenames against the first column of the tables above (case-sensitive;
only explicit `*` families match suffixes). It emits every missing spelling and
fails nonzero. It does not certify external fleet/deployment consumers.

The same check was written and run before this document: exit 1, 836 missing
spellings, zero rows. After adding the inventory it must pass. To test the negative
case again without altering Git, remove one env row from an in-memory copy and
run the same comparison; deleting `AGENT_BRIDGE_TOKEN` must fail.

```powershell
$ErrorActionPreference = 'Stop'
$doc = 'docs\name-scrub-inventory.md'
$rows = @(Get-Content $doc | Where-Object { $_ -match '^\| `' })
$patterns = @($rows | ForEach-Object {
    foreach ($m in [regex]::Matches(($_ -split '\|')[1], '`([^`]+)`')) {
        '^' + [regex]::Escape($m.Groups[1].Value).Replace('\*', '.*') + '$'
    }
})
$rx = '(?-i:\bAGENT_BRIDGE_[A-Z0-9_]+)|(?i:[a-z0-9_.-]*agent-bridge[a-z0-9_.-]*|agent_bridge_[a-z0-9_]*|[a-z0-9_.]*agentbridge[a-z0-9_.]*|agent bridge)'
$content = @(git grep -n -I -i -E 'agent[_ -]?bridge' -- . ':!docs/name-scrub-inventory.md')
if ($LASTEXITCODE -notin 0, 1) { throw 'git grep failed' }
$paths = @(git ls-files)
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed' }
$hits = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in @($content) + @($paths)) {
    $line = $line -creplace '\bAGENT_HA_[A-Z0-9_]*', ''
    $line = $line -ireplace '[a-z0-9_.-]*agent-ha-bridge[a-z0-9_.-]*', ''
    foreach ($m in [regex]::Matches($line, $rx)) {
        $name = $m.Value.TrimEnd('.')
        if ($name) { [void]$hits.Add($name) }
    }
}
$missing = @($hits | Where-Object {
    $name = $_
    -not @($patterns | Where-Object { $name -cmatch $_ }).Count
})
"Scanned $($content.Count) matching lines, $($paths.Count) paths, $($hits.Count) spellings."
if ($missing.Count) { $missing; throw "$($missing.Count) spellings missing from inventory" }
"PASS: all spellings covered by $($rows.Count) rows."
```

Sign-off required before slice 1 is done: Product for naming/order and Engineering for compatibility/consumer coverage, with Quality owning the exact-head gates and
merge. No later rename is authorized by this table alone.
