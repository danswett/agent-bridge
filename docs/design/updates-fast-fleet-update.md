# Fast updates and Update all (#2225)

Status: design for review. Engineering owns it; Product ranked the epic 4 (P1) on #2225.
Scope: how a machine learns of a release within minutes, one press (or one Update all) that takes
every machine across, progress that is only ever the machine's own word, and three update modes.
Not in scope (parked by Product): scheduling and per-machine channels.

Citations are to `main` at 0e17432. "App" is the Windows app (Slipgates), "host" its bridge.

## 1. Where things stand

**Who checks, and how often.**
- The app checks on start and every 30 minutes (`CheckEvery`, src/AgentBridge.App.Core/AutoUpdater.cs:72),
  every 5 minutes after a host failed an update (`CheckAfterFailedHost`, :75). The tick decides at :806-817.
- Every check on a machine goes through one shared cache, `ReleaseDiscovery`
  (src/AgentBridge.Host.Core/Updates/ReleaseDiscovery.cs:9-41). It asks the list at most once per
  `Interval` of 10 minutes plus up to 2 minutes of jitter (:63-64), whichever process or profile asks.
- The list comes first from the release relay at `releases.slipgates.com` (`ReadCdnFirst`, :47; `CdnList`, :51),
  and from GitHub's API (60 an hour per IP, unauthenticated, :10-11) when the relay is unusable, or every
  6 hours anyway (`GithubRevalidateEvery`, :57).
- The relay is a Cloudflare Worker (infra/cloudflare/release-feed/worker.js) that caches GitHub's list
  for 5 minutes (`FRESH_SECONDS`, :7) and serves a stale copy up to 24 hours with its real `Age` (:8, :88).
  It sends no ETag and has no 304. It serves the list only; packages and feeds still come from GitHub (:2-3).
- Hosts check too, for Home Assistant's update entity, through the same cache
  (src/AgentBridge.Host/HostUpdates.cs:27, :88-96).

**Worst case today** from publish to a machine knowing: 5 minutes (relay) + 12 minutes (cache) + 30 minutes
(app tick) = about 47 minutes. Under a GitHub refusal the app holds checks, doubling up to 2 hours
(`CheckBackoffAtMost`, AutoUpdater.cs:146; `HoldChecks`, :1159), and says "checking again after
{until.ToLocalTime():t}" (:1170). That is the app machine's local clock time, shown as-is on a phone in
another zone.

**Trust today.** It is set up in three places, and this design keeps all three as they are.
1. Publish. CI publishes a draft, downloads it back, checks every PE file against the pinned
   publisher's Authenticode EKU and each feed's hashes against its packages, and only then makes it
   public (build/publish-prerelease.ps1:19-23, :349-388; build/app-signing.ps1:1-11).
2. Discovery. A list from the relay is used only if every asset is under this repository's own
   download URL, nothing is a draft, and the channel's newest is no older than GitHub's last list or
   what is installed (`CdnProblem`, ReleaseDiscovery.cs:373-401). The relay is untrusted transport (:30-31).
3. Install. Feeds and packages come from GitHub download URLs. The package must match its feed's
   SHA256, checked again in the copy Update.exe opens (src/AgentBridge.App/Platform.cs:95-97, :166-170, #984).
   The client does not check Authenticode itself.

**Halt list.** `ReleaseEligibility` and `GitHubHaltListSource` exist
(src/AgentBridge.Host/ReleaseEligibility.cs:19-60, :77-80), but nothing in `src/` constructs them: no
updater consults `halts.json` yet. The source reads `api.github.com?per_page=100` (:80), which is quota.

**Modes today** (src/AgentBridge.App.Core/Stores.cs:80-106, app preferences, per machine and profile):
- `AutoUpdate` (default on): check and download in the background.
- `WhenUpdateReady`: `RestartToUpdate` (default, waits for a press, #516) or `InstallBetweenTurns`
  (installs at the first gap with no turn running and no question waiting, or at the 6-hour backstop,
  #689; AutoUpdater.cs:437, `WaitAtMost` :137). Unknown values read as `RestartToUpdate` (Stores.cs:84).
- An app from before #516 still installs by itself after 6 hours (docs/protocol/v1-update.md:39-43).

**Presses today** (docs/protocol/v1-update.md; src/AgentBridge.Host/UpdateService.cs):
- `update.check` and `update.install {request}` go only through the relay's Noise channel, only from a
  `pwa` or `app` device, and never to a read-only machine (UpdateService.cs:8-21, :289-325).
- Limits: 3 presses of each kind per device per 10 minutes, at most 8 waiting, and the same request is
  the same press (:42-48, :326-345; v1-update.md:47-56).
- Install waits until no session has been busy for 2 minutes (`IdleFor`, AutoUpdater.cs:96).
- Update now (#1159) is `update.install {now: SignedCommand}`. It passes the same signed-command gate as
  `manage.*` (src/AgentBridge.Host/SignedPress.cs:7-18, :55-73): the MAC verified for this machine, the
  action `update.install.now`, the release and the request; the signer must be the sending device, a
  person's device connected now, and not the app beside this host. It needs `permissions.remoteManage`
  (UpdateService.cs:185, :652). It is recorded durably in `update-now.json` before the app is asked
  (src/AgentBridge.Host/UpdateNow.cs:210-216; UpdateService.cs:385-421). It lives 15 minutes
  (src/AgentBridge.Protocol/Update.cs:33). A second Update now while one is under way is refused (UpdateService.cs:478).
- Leaving takes #805's lease, compares busy with what the person accepted, then announces (UpdateService.cs:498-640).
  #815's planned announcement is not built, so it leaves on a `Legacy` receipt (:635-637).

**What the phone sees.**
- `UpdateState` has `phase` idle, checking, downloading, waiting or installing, and `progress` 0-100
  (Update.cs:153, :175-219). The progress is real bytes from Velopack's callback
  (src/AgentBridge.App.Core/ObservedUpdater.cs:59, :93-114).
- The PWA reads `update.get` once a minute, or every 10 s while an update is under way (v1-update.md:939-941).
- The PWA shows "Restarting to update…" for 90 s after the channel closes on an Update now, then
  plain Offline (pwa/js/update.js:25-27, :497-519).
- The first start after an update reports `Last` updated or failed, with the request
  (`Record`, `Announce`, AutoUpdater.cs:1482, :1515).
- The app watches the new host for 10 minutes and records `failed` if it cannot start 3 times or never
  reaches a relay that answers (src/AgentBridge.App.Core/HostWatch.cs:11-19). Nothing tells the phone
  "did not come back".
- The PWA has Check all (pwa/js/update.js:529-531; pwa/js/ui/views.js:1400) but no Update all.

## 2. Detection

**Goal.** Every online machine knows of a published release within about 3 minutes, without any
machine using GitHub quota.

**Options considered.**
| Option | Verdict |
|---|---|
| A. Poll GitHub's API faster | Rejected: quota, and the brief forbids it. |
| B. Poll the release relay faster from every machine | Part of the answer: it uses no GitHub quota. But N machines × 1 a minute is wasted traffic, and the relay itself stays 5 minutes stale. |
| C. CI pushes to every machine | Rejected: CI would need a credential that reaches machines, which is a new trust path. |
| D. The relay (the existing always-on service) watches the release relay once a minute and sends connected hosts an untrusted *release hint* | Chosen. One fetch per relay instance, no new service, no new secret. |
| E. The PWA notices one machine is ahead and presses Check on the others | Kept as a fallback only: it works only while a PWA is open. |

**The chosen path (D, backed by B).**
1. **Release relay.** Its freshness drops from 300 s to 60 s, and it answers with an ETag and a 304, so
   a conditional poll costs nothing. To keep its own GitHub refreshes safe at that rate, it uses a
   read-only GitHub token as a Worker secret (the README already names this owner decision). Without a
   token it stays at 300 s.
2. **Relay hint.** The relay asks the release relay's list once a minute, conditionally. It reads the
   newest tag per channel, does not validate it, and sends `release.hint {tag, channel, seenAt}` to every
   connected host that advertised the `releaseHint` capability in hello. It sends it when the tag changes,
   and on every connect. The hint is never shown and never trusted.
3. **Host to app.** The host passes the hint to its app on the existing pipe report (`HostStatus`).
4. **App.** If the hinted tag is newer than both the installed version and the last list, and
   `AutoUpdate` is on, the app runs its normal check now. Nothing else changes.
   - The check calls `ReleaseDiscovery` with a hint. When the cached list lacks the hinted tag, it asks the
     release relay now, ignoring `FreshUntil`. It never asks GitHub early, which keeps its own schedule.
   - Every existing rule applies: `CdnProblem`, the floor, checksums.
   - At most one hint-driven check per tag, and at most one per `CheckAgainAfter` (1 minute,
     AutoUpdater.cs:113). A hostile relay can therefore cause at most one release-relay fetch per minute
     per machine, and no install.
5. **Fallback poll (B).** The background tick stays 30 minutes for machines and relays without the
   capability. A capable host that has had no hint for 10 minutes falls back to a conditional poll of the
   release relay every 5 minutes.

**Worst-case latency (machine online).**
| Step | With token | Without |
|---|---|---|
| GitHub publishes → release relay fresh | 60 s | 300 s |
| Release relay → relay hint | 60 s | 60 s |
| Hint → app check → `available` | a few seconds | a few seconds |
| Total to `available` | about 2-3 min | about 6-7 min |

The download then follows; it is shown in bytes.

**When the feed is limited or unreachable.**
- **Release relay down.** No hints arrive. Machines fall back to their poll, and then to GitHub as
  today (ReleaseDiscovery.cs:146-181). The phone says "release relay unreachable; checking GitHub", with
  the time as a duration.
- **GitHub limits the release relay.** It serves stale with its `Age` (worker.js:88). Clients reject a copy
  older than 1 hour (`CdnOldest`, ReleaseDiscovery.cs:55), so a stale copy never says "up to date". The
  hint simply stops advancing.
- **GitHub limits a machine.** Unchanged (`Held`, :569). The hold's end is sent as a UTC instant
  (`checksHeldUntil`, a new field) and worded relative by the reader ("in 12 minutes"). The local
  clock-time string at AutoUpdater.cs:1170 is removed from the state, which fixes the zone bug.

**Offline machines.** On reconnect the relay sends the latest hint at once, and the app checks within
seconds of coming online. A machine whose app is not running reports `installer: false` as today.

**Mixed versions.**
- A host without the `releaseHint` capability is never sent the frame. Its app keeps the 30-minute tick.
- A relay without the frame sends nothing, and capable hosts fall back as above.
- An app from before #516 is unchanged (self-installs after 6 hours). The Fleet header counts it as
  "behind", not as failed.

## 3. Per-machine state machine

**Rule.** Every state the phone shows is either a fact the host reported (in `UpdateState` or `Last`) or
an inference marked as one. The phone never animates progress it was not given. Elapsed time is fine;
a fake percentage is not.

**Ordered states.**
| # | State | Source | Entered on (event) | Left on |
|---|---|---|---|---|
| 1 | `current` | host | `UpdateState` with no `available` and a `checkedAt` | hint, check |
| 2 | `checking` | host | `phase: checking` | the check ends |
| 3 | `behind` | host | `available` set, not downloaded | download starts |
| 4 | `downloading(bytes, total)` | host | `phase: downloading`, `progress`, plus new `bytes`/`total` | done or failed |
| 5 | `ready` | host | new `downloaded: true` (checksum passed, ObservedUpdater `Ready`) | press or mode |
| 6 | `waiting(code)` | host | `phase: waiting`, `waitingCode` (quiet, turn, lease, cap) | goes or refused |
| 7 | `leaving(to, at, backBy)` | host | new `leaving` record, sent in the last `update.state` before the host stops | channel closes |
| 8 | `restarting` | **PWA inference** | channel closed while `leaving` was set, before `backBy` | host returns, or `backBy` |
| 9 | `back(version)` | host | the new host's first state: `current` = `to`, and `Last` updated with the same `request` | — |
| 10 | `slow` | **PWA inference** | `backBy` passed, under 10 minutes since `at` | host returns, or 10 min |
| 11 | `didNotComeBack` | **PWA inference** | 10 minutes since `at` (`HostWatch.Window`) with no channel | host returns |
| 12 | `failed(reason, next)` | host | `Last` outcome `failed` or `refused`, with code | next press |

**Details.**
- A failed download is state 12 with `retryAt` from `ObservedUpdater` (10 minutes, doubling to 4 hours;
  ObservedUpdater.cs:32-33). It says when it retries; it does not loop.
- `backBy` is set by the host as `at` + 5 minutes. A normal restart takes under a minute
  (pwa/js/update.js:310).
- If the host returns in state 9 on the **old** version, its `Last` says `failed`, and the PWA shows state 12.
  It never shows state 9 then.
- State 11's words come from the client's own table, for example "Did not come back. At the machine,
  open Slipgates; if it will not start, install <version> from its release page"
  (ops/runbooks/windows-release.md, Mitigate 3).
- If the host later returns and reports `Last`, that replaces the inference.
- The 90 s `RESTART_SHOWN_MS` is replaced by `backBy`, which comes from the host. A host too old to send
  `leaving` keeps today's 90 s, then Offline.

**Events (wire).**
- `update.state`: the same event, with new optional fields `bytes`, `total`, `downloaded`, `leaving`,
  `checksHeldUntil` and `mode`. A client that does not know a field ignores it; `[UnknownAs]` rules stand.
- `release.hint`: relay to host only.
- Creative gets the twelve states, their sources and the one-line words. Visuals are theirs.

## 4. One press and Update all

**One machine.**
- Install and Update now stay as they are.
- Update now becomes the one-press default wherever `canUpdateNow` is true. Install remains for
  machines whose app is too old (`OldApp`) or where remote management is off.

**Update all.** A PWA action over the machines shown. It adds no new op, no new signature and no new
trust.
1. **Pick.** The PWA picks the machines that are `behind` or `ready` for the newest release they report.
   It drops read-only machines, machines without an installed app, machines with `canUpdateNow` false,
   and machines whose release differs from the newest. Each dropped machine is listed with its reason.
2. **Confirm.** One sheet lists every picked machine with its busy summary (`nowSheet` per machine,
   update.js:314) and warnings: mid-turn, question waiting, approval waiting, can't tell, and "this is the
   machine you are using". One confirm covers each machine's `accept` exactly as shown. If one machine
   answers `confirm` (busier now), it asks again for that machine only; the others go on.
3. **Waves.** One machine first (the least busy), then two at a time. A wave starts when the previous one
   reaches `back` or a `failed` it must not continue past. It stops at the first `failed` or
   `didNotComeBack`, and says which machines it did not press.
   - Each press is signed when its wave starts, not up front, so no press lapses (15 minutes,
     Update.cs:33) while it waits for earlier waves.
   - Product's "keep one per role" needs roles on the roster (#55). Until then, the waves stand in for it.
4. **Sign.** Each machine gets its own `update.install {now}`: its own request UUID, `target.machine`,
   `release` and `accept`, signed with the key that machine's handshake proved (update.js:473). It
   goes through the same `SignedPress` gate. Nothing is batch-signed.
5. **Results.** A row per machine: accepted, refused with code (worded by `notUpdated`), confirm, no
   answer, then the state machine above, ending in `back on <version>` or `failed`/`didNotComeBack` with
   its next step. The header gives counts: "3 back on v0.1.912, 1 did not come back, 2 not pressed".

**Idempotence.**
- **The same device presses twice.** Update all is disabled while a run is open, and each machine's press
  is already single-flight (update.js:401, :428).
- **A resend of the same press.** It gets the recorded answer (`Again`, UpdateService.cs:424-436).
- **Another device presses Update all.** Each machine refuses with "another Update now is already under
  way" (:478), and its row reads "already updating".
- **Again after it succeeded.** The machine is `current` and is not picked. A later restart within 6 hours
  is refused `restart-cap` (v1-update.md:291) and shown with its time.

**Refused, and stays refused.**
- A press from a machine or agent rather than a person's device (SignedPress.cs:73).
- The app beside the host (:74).
- A read-only machine (:75).
- `remoteManage` off.
- An app too old for Update now.
- A changed release (UpdateService.cs:459-462).
- The restart cap.
- A halted release (new, §7).
- A press older than 15 minutes.

Owner-only presses wait for the roster (#55). Today "a person's device on the account" is the rule
(SignedPress.cs:21-22).

**If the PWA closes mid-run.** Machines already pressed carry on, since each press is self-contained.
Machines not yet pressed stay unpressed. The next open shows what each machine reports; there is no
host-side fleet coordinator (see §9).

## 5. Three modes

The setting **When an update is ready** gets three values. It is stored where it is today: app
preferences, `WhenUpdateReady` (Stores.cs:103), per machine and profile.

| Mode | Value | Who enforces | Behaviour |
|---|---|---|---|
| Ask me | `RestartToUpdate` (default) | app | Downloads, shows `ready`, waits for a press (local or remote). |
| Between turns | `InstallBetweenTurns` | app | As today: first gap with no turn running and no question waiting, else the 6-hour backstop. |
| As soon as ready | `AsSoonAsReady` (new) | app, with the host's lease | Installs once `ready`. Takes #805's lease (purpose Update), which waits only for typing or delivery in flight. Does not wait for turns. |

**How the third mode goes.** It uses the departure Update now uses (`DepartAsync`) with an automatic
request. The host announces it planned (#498/#815), so sessions resume after.
- It is offered only once #815's planned announcement is built. Until then the app does not show it.
  `SignedPress` deliberately grants a `Legacy` departure only to a person's press (SignedPress.cs:15-17),
  and this keeps that.
- The 1-per-6-hours cap applies to it as well.

**A press wins over any mode.**
- Update now goes when confirmed.
- Install waits for quiet.
- A mode governs only installs nobody pressed.
- Turning `AutoUpdate` off stops checks, downloads and hint-driven checks, but not presses.

**The phone sees the mode.** `UpdateState.mode` (new, read-only) lets it word `ready` correctly ("installs
at the next gap", "waiting for your press"). Changing the mode remotely is not in this design: it would
need a new signed op (§9).

**Migration.**
- Existing values keep their meaning; unset still means `DefaultWhenReady`.
- An older app that reads `AsSoonAsReady` treats it as `RestartToUpdate` (`[UnknownAs]`, Stores.cs:84),
  which is the safe direction.
- No file format changes.

## 6. Product suggestions (one line each)
- Version on each card and an "N behind" Fleet header, counted from host-reported `current` (#586).
- Mixed versions: the header shows each version in use; capability gates keep old machines working (§2).
- Halt: wire the halt list (§7, slice 8) before Update all ships; rollback stays "a newer build" (#568, #580).
- Failed update: retry the download once at its hold; keep the old version until `back` is proven (#581, #1173).
- Staggered Update all: one, then two at a time, stop on the first failure (§4).
- Sessions survive: the third mode waits for the planned announcement (#954, #498).
- Release notes: one line from the release's notes on `behind` and `ready`, linking the release page.
- Show the PWA's own version beside the fleet's (#1246).
- Owner-only press: needs the roster (#55); signed per machine today.
- Fix first: #1740, #1920 and #1173, as Product asks. §2's relative hold time covers the zone part.

## 7. Failure modes and their tests

| Failure | Behaviour | Test (file) |
|---|---|---|
| Release relay unreachable | No hints. Fall back to the 5-minute poll, then GitHub as today. The phone says so with a duration. | ReleaseDiscoveryTests: hint with relay down asks nothing early. AutoUpdaterTests: fallback poll after 10 min without hints. |
| GitHub limits the release relay | Stale copy with `Age`; over 1 hour rejected; the hint stops advancing. | worker.test.mjs: ETag/304, stale-with-Age. ReleaseDiscoveryTests: an old copy is not "up to date". |
| GitHub limits a machine | Hold as today; `checksHeldUntil` in UTC; relative words. | AutoUpdaterTests: state has the instant, no local-time string. update.test.mjs: words in another zone. |
| Forged or spammed hint | At most one release-relay fetch per tag and per minute; no install; `CdnProblem` unchanged. | ReleaseDiscoveryTests: 100 hints → 1 fetch; a hint for a tag not in a valid list → no `available`. |
| Bad signature or hash | The package fails its feed's SHA256 → `failed`, old version kept (Platform.cs:166-170). Publish refuses an unsigned draft. | Existing package-mismatch tests stay green; one added for a hint-driven download. |
| Partial download | `ObservedUpdater` hold (10 min, doubling to 4 h); state 12 with `retryAt`; never `ready`. | AutoUpdaterTests: a cut-off download never sets `downloaded`. |
| Machine does not return | `restarting` → `slow` at `backBy` → `didNotComeBack` at 10 min; replaced by the host's `Last` when it returns. | update.test.mjs with a fake clock: each step; the old version back shows `failed`. |
| Two presses / two Update alls | Same request: recorded answer. A second Update now: refused busy. Same device: button disabled. | UpdateNowTests: a parallel second press is refused. update.test.mjs: double click sends one per machine. |
| Update during a running turn | Ask me and Between turns never go over a turn. Update now goes only within `accept`, else `confirm`. As soon as ready waits for the planned announcement. | UpdateFlowTests: busier than accepted → confirm. AutoUpdaterTests: As soon as ready hidden without #815. |
| Host/PWA version skew | New fields optional. An old PWA reads 5 phases. An old host gets no hint and keeps the 90 s rule. Unknown enums go safe. | Protocol tests: an old `UpdateState` round-trips. update.test.mjs: no `leaving` → today's 90 s path. |
| Halted release | Not offered and not installed. Halt list read from the same list (no API quota). Missing list: hold an automatic install, allow a press with a warning. | Host.Tests: `ReleaseEligibility` wired; a halted tag is never `ready`. |
| Update all stops midway | Rows say "not pressed"; pressed machines carry on. | update.test.mjs: a failure in wave 1 → wave 2 never signed. |

## 8. Slices

Each slice is 400 lines or fewer. Rows marked **2F** touch the update, install or signing path, or the
relay protocol, and need two families. Security-subject docs are read in full by both.

| # | Slice | Depends on | Review |
|---|---|---|---|
| 1 | `checksHeldUntil` as UTC in `UpdateState`; drop the local-time string; PWA and app word it relative. | — | 1F |
| 2 | Release relay: ETag + 304; freshness 60 s when a token secret is set (owner decision), else 300 s. Tests. | — | 1F + SRE |
| 3 | Protocol: `release.hint` frame, `releaseHint` hello capability, new optional `UpdateState` fields (`bytes`, `total`, `downloaded`, `leaving`, `mode`). Docs in v1-update.md. | — | **2F** |
| 4 | Relay: a conditional poll of the release relay once a minute; send the hint on change and on connect, only to capable hosts. | 3 | **2F** |
| 5 | Host and app: pass the hint over the pipe; `ReleaseDiscovery` hint path (release relay only, once per tag, 1/min); fallback 5-minute poll. | 3 | **2F** |
| 6 | Host and app: report `bytes`/`total`/`downloaded`; send `leaving` with `backBy` before stopping; `Last` on return. | 3 | **2F** |
| 7 | PWA: the state machine of §3, Fleet "N behind" header, version per card, release-notes line. | 6 | 1F |
| 8 | Halt list wired into the app's install path, reading `halts.json` via the shared list (no API quota). | — | **2F** |
| 9 | PWA Update all: pick, one confirmation, per-machine signing in waves, results rows, stop on failure. | 7, 8 | **2F** |
| 10 | Third mode `AsSoonAsReady`: enum value, Settings, the automatic departure through the lease. | #815 built, 6 | **2F** |

Slices 1, 2, 3 and 8 can start at once.

## 9. Open questions
1. **Owner.** Give the release relay a read-only GitHub token as a Worker secret (60 s instead of 300 s)?
2. **Product.** Should Update all go on if the PWA closes? Doing so needs a host-side coordinator, which
   is new always-on state. This design stops instead.
3. **Product.** When the halt list cannot be read, should a person's press still go, with a warning? This
   design says yes for a press and no for an automatic install.
4. **Engineering.** Changing a machine's mode from the phone needs a new signed op. Is it in this epic, or later?
