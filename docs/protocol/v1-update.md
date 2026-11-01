# Protocol v1: updates (`update.*`)

Another of the person's devices sees a machine's updates and asks for one: the PWA and the
Windows app list, under each machine that offers them, the release it runs, a newer one once a
check has found it, what its app is doing about it, and how the last update ended, with
**Check**, **Install** and **Check all** (#51).

The Windows app on that machine does the work, as it does its automatic updates
([app-windows.md](../app-windows.md#updates-velopack-one-version-for-app-and-host)): it finds the release on the machine's own
update feed (never agent-ha-bridge's, design G2), downloads it, waits until no session there has
a turn running or a question waiting for two minutes (or six hours, whatever they are doing),
then stops the host and restarts both on the new release. The host only carries what its app
reports to the devices, and their presses to its app. It decides who may press.

## Who may ask

| Rule | Why |
|---|---|
| Through the relay only (a Noise channel); never on the machine's own pipe | The pipe is where agents, the MCP server and the Home Assistant connector are. The app beside the host updates its machine from Settings |
| Only a device the relay lists as `pwa` or `app` (a person's phone, browser or Windows app) | Another machine's host is where agents act. As for folder-trust answers, the kind is what a device says it is: something holding the account's token could claim `pwa`, and all it could then do is what the app does unasked by default, install the machine's own release once no session is busy |
| Nothing on a read-only machine (`delivery.readOnly`, or agent-ha-bridge running for this user, G5) | hello offers no `update`, and every `update.*` is refused, reads included, as #112 withdrew Home Assistant's Install there |
| Not while another profile's app or host runs from the same installation | Velopack stops every process in the package, busy or not, and restarts only the profile that installed. The app refuses at once, before anything is downloaded, and again just before it stops the host |
| Not when the app is not running, or cannot update (a build folder rather than an installed copy) | Nothing would carry it out |

Being asked stands in for the app's "Install updates automatically" switch, for that release:
an asked install goes ahead with the switch off, by the same idle rules.

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
| `available` | string? | The newer release the last check found, from a device's press, the app's own schedule or its Settings; still there if its download failed, gone once a check finds nothing newer |
| `notes` | string? | Its release notes |
| `checkedAt` | time? | When the app last checked |
| `phase` | `idle` `checking` `downloading` `waiting` `installing` | `waiting`: a release is downloaded and will install (automatic updates are on, or a device asked) once the host is idle |
| `progress` | int? | 0–100 while downloading |
| `waiting` | string? | While waiting: for what (a session busy, another profile running) |
| `request` | string? | The press being carried out now, if one is |
| `automatic` | bool | Whether the app installs updates without being asked |
| `last` | `UpdateResult`? | How the last update asked for, or installed by itself, ended |

An unknown `phase` reads as `idle`. Text fields are cut to 64 bytes (versions), 512 (reasons)
and 4 KiB (notes).

### `UpdateResult`

| Field | Type | |
|---|---|---|
| `outcome` | `updated` `current` `refused` `failed` | `updated`: running the new release now. `current`: there was nothing newer. `refused`: not attempted, and `reason` says why. `failed`: attempted, and still the old release. An unknown outcome reads as `failed`, never as `updated` |
| `from`, `to` | string? | The releases |
| `reason` | string? | |
| `request` | string? | The press it answers; absent for an automatic update |
| `at` | time | |

An install restarts the app and host, so its outcome is reported by the new version: the app
keeps the press it is carrying out in its preferences when it hands over to the updater, and its
first start on the new release reports `updated` (or `failed`, "the update did not take") with
that `request`. A device following the machine sees its channel close as the host stops, and
the outcome when it reconnects.

### `UpdateCommandResult`

| Field | Type | |
|---|---|---|
| `outcome` | `accepted` `refused` | `accepted`: the machine's app has been asked, and how it goes is in the state (`request`, then `last` with this request). `refused`: nothing was asked |
| `reason` | string? | Why it was refused |
| `state` | `UpdateState`? | The state as the press found it |

The host refuses a device that is not connected to the relay now, one that is not a person's,
and a press with no app to carry it out. The app's refusals (another profile running, the host
gone, a build folder) come back as `last` with `outcome: "refused"`, since the host has already
answered `accepted` by then. A press the app has not taken within ten minutes lapses at the
host.

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
