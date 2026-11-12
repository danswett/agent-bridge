# Agent launch scope: ask-only launches by agents, with an hourly cap and an audit (#1403)

**Status: design for the owner's sign-off. No code changes with this note.** Slice 2a of the
Home Assistant retirement epic (#1396). It decides what slices 2b (#1404), 2c (#1413) and 2d
(the fleet repository's #106) build. It narrows [agent-principals.md](agent-principals.md) §3 and §5
for the first version; where the two disagree for v1, this note wins, and agent-principals.md
stays the long-term design. Times are Pacific.

**Pending-launch extension (#2357 / #1472):** §13 proposes the pre-session Needs-you
record and typed `needsPerson` result. It is docs only, not a new permission grant;
its host and client code slices wait on #1413 C1. The earlier scope rules remain.

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
   agent-started sessions per machine, ended after 2 hours idle; fleet workers have their own 6
   and are not ended for being idle.** You can raise a limit at the machine for up to 4 hours,
   for example to recover after a reboot.
4. Long prompts: **keep the 16 KiB limit and refuse longer ones** with a reason; a longer
   handover goes in a file the prompt points to.
5. Other machines: **this machine only** until device approval (#55). Meanwhile the
   dispatcher still decides centrally, and a small launcher on each machine starts its own
   workers.
6. Sessions an agent starts: **read only, cannot launch, cannot be resumed by agents** (fleet
   workers may also send messages, so they can report back).
7. Audit: **launches in a file of their own, kept 30 days or 16 MiB on each machine, no prompt
   text**, readable in the app and by a command.
8. What "ask-only" means at first: **the new session is started without the skip-permissions
   switch, in a clean worktree with no agent settings of the repository's own; your own agent
   settings still apply.** The stricter version, where the host also controls every agent setting,
   comes later (AP8). Recommended: start with this; or wait for the stricter one.

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
agents exactly as today; §13 separately proposes only an origin-bound withdrawal
exception, never Answer/Dismiss. The gate runs, in order, refusing at the first failure:

0. a `request` this principal has used before never starts a second launch. The gate checks the
   launch service's memory (`LaunchServiceOptions.RememberFor`, 25 minutes) **and** the set of
   requests rebuilt from the audit, which reads today's and yesterday's launch files **in
   full** (§5), so at least 24 hours. Identical or not is judged by `requestSha256` (§6), the
   SHA-256 of the request's normalised launch fields, prompt included, recorded on each line;
   the prompt itself is never recorded. A repeat with the same hash gets the recorded outcome
   (`unconfirmed` when only `reserved` or `interrupted` is recorded; §13's typed
   `needsPerson` when a startup question is positively observed); one with a different hash
   is refused (`requestReused`) and **counts against the attempt limit** (step 4), so a loop of
   mismatched repeats is limited like any other. Neither is counted against the caps or started
   again;
1. the credential is live and its scope is `read+reply+launch` (re-read on every call, so a
   revocation takes effect at once);
2. the machine policy is readable and `agents.launch.enabled` is `true`;
3. the request passes §3's content rules;
4. the attempt limit (§5) has room;
5. under one lock: the cap (§5) has room, a slot is reserved, and the audit line for the attempt
   (§6, `outcome: reserved`) is written. Two launches at once can never both take the last slot;
   an audit line that cannot be written releases the slot and refuses;

then the request goes to the launch service with the agent principal as its peer, which applies
its own checks unchanged ([docs/host-launch.md](../host-launch.md), `LaunchProtocol.Check`), plus one new check
**inside** the launch service: the worktree check of §3, which can only run once the worktree
exists, after `Worktrees.Create` in `LaunchService.StartAsync` and before the agent's process starts.
A failing worktree check removes the new worktree and refuses. **The reserved slot is released**
when the launch service answers `busy` or `refused` (nothing started), and kept for `started`,
`unconfirmed` and `failed`. A refusal at any step is written to the audit log too (§6).

**Fail closed.** An unreadable or malformed `agents` policy, an unreadable credential store
(already: `AgentCredentials.cs:132`), caps not yet rebuilt from the audit (§5) or an audit log
that cannot be written each refuse the launch. Unreadable is never read as allowed, the same
posture as the credential store and [docs/host-config.md](../host-config.md). In operation:
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
fleet must not move onto it. #1404 refuses `session.launch` from `CallerKind.Other`; the app (a
person client) keeps it. agent-principals.md §3.4 plans an `agent-bridge-host launch` command as
an exception, but it does not exist yet, so after #1404 a person at the machine launches from the
app, and a script that launches today must move to a credential first (the dispatcher: the fleet
grant, §11). #1404 logs each refused `Other` launch with the caller, so anything still using the
side door shows up before it is closed on the fleet. The other acting ops follow in AP8 as
planned.

## 3. What an agent launch may contain

Checked by `AgentLaunchGate` before the launch service sees it:

| Field | Rule for an agent | Why |
|---|---|---|
| machine | **this machine only**: the gate serves agent launches on the local pipe alone, and `session.launch` has no machine field (v1-launch.md:76-103); an agent launch arriving any other way is refused | Cross-machine waits for #55 (§9) |
| `permissions` | **must be present and `ask`**. Absent or `allowAll` is refused | Absent would take the machine default, which may be `allowAll` (v1-launch.md:101) |
| `transfer` | refused | Transfers need a person (agent-principals.md §5.2) |
| `resume` | **refused in v1** | Resuming types an agent's prompt into an existing conversation, possibly a person's; new sessions only keeps it simple |
| `attachments` | refused | `attachment.*` is already refused for agents (`LocalApi.cs:23`, `:68`) |
| `workspace` | required, must be on the machine's `agents.launch.workspaces` list (labels a person ticked "agents may launch here", default empty), and must be an isolated workspace, so each launch gets a fresh worktree. **The new worktree must hold none of the chosen agent's project configuration** (the worktree check, §2): presence alone refuses, whatever the contents, so it is an allow-list of "clean", not a deny-list of bad settings | A folder a person approved at that machine for this purpose. Without the check an agent could plant settings (Claude's `.claude/` can set a bypass default mode, hooks, an API key helper or MCP servers) and then launch over them |
| `agent` | required, from `launch.options`, on `agents.launch.agents`; `auto` and Agency refused; only an agent with a confirmed project-configuration list (below) | Agency adds profile config the host cannot show keeps ask (agent-principals.md:834-836) |
| `profile` | refused | Agency only, which is refused |
| `model`, `effort`, `context` | from `launch.options` (existing check, [docs/host-launch.md](../host-launch.md)) | |
| extra arguments | a launcher with any `launch.agents.<id>.extraArgs` is refused for agents | An extra argument can grant Allow all ([docs/host-launch.md](../host-launch.md)). `LaunchService.LaunchVerificationAsync` already refuses them |
| `agentScope` | refused | Only a person sets scope (§1, §4) |
| `prompt` | at most **16 KiB** of UTF-8; longer is refused (below) | |

**What "ask-only" means in v1, plainly.** No bypass flag (the host adds `--allow-all`,
`--dangerously-skip-permissions` or `--ask-for-approval never` only for `allowAll`,
`Launchers.cs:143`, `:156`, `:170`), no extra arguments, no Agency, and no agent project
configuration in the new worktree. It is **not** agent-principals.md's provably forced-ask launch
(agent-principals.md:828-833: a host-owned configuration home, a scrubbed environment, no managed
policy), which comes with AP8. The user's own agent settings (in the user's profile, not the
repository) still apply, as they do to every session the person launches. Shipping v1 on this
narrower meaning is an owner decision (§12, question 8).

**Which agents an agent may launch in v1.** Each needs a list of the repository paths through
which it reads permissions, hooks, tools or MCP servers, confirmed by #1404 from the agent's own
documentation and `--help` and kept as test vectors (as [docs/host-launch.md](../host-launch.md) did for extra
arguments). Expected: **Claude** (`.claude/`, `.mcp.json`) and **Copilot** (its repository agent,
hook and MCP configuration, list to be confirmed). **Codex** waits until its list is confirmed. An
agent without a confirmed list is refused for agent launches.

**#1447 W1 implementation (not yet wired).** `AgentLaunchWorktreeCheck` is a read-only
check of the created worktree: host marker, matching repository and approved subdirectory,
detached HEAD, and clean state including ignored files. It refuses filesystem links rather
than following them during configuration inspection. Claude's confirmed project locations
are `.claude/` (shared and local settings, hooks and plugins) and `.mcp.json`; presence
anywhere in the worktree refuses, regardless of contents. Main-checkout configuration is
also refused because Claude can inherit its local settings. Sources:
[settings files and worktree inheritance](https://code.claude.com/docs/en/settings),
[MCP scopes](https://code.claude.com/docs/en/mcp), and Claude Code 2.1.322 `--help`
(`--settings` and project/local settings controls). Copilot, Codex, Agency and unknown
kinds remain refused until their lists are confirmed. User-profile settings are unchanged.
W2 must call the check before process start and handle refusal audit, removal of only
the newly created worktree, and slot release; W1 does none of those and enables no launches.

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
refused, a 300-character prompt arrives whole) for an agent run directly; for an agent installed
through npm, which runs through `cmd.exe`, the 8,191-character command line is smaller and wins,
and the test asserts the host's "too long for the command line" refusal instead. That settles #1404's and #1413's "up to the
length #1403 decides". The MCP server needs no change to its cap; #1404 removes `Allow all`
from `launch_agent_session`'s schema (`mcp/src/server.mjs:45`; `mcp/src/tools.mjs:6`) and its
`profile` and `resume` arguments, so an agent is not offered what the host will refuse.

## 4. The launched session's own scope

**Decision.** A session started by an agent gets `read`, and nothing in the request can change
that. Its scope is the narrowest of: `read`, the machine's `agents.issue.launched`, and its
launcher's scope. It can never launch (`allowAgentLaunchedToLaunch` fixed at `false` in v1,
[agent-principals.md](agent-principals.md) §5.4 / §5.5).

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

**Decision.** Sliding one-hour windows, the agent ones from [agent-principals.md](agent-principals.md) §5.4:

| Limit | Default | Counted per |
|---|---|---|
| `limits.launchesPerOriginPerHour` | **3** | launching agent session |
| `limits.launchesPerMachinePerHour` | **10** | this machine, all agents together |
| `limits.fleetLaunchesPerMachinePerHour` | **8** | this machine, the fleet grant (§11); the dispatcher allows 6 workers per machine. Separate from the agent caps: fleet launches do not use up agents' 10, nor agents the fleet's 8 |

**What counts:** one charge per distinct (origin principal, launch `request`) in the
launch-state ledger. A `session.launch` reservation charges it; only a verified **pre-start**
launch outcome of `busy` or `refused` releases it (§2). Post-start `needsPerson`, Answer,
Cancel, withdrawal, expiry and any refused action never release a launch charge.
Rebuild by folding typed **launch-state transitions**, not the last audit line for a request.
Legacy `session.launch` reserve/outcome pairs retain that meaning; §13 action/refusal
events cannot alter it. An interrupted reservation stays charged, and malformed or missing
transition evidence fails closed with `capRebuilding`, never an inferred refund.
A `reserved` line with no outcome found
at host start gets an `outcome: interrupted` line ("the host stopped before this launch
answered"), keeps counting, and a repeat of that `request` returns `unconfirmed`, since something
may have started. **Pre-start launch** refusals and `busy` do not count, so a
busy machine does not burn an agent's hour; later action refusals leave its charge intact.
Refusals are rate-limited separately (60 per agent per
hour; past that, one summary audit line per minute) so a looping agent cannot flood the audit log;
that refusal limit is kept in memory only and a restart resets it, which is intended. Separately,
an **attempt limit** of **20 per agent per hour, any outcome**
(`limits.launchAttemptsPerOriginPerHour`, §2 step 4) stops an agent retrying `busy` or refused
launches in a loop and crowding out people's launches; it is rebuilt from the audit like the
caps.

**How long sessions live.** The caps are per hour, not on how many run at once, so two more
limits keep idle sessions from piling up: at most **6 live agent-launched sessions per machine**
(`limits.liveAgentLaunched`), and the host **ends an agent-launched session idle with no person
input for 2 hours** (`limits.agentLaunchedIdleHours`). Ending it is the host's own upkeep, not an
op an agent may call; it is audited, and a person's input to the session cancels it for good.
**Fleet workers are counted apart:** they have their own live limit, **6 per machine**
(`limits.liveFleetWorkers`, the dispatcher's own worker count), so workers never block
coordinators' launches or the reverse; and the 2-hour idle end does **not** apply to them, since an
unattended worker waiting on its build is not abandoned. The dispatcher ends its workers, as it
does today.

**At the cap:** `outcome: refused`, `code: rateLimited`, `retryAfter` in seconds, and the
reason "This machine has started 10 agent launches in the last hour (its limit). The next one
can start in 14 minutes. A person can still launch from the app." The agent sees the per-agent
or per-machine number, whichever stopped it.

**Across restarts:** the windows are rebuilt at host start from the audit log (§6), not kept in
memory only as [agent-principals.md](agent-principals.md) §5.5 allowed for messages. A launch is costly and the gate's
evidence (on each machine, 5 started and a sixth refused during the acceptance run, §10) must
survive a restart.
* **What is read:** today's and yesterday's launch files, in full (tens of KB, §6), and nothing
  of the rest of the log, so start-up stays quick. The caps use the last hour of them; the
  request-id set (§2 step 0) uses all of them.
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
hours, then lapses by itself. It is stored with its expiry in the machine's `config.json`
(`agents.limits.overrides`), so it **survives a restart** and still lapses on time; it is audited
and shown in `host.status`. Every launch line and every `rateLimited` refusal records the
**effective** limit and whether an override set it (`limit`, `overridden`), so the evidence shows
which number was in force.

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
`model`, `permissions`, `promptBytes`, `requestSha256` (§2 step 0; a hash of the normalised
launch fields, never the prompt's text), `outcome`, `code`, `reason`, `session` (the new one, when
known), `grant` (`agent` or `fleet`), `limit` and `overridden` (§5). **Never** the prompt, the token or a file path beyond the
workspace label (#1401's rule). Every attempt gets a line: a refusal at any gate step is one
line with its `code`; a launch that passes writes `outcome: reserved` **before** it starts (§2
step 5), and the outcome is a second line with the same `request`.

**Pending extension (§13):** versioned `event` distinguishes `launch-state`,
`pending-action` and `pending-refusal`; only the first changes the cap ledger. Include
`pending` (UUID), `questionKind`, `question` (UUID or absent), `revision` (integer),
`actor` (verified principal), `actionRequest` (UUID), `launchState`, `processStarted`
(sticky boolean) and `closed` (verified boolean), plus a fixed refusal `code`.
`closed: false` covers expiry, uncertainty and any still-open process. Pre-session
records have no required session id. Use a new major version before these event
semantics: new readers fold legacy v1 launch pairs; older readers reject new records
instead of ignoring `event` and applying unsafe last-line counts. Filter evidence
by `event: launch-state` as well as action/outcome. Unknown fields confer no authority.
Pending action refusals share §5's 60-per-actor/hour launch-related refusal budget and
one-per-minute summary after it; uncredentialed callers share one host bucket.
Proposed actor table: 256 verified actors, overflow sharing the host bucket; expire
hourly entries and never key by attacker text. Pending refusals also share a host-wide
60/hour line budget, then the same one/minute count summary.
Summaries retain counts/codes, never question, answer or unvalidated request content.

**Tamper-evidence:** each line carries the SHA-256 of the previous line, and the chain runs on
across daily files: each file opens with a header line holding the hash of the previous file's
last line, so pruning or rotation never reads as a break. The host shows the chain's state in
`host.status` ("audit chain intact" or "broken at <time>"), verified at start and incrementally on
each append, fully once a day, and cached: a status call never re-reads the log. That detects an
edited or removed line. It does not stop a same-user program rewriting the whole file, which is
the accepted same-user limit (agent-principals.md §1.3).

**Reading it:** the app's agent activity view (later) and `agent-bridge-host audit --action
session.launch --outcome <outcome> --since <time>`. Each launch writes two lines (`reserved`, then
its outcome), so the evidence filters by outcome: on the four machines together, `--outcome
started` shows 20 lines (5 per machine, §10) with distinct `request`s, and `--outcome refused`
shows the 21st with `code: rateLimited`. The rebuild and the command read the previous UTC day's
file as well, so an hour that spans midnight is whole.

## 7. What an agent sees

**Launch result:** `outcome`, `reason`, `code`, `retryAfter`, `session` (the new id), and
`launched` with `agent`, `workspace`, `worktree`, `model`, `effort`, `context`, `permissions`.
Not `cwd` (a path), `resume`, `transfer` or `profile` (v1-launch.md:133-137 lists them for person
clients). §13 adds typed pending identity and machine fields for the origin's own launch,
not question text or answer authority. `unconfirmed` still means uncertainty, never permission
to retry; a positively observed startup question becomes `needsPerson`.

**Reading a result back:** a repeat of the same `session.launch` with the same `request` returns
the remembered outcome and starts nothing (v1-launch.md:94). That is the readback; no new op.
Today `LaunchService.LaunchAsync` remembers by request key, and the MCP server
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
| **The fleet grant is abused** (§11) | Only the dispatcher's script credential; the caller's ancestry must be read **in full** and contain no agent session, so a partial or untraceable ancestry, which today falls to `Other` (local-pipe.md:104), is refused. **This rule is not a barrier:** a detached process whose parent has exited has a complete, agent-free ancestry (local-pipe.md:106-110), so the credential file is the only real barrier; fleet-ticked isolated workspaces only; own cap; audit | Something that tricks the dispatcher (for example text in an issue it reads) can start an Allow-all worker in a fleet workspace, bounded by list and cap. The script credential sits in an owner-only file (#1402), which any same-user program can read: an agent that copies it and runs a parent-spoofed helper can reach the grant. That is the same-user limit (agent-principals.md §1.3), no worse than today's HA token, which every session's environment holds |
| **Launch loops starve people** (busy or refused attempts are not capped) | Attempt limit of 20 per agent per hour, any outcome (§5); a refused launch removes its worktree, so it cannot fill the per-repository worktree cap | A looping agent can take 20 attempts an hour |
| **A prompt-injected agent edits the stored overrides** (§5) | The host clamps each stored override to its 4-hour maximum from the time it was set, refuses values outside each limit's range, and audits any override that changed outside the CLI, when it loads the config and when the file changes | `config.json` is the user's file, so a same-user agent with a file tool can still set a value within range for up to 4 hours. That is the same-user limit (agent-principals.md §1.3); the audit line shows it |
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
  assignment may go back to the dispatcher only after its old launcher proves no process
  started, a person cancels it, or its authenticated origin withdraws it with `closed: true`
  (§13). Missing acknowledgement, sleep, reboot, tracking expiry or removal of an assignment
  file is not that proof; uncertain claims remain visible and cannot be reassigned.
* No machine adds GitHub REST polling: the launcher reads by `git fetch`, and the dispatcher's
  own calls do not change. Machines without the GitHub App's private key need no token.
* A launcher on a host without the fleet grant (an older version) launches nothing there and says
  so on the board. It never falls back to HA or to the unattributed pipe path. Rollout is one
  machine first, with a kill switch for the launchers like the event pump's.
* A launcher that cannot fetch the fleet repository for 10 minutes starts nothing new and shows
  `fetchStale` on the board and in its log. Assignments are written only when the dispatcher
  places work (at most its 3 launches per cycle), and an assignment's file is removed once its
  outcome is read back, so the assignments stay a handful of small files, not a growing log.

This makes #106 S-M (about 2 days) instead of S. Four dispatchers on a shared queue were
rejected: they need an exactly-once claim, a GitHub token on every machine and more REST calls.

## 10. Rollout and tests

| Slice | Contents | Size | Tests (fail-closed cases named) |
|---|---|---|---|
| **#1401** audit log (first) | as filed, plus the launch file, hash chain across files, line version, folder lock, pruning rules (§6), and the `audit` command with `--outcome` and `audit archive` | M | an unwritable log refuses the action; a removed line shows "broken"; pruning and rotation keep the chain intact; a torn last line is ignored; a newer major version is refused, unknown fields ignored; a second writer is refused |
| **2b #1404** scope and enforcement | `agentScope` on person launches; the `agents.launch` and `agents.limits` config keys; `agent-bridge-host agents revoke`; `AgentLaunchGate` (§2, §3) with the worktree check and per-agent configuration lists; **in-memory caps and attempt limit with the §5 defaults, and step 0 remembering every (principal, `request`) with its hash for at least the hour**, so agents are never uncapped between 2b and 2c; refuse `session.launch` from `Other`; MCP schema trimmed; agent prompt wording | L (about 450; may split the worktree check off) | refused: no scope, scope revoked mid-session, switch off, policy unreadable, `permissions` absent, `allowAll`, `transfer`, `resume`, attachments, workspace not listed, workspace not isolated, worktree with a tool-approving settings file, Agency, `auto`, extra arguments, `agentScope` from an agent, launcher at depth 1, an adopted session, a script caller, two launches racing for the last slot; worktree holding `.claude/` or `.mcp.json` refused and the worktree removed, an agent kind without a confirmed list, a slot released after `busy` and after `refused`, the 21st attempt in an hour, the same request 16 minutes later starts nothing and counts once, a `requestReused` loop stopped by the attempt limit; accepted: ask-only launch in a listed workspace; 16 KiB prompt whole, 16 KiB + 1 refused, the npm agent's command-line refusal; child credential is `read` |
| **2c #1413** cap, result, options | caps and attempt limit made durable (typed launch-state fold at start, `interrupted` lines, 30 s rebuild retry); request ids and `requestSha256` across restart (§2); agent live limit and idle end; stored, clamped, range-checked overrides, outside-CLI changes audited; fail-closed `host.status`/cap use; principal-keyed result/options/busy; runbook lines. C1 is the prerequisite for §13, not its implementation | M-L (about 380; may split live limit/idle end) | 4th launch by one agent refused; 11th on machine refused; 7th live agent-launched session refused; idle ended after 2 h, not after person input; cap survives restart; crash between reserved/outcome still counts; future line counts; defect inside hour refuses capRebuilding, older warns; locked policy retried then policyUnreadable; override lapses; different principal never gets readback; 20-hour request starts nothing; far-future/out-of-range override clamped/refused and audited; pre-start refused/busy does not count after restart; override survives restart and refusal records effective limit; options have no path/resumable/trust or pending text for agents; action/refusal lines never refund; old schema readers fail closed on new event semantics |
| **2e (new)** fleet launch grant | the grant in §11 on the script credential: a new branch in `LocalApi.Handler` for script-credential connections (the agent gate sits only on `CallerKind.Agent`, `LocalApi.cs:68`), the fleet cap, the fleet live limit (`limits.liveFleetWorkers`) and the idle-end exemption, the `diskLow` check, and the runbook line for `diskLow` | S (about 120), after #1414 and #1404 | refused: from inside an agent session, workspace not fleet-ticked, cap 9th, under 1 GiB free (`diskLow`), scope above `read+reply` for the worker; the 7th live fleet worker refused while coordinators still launch; a fleet worker is not ended at 2 h idle |
| **2f (new)** app controls | the app's Settings ticks ("agents may launch here", "fleet workers may run here", the machine switch), the "may start agents" tick on Launch, and "Stop this session starting agents"; until then the owner uses `agent-bridge-host config` and `agents revoke` | S-M, after #1404 | each tick writes only its key; refused inside agent sessions |
| **2d fleet #106** dispatcher | central dispatcher plus per-machine launchers (§9) through `session.launch` with the fleet grant; result/options, kill switch, one machine first; §13 origin withdrawal only after its prerequisites | S-M (about 2 days) | retry starts nothing twice; sleeping/uncertain assignment is not reassigned without no-start/verified-close proof; authenticated origin withdrawal precedes target change; ready/unrelated launch never stopped; no grant means no launch/fallback |

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

**Pending withdrawal addition (proposed, §13):** `launch:fleet` includes readback and
withdrawal of that script principal's **own unready launch only**, on its local host.
It grants no Answer, Cancel-as-person, ready-session stop or withdrawal of another
principal's request. The script's allow-list must explicitly add this op after validated
origin/grant checks; the current read-only script connection cannot call it.

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
   hour; at most 6 live agent-started sessions per machine, ended after 2 hours idle; fleet
   workers a separate 6 live, not ended for idling **(recommended)**.
4. Prompts over 16 KiB: refused, with "put it in a file" **(recommended)**; or raise the limit
   with a prompt-file channel (about 2 more days, new content path).
5. Cross-machine: this machine only until #55, with a central dispatcher and a launcher on
   each machine for the fleet **(recommended)**; or extend the temporary own-account mode to
   launch now.
6. Sessions agents start: read only, no launch, agents cannot resume **(recommended)**.
7. Audit: launches in their own file, 30 days or 16 MiB per machine, no prompt text
   **(recommended)**; or keep prompts too.
8. Ask-only in v1 means no bypass flag, no extra arguments, no Agency and no agent project
   configuration in the worktree, with the user's own agent settings still applying
   **(recommended)**; or wait for AP8's provably forced-ask launch before any agent launch.

## 13. Pending launches and Needs you (#2357 / #1472)

**Status: proposed design extension, not implemented.** This covers a host-owned launch
waiting before its session is ready, even when no session exists. Ask-only and the
person-only trust boundary stay. #149's question kinds stay distinct. No paging, owner
alerts, HA fallback, live bridge, host setup or credential changes are in this docs slice.

### 13.1 Placement, identity and bounds

Each participating **host** owns its process and pending truth under its profile lock,
as in [client-protocol.md](client-protocol.md#placement-and-state). No dedicated fleet
machine, transfer of records or relay-presence reconstruction. Losing that host means
unavailable, not closed; another host cannot launch its replacement on that evidence.

Create a versioned, atomically replaced, flushed private journal after the audited
reservation and **before process start**. Owner-only protection follows the credential
store; question snapshots travel only on the paired encrypted host channel. No workspace,
fleet git file, registry metadata or client cache is authoritative.
Key one record by `(relayAccount, origin, request)` and §2's normalized request hash.
`relayAccount` is the host's authenticated relay/roster tenancy, **not** the Copilot/provider
login checked by `LaunchService.CheckAccount`. The host derives account, origin and device identity;
caller-supplied identities confer nothing. Identical request bytes read the current
result, even after restart; changed bytes are `requestReused`, not a new launch.

| Stored field | Meaning |
|---|---|
| `v`, `pending`, `request`, `requestSha256` | Journal version, host-generated pending UUID, original launch UUID and normalized request hash |
| `relayAccount`, `origin`, `machine` | Immutable authenticated tenancy, principal kind/id and credential binding, local host device id; no display-name routing |
| `agent`, `workspace`, `startedAt`, `expiresAt`, `clockFloorUtc` | Settled labels; UTC launch time, current answer deadline and durable clock high-water mark |
| `state`, `revision`, `processStarted`, `closed` | Starting/needsPerson/answering/expired/closing/reconciliationRequired/terminal state; monotonic revision; sticky start evidence; verified closure |
| `question` | UUID, kind, observation time, full-screen digest, exact safe warning/choices and full trust folder/root scope; never approving truncated text |
| `process`, `session` | Private PID **and start time**, verified owned launch-process set and provisional/ready session id; no process details to origins |
| `action` | Action UUID, verified actor, question/revision/choice digest, durable claim and result; no secret, credential or raw answer |

`starting` is not a Needs-you question. Positive question observation produces
`needsPerson`; registration alone never proves readiness. Ready moves to `started` and
its real session. Verified explicit closure produces `dismissed` or `withdrawn`,
`closed: true`. **Expiry ends the answer window, never kills the process**: keep
`expired, closed: false`, its card and reservation until readiness or verified closure.

**Existing behavior:** `LaunchServiceOptions` follows ordinary startup for 10 minutes,
remote-answerable trust for 30; `FollowAsync` stops watching, not the process, at expiry.
The 60-second registration wait is not a sign-in deadline. `TrustBusyFor` is a separate
120-second trust/bypass busy interval, currently based on the first question; HooksReview
is not that serialization. Do not turn any of those into a two-minute automatic kill.
**Proposed change:** `expiresAt` is the current follow/answer deadline; a genuinely new
question renews it by the applicable 10/30-minute follow period from observation.
Rereads, repeat launches and action refusals never renew it. Old question ids expire.
Renewal is a deliberate change from today's first-question timing, not an existing rule;
the busy interval remains unchanged pending resolution of the "newest" wording in
[docs/protocol/v1-launch.md](../protocol/v1-launch.md).

**Proposed tuning, not approved policy:** four unresolved records across this profile/
relay tenancy, each at most 8 KiB encoded payload, total 32 KiB; the complete protected
journal file is capped at 64 KiB including envelope/header. This is a new resource ceiling,
not a claim of one-record busy serialization. Starting, expired, closing and uncertain
records consume slots. Ready/verified-closed records leave the active set; §5's hourly
launch charge remains. No unresolved eviction, refund or account-change migration.
After account change, quarantine old evidence and block adoption until reconciled.
Bound all variable fields: at most 16 choices, 256 UTF-8 bytes each, full scope at most
4 KiB and display warning at most 2 KiB, **also** subject to the 8 KiB total.
Oversize/incomplete/secret-bearing observations retain only safe identity/digests and a
fixed read-only reason. Never collect sign-in passwords, tokens or device codes.

Action replay is a **separate** protected store: proposed 256 entries / 512 KiB total,
at most 2 KiB per result, plus bounded durable nonce/floor state required by
[devices.md](../protocol/devices.md#vetoes-person-commands-and-head-attestations).
Discard a result only after its replay freshness window and durable floor reject it;
full storage refuses effects, not replay protection. Compact terminal identity/result
evidence retains §6's 24-hour-minimum request readback. Unresolved identity outlives
audit pruning. Clock checks use `max(nowUtc, clockFloorUtc)` persisted before claims;
rollback never reopens an expired answer. A same-user restoration of **all** protected
state is not detected by a local high-water mark; no rollback-proof claim is made.
Audit uses §6's separate 16 MiB retention policy, not the journal/replay budgets.
If permitted pruning cannot provide safe write headroom, refuse new pending effects/
starts and report it; never prune unresolved identity or current cap/replay evidence.

### 13.2 Typed result and origin behavior

Only a **positively identified** blocking startup question produces `needsPerson`,
not a timeout or unreadable `None` screen. Existing `StartupQuestions` detects Trust,
HooksReview and BypassPermissions: map those to `folderTrust`, `hookReview` and
`permission` (bypass only). `signIn`, other permission prompts and `unknown` need
versioned positive detectors with fixtures before advertising them. Missing detection
stays `unconfirmed`; no generic text match invents a question or answer authority.
The origin's typed projection is:

```text
outcome: needsPerson
code: startupQuestion
request: <original launch UUID>
machine: <authenticated target device id>
machineName: <optional validated display name>
pending: <pending UUID>
questionKind: folderTrust | signIn | permission | hookReview | unknown
question: <question UUID>
revision: <integer>
expiresAt: <current answer deadline>
state: needsPerson | answering | expired | closing | reconciliationRequired
closed: false
```

Machine names follow [agent-operations.md](../protocol/agent-operations.md) validation,
never routing/pinning. Machine identity helps the fleet launcher, not a new cross-machine
agent permission. Origins see only their own IDs/kind/state/deadline, never warning,
scope, choices, process or another origin's record. Busy retains its existing timing/
fixed reason; the new pending ceiling returns `pendingLimit`, not false busy semantics.
Readback returns current pending/terminal state, not free text or a stale cached launch
answer. Expiry does not turn into `started`, a refundable refusal or permission to retry.

**No retry/reassignment while unresolved.** The origin cannot answer, press Enter,
bypass, impersonate a person, change trust, or launch that assignment elsewhere.
Proposed `launch.pending.withdraw` uses request/pending/revision and the **original
authenticated origin binding**, no typing. Session origins need their live bound
`read+reply+launch` credential and current local launch grant; script/fleet origins need
the exact `script:<machine>/<name>` binding, complete agent-free ancestry and current
`launch:fleet` grant (§11). Person origins use their approved person identity.
No credential rotation silently changes ownership; revoked/expired origins wait for a
person to cancel. Base scripts, uncredentialed services and another origin are refused.
Explicit allow-list wiring is required; today's script reads do not enable withdrawal.

Withdrawal shares guarded unready-close with Cancel, **not** ready-session stop.
Only verified `withdrawn`/`dismissed, closed: true`, or verified no-process-start proof,
permits reassignment (§9). Readiness wins with `started` and no replacement.
The dispatcher links a fresh target/request to the old proven-closed assignment; no
record migrates. Unknown delivery, expiry, offline or file deletion is not close proof.
Without the new close/grant wiring, origin withdrawal is unavailable and the launcher
waits. Cross-machine agent launch still waits on §9/#55.

### 13.3 Common client card

Proposed `launch.pending` capability: common bounded person snapshot via
`launch.options.pending` and `launch.pending.subscribe`/snapshot-and-change events.
Authorize **before** building any projection; reconnect/gaps require a fresh snapshot.
Pin vectors before use. Never insert a fake session into `sessions.list`.
One card per pending UUID in **Needs you**, including before session registration:

```text
<Kind title>
<Machine> · <Agent>
<Exact safe question, or fixed read-only reason>
Started 2:10 PM · Answer by 2:25 PM
The launch stays open on the machine until you cancel it.
[Answer OR See details]  [Cancel launch]
Stops this launch. It does not answer the question.
```

Kind titles: "Folder trust question", "Sign-in needed", "Permission confirmation",
"Hook review", "Question waiting". Protocol words `needsPerson`, `started`, `unconfirmed`,
`withdrawn` never become person-facing status labels. Times are viewer-local; samples
are Pacific. "Answer by" ends answering **here**, not the launch at the machine.
At expiry keep the same kind/title/card and Cancel; replace the time with
"Started 2:10 PM · Time to answer ended 2:25 PM" and Answer/See details with
"This can no longer be answered here. Answer it at the machine, or cancel the launch."

Only safe, authorized folder trust has **Answer**, opening the protected exact warning,
full folder/root and choices; opening sends nothing. No preselection; final choices are
buttons with words, no hidden/shortened scope, Markdown or command-copy approval.
Sign-in, permission, hook review, unknown or unavailable trust authority use **See details**:
"Answer this at the machine." plus a fixed host reason. No credential entry or new remote
permission channel. Unsafe read: "Can't show this question safely. Answer it at the machine."
Unreachable: "Can't reach <Machine>. Showing the last known state." Disable remote effects,
never label a local hide cancelled. Ready replaces pending with the actual session.

Cancel shows "Cancelling…" with progress and stays counted; fixed failure:
"Couldn't cancel. Try again." Never host text. Remove only on confirmed closure or readiness,
not expiry. Until guarded close exists, disable Cancel with an explicit machine-only reason.
Every card has the Needs mark **and word**, amber only. Answer alone is primary/Bridge teal;
Cancel is quiet secondary, not red/primary. No green readiness status on a pending card.
Wrap long text; preserve focus, accessible names/state announcements and narrow layouts.
Escape untrusted labels; they are not commands, links or routing keys. Older peers remain
explicitly read-only/unconfirmed; no compatibility action or claimed parity.

### 13.4 Person boundary, delivery and audit

Recheck current `relayAccount`, person/device approval and policy on every snapshot,
event and effect. Sign-in, a client-kind string, HA token or signing key alone is not
person authority. Agents/scripts/services cannot read protected text or Answer/Dismiss.
Origins' minimal readback/withdrawal is a different route, never the person projection.

**Local gate must be implemented:** add new Answer/Dismiss actions to `LocalApi.Acts`
**and** an explicit validated-App-only gate for actions and pending text/subscriptions.
`CallerKind.Agent`, script credentials, `Other` and services refuse before lookup;
same-user scripts must not fall through as a person. Recheck ancestry/credential state,
not a caller's claimed kind. Strip `launch.options.pending` before agent/script serialization,
including mixed options and subscription events. Withdraw alone has the narrow validated
origin exception in §13.2, explicitly wired after script operation checks.
This is a same-user safety rail, not a new pipe security boundary
([local-pipe.md](../protocol/local-pipe.md), [agent-principals.md](agent-principals.md) §1.3).

**Remote Answer and Cancel require equal person-command strength**: paired approved
same-tenancy roster person device, fresh explicit choice/confirmation bound to host,
pending/question/revision and durable single-use replay/nonce/floor enforcement.
`devices.md` has no pending-dismiss action today: pin closed allow-list verbs/vectors
and gateway enforcement for **both** pending actions before enabling them.
Today's `SignedCommands` trust MAC alone is not that gateway. After it, folder trust
still requires default-off remote-trust permission, individual device approval, exact
question/choice signature and existing screen guards before every key/injector slot.
Reuse that delivery, not a new generic answer. Existing v1 callers are not silently
upgraded; without the new gateway the pending card is read-only. Never fall back to
`session.reply`, legacy trust MAC alone or remote Bypass Permissions.

**Explicit-close support is a prerequisite, not an existing launch API.**
`WindowsProcessControl.Open(pid, startedAt)` supplies a validated held **single-process**
handle/Terminate; `IAgentStarter`/`ISessionLauncher` provide no owned process-group close.
New wiring must retain/rebind exact launch identities, prove the complete owned startup
process set, hold handles, recheck not-ready, terminate without typing and observe all
owned processes gone. Never PID/name-only killing, guessed descendants or another window.
If identity/completeness cannot be proved, refuse close and retain `closed: false`.
No uncertain worktree reclamation. This support must be reviewed/tested before advertising
Cancel/Withdraw; the existing pre-start cancellation token is not post-start cancellation.

Readiness, Answer, Cancel, withdrawal and expiry share one per-launch lock.
Persist audit **and** action claim before effects. Same UUID reads its durable result;
changed action bytes refuse. A claimed/in-flight answer holds closure until reconciliation;
check expiry/policy/screen again immediately before the first key. A genuinely new
question gets a new id/revision/deadline, never replays an old choice.
Ready wins with its session, not a stop. Late Cancel/Withdraw cannot close a ready session.
§6 records observation, attempt/refusal/claim/result, expiry and restart reconciliation
with typed fields and scope/choice digests; never raw question, answer, secret or path.
Action lines never mutate launch charges or pending slot state without a verified
state transition. A refused Answer/Cancel cannot un-count a live record.

Storage/audit failure blocks effects and new starts, reports `auditUnwritable`/
`pendingStorageUnavailable`, and retains/quarantines evidence. Reconcile every 30 seconds,
each pass bounded to 10 seconds; after five minutes mark `reconciliationRequired`, stop
automatic effect retries and offer person-initiated reconciliation at the machine.
It resumes only after writable stores, valid identity/state and durable reconciliation
evidence; unknown actions never resend keys/termination. No timeout/force-clear refund
or reassignment. Permanent damage requires person repair and positive process evidence,
not a guessed "empty" journal. Status/readback show the failure without paging the owner.

### 13.5 Failure and race contract

| Case | Required result |
|---|---|
| Host restarts before process start | Reconcile the reservation; no launch replay. An interrupted attempt follows §2/§5 |
| Host restarts pending / interrupted action | Restore binding/deadline/claim, positively rebind and reread; no blind replay. Missing identity is reconciliationRequired, closed false |
| Target/client offline | No closure. Host ends answerability at deadline without a client; reconnect snapshots current state |
| Expiry, including answer arriving late / clock rollback | Refuse new/late answer claims at deadline, persist expired state, keep process/card/slot. No automatic kill; new genuine question alone renews |
| Expiry audit fails | Close answer gate from durable deadline/floor; quarantine, report storage failure, reconcile within the bounded retry contract, never fabricate a committed expiry or release |
| Close identity/termination fails | Unknown, closed false; fixed Cancel failure, no replacement. Missing start-to-identity evidence never authorizes a guessed kill |
| Duplicate answer / two persons or clients answer | Same UUID reads result; changed bytes refuse. Only first valid question/revision claim acts; loser gets alreadyHandled/stale, never another key |
| Answer races Cancel/withdraw/expiry/ready | One claim/lock; in-flight effect reconciles first. Expiry prevents new keys, never undoes delivered keys; ready cannot be stopped |
| Delivered answer, lost acknowledgement | Readback/reconcile; never resend. Next question is not evidence that the previous answer failed |
| Direct answer at machine / replacement question | Observe readiness or exact new question; renew only that question's deadline, no new launch |
| Account/grant/device/policy/screen changes | Recheck at effect; refuse/quarantine stale evidence. New account cannot adopt/answer old records |

### 13.6 Bounded implementation cuts

**Host delivery cut: blocked on #1413 C1** (principal-keyed cap/result/options),
approved protocol vectors and separately sized support for account-scoped journal/
clock/replay, typed cap ledger, origin binding, positive detectors, equal-strength
person gateway and validated explicit-close. Delivery wires those helpers to the host.
The earlier ~300-line all-in estimate is withdrawn: **not a claim** that persistence,
new detectors, process ownership and authorization fit that cut. Engineering must
split absent support into independently reviewed <=400-line prerequisites before code.
Neither read-only staging nor a partial detector is claimed complete #2357 parity.

Failing-first acceptance: pre-session record/current readback and changed-byte refusal;
Trust/HooksReview/Bypass mapping plus separately fixtured signIn/permission/unknown,
None/unreadable not invented; replacement deadline vs unchanged busy; byte/record/
replay/refusal bounds; account/origin/grant separation and script allow-list; App-only
actions and zero protected fields in agent/script options/events; refused actions never
release slot/charge; journal/audit/expiry-write failure, restart and bounded repair;
duplicate/two-person/changed-screen/late-answer/lost-ack and close/ready races; single
effect only; no kill on expiry and no replacement until proven close; ready/unrelated
process never stopped. Required support cannot be omitted to meet the line budget.

**Client card cut: blocked on #1413 C1 and pinned host capability/vectors.**
Shared PWA/Windows projection, protected details/actions and resync; target <=400 lines
including focused tests, split adapters before delivery if necessary. Creative reviews
actual card wording/layout on that head. No new host capability is inferred from UI.
**macOS-client follow-up:** a separate bounded adapter PR consumes the same encrypted
snapshot/revision fixtures, first read-only; action controls wait on its protected
renderer and person gateway. Repeat the acceptance list on macOS before claiming parity.
No macOS host launch, process closure or script credential support is implied.

Failing-first acceptance: pre-session/expired/closing cards counted once; exact title/
safe labels/full scope and fixed time/unsafe/offline/failure wording; Answer only for
authorized trust, no send on open/preselection, See details elsewhere; expiry disables
answer not Cancel; Cancel progress/failure leaves card until proof; ready replaces it;
capability absence stays read-only; reconnect/gap/two-client stale resync; keyboard/
screen-reader/narrow layout and protected renderer preserve authority on each client.

### 13.7 Open choices before code

Engineering/Product must approve the proposed four-record/storage/replay/reconciliation
budgets and replacement-question renewal, resolving v1's newest-vs-first busy wording
without silently changing it. Confirm origin-only withdrawal grants vs person-only
cancellation (the latter blocks unattended reassignment). Pin common events and **both**
person-command verbs, detector versions and process-ownership/close proof; size all
missing prerequisites before delivery. Creative's supplied wording is the design text,
not a receipt for unbuilt UI. No automatic kill, sign-in or permission answer channel
is approved here.
