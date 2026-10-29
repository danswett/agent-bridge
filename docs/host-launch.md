# How the host launches sessions

`session.launch` and `launch.options` ([contract](protocol/v1-launch.md)) start or resume an
agent session from another device, the way agent-ha-bridge's launch card does. The settings
they draw on are the profile's `config.json` ([host-config.md](host-config.md)). The code is
`src/AgentBridge.Host.Delivery/Launch/`.

## What agent-ha-bridge does

From `hooks/daemon-launch.ps1` (`Use-DaemonLaunchRequest`, `Resolve-DaemonRequestedLaunch`,
`Start-DaemonLaunch`, `Update-DaemonPendingLaunch`) and `hooks/session-launch.ps1`
(`Start-BridgeCopilotSession`, `Get-BridgeNewSessionArguments`, `New-BridgeSessionWorktree`,
`Get-BridgeResumableSessions`):

* **Where.** Only a workspace in `newSession.workspaces`, labelled; discovery suggests folders
  but never approves one. A resume only in its own folder, and only if that folder is in an
  approved workspace (or a worktree the bridge made for an isolated one). Paths are compared
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
| Folder trust | Answered by the bridge on a person's second press | **Never answered from here.** The outcome says the agent is asking, and only a person at the machine answers it. Copilot's own "Confirm folder trust" is recognised too. Launches are `busy` while it shows, for up to two minutes |
| Followed | One launch at a time; a newer press stops following the last | Each launch is followed on its own; starting (worktree, process) is one at a time |
| Codex binding | The first Codex registration written after the launch | A new Codex session whose process is the one started or runs under it (a process-tree walk, start times checked so a recycled id does not count). Timing alone only when the process cannot be traced and no other Codex launch could claim the session; a session is bound to one launch at most |
| Resuming twice | Prevented by following one launch at a time | A session a launch started an agent on is not resumable while that agent runs, checked again under the start lock, so two presses from two devices start one agent |
| Agency as a batch file | Run through PowerShell | `agency.cmd` is asked for its profiles and sessions through `cmd.exe` with the same quoting as a launch |
| The answer | Intermediate notes, then `started`, `failed` or `unconfirmed` | One result, after up to 60 s: `started` with the session id, else `unconfirmed` (still followed for 10 minutes, to release its worktree lock once it registers), `failed`, `busy` or `refused` |
| Models | Discovered by starting each agent headless (Copilot's JSON-RPC, Claude's SDK, Codex's app-server) | Not discovered: the configured list, else the built-in one, plus the models live sessions of that kind are using. Starting an agent to ask is left for later. An effort a model does not take is not moved to one it does |
| `auto` | The installed agent used most recently, then signed in | The first installed, in order (Agency, Copilot, Claude, Codex) |
| Codex without a prompt | Opened, then a first message offered from the card | Refused: Codex creates its session only on a first message, so a launch must carry one |
| Attachments | No | Copilot and Agency: files sent first (as for a reply) are staged privately and the prompt mentions each as `@path` |
| The agent token variable | Set in the child's environment, so a launched agent drives the bridge as itself | Not set: agent-bridge has no agent principal yet. The agent gets the host's environment |
| Where the host runs | A logon task, always on the desktop | Wherever it was started. Launching is offered only when the host is unelevated, interactive, in a logon session with a desktop (`WinSta0`); elevated, as a service or over SSH it is not, since the agent would be an administrator's, or on no screen |
| Worktree marker | `agent-bridge-created` | `agent-bridge-host-created`, so neither product counts or claims the other's trees. Mutex `Local\AgentBridgeHostWorktree_<sha256>`, lock reason `agent-bridge-host-launch:<hex>` |
| Reclaiming worktrees | Before each isolated launch, by its rules | **None.** This host never deletes a worktree. Those it made stay until a person removes them, and once `launch.worktrees.limit` of them exist for a repository, isolated launches there are refused saying so. `launch.worktrees.idleHours` is kept for the reclaimer that may come, which must follow agent-ha-bridge's rules exactly (marker, unlocked, clean including ignored files but its named build output, detached, no commit ahead of the base, no live session at or below it, older than the idle hours, never the shared root, never `--force`) |
| macOS and Linux | tmux | Not offered yet |

## In order, for every launch

1. Request UUID (`refused` otherwise). Every outcome is remembered for 15 minutes and
   returned for a repeat, which starts nothing.
2. Platform, agent-ha-bridge (G5), `launch.enabled`, and whether this process can start an
   agent on the desktop (`refused`); `LaunchProtocol.Check` (`refused`).
3. A trust question waiting (`busy`).
4. The options as `launch.options` would give them, and the request against them: the
   launcher installed; a resume on the list, its launcher of its kind, its folder still
   approved; otherwise an approved workspace, existing; profile, model, effort and context
   among those offered, the machine's default for each left out; permissions (`refused`,
   naming what is offered).
5. Attachments, claimed for this launch (`refused` if the agent cannot take them or they are
   not held for this device).
6. The start turn, one launch at a time. agent-ha-bridge and trust again. An isolated
   workspace gets its worktree (below), a resume in one of this host's worktrees reuses it;
   either is locked until the session registers. Attachments are staged. The command line is
   built and checked for length (`refused`). The agent is started (`failed` if Windows will
   not): a new console, the workspace as working directory, the host's environment.
7. The launch is followed: every 500 ms, a discovery pass (during the first 60 s) and a look
   for its session; its window read every second for a startup question; its process checked.
   Registered: `started`. Exited first (with 3 s for a launcher that hands over and exits):
   `failed`. A trust question: `unconfirmed`, and other launches are `busy` while it shows, up
   to two minutes. Codex's hook review: `unconfirmed`. Nothing in 60 s: `unconfirmed`. It is
   followed for up to 10 minutes in all; its worktree lock is lifted once it registers, and
   left for a person to look at if it never does.

## Isolation

For a workspace with `isolate: true`, each new session (never a resume):

1. `git rev-parse --show-toplevel` in the workspace: its repository (fails closed outside one).
   The workspace's subfolder in the repository is kept, so a workspace at `repo\src\svc`
   launches in `<worktree>\src\svc`.
2. A per-repository mutex across processes, by the repository's common git folder; a second
   operation at once is refused, not queued.
3. `git fetch origin --quiet` (a failure is logged and the local base used); the base is
   origin's default branch, else `origin/main`, `origin/master`, else `HEAD`.
4. The cap: worktrees registered with the repository that carry this host's marker, against
   `launch.worktrees.limit` (0 is no cap). At the cap: `refused`.
5. `<root>\<repo>-<yyyyMMdd-HHmmss>` (local time), `-1`, `-2`… for a name in use, then `git
   worktree add --detach <target> <base>`, and the marker written into the worktree's git
   administration folder with the time.
6. `git worktree lock --reason agent-bridge-host-launch:<random>`, lifted only if the lock
   still has that reason once the session registers.

Worktrees are under `launch.worktrees.root`, by default `~/repos/wt`, the same root
agent-ha-bridge uses: shared with other sessions and other repositories. This host never
sweeps it, removes anything from it, or touches a worktree it did not create.

## The log

```
launch 2b6e1f0a received (agent=agency, workspace=Bridge, profile=work; prompt of 812 chars)
launch 2b6e1f0a: worktree: created C:\Users\me\repos\wt\bridge-20261028-131443 at origin/main
launch 2b6e1f0a: new Agency session in 'Bridge' (C:\Users\me\repos\wt\bridge-20261028-131443) profile 'work'
launch 2b6e1f0a: started pid 41208 in C:\Users\me\repos\wt\bridge-20261028-131443 via Agency (session 0f3c1a2b-…)
launch 2b6e1f0a: Agency in Bridge registered as session 0f3c1a2b after 3.4 s
launch 2b6e1f0a: started session 0f3c1a2b
```

A trust question logs `… is asking whether to trust the folder; left for a person at the
machine` and `…: unconfirmed session … (Copilot started, and is asking whether to trust …)`;
a later press while it shows, `…: busy (Busy: …)`. At start the host logs whether launching
is on and which workspaces are approved, and any problem in `config.json`'s `launch` section.
Prompts and attachment names are never logged, only their length and count.
