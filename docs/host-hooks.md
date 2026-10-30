# Hooks and hook intake

On a machine migrated from agent-ha-bridge, the host takes over what agent-ha-bridge's
PowerShell daemon did with the agents' hooks: it keeps the heartbeat the hooks check, takes
the events they spool, records the Claude and Codex session registrations and the Codex
approval marker, and tells the rest of the host. It never reaches Home Assistant from this
path. When the host is down, every hook still answers its agent at once with the fixed safe
reply. Issue #37; design §6.1 `Host.Intake`, §6.3 invariants 3 and 14.

On every other machine nothing here runs: no spool is read, no heartbeat is written and no
registration is touched (G4, G5). Taking over is the explicit migration of #38
(`agent-bridge-host migrate`).

## Decision: the host ships its own native hook, speaking agent-ha-bridge's spool contract

The design said to keep agent-ha-bridge's Go hook and change only its fallback target to a
NativeAOT `agent-bridge-host hook --fallback`. That cannot work as written. The Go hook
(`hook/main.go`, `runPowerShell`) runs its fallback only as `pwsh -File <script>`, so any
fallback reached through it costs PowerShell's start (about 250 ms) and has to be a script.
The alternative of dropping the fallback argument, so the Go hook prints its fixed reply,
leaves the product depending on `~/.agent-ha-bridge\bin\agent-bridge-hook.exe`, which
agent-ha-bridge's uninstall removes. It also gives fresh installs (#54) nothing to register.

So the product ships **`agent-bridge-host-hook`**: NativeAOT, in the app's folder beside the
host. It is the same program as the Go hook in every way an agent or a consumer can see:

* **The same spool contract**: folder, file format, names, heartbeat and log (below).
* **The same arguments**, minus the fallback script: `agent-bridge-host-hook <agent> <hook>`.
  Re-pointing a registration is therefore one mechanical rewrite of its command.
* **The same replies** (below).

What makes migration and rollback safe is that **both hooks speak one contract to both
consumers**:

| Hook in the registration | Host taking events (migrated) | agent-ha-bridge's daemon taking events (rolled back) | Nobody taking events |
|---|---|---|---|
| agent-ha-bridge's Go hook (a session started before migrating) | spools to the host | spools to the daemon | runs its PowerShell script, as before |
| `agent-bridge-host-hook` (a session started while migrated) | spools to the host | spools to the daemon | the fixed reply |

An agent reads its hook commands when a session starts. A session running across a migrate or
a rollback keeps calling the hook it started with, and is still served by whichever bridge is
running. Rollback restores the registrations byte for byte from the snapshot taken at migrate
time (#38). The migrate side writes the host's hook into them, with a pointer beside it to the
installation's runtime folder (below).

The design's `agent-bridge-host hook --fallback` is this program's own fallback: with no fresh
heartbeat it prints the fixed reply and nothing else. `agent-bridge-host-hook --fallback
<agent> <hook>` does the same unconditionally, for a registration that must never spool. It
replies before touching stdin, then drains it for up to 2 s, so neither an inherited console nor
a pipe left open can hold it, and a writer still sending its event is not cut off. Each such run
is logged as unspooled (`reply`, `--fallback`), so a Codex hook that ran this way still counts
as one the intake did not hear. Without
`--fallback` it waits at most 5 s for the whole event (one arrives in milliseconds), then gives
the fixed reply without spooling and logs `event not finished within 5s`: an agent that never
closes stdin cannot hold `ask_user` (invariant 3). agent-ha-bridge's Go hook waits for the end.

## The spool contract (both bridges)

All of it lives under the installation's runtime folder: agent-ha-bridge's
`<bridgeHome>\runtime`, which is `~\.agent-ha-bridge\runtime` by default. `SpoolContract`
holds every name; it is tested against files the Go hook writes and the daemon reads.

| Path | Who writes it | What |
|---|---|---|
| `agent-bridge-daemon.heartbeat` | The consumer: the daemon each pass, the host's intake every 2 s | A hook spools only while this is younger than 60 s (`Test-BridgeDaemonAlive`). The daemon writes its pid; the host writes its own, so it can remove only its own when it stops |
| `agent-bridge-spool\<name>.json` | Either hook | `{ "v": 1, "agent", "hook", "ancestors": [pids, nearest first], "receivedAt", "event": <stdin verbatim> }`, written as `<name>.json.tmp` and renamed so no reader sees half of one |
| `agent-bridge-hook.log` (+ `.1`) | Either hook | One JSON line per run: `{ at, agent, hook, path, reason?, ms }`. `path` is `spool`, `fallback` (the Go hook ran its PowerShell) or `reply` (the fixed reply alone). Rotated at 1 MB, keeping one previous file. This product's hook appends each line in one write through an append-only handle (`FILE_APPEND_DATA`, as the Go hook's `O_APPEND`), so two hooks finishing together never overwrite each other's line on Windows |

`<name>` is the time in Unix nanoseconds (20 digits), the hook's pid and 8 random hex digits.
Names sort in the order the events happened, which is the order they are handled. Events are
at most 16 MB and 512 levels deep, and the ancestry at most 12 processes. This product's hook
stops reading at 16 MB, even with the pipe still open, and gives such an event the fixed reply.
The Go hook checks its 16 MB before `json.Marshal` escapes `<`, `>` and `&` as six bytes each, so
the intake accepts a spool file of up to six times that.

## The hook: `agent-bridge-host-hook`

```
agent-bridge-host-hook <agent> <hook>              spool while the host takes events, else the fixed reply
agent-bridge-host-hook --fallback <agent> <hook>   the fixed reply only
agent-bridge-host-hook --version
```

It reads the event from stdin and finds the runtime folder from `hook.json` beside itself
(`{ "version": 1, "runtimeRoot": "<absolute path>" }`, written by `migrate`). It then records
its ancestry from its parent upward and checks the heartbeat. It spools the event if the event
is one JSON value, prints the reply, logs the run and exits 0.

| Hook | Reply |
|---|---|
| `claude/register`, `claude/stop`, `claude/ask`, `claude/notification`, `codex/hook` | nothing (their output would reach the model or change a decision) |
| `copilot/ask_user` | `{"permissionDecision":"allow"}` |
| `copilot/agent_stop`, `copilot/permission` | `{}` |

* **Invariant 14, hooks never fail the agent.** Each of these prints the fixed reply and
  exits 0:
  * no pointer;
  * an invalid pointer;
  * no heartbeat, or a stale one;
  * an event that is not JSON;
  * a spool it cannot write;
  * stdin that breaks;
  * any exception.

  There is no PowerShell to fall back to, and nothing on this path talks to the network.
* **Invariant 3, `ask_user` never blocks.** Copilot's `ask_user` always gets its allow
  straight away, so the question is drawn at the terminal and stays answerable there. The
  host reads it from the transcript and answers it the same way (first answer wins).
* **Ancestry** is read process by process on Windows (`NtQueryInformationProcess`). A
  snapshot of every process measured 30–45 ms on a busy machine, most of the budget, so it is
  only the fallback for when the hook's own parent cannot be asked. Linux reads `/proc`.
  macOS records none yet. The host ships on Windows first, and #48 covers the Mac.
* **Timings** on DSWETT-DEV-VM1 (547 processes), measured 2026-10-29:
  * spooled runs: 30–35 ms, as the hook logs them (from process start);
  * the fixed reply with no heartbeat: 23–28 ms;
  * agent-ha-bridge's Go hook on the same machine: an average of 45 ms over its last 400
    spooled runs.

  CI runs the published NativeAOT build as an agent does (`HookProcessTests`).

## The intake (`HookIntake`)

`serve` starts it only for a profile with an **`intake.json`** in its folder (`IntakeState`:
the runtime folder, the agent-ha-bridge installation's home and id, and when it migrated). A
missing or unreadable record means no intake. Off Windows the intake does not start yet: it
must be able to see agent-ha-bridge's daemon first. `sessions`, `get`, `watch` and `usage` read
the same profile's record (without creating the profile), so they find a migrated
installation's registrations where `serve` does.

**G5.** Before every pass, before every file, before each registration or marker it writes or
removes, and before deleting a handled file, the intake asks the same probe that
gates typing (`AgentHaBridgeProbe`, cached for 2 s). While agent-ha-bridge's daemon or
supervisor runs, or its task is running, or that cannot be checked, the intake stands back
completely:

* it writes no heartbeat;
* it lists, reads and deletes no spool file;
* it writes and prunes no registration.

Those all belong to the daemon then, and two consumers would handle each event twice. An event
being handled when the daemon is found commits nothing further and stays in the spool for it,
and is not counted as a failed attempt, nor dropped, even when its handling failed. (The probe's 2 s cache, and the moment between a check
and the write it guards, are the window left: agent-ha-bridge takes no lock the host could
share.) It takes the spool again once the daemon is gone. Every read is of a copy in memory, through a
handle that shares delete, so a reader never makes another consumer's delete fail. The shadow
comparison (#40, `docs/host-shadow.md`) never reads the spool at all. It does read the
heartbeat as the daemon's, so it is for a machine before it migrates: once migrated the
heartbeat is the host's.

**One host per spool.** The intake takes the spool only while it holds
`agent-bridge-host-intake.lock` in the runtime folder, opened for it alone and deleted when
closed. A second host taking the same installation's spool (another profile with an
`intake.json` for it) stands back in the same way until the first lets go. `migrate` also
refuses a second profile for an installation that one already has.

**Each pass**, as `Invoke-DaemonHookSpool`:

1. Touch the heartbeat.
2. List `*.json` in the spool, in ordinal name order. A `.tmp` is still being written and is
   left alone.
3. For each file: read it, record what it means (below), delete it, and tell subscribers.
   An event that fails is kept for one more try on the next pass, then dropped, deleted and
   logged. This covers an unknown hook, a file that is not JSON and an unreadable file.
   Until it has been tried again, nothing after it is handled. The daemon carried on past a
   failure, but then a Codex `PermissionRequest` retried after the `PreToolUse` that answered
   it would arm an approval nobody is being asked. Keeping strict order costs one pass.
   An event handled whose file could not be deleted (a reader that would not share the
   delete) is only deleted again on later passes, never handled again, for the same reason.
   The heartbeat is touched again every 10 s while a long backlog drains, so hooks keep
   spooling meanwhile. A host stopping ends the drain at the next file and leaves the rest for
   the next host, rather than outlasting the replacement's wait for the profile's lock.
4. Prune now and then (below), unless agent-ha-bridge started during the pass. Pruning, and
   withdrawing approvals (below), check for agent-ha-bridge again before each delete.

A `FileSystemWatcher` on the spool wakes a pass the moment a hook renames a file into place.
A sweep every 2 s covers anything the watcher misses.

**Pruning.** Every 60 s, as the daemon's reconcile did, the intake deletes registrations whose
session is over. Nothing else would ever delete them, and discovery reads every one each
second. It deletes:

* a Claude registration whose process has gone and that was last written over 4 hours ago;
* a Codex registration whose process has gone, or that has no process and is either ended or
  over 4 hours old. An ended one stays while its process runs, as the daemon's
  (`$endedCanPrune`): that process may still hold the thread's lock, and without the
  registration saying the session ended, discovery would find it again through the lock.

It prunes only when the process list is complete. A runtime (node, bun) whose command line
could not be read counts as live: it may be the session's.

**Stopping.** On a clean stop the host removes the heartbeat if it is still its own. Hooks then
give their fixed reply at once, rather than spooling to nobody until it goes stale.

### What each event records

| Spooled hook | agent-ha-bridge function | The host records |
|---|---|---|
| `claude/register` | `Invoke-ClaudeRegisterHook` | The registration. `UserPromptSubmit` sets `HookStatus: working`; `SessionStart` sets no status |
| `claude/stop` | `Invoke-ClaudeStopHook` | The registration, `HookStatus: idle` |
| `claude/ask` | `Invoke-ClaudeAskHook` | The registration, for `AskUserQuestion` only. The daemon's pending-decision marker is not written: the host reads questions from transcripts |
| `claude/notification` | `Invoke-ClaudeNotificationHook` | The registration. `HookStatus: waiting` only for a notification Claude is blocked on (`notification_type`, or anything but the idle reminder's wording) |
| `codex/hook` | `Invoke-CodexHook` | The registration's status and activity per event (`SessionStart` idle, `UserPromptSubmit` and `PreToolUse` working, `PermissionRequest` waiting, `Stop` idle with the reply's first line, `SessionEnd` ended). `SessionStart`, `PreToolUse`, `UserPromptSubmit`, `Stop` and `SessionEnd` remove a pending approval marker (`SessionStart` too, unlike the daemon: a Codex that died waiting never sent `SessionEnd`, and its resumed session must not offer the dead run's approval). **`PermissionRequest` writes the approval marker** |
| `copilot/*` | `Invoke-Copilot*Hook` | Nothing. Copilot's sessions are read from its own folder; the event is passed on to subscribers |

The files and their shapes are agent-ha-bridge's:

* `agent-bridge-claude\<id>.json`: `SessionId`, `ProcessId`, `TranscriptPath`,
  `WorkingDirectory`, `Updated`, `HookStatus`, `HookStatusAt`;
* `agent-bridge-codex\<id>.json`: `SessionId`, `ProcessId`, `TranscriptPath`,
  `WorkingDirectory`, `Model`, `Status`, `Activity`, `Ended`, `Updated`;
* `agent-bridge-codex\<id>.approval.json`: `SessionId`, `DecisionId`, `Question`, `Created`.

Three rules carry over from agent-ha-bridge:

* A later event without a pid or status keeps the one already recorded.
* Each file is written whole and renamed into place.
* The `DecisionId` is `agent_bridge_<node>-<unix ms>`, as agent-ha-bridge names its decisions.
  The milliseconds never repeat: two approvals handled in one millisecond get consecutive
  ones, so an answer made for the first can never pass the check for the second.

Two differ. Times in these files (`Updated`, `HookStatusAt`, a marker's `Created`) are when the
hook ran (the spooled `receivedAt`), not when the event was handled. The daemon used the time it
handled it, which was almost always the same. Here an event can wait in the spool while the host
is down, and must not then read as newer than what the transcript said since; a recycled pid
must not make an old Claude session look alive. And an event older than its session's
registration's `Updated` is not applied at all: it can only be a replay (a file whose delete
failed, handled again after a restart) or a late arrival, and a replayed `PermissionRequest`
would arm an approval its `PreToolUse` has already settled. One that nothing overtook arms
nothing either: it is from before the restart, and its marker has been withdrawn (below), so an
answer already tried on it is never typed again under a new `DecisionId`.

Discovery (`RegistrationDiscovery`) and the answer path (`ApprovalMarkerRead`,
`AnswerService`) read these unchanged. On a rollback, agent-ha-bridge's daemon reads them too.

**The Codex approval marker.** agent-ha-bridge wrote the marker only after it reached Home
Assistant, because only its card could answer an approval. Here a client of this host
answers it, so the marker is written for every approval Codex asks for. A Codex approval
on a migrated machine can then be answered from the phone and the app (`AnswerService`).

**Approvals across an outage.** While nobody takes events, a Codex hook gives its fixed reply
and its event goes nowhere, so the `PreToolUse` or `Stop` that ended an approval answered at the
terminal may never arrive. The hook log records such runs, but only as best it can: a hook that
could not spool (a full disk, say) may not have been able to log either. So whenever the intake
takes the spool again after a gap (it starts, agent-ha-bridge stops, the heartbeat has gone
older than the 60 s hooks allow, renewed or not, or a Codex event, or one it could not read,
failed, from its first failure rather than only once dropped after its retries), it withdraws **every** approval marker asked before that moment,
and a `PermissionRequest` still waiting in the spool from before it is handled but arms no
approval. Nothing proves such an approval unanswered, so it is answered at the terminal.

The hook does not rely on the log alone. A Codex run it could not spool (no fresh heartbeat, a
failed write, `--fallback`) deletes that session's marker itself before replying, as
agent-ha-bridge's PowerShell hook removed it whenever an event did not reach the daemon; a
delete needs no free space. And a full disk is a gap of its own: the heartbeat's few bytes fit in
the file's own MFT record, so it can stay fresh while events, and the log lines that would say
they were lost, fail. Before each heartbeat the intake allocates (and gives back) a realistic
event's worth of space (1 MiB) in the runtime folder. While that fails it removes its own
heartbeat, so hooks give their fixed reply at once, withdraws every approval, and answers none.
A pass whose heartbeat could not be written never counts approvals as settled.

The wall clock moving back (a correction, a restored VM) is a gap too: an event timed by it can
look older than the registration it should update and be passed over as a replay, leaving the
approval it settled answerable. The intake withdraws every approval, whatever its time, when it
reads a time behind the last it read, however little, and a marker dated more than 5 s ahead of
now is withdrawn by any sweep (one written before the clock moved back, even across a restart).
A `PermissionRequest` timed more than 5 s ahead of now (one that waited in the spool across the
change) arms nothing, since its marker's time would be clamped to now and hide it from the sweep.
An event that would retire an approval (`SessionStart`, `PreToolUse`, `UserPromptSubmit`,
`Stop`, `SessionEnd`) withdraws its session's marker even when it looks older than the
registration and is otherwise not applied.

A registration dated more than 5 s ahead of now was written before the clock moved back. An event
timed by the clock as it is now is applied to it, not held back as a replay until the clock
catches up; one timed by the clock before the change (more than 5 s ahead, or older than the
registration as before) is still compared, and passed over.

While the intake runs, the log is followed every pass (only what it gained), and a Codex hook
that fails to spool while the heartbeat is fresh withdraws every approval asked before it. Only
one previous log is kept: the intake knows a rotation it followed (the log it was reading is the
one now kept as `.1`) from one it missed, and after a missed one treats everything before the
oldest line kept the same way. While the log cannot be read, no approval is offered at all.

A marker that cannot be deleted (a reader holding it without sharing its deletion), whether a
cut-off or the session's next event retired it, is remembered and tried again every pass, apart
from the event, which may be dropped after its retries; each retry asks first whether
agent-ha-bridge has started. A new `PermissionRequest` first withdraws its session's previous
marker, and arms nothing if that cannot be done yet: writing over a marker a reader holds fails,
and the old one would be answerable for the new prompt. A cut-off counts as settled only once
every marker it covers is gone, and the highest asked for is kept until then, so a later pass
with a lower one (the log readable again, say) never lets an unfinished sweep be forgotten. The log is always read through a handle that lets hooks keep
appending (`hooks status` too), and markers through one that lets a starting daemon replace or
delete them.

Until the markers are settled (after a gap, while the log cannot be read, after a later unheard
run, or while a withdrawal is pending), the answer path refuses Codex approvals
(`HookIntake.ApprovalsUnsettled`), so an answer cannot reach a marker that may already have been
answered. A migrated profile whose `intake.json` cannot be read runs no intake, and refuses them
all (`HookIntake.AnswerGate`). And on a migrated profile only markers in the runtime the intake
took are answerable: discovery still reads the other registration roots (the default runtime,
the legacy temp folder), but nothing settles or withdraws a marker there.

Answering an approval twice could type into whatever the terminal shows now; losing a remote
answer only means answering at the terminal.

**The owning process** is found from the recorded ancestry, so the shells in between can have
exited:

* **Claude:** the nearest ancestor that is a running `claude` (`Find-BridgeAgentAncestor`).
* **Codex:** as `Get-CodexOwningProcessId`. Newer Codex runs hooks under a shared
  `codex app-server`, which has no console to type into, so the walk goes past it to the
  window above. The app-server is recognised as discovery recognises it
  (`CommandLine.IsCodexAppServer`), by its subcommand, so a window whose prompt mentions
  "app-server" is still a window. That window is taken only if no other live session has claimed it. Failing
  that, the owner is:
  1. the window this session recorded, while it still runs, is still that process and no other
     live session has claimed it;
  2. then the newest unclaimed window;
  3. then the app-server.

  A registration written more than a second before the process under its pid started names an
  earlier process whose pid was reused: it neither binds that window to its session nor claims
  it from another (discovery's own rule).

When no owner is found (no ancestry, Claude or Codex), the registration keeps the pid it already
records only while that pid is still a running agent of that kind and the same process by that
rule. Otherwise it records 0: keeping it would refresh `Updated` and so bind the session to
whatever reused the pid, and answers would be typed into it.

## Notices for the rest of the host

`HookIntake.Subscribe()` returns a channel of `HookNotice`, one per event handled, in order:

* the agent, the hook and `hook_event_name`;
* the session;
* the owning pid it found;
* the status it recorded;
* for a Codex approval, the `ApprovalId` a client answers with;
* the event as it arrived, with its ancestry and when it was received.

This is how the Home Assistant connector (#60, `docs/ha-connector.md`) takes over what
agent-ha-bridge's hooks published on demand. Examples are a Claude notification's waiting
status and message, or a Codex approval to arm on the decision selector. The hook path itself
never makes a network call. The subscriber decides what reaches Home Assistant, under the
connector's own guards. Discovery needs no notice: it watches the registration folders.

## Status

`agent-bridge-host hooks status [--profile <name>]` is read-only and reports four things:

* whether this profile has taken over the spool, and for which installation;
* the heartbeat: its age, whose it is, and so whether hooks are spooling;
* how many events are waiting (only when migrated);
* the last 24 hours of the hook log, as `Get-BridgeHookStats`: runs, how many were not
  spooled and why (numbers folded: `daemon heartbeat Ns old`), and the median and p95 ms.

## Not yet

* **macOS ancestry, and intake off Windows**: #48 and the Mac's migration.
* **Hook registration for a fresh install** with no agent-ha-bridge (#54). The program and the
  contract are ready for it.
* **A Claude `AskUserQuestion` waiting at the terminal.** Claude writes its `tool_use` to the
  transcript only once it is answered (#59), so the host cannot show the question from the
  transcript. The `claude/ask` notice carries it the moment it is asked, for the Claude/Codex
  live slice to surface.
