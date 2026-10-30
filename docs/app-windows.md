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
Noise channel through the relay, carrying the same v1 ops. Answers, replies (attachments up
to 256 KiB, an image shrunk to fit), End session, usage and toasts work on another machine's
cards exactly as on this one's, each going to that card's own host.

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
* **Not the app's management.** Another machine's host is never managed from here: `host.*`
  is the pipe's alone, and a relay channel answers it with `unknown op`.
* **On screen.** With more than one machine, a picker over the cards shows every machine or one
  of them, and each card starts with its machine's name. Usage merges every machine's
  readings into one row per allowance, the freshest reading of each, as the PWA's does. A
  toast for another machine's question says which machine, and its answer goes there.

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

The Velopack package holds the app, the host, its launcher and the injector, published
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
  off), or a folder or URL holding `releases.win.json` in
  `%LOCALAPPDATA%\AgentBridge\app\feed.txt` (for testing on a VM).
* Uninstall (`OnBeforeUninstallFastCallback`) stops the host and removes the logon task,
  the app's `Run` entry and the toast registration. The host's profile (device key,
  account, `config.json`, logs) stays in `%LOCALAPPDATA%\AgentBridge`, like any app's
  data, so a reinstall picks up where it was.

### Signing (later)

CI builds an unsigned Velopack artifact only. A release needs `vpk pack --signParams`
(signtool arguments) or `--azureTrustedSignFile` with the certificate or Trusted Signing
account held as a repository secret, signing every executable in the package and
`Setup.exe`, and a release workflow that publishes GitHub releases as drafts. Until then a
downloaded `Setup.exe` meets SmartScreen and, where it is on, Smart App Control.

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
reason in a banner over the cards and on the Settings page, and nothing more. Migration is a later,
explicit step.

## The app

| Page | What it has | Parity source |
|---|---|---|
| Sessions | One card per live session of every machine on the account (this one's over the pipe, the others' through the relay), with a picker for one machine: status and glow (working, waiting, delegating, ending), title, agent, repository, branch, model, current tool, response (Markdown), reasoning, last message, tool history, background agents and commands, turns, compactions; question forms (single and multi choice, Other, boolean, text, number, Copilot permission Approve once / Deny), reply box (Ctrl+Enter, paste an image, attach files: up to 100 MiB each in chunks over the pipe, `sessions.attach.direct`, or 256 KiB from an older host, which shrinks a larger image to fit), End session with the host's two-press rule; fold per session, persisted, reopened by a new question. While the host's discovery is held, a line over the cards says what holds it ("Discovery held: …", [v1-sessions.md](protocol/v1-sessions.md#discovery-held)) | PWA cards (`pwa/js/ui`), which mirror `agent-bridge-reply-card.js` |
| Launch | Start or resume a session on any machine on the account that offers it (`sessions.launch`): machine, resume, agent, workspace (a new worktree where the folder is isolated), profile, model, effort, context and permissions as that machine offers them, and a first prompt sent whole. Each press is one launch with its own request id, sent again with the same id after a lost connection, so it starts once; the outcome (started, busy with the reason, refused, failed, unconfirmed) is shown and announced to screen readers, and the new session's card opens when it arrives. Try again sends a launch that could not be sent with the same request, but only to the same host process, never to one that restarted since, and only while the host still remembers the request (9 minutes from the first send). An error from the host says the launch may still have started, and a failure says what Try again would send, since the form may have changed since. A launch the machine reports failed is sent again as a new request. A host that names no run in its hello counts each connection as a new run, so a lost launch to it is reported unconfirmed rather than sent again. What was picked on a machine is kept while it drops out and when another machine is picked. A machine waiting on a folder-trust question says it is busy when the page opens or its choices are reloaded. Codex is not launched without a first prompt; why the form cannot go is said beside the Launch button, apart from the last launch's outcome | PWA launch view (`pwa/js/ui/launch-view.js`), agent-ha-bridge's launch card |
| Usage | Each agent's windows: percent, amber at 75 %, red at 90 %, pace against the window, "resets in" | PWA usage panel |
| Settings | Host: running, version, profile, pid, start/stop/restart, start at sign-in, log tail; relay: sign in with GitHub (device code shown in the app), machine name, connection state; this machine: approving Copilot permission prompts from other devices (`permissions.remoteApproval`), agent-ha-bridge state; app: show reasoning and tools (the old Detail toggle, kept in the app since every client now gets the full activity), toasts, start the app at sign-in, version and updates | status card, `install.ps1`, `agent-ha-bridge configure` |

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

* **Large attachments to other machines.** A reply to another machine's session carries
  files up to 256 KiB (an image is shrunk to fit). Larger ones will go through the relay's
  blob store as the PWA's do (`sessions.attach.blob`); the app's own machine needs none,
  since its pipe takes them.
* **Session transfer** (#70) from the app: the PWA's Move to another machine. It needs the
  app to sign transfer commands with its device key; the Launch page has none yet.
* **Pairing and a Devices page**: the account's other devices are trusted by signing in to
  it, as everywhere else for now.
* **Cancelling a large attachment** part way. The PWA has a Cancel while a file uploads; the
  app sends over the pipe at local speed and has none yet.
* Agent hook registration, migration from agent-ha-bridge and rollback (G4, G8).
* Code signing, attestations and publishing releases.
* Arm64 builds, the first-run wizard's agent detection and winget installs.

## Testing

* `tests/AgentBridge.Local.Tests`: framing, the server and client over a real pipe (or
  Unix socket), and refusals.
* `tests/AgentBridge.Host.Tests/LocalApiTests.cs`: the real host's handler over the pipe,
  `SessionService` with fixture sessions (the same snapshot and ops the relay tests
  check), the management ops, and that the relay never dispatches them.
* `tests/AgentBridge.App.Tests`: view models (the window's list, cards, forms, reply, End
  session, usage, settings), other machines (`FleetTests`: cards, answers and toasts per
  machine, the picker, machines coming and going, usage merged; `LaunchTests`: the Launch
  page's choices, every setting and the whole prompt sent, resume, Codex's first prompt,
  busy, failed and Try again, a lost response sent again with the same request, no approved folder,
  profile and context, and a launch on another machine, including one whose machine leaves or drops out; and the app reaching another
  host through the in-process relay with the host's account), toast plans and activation handling, host lifecycle with a
  fake task scheduler and launcher, the Markdown model and the demo host. Platform-neutral,
  so they run on Linux too.
* CI (`app` job) runs `build/pack-app.ps1`: publishes the app, host, launcher and injector
  into one folder, checks for the app's `.pri` and the executables, packs it with `vpk`
  and uploads `Setup.exe` and the packages as an artifact. It then starts the published
  app with `--self-test`, which runs the demo, finds the cards by automation id, answers a
  question on each machine by invoking its button's automation peer, narrows the list with
  the machine picker, launches a session from the Launch page and waits for its card,
  checks Codex on the other machine is not launched without a prompt, and saves a picture
  of each page.
* Live checks run by the coordinator on a disposable VM (the checklist in the pull
  request).

To build and try it locally (nothing is installed or registered):

```powershell
pwsh build/pack-app.ps1 -Version 0.1.0 -NoPack
out\app\publish\AgentBridge.exe --demo
```
