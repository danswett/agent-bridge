# Public status data contract

`status-page/contract.mjs` is the sole parser and validator for `status.json` in the
producer and page. They pass it UTF-8 JSON text and a clock value; it returns either
an unavailable result or validated groups with a derived banner. It performs no I/O.

## Schema and privacy

The top-level keys are exactly `schemaVersion`, `generatedAt`, `delivery`, `fleet`,
`systems`, `quota`, `programs` and `incidents`; the version is `1`. All timestamps
are UTC ISO-8601 strings. Every group has `observedAt`, except an unproduced optional
group, which is exactly `{ "enabled": false }`.

| Group | Allowed contents |
|---|---|
| `delivery` | `mergesPerHour` and `minutesSinceLastMerge` maps keyed only by `agentBridge`, `fleet`, `roadmap`; `mainCi` (`green`, `red`, `running`, and `ageMinutes`); `actionsQueue` (`queued`, `inProgress`); `pullRequests` (`ready`, `armed`, `blocked`); `flakeCount` |
| `fleet` | `liveSessions`, `builders` and `reviewers` (`working`, `idle`), `agentErrors`, `machinesOnline`, `cpuBands` and `memoryBands` (`low`, `medium`, `high`; each band total equals `machinesOnline`), `readyIssues` |
| `systems` | When enabled, exactly F2–F8 plus `countsProducer`, `peopleProducer`, and `telemetry`; each row has `state` and `heartbeatAgeMinutes`. F7 is `live` or `shadow`; other rows are `live` or `standby` |
| `quota` | `githubRemainingPercent` (1–8 percentages without identity labels), `modelConcurrency` (`active`, `cap`), `vmLoadBands` (`low`, `medium`, `high`) |
| `programs` | When enabled, `haRetirement` (`done`, `total`, `parity`: `proved` or `pending`), `fleetView` (`ready` or `degraded`), and `f7` (`live` or `shadow`) |
| `incidents` | At most 20 entries with `state` (`active` or `resolved`), `startedAt`, and only optional `resolvedAt`, `blockedMerges` and one `label` |

Incident labels are a closed set: `Merges delayed`, `Builds or tests failing`, `Fleet
capacity reduced`, `Messaging delayed`, `Updates delayed`, `Usage limit reached`,
`Status data delayed`, `Other`. No incident title, name, node or machine id, path,
content, or free-text field is accepted. A resolved incident requires a resolution
time no earlier than its start. People choose labels; the producer does not author
them. Changing this set requires a Product decision recorded on #2136.

The validator rejects malformed JSON, unknown or missing keys, wrong types, unsafe
integers, values outside 0–1,000,000, invalid enums, invalid timestamps, over 20
incidents, more than eight quota percentages, and documents over 64 KiB. Percentages
are 0–100. A timestamp with a future value is unavailable.

## Freshness and banner

The document and each produced group's `observedAt` must be no more than ten minutes
old. Exactly ten minutes is fresh; strictly older or future data is unavailable.
An unavailable whole document returns no data. An unavailable group exposes no
stale values. An enabled F-system row whose heartbeat is strictly over ten minutes
old (including elapsed time since Systems was observed) is unavailable; exactly ten
minutes remains fresh. Disabled Systems and Programs
are shown as Unavailable and are not counted until their producer is enabled.
Without script, the page fallback says Stale and never Operational.

The banner is never read from JSON. The validator derives it in this order:

1. **Outage** if an active incident blocks merges, or live sessions exist while no
   machines are online.
2. **Degraded** if a required group is unavailable; an enabled optional group or
   system row is unavailable, or Programs' fleet view is degraded; main CI is red or running over 60 minutes; Actions has
   more than 5 queued jobs; any PR is blocked; any flake or agent error exists; ready
   issues outnumber working plus idle builders; any CPU, memory, or VM load is high;
   any GitHub remaining percentage is below 10; model concurrency is at or above its cap; or
   an incident is active.
3. **Operational** otherwise.

These are S1 thresholds and part of the contract, not producer-authored status.
Scope and privacy: [#2136](https://github.com/danswett/agent-bridge/issues/2136) and [#2356](https://github.com/danswett/agent-bridge/issues/2356).
