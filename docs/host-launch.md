# How the host launches sessions

`session.launch` and `launch.options` ([contract](protocol/v1-launch.md)) start or resume an
agent session from another device, the way agent-ha-bridge's launch card does. A session from
another machine is a launch too: [host-transfer.md](host-transfer.md). So is a press of Launch on
agent-ha-bridge's card for a migrated machine: the Home Assistant connector hands it to the same
launcher, as the peer `home-assistant` ([ha-connector.md](ha-connector.md#stage-5-launch)). The settings
they draw on are the profile's `config.json` ([host-config.md](host-config.md)). The code is
`src/AgentBridge.Host.Delivery/Launch/`.

## What agent-ha-bridge does

From `hooks/daemon-launch.ps1` (`Use-DaemonLaunchRequest`, `Resolve-DaemonRequestedLaunch`,
`Start-DaemonLaunch`, `Update-DaemonPendingLaunch`) and `hooks/session-launch.ps1`
(`Start-BridgeCopilotSession`, `Get-BridgeNewSessionArguments`, `New-BridgeSessionWorktree`,
`Get-BridgeResumableSessions`):

* **Where.** Only a workspace in `newSession.workspaces`, labelled; discovery suggests folders
  but never approves one. A resume only in its own folder, and only if that folder is an
  approved workspace itself, not one below it (or is in a worktree this host or agent-ha-bridge made for an
  isolated one; agent-ha-bridge's are resumed in but never counted, locked or removed). Paths are compared
  physically, links resolved.
* **How.** `Start-Process` of the agent's executable (CreateProcess with a new console), in
  the working directory, with the daemon's environment plus the agent token variable. The
  daemon runs unelevated in the user's logon session (a logon task, hidden through a `.vbs`),
  so the agent's console is a normal window on the person's desktop. No window title is set
  on Windows.
* **Width.** The host's launches (Windows) make the agent's console 250 columns wide, so
  Copilot's form tab bar (the console's width less four) shows every field's title and the
  host can tell an answer's form is the question's before it types (#1123; a six-field form
  with long titles needed about 230). No console host takes a width from the start: with
  "wrap text output on resize" on (the default) the console host's buffer follows its window,
  and the screen clamps the window, so STARTUPINFO's count of characters
  (`STARTF_USECOUNTCHARS`) of 250 gave 75 columns on a 640-pixel screen, and `STARTF_USESIZE`
  no more (found by #1160's CI). A buffer set wider once the console exists is kept, the window
  scrolling across it. So the host widens it then: `agent-bridge-injector <pid> --widen 250
  <created>` holds the process open, checks it is still the one launched (its creation time,
  so a later process given the same id is never touched), attaches, types nothing, and calls
  `SetConsoleScreenBufferSize` (its height kept; never narrower), asked again every 100 ms for
  up to 5 s while the new process has no console yet, and not once it has exited.
  The log says the width it got, or why not. Windows Terminal, as the default terminal, takes
  a new console over and sizes it itself (its `ConptyConnection::InitializeFromHandoff`); the
  console host does not hand over a console started hidden or minimized
  (`_shouldAttemptHandoff`). So a launch starts minimized without taking the focus
  (`SW_SHOWMINNOACTIVE`): on the taskbar, in the console host, whatever the default terminal.
  The agent draws to the buffer's width (libuv's columns, which follow a resize), and the
  injector reads whole buffer rows. Restoring or resizing the window takes the buffer back to
  the window's width, as wide as the screen allows (about 200 columns or more at 1920
  pixels). Maximizing a launched session's window therefore narrows it, from 250 to what the
  screen allows. Where a form is then too wide (that, a session a person started, or titles
  longer than 250 columns hold), the answer is refused before any key, saying how many
  columns the titles need and to widen the window or make its font smaller, or, where they
  need more than 250, that the titles are too long for Slipgates to check (host-answers.md).
  Considered and not first: tabs of one Windows Terminal window (`wt -w <name> new-tab`):
  `wt` returns its own process, not the agent's, and the host reads and types through the
  agent's console (`AttachConsole` on its process); unproven. Wrapping the agent in
  `conhost.exe --width`: conhost re-splits and re-quotes the command line, undoing the quoting
  that keeps an npm `.cmd` shim's arguments safe, and the process started would be conhost.
  Unverified live: a Copilot session redrawing at the new width on Windows 11 with Windows
  Terminal as the default terminal, and how a 250-column window looks once restored.
* **The command line**, per agent: Copilot `--session-id <id> --banner [--model]
  [--reasoning-effort] [--context] [--allow-all] [extras] [-i <prompt>]`; Agency `copilot
  [--profile-only <profile>]` then Copilot's; Claude `--session-id <id>` (`--resume <id>`)
  `[--model] [--effort] [--autocompact] [--dangerously-skip-permissions] [extras] [-- <prompt>]`;
  Codex `--no-daemon -c model_reasoning_summary=detailed [-c model_reasoning_effort=…]
  [-c model_context_window=…] [resume] [--model] [--ask-for-approval never] [extras] [<id>]
  [-- <prompt>]`. Line breaks in the prompt become spaces.
* **Binding.** Copilot, Agency and Claude are given a fresh session id; Codex picks its own,
  and its launch is bound to the first registration written after it. A launch has started
  once its session registers; one process at a time is followed, for up to 10 minutes.
* **Trust.** Claude can stop on "Do you trust this folder?" (and, with Allow all, its Bypass
  Permissions warning) before it registers. The daemon reads the window, asks for a second
  press of Launch within two minutes, and then answers it. While it waits, a launch an agent
  requests is `busy`. Codex's "Hooks need review" is reported, never answered.
* **Isolation.** A workspace marked `isolate` starts each new session in
  `~/repos/wt/<repo>-<yyyyMMdd-HHmmss>`, `git worktree add --detach` at a freshly fetched base,
  marked `agent-bridge-created` in its git administration folder, locked while the session
  starts, capped per repository (10), failing closed. The cap is enforced after a reclaimer
  has removed finished, idle, marked worktrees.

## What this host does

The same, from a request that carries every setting and the whole prompt, with these
differences, each deliberate:

| | agent-ha-bridge | This host |
|---|---|---|
| The request | A press of a shared button, with a payload slot for settings and the long prompt | One `session.launch`, with a request UUID: a resend gets the first outcome and starts nothing |
| The prompt | Any length through the payload; line breaks become spaces | At most 16 KiB in the request; every control character becomes a space; one starting with `/` or `!` is refused (the agent would run it as a command, as for a reply); one too long for the command line is refused, not cut |
| A batch-file agent (npm's `copilot.cmd`, `codex.cmd`) | Quoted for `CommandLineToArgvW` only, so `cmd.exe` acts on `&`, `|`, `%VAR%` in the prompt | `cmd.exe /e:ON /v:OFF /d /c` with each argument quoted as Rust's standard library does since CVE-2024-24576: `%` cannot expand and nothing can leave its quotes. Tested end to end through `cmd.exe` |
| Folder trust | Answered by the bridge on a person's second press | **Never answered on its own.** The outcome says the agent is asking, with the question (folder, repository root, the agent's words), and a person answers it at the machine or, with `launch.remoteFolderTrust` on, from their phone, browser or Windows app with a command that device signs (`launch.trust.answer`, below). Never on a press of Launch, from Home Assistant, the MCP server or an agent. Copilot's own "Confirm folder trust" is recognised too. Launches are `busy` while it shows, for up to two minutes |
| Followed | One launch at a time; a newer press stops following the last | Each launch is followed on its own; starting (worktree, process) is one at a time |
| Codex binding | The first Codex registration written after the launch | A new Codex session whose process is the one started or runs under it (a process-tree walk, start times checked so a recycled id does not count). Timing alone only when the process cannot be traced and no other Codex launch could claim the session; a session is bound to one launch at most |
| Resuming twice | Prevented by following one launch at a time | A session a launch started an agent on is not resumable while that agent runs, checked again under the start lock, so two presses from two devices start one agent |
| Agency as a batch file | Run through PowerShell | `agency.cmd` is asked for its profiles and sessions through `cmd.exe` with the same quoting as a launch |
| The answer | Intermediate notes, then `started`, `failed` or `unconfirmed` | One result, after up to 60 s: `started` with the session id, else `unconfirmed` (still followed for 10 minutes, to release its worktree lock once it registers), `failed`, `busy` or `refused` |
| Models | Discovered by starting each agent headless (Copilot's JSON-RPC, Claude's SDK, Codex's app-server) | Not discovered: the configured list, else the built-in one, plus the models live sessions of that kind are using. Starting an agent to ask is left for later. An effort a model does not take is not moved to one it does |
| `auto` | The installed agent used most recently, then signed in | The first installed, in order (Agency, Copilot, Claude, Codex) |
| Codex without a prompt | Opened, then a first message offered from the card | Refused: Codex creates its session only on a first message, so a launch must carry one |
| Attachments | No | Copilot and Agency: files sent first (as for a reply) are staged privately and the prompt mentions each as `@path` |
| The agent token variable | Set in the child's environment, so a launched agent drives the bridge as itself | No agent principal yet (#45). The agent gets the host's environment plus `AGENT_BRIDGE_SESSION` (its session id, or `launched` for Codex), which everything it runs inherits, however detached; the host's own command line refuses to change its config or sign in when it is set ([local-pipe.md](protocol/local-pipe.md#who-may-manage-the-host)) |
| Where the host runs | A logon task, always on the desktop | Wherever it was started. Launching is offered only when the host is unelevated, interactive, in a logon session with a desktop (`WinSta0`); elevated, as a service or over SSH it is not, since the agent would be an administrator's, or on no screen |
| Worktree marker | `agent-bridge-created` | `agent-bridge-host-created`, so neither product counts or claims the other's trees. Mutex `Local\AgentBridgeHostWorktree_<sha256>`, lock reason `agent-bridge-host-launch:<hex>` |
| Reclaiming worktrees | Before each isolated launch, by its rules | The same rules, at the same point ([below](#reclaiming-finished-worktrees)), for this host's own marker only, and the same two exempt build outputs. Each tree kept is logged with its reason |
| Incomplete discovery | Every launch refused while it lasts (`Launch held: waiting on …`) | Only what reads an absence waits: a resume of a held agent's session, or of one not on the list, and a new Codex session are `busy`, saying what holds it; nothing is reclaimed. Other new sessions start ([v1-launch.md](protocol/v1-launch.md#while-discovery-is-held)) |
| macOS and Linux | tmux | Not offered yet |

## In order, for every launch

1. Request UUID (`refused` otherwise). Every outcome is remembered for 15 minutes and
   returned for a repeat, which starts nothing.
2. Platform, agent-ha-bridge (G5), `launch.enabled`, and whether this process can start an
   agent on the desktop (`refused`); `LaunchProtocol.Check` (`refused`).
3. A trust question waiting (`busy`). A resume not on the list while discovery is held
   (`busy`).
4. The options as `launch.options` would give them, and the request against them: the
   launcher installed; a resume on the list, its launcher of its kind, its folder still
   approved; otherwise an approved workspace, existing; profile, model, effort and context
   among those offered, the machine's default for each left out; permissions (`refused`,
   naming what is offered).
5. Attachments, claimed for this launch (`refused` if the agent cannot take them or they are
   not held for this device).
6. The start turn, one launch at a time. agent-ha-bridge and trust again; a resume, or a new
   Codex session, whose agent's discovery is held (`busy`). An isolated
   workspace gets its worktree (below), a resume in one of this host's worktrees reuses it;
   either is locked until the session registers. Attachments are staged. The command line is
   built and checked for length (`refused`). The agent is started (`failed` if Windows will
   not): a new console, minimized, in the console host, then widened to 250 columns (Width,
   above), the workspace as working directory, the host's environment.
7. The launch is followed: every 500 ms, a discovery pass (during the first 60 s) and a look
   for its session; its window read every second for a startup question; its process checked.
   Registered: `started`, except for Copilot, which registers (its `inuse.<pid>.lock`) before
   its window is ready and can ask its trust question after that. Its launch is `started` only
   once its window shows its input box or key footer, and `unconfirmed` if neither shows within
   20 s. It is still followed after that, so a trust question it draws later still makes other
   launches `busy`. A launcher that exits, handing the session on, is followed through the
   session's own window. Exited first (with 3 s for a launcher that hands over and exits):
   `failed`. A trust question: `unconfirmed`, and other launches are `busy` while it shows, up
   to two minutes. Codex's hook review: `unconfirmed`. Nothing in 60 s: `unconfirmed`. It is
   followed for up to 10 minutes in all (30 while a trust question a person may answer from
   their devices waits); its worktree lock is lifted once it registers, and left for a person
   to look at if it never does.
8. A trust question answered from a person's device (`launch.trust.answer`, with
   `launch.remoteFolderTrust` on): the host checks the signed command and that its signer is a
   person's device on the relay that was approved at the machine (`launch.remoteFolderTrustDevices`), reads the agent's window again, and types only into that
   agent's own console while the folder and options read exactly as they did. Trust moves the
   highlight to the trusting option (Claude highlights "No, exit" first), sees it there, and
   presses Enter; Quit presses Esc. It answers `started` once the session registers (Copilot:
   its window ready), `declined` once the agent closes, and otherwise `unconfirmed`, "Not
   confirmed: answer at the machine", after 60 s. Nothing is typed twice.

## Isolation

For a workspace with `isolate: true`, each new session (never a resume):

1. `git rev-parse --show-toplevel` in the workspace: its repository (fails closed outside one).
   The workspace's subfolder in the repository is kept, so a workspace at `repo\src\svc`
   launches in `<worktree>\src\svc`.
2. A per-repository mutex across processes, by the repository's common git folder; a second
   operation at once is refused, not queued.
3. Finished worktrees are reclaimed ([below](#reclaiming-finished-worktrees)).
4. `git fetch origin --quiet` (a failure is logged and the local base used); the base is
   origin's default branch, else `origin/main`, `origin/master`, else `HEAD`.
5. The cap, counted after reclaiming: worktrees registered with the repository that carry
   this host's marker, against `launch.worktrees.limit` (0 is no cap). At the cap: `refused`.
6. `<root>\<repo>-<yyyyMMdd-HHmmss>` (local time), `-1`, `-2`… for a name in use, then `git
   worktree add --detach <target> <base>`, and the marker written into the worktree's git
   administration folder with the time.
7. `git worktree lock --reason agent-bridge-host-launch:<random>`, lifted only if the lock
   still has that reason once the session registers.

Worktrees are under `launch.worktrees.root`, by default `~/repos/wt`, the same root
agent-ha-bridge uses: shared with other sessions and other repositories. This host never
sweeps it, and never touches a worktree it did not create.

## Reclaiming finished worktrees

agent-ha-bridge's `Remove-BridgeFinishedWorktree`, protection for protection. It runs where
agent-ha-bridge runs it, before each isolated launch (not a resume) and before the cap is
counted, with the repository's worktree mutex held. agent-ha-bridge does not run it on a
timer, so neither does this host. It is best effort: anything it cannot clear stays, and the
launch goes on to the cap.

**Which worktrees.** Only those the repository's own `git worktree list --porcelain` registers,
never anything found by looking in the shared root, so a worktree of another repository is
never even considered. Of those, only the ones carrying this host's marker
(`agent-bridge-host-created` in the worktree's git administration folder): not
agent-ha-bridge's, not one a person made, and not one made before the marker existed. Never
the primary checkout.

**What keeps one.** Unknown means kept. A worktree stays if any of these holds:

| | Logged as |
|---|---|
| Whether a session is using it cannot be established (below), which keeps every one | `N worktree(s) were retained: whether a session is using them cannot be established (…)` |
| A live session, of any agent, works in it or below it (physically, links resolved) | `a live session is working in it` |
| A session waiting to be restored after a reboot or a crash worked in it or below it, or the host's record of those cannot be read, which keeps every one ([below](#restoring-after-a-reboot-or-a-crash)) | `a live session is working in it`, or `the sessions waiting to be restored cannot be read` |
| Its git common folder is not the repository's | `which repository it belongs to is uncertain` |
| It is locked (`git worktree lock`): a launch still starting, or anyone's lock | `it is locked (…)` |
| A tracked entry is a link, a submodule or a conflict; a path in it is a junction or symlink; a tracked file is missing | `it holds a link, a submodule, a conflict, or a tracked file that is missing or unreadable` |
| Any uncommitted or untracked file | `it has uncommitted or untracked files` |
| Any ignored file except the build outputs named below | `it has ignored files, which may be someone's local data` |
| A branch is checked out | `a branch is checked out in it` |
| `HEAD` has a commit that is not in the base (resolved to a commit in the repository, so with no `origin` it is the checkout's `HEAD`; agent-ha-bridge reads `HEAD` inside the worktree there, which finds nothing ahead) | `it has commits that are not in the base` |
| Its marker's time is missing or unreadable | `its ownership marker has no readable time` |
| Made less than `launch.worktrees.idleHours` ago | `it is younger than launch.worktrees.idleHours` |
| Git cannot answer any of these | `its git state cannot be read` |
| Git is working in it (its `HEAD.lock` or `index.lock` exists) | `git is working in it` |
| Removal stopped partway, because something arrived in it meanwhile | `its removal stopped without deleting anything recursively (…); the tracked files it had removed were restored` |

Each is logged once per launch as `retained worktree <folder name>: <reason>`, and a removal
as `removed finished worktree <folder name>`: the folder's name, never a path or a file in it.

**Live sessions.** Nothing is reclaimed while session discovery is held
([v1-sessions.md](protocol/v1-sessions.md#discovery-held)): a session it could not see may be
working in any of the trees, so the snapshot is unknown (`session discovery is held, waiting
on …`), asked again before each removal so a hold that begins meanwhile stops it. Then a
strict snapshot, agent-ha-bridge's `Get-BridgeWorktreeUsage`, not session discovery: every Copilot, Claude and Codex process (through every way this host recognises
one) must be accounted for by a session with a readable working directory (Copilot's
`workspace.yaml` for the session its `--session-id` or `inuse.<pid>.lock` names; Claude's and
Codex's agent-ha-bridge registrations, whose time must fall within the process's life). It is
unknown if any Agency launcher is running (it cannot be told from a session still starting),
if a process's start time cannot be read, if a registration cannot be read or names a different
live process, if a lock is older than its process, or if a session folder, registration or
working directory is a link or cannot be resolved. Since Agency stays running for as long as
its session does, **no worktree is reclaimed while any Agency session runs on the machine**,
exactly as with agent-ha-bridge.

**How it is removed.** Never `--force`, and never `git worktree remove`, which deletes ignored
files recursively even without force. With the worktree's own `HEAD.lock` and `index.lock`
taken (so nothing else works in it meanwhile), every check above is made again, liveness
included; then the clean tracked files are deleted through a private index (`git read-tree
HEAD`, `git rm -r`, which refuses a modified file), the named build outputs by exact path,
each folder with a non-recursive delete, the `.git` pointer moved aside and the worktree
folder deleted only if empty. Anything that arrived since makes a delete fail: the pointer is
put back and `git checkout-index --all` restores only the tracked files that are missing,
never overwriting one. The worktree's administration folder is left for git's own maintenance
(`git worktree prune` across the repository would also retire other owners' missing trees).

**Build outputs.** agent-ha-bridge exempts exactly `hook/agent-bridge-hook` and
`hook/agent-bridge-hook.exe`, the native hook its `AGENTS.md` tells every session to build,
without which every worktree of that repository was kept for ever and the cap refused all
launches there (#143). This host exempts the same two files, so a worktree of that repository
is reclaimed the same way under either product. They are exact paths, not patterns or folders,
and are reproducible only because a worktree reaches removal with no tracked file modified.
agent-bridge names none of its own: its build output is `bin/` and `obj/` folders, and an
ignored folder keeps a worktree like any other ignored data, since removing it would mean a
recursive delete. A worktree where agent-bridge was built stays until a person removes it.

## Restoring after a reboot or a crash

A machine that reboots, or a host that crashes, takes the sessions running there with it (#780).
The host keeps a record of the sessions it sees live, and when it starts again it restores those
that died, idle, through this page's resume path and every rule it has, and says what went.

**The record.** `restore.json` in the profile's folder, beside `config.json`. For each live session
only what `launch.options` already says of one: its id, agent, title, folder, model, whether a turn
was running and whether a question was waiting, and for a session this host launched, what it was
launched with (launcher, workspace, worktree, profile, model, effort, context, permissions), and
when this host restored it, until it has stayed live 10 minutes (for a session restored on request that the record holds no entry of, in `stamps`: its id and when, kept a day at most if it never registers). Never a prompt, a response, a
question's words or an attachment. With it, when the machine booted, the logon session's number
(an opaque id, so a missing logon marker within the same logon is not read as a new logon), this run's
`machine.instance`, when it was written and, after a clean stop, `stopped`. It is written on each
change that matters, at most every 250 ms, whole: to a temporary file, flushed to the disk, then
renamed over the last, so a power cut leaves the record before or the record after. A session that
goes stays in it for 60 s more, because a shutting-down machine ends its agents before its host. A
record that cannot be read (or one from a newer host) is set aside as `restore.json.unreadable`,
offers nothing, and keeps every worktree, on that start and every later one, until a person
removes that file: what was owed is unknown, and unknown is never read as nothing owed. So is one holding a field longer than the host ever writes, by the protocol's own limits: an id or a launch setting past 256 bytes, a title past 240, a model past 256, or a folder past Windows' longest path (32,767 characters); the host cuts the title and model it writes to the same limits. And one whose owed entry lacks its `entry`, `cause`, `at` or `decided`. One that
cannot be moved aside is left as it is and never written over: the record does not start that run.

**At start**, after discovery's first pass, each session the last run saw live is judged:

| | |
|---|---|
| Running now | Adopted as it is. Nothing to restore |
| Its agent's discovery is held ([v1-sessions.md](protocol/v1-sessions.md#discovery-held)) | Waits, and is judged once it can be: never read as gone (§6.3, #43) |
| The machine booted since (boot times more than 2 minutes apart), or on Windows the person's logon is new (a sign-out, or a shutdown with Fast Startup, which keeps the boot time): an empty, volatile key of the profile's, `HKCU\Software\AgentBridge\logon-<hash>`, which Windows drops with the logon and which holds no value (uninstall removes any left behind), missing, and the logon session's number not the one the record was written in; a start makes it only once it has written its record, so one that goes down before that leaves the next start reading the logon as new too | Died: a shutdown or a sign-out ends every session, so an ending written as it went down is the shutdown's |
| Same boot, and the last run saw it go | Ended under the host's eyes |
| Same boot, and its agent's endings are not yet all taken in (on a machine migrated from agent-ha-bridge, a Claude or Codex SessionEnd still in the hook intake's spool) | Waits until the intake has drained the spool, then is judged; while the profile's `intake.json` cannot be read no intake runs, and it waits; so it does while the intake cannot claim the spool (another host has it, or the claim file cannot be opened), since nothing here takes it in |
| Same boot, and its agent recorded a normal end | Ended. Copilot: a `session.shutdown` of the main agent after the last `user.message` and the last `session.resume` in its `events.jsonl`. Claude and Codex: its hook registration says `Ended` (their SessionEnd hook), the newest of its roots; an ending read while discovery was held is kept, since the intake may prune the registration before the hold lifts |
| Same boot, a Claude or Codex session, and its registration is gone | Uncertain: agent-ha-bridge's daemon prunes an ended registration within about a minute, and a Claude one once it is 240 minutes old, so its absence says nothing either way. Not offered, not in the push and never restored; its folder is kept from the worktree reclaimer for as long as an offer's (7 days), and the host log says so |
| Otherwise | Died with the host |

The cause is `reboot` across boots or logons; on the same boot, `restart` if the last run wrote `stopped`, else
`crash`. A Copilot session a person closed while the host was down, with nothing its agent records, reads
as died: it is offered, never typed into. A Claude or Codex one whose registration is gone reads as
uncertain (above): not offered, its folder kept.

**Restoring.** Once every session is judged, or 10 minutes after start while one still is (its agent's discovery held: those
judged are not kept back by it, and it is restored once it is judged, #855), what this start found died is restored, newest first,
one at a time, each as a resume from Launch would be (`launch <id> (host-restore): …` in the log):
every check that resume meets (agent-ha-bridge, `launch.enabled`, discovery held, its folder still
approved, not already running, busy while a folder-trust or Bypass Permissions question waits for a
person) and no prompt, so the session opens **idle** and nothing is typed. The agent's `extraArgs` in `config.json` (on a machine migrated from agent-ha-bridge, its imported `newSession.extraArgs`, which go to every agent's) are checked first (Engineering's ruling on #837; Launch is unchanged). A restore is refused, and the session stays offered, when they hold the agent's prompt flag (`-i` for Copilot and Agency, `--` for Claude and Codex), or any token that is not an option on the agent's allowlist with its value in the expected form: so no positional token, no `--` and nothing after it. The allowlist holds only options confirmed from each agent's own `--help` to carry no prompt: `--add-dir <directory>` for Copilot, Claude and Codex, its value a full path (as the next argument or after `=`). Agency has none yet, so its restores are offered rather than automatic. The refusal, which is logged, names the option and never a value. An extra argument could submit a prompt (`-i`, or Claude's bare positional one) or grant what the recorded permission mode does not (`--allow-all`, `--dangerously-skip-permissions`, `--ask-for-approval never`), and one an agent adds later could do either. Folder trust is never
answered: a restore that meets the question is `unconfirmed`: never counted restored nor shown with `restored` or Continue (one tap would type into the question), with replies to it refused while its window has not been seen ready (`LaunchService.HeldForReply`), and offered until its session registers; seen ready later, while its launch still follows it (even after its stamp is cleared, 10 minutes live), it is counted and shown restored then (`LaunchService.SeenReadyLate`), with the notification sent then if it was all that one waited for, and the next waits while launches are
busy, trying again every 15 s. It is not limited to the resume list's length: the session is found by its id, whatever its age, and a Copilot session with no summary or prompt to name it is found too, as "Copilot session". Its settings are what
this host launched it with, each kept only while `launch.options` still offers it (else the
machine's default), and one that launch left to the agent is left to it; a session started by hand, of which nothing is known, asks permission, whatever
the machine's default is: it is never given Allow all it may not have had. A restore that is refused
or fails stays offered (one that registered and then failed, as one that died again). Restored, and once its window has been seen ready, a session carries `restored` ([v1-sessions.md](protocol/v1-sessions.md#sessions))
for this run, at most 24 hours (the badge goes then; what the interrupted run left stays hidden while the session lives): when, why, whether it went mid-turn or with a question waiting, and
for one that went mid-turn `continue`, the words of the reply a client offers as **Continue**
("You were interrupted by a reboot at 03:12; carry on where you left off", in the machine's time).
Continue is an ordinary `session.reply` a person sends, with no more trust than any reply; no host
sends it, and `midTurn` and `continue` go once a prompt arrives after the restore, whether its turn is still running when the host looks or has already ended. A question or approval the interrupted run left waiting is not shown as asked (`questionLost` says it went); one asked after the restore is. Nor is the turn it was in shown as running: the session reads idle until a prompt arrives after the restore. That boundary outlives the host run: the record keeps each restored session's `boundary` (its latest restore), with `interrupted`, the ids of the questions it set aside as the interrupted run's and still unanswered (a stale Claude hook approval by its tool and time), the newest 64 of them (one left out is shown as new after a restart, never a new one hidden), for as long as it keeps the session, live or owed (one held undecided at a start and adopted once the hold lifts keeps it too), apart from the crash-loop stamp, and a host started again holds it on the sessions before its first discovery pass publishes them, so the interrupted run's question is never listed again as asked, or answerable, after a host restart. Replayed, those questions are told by their ids, not their times: a question asked after the restore that a clock set back dated before it is still shown. An entry without `interrupted` (an older record, or one not yet read with its boundary) is told by time, as before. The badge and Continue are not shown again. Replies still read the transcript first and refuse while it holds an unanswered ask, since typing into a question the agent may still show would answer it; for Copilot, a turn left open by a CLI that was killed ends with the new CLI's `session.resume`, but what it left asking still keeps replies out (the new CLI may show it again without writing it again, and Enter would accept it) until it is completed, aborted, replaced by a question the session itself asks since (a subagent's is its own, #855), or a prompt is typed at the machine; an answer to it is refused as stale.

**Crash loops.** Each restore is stamped in the record **before** its launch (on its owed entry, or in `stamps` for one the record holds no entry of), so a session that registers and dies at once, with the host, is still known as restored. A restore that starts nothing (busy, refused, or withdrawn because the host is stopping or its caller cancelled it before the agent started) puts back what was there before, its own stamp and mark only; a restore that registers and dies before its launch returns is offered as a loop. A restored session that dies before it has been seen live 10 minutes (with the host
watching, judged as at a start, so one its agent ended normally does not count; or found dead at the next start, including an offer an earlier run restored whose session had not registered) is not restored again: it is kept owed, offered, and counted in
`loops`. Its stamp is cleared only once it has been seen live that long; one still waiting to register (at folder trust) keeps it. A host that keeps starting
cannot restore one more than once. A host that stops while a restore waits its turn starts nothing more, and lets the one under way finish (a few seconds at most) before its last write, whether the queue's or one asked for; a stamp carried by no entry that never registers goes after 24 hours, with a write due then.

**What died** and was not restored is kept in the record as owed, offered for 7 days (counted from when it is offered, and let go on time while the host keeps
running; one still being judged is never let go) unless it is resumed or seen live again, and said in `hello`'s `machine.restore` ([v1-sessions.md](protocol/v1-sessions.md#hello)) for
this run, at most 24 hours, and in one `restored` notification, sent when every session is judged and
restored, or 10 minutes after start, and only if this start found any that died. The notification
says how many were restored (a session restored and then offered as a loop is counted once, as a loop) and counts those offered, and names only those, restored or not, that went mid-turn or with a question waiting; the rest are left to Launch, where they are offered; one an earlier start offered is offered still in
`hello`, not notified again, and is not restored by a later start: a person resumes it from Launch.
One an earlier start was still judging is judged now, as of this start's cause. Beside agent-ha-bridge,
or while the machine is read-only or launching is off, nothing is restored: the sessions are only
offered (design G5), and are restored if the machine takes launches again while this run lasts.
A host the logon task's launcher started after the person pressed Stop in the app restores nothing: it
looks for the app's Stop marker before the record starts, and exits as it always has; and no restore
resumes an agent until it has looked again with its pipe open, so a Stop pressed while it starts leaves
what died offered (#986). With
`sessions.restoreOnStart` off ([host-config.md](host-config.md)), nothing is restored, what went is
said and nothing is kept owed, and what earlier starts kept owed is let go. (**Proposed, not built,
#948:** a person may restore what this run said from a client, `restore.run`, for as long as the
notice offers it; [restore-run.md](protocol/restore-run.md#what-changes-in-the-record).) Off only because
`config.json` could not be read, nothing is restored but what died is kept owed, as when it is on.

**For other callers** (#800, sessions an expired sign-in stopped): `ISessionRestorer.RestoreAsync`
resumes one session the same way, through the same one-at-a-time queue and checks, marks it restored,
once its window is seen ready (#855), with the cause it is given and Continue when it went mid-turn, and is refused while the machine is
read-only, or while that session is already running or being restored. It is busy, and starts nothing,
while the restore record cannot take its stamp first: the recorder did not start (a record that could
not be set aside) or failed, or the write failed.

**Worktrees.** The reclaimer keeps the worktree of every session owed or still being judged, as it
keeps a live session's, and of a restored session while it is still stamped, live or just gone, so the record's own snapshot holds
its folder as it dies (#855; every worktree while that folder is not known), and of a session restored on request until it registers, starts nothing, or
its stamp goes (every worktree, while its folder is not known, and across a restart, when only its stamp is left). A folder deleted since is placed under what is left of its path, so it keeps
the worktree it was in; one owed without a folder, one whose place cannot be resolved (a link that
cannot be read), or a record set aside, keeps every worktree.

**A clock set back** (a bad RTC, a VM restored from a snapshot, a year corrected) leaves times in the
record ahead of it. One more than 2 minutes ahead is read as now: an offer is kept, and let go a week
from then. No timer is set more than a day ahead, and one that fires early only looks again, since the
system's timers refuse anything past about 49.7 days and that refusal stopped the host at every start.
How long `hello` says what went (24 hours) and the 10 minutes before a partial notification are
counted on the monotonic clock, so a clock set back stretches neither.
**A clock set forward** more than the 7 days an offer is kept would let every offer go at once, nobody having
seen it. A step the host did not live through is not counted against them (#855): while it runs, a step of more
than 7 days that the monotonic clock did not take; at a start, a record last written more than 7 days
before, which the wall clock cannot tell from a machine off that long, so each offer keeps the time it had left
when the record was written, less 3 hours. While anything is offered the record is written at least every 3 hours,
so the host can have run at most that long after it: days it ran with nothing else to write are never forgiven, and an
offer goes after 7 days the host saw, however often it crashes. A smaller step counts, as days with the machine off do.
Anything else that stops the record from starting, or from being looked at later, is logged and keeps
every worktree for that run; the host goes on serving.

## The log

```
launch 2b6e1f0a received (agent=agency, workspace=Bridge, profile=work; prompt of 812 chars)
launch 2b6e1f0a: worktree: removed finished worktree bridge-20261026-090112
launch 2b6e1f0a: worktree: retained worktree bridge-20261027-164500: a branch is checked out in it
launch 2b6e1f0a: worktree: removed 1 finished; created C:\Users\me\repos\wt\bridge-20261028-131443 at origin/main
launch 2b6e1f0a: new Agency session in 'Bridge' (C:\Users\me\repos\wt\bridge-20261028-131443) profile 'work'
launch 2b6e1f0a: started pid 41208 in C:\Users\me\repos\wt\bridge-20261028-131443 via Agency (session 0f3c1a2b-…)
launch 2b6e1f0a: Agency in Bridge registered as session 0f3c1a2b after 3.4 s
launch 2b6e1f0a: started session 0f3c1a2b
```

A trust question logs `… is asking whether to trust the folder; left for a person at the
machine` (or `a person can answer it here or from their devices`) and `…: unconfirmed session …
(Copilot started, and is asking whether to trust …)`; a later press while it shows, `…: busy
(Busy: …)`. An answer from a device logs `folder trust for pwa 1a2b3c4d: trust: enter (if the
screen still shows it) -> Typed ok` and `folder-trust answer 5d0c9f3e from pwa 1a2b3c4d: started`;
a refusal, `folder-trust answer refused from 1a2b3c4d: …`. At start the host logs whether launching
is on and which workspaces are approved, and any problem in `config.json`'s `launch` section.
Prompts and attachment names are never logged, only their length and count.
