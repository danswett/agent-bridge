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
