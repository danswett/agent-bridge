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
  so the agent's console is a normal window on the person's desktop, in whatever the default
  terminal is. No window title is set on Windows.
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
   not): a new console, the workspace as working directory, the host's environment.
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
   person's device on the relay, reads the agent's window again, and types only into that
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
