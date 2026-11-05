# Host lifecycle seam: quiet lease and release eligibility (slice 1)

Implementation slice for #805/#580/#581, consumed by #819. The full design remains
draft [#815](https://github.com/danswett/agent-bridge/pull/815).
[SRE's slice decision](https://github.com/danswett/agent-bridge/pull/815#issuecomment-6654417590)
permits **graceful, process-local** exclusion only; Connection may veto.
Engineering's independent review is required. No scheduler, installer, apply loop,
remote management command or forced watchdog restart is added.

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
one-typing-worker rules remain. Unexpected worker failure makes quiet acquisition
fail closed with `JournalUncertain` for this process.

Quiet means **no delivery/mutation in flight and no typing now**. Running turns,
questions and session `Working`/`Idle` state are not consulted. #819 and #801
must not add an `AutoUpdater.IdleFor`/whole-session idle test or another fence.

## API (`AgentBridge.Host`)

| API | Result / bound |
|---|---|
| `DepartureConditions.Register(DeparturePurpose, params IDepartureCondition[])` | Once per purpose, at composition. Invalid kinds, duplicate registration or registration after coordinator construction are refused. |
| `LifecycleCoordinator(DeliveryGate, DepartureConditions, IPlannedStateTransport?, TimeProvider?)` | Freezes/copies the composition-time conditions. Not a grant of remote authority. |
| `TryAcquireQuiet(TimeSpan maxWait, DeparturePurpose purpose, CancellationToken, DepartureMode)` | `Task<QuietResult<QuietLease>>`; default-wait overload takes purpose only. Wait 0–10 s, default 5. Returns `Value` or typed `Reason`, never a free-text error. |
| `AnnouncePlanned(QuietLease, CancellationToken)` | `Task<QuietResult<PlannedStateReceipt>>`; five seconds for durable acceptance, inside the lease lifetime. |
| `CommitDeparture(QuietLease)` | `DepartureResult`: `Guard` on commit, otherwise closed `Kind`. In one critical section under the delivery fence, rechecks fence/receipt and evaluates every registered condition exactly once. No call-site delegate or condition list. |
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
`ConditionFault`. `IDepartureCondition` exposes `Kind` and synchronous
`bool IsBlocking()`: no I/O or side effects, reading only its owner's in-memory
snapshot refreshed outside the fence. Kinds are validated/captured at registration.
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

`IPlannedStateTransport` supplies `Negotiated`, connection `Generation`, and
`SendAsync(PlannedStateMessage, CancellationToken) -> Task<bool>`.
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

## Slice 2 proposal: durable journal and cross-process handover

**Proposal for Connection (`eb29d6d8`), 2026-11-05 PT; not agreed or implemented.**
This section is isolated on `sre/805-slice2`, based on #874 at `0327cc2e`.
It does not change #874's reviewed head or enable its refused paths. Agreement
on the questions below, Engineering's independent review and disposable-VM
evidence are prerequisites for code. Silence during async review is not approval.

### Journal: durable custody before acceptance

Today [`AttemptLedger`](../src/AgentBridge.Host.Delivery/AttemptLedger.cs)
flushes question/prompt claims before typing and prevents answering that prompt
twice. It does **not** contain queued answer/reply text, attachment custody,
request outcomes, injector ownership or a handover transaction.
[`OutcomeMemory`](../src/AgentBridge.Host.Delivery/DeliveryGate.cs) remembers
request UUIDs/results only in one process. Neither can prove that a replacement
has retained accepted input or that the old injector has stopped.
Keep existing prompt claims as a dedup safety input; never reinterpret an old
claim as proof of delivery or permission to type again.

Propose one versioned journal per user/profile, under
`%LOCALAPPDATA%\AgentBridge\<profile>\lifecycle\` (other OS adapters use the
product's own profile root). First choice for review: transactional SQLite/WAL
with full durability, encrypted input fields and immutable attempt IDs. This is
a storage-format proposal, **not** a new dependency in slice 1.

| Record | Proposed contents |
|---|---|
| Header / fence | Schema, profile binding, boot identifier, monotonically increasing fence epoch, gate state (`open`, `closed`, `recovering`), owner process identity and durable sequence. |
| Queued request | Original request UUID, operation, target session/instance/question binding, originating approved grant/account scope and grant generation; encrypted recoverable input and integrity digest; retained attachment references with verified bytes/hash. |
| Attempt | Separate random attempt ID, request UUID, epoch, fixed stage, injector launch intent, helper PID **and creation time**, local peer nonce and terminal outcome/sequence. No second injection attempt is allowed merely because an acknowledgement was lost. |
| Handover | Transition ID, old epoch/owner, intended successor nonce, last committed sequence, replay/settlement checkpoint, and successor's completion record. |

`queued` is durably committed **before** the host accepts a new delivery.
Validate/prepare from that record; durably record `injecting` before the first
key, then the terminal outcome before releasing its mutation claim. A transport
acknowledgement never substitutes for the journal commit. Attachments must be
pinned in owner-private storage before acceptance, not left as expiring upload
paths. A write/quota failure refuses before acceptance and retains the client's
draft. Management secrets and #800 device-flow codes/URLs are not replayable
delivery commands and never become journal metadata.

On Windows verify an owner-only user DACL on the directory, database, WAL,
shared-memory and attachment files; deny network access and reject reparse-point/
symlink redirection. Encrypt payload fields with the platform's user-bound key
facility (proposed DPAPI current-user on Windows). On Unix require owner/0700
directories and 0600 files plus its approved user-key adapter; do not silently
fall back to plaintext. Encryption/decryption or ownership-verification failure
is `JournalUncertain`, not an empty queue. Same-user arbitrary-code compromise
is not claimed to be a sandbox boundary.

Proposed initial limits for review: 64 accepted unresolved requests, 8 MiB of
encrypted journal input/metadata and 256 MiB of pinned attachments per profile.
Refuse new admissions when full; do not evict accepted unresolved input.
Remove successful payloads after durable outcome acknowledgement, no later than
24 hours; keep bounded dedup tombstones for 30 days. Unknown/unresolved input
does not expire into success or disappear to make room: it consumes quota until
explicit owner resolution. Ask the owner to approve retention and secure purge
semantics; database free pages/WAL/backups must not retain old plaintext.
Nothing is uploaded to the relay, diagnostics, push or HA. Logs/metrics contain
fixed stages, counts and outcomes only: no input, identities, paths, IPs, flow
codes, tokens or device/request IDs.

### One logical delivery fence across processes

Propose a Windows named mutex scoped by a hash of user SID and canonical profile
path, with verified owner/elevation ACLs. Use a cross-session namespace so another
logon session cannot become a second writer; if creation/ACL verification is
forbidden, fail closed instead of silently choosing a weaker namespace.
Other platforms need an equivalent owner-private advisory lock plus the same
durable epoch protocol, tested independently.

**The mutex and journal's closed epoch are one logical fence**, adapting
`DeliveryGate`, not a third quiet/idle definition:

1. Journal admission, attempt transitions and lifecycle ownership changes are
   serialized under this mutex. Lock order is mutex, then journal transaction;
   never wait for the mutex while retaining a database transaction. Before every
   admission and immediately before
   keys, participants verify the current epoch and gate state. An old cached
   in-memory permission cannot authorize work after the gate closes.
2. The injector owns the mutex on a dedicated OS thread throughout actual
   typing. Named-mutex ownership is thread-affine: do not acquire on one async
   continuation and release on another, or pretend its ownership transfers to
   the replacement process. Prepare/confirmation still carry durable active
   claims, so acquiring the mutex alone is not evidence that a delivery settled.
3. Before forced/external departure, persist `closed` plus a new fencing epoch,
   owner/transition and intended successor nonce. All old-epoch admission and
   helper permits then fail, including a helper launched late. Record a helper
   launch intent before spawning it and register its process identity before
   permitting any keys. No unregistered helper may inject.
4. The mutex may be released for successor startup, but the **durable closed
   gate stays closed**. No injection can pass during that interval. Slice 1's
   local committed `DepartureGuard` still never releases before its host exits.
   The successor establishes new ownership; no one disposes the old guard to
   reopen delivery.

Windows abandonment/owner death means **inspect and recover**, never "the lock
is free, so the input must be safe". Verify boot identity, process creation
time, held process handles, helper launch intents and journal integrity.
PID reuse, age of a file or stale heartbeat alone proves nothing. Never delete
a lock/journal because it looks old. If an injector remains alive, ownership is
unclear, or an `injecting` attempt is unresolved, refuse forced departure with
`JournalUncertain` (or `Typing` while positively known active). A hung delivery
may therefore remain degraded rather than be killed.

The supervisor may terminate only its verified own host child, **not its agent
process tree**, after it owns a durable closed epoch and proves there is no
possibly active injection. App/launcher restarts by another process must obtain
the same fence and authorization; a new CLI restart route cannot bypass #67.
Two simultaneous watchdog/app requests serialize by epoch; only one wins.
Keep slice 1's closed conditions, evaluated once from refreshed memory snapshots;
never put disk/network I/O into `IDepartureCondition` or revive `stillEligible`.
Journal persistence is coordinator-owned recovery work with a separately agreed
prepare/commit ordering, not a caller callback. A stuck durability operation
cannot justify killing, clearing the gate or promising a hard recovery deadline.

### Handover and proof before clearing JournalUncertain

The proposed local recovery exchange is **not a relay message or a management
grant**. Bind it to the private launcher/host channel, same owner/profile,
verified process handles, boot/epoch and an unpredictable successor nonce.
The relay's planned-state acknowledgement remains only an availability receipt.

1. The launcher retains the durable closed gate, verifies the old host exited
   and accounts for every registered/launch-intent injector. If it cannot prove
   that none can still type, do not admit or replay into a new writable host.
2. Start the intended successor in **recovery-only admission**, using the
   existing per-profile host lock too. It verifies storage ownership/schema/
   integrity/decryption and adopts the exact closed epoch. A different profile,
   changed identity or stale successor nonce cannot adopt the queue.
3. Enumerate every committed request through the handover sequence:
   - Settled: restore its durable UUID/outcome and prompt/attempt dedup barriers.
   - Queued, provably never injected: revalidate current target/question/process,
     grant, account and #800 auth state. Only then replay under an internal
     recovery permit using the **same fence/epoch**, with ordinary admission
     closed. Write its attempt and terminal outcome durably.
   - No longer eligible: durably settle **not delivered**, preserve a private
     recoverable draft and report the fixed refusal to its authorized client.
     No implicit Continue, sign-in or retyping is performed.
   - `injecting` or uncertain: **never replay**. Preserve the input/draft and
     dedup barrier; seek verifiable outcome evidence or an explicit person's
     resolution. "Outcome unknown" is not "delivered".
4. The successor commits `RecoveryComplete(epoch, nonce, throughSequence,
   settlementCheckpoint)` only when every accepted request through that
   sequence was replayed and settled, already settled, or explicitly refused
   with retained draft. The conservative default keeps `JournalUncertain` while
   an uncertain injection is unresolved; quarantine alone does not clear it.
5. The launcher independently reads and verifies the committed checkpoint,
   matching process identity, nonce and epoch. Under the mutex, the successor
   atomically records its new owner/open epoch only after that acknowledgement.
   Only then may ordinary delivery resume and the recovery blocker clear.
   The proof survives a lost IPC acknowledgement; it is not just `ready: true`.

If either side dies before step 5, the closed gate persists. A replacement
recovery attempt must establish a new successor nonce after proving its
predecessor/helpers are gone, and inspect attempts again before any replay.
Unknown schema, corrupted history or undecipherable payload keeps a read-only
diagnostic host reachable; never create a new empty journal as "repair".

### What Connection's #800 state must supply

Ask Connection for a versioned **in-memory lifecycle/auth snapshot**, refreshed
outside the fence and bound to host/account/grant generation:

- Structured `attention`/machine auth and recovery state, not inferred from
  `Working`, provider strings or timestamp age. Auth failure alone must never
  look like a hung host and trigger watchdog termination.
- Whether a device-code flow owns a host-child process that cannot survive
  departure (Q-C8), its process identity and flow generation, and an agreed
  preserve/cancel/restart policy. Until proved transferable or safely settled,
  refuse departure. Never journal/forward the verification code/URL or silently
  start the login again after recovery.
- Current approved-person/grant and account identity binding for replay.
  Revocation, sign-out, key/account change or unknown status blocks replay;
  stale saved authority is not sufficient. Retain the draft and settle a
  known-not-injected request as not delivered where possible.
- The distinction between sign-in success and actual recovery: #800's existing
  `pending` state is not proof the agent answered successfully. Slice 2 must
  not clear `signInNeeded` or type Continue merely because the journal settled.
- A negotiated request/outcome continuity rule across host instances. Current
  clients avoid blindly resending to a fresh `machine.instance`; durable UUID
  recovery must preserve that defense. Agree an E2E, scoped outcome lookup/
  continuity proof before allowing cross-instance recovery; this proposal adds
  no wire capability or relaxation on its own.

Do not treat the absence of an adapter/capability as "auth ready". #800's spec
currently describes staged/unregistered recovery entrypoints; this proposal
does not assume production `sessions.signin` or credential adapters are enabled.

### Failure modes and focused phase-2 tests

No tests or builds run for this documentation round. Future tests use fake
storage/time/process handles, then disposable VMs for real OS locks/crashes.

| Failure / suite | Tests named as sentences |
|---|---|
| Journal durability, `AgentBridge.Host.Delivery.Tests` | "A queued input is flushed before acceptance." "A failed flush refuses without typing or losing the client draft." "Pinned attachments survive the departing host." "An uncertain injection is never replayed." |
| Lock adapter, `AgentBridge.Local.Tests` / `AgentBridge.Host.Tests` | "An abandoned mutex with a live injector never permits recovery." "A reused PID cannot claim the old owner's journal." "Two restart callers cannot own the same epoch." "A late old-epoch helper cannot type after the gate closes." |
| Handover, `AgentBridge.Host.Tests` | "Releasing the mutex for startup does not open delivery admission." "A successor cannot clear JournalUncertain without a durable complete checkpoint." "A crash after injecting but before settlement never types the input again." "A stale successor nonce cannot acknowledge recovery." |
| Auth seam, `AgentBridge.Host.Tests` / protocol-client fixtures | "Auth failure does not trigger a hung-host restart." "A host-owned login child blocks an unsafe handover." "Revocation or account change refuses queued replay with its draft intact." "A recovered journal never marks sign-in recovered or sends Continue." |
| Corruption/retention, host/delivery suites | "A torn or unknown journal never becomes an empty queue." "Quota pressure never evicts accepted unresolved input." "A lost completion acknowledgement reuses its committed proof rather than replaying." |

A disk stall after durable injection intent, unresolved helper, opaque auth
state or missing completion proof leaves the gate closed and reports
`JournalUncertain`; it does not extend an in-memory lease until it is assumed
safe. Cancellation stays outside locks/fences, five-second bounded, with
`CancelUnsent` observable. Retain one closed lifecycle contract and #67/#801
authorization; an agent cannot manufacture recovery or exhaust restart budgets
by writing a spool event. OS reboot/power loss is not controllable terminal
atomicity: the journal preserves uncertainty rather than inventing success.

### Proposed unlocks and agreement requested

After review and implementation, this can unlock **watchdog forced recovery
outside an unsafe injection**, **launcher/App restarts performed by another
process**, and **recoverable queued-input handover**. It does **not** unlock a
mid-typing kill, a blind resend, Dev Box power actions, bypassing #800 sign-in
or #67, or silently continuing a session. Slice 1 remains unchanged.

Connection: agree or counter-propose (a 15-minute async review window is offered
on #815; a later named window is fine):

1. Journal custody schema/storage/key binding, quotas/retention and the rule
   that unresolved injection keeps the recovery blocker.
2. Mutex-plus-durable-epoch fencing, including helper launch registration,
   abandonment and thread affinity; exact durable prepare/commit ordering.
3. Successor checkpoint/acknowledgement and cross-instance request continuity.
4. #800 snapshot/flow ownership and authority refresh, including which existing
   closed blocker represents a nontransferable login child. No new blocker or
   client capability is assumed until that mapping is agreed.

Engineering then reviews the agreed sensitive design. No code, production
storage, hooks/tasks, running host, HA/MQTT, deployment or new PR is authorized
by this proposal branch.
