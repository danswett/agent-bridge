# Host idle-wakeup audit

Issue: [#2295](https://github.com/danswett/agent-bridge/issues/2295)

Baseline: `origin/main` at `8053c86951b434014fbe8ec9d78ae6c8af82f9bb`

Scope: host runtime and referenced host projects, with Home Assistant work identified separately for the off-HA lane. This is a source audit, not a benchmark; no runtime wake counts or CPU measurements were collected.

## Findings and proposed follow-ups

Ranked by worst-case nominal idle wake frequency when the relevant feature is enabled, then by the work each wake can cause. Counts below are interval-derived estimates, not measurements.

| Rank | Site and issue | Nominal idle wakes | Proposed failing test | Follow-up |
| --- | --- | --- | --- | --- |
| 1 | `src/AgentBridge.Host/RemoteAuthorization.cs:46` — trusted-peer enforcement starts a 250 ms periodic timer even with no attached channels. | 4/s while enforcement is enabled. | `RemoteAuthorizationTests`: with no channels, advancing fake time does not run periodic revalidation; attaching a channel still enforces the current trust state immediately. | Chief to file a new `not-ready` issue under #2293. |
| 2 | `src/AgentBridge.Host/Lite/LiteKill.cs:145` — while Lite kill mode is on, a 1 s watcher checks whether the mode turned off, even with no in-flight sends. | 1/s while the mode is on. | A `LiteKill` test: with no flights, fake time does not cause periodic mode checks; turning the mode off still cancels an active flight promptly. | Chief to file a new `not-ready` issue under #2293. |
| 3 | `src/AgentBridge.Host/Fleet/FleetPublication.cs:88,91` — connected publication loop polls every 2 s even when sharing is off or the roster has not changed. | 0.5/s per connected relay. | `FleetPublisherTests`: after the initial sharing-off publication, an unchanged source causes no repeated publication before a change or required heartbeat. | Chief to file a new `not-ready` issue under #2293. |

These are proposals, not tests added or run. The issue is documentation-only. The earlier discovery and process-load findings are not proposed again: current `SessionSource` has an explicit 15 s missed-signal recovery bound, and `ProcessLoadSampler` backs off to 60 s without demand. The existing `IdleDiscoveryTests` and process-load demand tests cover those changes. The previously referenced #2296 and #2298 are closed; #2297 is open and `ready`, so none is represented as a new follow-up.

## Recurring and deadline-driven work

| Source location | Interval / trigger | Runs when feature is disabled? | Clock and retry behavior | Idle count |
| --- | --- | --- | --- | --- |
| `src/AgentBridge.Host/RemoteAuthorization.cs:46` | 250 ms while trusted-peer enforcement is enabled. | No when enforcement is off; yes with enforcement on and zero channels. | Injected `TimeProvider` periodic timer; no retry delay. | 4 wakes/s when enabled; not measured. |
| `src/AgentBridge.Host/Fleet/FleetPublication.cs:88,91`; `src/AgentBridge.Host/Fleet/FleetPublisher.cs:60` | 2 s while relay connection is alive; publication also has a 20 s reassertion path. | The loop remains active when fleet sharing is off; it publishes the sharing-off state. | Injected time provider for scheduling; publication failures back off 2, 4, 8, 16, then 30 s. | 0.5 loop wakes/s per connection; not measured. |
| `src/AgentBridge.Host.Core/Usage/UsageMonitor.cs:95`; `src/AgentBridge.Host.Core/Usage/UsageOptions.cs:40,52` | 15 s scheduler tick when `usage.publish` is enabled; collector idle interval is 900 s. | No when usage publishing is disabled. | Injected `TimeProvider`; probe deferral and request limits use timed waits, not a hot retry loop. | 0.067 scheduler wakes/s when enabled; not measured. |
| `src/AgentBridge.Host/Lite/LiteKill.cs:145` | 1 s while Lite kill mode is on. | No when the mode is off; the watcher is scoped to the enabled mode. | Injected `TimeProvider` periodic timer; observes mode-off and cancels in-flight sends. | 1 wake/s while enabled, including no-flight periods; not measured. |
| `src/AgentBridge.Host.Delivery/Staging/AttachmentStore.cs:33,139` | 10 min sweeper while an attachment store instance is alive. | No switch; it runs even when no staged attachments need pruning. | Injected `TimeProvider` periodic timer; one pass per tick, no retry loop. | 1 wake per 10 min per live store; not measured. |
| `src/AgentBridge.Host.Core/SessionSource.cs:20,357,373` | 1 s active cadence; 15 s idle recovery bound. | No feature switch; host discovery is always present. | Elapsed-time scheduling; watcher/explicit refresh can wake sooner; missed signals recover at the bound. | One idle discovery pass per 15 s; not measured. |
| `src/AgentBridge.Host.Core/Load/ProcessLoad.cs:123,148` | 5 s with consumer demand; 60 s without demand. | Sampler remains present, but idle demand selects the slower cadence. | Time-provider-backed scheduling with demand-change wakeups; no retry loop when idle. | One sample/minute without demand; not measured. |
| `src/AgentBridge.Host/AgentReadinessService.cs:34,52-59` | Refresh pass immediately on start, then every 10 min. | Service runs while the host is up; unchanged configuration does not disable the timer. | Injected `TimeProvider` delay; scans are serialized and explicit checks can reuse a sufficiently recent result. | 1 wake per 10 min after startup; not measured. |
| `src/AgentBridge.Host/SignIn/AgentSignInService.cs:494,504,514` | 250 ms sweep while a sign-in flow or retained request has a due check; no timer while there is nothing due. | No sweep when sign-in service is absent. | One-shot timer from injected `TimeProvider`; each sweep schedules the next due check. | Zero idle wakes with no flow/request. |
| `src/AgentBridge.Host.Core/Restore/RestoreRecorder.cs:770,775,1155,1383,1487,191` | 5 s while judging active restore work; otherwise one-shot deadlines, 250 ms write coalescing, a 5 s write retry, and a 3 h seen-record interval. | The recorder is present, but timers are armed only for pending obligations or writes. | Injected `TimeProvider`; busy/held retries are bounded at 15 s/1 min and deadline waits cap at 1 day. | No judge tick without active work; pending timers are obligation-dependent. |
| `src/AgentBridge.Host.Core/Intake/HookIntake.cs:256,258,260,279` | Starts at the configured 2 s sweep; backs off exponentially to a 30 s idle ceiling; wake signals reset it. | The intake loop runs while the host's intake service is started; pausing processing does not stop its loop. | Injected `TimeProvider` cancellation timeout; immediate event wake, exponential idle backoff. | 0.5 wakes/s initially, at most 0.033/s at idle ceiling; not measured. |
| `src/AgentBridge.Host/UsagePeers.cs:46,49,72` | 30 s due-peer check; peers are eligible every 5 min, with a 1 min minimum gap. | No transfer without an attached relay/hub; the loop's timed wait remains while the service is started. | Semaphore timeout uses the system timer; presence changes signal it early. | Up to 0.033 check wakes/s; not measured. |
| `src/AgentBridge.Host/SessionService.cs:555,591` | 10 s Hello check while at least one channel has an incomplete hello. | No timer without watched channels. | Injected `TimeProvider` periodic timer; channel closure removes the watch. | Zero with no channels; up to 0.1/s while a watch remains. |
| `src/AgentBridge.Host/Program.cs:971,1081,1085` | Sign-in wake signal with a 5 s timeout while waiting for credentials. | The wait is used only when sign-in is required. | Signal-first `WaitAsync` with 5 s timeout; no rapid retry. | At most 0.2 timeout wakes/s while blocked; not measured. |
| `src/AgentBridge.HomeAssistant/HaConnector.cs:29,336,341`; `src/AgentBridge.HomeAssistant/HaConnector.Notify.cs:80,85,94,113` | 1 s connector/notification checks; notification failures back off from 2 s to 60 s. | No when Home Assistant is not configured; these are outside the off-HA runtime lane. | Injected `TimeProvider`; bounded exponential retry, reset on success. | Conditional on HA configuration and active handlers; not measured. |

Other timer-shaped matches were classified as one-shot obligations or request-scoped waits, not always-running idle pollers. In particular, bounded timeouts, file-lock waits, stream/channel reads, retry delays, and child-process exit waits do not create an independent host idle cadence. Comment-only and finite parsing/trimming/enumeration matches are not scheduled work.

## Operation-scoped and one-shot waits

| Source location | Trigger / timing | Disabled or idle behavior | Clock / retry | Idle count |
| --- | --- | --- | --- | --- |
| `src/AgentBridge.Host.Core/SessionSource.cs:1250` | One-shot coalescing timer after a watcher event. | Not armed without an event. | Injected time provider; event-driven, no retry. | Zero without events. |
| `src/AgentBridge.Host/SessionService.cs:920` | One-shot timer releases held events after the 12 s quiet interval (`src/AgentBridge.Host/SessionService.cs:198`). | Not armed without held events. | Injected time provider; refocus can release earlier. | Zero without held events. |
| `src/AgentBridge.Host/DepartureWatchdog.cs:53`; `src/AgentBridge.Host/AgentVerificationService.cs:161,241` | Departure bound or verification timeout/poll, only during the corresponding operation. | Not active while no departure or verification is in progress. | Injected time provider; operation deadline, cancellation-aware polling. | Zero outside active operations. |
| `src/AgentBridge.Host.Delivery/DeliveryGate.cs:195`; `src/AgentBridge.Host.Delivery/AttemptLedger.cs:409` | One-shot lease/attempt expiry when a delivery obligation is created. | Not armed with no outstanding obligation. | Injected time provider; expiry callback, no recurring retry. | Zero without outstanding work. |
| `src/AgentBridge.Host/Lite/AgentLiteSender.cs:74` | Per-send mode watcher at the configured `watchEvery` interval. | No watcher without an in-flight send. | System `PeriodicTimer`; canceled with the send flight, with no retry loop. | Zero outside a send. |
| `src/AgentBridge.Host/Shadow/ShadowCommand.cs:42` | Explicit shadow-command interval. | Timer exists only while that command runs. | Injected `TimeProvider` periodic timer; command-scoped. | Zero outside the command. |
| `src/AgentBridge.Host.Core/DevBox/KeepAwakeService.cs:245` | One-shot timer completes an active keep-awake request. | Not armed without an active request. | Injected `TimeProvider`; cancellation-aware deadline. | Zero without an active request. |
| `src/AgentBridge.HostLauncher/HostSupervisor.cs:23,32,75`; `src/AgentBridge.HostLauncher/HostJob.cs:114` | 3, 6, 12 s retry backoff after child startup failure; child exit is awaited. | No retry delay while child is healthy or no restart is needed. | Injected `TimeProvider` delay; backoff resets after a 10 min healthy period. | Zero while healthy; retries only after failure. |

## Search and limits

The scan covered `AgentBridge.Host`, `Host.Core`, `Host.Delivery`, `HostLauncher`, `Local`, `Transport`, `HomeAssistant`, `Hooks`, `Roster`, `AgentEnvelope`, and `FeedbackEnvelope`; `Hook` and `Injector` were also scanned to check process boundaries. The scans found 320 primary-pattern matches, 290 supplemental indirect-timer matches, and 38 broad timer/loop matches; categories overlap. Matches were reviewed as scheduled work, operation-scoped waits, bounded data work, or comments. `Hook` had no matches; `Injector` matches belong to its separate child process and are not host idle work. Data-only protocol code and unrelated app projects are outside this host inventory.

This audit makes no production changes and claims no RED/GREEN test evidence. The three proposed tests above should be made executable by the Chief's follow-up issues before changing runtime behavior. The Chief is to file those issues as `not-ready` children of #2293 after handoff.
