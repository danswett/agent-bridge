# status.json contract (#2356)

Slice S1 of the status page (#2136): the file the producer (S2, #2138) writes and the page (S3, #2139; S5) shows.
`StatusContract.Read` (`src/AgentBridge.Protocol/StatusContract.cs`) is its **only reader**: pure code, no network, GitHub or machine-specific input.

## Allow-list (fail closed)

Each object accepts exactly its listed fields. Any of these refuses the **whole** file, which then reads Unavailable and exposes no values: an unknown or duplicate field, a missing required field, a wrong type, a count outside 0..100000, a percent over 100, a list over its limit, a file over 65536 UTF-8 bytes. `schema` is 1. Times are real UTC dates in whole seconds (`2026-11-20T12:00:00Z`).

| Section | Fields (`?` optional) |
|---|---|
| root | `schema`, `generatedAt`, `sections` |
| `delivery` | `generatedAt`, `mergesPerHour` {`agentBridge`, `fleet`, `roadmap`}, `minutesSinceLastMerge`, `mainCi` (green, red, running, unknown), `mainCiMinutes`, `actionsQueued`, `actionsInProgress`, `readyPrs`, `armedPrs`, `blockedPrs`, `flakes` |
| `fleet` | `generatedAt`, `sessionsLive`, `buildersWorking`, `buildersIdle`, `reviewersWorking`, `reviewersIdle`, `agentErrors`, `machinesOnline`, `readyIssues`, `cpuBand`, `memoryBand` (low, medium, high, unknown) |
| `quota` | `generatedAt`, `restRemainingPercent` (≤8 anonymous per-identity percents), `modelsInUse`, `modelCap`, `loadBand` |
| `incidents` | `generatedAt`, `rows` (≤20): `generatedAt`, `state` (investigating, identified, resolved), `openedAt`, `resolvedAt?` (resolved only, not before `openedAt`), `label?`, `blocked?` (`merges`, needs a label) |
| `systems?` | `generatedAt`, `rows` (≤11, unique): `name` (f2-live, f2-standby, f3..f8, counts, people, telemetry), `state` (ok, degraded, down, unknown), `mode` (shadow, live, off), `generatedAt` |
| `programs?` | `generatedAt`, `rows` (≤5, unique): `name` (ha-retirement, fleet-view, f7), `done` ≤ `total`, `parityState` (not-required, pending, passed, failed, unknown), `generatedAt` |

The file holds only counts, closed codes and times: no node ids, machine names, tokens, paths, titles or free text. Incident labels follow the [Nov 14 Product ruling](https://github.com/danswett/agent-bridge/issues/2136#issuecomment-6881386561) exactly: Merges delayed; Builds or tests failing; Fleet capacity reduced; Messaging delayed; Updates delayed; Usage limit reached; Status data delayed; Other. People choose them. No label means state and times only. Changing the list needs a Product decision.

## Freshness

The file, each section and each row are fresh only if `generatedAt` is at most 600 seconds old and not in the future (exactly 600 is fresh). A stale or future file reads Unavailable. A stale section or row reads Unavailable on its own (`Available["delivery"]`, `Available["systems/f3"]`): its numbers never feed the banner, and if it is counted it makes the banner Degraded. If no script runs, S3 shows Stale, never Operational.

## Banner (pure function; Outage beats Degraded)

The banner counts delivery, fleet, quota and incidents. Systems and programs count only when trusted configuration passes `StatusProducers`; until then they show Unavailable but do not count. The 60-minute and 20/5-percent values follow the later Product ruling recorded on #2356 (they replace 120 minutes and 10 percent).

| Banner | Rule (available numbers only) |
|---|---|
| Unavailable | refused file, or file older than 600 s or in the future |
| Outage | `readyPrs` > 0 and `minutesSinceLastMerge` ≥ 60; `mainCi` red ≥ 60 min; `actionsQueued` > 150; lowest `restRemainingPercent` < 5 |
| Degraded | `mainCi` red or unknown, or running > 45 min; `actionsQueued` > 25; `agentErrors` > 3; lowest percent < 20 or none reported; `modelsInUse` ≥ `modelCap` > 0; `buildersWorking` × 2 < `readyIssues`; an open incident; a counted system not ok or program parity failed; a counted section or row unavailable |
| Operational | none of the above |
