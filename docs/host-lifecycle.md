# Host lifecycle seam: quiet lease and release eligibility (slice 1)

Implementation slice for #805/#580/#581, consumed by #819. The full design remains
draft [#815](https://github.com/danswett/agent-bridge/pull/815).
[SRE's slice decision](https://github.com/danswett/agent-bridge/pull/815#issuecomment-6654417590)
permits **graceful, process-local** exclusion only; Connection may veto.
Engineering's independent review is required. No scheduler, installer, apply loop,
remote management command or forced-recovery watchdog is added; the only restart
added is the committed-departure watchdog below, through the existing `host.restart` path.

## One host, one delivery gate

`ServeAsync` creates one `DeliveryGate` and shares it with answers, replies
(including attachment injection), stops, launch/folder-trust answers and
`HostControl.Lifecycle`. The latter is a host-internal object, not serialized in
status and not exposed as a new pipe or relay op.

`OutcomeMemory` takes admission synchronously **before** creating a new request
entry/scheduling the worker, and holds it through result settlement. A session
claim inside the worker was too late: a quiet probe could miss accepted work
that had not yet reached `TryClaim`. Repeated requests still return their existing
outcome. A new request refused while quiet is not remembered as accepted/busy
forever; a later explicit retry may be admitted. Existing per-session and
one-typing-worker rules remain. Outcome completion and admission release are
atomic with respect to the gate, so a quiet grant cannot overtake settlement.
Unexpected worker failure makes quiet acquisition fail closed with
`JournalUncertain` for this process. The shared-gate injector wrapper also marks
uncertainty before a service can normalize an input-injection exception into a
result; an unconfirmed helper exit must not become permission to restart. Screen
reads pass through unmarked: the read-only worker never sends keys.

Quiet means **no delivery/mutation in flight and no typing now**. Running turns,
questions and session `Working`/`Idle` state are not consulted. #819 and #801
must not add an `AutoUpdater.IdleFor`/whole-session idle test or another fence.

## API (`AgentBridge.Host`)

| API | Result / bound |
|---|---|
| `DepartureConditions.Register(DeparturePurpose, params IDepartureCondition[])` | Once per purpose, at composition. Invalid kinds, duplicate registration or registration after coordinator construction are refused. |
| `LifecycleCoordinator(DeliveryGate, DepartureConditions, IPlannedStateTransport?, TimeProvider?, DepartureWatchdog?)` | Freezes/copies the composition-time conditions. Not a grant of remote authority. |
| `TryAcquireQuiet(TimeSpan maxWait, DeparturePurpose purpose, CancellationToken, DepartureMode)` | `Task<QuietResult<QuietLease>>`; default-wait overload takes purpose only. Wait 0–10 s, default 5. Returns `Value` or typed `Reason`, never a free-text error. |
| `AnnouncePlanned(QuietLease, CancellationToken)` | `Task<QuietResult<PlannedStateReceipt>>`; five seconds for durable acceptance, inside the lease lifetime. |
| `CommitDeparture(QuietLease)` | `DepartureResult`: `Guard` on commit, otherwise closed `Kind`. Checks/marks `Committing` under the gate lock, evaluates snapshots under the coordinator lock only, then re-takes the gate lock to commit the same exclusion. No call-site delegate or condition list. |
| `Abort(QuietLease)` | Before commitment only: releases locally, then sends bounded cancellation outside every internal lock. Returns observable `CancellationOutcome`; a committed lease cannot be released. |
| `QuietLease.Dispose()` | Non-awaiting abort fallback while provisional. It cannot release an already committed guard. |

`DeparturePurpose`: `Update` (both update paths), `RemoteRestart`,
`RemoteStop`, `WatchdogRecovery`, `Sleep`, `Shutdown`, `Reboot`, `SignOut`.
Purpose is a label, not authorization. The closed `QuietBlockedReason` set is:
`Typing`, `DeliveryInFlight`, `JournalUncertain`, `LifecycleInProgress`,
`AnnounceUnacked`, `AnnounceUnavailable`, `LeaseExpired`, `EligibilityChanged`,
`Cancelled`, `ConditionFault`.

The separate, closed `DepartureBlocker` set for commit/conditions is exactly:
`DeliveryInFlight`, `Typing`, `AnnounceUnacked`, `JournalUncertain`,
`AppRunning`, `OtherHostRunning`, `ChannelChanged`, `TransferInFlight`,
`ConditionFault`, `LeaseExpired`. `LeaseExpired` (#885) is the coordinator's own
outcome: the provisional lease's 30-second lifetime ran out before commit, whether
or not the gate's expiry notification has run yet. Callers report it as
"changed" and may start again; it was previously `ConditionFault`, which callers
could not tell from a condition that really faulted. A lease released before its
lifetime (abort, dispose, refused announcement) and a lease already committed still
abort as `ConditionFault`. Registration refuses a condition whose `Kind` is
`LeaseExpired`. `IDepartureCondition` exposes `Kind` and synchronous
`bool IsBlocking()`: lock-free and non-blocking, with no I/O or side effects, reading only its owner's in-memory
snapshot refreshed outside the fence. Kinds are validated/captured at registration.
No owner or transport getter runs while the delivery gate lock is held.
The `Committing` exclusion still refuses new work while those reads occur.
All conditions run exactly once, even if an earlier one blocks/throws; throws
and re-entrant coordinator calls become `ConditionFault`, never a deadlock or
authority to leave. Missing registration also fails closed with `ConditionFault`.
The real App, other-host, channel and transfer conditions belong to their owners
and are **not implemented** here. `ServeAsync` supplies a frozen empty registry;
before connecting consumers, their owners must register their conditions at
composition. No existing command is routed through an incomplete policy.

The provisional lease is nonrenewable, expires after **30 seconds** using
monotonic time, and atomically blocks new accepted work. It never interrupts
existing typing. Expiry/release admits new work, but a stale lease cannot commit
or release a replacement lease. Committing transfers the **same local exclusion**
to a guard; the provisional timer and a `using` disposal cannot unlock it.
The guard remains until this host exits. Guard disposal/finalization and lease
disposal cannot reopen deliveries after commit. If apply fails after commitment,
the host remains fenced until exit; there is no guard-release/abort API.
Prepare/download/verify outside the lease. Suspend invalidation must be supplied
by the future OS consumer through cancellation/abort; no OS event adapter is
installed by this slice.

### Committed-departure watchdog (#805 N1)

A committed guard fences deliveries until the process exits, so a follow-on action
that fails after commit (an update apply that throws, a restart that never happens)
would leave the host refusing deliveries for ever. `DepartureWatchdog` notices that.

- `LifecycleCoordinator(..., DepartureWatchdog? watchdog)` arms it when
  `CommitDeparture` commits, and only then. It runs on an injected `TimeProvider`;
  the bound defaults to **2 minutes** (`DepartureWatchdog(handler, time, bound)`).
  Without one, the coordinator keeps a state-only watchdog on its own clock.
- When the bound passes with the process still alive, the state becomes `Stuck` with
  the closed reason `departure-stuck`, and `IDepartureStuckHandler.DepartureStuck`
  is called **once**. A throwing handler is contained; the state stays `Stuck`.
  Stuck never reopens deliveries in this process.
- `ServeAsync`'s default, `RestartingDepartureStuckHandler`, logs one fixed line
  (`departure stuck: committed departure did not exit; restarting the host`, with
  no exception text or path) and calls the host's **existing `host.restart` path**
  (`HostHandover.Request()` then the serve stop token): a host under the logon
  task's launcher exits with `HostLauncherContract.Restart` for the launcher to
  restart; an unsupervised one starts its replacement after it has stopped and
  let go of its lock. No new restart or exit path is added. This is not
  `WatchdogRecovery`/forced recovery: commit required no accepted work in flight,
  and the fence has refused new work since, so the restart carries no input.
  With no restart action the handler only logs that deliveries stay refused. If the
  host is already stopping when the bound passes (a committed `RemoteStop`,
  `Shutdown` or `SignOut` whose teardown runs past 2 minutes; one usage-monitor
  disposal alone can wait 90 s), it only logs, so a requested stop is never turned
  into a restart. A disposed watchdog (the serve loop has ended) never fires.
- `LifecycleCoordinator.Departure` and `HostControl.Departure` expose
  `DepartureStatus(State: None | Committed | Stuck, CommittedAt, Reason)` to the
  host's status/health readers. It is **not** added to `HostStatus` (the local-pipe
  contract) or any relay message; showing it on the app or Fleet row (#755) is a
  separate contract change.

### Hard slice boundary

`WatchdogRecovery` acquisition always returns `JournalUncertain`.
Acquisition for `ForcedRecovery`, `ExternalRestart` or `QueuedInputHandover`
also returns `JournalUncertain`, so no such lease can reach commit. Only
`SelfInitiatedGraceful` is implemented: the host holding the fence is the one
leaving. There is **no durable input journal, recovered-queue transfer,
cross-process fence, helper-termination proof or forced-restart authorization**.
Those are slice 2, not functionality to emulate with a process-local boolean.
An active accepted request must settle before slice 1 can grant; it is never
carried to another process by this API.

### Announcement abstraction

`IPlannedStateTransport` supplies `Negotiated`, connection `Generation`, one stable
`DepartureConnection Connection` object for its lifetime, and
`SendAsync(PlannedStateMessage, CancellationToken) -> Task<bool>`.
The transport publishes every reconnect/capability change through
`Connection.Publish(generation, negotiated)` before exposing it elsewhere.
Generation cannot decrease; the library-owned snapshot revision also invalidates
capability changes that return to their previous value. Cached receipts are
revalidated against the current generation, capability and revision.
Commit's final gate-locked step compares the receipt with this sealed object's
snapshot and commits under its lock: no transport/owner getter runs under the
gate. Lock order is coordinator, gate, then connection state; `Publish` takes
only the connection-state lock and invokes no callbacks.
Its implementation must serialize controls in sequence order and return true
only for matching durable relay acceptance of that message. No relay handler or
capability is implemented here. The default `UnnegotiatedPlannedStateTransport`
makes this absence explicit.

Only the independently authorized person's `RemoteRestart`/`RemoteStop` may
receive a `PlannedStateReceipt.Legacy` when capability was **not negotiated**.
Automatic host updates cannot use Legacy and remain `AnnounceUnavailable` until
the transport is implemented. A negotiated timeout is `AnnounceUnacked`, never
Legacy; a sent plan is cancelled on abort, including lost acknowledgement.
An announced provisional lease also cancels when released or expired without
commit. **Cancellation is dispatched only after the delivery fence and all
internal locks have been released**, with a five-second bound, including a
transport that blocks before returning its task. A hung cancel cannot block
the next quiet acquisition. `QuietLease.CancellationStatus` and the awaitable
`QuietLease.Cancellation` expose `NotRequired`, `Pending`, `Sent` or
**`CancelUnsent`**; timeout, refusal, exception or a changed connection reports
`CancelUnsent`, never a swallowed failure. Commit returns without waiting for
that network operation. The transport must reject a stale/late lower-sequence
plan; failed cancellation does not make an old receipt usable again.
Late receipts and receipts from a different connection/lease cannot commit.
Abort competes atomically with commit under the coordinator lock; cancellation
is dispatched after unlocking, and aborting an already committed lease returns
immediately rather than waiting on an unstarted cancellation.
Update plans are `reason: update`, default return 90 s; host restart 60 s,
OS reboot 300 s. Other reasons have no expected return. No actor, input or
command request ID is in the relay-visible record.

#819 must still validate its verified target/package/channel and host-only apply
boundary; these are not quiet tests. It must not close the App automatically.
#801 supplies approved-person authorization and #800 login-child eligibility.
This slice connects neither consumer's departure/apply command.

## #580/#581 read-only adapters

`ReleaseEligibility(IHaltListSource)` exposes:

- `IsHalted(version, CancellationToken) -> Task<HaltCheck>`:
  `Halted == false` only after a valid published list was read.
- `IsNewer(target, installed) -> bool`: canonical `0.1.<run>`, numeric nonnegative
  64-bit run; strictly greater only. Equal, older, malformed and another series
  are false. Never an automatic downgrade, even from a halted version.
- `Evaluate(target, installed, CancellationToken) -> Task<ReleaseEligibilityResult>`:
  explicit `HaltListUnavailable`, `Halted`, `NotNewer` or `InvalidVersion`.
  Unreadable policy is checked before "not newer", never reported as up to date.

**Single source:** `ops/halts.json`, reviewed with the repository, has schema
`{"v":1,"halted":[]}` (at most 1,024 canonical versions, 64 KiB). A deliberately
published empty array means no releases are halted; a missing file/asset is not
an empty array. Change this canonical file through review to halt/roll forward.
The publisher uploads it as `halts.json` alongside every release, **before**
publishing the draft, shared across architectures. This is a sensitive build
change. Existing clients are not switched to a new policy in this slice.

**Limitation:** a policy embedded in a build's commit cannot retroactively halt
the release that just shipped with it. Changing `ops/halts.json` through the
normal build/publish path produces a newer release; it is not an emergency
halt of the current newest release.

**Current emergency path (manual, not performed by this PR):** the owner, or a
session with release-write authority assigned that maintenance task, may
re-upload a **validated** `halts.json` asset to the numerically newest published
`v0.1.<run>` release. The staged file must be named `halts.json`. Add the affected version, validate with the same client
schema (`. build\validate-halt-list.ps1; Assert-ReleaseHaltList <file>`), then
replace that release's `halts.json` asset with `gh release upload <tag> <file>
--clobber --repo danswett/agent-bridge`. Re-read the newest tag before/after the
upload to detect a racing publication, and verify the resulting asset and
GitHub SHA-256 digest; do not report the halt effective if a newer unmodified
policy won the race. Keep the prior validated policy for audit/repair, without
removing existing halts. Review/land the same change to canonical
`ops/halts.json` so the next build does not overwrite the emergency policy with
an older list; until then monitor/reapply if publication advances. A reviewed no-build upload workflow is a separate
Platform-owned follow-up; this PR does not grant live release-write permission
or execute an upload.

`GitHubHaltListSource(HttpClient)` reads the existing repository's release API,
selects the numerically newest non-draft `v0.1.<run>` among its newest 100 releases,
and requires exactly that release's `halts.json` asset. It validates the fixed
repository/tag download URL, bounded size and GitHub's HTTPS metadata SHA-256
digest against the downloaded bytes. Missing assets/digests, HTTP/parse/size
errors and hash mismatch yield `HaltListUnavailable`, with no cached-empty or
custom-feed fallback. Reads have a ten-second timeout. There is no new service
endpoint, polling loop, signing key or installer.

This is **verified GitHub release metadata**, not an independent publisher
signature. Its trust is GitHub HTTPS/repository write authority, like release
discovery; it does not prove a separate signed policy. No raw URL/exception,
identity or IP is logged. Consumers call the read-only adapter from their
existing cadence and again before apply; no stale downloaded-package policy
may authorize installation. The existing #580 full rollout proposal/owner tools
are not imported or enabled here.

Until a release carrying this new asset is actually published, the adapter
fails closed. Engineering must review the source/trust and build publication
path before readiness; this draft is not permission to bootstrap a live policy,
deploy or apply an update.
