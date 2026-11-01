# Shadow comparison with agent-ha-bridge

Before a machine leaves agent-ha-bridge's PowerShell daemon, this host runs beside it,
read-only, and a diff of what each of them shows has to be clean (design §6.4, "Rewrite
method: trace replay plus shadow mode", step 4; issue #40). `agent-bridge-host shadow`
takes that diff. How long it runs is not fixed: it runs until it has covered enough of
each kind of event, cleanly, which `shadow summary --gate` judges (see "The gate"). It never injects, launches, writes Home Assistant, or touches the
daemon's spool, and it changes nothing in agent-ha-bridge. The only network requests it makes
are reads of this machine's usage sensors and resume list in Home Assistant, and of the usage
sensor of the machine reading an account this one stands down for, approved by the owner
(see "Home Assistant reads" below).

## How it sees each side

**The daemon**, from the files it already writes, under `~/.agent-ha-bridge` (an
installation with metadata, 1.33.x):

| File | Written by | Gives |
|---|---|---|
| `runtime/agent-bridge-daemon-state.json` | `Write-DaemonState`, whole, then renamed into place | Every live session it publishes: `Status`, `Kind`, `Model`, `Compactions`, `BackgroundAgents`, `BackgroundShells`, `LastResponse` |
| `runtime/agent-bridge-daemon.heartbeat` | `Set-BridgeDaemonAlive`, every pass | Whether it is running: a pass within the last minute, its own rule (`Test-BridgeDaemonAlive`) |
| `installer/VERSION` | the installer | Its version, for the report |
| `runtime/daemon.process.json` | `Register-BridgeRuntimeProcess`, when the daemon starts | Its start time (`startedUtcTicks`), to see it restart |
| `<copilot home>/session-state/<id>/agent-pending-decision.json`, else `runtime/copilot-bridge-markers/<id>/agent-pending-decision.json` | the `ask_user` and `AskUserQuestion` hooks (`Write-CopilotDecisionMarker`); deleted by the daemon once answered | The question it has armed: `fields` (or `choices`), `terminalOnly`, `toolCallId` (Claude), `armedAt` |

Each is opened for a moment, sharing read, write and delete, so the daemon's rename of its
state file and a hook rewriting a marker never meet a file this holds. Nothing else is
read but `config.json`, and only its Home Assistant address and token (next section); never
the spool, and no folder is listed. A marker caught half written (the hook does not write it by renaming)
leaves that question uncompared for one sample rather than counting as "no question". A
stale heartbeat means the daemon is down, and nothing else is compared until it is back.

**The daemon's usage and resume list**, which it keeps only in Home Assistant, from
Home Assistant (next section).

**A Copilot session's transcript** (`<copilot home>/session-state/<id>/events.jsonl`), only
for a session with a difference in background work or model, to check it against the
daemon's known bugs ("Known daemon bugs" below). Read once from the start, then only what is
appended, with the same sharing as the daemon's files; what is kept is ids, byte offsets and
model names, in memory.

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
  path but `/api/states/<entity>` for one of seven entities named from this machine's slug
  (`HaContract.MachineSlug`): `sensor.agent_bridge_<slug>_usage_copilot`, `_usage_claude`,
  `_usage_codex`, the further Copilot logins' `_usage_copilot_2` to `_4`, and
  `select.agent_bridge_<slug>_new_resume`. Never a POST, a service call,
  a WebSocket or MQTT. Redirects are not followed.
* **The account's reader.** agent-ha-bridge asks the vendor about an account on one machine
  at a time. While that machine's readings are current, the others stand down
  (`Test-BridgeUsageCovered`) and their own sensors keep the last figure they read, hours
  old. For a sensor whose figure here has lapsed (older than the daemon's own pace,
  `DaemonUsagePace.Lapsed`), the daemon's reading is the account reader's. That is a usage
  sensor of a machine other than this one whose current reading is:
  * of the same agent (`client`);
  * by that machine (`machine_slug`);
  * for the same account (`account_id`, else `account`, as `Test-BridgeUsageCovered` matches
    them; a Copilot login in any case).

  A further Copilot login (`usage_copilot_2` to `_4`) may be on any of that machine's Copilot
  sensors, so each is looked at. The latest reading wins. A reading stamped more than a poll
  ahead of now is no current reader. Its figure is not taken as confirmed beyond its
  measurement, since that machine's pace is its own, but it holds across reads here
  (`HeldSince`).

  The machines looked at are only those the daemon itself names, at most eight:
  * the machines in its resume list's "<agent> on <machine>: …" options (already read);
  * its `dashboard.publication.writer`;
  * the reader found last.

  Each sensor is read by its one exact entity, `sensor.agent_bridge_<their slug>_usage_<key>`,
  once a pass, and the handler allows it for that one request only. Never a listing of states
  or a pattern. A sensor that cannot be read is skipped. With no current reading found, the
  lapsed figure stands and usage is **not compared** (see "Not compared").

  The same goes for a place where this machine's daemon has **no** reading and the host has
  one, matched on the host's account: typically a further Copilot login the host is given
  (`usage.copilotAccounts`) and this machine's agent-ha-bridge is not, because another
  machine reads it. Found as `usage.missingOnDaemon` on `copilot_2` for hours on DEV-VM1
  (2026-10-30), while DSWETT-HOME, the daemon's dashboard writer, read that login. With no
  named machine reading it, the daemon has no reading, and that is still a mismatch.
  If the only reader of that login has itself stopped reading (its figure lapsed), its
  figure is what the daemon's machines show, and it is **not compared**, as a lapsed figure of
  this daemon's own is not. Taking it as no reading at all counted a mismatch on DEV-VM1 on
  2026-10-30, 22:40 PT, while DSWETT-HOME went 35 minutes without reading the login.
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
agent-bridge-host shadow summary [--days 7] [--gate] [--json]
#   all take [--profile <name>] (default "default") and [--agent-ha-bridge-home <dir>]
```

* `run` samples both every `--interval` seconds until stopped, and writes the day's report
  after each sample. One per profile (`shadow/shadow.lock`); it logs to `shadow/shadow.log`
  as well as the console: each new mismatch as it happens, and an hourly count. It refuses
  to start where agent-ha-bridge is not installed, or before the profile exists (run
  `serve` once).
* `once` takes one sample and prints every difference it sees at that moment, and what it
  could not compare, writing nothing, with the cause of any known difference. Exit code 1 when there are differences without a known cause. For checking
  the setup; a difference in one sample is not yet a mismatch.
* `summary` reads the last `--days` daily reports (today included) and prints, per day,
  how long both sides were compared, how many mismatches there were and the verdict, then
  the mismatches by kind across all of them, the timing skew by kind, in how many compared
  samples usage and the resume list were compared too (and the commonest reasons they were
  not), what is never compared, and how many days were clean, and of those how many were
  compared for at least 20 hours. Then the known differences, by kind and cause, and the
  coverage (below). `--json` gives the same as data.
* `summary --gate` reads every report there is (up to 92 days; `--days` narrows it) and ends
  with the gate's verdict: `PASS`, `NOT YET (what is missing)` or `FAIL (why)`. Exit code 0,
  3 or 1 respectively.

## Running it

On the machine being cut over (DSWETT-DEV-VM1 first), as the user agent-ha-bridge runs for,
from a checkout of the commit to be compared, in PowerShell 7, not elevated, and from an ordinary terminal: inside an agent session agent-bridge-host refuses to change its own config (#44), and the install stops saying so:

```powershell
pwsh build/shadow-week.ps1 -Install -WhatIf   # what it would do
pwsh build/shadow-week.ps1 -Install           # this machine only
pwsh build/shadow-week.ps1 -Install -Relay    # and on the phone, read-only
pwsh build/shadow-week.ps1 -Update            # later: a new build only, tasks and config as they are
```

`-Install` prepares everything while a previous install keeps running, and swaps only once
nothing is left that can fail for want of an input:

1. Checks agent-ha-bridge is installed and, with `-Relay` or `-RelayUrl`, that the relay is an
   `https` address (`http` only to this machine). Then publishes `agent-bridge-host` and
   `agent-bridge-hostw` from the checkout, self-contained, into
   `%LOCALAPPDATA%\AgentBridgeShadow\bin.new`, beside the folder the running install uses.
   Not the app's folder, and nothing on `PATH`.
2. Makes the profile `shadow` approve exactly agent-ha-bridge's workspaces
   (`newSession.workspaces`, `~` expanded, labelled as `Get-BridgeWorkspaceChoices` labels them, a repeated label getting its path after it, with `isolate`): whatever it
   approved before is removed with `workspace remove`, then each is added with
   `workspace add`. `newSession.resumeCount` is copied to `launch.resumeCount` (unset when
   absent). The host offers to resume only sessions in folders it approves, so without this
   the resume lists cannot agree; any step failing stops the install. A folder that no
   longer exists is skipped with a warning. Its further Copilot logins,
   `usage.copilotAccounts`, are copied to the profile's `usage.copilotAccounts` when it sets
   them (a list, or one login alone), each in its place. An entry that is not a login is
   passed on empty, keeping its place, and is never repeated in the output: a token pasted
   there would otherwise reach a second config. When agent-ha-bridge does not set it, the
   profile's own value is left as it is, so one set by hand survives a reinstall. Only those
   three keys are read from agent-ha-bridge's config. Re-run `-Install` after changing
   agent-ha-bridge's workspaces or logins.
   Then sets `delivery.readOnly`, and with `-Relay` `push.enabled` and signs in (below). A
   running host reads its config only when it starts, so none of this reaches it until the swap.
3. Swaps: stops the previous install's tasks and processes (only those running from its own
   folder), moves `bin` to `bin.previous` and `bin.new` to `bin`, then registers two per-user
   logon tasks, made like the app's (`LogonTaskXml`): the user's
   interactive token, `LeastPrivilege`, hidden, restarted every minute on failure (three
   times), no time limit, not stopped on battery, one instance:
   * `AgentBridgeShadowHost_shadow_<user hash>`: `agent-bridge-hostw serve --profile shadow --local-only`,
     or with `-Relay`, `agent-bridge-hostw serve --profile shadow --name "<COMPUTERNAME> (shadow)"
     --relay <the relay its account came from>`.
   * `AgentBridgeShadowRun_shadow_<user hash>`: `agent-bridge-hostw shadow run --profile shadow
     --interval 15 --grace 60`, a minute after logon so the host is serving by then.

   Never named `AgentBridgeDaemon*` (agent-ha-bridge's, which the G5 probe looks for) or
   `AgentBridgeHost_*` (the app's).
4. Starts both, and waits to see them serve: the host's log written since it started, and both
   tasks still running a few seconds later (`Start-ScheduledTask` only asks). Only then is
   `bin.previous` dropped. With `-AgentHaBridgeHome`, the run task is given it too
   (`--agent-ha-bridge-home`), so it compares the daemon the workspaces were copied from.

**All or nothing.** An install that fails in steps 1 and 2 (a bad relay, including one a
signed-in profile already uses, a task that cannot be exported, a failed publish, a refused
config change, a sign-in that did not complete, `-Confirm` declined at the swap) has stopped nothing: the previous
tasks keep running as they were, and the profile's config is put back as it was. One that
fails in the swap, including a new host or comparison that exits at once, stops the new build, puts back `bin`, the profile's config and the previous
tasks exactly as exported, starts again those that were running, and only then fails, saying
so (or saying what it could not put back). Interrupted with Ctrl+C, it puts them back the same
way, and writes to stderr anything it could not. An install cut short harder than that (the
machine losing power mid-swap) leaves `bin.swapping` beside `bin.previous`; the next `-Install`
then keeps `bin.previous`, the last build known to serve, as the one to fall back to, and
drops the unfinished `bin`.
**A new build only (`-Update`).** Publishes the checkout and swaps it in under the tasks
already registered, the same all-or-nothing way (steps 1, 3 and 4, with nothing registered),
and changes nothing else: no config, no workspace, no sign-in. It therefore runs inside an
agent session too, where `-Install` stops at its first config change (#44), and the guard
stays as it is. A swap that fails puts back the previous build and starts the tasks that
were running, without re-registering them. With no install to update (either task or the
build missing), it refuses and changes nothing. It also leaves the profile as it was, so
re-run `-Install` from an ordinary terminal after agent-ha-bridge's workspaces or logins
change, or to change this script's options (`-Relay`, `-Interval`, `-Grace`). Added after
the 2026-10-30 redeploy on DEV-VM1 had to be a binary swap done by hand.
`pwsh build/test-shadow-week.ps1` checks each of those against stand-ins for the tasks,
`dotnet` and the host, and CI runs it on Windows. `-WhatIf` shows the whole plan and changes
nothing.

**Read-only by construction.** `-Install` sets `delivery.readOnly` in the profile's config
([host-config.md](host-config.md)), so the host takes no answer, reply, stop, launch, approval
or transfer from any device, **whether or not agent-ha-bridge is running**. Its G5 hold alone
lasted only while the daemon ran: an agent-ha-bridge update, a crash or a logoff race
restarting it would have left the shadow host acting in the gap. While agent-ha-bridge runs,
the host also withdraws them for G5, and `machine.g5` reports it if it ever does not.
Without `-Relay`, `--local-only` keeps it off the relay. A new profile has no Home Assistant token of its
own, so the host's Home Assistant connector is off (its log says so) and it publishes
nothing over the daemon's entities. `shadow run` writes only its reports.

### On the phone (`-Relay`)

`-Install -Relay` puts the shadow host on the relay too, so the phone shows it beside the
others, read-only:

* **Named apart.** It serves as `<COMPUTERNAME> (shadow)` (`--name`), so the machine list
  tells it from the machine's own host.
* **No notifications.** The install sets `push.enabled` to `false` in the profile's config
  ([host-config.md](host-config.md)): agent-ha-bridge already notifies the phone, and two
  notices for each question would be noise. The host then takes no subscription and sends
  nothing; `hello` leaves `push` out, so the phone says the machine doesn't send
  notifications, and a `push.subscribe` sent anyway fails saying so.
* **Read-only, and saying why.** `hello` leaves out `sessions.answer`, `sessions.approve`,
  `sessions.reply`, `sessions.stop`, `sessions.launch` and transfers, and carries
  `machine.readOnly`, the reason ([v1-sessions.md](protocol/v1-sessions.md#hello)): "this is a
  read-only comparison host (delivery.readOnly in its config); answer at the machine". Each of
  those sent anyway is refused with the same words, and nothing is typed, started or ended
  (`LaunchFlowTests.A_read_only_host_takes_nothing_from_a_phone_with_agent_ha_bridge_not_running_and_says_why`).
  The phone shows the reason on the machine's status line and on each question, offers no
  reply box, End session or Launch, and shows questions read-only. Because of `delivery.readOnly`
  this holds while agent-ha-bridge is stopped or restarting too; only the reason differs (G5's
  is given when the switch is off). Making the machine an acting host after the cutover means
  turning the switch off on purpose (`config unset delivery.readOnly`), not just stopping
  agent-ha-bridge.
* **Signed in once.** A profile that has never signed in is signed in during the install:
  `agent-bridge-host sign-in --profile shadow` prints an address and a device code; open it
  on any device and sign in as the account the phone uses. The relay is `-RelayUrl`, else
  `AGENT_BRIDGE_RELAY`, else the one the profile used before, else the app's own profile's,
  else the relay the phone's app uses: `S4_RELAY` in `.github/workflows/pages.yml`, which the
  Pages build bakes into the PWA, read from the checkout. So on a machine without the app
  (DEV-VM1) `-Install -Relay` needs no `-RelayUrl`.
  The task names the relay that issued the profile's account (`--relay`), so the host never
  sends that account's token to another relay, `AGENT_BRIDGE_RELAY` included. A profile already
  signed in keeps its relay; a different `-RelayUrl` stops the install until it is signed in
  again there.
  To do it by hand, from an ordinary terminal (inside an agent session it is refused, #67):

  ```powershell
  & "$env:LOCALAPPDATA\AgentBridgeShadow\bin\agent-bridge-host.exe" sign-in --profile shadow --relay <relay url>
  ```

The comparison does not change: `shadow run` reads the host over its local pipe, never the
relay, so whether the host is connected to the relay, or signed in at all, does not affect
it.

**Logoff and reboot.** Both tasks start at every logon of that user, so they come back
after a reboot or a logoff with no one touching them. While the user is logged off, both
are stopped, as are agent-ha-bridge's daemon (also a logon task) and the sessions it would
show, so there is nothing to compare; the day's `comparedSeconds` shows the gap. A
disconnected remote desktop session is not a logoff: everything keeps running.

**Checking on it:**

```powershell
pwsh build/shadow-week.ps1 -Status            # both tasks' state, the relay (on or off, signed in, last said), then the summary
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
| `usage.missingOnHost`, `usage.missingOnDaemon` | One side has a reading for an agent (Copilot, Claude, Codex), or for a further Copilot login in a place (`copilot_2` to `copilot_4`, agent-ha-bridge's and the host's `usage.copilotAccounts`), and the other none. The report names the agent or the place in place of a session, never the login |
| `usage.account` | The account the reading is for differs (compared case-insensitively, never written) |
| `usage.windows` | The metered windows differ (by key: `monthly`, `five_hour`, …) |
| `usage.percent` | A window's percent used differs by more than 5 points. Judged only on a pair of fresh readings, within 3 minutes of each other: the host reads every 15 minutes while the daemon runs, so this is now and then. The daemon publishes a change at its next poll but an unchanged figure only every half hour (every two intervals with a `dashboard.publication`), and republishes it with a new `measured_at`, so its figure counts as holding from when it first stood (the same figure read again, minute by minute, republished or not) until its last poll could have been: the read less `usage.idleIntervalSeconds` and a minute, while `measured_at` is recent enough for the daemon still to be republishing it. `run` counts it once two such pairs in a row disagree, the second with both sides having read again since the first, and keeps it until a fresh pair agrees, so a lasting difference outlasts the grace and a rise one side has not read yet never counts. `once` reports a single fresh pair |
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
* **A question waits as the card shows it.** agent-ha-bridge's status for a Copilot session
  comes from the transcript and stays `working` while `ask_user` waits; no hook publishes
  `waiting` for it. Both its cards say "waiting for you" whenever the session's decision
  entity carries a question, which the hook that arms its marker puts there. So a session
  with an armed marker is the daemon's `waiting`, which is what its user sees; a marker
  caught half written matches either. Found live as `status` daemon `working`, host
  `waiting`, for minutes while a session asked a question with background agents running:
  the host was right. It is not a sub-agent precedence question (#84): the daemon's
  transcript status for Copilot is only `working` or `idle`, its sub-agents' events already
  left out, so it stays `working` through every `ask_user`, with background agents or without.
  The host's status logic is unchanged; only how the daemon's view is read.
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

The first live runs, on DSWETT-DEV-VM1 against agent-ha-bridge 1.33.32, turned up six
places where the daemon is wrong and the host right, each checked against the transcript
and, for shells, against the processes actually running under the session's CLI. Each is
filed against agent-ha-bridge and left there.

A difference is put down to one of them only on evidence (`Shadow/ShadowWitness.cs`), never
because of the field it is in. The daemon's state file lists the ids behind its counts
(`BackgroundAgents` by tool call, `BackgroundShells` by shell id) and how far it has read the
transcript (`Offset`). For a Copilot session with a difference in background work or model,
the comparison reads the transcript and checks every id the difference involves for the bug's
signature below, counting only what the daemon has read (an end, a completion or a wake it
has not reached yet is the daemon being behind, not its bug). The daemon's list must carry
every id behind its count. Then the counts have to add up
exactly: the daemon's, less what the signatures explain (plus, for #276, the restarts it
dropped), must equal the host's. Anything
else, an id with no signature or a count left over, keeps the difference a mismatch. A
difference that matches is a **known difference**: tallied in the day's `known`, by kind and
issue, and never a mismatch. The first minutes of the live run on DEV-VM1 showed three of the
five, and the comparison now classifies each as below.

| Kind | Signature in the transcript | Filed |
|---|---|---|
| `background.agents`, daemon lower | Each agent the host counts and the daemon does not is working again (its own events) after its first `subagent.completed`, both before the daemon's `Offset`, and none of those the daemon lists is one of them | agent-ha-bridge#258 |
| `background.shells` or `background.agents`, daemon higher | Each extra id's end (a completion line, a stop, a `shell_completed`; a `subagent.completed`) is in the transcript after its start and before the daemon's `Offset`: the daemon has read past it and not applied it | agent-ha-bridge#259 |
| `model` | The last `assistant.message` the daemon has read is a subagent's and names the daemon's model, and the host's is the session's own | agent-ha-bridge#260 |
| `background.shells`, daemon higher | Every start of each extra id since its last end is in a subagent's tool result | agent-ha-bridge#261 |
| `background.shells`, daemon higher | Each extra id's only start of the session's own is an "is still running" line that is not its tool result's last line | agent-ha-bridge#262 |
| `background.shells`, daemon lower | Each shell the host counts and the daemon does not list ended and was then started again under the same id, by the session itself (a start of its own, read as the host reads one), with no end since; the end and the restart both before the daemon's `Offset` | agent-ha-bridge#276 |
| `question.missingOnHost`, and `status` daemon `waiting` | The daemon's question is the `ask_user` its marker names (by call id, else the one started within 10 s of the marker being armed); that `ask_user` has completed in the transcript, and its start lies more than 4 MiB before the end of the line that completed it. For `status`, the daemon's own status must also be the host's; when it is not, the mismatch stands and names the two statuses | agent-ha-bridge#259 (filed as #263, closed as its duplicate) |

What each bug does:

| Kind | What the daemon does | Filed |
|---|---|---|
| `background.agents`, daemon lower | Drops a background agent at its first `subagent.completed`. An agent the session wakes again with `write_agent` runs more turns and is never counted again, so the daemon shows fewer agents than are running | agent-ha-bridge#258 |
| `background.shells` (or `background.agents`), daemon higher, lasting until the session ends | Its tail reader skips the start of a burst over `MaxTailBytes` (512,000 bytes) between passes. A shell's or agent's end in the skipped part is lost and the daemon keeps waiting on it. Compactions are counted over every byte, so they are not affected | agent-ha-bridge#259 |
| `model`, flipping | Sets the session's model from every `assistant.message`, subagents' included, so a session whose agents run another model shows theirs whenever one speaks last. parity.md §1: a subagent's model is not the session's | agent-ha-bridge#260 |
| `background.shells`, daemon higher, in a session with background agents | Counts a background agent's shells as the session's. Their ends go to the agent and often never reach the session's transcript: in the session that found it, nine shells agents had moved to the background had no process left and no recorded end, and stayed counted. The host leaves an agent's shells to the agent, which is counted while it runs | agent-ha-bridge#261 |
| `background.shells`, daemon higher, a session reading `shell` while idle | Takes an "is still running" marker anywhere at a line's start in a tool result. A `view` of a file quoting one invented a shell nothing will end. The host takes it only as a result's last line, where Copilot writes it | agent-ha-bridge#262 |
| `background.shells`, daemon lower, while a restarted shell runs | Folds a batch's shell starts and ends without their order (`Update-DaemonBackgroundShells` adds every start, then removes every end). A shell ended and started again under the same id within one batch loses the new start to the old end, and nothing later adds it back. Found on 2026-11-01 when a `pr-watch` watcher completed at 00:12:35 PDT and was restarted at 00:13:15: the daemon listed nothing from then on while its process ran, 41 mismatches in a morning (one disagreement, split each time the daemon's heartbeat lagged). Replaying the daemon's own functions over those lines gives 0 as one batch and 1 as two | agent-ha-bridge#276 |
| `question.missingOnHost` and `status` daemon `waiting`, after the question was answered | Learns that a question was answered only from the last 4 MiB of the transcript (`Get-CopilotAskUserState`). When a session writes more than that while its question waits, the `ask_user` start has left the tail by the time it is answered, so the daemon never sees the question complete: the marker stays, and so do the card's question and "waiting for you", until the session asks again. Found when a question asked at 00:58 and answered at 09:32 PT, 7.1 MiB later, was still on the card 20 minutes after. The host read it answered at once | agent-ha-bridge#259 (filed as #263, closed as its duplicate) |

**The first minutes on DEV-VM1**, classified:

| Seen | What it was |
|---|---|
| `background.agents` daemon 0, host 2 to 4, on the session running many sub-agents | The sub-agents woken again with `write_agent` (#258). The host is right |
| `background.shells` daemon 13 to 17, host 0 to 1, same session | Shells started by its sub-agents (#261), one of whose ends the daemon also read past (#259). The host is right |
| `background.shells` daemon 1, host 0, another session | The quoted "still running" line found earlier (#262). The host is right |
| `status` daemon `shell`, host `working`, for 0 s | Timing: the host saw a new turn start a sample before the daemon's pass. Never lasted the grace |
| `activity.response` differing for 91 s on the busy session | Timing: its sub-agents' messages changed the latest response every few seconds, and the daemon, writing on its own pass, kept showing the one the host had shown a sample or two before. Now classified: a response one side is showing that the other showed in the last three minutes is a `timing` known difference; one neither showed is still a mismatch |
| `status` daemon `working`, host `waiting`, for 60 s and 167 s, after the redeploy | The busy session asking its user a question (`ask_user`) with background agents running. The daemon's state stays `working`, but its cards show "waiting for you" from the armed question, so the host is right; now normalized (above) |

**The first full day** (2026-10-30), classified:

| Seen | What it was |
|---|---|
| `status` daemon `working`, host `waiting`, four episodes from 00:58 to 09:30 PT (6 h 45 min the longest), on the busy session | The same question pending all night, compared by a build from before the normalization above (#82). The host was right. These stay mismatches in that report: `gate.json` can explain them with `through` (below), or they stop failing the gate 24 hours after they ended |
| `question.missingOnHost` and `status` daemon `waiting`, host `working` or `idle`, from 09:37 PT on, same session | That question, answered at 09:32, kept by the daemon (agent-ha-bridge#259; filed as #263). The host was right; now classified |
| `usageReadings` 0 events, with usage "compared" in nearly every sample | Another machine (DSWETT-HOME) was reading the account's Copilot usage, so this machine's daemon had stood down (agent-ha-bridge's `Test-BridgeUsageCovered`), and its sensor held an 11-hour-old figure. No pair of readings was ever close enough to count, and none should have been called compared. The comparison now takes the account reader's figure ("Home Assistant reads"), and, with none current, does not compare usage |

### Fixed in the host

The same runs, and review, found six host bugs, all fixed:

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
* **A repeated label named differently** (`LaunchFlowTests`). agent-ha-bridge tells repeated
  labels apart (ignoring case, the repeat taking its id's first eight characters) over that
  same list, running sessions included, and only then leaves the running ones out. The host
  did it after, so an older namesake of a running session had a plain label here and a
  suffixed one there. It now does it in the same order.
* **Working after the turn had ended** (`BackgroundTests`). Copilot answers a background
  agent's `agent_idle` notification inside the turn it starts: a reply asking for
  `read_agent`, its result, then a reply with nothing more to run, then `turn_end`. The host
  remembered the first reply's tool request, so it read that `turn_end` as one more round to
  come and showed the session working until its next prompt. Seen as `status` daemon `shell`,
  host `working` on 2026-10-30 from 17:05 PT, cut short by the next bug. The daemon was right:
  a turn's latest reply now decides.
* **A relay socket that aborted itself crashed the host** (`RelayTests`). The keep-alive
  aborts a socket whose pong never comes, and the receive then throws
  `OperationCanceledException`, which the relay client let through. Four minutes after a
  production relay deploy on 2026-10-30, the shadow host exited at 17:08 PT, and its task
  did not restart it (`machine.host` for hours). The connection now just ends and the host
  reconnects.

### Not compared

| What | Why |
|---|---|
| Usage with no current reading of the account | A figure here older than the daemon's own pace allows (`usage.idleIntervalSeconds`, plus its re-assert, plus a minute) is one this machine's daemon has stopped reading, usually because another machine reads the account. The account reader's figure stands in for it ("Home Assistant reads"). When no machine the daemon names has a current one, the sample counts toward `notComparedWhy` ("usage: no current reading of the account, …"), not `usageCompared` |
| Titles | agent-ha-bridge names sessions for Home Assistant ("Copilot: …") |
| Reasoning and tool history | Bounded differently and shown differently (§1) |
| Driver, the purple edge | Set by the presses on the cards, which only the daemon reads while it runs: beside it, the host takes none, so its own would always read as the person's (#46) |
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

A count that turns the other way (the daemon's higher, then the host's) is two disagreements,
each judged against the grace on its own. On DEV-VM1 a shell ended and was started again a
minute later, with the daemon a pass behind each time. Two opposite lags, each shorter than
the grace, had run on as one 60 s mismatch. This applies to the kinds compared as two counts
(`background.*`, `compactions`, `resumable.count`); a count that moves the same way stays one
disagreement.

## Coverage

Every compared sample is also turned into **events**, each counted once however many samples
show it, in the day's `coverage`:

| Kind | One event is | Mismatches counted against it |
|---|---|---|
| `sessions` | A session either side listed, with its agent | `session.*`, `agent` |
| `questions` | A question either side showed, by identity, of kind `choice`, `multi` or `text`, answered or not. Each side knows it by its own identity (the host by the tool call, the daemon by its hook's clock), so a session's run of questions counts the larger of the two sides' counts, each of the kind the side ahead shows | Each question a `question.*` mismatch was about, once (`failedQuestions`, keyed by the identity it was first known by, which each side's identity for the same question maps to, and across midnight with every kind it had), however many kinds it had and however many questions one mismatch ran on across |
| `statusTransitions` | A state a session showing on both sides is compared in: the status it has when first compared, then each change of status (working, idle, waiting, ending) on either side, once a sample. Kept in `shown` (per session hash, the status pair and a hash of the two response hashes), so neither a restart of the comparison nor midnight counts a state again. A marker caught half written counts as the comparison judged it | Each state a `status` mismatch was about, once (`failedStates`, by session, kind and when the state was counted, and marked failed in `shown`): a mismatch running on across several states fails each, those it was about during its grace included, and one split by a restart of the comparison or midnight fails its state once |
| `responses` | Likewise each latest response compared, when both sides have one: the first, then each change on either side | Each response state an `activity.response` mismatch was about, once, likewise |
| `background` | A background agent or shell starting or finishing, as the daemon listed it (and `backgroundSessions`, the sessions that had any) | `background.*` |
| `compactions` | A compaction finishing | `compactions` |
| `modelChanges` | A session's model changing | `model` |
| `resumeSnapshots` | A resume list compared that differs, on either side, from the last one compared | `resumable.*` |
| `usageReadings` | A new host reading of an agent's usage, judged on a fresh pair against the daemon's | `usage.*` |
| `usageResets` | A usage window's percentage dropping, as at a new period | (shows as `usage.percent`) |
| `machineEvents` | The daemon restarting (`daemonRestart`), the host restarting (`hostRestart`), or the comparison coming back after a gap of over three minutes, as after a logoff or a reboot (`observerResumed`) | `machine.*` |

A question answered within one sample (15 s) is never seen, and one pending across a
restart of the comparison is counted again; both are rare, and the counts err low elsewhere.

`summary` adds the days up and prints, for each kind, the events, the mismatches about them
that `gate.json` does not explain, those it does, and the **95% upper bound** on the
unexplained mismatch rate: one-sided, `3/n` with none (the rule of three), otherwise the
exact Clopper-Pearson bound. Mismatches count only from when that kind's events did, and
those past a day's cap (`overflow`) count as unexplained.

**Counting starts** with the first sample a release with coverage writes; reports from before
it (written by 30ef3a7, which DEV-VM1 started on 2026-10-29) have no `coverage`. What they do
hold still counts: the sessions they list (agent `unknown` unless a later report names it),
a side going down and coming back (a `machine.*` disagreement that ended, counted as
`sideDownAndBack`, also for the part of the redeploy's day before it), the time compared,
the first sample and every mismatch. Every other event is counted from the redeploy.

Until the release after 5d5149b, `statusTransitions` counted the daemon's changes alone, and
`status` and `activity.response` mismatch episodes counted against them, so a disagreement
begun by the host's change alone (its `waiting` on a question against the daemon's unchanged
`working`) was a mismatch with no event. From that release (`statesSince`) each state
compared counts whichever side changed, and each state a mismatch was about fails once, so
the rate is failed states over states. `statesSince` is carried across midnight with `shown`. Before `statesSince`, a `status` episode, and a day's
`status` overflow, still count against `statusTransitions` as they did; `activity.response`
ones count nowhere, and a day's overflow from the redeploy on (which has no time to place it)
counts only through the gate's overflow check. Past a day's cap of 5,000 failed states, the
rest count in `failedStatesOverflow`, unexplained, as do the states past the 256 an episode keeps
by identity during its grace.

## The gate

`shadow summary --gate --profile shadow` ends with the verdict over every report:

* **`FAIL (…)`** when any mismatch seen in the last 24 hours is unexplained, when a day in
  that window has mismatches past its cap (they keep no time or session, so nothing can
  explain them), or when the questions' observed unexplained failure rate is over the
  allowed one, however few questions there are.
* **`NOT YET (…)`**, listing what is missing, until all of these hold:

  | Needs | Default |
  |---|---|
  | Questions, with the 95% upper bound on their mismatch rate at most `questionsMaxRate`. Questions matter most: they are what answers act on | 100, 3% |
  | Distinct sessions | 30 |
  | Status transitions | 500 |
  | Background starts and finishes | 50 |
  | Compactions | 5 |
  | Resume snapshots | 100 |
  | Usage readings | 50 |
  | Machine events: a restart, a logoff or a reboot, seen and recovered | 1 |
  | Hours since the first sample (an overnight and a daily usage reset) | 24 |
  | Hours compared | 8 |

* **`PASS`** when nothing is missing or failing.

An agent with fewer than 5 sessions is listed as **not covered**, not failed: the gate says
nothing about it either way.

**`gate.json`**, beside the reports (`%LOCALAPPDATA%\AgentBridge\shadow\shadow\gate.json`),
is the operator's; the host never writes it. Every key is optional, and comments are allowed:

```json
{
  "questions": 100, "questionsMaxRate": 0.03, "sessions": 30, "statusTransitions": 500,
  "background": 50, "compactions": 5, "resumeSnapshots": 100, "usageReadings": 50,
  "machineEvents": 1, "hours": 24, "comparedHours": 8, "cleanHours": 24, "agentSessions": 5,
  "explained": [
    { "kind": "machine.daemon", "session": null, "reason": "agent-ha-bridge updated itself; down 4 minutes" }
  ]
}
```

`explained` is where a mismatch that has been investigated and documented as a known
difference (above) goes: by kind, by the session's hash to explain only that one, and by
`through` (a time, ISO 8601) to explain only what had ended by then. A mismatch it matches
no longer fails the gate or counts against the rate. `through` is for a report written
before the comparison classified a cause, which keeps the mismatch for good: it explains
those without excusing the next one. For example, for the first full day above:

```json
"explained": [
  { "kind": "status", "session": "s8a666b6daa65", "through": "2026-10-30T16:31:00Z", "reason": "pre-#82 build: the armed question is the daemon's waiting" },
  { "kind": "question.missingOnHost", "session": "s8a666b6daa65", "through": "2026-10-30T18:00:00Z", "reason": "agent-ha-bridge#259, before it was classified" },
  { "kind": "status", "session": "s8a666b6daa65", "through": "2026-10-30T18:00:00Z", "reason": "agent-ha-bridge#259, before it was classified" }
]
``` A host bug is not
explained: it is fixed, and the gate passes once the last of it is over 24 hours old.

Mismatches from before the redeploy that the comparison now classifies as known (the
background differences above, from the first night) are still mismatches in those reports.
They stop failing the gate 24 hours after they ended, or sooner if `explained` names them.

## The reports

`%LOCALAPPDATA%\AgentBridge\<profile>\shadow\shadow-<yyyy-MM-dd>.json` (`~/.agent-bridge/<profile>/shadow/`
elsewhere), one per local day, in a folder made private to the user like the attachment
staging folder. Rewritten whole after each sample, by renaming. A file that cannot be read
when `run` starts is kept beside it as `.unreadable-<time>`, never overwritten.

```json
{
  "schema": 2, "date": "2026-11-02", "timeZone": "Pacific Standard Time",
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
  "known": {
    "background.shells": { "agent-ha-bridge#261": { "samples": 5600, "sessions": ["s8a666b6daa65"] } },
    "activity.response": { "timing": { "samples": 12, "sessions": ["s8a666b6daa65"] } }
  },
  "coverage": {
    "since": "…", "sessions": { "s8a666b6daa65": "copilot" }, "questions": { "choice": 14, "text": 3 },
    "failedQuestions": { "4c1d…": { "session": "s8a666b6daa65", "kinds": ["question.options"], "last": "…" } },
    "statusTransitions": 410, "responseChanges": 380, "statesSince": "…",
    "shown": { "s8a666b6daa65": { "status": "working/working", "statusAt": "…", "statusFailed": false,
      "response": "9b2e…", "responseAt": "…", "responseFailed": false } },
    "failedStates": { "77ac…": { "session": "s9fc81a924c6a", "kind": "status", "last": "…" } }, "failedStatesOverflow": {},
    "backgroundSessions": ["s8a666b6daa65"], "backgroundStarts": 61,
    "backgroundFinishes": 58, "compactions": 2, "modelChanges": 0, "resumeSnapshots": 37,
    "usageReadings": 96, "usageResets": 1, "machineEvents": { "observerResumed": 1 },
    "daemonStartedAt": "…", "hostStartedAt": "…"
  },
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
* `known` counts, by kind and then cause, the samples in which a difference matched a known
  cause (an agent-ha-bridge issue, or `timing`), and the sessions it was seen in. Schema 2;
  a day begun by an earlier release has it only from the redeploy, as it has `coverage`.

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

## Judging it

By the gate, not by days: `shadow summary --gate` reading `PASS`. Days still matter as
context (the per-day verdicts, and how much of each was compared), but a clean day with
nothing happening in it says little, which is why coverage is counted in events.
Timing skew is expected and is not a mismatch, but a kind whose longest skew keeps
approaching the grace deserves a look.

## Not done here

The issue's other two parts are separate. The **recorder** inside agent-ha-bridge is not
needed for this diff: the daemon's own state file, heartbeat and question markers, and what
it already publishes to Home Assistant, carry what is compared, so agent-ha-bridge is
unchanged (no G10 release). The **replay harness**,
which turns recorded traces into xUnit cases named for agent-ha-bridge's scenarios, still
needs recorded traces and remains open in #40.
