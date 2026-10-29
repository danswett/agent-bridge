# Shadow comparison with agent-ha-bridge

Before a machine leaves agent-ha-bridge's PowerShell daemon, this host runs beside it,
read-only, and a diff of what each of them shows has to be clean for a week (design §6.4,
"Rewrite method: trace replay plus shadow mode", step 4; issue #40). `agent-bridge-host
shadow` takes that diff. It never injects, launches, writes Home Assistant, or touches the
daemon's spool, and it changes nothing in agent-ha-bridge.

## How it sees each side

**The daemon**, from the files it already writes, under `~/.agent-ha-bridge` (an
installation with metadata, 1.33.x):

| File | Written by | Gives |
|---|---|---|
| `runtime/agent-bridge-daemon-state.json` | `Write-DaemonState`, whole, then renamed into place | Every live session it publishes: `Status`, `Kind`, `Model`, `Compactions`, `BackgroundAgents`, `BackgroundShells`, `LastResponse` |
| `runtime/agent-bridge-daemon.heartbeat` | `Set-BridgeDaemonAlive`, every pass | Whether it is running: a pass within the last minute, its own rule (`Test-BridgeDaemonAlive`) |
| `installer/VERSION` | the installer | Its version, for the report |
| `<copilot home>/session-state/<id>/agent-pending-decision.json`, else `runtime/copilot-bridge-markers/<id>/agent-pending-decision.json` | the `ask_user` and `AskUserQuestion` hooks (`Write-CopilotDecisionMarker`); deleted by the daemon once answered | The question it has armed: `fields` (or `choices`), `terminalOnly`, `toolCallId` (Claude), `armedAt` |

Each is opened for a moment, sharing read, write and delete, so the daemon's rename of its
state file and a hook rewriting a marker never meet a file this holds. Nothing else is
read: never `config.json`, which holds the Home Assistant token, never the spool, and no
folder is listed. A marker caught half written (the hook does not write it by renaming)
leaves that question uncompared for one sample rather than counting as "no question". A
stale heartbeat means the daemon is down, and nothing else is compared until it is back.

**The host**, by default through the running host's own pipe for the profile
([local-pipe.md](protocol/local-pipe.md)): `hello`, `host.status` and `sessions.list`, a
fresh connection each sample. That is exactly what the user's devices are shown.
`--in-process` scans in the shadow process instead, with the same read-only core and the
same agent-ha-bridge probe, for a profile no host is serving.

## Usage

```powershell
agent-bridge-host shadow run [--interval 15] [--grace 60] [--in-process]
agent-bridge-host shadow once [--in-process] [--json]
agent-bridge-host shadow summary [--days 7] [--json]
#   all take [--profile <name>] (default "default") and [--agent-ha-bridge-home <dir>]
```

* `run` samples both every `--interval` seconds until stopped, and writes the day's report
  after each sample. One per profile (`shadow/shadow.lock`); it logs to `shadow/shadow.log`
  as well as the console: each new mismatch as it happens, and an hourly count. It refuses
  to start where agent-ha-bridge is not installed, or before the profile exists (run
  `serve` once).
* `once` takes one sample and prints every difference it sees at that moment, writing
  nothing. Exit code 1 when there are differences. For checking the setup; a difference in
  one sample is not yet a mismatch.
* `summary` reads the last `--days` daily reports (today included) and prints, per day,
  how long both sides were compared, how many mismatches there were and the verdict, then
  the mismatches by kind across all of them, the timing skew by kind, and what is not
  compared. `--json` gives the same as data.

To run it for the week on a machine, start it at logon, for example:

```powershell
$exe = (Get-Command agent-bridge-host).Source
$action = New-ScheduledTaskAction -Execute $exe -Argument 'shadow run'
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
Register-ScheduledTask -TaskName 'AgentBridgeShadow' -Action $action -Trigger $trigger `
    -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1))
```

and remove the task afterwards (`Unregister-ScheduledTask AgentBridgeShadow`). The reports
stay where they are.

## What is compared

Per session, for every session either side lists:

| Kind | Disagreement |
|---|---|
| `session.missingOnHost`, `session.missingOnDaemon` | One side lists the session and the other does not |
| `agent` | Copilot, Claude or Codex |
| `status` | `working`, `waiting`, `idle` or `ending`, after the mappings below |
| `background.agents`, `background.shells` | How many background agents and commands it is still waiting on |
| `model` | The model in use, following model changes |
| `compactions` | How many compactions have finished |
| `activity.response` | The end (last 200 characters, whitespace collapsed) of the latest response, when both have one |
| `question.missingOnHost`, `question.missingOnDaemon` | One side shows a question and the other does not |
| `question.identity` | Not the same question: the call ids differ (Claude), or they were asked over two minutes apart (Copilot, whose marker is named for the hook's clock, not the call) |
| `question.message` | The start of a Copilot question differs (its first 64 characters, as a prefix, since agent-ha-bridge appends its own hints) |
| `question.fields`, `question.fieldType`, `question.options` | How many fields; each field's kind (choice, multi, text); how many options each has, and whether they are the same ones (by the values the CLI records) |

And for the machine:

| Kind | Disagreement |
|---|---|
| `machine.daemon` | The daemon is down (stale heartbeat) or its state cannot be read |
| `machine.host` | The host is not running for the profile, refused the shadow, or did not answer |
| `machine.g5` | The daemon is running and the host does not see it, so it would deliver answers, replies and stops beside it (design G5) |

### Known differences that are not mismatches

These are the intentional differences in [parity.md](parity.md), normalized before
comparing:

* **Delegating** (§1). The daemon's `agents` and `shell` statuses are the host's `idle` with
  background work counted beside it; the counts are compared on their own. Its `ended` is
  the host's `ending`.
* **Copilot permission prompts** (§3, beyond parity). The host offers them to approve and
  reads as `waiting`; agent-ha-bridge only mirrors them and stays `working`. Approval
  questions are not compared, and the host's `waiting` on one matches the daemon's
  `working`.
* **Long text is bounded differently** (§1). Only the end of a response is compared.
* **Forms answered at the machine** (§2). Where either side cannot drive a prompt from
  another device (agent-ha-bridge's `terminalOnly`, the host's `answerAtMachine`, or a
  question the host had to cut), each shapes it its own way, so only that it is asked, and
  which one, is compared.
* **Field shapes** (§2). A question whose only field is free text is the same as one with
  none (agent-ha-bridge asks it in its reply box). A number field is typed like text, and a
  checkbox is a choice of `true` or `false`, as `Get-DecisionSchemaFieldChoices` has it.
  "Other" and the Cancel request row are card controls, not part of the question.
* **Claude's message** is composed differently on each side (the host lists option
  descriptions under it), so only its fields are compared.

### Not compared

| What | Why |
|---|---|
| Usage | agent-ha-bridge keeps its readings only in Home Assistant entities |
| Resumable sessions | The same: only the `new_resume` select in Home Assistant |
| Titles | agent-ha-bridge names sessions for Home Assistant ("Copilot: …") |
| Reasoning and tool history | Bounded differently and shown differently (§1) |
| Driver, the purple edge | Missing here (#46) |
| Notifications | Not state |

Usage and resumable sessions could only be compared by reading what agent-ha-bridge
publishes to Home Assistant (its MQTT entities or `GET /api/states`), which needs a Home
Assistant token for reads and so the owner's approval. Until then every report lists them
under `notCompared`, so a clean week is never read as covering them.

## Grace and timing skew

The daemon publishes on its own loop and the host within a second, so for a moment after
every change the two differ by design. A disagreement becomes a **mismatch** only once it
has lasted `--grace` seconds (default 60) across consecutive samples. One that ends sooner
is **timing skew**: counted by kind, with the longest, and not a mismatch. `machine.*`
kinds get at least 180 seconds, the three missed beats agent-ha-bridge itself allows
before calling a machine offline, because both sides restart for updates.

When either side cannot be read, the session-level disagreements open at that moment end;
the gap is not counted against them.

## The reports

`%LOCALAPPDATA%\AgentBridge\<profile>\shadow\shadow-<yyyy-MM-dd>.json` (`~/.agent-bridge/<profile>/shadow/`
elsewhere), one per local day, in a folder made private to the user like the attachment
staging folder. Rewritten whole after each sample, by renaming. A file that cannot be read
when `run` starts is kept beside it as `.unreadable-<time>`, never overwritten.

```json
{
  "schema": 1, "date": "2026-11-02", "timeZone": "Pacific Standard Time",
  "graceSeconds": 60, "intervalSeconds": 15,
  "samples": 5760, "compared": 5752,
  "daemonVersion": "1.33.32", "hostVersion": "0.1.0", "hostDev": true,
  "daemonSessionsMax": 5, "hostSessionsMax": 5, "sessions": ["s8a666b6daa65", "…"],
  "mismatches": [
    { "kind": "status", "session": "s9fc81a924c6a", "first": "…", "last": "…",
      "seconds": 75, "samples": 6, "open": false, "details": ["daemon=working host=idle"] }
  ],
  "overflow": {},
  "transient": { "activity.response": { "count": 40, "longestSeconds": 15 } },
  "notCompared": ["usage (…)", "…"],
  "verdict": "mismatches"
}
```

* `verdict` is `clean` (compared, no mismatch), `mismatches`, or `no data` (never compared).
  How much of the day was compared is `compared` × `intervalSeconds`; `summary` prints it.
* `open` is a mismatch still going when the file was last written: at the end of the day
  (the next day's file carries it on with the same `first`, and `summary` counts it once)
  or when `run` stopped.
* At most 1,000 mismatches are listed per day; any beyond are counted in `overflow` by
  kind, never dropped.

**No content.** A report holds kinds, hashed session ids, counts, statuses, agent kinds,
model names and times. Never a prompt, a response, a question, an option, a field's name
or a raw session id: text is compared as hashes or in memory and only whether it matched
is written. A value written as-is that is not a short plain token reads `(other)`.

A session's hash is `s` and the first 12 hex digits of SHA-256 of `agent-bridge-shadow`, a
newline, and its id. To find which live session a hash is, at the machine:

```powershell
$sha = [Security.Cryptography.SHA256]::Create()
(agent-bridge-host sessions --compact | ConvertFrom-Json).sessions | ForEach-Object {
    $h = 's' + ([Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("agent-bridge-shadow`n$($_.id)")))).ToLower().Substring(0, 12)
    "$h $($_.id) $($_.status)"
}
```

## Judging the week

Seven consecutive days whose verdict is `clean`, each compared for most of the day, or
with every mismatch explained (an intentional difference to add above) or filed and fixed.
Timing skew is expected and is not a mismatch, but a kind whose longest skew keeps
approaching the grace deserves a look.

## Not done here

The issue's other two parts are separate. The **recorder** inside agent-ha-bridge is not
needed for this diff: the daemon's own state file, heartbeat and question markers carry
what is compared, so agent-ha-bridge is unchanged (no G10 release). The **replay harness**,
which turns recorded traces into xUnit cases named for agent-ha-bridge's scenarios, still
needs recorded traces and remains open in #40.
