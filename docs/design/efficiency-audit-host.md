# Host idle-wakeup audit

November 12, 2026, Pacific. Source baseline:
`535ff0ceef50dbe9302e1f6b2595d720f61451a8`.
Scope: [#2295](https://github.com/danswett/agent-bridge/issues/2295), under
[#2293](https://github.com/danswett/agent-bridge/issues/2293).
This is a source inventory, not a CPU profile or a performance improvement.
Every measured idle-wakeup count is **unverified**; intervals below are configured
defaults, not observed wakeups. No live process, installation or permission was changed.

## Ranked follow-ups and failing-test list

Ranking is a hypothesis from repeated work, default reach and wake frequency, not
measured CPU cost. Re-rank with the real-clock baseline harness
[#2294](https://github.com/danswett/agent-bridge/issues/2294) when available.

| Rank | Offender and reason | Fix issue (`not-ready`, parent #2293) |
| --- | --- | --- |
| 1 | Session discovery: full process/registration discovery every second even with no sessions or file changes; watchers do not replace the timeout. | [#2296](https://github.com/danswett/agent-bridge/issues/2296) |
| 2 | Phone sign-in: 250 ms sweep while enabled but with no flows; snapshots flows and trims request state four times/second. Off is correctly gated in production. | [#2297](https://github.com/danswett/agent-bridge/issues/2297) |
| 3 | Process-load sampling: unconditional whole-process-table read every 5 s, even with no sessions or relay-load subscribers; required HA/log consumers must be distinguished from relay sharing before reducing work. | [#2298](https://github.com/danswett/agent-bridge/issues/2298) |

These are **proposed failing tests**, not added/executed tests or claimed red proofs.
The eventual runtime builders write them first and obtain their own hosted-red proof.
Each fix stays at most 400 changed lines and needs exact-head two-family review.

| Project | Test name | Assertion that should fail before the future fix |
| --- | --- | --- |
| AgentBridge.Host.Core.Tests | `Empty_source_does_not_repeat_one_second_discovery_between_recovery_deadlines` | After the startup pass, an empty unchanged source makes no inspector calls before an approved missed-event recovery deadline. |
| AgentBridge.Host.Core.Tests | `Watcher_signal_wakes_idle_source_and_missed_signal_recovers_at_bound` | A data signal wakes immediately; a deliberately dropped event is still reconciled within the agreed bound. |
| AgentBridge.Host.Core.Tests | `Advancing_wall_clock_does_not_spin_idle_discovery` | Independently advancing UTC creates no immediate retry storm; cancellation ends the outstanding wait. |
| AgentBridge.Host.Tests | `Enabled_sign_in_with_no_flows_arms_no_repeating_timer` | An enabled service with no flows or retained requests owns no repeating timer. |
| AgentBridge.Host.Tests | `Last_flow_and_request_expiry_disarm_the_sign_in_scheduler` | The last active/retained deadline releases scheduling, without leaking a callback. |
| AgentBridge.Host.Tests | `Data_revocation_and_code_expiry_end_active_flow_without_periodic_idle_sweeps` | Authority changes, code expiry and reattach deadlines still end the right flow promptly; per-send checks remain. |
| AgentBridge.Host.Core.Tests | `Sampler_without_required_consumers_leaves_process_table_unread_until_demand` | Explicitly absent collection demand means no table reads or periodic callbacks; empty sessions alone do not establish absent demand. |
| AgentBridge.Host.Core.Tests | `First_consumer_gets_a_baseline_and_second_sample_before_cpu_is_known` | Resuming demand takes fresh counters; CPU stays unknown until two usable samples exist. |
| AgentBridge.Host.Core.Tests | `Last_consumer_release_disarms_sampling_and_stop_cancels_wait` | Removing the last required consumer disarms scheduling and stop cancels promptly, including concurrent demand changes. |
| AgentBridge.Host.Tests | `Disabled_relay_sharing_preserves_required_local_and_HA_sampling` | Relay sharing off is not a collection-off switch; required local/HA evidence stays fresh. |

Use counting fake process tables/inspectors and a counting TimeProvider with separate
UTC and monotonic progression. Assert callbacks, pending timers and work calls, not
real-clock sleeps. Include forward/backward UTC changes and shutdown. Pure clock-
safety/continuity cases may already pass; do not claim every proposed case is red.
The source recovery bound, sign-in deadline coverage and collection-demand contract
must be agreed before these `not-ready` issues become implementation grants.
[#2268](https://github.com/danswett/agent-bridge/issues/2268) concerns the sampler
test's real-time flake, not this runtime sampling contract.

## Method and coverage

Read each scheduling site and its start/stop/composition path, not only the match.
The host executable references Core, Delivery, Local, Transport, HomeAssistant,
Roster and AgentEnvelope; Core also references Hooks. HostLauncher is included as
the host's supervisor. FeedbackEnvelope is scanned conservatively as another shared
library. Protocol is records/validation, not a background runtime; desktop App,
App.Core, relay server, native hook/injector processes, tools and tests are outside
this host-process audit. Hook invocation waits are noted where Hooks runs on demand.
The efficiency design was not present at this baseline; the audit does not wait on it.

Run from the repository root in PowerShell; ripgrep respects ignored build outputs:

```powershell
$roots = 'AgentBridge.Host','AgentBridge.Host.Core','AgentBridge.Host.Delivery','AgentBridge.HostLauncher','AgentBridge.Local','AgentBridge.Transport','AgentBridge.HomeAssistant','AgentBridge.Hooks','AgentBridge.Roster','AgentBridge.AgentEnvelope','AgentBridge.FeedbackEnvelope' | ForEach-Object { Join-Path src $_ }
rg -n -g '*.cs' 'Task\.Delay|PeriodicTimer|System\.Threading\.Timer|DispatcherTimer|Thread\.Sleep|while\s*\(' @roots
rg -n -g '*.cs' 'CreateTimer|CancelAfter|CancellationTokenSource\(|WaitAsync\(|WaitOne\(|WaitForExit|KeepAlive|Task\.Run|\.Change\(' @roots
rg -n -g '*.cs' '\bTimer\b|\bITimer\b|for\s*\(\s*;|CancellationTokenSource\s+\w+\s*=\s*new\(' @roots
```

The second search catches indirect timers (notably cancellation timeouts), timed
semaphore waits and TimeProvider.CreateTimer. No DispatcherTimer or explicitly
qualified System.Threading.Timer occurs. There is no synchronous wall-clock-change
retry before awaiting in the included schedulers; KeepAwakeService's known exception
is excluded below. A UTC deadline check alone is not the #2283 spin pattern.
An event/IO read loop is not a periodic wakeup merely because it uses `while`.

Paths in the tables are relative to `src`, with these aliases:
`H` = `AgentBridge.Host`, `C` = `AgentBridge.Host.Core`,
`D` = `AgentBridge.Host.Delivery`, `L` = `AgentBridge.Local`,
`T` = `AgentBridge.Transport`, `A` = `AgentBridge.HomeAssistant`,
`K` = `AgentBridge.Hooks`, `S` = `AgentBridge.HostLauncher`,
`R` = `AgentBridge.Roster`, `F` = `AgentBridge.FeedbackEnvelope`.
Comma-separated lines group sites with the same classification, not omitted matches.
**TP/no** means injected TimeProvider, normally the real system clock, with no
unawaited retry on ordinary clock movement; **SYS/no** means real system/monotonic
clock with no such retry. Neither means wall-clock jump correctness was tested here.
**Event/no** means blocked on data/IO, not a clock retry. All count cells are unverified.

## Recurring or deadline-driven host work

| File:line | Interval / trigger | Runs with feature disabled? | Clock / clock-change retry | Measured idle wakes |
| --- | --- | --- | --- | --- |
| `C\SessionSource.cs:339,342,356,381` | 1 s recovery/discovery; 50 ms debounce after file/data wake (`SessionSourceOptions.cs:34`) | Always while source started; watchers off still polls. Empty source still discovers. | TP/no; UTC due check only after awaited timeout/data | unverified |
| `C\SessionSource.cs:1217` | One-shot remaining part of 250 ms coalescing interval | No pending change: no timer; no subscribers is not a gate | TP/no | unverified |
| `C\Load\ProcessLoad.cs:102,111` | Initial sample, then 5 s periodic tick | Yes with relay `load.enabled=false`; `H\Program.cs:449,457` starts unconditionally, with HA at :607 and log at :451-455 | TP/no | unverified |
| `C\Intake\HookIntake.cs:250,258` | Semaphore signal or 2 s sweep; 60 s prune inside pass | No intake without configured installation; G5 pause leaves sweep running but consumes nothing | SYS/no timeout; TP timestamps in pass | unverified |
| `C\Usage\UsageMonitor.cs:90,95` | 15 s scheduling check; vendor reads normally 120 s busy / 900 s idle | No: `usage.publish` prevents construction (`H\Program.cs:518`); empty sessions still tick when on | TP/no; backwards UTC makes next pass due, not an immediate loop | unverified |
| `C\Restore\RestoreRecorder.cs:770,775` | 5 s while judging/restoring; otherwise next owed expiry, capped at 1 day (7-day retention) | `restoreOnStart=false` does not stop recording/judging; no owed entries: no judge timer | TP/no; UTC/monotonic reconciliation, no busy retry | unverified |
| `C\Restore\RestoreRecorder.cs:1155` | One-shot restored-mark expiry, 24 h (`AgentBridge.Protocol\Restore.cs:27`) | Only a shown mark, not a permanent poll | TP/no | unverified |
| `C\Restore\RestoreRecorder.cs:1378,1383` | 250 ms write coalesce; pending offers checkpoint every 3 h; failed write retry 5 s (:1487) | Recording continues with automatic restoration off; only pending writes/deadlines | TP/no; tracks elapsed time | unverified |
| `H\Fleet\FleetPublication.cs:88,91` | 2 s checks per connected relay; publisher reasserts unchanged record every 20 s (`FleetPublisher.cs:60`) | Yes with `fleet.share=false`: empty records still reassert; signed-out/local-only has no connected loop | TP/no; monotonic retry gate | unverified |
| `H\SignIn\AgentSignInService.cs:68` | 250 ms periodic sweep, including no flows | Constructor always arms, even Enabled=false; production `PhoneSignInSetup.cs:42` avoids constructing when feature off | TP/no; flow lifetime also monotonic | unverified |
| `H\RemoteAuthorization.cs:46` | 250 ms trust revalidation, including no attached hub; raises Changed | No when enforcement off; yes while enabled with no channels | TP/no; safety behavior must be retained | unverified |
| `H\SessionService.cs:517` | 10 s hello-change watch | Only after a channel asks; final empty watch stops at next tick (:567) | TP/no | unverified |
| `H\SessionService.cs:837` | One-shot focus quiet deadline (12 s default) | No held changes: no timer; only subscribed channels | TP/no | unverified |
| `H\AgentReadinessService.cs:56,59` | Refresh at start, then every 10 min | Always started (`Program.cs:850`); read-only skips version commands, not scheduling | TP/no | unverified |
| `H\UsagePeers.cs:79,84` | Presence signal or 30 s check; peer reads every 5 min, minimum gap 1 min | No when usage off; yes without an attached link once started | SYS/no semaphore timeout; TP due checks, backwards UTC handled next pass | unverified |
| `H\PushNotifier.cs:646,655,666` | Data wake or next due time, clamped 10 ms..1 min (:818-839); error retry 1 s | No when push off; empty state still has maximum 1 min wait | SYS/no wait; TP due calculation | unverified |
| `D\Staging\AttachmentStore.cs:139` | Initial cleanup, then every 10 min | Always in serving attachment store, including empty/read-only use; explicit `sweep=false` suppresses | TP/no | unverified |
| `D\AttemptLedger.cs:409` | One-shot nearest attempt expiry | No expiring entry: no timer; safety retention is independent of notification settings | TP/no | unverified |
| `H\Program.cs:898,1008` | Relay reconnect backoff: full jitter, exponential ceiling 1..30 s; server hints may change wait | No when signed out/local-only; permanent auth/replacement waits for sign-in | SYS/no semaphore wait; no delay while healthy connection blocks | unverified |
| `H\Program.cs:1022,1024` | Sign-in signal or 5 s account-file check | Yes while signed out or local-only; CLI sign-in observed from disk | SYS/no | unverified |
| `S\HostSupervisor.cs:46,75` | Wait for child exit; crashes retry after 3, 6, 12 s, then stop | No healthy-child polling; explicit stop not restarted | TP/no | unverified |
| `H\Shadow\ShadowCommand.cs:42,59` | Shadow comparison interval, default 15 s (`:147`), allowed 5..3600 s | Not normal serve; only explicit shadow command | TP/no | unverified |
| `T\RelayClient.cs:269,270` | WebSocket keepalive 20 s; response timeout 30 s | Only connected relay client; not application-session activity gated | SYS/no; framework timer, not source delay loop | unverified |

Lower-ranked candidates remain visible: fleet publication (including sharing off),
250 ms trust revalidation, HA's overlapping 1 s monitors, and the notifier's empty
one-minute wake. Do not remove trust/retention/liveness checks just to hit zero.
This inventory makes no owner paging/alerting proposal and changes none of those paths.

## Compatibility connector: recurring checks and operation waits

`H\HaRunning.cs` gates construction on HA configuration. All connector rows are
absent when HA is off. G5/auth pause is different: existing loops still wake.
Nested upkeep/update schedules below share the main tick; they are not extra timers.

| File:line | Interval / trigger | Runs with feature disabled? | Clock / clock-change retry | Measured idle wakes |
| --- | --- | --- | --- | --- |
| `A\HaConnector.cs:336,341` | Main 1 s tick; reconcile 15 s, launch options 30 s, answer fallback 5 s, reply fallback 15 s | No when connector off; yes paused/refused, with work guarded | TP/no | unverified |
| `A\HaConnector.Answers.cs:130,136,142,144,157` | Answer-watch empty/changed-state monitor 1 s; reconnect doubles 2..60 s | No if answers not served; no watched entities still wakes | TP/no; successful event resets backoff | unverified |
| `A\HaConnector.Notify.cs:81,85,92,94,112` | Notification tap pause/held-state monitors 1 s; reconnect 2..60 s | Only when buttons served; paused loop still wakes | TP/no | unverified |
| `A\HaConnector.cs:830,908`; `A\HaConnector.Answers.cs:517,547` | Field registration 900 ms; select retry 150 ms, at most 12 attempts | Only publishing/answering fields; no idle wait | TP/no | unverified |
| `A\HaConnector.Replies.cs:512` | Commit wait 500 ms, at most 6 attempts | Only a reply press waiting for box words | TP/no | unverified |
| `A\HaConnector.Upkeep.cs:108,182,222` | Helper 800 ms x at most 5; discovery reset 1.5 s; registry 250 ms x at most 12 | Only repair/publication attempts | TP/no | unverified |
| `A\HaConnector.cs:123,201,204,1012`; `A\HaContract.cs:32,35,51` | Heartbeat 60 s; global reassert 300 s; entity restore 60 s; machine load 30 s, fleet load 10 s | Feature-specific guards inside main tick; no separate tick if HA off | TP/no | unverified |
| `A\HaConnector.Updates.cs:233,357` | Check timeout 2 min; due checks 30 min / retry 5 min inside main tick | Update checks off skips feed work, not connector tick | TP/no | unverified |
| `A\HaAuthGuard.cs:244,284` | Outstanding cross-process probe reservation: 200 ms poll | Only explicit authorized probe attempt; no standalone idle loop | SYS/no delay, TP monotonic reservation bound | unverified |
| `A\HaAuthGuard.cs:888,895,927,934` | File contention sleeps 25 ms / 20 ms until monotonic deadline | Only record mutation/lock contention | SYS/no | unverified |
| `A\HomeAssistantClient.cs:458,493,545,639,653,682` | WebSocket event/frame reads and awaited event-window deadline | Only watch/event or request in progress; blocked IO, not periodic HTTP polling | Event/no | unverified |
| `A\HomeAssistantClient.cs:305,342,394,544,563` | HTTP timeout (production 15 s), remaining body budget; WebSocket setup/reply 30 s; requested event window | Only network operation in progress | TP/no remaining body; SYS/no other budgets | unverified |
| `A\HaConnector.Replies.cs:170` | Shutdown drain at most 10 s | Connector stop only | SYS/no | unverified |

## Operation-scoped waits (not quiescent-host background work)

No separate idle scheduler for these rows. "Operation only" means that disabling or
never invoking the operation leaves no wait, not that every loop has a config flag.

| File:line | Interval / trigger | Runs with feature disabled? | Clock / clock-change retry | Measured idle wakes |
| --- | --- | --- | --- | --- |
| `C\TranscriptWrites.cs:65,69,73` | Transcript write signal or caller's poll (normally 250 ms); losing delay cancelled | Delivery wait only | TP/no; awaits winner before cancelling | unverified |
| `C\Usage\CodexUsageProbe.cs:193,250` | 30 s grace; caller's bounded probe/report timeout (60 s default) | Usage/probe operation only | TP/no | unverified |
| `C\Usage\CodexUsageProbe.cs:114` | Managed-daemon connect at most 5 s | Probe operation only | SYS/no | unverified |
| `C\Usage\CodexUsageProbe.cs:449,451` | 250 ms other-process watch during standalone Codex probe | Only until probe End; not permanent host watch | TP/no | unverified |
| `C\Usage\ClaudeUsage.cs:424`; `C\Usage\UsageMonitor.cs:303,330` | Probe timeout; shutdown/settle bounds, normally up to report+grace (90 s) | Probe/shutdown only | TP/no | unverified |
| `C\Usage\UsageHttp.cs:63` | Retry delay 400 ms x attempt, 3 attempts default | HTTP operation only | SYS/no | unverified |
| `C\Usage\CommandRunner.cs:43`; `C\Processes\ProcessInspector.cs:423` | Command timeout / process-table command 2 s exit wait | Command/discovery call only; discovery cadence is ranked above | SYS/no | unverified |
| `C\Restore\RestoreRecorder.cs:1516` | Stop wait at most 5 s | Disposal only | SYS/no; losing delay may live to bound | unverified |
| `C\Updates\ReleaseDiscovery.cs:332,589,596` | CDN timeout 10 s; 100 ms contended-file retry, bounded by 45 s Stopwatch | Explicit update check only; cached feed eligibility is not a scheduler | SYS/no; awaits every retry | unverified |
| `H\AgentVerificationService.cs:161,173,177,241` | Verification deadline 8 min; observation every 250 ms | Explicit verification only, never automatic question/permission answering | TP/no | unverified |
| `H\Lite\AgentLiteSender.cs:70,71,78` | Mode watch 500 ms; acknowledgement deadline 10 s | Send only, after enabled check | SYS/no; PeriodicTimer does not use injected time | unverified |
| `H\Lite\LiteKill.cs:145,146,264` | 1 s watch per in-flight guarded send; file mutex wait at most 5 s | No guarded send/off mode: no watch | TP/no watch; SYS/no mutex | unverified |
| `H\LifecycleCoordinator.cs:164,178,236,241,388,394` | 25 ms quiet-lease poll up to 5 s (max 10 s); announce budget 5 s | Planned departure only | TP/no; monotonic remaining bound | unverified |
| `H\DepartureWatchdog.cs:53` | One-shot bound supplied by committed departure | No committed departure: no timer | TP/no | unverified |
| `H\ManageService.cs:271,288` | Leave fallback 3 s; restart notice bound 3 s | Management departure only | TP/no | unverified |
| `H\UpdateService.cs:225`; `H\UsageService.cs:94` | One-shot busy push within 2 s / shared usage coalesce within 15 s | Only data change needing a push, not recurring | TP/no | unverified |
| `H\HostControl.cs:364,402` | Sign-in flow lifetime 20 min; code display wait 30 s | Explicit host account sign-in only | SYS/no | unverified |
| `H\SignIn\AgentSignInService.cs:31,138,141,268,274,287,327,390,393,487` | Flow lifetime 10 min; recovery/send/cleanup budgets 5 s | Phone sign-in flow only, in addition to recurring sweep above | TP/no; monotonic maximum lifetime | unverified |
| `H\SignIn\PrivateSignInRunner.cs:56,95,109,146,168` | Lifetime 10 min, first code 30 s, disposal 1 s; code expiry reschedules lifetime | Sign-in operation only | TP/no; CancelAfter is relative, not a retry loop | unverified |
| `H\AgentSetupService.cs:20`; `H\AgentInstallService.cs:44`; `H\AgentCliSignInService.cs:31` | Mutation budgets 3 min / 10 min / 15 s | Explicit operation only | SYS/no | unverified |
| `H\AgentReadinessService.cs:127,136` | Version command 3 s; cleanup at most 2 s | Readiness pass only; read-only skips commands | SYS/no | unverified |
| `H\SessionTransfers.cs:169,269,270`; `H\RelayBlobs.cs:27`; `H\RemoteDiagnostics.cs:111` | Transfer fetch 20 min / send 9 min, send-slot 30 s; blob 20 min; diagnostics server deadline | Request only | SYS/no fetch/slot/blob; TP/no transfer send and diagnostics | unverified |
| `H\ReleaseEligibility.cs:97`; `H\Shadow\HaReader.cs:391`; `H\Migration\WindowsRuntime.cs:251` | Halt read 10 s; HA reader patience; migration shutdown timeout | Check/shadow/migration operation only | SYS/no except runtime shutdown injected TimeProvider | unverified |
| `H\Migration\Migrator.cs:899,903`; `H\Migration\WindowsRuntime.cs:87,308` | 250 ms stop poll (`MigrationOptions.PollEvery`); process-exit budgets 3 s / 10 s | Explicit migration only | TP/no poll; SYS/no process wait | unverified |
| `H\LocalApi.cs:212,215` | Agent credential file lock retry 250 ms until deadline | Credential mutation only | SYS/no; wall-clock bound, awaited by blocking sleep | unverified |
| `H\AgentSignKey.cs:90,93,129`; `H\HostSignKey.cs:89,93` | Lock 10 ms, bounded 30 s; key-read contention 10 ms x attempt (at most 20 retries) | Key operation only | SYS/no; wall-clock / Stopwatch bounds respectively, not idle clock-change spin | unverified |
| `H\OwnPeers.cs:90` | Mutex wait at most 10 s | Pin mutation only | SYS/no | unverified |
| `D\AnswerService.cs:853,855,937,939` | Resume 1 s / reset 250 ms checks, each bounded by 60 s | Active answer only | TP/no; elapsed-time bounds | unverified |
| `D\AnswerService.cs:1184,1229,1233,1269,1271,1499,1508,1520,1531,1540,1544,1559,1575` | Form/screen checks normally 250 ms; bounded by each answer phase | Active answer only; :1184 retries a refused write after an awaited same-field reread | TP/no | unverified |
| `D\AnswerService.cs:1594,1621,1728,1742,1752,1828,1848` | Confirmation/sweep: transcript event or 250 ms fallback; normally 15 s confirmation | Answer/unconfirmed sweep only | TP/no | unverified |
| `D\ReplyService.cs:338,340,685,690,704,707,711,769,801,833,861` | Reply/submit event or 250 ms fallback; confirm 60 s; optional late-watch every 5 s | Active reply only; late-watch default off (zero) | TP/no; elapsed-time bounds | unverified |
| `D\Launch\LaunchService.cs:278,1194,1217,1253,1349,1798,1800` | Options budget 60 s; registration 60 s; follow 500 ms, up to 10/30 min; exit grace 3 s; trust confirmation 60 s | Active launch/options/follow only; StartScanning (:548) is data-driven, not a timer | TP/no | unverified |
| `D\Launch\VersionCommandRunner.cs:103,117,122,126`; `D\Launch\BatchFileRunner.cs:41` | Command timeout; 10 ms cleanup poll bounded by 1 s | Command only | SYS/no | unverified |
| `D\Windows\ProcessControl.cs:93,97` | 100 ms process-exit check until supplied timeout | Stop operation only | SYS/no | unverified |
| `D\StopService.cs:271,281`; `D\Windows\WorkerInjector.cs:274` | Graceful exit 12 s / termination 5 s; injector teardown 5 s | Stop/injection operation only | SYS/no | unverified |
| `D\Windows\WorkerInjector.cs:91,147,216` | Console probe budget 10 s; injection budget plan duration + 15 s | Active injection/probe only | SYS/no | unverified |
| `D\AttemptLedger.cs:550` | 20 ms x attempt transient-write sleep | Ledger mutation only | SYS/no | unverified |
| `D\DeliveryGate.cs:93,97,193` | Quiet/departure budget; one-shot quiet-lease lifetime | Active lease only; no idle poll | TP/no timer; SYS/no CancelAfter | unverified |
| `D\Handoff\HandoffService.cs:100`; `D\Launch\Worktrees.cs:47` | Handoff answer timeout; git process exit default 30 s | Operation only | SYS/no | unverified |
| `T\ChannelHub.cs:135,216,249` | Presence grace 5 s + 50 ms; bounded handshake waits | Presence loss/open request only | SYS/no | unverified |
| `T\RelayClient.cs:127,170,189,204,223,239,364,528,532,545,552,553` | Control request 30 s; send pacing until window available; presence wait 50 ms; close/drain 2 s | Network operation only; no active send means no pacing timer | SYS/no | unverified |
| `T\SecureChannel.cs:121`; `L\LocalChannel.cs:76` | Request timeout normally 15 s | Request only; send locks are event waits | SYS/no | unverified |
| `T\SignIn.cs:23,25` | Device-login endpoint interval, bounded by offered expiry | Explicit sign-in only | SYS/no; wall-clock bound, delay awaited | unverified |
| `T\RosterSigningKeys.cs:79,82,83`; `L\HostLauncherContract.cs:47,57` | Key file retry 20 ms / launcher record retry 10 ms until deadline | Record/key operation only | SYS/no | unverified |
| `L\LocalClient.cs:61,87,96`; `L\LocalServer.cs:194,196,198` | Connect budget (default 2 s), pipe poll 25 ms; refusal send 2 s, receive 5 s, final 200 ms | Connect/refusal only; not host's idle accept loop | SYS/no | unverified |
| `L\ProcessChain.cs:174` | Process-ancestry helper exit wait 5 s | Ancestry lookup only | SYS/no | unverified |

## Excluded matches: event/IO loops and finite data work

These rows justify every remaining primitive-search match. Interval is **none**;
disabled-feature behavior is **operation/data only**; clock retry is **none**;
measured idle wakes are **unverified**, not an inferred measured zero.
Comment-only matches are separately identified. No timer is hidden by a parser label.

| File:line | Why excluded from idle scheduler ranking |
| --- | --- |
| `C\DevBox\KeepAwakeService.cs:173,223,229` | Explicit scope exclusion: real-clock spin tracked in [#2283](https://github.com/danswett/agent-bridge/issues/2283); no duplicate fix. Desktop CPU is separately [#2284](https://github.com/danswett/agent-bridge/issues/2284). |
| `C\Conversation\HistoryDialects.cs:174`; `C\Conversation\HistoryReader.cs:291,312,351,664,670,716` | Cache eviction and finite bounded transcript scanning/reads. |
| `C\Diagnostics\DiagnosticsJournal.cs:151,168,169,174,215` | Shrinks encoded frame list; trims journal; scans finite text. |
| `C\Discovery\Discovery.cs:653`; `C\Json.cs:73`; `C\Load\MachineLoadMapper.cs:38`; `C\Load\ProcessLoad.cs:218` | Bounded head read, text normalization, payload trimming and finite process-tree traversal. |
| `C\Processes\ProcessInspector.cs:175,352,636` | Bounded ancestor walk and argument/path normalization. |
| `C\Parsing\ClaudeParser.cs:307`; `C\Parsing\ClaudeSubagents.cs:155`; `C\Parsing\CopilotFieldSchema.cs:303`; `C\Parsing\CopilotPermission.cs:465,466`; `C\Parsing\SessionState.cs:270` | Finite parsing/trimming/cache eviction. |
| `C\Transcripts\TranscriptTail.cs:202,207`; `C\Usage\UsageProbes.cs:82`; `C\Updates\ReleaseDiscovery.cs:349` | Finite transcript, parent-directory and bounded HTTP stream reads. |
| `C\Usage\CodexUsageProbe.cs:406,548,631,648` | Awaited RPC/pipe/WebSocket reads during a probe; close has one 2 s bound, not an idle tick. |
| `C\Restore\RestoreRecorder.cs:341` | Comment containing "meanwhile", not a loop. |
| `H\AgentLimits.cs:121`; `H\Fleet\FleetList.cs:83`; `H\HostConfigFile.cs:36`; `H\HaReplies.cs:98`; `H\LoadService.cs:29` | Finite limit eviction, text scan/trim and payload-size fitting. |
| `H\HaCommands.cs:208`; `H\Program.cs:364` | Explicit CLI console input blocked on ReadKey, not host serve or a timer. |
| `H\Lite\LiteDeliver.cs:86`; `H\Lite\LiteReceiver.cs:133`; `H\Lite\LiteKill.cs:247` | Rate-window eviction and lock-free monotonic compare/exchange retry, not wall-clock retry. |
| `H\ManageService.cs:124,501,504,523,710`; `H\PeerUsage.cs:202`; `H\PushStore.cs:121`; `H\UpdateService.cs:368,665,668` | Finite bounded history/rate/refusal/cache trimming. |
| `H\PushNotifier.cs:677,709,1179`; `H\RemoteDiagnostics.cs:256`; `H\ReleaseEligibility.cs:142` | Rate/history eviction, response fitting and bounded HTTP read. |
| `H\StandaloneHooks.cs:170,182`; `H\Shadow\HaReader.cs:411`; `H\Shadow\ShadowWitness.cs:481,484,1076` | Finite parent walk and file/HTTP record reading; no periodic scheduler. |
| `H\SignIn\CopilotSignInAdapter.cs:189,292,298`; `H\SignIn\PrivateSignInRunner.cs:194,218` | Awaited bounded stream/channel reads and buffer cleanup, only during sign-in. |
| `H\SettledRestart.cs:10`; `H\Shadow\ShadowWitness.cs:297` | Comment-only "meanwhile" matches. |
| `D\AgentReplies\Composer.cs:45,48,94,96,103`; `D\AnswerService.cs:1461` | Finite console rows/send-plan traversal. |
| `D\Copilot\CopilotFormScreen.cs:96,373,404,695,702,723,724,734`; `D\Copilot\CopilotInputBox.cs:228,248` | Finite screen parsing and text bounds. |
| `D\Copilot\CopilotPermissionAnswer.cs:362,382,397,399,469,488,531,535,536`; `D\Copilot\Jsonl.cs:56` | Finite permission screen parsing and bounded transcript read. |
| `D\SharedAgentHaBridgeProbe.cs:42`; `D\Windows\AgentHaBridgeProbe.cs:319` | Compare/exchange publishes a completed probe; native object enumeration advances a cursor. Neither retries because real time moved. |
| `D\Windows\AgentHaBridgeProbe.cs:74` | Comment-only "meanwhile". |
| `D\Windows\WorkerInjector.cs:94,150,219`; `D\Launch\ProbeLauncher.cs:103,114` | Awaited child-output reads; operation deadlines, never an idle-host task. |
| `D\Handoff\HandoffAdapters.cs:65` | Awaits source-change channel when a requested handoff response is not yet ready. |
| `D\Launch\AgencyCatalog.cs:86` | One data-triggered scan; repeats only if another refresh event arrived while it ran. |
| `D\Launch\LaunchService.cs:371,375`; `D\Launch\WorktreeUsage.cs:84`; `D\Launch\SessionBundles.cs:118,197,373,416,453,521`; `D\Launch\StartupQuestions.cs:141,198,199,215` | Payload fitting, parent traversal, bounded bundle streams and finite startup-question parsing. |
| `A\HaConnector.Notify.cs:282`; `A\HaConnector.Answers.cs:604`; `A\HaConnector.Launch.cs:659,770`; `A\HaConnector.Replies.cs:125,199,345`; `A\HaConnector.Stops.cs:176` | Finite event/result queues, text trim and pending-reply queue processing. |
| `A\HaConnector.Notify.cs:460`; `A\HaConnector.Stops.cs:41`; `A\HaAuthGuard.cs:18`; `A\HomeAssistantClient.cs:633` | Comment-only matches. |
| `A\HaDecision.cs:209,547`; `A\HaPayloads.cs:305,319,374`; `A\HomeAssistantClient.cs:319` | Text/payload shrinking, option collision resolution and bounded HTTP stream reading. |
| `L\LocalServer.cs:101,159`; `L\LocalChannel.cs:143`; `L\LocalClient.cs:145`; `L\Frames.cs:43`; `L\ProcessChain.cs:55,129` | Blocking async accept/frame reads, queued-event drain, finite frame filling and bounded ancestry walks. |
| `T\RelayClient.cs:373,408,424`; `T\Wire.cs:107`; `T\Blobs.cs:50,163,177,189`; `T\MessageDeflate.cs:42` | Finite pacing eviction, awaited network reads, finite blob/deflate framing; WebSocket keepalive separately listed above. |
| `T\ChannelHub.cs:366`; `T\Noise.cs:442` | Comment-only "meanwhile" matches. |
| `K\Ancestry.cs:61`; `K\HookRunner.cs:219`; `S\HostJob.cs:135`; `F\FeedbackEnvelopeV1.cs:129`; `R\RosterGraph.cs:47`; `R\RosterVerifier.cs:480` | Bounded ancestry/stdin reads, argument escaping, allocation sizing, bit iteration and finite adoption fixed point; invocation only. |

Other asynchronous tasks found by the supplemental search wait on channels, source
events, locks or child exits, or are request-scoped work already grouped above;
starting a Task does not itself establish a timer. In particular launch scanning is
event-driven, unlike session discovery. AgentEnvelope has no listed primitive match.
Untimed CancellationTokenSource construction and cancellation-only WaitAsync create
no clock deadline by themselves; timer field declarations are not extra instances.
No measured "near zero" result, clock-safety test pass, budget approval or fix
completion is implied by this audit.
