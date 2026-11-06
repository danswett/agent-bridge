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
| Only a device the relay lists as `pwa` or `app` (a person's phone, browser or Windows app) | Another machine's host is where agents act. As for folder-trust answers, the kind is what a device says it is: something holding the account's token could claim `pwa`, and all it could then do is install the machine's own release once no session there has been busy for two minutes |
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
| `update.install` | `{ "request": "<uuid>" }` | `UpdateCommandResult` |

`request` is the device's UUID for one press; the same press sent again is the same ask.
`update.check` and `update.install` without one fail with an error.

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

An unknown `phase` reads as `idle`. Text fields are cut to 64 bytes (versions), 512 (reasons)
and 4 KiB (notes).

### `UpdateResult`

| Field | Type | |
|---|---|---|
| `outcome` | `updated` `current` `refused` `failed` | `updated`: running the new release now. `current`: there was nothing newer. `refused`: not attempted, and `reason` says why. `failed`: attempted, and still the old release. An unknown outcome reads as `failed`, never as `updated` |
| `from`, `to` | string? | The releases |
| `reason` | string? | |
| `request` | string? | The press it answers; absent for one the person started at the machine (Restart to update), or an automatic one before #516 |
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
| `outcome` | `accepted` `refused` | `accepted`: the machine's app has been asked, and how it goes is in the state (`request`, then `last` with this request). A Check that finds a newer release ends instead with `request` gone and `available` and `checkedAt` set (`last` stays how the last update, or a check finding nothing, ended). `refused`: nothing was asked |
| `reason` | string? | Why it was refused |
| `state` | `UpdateState`? | The state as the press found it |

The host refuses a device that is not connected to the relay now, one that is not a person's,
and a press with no app to carry it out. The app's refusals (another profile running, the host
gone, a build folder) come back as `last` with `outcome: "refused"`, since the host has already
answered `accepted` by then. A press the app has not taken within ten minutes lapses at the
host, and an app that comes to it later (from a status it was slow to read) refuses it, with
`last` saying so, rather than carrying it out late.

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
nothing newer.
