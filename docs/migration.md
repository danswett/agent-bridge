# Migrating a machine from agent-ha-bridge, and rolling it back

`agent-bridge-host migrate` moves one Windows machine from agent-ha-bridge's PowerShell
daemon to this host. `agent-bridge-host rollback` puts agent-ha-bridge back exactly as it
was. Both are explicit, at the machine, reversible and verified (design G8). Issue #38.

```
agent-bridge-host migrate [--dry-run] [--bridge-home <dir>] [--hook <file>] [--profile <name>]
agent-bridge-host rollback [--dry-run] [--profile <name>]
agent-bridge-host migration status [--profile <name>]
```

* `--dry-run` reads everything, says what it would do, and changes nothing.
* `--bridge-home` is the installation to migrate. It defaults to `~\.agent-ha-bridge`; a
  relative path is made absolute first.
* `--hook` is the `agent-bridge-host-hook.exe` to install. It defaults to the one beside the
  host, which is where the app ships it.
* `--profile` is the host profile to migrate into (`default` if omitted). A missing or invalid
  name is refused. The profile's folder is only read to look for it; it is never created, so a
  dry run against a name that does not exist leaves nothing behind.
* Any other word is refused before anything runs, so a mistyped `--dry-run` never migrates.
* Exit codes: 0 done, 1 refused or failed (the report says why), 2 usage or not Windows, 4
  run inside an agent session.

`migrate` and `rollback` are for a person, in a terminal of their own (§6.3 invariant 13). Inside
an agent session, or where their ancestry cannot be read in full, they refuse before reading
anything; a dry run and `migration status` still work. The host lets its own command line restart
it, and nothing else the app may (`docs/protocol/local-pipe.md`).

The host serving the profile must be running for `migrate`. The migration proves the hand-over
by restarting it, and a rollback restarts it too. The Windows app's Settings page does not offer
this yet; that is WinUI slice 2 (`docs/app-windows.md`).

## What migrate does

In order. A failure at any step after the snapshot rolls back on its own, and the report ends
with what the rollback did. A migrate or rollback (not a dry run) holds `migration.lock` beside the
profiles for all of it, so two at once (from any profile) cannot both pass the checks and the
second snapshot the first's changes as the state to restore; the second is refused.

1. **Preflight.** Refuses, before touching anything, when:
   * the profile is already migrated, or part-way through a migration;
   * the profile already has an `intake.json` with no migration record;
   * another profile is migrated, or part-way through a migration, from this installation or any
     other. Two hosts would handle every event twice (the intake also refuses to share a spool,
     see `docs/host-hooks.md`), and the host takes one installation's hooks at a time. A
     profile whose `intake.json` or `migration.json` cannot be read counts: it may hold a
     migration not yet rolled back, whose disabled tasks would be snapshotted as the originals;
   * `installation.json` is missing, is not schema 1 with a 32-hex id, belongs to another path,
     or is an isolated (test) installation;
   * agent-ha-bridge's `config.json` cannot be read, so its publication policy cannot be
     checked. Its names are read case-insensitively, as `Get-BridgeSetting` reads them (a
     `Dashboard.Publication` is the policy), and two names differing only in case make it
     unreadable, as `ConvertFrom-Json` finds them;
   * **this machine is the dashboard's publication writer** (`dashboard.publication.participant`
     equals `writer`), or the policy is there but cannot be read (G7). The writer migrates
     last, at P7. A machine with no `dashboard.publication` is not a writer;
   * the hook program is missing;
   * a settings file it would rewrite (or the profile's `config.json`) has another hard link;
   * the hook program's path has a space and Codex runs agent-ha-bridge's PowerShell hook
     directly: Codex cannot run a path with a space, and that entry would go on writing beside
     the host. (Entries running the Go hook are left: they hand their events to the spool);
   * an agent's registrations run agent-ha-bridge's PowerShell directly (an older install's
     `powershell`, `run-hook.cmd` or `pwsh -File` entries) while any of that agent's processes
     runs, or they cannot all be identified (a node or bun whose command line could not be read
     counts as one that may be the agent's). A session keeps the hooks it started with, so
     re-pointing the file would not reach it, and those scripts would go on writing
     registrations, approval markers and Home Assistant state beside the host. End its
     sessions, or let agent-ha-bridge's installer register its native hook first. Sessions
     holding the Go hook's entries do not matter: those spool to the host. (After a rollback,
     a session holding the host's hook spools too, to the daemon, which takes the same spool);
   * no host is serving the profile;
   * the settings to carry over would leave the profile's `config.json` with problems, in
     launching or in the Home Assistant connector (a `baseUrl` it could not use, say).
2. **Snapshot.** Writes `migration.json` in the profile's folder (state `migrating`) and a copy
   of every file it will rewrite in `migration-snapshot\` beside it. The profile's folder is
   private to the user. It records each file's SHA-256 before and after, each hook entry it
   re-points, and whether each task was enabled. agent-ha-bridge's `config.json`,
   `installation.json` and instruction files are recorded **by hash only**: the config can hold
   secrets and is never copied. A snapshot that cannot be written refuses the migration, with
   nothing changed and nothing left behind.
3. **Daemon.** A task the scheduler cannot read (access denied, say) refuses the migration; only
   a task that does not exist is left out. Disables (never deletes) `AgentBridgeDaemon_<id>` and
   `AgentBridgeDevBoxKeepAwake_<id>`, then stops the supervisor, the daemon and the keep-awake.
   They are found as agent-ha-bridge's own `Stop-BridgeOwnedRuntime` finds them: pwsh running
   `-File <bridgeHome>\hooks\agent-bridge-<role>.ps1`, honouring the receipt in
   `runtime\<role>.process.json` when it names the process. The supervisor goes first, so it
   cannot restart the daemon. It then waits until the G5 probe sees agent-ha-bridge gone.
4. **Hook.** Copies `agent-bridge-host-hook.exe` to
   `%LOCALAPPDATA%\AgentBridge\bin\<installation id>`, with a `hook.json` pointing at the
   installation's runtime folder, and runs it once (`--version`) to prove it starts. It goes in a
   folder of the product's own because an app update replaces the app's folder, and a
   registration must never point at nothing. One folder per installation, because rollback keeps
   the hook and its pointer for sessions still running it: a later migration of another
   installation then gets a folder of its own, and never sends those sessions' events into its
   spool.
5. **Registrations.** Re-points agent-ha-bridge's own hook entries and nothing else (below).
   Each file's hash after is recorded before the file is written, so a migration cut short
   between the two still rolls back what it wrote. Each file is re-read immediately before it
   is written and must still be what the change was planned from (the same goes for the
   profile's `config.json`): an agent already on its native hook keeps running and may write its
   settings (`/model`, `/permissions`) meanwhile. If one has, the migration is undone and that
   file is left as the agent wrote it (one deleted meanwhile stays deleted). A save landing between that check and the rename is
   caught too: the file replaced is kept aside and checked, and put back if it was not what was
   planned from (and if it cannot be put back, it stays beside the file, the error naming it; a
   second save landing while the first is put back is kept beside it the same way).
   A file that was absent is created only if it still is.
6. **Settings.** Carries agent-ha-bridge's settings into the profile's `config.json` (below).
7. **Hand-over.** Writes `intake.json` (the runtime folder, and the agents' homes the
   installation recorded, whose hooks were re-pointed: the host reads sessions from those unless
   started with `--copilot-home`, `--claude-home` or `--codex-home`, since the host the app
   starts may not share the `COPILOT_HOME` agent-ha-bridge was installed under), restarts the host, waits for its heartbeat in the
   runtime folder, and runs the installed hook as Copilot would (`copilot agent_stop`). The
   hook gives the same fixed reply whether or not it could spool, so the proof is its own log
   line saying the run was spooled. It then waits until no event that old is left in the spool.
   Only then is the state `migrated`.

### The registrations

Each agent's file is matched exactly as agent-ha-bridge's own removal code matches it
(`Remove-BridgeCopilotHookEntries`, install-claude.ps1's `Remove-BridgeHooks`), so an entry
of the person's, or another installation's, is never taken. Only the command changes; every
other field (matcher, timeout, type) stays.

| Agent | File | agent-ha-bridge's entry | Becomes |
|---|---|---|---|
| Copilot | `~\.copilot\hooks\decision-notifier.json` | `exec <bridgeHome>\bin\agent-bridge-hook.exe`, `args [copilot, <name>, <script>]`, or `powershell & '<script>'` | `exec <hook>`, `args [copilot, <name>]` (`agent_stop`, `ask_user`, `permission`) |
| Claude | `~\.claude\settings.json` | `"<go hook>" claude <name> "<script>"`, or `"<ha-bridge>\run-hook.cmd" "<script>"` | `"<hook>" claude <name>` (`ask`, `notification`, `stop`, `register`) |
| Codex | `<bridgeHome>\codex-bridge\plugins\agent-ha-bridge\hooks.json` | `<go hook> codex hook <script>`, or `pwsh -NoProfile -ExecutionPolicy Bypass -File <script>` | `<hook> codex hook` |

Files are written whole, flushed to the disk and renamed into place (snapshots, `migration.json`,
`intake.json` and `hook.json` too), so a power cut leaves either the old file or the new one,
never a zero-filled one. A plain file is replaced keeping its ACL and attributes. A symbolic link
(a dotfiles checkout, say) is written through to its target and stays a link, as
agent-ha-bridge's `Set-Content` wrote it. The link is recorded, and rollback writes through it the
same way (making it a link again if something replaced it with a copy of what migration wrote).
A file with another hard link is refused in preflight: a rename would leave its other names on
the old contents, and rewriting in place could leave every name truncated if cut short.
JSON keeps a two-space indent and any byte order mark the file had. Codex asks for its hooks to be trusted again once, since their command
changed, and again after a rollback. Its trust lives in `~\.codex\config.toml`, which neither
step touches. Codex cannot quote a command, so if the hook's path has a space Codex's entries
are left on agent-ha-bridge's Go hook, which spools to the host just the same
(`docs/host-hooks.md`).

### The settings

Into the profile's `config.json` (`docs/host-config.md`), each keeping its meaning. A setting
the profile already has is kept, and the report says so. Workspaces are added to any already
approved; one whose folder is already approved (compared as the host reads it, with `~`, `.`
and `..` resolved) is skipped, and one whose label the profile already gives another folder is
skipped and reported. Each entry's `path`, `label` and `isolate` are read whatever their letter
case, as agent-ha-bridge reads them, and written as the host reads them. Two of agent-ha-bridge's sharing a label are both imported, and the host
tells them apart by folder, as agent-ha-bridge did.

| agent-ha-bridge | This host |
|---|---|
| `newSession.enabled` | `launch.enabled` |
| `newSession.workspaces` (label, path, isolate; one path or entry as a list of one, as `@(...)` reads it) | `launch.workspaces` |
| `newSession.defaultWorkspace` (as the label the host offers the folder it chose: agent-ha-bridge's `-eq` ignores case, the host matches exactly, and the profile may already approve that folder under its own label, or as the second of two sharing one, `label (folder)`) | `launch.defaultWorkspace` |
| `newSession.launcher` (trimmed and in lowercase, as `Get-BridgeLauncherKind` reads it; one it does not know is `auto`, as it was there, and reported) | `launch.defaultAgent` |
| `newSession.profiles` (one string as a list of one, as agent-ha-bridge's `@(...)` reads it), `defaultProfile` (as the listed profile it matched, whatever the case; as written when there is no list, since Agency's own are known only at launch) | `launch.profiles`, `launch.defaultProfile` |
| `newSession.resumeCount` | `launch.resumeCount` (at most 50 here; a larger value is reported) |
| `newSession.allowAllTools` | `launch.permissions` (`allowAll` or `ask`) |
| `newSession.model` (text: every agent's, as agent-ha-bridge falls back to it; object: per agent) | `launch.agents.<agent>.model` |
| `newSession.effort.<agent>`, `context.<agent>` | `launch.agents.<agent>.effort`, `context` |
| `newSession.models`, `efforts`, `contexts` `.<agent>` (numbers as their text, as agent-ha-bridge passes them; one value as a list of one, as its `@(...)` reads it) | `launch.agents.<agent>.models`, `efforts`, `contexts` |
| `newSession.extraArgs` (one string as a list of one) | `launch.agents.<each agent>.extraArgs` (agent-ha-bridge added them to every launcher) |
| `newSession.<agent>Path` | `launch.agents.<agent>.path` |
| `newSession.worktreeRoot`, `worktreeLimit`, `worktreeIdleHours` | `launch.worktrees.root`, `limit`, `idleHours` |
| `homeAssistant.baseUrl` | `homeAssistant.baseUrl` |
| `dashboard.urlPath` | `homeAssistant.dashboardUrlPath` |
| `detailedActivity` | `homeAssistant.detailedActivity` |
| `homeAssistant.agentUserIds`, `agentTokenEnvVar` | `homeAssistant.agentUserIds`, `agentTokenEnvVar` (read by the connector from its stage 2) |

#38's table lists `detailedActivity` and `homeAssistant.*` as having no equivalent. The HA
connector (#60) has since given the first four rows above one, so they are carried. The
connector is pointed at the same Home Assistant and **left off**. It needs a token of its own,
given at the machine (`agent-bridge-host ha token set`), and then `homeAssistant.enabled`.

Everything else is named in the report as not carried and stays in agent-ha-bridge's file:
`codexStartPrompt`, `discoverWorkspaces`, `platform.*`, `devBox.*`, `usage.*`,
`answerButtons.*`, `notifications.*`, `updates.*`, `clients`, the rest of `homeAssistant.*`
and `dashboard.*`. **No secret is ever copied**: `homeAssistant.token`, `agentToken` and
`updates.token` are named as such. `dashboard.publication` is never carried: this host never
publishes the dashboard (G7).

### What migrate leaves alone

* **`~\.agent-ha-bridge`**: nothing in it is written but the Codex plugin's `hooks.json`,
  which is where Codex reads agent-ha-bridge's hook command from. Its `config.json`,
  `installation.json`, scripts, secrets and runtime records are as they were.
* **Instruction blocks** (`~\.copilot\instructions\agent-ha-bridge.instructions.md`, the
  marked blocks in `~\.claude\CLAUDE.md` and `~\.codex\AGENTS.md`): left in place. They name
  agent-ha-bridge's agent token, which still authenticates against Home Assistant.
* **Home Assistant**: the dashboard, its Lovelace resources and the publication policy are
  never touched. Entities move as `docs/ha-connector.md` describes.
* **Worktrees** (the decision #38 asks for): agent-ha-bridge's trees, marked
  `agent-bridge-created` under its `newSession.worktreeRoot`, are neither counted, claimed nor
  deleted by this host. **While migrated nobody reclaims them.** They are a fixed set, since
  nothing creates new ones. A rollback resumes agent-ha-bridge's own reclaimer on them. When
  a machine is finally done with agent-ha-bridge, the person runs its
  `Remove-BridgeFinishedWorktree` once before uninstalling. The host's own trees carry
  `agent-bridge-host-created` and are reclaimed by the host.
* **Attachments**: files agent-ha-bridge staged stay in
  `%LOCALAPPDATA%\agent-ha-bridge\attachments\install-<id>`. Its 24-hour sweep stops with the
  daemon and resumes after a rollback. This host stages its own in its profile and keeps them
  24 hours too.

## What rollback does

1. Removes `intake.json`.
2. Restores each file migration rewrote. A file unchanged since the migration is restored
   **byte for byte** from the snapshot. A file edited since gets back only the command of each
   entry migration re-pointed, with the later edits kept, a changed timeout included. Entries
      are matched by the command they now run, within the event they were registered under, and
      paired one to one in order, so two events whose originals differ but share a replacement
      (Claude's `run-hook.cmd` form and its Go form both become `claude register`) each get their
      own original back. A hook still running this host's program that cannot be matched,
   or a file that cannot be reverted, is reported as a problem, and the snapshot is kept. The profile's own `config.json` is restored, or removed if
   migration created it, unless a person has changed it since. Each is re-read just before it
   is written. A file that cannot be put back (a missing snapshot, a read-only file, a sharing
   violation) is reported and the rest carry on: the steps below always run, since with
   `intake.json` gone nothing else would take the hooks' events. An undo that fails anyway keeps
   its report, and the record and snapshot for `rollback`.
3. Checks the files recorded by hash, and reports any that changed while migrated (by someone
   else; neither step writes them).
4. Restarts the host, which reads both only when it starts: it lets go of the spool, removes
   its heartbeat, and runs on its restored settings. Rollback then waits (up to the stop
   timeout) until the spool's claim file, `runtime\agent-bridge-host-intake.lock`, is free. If
   the host still holds it, agent-ha-bridge's tasks are re-enabled but not started, the report
   says so and the record is kept, so the two never take the spool at once; run rollback again once the host has let go.
5. Re-enables each task that was enabled before, starts it, and waits until the G5 probe sees
   agent-ha-bridge's daemon running again: its own mutex seen, since a probe that cannot tell,
   or a supervisor or task running while the daemon fails to start, would otherwise pass for
   it, and the record and snapshot would go with nothing running. A task that was disabled before stays disabled.
6. Deletes `migration.json` and the snapshot. If anything above went wrong they are kept, and
   the report says what to do. A migration cut short (`migration status` says "part-way") is
   rolled back the same way.

Rollback keeps `%LOCALAPPDATA%\AgentBridge\bin\<installation id>\agent-bridge-host-hook.exe`
and its `hook.json`. A session started while migrated still calls that hook until it exits, and
it spools to agent-ha-bridge's daemon just as the Go hook does (`docs/host-hooks.md`). A later
migration of the same installation replaces it; another installation's goes in a folder of its
own, so this pointer is never changed under those sessions.

## While migrated

* Do not run agent-ha-bridge's installer or its update (the update button does nothing while
  the daemon is stopped). It would add its hooks back beside the re-pointed ones, and every
  event would be spooled twice. Roll back first.
* The host brings the hook migrated agents run (the copy in
  `%LOCALAPPDATA%\AgentBridge\bin\<installation id>`) up to the one beside it whenever it starts,
  so an app update's hook reaches them; `hook.json` is left as it is, and a hook still running
  finishes on the old copy. A new copy that cannot be moved into place puts the previous one
  back, and a replacement cut short (a crash between the two moves) is put back the next time
  the host starts, before it takes the spool, even with no hook beside it to update from.
* `agent-bridge-host migration status` shows the installation, since when, and whether each
  re-pointed file is still as migrated. `agent-bridge-host hooks status` shows the spool.
* Sessions the host launches do not get agent-ha-bridge's agent token in their environment
  (`AGENT_HA_AGENT_TOKEN`), because the secret is not copied. The connector's stage 2 owns
  agent attribution.

## What the live rehearsal still checks

The tests (`MigrationTests`) migrate and roll back a fake installation laid out on disk as
`install.ps1`, `install-claude.ps1` and `install-codex.ps1` lay one out, under a temporary home:

* registrations in all three agents' files, with the person's own entries beside them;
* config with secrets and a publication policy, instruction blocks, and runtime receipts.

They run the real hook logic against the real intake, and a whole-home listing must be equal
before migrating and after rolling back. Stopping agent-ha-bridge's processes is tested
against real pwsh processes. Task Scheduler and the host's restart are faked.

What only a live run shows is #38's own "verify before finishing" list:

* a question answered from the HA card and from the app;
* a reply delivered;
* the dashboard and phone flow as before after a rollback.

That is the VM rehearsal (#39; `docs/parity.md`, "Migration readiness"). Answering from the HA
card on a migrated machine waits for the connector's command stage.
