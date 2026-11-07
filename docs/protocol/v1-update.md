# Protocol v1: updates (`update.*`)

Another of the person's devices sees a machine's updates and asks for one: the PWA and the
Windows app list, under each machine that offers them, the release it runs, a newer one once a
check has found it, what its app is doing about it, and how the last update ended, with
**Check**, **Install** and **Check all** (#51).

The Windows app on that machine does the work, as it does its own background updates
([app-windows.md](../app-windows.md#updates-velopack-one-version-for-app-and-host)): it finds the release on the machine's own
update feed (never agent-ha-bridge's, design G2), downloads it, waits until no session there has
had a turn running or a question waiting for two minutes, however long that takes (#516; before,
it went ahead after six hours whatever they were doing), then stops the host and restarts both on
the new release. The host only carries what its app
reports to the devices, and their presses to its app. It decides who may press. Test vectors for
the records are in [`vectors/update.json`](vectors/README.md); [v1-sessions.md](v1-sessions.md#test-vectors)
says how to regenerate them.

## Who may ask

| Rule | Why |
|---|---|
| Through the relay only (a Noise channel); never on the machine's own pipe | The pipe is where agents, the MCP server and the Home Assistant connector are. The app beside the host updates its machine from Settings |
| Only a device the relay lists as `pwa` or `app` (a person's phone, browser or Windows app) | Another machine's host is where agents act. As for folder-trust answers, the kind is what a device says it is: something holding the account's token could claim `pwa`, and all it could then do is install the machine's own release once no session there has been busy for two minutes. Update now (#1159, design below) adds a signed press and #801's restart cap, since it does not wait |
| Nothing on a read-only machine (`delivery.readOnly`, or agent-ha-bridge running for this user, G5) | hello offers no `update`, and every `update.*` is refused, reads included, as #112 withdrew Home Assistant's Install there. A machine that becomes read-only after a press was accepted lets go of presses still waiting, and its app, which reads the host's `readOnly` again just before stopping it, refuses an install it took before |
| Not while another profile's app or host runs from the same installation | Velopack stops every process in the package, busy or not, and restarts only the profile that installed. The app refuses at once, before anything is downloaded, and again just before it stops the host |
| Not when the app is not running, or cannot update (a build folder rather than an installed copy) | Nothing would carry it out |

Set to "Restart to update" (the default), the app never installs a release by itself (#516): it
downloads it in the background ("Download updates automatically") and waits for a person, Restart
to update at the machine or Install here. Set to "Install between turns" (#689, a machine agents
use), it installs at the first gap with no turn running and no question waiting, or at a six-hour
backstop that never installs while a question waits; its state says so (`automatic` true, phase
`waiting`), and while the backstop skips for a question `waiting` reads "Update ready · waiting for
a question to be answered".
Being asked stands in for that switch, for that release: an asked install goes ahead with the
switch off. However long ago the release was found, it waits until no session there has been busy
for two minutes; nobody at the machine was warned, so a busy session always holds it.

An app from before #516 still installs by itself, and goes ahead six hours after it found the
release whatever the sessions are doing. Its update state can't be told from a newer app's (it
sends `automatic` too, and nothing in the state carries the app's behaviour), so a device's Install
confirmation keeps that caveat rather than promising every machine waits as long as it takes.
Such an app usually installs a newer release by itself within about six hours of finding it, but
not if its "Install updates automatically" is off or it isn't running, so a mixed fleet can last
longer, and the caveat stays until no such app is left.

## How often

| Limit | |
|---|---|
| Presses per device | Each device may have 3 presses of each kind taken in 10 minutes. The rest are refused (`"too many presses of Install from this device just now; try again in 7 minutes"`), and the host logs a refusal from a device at most once a minute |
| Presses waiting | At most 8 wait for the machine's app at once; a new one is then refused (`"this machine has too many presses waiting for its app already; try again in a few minutes"`), never one already accepted dropped |
| The same press | A request sent again is the same press, before and for ten minutes after the app took it, and the app remembers the last 32 it took across restarts: none is carried out twice |
| The feed | A press within a minute of the app's last check is answered from that check (its release, or its failure) without asking the feed again |
| Downloads | A release whose download failed is not downloaded again for ten minutes, whoever asks (the machine's own Settings and background downloads included), and for twice as long after each further failure in a row, up to four hours; the app tries it again in the background when that ends, without asking the feed again. The press is answered `failed`, saying when it is tried again |

## Capability

hello's `capabilities` include `update` on a channel through the relay to a machine that is not
read-only. The pipe never offers it.

## Ops

| Op | Data | Result |
|---|---|---|
| `update.get` | none | `UpdateState` |
| `update.subscribe` | none | `UpdateState`, then an `update.state` event (data: `UpdateState`) whenever it changes. Nothing is sent ahead of the response |
| `update.check` | `{ "request": "<uuid>" }` | `UpdateCommandResult` |
| `update.install` | `{ "request": "<uuid>" }`, or for Update now `{ "now": SignedCommand }` | `UpdateCommandResult` |

`request` is the device's UUID for one press; the same press sent again is the same ask.
`update.check` and `update.install` without one fail with an error. `now` is **Update now**
([below](#update-now-1159-design-not-built-yet)): designed, not built. Its press carries the
request **only inside the signed command**, never beside it, so a host that doesn't know `now`
finds no `request` and refuses the press with that error: it can never fall back to a plain
Install (Connection, settled decision 6). A client sends it only to a machine whose state says
`canUpdateNow`.

### `UpdateState`

| Field | Type | |
|---|---|---|
| `current` | string? | The release installed. With no app reporting, the host's own version |
| `installer` | bool | Whether an updater is there to act: the Windows app, running and installed |
| `unavailable` | string? | Why nothing can be asked now (`"the Agent Bridge app is not running on this machine, and it is what installs updates"`, `"this copy of the app was not installed by its updater, so it cannot update"`) |
| `available` | string? | The newer release the last check found, from a device's press, the app's own schedule or its Settings; still there if its download failed, gone once a check finds nothing newer or the app's Include pre-releases changes. Before any check, or while the feed refuses, a release an earlier run of the app downloaded |
| `notes` | string? | Its release notes |
| `checkedAt` | time? | When the app last checked |
| `phase` | `idle` `checking` `downloading` `waiting` `installing` | `waiting`: a release is downloaded and will install, because a device asked, once the host is idle. One downloaded and waiting for the person at the machine is `idle`, with `available` set |
| `progress` | int? | 0–100 while downloading |
| `waiting` | string? | While waiting: for what (a session busy, another profile running). Also, in any phase, once a release has waited more than six hours (held by the machine, or downloaded and not restarted into): "Needs you: the update to X has waited since T because Y" (#800, a reminder; nothing installs because of it) |
| `request` | string? | The press being carried out now, if one is |
| `automatic` | bool | Whether the app installs updates without being asked. From #516 on, `true` only on a machine set to "Install between turns" (#689), whose phase is then `waiting` while a release waits for a gap between turns |
| `last` | `UpdateResult`? | How the last update asked for, or installed by itself, ended |
| `canUpdateNow` | bool | **#1159, not built.** Whether Update now can be pressed here: the app running, installed and new enough to carry it out, `permissions.remoteManage` on, the restart cap not spent. Absent reads as false |
| `updateNowWhy` | string? | **#1159, not built.** With `available` set and `canUpdateNow` false: why, as a code (`disabled`, `old-app`, `unavailable`, `restart-cap`), which a client words itself ([What the person sees](#what-the-person-sees)) |
| `updateNowAt` | time? | **#1159, not built.** With `updateNowWhy` `restart-cap`: when it can be pressed again |
| `busy` | `UpdateBusy`? | **#1159, not built.** Only with `canUpdateNow`: what Update now would go over, from the host's own session snapshot ([What the phone sees](#what-the-phone-sees-before-the-press)) |
| `requestNow` | bool | **#1159, not built.** The press in `request` is an Update now |
| `waitingCode` | string? | **#1159, not built.** With phase `waiting`: why, as a code a client words itself (`turn-running`, `question-waiting`, `unknown`), never showing `waiting`'s text ([The confirmation](#the-confirmation)) |

An unknown `phase` reads as `idle`. Text fields are cut to 64 bytes (versions), 512 (reasons)
and 4 KiB (notes).

### `UpdateResult`

| Field | Type | |
|---|---|---|
| `outcome` | `updated` `current` `refused` `failed` | `updated`: running the new release now. `current`: there was nothing newer. `refused`: not attempted, and `reason` says why. `failed`: attempted, and still the old release. An unknown outcome reads as `failed`, never as `updated` |
| `from`, `to` | string? | The releases |
| `reason` | string? | |
| `request` | string? | The press it answers; absent for one the person started at the machine (Restart to update), or an automatic one before #516 |
| `code` | string? | **#1159, not built.** For an Update now, as in `UpdateCommandResult` |
| `at` | time | |

An install restarts the app and host, so its outcome is reported by the new version: the app
keeps the press it is carrying out in its preferences when it hands over to the updater, and its
first start on the new release reports `updated` (or `failed`, "the update did not take") with
that `request`. A device following the machine sees its channel close as the host stops, and
the outcome when it reconnects. Restart to update at the machine (its banner or Settings) answers
an Install a device pressed and the app had not yet carried out, and one that fails there is
reported `failed` too, its download failing included. A press the app took and had not finished when it stopped for
anything else (quit, sign-out, the machine restarting) is not carried out by its next start,
which reports it `refused`, "the app on this machine restarted before carrying it out; press it
again": an install pressed hours before must not go ahead on sessions never watched for it.

### `UpdateCommandResult`

| Field | Type | |
|---|---|---|
| `outcome` | `accepted` `refused` `confirm` | `confirm` (#1159, not built): an Update now that would go over more than the person accepted; nothing was asked, and `state.busy` says what is busy now. An unknown outcome reads as `refused`. `accepted`: the machine's app has been asked, and how it goes is in the state (`request`, then `last` with this request). A Check that finds a newer release ends instead with `request` gone and `available` and `checkedAt` set (`last` stays how the last update, or a check finding nothing, ended). `refused`: nothing was asked |
| `reason` | string? | Why it was refused |
| `state` | `UpdateState`? | The state as the press found it |
| `code` | string? | **#1159, not built.** Only for a press with `now`: why it was refused or needs confirming, as a code: `manage.*`'s reason codes (`ManageReason`) and Update now's own (`release-changed`, `other-profile`, `old-app`, `restart-cap`, `sign-in`). Clients word it themselves ([What the person sees](#what-the-person-sees)); `reason` stays for the log and older clients. An unknown code reads as a refusal |

The host refuses a device that is not connected to the relay now, one that is not a person's,
and a press with no app to carry it out. The app's refusals (another profile running, the host
gone, a build folder) come back as `last` with `outcome: "refused"`, since the host has already
answered `accepted` by then. A press the app has not taken within ten minutes lapses at the
host, and an app that comes to it later (from a status it was slow to read) refuses it, with
`last` saying so, rather than carrying it out late.

## Update now (#1159): design, not built yet

Everything in this section is a design for review. No host, app or client carries it yet, and
the fields marked #1159 above are not sent.

**Why.** The owner, 2026-11-07 10:33 PT: *"I need a way to force updates to happen from my
phone. When machines are really active they can't find a gap in turns to update, and I want to
be able to accept the temporary downtime of an update."* An Install from a device waits until no
session has been busy for two minutes; on a machine agents keep busy (the coordinator VMs) that
never comes, and the machine stays behind (#819). The owner's decisions on #1159: ask for
confirmation **only** when a session there is mid-turn or has a question waiting (a quiet
machine updates on one press, as Install does today); it is next in priority; and it stays one
update path with #819 (fleet convergence) and #801 (remote host management).

**What it is.** Install, without the app's wait for a quiet machine. It skips:

* the two-minute idle wait (`AutoUpdater.IdleFor`) a device's Install waits for, however long;
* Install between turns' six-hour backstop, which skips while a question waits;
* the "Restart to update now?" dialog at the machine: nobody is there, and the phone's
  confirmation stands in for it.

It waits for nothing the person confirmed. If, when it is ready to go, the machine is busier
than the person confirmed, it keeps the download and waits for the next quiet moment (Install
between turns' five-second settled gap, with no backstop), or for a fresh confirmation (Product,
settled decision 3).

It skips nothing else. In particular it never skips **#805's quiet lease**
([host-lifecycle.md](../host-lifecycle.md)): the host still leaves only through
`TryAcquireQuiet`, `AnnouncePlanned` and `CommitDeparture`, so it never stops while an answer,
a reply or a stop is being typed or delivered, and no answer is ever typed twice. That lease
looks at deliveries and typing only; it never looks at running turns or waiting questions, and
neither Update now nor #819 adds an idle test or a second fence to it. Running turns and
waiting questions are what the person is **told** and **confirms**, and what the host compares
with that confirmation when it acts ([Going](#going-the-lease-and-the-departure)); they are
never a lease condition. There is no force flag. `ForcedRecovery` and `WatchdogRecovery` are #805's slice 2 (a
durable input journal and a recovered queue), are refused `JournalUncertain` today, and are
never a person's skip; the `departure-stuck` watchdog restarts a host only after a graceful
commit, which needed no accepted work in flight. Update now takes the lease as
`SelfInitiatedGraceful`, purpose `Update`. **No update takes the lease today:** the app's
Install, Restart to update and Install between turns stop the host over the pipe
(`host.stop`), and nothing uses `DeparturePurpose.Update` yet; Update now is its first consumer
([open question 1](#open-questions)).

**Supersedes Q-O7 for updates only.** #801's restart rule is that a restart "is refused at once
when the lease cannot be had, never forced, never carried out later" (owner, Q-O7; ManageService,
[v1-sessions.md](v1-sessions.md#managing-a-machine)). The owner's 2026-11-07 decision above
supersedes the "never carried out later" part for an update a person asks to go **now**: the
download comes first and may take minutes, the lease is tried for a bounded minute
([Going](#going-the-lease-and-the-departure)), and a machine that became busier meanwhile
installs at its next quiet moment (decision 3). "Never forced" still holds: no lease, no
departure. `manage.restart` keeps Q-O7 exactly.

### The press

`update.install` with `now`: a [signed command](v1-transfer.md) the pressing device made for this
machine, as `manage.*` takes ([v1-sessions.md](v1-sessions.md#managing-a-machine)), and nothing
else, not even `request`:

```json
{ "now": { "body": "<base64url>", "by": "<base64url>", "mac": "<base64url>" } }
```

**What the signature binds** (Connection, settled decision 6): its body's `action` is
`update.install.now` and its `args` carry `"now": true`, both required; its `request` is the
press's UUID (the one the host dedupes on, and `last` answers with); its `args.release` is the
target version; its `target.machine` is this machine. So a plain Install, which is unsigned,
can't be replayed as a forced one, nor an Update now for one release or machine as another. It
lives at most **2 minutes** from `issuedAt`, as a manage command does. Its `args`, signed with
the rest:

| Arg | | |
|---|---|---|
| `now` | `true` | Required. Anything else is refused `bad-args` |
| `release` | string | The release the person was shown (`available`). Anything else at the press, or at the departure, is refused `release-changed` |
| `accept` | `UpdateBusy`? | What the person confirmed it may go over. Absent: the one-press case, valid only while the machine is quiet |

Anything else in `args`, or a `contentSha256`, is refused `bad-args`.

The result is `UpdateCommandResult`, with one more outcome, `confirm`, and, for a press with
`now`, a `code` beside `reason`, so a client words the outcome itself from a fixed list
([What the person sees](#what-the-person-sees)) and never shows the host's text (#826). The
host's `reason` is plain English, never an exception's text, for the log and older clients. How
it ends is in `last`, as for Install, with the same `code`.

### What still refuses, and why

Every rule Install has today stays, and the signed-command rules of `manage.*` are added, since
Update now interrupts what Install would wait for.

| Refused | `code` | Why it stays |
|---|---|---|
| A read-only machine (`delivery.readOnly`, or agent-ha-bridge running, G5) | `read-only` | It serves no `update.*` at all, and the `Update` purpose's eligibility is read again just before the commit, so a machine that turns read-only after the press never leaves for it |
| `permissions.remoteManage` off ([host-config.md](../host-config.md)) | `disabled` | The one switch that takes remote management back. `canUpdateNow` is false and Install still works |
| Not through the relay, not a person's device (`pwa` or `app` in presence until #55), not connected now, or this machine's own app | `not-person`, `not-peer`, `not-connected`, `local-device` | As `update.*` and `manage.*`: the pipe is where agents, the MCP server and the Home Assistant connector are, and an agent here could use the app's key as the same user |
| A signature that doesn't verify, is older than 2 minutes or from the future, or names another machine or action | `bad-signature`, `expired`, `future` | #801's bound on a disruptive press: whoever pressed did so in the last two minutes, on that device |
| Another profile's app or host running from the same installation | `other-profile` (the app's, in `last`) | Velopack stops every process in the package, busy or not, and restarts only the profile that installed. The app refuses at once, before anything is downloaded, and again just before the host stops |
| The app not running, a build folder rather than an installed copy, or an app too old for Update now | `unavailable`, `old-app` | Nothing would carry it out. An old app is told apart, since Install still works there |
| #800's agent sign-in entrypoint on | `sign-in` | A restart cancels a pending sign-in, as for `manage.restart` (Connection, Q-C8) |
| More than 3 presses of Update now from one device in 10 minutes; 8 presses waiting already; another Update now downloading or leaving on the machine | `rate-limited`, `busy` | As today. Update now is its own kind for the per-device limit, so Install stays usable. An Update now **waiting for a quiet moment** is not "under way": a new press replaces it, keeping its download (decision 3) |
| More than **2 committed restarts in 6 hours** on the machine, counted together with `manage.restart` (6 an hour once #55's roster is enforced) | `restart-cap` | An Update now is a restart the person caused; its cap is #801's, shared, so the two can't be added up. The words say when it works again |
| The same press sent again | (none) | The same-press rule: one request is one press, before and for ten minutes after the app took it, and the app remembers the last 32 across restarts; the signed command's `request` is acted on once |
| The release offered is no longer `args.release` | `release-changed` | The person confirmed one version |
| More busy than the person accepted | (outcome `confirm`) | [The confirmation](#the-confirmation) |

### What the phone sees before the press

The host adds `busy` to the `UpdateState` it serves, from **its own session snapshot**, never
from the app's report:

`UpdateBusy`: `{ "working": int, "waiting": int, "unknown"?: true, "sessions"?: [{ "id", "state": "working" | "question" | "approval" }] }`

* `working`: sessions whose `status` is `working`, or `idle` with `activity.background`
  (delegating): a turn or work in flight, as `session.stop` counts it.
* `waiting`: sessions with a `question`, or whose `status` is `waiting`. A session that is both
  counts as `waiting`. In `sessions` its state is `approval` when its question's `kind` is
  `approval`, else `question`, so a waiting approval is named as one (Product, below).
* `unknown`: the snapshot isn't whole yet, or the host is unsure of a session, so it can't be
  told whether one is busy (`AutoUpdater`'s `Sessions.Complete` and `Unsure`).
* `sessions`: the busy sessions themselves, at most 32, by id and state only, so a client can
  list each by the name it already shows for it (Creative's rule, below). In `accept`, a client
  sends the counts only.

Ids and states only: never a session's title, prompt text, question text or answer. The devices
that may read `update.*` already see those sessions, with their names, through `sessions.*`, so
this discloses nothing new; it is there so every client lists the same sessions, and so the
press can carry what was shown. The host recomputes it when a session changes and sends
`update.state` at most once every 2 s for it.

**Does the host know a turn is running?** Yes, as far as its snapshot does: each session's
`status`, `question` and `activity.background` come from the agents' hooks and transcripts, and
are what the app's own wait reads today (`AutoUpdater.Busy`: a question, or `working` or
`waiting`) and what `session.stop`'s two-press rule reads. It is the bridge's view, not the
agent's: a session the host doesn't see (started where its hooks aren't set up, or another
user's) isn't counted, and a status can lag a turn by a moment. That is why the host still
enforces the confirmation itself, and why nothing but the confirmation depends on it.

### The confirmation

The owner's rule: a confirmation only when a session there is mid-turn or has a question
waiting. A quiet machine updates on one press.

* **The client asks.** It reads `busy` from the state it already follows. With `working` and
  `waiting` both 0 and not `unknown`, the press goes at once, with no `accept`. Otherwise,
  `unknown` included (Product: "if it cannot tell, treat the machine as busy"), it shows the
  confirmation (below), and the press carries `accept`: the counts the person saw.
* **The host checks, twice.** At the press, from its snapshot then, as an early answer: a press with no `accept` is taken only
  if the machine is quiet and its sessions are known; one with `accept` if `working` and `waiting` are each no more
  than accepted, and, if the host is unsure now, only if `accept.unknown` was set. Otherwise
  the outcome is `confirm`, nothing is asked of the app, and `state.busy` says what is busy now;
  the client shows the confirmation again with the new counts. The check that decides is the
  same comparison made again **when the host acts**, with the lease held (Connection, decision
  6; [Going](#going-the-lease-and-the-departure) step 3). Both are the host's, as
  `session.stop`'s is, so an old or careless client can't skip the person; they are a seatbelt,
  not the security boundary, which is the signature and the caps.
* **After the press: never over a turn nobody confirmed** (Product, settled decision 3). When the
  download is done and checked, the app looks at the machine again. It leaves at once if the
  press had no `accept` and the machine is still quiet, or if the press had `accept`, is less
  than 15 minutes old, and nothing is busier than the person accepted (`working` and `waiting`
  each no more, and `unknown` only if accepted). Otherwise it **keeps the download and does not
  install**: the phase is `waiting` with `waitingCode` `turn-running`, `question-waiting` or
  `unknown`, and the release installs at the **next quiet moment** (no turn running, no question
  waiting, its sessions known, settled for five seconds: Install between turns' gap, never the
  two-minute wait or a backstop), through the same lease. Or the person presses Update now
  again, sees the confirmation again with what is busy now, and that press replaces the waiting
  one, with no second download. Nothing is refused for it, and nothing is downloaded twice.

`UpdateState` gains, for this, **`waitingCode`** (#1159, not built): with phase `waiting`, why,
as a code a client words itself: `turn-running`, `question-waiting` or `unknown`. The host's
`waiting` text stays for the log and older clients, and is never shown by a client that knows
the code.

### What the person sees

Creative's rules (#1159, comment 6711605555); the words are the clients', from codes, never the
host's own text (#826). "Bridge", never "host".

* **The button.** **Update now**, beside Install, the same small secondary button, only when an
  update is ready (`available`) and this device can carry it out: a person's device, on a
  machine that isn't read-only, with `canUpdateNow`. When an update is ready but `canUpdateNow`
  is false, the row says why in secondary text instead of the button, as #1142's rows do, from
  `updateNowWhy`: `disabled` "Update now is off on this machine"; `old-app` "Update now needs a
  newer app there. Install still works."; `unavailable` "The app there can't install updates
  now"; `restart-cap` "Update now is available again at *time*" (`updateNowAt`, in the viewer's
  local time). A read-only machine already says "View only", and offers neither. Install keeps
  its words and its wait.
* **One press on a quiet machine** (owner): nothing working, no question or approval waiting,
  and its sessions known. No dialog.
* **Otherwise one confirmation**, the same dialog as the confirm before a more expensive model
  (#1142), and also whenever the bridge can't tell (Product: err toward warning). Title
  "Update *machine* now?" Body: each busy session from `busy.sessions`, by the name the client
  shows for it, with its state in words, "Mid-turn", "Question waiting" or "Approval waiting"; a
  session the client has no name for yet reads "A session". If `unknown`: "Some sessions there
  haven't reported yet." (Creative.) Then one line: "They pause while the bridge restarts,
  usually under a minute." (The time is one the bridge backs up: planned update restarts came
  back in 20 to 25 s in #498's logs, and #815 announces 90 s as the most it expects.) Until #954
  lands, and only when a question or approval is waiting or the bridge can't tell, one more
  line: "A question or approval waiting may have to be answered at the machine afterwards."
  Buttons **Update now** and **Cancel**, focus on Cancel. If the machine answers `confirm`, the
  dialog comes back with the sessions it names now.
* **Waiting for a quiet moment** (decision 3), on the update line, from `waitingCode`:
  `turn-running` "Update ready · waiting for the current turn to finish"; `question-waiting`
  "Update ready · waiting for a question to be answered" (the line #719 already uses);
  `unknown` "Update ready · waiting until the bridge can tell the sessions are quiet". **Update
  now** stays offered beside it, and brings up the confirmation.
* **While it goes:** "Downloading *v* · 40%", then **"Restarting to update…"**, the Windows app's
  words for its own restart ([app-windows.md](../app-windows.md#updates-velopack-one-version-for-app-and-host)),
  kept through the restart ([Announced as planned](#announced-as-planned-498)). Never
  "Updating…", which means the screen refreshing after a machine comes back (#950).
  **Screen readers** (Creative): the download line is a polite live region, announced when the
  download starts and when it finishes, never at each percent; "Restarting to update…" is
  announced once.
* **Afterwards, on the machine row, in words:** "Updated to *v*", or "Not updated: *reason*",
  the reason fixed by its `code` (in the press's result, or in `last`):

  | `code` | Reason |
  |---|---|
  | `not-quiet` | a message was being delivered there the whole time |
  | `changed` | something changed on the machine; try again |
  | `release-changed` | a newer update arrived; try again |
  | `rate-limited` | too many presses just now; try again in a few minutes |
  | `restart-cap` | it has restarted as often as allowed for now; try after *time* |
  | `busy` | another update or restart is under way there |
  | `sign-in` | an agent sign-in is under way there |
  | `announce-failed` | the bridge couldn't tell your devices it was restarting |
  | `other-profile` | another bridge profile is running there |
  | `disabled`, `read-only` | Update now is off on this machine |
  | `expired`, `future`, `bad-signature` | the request didn't verify; try again |
  | `unavailable`, `old-app` | the app there can't install it now |
  | `failed` (outcome) | the update didn't take |
  | anything else | the machine refused it |

* **A question or approval waiting during the restart** stays on its card, still answerable once
  the bridge is back (#954): the restart doesn't take the card away. Until #954 lands the host
  can't promise that for an approval it carried through the agent's hook, so the confirmation
  names every waiting question and approval and warns (settled decision 8).

### Going: the lease and the departure

One update path. Update now adds the press, its authorization, the confirmation and skipping the
app's wait; it adds no lease, fence, idle test, downloader or installer of its own.

1. The host verifies the press (above) and hands it to the app as an ask, as Install's
   (`HostStatus.UpdateAsks`), marked `now` with its `release` and `accept`.
2. The app takes it, refuses at once what it refuses today (another profile, a build folder),
   then downloads and checks the package **outside the lease** (`DownloadMark`, and #984's
   per-profile copy). It holds the usage probes (#238) as every install does. It does not wait
   for a quiet machine unless, when it is ready, the machine is busier than the person
   confirmed, or the press carried `accept` and is 15 minutes old by then (a download that slow): then it keeps the
   download and waits for the next quiet moment ([After the press](#the-confirmation)), never
   going on sessions nobody confirmed it for.
3. The app asks the host to leave: one app-only pipe op (proposed `host.update.depart`,
   `{ "request" }`), the seam #819's installs will use too ([open question 1](#open-questions)). The
   host takes #805's lease, purpose `Update`, waiting up to 10 s. If an answer, a reply or a stop
   is being typed or delivered, it doesn't get it; it tries again, up to 6 times (about a
   minute), then refuses `not-quiet`, having interrupted nothing. **With the lease held, it
   compares its session snapshot then with the press** (Connection: busy and question state are
   checked when the host acts, not only when the person pressed): `busy` no more than `accept`,
   or quiet and known for a press without one. It does so before announcing and again just
   before committing; a failure aborts the lease (cancelling any announcement) and sends the
   press back to `waiting` with its download, never a refusal. This is the update consumer's own
   check, outside the lease's conditions: the lease still adds no idle test. It then announces the
   planned departure (`reason: update`, expected back in 90 s), reads the `Update` purpose's
   eligibility again (read-only, `permissions.remoteManage`, the sign-in entrypoint, another
   profile's host), and commits. **The restart cap is checked and spent with the commit**, under
   the same lock `ManageService` keeps its committed restarts in, so a `manage.restart`
   committed while the update downloaded can't make it three in six hours: such a press is
   refused `restart-cap` there. Any step failing ends it there, with `announce-failed`,
   `changed` (a lease that ran out), `restart-cap` or the eligibility's reason, and the host
   serves on.
4. Committed, the host refuses new deliveries until it exits (`DepartureGuard`), answers the app
   `committed`, and waits up to 3 s (`ManageService.LeaveAnyway`) for its replies to be sent.
   The app stops the host and hands the checked copy to `Update.exe`, as today. If the host has
   not exited 2 minutes after committing, #805's watchdog restarts it (`departure-stuck`), and
   the app reports `failed`, "the update did not take".
5. The new release's first start reports `updated` or `failed` in `last`, with the press's
   `request`, as an Install's.

The owner's other devices get the quiet notice `manage.restart` sends (Q-O3): a `finished` push
of class `done`, silent, kept in the tray, naming the machine, "updated now to *v*" and which
device pressed. The app logs one line naming the device and the counts it went over.

### Announced as planned (#498)

The departure's announcement is #815's `host-planned-state` (`reason: update`, return 90 s),
which [host-gone.md](host-gone.md) row 2 already treats as deliberate: every client shows
"Restarting to update…" (ui.md's machine-away treatment, neutral; never "Updating…", #950),
never "offline" or "host gone", and turns it to Offline only if the bridge isn't back in time,
so an update that hangs is never hidden. A reconnect clears it.

That transport isn't built yet (`UnnegotiatedPlannedStateTransport`), and #805 lets only a
person's `RemoteRestart` and `RemoteStop` leave on a `Legacy` receipt without it; an automatic
update never may. **Open question 2:** may a signed Update now take `Legacy` too, until the
transport lands? Proposed yes, since a person pressed it and is watching, as for
`manage.restart`. Meanwhile the clients bridge the gap themselves: a machine whose last
`update.state` was `installing` with `requestNow` is shown "Restarting to update…" for 90 s after its
channel closes, then Offline as usual.

### Questions and answers across the restart

* **An answer, reply or stop mid-delivery.** The lease isn't granted while one is being typed or
  delivered, and once committed nothing new is admitted, so the host never stops in the middle of
  one. The attempt ledger (`answer-attempts.json`, written before any key) holds across the
  restart, so a prompt typed into before it is never typed into after it: no answer is typed
  twice (#1100). What crosses the restart less well is the answer's **outcome**: the record that
  lets an identical retry get the first outcome (#1093, #1100) is the host's memory only. A phone
  that missed the result and retries after the restart is told the prompt was already answered
  (the ledger's refusal), not "delivered". **Open question 4.**
* **A question or approval waiting.** The agents keep running, and a question still pending when
  the new host starts is notified again ([v1-sessions.md](v1-sessions.md#when-a-host-notifies)).
  An approval the host carries through the agent's hook may not survive (#954). So, until #954
  lands, the confirmation names every question and approval the bridge knows is waiting, warns
  that it may have to be answered at the machine afterwards, and treats a machine it can't read
  as busy (settled decision 8). Update now doesn't wait for #954: the owner accepted the
  downtime, and the person decides with the warning in front of them.

### Security

**Who may send it:** a person's device through the relay, signed by that device within 2
minutes, connected now, not this machine's own app, on a machine that isn't read-only and has
`permissions.remoteManage` on. Never an agent, the MCP server, the Home Assistant connector or
another machine's host, and never on the pipe.

**What a stolen `pwa`-kind token could do.** Until #55's roster, anything holding the account's
sign-in can connect a key calling itself `pwa` and sign with it, so the signature binds a press
to the device that sent it, not to an approved person (as for `manage.*`).

| | Install today | Update now |
|---|---|---|
| Needs | The account's token, claiming `pwa` | The same, plus signing each press with a key it connected (it can make one); `permissions.remoteManage` on |
| What it can make happen | The machine installs the newest release from **its own feed**, once no session has been busy for two minutes: what the app offers anyway | The same release, **now**: the bridge restarts over running turns and waiting questions, for about a minute |
| What it can't | Pick the release or the feed, downgrade, type anything, read anything new | The same; and it can't skip the lease, so it can't cut a delivery or have an answer typed twice |
| How often | 3 presses a device in 10 minutes; no restart cap (it waits for quiet) | 3 a device in 10 minutes, and **2 committed restarts a machine in 6 hours**, shared with `manage.restart` |
| Who sees it | `last` on every device | `last`, the owner's other devices' quiet notice naming the device, and the app's log |

So the new exposure is availability: at most two bridge restarts a machine every six hours,
each about a minute, during which replies and questions wait, and an approval the host carried
may have to be answered at the machine (until #954). The agents' own work keeps running. The
remedy is the one #801 has: `permissions.remoteManage` off, and #55's roster.

### Build plan

Each slice is its own PR. Slices 1 and 2 are sensitive (protocol, updater and install path) and
need an independent review and a second model family; 3 and 4 touch how a confirmation renders,
so they need one too.

1. **Host and protocol.** `Update.cs`: `now` on the command, `canUpdateNow`, `busy`,
   `requestNow`, `waitingCode`, outcome `confirm`, `code`. `UpdateService` verifies `now` through the same
   signed-command check as `ManageService` (one shared gate, not a copy), counts the restart cap
   with it, computes `busy`, and passes `now` asks to the app. The app-only departure op takes
   the lease, announces and commits; the `Update` purpose's conditions are registered at
   composition (read-only and `remoteManage`, the sign-in entrypoint, another profile's host),
   whichever of this or #870 lands first. `ManageService`'s header comment changes from "never
   forced, never carried out later (owner, Q-O7)" to "never forced; a restart is never carried
   out later (owner, Q-O7); an update a person asks for now (#1159, owner 2026-11-07) waits for
   its download and up to a minute for the lease, and goes over no turn nobody confirmed: if the
   machine became busier meanwhile it keeps the download and installs at the next quiet moment
   (Product, #1161)". Vectors in `update.json`
   and `signed-command.json`. Tests, among others: "Update now on a quiet machine is accepted on
   one press"; "Update now with a turn running and no accept is answered confirm and asks the
   app nothing"; "Update now accepts no more busy sessions than the person confirmed"; "Update
   now is refused on a read-only machine, reads included"; "Update now is refused with remote
   management off"; "Update now from this machine's own app is refused"; "an Update now signed
   more than two minutes ago is refused"; "Update now and a remote restart share one cap of two
   in six hours"; "a remote restart committed while an Update now downloads leaves the Update now
   refused restart-cap"; "busy names a waiting approval as an approval and never carries any text"; "the departure is refused
   while an answer is being typed, and nothing is typed twice"; "an Update now press with no
   signed request is refused, never taken as an Install"; "a plain Install can't be replayed as an
   Update now"; "a turn started after the press sends the Update now back to waiting, with its
   download, when the host acts"; "a committed update departure
   refuses new deliveries until the host exits".
2. **App.** `AutoUpdater` takes a `now` ask: refuses another profile or a build folder at once,
   downloads and checks, holds the probes, skips `IdleFor` and the backstop (using the settled
   gap only while waiting for a quiet moment), leaves through the departure op, and reports
   `last` with its code, and `waitingCode` while it waits, through `host.update.report`. It reports that it
   can carry Update now. Tests: "an Update now ask installs without waiting for the machine to be
   idle"; "an Update now ask with another profile running is refused before anything is
   downloaded"; "a refused departure starts nothing and keeps the release offered"; "a quiet
   press that finds a turn running after the download keeps the download, installs nothing and
   waits with turn-running"; "a waiting Update now installs at the next quiet moment"; "an
   Update now pressed again while one waits replaces it without downloading again"; "an
   accepted press 15 minutes old waits for a quiet moment rather than going over a turn".
3. **PWA.** Update now beside Install, or the row's reason when it can't be pressed, the
   confirmation, signing the press with `accept`, the `confirm` outcome shown as the
   confirmation again, the lines while it goes, the 90 s "Restarting to update…" across the
   channel's close, and the result in words from `code`. Unit tests for the lists and words
   from `busy` and `code`, and Playwright: "a quiet machine updates on one press", "a busy
   machine lists each busy session by name and state, and sends what was shown", "the
   confirmation's focus starts on Cancel", "Cancel sends nothing", "a confirm outcome shows the
   sessions busy now", "a machine that can't update now says why instead of the button", "a
   refusal shows its fixed reason, never the machine's text", "a waiting approval is named and
   warned of", "a machine the bridge can't read asks first", "an update waiting for a turn says
   so from its code, never the machine's text", plus the demo's states for the
   visual check.
4. **Windows app, for another machine.** The Machines row's Update now (or its reason) and a
   `ContentDialog` with the same list, words and buttons, focus on Cancel, as #1142's model
   confirmation, through `App.Core` view-model tests mirroring the PWA's.

Order: 1, then 2, then 3 and 4 together.

### Settled decisions

3. **A machine that turns busy during the download** (Product, 2026-11-07, on #1161): "finish the
   download but do NOT install. The phone shows 'Update ready - waiting for the current turn to
   finish'. It installs at the next quiet moment, or the person presses Update now again and sees
   the confirmation again. Never interrupt a running turn without a fresh confirmation, and never
   discard the download." Specified in [The confirmation](#the-confirmation) (After the press)
   and [What the person sees](#what-the-person-sees); the line is written with the middle dot
   of the existing "Update ready · waiting for a question to be answered".
8. **A question or approval waiting before #954 lands** (Product, 2026-11-07, on #1161): "Warn
   about a waiting approval or question even before #954 lands: if the bridge knows of one, the
   confirmation names it; if it cannot tell, treat the machine as busy and show the
   confirmation. Err toward warning." Specified in [What the person sees](#what-the-person-sees)
   and [Questions and answers across the restart](#questions-and-answers-across-the-restart).
6. **The op's shape** (Connection, 2026-11-07, on #1161): use `now` on `update.install` as
   drafted, not a new signed op. Connection's conditions: "the SignedCommand must bind the
   request uuid, the target version and the `now` flag, so a plain Install cannot be replayed
   as a forced one; the host re-checks busy/question state at the moment it acts (not at press
   time); a relay or older host that does not know `now` must refuse, never fall back to a plain
   Install." No new relay work: the relay carries the Noise channel's bytes and reads no
   `update.*`. Specified in [Ops](#ops) (the request only inside the signature, so an older
   host refuses), [The press](#the-press) (what the signature binds) and
   [Going](#going-the-lease-and-the-departure) step 3 (the check when the host acts).

### Open questions

Numbered as first raised; 3, 6 and 8 are settled above.

1. **One departure seam with #819.** #870 (#819's host-only convergence draft) also has to
   leave through `Update`; which lands the pipe op and the `Update` registrations, and does
   every app-started install (Restart to update, Install, Install between turns) move to it in
   the same slice, or only Update now first? Proposed: whichever lands first owns it; only Update
   now uses it until #815's negotiated transport lands, since an automatic update may not take a
   `Legacy` receipt and would otherwise be refused `announce-failed` on every machine, freezing
   the fleet (#819 made worse); the other installs move once the transport is there. Engineering
   and SRE.
2. **`Legacy` announcement for Update now** until #815's transport lands (above). Without a yes
   here, or the transport, every Update now is refused `announce-failed`, so slice 1 is not
   useful until one of them. SRE.
4. **An answer's outcome across the restart.** Persist the first-outcome record with the attempt
   ledger, so a retry after the restart is told "delivered" or "unconfirmed" rather than the
   ledger's refusal; or wait longer than 3 s at the departure for answer results to reach their
   devices? Related to #1101. Trust and Connection.
5. **Usage probes.** An install waits up to 11 minutes for a Codex usage probe (#238). Keep that
   for Update now, or bound it lower and accept the Codex sign-out risk? Proposed: keep it; the
   line says why it waits.
7. **The cap.** Two committed restarts in six hours, shared with `manage.restart`, may be too few
   on a coordinator VM that also gets restarted remotely. Keep it until #55, as proposed? Owner.

## Between the host and its app

The pipe carries this ([local-pipe.md](local-pipe.md)): the app sends `host.update.report` with
its `UpdateState` and the requests it has taken, on connecting and on every change, and follows
the host's status (`host.subscribe`), whose `updateAsks` lists the presses waiting for it. Only
the app may send the report. A report from an older app, or none, leaves the devices told the
app is not running.

## Clients

The PWA follows `update.subscribe` on each machine shown and reads `update.get` with its
once-a-minute look at the others, every ten seconds instead while an update there is under way.
The Windows app follows it on each other machine's connection, and lists them on the Sessions
page; its own machine's updates are in Settings, and its Check all checks this machine too.
Neither offers Install on a machine that is read-only, whose app is not running, or that has
nothing newer. Update now (#1159, not built) is offered beside Install only where `canUpdateNow`
is true.
