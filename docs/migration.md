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
* `--bridge-home` is the installation to migrate. It defaults to `~\.agent-ha-bridge`.
* `--hook` is the `agent-bridge-host-hook.exe` to install. It defaults to the one beside the
  host, which is where the app ships it.
* Exit codes: 0 done, 1 refused or failed (the report says why), 2 usage or not Windows.

The host serving the profile must be running for `migrate`. The migration proves the hand-over
by restarting it, and a rollback restarts it too. The Windows app's Settings page does not offer
this yet; that is WinUI slice 2 (`docs/app-windows.md`).

## What migrate does

In order. A failure at any step after the snapshot rolls back on its own, and the report ends
with what the rollback did.

1. **Preflight.** Refuses, before touching anything, when:
   * the profile is already migrated, or part-way through a migration;
   * the profile already has an `intake.json` with no migration record;
   * `installation.json` is missing, is not schema 1 with a 32-hex id, belongs to another path,
     or is an isolated (test) installation;
   * agent-ha-bridge's `config.json` cannot be read, so its publication policy cannot be
     checked;
   * **this machine is the dashboard's publication writer** (`dashboard.publication.participant`
     equals `writer`), or the policy is there but cannot be read (G7). The writer migrates
     last, at P7. A machine with no `dashboard.publication` is not a writer;
   * the hook program is missing;
   * no host is serving the profile;
   * the settings to carry over would leave the profile's `config.json` with problems.
2. **Snapshot.** Writes `migration.json` in the profile's folder (state `migrating`) and a copy
   of every file it will rewrite in `migration-snapshot\` beside it. The profile's folder is
   private to the user. It records each file's SHA-256 before and after, each hook entry it
   re-points, and whether each task was enabled. agent-ha-bridge's `config.json`,
   `installation.json` and instruction files are recorded **by hash only**: the config can hold
   secrets and is never copied.
3. **Daemon.** Disables (never deletes) `AgentBridgeDaemon_<id>` and
   `AgentBridgeDevBoxKeepAwake_<id>`, then stops the supervisor, the daemon and the keep-awake.
   They are found as agent-ha-bridge's own `Stop-BridgeOwnedRuntime` finds them: pwsh running
   `-File <bridgeHome>\hooks\agent-bridge-<role>.ps1`, honouring the receipt in
   `runtime\<role>.process.json` when it names the process. The supervisor goes first, so it
   cannot restart the daemon. It then waits until the G5 probe sees agent-ha-bridge gone.
4. **Hook.** Copies `agent-bridge-host-hook.exe` to `%LOCALAPPDATA%\AgentBridge\bin`, with a
   `hook.json` pointing at the installation's runtime folder, and runs it once (`--version`)
   to prove it starts. It goes in a folder of the product's own because an app update replaces
   the app's folder, and a registration must never point at nothing.
5. **Registrations.** Re-points agent-ha-bridge's own hook entries and nothing else (below).
6. **Settings.** Carries agent-ha-bridge's settings into the profile's `config.json` (below).
7. **Hand-over.** Writes `intake.json`, restarts the host, waits for its heartbeat in the
   runtime folder, runs the installed hook as Copilot would (`copilot agent_stop`), and
   waits for the host to take that event from the spool. Only then is the state `migrated`.

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

Files are written whole and renamed into place, with a two-space indent and any byte order
mark the file had. Codex asks for its hooks to be trusted again once, since their command
changed, and again after a rollback. Its trust lives in `~\.codex\config.toml`, which neither
step touches. Codex cannot quote a command, so if the hook's path has a space Codex's entries
are left on agent-ha-bridge's Go hook, which spools to the host just the same
(`docs/host-hooks.md`).

### The settings

Into the profile's `config.json` (`docs/host-config.md`), each keeping its meaning. A setting
the profile already has is kept, and the report says so. Workspaces are added to any already
approved; one whose folder is already approved is skipped, and one whose label is taken by
another folder is skipped and reported.

| agent-ha-bridge | This host |
|---|---|
| `newSession.enabled` | `launch.enabled` |
| `newSession.workspaces` (label, path, isolate) | `launch.workspaces` |
| `newSession.defaultWorkspace` | `launch.defaultWorkspace` |
| `newSession.launcher` | `launch.defaultAgent` |
| `newSession.profiles`, `defaultProfile` | `launch.profiles`, `launch.defaultProfile` |
| `newSession.resumeCount` | `launch.resumeCount` (at most 50 here; a larger value is reported) |
| `newSession.allowAllTools` | `launch.permissions` (`allowAll` or `ask`) |
| `newSession.model` (text: Copilot's; object: per agent) | `launch.agents.<agent>.model` |
| `newSession.effort.<agent>`, `context.<agent>` | `launch.agents.<agent>.effort`, `context` |
| `newSession.models`, `efforts`, `contexts` `.<agent>` | `launch.agents.<agent>.models`, `efforts`, `contexts` |
| `newSession.extraArgs` | `launch.agents.<each agent>.extraArgs` (agent-ha-bridge added them to every launcher) |
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

1. Removes `intake.json` and restarts the host, which then stops taking the spool and removes
   its heartbeat. The host stands back before the daemon starts.
2. Restores each file migration rewrote. A file unchanged since the migration is restored
   **byte for byte** from the snapshot. A file edited since gets back only the entries
   migration re-pointed, with the later edits kept. A file that cannot be reverted is left, and
   the rollback reports a problem. The profile's own `config.json` is restored, or removed if
   migration created it, unless a person has changed it since.
3. Checks the files recorded by hash, and reports any that changed while migrated (by someone
   else; neither step writes them).
4. Re-enables each task that was enabled before, starts it, and waits until the G5 probe sees
   agent-ha-bridge running again. A task that was disabled before stays disabled.
5. Deletes `migration.json` and the snapshot. If anything above went wrong they are kept, and
   the report says what to do.

Rollback keeps `%LOCALAPPDATA%\AgentBridge\bin\agent-bridge-host-hook.exe` and its
`hook.json`. A session started while migrated still calls that hook until it exits, and it
spools to agent-ha-bridge's daemon just as the Go hook does (`docs/host-hooks.md`). A later
migration replaces it.

## While migrated

* Do not run agent-ha-bridge's installer or its update (the update button does nothing while
  the daemon is stopped). It would add its hooks back beside the re-pointed ones, and every
  event would be spooled twice. Roll back first.
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
