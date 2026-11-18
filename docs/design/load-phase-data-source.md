# Sampler load and phase data sources

Discovery only for [agent-bridge 3046](https://github.com/danswett/agent-bridge/issues/3046),
under [HA retirement](https://github.com/danswett/agent-bridge/issues/1396).
No producer, transport, configuration, installed host or scheduled task changes in this cut.
Source snapshots inspected on Nov 17, 2026 (PT):
[Bridge main 8b55adc6324911aa85df0801e15be6a1e270b9ed](https://github.com/danswett/agent-bridge/commit/8b55adc6324911aa85df0801e15be6a1e270b9ed)
and [Fleet main d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3](https://github.com/danswett/slipgates-fleet/commit/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3).
Bridge links below resolve within this repository; Fleet links pin the inspected commit.
Source presence is not deployment, live consumption or acceptance of another branch.

## Shared sampler, separate feeds

[HostLoadSampling.cs:8-13](../../src/AgentBridge.Host/HostLoadSampling.cs#L8-L13)
creates one `ProcessLoadSampler`; explicit demand is `(relay && LoadEnabled) || HomeAssistant.Enabled`.
[Program.cs:499-509](../../src/AgentBridge.Host/Program.cs#L499-L509) starts it before HA;
[Program.cs:658-660](../../src/AgentBridge.Host/Program.cs#L658-L660) then updates demand using the actual HA connector.
[ProcessLoad.cs:105-112](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L105-L112)
also keeps active sampling for discovered sessions. Its active interval is 5 seconds,
idle interval 60 seconds, and window two samples, not a phase-dependent cadence
([ProcessLoad.cs:52-56](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L52-L56)).

HA reads `load.Current` through the callback and publishes every 10 seconds when `PublishLoad`
allows it ([HaConnector.cs:199-204](../../src/AgentBridge.HomeAssistant/HaConnector.cs#L199-L204),
[HaConnector.cs:1008-1027](../../src/AgentBridge.HomeAssistant/HaConnector.cs#L1008-L1027)).
The machine entity is `sensor.agent_bridge_<slug>_fleet_load`; MQTT state and attribute
topics end in `/fleet_load/state` and `/fleet_load/attr`
([HaContract.cs:209-223](../../src/AgentBridge.HomeAssistant/HaContract.cs#L209-L223)).
Separately, the sessions sensor has a `load` attribute containing per-session readings
([HaConnector.cs:998](../../src/AgentBridge.HomeAssistant/HaConnector.cs#L998),
[HaPayloads.cs:519-540](../../src/AgentBridge.HomeAssistant/HaPayloads.cs#L519-L540)).

Native relay handlers already exist on inspected main:
[Program.cs:741-745](../../src/AgentBridge.Host/Program.cs#L741-L745) wires the same sampler into
[LoadService.cs:25-40](../../src/AgentBridge.Host/LoadService.cs#L25-L40).
`load.get` maps the current report with a 15-second stale limit.
**The local pipe is NOT a replacement available on main:** load dispatch requires `SecureChannel`
([SessionService.cs:285-286](../../src/AgentBridge.Host/SessionService.cs#L285-L286));
the [protocol](../protocol/v1-machine-load.md) explicitly excludes local-pipe load requests.
`FleetLoadRecord` is the HA machine-totals shape, not evidence that relay `load.get` emits it.

## Field-to-consumer inventory

Each row distinguishes source evidence from a proposed replacement. Unknown stays unknown, not zero.

| Field | Producer file and line | HA entity / payload | Native replacement on inspected main | Fleet consumer and gap |
|---|---|---|---|---|
| `cpuPercent`, `cpuPeakPercent`, `cores` | [ProcessLoad.cs:224-253](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L224-L253) computes machine CPU and two-sample mean/peak; [FleetLoadRecord.cs:23-29](../../src/AgentBridge.Host.Core/Load/FleetLoadRecord.cs#L23-L29) maps the window. | Fleet-load attributes of these names ([HaPayloads.cs:501-514](../../src/AgentBridge.HomeAssistant/HaPayloads.cs#L501-L514)). | `cpu.percent` / `cpu.cores` exist via [MachineLoadMapper.cs:17-36](../../src/AgentBridge.Host.Core/Load/MachineLoadMapper.cs#L17-L36), but percent is the current interval, not HA's mean; no window peak. | [dispatcher/FleetLoad.psm1:79-96](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/dispatcher/FleetLoad.psm1#L79-L96) uses mean CPU for hysteresis. Do not silently substitute instantaneous CPU. |
| `runQueue`, `runQueueAvg`, `runQueueMax` | [ProcessLoad.cs:373-406](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L373-L406) reads OS queue; [ProcessLoad.cs:246-254](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L246-L254) aggregates two samples. | Fleet-load attributes ([HaPayloads.cs:511-514](../../src/AgentBridge.HomeAssistant/HaPayloads.cs#L511-L514)). | **Absent** from [Protocol/MachineLoad.cs:16-43](../../src/AgentBridge.Protocol/MachineLoad.cs#L16-L43). Relay contract/mapper extension required; local pipe also absent. | [dispatcher/FleetLoad.psm1:84-96](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/dispatcher/FleetLoad.psm1#L84-L96) gates on average per core; [tools/Setup-FleetVm.ps1:862-875](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/tools/Setup-FleetVm.ps1#L862-L875) explicitly reads the HA sensor. |
| Freshness: `observedAt`, `ageMs` | Sampler stamps `SampledAt` ([ProcessLoad.cs:171-174](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L171-L174)); [MachineLoadMapper.cs:25-34](../../src/AgentBridge.Host.Core/Load/MachineLoadMapper.cs#L25-L34) computes host-clock age/state. | Fleet-load state and `observedAt` attribute are sample time, not publication time ([HaConnector.cs:1024-1026](../../src/AgentBridge.HomeAssistant/HaConnector.cs#L1024-L1026)). HA does not carry `ageMs`. | `sampledAt`, `ageMs`, `state`, `staleAfterMs` exist. Future sample times produce unavailable state, not age zero. | [dispatcher/FleetLoad.psm1:62-70](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/dispatcher/FleetLoad.psm1#L62-L70) computes `AgeSeconds` from its own clock and `observedAt`, not `ageMs`. Adoption must preserve native stale/unavailable refusals, not restamp on receipt. |
| Memory: available / total / used, session working set | [ProcessLoad.cs:423-450](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L423-L450) reads physical total/available; [ProcessLoad.cs:28-45](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L28-L45) separates tree working sets from headroom. | Fleet-load `availableMemoryMiB` converts bytes by integer division ([FleetLoadRecord.cs:26-29](../../src/AgentBridge.Host.Core/Load/FleetLoadRecord.cs#L26-L29)); sessions `load` carries machine available bytes and tree `working_set_bytes` ([HaPayloads.cs:519-540](../../src/AgentBridge.HomeAssistant/HaPayloads.cs#L519-L540)). | `memory.totalBytes`, `availableBytes`, validated `usedPercent` and per-session `workingSetBytes` exist ([MachineLoadMapper.cs:19-23](../../src/AgentBridge.Host.Core/Load/MachineLoadMapper.cs#L19-L23), [MachineLoadMapper.cs:53-65](../../src/AgentBridge.Host.Core/Load/MachineLoadMapper.cs#L53-L65)). `committedPercent` is not measured. | [dispatcher/FleetLoad.psm1:79-85](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/dispatcher/FleetLoad.psm1#L79-L85) uses free MiB; [dispatcher/FleetPlacement.psm1:206-214](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/dispatcher/FleetPlacement.psm1#L206-L214) has a distinct optional memory-percent cap. Never use summed working sets as free RAM. |
| Session/process counts and alleged per-process `phase` | [ProcessLoad.cs:18-45](../../src/AgentBridge.Host.Core/Load/ProcessLoad.cs#L18-L45) defines availability state, tree counts and numbers, **no phase**. [HaConnector.cs:1014-1016](../../src/AgentBridge.HomeAssistant/HaConnector.cs#L1014-L1016) supplies HA's published-session count separately. | Machine `sessions` is a count; sessions `load.sessions[node]` contains `state`, `processes`, CPU and working set, no phase ([HaPayloads.cs:509-540](../../src/AgentBridge.HomeAssistant/HaPayloads.cs#L509-L540)). | Per-session numbers/state exist in [Protocol/MachineLoad.cs:44-56](../../src/AgentBridge.Protocol/MachineLoad.cs#L44-L56). Its list is capped at 200, so its length is NOT a total-session replacement. No per-process phase field or PID is published. | No per-process phase consumer identified in the inspected candidate sources. [meter/Sample-GhProcs.ps1:1-11](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/meter/Sample-GhProcs.ps1#L1-L11) measures gh call attribution, not load/phase. Workload phases are accounted for below. |

## Other fleet data is not the HA sampler feed

- [telemetry/load-sample.ps1:29-65](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/telemetry/load-sample.ps1#L29-L65)
  independently collects eight OS counter samples at 5-second intervals: `cpu_avg`, `cpu_max`,
  `mem_avail_gb_min`, `proc_queue_avg`, `logical_cpus`, and copilot/node process counts.
  Its output is a capacity snapshot, not `ProcessLoadReport`.
  [pump/fleet_pump/load_heartbeat.py:11-13](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/pump/fleet_pump/load_heartbeat.py#L11-L13)
  names the GitHub heartbeat branch/file; it does not read HA.
- [telemetry/capacity.ps1:2-25](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/telemetry/capacity.ps1#L2-L25)
  separately collects capacity and heavy process groups. The dispatcher currently reads F1
  snapshot files and their collection age, plus HA launch offers
  ([dispatcher/Invoke-DispatcherCycle.ps1:219-242](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/dispatcher/Invoke-DispatcherCycle.ps1#L219-L242)).
  `FleetLoad.psm1` accepts an injected reader; that seam alone proves neither HA wiring nor native adoption.
- A second non-HA v1 producer already maps F1 `proc_queue_avg` and free GiB into governor
  `runQueueAvg` and MiB ([governor/fleet_governor/load_record.py:7-35](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/governor/fleet_governor/load_record.py#L7-L35)).
  [governor/fleet_governor/load_publisher.py:25-66](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/governor/fleet_governor/load_publisher.py#L25-L66)
  preserves collection time and writes shared GitHub state, not relay load;
  [governor/fleet_governor/live.py:138-170](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/governor/fleet_governor/live.py#L138-L170)
  validates the queue/memory/age for launch admission. Do not confuse these records with the host's HA publisher.
- [meter/Sample-RateLimit.ps1:2-8](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/meter/Sample-RateLimit.ps1#L2-L8)
  measures GitHub quota. Neither meter candidate supplies sampler memory, run queue or phase.
- [tools/loadparity/scenarios.py:43-74](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/tools/loadparity/scenarios.py#L43-L74)
  labels planned messages `steady`, `burst`, `fault` (and duplicates), not process execution phases.
  [tools/loadparity/ledger.py:8-36](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/tools/loadparity/ledger.py#L8-L36)
  adapts those labels to steady/burst/fault/recovery.
  [tools/loadparity/compare.py:62-83](https://github.com/danswett/slipgates-fleet/blob/d0afd354d0ff3565a5cd77f3f32bf1a080e1c9b3/tools/loadparity/compare.py#L62-L83)
  groups delivery latency by workload phase. There is no HA phase entity here to migrate.
  This source-only discovery executes none of these collectors, meters or workload drivers.

## Follow-up cuts, not implementation authority

Product cuts these after discovery merges, reconciling the accepted
[MachineLoad producer 2982](https://github.com/danswett/agent-bridge/issues/2982),
[native feed 2611](https://github.com/danswett/agent-bridge/issues/2611) and
[HA control 2862](https://github.com/danswett/agent-bridge/issues/2862).
The main baseline above already contains a mapper/relay handler; do not duplicate it or infer
that an unmerged producer branch is accepted. Estimates below include tests and docs, <=400 WHOLE each.

| Cut | Bounded deliverable / estimated WHOLE | Prerequisite and focused proof |
|---|---|---|
| Native machine-totals parity | Add explicit CPU-window/queue fields and an uncapped total-session count to the native contract/mapper, preserving unknowns; about 300. Split contract and mapper if needed. | Accepted producer baseline; fake-clock/table vectors for missing counters, first sample, window bounds and >200 sessions. No process phase/name/PID extension. |
| Fleet native reader | Adopt authenticated `load.get` at the injected reader seam, with explicit units, native freshness/state and unchanged admission/hysteresis; about 300. | Totals parity plus accepted fleet launch transport ([fleet 783](https://github.com/danswett/slipgates-fleet/issues/783)). Fixtures prove stale/future/partial/unknown refusals and that truncated lists cannot become total counts. |
| Native setup evidence | Replace the HA fleet-load probe with native evidence; about 180. Frozen setup versions stay untouched. | Native totals/read route accepted; offline tests for unavailable evidence, stale readings, missing queue and memory. SRE owns installation, not this cut. |
| Connector/demand retirement | Remove HA-only sampler demand/publication reliance after native consumers are accepted; about 180. | Accepted HA control and [connector cut 3045](https://github.com/danswett/agent-bridge/issues/3045); fake consumers prove relay-only and session-only sampling survives with HA disabled. |

If fleet requires local-pipe access rather than its existing relay admission, Product must first
scope a separate authenticated read-only contract/access cut (estimated 250), not silently
relax the `SecureChannel` guard. No move of workload-phase labels is required by this evidence.
Any replacement runs on the reporting host; when it leaves, its readings become stale/unavailable.
Sampler state is already in host memory; fleet admission state must remain shared, not gain a
designated machine or a new per-host cache. No deployment, live HA probe or load test is authorized here.
