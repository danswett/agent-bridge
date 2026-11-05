# The Windows app (P3 core)

The first shippable desktop app: a WinUI 3 window over this machine's Slipgates host, the
host's installer and manager, and toasts that take answers. It is a client of the host,
never the host itself (design §3.2): closing the app never stops a session's bridge, and
everything the app does goes through the same protocol ops and the same host code as the
phone.

```
 AgentBridge.exe (WinUI 3, unpackaged)          agent-bridge-host.exe serve
 ┌──────────────────────────────┐   local pipe  ┌───────────────────────────┐
 │ Sessions · Usage · Settings  │◄─────────────►│ SessionService (as relay) │
 │ toasts (own COM activator)   │  v1 ops/events│ HostControl (host.* only) │──► relay (optional)
 │ tray · Velopack updates      │  + host.*     │ delivery via injector     │
 └──────────────────────────────┘               └───────────────────────────┘
        │ registers / runs                              ▲
        ▼                                               │ at logon
 Task Scheduler: "AgentBridgeHost_<profile>_<user>" ── agent-bridge-hostw.exe (no window)
```

## Decisions

### Local API: a named pipe owned by the host

Settings' **Agents on this machine** section uses the host's shared readiness report
(`#384` foundation, partial `#231`), with executable/version detection, hook ownership/intake
evidence and separately labelled current-run hook activity. **Check agents again** requests
a fresh host scan. CLI sign-in, trust and new-session activation stay explicitly unverified;
this is not yet the first-run installer. Each row offers one verb for its hooks' state (#678):
**Set up** while they aren't set up (no hooks, this profile's intake off, or intake on with no
owned setup record for this agent), **Repair** only when they're broken, and none when nothing
needs doing: working, held by another profile or a read-only host, or set up (an owned record) but
not yet tested by a new session, which the host can't do yet; that row says "Not tested yet" (#705), never "Ready". The host's `hookSetup` (`notSetUp`, `incomplete`, `completed`, `unknown`) says which: `completed` is set up and not tested yet, `notSetUp` offers **Set up**, and `incomplete`, a setup that failed after the host saved its record, offers **Repair** across fresh checks; a held or read-only agent still offers none. App.Core reads it through one adapter (`HookSetups`), which reads a missing field as `unknown`. With `unknown`, or a host that doesn't send it, the row falls back on the hooks detail, and a row whose own setup failed keeps its failure and **Repair** through a fresh check. **Remove connection** comes after it, in Danger text, never an accent button. Both ask for
confirmation naming the agent (Cancel is the default) and use the host's owned hook backend. The
buttons are named for their agent ("Set up Copilot CLI", "Remove Copilot CLI's connection"), with
AutomationIds of their own (`SetUpAgent_<id>`, `RemoveAgent_<id>`). A setup's progress ("Setting
up…", "Repairing…", "Removing…") and result stay on its own row (`AgentStatus_<id>`), a polite
live region named for its agent ("Copilot CLI: Setting up…") and raised again as each is drawn,
once per status: a raise for a status the row has moved on from is skipped. A failure is in Danger. Successful changes refresh
readiness evidence, and restart remains a separate person's action. No CLI sign-in, hook trust or workspace approval is implied.
Older hosts explain that an update
and restart are needed, and disconnected hosts show no stale agent rows. See
[agent readiness evidence](protocol/local-pipe.md#agent-readiness-evidence) for the contract.
Version commands are contained in Windows process jobs; non-Windows hosts keep installation
and ownership detection but explicitly report version probing unavailable for now.
Home Assistant setup, configuration and verification are outside the first-run design
while its model is redesigned (#384); existing integrations are unchanged.

**Install CLI** is a separate, cancel-default confirmation for missing Copilot, Claude and
Codex executables. It uses Windows Package Manager's community `winget` source with fixed
IDs `GitHub.Copilot`, `Anthropic.ClaudeCode` and `OpenAI.Codex`, keeping package hash checks
and Windows policy intact. Consent covers source/package agreements and required runtimes:
Copilot may need PowerShell, and Codex may need ripgrep and the Visual C++ runtime.
User scope applies to the selected CLI, **not necessarily its dependencies**; required
runtimes can request Windows administrator approval. No existing CLI is upgraded,
no package manager is silently bootstrapped, and no credentials, hooks, folder trust or
permission bypasses are configured. Agency remains organization-managed.
Missing App Installer or an explicitly configured executable gets actionable feedback.
Progress belongs to the host and is recovered by new Settings windows and after reconnect.
A lost reply leaves cancellation visible instead of implying the installer stopped.
The host contains WinGet in a process job, confirms cleanup before another install, and
cancels and awaits active installation cleanup before shutdown or replacement. Unconfirmed
cleanup blocks further installs in that host and requires checking Windows installation
activity before restarting. Cancellation cannot promise rollback of external installers,
and Windows deployment services outside the job may already have changed the machine.
The host refreshes executable discovery
before reporting installation and Settings rechecks readiness even after incomplete work.
This is not yet a complete first-run wizard or a clean-machine qualification.

* **Same protocol as the relay channel.** The pipe carries the v1 messages exactly as a
  Noise channel does (`{id, op, data}`, `{re, ok, result|error}`, `{ev, seq, data}`), so
  `SessionService` serves both through one interface (`IProtocolChannel`). Answers,
  replies, attachments, stops and usage behave identically from the app and the phone.
  `push.*` is the one exception: Web Push is for devices through the relay, and the app
  has its own toasts.
* **Framing:** a 4-byte little-endian length, then that many bytes of UTF-8 JSON, at most
  the size of one Noise transport message. Details in
  [protocol/local-pipe.md](protocol/local-pipe.md).
* **No Noise locally.** The pipe never leaves the machine (its ACL denies network logons),
  and both ends are checked by the OS: only the current user can open it, and the host
  checks the connecting process's user and elevation. Anything that could read or forge
  pipe traffic already runs as this user, and could read the device key (DPAPI) and the
  transcripts directly, so encryption would add key management and no protection.
* **Who may connect:** the pipe's DACL grants the current user only and denies `NETWORK`;
  the first instance is created with `FILE_FLAG_FIRST_PIPE_INSTANCE`, so a pipe someone
  else created first under the name makes the host fail loudly rather than share it; the
  client opens with `CurrentUserOnly`, which checks the pipe's owner; the host refuses a
  client process of another user, or of a different elevation (a medium-integrity app
  must not drive an elevated host, and an elevated app talking to an unelevated host is
  a sign something is wrong). macOS and Linux use a Unix socket in the profile folder,
  which is `0700`.
* **Host management ops are local-only** (`host.status`, `host.subscribe`, `host.signIn`,
  `host.settings`, `host.restart`, `host.stop`): the relay path never dispatches them.
* **Only the app manages the host.** The host traces each connection's process and its
  ancestors. One that is, or runs inside, an agent session (a live session's process, one the
  host launched, or any agent CLI) may read sessions and nothing else; the app is
  `AgentBridge.exe` (the app) beside the host, started from anywhere but an agent session. Anything
  else of the user's keeps the sessions ops but not management. A host that refuses the app's
  stop or restart is left running, never ended, and the install, update and uninstall hooks do
  nothing to the host from inside an agent session. A safety rail against an agent restarting
  its own host, not a security boundary
  ([local-pipe.md](protocol/local-pipe.md#who-may-manage-the-host)).

### Other machines: through the relay, as a device of the app's own

The app shows every machine on the account, as the PWA does
(`pwa/js/fleet.js`): this machine's sessions over its pipe, and each other machine's over a
Noise channel through the relay, carrying the same v1 ops. Answers, replies, End session,
usage and toasts work on another machine's cards exactly as on this one's, each going to that
card's own host. A reply's file over 256 KiB, up to 100 MiB, goes to another machine through
the relay's blob store as the PWA's do (sessions.attach.blob, [blobs.md](protocol/blobs.md)):
sealed here with a key of its own, uploaded straight to storage, and fetched, opened and
checked by that machine's host; the key travels only inside the channel. Where the machine or
the relay does not offer it, files go up to 256 KiB, and an image is shrunk to fit.

* **Its own device, the host's account.** The app connects to the relay with a P-256 key of
  its own (kind `app`), kept like the host's (in the TPM, or with DPAPI) in
  `%LOCALAPPDATA%\AgentBridge\app\device\<profile>`. It never uses the host's key, so the
  relay sees two devices, and the host's own connection is never replaced. For the account it
  asks this machine's host (`host.relay.account`, [local-pipe.md](protocol/local-pipe.md)),
  which hands it to the app alone, like every management op, so there is no second sign-in, and
  the token only ever goes to the relay that issued it. It asks again when the host signs in
  to another account, and after the relay refuses it.
* **One copy per person.** The relay keeps one connection per device key, and the
  single-instance lock is per Windows session, so the same person signed in twice runs the
  app twice with one key. Only the copy holding `relay.lock` (in the key's folder, open for
  the app's lifetime) connects; the other shows no other machines and says where they are, and
  takes over when the first copy ends. The key is loaded or created under a second, brief lock
  (`init.lock`), so two first starts at once make one key, not two. If the relay still replaces the app's connection
  (`4009`), the app does not reconnect on its own, as the host does not: it waits until this
  machine's host signs in again or comes back, so two copies never evict each other in a loop.
* **Which machines.** Every host in the relay's presence for the account, other than this
  machine's own (its device id is the `machine.id` its hello gives), which the app reaches over
  its pipe. Each gets a connection that subscribes to sessions and usage and reconnects as the
  pipe's does. A host that refuses this device is not a lost connection (#602): an error in reply
  to hello, or to the subscribe after a hello that offered nothing to read (no `sessions.read`, as
  a host that has not approved this device answers). It changes only at that machine, so the app
  logs it once, not at every attempt, and asks again after 5 minutes (`Fleet.RefusedRetryDelay`),
  or sooner when the account's machines change, that machine comes back online or it is opened,
  though never sooner than the ordinary 5 s. Any other failure is retried every 5 s, as before.
  Of a host that offers `sessions.patch`, either connection asks for each change as only what
  changed, and, reconnecting to the same run of that host with a list it held whole, for only the
  sessions changed since and the ids of the live ones
  ([v1-sessions.md](protocol/v1-sessions.md#patches-sessionpatch), #601). A patch it can't apply,
  or a resumed snapshot that can't be whole, asks for the full snapshot, as a gap does.
  Session writes posted to the UI are applied only while their channel is still followed,
  checked when the post runs, not when it was queued (#547). A replaced channel cannot
  restore or remove a session, refuse the current list, or change its discovery hold.
  A discovery change updates a pending snapshot end only on that snapshot's channel.
  A machine that leaves presence while the app's relay connection stays up on the
  same account stays (#269): its rows, the session open, its tab, your order and pins are kept,
  each row saying "Offline · last seen …", and its connection goes on trying. Nothing on those rows
  can be answered, approved, replied to, attached to, ended or launched on, and their questions
  are not in Needs you, the badges or toasts (a question toast it raised is withdrawn, and raised
  again quietly if still asked once it is back). Its rows are acted on again only once the
  snapshot after it returns has confirmed them: rows that ended meanwhile go then, and a question
  that ended never becomes answerable. Every answer, reply and End session passes one gate
  (`Deliveries.Hold`, set by `Machine`), so a toast's button is held too. It goes, with its cards
  and toasts (answer outcomes too), once forgotten or after 15 minutes offline (`Fleet.OfflineKeep`,
  as the PWA), and stays listed with the offline machines. While the app's relay connection is down,
  every machine stays as it was and reconnects when it is back. With no account to find them
  through (this machine's host signed out, or refused), they all go, and so do the last account's
  when the host signs in to another account or relay, without waiting for the new one to connect. The
  app serves nothing on the relay: it only opens channels, as the PWA does.
* **Offline machines.** The account's hosts that are not connected, from the relay's roster
  ([roster.md](protocol/roster.md)), are listed at the foot of the Machines page with when each was last seen,
  never this machine's own. **Forget** takes two presses, the first arming for 5 seconds; the
  app is a person's device (kind `app`), so the relay accepts it, for a machine that is not
  connected. One that comes back leaves the list, its arm with it. A relay without a roster
  lists none.
* **Not the app's management.** Another machine's host is never managed from here: `host.*`
  is the pipe's alone, and a relay channel answers it with `unknown op`.
* **On screen.** With more than one machine, machine tabs over the list show every machine or one
  of them (those that don't fit under "N more", never scrolled sideways, #290), and each row names its machine on line 2. Usage merges every machine's
  readings into one row per allowance, the freshest reading of each, as the PWA's does, asks
  each host for the readings it keeps from the account's other machines (`usage.shared`), and
  keeps the last reading of each allowance between runs in `<profile>-usage.json`, so a reading
  outlives its machine going offline; a row's age says `read on <machine>` in its tooltip and
  accessible description unless this machine read it (#172), not on the line, where the account
  is shown whole instead, and the summary names each allowance's account (#225). A toast for
  another machine's question says which machine, and its answer goes there.
* **Needs you** (#165, [ui.md](design/ui.md) §6.1). The list pane's first section, above the rows (#180), every pending question on
  every machine, whatever the machine tabs show, oldest first: the session's title, the question's
  first line (an approval's whole prompt, when it fits), the machine and agent, how long it has
  waited, and a delegate's name when a delegate asks. Approve once and Deny press the card's
  own buttons (its `QuestionViewModel`), offered only for Copilot's prompts, which a host writes
  whole and literal, and only under the whole prompt (600 characters and 12 lines at most, no
  table, code wrapped, shaped as a host writes it: prose, then at most one fenced block ending
  it, its fence naming no language but json or diff, no link, whose words hide where it goes, and no
  character drawn other than where it runs, such as a bidi control, a zero-width space, a
  variation selector, a right-to-left letter or a blank-looking space (U+00A0, U+2800, #546); a hidden character
  the entry's line shows as an escape like `\u202E`) since Approve once allows all of it; a
  longer approval, a Codex one, a
  form and anything this app can't answer open the session in the session pane, its row selected; a prompt marked
  `answerAtMachine` says so, with no controls. The lane never moves a row. Its count is the
  tray's "Slipgates · N need you" and the Sessions badge.

### Starting the host at logon: a scheduled task

Per §3.1 and §3.2, a per-user scheduled task with a logon trigger, `InteractiveToken`
and `LeastPrivilege`, so the host runs unelevated in the person's interactive session,
where delivery can reach their consoles (§6.2). A task rather than a `Run` key because it
never stops the host on battery or after 72 hours, and does not depend on Explorer's startup
list. Non-admin users can register a task for themselves; nothing here needs elevation.

* Name: `AgentBridgeHost_<profile>_<hash of the user SID>` (G3: never `AgentBridgeDaemon*`,
  which agent-ha-bridge and the G5 probe use).
* Action: `agent-bridge-hostw.exe serve --profile <profile>`, a tiny `WinExe` beside the
  host that starts `agent-bridge-host.exe` with no console window and waits for it, so no
  console flashes at logon. The host itself stays a console program for the CLI.
* A host that crashes is started again by the launcher, not by Task Scheduler: the task's
  restart-on-failure acts only when the task fails to launch, never when its process exits
  with an error, so a crash used to leave the machine without a host until the next sign-in
  (#106). The launcher starts it again after 3 s, 6 s and 12 s (each under the app's 20 s
  start timeout, so a Start pressed meanwhile gets that host), then gives up and says so in
  the host's log, which the app shows. A host that ran 10 minutes before crashing has its
  attempts back. Only a crash counts, by exit code: an unhandled exception (0xE0434352) or
  another NTSTATUS error or exception code (off Windows, a fatal signal such as SIGABRT),
  including one on the way out of a `host.restart`. Under the launcher, `host.restart` ends
  the host with code 75 once it has fully stopped, and the launcher starts the replacement at
  once and keeps watching it. Everything stopped on purpose ends with a small code and is never
  started again: 0 for a stop from the app, `host.stop`, an update or an uninstall; 2,
  3 (another host has the profile) and 4 (refused); -1 when the app ends a host that would
  not stop; 1 from Task Manager; 0xC000013A for Ctrl+C or logging off. A Stop from the app
  also covers the launcher, which no pipe reaches between crash restarts or while the host it
  restarted is still starting: Stop writes a marker (`host.stopped` in the profile's folder)
  that the launcher checks after every host exit and every wait before starting the host again;
  waits until the task's run has ended, stopping a host that opens its pipe meanwhile; and ends
  the run itself if it is still there after the 20 s start timeout. A host the launcher starts
  also looks for the marker once its pipe is open and, finding it, exits 0 without serving, so
  a host still starting when the launcher was ended, or under a launcher this app did not start
  (one from before the app was last opened), stops too: either the app's Stop reaches its pipe
  or the host sees the marker. A host that refuses the app's connection (one of them elevated)
  is a refused Stop: left running, with its launcher, and the marker cleared. Without the task,
  a Start while the launcher the app started is between crash restarts waits for its host rather
  than starting a second launcher. Every start clears the marker
  (the app's Start, and a launcher starting, at sign-in or through the task).
* The app starts the host through the task when it is registered (so it runs the same way
  at logon and on demand), and directly otherwise.
* The task allows one instance at a time (`IgnoreNew`), and its instance is the launcher, which
  outlives the host it waits for by a moment. Task Scheduler drops a run asked for meanwhile
  without a word, so a start right after a stop (Restart, an update that failed to apply, the
  app quitting during one) runs the task only once its last instance has ended, waiting up to
  the 20 s start timeout for that, and then gives the host it starts the start timeout of its
  own (#280). A host that instance is still bringing up counts as started.

### Updates: Velopack, one version for app and host

The Velopack package holds the app, the host, its launcher, the injector and the agents'
hook (`agent-bridge-host-hook`, docs/host-hooks.md), published
into one folder with one version. An app update is a host update, so they never skew.

* Pack id `AgentBridgeApp`, installed in `%LOCALAPPDATA%\AgentBridgeApp`. Not
  `%LOCALAPPDATA%\AgentBridge`: that is the host's data root (device keys, accounts,
  config, logs), and Velopack deletes its install folder on uninstall.
* `OnBeforeUpdateFastCallback` (the old version, before files are replaced) stops the host
  over the pipe and waits for its process to exit, so nothing in `current\` is locked;
  `OnAfterUpdateFastCallback` (the new version) starts it again through its logon task.
  The app also stops the host itself before `ApplyUpdatesAndRestart`, and starts it on
  launch if it is not running.
* `OnBeforeUninstallFastCallback` stops the host and attempts bounded cleanup of only the
  default profile's owned agent hooks and instruction blocks, using its saved agent homes.
  It never uninstalls a CLI, signs out an account or takes over migrated/unowned hooks.
  Failure is logged and recovery data remains; reinstall and use Settings to repair.
  Private hook binaries/runtime and profile data are retained for existing sessions.
* Feed: GitHub Releases of `danswett/agent-bridge` (pre-releases included unless turned
  off; every merge to main publishes one, below), or a folder or URL holding
  `releases.<channel>.json` (`releases.win.json` for x64, `releases.win-arm64.json` for Arm64) in
  `%LOCALAPPDATA%\AgentBridge\app\feed.txt` (for testing on a VM).
* **One channel per architecture** (#222, `build/app-channels.ps1`). x64 is in Velopack's default
  channel, `win`, as every copy installed before Arm64 builds was packed; Arm64 is in
  `win-arm64`. The channel is recorded in the installed package (`current\sq.version`), and
  Velopack's `UpdateManager` reads only that channel's feed when nothing names one, which neither
  the app nor the host does: an x64 copy is never offered an Arm64 package, nor the reverse,
  whichever is newer (`HostUpdatesTests`). Moving x64 to another channel name would strand every
  installed copy on its last version, so it stays `win`.
* **Automatic updates** (`AutoUpdater`): Settings has "Include pre-releases in updates" and
  "Install updates automatically", both per profile (`<profile>-settings.json`) and both on by
  default. The installed app checks on start and every 30 minutes, and downloads a release it
  finds at once. It installs it once this machine's host has been idle (no session with a turn
  running or a question waiting) for two minutes without a break, seen at every minute's tick
  and every session change, and since its sessions were last read in full (a reconnect starts
  the wait again), or six hours after it first found one, whatever the sessions are
  doing (though still only once they are read in full, so not in the middle of a reconnect).
  The path is that of "Update and restart": the host stopped over the pipe, then
  `ApplyUpdatesAndRestart` with `--background --profile <profile>`, whose new version starts the
  host again. The new version's first start shows a toast: the version it came from, and the
  release's notes.
  * A read-only host (`delivery.readOnly`) and one beside a running agent-ha-bridge (G5) update
    like any other: those rules guard what other devices may do (the Home Assistant Install
    button below), and replacing the app and host touches neither the agents' consoles nor
    agent-ha-bridge. That is how a shadow beside agent-ha-bridge keeps itself current.
  * It never installs while another profile's app or host runs from this installation (Velopack
    stops every process in the package and restarts only this profile), or while the host is not
    running or its sessions are not yet read in full (`sessions.synced`), so that it cannot say
    whether they are busy. Just before stopping the host it asks again: `host.status` from the
    host itself, and the processes running from the package.
  * Those are the machine's refusals, and they don't hold an update silently for ever (#800: a
    VM's app lost its link to a live host and sat on an old release for ~20 h, one log line
    saying so). So are sessions never read in full on a connected host. It still checks and
    downloads behind them (only "Install updates automatically" off stops the checks), and a
    device's Install waits behind sessions not yet read in full, as before. The six hours run from the first find through
    them; only the switch turned off restarts them. Once the machine stops refusing an update
    held that long (the link back, say), it installs as soon as the sessions have been idle for
    two minutes, or 20 minutes later whatever they are doing (`ReturnGrace`), never at the first
    tick. A refusal in force is logged again every hour, with the app's link state while the
    host is the reason. A release held past six hours is told to the owner: the update state the
    devices see (`waiting`) reads "Needs you: the update to X has waited since T because Y", and
    the app logs that line at most daily. The wait it shows never names an "installs anyway"
    time already past.
  * The link to the host lost (not `Connected`, or no `host.status` on it): before refusing again
    the app re-attaches, at once and then backing off to every 30 minutes, and logs what it did
    at most hourly. A connected link is asked for the host's status again. Otherwise, when the
    host answers on its pipe, the link's attempt under way is ended and a new one started
    (`HostConnection.Reconnect`, which never runs two attempts at once). The updater never starts
    a host: whether one runs is the person's, and the app's own start's.
  * A release check the feed refuses for its rate limit (GitHub's unauthenticated 60 requests an
    hour per address, which machines behind one address share: a 429, or a 403 whose reason says
    "rate limit" or when GitHub's free `rate_limit` endpoint shows the limit spent; any other 403
    is a failure like any other) holds the checks off until GitHub's `Retry-After` or the limit's
    reset (read from that endpoint, as Velopack's error carries no headers), or otherwise for an
    hour, then two hours each time, until one succeeds. The next check is made at the hold's end,
    however recently a press was answered. A press from a device in the meantime is answered with
    the reason without asking the feed. The reason goes to the devices as the last update's
    outcome (`failed`), once each time, though never over an update made, or a press answered,
    in the last day, which the devices may not have seen yet. Velopack's `GithubSource` is given
    no token, so the checks are unauthenticated.
  * Settings and the automatic updates share one updater, and only the release actually
    downloaded is applied (`IUpdater.Ready`), checked again immediately before the host stops: a
    release replaced since (a check from Settings, or "Include pre-releases" or `feed.txt`
    changed, which drops the downloaded release, and any check still running against the old
    source finds nothing) is checked for and downloaded again first.
  * The host is never left down (`HostManager.StopAndApplyAsync`, one install at a time: Update
    and restart pressed while the automatic one installs is refused): an install that fails starts
    it again and waits for the next check; quitting the app while the host is stopping for an
    update, or stopped and not yet updated, waits for the stop to finish, applies nothing, and
    starts the host on the way out; and an app killed meanwhile starts it at its next start, as
    every start does, with the host's logon task at sign-in behind that.
  * Only the app's own timer starts it, or a person's press on another of their devices (below),
    so nothing an agent can reach does. A development copy and the demo never update.
* **From another device** (#51, [v1-update.md](protocol/v1-update.md)): the PWA and the app on
  another machine show this machine's updates and ask for a check, or for the latest release to
  be installed now. The app here does it, through the same `AutoUpdater` (`UpdateReporter`
  between the two): it tells its host what the updater is doing (`host.update.report`), which the
  host serves to the account's devices as `update.*`, and takes the presses the host carries in
  its status (`updateAsks`), each once. A check is made at once. An install is refused at once
  if another profile's app or host runs from this installation, or the host is gone; otherwise
  the latest release is found, downloaded and installed by the rules above (idle for two
  minutes, or six hours), with "Install updates automatically" off too: being asked stands in
  for the switch, for that release. How it ended is kept in the profile's settings
  (`lastUpdate`, with the press it answers in `updateRequest` across the restart), so the new
  version reports `updated`, or `failed` if it did not take, and the devices see it; an
  automatic install that fails is reported `failed` too, as is one from Settings (its download
  included), whose Update and restart also answers a device's Install not yet carried out. A
  press the app comes to more than ten minutes after it was made has lapsed at the host and is
  refused, not carried out late. The presses being carried
  out are kept too (`updateInstallAsked`, `updateCheckAsked`): one left unfinished when the app
  stopped for anything but an update is reported `refused` by its next start, never carried out
  then. Settings and the automatic updates
  share one `ObservedUpdater`, so a check or download from Settings is reported as well, and a
  release whose download failed is still reported as there, and their downloads take turns
  (Velopack's update lock fails a second rather than waiting): the later one finds the release
  already downloaded. A pressed install's six hours run from no earlier than the press, and any
  install's from no earlier than the switch last allowed it (not the machine's refusals), so turning the switch on hours
  after a check found a release still waits for busy sessions. A press within a minute of the last check is answered from it,
  a failed download is not tried again for ten minutes, and the presses taken are remembered
  across restarts (`updateTaken`) so none is carried out twice; the host takes at most three
  presses of each kind from a device in ten minutes ([v1-update.md](protocol/v1-update.md#how-often)). The host
  takes these presses only through the relay, only from a device listed as a person's (`pwa` or
  `app`, never a machine), and none on a read-only machine (`delivery.readOnly`, or
  agent-ha-bridge running, G5): those rules are #112's for Home Assistant's Install button.
* The host can start an update too: the Home Assistant connector's Install button
  ([ha-connector.md](ha-connector.md), stage 6) for a machine driven from the dashboard. It
  uses the same feed and the app's "Include pre-releases" for its profile
  (`<profile>-settings.json` beside `feed.txt`), downloads, calls
  `WaitExitThenApplyUpdates` (silent, restarting the app with `--background --profile
  <the host's profile>`) and stops as on Ctrl+C. Update.exe applies the release once the host
  has exited, force-stops everything still running from the package folder (the app, and every
  other profile's host), and starts the app again in the background for that profile, which
  starts the host. Another profile's host comes back as after an update from the app: at its next
  start. Hence a read-only host serves no Install button, and an agent's press of it is refused. Update.exe outlives the host
  that started it, so the update hooks may find their ancestry incomplete and leave the host
  alone (`LifecycleGuard`); the app's own start brings it back either way.
* Uninstall (`OnBeforeUninstallFastCallback`) stops the host and removes the logon task,
  the app's `Run` entry and the toast registration. The host's profile (device key,
  account, `config.json`, logs) stays in `%LOCALAPPDATA%\AgentBridge`, like any app's
  data, so a reinstall picks up where it was.

### Releases and updates: every merge to main is a pre-release

CI's `publish` job (`.github/workflows/ci.yml`) makes every push to `main` a GitHub
pre-release, `v0.1.<n>`, of the whole package. Installed copies include pre-releases by
default and install them by themselves once their host is idle (Automatic updates, above);
Settings, **Check for updates** and **Install**, does it at once, as does the Home Assistant
connector's Install button on a machine the dashboard drives.

* **Version** `0.1.<CI run number>`. The number is the CI workflow's own count, shared by pull
  requests and pushes, so main's versions only rise, with gaps; a re-run keeps its number.
  Renaming or recreating `ci.yml` starts the count at 1 again, below every release, and no
  installed copy would ever take one: move the minor version up (`0.2.<n>`) in the same change.
* **When:** after every check job in CI on that commit has passed: the `product` tests (Linux,
  Windows x64 and Windows on Arm) and both `app` jobs (x64 and Arm64: build, pack, self-test,
  install), and also the PWA, Home Assistant, spike and light jobs, none of which may have failed
  or been cancelled. It runs for pushes to `danswett/agent-bridge`'s `main` only: never a pull
  request or a fork. It
  is the only job with `contents: write`, and runs one at a time (concurrency group
  `publish-prerelease`). Before it, `publish-gate` lets through only the commit `main` is at
  now: GitHub keeps one waiting job per concurrency group and cancels the older waiting one, so
  a slow build of an older merge must not reach the group and evict a newer one. A run whose
  commit main has moved past publishes nothing, and the newer commit's run publishes instead
  (re-run it if it failed).
* **What:** `build/publish-prerelease.ps1` uploads the x64 app job's folder with `vpk upload github
  --pre` as a draft, then the Arm64 job's into the same draft (`--merge`, its own channel), adds
  each `assets.<channel>.json` and the notes, and only then publishes it, creating the tag
  `v<version>` on the exact commit; a client never sees half a release, nor a release with one
  architecture (the script refuses to publish without both folders). Assets, x64's exactly as on
  the first release (`v0.1.583`, published by hand) plus the delta:
  `AgentBridgeApp-<v>-full.nupkg`, `AgentBridgeApp-<v>-delta.nupkg`,
  `AgentBridgeApp-win-Setup.exe`, `AgentBridgeApp-win-Portable.zip`, `releases.win.json`
  (this version's packages), `RELEASES` and `assets.win.json`; and Arm64's beside them,
  `AgentBridgeApp-<v>-win-arm64-full.nupkg`, `AgentBridgeApp-<v>-win-arm64-delta.nupkg`,
  `AgentBridgeApp-win-arm64-Setup.exe`, `AgentBridgeApp-win-arm64-Portable.zip`,
  `releases.win-arm64.json` and `assets.win-arm64.json`. Before anything goes up it checks that
  each folder's app is built for its channel's machine, so an Arm64 package can never land in
  x64's feed.
* **Release notes:** `build/release-notes.ps1` lists the commits since the latest release, newest
  first (GitHub's compare API; the commit alone when that cannot be read), and `pack-app.ps1
  -ReleaseNotes` packs them (`vpk pack --releaseNotes`), so they are in the feed's
  `NotesMarkdown` for an installed copy to show after it updates.
  The public GitHub release additionally carries GitHub-generated change notes: merged PRs,
  contributors and the full changelog, without the package notes' 15-commit cap.
  `publish-prerelease.ps1` generates these under its publish lock, comparing the previous
  published version (including pre-releases, excluding drafts) to this build's exact commit.
  This includes changes from merges that did not publish. It retains the install instructions
  and CI provenance; a failed or empty notes response stops publication before uploading or
  replacing a draft, rather than silently publishing installation-only notes. PR dry runs
  remain read-only and exercise the generation/failure behavior through stand-in tests.
* **Deltas:** `pack-app.ps1 -DeltaFrom` empties its releases folder, downloads the latest
  release's full package of its own channel (`vpk download github --pre --channel <channel>`)
  and, if it is older, puts it there, so
  `vpk pack` builds a delta from it. Another merge may publish between that pack and this one's
  publish, so under the lock the script checks the delta again: `vpk delta patch` on the latest
  release's full package must rebuild this version's full package byte for byte. If it does not
  (or there is no delta), it is rebuilt from that release with `vpk delta generate` and checked
  the same way; if that fails too, the full package is published alone, with a warning, since a
  wrong delta would cost every copy a failed patch first. A copy on the previous version
  downloads only the delta rather than the ~100 MB full package; one further behind applies the
  chain of deltas, as far back as the newest ten releases Velopack's GitHub source reads, or
  else the full package. Each architecture's delta is checked against the same channel's
  package; the first release with Arm64 in it has no Arm64 base, so it publishes Arm64's full
  package alone (with the warning) and x64's delta as usual.
* **Once per version:** a version whose tag exists is skipped, so re-running a run publishes
  nothing twice. A version older than one already published is skipped (two merges close
  together, the later one's build first). A draft left by a failed attempt is deleted and the
  version uploaded again.
* **Pruning:** the newest 20 pre-releases stay; older ones are deleted with their assets, and
  their tags stay. A full (non-pre-release) release, a draft, or a release not tagged
  `v<major>.<minor>.<patch>` is never touched.
* **GitHub failing a call:** listing and deleting releases are each tried 3 times (waits of 2 s
  and 8 s), because GitHub's API now and then fails a single call. A DELETE that failed but went
  through counts as done. Pruning that still fails, after the release is out, is a warning, not a
  red `main`; the next publish prunes again. A leftover draft that can't be deleted still stops the
  publish before anything is uploaded (#907).
* **Pull requests** prove it without publishing: each app job runs the same script with
  `-DryRun` on its own folder, which checks the folder and its delta against the latest release
  as above, has vpk read it into a scratch feed (`vpk upload local`) that must list that
  channel's packages and nothing else,
  and prints what it would upload, publish and prune against the real releases (in the job
  summary too), writing nothing.
* **Release builds only.** The app job packs with `pack-app.ps1 -Release` exactly when the run
  publishes (`APP_RELEASE`: a push to `danswett/agent-bridge`'s main), which builds the host with
  `-p:AgentBridgeRelease=true`, so it carries the assembly metadata `AgentBridgeRelease=true`.
  Every pull request and fork packs a dev host. Three checks hold that: `pack-app.ps1` reads the
  host it published (`build/host-identity.ps1`) and stops if it is not the build asked for; the
  app job asserts the packed host is a release build exactly when the run publishes, and that
  the property makes one; and `publish-prerelease.ps1` refuses to publish a package whose host
  is a dev build (a dry run only says so). What a release build changes, all keyed off the host's
  `IsDevBuild()`:
  * The Home Assistant connector may write to a Home Assistant not marked
    `homeAssistant.disposable` (`HaGate`, design G6). Everything else in its gate still holds:
    enabled, a base URL and token, and agent-ha-bridge not running for this user (G5).
  * hello's `machine.dev` is left out, so the PWA's and app's machine status and the app's
    Settings show the version without "(dev)"; the local API's status `dev` is false.
  * The connector's capabilities attribute `dev` is false.
  * The shadow ledger records `hostDev` false (`ha shadow` reports it), and `ha status` no
    longer gives the G6 refusal.

  Nothing else reads it: not updates, delivery, migration or the relay.
* A pre-release is unsigned (below), but attested: once `publish` has uploaded it (not on a
  re-run that finds it already out, whose rebuilt SBOMs would not be of it), CI's `attest` job downloads
  the release's packages back (the full and delta packages, `Setup.exe` and `Portable.zip` of
  each architecture) and records, in GitHub's attestation store, SLSA build provenance for each
  (the workflow run and commit that built it) and its architecture's CycloneDX SBOM (every
  NuGet package and project in the published folder, read from its `.deps.json` files by Trivy
  in the `app` job, which keeps it as the `sbom-<runtime>-<version>` artifact on every build).
  To check a download: `gh attestation verify AgentBridgeApp-win-Setup.exe -R
  danswett/agent-bridge`, and add `--predicate-type https://cyclonedx.org/bom` for the SBOM.

To install the latest, download the installer for the machine from the newest pre-release at
<https://github.com/danswett/agent-bridge/releases> and run it (SmartScreen: More info, then Run
anyway):

| Machine | Installer |
|---|---|
| x64 (Intel or AMD) | `AgentBridgeApp-win-Setup.exe` |
| Windows on Arm (Snapdragon and other Arm64) | `AgentBridgeApp-win-arm64-Setup.exe` |

Settings, System, About, "System type" says which. The Arm64 installer refuses an x64 machine.
The x64 one does install on Windows on Arm, but runs under emulation, which is untested (#222),
and keeps updating from the x64 channel: to move such a copy to Arm64, uninstall it and install
the Arm64 one (the host's profile in `%LOCALAPPDATA%\AgentBridge` stays). From a shell:

```powershell
$arm = (Get-CimInstance Win32_Processor | Select-Object -First 1).Architecture -eq 12   # 12: ARM64, also from an emulated shell
$setup = if ($arm) { 'AgentBridgeApp-win-arm64-Setup.exe' } else { 'AgentBridgeApp-win-Setup.exe' }
$tag = gh release list -R danswett/agent-bridge -L 1 --exclude-drafts --json tagName --jq '.[0].tagName'
gh release download $tag -R danswett/agent-bridge -p $setup
& ".\$setup"
```

(`gh release download` without a tag takes the latest full release, and there is none yet.)
From then on it keeps itself on the newest pre-release ("Install updates automatically"), or
Settings, **Check for updates**, then **Install**, takes it there at once; turning off "Include
pre-releases" in Settings stops it finding them until there are full releases.

### Signing (later)

Releases are unsigned for now. The hook is in place: `build/pack-app.ps1` passes
`-SignParams` (signtool arguments) or `-AzureTrustedSignFile` (an Azure Trusted Signing
`metadata.json`) to `vpk pack`, which signs every executable in the package and `Setup.exe`
before the delta and installer are built. To turn it on, hold the certificate or Trusted Signing
account as a repository secret and add the argument to the app job's pack step for pushes to
main only (a pull request from a fork gets no secrets). Until then a downloaded `Setup.exe` meets
SmartScreen and, where it is on, Smart App Control.

### Starting with Windows

The installed app's first run turns on both: the host's logon task, and an HKCU `Run`
entry that starts the app with `--background` into the notification area, so toasts keep
arriving after a restart. The app needs no restart-on-failure and nothing more than
Explorer's startup list, so a `Run` entry is enough for it. Either can be turned off in
Settings, and stays off. Closing the window keeps the app in the notification area; its
menu quits it, which leaves the host running.

An elevated app (for example one started by an elevated installer) never starts the host
directly: the host would be elevated too, unable to type into ordinary consoles and
refusing the app once it runs normally. It starts the host only through the logon task,
which always runs it unelevated.

### agent-ha-bridge on the same machine (G4, G5)

The app never writes agent hook registrations (no Copilot, Claude or Codex hooks), and its
names, folders, pipe, task and AUMID are its own. Without hooks the host still finds Copilot
sessions (their session folders) and Claude sessions (Claude Code's own
`~/.claude/sessions/<pid>.json`), and Codex ones run with `--no-daemon`, as every launch
from this host is (the terminal holds `~/.codex/thread-writer-locks/<id>.lock`). A Codex
started the default way, through its shared app-server daemon, is found only through
agent-ha-bridge's Codex hook; and what only a hook reports is missing (a Claude question still waiting,
Claude permission prompts, Codex approvals: [host-answers.md](host-answers.md)). The host
already refuses to type into any session while agent-ha-bridge is
active for the user (G5), and offers read-only capabilities then; the app shows the
reason in a banner over the cards and on the Settings page, and nothing more. Migration is a
separate, explicit step ([migration.md](migration.md)).

## The app

**Inline mono** ([ui.md](design/ui.md) §4.2, #215): a path, an id, a branch or a tool call on a line of text is in Slipgates Mono at that line's role's generated inline size (`AbInlineMono*`, through the app's `InlineMonoRow` and `InlineMonoMeta` styles), so its x-height is Slipgates Sans', on the role's own line height and weight; a code span in a response is scaled the same way (`AbInlineMonoScale`). Repository, machine and model names are in the text face, and separators are always text. Code standing on its own lines keeps the Code role.

**The window** ([ui.md](design/ui.md) §7, #180): Mica behind the app's own 48 px title bar, which carries the mark (its native 20 px cut, in Bridge, which is Highlight in high contrast), the product's name and a search box (Ctrl+K), beside the system caption buttons, which the app paints in its theme on the UI thread (not with the title bar's `PreferredTheme`, whose repaint from a thread-pool thread crashed the app on a high-contrast switch, #240) and leaves in the system's colours in high contrast; a compact rail of Sessions, Machines, Launch and Usage, with Settings at its foot and a 3×16 Bridge pill on the page shown; and the pages on a layer with an 8 px top-left radius. Each page has its title in the type ramp's title role. The search box finds sessions on every machine by the words of their title, repository, branch, agent or machine, titles starting with them first, then the most recently updated, whichever machine the list shows, and opens the one chosen as a toast does. The window resizes freely down to 360×600 and opens where it was left (`<profile>-window.json`, maximised too), unless that is off every screen, when it opens centred on the main display at 1280×860 or as large as the display allows. WinUI's controls that follow the system accent (accent buttons, the rail's pill, checked controls, focused fields, selected text and links) are Bridge, from the generated accent palette ([design/README.md](../design/README.md)); high contrast keeps the system's colours.
| Page | What it has | Parity source |
|---|---|---|
| Sessions | Panes by window width ([ui.md](design/ui.md) §5.2, #180), at the PWA's breakpoints: from 1280 px the list (320), the session and an inspector (336, hidden and shown from the session's header) with the delegate selected in the strip, or with none the session's Details and Tools; from 760 the list (320) and the session with Conversation, Delegates and Tools tabs; below, one column, the list or the session with Back. A divider between the list and the session, and between the session and the inspector (#297), drags, moves 16 px with Left and Right when focused, and goes back to the default on a double-click: the list 264–480, the inspector 280–560, the session never under 440, the inspector then the list giving way at a narrower window and the widths kept coming back once it widens; kept in `<profile>-panes.json` (`PaneWidths`). The session open is the selected row (`MainViewModel.Selected`), whatever opened it (a row, search, a toast, the tray, a launch), and stays open through every resize; a machine tab that hides its row closes it. **List** (§6.1): one row per live session of every machine on the account (this one's over the pipe, the others' through the relay), with machine tabs (All, then each machine) and, on the title's row, the sort menu (Your order, Most recent, Machine) and New, which opens Launch. A row is two lines: the status glyph aligned to line 1 ([ui.md](design/ui.md) §2.2: the generated glyphs, turning while working or delegating unless Windows has animations off, stopping and starting as that setting changes, the violet diamond while an agent drives), the title, semibold with the 6 px Bridge dot for unread activity or else the age, then at most two facts on one line (#214): the status's first fact in its tone's colour, and the machine once there is more than one, else the repository. Never the model, counts or pid: those are in Details. Rows keep the person's order, kept in `<profile>-order.json` (#164): a new session goes to the top once and an update never moves a row; Alt+Up/Down on a row, its menu (right-click, press and hold, Shift+F10: Move up, Move down, Pin) and a drag by the grip that shows on hover move and pin rows, with "Pinned" and "Sessions" over each group once anything is pinned; a row whose session updated since it was last open has the dot; sessions a host removed while the app ran fold into Ended · N below the rows. **Session** (§6.2, §6.4, §6.5): the title and one meta line (glyph, the whole status, machine, repository), with Pin, Details and the inspector toggle as icon buttons; the conversation at the reading measure (770 px), centred in the pane: your last message, or a prompt you didn't type (a schedule's or an agent's, `activity.userMessageFrom`, #489) as a collapsed line at the column's start in agent colour, a clock and "Scheduled prompt · #4" or the driven diamond and "Sent by an agent", two lines of it and Show prompt (a compact disclosure, as the PWA's: a chevron leading its words, pointing right shut and down open, at the label's size, opening on all of it inside a thin agent rule), a schedule's repeat folded to "same as last time"; the main agent's response (Markdown) with its age, its code blocks wrapped rather than scrolled, left to right with hidden and bidi characters and right-to-left letters drawn as escapes (#220) (an approval's card draws its whole message as one such block, exactly as written, fence markers included; nothing in it is read as Markdown, and it has no Copy; a block says "Contains hidden characters" or "Contains right-to-left text" when it does), and each with Copy, which copies what is drawn (#296), reasoning behind "Show reasoning", the current tool's glyph named for its state with the call in mono; question forms in the stream headed "Main agent asks" or the delegate's name (single and multi choice, Other, boolean, text, number, Copilot permission Approve once / Deny); and the reply box, centred under it, a card with a 2 px Bridge edge (Ctrl+Enter or the icon Send, paste an image, Attach: up to 100 MiB each in chunks over the pipe, `sessions.attach.direct`, or through the relay's blob store to another machine, `sessions.attach.blob`, or 256 KiB where neither is offered, which shrinks a larger image to fit). **Details** (the header's Details and the inspector): status, agent, machine, model, repository, branch and folder (mono, cut in the middle, whole in the tooltip), driver, turns, compactions, process, for how long, updated, ID (mono), then End session with the host's two-press rule. **Tools** (the inspector and the Tools tab): the tool history, loaded as it comes on screen and kept current only while it is. **Delegates** (§6.3, §6.4, #229; the rules are `Attribution`, the PWA's `pwa/js/attribution.js`, case for case): from 1280 px a strip under the header, one item per delegate the main agent started (glyph, name, how long it has run and "N nested"), the selected one in Bridge, the finished folded into "N finished", and "N more delegates not listed" for what the host left out; below 1280 the Delegates tab, every delegate nested under its parent, the finished under "N finished". Every delegate name is its safe label (`DelegateLabel`, §6.3): invisible and bidi characters removed, cut to 60 characters, and "Delegate “…”" for one that reads as the main agent. A delegate opened (from the strip, the tab, a nested list or the conversation) shows in the inspector, or is pushed in the tab: its name under a fixed "Delegate" overline, and its glyph; Status, Model, Kind and ID (mono); its Task, Latest (its own response, fetched with `session.get { id, agent }` while on screen, again every 10 s while it runs) and Reasoning, collapsed; its Tools, mono, a glyph per call with how long each took; and the delegates it started. Its one action, "Ask main agent to stop it", fills the reply box with a message to the main agent naming it by its id alone ("Please stop your delegate *id*.") and sends nothing: replies only ever go to the main agent, and nothing types to a delegate ([subagents.md](design/subagents.md) §6). The conversation has the main agent's own words only, labelled "Main agent" where the host says whose they are, and one meta line for each of its delegates starting ("Started 2 delegates · 17m") and reporting back ("Delegate *name* reported back · 14m"), the name linking to it. A host that lists no delegates (Claude, Codex, an older host: status rule 8) has no strip or list, only the background agents and commands it counts, in the tab and in Details. While a machine's discovery is held, a banner over every page says what holds it ("Discovery held: …", [v1-sessions.md](protocol/v1-sessions.md#discovery-held)), one banner per machine. Its close button ("Dismiss Discovery held on *machine*") hides that machine's banner and no other, and focus moves on to what Tab reaches next. Dismissing changes only what is shown: the host still holds and is told nothing. The banner comes back when that machine's hold has another reason, or clears and comes back, or the app restarts: dismissals last for the app's run and are never saved (#293). The banner that says what stops the app from its job (the host not running, refusing the app, connecting, or view only) has no close button. Each other machine's updates (#51, [v1-update.md](protocol/v1-update.md)) are on the Machines page | PWA cards (`pwa/js/ui`), which mirror `agent-bridge-reply-card.js`; the dashboard's `install_update` and `check_updates` buttons |
| Machines | One row per machine on the account (#180, #213): this one first, then the others connected, then those offline from the relay's roster. A row has the machine icon and the name on line 1 with its status trailing it (This machine, Online, Connecting or Offline), and at most two facts on line 2: the session count, and `View only` for a host that takes no answers, replies or End session from here, with the host's reason as the facts' tooltip rather than on the row. A machine offering updates (#51) has what its app is doing and how the last update ended inside its row, with Check and Install; an offline one has when it was last seen and the two-press Forget; one connected has Show sessions, which picks it in the Sessions page's machine tabs. Its buttons share one style, in one column under the text. Check all, beside the page's title, checks every machine listed and this one | PWA machines (pwa/js/ui/views.js) |
| Launch | Start or resume a session on any machine on the account that offers it (`sessions.launch`): machine, resume, agent, workspace (a new worktree where the folder is isolated), profile, model, effort, context and permissions as that machine offers them, and a first prompt sent whole. Each press is one launch with its own request id, sent again with the same id after a lost connection, so it starts once; the outcome (started, busy with the reason, refused, failed, unconfirmed) is shown and announced to screen readers, and the new session's card opens when it arrives. Try again sends a launch that could not be sent with the same request, but only to the same host process, never to one that restarted since, and only while the host still remembers the request (9 minutes from the first send). An error from the host says the launch may still have started, and a failure says what Try again would send, since the form may have changed since. A launch the machine reports failed is sent again as a new request. A host that names no run in its hello counts each connection as a new run, so a lost launch to it is reported unconfirmed rather than sent again. What was picked on a machine is kept while it drops out and when another machine is picked. A machine's choices are fetched only while the page is open, since that is costly at the host. A machine waiting on a folder-trust question says it is busy when the page opens or its choices are reloaded. Codex is not launched without a first prompt (nor needs one for a session brought over). Where the machine takes sessions from the account's others (`sessions.transfer`), Resume also lists the ended sessions they hand on (`sessions.transfer.serve`), by machine: picking one copies it over and resumes it as a new session in the workspace picked, with a command the app signs with its own device key, as a person's device (kind `app`), for the machine the session is on ([v1-transfer.md](protocol/v1-transfer.md)); the original stays. A transfer to this machine goes to its host through the relay, as the protocol requires, not the pipe; a transfer waits up to 12 minutes for its outcome and is resent for up to 20; why the form cannot go is said beside the Launch button, apart from the last launch's outcome. A launched agent asking whether to trust its folder ([v1-launch.md](protocol/v1-launch.md#folder-trust)), listed by the machine or named in a launch's outcome (which counts only until the machine's next listing, asked for at once), is shown in full (agent, the whole folder path wrapped and selectable, the repository root it applies to, the agent's own words); where the machine allows it and has approved this app, Trust and Quit answer it with `launch.trust.answer` and a command the app signs as a person's device for that machine, question and choice, through the relay only (this machine's own host through its loopback, never the pipe). Otherwise it says to answer at the machine and why; for this machine, "Allow answering from this app" turns the setting on with this app approved. An answer that may have typed is never offered again; an error offers only that same request again. A machine that takes no launches now (agent-ha-bridge runs there) is still listed, to show such a question read-only | PWA launch view (`pwa/js/ui/launch-view.js`), agent-ha-bridge's launch card |
| Usage | Each agent's windows: percent, amber at 75 %, red at 90 %, pace against the window, "resets in" | PWA usage panel |
| Settings | Host: running, version, profile, pid, start/stop/restart, start at sign-in, log tail; relay: sign in with GitHub (device code shown in the app), machine name, connection state; this machine: approving Copilot permission prompts from other devices (`permissions.remoteApproval`), answering folder-trust questions from devices (`launch.remoteFolderTrust`, applied at once; turned on with this app approved) and which devices may (`launch.remoteFolderTrustDevices`: this app, the account's phones, browsers and apps the relay lists, and any approved that are not connected; changes from here and from the Launch page go one at a time, each made from the list the host has just then, since the host replaces the whole list; a list changed meanwhile by another app or at a terminal is refused by the host and the change made again from it, so a device revoked there stays revoked), agent-ha-bridge state; on a Microsoft Dev Box only, keeping it awake while the host runs (`devBox.keepAwake`, applied at once; [host-config.md](host-config.md#dev-box-keep-awake)), with the last pass and the next, and a failed or blocked pass (no `az login`, say), Dev Box agent settings the host cannot use, or a stand-back with Task Scheduler unreadable in red since the Dev Box may then hibernate; app: show reasoning and tools (the old Detail toggle, kept in the app since every client now gets the full activity), toasts, start the app at sign-in, version and updates | status card, `install.ps1`, `agent-ha-bridge configure` |

**Toasts** (§7.2): a question raises a toast with its options as buttons and a text box
for Other or a free-text answer; anything larger (several fields, multi-select) gets Open.
An approval's toast (a permission prompt) has only Open, and a click anywhere on it opens
its question ([ui.md](design/ui.md) §7, #372): its body is plain text the OS cuts at about
300 characters, so it can't show the whole command or the escapes the card gives hidden and
bidi characters. A button on a toast an older build left in Notification Center, which may
say Approve once, only opens too: the app answers from a toast only when its arguments carry
this build's mark (the argument `v=2`: `ToastXml.AnswerVersionKey`, `ToastXml.AnswerVersion`)
and never for a question it knows is an approval. The answer goes to the host over the pipe
with a fresh request id, and a follow-up toast says what became of it. A session's toasts
share one tag, so a question replaces only its own, and a question answered anywhere
withdraws it. A question that comes back under its id saying something else, or answerable
otherwise (a host updated to refuse what it offered), quietly replaces its toast (or withdraws
it, where no toast would be raised now), and its card rebuilds its controls: nothing chosen for
what it said is kept, what was typed stays on the card, and an answer that failed to go is not
offered again ("The question changed since, so answer it again"). A finished turn raises a toast with the start of the main agent's own response, never a delegate's (it can be turned
off on its own). A click opens the app on that session, waiting for it to arrive if the
click is what started the app, with keyboard focus in it: on its question's heading if it
has one, never on an answer such as Approve once (#484), or else its reply box. Nothing is raised while the app's window is in front.
Toasts go quietly into Notification Center, without popping up, while Windows says the
person is busy (Focus Assist, presentation mode, a full-screen app). Every toast is silent
(`<audio silent="true"/>`, added where `ToastXml` closes each toast, so one put up again
under its tag is silent too): without it Windows plays its default sound, which dinged for
every question (#754). The app keeps running in the notification area when its window
closes, so toasts keep coming.

The activator is the app's own (`INotificationActivationCallback` in an HKCU
`LocalServer32` registration, S3 finding 1, without the toolkit S3 used), under the AUMID
Velopack gives the app's shortcuts (`velopack.AgentBridgeApp`). It is registered with COM
before XAML starts, so a click that starts the app is held until the app is ready, and is
removed by Velopack's uninstall hook. A click on an answer button answers through the pipe
without opening a window. Only the installed app registers anything: a development copy,
another `--profile` and the demo show no toasts and write nothing to the registry.

**The product's name and art** (#537): every place the app, the host and its launcher name
the product, Slipgates, or the host service ("the Slipgates host"), reads it from `Product`
(`AgentBridge.Protocol`), whose name is the brand token's (`DesignBrand`, generated from
`design/tokens.json` `brand`; [brand.md](design/brand.md) §2):
the window and title bar, the tray's tooltip (`MainViewModel.TrayTip`) and menu, the toasts'
sender (the AUMID's `DisplayName`, not the AUMID) and default title, the update toast, the
device name other devices see, the logon task's description and the host's messages. The exe's
`Product` and `AssemblyTitle`, its icon and the toast icon are named once in the app's project
(`Product`, `AppIconFile`, `AppLogoFile`); `build/app-channels.ps1` reads them for the
installer's title and icon and the releases' titles, and the app's code reads the files from
`AppAssets`. The host's executables carry the name too, as "Slipgates host" and so on, in
Task Manager and their properties; their file names stay. The title bar draws the mark's
native 20 px cut (`AbIconMark20`). `AppBrandTests` holds these together and fails on copy that names the product by
hand. Identifiers that contain the name (the AUMID, the pack id, exe names, paths, the pipe,
HA ids and names) are not the name and stay (#541).

**Demo** (`AgentBridge.exe --demo`): the app with a pretend host of its own, serving the
protocol's example sessions on a private pipe and answering every op as a cooperative
host would, and a second pretend host ("Build server") that the app reaches as it would
another machine through the relay. For trying the app without agents, for screenshots, and for CI. It never
reaches the real host or its profile.

## Deferred, explicitly

* **Pairing and a Devices page**: the account's other devices are trusted by signing in to
  it, as everywhere else for now.
* **Cancelling a large attachment** part way. The PWA has a Cancel while a file uploads; the
  app has none yet, over the pipe or through the blob store.
* Agent hook registration for a fresh install (#54).
* **Migrate and Roll back on the Settings page** (WinUI slice 2). Both exist in the host
  (`agent-bridge-host migrate` / `rollback`, [migration.md](migration.md)); the page needs
  to show the dry run's report, confirm, and show the result, and it cannot be reached through
  the relay.
* Code signing.
* Arm64 builds, the first-run wizard's agent detection and winget installs.

## Testing

* `tests/AgentBridge.Local.Tests`: framing, the server and client over a real pipe (or
  Unix socket), and refusals.
* `tests/AgentBridge.Host.Tests/LocalApiTests.cs`: the real host's handler over the pipe,
  `SessionService` with fixture sessions (the same snapshot and ops the relay tests
  check), the management ops, and that the relay never dispatches them.
* `tests/AgentBridge.App.Tests`: view models (the window's list, rows and panes at every width,
  the session open, forms, reply, End session, usage, settings), other machines (`FleetTests`: cards, answers and toasts per
  machine, the picker, machines coming and going, usage merged; `LaunchTests`: the Launch
  page's choices, every setting and the whole prompt sent, resume, Codex's first prompt,
  busy, failed and Try again, a lost response sent again with the same request, no approved folder,
  profile and context, and a launch on another machine, including one whose machine leaves or drops out; and the app reaching another
  host through the in-process relay with the host's account), toast plans and activation handling, automatic updates (`AutoUpdaterTests`: when one waits for the sessions, when it goes anyway, a read-only host beside agent-ha-bridge updating like any other, the other profiles it never stops, the host never left down and never started by it, a lost link made again, an update held past six hours told as "Needs you", the 20-minute grace once the host is back, and the rate-limit hold), host lifecycle with a
  fake task scheduler and launcher, the Markdown model and the demo host. Platform-neutral,
  so they run on Linux too.
* CI (`app` jobs, one for x64 on `windows-latest` and one for Arm64 on `windows-11-arm`) runs
  `build/pack-app.ps1 -Runtime <win-x64|win-arm64>`: publishes the app, host, launcher, injector
  and hook into one folder for that architecture, checks for the app's `.pri`, the executables
  (each built for that machine) and the bundled Slipgates Sans
  and Slipgates Mono collections with their licences (`Assets\Fonts`; licensed separately under SIL OFL 1.1, not this project's AGPL), and packs it
  with `vpk` into that architecture's channel, with a delta from the latest release. It then starts the published
  app with `--self-test`, which runs the demo (on CI, on a screen made 1600×1000 or more for the pass, as a
  window is never wider than its screen), finds the rows by automation id, checks each row's two lines, opens sessions from
  the list and answers a
  question on each machine by invoking its button's automation peer, checks the generated
  design resources in every theme, draws every glyph and icon, checks the status words' colours on rows and in the header and the tool glyph's name and that words and glyphs follow a theme flip, measures inline mono's x-height against the text face's in each ramp role (within 1 px, #215), checks every weight and style of both bundled fonts is a real face (DirectWrite reports no simulation, and bold and italic text is as wide as the real face draws it, #544), sets code in the bundled
  font, narrows the list with the machine tabs, resizes the window across the breakpoints (three panes at 1440, two with
  tabs at 1000, one column at 400×900, Back, and the session open kept through each), drags the panes' dividers, moves them with
  the arrow keys and sets them through their automation peers, and checks the session keeps its narrowest and no pane leaves the
  page at 1300, 1000 and 800 px, the widths come back at 1440, and resetting them gives the defaults (#297), adds three machines with long names and checks every machine is a tab or under More with nothing past the pane's edge at the narrowest list, at 1000 and at 400 px, More's menu names each with its state and says so while open when one goes offline, and one chosen there becomes a selected tab, its name cut short and named in full (#290), launches a session from the Launch page
  and waits for its row, then checks your message in it and in the session with delegates is at the right of the conversation's column, at most 85% of it, and the column and the reply box at the same measure, centred in the pane with its scroll bar at the pane's edge, at 1440, 1200 and 400 px (#255, #294), and still centred while the list's divider is dragged,
  sends a session a scheduled prompt and checks it is a line at the column's start, not your bubble, with a clock and "Scheduled prompt · #4" in agent colour, two lines of it and Show prompt (compact: a chevron pointing right before its words, at the label's size, as wide as they are; a control Tab reaches, named "Scheduled prompt 4, Show prompt" and heard as collapsed), which opens, its chevron turned down, on all of it inside a thin agent rule; then its repeat folded to "same as last time", an agent's prompt with the driven diamond, and your bubble back for your own (#489),
  checks Codex on the other machine is not launched without a prompt, checks Settings' agent rows (#678: each button named for its agent with an id of its own, one verb per hooks' state and none for an agent set up and not tested yet, and a setup made through its button and confirmation, named for the agent with Cancel the default, raised for Narrator as it starts and ends, named for its agent, a superseded raise skipped, its result on its own row as a polite live region, a failure in Danger, pictured in Light, Dark and high contrast), checks the shell (the 48 px title bar, the
  search box finding and opening another machine's session, the rail and its Bridge pill, the layer's radius, an
  accent button in Bridge, the caption buttons in the app's theme, the 360×600 minimum), checks the Machines page's rows (#213), draws every page, the Sessions page in three panes with the inspector, in Light and
  Dark and, on CI, turns Windows' high contrast on to draw every page again and check that every role and surface is a
  system colour and the caption buttons the system's, then turns it back off and checks the caption buttons come back in the app's theme, and saves a picture
  of each page. Whole-window pictures capture the window's composed client, including Mica,
  rather than only the XAML tree: a translucent page layer without its backdrop otherwise
  becomes light in a PNG viewer (#785). Isolated-control pictures still use XAML rendering.
  Before native capture, two distinct XAML rendering ticks and another UI dispatcher turn
  let a preceding update cycle finish even when the page is already idle; the self-test
  fails if that frame barrier cannot complete within 10 seconds.
  The dark page pass reopens each output PNG and checks a page-background pixel is opaque
  and no lighter, in sRGB relative luminance, than the platform's dark layer token's straight
  colour (the upper bound for that layer over dark Mica). Encoded white and transparent
  regression samples exercise that guard too; missing, blank or failed window captures fail
  the self-test. No capture-only page fill or bitmap recolouring is applied.
  Last, it measures a live resize (#765): it resizes the window a step a frame, 8 px at a time, from 1560 px to
  400 and back, and keeps every interval between the UI thread's frames (`CompositionTarget.Rendering`) by the window's width
  class, once with the demo's own sessions and once with a window as heavy as a real one (16 more sessions, a conversation of
  over 200 items, 50 tool rows and a strip of 20 delegates, the finished ones unfolded), and writes p50, p95, the longest and how many frames went over
  16.7 and 33 ms into the result's `resize` and `result.resize.json`, the p95 in each width class in its log line, with a picture of where the drag ended. Before the heavy drag it scrolls a block in the middle of the response to the top of the conversation and pictures the conversation (`result.resize.before.png`); after it, it checks that block is where it was in the layout (its top against the viewport within 0.5 DIP, the proof) and pictures it again (`result.resize.after.png`), which must be drawn alike across the reading column: no more than 4 pixels more than 8 apart in a channel, and none more than 96 apart (`PictureDifference`). So a renderer's noise between two renders (a single speck of a card's fill by its rounded corner, or an anti-aliased edge) doesn't count, while a block moved by a pixel, which differs along every edge of its text, does; the log gives the count and the largest difference either way. It then presses Page Down and Down for real on a link in the conversation (`SendInput` with the window in the foreground, else posted to the window with keyboard focus, as the keyboard sends them; the result's `keysPressedBy` says which) and checks the text scrolled with focus where it was and nothing selected; that Tab from the last link or Copy button drawn goes to the first in the next block with one, which it realises (the move made with `FocusManager.TryMoveFocus`, as Tab makes it, not an injected key), that Tab pressed for real onto the Copy button of a code block laid out across the conversation's bottom edge brings the block's start to the leading edge, and onto one wholly in view moves nothing (the result's `tabInViewProblem`), that Shift+Tab pressed for real onto the Copy button of a code block wholly in view, its top 3 DIP below the view's, where the focus manager's 20 DIP padding round a control would scroll it, move nothing (within 0.5 DIP; the result's `tabNearEdgeProblem`), that Tab pressed for real onto the first link of a block taller than the view (a quoted log of 44 lines just before the run of linked paragraphs) brings its start to the leading edge (within 1.5 DIP), and Shift+Tab onto its last link brings that link wholly into view (within 1.5 DIP) at the nearest edge, no further than WinUI's 20 DIP margin past it, the block's start above the view (the result's `tallTabProblem`), and that Tab, pressed for real (`SendInput`, else posted; the result's `linkTabPressedBy` says which) on the last link of the last block the list has realised, goes to the first link of the next block, which it hadn't, and brings that block in at the leading edge numbered in its set, each block found from its link (a link is a text element in its block's text, not a control; the heavy response ends with a run of 110 short linked paragraphs, more than twice the most the list was seen to realise from a view's top, so with the run's first block at the top of the view the last block realised and the next both fall in it wherever the list stops; the check fails with "setup: ..." rather than skips if they don't); that no block's container is a tab stop, selected, filled or drawn with chrome or a focus visual; that the last block, off screen and named as no other block is, is found by its name and brought into view through the peer the search found; and that a screen reader is given every block as a list item numbered 1 to N in a set of N, with the header and footer outside it. WinUI numbers an item only once it is realised, so the check walks the items as a screen reader does: the list's ItemContainer pattern finds each next item (`FindItemByProperty` with no property), and one not yet realised is realised through its VirtualizedItem pattern (`Realize`) before its numbers are read, none of them -1 (the result's `listRealisedByPeer` counts the realised ones, `listWalkSeconds` the walk). A capture of your message and its three-block reply is also taken in high contrast at 1440, 1200 and 400 (`result.yours.<width>.highcontrast.png`), beside the light and dark ones. The frame times are a report, not a
  bar: the runners draw in software, so they fail it only when it could not measure: when a sweep never reached all three width classes. The two sweeps share 30 s, a hang guard that keeps the self-test well inside its 300 s (180 s until #800's sign-in captures: the banner, the sheet and a Needs sign-in row in every state they can be in, in Light, Dark and high contrast); a sweep cut short by it is reported (`lightFinished`, `heavyFinished` and the log), not failed. `build/app-self-test.ps1` runs it; when the app ends with anything but 0, or leaves a check unfinished (the result's `unfinished`, even with 0), it fails and prints the exit code as an NTSTATUS, the check it was in, the end of the app's log and the crash Windows recorded, faulting module and offset (#240). On CI it also has Windows Error Reporting keep a minidump of a crash beside the logs, and prints the crashing thread's stack from it with the runner's `cdb` (#242): for a fail fast the event names no module. Then it runs the `Setup.exe` it packed, silently, on the throwaway runner,
  checks the installed copy's channel and that its executables and `Update.exe` are for that
  machine, and runs the same self-test from the installed copy. Last, it dry-runs publishing
  the package (`build/publish-prerelease.ps1
  -DryRun`) and uploads `Setup.exe` and the packages as an artifact, which the `publish` job
  publishes for a push to main.
* The `product` job runs the tests on Linux, Windows x64 and Windows on Arm, including the
  NativeAOT injector typing into a real console and the NativeAOT hook, each built for the
  runner's own architecture.
* `build/test-publish-prerelease.ps1` (the `product` job, on Windows): publishing and pruning,
  refusing a dev build, a release missing an architecture or a package in another
  architecture's channel, x64's channel and asset names unchanged, and the delta base, channel
  and build identity `pack-app.ps1` gives the package, against stand-ins for gh, vpk and dotnet.
* Live checks run by the coordinator on a disposable VM (the checklist in the pull
  request).

To build and try it locally (nothing is installed or registered):

```powershell
pwsh build/pack-app.ps1 -Version 0.1.0 -NoPack
out\app\publish\AgentBridge.exe --demo
```

On Windows on Arm, `-Runtime win-arm64` builds the Arm64 copy into `out\app-arm64` instead.
