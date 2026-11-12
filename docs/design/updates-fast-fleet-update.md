# Fast updates and Update all (#2225)

Status: design for review, revision 2. Engineering owns it; Product ranked the epic 4 (P1) on #2225.
Scope: how a machine learns of a release within minutes, one press (or one Update all) that takes
every machine across, progress that is only ever the machine's own word, and three update modes.
Not in scope (parked by Product): scheduling and per-machine channels.

Revision 2 follows Engineering's brief (#2225, comment 6815111084) on Creative's receipt (comment
6815094187; board in the studio, PR 30 at 2bc31243). It changes §3, §4, §5, §6, §8 and §9.

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
- The release relay is a Cloudflare Worker (infra/cloudflare/release-feed/worker.js) that caches GitHub's
  list for 5 minutes (`FRESH_SECONDS`, :7) and serves a stale copy up to 24 hours with its real `Age`
  (:8, :88). It sends no ETag and has no 304. It serves the list only; packages come from GitHub (:2-3).
- Hosts check too, for Home Assistant's update entity, through the same cache
  (src/AgentBridge.Host/HostUpdates.cs:27, :88-96).

**Worst case today** from publish to a machine knowing: 5 minutes (relay) + 12 minutes (cache) + 30 minutes
(app tick) = about 47 minutes. Under a GitHub refusal the app holds checks, doubling up to 2 hours
(`CheckBackoffAtMost`, AutoUpdater.cs:146; `HoldChecks`, :1159), and says "checking again after
{until.ToLocalTime():t}" (:1170): the app machine's clock time, shown as-is on a phone in another zone.

**Trust today.** It is set up in three places, and this design keeps all three as they are.
1. Publish. CI publishes a draft, downloads it back, checks every PE file against the pinned
   publisher's Authenticode EKU and each feed's hashes against its packages, and only then makes it
   public (build/publish-prerelease.ps1:19-23, :349-388; build/app-signing.ps1:1-11).
2. Discovery. A list from the relay is used only if every asset is under this repository's own
   download URL, nothing is a draft, and the channel's newest is no older than GitHub's last list or
   what is installed (`CdnProblem`, ReleaseDiscovery.cs:373-401). The relay is untrusted transport (:30-31).
3. Install. The package must match its feed's SHA256, checked again in the copy Update.exe opens
   (src/AgentBridge.App/Platform.cs:95-97, :166-170, #984). The client does not check Authenticode itself.

**Halt list.** `ReleaseEligibility` and `GitHubHaltListSource` exist
(src/AgentBridge.Host/ReleaseEligibility.cs:19-60, :77-80), but nothing in `src/` constructs them: no
updater consults `halts.json` yet. The source reads `api.github.com?per_page=100` (:80), which is quota.

**Modes today** (src/AgentBridge.App.Core/Stores.cs:80-106, app preferences, per machine and profile):
`AutoUpdate` (default on) checks and downloads in the background. `WhenUpdateReady` is
`RestartToUpdate` (default, waits for a press, #516) or `InstallBetweenTurns` (first gap with no turn
running and no question waiting, or the 6-hour backstop, #689; AutoUpdater.cs:437, `WaitAtMost` :137).
Unknown values read as `RestartToUpdate` (Stores.cs:84). The phone only shows the choice; it is set in
the app's Settings (docs/design/ui.md:1977-1991). An app from before #516 installs by itself after 6 hours.

**Presses today** (docs/protocol/v1-update.md; src/AgentBridge.Host/UpdateService.cs):
- `update.check` and `update.install {request}` go only through the relay's Noise channel, only from a
  `pwa` or `app` device, and never to a read-only machine (UpdateService.cs:8-21, :289-325).
- Limits, kept by each machine's own host: 3 presses of each kind per device per 10 minutes, keyed
  by device and kind (`pressed`, :64; :326-345), at most 8 waiting (`MaxAsks`, :43), and the same request
  is the same press (v1-update.md:47-56). A refusal says "too many presses … try again in 7 minutes"
  in `reason`; the PWA words code `rate-limited` as "try again in a few minutes" (pwa/js/update.js:225).
- Install waits until no session has been busy for 2 minutes (`IdleFor`, AutoUpdater.cs:96).
- Update now (#1159) is `update.install {now: SignedCommand}`. It passes the same signed-command gate as
  `manage.*` (src/AgentBridge.Host/SignedPress.cs:7-18, :55-73): the MAC verified for this machine, the
  action `update.install.now`, the release and the request; the signer must be the sending device, a
  person's device connected now, and not the app beside this host. It needs `permissions.remoteManage`
  (UpdateService.cs:185, :652), is recorded durably before the app is asked
  (src/AgentBridge.Host/UpdateNow.cs:210-216), lives 15 minutes (src/AgentBridge.Protocol/Update.cs:33),
  and is its own kind for the per-device limit (`NowKind`, UpdateService.cs:41). A second Update now
  while one is under way is refused `busy` (:478).
- Restart cap: Update now spends 1 restart per machine in 6 hours of its own, then the 2 in 6 hours it
  shares with `manage.restart` (src/AgentBridge.Host/ManageService.cs:29-30, :39-40, :432-445). Past it the
  card shows "Update now is available again at …" in place of the button (update.js:180-183).
- Leaving takes #805's lease, compares busy with what the person accepted, then announces (UpdateService.cs:498-640).
  #815's planned announcement is not built, so it leaves on a `Legacy` receipt (:635-637).

**What the phone sees.**
- `UpdateState` has `phase` idle, checking, downloading, waiting or installing, and `progress` 0-100
  (Update.cs:153, :175-219), real bytes from Velopack (src/AgentBridge.App.Core/ObservedUpdater.cs:59, :93-114).
- The PWA reads `update.get` once a minute, or every 10 s while an update is under way (v1-update.md:939-941).
- "Restarting to update…" for 90 s after the channel closes on an Update now, then plain Offline
  (pwa/js/update.js:27, :497-519).
- The first start after an update reports `last` updated or failed, with the request
  (`Record`, `Announce`, AutoUpdater.cs:1482, :1515).
- The app watches the new host for 10 minutes and records `failed` if it cannot start 3 times or never
  reaches a relay that answers (src/AgentBridge.App.Core/HostWatch.cs:11-19). Nothing tells the phone.
- Each card has a Check (Check now) button (pwa/js/ui/views.js:1124-1126); the page has Check all
  (:1400-1401). There is no Update all.

## 2. Detection

**Goal.** Every online machine knows of a published release within about 3 minutes, without any
machine using GitHub quota.

| Option | Verdict |
|---|---|
| A. Poll GitHub's API faster | Rejected: quota, and the brief forbids it. |
| B. Poll the release relay faster from every machine | Part of the answer: no GitHub quota. But N machines × 1 a minute is waste, and the relay stays 5 minutes stale. |
| C. CI pushes to every machine | Rejected: CI would need a credential that reaches machines, a new trust path. |
| D. The relay (the existing always-on service) watches the release relay once a minute and sends connected hosts an untrusted *release hint* | Chosen. One fetch per relay instance, no new service, no new secret. |
| E. The PWA notices one machine is ahead and presses Check on the others | Fallback only: it works only while a PWA is open. |

**The chosen path (D, backed by B).**
1. **Release relay.** Freshness drops from 300 s to 60 s, and it answers with an ETag and a 304, so a
   conditional poll costs nothing. To keep its own GitHub refreshes safe at that rate it uses a
   read-only GitHub token as a Worker secret (the README already names this owner decision). Without a
   token it stays at 300 s.
2. **Relay hint.** The relay asks the release relay's list once a minute, conditionally, reads the newest
   tag per channel without validating it, and sends `release.hint {tag, channel, seenAt}` to every
   connected host that advertised `releaseHint` in hello: on change, and on every connect. Never shown,
   never trusted.
3. **Host to app.** The host passes the hint to its app on the existing pipe report (`HostStatus`).
4. **App.** If the hinted tag is newer than both the installed version and the last list, and
   `AutoUpdate` is on, the app runs its normal check now.
   - `ReleaseDiscovery` gets the hint. When the cached list lacks the tag it asks the release relay now,
     ignoring `FreshUntil`. It never asks GitHub early.
   - Every existing rule applies: `CdnProblem`, the floor, checksums.
   - At most one hint-driven check per tag, and one per `CheckAgainAfter` (1 minute, AutoUpdater.cs:113).
     A hostile relay can cause at most one release-relay fetch per minute per machine, and no install.
5. **Fallback poll (B).** The 30-minute tick stays for machines and relays without the capability. A
   capable host with no hint for 10 minutes polls the release relay conditionally every 5 minutes.

**Worst-case latency (machine online).**
| Step | With token | Without |
|---|---|---|
| GitHub publishes → release relay fresh | 60 s | 300 s |
| Release relay → relay hint | 60 s | 60 s |
| Hint → app check → `available` | a few seconds | a few seconds |
| Total to `available` | about 2-3 min | about 6-7 min |

**When the feed is limited or unreachable.**
- **Release relay down.** No hints. Machines fall back to their poll, then to GitHub as today
  (ReleaseDiscovery.cs:146-181).
- **GitHub limits the release relay.** It serves stale with its `Age` (worker.js:88). Clients reject a copy
  older than 1 hour (`CdnOldest`, ReleaseDiscovery.cs:55), so a stale copy never says "up to date".
- **GitHub limits a machine.** The hold is unchanged (`Held`, :569). Its end goes out as a UTC instant
  (`checksHeldUntil`, new) for ordering and the log. A person never sees it as a limit: the card says
  "Checked 20 minutes ago · checking again soon". The local clock-time string (AutoUpdater.cs:1170)
  leaves the state, which also fixes the zone bug.

**Offline machines.** On reconnect the relay sends the latest hint at once.
**Mixed versions.** A host without `releaseHint` is never sent the frame and keeps the 30-minute tick. A
relay without the frame sends nothing, and capable hosts fall back. An app from before #516 is unchanged;
the Fleet header counts it as behind, not failed.

## 3. Per-machine state machine

**Rule.** Every state the phone shows is either a fact the host reported (in `UpdateState` or `last`) or
an inference marked as one. The phone never animates progress it was not given. Elapsed time is fine;
a fake percentage is not.

**Ordered states.**
| # | State | Source | Entered on | Left on |
|---|---|---|---|---|
| 1 | `current` | host | no `available`, and a `checkedAt` | hint, check |
| 2 | `checking` | host | `phase: checking` | the check ends |
| 3 | `behind` | host | `available` set, not downloaded | download starts |
| 4 | `downloading(bytes, total)` | host | `phase: downloading`, `progress`, new `bytes`/`total` | done or failed |
| 5 | `ready` | host | new `downloaded: true` (checksum passed, ObservedUpdater `Ready`) | press or mode |
| 6 | `waiting(code)` | host | `phase: waiting`, `waitingCode` | goes or refused |
| 7 | `installing(at)` | host | new `installing` instant (older host: `phase: installing`) | `leaving` |
| 8 | `leaving(to, at, backBy)` | host | new `leaving`, in the last `update.state` before the host stops | channel closes |
| 9 | `restarting` | **PWA inference** | channel closed while `leaving` set, before `backBy` | host returns, or `backBy` |
| 10 | `back(from, to, at)` | host | new `back` with this press's `request`, `current` = `to` | 10 minutes, or dismissed |
| 11 | `slow` | **PWA inference** | `backBy` passed (3 minutes after `at`), no channel | host returns, or 10 min |
| 12 | `didNotComeBack` | **PWA inference** | 10 minutes after `at` with no channel | host returns |
| 13 | `failed(code, retryAt)` | host | `last` outcome `failed` or `refused`, with code | next press, or dismissed |

**New optional `UpdateState` fields, and what an old client does.**
| Field | Set by | Meaning | An old PWA | A new PWA, old host |
|---|---|---|---|---|
| `installing` | app, via host | UTC instant the install began: package verified, lease held | ignores it; reads `phase: installing` as today | uses `phase: installing` for step 2 |
| `leaving` | host | `{to, at, backBy}`, sent just before it stops; `backBy` = `at` + 3 minutes | ignores it; 90 s "Restarting…" then Offline | today's 90 s rule, then Offline |
| `back` | new host | `{from, to, request, at}`, in its state for 10 minutes after the app's `Announce` | reads `last` as today | infers from `current` = target on reconnect, marked as inference |
| `bytes`, `total`, `downloaded`, `mode`, `checksHeldUntil` | app | §2, §5 | ignores them | `progress` only; no mode line |

A client ignores a field it does not know; `[UnknownAs]` rules stand.

**Creative's four steps.** The card shows an ordered list of four named steps (Creative, rule A4).
| Step | Shown as current while | Source | Old host |
|---|---|---|---|
| Downloading (bar and %) | state 4 | host: `bytes`/`total`, else `progress` | `progress`: host |
| Installing | states 5-8 after a press or mode decision | host: `installing`, `leaving` | `phase: installing`: host |
| Restarting | states 9 and 11 | **inference**: channel closed after `leaving` | **inference**: closed after `installing`, 90 s |
| Back online | state 10 | host: `back` | host: `current` on reconnect, tied to the press by inference |

Waiting (state 6) sits before Installing as Creative's waiting line, never as a step. Health bars read
"Not measured while updating" from state 7 to state 10.

**Thresholds: one set.** `slow` at 3 minutes, `didNotComeBack` at 10 minutes.
- 3 minutes is Creative's "not back after 3 minutes" card. A normal restart is under a minute
  (`PAUSE_LINE`, update.js:310), so 3 is three times normal and early enough for a person to act. The host
  sets it as `backBy`, so it can be tuned host-side later. It replaces this design's earlier 5 minutes.
- 10 minutes is the app's own `HostWatch.Window` (HostWatch.cs:18). Until then the app may still be
  starting the host (3 tries), so the phone never declares failure before the machine does.
- So `slow` shows Creative's card c (numbered steps, Keep waiting, Try again, which reconnects and sends
  no press). `didNotComeBack` keeps the steps, drops Keep waiting, and says "Did not come back. At the
  machine, open Slipgates; if it will not start, install <version> from its release page"
  (ops/runbooks/windows-release.md, Mitigate 3).
- If the host returns on the **old** version, its `last` says `failed`, and the PWA shows state 13.
- A host's `last` always replaces an inference. The 90 s `RESTART_SHOWN_MS` (update.js:27) stays only
  for hosts that send no `leaving`.
- A failed download is state 13 with `retryAt` from `ObservedUpdater` (10 minutes, doubling to 4 hours;
  ObservedUpdater.cs:32-33): "Download failed · trying again in 10 minutes", with Try again now (§4).
- Success stays 10 minutes or until dismissed; a failure stays until dismissed (Creative, rules A7-A8).

**Events (wire).** `update.state` gains the optional fields above. `release.hint` is relay to host only.

## 4. One press, Update all, and limits

**One machine.**
- Update now is the card's one primary, Update, wherever `canUpdateNow` is true. A quiet machine goes
  on one press. A busy one gets Creative's confirm, whose buttons map to presses: **Update now** =
  `update.install {now}` with `accept`; **Update when idle** = `update.install` (no `now`); **Cancel**.
- Install remains where the app is too old (`old-app`) or remote management is off.

**Update all.** A PWA action over the machines shown. No new op, no new signature, no new trust.
1. **Pick.** Machines `behind` or `ready` for the newest release they report. Dropped, each with its
   reason as a disabled row: read-only, no installed app, `canUpdateNow` false with no Install fallback,
   a different release.
2. **Confirm.** Creative's sheet: a row per machine (version change, "2 sessions will pause", checkbox),
   built from each machine's `nowSheet` (update.js:314). One confirm covers each machine's `accept` as
   shown. A machine that answers `confirm` (busier now) asks again for that machine only.
3. **Waves.** The first wave is the single least busy machine. After it, two at a time. A wave ends
   when each of its machines is `back`, `failed` or `didNotComeBack`, or was pressed with Update when idle
   (that row reads "updates when its sessions are quiet" and holds no wave).
   - Each press is signed when its wave starts, so none lapses (15 minutes, Update.cs:33) while waiting.
   - **Pause on the first failure.** A `failed` or `didNotComeBack` stops new presses: "Paused: 1 failed.
     Continue / Stop". Machines already pressed carry on. A `refused` row (for example `busy`) does not pause.
   - **Continue** resumes waves of two over the unpressed machines, in order. Each is read again at its
     wave: one now current is dropped, one busier than accepted gets its own confirm, and each is signed
     then. The failed machine is not pressed again; Try the failed again does that, as a new press.
   - **Stop** leaves the unpressed machines unpressed, listed "not pressed". Closing the PWA while paused
     is Stop.
   - Product's "keep one per role" needs roles on the roster (#55). Until then the waves stand in for it.
4. **Sign.** Each machine gets its own `update.install {now}`: its own request, `target.machine`,
   `release` and `accept`, signed with the key that machine's handshake proved (update.js:473), through
   the same `SignedPress` gate. Nothing is batch-signed.
5. **Results.** A row per machine, then the state machine above. The bar reads "Updating 2 of 4 · 1 done
   · 1 waiting"; the end reads "3 updated, 1 failed" with Try the failed again.

**Idempotence.**
- **The same device presses twice.** Update all is disabled while a run is open (it is running, not
  limited), and each press is single-flight (update.js:401, :428).
- **A resend of the same press.** It gets the recorded answer (`Again`, UpdateService.cs:424-436).
- **Another device presses Update all.** Each machine refuses `busy` (:478); its row reads "already updating".
- **Again after it succeeded.** The machine is `current` and is not picked.

**Refused, and stays refused.** A press from a machine or agent (SignedPress.cs:73); the app beside the
host (:74); a read-only machine (:75); `remoteManage` off; an app too old for Update now; a changed
release (UpdateService.cs:459-462); a halted release (new, §7); a press older than 15 minutes. Owner-only
presses wait for the roster (#55); today "a person's device on the account" is the rule (SignedPress.cs:21-22).

**If the PWA closes mid-run.** Pressed machines carry on; unpressed ones stay unpressed. The next open
shows what each machine reports. There is no host-side fleet coordinator (§9).

**Limits a person meets (owner's rule).** The rule is about what a person meets: never an allowance, a
rate-limit message or a disabled button in ordinary use.
- **Check now leaves the card.** Checks are automatic (§2). Check for updates stays in the card menu and
  in Settings; Check all moves from the page into the Fleet page menu.
- **The abuse guards stay** on the relay path: 3 presses per device per kind per 10 minutes, and 8 waiting
  per machine (UpdateService.cs:42-48). They stop a stolen token or a stuck client, not a person.
- **Update all never reaches them.** Both are kept per machine, in that machine's own host, and the
  per-device count is keyed by device and kind (UpdateService.cs:64). Update all sends exactly one
  Update now to each machine, so across N machines it spends 1 of 3 on each, whatever N is. Try the
  failed again spends a second; a third is still allowed. It opens no new relay connection, so the
  relay's connect and sign-in limits (src/AgentBridge.Relay/RelayApp.cs:105-112) do not apply. Tested (§7).
- **The PWA's own automatic checks** (option E) spend Check presses. A Check past the device's limit
  is answered from the machine's last check instead of being refused, which is what a press within a
  minute already gets (v1-update.md:54). No person sees a refused Check.
- **A refusal by a limit is a plain retry time.** `UpdateCommandResult` gains `retryAt` (UTC) with
  `rate-limited`. The PWA says "Not updated yet. Try again after 9:12 PM", in the viewer's time, never
  "too many presses". An old host sends no `retryAt`; the PWA then says "Try again in a few minutes".
- **The restart cap is never a disabled button.** At the cap (`updateNowWhy: restart-cap`), the card's
  Update and Update all send Update when idle instead, and the row says "updates when its sessions are
  quiet". Install has no restart cap, since it never restarts over a busy session (v1-update.md:642).
  The cap itself is unchanged.
- **A failed download's Try again now works.** Today a release whose download failed is not downloaded
  again for 10 minutes, whoever asks (v1-update.md:55). A person's press now starts it once at once; the
  hold still governs background retries, and the per-device limit bounds the presses.
- **GitHub's quota hold** is shown as "checking again soon" (§2), never as a limit or a time.

The last four are changes; they are slice 10 (§8).

## 5. Three modes

**When an update is ready** gets three values, stored where it is today (app preferences,
`WhenUpdateReady`, Stores.cs:103), per machine and profile. Labels are Creative's.

| Label | Value | Behaviour |
|---|---|---|
| Restart to update (default) | `RestartToUpdate` | Downloads, shows `ready`, waits for a press (local or remote). |
| Install between turns | `InstallBetweenTurns` | As today: first gap with no turn running and no question waiting, else the 6-hour backstop. |
| Install as soon as available | `AsSoonAsReady` (new) | Installs once `ready`. Takes #805's lease (purpose Update), which waits only for typing or delivery in flight, not for turns. Creative's pause warning sits under it. |

**How the third mode goes.** It uses the departure Update now uses (`DepartAsync`) with an automatic
request, and the host announces it planned (#498/#815), so sessions resume after.
- It is offered only once #815's planned announcement is built; until then no client shows it.
  `SignedPress` grants a `Legacy` departure only to a person's press (SignedPress.cs:15-17), and this keeps that.
- The 6-hour cap applies to it: at most one automatic restart per machine in 6 hours. Past it the
  machine waits for the next gap, as Install between turns does.

**A press wins over any mode.** Update now goes when confirmed; Update when idle waits for quiet; a mode
governs only installs nobody pressed. Turning `AutoUpdate` off stops checks, downloads and hint-driven
checks, not presses.

**The phone sees the mode.** `UpdateState.mode` (read-only) words `ready` ("installs at the next gap",
"waiting for your press").

**Changing the mode from the phone (resolves the old open question 4).** Creative's per-machine
three-choice list and the fleet-level **Apply to all machines** are both included. Each is a signed
per-machine mode change, so it needs a new op:
- `update.mode {mode, now: SignedCommand}`, action `update.mode`, naming the machine, the mode and a
  request. It passes the same `SignedPress` gate as Update now: a person's device, connected now, not
  the app beside the host, not read-only, `remoteManage` on. Recorded durably, idempotent by request.
- The host hands it to its app over the pipe; the app stores it and reports `mode`. A host or app too
  old for it never offers it (a hello capability), and the row stays read-only there.
- **Apply to all machines** confirms with a list of what changes, then signs one `update.mode` per
  machine, never a batch. Rows report per machine; a refusal names its reason.
- Setting `AsSoonAsReady` remotely is refused until the mode itself ships (#815 gate).
- It is its own two-family slice after the mode slice (§8, slice 12).

**Migration.** Existing values keep their meaning; unset still means `DefaultWhenReady`. An older app
reads `AsSoonAsReady` as `RestartToUpdate` (`[UnknownAs]`, Stores.cs:84), the safe direction. ui.md's
two-choice text becomes three in the mode slice. No file format changes.

## 6. Product suggestions and Creative's (one line each)
- Version on each card and "N machines behind" with Update all in the Fleet header, from host-reported `current` (#586).
- Halt: wire the halt list (§7, slice 8) before Update all ships; rollback stays "a newer build" (#568, #580).
- Failed update: keep the old version until `back` is proven; no rollback button in v1 (#581, #1173).
- Sessions survive: the third mode waits for the planned announcement (#954, #498).
- Success and failure stay in-app; no new push types.
- Show the PWA's own version beside the fleet's (#1246).
- Fix first: #1740, #1920 and #1173, as Product asks. §2's hold wording covers the zone part.
- **Later: hold a machine on a version.** Needs a per-machine skip that the halt list and Update all both honour; halt first.
- **Later: mixed-version warning on Move to machine.** Needs a defined cross-version compatibility rule, which does not exist yet.
- **Later: release-notes sheet.** Needs an owner for notes content; until then What's new opens the release page.
- **Later: quiet hours for the two faster modes.** That is scheduling, which Product parked.
- **Active role colour:** Creative's to name as a token (the board uses blue); the slices use that token.
- **Contrast:** checked in the PWA slices on real WebKit and forced colours, not by token choice alone.

## 7. Failure modes and their tests

| Failure | Behaviour | Test (file) |
|---|---|---|
| Release relay unreachable | No hints; the 5-minute poll, then GitHub as today. | ReleaseDiscoveryTests: hint with relay down asks nothing early. AutoUpdaterTests: poll after 10 min without hints. |
| GitHub limits the release relay | Stale with `Age`; over 1 hour rejected; the hint stops advancing. | worker.test.mjs: ETag/304, stale-with-Age. ReleaseDiscoveryTests: an old copy is not "up to date". |
| GitHub limits a machine | Hold as today; `checksHeldUntil` in UTC; "checking again soon". | AutoUpdaterTests: no local-time string. update.test.mjs: the card shows no time or limit. |
| Forged or spammed hint | One release-relay fetch per tag and per minute; no install. | ReleaseDiscoveryTests: 100 hints → 1 fetch; a tag not in a valid list → no `available`. |
| Bad signature or hash | Fails its feed's SHA256 → `failed`, old version kept (Platform.cs:166-170). | Existing package-mismatch tests stay green; one added for a hint-driven download. |
| Partial download | Hold (10 min to 4 h); state 13 with `retryAt`; never `ready`. A person's Try again now starts it once. | AutoUpdaterTests: a cut-off download never sets `downloaded`; a press skips the hold once, a background retry does not. |
| Machine does not return | `restarting` → `slow` at 3 min → `didNotComeBack` at 10; replaced by `last` on return. | update.test.mjs, fake clock: each step at its time; old version back shows `failed`. |
| Update all across many machines | One press per machine; never `rate-limited`. | update.test.mjs: Update all over 12 machines sends 12 presses, each 1st of its machine, none refused. UpdateServiceTests: "Update now after three Installs from the same device is accepted"; "a third press from one device in 10 minutes is accepted". |
| A limit is reached anyway | Check: answered from the last check. Install or Update now: `retryAt`, worded as a time. | UpdateServiceTests: a fourth Check is accepted with the cached state; a fourth Install carries `retryAt`. update.test.mjs: no "too many" text. |
| Restart cap reached | Update sends Update when idle; no disabled button. | update.test.mjs: `restart-cap` → Install sent, quiet line shown. |
| Two presses / two Update alls | Same request: recorded answer. Second Update now: `busy`. Same device: one per machine. | UpdateNowTests: a parallel second press is refused. update.test.mjs: double click sends one per machine. |
| Update during a running turn | Restart to update and Install between turns never go over a turn. Update now only within `accept`. Install as soon as available waits for #815. | UpdateFlowTests: busier than accepted → confirm. AutoUpdaterTests: the third mode hidden without #815. |
| Update all fails midway | Paused; no new presses; Continue re-reads and signs the rest; Stop lists "not pressed". | update.test.mjs: a failure in wave 1 → wave 2 never signed; Continue signs wave 2 fresh and skips the failed machine. |
| Host/PWA version skew | New fields optional; old PWA reads 5 phases; old host gets no hint and keeps the 90 s rule. | Protocol tests: an old `UpdateState` round-trips. update.test.mjs: no `leaving` → 90 s path. |
| Halted release | Not offered, not installed. Missing list: hold an automatic install, allow a press with a warning. | Host.Tests: `ReleaseEligibility` wired; a halted tag is never `ready`. |
| Remote mode change | Signed per machine; refused without `remoteManage`, from an agent, or for the third mode before #815. | UpdateServiceTests: an unsigned `update.mode` is refused. update.test.mjs: Apply to all signs one per machine. |

## 8. Slices

Each slice is 400 lines or fewer and starts with its tests. Rows marked **2F** touch the update,
install or signing path, or the relay protocol, and need two families; both read security-subject docs in full.

| # | Slice | Depends on | Review |
|---|---|---|---|
| 1 | `checksHeldUntil` as UTC; drop the local-time string; the card says "checking again soon". | — | 1F |
| 2 | Release relay: ETag + 304; freshness 60 s when a token secret is set (owner decision), else 300 s. | — | 1F + SRE |
| 3 | Protocol: `release.hint`, `releaseHint` capability, optional `UpdateState` fields (`bytes`, `total`, `downloaded`, `installing`, `leaving`, `back`, `mode`), `retryAt` on results. v1-update.md. | — | **2F** |
| 4 | Relay: conditional poll of the release relay once a minute; hint on change and on connect, capable hosts only. | 3 | **2F** |
| 5 | Host and app: hint over the pipe; `ReleaseDiscovery` hint path (release relay only, once per tag, 1/min); 5-minute fallback poll. | 3 | **2F** |
| 6 | Host and app: report `bytes`/`total`/`downloaded` and `installing`; `leaving` with `backBy` = `at` + 3 min; `back` for 10 minutes. | 3 | **2F** |
| 7 | PWA card: the four steps and states of §3 (3 and 10 minutes), version per card, "N machines behind"; Check now off the card, Check all into the page menu; What's new opens the release page. | 6 | 1F |
| 8 | Halt list wired into the app's install path, reading `halts.json` via the shared list (no API quota). | — | **2F** |
| 9 | PWA Update all: confirm sheet, least-busy first then two at a time, Paused with Continue/Stop, results, the many-machines limit tests. | 7, 8 | **2F** |
| 10 | Limits never met: Check past the limit answered from the last check; `retryAt` worded as a time; restart cap falls back to Update when idle; a person's Try again now skips the download hold once. | 3 | **2F** |
| 11 | Third mode `AsSoonAsReady`, labelled "Install as soon as available": enum, app Settings, ui.md's three choices, the automatic departure through the lease, its 6-hour cap. | #815 built, 6 | **2F** |
| 12 | Signed mode change: `update.mode` op through `SignedPress`, capability, app storage; PWA per-machine three-choice list and Apply to all machines (one signature per machine). | 11 | **2F** |

**Changed in revision 2.** Slices 1, 3, 6, 7, 9 and 11 changed; 10 and 12 are new; 2, 4, 5 and 8 are as
before. Slices 1, 2, 3 and 8 can start at once; 10 needs only 3.

## 9. Open questions
1. **Owner.** Give the release relay a read-only GitHub token as a Worker secret (60 s instead of 300 s)?
2. **Product.** Should Update all go on if the PWA closes? That needs a host-side coordinator, which is
   new always-on state. This design stops instead, and a pause is a Stop.
3. **Product.** When the halt list cannot be read, should a person's press still go, with a warning? This
   design says yes for a press and no for an automatic install.
4. **Creative.** The Active role's colour token, and owner sign-off on the board at 2bc31243.
