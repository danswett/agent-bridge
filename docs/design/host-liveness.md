# Host liveness, watchdog and offline reasons (#805)

**Draft for contract review, 2026-11-04 PT. No implementation authorized.**
Platform SRE (`026d5395`) writes this after #800 and the P0s.
[Engineering's staffing decision](https://github.com/danswett/agent-bridge/issues/689#issuecomment-6646652399)
assigns Connection (`eb29d6d8`), owner of #800's auth state and connection layer,
the host-contract review. This is sensitive under rule 5; Engineering owns the
independent review before implementation/merge approval.

The wire contract is [host-liveness](../protocol/host-liveness.md). This document
explains its safety and operating choices; both are proposals, not existing behavior.
[#801](https://github.com/danswett/agent-bridge/issues/801)'s sibling draft is on
`sre/801-remote-host-mgmt`. Its writer agreed to consume the same
`host-planned-state` message, correlate its E2E command result by `transition`,
and keep actors and command request IDs out of relay-visible state.

## 1. What runs today, and who watches whom

Source paths below are relative to the repository; baseline is `cf042d43`.

| Evidence | Current behavior |
|---|---|
| [`HostManager.cs`](../../src/AgentBridge.App.Core/HostManager.cs), `LogonTaskXml.Build/TaskName` | Per-user `AgentBridgeHost_<profile>_<SID hash>` logon task runs the installed launcher, using the interactive token, least privilege and `IgnoreNew`. No time limit or battery stop; scheduler launch-failure retry is one minute, three times. |
| [`HostLauncher/Program.cs`](../../src/AgentBridge.HostLauncher/Program.cs) | `agent-bridge-hostw` creates and waits for its own `agent-bridge-host` child, with no console. It sets `AGENT_BRIDGE_HOSTW=1`, passes profile arguments and checks the profile stop marker. |
| [`HostSupervisor.cs`](../../src/AgentBridge.HostLauncher/HostSupervisor.cs) | #106 restarts fatal exits with 3, 6, 12 second delays, at most three retries. Ten minutes of runtime resets the crash counter. Clean/usage/duplicate/refused exits and deliberate termination do not restart. Exit 75 restarts immediately and currently resets the counter. This watches exits, not responsiveness. |
| [`HostLauncherContract.cs`](../../src/AgentBridge.Local/HostLauncherContract.cs) | Shared stop marker `host.stopped` and deliberate restart exit 75. A new launcher clears an old stop marker; a running launcher checks it before replacing a child. Host clears the launcher environment flag before spawning agents. |
| [`HostControl.cs`](../../src/AgentBridge.Host/HostControl.cs), [`local-pipe.md`](../protocol/local-pipe.md) | Local status and management, including the #67 ancestry guard; these are not a watchdog capability or a remote management authorization. |
| [`DeliveryGate.cs`](../../src/AgentBridge.Host.Delivery/DeliveryGate.cs), [`DeliveryTimeline.cs`](../../src/AgentBridge.Host.Delivery/DeliveryTimeline.cs) | One typing worker across sessions; request outcome memory survives a channel closing, **not a host process dying**. There is no durable exactly-once injection guarantee. |

**Proposed supervision:** the existing launcher alone watches its own child.
The app remains an installer/lifecycle client, not a second watchdog. The relay
never kills or remotely restarts a host. Task Scheduler starts the launcher at
sign-in; it is not the hung-child detector. A directly invoked unsupervised
`serve` reports watchdog unavailable; it must not start a competing supervisor.
macOS/Linux need matching adapters and disposable validation, not Windows APIs
inside shared product logic.

## 2. Detect a hung host without interrupting delivery

### A local progress signal, not a network ping

Propose a private, per-child launcher/host health pipe, bound to the child's
process handle, start time, profile and random boot nonce. Restrict its OS ACL to
the user/elevation, verify the peer PID/start time and use an inherited private
handle for the nonce; never expose it through `host.status`, a normal local API,
environment inherited by agents, relay frames, or a file an agent can write.
The launcher is unelevated. This is a new local contract, not an extension of
`host.manage` that an agent could call.

Every **5 seconds**, a challenge is answered within **2 seconds** with:
nonce/challenge, monotonic sample counter, essential-loop progress counter,
local-dispatch progress counter, lifecycle phase and mutation-fence state.
The essential loop must actually service a no-op on its normal work path; an
independent timer that answers while product dispatch is dead is insufficient.
No transcripts, session IDs, typed text or remote identities are in health data.

**N = 60 seconds** of missing responses or unchanged essential progress
(12 sampling intervals) enters `suspect`. This tolerates brief GC, scheduling and
ordinary I/O stalls while bounding an idle deadlock to about a minute. Allow
120 seconds for first startup/recovery; failure then enters the same safety
decision, not an unconditional kill. Use monotonic awake time. Suspend/resume
resets the sampling baseline and gives 60 seconds of grace; time asleep is not
a hang. Relay/network/auth loss, including #800, is not host unresponsiveness.
An answering host with expired credentials stays alive and reports auth state.

Long downloads, transcript reads, usage probes and agent turns run asynchronously
off essential dispatch. Health reports a bounded, allowlisted operation phase,
not an arbitrary "busy" lease that can be renewed forever. Their own timeouts
cancel work, not the host. A busy agent turn alone never exempts a stuck host;
**an actual delivery/mutation fence does prohibit termination indefinitely**.
If a blocking OS call cannot be isolated safely, the watchdog reports degraded
and cannot claim automatic recovery from that class of hang.

### Safety invariant: no watchdog kill with ambiguous input

The current in-memory `DeliveryGate` is insufficient. **Phase 2 may not enable
forced recovery until all the following exist and pass the fault tests:**

1. All answer/reply/attachment injection paths, including injector helpers,
   participate in a cross-process mutation fence owned for the entire operation,
   from final pre-type checks through writing its terminal outcome. Other
   destructive lifecycle operations use the same exclusion boundary.
2. Before accepting a typed request, keep its recoverable input and request UUID
   in a bounded, user-private durable local journal; flush `queued`, then
   `injecting` before the first key, then the settled result. Encrypt stored input
   with the platform user-key facility; never upload or log it. If journaling
   fails or is full, refuse before typing, retaining the client's input.
3. When suspect, the launcher tries to acquire that fence **without stealing it**.
   Acquiring it excludes new mutations atomically, not merely a racy read of an
   `inFlight == 0` heartbeat. It checks the journal is consistent and there is no
   unresolved `injecting` entry. Unknown, abandoned or unverifiable fence/journal
   state fails closed. A suspended/stuck injector also holds recovery off.
4. With the fence held, consume the quiet lease into the same `DepartureGuard`
   described below and request a cooperative quiesce/exit (10 seconds).
   Only if that fails may the launcher terminate **its own held child handle**,
   never a PID found by name or an agent process tree. Prove the old host and
   injector helpers have exited before starting the replacement; retain the
   guard continuously through replacement journal ownership/recovery. Release
   exclusion only after that handoff, never between termination and startup.
   Unprovable exit leaves safety exclusion held and recovery degraded.
5. Replacement restores queued input and durable dedup results, but **never
   automatically retypes an `injecting`/uncertain request**. Show "Delivery outcome
   unknown; check the terminal before sending again", preserving the draft and
   request identity. A healthy settled delivery is never resent on reconnect.

A hang mid-delivery therefore stays degraded, with a diagnosable reason, rather
than losing half a typed answer to a kill. Do not solve this by expiring the fence,
clearing it from an agent, dropping queued text, or blindly replaying input.
Durable journal retention/cap and recovery UX require Connection/owner agreement
before enabling the feature. OS power loss, forced sign-out and process crashes
cannot promise atomic external terminal input; journal plus explicit uncertain
outcome is the honest boundary, not a claim of exactly-once keys.

### Shared bounded quiet primitive (#805 owns it)

**"Quiet now", not "fully idle".** #819's automatic host update, #801's remote
restart/stop and this watchdog consume **one** lifecycle coordinator over the
delivery/mutation fence above. Neither #819 nor #801 may add another fence,
whole-session idle test, idle-duration reset rule or third safety definition.
An always-`Working` session may pass when no delivery is active; an `Idle`
session with a typing worker must fail. A pending question or running agent turn
does not itself block this primitive. Separate action authorization/eligibility
checks remain mandatory, but cannot quietly reintroduce a whole-session idle
requirement for this shared seam.

Proposed in-process API contract, not implemented and not a public agent op:

```text
TryAcquireQuiet(maxWait, purpose, cancellation) -> QuietLease | Blocked(reason)
AnnouncePlanned(lease, plan, cancellation) -> PlannedStateReceipt | Blocked(reason)
CommitDeparture(lease, receiptOrLegacy, cancellation) -> DepartureGuard | Blocked(reason)
QuietLease.Dispose() -> release provisional exclusion
```

`purpose` is closed: `AutomaticHostUpdate`, `PersonUpdate`, `RemoteRestart`,
`RemoteStop`, `WatchdogRecovery`. It labels a previously authorized local
lifecycle operation; choosing a purpose grants no authority. The launcher's
private health channel adapts to the same coordinator/fence rules, not a second
watchdog-only definition. Proposed phase-2 owner:
`src/AgentBridge.Host/LifecycleCoordinator.cs` (not created in this docs PR);
the launcher adapter uses its private contract and the same cross-process fence.
Discovery progress, a pending sign-in flow or a waiting question alone are not
quiet blockers. Unknown actual injector/delivery state is `JournalUncertain`,
not permission to treat incomplete discovery as quiet. An active terminal-mutating
launch/stop/transfer also owns this fence; it cannot race a departure.
Do not assume #800's login subprocess survives a host restart merely because
its flow status is not a quiet blocker: Connection must establish detached/
supervisor ownership or safe transfer, otherwise refuse departure through the
consumer's `EligibilityChanged` recheck for a host-owned nontransferable login.
This lifecycle-safety dependency is not a whole-session idle check.

- **Bounded acquisition:** `maxWait` is 0–10 seconds, default 5; reject invalid
  arguments before waiting. Use monotonic awake time and honor cancellation.
  At the bound, return the current closed blocker; never wait for an agent
  session to become idle and never cancel/steal an active delivery to make room.
  Waiting alone holds no lease and does not interrupt typing. Simultaneous
  callers are serialized; a waiter cannot extend another caller's deadline.
- **Atomic grant:** only while no delivery owns the mutation fence, no typing
  helper remains active and the journal has no uncertain injection. Acquisition
  and closing new delivery admission are one atomic operation. A new accepted
  input cannot slip between a `quiet == true` check and restart. During exclusion,
  refuse new deliveries before accepting/typing, preserving the client draft;
  already accepted, durably queued input stays recoverable and is not dropped.
  The lease never interrupts mid-typing. Readers, agent turns and transcript
  collection may continue.
- **Lease lifetime:** one nonrenewable **30-second** provisional lease from grant,
  bound to the profile, current host generation, holder and one random transition.
  Prepare/download/verify packages and check package/process eligibility before
  acquisition, not under it. The five-second announcement timeout is inside,
  not added to, those 30 seconds. Dispose, cancellation, generation change or
  expiry invalidates the provisional authority and releases its exclusion; no
  delayed callback/receipt may commit an invalid lease.
- **Commit and handoff:** before expiry, atomically recheck the fence/journal,
  current action eligibility and matching announcement receipt, then consume
  the lease into a `DepartureGuard`. This transfers the same exclusion to the
  supervisor/replacement handoff; it does not unlock and reacquire. The guard
  excludes new deliveries until the old host and injector helpers have exited
  and the replacement owns the recovered journal (or a deliberate stop completes).
  It is not a renewable quiet lease. The ten-second cooperative-exit bound and
  watchdog kill rules above still apply; if exit cannot safely be proved, retain
  safety exclusion and report degraded rather than releasing it on a timer.
  The bounded **decision/wait** never promises a bounded destructive operation.
  Watchdog recovery may commit without a relay receipt only through its existing
  private suspect/recovery path; it still needs the same lease and delivery checks.
- **Abort:** any abort after sending a plan, including uncertain/timed-out
  acknowledgement, sends `phase: cancelled` for the same transition with a greater
  sequence (best effort if disconnected). Cancel before releasing when possible,
  but never extend the 30-second provisional bound waiting for it. Fence checks
  reject a late acknowledgement; `departBy`/generation rules bound stale relay
  intent. Never turn an `AnnounceUnacked` failure into a restart.

The shared result's **closed `BlockedReason` set**, used by acquire/announce/commit:

| Reason | Meaning |
|---|---|
| `Typing` | A typing/injector helper currently owns the fence; takes precedence over `DeliveryInFlight`. |
| `DeliveryInFlight` | An accepted delivery or terminal-mutating launch/stop/transfer owns the fence outside the typing stage, including final checks/outcome settlement. |
| `JournalUncertain` | Unresolved injection, unavailable/inconsistent journal or abandoned/unverifiable fence; fail closed. |
| `LifecycleInProgress` | Another provisional lease or departure guard owns exclusion. |
| `AnnounceUnacked` | A sent plan lacks a successful, matching durable receipt within five seconds, or earlier uncertain intent has not been reconciled. Maps to `announce-failed` for #801/#819. |
| `AnnounceUnavailable` | Required capability/store/transport is unavailable or the relay refuses the plan. Maps to `announce-failed`; #801's documented old-relay compatibility exception remains explicit. |
| `LeaseExpired` | Lease expired, was consumed/disposed, or belongs to another holder/host generation; no commit authority. |
| `EligibilityChanged` | The consumer's mandatory authorization/read-only/stop-marker/package-process eligibility recheck failed at commit; not a new idle test. |
| `Cancelled` | Caller cancellation or OS suspend invalidated an uncommitted attempt. |

No free-text exception becomes a reason and no session/device identity is logged.
At acquisition timeout return `Typing`, `DeliveryInFlight` or the other actual
blocker, not an ambiguous "busy"; at zero wait inspect once. Announce reasons are
results of `AnnouncePlanned` (or unresolved prior intent), not a requirement to
announce before acquiring. `AppRunning`, `OtherHostRunning`, `NotInstalled`,
`NoVerifiedPackage`, halted release and wrong architecture belong to #819's
separate typed apply/target eligibility, not new quiet definitions. A later
eligibility change invalidates commit; nothing is forced.

The host-side send/ack seam above uses the
[single planned-state wire contract](../protocol/host-liveness.md), durably accepts
within five seconds, and binds its receipt to this transition/current relay
connection. It does not create another update message or installer. All of these
API names and bounds await Connection/Engineering contract review, not runtime
or rollout permission.

### Recovery budget and logs

Extend #106's supervisor rather than adding nested retry loops. A watchdog
replacement and a fatal crash consume the **same three-retry budget**, using
3/6/12 second backoff. Also cap automatic recoveries at **three in any rolling
hour**, persisted in a small per-profile user-private supervisor record so
restarting the launcher cannot defeat the cap. Reset the burst budget only after
ten minutes of verified progress, not mere process uptime or exit 75. Authorized
human restarts keep their own #801 rate limit; automatic code must not manufacture
75 to reset a budget. A stop marker, uninstall, update handoff or OS sign-out wins
over an in-progress watchdog decision; recheck while holding the fence.

At the cap leave recovery disabled with a local machine-status diagnostic; a
person's approved action may recover it through #801 once that path is usable.
Never silently keep retrying while #800 auth is broken. If the launcher itself
dies or Windows is offline, this watchdog cannot repair it; no Dev Box power
action is implied.

Local bounded/rotated logs record suspect/recovered, awake duration, fixed phase,
fence-blocked, cooperative/forced outcome, attempt/backoff, cap and exit category.
Rate-limit repeated blocked notices to one per minute. Respect the existing host
log rotation policy and avoid concurrent uncoordinated launcher/host appends.
No input, key, credential, session text, IP or identity is logged. Relay logs get
only allowlisted event/category and a random socket tag; health details remain
local. Restart proof is not an excuse to add client-identifying telemetry.

## 3. Planned departure and truthful offline reasons

One host lifecycle coordinator owns quiesce, announcement and cancellation.
Adapters observe Windows suspend/session-ending/power events; report only what is
known, not a guessed reboot from a disappearing socket. Abrupt battery/network
loss may leave no announcement. An OS callback is best effort, with at most a
two-second announcement wait; do not block shutdown/suspend indefinitely.

| State | When announced |
|---|---|
| `sleep` | Suspend notification before sleep; clear on authenticated return. No return timer. |
| `shutdown`, `reboot` | OS shutdown vs explicitly known reboot. If the OS cannot distinguish, use `shutdown`, not a guessed reboot/push timer. |
| `sign-out` | Session ending or explicit product sign-out, before closing/revoking credentials. Does not expose the account or credential. |
| `update` | At apply time after a shared quiet lease, before host restart: either an eligible unattended **host-only** update (#819) or the person-chosen update path. Not at download/check time. |
| `restart`, `stop` | #801's authorized person's action, after authorization and the shared quiet lease, before exit. Hook repair is not a departure. |

For deliberate product actions on a capable relay, await durable relay
acknowledgement (five-second timeout) before committing the exit. Refuse with
`announce-failed` if a negotiated announcement cannot be acknowledged. An older
relay without the capability does not block #801's separately authorized stop/
restart: the pressing client retains its E2E result and others use legacy offline
presentation, without no-return pushes. Cancelled/aborted actions
publish `phase: cancelled` for the same transition. An OS shutdown still proceeds
if offline: absence of a reason is better than fabricated success.

[#498](https://github.com/danswett/agent-bridge/issues/498) originally described
automatic idle installs; its later #516 direction keeps the **App** on an explicit
person's Restart to update. The
[newer host-only owner decision](https://github.com/danswett/agent-bridge/issues/819#issuecomment-6647803922)
also permits automatic **host-only** convergence; it does not permit automatic
App exit or a package apply that stops the App. Preserve both paths:

- #819 checks the non-halted/no-downgrade/own-architecture verified target and
  host-only package/process eligibility, then acquires the shared quiet lease.
  At apply time it sends `reason: update`, default `expectedReturnSeconds: 90`,
  requires durable acceptance within five seconds and commits the host-only
  restart with that receipt. Failure/abort, including an apply precondition
  changing, sends `phase: cancelled`; `announce-failed` never forces restart.
  No negotiated announcement support means unattended apply is blocked, not
  silently downgraded to #801's legacy exception.
- A person-chosen update uses the same lease/announcement/cancellation seam and
  existing authorization. #516/#719 retain explicit App restart; no new
  auto-installer, scheduler, package copy or package-splitting claim is implied.
  If the current package cannot apply without closing a running App/other host,
  #819 reports that existing eligibility blocker and waits.

This reconciles
[#819's proposed seam, items 1–2](https://github.com/danswett/agent-bridge/issues/819#issuecomment-6648160430);
it is a proposed shared contract, not approval of the remaining apply/target
interfaces. Reuse `UpdateState` for download/version/progress and the single
planned-state contract solely to bridge its disconnect; no competing update
command/state machine. Replacement clears planned updating but E2E `UpdateResult`
still establishes whether the update succeeded. Ninety seconds is the optimistic
Updating treatment proposed by Design, not an availability guarantee.

### Relay storage and client presentation

The relay holds a bounded last-observation record per account/device, never an
unbounded event history: observed disconnect time, last numeric close code and
direction/category, planned state/transition/receipt/expiry, return deadline,
generation and push dedup outcome. Admission failures without proven identity
create no per-device record. An old replaced socket cannot overwrite new state.
The wire spec defines expiry and existing close codes; none are repurposed as
"sleep" or "host reboot".

The relay already sees routing IDs and the roster; this deliberately adds the
minimal **plaintext lifecycle metadata**, not session content or command actors.
That is a scoped privacy change requiring Connection/owner approval. It is not
E2E proof that the OS really rebooted. Account-bound authenticated clients may
read it; never expose it through public ops endpoints or telemetry.

Keep detailed offline observations at most **24 hours** after disconnect,
delete on forget/account deletion and clear active intent immediately on a new
authenticated host generation. Roster `lastSeen` can remain under its existing
retention rules. A no-return incident retains only its bounded dedup tombstone
until the 24-hour limit. Storage is user-associated private service state, **not
logs**; reuse the roster's HMAC account naming/managed identity/ETag discipline,
but do not silently change roster v1's parser/merge semantics.

Both Windows (`Fleet`/`OfflineMachineViewModel` in
[`Machines.cs`](../../src/AgentBridge.App.Core/Machines.cs)) and PWA
(`js/relay.js`, `js/connection.js`, machine rendering) consume the same
optional presence extension, reconcile new/forgotten/account-switched devices,
and discard stale intent on reconnect. Show:

- "Offline since 03:12 PT: rebooting" during the bounded expected return.
- "Updating" in a quiet neutral connecting treatment for up to 90 seconds;
  then "Offline since 03:12 PT: update has not returned", not endless Updating.
- "Offline since 03:12 PT: asleep", "shut down", "signed out", or
  "stopped; returns at next sign-in or Start", while the observation is retained.
- After a return deadline: "Offline since 03:12 PT: restart has not returned".
- Without reliable planned state: "Lost connection" (and known offline time),
  never "network failure" or "host crashed" inferred from a missing socket.
- Relay 1012: reconnecting to the relay, not "machine rebooting".

Times are relay-observed UTC on the wire, formatted as Pacific/PT in both clients
with daylight-saving conversion; date shown for prior days. Mark stale cached
data when the client itself lost the relay. Do not present an exact offline time
after a relay crash if only an older `lastSeen` is known. Stop attribution such
as "Stopped from <device>" comes only from #801's E2E result cached by that
client; the relay supplies no actor and other clients use generic copy.
No toast/Needs you item for an ordinary expected departure; text and accessible
status convey meaning without relying on color. Final copy/state rows need
Design approval under #498.

## 4. No return: relay decision, existing Web Push path

**N = 10 minutes for a planned reboot:** expected 300 seconds + 300 seconds
grace, starting at observed disconnect, not at an announcement while still online.
Defaults for host restart are 60 + 120 seconds; update 90 + 210 seconds.
The relay bounds expected duration to 30–900 seconds and fixes grace by reason.
Only `reboot`, `restart`, `update` arm a no-return timer. Sleep, shutdown,
sign-out and stop do not imply a promised return.

The relay owns the timer because it owns offline state; a host cannot notify
while offline and an unopened phone cannot decide reliably. There are two durable
stages: persist pending intent, `departBy` and notice references before acknowledging
the plan; on an eligible observed disconnect, atomically persist `offlineSince`
and arm `returnDue`. Never start the return clock at acknowledgement. At due time
persist a conditional dispatch claim before HTTP, not while acknowledging a plan.
On handover/restart read pending timers, reconcile authoritative connection
generations, and use conditional claims so only one replica sends.
A connection before the deadline,
cancellation or forget cancels the timer; recheck immediately before dispatch.
Relay outages/deploy drain are not machine failures: give recovered connections
three minutes to reconcile, then evaluate overdue plans only while the relay
is healthy. Mark missing-store/reconciliation coverage unknown, not "no incidents".

The existing [`PushGateway`](../../src/AgentBridge.Relay/PushGateway.cs) only
forwards **already RFC 8291-encrypted** bodies and signs with VAPID; it cannot
invent a host-encrypted notification after the host disappears.
Propose that the host pre-seal one generic no-return payload per opted-in
person's push subscription before leaving and attach bounded envelopes to the
same announcement. The relay keeps no browser decryption keys. The protocol
defines a short-lived target registration/consent requirement: arbitrary
host-supplied endpoints are not sufficient authority to notify someone.

Plaintext inside that encryption is only version, notice kind, opaque transition,
expiry and generic copy: "A machine has not returned after a planned restart.
Open Agent Bridge to check." No machine name/device ID, account, agent/session,
transcript, token, folder, IP or action/approval capability. Clicking opens the
ordinary app; authenticated presence resolves details. No remote restart button
in the push. Use the existing endpoint allowlist, HTTPS/port/size checks,
VAPID configuration and account rate budget (currently 30/min); no new push
provider or key. Errors on this path must be sanitized to fixed categories,
never raw HTTP exception text that might expose an endpoint/address.

At most one notification per transition/recipient, **one incident per device
per hour and three per account in 24 hours**, across relay restarts. At most
eight opted-in recipients; no expanding fanout. Claim before send; delivery
uncertainty consumes the claim (at-most-once dispatch, no retry buzz loop).
Use a stable opaque collapse topic, `urgency: low`, TTL 3600 seconds, and a
service-worker tag with `renotify: false`. Opt-in, existing push enablement and
per-recipient quiet schedule apply. During quiet hours keep one pending incident
only until its one-hour notification TTL; recheck online state at quiet end,
then send or drop. Quiet preferences are relay-enforceable consent, not an
unenforceable client hint. Missing consent/envelope/VAPID means no push; UI still
shows the overdue state. Revoke/delete targets on subscription change/forget.
OS sound/vibration ultimately follows the person's browser/OS preferences.

## 5. SRE fit and threats

Proposed instruments for [ops/plan.md §4](../../ops/plan.md#4-observability)
and [§9.2](../../ops/plan.md#92-fleet-586):

| Instrument (proposal) | Bounded labels / use |
|---|---|
| `relay.host.offline` counter | `classification=planned/unplanned/unknown`, allowlisted `reason`, close-code category; one increment per current-generation offline transition. |
| `relay.host.observed.seconds` counter | Aggregate authenticated-host observation exposure; denominator for device-days. |
| `relay.host.return.duration` histogram | Fixed `reason`, `outcome=returned/expired`; planned disconnect to return. |
| `relay.host.no_return` counter | `outcome=sent/suppressed/cancelled/expired/unavailable`; quiet/cap/failure distinguishable with fixed categories. |
| `relay.host.lifecycle.records` gauge | `state=pending/offline/tombstone`, plus bounded storage failures and invalid/over-quota plan counters. |

No IP, name, account/device/transition/request ID, push endpoint or socket tag in
metric labels. Version/OS/ring labels only once #570's bounded allowlist is
available. Watchdog diagnostics stay local; do not pretend the relay observed
a host crash or export a per-machine watchdog heartbeat.

**Candidate SLO-relevant fleet signal:** unplanned offline transitions per
observed device-day (`sum(unplanned) / (sum(observed.seconds)/86400)`).
This is exposure while admitted, not a census of all registered device-days.
Do not count initial admission refusal, replaced socket 4009 or relay deploy 1012
as host failures; exclude relay-unhealthy windows and mark gaps unknown.
Self-reported plans are untrusted: graph all disconnects and expired planned
returns alongside the unplanned ratio so a lying client cannot hide deterioration.
This is a release-safety indicator, not a new SLO target or error-budget debit
until two weeks of baseline and owner approval.

The owner's fleet view gets only that account's offline reasons; the Azure
workbook gets aggregates. This is an explicit lifecycle exception to plan
§9.2's E2E per-machine health principle, not permission to move versions,
diagnostics or operator authority into the relay.

No new page for one offline laptop. Proposed fleet alert: after baseline, a
Sev 3 **email only** if unplanned rate exceeds 3× the preceding seven-day baseline
for 30 minutes, with at least 20 transitions and five observed device-days in
the evaluated window; otherwise digest only. Suppress during relay incidents/
drain; correlate A-STORM/A-RELEASE instead of duplicating them. Before enabling,
add a runbook and fire it on staging per [alerts.md](../../ops/alerts.md).
The owner's email-only/no-night-paging decision in that catalog supersedes the
earlier phone-routing proposal in the plan. Personal no-return push is an opt-in
product notice, not an ops alert.

| Threat | Mitigation / residual limit |
|---|---|
| Device lies about planned state to suppress pushes | Bind to admitted proven host connection and current generation; no arbitrary target ID. Allowlisted reasons, two-minute departure window, nonrenewable return deadline, independent expired-return metric. No plan grants authority or indefinite health exemption. A compromised host can lie about itself; the relay cannot attest Windows power state. |
| Relay storage growth | One record per roster slot (64/account), one active transition/device, 24-hour detailed TTL, bounded fanout/envelopes and global byte/account admission caps; conditional writes, expiry sweeper and metadata-only fallback when full. No per-heartbeat persistent writes. |
| Push abuse / endpoint SSRF | Person-controlled consent and target binding, account isolation, existing HTTPS/host allowlist and VAPID path, short TTL, global/account/device/fanout caps, atomic durable dedup and generic non-actionable encrypted payload. Revocation cancels pending envelopes. |
| Agent deliberately triggers watchdog | No kill/restart op on public pipe/relay/agent API; private authenticated child health; challenge cannot be written through spool/config. Agent activity/relay loss is not failure. All mutation paths fenced, bounded recovery and fail-closed ambiguous input. Same-user arbitrary-code compromise can still starve/terminate processes; not claimed as a sandbox boundary. |
| Replayed plan / replaced socket / relay outage | Fresh transition and connection generation, monotonic sequence, server timestamps, cancellation tombstones, ETag claims and reconnect precedence. Refusals before proof never poison a roster entry. |

## 6. Phase 2 test contract (not run for this docs PR)

Use fake time, controlled channels/process handles and disposable injector
fixtures. Named tests should read as sentences; extend existing suites rather
than creating a broad soak/load harness.

| Suite / existing home | Focused sentences |
|---|---|
| `AgentBridge.App.Tests/HostSupervisorTests.cs` | "A host with no essential progress for sixty awake seconds is replaced only while the mutation fence is held." "A long asynchronous download does not cause a watchdog restart." "Suspend time does not consume the hang deadline." "Crashes and watchdog recoveries share a persisted hourly cap." "A stop marker wins over a pending recovery." |
| `AgentBridge.Host.Delivery.Tests` | "A hang after the first key is never killed by the watchdog." "Acquiring the recovery fence prevents a queued delivery from starting." "A failed journal write refuses before typing." "A recovered uncertain delivery preserves input and never types it again." Include answer, reply and attachment paths and helper-process lifetime. |
| `AgentBridge.Local.Tests` / `AgentBridge.Host.Tests` | "An agent cannot open the private watchdog channel or manufacture a health response." "Relay auth failure leaves a responsive host running." "A cancelled lifecycle operation withdraws its planned state." |
| `AgentBridge.Host.Tests` / `AgentBridge.Host.Delivery.Tests` shared coordinator coverage | "An always Working session can acquire quiet when no delivery is active." "An Idle session with an active injector returns Typing." "A quiet waiter returns the closed blocker within its maximum wait." "A new delivery cannot enter after the quiet lease is granted." "No delivery is admitted between old-host exit and replacement journal ownership." "A late announcement receipt cannot commit an expired lease." "An expired provisional lease never releases a committed departure guard." |
| `AgentBridge.App.Tests` / `AgentBridge.Host.Tests` update consumer coverage | "An unattended host-only update announces after quiet acquisition and before restart." "An unacknowledged update cancels its plan without applying." "A running App blocks host-only package apply without becoming an idle test." |
| `AgentBridge.Transport.Tests/RelayTests.cs` and `RelayDeploymentTests.cs` | "An old replaced socket cannot overwrite its replacement's offline reason." "Unproved admission failure creates no offline record." "A plan expires if the host does not depart within two minutes." "A reconnect clears a plan before a due push can dispatch." "A reboot still missing after ten minutes sends at most one push across relay handover." |
| `AgentBridge.Transport.Tests` push/roster coverage | "Quiet hours and the account cap suppress no-return pushes without hiding offline state." "A revoked target cannot receive a pre-sealed push." "Oversized plans and cross-account targets are refused before storage or HTTP." "Expired and forgotten lifecycle records are removed." "Relay metrics and logs contain no identity or address." |
| `AgentBridge.Protocol.Tests` and PWA `test/unit/protocol-vectors.test.mjs` | "Unknown lifecycle capabilities retain legacy presence behavior." "Unknown reasons fall back to lost connection." Shared fixtures cover replay, cancellation, version/field/size limits, existing close-code meanings and absent close frames. |
| `AgentBridge.App.Tests` / PWA `test/unit/fleet.test.mjs`, `relay.test.mjs`, `sw.test.mjs` | "Updating expires without masking a failed return." "A relay restart never says the machine is rebooting." "An account switch drops cached offline reasons." "A stale no-return notification cannot repeat after reconnect." |
| PWA Playwright `test/machines.spec.mjs`, `update.spec.mjs`, `push.spec.mjs`; Windows app self-test fixtures | "The machine row announces its offline reason and Pacific time without relying on color." "An expected departure is quiet and a missing reason says lost connection." |

OS suspend, shutdown, sign-out, crash/hang and update replacement ordering require
ring-3 disposable Windows VM tests; other OS adapters need their own disposable
fixtures. Never exercise them on the live bridge or an owner's workstation.

## 7. Decisions required before phase 2

**Connection (`eb29d6d8`):**

1. Agree the essential-loop/private-pipe shape, #800 auth separation, replacement
   generation fencing and capabilities in the wire contract, including the shared
   quiet API's ten-second acquisition/30-second provisional bounds and guard handoff.
2. Approve the durable delivery journal, cross-process injector fence and honest
   uncertain-outcome UX; define retention/size and request dedup across restarts.
   Without these, detection may ship but forced restart must remain disabled.
3. Approve the relay-visible metadata/privacy exception and account/target consent
   binding; decide the lifecycle store schema, merge and restart reconciliation.
4. Confirm a failed negotiated acknowledgement refuses #801's deliberate remote
   departure, while old-relay compatibility proceeds without a reason/push and
   OS departure remains best effort.
5. Resolve #801's Q-C8 with #800: a pending flow alone is not session-wide busy,
   but a host-owned login child must survive/transfer safely or block commit via
   `EligibilityChanged`; do not assume ongoing device-code recovery survives.

**Owner / Engineering / Design, coordinated with #801 and #498/#516:**

1. Accept 60-second detection, three/hour recovery cap and the safety tradeoff
   that a mid-delivery hang stays alive rather than risking typed input.
2. Accept reboot ten-minute return, restart/update grace, 24-hour metadata
   retention, explicit opt-in/quiet schedule and three/account/day push cap.
3. Approve final Windows/PWA words and overdue update treatment; confirm how OS
   reboot vs shutdown is reliably known and what to show when it is not.
4. Confirm rollout flags default off, Dev/disposable/PPE evidence before Beta,
   and independent sensitive review after Connection signs the contract.

**Phase 2 blockers:** #800 and P0 priority/landing, Connection's contract approval,
Engineering's independent review, #801 shared-contract reconciliation, Design's
state/copy approval and the delivery-safety/store/consent decisions above.
#55 remains #801's separate config/token authorization dependency; this design
does not bypass it. A draft document is not remote-management permission.

**Out of scope:** Dev Box "Start"/power-cycle via the owner's `az devbox`, a
separate design; changing #67; remote crash injection; new relay trust in command
actors; production rollout, installed settings/tasks/hooks or live-process work.
