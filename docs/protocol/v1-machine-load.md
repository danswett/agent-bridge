# Bridge Protocol v1: machine load and session move (contract)

Status: **contract for a builder, not yet implemented.** It settles what #1150 (host measures
session process trees and headroom) must publish, what a session move carries and records
(#1146, fleet #22), and where #1142 (real launch options) already stands. Same channel, framing
and rules as [v1-sessions.md](v1-sessions.md). Records will live in
`src/AgentBridge.Protocol/MachineLoad.cs`; the builder adds `vectors/machine-load.json` through
`ProtocolVectors.cs`, as for the other files ([vectors/README.md](vectors/README.md)).

Counts and sizes only. Nothing here carries a path, command line, PID, prompt, transcript or
credential, and a figure leaves the machine only after Compliance has cleared it (#1150).

## 1. `machine.load` (#1150)

`hello`'s `capabilities` includes `load` when the host offers `load.get` and `load.subscribe`.
`load.get` takes `{}` and answers a [MachineLoad](#machineload); `load.subscribe` answers the same,
then sends `load.update` with the whole record when a figure moved by 5% of capacity or 30 s
passed, never more than one per 5 s. `seq` is the channel's.

### MachineLoad

| Field | Type | Notes |
|---|---|---|
| `sampledAt` | time | When the snapshot was taken |
| `ageMs` | number | Host clock age when sent, so a client need not trust the two clocks to agree |
| `state` | `"ok"` \| `"partial"` \| `"stale"` \| `"unavailable"` | `partial`: some fields or sessions could not be read. `stale`: the last sample is older than three intervals. `unavailable`: the sampler is not running or the OS gives nothing. Unknown is never sent as `0` |
| `intervalMs` | number | The sampling interval, a target (5000), 1000 to 60000 |
| `cpu` | `{ percent, cores }`? | `percent` 0 to 100, **of the whole machine**: the counter delta over the interval divided by (cores x interval). `cores` are logical cores. Absent on the first sample or after a counter reset |
| `memory` | `{ totalBytes, availableBytes }`? | `availableBytes` is what the OS says can be given out now. This, not the session sum, is headroom |
| `disk` | `{ freeBytes, totalBytes }`? | The volume holding the host's workspaces |
| `sessions` | SessionLoad[] | One per discovered session, at most 200 |
| `unattributed` | `{ processes, workingSetBytes }`? | Processes under the host's builders' roots that no session owns |

### SessionLoad

| Field | Type | Notes |
|---|---|---|
| `session` | string | The session id as in [v1-sessions.md](v1-sessions.md), the existing identity; no new key |
| `processes` | number | Processes in the whole tree, helpers and descendants included |
| `cpuPercent` | number? | Of the whole machine, same normalisation. Absent on a first sample |
| `workingSetBytes` | number? | The **sum of working sets** of the tree. It double-counts shared pages, so it is labelled so and is never a headroom figure |
| `handles` | number? | Windows handles, or open descriptors on macOS and Linux; the field says which in `handleKind` (`"handles"` or `"fds"`) |
| `state` | `"ok"` \| `"partial"` \| `"unavailable"` | `partial`: a process of the tree exited or could not be read mid-sample |

Not in v1 of this record: disk growth per session. Process I/O write bytes are not retained disk
size and a recursive workspace scan per tick is not allowed, so the field is absent until a
metadata-only measure is agreed (a later change adds `workspaceBytes`, sampled at most once a
minute, with `measuredAt`). A client never infers it.

### Attribution rules

1. The root is `ISessionSource.ProcessOf`'s PID plus start time. A PID whose start time differs
   is a different process: its tree is dropped and the session reads `partial` until rebound.
2. One process snapshot per tick is shared by all sessions; no per-session scan and no second
   discovery loop.
3. A descendant belongs to the nearest registered session root above it. A nested session
   registered on its own owns its subtree; the outer session does **not** count it. A process
   counts once.
4. A root that exits mid-sample: the session reads `unavailable` for that tick, its last figures
   are not repeated. If its children outlive it they go to `unattributed` until the host rebinds.
5. An unknown parent (it exited, its PID was reused) ends the walk there. The process is
   `unattributed`, never attached to a guess.
6. Overlapping trees (two roots claiming one process): the process goes to the root that started
   first.
7. CPU is a counter delta between two snapshots. A counter that went backwards, a start-time
   change, or the first sample omits the figure for that tick.
8. Sampling runs at low priority, can be cancelled, keeps at most one previous snapshot, and a
   tick that overruns its interval is skipped, not queued.

### Limits and refusals

A record is at most 64 KiB as sent. A client that sees `state: "stale"` or `unavailable` shows
"no figure", not zero. The host offers `load` only when Compliance has cleared it
(`launch.transfer`-style setting `load.enabled`, default off).

## 2. Session move (#1146, fleet #22)

v1 is **handoff, then fork and resume on another machine**. An ended session may be forked; a
running one is a later phase. It extends
[v1-transfer.md](v1-transfer.md): same signed `session.transfer`, same sealed blob, same new
session id. The original is left untouched; a move that the person wants as a move ends the
original afterwards, which the app asks and the host does not decide.

### Handoff package

The blob stays a zip. It gains a first entry `handoff.json` (a [HandoffManifest](#handoffmanifest))
and, optionally, a second part.

1. **Session files (always, as today):** the agent's own session folder or transcript. No
   credentials, ever.
2. **Workspace changes (optional, second):** `workspace.json` and `workspace.patch`. Only when
   the person asked for it and the workspace is a git repository.

### HandoffManifest

| Field | Type | Notes |
|---|---|---|
| `version` | `1` | |
| `moveId` | string | 16 random bytes, hex. Ties the steps in [§ Telemetry](#telemetry) together |
| `agent` | string | The session's kind, as the launcher names it |
| `model` | string? | As the agent reports it; the target may not offer it and then says so (`refused`) |
| `session` | string | The source session's id |
| `workspace` | string? | The workspace *name* (never the path) |
| `files`, `bytes`, `sha256` | number, number, string | Of the session files, as `session.transfer` already states |
| `workspaceChanges` | object? | Present only with the second part: `{ branch, baseCommit, patchBytes, untracked, truncated }`. `branch` and `baseCommit` are names and hashes; `untracked` counts files, their bodies are in the patch as added files |

### Workspace changes

* `workspace.patch` is `git diff --binary` of the tracked changes, plus untracked files that are
  not ignored, against `baseCommit`. At most **5 MiB**; over that the part is dropped whole and
  `truncated` is `true`, never cut mid-file.
* A file the host would not put in a transcript is not put here: ignored files, `.env*`,
  `*.pem`, `*.key`, anything under `.git`, and any file the host's secret scan flags. The host
  names how many it left out in `skipped` (count only).
* The target applies it in a fresh worktree on the named branch only if it already has
  `baseCommit`; otherwise it does not apply it and the launch says `workspaceChanges: "not
  applied"` with the reason code. The session starts either way.
* Never pushed to a remote by the host.

### Outcomes

As a launch's, plus on `launched`: `transfer.workspace` is `"applied"`, `"not-applied"` or
`"none"`. Refusals add `workspace-base-missing` and `workspace-too-large` as reasons, not as
failures of the launch.

## Telemetry

One record per move, written by each host for the steps it sees, sent over the existing relay
channel to the machine that asked (the app or SRE collector), and kept for 30 days. Content-free.

### MoveTelemetry

| Field | Type | Notes |
|---|---|---|
| `moveId` | string | As in the manifest |
| `from`, `to` | string | Device ids (32 hex) |
| `session` | string | Source session id |
| `outcome` | `"launched"` \| `"online"` \| `"refused"` \| `"failed"` \| `"timeout"` | `online` is the last step reached |
| `reason` | string? | A launch's refusal code when not `online` |
| `steps` | Step[] | In the order below; a step not reached is absent |
| `bytes` | `{ session, workspace }` | Bytes pushed |

A **Step** is `{ name, at, ms }`: `at` the time on the host's clock, `ms` the time since the
previous step (0 for the first). The five names, in order:

| `name` | Taken by | Means |
|---|---|---|
| `handoffRequested` | target | The command to hand off was sent to the source |
| `handoffConfirmed` | source | The source accepted, and finished packing |
| `artifactsPushed` | source | The blob is in the relay store |
| `newLaunched` | target | The new session's process started |
| `newOnline` | target | The new session answered its first `sessions.list` entry |

The total is `newOnline.at` minus `handoffRequested.at`, **both from the target's clock**; the
source's `at` values are for display and its `ms` values are what the source measured. A client
does not subtract across hosts.

Targets for the first release (not claims): `handoffRequested` to `handoffConfirmed` under 30 s;
`artifactsPushed` under 2 min per 100 MiB.

## 3. #1142: launch options

Already offered: `launch.options` ([v1-launch.md](v1-launch.md#launchoptions)) returns, per
launcher the host really has, the models, efforts and contexts it accepts. The gap #1142 names is
on the Home Assistant side only: the `select` entities show the selected agent's list. This
contract changes nothing in `launch.options`. A builder who wants the HA sensor reads
`launch.options` and publishes `{ agent: { models, efforts, contexts } }`, refreshed on the
host's timer; whether HA is still the surface is for #1587.

## Owners and slices

| Slice | Owner | Needs |
|---|---|---|
| Contract (this note) | Engineering | |
| P1 sampler and `load.get` | A new builder SRE places | This note and `vectors/machine-load.json` |
| P2 manifest, workspace part, step timestamps | The same builder, after P1 | §2 |
| Placement trigger from the load feed (fleet #205) | Migration | P1 |
| Amend ui.md 6.6.2 to a copy | Creative | §2 |
| Collector for telemetry | SRE | `MoveTelemetry` |
