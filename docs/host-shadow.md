# Shadow comparison with agent-ha-bridge

Before a machine leaves agent-ha-bridge's PowerShell daemon, this host runs beside it,
read-only, and a diff of what each of them shows has to be clean for a week (design §6.4,
"Rewrite method: trace replay plus shadow mode", step 4; issue #40). `agent-bridge-host
shadow` takes that diff. It never injects, launches, writes Home Assistant, or touches the
daemon's spool, and it changes nothing in agent-ha-bridge. The only network requests it makes
are reads of this machine's usage sensors and resume list in Home Assistant, approved by the
owner (see "Home Assistant reads" below).

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
read but `config.json`, and only its Home Assistant address and token (next section); never
the spool, and no folder is listed. A marker caught half written (the hook does not write it by renaming)
leaves that question uncompared for one sample rather than counting as "no question". A
stale heartbeat means the daemon is down, and nothing else is compared until it is back.

**The daemon's usage and resume list**, which it keeps only in Home Assistant, from
Home Assistant (next section).

**The host**, by default through the running host's own pipe for the profile
([local-pipe.md](protocol/local-pipe.md)): `hello`, `host.status` and `sessions.list`, a
fresh connection each sample, and `usage.get` and `launch.options` at most once a minute.
That is exactly what the user's devices are shown. `--in-process` scans in the shadow
process instead, with the same read-only core and the same agent-ha-bridge probe, for a
profile no host is serving; usage and the resume list are then not compared.

### Home Assistant reads

agent-ha-bridge publishes each agent's usage and its resume list to Home Assistant and keeps
them nowhere else. The owner approved reading those, and only those, with agent-ha-bridge's
own token. Enforced in code (`Shadow/HaReader.cs`), and tested:

* **GET only, of an exact list.** Every request passes through `ReadOnlyHomeAssistantHandler`,
  which refuses, before anything is sent, any method but GET, any body, any query, and any
  path but `/api/states/<entity>` for one of four entities named from this machine's slug
  (`HaContract.MachineSlug`): `sensor.agent_bridge_<slug>_usage_copilot`, `_usage_claude`,
  `_usage_codex`, and `select.agent_bridge_<slug>_new_resume`. Never a POST, a service call,
  a WebSocket or MQTT. Redirects are not followed.
* **The token.** `homeAssistant.baseUrl` and `homeAssistant.token` are read from
  `~/.agent-ha-bridge/config.json` at the moment of each read (so a rotated token is the one
  used), sent in that request's `Authorization` header, and dropped. Never logged, written
  to a report, put in an exception or stored. Nothing else in `config.json` is read.
* **At most once a minute**, the last reading reused between: the daemon refreshes usage
  every two minutes and its resume list every three.
* **Fail closed.** Home Assistant unreachable, timing out, refusing the token, answering
  with an error, or a config with no token: usage and the resume list are **not compared**
  for that sample, with the reason counted in the day's `notComparedWhy`. Never a mismatch.
* `--no-home-assistant` turns the reads off; usage and the resume list are then not compared.

## Usage

```powershell
agent-bridge-host shadow run [--interval 15] [--grace 60] [--in-process] [--no-home-assistant]
agent-bridge-host shadow once [--in-process] [--no-home-assistant] [--json]
agent-bridge-host shadow summary [--days 7] [--json]
#   all take [--profile <name>] (default "default") and [--agent-ha-bridge-home <dir>]
```

* `run` samples both every `--interval` seconds until stopped, and writes the day's report
  after each sample. One per profile (`shadow/shadow.lock`); it logs to `shadow/shadow.log`
  as well as the console: each new mismatch as it happens, and an hourly count. It refuses
  to start where agent-ha-bridge is not installed, or before the profile exists (run
  `serve` once).
* `once` takes one sample and prints every difference it sees at that moment, and what it
  could not compare, writing nothing. Exit code 1 when there are differences. For checking
  the setup; a difference in one sample is not yet a mismatch.
* `summary` reads the last `--days` daily reports (today included) and prints, per day,
  how long both sides were compared, how many mismatches there were and the verdict, then
  the mismatches by kind across all of them, the timing skew by kind, in how many compared
  samples usage and the resume list were compared too (and the commonest reasons they were
  not), what is never compared, and how many days were clean, and of those how many were
  compared for at least 20 hours. `--json` gives the same as data.

## Running the week

On the machine being cut over (DSWETT-DEV-VM1 first), as the user agent-ha-bridge runs for,
from a checkout of the commit to be compared, in PowerShell 7, not elevated, and from an ordinary terminal: inside an agent session agent-bridge-host refuses to change its own config (#44), and the install stops saying so:

```powershell
pwsh build/shadow-week.ps1 -Install -WhatIf   # what it would do
pwsh build/shadow-week.ps1 -Install
```

`-Install`:

1. Stops a previous install's tasks and processes (only those running from its own folder),
   then publishes `agent-bridge-host` and `agent-bridge-hostw` from the checkout,
   self-contained, into `%LOCALAPPDATA%\AgentBridgeShadow\bin`. Not the app's folder, and
   nothing on `PATH`.
2. Makes the profile `shadow` approve exactly agent-ha-bridge's workspaces
   (`newSession.workspaces`, `~` expanded, labelled as `Get-BridgeWorkspaceChoices` labels them, a repeated label getting its path after it, with `isolate`): whatever it
   approved before is removed with `workspace remove`, then each is added with
   `workspace add`. `newSession.resumeCount` is copied to `launch.resumeCount` (unset when
   absent). The host offers to resume only sessions in folders it approves, so without this
   the resume lists cannot agree; any step failing stops the install. A folder that no
   longer exists is skipped with a warning. Only those two keys are read from
   agent-ha-bridge's config. Re-run `-Install` after changing agent-ha-bridge's workspaces.
3. Registers two per-user logon tasks, made like the app's (`LogonTaskXml`): the user's
   interactive token, `LeastPrivilege`, hidden, restarted every minute on failure (three
   times), no time limit, not stopped on battery, one instance:
   * `AgentBridgeShadowHost_shadow_<user hash>`: `agent-bridge-hostw serve --profile shadow --local-only`.
   * `AgentBridgeShadowRun_shadow_<user hash>`: `agent-bridge-hostw shadow run --profile shadow
     --interval 15 --grace 60`, a minute after logon so the host is serving by then.

   Never named `AgentBridgeDaemon*` (agent-ha-bridge's, which the G5 probe looks for) or
   `AgentBridgeHost_*` (the app's).
4. Starts both.

**Read-only by construction.** While agent-ha-bridge runs, the host withdraws answering,
replying, stopping and launching (G5), and `machine.g5` reports it if it ever does not.
`--local-only` keeps it off the relay. A new profile has no Home Assistant token of its
own, so the host's Home Assistant connector is off (its log says so) and it publishes
nothing over the daemon's entities. `shadow run` writes only its reports.

**Logoff and reboot.** Both tasks start at every logon of that user, so they come back
after a reboot or a logoff with no one touching them. While the user is logged off, both
are stopped, as are agent-ha-bridge's daemon (also a logon task) and the sessions it would
show, so there is nothing to compare; the day's `comparedSeconds` shows the gap. A
disconnected remote desktop session is not a logoff: everything keeps running.

**Checking on it:**

```powershell
pwsh build/shadow-week.ps1 -Status            # both tasks' state and last result, then the summary
& "$env:LOCALAPPDATA\AgentBridgeShadow\bin\agent-bridge-host.exe" shadow summary --profile shadow
Get-Content "$env:LOCALAPPDATA\AgentBridge\shadow\shadow\shadow.log" -Tail 20
```

**Afterwards:** `pwsh build/shadow-week.ps1 -Uninstall` stops and removes both tasks. The
reports, the `shadow` profile and the published folder stay until deleted by hand.

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

And, from Home Assistant on the daemon's side and the pipe on the host's, at most once a
minute:

| Kind | Disagreement |
|---|---|
| `usage.missingOnHost`, `usage.missingOnDaemon` | One side has a reading for an agent (Copilot, Claude, Codex) and the other none. The report names the agent in place of a session |
| `usage.account` | The account the reading is for differs (compared case-insensitively, never written) |
| `usage.windows` | The metered windows differ (by key: `monthly`, `five_hour`, …) |
| `usage.percent` | A window's percent used differs by more than 5 points. Judged only on a pair of fresh readings, taken within 3 minutes of each other: the host reads every 15 minutes while the daemon runs and the daemon every 2, so this is now and then. `run` counts it once two such pairs in a row disagree, the second with both sides having read again since the first, and keeps it until a fresh pair agrees, so a lasting difference outlasts the grace and a rise one side has not read yet never counts. `once` reports a single fresh pair |
| `resumable.count` | The resume lists are of different lengths |
| `resumable.missingOnHost`, `resumable.missingOnDaemon` | How many of one side's sessions the other does not offer, by label ("summary - folder", as both write it, whitespace collapsed). Each side cuts its list to its own resume count, so when the lengths differ only the shorter list's sessions are looked for in the longer one |

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
* **No approved workspace.** A host that approves none offers nothing to resume, by
  design; the resume list is then not compared, with that reason, rather than reported as
  every session missing. `build/shadow-week.ps1` approves agent-ha-bridge's.
* **Other machines' sessions** (§11, transfer in flight). The daemon appends sessions its
  peers share to its resume list, labelled `<agent> on <machine>: …`
  (`Get-DaemonRemoteResumeLabel`). This host does not offer them yet, so they are left out
  of the daemon's list before comparing.

### Known daemon bugs

The first live runs, on DSWETT-DEV-VM1 against agent-ha-bridge 1.33.32, turned up five
places where the daemon is wrong and the host right, each checked against the transcript
and, for shells, against the processes actually running under the session's CLI. Each is
filed against agent-ha-bridge and left there. The comparison still reports them, so a
mismatch of these kinds on the pattern below is explained rather than a host bug.

| Kind | What the daemon does | Filed |
|---|---|---|
| `background.agents`, daemon lower | Drops a background agent at its first `subagent.completed`. An agent the session wakes again with `write_agent` runs more turns and is never counted again, so the daemon shows fewer agents than are running | agent-ha-bridge#258 |
| `background.shells` (or `background.agents`), daemon higher, lasting until the session ends | Its tail reader skips the start of a burst over `MaxTailBytes` (512,000 bytes) between passes. A shell's or agent's end in the skipped part is lost and the daemon keeps waiting on it. Compactions are counted over every byte, so they are not affected | agent-ha-bridge#259 |
| `model`, flipping | Sets the session's model from every `assistant.message`, subagents' included, so a session whose agents run another model shows theirs whenever one speaks last. parity.md §1: a subagent's model is not the session's | agent-ha-bridge#260 |
| `background.shells`, daemon higher, in a session with background agents | Counts a background agent's shells as the session's. Their ends go to the agent and often never reach the session's transcript: in the session that found it, nine shells agents had moved to the background had no process left and no recorded end, and stayed counted. The host leaves an agent's shells to the agent, which is counted while it runs | agent-ha-bridge#261 |
| `background.shells`, daemon higher, a session reading `shell` while idle | Takes an "is still running" marker anywhere at a line's start in a tool result. A `view` of a file quoting one invented a shell nothing will end. The host takes it only as a result's last line, where Copilot writes it | agent-ha-bridge#262 |

### Fixed in the host

The same runs found three host bugs, all fixed:

* **No model on a busy session** (`TailTests`). The host read a session's model only from
  the last 4 MiB of the transcript, so a session whose background agents filled that tail
  showed no model where the daemon showed the right one. It now also comes from the
  whole-transcript skim (`session.start`, `session.resume` and the session's own
  `session.model_change`). Its own background shells had the same limit, a latent gap that
  had not yet shown (a shell started hours ago in a busy session would have been missed),
  and are now taken from the skim as well, with `session.shutdown` ending them.
* **Every resume label named its agent** (`LaunchFlowTests`). The host put `Agency: ` on
  every label once any session it found was of another kind, a Claude session in a folder
  nobody approved included. agent-ha-bridge decides over what it could offer: the newest
  200 sessions in approved folders. The host now does the same.
* **Sessions below an approved folder were resumable** (`LaunchFlowTests`). The host
  offered any session whose folder was at or under an approved workspace, so approving
  `~\repos` offered every repository's sessions. agent-ha-bridge offers only a session that
  ran in an approved folder itself, or in a worktree the bridge made for an isolated one
  (`Test-BridgeWorkspacePathApproved`); the host now does the same, agent-ha-bridge's worktrees
  included (host-config.md).

### Not compared

| What | Why |
|---|---|
| Usage of extra Copilot accounts | Published under other entity names; only the three `usage_<agent>` sensors are read |
| Titles | agent-ha-bridge names sessions for Home Assistant ("Copilot: …") |
| Reasoning and tool history | Bounded differently and shown differently (§1) |
| Driver, the purple edge | Missing here (#46) |
| Notifications | Not state |

Every report lists these under `notCompared`, so a clean week is never read as covering them.

## Grace and timing skew

The daemon publishes on its own loop and the host within a second, so for a moment after
every change the two differ by design. A disagreement becomes a **mismatch** only once it
has lasted `--grace` seconds (default 60) across consecutive samples. One that ends sooner
is **timing skew**: counted by kind, with the longest, and not a mismatch. Some kinds get
longer whatever `--grace` says: `machine.*` at least 180 seconds, the three missed beats
agent-ha-bridge itself allows before calling a machine offline, because both sides restart
for updates; `usage.*` and `resumable.*` at least 300 seconds, since the daemon refreshes
them on its own two- and three-minute pace and each side asks the vendors separately.

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
  "samples": 5760, "compared": 5752, "comparedSeconds": 86280,
  "daemonVersion": "1.33.32", "hostVersion": "0.1.0", "hostDev": true,
  "daemonSessionsMax": 5, "hostSessionsMax": 5, "sessions": ["s8a666b6daa65", "…"],
  "mismatches": [
    { "kind": "status", "session": "s9fc81a924c6a", "first": "…", "last": "…",
      "seconds": 75, "samples": 6, "open": false, "details": ["daemon=working host=idle"] }
  ],
  "overflow": {},
  "transient": { "activity.response": { "count": 40, "longestSeconds": 15 } },
  "usageCompared": 5700, "resumableCompared": 5700,
  "notComparedWhy": { "usage: Home Assistant is unreachable": 52 },
  "notCompared": ["titles (…)", "…"],
  "verdict": "mismatches"
}
```

* `verdict` is `clean` (compared, no mismatch), `mismatches`, or `no data` (never compared).
  How much of the day was compared is `comparedSeconds`: each compared sample adds the
  interval it was taken at, so a run restarted with another `--interval` does not rescale
  the rest of the day. `intervalSeconds` and `graceSeconds` are the latest run's.
* `open` is a mismatch still going when the file was last written: at the end of the day
  (the next day's file carries it on with the same `first`, and `summary` counts it once)
  or when `run` stopped. One that began before midnight but lasted its grace only after it
  is added to the day it began as well, so that day does not read clean.
* At most 1,000 mismatches are listed per day; any beyond are counted in `overflow` by
  kind, never dropped.
* `usageCompared` and `resumableCompared` are how many of the compared samples compared
  those too; `notComparedWhy` counts the reasons the rest did not, from a fixed vocabulary.

**No content.** A report holds kinds, hashed session ids, counts, statuses, agent kinds,
model names, usage window keys and percentages, and times. Never a prompt, a response, a
question, an option, a field's name, a raw session id, an account, a resumable session's
label, a Home Assistant address or a token: text is compared as hashes or in memory and
only whether it matched is written. A value written as-is that is not a short plain token
reads `(other)`.

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

Seven consecutive days whose verdict is `clean`, each compared for at least 20 hours (the
second count on `summary`'s last line), or with every mismatch explained (an intentional
difference to add above) or filed and fixed.
Timing skew is expected and is not a mismatch, but a kind whose longest skew keeps
approaching the grace deserves a look.

## Not done here

The issue's other two parts are separate. The **recorder** inside agent-ha-bridge is not
needed for this diff: the daemon's own state file, heartbeat and question markers, and what
it already publishes to Home Assistant, carry what is compared, so agent-ha-bridge is
unchanged (no G10 release). The **replay harness**,
which turns recorded traces into xUnit cases named for agent-ha-bridge's scenarios, still
needs recorded traces and remains open in #40.
