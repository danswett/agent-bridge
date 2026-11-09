# Agent launch scope: ask-only launches by agents, with an hourly cap and an audit (#1403)

**Status: design for the owner's sign-off. No code changes with this note.** Slice 2a of the
Home Assistant retirement epic (#1396). It decides what slices 2b (#1404), 2c (#1413) and 2d
(the fleet repository's #106) build. It narrows [agent-principals.md](agent-principals.md) §3 and §5
for the first version; where the two disagree for v1, this note wins, and agent-principals.md
stays the long-term design. Times are Pacific.

## Owner summary (one page)

**Fleet workers (decide first).** Workers are launched today through Home Assistant with
"Allow all". The ask-only rule would stop them: a worker that asks before every command waits
for a person on every step. **Recommended: a separate, narrower fleet launch grant.** Only the
dispatcher's own machine credential (not any agent session) may launch with Allow all, only in
folders you tick "fleet workers may run here" at that machine, always in a fresh worktree, under
its own hourly cap, and every launch is audited. Agents themselves stay ask-only, always.
Cost: one extra grant to build (about 1 day, after the script credential #1414), and an Allow-all
path that a person or program tricking the dispatcher could reach, bounded by the folder list
and cap. **Alternative: force ask-only for workers too.** Cost: workers stall on every tool
prompt until a person approves it; the fleet stops being unattended and needs a rework of how
workers run (weeks), or you approving hundreds of prompts a day.

**What an agent can do** (once you switch it on at a machine, and only for sessions you allow):
start a new session **on the same machine**, with permissions **Ask permission** only, in a
folder a person approved at that machine, with an agent and model that machine offers, and an
opening prompt up to 16 KiB. It sees whether the launch started and the new session's id.

**What an agent can never do:** launch with Allow all; resume or bring over an existing
session; launch on another machine (until device approval, #55, lands); pick a folder, flag or
setting the machine does not offer; answer the new session's folder-trust or permission
questions; give the new session more rights than it has. A session an agent starts can read,
and cannot launch anything itself.

**What could go wrong, and what stops it:**
* *An agent is tricked by text it read into launching sessions.* Ask-only means the new session
  still asks you before acting; the hourly caps (3 per agent, 10 per machine) bound the number;
  every attempt is in the audit log.
* *A runaway chain of agents starting agents.* A launched session cannot launch: the chain stops
  at one step.
* *A stolen session credential.* It only works from inside its own session, and dies with it.
  This is a safety rail, not a wall: a program running as you can get round it (it can already
  do anything you can).
* *A new session reads files in its folder.* It runs as you, as every session does. That is why
  agents may only use folders you approved for this.
* *Once HA launch is gone, a full disk or an unwritable log stops worker launches on that
  machine.* Launches never go unrecorded; the machine shows why, and refuses the fleet grant
  with under 1 GiB free so it is caught early.

**Decisions for you** (each has a recommended default; say "defaults" to take them all):
1. Fleet workers: the fleet launch grant above (**recommended**), or force ask-only.
2. Who may give a session the right to launch: **you, per session, when you launch it** (a
   "may start agents" tick), and only on machines where you turned agent launch on. Off by
   default everywhere.
3. Limits: **3 launches per agent session and 10 per machine per hour from all agents; fleet
   grant 8 per machine per hour** (the dispatcher allows 6 workers per machine); **at most 6 live
   agent-started sessions per machine, ended after 2 hours idle.** You can raise a limit at the
   machine for up to 4 hours, for example to recover after a reboot.
4. Long prompts: **keep the 16 KiB limit and refuse longer ones** with a reason; a longer
   handover goes in a file the prompt points to.
5. Other machines: **this machine only** until device approval (#55). Meanwhile the
   dispatcher still decides centrally, and a small launcher on each machine starts its own
   workers.
6. Sessions an agent starts: **read only, cannot launch, cannot be resumed by agents** (fleet
   workers may also send messages, so they can report back).
7. Audit: **launches in a file of their own, kept 30 days or 16 MiB on each machine, no prompt
   text**, readable in the app and by a command.

---

## 1. Scope model

**Decision.** Agent launch is the existing `session.launch` called on an agent connection
whose credential carries the `read+reply+launch` scope. There is **no agent-only operation**:
the same op, records and outcomes every client uses ([v1-launch.md](../protocol/v1-launch.md)),
with stricter checks for the agent principal. That follows the one-client-protocol rule (#1396).

| Scope | Ops on the pipe | Who has it in v1 |
|---|---|---|
| `none` | none | a revoked or disabled session |
| `read` | `sessions.list`, `session.get`, `sessions.subscribe`, `usage.*`, `launch.options` (agent projection, §7) | **default** for every launched session (`AgentCredentials.cs:104`) |
| `read+reply` | the above, `session.reply`, `agent.send` | a session a person grants it to |
| `read+reply+launch` | the above, `session.launch` under §3's rules | a session a person grants it to, on a machine with agent launch on |

The four scope names already exist (`AgentPrincipals.cs:18-23`) and are stored per credential
(`AgentCredentials.cs:12-13`, `:82`); today every launch mints `read`
(`AgentCredentials.cs:103-104`, called at `LaunchService.cs:969`) and no op reads the scope.

**Who grants it.** Two keys, both needed:
* **Machine switch:** `agents.launch.enabled` in that machine's `config.json`, **default
  `false`**. Changed only at the machine (the app's Settings, or `agent-bridge-host config`).
  Changing settings is already refused inside agent sessions (`LocalApi.cs:54-63`;
  local-pipe.md:101); the new key is one more setting under that rule.
* **Per session:** a person's launch may set `agentScope` (additive field on `session.launch`, as
  agent-principals.md §3.3 planned). Only a person client (the app, the PWA, the Windows app) may
  set it. An agent connection or a connector that sends `agentScope` is refused. With the switch
  off, `agentScope` above `read+reply` is refused with that reason. **Adopted sessions never get
  launch in v1**: their permission mode is unknown (agent-principals.md §5.3), so adoption gives
  at most `read+reply`.

**Turning it off.** Setting the switch off re-scopes every live `read+reply+launch` credential on
that machine to `read+reply` at once (the stored record changes; agent-principals.md §3.4,
"Revocation"), and the next launch is refused. Per session: "Stop this session starting agents"
in the app, or `agent-bridge-host agents revoke <session>`. The kill switch for all agent
acts is `agents.enabled: false`, which makes every agent connection `read`.

## 2. Enforcement point

**Decision.** One check, at the pipe, before the launch service: in `LocalApi.Handler`, where
acting ops are refused for `CallerKind.Agent` (`LocalApi.cs:68-71`). `session.launch` from a
connection with a live agent credential (`AgentCredentials.Of`, `AgentCredentials.cs:194-206`)
goes to a new `AgentLaunchGate`; every other op in `Acts` (`LocalApi.cs:22-23`) stays refused for
agents exactly as today. The gate runs, in order, refusing at the first failure:

0. a repeat of a `request` this principal already sent, with identical bytes, gets the remembered
   outcome (§7) and stops here: it is neither counted nor started again;
1. the credential is live and its scope is `read+reply+launch` (re-read on every call, so a
   revocation takes effect at once);
2. the machine policy is readable and `agents.launch.enabled` is `true`;
3. the request passes §3's content rules;
4. under one lock: the cap (§5) has room, a slot is reserved, and the audit line for the attempt
   (§6) is written. Two launches at once can never both take the last slot; an audit line that
   cannot be written releases the slot and refuses;

then the request goes to the launch service with the agent principal as its peer, which applies
its own checks unchanged (host-launch.md:110-125, `LaunchProtocol.Check`). A refusal at any step
is written to the audit log too (§6).

**Fail closed.** An unreadable or malformed `agents` policy, an unreadable credential store
(already: `AgentCredentials.cs:132`), caps not yet rebuilt from the audit (§5) or an audit log
that cannot be written each refuse the launch. Unreadable is never read as allowed, the same
posture as the credential store and host-config.md:139-141. In operation:
* **Transient is not unreadable.** A file briefly locked (a virus scan, a backup, a sync client)
  is retried for about a second before the host decides.
* **Distinct codes, all with `retryAfter`:** `policyUnreadable`, `auditUnwritable`,
  `capRebuilding`, so a dispatcher retries later instead of treating them as permanent. The pipe
  never hangs: while caps rebuild, launches are refused at once with `capRebuilding`.
* **Visible.** A refusal that cannot be audited is otherwise invisible, so `host.status` carries
  the current fail-closed reason and since when, the last successful audit write, refusal counts
  by code, and the caps' current use; the host's own log is the fallback record. SRE alerts on it.
  (Any client, the HA component included, shows it from `host.status`.)

**Closing the side door.** Today only `CallerKind.Agent` is refused acting ops: any other
same-user process (a script, the HTTP MCP) may `session.launch` as the person, with Allow all
and no audit (`LocalApi.cs:14-15`, `:68`; local-pipe.md:104). That is a private path, and the
fleet must not move onto it. #1404 refuses `session.launch` from `CallerKind.Other`, keeping the
app and the host's own `launch` command (agent-principals.md §3.4, "three explicit exceptions").
The other acting ops follow in AP8 as planned.

## 3. What an agent launch may contain

Checked by `AgentLaunchGate` before the launch service sees it:

| Field | Rule for an agent | Why |
|---|---|---|
| machine | **this machine only**: the gate serves agent launches on the local pipe alone, and `session.launch` has no machine field (v1-launch.md:76-103); an agent launch arriving any other way is refused | Cross-machine waits for #55 (§9) |
| `permissions` | **must be present and `ask`**. Absent or `allowAll` is refused | Absent would take the machine default, which may be `allowAll` (v1-launch.md:101) |
| `transfer` | refused | Transfers need a person (agent-principals.md §5.2) |
| `resume` | **refused in v1** | Resuming types an agent's prompt into an existing conversation, possibly a person's; new sessions only keeps it simple |
| `attachments` | refused | `attachment.*` is already refused for agents (`LocalApi.cs:23`, `:68`) |
| `workspace` | required, must be on the machine's `agents.launch.workspaces` list (labels a person ticked "agents may launch here", default empty), and must be an isolated workspace, so each launch gets a fresh worktree. The host refuses the launch if that worktree holds a project settings file that pre-approves tools for the chosen agent (for example Claude's `.claude/settings.json` with `permissions.allow`); an agent whose project settings the host cannot check is refused | A folder a person approved at that machine for this purpose. Without the settings check, an agent could plant an allow list in the repository and then launch over it |
| `agent` | required, from `launch.options`, on `agents.launch.agents`; `auto` and Agency refused | Agency adds profile config the host cannot show keeps ask (agent-principals.md:834-836) |
| `profile` | refused | Agency only, which is refused |
| `model`, `effort`, `context` | from `launch.options` (existing check, host-launch.md:117-119) | |
| extra arguments | a launcher with any `launch.agents.<id>.extraArgs` is refused for agents | An extra argument can grant Allow all (host-launch.md:295). The verification launcher already does exactly this (`LaunchService.cs:561-565`) |
| `agentScope` | refused | Only a person sets scope (§1, §4) |
| `prompt` | at most **16 KiB** of UTF-8; longer is refused (below) | |

**Prompt length: keep 16 KiB, refuse longer.** The protocol limit is 16 KiB of UTF-8 in a request
of at most 20 KiB (`Launch.cs:17-18`; v1-launch.md:102, :105-115), the MCP server checks the
same 16 KiB before calling the host (`mcp/src/server.mjs:70`, `:104`), and the host refuses a
longer prompt with "send the rest as an attachment or a reply once it has started"
(`Launch.cs:77`). We keep that number and do not raise it:
* the prompt rides the agent's command line; an npm-installed agent through `cmd.exe` takes
  only 8,191 characters, and the host already refuses what does not fit (v1-launch.md:113-115);
  a larger cap would need a second channel (a prompt file), which is a new way for content to
  enter a session and is not worth it for v1;
* the signed cross-machine envelope later relies on these limits unchanged
  (agent-principals.md §4.3, `dataSha256`).

A 20 KB prompt is therefore **refused, not delivered**. For agents the refusal reason drops
"as an attachment" (agents may not attach) and says: "the prompt is longer than 16 KiB; put the
rest in a file in the workspace and point to it." HA's "any length" was HA's own text store; the
dispatcher's prompts are short templates. **Who implements:** nothing new for the limit itself.
#1404 adds the agent wording and the tests (a 16 KiB prompt arrives whole, one byte more is
refused, a 300-character prompt arrives whole), which settles #1404's and #1413's "up to the
length #1403 decides". The MCP server needs no change to its cap; #1404 removes `Allow all`
from `launch_agent_session`'s schema (`mcp/src/server.mjs:45`; `mcp/src/tools.mjs:6`) and its
`profile` and `resume` arguments, so an agent is not offered what the host will refuse.

## 4. The launched session's own scope

**Decision.** A session started by an agent gets `read`, and nothing in the request can change
that. Its scope is the narrowest of: `read`, the machine's `agents.issue.launched`, and its
launcher's scope. It can never launch (`allowAgentLaunchedToLaunch` fixed at `false` in v1,
agent-principals.md:685, :1092).

**Maximum depth: one agent launch step.** The credential records `depth` and `chain`
(`AgentCredentials.cs:12-13`), and a person's launch mints depth 0 (`AgentCredentials.cs:80`,
`:104`). A person launches a coordinator (depth 0), the coordinator launches a worker (depth 1),
the worker cannot launch: the gate refuses any launch whose launcher's `depth` is 1 or more, so
even a mis-set policy cannot make a chain. A child cannot widen its parent's scope:
the gate computes the child's scope, the request has no say (`agentScope` refused, §3), and a
resume, which could pick up an old wider credential, is refused.

Fleet workers launched through the fleet grant (§11) get `read+reply`, so they can report to
their coordinator; never `launch`. Their reply reach is the same as any agent's (`agent.send`
under the own-account mode, `session.reply` only into sessions agent-principals.md §5.3 allows);
running with Allow all does not widen it.

## 5. Cap and fairness

**Decision.** Sliding one-hour windows, the agent ones from agent-principals.md:706-707:

| Limit | Default | Counted per |
|---|---|---|
| `limits.launchesPerOriginPerHour` | **3** | launching agent session |
| `limits.launchesPerMachinePerHour` | **10** | this machine, all agents together |
| `limits.fleetLaunchesPerMachinePerHour` | **8** | this machine, the fleet grant (§11); the dispatcher allows 6 workers per machine. Separate from the agent caps: fleet launches do not use up agents' 10, nor agents the fleet's 8 |

**What counts:** each distinct (principal, `request`) with a `reserved` line (written at §2 step
4, before the start) or any of `started`, `unconfirmed`, `failed`. So a launch counts once, and a
host that crashed between the two lines still counts it. Refusals and `busy` do not count, so a
busy machine does not burn an agent's hour. Refusals are rate-limited separately (60 per agent per
hour; past that, one summary audit line per minute) so a looping agent cannot flood the audit log;
that refusal limit is kept in memory only and a restart resets it, which is intended.

**How long sessions live.** The caps are per hour, not on how many run at once, so two more
limits keep idle sessions from piling up: at most **6 live agent-launched sessions per machine**
(`limits.liveAgentLaunched`, the fleet's own worker count), and the host **ends an agent-launched
session idle with no person input for 2 hours** (`limits.agentLaunchedIdleHours`). Ending it is
the host's own upkeep, not an op an agent may call; it is audited, and a person's input to the
session cancels it for good.

**At the cap:** `outcome: refused`, `code: rateLimited`, `retryAfter` in seconds, and the
reason "This machine has started 10 agent launches in the last hour (its limit). The next one
can start in 14 minutes. A person can still launch from the app." The agent sees the per-agent
or per-machine number, whichever stopped it.

**Across restarts:** the windows are rebuilt at host start from the audit log (§6), not kept in
memory only as agent-principals.md:1138 allowed for messages. A launch is costly and the gate's
evidence (20 started, the 21st refused) must survive a restart.
* **What is read:** the tail of today's and yesterday's launch files only, never the whole log,
  so start-up stays quick.
* **Clock:** the window is judged on each line's wall-clock `at`. A line dated in the future (the
  clock stepped back) counts as if dated now, so a clock jump can neither open nor close the cap.
* **Damage:** a torn last line (power loss mid-append) is ignored, and its complete `reserved`
  line still counts. A bad line or a broken chain **inside the last hour** refuses agent launches
  (`capRebuilding`, with the reason); an older defect is a warning in `host.status` only, so one
  flipped byte from last month never disables launches.
* **Recovery:** the host retries the rebuild every 30 s, with the reason in `host.status`; no
  restart is needed.
* **After a reboot** the fleet relaunches its workers, and a second restart within the hour can
  reach the fleet cap and leave a machine short for up to an hour. That is intended; `retryAfter`
  and the cap's use are in `host.status`, and the runbook gives the owner's time-boxed override
  (below).

**Overrides.** `agent-bridge-host agents limits override <limit> <value> --for <minutes>` (at the
machine, refused inside agent sessions like every setting) raises or lowers a cap for at most 4
hours, then lapses by itself. It is audited and shown in `host.status`.

## 6. Audit

**Decision.** Launch lines go in the agent audit log that #1401 builds (it lands first), in a
**file of their own**, `<profile>/audit/launches-YYYY-MM-DD.jsonl`, owner-only, beside #1401's
`agents-YYYY-MM-DD.jsonl` for sends and reads (agent-principals.md §5.6). A flood of message
traffic or refusals can then never push out the launch evidence that the caps and the retirement
gate rest on.
* **Size:** two lines of about 0.7 KB per launch, so at every cap the launch file grows by tens
  of KB an hour, well under its own **16 MiB** quota for 30 days; refusals are already limited
  (§5).
* **Pruning:** whole daily files, oldest first, at host start and once a day, never per append;
  never a file that holds a line from the last two hours; a prune that fails warns in
  `host.status` and never blocks a launch. A day over the quota on its own keeps that day and
  warns.
* **A person may archive** the log (`agent-bridge-host audit archive`, at the machine); the
  archive is noted in the next line and in `host.status`.
* **One writer.** One host runs per profile, under its lock (`LocalApi.cs:106-128`), and a
  replacement starts only after the old host has let go (`LocalApi.cs:130-137`). The host also
  holds an exclusive lock on the audit folder while it appends, so two writers can never fork
  the chain or both take the last slot.
* **Format:** each line has `v: 1`. Unknown fields are ignored and an unknown major version is
  refused, so a host rolled back after an upgrade still reads newer lines.

Fields: `v`, `at` (UTC), `principal` (`agent:<machine>/<session>`, `AgentPrincipals.cs:27`), `scope`,
`depth`, `action: "session.launch"`, `request`, `machine`, `agent`, `workspace` (label),
`model`, `permissions`, `promptBytes`, `outcome`, `code`, `reason`, `session` (the new one, when
known), `grant` (`agent` or `fleet`). **Never** the prompt, the token or a file path beyond the
workspace label (#1401's rule). Every attempt gets a line: a refusal at any gate step is one
line with its `code`; a launch that passes writes `outcome: reserved` **before** it starts (§2
step 4), and the outcome is a second line with the same `request`.

**Tamper-evidence:** each line carries the SHA-256 of the previous line, and the chain runs on
across daily files: each file opens with a header line holding the hash of the previous file's
last line, so pruning or rotation never reads as a break. The host shows the chain's state in
`host.status` ("audit chain intact" or "broken at <time>"), verified at start and incrementally on
each append, fully once a day, and cached: a status call never re-reads the log. That detects an
edited or removed line. It does not stop a same-user program rewriting the whole file, which is
the accepted same-user limit (agent-principals.md §1.3).

**Reading it:** the app's agent activity view (later) and `agent-bridge-host audit --action
session.launch --since <time>`. The retirement-gate evidence is read with that command on each
of the four machines: 20 `started` lines in all (5 per machine, §10) with distinct `request`s,
and a 21st `refused` with
`code: rateLimited`.

## 7. What an agent sees

**Launch result:** `outcome`, `reason`, `code`, `retryAfter`, `session` (the new id), and
`launched` with `agent`, `workspace`, `worktree`, `model`, `effort`, `context`, `permissions`.
Not `cwd` (a path), `resume`, `transfer` or `profile` (v1-launch.md:133-137 lists them for person
clients).

**Reading a result back:** a repeat of the same `session.launch` with the same `request` returns
the remembered outcome and starts nothing (v1-launch.md:94). That is the readback; no new op.
Today the memory is keyed by `request` alone (`LaunchService.cs:570-571`), and the MCP server
keeps its own ledger (`mcp/src/tools.mjs:102-115`). #1413 keys it by (principal, `request`) for
agents, so another caller repeating an agent's request id gets `refused`, never that agent's
result, and makes `read_launch_result` repeat the request.

**Options:** an agent's `launch.options` lists only the agents and workspace labels it may use
(no `path`), no `resumable` and no `trust` questions (agent-principals.md §5.2 already drops
titles). **Busy:** an agent gets "a launch on this machine is waiting for a person at the
machine; try again in a few minutes", never the other session's agent, workspace or question
(v1-launch.md:53, :128).

## 8. Threat model

| Threat | Mitigation | Accepted residual |
|---|---|---|
| **Prompt injection makes an agent launch sessions** | Off by default (two keys, §1); ask-only, so every action of the new session still asks a person; approved workspaces only; caps 3 and 10 per hour, at most 6 live and idle ones ended after 2 h; full audit; the new session is `read` and cannot launch | Up to 10 unwanted ask-only sessions per machine per hour (6 live at once), each needing a person to approve anything; the injected prompt runs in the new session (it is the agent's own prompt) |
| **A stolen session token** | Accepted only from inside its own session's process tree, read in full (`AgentCredentials.cs:145-156`); dies with the session (`:157-162`); one per session (`:94-95`); revocable | **Safety rail, not a boundary** (agent-principals.md §3.4): a same-user process can spoof its parent or join the Job. Such a process can already do anything the user can |
| **Runaway fan-out** | One agent launch step at most (§4); per-agent and per-machine caps (§5) that survive restarts; refusals rate-limited | None beyond the caps |
| **The new session reads secrets in its folder** | Workspaces only from the "agents may launch here" list a person set at the machine; isolated workspaces start in a fresh worktree | The session runs as the user and can read what the user can, like any session. The list is what the owner controls |
| **Ask-only is weaker than it looks** | Ask flag forced, extra arguments refused, Agency refused, fresh worktree, and a worktree with a tool-approving project settings file refused (§3) | User-level agent settings outside the repository, and settings formats the host does not yet know (it refuses agents it cannot check). The full "provably forced-ask" launch (host-owned config, agent-principals.md §5.2) is AP8 |
| **The fleet grant is abused** (§11) | Only the dispatcher's script credential; the caller's ancestry must be read **in full** and contain no agent session, so a partial or untraceable ancestry, which today falls to `Other` (local-pipe.md:104), is refused; fleet-ticked isolated workspaces only; own cap; audit | Something that tricks the dispatcher (for example text in an issue it reads) can start an Allow-all worker in a fleet workspace, bounded by list and cap. The script credential sits in an owner-only file (#1402), which any same-user program can read: an agent that copies it and runs a parent-spoofed helper can reach the grant. That is the same-user limit (agent-principals.md §1.3), no worse than today's HA token, which every session's environment holds |
| **A side door: launching as the person from a script** | Closed for `session.launch` in #1404 (§2) | Other acting ops until AP8 |
| **Until #1404 and 2e ship** | Nothing new: agent launch stays refused (`LocalApi.cs:68-71`) | Today a same-user script can launch with Allow all through the pipe (`LocalApi.cs:68`), and anything holding the HA token can press Allow all on any machine. Both stay until #1404 and the switch-off of HA launch (#106, then S7) |

## 9. Cross-machine

**Decision: this machine only in v1.** The MCP server already ignores `machine`
(`mcp/src/server.mjs:36`; local-pipe.md:212-226). Cross-machine launch needs the signed
envelope, whose action set already includes `session.launch` (agent-principals.md §4.3), and that
waits for the device roster (#55), as the owner decided (agent-principals.md §1.1, decision 4).
The temporary own-account mode (AP5-lite) carries `agent.send` only and is not extended to launch.

**For the HA retirement:** HA launches across machines today (the dispatcher presses each
machine's `new_session` button from one place). Until #55, **one dispatcher still decides, and a
small launcher on each machine carries out its own assignments**:
* The dispatcher stays where it is and keeps choosing which item goes to which machine, so there
  is no claim race between machines. It writes each assignment (item, machine, a `request` UUID
  made once per item and machine) to the fleet repository, which every machine already reads with
  its own git credentials.
* Each machine's launcher reads only its own assignments, launches through its local pipe with
  the fleet grant and that `request`, and writes the outcome back. A repeat of the same `request`
  starts nothing (§7), so a launcher that crashes and retries cannot start an item twice. An
  assignment not taken up within its time limit (a machine asleep or rebooting) goes back to the
  dispatcher.
* No machine adds GitHub REST polling: the launcher reads by `git fetch`, and the dispatcher's
  own calls do not change. Machines without the GitHub App's private key need no token.
* A launcher on a host without the fleet grant (an older version) launches nothing there and says
  so on the board. It never falls back to HA or to the unattributed pipe path. Rollout is one
  machine first, with a kill switch for the launchers like the event pump's.

This makes #106 S-M (about 2 days) instead of S. Four dispatchers on a shared queue were
rejected: they need an exactly-once claim, a GitHub token on every machine and more REST calls.

## 10. Rollout and tests

| Slice | Contents | Size | Tests (fail-closed cases named) |
|---|---|---|---|
| **#1401** audit log (first) | as filed, plus the launch file, hash chain across files, line version, folder lock, pruning rules (§6) and the `audit` command | M | an unwritable log refuses the action; a removed line shows "broken"; pruning and rotation keep the chain intact; a torn last line is ignored; a newer major version is refused, unknown fields ignored; a second writer is refused |
| **2b #1404** scope and enforcement | `agentScope` on person launches; `agents.launch` policy; `AgentLaunchGate` (§2, §3); refuse `session.launch` from `Other`; MCP schema trimmed; agent prompt wording | M-L (about 380) | refused: no scope, scope revoked mid-session, switch off, policy unreadable, `permissions` absent, `allowAll`, `transfer`, `resume`, attachments, workspace not listed, workspace not isolated, worktree with a tool-approving settings file, Agency, `auto`, extra arguments, `agentScope` from an agent, launcher at depth 1, an adopted session, a script caller, two launches racing for the last slot; accepted: ask-only launch in a listed workspace; 16 KiB prompt whole, 16 KiB + 1 refused; child credential is `read` |
| **2c #1413** cap, result, options | caps rebuilt from the audit at start; result keyed by principal; agent projection of options and busy | M-L (about 380; may split the live limit and idle end off) | 4th launch by one agent refused; 11th on the machine refused; 7th live agent-launched session refused; idle session ended after 2 h, and not after a person's input; cap survives a host restart; a crash between `reserved` and the outcome still counts; a future-dated line counts; a defect inside the hour refuses with `capRebuilding`, an older one warns; a locked policy file retried, then `policyUnreadable`; override lapses by itself; another caller's repeat of the request id never gets the result; options carry no path, resumable or trust |
| **2e (new)** fleet launch grant | the grant in §11 on the script credential | S (about 120), after #1414 and #1404 | refused: from inside an agent session, workspace not fleet-ticked, cap 9th, under 1 GiB free (`diskLow`), scope above `read+reply` for the worker |
| **2d fleet #106** dispatcher | central dispatcher plus per-machine launchers (§9) through `session.launch` with the fleet grant; result and options from the call; kill switch; one machine first | S-M (about 2 days) | as filed, plus: a retried assignment starts nothing twice; an assignment on a sleeping machine returns; a host without the grant launches nothing and never falls back |

**Quality acceptance run** (four Windows machines, after 2c): set
`launchesPerMachinePerHour` and `launchesPerOriginPerHour` to 5 on each machine with the
time-boxed override (§5), which lapses by itself and is audited. One coordinator session with
launch scope on each machine launches 5 ask-only sessions there: 20 `started` in all. The 21st, a
sixth launch on any one machine, is `refused` `rateLimited`. A person then restarts that host
(agents cannot), and the next launch there is still refused. The default per-agent cap of 3 is
tested separately in 2c. **Teardown:** the run lead ends the 20 sessions from the app (agents
cannot stop sessions in v1); any left over are ended by the 2-hour idle rule. **Close-out:** read
`host.status` on each machine to show the overrides lapsed and the limits are back to 3, 10
and 8. An agent
without the scope, an `allowAll` launch and an unlisted workspace are each refused. Evidence: the
`audit` command's output from each machine, with request ids matching the launching session's
log. The fleet run (#106: 20 worker launches, zero through HA) follows 2e separately.

## 11. Fleet workers: the fleet launch grant

**Recommended.** The dispatcher is a scheduled script, not a session, so it has no agent
credential. It gets the script credential that #1402 designs (#1414 builds), with one extra
grant, `launch:fleet`, that only a person at the machine turns on. With it, `session.launch` may
carry `permissions: allowAll`, only when all hold:
* the caller presents the script credential, its ancestry was read **in full**, and no process in
  it is an agent session (the reverse of `agent.hello`'s rule, local-pipe.md:101); an ancestry read
  only in part is a refusal, never `Other`;
* the workspace is on `agents.fleet.workspaces` ("fleet workers may run here", default empty),
  and it is an isolated workspace, so each worker gets a fresh worktree;
* the machine's fleet cap has room (§5), and the attempt is audited with `grant: "fleet"`;
* §3's other rules hold (no transfer, resume, attachments, extra arguments, or `agentScope`).

The worker's credential is `read+reply`. This keeps #1402's script credential "send and read, no
launch" by default: launch is a separate grant on it, not a new credential kind, and is off
until the owner ticks it. **Cost:** about 1 day of host work (2e), and the residual in §8. It
is narrower than today, where any holder of the HA token can press Allow all on any machine.

**Alternative: force ask-only for workers.** No extra grant, and every launch is ask-only. Cost:
workers stop at every tool prompt (shell, edit, git), so the fleet is no longer unattended until
workers are reworked to run with per-repository allowed-tool lists, which the forced-ask design
later turns off (agent-principals.md §5.2); several weeks, or the owner approves prompts all day.

Until 2e lands the dispatcher keeps launching through HA; nothing about today's fleet changes.

**No fallback once HA launch is retired (S7).** Failing closed (§2) then means a full disk or a
locked audit file stops every unattended worker launch on that machine until it is fixed. That is
accepted: the alternative is launches nobody can account for. To make it rare and visible, the
host refuses to turn the fleet grant on, and refuses fleet launches, with under 1 GiB free on the
profile's disk (`code: diskLow`), and `host.status` shows it. The runbook gets one line each for
`diskLow`, `auditUnwritable`, `capRebuilding` and the recovery override (§5).

## 12. Open questions for the owner

Each has a recommended default; the summary's list is the same.

1. Fleet workers: fleet launch grant (§11) **(recommended)**, or force ask-only.
2. Who grants launch scope: a person, per session, at launch, on machines with the switch on
   **(recommended)**; or every session a person launches on such a machine.
3. Limits: 3 per agent session and 10 per machine per hour; fleet grant 8 per machine per
   hour; at most 6 live agent-started sessions per machine, ended after 2 hours idle
   **(recommended)**.
4. Prompts over 16 KiB: refused, with "put it in a file" **(recommended)**; or raise the limit
   with a prompt-file channel (about 2 more days, new content path).
5. Cross-machine: this machine only until #55, with a central dispatcher and a launcher on
   each machine for the fleet **(recommended)**; or extend the temporary own-account mode to
   launch now.
6. Sessions agents start: read only, no launch, agents cannot resume **(recommended)**.
7. Audit: launches in their own file, 30 days or 16 MiB per machine, no prompt text
   **(recommended)**; or keep prompts too.
