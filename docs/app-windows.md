# The Windows app (P3 core)

The first shippable desktop app: a WinUI 3 window over this machine's Bridge Host, the
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
  pipe's does. A machine that leaves presence goes, with its cards and toasts (answer outcomes
  too); while the app's relay connection is down, every machine stays as it was and reconnects
  when it is back. With no account to find them through (this machine's host signed out, or
  refused), they all go, and so do the last account's when the host signs in to another account
  or relay, without waiting for the new one to connect. The
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
  of them, and each row names its machine on line 2. Usage merges every machine's
  readings into one row per allowance, the freshest reading of each, as the PWA's does, asks
  each host for the readings it keeps from the account's other machines (`usage.shared`), and
  keeps the last reading of each allowance between runs in `<profile>-usage.json`, so a reading
  outlives its machine going offline; a row says `read on <machine>` unless this machine read it
  (#172). A toast for another machine's question says which machine, and its answer goes there.

### Starting the host at logon: a scheduled task

Per §3.1 and §3.2, a per-user scheduled task with a logon trigger, `InteractiveToken`
and `LeastPrivilege`, so the host runs unelevated in the person's interactive session,
where delivery can reach their consoles (§6.2). A task rather than a `Run` key because it
restarts the host if it fails (every minute, as agent-ha-bridge's does), never stops it on
battery or after 72 hours, and does not depend on Explorer's startup list. Non-admin users
can register a task for themselves; nothing here needs elevation.

* Name: `AgentBridgeHost_<profile>_<hash of the user SID>` (G3: never `AgentBridgeDaemon*`,
  which agent-ha-bridge and the G5 probe use).
* Action: `agent-bridge-hostw.exe serve --profile <profile>`, a tiny `WinExe` beside the
  host that starts `agent-bridge-host.exe` with no console window and waits for it, so the
  task tracks the host's exit for its restart rule, and no console flashes at logon. The
  host itself stays a console program for the CLI.
* The app starts the host through the task when it is registered (so it runs the same way
  at logon and on demand), and directly otherwise.

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
* Feed: GitHub Releases of `danswett/agent-bridge` (pre-releases included unless turned
  off; every merge to main publishes one, below), or a folder or URL holding `releases.win.json` in
  `%LOCALAPPDATA%\AgentBridge\app\feed.txt` (for testing on a VM).
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
  install's from no earlier than installing was last allowed, so turning the switch on hours
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
* **When:** after the `product` tests (both systems) and the `app` job (build, pack, self-test)
  pass, for pushes to `danswett/agent-bridge`'s `main` only: never a pull request or a fork. It
  is the only job with `contents: write`, and runs one at a time (concurrency group
  `publish-prerelease`). Before it, `publish-gate` lets through only the commit `main` is at
  now: GitHub keeps one waiting job per concurrency group and cancels the older waiting one, so
  a slow build of an older merge must not reach the group and evict a newer one. A run whose
  commit main has moved past publishes nothing, and the newer commit's run publishes instead
  (re-run it if it failed).
* **What:** `build/publish-prerelease.ps1` uploads the app job's folder with `vpk upload github
  --pre` as a draft, adds `assets.win.json` and the notes, and only then publishes it, creating
  the tag `v<version>` on the exact commit; a client never sees half a release. Assets:
  `AgentBridgeApp-<v>-full.nupkg`, `AgentBridgeApp-<v>-delta.nupkg`,
  `AgentBridgeApp-win-Setup.exe`, `AgentBridgeApp-win-Portable.zip`, `releases.win.json`
  (this version's packages), `RELEASES` and `assets.win.json`, as on the first release
  (`v0.1.583`, published by hand) plus the delta.
* **Release notes:** `build/release-notes.ps1` lists the commits since the latest release, newest
  first (GitHub's compare API; the commit alone when that cannot be read), and `pack-app.ps1
  -ReleaseNotes` packs them (`vpk pack --releaseNotes`), so they are in the feed's
  `NotesMarkdown` for an installed copy to show after it updates.
* **Deltas:** `pack-app.ps1 -DeltaFrom` empties its releases folder, downloads the latest
  release's full package (`vpk download github --pre`) and, if it is older, puts it there, so
  `vpk pack` builds a delta from it. Another merge may publish between that pack and this one's
  publish, so under the lock the script checks the delta again: `vpk delta patch` on the latest
  release's full package must rebuild this version's full package byte for byte. If it does not
  (or there is no delta), it is rebuilt from that release with `vpk delta generate` and checked
  the same way; if that fails too, the full package is published alone, with a warning, since a
  wrong delta would cost every copy a failed patch first. A copy on the previous version
  downloads only the delta rather than the ~100 MB full package; one further behind applies the
  chain of deltas, as far back as the newest ten releases Velopack's GitHub source reads, or
  else the full package.
* **Once per version:** a version whose tag exists is skipped, so re-running a run publishes
  nothing twice. A version older than one already published is skipped (two merges close
  together, the later one's build first). A draft left by a failed attempt is deleted and the
  version uploaded again.
* **Pruning:** the newest 20 pre-releases stay; older ones are deleted with their assets, and
  their tags stay. A full (non-pre-release) release, a draft, or a release not tagged
  `v<major>.<minor>.<patch>` is never touched.
* **Pull requests** prove it without publishing: the app job runs the same script with
  `-DryRun`, which checks the folder and its delta against the latest release as above, has vpk
  read it into a scratch feed (`vpk upload local`),
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
* A pre-release is unsigned (below).

To install the latest, download `AgentBridgeApp-win-Setup.exe` from the newest pre-release at
<https://github.com/danswett/agent-bridge/releases> and run it (SmartScreen: More info, then Run
anyway). From a shell:

```powershell
$tag = gh release list -R danswett/agent-bridge -L 1 --exclude-drafts --json tagName --jq '.[0].tagName'
gh release download $tag -R danswett/agent-bridge -p AgentBridgeApp-win-Setup.exe
.\AgentBridgeApp-win-Setup.exe
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

**The window** ([ui.md](design/ui.md) §7, #180): Mica behind the app's own 48 px title bar, which carries the mark, "Agent Bridge" and a search box (Ctrl+K), beside the system caption buttons; a compact rail of Sessions, Machines, Launch and Usage, with Settings at its foot and a 3×16 Bridge pill on the page shown; and the pages on a layer with an 8 px top-left radius. Each page has its title in the type ramp's title role. The search box finds sessions on every machine by the words of their title, repository, branch, agent or machine, titles starting with them first, and opens the one chosen as a toast does. The window resizes freely down to 360×600 and opens where it was left (`<profile>-window.json`, maximised too), unless that is off every screen, when it opens centred on the main display at 1280×860 or as large as the display allows. WinUI's controls that follow the system accent (accent buttons, the rail's pill, checked controls, focused fields, selected text and links) are Bridge, from the generated accent palette ([design/README.md](../design/README.md)); high contrast keeps the system's colours.
| Page | What it has | Parity source |
|---|---|---|
| Sessions | Panes by window width ([ui.md](design/ui.md) §5.2, #180), at the PWA's breakpoints: from 1280 px the list (320), the session and an inspector (336, hidden and shown from the session's header) with its Details and Tools; from 760 the list (264–320) and the session with Conversation, Delegates and Tools tabs; below, one column, the list or the session with Back. The session open is the selected row (`MainViewModel.Selected`), whatever opened it (a row, search, a toast, the tray, a launch), and stays open through every resize; a machine tab that hides its row closes it. **List** (§6.1): one row per live session of every machine on the account (this one's over the pipe, the others' through the relay), with machine tabs (All, then each machine) and, on the title's row, the sort menu (Your order, Most recent, Machine) and New, which opens Launch. A row is two lines: the status glyph aligned to line 1 ([ui.md](design/ui.md) §2.2: the generated glyphs, turning while working or delegating unless Windows has animations off, stopping and starting as that setting changes, the violet diamond while an agent drives), the title, semibold with the 6 px Bridge dot for unread activity or else the age, then at most two facts on one line (#214): the status's first fact in its tone's colour, and the machine once there is more than one, else the repository. Never the model, counts or pid: those are in Details. Rows keep the person's order, kept in `<profile>-order.json` (#164): a new session goes to the top once and an update never moves a row; Alt+Up/Down on a row, its menu (right-click, press and hold, Shift+F10: Move up, Move down, Pin) and a drag by the grip that shows on hover move and pin rows, with "Pinned" and "Sessions" over each group once anything is pinned; a row whose session updated since it was last open has the dot; sessions a host removed while the app ran fold into Ended · N below the rows. **Session** (§6.2, §6.4, §6.5): the title and one meta line (glyph, the whole status, machine, repository), with Pin, Details and the inspector toggle as icon buttons; the conversation at the reading measure (712 px): your last message, the main agent's response (Markdown) with its age, reasoning behind "Show reasoning", the current tool's glyph named for its state with the call in mono; question forms in the stream headed "Main agent asks" or the delegate's name (single and multi choice, Other, boolean, text, number, Copilot permission Approve once / Deny); and the reply box, a card with a 2 px Bridge edge (Ctrl+Enter or the icon Send, paste an image, Attach: up to 100 MiB each in chunks over the pipe, `sessions.attach.direct`, or through the relay's blob store to another machine, `sessions.attach.blob`, or 256 KiB where neither is offered, which shrinks a larger image to fit). **Details** (the header's Details and the inspector): status, agent, machine, model, repository, branch and folder (mono, cut in the middle, whole in the tooltip), driver, turns, compactions, process, for how long, updated, ID (mono), then End session with the host's two-press rule. **Tools** (the inspector and the Tools tab): the tool history, loaded as it comes on screen and kept current only while it is. **Delegates** (the tab, until U8, #176): the background agents and commands the host counts. While the host's discovery is held, a line over every page says what holds it ("Discovery held: …", [v1-sessions.md](protocol/v1-sessions.md#discovery-held)). Each other machine's updates (#51, [v1-update.md](protocol/v1-update.md)) are on the Machines page | PWA cards (`pwa/js/ui`), which mirror `agent-bridge-reply-card.js`; the dashboard's `install_update` and `check_updates` buttons |
| Machines | One row per machine on the account (#180, #213): this one first, then the others connected, then those offline from the relay's roster. A row has the machine icon and the name on line 1 with its status trailing it (This machine, Online, Connecting or Offline), and at most two facts on line 2: the session count, and `View only` for a host that takes no answers, replies or End session from here, with the host's reason as the facts' tooltip rather than on the row. A machine offering updates (#51) has what its app is doing and how the last update ended inside its row, with Check and Install; an offline one has when it was last seen and the two-press Forget; one connected has Show sessions, which picks it in the Sessions page's machine tabs. Its buttons share one style, in one column under the text. Check all, beside the page's title, checks every machine listed and this one | PWA machines (pwa/js/ui/views.js) |
| Launch | Start or resume a session on any machine on the account that offers it (`sessions.launch`): machine, resume, agent, workspace (a new worktree where the folder is isolated), profile, model, effort, context and permissions as that machine offers them, and a first prompt sent whole. Each press is one launch with its own request id, sent again with the same id after a lost connection, so it starts once; the outcome (started, busy with the reason, refused, failed, unconfirmed) is shown and announced to screen readers, and the new session's card opens when it arrives. Try again sends a launch that could not be sent with the same request, but only to the same host process, never to one that restarted since, and only while the host still remembers the request (9 minutes from the first send). An error from the host says the launch may still have started, and a failure says what Try again would send, since the form may have changed since. A launch the machine reports failed is sent again as a new request. A host that names no run in its hello counts each connection as a new run, so a lost launch to it is reported unconfirmed rather than sent again. What was picked on a machine is kept while it drops out and when another machine is picked. A machine's choices are fetched only while the page is open, since that is costly at the host. A machine waiting on a folder-trust question says it is busy when the page opens or its choices are reloaded. Codex is not launched without a first prompt (nor needs one for a session brought over). Where the machine takes sessions from the account's others (`sessions.transfer`), Resume also lists the ended sessions they hand on (`sessions.transfer.serve`), by machine: picking one copies it over and resumes it as a new session in the workspace picked, with a command the app signs with its own device key, as a person's device (kind `app`), for the machine the session is on ([v1-transfer.md](protocol/v1-transfer.md)); the original stays. A transfer to this machine goes to its host through the relay, as the protocol requires, not the pipe; a transfer waits up to 12 minutes for its outcome and is resent for up to 20; why the form cannot go is said beside the Launch button, apart from the last launch's outcome. A launched agent asking whether to trust its folder ([v1-launch.md](protocol/v1-launch.md#folder-trust)), listed by the machine or named in a launch's outcome (which counts only until the machine's next listing, asked for at once), is shown in full (agent, the whole folder path wrapped and selectable, the repository root it applies to, the agent's own words); where the machine allows it and has approved this app, Trust and Quit answer it with `launch.trust.answer` and a command the app signs as a person's device for that machine, question and choice, through the relay only (this machine's own host through its loopback, never the pipe). Otherwise it says to answer at the machine and why; for this machine, "Allow answering from this app" turns the setting on with this app approved. An answer that may have typed is never offered again; an error offers only that same request again. A machine that takes no launches now (agent-ha-bridge runs there) is still listed, to show such a question read-only | PWA launch view (`pwa/js/ui/launch-view.js`), agent-ha-bridge's launch card |
| Usage | Each agent's windows: percent, amber at 75 %, red at 90 %, pace against the window, "resets in" | PWA usage panel |
| Settings | Host: running, version, profile, pid, start/stop/restart, start at sign-in, log tail; relay: sign in with GitHub (device code shown in the app), machine name, connection state; this machine: approving Copilot permission prompts from other devices (`permissions.remoteApproval`), answering folder-trust questions from devices (`launch.remoteFolderTrust`, applied at once; turned on with this app approved) and which devices may (`launch.remoteFolderTrustDevices`: this app, the account's phones, browsers and apps the relay lists, and any approved that are not connected; changes from here and from the Launch page go one at a time, each made from the list the host has just then, since the host replaces the whole list; a list changed meanwhile by another app or at a terminal is refused by the host and the change made again from it, so a device revoked there stays revoked), agent-ha-bridge state; app: show reasoning and tools (the old Detail toggle, kept in the app since every client now gets the full activity), toasts, start the app at sign-in, version and updates | status card, `install.ps1`, `agent-ha-bridge configure` |

**Toasts** (§7.2): a question raises a toast with its options as buttons (Approve once /
Deny for a permission prompt) and a text box for Other or a free-text answer; anything
larger (several fields, multi-select) gets Open. The answer goes to the host over the pipe
with a fresh request id, and a follow-up toast says what became of it. A session's toasts
share one tag, so a question replaces only its own, and a question answered anywhere
withdraws it. A finished turn raises a toast with the response's start (it can be turned
off on its own). A click opens the app on that session, waiting for it to arrive if the
click is what started the app. Nothing is raised while the app's window is in front.
Toasts go quietly into Notification Center, without popping up, while Windows says the
person is busy (Focus Assist, presentation mode, a full-screen app). The app keeps running
in the notification area when its window closes, so toasts keep coming.

The activator is the app's own (`INotificationActivationCallback` in an HKCU
`LocalServer32` registration, S3 finding 1, without the toolkit S3 used), under the AUMID
Velopack gives the app's shortcuts (`velopack.AgentBridgeApp`). It is registered with COM
before XAML starts, so a click that starts the app is held until the app is ready, and is
removed by Velopack's uninstall hook. A click on an answer button answers through the pipe
without opening a window. Only the installed app registers anything: a development copy,
another `--profile` and the demo show no toasts and write nothing to the registry.

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
* Code signing, attestations and publishing releases.
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
  host through the in-process relay with the host's account), toast plans and activation handling, automatic updates (`AutoUpdaterTests`: when one waits for the sessions, when it goes anyway, a read-only host beside agent-ha-bridge updating like any other, the other profiles it never stops, and the host never left down), host lifecycle with a
  fake task scheduler and launcher, the Markdown model and the demo host. Platform-neutral,
  so they run on Linux too.
* CI (`app` job) runs `build/pack-app.ps1`: publishes the app, host, launcher, injector and hook
  into one folder, checks for the app's `.pri`, the executables and the bundled Agent Bridge
  Mono TTFs with their `OFL.txt` (`Assets\Fonts`; SIL Open Font License, not MIT), and packs it
  with `vpk`, with a delta from the latest release. It then starts the published
  app with `--self-test`, which runs the demo (on CI, on a screen made 1600×1000 or more for the pass, as a
  window is never wider than its screen), finds the rows by automation id, checks each row's two lines, opens sessions from
  the list and answers a
  question on each machine by invoking its button's automation peer, checks the generated
  design resources in every theme, draws every glyph and icon, checks the status words' colours on rows and in the header and the tool glyph's name and that words and glyphs follow a theme flip, sets code in the bundled
  font, narrows the list with the machine tabs, resizes the window across the breakpoints (three panes at 1440, two with
  tabs at 1000, one column at 400×900, Back, and the session open kept through each), launches a session from the Launch page
  and waits for its row,
  checks Codex on the other machine is not launched without a prompt, checks the shell (the 48 px title bar, the
  search box finding and opening another machine's session, the rail and its Bridge pill, the layer's radius, an
  accent button in Bridge, the 360×600 minimum), checks the Machines page's rows (#213), draws every page, the Sessions page in three panes with the inspector, in Light and
  Dark and, on CI, turns Windows' high contrast on to draw every page again and check that every role and surface is a
  system colour, then turns it back off, and saves a picture
  of each page. Last, it dry-runs publishing the package (`build/publish-prerelease.ps1
  -DryRun`) and uploads `Setup.exe` and the packages as an artifact, which the `publish` job
  publishes for a push to main.
* `build/test-publish-prerelease.ps1` (the `product` job, on Windows): publishing and pruning,
  refusing a dev build, and the delta base and build identity `pack-app.ps1` gives the package,
  against stand-ins for gh, vpk and dotnet.
* Live checks run by the coordinator on a disposable VM (the checklist in the pull
  request).

To build and try it locally (nothing is installed or registered):

```powershell
pwsh build/pack-app.ps1 -Version 0.1.0 -NoPack
out\app\publish\AgentBridge.exe --demo
```
