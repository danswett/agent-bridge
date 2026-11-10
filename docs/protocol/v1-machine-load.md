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
then sends `load.update` with the whole record each time a sample is taken for an event (a session
typed into, launched, or a process change, per owner rule #1511). It never sends more than one
update per second. Nothing waits, refuses or recovers because an interval elapsed. A background
sampler is measurement only: it keeps a short in-memory ring so an event's record can show the
minutes before it, and a last-resort timer is a documented alarm reported as a fault, never a
trigger. Both calls are for a client admitted on the relay channel only (not the local pipe); a
connection may hold one subscription, and `load.unsubscribe` `{}` ends it. `seq` is the channel's.

### MachineLoad

| Field | Type | Notes |
|---|---|---|
| `sampledAt` | time | When the snapshot was taken |
| `ageMs` | number | Host clock age when sent, so a client need not trust the two clocks to agree |
| `state` | `"fresh"` \| `"partial"` \| `"stale"` \| `"unavailable"` | `partial`: some fields or sessions could not be read. `stale`: the figures are older than the host's own stale limit (`staleAfterMs`, in the record, so a client never guesses). `unavailable`: the sampler is not running or the OS gives nothing. Unknown is never sent as `0` |
| `staleAfterMs` | number | How old `sampledAt` may be before the host calls it `stale` |
| `cpu` | `{ percent, cores }`? | `percent` 0 to 100, **of the whole machine**: the counter delta over the interval divided by (cores x interval). `cores` are logical cores. Absent on the first sample or after a counter reset |
| `memory` | `{ totalBytes, availableBytes, committedPercent }`? | `availableBytes` is what the OS says can be given out now. This, not the session sum, is headroom. `committedPercent` is committed memory, 0 to 100 |
| `topProcesses` | `{ name, cores }[]`? | Present only while machine `cpu.percent` is above 85: at most five, by process name and cores used. Names and numbers only |
| `sessions` | SessionLoad[] | One per discovered session, at most 200. Over 200 the host sends the 200 with the most working set and sets `state` to `partial`. Anything published beyond the channel is machine totals only: the Compliance clearance (#1150) covers totals, so `sessions` stays on the channel |
| `unattributed` | `{ processes, workingSetBytes }`? | Processes the host started under a session root that no session now owns (for example children of an exited root) |

Disk is not in this record: SRE ruled it out of this slice. A later contract adds it.

### SessionLoad

| Field | Type | Notes |
|---|---|---|
| `session` | string | The session id as in [v1-sessions.md](v1-sessions.md), the existing identity; no new key |
| `agent` | string | The agent kind, as the launcher names it (`agency` or `direct` for Copilot) |
| `cliVersion` | string? | The agent CLI's version |
| `processNames` | `{ name, count }[]` | Process counts by name (for example `agency.exe` x2); never paths, arguments or prompts |
| `processes` | number | Processes in the whole tree, helpers and descendants included |
| `cpuPercent` | number? | Of the whole machine, same normalisation. Absent on a first sample |
| `workingSetBytes` | number? | The **sum of working sets** of the tree. It double-counts shared pages, so it is labelled so and is never a headroom figure |
| `handles` | number? | Windows handles, or open descriptors on macOS and Linux |
| `handleKind` | `"handles"` \| `"fds"`? | Says which `handles` is. Present with `handles` |
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
   first; equal start times go to the lower session id.
7. CPU is a counter delta between two snapshots. A counter that went backwards, a start-time
   change, or the first sample omits the figure for that tick.
8. Sampling runs at low priority, can be cancelled, keeps at most one previous snapshot, and a
   sample still running when the next event arrives is not queued: the event reuses the last one.

### Limits and refusals

A record is at most 64 KiB as sent. A client that sees `state: "stale"` or `unavailable` shows
"no figure", not zero. The host offers `load` only when Compliance has cleared it: a new host setting `load.enabled` (boolean, default `false`, beside `launch.transfer.serve`; the builder adds it to host-config.md with the first slice).

## 2. Session move (#1146, fleet #22)

v1 is **handoff, then fork and resume on another machine**. Only an **ended** session may be
forked: idle, paused and working sessions are refused (`session-running`), and a live move is a
later phase. It extends
[v1-transfer.md](v1-transfer.md): same signed `session.transfer`, same sealed blob, same new
session id. The original is left untouched (ui.md 6.6.2 rule 3 says it "stops taking answers": Creative's
amend slice below must drop that); a move that the person wants as a move ends the original
afterwards, which the app asks and the host does not decide.

### What is signed

The target is an untrusted carrier, so the source acts only on the signed command body
([v1-transfer.md](v1-transfer.md)). This note adds two optional fields to that body, after
`to`, in this order: `moveId` (the manifest's) and `withWorkspace` (`true` only when the person
asked). The person's device signs them. The source adds the workspace part only when the verified
body says `withWorkspace: true`, and takes `moveId` for the manifest and the telemetry from the
same body. A body without them is today's command and gets no workspace part. They count toward
the 2048-byte body bound.

### Handoff package

The blob stays a zip. It gains a first entry `handoff.json` (a [HandoffManifest](#handoffmanifest))
and, optionally, a second part. The installer's entry allow-list in v1-transfer.md gains
`handoff.json` and `workspace.patch` and nothing else; P2 changes it. An older target refuses
the unknown entries and the move is `refused`, never half-applied.

1. **Session files (always, as today):** the agent's own session folder or transcript. No
   credentials, ever.
2. **Workspace changes (optional, second):** `workspace.patch` only; its description is the
   manifest's `workspaceChanges`, there is no separate `workspace.json`. Only when the signed
   command says `withWorkspace: true` and the workspace is a git repository.

### HandoffManifest

| Field | Type | Notes |
|---|---|---|
| `version` | `1` | |
| `moveId` | string | 16 random bytes, hex. Ties the steps in [§ Telemetry](#telemetry) together |
| `agent` | string | The session's kind, as the launcher names it |
| `model` | string? | As the agent reports it; the target may not offer it and then says so (`refused`) |
| `session` | string | The source session's id |
| `workspace` | string? | The workspace *name* (never the path) |
| `files`, `bytes`, `sha256` | number, number, string | Of the **session files only**, not of the zip (a zip cannot hold its own hash): the entry count, their total uncompressed bytes, and the SHA-256 (base64url, no padding) over the entries in name order, each as its name, a zero byte, then its content. `session.transfer`'s own `size` and `sha256` still describe the sealed zip |
| `workspaceChanges` | object? | Present only with the second part: `{ branch, baseCommit, patchBytes, untracked, skipped, truncated }`. `branch` and `baseCommit` are names and hashes; `untracked` and `skipped` count files, and an untracked file's body is in the patch as an added file. A count that is not known is absent, never `0`. `baseCommit` is the commit the diff is against, even when it was never pushed (then the target cannot have it and the part is `not-applied`). Symlinks are recorded as the link and never followed; submodules appear as their commit hash only |

### Workspace changes

* `workspace.patch` is `git diff --binary` of the tracked changes, plus untracked files that are
  not ignored, against `baseCommit`. At most **5 MiB uncompressed**; over that the part is dropped whole and
  `truncated` is `true`, never cut mid-file.
* A file the host would not put in a transcript is not put here: ignored files, `.env*`,
  `*.pem`, `*.key`, anything under `.git`, and any file the host's secret scan flags. The host
  names how many it left out in `workspaceChanges.skipped` (count only).
* The target applies it in a fresh worktree on the named branch only if it already has
  `baseCommit`; otherwise it does not apply it and the launch says `workspace: "not-applied"` with the reason
  code. If the named branch already exists on the target, the worktree uses a new branch name
  and says so in the reason; an existing branch is never overwritten. The session starts either
  way.
* Never pushed to a remote by the host.

### Outcomes

As a launch's, plus on `launched`: `transfer.workspace` is `"applied"`, `"not-applied"` or
`"none"` (one spelling everywhere). `workspace-base-missing` and `workspace-too-large` are never
refusals: they are the `reason` beside `"not-applied"`, and the session still starts.

Every refusal carries a stable `code` beside the plain words (the words stay, and the UI's words
in ui.md 6.6.2 map from the code):

| `code` | Means | ui.md word |
|---|---|---|
| `session-not-resumable` | The source cannot resume that session (no files, wrong agent) | |
| `session-running` | The session is not ended (idle, paused or working) | |
| `session-changed-while-packing` | Its files changed while the source packed them | |
| `too-large` | Over the size limit; see v1-transfer.md | |
| `busy` | Two other packs are already running | |
| `no-matching-agent` | The target has no launcher for the agent | "No agent" |
| `workspace-required` | The launch named no workspace | "No workspace here" |
| `hash-mismatch` | The blob's hash did not match | |
| `blob-expired` | The relay store dropped the blob | |
| `target-full` | Afterwards only: the target is at its session limit | "Can't take more" |
| `timeout` | A step did not finish in time | |

A blank ui.md word is Creative's to fill in the amend slice.

## Telemetry

One record per move. The **target** host is the record of truth: it writes steps 1, 4 and 5,
and the source returns steps 2 and 3 (their `at` and `ms` on the source's clock) in the
`session.transfer` result as an optional `steps` list, which the target merges. The source keeps
no record beyond its `host.log` line. The target keeps the merged record for 30 days and sends
it, content-free, over the relay channel to the signing person's device (`by` in the command)
and, if the owner enabled it, to the SRE collector. No other device is sent it or may ask for it.

### MoveTelemetry

| Field | Type | Notes |
|---|---|---|
| `moveId` | string | As in the manifest |
| `from`, `to` | string | Device ids (32 hex) |
| `session` | string | Source session id |
| `outcome` | `"launched"` \| `"online"` \| `"refused"` \| `"failed"` \| `"timeout"` | `online` is the last step reached |
| `reason` | string? | `refused`: the refusal `code` from the table above. `failed`: `blob-expired`, `hash-mismatch`, `session-changed-while-packing` or `launch-failed` (the target's launch did not start). `timeout`: `timeout`. Absent for `launched` and `online` |
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
`launch.options` and publishes `{ agent: { models, efforts, contexts } }`, refreshed when `launch.options` changes and at host start, not on a timer; whether HA is still the surface is for #1587.

## Owners and slices

| Slice | Owner | Needs |
|---|---|---|
| Contract (this note) | Engineering | |
| P1 sampler and `load.get` | A new builder SRE places | This note and `vectors/machine-load.json` |
| P2 manifest, workspace part, step timestamps | The same builder, after P1 | §2 |
| Placement trigger from the load feed (fleet #205) | Migration | P1 |
| Amend ui.md 6.6.2 to a copy | Creative | §2 |
| Collector for telemetry | SRE | `MoveTelemetry` |
