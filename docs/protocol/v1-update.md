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
Install (Connection, settled decision 6). A client never sends both: a host that knows `now`
refuses a press carrying `request` beside `now` as `bad-args`, so a client that did would be
caught against a new host rather than turned into a plain Install by an old one (security
review, Low). A client sends it only to a machine whose state says `canUpdateNow`.

### `UpdateState`

| Field | Type | |
|---|---|---|
| `current` | string? | The release installed. With no app reporting, the host's own version |
| `installer` | bool | Whether an updater is there to act: the Windows app, running and installed |
| `unavailable` | string? | Why nothing can be asked now (`"the Agent Bridge app is not running on this machine, and it is what installs updates"`, `"this copy of the app was not installed by its updater, so it cannot update"`) |
| `available` | string? | The newer release the last check found, from a device's press, the app's own schedule or its Settings; still there if its download failed, gone once a check finds nothing newer or the app's Include pre-releases changes. Before any check, or while the feed refuses, a release an earlier run of the app downloaded |
| `notes` | string? | Its release notes |
| `checkedAt` | time? | When the app last checked |
| `phase` | `idle` `checking` `downloading` `waiting` `installing` | `waiting`: a release is downloaded and will install, because a device asked, once the host is idle; for an Update now (`requestNow`), at the next quiet moment within its 15 minutes, after any usage-check hold (`waitingCode` `usage-check`, which ends `refused` with code `usage-check` after 11 minutes), else `last` says `refused` with code `expired` ([The confirmation](#the-confirmation)). One downloaded and waiting for the person at the machine is `idle`, with `available` set |
| `progress` | int? | 0–100 while downloading |
| `waiting` | string? | While waiting: for what (a session busy, another profile running). Also, in any phase, once a release has waited more than six hours (held by the machine, or downloaded and not restarted into): "Needs you: the update to X has waited since T because Y" (#800, a reminder; nothing installs because of it) |
| `request` | string? | The press being carried out now, if one is |
| `automatic` | bool | Whether the app installs updates without being asked. From #516 on, `true` only on a machine set to "Install between turns" (#689), whose phase is then `waiting` while a release waits for a gap between turns |
| `last` | `UpdateResult`? | How the last update asked for, or installed by itself, ended |
| `canUpdateNow` | bool | **#1159, not built.** Whether Update now can be pressed here: the app running, installed and new enough to carry it out, `permissions.remoteManage` on, the machine not read-only, no agent sign-in under way, no other Update now downloading or leaving, and a restart slot free (its own allowance or the shared cap, settled Q7). Absent reads as false |
| `updateNowWhy` | string? | **#1159, not built.** With `available` set and `canUpdateNow` false: why, as a code (`disabled`, `read-only`, `old-app`, `unavailable`, `sign-in`, `busy`, `restart-cap`), which a client words itself ([What the person sees](#what-the-person-sees)). A read-only machine serves no `update.*` at all; `read-only` is for a state a client already holds when the machine turns read-only, or one served in the moment before it notices |
| `updateNowAt` | time? | **#1159, not built.** With `updateNowWhy` `restart-cap`: when the first slot frees, of its own allowance or the shared cap |
| `busy` | `UpdateBusy`? | **#1159, not built.** Only with `canUpdateNow`: what Update now would go over, from the host's own session snapshot ([What the phone sees](#what-the-phone-sees-before-the-press)) |
| `requestNow` | bool | **#1159, not built.** The press in `request` is an Update now |
| `waitingCode` | string? | **#1159, not built.** With phase `waiting`: why, as a code a client words itself (`turn-running`, `question-waiting`, `unknown`, `usage-check`), never showing `waiting`'s text ([The confirmation](#the-confirmation)) |

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
| `code` | string? | **#1159, not built.** Only for a press with `now`: why it was refused or needs confirming, as a code: `manage.*`'s reason codes (`ManageReason`) and Update now's own (`release-changed`, `other-profile`, `old-app`, `restart-cap`, `sign-in`, `usage-check`). Clients word it themselves ([What the person sees](#what-the-person-sees)); `reason` stays for the log and older clients. An unknown code reads as a refusal |

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
([settled decision 1](#settled-decisions)).

**Supersedes Q-O7 for updates only.** #801's restart rule is that a restart "is refused at once
when the lease cannot be had, never forced, never carried out later" (owner, Q-O7; ManageService,
[v1-sessions.md](v1-sessions.md#managing-a-machine)). The owner's 2026-11-07 decision above
supersedes the "never carried out later" part for an update a person asks to go **now**: the
download comes first and may take minutes, the lease is tried for a bounded minute
([Going](#going-the-lease-and-the-departure)), and a machine that became busier meanwhile
installs at its next quiet moment (decision 3), but only within the press's life of **15
minutes from `issuedAt`**, after which the press is dropped (security review). "Never forced"
still holds: no lease, no departure. `manage.restart` keeps Q-O7 exactly.

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
can't be replayed as a forced one, nor an Update now for one release or machine as another. The
command lives at most **2 minutes** from `issuedAt`, as a manage command does; the press it
starts lives at most **15 minutes** from that `issuedAt` ([After the press](#the-confirmation)).

**Replay, durably** (security review, Medium 3). The host records each `request` it verifies in
its profile folder (proposed `update-now.json`, as `manage.json` keeps `manage.*`'s), written
through **before** the ask is handed to the app, and keeps it for the press's longest life, 15
minutes from `issuedAt` plus `Skew`; the record survives the host's own restart. A command whose
`request` is recorded is the same press: it gets that press's answer and is never acted on
again, whether the first was handed to the app, was still being verified, or was taken by a host
that has restarted since. The app's own record of the last 32 presses stays as a second line,
not the authority. Its `args`, signed with the rest:

| Arg | | |
|---|---|---|
| `now` | `true` | Required. Anything else is refused `bad-args` |
| `release` | string | The release the person was shown (`available`). Anything else at the press, or at the departure, is refused `release-changed` |
| `accept` | `UpdateBusy`? | What the person confirmed it may go over: the counts, `unknown` if it was shown, `sessions`, the ids and states the confirmation listed, and `more`, how many it left out past 32. Absent: the one-press case, valid only while the machine is quiet |

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
| A Codex usage check still running after the 11 minutes every install waits for it (#238) | `usage-check` (the app's, in `last`) | Stopping the host cuts the app-server's input and can sign Codex out; Product kept the wait for now (settled Q5) |
| #800's agent sign-in entrypoint on | `sign-in` | A restart cancels a pending sign-in, as for `manage.restart` (Connection, Q-C8) |
| More than 3 presses of Update now from one device in 10 minutes; 8 presses waiting already; another Update now downloading or leaving on the machine | `rate-limited`, `busy` | As today. Update now is its own kind for the per-device limit, so Install stays usable. An Update now **waiting for a quiet moment**, or in its usage-check hold, is not "under way": a new press replaces it, keeping its download (decision 3) |
| Update now's **own allowance** (1 committed restart per machine in 6 hours) **and** the **shared cap** (2 committed restarts in 6 hours, counted together with `manage.restart`; 6 an hour once #55's roster is enforced) both used | `restart-cap` | The owner's ruling (settled Q7): an Update now spends its own allowance first, and only when that is used does it spend a slot of the shared cap, so it adds at most one restart in 6 hours beyond what `manage.restart` may do. `manage.restart` never spends Update now's allowance. The words say when it works again |
| The same press sent again | (none) | The same-press rule: one request is one press. The host's durable record of verified `request`s, kept 15 minutes from `issuedAt` plus `Skew` across its own restart ([The press](#the-press)), decides it; the app's record of its last 32 is a second line |
| The release offered is no longer `args.release` | `release-changed` | The person confirmed one version |
| More busy than the person accepted | (outcome `confirm`) | [The confirmation](#the-confirmation) |

### What the phone sees before the press

The host adds `busy` to the `UpdateState` it serves, from **its own session snapshot**, never
from the app's report:

`UpdateBusy`: `{ "working": int, "waiting": int, "unknown"?: true, "sessions"?: [{ "id", "state": "working" | "question" | "approval" }], "more"?: int }`

* `working`: sessions whose `status` is `working`, or `idle` with `activity.background`
  (delegating): a turn or work in flight, as `session.stop` counts it.
* `waiting`: sessions with a `question`, or whose `status` is `waiting`. A session that is both
  counts as `waiting`. In `sessions` its state is `approval` when its question's `kind` is
  `approval`, else `question`, so a waiting approval is named as one (Product, below).
* `unknown`: the snapshot isn't whole yet, or the host is unsure of a session, so it can't be
  told whether one is busy (`AutoUpdater`'s `Sessions.Complete` and `Unsure`).
* `sessions`: the busy sessions themselves, at most 32, by id and state only, so a client can
  list each by the name it already shows for it (Creative's rule, below). With more than 32
  busy, it lists approvals first, then questions, then turns, and **`more`** says how many busy
  sessions it left out (`working` plus `waiting` minus those listed). In `accept`, a client
  sends back the counts, `unknown` if it was shown, the `sessions` it listed and `more`, so the
  person's confirmation names the sessions it covers, not only how many (security review, Low).

**Covered by the confirmation**, the one comparison the host makes at the press and again when
it acts: every busy session now that is in `accept.sessions` has the same state there (a
session accepted as `working` that now has a question or approval waiting is not covered: the
person wasn't warned of it); the busy sessions now that aren't in `accept.sessions` are, by
kind, no more than the confirmation left out: those working no more than `accept.working`
minus the listed `working`, those with a question or approval no more than `accept.waiting`
minus the listed `question` and `approval`, and one with an approval only if the list held
nothing but approvals (approvals are listed first, so the person saw every approval by name
unless more than 32 approvals were waiting); `working` and `waiting` are each no more than accepted; and
the host is unsure only if `accept.unknown` was set. An `accept` whose `more` doesn't match its
list (`more` above 0 with fewer than 32 `sessions`, or the listed sessions plus `more` not
equal to `working` plus `waiting`) is refused `bad-args`, so a client can't skip the list on a
machine with 32 or fewer busy, where `more` is absent and every one must be listed. So past 32
the confirmation covers the left-out sessions by number and kind only, and an unchanged
machine is covered on the first confirmation; one whose busy set grew, where a listed
session's state changed, or where a left-out session gained a question or approval, is not,
and gets the usual answer:
`confirm` at the press, back to `waiting` when the host acts. A press with no `accept` is
covered only while the machine is quiet and its sessions are known.

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
  confirmation (below), and the press carries `accept`: what the person saw.
* **The host checks, twice.** At the press, from its snapshot then, as an early answer: the
  press is taken only if what is busy is [covered by the confirmation](#what-the-phone-sees-before-the-press).
  Otherwise the outcome is `confirm`, nothing is asked of the app, and `state.busy` says what is busy now;
  the client shows the confirmation again with the sessions busy now. The check that decides is the
  same comparison made again **when the host acts**, with the lease held (Connection, decision
  6; [Going](#going-the-lease-and-the-departure) step 3). Both are the host's, as
  `session.stop`'s is, so an old or careless client can't skip the person; they are a seatbelt,
  not the security boundary, which is the signature and the caps.
* **After the press: never over a busy session the confirmation didn't cover** (Product, settled
  decision 3; it is the bridge's view of the sessions, so a session it doesn't see isn't
  counted, as above). When the download is done and checked, the app looks at the machine
  again. It leaves at once if what is busy is covered. Otherwise it **keeps the download and
  does not install**: the phase is `waiting` with `waitingCode` `turn-running`,
  `question-waiting` or `unknown`, and the release installs at the **next quiet moment** (no
  turn running, no question waiting, its sessions known, settled for five seconds: Install
  between turns' gap, never the two-minute wait or a backstop), through the same lease. Or the
  person presses Update now again, sees the confirmation again with what is busy now, and that
  press replaces the waiting one, with no second download. Nothing is downloaded twice.
* **A press lives 15 minutes from its `issuedAt`** (security review, Medium 1), whatever it is
  doing then: downloading, held for a usage check, waiting for a quiet moment, or trying the lease. A press that hasn't
  committed by then is dropped: `last` says `refused` with code `expired`, nothing restarts, it
  spends no restart slot (its own or a shared one) and cancels no pending agent sign-in, and a fresh press with
  a fresh confirmation is needed. The download is never discarded for it: the release stays
  ready, offered as before (Install, Update now, or Restart to update at the machine). So a
  forgotten press can't restart the bridge hours later, unwatched.

`UpdateState` gains, for this, **`waitingCode`** (#1159, not built): with phase `waiting`, why,
as a code a client words itself: `turn-running`, `question-waiting`, `unknown` or `usage-check`. The host's
`waiting` text stays for the log and older clients, and is never shown by a client that knows
the code.

### What the person sees

Creative's rules (#1159, comment 6711605555); the words are the clients', from codes, never the
host's own text (#826). "Bridge", never "host".

* **The button.** **Update now**, beside Install, the same small secondary button, only when an
  update is ready (`available`) and this device can carry it out: a person's device, on a
  machine that isn't read-only, with `canUpdateNow`. When an update is ready but `canUpdateNow`
  is false, the row says why in secondary text instead of the button, as #1142's rows do, from
  `updateNowWhy`: `disabled` "Update now is off on this machine"; `read-only` "View only" (the
  row's own word); `old-app` "Update now needs a newer app there. Install still works.";
  `unavailable` "The app there can't install updates now"; `sign-in` "An agent sign-in is under
  way there"; `busy` "Another update or restart is under way there"; `restart-cap` "Update now
  is available again at *time*" (`updateNowAt`, in the viewer's
  local time). A read-only machine already says "View only", and offers neither. Install keeps
  its words and its wait.
* **One press on a quiet machine** (owner): nothing working, no question or approval waiting,
  and its sessions known. No dialog.
* **Otherwise one confirmation**, the same dialog as the confirm before a more expensive model
  (#1142), and also whenever the bridge can't tell (Product: err toward warning). Title
  "Update *machine* now?" Body: each busy session from `busy.sessions`, by the name the client
  shows for it, with its state in words, "Mid-turn", "Question waiting" or "Approval waiting"; a
  session the client has no name for yet reads "A session"; with `more`, one line after the
  list: "and *N* more". If `unknown`: "Some sessions there
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
  `unknown` "Update ready · waiting until the bridge can tell the sessions are quiet";
  `usage-check` "Update ready · waiting for a Codex usage check to finish". **Update
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
  | `restart-cap` | Update now has used its restarts for the last 6 hours; try after *time* |
  | `busy` | another update or restart is under way there |
  | `sign-in` | an agent sign-in is under way there |
  | `announce-failed` | the bridge couldn't tell your devices it was restarting |
  | `other-profile` | another bridge profile is running there |
  | `usage-check` | a Codex usage check was running there; try again in a few minutes |
  | `disabled`, `read-only` | Update now is off on this machine |
  | `expired` | the request expired before it could go; press Update now again |
  | `future`, `bad-signature` | the request didn't verify; try again |
  | `bad-args` | the request wasn't one this machine understands; try again |
  | `not-person`, `not-peer`, `not-connected`, `local-device` | this device can't update that machine |
  | `needs-roster` | this needs your approved devices first |
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
   per-profile copy). Once the download is checked it holds the usage probes (#238) as every
   install does: the press is in phase `waiting` with `waitingCode` `usage-check`, a hold of
   its own before any quiet check, not "the next quiet moment". It waits up to 11 minutes for a
   Codex usage check to end (settled Q5); waiting spends no restart slot (slots are spent only at
   the commit, step 3) and touches no lease. When the check ends, what is busy is compared again
   with what the person confirmed, as below, before anything else. If the 11 minutes run out
   first, the press ends `refused` with code `usage-check`; if its own 15-minute life runs out
   first (the 11 minutes plus a slow download can pass it), it ends `expired`, as below. A new
   press during the hold replaces this one like one waiting for a quiet moment (it keeps the
   download and the hold, whose 11 minutes don't start again, and has its own 15-minute life). It does not wait
   for a quiet machine unless, when it is ready, what is busy isn't covered by the person's
   confirmation: then it keeps the download and waits for the next quiet moment ([After the
   press](#the-confirmation)), never going on sessions the confirmation didn't cover. Throughout,
   the press lives at most **15 minutes from `issuedAt`**; at that moment, wherever it is, it is
   dropped with `last` code `expired`, before any lease, cap slot or sign-in is touched.
3. The app asks the host to leave: one app-only pipe op (proposed `host.update.depart`,
   `{ "request" }`), the step #870 owns, which #819's installs move to later ([settled decision 1](#settled-decisions)). The
   host takes #805's lease, purpose `Update`, waiting up to 10 s. If an answer, a reply or a stop
   is being typed or delivered, it doesn't get it; it tries again, up to 6 times (about a
   minute), then refuses `not-quiet`, having interrupted nothing. **With the lease held, it
   compares its session snapshot then with the press** (Connection: busy and question state are
   checked when the host acts, not only when the person pressed): what is busy must be
   [covered by the confirmation](#what-the-phone-sees-before-the-press). It does so before
   announcing and again just before committing, and reads the press's 15-minute life again just
   before committing too; a failure aborts the lease (cancelling any announcement) and sends the
   press back to `waiting` with its download (or, past its life, drops it `expired`). This is the update consumer's own
   check, outside the lease's conditions: the lease still adds no idle test. It then announces the
   planned departure (`reason: update`, expected back in 90 s), reads the `Update` purpose's
   eligibility again (read-only, `permissions.remoteManage`, the sign-in entrypoint, another
   profile's host), and commits. **The two counters are checked and spent with the commit**,
   under the same lock `ManageService` keeps its committed restarts in: Update now's own
   allowance if a slot is free, else a slot of the shared cap, else refused `restart-cap` there.
   So a `manage.restart` committed while the update downloaded can't push the shared cap past
   two. Any step failing ends it there, with `announce-failed`,
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
update never may. **Until it lands, a signed Update now leaves on a `Legacy` receipt too, as a
remote restart does** (SRE, settled decision 2), and no automatic update ever does. That
replaces only the announcement, never the quiet check: it still waits for #805's quiet lease and
for what is busy to be covered by the person's confirmation, and it still counts against the
shared cap of 2 restarts in 6 hours, checked when the update commits (Update now's own extra
allowance of 1 in 6 hours, the owner's, is on top: settled Q7). The old way carries no reason a
restart could be traced to an update by (`Legacy` sends no planned state), so the host writes
one fixed line to its own log, "restart for update: *from* to *to*, asked from *device*", and the owner's other devices get the quiet notice above; relay and fleet monitoring
already expect a drop from a remote restart, so no false offline alert. **Switch-over:** once
#815's planned announcement is negotiated, Update now announces through it (`reason: update`)
and the `Legacy` path for Update now is deleted in that same PR. Meanwhile the clients bridge
the gap themselves: a machine whose last `update.state` was `installing` with `requestNow` is
shown "Restarting to update…" for 90 s after its channel closes, then Offline as usual.

### Questions and answers across the restart

* **An answer, reply or stop mid-delivery.** The lease isn't granted while one is being typed or
  delivered, and once committed nothing new is admitted, so the host never stops in the middle of
  one. The attempt ledger (`answer-attempts.json`, written before any key) holds across the
  restart, so a prompt typed into before it is never typed into after it: no answer is typed
  twice (#1100). What crosses the restart less well is the answer's **outcome**: the record that
  lets an identical retry get the first outcome (#1093, #1100) is the host's memory only. A phone
  that missed the result and retries after the restart is told the prompt was already answered
  (the ledger's refusal), not "delivered". **Settled Q4** (Product): the result is saved with
  the durable record of typed answers, so an identical retry after the restart gets the first
  outcome, as before it; the 3 s wait at the departure is not lengthened. Update now can't ship
  until that record carries the result ([build plan](#build-plan), slice 0).
* **A question or approval waiting.** The agents keep running, and a question still pending when
  the new host starts is notified again ([v1-sessions.md](v1-sessions.md#when-a-host-notifies)).
  An approval the host carries through the agent's hook may not survive (#954). So, until #954
  lands, the confirmation names every question and approval the bridge knows is waiting, warns
  that it may have to be answered at the machine afterwards, and treats a machine it can't read
  as busy (settled decision 8). Update now doesn't wait for #954: the owner accepted the
  downtime, and the person decides with the warning in front of them.
* **The agent's hook never approves anything, with the bridge up, gone or back** (security
  review, Medium 2). Its replies are fixed and the same whether the host is up or not
  ([host-hooks.md](../host-hooks.md#the-hook-agent-bridge-host-hook), invariant 14): `{}` or
  nothing for a permission or approval, which leaves the agent's own prompt waiting at the
  terminal, and Copilot's `ask_user` "allow", which only lets the question be drawn there
  (invariant 3), never a tool call. An approval is only ever given by an answer typed into that
  prompt, through the attempt ledger. So it fails closed: a restart, a host that is gone, or one
  back without the approval leaves the prompt waiting, answerable at the machine and, once the
  host reads it again, remotely; Update now can never approve a pending tool call by itself. The
  build keeps a test that says so for every hook with no heartbeat.

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
| What it can't | Pick the release or the feed, downgrade, type anything, read anything new | The same; and it can't skip the lease, so it can't cut a delivery or have an answer typed twice; it can't approve a tool call (the hook never does); it can't replay a press, even across the host's restart (the host's durable record); and a press can't go off later than 15 minutes after it was signed |
| How often | 3 presses a device in 10 minutes; no restart cap (it waits for quiet) | 3 a device in 10 minutes; at most **1 committed restart a machine in 6 hours of its own**, then slots of the **2 in 6 hours** shared with `manage.restart`, all spent only at the commit |
| Who sees it | `last` on every device | `last`, the owner's other devices' quiet notice naming the device, and the app's log |

So the new exposure is availability: at most three bridge restarts a machine every six hours
from Update now (its own one and the two it shares with `manage.restart`), and never more than
three from both together,
each about a minute, during which replies and questions wait, and an approval the host carried
may have to be answered at the machine (until #954). The agents' own work keeps running. The
remedy is the one #801 has: `permissions.remoteManage` off, and #55's roster.

### Build plan

Each slice is its own PR. Slices 0, 1 and 2 are sensitive (answer delivery, protocol, updater and install path) and
need an independent review and a second model family; 3 and 4 touch how a confirmation renders,
so they need one too. **Trust reviews slice 0** as well: answers can contain secrets, and Trust
owns what the host keeps on disk; slice 0 lands only with all of Trust's conditions met
(below).

0. **Answer results across the restart** (settled Q4; must land before Update now ships). The
   durable record of typed answers, `AttemptLedger` (`answer-attempts.json`, written through
   before any key), today keeps only which prompts were claimed; the record that answers an
   identical retry with the first outcome is `AnswerService`'s, kept in the host's memory while
   the prompt's claim is (#1093, #1100). Each ledger entry gains that answer's result: its
   outcome and stage, the sender, session and question it was admitted for, and a hash of the
   answer, which tells an identical retry from a different answer; and, while the
   result is `unconfirmed`, what `AnswerService`'s re-read needs to read the session again (its
   context today, `FirstAnswer.Read`): the agent, the transcript path, the call or tool-use id,
   the read offset; and the answer's words, from which the re-read rebuilds the plan it compares,
   kept apart from the ledger (below). **This departs from #1100's hash-only plan on purpose:** the re-read compares the
   words the session recorded with the words typed, which a hash can't do once the host that
   typed them is gone. The agent's own transcript already holds the same words on the same disk,
   so the only new copy is the host's. **Trust** accepted it on conditions (#1161, comment
   6712503332): "Acceptable, with conditions; none of it may go ahead without all of them." The
   rules for that copy, which `AnswerService`'s memory follows too, so a retry is told the same
   before a restart as after it:
   * **Where it lives: not in the ledger** (Trust condition 1). `answer-attempts.json` (#1108)
     is today readable by the `CodexSandboxUsers` group through its folder's ACL, so the words
     never go into it, and its ACL is left as it is. They go in a separate file,
     `answer-words.json`, in a new subfolder `private\` of the host's profile folder whose ACL is
     protected (nothing inherited) and grants this Windows user and SYSTEM alone, set when the
     folder is created. Before writing any words the host checks that ACL; if anything else can
     read the folder or the file, it keeps no words and refuses the answer as below, before any
     key. The ledger entry keeps only a key to its words.
   * **Text only** (condition 6). The file holds the answer's text as sent (each field's, for a
     form) under that key, and nothing else: not the question, its options, the keys typed or
     the transcript. The re-read context (agent, transcript path, call or tool-use id, read
     offset) stays in the ledger entry, and the plan the re-read compares with is rebuilt from
     the text. It is written atomically to a temporary file in `private\` (so it carries the
     folder's ACL) and renamed over the old one with no backup file; it is never memory-mapped,
     a failed write deletes its temporary file, and no other copy is kept.
   * **Held only while unconfirmed** (condition 3). The words are deleted at the first of: the
     result turning anything but `unconfirmed` (`delivered` included); the claim expiring or
     being cancelled (the session or its question gone, the claim released, or the ledger's own
     `Keep`); and, for an `unconfirmed` entry nobody retries, the end of the claim's life, and
     never later than 24 hours after they were typed (a delete that fails is retried and counted
     as under Startup sweep until it succeeds). Without the words, the entry keeps its
     outcome, stage and hash, so an identical retry is still told nothing was typed again
     (#1100's reply), never typed again.
   * **Startup sweep** (condition 4). A host that starts deletes, before serving, every leftover
     temporary file in `private\`, the words of every entry past an exit above (expired or
     cancelled claims included), and words with no ledger entry. **A failed delete fails
     closed:** that entry is treated as having no words, so a retry gets #1100's reply
     (`unconfirmed`, nothing typed again) and is never re-read from stale words; that is how
     Trust's "refused, not retried" is met, since nothing is typed or read from them.      Trust
          confirmed that reading (#1161, comment 6712803540).      The delete is tried again at every write, at every start, and before
          every cap check (so an answer that finds the cap full first retries the pending deletes,
          then checks again, with no restart needed), and until it succeeds the words stay in `answer-words.json` and **still count against
          the caps below**.
   * **Bounds** (condition 5). At most 32 entries hold words, at most 16 KiB of words each and
     256 KiB in all, counting words whose delete failed. The protocol's own request limit (`Limits.AnswerRequest`, 16 KiB) already
     refuses a longer answer with its own reason; an answer that would pass the entry or total
     cap is refused before any key, as a `refused` answer whose reason says to answer at the
     machine (the words added to v1-sessions.md's answer reasons in this slice). Nothing is
     evicted to make room and no words are truncated. So a delete that keeps failing fills the
     caps and the host stops accepting new answers from other devices (each refused, saying to
     answer at the machine) until a delete succeeds and frees the space; it never evicts
     silently.
   * **Never anywhere else** (condition 2). The words never go into a log, diagnostics, a
     support bundle, `host-state`, a local-API response, a trace, an error message, or anything
     sent to the relay or a device; only their outcome does, as today. Trust: "Compliance review
     is not needed if condition 2 is tested; if any path can reach a bundle or diagnostics, bring
     Compliance in." So slice 0 needs no Compliance review while its canary test passes, and gets
     one if any path is found to reach a support bundle or diagnostics.

   The ledger is updated when
   the outcome settles and when an `unconfirmed` one is read again as `delivered`. A host that
   starts reads it back, so an identical retry from the
   same sender after a restart gets the first outcome, and a different answer or sender still
   gets the plain refusal, as before the restart. The 3 s `LeaveAnyway` wait is unchanged.
   Tests, one for every exit (Trust condition 7): "an answer delivered before a restart is told
   delivered when retried after it"; "an unconfirmed answer retried after a restart is read
   again and never typed again"; "a different answer after a restart is still refused";
   "delivered: the words are deleted"; "a refused or stale answer's words are deleted";
   "expired: the words go when the claim expires"; "cancelled: the words go when the claim is
   released or the session or question goes"; "an unconfirmed answer nobody retries loses its
   words when its claim's life ends, and by 24 hours at most, and a later retry types nothing,
   before a restart and after it alike"; "crash sweep: a host starting after a crash deletes
   leftover temporary files, expired or cancelled claims' words and words with no entry"; "a
   failed delete gets #1100's reply on retry, never a re-read from the words"; "cap reached: the
   33rd entry or the 256 KiB total is refused before any key, saying to answer at the machine,
   and nothing held is evicted or cut"; "cap after a failed delete: words whose delete failed
   still count, so a full cap refuses the next answer, and once the delete can succeed the next
   answer's cap check retries it and is admitted, with no restart (and at start too)"; "ACL: `private\` and `answer-words.json` grant this user and SYSTEM
   alone, after every rewrite too, and a wider ACL keeps no words"; "`answer-attempts.json`
   never holds an answer's words"; "canary: an answer carrying a known canary string, driven
   through delivered, unconfirmed, refused, a failed delete and a restart, never appears in the
   logs, diagnostics, a support bundle, host-state, local-API responses, traces, error messages
   or relay and device messages".

1. **Host and protocol.** `Update.cs`: `now` on the command, `canUpdateNow`, `busy`,
   `requestNow`, `waitingCode`, outcome `confirm`, `code`. `UpdateService` verifies `now` through the same
   signed-command check as `ManageService` (one shared gate, not a copy), keeps Update now's own
   allowance beside the shared cap it counts with `ManageService`, computes `busy`, and passes `now` asks to the app. The app-only departure op takes
   the lease, announces and commits; the `Update` purpose's conditions are registered at
   composition (read-only and `remoteManage`, the sign-in entrypoint, another profile's host).
   The #870 lane owns the departure step's contract and Daily owns the Update now caller; a
   change to the step is reviewed in both PRs together (SRE, decision 1). `LifecycleCoordinator`
   grants `Legacy` to a person-signed `Update` departure as it does to `RemoteRestart`, never to
   an automatic one, and host-lifecycle.md says so (decision 2). `ManageService`'s header comment changes from "never
   forced, never carried out later (owner, Q-O7)" to "never forced; a restart is never carried
   out later (owner, Q-O7); an update a person asks for now (#1159, owner 2026-11-07) waits for
   its download, up to 11 minutes for a Codex usage check and up to a minute for the lease, lives at most 15 minutes from its signature,
   and goes over no busy session its confirmation didn't cover: if the machine became busier
   meanwhile it keeps the download and installs at the next quiet moment within that life
   (Product and the security review, #1161)". Vectors in `update.json`
   and `signed-command.json`. Tests, among others: "Update now on a quiet machine is accepted on
   one press"; "Update now with a turn running and no accept is answered confirm and asks the
   app nothing"; "Update now goes over no busy session the confirmation didn't list"; "with 40
   busy sessions, a confirmation of the 32 listed and 8 more is covered while nothing changes,
   and not once a ninth unlisted session or a listed session's new question appears"; "with more
   than 32 busy, an unlisted turn that gains an approval while a listed approval clears is not
   covered"; "an accept with `more` and a short list is refused bad-args"; "Update
   now is refused on a read-only machine, reads included"; "Update now is refused with remote
   management off"; "Update now from this machine's own app is refused"; "an Update now signed
   more than two minutes ago is refused"; "Update now spends its own slot before a shared one";
   "an Update now is refused when its own slot is spent and a remote restart and an Update now took the shared ones";
   "a remote restart never spends Update now's own slot"; "a remote restart committed while an
   Update now downloads, taking the last shared slot with the own slot spent, leaves the Update now
   refused restart-cap"; "busy names a waiting approval as an approval and never carries any text"; "the departure is refused
   while an answer is being typed, and nothing is typed twice"; "an Update now press with no
   signed request is refused, never taken as an Install"; "a plain Install can't be replayed as an
   Update now"; "a turn started after the press sends the Update now back to waiting, with its
   download, when the host acts"; "a request the host verified is refused as the same press
   after the host restarts"; "a press carrying request beside now is refused bad-args"; "an
   Update now past 15 minutes from its signature is dropped expired, spending no cap slot and
   cancelling no sign-in"; "a committed update departure
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
   Update now pressed again while one waits replaces it without downloading again"; "a waiting
   Update now 15 minutes past its signature is dropped expired and keeps the download offered";
   "an Update now waits in usage-check while a Codex usage check runs, spending no restart slot";
   "when the usage check ends, busy is compared with the confirmation again before the lease";
   "an Update now still held by a usage check after 11 minutes ends refused usage-check"; "an
   Update now whose 15 minutes run out during the usage-check wait ends expired, not
   usage-check"; "a new press during the usage-check hold replaces the first without restarting
   the 11 minutes".
3. **PWA.** Update now beside Install, or the row's reason when it can't be pressed, the
   confirmation, signing the press with `accept`, the `confirm` outcome shown as the
   confirmation again, the lines while it goes, the 90 s "Restarting to update…" across the
   channel's close, and the result in words from `code`. Unit tests for the lists and words
   from `busy` and `code`, and Playwright: "a quiet machine updates on one press", "a busy
   machine lists each busy session by name and state, and sends what was shown", "past 32 busy
   sessions the confirmation lists 32 and says how many more, and sends both", "the
   confirmation's focus starts on Cancel", "Cancel sends nothing", "a confirm outcome shows the
   sessions busy now", "a machine that can't update now says why instead of the button", "a
   refusal shows its fixed reason, never the machine's text", "a waiting approval is named and
   warned of", "a machine the bridge can't read asks first", "an update waiting for a turn says
   so from its code, never the machine's text", plus the demo's states for the
   visual check.
4. **Windows app, for another machine.** The Machines row's Update now (or its reason) and a
   `ContentDialog` with the same list, words and buttons, focus on Cancel, as #1142's model
   confirmation, through `App.Core` view-model tests mirroring the PWA's.

Order: 0 (Update now ships only once it has landed), 1, then 2, then 3 and 4 together.

### Settled decisions

- **Q1. One departure step with #819** (SRE, 2026-11-07, on #1161): "agree with your proposal. Only
   Update now uses the shared departure step until the announcement exists, and automatic
   updates keep their current path. Ownership: the #870 lane owns the step's contract. Daily owns
   the Update now caller. If the step changes, both PRs get reviewed together." Automatic
   installs (Restart to update, Install, Install between turns) move to the step only once
   #815's planned announcement exists, since they may never take `Legacy`.
- **Q2. Announcing the old way until the planned announcement exists** (SRE, 2026-11-07, on #1161,
   comment 6711994130): "Update now may announce the old way (as a remote restart does) until
   the planned-update announcement exists." Its conditions: the restart "still counts against
   the shared cap of 2 in 6 hours, checked when the update commits" (Update now's own extra
   allowance of 1 in 6 hours is on top, settled Q7); "It still waits for the #805 quiet lease or
   the person's confirmation. The old announcement replaces only the announcement, never the
   quiet check"; "Mark the announcement so it can be traced to an update, if the old way allows a
   reason. If it cannot, record a 'restart for update' line in the host's own log" (it cannot:
   `Legacy` sends nothing, so the host logs the line); and "When the planned announcement lands,
   switch over and delete the old path in the same PR."
   Specified in [Announced as planned](#announced-as-planned-498).
- **Q3. A machine that turns busy during the download** (Product, 2026-11-07, on #1161): "finish the
   download but do NOT install. The phone shows 'Update ready - waiting for the current turn to
   finish'. It installs at the next quiet moment, or the person presses Update now again and sees
   the confirmation again. Never interrupt a running turn without a fresh confirmation, and never
   discard the download." Specified in [The confirmation](#the-confirmation) (After the press)
   and [What the person sees](#what-the-person-sees); the line is written with the middle dot
   of the existing "Update ready · waiting for a question to be answered". The security review
   (#1161, Medium 1) bounds the wait: the press lives 15 minutes from its signature, then is
   dropped `expired` with the download kept, so "the next quiet moment" is one within that life.
- **Q6. The op's shape** (Connection, 2026-11-07, on #1161): use `now` on `update.install` as
   drafted, not a new signed op. Connection's conditions: "the SignedCommand must bind the
   request uuid, the target version and the `now` flag, so a plain Install cannot be replayed
   as a forced one; the host re-checks busy/question state at the moment it acts (not at press
   time); a relay or older host that does not know `now` must refuse, never fall back to a plain
   Install." No new relay work: the relay carries the Noise channel's bytes and reads no
   `update.*`. Specified in [Ops](#ops) (the request only inside the signature, so an older
   host refuses), [The press](#the-press) (what the signature binds) and
   [Going](#going-the-lease-and-the-departure) step 3 (the check when the host acts).
- **Q8. A question or approval waiting before #954 lands** (Product, 2026-11-07, on #1161): "Warn
   about a waiting approval or question even before #954 lands: if the bridge knows of one, the
   confirmation names it; if it cannot tell, treat the machine as busy and show the
   confirmation. Err toward warning." Specified in [What the person sees](#what-the-person-sees)
   and [Questions and answers across the restart](#questions-and-answers-across-the-restart).
- **Q7. The cap** (owner, 2026-11-07, on #1161): "Update now gets its OWN extra allowance of 1
   restart per 6 hours, on top of the shared cap of 2 per 6 hours (manage.restart and automatic
   restarts keep the shared 2)." Confirmed by the owner on #689 (comment 6712104700). The extra
   allowance is Update now's alone: `manage.restart` never spends it, and an automatic install,
   which waits for quiet and counts against no cap today, gains none. An Update now spends its own slot first, then a shared one;
   with both used it is refused `restart-cap`, worded "Not updated: Update now has used its
   restarts for the last 6 hours; try after *time*". #55's roster changes the shared cap only.
   This refines SRE's Q2 condition that an Update now counts against the shared cap: it does,
   once its own slot is used, and both are checked when the update commits.
   Specified in [What still refuses](#what-still-refuses-and-why) and
   [Going](#going-the-lease-and-the-departure) step 3.
- **Q4. An answer's outcome across the restart** (Product, 2026-11-07 12:00 PT, on #1161): "(a):
   save the answer result with the durable record of typed answers, so a person never loses or
   retypes an answer across the restart; do NOT lengthen the 3 s wait." Specified in
   [Questions and answers across the restart](#questions-and-answers-across-the-restart) and
   [build plan](#build-plan) slice 0, which extends `AttemptLedger` with `AnswerService`'s
   first-outcome record and must land before Update now ships.
- **Q5. Usage probes** (Product, 2026-11-07 12:00 PT, on #1161): "keep the 11-minute wait for a
   Codex usage check (#238) for now; if Update now is refused for that reason, the phone shows
   why in plain words." Specified in [Going](#going-the-lease-and-the-departure) step 2
   (`waitingCode` and code `usage-check`) and the reasons table in
   [What the person sees](#what-the-person-sees).

### Open questions

None open: Q1 to Q8 are settled above.

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
