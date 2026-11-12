# Fast updates and Update all (#2225)

Status: design for review, revision 3. Engineering owns it; Product ranked the epic 4 (P1) on #2225.
Scope: how a machine learns of a release within minutes, one press (or one Update all) that takes
every machine across, progress that is only ever the machine's own word, and three update modes.
Not in scope (parked by Product): scheduling and per-machine channels.

Revision 3 answers both receipts on PR 2226 (Reviewer M, comment 6815300702; Reviewer H, 6815482733)
under Engineering's brief (#2225, 6815507156). It adds §4 (halt policy) and §10 (later designs), and
changes §2, §3, §5, §8 and §9. Product's rulings stand: Update all stops when the app closes, pressed
machines carry on, and an unreadable halt list blocks Update all while one machine may be pressed
with a warning. v1 recovery is forward only (Product confirmed, point 8).

Citations are to `main` at 5226150 (no cited source changed since 0e17432). "App" is the Windows app
(Slipgates), "host" its bridge, "relay" the Slipgates relay, "release relay" the Cloudflare Worker.

## 1. Where things stand

**Checks.** The app checks on start and every 30 minutes (`CheckEvery`, src/AgentBridge.App.Core/AutoUpdater.cs:72),
every 5 after a host failed an update (:75). Every check on a machine, app's and host's
(src/AgentBridge.Host/HostUpdates.cs:27), goes through one cache, `ReleaseDiscovery`
(src/AgentBridge.Host.Core/Updates/ReleaseDiscovery.cs), at most once per `Interval` of 10 minutes plus
up to 2 of jitter (:63-64). It asks the release relay first (`CdnList`, :51), and GitHub's API
(unauthenticated, 60 an hour per IP) when the relay's list fails its checks, or at least every 6 hours
(`GithubRevalidateEvery`, :57). Under a GitHub refusal a relay list may be used for up to 24 hours from
GitHub's last answer (`GithubRelayAtMost`, :58; `RelayWindow`, :281-286). GitHub requests carry an ETag
(:247); a relay list is kept without one (:234). The release relay (infra/cloudflare/release-feed/worker.js) caches
GitHub's list for 5 minutes (:7), serves stale up to 24 hours with its real `Age` (:8, :88), sends no
ETag, and serves the list only; packages come from GitHub.

**Worst case today:** 5 (release relay) + 12 (cache) + 30 (app tick) = about 47 minutes. After a GitHub
refusal the app holds checks, doubling up to 2 hours (`checksHeldUntil`, AutoUpdater.cs:199, :1159-1170);
`CheckAsync` refuses before reaching discovery while held (:959), and answers from its cached failure
for 1 minute (`CheckAgainAfter`, :113, :951). Three strings put the app machine's local time in what the
phone reads: :1170 ("checking again after"), :429 ("retrying at") and ObservedUpdater.cs:107 (download held).

**Trust, kept as it is.** (1) Publish: CI downloads the draft back and checks each feed's hashes; files
we own must carry the pinned publisher's Authenticode EKU, and other PE files a valid signature from
their own publisher (build/app-signing.ps1:59-65; build/publish-prerelease.ps1:349-388). (2) Discovery:
a relay list is used only if every asset is under this repository's download URL, nothing is a draft,
and the channel's newest is no older than GitHub's last list or what is installed (`CdnProblem`,
ReleaseDiscovery.cs:373-401); the relay is untrusted transport. (3) Install: the package must match its
feed's SHA256 (src/AgentBridge.App/Platform.cs:95-97, :166-170).

**Halt list.** `ReleaseEligibility` and `GitHubHaltListSource` exist (src/AgentBridge.Host/ReleaseEligibility.cs:19-113)
but nothing in `src/` constructs them. The source takes the newest release and its `halts.json` digest
from GitHub's HTTPS API (`?per_page=100`, no ETag, :80) and checks the body's SHA256 against it (:103-108).

**Modes** (src/AgentBridge.App.Core/Stores.cs:80-106, per machine and profile): `AutoUpdate` (default
on); `WhenUpdateReady` is `RestartToUpdate` (default, #516) or `InstallBetweenTurns` (#689, 6-hour
backstop). Unknown values read as `RestartToUpdate` (:84). The phone only shows the choice (docs/design/ui.md:1977-1991).

**Presses** (docs/protocol/v1-update.md; src/AgentBridge.Host/UpdateService.cs):
- `update.check` and `update.install` come only over the relay's Noise channel, from a `pwa` or `app`
  device, never to a read-only machine. Limits per machine's own host: 3 presses per device per kind per
  10 minutes (`pressed`, :64), 8 waiting (`MaxAsks`, :43).
- Update now (#1159) is `update.install {now: SignedCommand}` through the `SignedPress` gate
  (src/AgentBridge.Host/SignedPress.cs:55-76): a person's device, not the app beside this host, not
  read-only, `remoteManage` on. **The signed command must reach the host within 120 seconds of signing**
  (`ManageProtocol.MaxLifetime`, src/AgentBridge.Protocol/Manage.cs:42; SignedPress.cs:83-89). **The
  work it starts lives 15 minutes** from the same signature (`NowLife`, Update.cs:33).
- A press spends its device's `NowKind` budget before it is decided (UpdateService.cs:407-419), so a
  press answered `confirm` (busier than accepted, :465-468) has spent one.
- A second Update now is refused `busy` only while one is queued, carried or departing (:478, `UnderWay`
  :196-206). `Carrying` excludes `Waiting` (:210): a press waiting for quiet or a usage check is
  **replaced** by a newer one, and `AutoUpdater.Ask` (:510-557) gives the replaced request no result.
- Restart cap: Update now spends its own 1 in 6 hours first, then the 2 in 6 hours shared with
  `manage.restart` (src/AgentBridge.Host/ManageService.cs:29-40).

**After handover.** Checks before handover (checksum, lease, busy compare) keep the old version running.
After it, `Platform.ApplyAndRestart` (Platform.cs:186) starts Update.exe and the old app exits; the new
app's `WatchHostAsync` (AutoUpdater.cs:83-91, HostWatch.cs:11-19) records `failed` if its host cannot
start 3 times or reach the relay in 10 minutes, then looks for a fix every 5 minutes. Recovery is forward only.

**The phone** reads `UpdateState` (`phase`, `progress`, `request`, `last`; Update.cs:175-219) once a
minute, every 10 s while updating; shows "Restarting to update…" for 90 s after an Update now's channel
closes (pwa/js/update.js:27). Each card has Check; the page has Check all; there is no Update all.

## 2. Detection

**Goal.** Every online machine knows of a release within about 3 minutes, with no GitHub API call
added to detection. GitHub stays in two places, both bounded: the 6-hourly revalidation, and the halt
policy read (§4). Both are conditional, but GitHub exempts a 304 from the primary limit only for an
authorized request, and machines call GitHub unauthenticated: a 304 still costs one of the 60 an hour per
IP, shared by every machine behind it. §4 sets the budget; no machine credential is added.

| Option | Verdict |
|---|---|
| A. Poll GitHub faster | Rejected: quota. |
| B. Every machine polls the release relay faster | Fallback only: N × 1/min is waste. |
| C. CI pushes to machines | Rejected: a credential that reaches machines is a new trust path. |
| D. The relay watches the release relay once a minute and sends hosts an untrusted *release hint* | Chosen. One fetch per relay instance, no new service. |
| E. The PWA presses Check when one machine is ahead | Rejected: works only while a PWA is open. |

**The path.**
1. **Release relay.** Freshness 60 s instead of 300 s, with ETag and 304. At that rate its own GitHub
   refreshes need a read-only token as a Worker secret (owner decision; its README's "No secrets" line
   changes with it). Without a token it stays at 300 s.
2. **Relay hint.** The relay reads the release relay's list once a minute, conditionally, and sends
   `release.hint {tag, channel, seenAt}` to each connected host that advertised `releaseHint`: on change
   and on connect. Never shown, never trusted.
3. **Validation, at host and app, before any use or log.** `tag` matches `^v0\.1\.[0-9]{1,19}$` and
   `ReleaseEligibility.TryRun`; `channel` is one of the app's Velopack channels (`win`, `win-arm64`);
   `seenAt` is UTC, under 40 characters, not ahead of now by more than 5 minutes; the frame is under
   256 bytes. Anything else is dropped and counted; the log names the field, never the value.
4. **Host to app** on the existing pipe report. **App:** a valid tag newer than the installed version
   and the last list, with `AutoUpdate` on, starts the **expedited read**:
   - one release-relay read per new tag, ignoring `FreshUntil`, the app's `checksHeldUntil` and its
     cached failure (`CheckAgainAfter`), **for that read only**;
   - **a global bound per machine:** at most one expedited read per 60 seconds whatever the tags.
     Further valid tags inside the minute coalesce into the newest one, read when the minute ends; the
     de-duplication memory is the last 16 tags, oldest dropped. A burst of distinct valid tags costs
     one read a minute, not one each;
   - no GitHub call, early or otherwise; GitHub's backoff (`NotBefore`) is untouched;
   - the 6-hour revalidation and the 24-hour anchor still bind: if GitHub revalidation is due and the
     machine is not inside the `RelayWindow`, the hint does nothing and the machine waits for its normal
     GitHub check. The trust deadline is never removed or extended by a hint;
   - every existing rule applies: `CdnProblem`, the floor, checksums. A tag not in a valid list is no
     `available`. A hostile relay causes at most one release-relay read per tag, and no install.
5. **Fallback.** The 30-minute tick stays for machines and relays without the capability. There is no
   5-minute poll: `ReleaseDiscovery`'s 10-minute `Interval` is unchanged, and the release-relay ETag
   client is new in slice 5 (it needs slice 2's ETag).

**Latency, machine online:** release relay fresh (60 s with token, 300 s without) + hint (≤ 60 s) +
check (seconds) = about 2-3 minutes, or 6-7 without the token. A held machine gets the same, inside its windows.

**Limited or unreachable.** Release relay down: no hints; the normal check as today. GitHub limits the
release relay: it serves stale with `Age`; clients reject a copy older than 1 hour (`CdnOldest`, :55).
GitHub limits a machine: the hold stays; its end goes out as a UTC instant (`checksHeldUntil`, slice 3);
the card says "Checked 20 minutes ago · checking again soon". **Offline:** on reconnect the relay sends
the latest hint. **Mixed versions:** a host without `releaseHint` gets no frame and keeps the tick.

## 3. Per-machine state machine

**Rule.** Every state the phone shows is a fact the host reported, or an inference marked as one. No
progress is animated that was not given. Elapsed time is fine; a fake percentage is not.

| # | State | Source | Entered on |
|---|---|---|---|
| 1 | `current` | host | no `available`, a `checkedAt` |
| 2 | `checking` | host | `phase: checking` |
| 3 | `behind` | host | `available`, not downloaded |
| 4 | `downloading(bytes, total)` | host | `phase: downloading`, new `bytes`/`total` |
| 5 | `ready` | host | new `downloaded: true` (checksum passed) |
| 6 | `waiting(code)` | host | `phase: waiting`, `waitingCode` |
| 7 | `installing(at)` | host | new `installing` instant (old host: `phase: installing`) |
| 8 | `leaving(to, at, backBy)` | host | new `leaving`, in the last state before the host stops |
| 9 | `restarting` | **PWA inference** | channel closed while `leaving` set, before `backBy` |
| 10 | `back(from, to, at)` | host | new `back` with this press's `request`, `current` = `to` |
| 11 | `slow` | **PWA inference** | `backBy` (3 min after `at`) passed, no channel |
| 12 | `didNotComeBack` | **PWA inference** | 10 minutes after `at`, no channel |
| 13 | `failed(code, step, retryAt)` | host | `last` `failed` or `refused`, with code |

**New optional `UpdateState` fields** (slice 3): `installing`, `leaving {to, at, backBy}`, `back {from,
to, request, at}`, `bytes`, `total`, `downloaded`, `mode`, `checksHeldUntil`, `policy` (§4), and
`step` on `last`. An old PWA ignores them and reads `phase` as today; a new PWA facing an old host uses
`phase`, then the 90 s rule, then Offline. `[UnknownAs]` rules stand.

**Creative's four steps** (rule A4): Downloading (host `bytes`/`total`, else `progress`), Installing
(host `installing`, `leaving`), Restarting (**inference**: channel closed after `leaving`), Back online
(host `back`; old host: `current` on reconnect, by inference). Waiting is Creative's waiting line before
Installing, never a step. Health bars read "Not measured while updating" from state 7 to 10.

**Thresholds.** `slow` at 3 minutes: three times a normal restart (`PAUSE_LINE`, update.js:310), and
Creative's card c (Keep waiting; Try again reconnects and sends no press). `didNotComeBack` at 10: the
app's own `HostWatch.Window`, so the phone never declares failure before the machine could. The 90 s
`RESTART_SHOWN_MS` stays only for hosts that send no `leaving`. A host's `last` always replaces an inference.

**Failure is plain and says what next** (Product, point 8). v1 does not keep the old version running
once handover starts; recovery is forward only. Every failure card names the last step reached and one
next action:
| Last step reached | Card says | Next action |
|---|---|---|
| Downloading | "Download failed · trying again in 10 minutes" | Try again now (§5) |
| Installing, before handover | "Not installed: <reason>. Still on <old>." | Try again, or Update when idle |
| Restarting, came back on the old version | "Update did not take. Still on <old>." | Try again |
| Restarting, came back on the new version, host failed | "Updated, but its bridge did not start" | "Open Slipgates at the machine; a fix is looked for every 5 minutes" |
| Did not come back | "Did not come back after 10 minutes" | "At the machine, open Slipgates; if it will not start, install <version> from its release page" |
The release link is built from the validated tag with the fixed prefix
`https://github.com/danswett/agent-bridge/releases/tag/`, never from a host's text. Success stays 10
minutes or until dismissed; a failure until dismissed (Creative, A7-A8).

## 4. Halt policy

**Source.** The authoritative policy and its digest come from GitHub's own HTTPS API, as
`GitHubHaltListSource` does today: the newest non-draft release by run number, its single `halts.json`
asset, the asset's `sha256:` digest from GitHub's metadata, and the body checked against it. The
release relay's list may only tell a machine that something new exists; it never supplies the digest,
never clears a halt, and never hides one.

**Cost.** The read moves into `ReleaseDiscovery`'s file and lock, so it is once per machine, not per
process, and becomes conditional (ETag). Machines are unauthenticated, so a 304 is **not** exempt: it
costs one of the 60 an hour per IP, as does the 6-hourly revalidation. The budget is therefore **at
most one policy read per machine per hour**, and an install decision uses the stored policy while it is
fresh (below) rather than reading again. Ten machines behind one IP spend about 10 of 60 an hour on
policy plus revalidation, leaving the rest for checks. If GitHub refuses (403/429) the read follows
`NotBefore`; the policy then ages to stale after 6 hours and automatic installs and Update all hold,
with the plain message of §2. Nothing relaxes the 6-hour deadline and the relay's list is never used in
its place. Reading with a token would restore the exemption, but a credential on every machine is an
owner decision not taken here; it is a candidate for a later slice.

**Freshness and replay.**
- A policy is **fresh** for 6 hours from GitHub's answer (`GithubRevalidateEvery`). Past that it is
  **stale**, and stale counts as unreadable below.
- **Known halts are kept.** Each halt is stored with the release whose list carried it. A later answer
  removes it only if it comes from GitHub, is fresh, and its newest release is at least as new as that
  one. An answer whose newest release is older (a replay, or a list that omits the newest release)
  removes nothing.
- A policy GitHub did not answer can never clear a halt, whatever the release relay says.

**Availability is observable.** `UpdateState.policy {state: ok | stale | unreadable, at}` (slice 3),
shown on the card's menu as "Bad-release protection: checked 2 hours ago" or "could not be checked".

**What each path does.**
| Path | Halted | Policy stale or unreadable |
|---|---|---|
| Automatic install (any mode, hint or tick) | Never | Held; "waiting to check for bad releases" |
| One machine, a person's press | Refused `halted` | Goes, with the plain warning "Bad-release protection could not be checked on this machine" in the confirm; the signed args carry `unchecked: true`, without which the host refuses `policy-unreadable` |
| Update all | Machine dropped, "halted" | **Refused** (§5 preflight); the signed args carry `batch: true`, and the host refuses `policy-unreadable` itself |

The host enforces both, so a PWA that skips its own check cannot start a batch; a single press can still go with `unchecked` and a warning.

## 5. One press, Update all, and limits

**One machine.** Update now is the card's primary wherever `canUpdateNow`. A busy machine gets
Creative's confirm: **Update now** = `update.install {now}` with `accept`; **Update when idle** =
`update.install`; **Cancel**. Install remains where the app is too old or remote management is off.

**Update all.** A PWA action. No batch signature, no new trust: each machine gets its own
`update.install {now}`, its own request, `target.machine`, `release` and `accept`, signed with the key
that machine's handshake proved (update.js:473), through `SignedPress`.
1. **Pick.** Machines `behind` or `ready` for the newest release they report. Dropped, each a disabled
   row with its reason: read-only, offline, no installed app, too old for Update now (it is updated on its
   own card), halted, a different release, or an availability exclusion (below).
2. **Preflight.** Every picked machine must report `policy: ok`. If any does not, Update all does not
   start: "Bad-release protection could not be checked on <machine>. Untick it, or update it on its own."
3. **Confirm.** Creative's sheet: a row per machine (version change, "2 sessions will pause",
   checkbox), from each machine's `nowSheet` (update.js:314). One confirm covers each row's `accept`.
4. **Waves.** Wave size is a setting, default 2 (Product). The first wave is the single least busy
   machine. A wave ends when each machine is `back`, `failed`, `didNotComeBack`, `superseded`, or was
   sent Update when idle. Before each later wave: re-read each machine; drop one now current; re-run the
   preflight (a machine now not `ok` stops the batch as a failure would); re-confirm one busier than
   accepted; then sign. A signature is made only as its press is sent, so none nears its 120 seconds.
5. **Stop on first failure.** A `failed` or `didNotComeBack` stops new presses: "Stopped: 1 failed.
   Continue / Stop". Pressed machines carry on. Continue resumes with step 4 over the rest; the failed
   machine is not pressed again (Try the failed again does that, as a new press). A `refused` row
   (`busy`, `confirm` declined) and an offline machine do not stop the batch; they end "not updated".
6. **Results.** "Updating 2 of 4 · 1 done"; then "3 updated, 1 failed" with Try the failed again.

**Availability exclusions** (Product: never the last machine of a role, never the machine pressed from).
- **Roles.** A role is an owner-set label per machine (app Settings; the roster's once #55 lands),
  reported as `role` in hello by every new host, an empty string meaning none (slice 3). A machine **with** a role is picked into a wave only
  if another machine with that role is **a protector**: online, reporting no pending, queued, waiting
  or running update (`UpdateState.request` empty, not `updating`), not in this wave, not failed, not
  sent Update when idle by this or another open batch, and not picked by a batch from another device
  that this device can see (read in the pre-wave re-read). A machine sent Update when idle counts as
  departing, so it is never anyone's protector. The contract is as strong as the re-read: a press by
  another device between the re-read and the send can still race, and the host refuses `busy` only for
  its own machine. That residual is stated, not hidden; the failure row names it. A machine whose role
  cannot be read (an old host, a status not yet received) is **not picked**: "update it on its own card".
  A machine reporting no role has none to protect.
- **Origin.** The machine the press comes from is never in a wave. The `app` device knows its own
  machine. A `pwa` device knows it from a per-device "this device is <machine>" fact, set when the app
  opens it, or by the person once in the PWA's Settings. A phone (installed standalone on a mobile OS)
  is no machine. A desktop browser without the fact asks in the confirm sheet "Which machine are you
  on?" (one of the machines, or "none of these"); Update all does not start until it is answered.
- Wave size alone is never treated as this protection.

**A waiting press replaced by another device.** While a machine's press waits (`waiting(code)`, for
quiet or a usage check), another device's Update now is accepted and replaces it (`AutoUpdater.Ask`),
and the replaced request gets no result of its own. The batch watches `UpdateState.request`: if it
changes away from the batch's request before a terminal `last` for it, that row ends **superseded**,
"taken over by a press from another device". It is neither done nor failed, does not stop the batch,
and is never credited with the replacement's outcome; the card shows that press's progress as usual.

**Interrupted batch** (Product: stops when the app closes, then says so). The PWA keeps a **batch
journal** in its own storage, keyed by the signed-in account: batch id, release, wave size, and per
machine `selected | pressed(request) | ended(outcome)`, written before each press is sent and after each
answer. It holds no signature, key or token, and nothing in it can authorise anything. A row is written
`pressed(request)` before the send and `ended(outcome)` only on a received answer or `last`; a pressed
row with neither is **unconfirmed**. On next open the PWA reconciles it by reading each host's current
`request` and `last`: the host's `request` or `last` equals the row's request → the press arrived, the
row takes its state; neither does → it did not arrive (crash before dispatch) or its acknowledgment was
lost, and it is counted as **not updated** and shown as "may not have been sent". Nothing is ever
resent blindly: Update them now is a new Update all with a new preflight, confirm and signature, and
the host's recorded `Again` answer makes a duplicate harmless. A selected machine never pressed is also
counted. If any are not updated or unconfirmed, the Fleet page says "Update all stopped when the app
closed: N machines not updated. Update them now." Update them now is a new Update all over those
machines: new preflight, new confirm, fresh signatures. The banner goes when acted on or dismissed;
a journal older than 7 days is dropped. Other devices see only what the hosts report.

**Idempotence.** Update all is disabled while a batch is open on this device; each press is
single-flight (update.js:401, :428). A resend gets its recorded answer (`Again`, UpdateService.cs:427-436).
A press while another's is carried or queued is refused `busy` ("already updating"); one while
another's waits is the supersession above. A machine already `current` is not picked.

**Refused, and stays refused.** A press from a machine or agent (SignedPress.cs:73), the app beside the
host (:74), a read-only machine (:75), `remoteManage` off, an app too old, a changed release, a halted
release, an unreadable policy for a batch (§4), a command received more than 120 seconds after signing,
work not done within 15 minutes. Owner-only presses wait for the roster (#55).

**Limits a person meets (owner's rule: no allowance, rate-limit message or disabled button in ordinary use).**
- **Check now leaves the card;** Check for updates stays in the card menu and Settings; Check all moves to the page menu.
- **The abuse guards stay:** 3 presses per device per kind per 10 minutes, 8 waiting, per machine.
- **What Update all spends.** One `NowKind` press per machine from this device. A busy confirm that
  is accepted spends a second (the retry is a new signed press), and Try the failed again another, so
  within 10 minutes a machine that already took a press from this device, or two confirms, can reach the
  guard. Then the row is not a refusal message: it shows `retryAt` as a time (below). The guard stays.
- **A Check past the limit** is answered from the machine's last check, as a press within a minute is.
- **A limit is a plain time.** `UpdateCommandResult` gains `retryAt` (UTC) with `rate-limited`: "Not
  updated yet. Try again after 9:12 PM", in the viewer's time. An old host: "Try again in a few minutes".
- **The restart cap is never a disabled button:** at `restart-cap`, Update and Update all send Update when
  idle; the row says "updates when its sessions are quiet". The cap is unchanged.
- **A failed download's Try again now** starts it once at once; the hold governs background retries.
- **GitHub's hold** reads "checking again soon" (§2), never a limit or a time.

## 6. Three modes

**When an update is ready** gets three values (app preferences, `WhenUpdateReady`, Stores.cs:103):
| Label | Value | Behaviour |
|---|---|---|
| Restart to update (default) | `RestartToUpdate` | Downloads, shows `ready`, waits for a press. |
| Install between turns | `InstallBetweenTurns` | As today: first gap, else the 6-hour backstop. |
| Install as soon as available | `AsSoonAsReady` (new) | Installs once `ready`, through #805's lease (waits for typing or delivery in flight, not turns). Creative's pause warning sits under it. |

- The third mode departs as Update now does (`DepartAsync`) with an automatic request and a planned
  announcement, so it is offered only once #815's announcement is built; `SignedPress` grants a `Legacy`
  departure only to a person's press (SignedPress.cs:15-17), and this keeps that.
- **Restart caps.** It has its own 1 automatic restart per machine in 6 hours, and each counts in the
  2-in-6-hours total shared with `manage.restart` and Update now (ManageService.cs:29-30). Past either,
  it waits for the next gap, as Install between turns does.
- Automatic installs in every mode need a fresh, readable halt policy (§4).
- A press wins over any mode. Turning `AutoUpdate` off stops checks, downloads and hints, not presses.
- `UpdateState.mode` (read-only) lets the phone word `ready`.

**Changing the mode from the phone.** `update.mode {mode, now: SignedCommand}`, through the same
`SignedPress` gate, recorded durably and idempotent by request; the host hands it to its app. Apply to
all machines signs one per machine, never a batch. A host too old never offers it. Setting
`AsSoonAsReady` remotely is refused until the mode ships. **Migration:** an older app reads
`AsSoonAsReady` as `RestartToUpdate` (`[UnknownAs]`), the safe direction; no file format changes.

## 7. Product and Creative suggestions (one line each)
- Version on each card, "N machines behind" with Update all in the Fleet header, and a warning when a host and its client differ (#586).
- Halt policy wired before Update all and before any hint-driven install (§4, slice 8).
- No rollback in v1; failure says what happened and what next (§3). Health-gated recovery is §10.
- Sessions survive: the third mode waits for the planned announcement (#954, #498).
- In-app only; no new push types. The PWA's own version beside the fleet's (#1246).
- Release notes: one plain line before Update, from the release body, else What's new opens the release page.
- Fix first: #1740, #1920 and #1173, as Product asks.
- **Later:** hold a machine on a version (after the core flow); mixed-version warning on Move to
  machine (needs a compatibility rule). Quiet hours: not in scope (Product).
- Active role colour is Creative's token; contrast is checked on real WebKit and forced colours.

## 8. Failure modes and their tests

| Failure | Behaviour | Test (file) |
|---|---|---|
| Release relay unreachable | No hints; the normal check. | ReleaseDiscoveryTests: a hint with the relay down asks nothing else. |
| GitHub limits the release relay | Stale with `Age`; over 1 hour rejected. | worker.test.mjs: ETag/304, stale-with-Age. |
| GitHub holds a machine | Hint still read from the release relay once, no GitHub call. | AutoUpdaterTests: a machine held 2 hours gets a valid new release from a hint within one tick, and GitHub's fake sees no request. |
| Hint past the windows | Revalidation due outside `RelayWindow`: hint does nothing. | ReleaseDiscoveryTests: 25 hours after GitHub's last answer a hint leaves the release unknown. |
| Forged or spammed hint | At most one release-relay read a minute per machine; no install; bad fields dropped. | ReleaseDiscoveryTests: 100 hints → 1 read; a burst of 50 distinct increasing valid tags → 1 read per minute, newest coalesced, memory ≤ 16; tag not in a valid list → no `available`. HostTests: `v0.1.1x`, an unknown channel, 300 bytes → dropped, not logged. |
| Halt hidden by the relay | Policy from GitHub only; relay list cannot clear. | HostTests: a relay list omitting the newest halt-bearing release leaves the halt; a GitHub answer whose newest release is older keeps it (replay). |
| Halt policy stale or unreadable | Automatic install held; one press with warning; Update all refused. | AutoUpdaterTests: policy 7 hours old → no automatic install. UpdateServiceTests: `batch` press on `unreadable` → refused `policy-unreadable`; one press without `unchecked` → refused. update.test.mjs: preflight with one unreadable machine sends no press at all. |
| GitHub 304s cost quota | Policy read ≤ 1 per machine per hour; refusal follows `NotBefore`; stale after 6 hours holds installs. | ReleaseDiscoveryTests: 10 machines' reads in 24 hours stay under 60 an hour; a 403 backs off and does not read the relay's list as policy. |
| Interrupted send | Unconfirmed press counted "may not have been sent"; never resent blindly. | update.test.mjs: close after `pressed` before dispatch → banner counts it; lost acknowledgment with host `last` matching → row ends from the host. |
| Protector departing | Idle-queued or batched machine is not a protector. | update.test.mjs: A sent Update when idle, B slow → B not picked; two concurrent batches never pick both of a role's machines in their re-reads. |
| Policy fails between waves | Batch stops before the next wave. | update.test.mjs: wave 2's machine turns `stale` → nothing signed for wave 2. |
| Bad hash | Fails its feed's SHA256 → `failed`, still on old. | Existing package-mismatch tests, plus one for a hint-driven download. |
| Machine does not return | `restarting` → `slow` (3) → `didNotComeBack` (10); `last` replaces. | update.test.mjs, fake clock: each step at its time; each row of §3's failure table names its step and action. |
| Update all over many machines | One press per machine; prior presses and confirms counted. | update.test.mjs: 12 fresh machines → 12 presses, none refused. UpdateServiceTests: a machine with 2 earlier presses from the device plus 1 confirm refuses the batch press with `retryAt`, worded as a time. |
| Second device during waiting | Replacement accepted; batch row `superseded`. | AutoUpdaterTests + update.test.mjs: for both waiting-for-quiet and waiting-for-usage-check, a second device's press replaces the batch's; the row ends superseded, is not credited with the replacement's `back`, and the batch goes on. |
| Simultaneous presses | Same request: recorded answer; second while carried: `busy`. | UpdateNowTests: parallel second press refused. update.test.mjs: double click sends one per machine. |
| Availability exclusions | Last of a role and the origin machine never picked; unknown role not picked. | update.test.mjs: two machines of role A, wave size 2 → one per wave; an old host without `role` → not picked; a desktop browser with no origin fact → sheet asks, nothing sent. |
| Offline or cannot sign | Row "not updated"; batch goes on. | update.test.mjs: a machine offline at its wave and one whose signing throws → neither stops the batch. |
| App closes mid-batch | Pressed carry on; banner on next open. | update.test.mjs: close after wave 1, reopen → "Update all stopped when the app closed: 2 machines not updated"; Update them now re-runs preflight and signs fresh; the journal holds no signature. |
| Update all fails midway | Stopped; Continue re-reads and signs the rest. | update.test.mjs: a failure in wave 1 → wave 2 never signed. |
| Host/PWA version skew | New fields optional. | Protocol tests: an old `UpdateState` round-trips; no `leaving` → 90 s path. |
| Remote mode change | Signed per machine. | UpdateServiceTests: unsigned `update.mode` refused. update.test.mjs: Apply to all signs one per machine. |

## 9. Slices

Each slice is 400 lines or fewer and starts with its tests. **2F** rows touch the update, install or
signing path, or the protocol, and need two families, both reading security-subject docs in full.

| # | Slice | Depends on | Review |
|---|---|---|---|
| 1 | Drop the local-time strings (AutoUpdater.cs:1170, :429; ObservedUpdater.cs:107); the card says "checking again soon". No wire change. | — | 1F |
| 2 | Release relay: ETag + 304; 60 s with a token secret (owner decision), else 300 s; README's "No secrets" line. | — | 1F + SRE |
| 3 | Protocol: `release.hint` with its validation, `releaseHint` capability, `role` in hello, `UpdateState` fields (§3, incl. `checksHeldUntil`, `policy`), `step` on `last`, `retryAt`, `batch` and `unchecked` signed args. v1-update.md. | — | **2F** |
| 4 | Relay: conditional release-relay poll once a minute; hints to capable hosts. | 3 | **2F** |
| 5 | Host and app: hint over the pipe; expedited read (§2.4) past the app's hold, within the windows; release-relay ETag client. The 10-minute `Interval` stays. | 2, 3, **8 accepted** | **2F** |
| 6 | Host and app: `bytes`/`total`/`downloaded`, `installing`, `leaving` with `backBy`, `back`, `step`. | 3 | **2F** |
| 7a | PWA card: the four steps and states of §3, thresholds, the failure table. | 6 | 1F |
| 7b | PWA fleet: version per card, "N machines behind", host/client mismatch warning, Check moves, release-notes line. | 6 | 1F |
| 8 | Halt policy (§4): GitHub source in `ReleaseDiscovery`'s file, conditional; freshness; known halts kept; `policy` state; enforcement of `batch` and `unchecked`; the `halted` refusal and the automatic-install hold. | 3 | **2F** |
| 9a | PWA Update all core: pick, preflight, confirm, waves with role and origin exclusions, signing per wave. | 7a, 8 | **2F** |
| 9b | PWA Update all run: stop/Continue, results, supersession, offline rows, batch journal and reopen banner. | 9a | **2F** |
| 10 | Limits never met: Check from the last check; `retryAt` as a time; cap → Update when idle; Try again now. | 3 | **2F** |
| 11 | Third mode `AsSoonAsReady`: enum, Settings, ui.md, the automatic departure, its caps. | #815, 6, **8 accepted** | **2F** |
| 12 | Signed mode change `update.mode`; PWA three-choice list and Apply to all machines. | 11 | **2F** |
| 13 | Later, owner-gated: fleet coordinator (§10.1). | owner decision on fleet #22 | **2F** |
| 14 | Later: health-gated recovery (§10.2). | its own design; after 8 and 9b | **2F** |

Slices 5 and 11 come after slice 8 is accepted: no hint-driven or unattended install before the halt
policy reads. Slices 1, 2, 3 and 8 can start at once; 10 needs only 3. Slices 7 and 9 were split
because each ran past 400 lines.

## 10. Later designs (pointers, not v1)

**10.1 Fleet coordinator (owner-gated, fleet #22).** Product wants a run that outlives the phone, but
not at the cost of security, so v1 stops. Today a phone cannot sign later waves in advance: a signed
command must reach the host within 120 seconds, and the work it starts lives 15 minutes. A coordinator
therefore needs delegated signing. **Recommended owner answer:** a person signs one bounded *batch
grant* naming the exact machines, release, wave size, each machine's `accept`, and an expiry of at most
1 hour, with presence (WebAuthn where the device has it); it is carried by one of the owner's own hosts,
never the relay; each host verifies the grant against the roster (#55) itself, acts on it at most once,
only for that release, and still applies §4 and every refusal above. No code until the owner decides.

**10.2 Health-gated recovery.** v1 does not give "the old version keeps running until the new one is
proven" (Product confirmed). That needs Update.exe to keep the old install, a start-and-health gate on
the new host, and a way back to the old one if the gate fails: its own design, after the core flow.

## 11. Open questions
1. **Owner.** A read-only GitHub token as a Worker secret for the release relay (60 s instead of 300 s)?
2. **Owner.** The coordinator's delegated signing (§10.1) on fleet #22.
3. **Creative.** The Active role's colour token, and owner sign-off on the board at 2bc31243.
