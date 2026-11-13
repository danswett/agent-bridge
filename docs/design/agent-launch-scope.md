# Agent launch scope: ask-only launches by agents, with an hourly cap and an audit (#1403)

**Status: design for the owner's sign-off. No code changes with this note.** Slice 2a of the
Home Assistant retirement epic (#1396). It decides what slices 2b (#1404), 2c (#1413) and 2d
(the fleet repository's #106) build. It narrows [agent-principals.md](agent-principals.md) §3 and §5
for the first version; where the two disagree for v1, this note wins, and agent-principals.md
stays the long-term design. Times are Pacific.

**Prerequisite cut (#2357 / #1472):** scope, accounting, audit compatibility and
restore requirements only. The complete pending-record/card contract follows in a
separate reviewed cut. All pending behavior remains disabled under §13's gates;
references to the companion's §13.x sections below do not authorize partial code.

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
| `read` | `sessions.list`, `session.get`, `sessions.subscribe`, `usage.*`, `launch.options` (agent projection, §7) | **default** for every launched session (`AgentCredentials.Prepare`) |
| `read+reply` | the above, `session.reply`, `agent.send` | a session a person grants it to |
| `read+reply+launch` | the above, `session.launch` under §3's rules | a session a person grants it to, on a machine with agent launch on |

The four scope names exist in `AgentPrincipals` and are stored by `AgentCredentials`.
Today `LaunchService.StartAsync` calls credential `Prepare` with `read`;
this proposal adds the launch-scope check.

**Who grants it.** Two keys, both needed:
* **Machine switch:** `agents.launch.enabled` in that machine's `config.json`, **default
  `false`**. Changed only at the machine (the app's Settings, or `agent-bridge-host config`).
  `LocalApi.Handler`'s `host.*` gate refuses settings inside agent sessions;
  the new key is one more setting under that rule.
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
acting ops are refused for `CallerKind.Agent`. `session.launch` from a
connection with a live agent credential (`AgentCredentials.Of`)
goes to a new `AgentLaunchGate`; every other op in `LocalApi.Acts` stays refused for
agents exactly as today; §13 separately proposes only an origin-bound withdrawal
exception, never Answer/Dismiss. The gate runs, in order, refusing at the first failure:

0. a `request` this principal has used before never starts a second launch. The gate checks the
   launch service's memory (`LaunchServiceOptions.RememberFor`, 25 minutes) **and** the set of
   requests rebuilt from today's/yesterday's audit **and every unresolved journal of any
   age**, including its origin binding/hash. An unresolved request never ages out of dedup.
   Missing/unreadable evidence refuses launches (§6). Identity uses `requestSha256` (§6), the
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
5. under one lock: check hourly/live caps (§5) and §13 pending capacity **before charging**.
   Full pending returns pendingLimit without a reservation. Otherwise reserve all three
   together and write the attempt audit (§6, `outcome: reserved`). An unwritable audit undoes them
   before dispatch and refuses; concurrent launches cannot both take the last place;

then the request goes to the launch service with the agent principal as its peer, which applies
its own checks unchanged ([docs/host-launch.md](../host-launch.md), `LaunchProtocol.Check`), plus one new check
**inside** the launch service: the worktree check of §3, which can only run once the worktree
exists, after `Worktrees.Create` in `LaunchService.StartAsync` and before the agent's process starts.
A failing worktree check removes the new worktree and refuses. **Hourly charge:** only
verified pre-start `busy`/`refused` refunds it; `failed`/interrupted stays charged.
**Pending/live reservations:** §13.5 releases them on proven no-start/closure, including
pre-start failure; readiness converts live without double count. An outcome alone is
not closure proof. Refusals at every step are audited (§6).

**Fail closed.** An unreadable or malformed `agents` policy, an unreadable credential store
(already enforced by `AgentCredentials`), caps not yet rebuilt from the audit (§5) or an audit log
that cannot be written each refuse the launch. Unreadable is never read as allowed, the same
posture as the credential store and [docs/host-config.md](../host-config.md). In operation:
* **Transient is not unreadable.** A file briefly locked (a virus scan, a backup, a sync client)
  is retried for about a second before the host decides.
* **Distinct codes:** transient `policyUnreadable`, `auditUnwritable`, `capRebuilding`
  and `pendingStorageUnavailable` carry `retryAfter`. `pendingLimit` has no known release
  time; sticky `pendingStorageUnavailable` with `manualRecoveryRequired` has no retryAfter.
  Retry uses the same request/readback, never authority for a new launch. The pipe never
  hangs: while caps rebuild, launches are refused at once with `capRebuilding`.
* **Visible.** A refusal that cannot be audited is otherwise invisible, so `host.status` carries
  the current fail-closed reason and since when, the last successful audit write, refusal counts
  by code, and the caps' current use; the host's own log is the fallback record. SRE alerts on it.
  (Any client, the HA component included, shows it from `host.status`.)

**Closing the side door.** Today only `CallerKind.Agent` is refused acting ops: any other
same-user process (a script, the HTTP MCP) may `session.launch` as the person, with Allow all
and no audit (`LocalApi.Handler`'s acting-op gate). That is a private path, and the
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
| machine | **this machine only**: local pipe only; neither `LaunchRequest` nor v1-launch.md's `session.launch` request table has a machine field. Other agent-launch channels are refused | Cross-machine waits for #55 (§9) |
| `permissions` | **must be present and `ask`**. Absent or `allowAll` is refused | Absent takes `defaults.permissions`, possibly `allowAll` (v1-launch.md, `session.launch` field table) |
| `transfer` | refused | Transfers need a person (agent-principals.md §5.2) |
| `resume` | **refused in v1** | Resuming types an agent's prompt into an existing conversation, possibly a person's; new sessions only keeps it simple |
| `attachments` | refused | `attachment.*` is already refused for agents (`LocalApi.Acts`, `Handler`) |
| `workspace` | required, must be on the machine's `agents.launch.workspaces` list (labels a person ticked "agents may launch here", default empty), and must be an isolated workspace, so each launch gets a fresh worktree. **The new worktree must hold none of the chosen agent's project configuration** (the worktree check, §2): presence alone refuses, whatever the contents, so it is an allow-list of "clean", not a deny-list of bad settings | A folder a person approved at that machine for this purpose. Without the check an agent could plant settings (Claude's `.claude/` can set a bypass default mode, hooks, an API key helper or MCP servers) and then launch over them |
| `agent` | required, from `launch.options`, on `agents.launch.agents`; `auto` and Agency refused; only an agent with a confirmed project-configuration list (below) | Agency adds profile config the host cannot show keeps ask (agent-principals.md §5.2, "Launch rules for agents") |
| `profile` | refused | Agency only, which is refused |
| `model`, `effort`, `context` | from `launch.options` (existing check, [docs/host-launch.md](../host-launch.md)) | |
| extra arguments | a launcher with any `launch.agents.<id>.extraArgs` is refused for agents | An extra argument can grant Allow all ([docs/host-launch.md](../host-launch.md)). `LaunchService.LaunchVerificationAsync` already refuses them |
| `agentScope` | refused | Only a person sets scope (§1, §4) |
| `prompt` | at most **16 KiB** of UTF-8; longer is refused (below) | |

**What "ask-only" means in v1, plainly.** No bypass flag (the host adds `--allow-all`,
`--dangerously-skip-permissions` or `--ask-for-approval never` only for `allowAll`,
`Launchers.cs:143`, `:156`, `:170`), no extra arguments, no Agency, and no agent project
configuration in the new worktree. It is **not** agent-principals.md's provably forced-ask launch
(agent-principals.md §5.2, "Launch rules for agents": host-owned configuration home, scrubbed
environment, no managed policy), which comes with AP8. The user's own settings (in the user's profile, not the
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
of at most 20 KiB (`LaunchProtocol.MaxPrompt`/`MaxRequest`; v1-launch.md, "The prompt goes whole"),
the MCP server checks the same 16 KiB (`server.mjs`, `MAX_TEXT`/`validate`), and the host refuses a
longer prompt with "send the rest as an attachment or a reply once it has started"
(`LaunchProtocol.Check`). We keep that number and do not raise it:
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
from `SCHEMAS.launch_agent_session` (`server.mjs`) and `PERMISSIONS` (`tools.mjs`), plus its
`profile` and `resume` arguments, so an agent is not offered what the host will refuse.

## 4. The launched session's own scope

**Decision.** A session started by an agent gets `read`, and nothing in the request can change
that. Its scope is the narrowest of: `read`, the machine's `agents.issue.launched`, and its
launcher's scope. It can never launch (`allowAgentLaunchedToLaunch` fixed at `false` in v1,
[agent-principals.md](agent-principals.md) §5.4 / §5.5).

**Maximum depth: one agent launch step.** The credential records `depth` and `chain`
(`AgentCredentials`), and a person's launch mints depth 0 (`Prepare`).
A person launches a coordinator (depth 0), the coordinator launches a worker (depth 1),
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
§13 reserves pending/starting processes in their agent or fleet six-live bucket, then
converts to ready without double-counting; pending evidence alone is not a session.
**Fleet workers are counted apart:** they have their own live limit, **6 per machine**
(`limits.liveFleetWorkers`, the dispatcher's own worker count), so workers never block
coordinators' six-live bucket or the reverse; §13's shared pending ceiling is separate.
The 2-hour idle end does **not** apply to them, since an
unattended worker waiting on its build is not abandoned. The dispatcher ends its workers, as it
does today.

**At the cap:** `outcome: refused`, `code: rateLimited`, `retryAfter` in seconds, and the
reason "This machine has started 10 agent launches in the last hour (its limit). The next one
can start in 14 minutes. App launches remain available unless a separate safety fault blocks them."
The agent sees the per-agent
or per-machine number, whichever stopped it.

**Across restarts:** the windows are rebuilt at host start from the audit log (§6), not kept in
memory only as [agent-principals.md](agent-principals.md) §5.5 allowed for messages. A launch is costly and the gate's
evidence (on each machine, 5 started and a sixth refused during the acceptance run, §10) must
survive a restart.
* **Cap/request fold:** today's/yesterday's audit in full; caps use its last hour.
  Request ids also fold every unresolved journal, regardless of age (§2 step 0).
  Separately, §6 preflights all retained versions and unresolved journals.
* **Clock:** the window is judged on each line's wall-clock `at`. A line dated in the future (the
  clock stepped back) counts as if dated now, so a clock jump can neither open nor close the cap.
* **Damage:** a torn last line (power loss mid-append) is ignored, and its complete `reserved`
  line still counts. A bad line or a broken chain **inside the last hour** refuses agent launches
  (`capRebuilding`, with the reason). Older known-major cap-only damage warns in `host.status`;
  this never overrides §6's unresolved/unknown-version evidence fault at any age.
* **Recovery:** ordinary cap rebuild retries every 30 s, no restart; §6's sticky manual
  evidence fault is different and never clears through these retries.
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
* **One writer.** One host runs per profile under `HostLock`, and a
  replacement starts only after `HostHandover` lets it take ownership. The host also
  holds an exclusive lock on the audit folder while it appends, so two writers can never fork
  the chain or both take the last slot.
* **Format/rollback:** legacy is `v: 1`; known-major readers ignore unknown fields, never
  unknown majors. Before new-format writes, install a durable minimum-reader marker and
  startup guard in every rollback-eligible host. **Incompatible old hosts refuse to start**,
  before APIs/cap rebuild; unguarded builds are not rollback candidates. Keep the marker while
  any unresolved journal or retained new-format evidence (archives included) exists, regardless of age.
  Only a compatible host under the profile lock may clear/lower it after a complete all-age
  scan proves neither remains; never clear by time, partial scan or deleting evidence.
  Acceptance: oldHostRefusesStart checks before APIs; rollbackPendingOlderThanTwoDays uses
  a 72-hour unresolved journal absent from today's/yesterday's audit: dedup finds it, marker stays.
  Preflight all retained versions/journals. Unreadable marker/journal/new-major evidence
  blocks old-host startup. Once §13's gates are met, **all session.launch, person-app included**,
  is blocked as pendingStorageUnavailable; persist manualRecoveryRequired=true, no retryAfter.
  This sticky fault is not timed capRebuilding; even readable files do not auto-clear it.
  **Owner recovery:** local App `launch.pending.repair` journal-restore mode (§13.4 gate),
  bound to profile/journal digest when record IDs are unreadable. Keep original bytes/hash
  in non-destructive quarantine; audit intent and old/new digest binding before restore, result after.
  No unaudited effects. Restore only complete validated authoritative evidence; finish all-age rebuild
  before clearing the manual block or marker (the latter also requires no retained evidence).
  Source: a protected same-profile journal backup plus every newer intact journal/start/audit/
  replay record. Audit alone cannot recreate private identity; gaps or lower clock/replay floors
  refuse repair. Verified backup/restore support ships before pending writes, not as a later fix.
  After validated restore/rebuild, the new copy is authoritative; the original is forensic-only,
  excluded from runtime readability checks but still pins the marker, never authorizing rollback.
  Missing identity/no complete evidence stays blocked/sticky; nothing deleted/force-cleared (§13.7).
  **Proposed quarantine bound, separate from pending storage:** four originals, 256 KiB/profile
  including metadata. At saturation/oversize retain the original in place, refuse allocations/
  repair, never copy beyond the budget or evict evidence. No automatic expiry; retained originals
  can pin the minimum reader indefinitely. Product must approve bounds/retention before enabling (§13).

Fields: `v`, `at` (UTC), `principal` (`agent:<machine>/<session>`, `AgentProtocol.Principal`), `scope`,
`depth`, `action: "session.launch"`, `request`, `machine`, `agent`, `workspace` (label),
`model`, `permissions`, `promptBytes`, `requestSha256` (§2 step 0; a hash of the normalised
launch fields, never the prompt's text), `outcome`, `code`, `reason`, `session` (the new one, when
known), `grant` (`agent` or `fleet`), `limit` and `overridden` (§5). **Never** the prompt, the token or a file path beyond the
workspace label (#1401's rule). Every attempt gets a line: a refusal at any gate step is one
line with its `code`; a launch that passes writes `outcome: reserved` **before** it starts (§2
step 5), and the outcome is a second line with the same `request`.

**Pending (§13):** new-major `event` is launch-state/pending-action/pending-refusal;
only launch-state changes charges. Fields: pending/question UUID, questionKind,
revision, verified actor/actionRequest, launchState/startClaim/readyEvidence,
sticky processStarted, verified closed and fixed code, including pendingLimit,
pendingStorageUnavailable, auditUnwritable and repairBlocked (§13.2); no pre-session id.
Closed proves absence, not known-ready history; expiry without closure stays false.
New readers fold legacy pairs; old readers reject new major, not unsafe last-line counts.
Filter event/action/outcome; unknown fields confer no authority.
pendingLimit is a launch-state refusal before reservation (no charge); repairBlocked is
pending-refusal for the repair action, never a launch-state transition/refund.
Refusals share §5's 60/actor/hour plus 60/host/hour, then one count-summary/minute.
Proposed 256 verified actor entries expire hourly; overflow/uncredentialed uses host
bucket, never attacker-keyed. Summaries contain counts/codes, no question/answer/request text.

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
the remembered outcome and starts nothing (v1-launch.md, `session.launch`'s `request` field).
That is the readback; no new op.
Today `LaunchService.LaunchAsync` remembers by request key, and the MCP server
keeps its own ledger (`mcp/src/tools.mjs`). #1413 keys it by (principal, `request`) for
agents, so another caller repeating an agent's request id gets `refused`, never that agent's
result, and makes `read_launch_result` repeat the request. §13 explicitly changes the
projection only for peers negotiating pending readback; legacy behavior remains.

**Options:** an agent's `launch.options` lists only the agents and workspace labels it may use
(no `path`), no `resumable` and no `trust` questions (agent-principals.md §5.2 already drops
titles). **Busy:** an agent gets "a launch on this machine is waiting for a person at the
machine; try again in a few minutes", never the other session's agent, workspace or question
(v1-launch.md:53, :128).

## 8. Threat model

| Threat | Mitigation | Accepted residual |
|---|---|---|
| **Prompt injection makes an agent launch sessions** | Off by default (two keys, §1); ask-only, so every action of the new session still asks a person; approved workspaces only; caps 3 and 10 per hour, at most 6 live and idle ones ended after 2 h; full audit; the new session is `read` and cannot launch | Up to 10 unwanted ask-only sessions per machine per hour (6 live at once), each needing a person to approve anything; the injected prompt runs in the new session (it is the agent's own prompt) |
| **A stolen session token** | `AgentCredentials.Check` binds the complete session process tree; `RevokeEnded` retires ended bindings; one credential per session, revocable | **Safety rail, not a boundary** (agent-principals.md §3.4): a same-user process can spoof its parent or join the Job. Such a process can already do anything the user can |
| **Runaway fan-out** | One agent launch step at most (§4); per-agent and per-machine caps (§5) that survive restarts; refusals rate-limited | §13 adds a separate bounded pending-availability risk |
| **Pending slots pinned / cancel-reassign churn** | Four proposed shared slots; also reserve the matching six-live bucket. Terminal proof releases pending/live slots, never evicts unresolved launches; each replacement is a fresh charged request | One/two authorized agents can pin all four, including fleet availability. No automatic kill. Product must settle reserved capacity and per-item replacement ceiling/backoff (§13.7) before enabling unattended reassignment |
| **Unknown answer/storage fault deadlocks a pending slot** | Bounded automatic reconciliation, then explicit local person-gated `launch.pending.repair` (§13.4); fence unknown effects and require closure/readiness evidence, never force-clear | Person must restore storage/compatible host and may need to close the owned window or reboot; until proof, the slot stays held and launches can fail closed |
| **The new session reads secrets in its folder** | Workspaces only from the "agents may launch here" list a person set at the machine; isolated workspaces start in a fresh worktree | The session runs as the user and can read what the user can, like any session. The list is what the owner controls |
| **Ask-only is weaker than it looks** | Ask flag forced, extra arguments refused, Agency refused, fresh worktree, and a worktree with a tool-approving project settings file refused (§3) | User-level agent settings outside the repository, and settings formats the host does not yet know (it refuses agents it cannot check). The full "provably forced-ask" launch (host-owned config, agent-principals.md §5.2) is AP8 |
| **The fleet grant is abused** (§11) | Only the dispatcher's script credential; the caller's ancestry must be read **in full** and contain no agent session, so a partial or untraceable ancestry, which today falls to `Other` (local-pipe.md:104), is refused. **This rule is not a barrier:** a detached process whose parent has exited has a complete, agent-free ancestry (local-pipe.md:106-110), so the credential file is the only real barrier; fleet-ticked isolated workspaces only; own cap; audit | Something that tricks the dispatcher (for example text in an issue it reads) can start an Allow-all worker in a fleet workspace, bounded by list and cap. The script credential sits in an owner-only file (#1402), which any same-user program can read: an agent that copies it and runs a parent-spoofed helper can reach the grant. That is the same-user limit (agent-principals.md §1.3), no worse than today's HA token, which every session's environment holds |
| **Launch loops starve people** (busy or refused attempts are not capped) | Attempt limit of 20 per agent per hour, any outcome (§5); a refused launch removes its worktree, so it cannot fill the per-repository worktree cap | A looping agent can take 20 attempts an hour |
| **A prompt-injected agent edits the stored overrides** (§5) | The host clamps each stored override to its 4-hour maximum from the time it was set, refuses values outside each limit's range, and audits any override that changed outside the CLI, when it loads the config and when the file changes | `config.json` is the user's file, so a same-user agent with a file tool can still set a value within range for up to 4 hours. That is the same-user limit (agent-principals.md §1.3); the audit line shows it |
| **A side door: launching as the person from a script** | Closed for `session.launch` in #1404 (§2) | Other acting ops until AP8 |
| **Until #1404 and 2e ship** | Nothing new: agent launch stays refused (`LocalApi.Handler`) | Today a same-user script can launch with Allow all through the pipe's acting-op gate, and anything holding the HA token can press Allow all on any machine. Both stay until #1404 and the switch-off of HA launch (#106, then S7) |

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
  assignment returns only on §13.5 terminal closure with no-start/never-ready evidence,
  not merely a Cancel acknowledgement or `closedUnconfirmed`. Missing acknowledgement,
  sleep, host restart, tracking expiry or removal of an assignment
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
| **#1401** audit log (first) | as filed, plus the launch file, hash chain across files, line version, folder lock, pruning rules (§6), and audit commands with `--outcome`/`audit archive`; compatibility prerequisite owns minimum-reader marker, startup guard and locked all-age clearing. Every new-major write is gated on its deployment to all rollback-eligible builds; pending writes/events/person-app block also wait on §13's complete design and repair gateway | M | an unwritable log refuses the action; a removed line shows "broken"; pruning/rotation keep the chain intact; torn last line ignored; newer major refused, unknown fields ignored; second writer refused; oldHostRefusesStart before APIs; rollbackPendingOlderThanTwoDays; unresolved/archive evidence prevents marker clearing; writes refused before guard or repair deployment; off/observe/presence-none keep existing launch rules; existing faults never downgrade |
| **2b #1404** scope and enforcement | `agentScope` on person launches; the `agents.launch` and `agents.limits` config keys; `agent-bridge-host agents revoke`; `AgentLaunchGate` (§2, §3) with the worktree check and per-agent configuration lists; **in-memory caps and attempt limit with the §5 defaults, and step 0 remembering every (principal, `request`) with its hash for at least the hour**, so agents are never uncapped between 2b and 2c; refuse `session.launch` from `Other`; MCP schema trimmed; agent prompt wording | L (about 450; may split the worktree check off) | refused: no scope, scope revoked mid-session, switch off, policy unreadable, `permissions` absent, `allowAll`, `transfer`, `resume`, attachments, workspace not listed, workspace not isolated, worktree with a tool-approving settings file, Agency, `auto`, extra arguments, `agentScope` from an agent, launcher at depth 1, an adopted session, a script caller, two launches racing for the last slot; worktree holding `.claude/` or `.mcp.json` refused and the worktree removed, an agent kind without a confirmed list, a slot released after `busy` and after `refused`, the 21st attempt in an hour, the same request 16 minutes later starts nothing and counts once, a `requestReused` loop stopped by the attempt limit; accepted: ask-only launch in a listed workspace; 16 KiB prompt whole, 16 KiB + 1 refused, the npm agent's command-line refusal; child credential is `read` |
| **2c #1413** cap, result, options | caps and attempt limit made durable (typed launch-state fold at start, `interrupted` lines, 30 s rebuild retry); request ids and `requestSha256` across restart (§2); agent live limit and idle end; stored, clamped, range-checked overrides, outside-CLI changes audited; fail-closed `host.status`/cap use; principal-keyed result/options/busy; runbook lines. C1 is the prerequisite for §13, not its implementation | M-L (about 380; may split live limit/idle end) | 4th launch by one agent refused; 11th on machine refused; 7th live agent-launched session refused; idle ended after 2 h, not after person input; cap survives restart; crash between reserved/outcome still counts; future line counts; defect inside hour refuses capRebuilding, older warns; locked policy retried then policyUnreadable; override lapses; different principal never gets readback; 20-hour request starts nothing; far-future/out-of-range override clamped/refused and audited; pre-start refused/busy does not count after restart; override survives restart and refusal records effective limit; options have no path/resumable/trust or pending text for agents; action/refusal lines never refund; old schema readers fail closed on new event semantics |
| **2e (new)** fleet launch grant | §11 grant on the script credential: new branch in `LocalApi.Handler` (the agent gate handles only `CallerKind.Agent`); fleet cap/live limit, idle exemption, `diskLow` check/runbook | S (about 120), after #1414/#1404 | refuse agent ancestry, unticked workspace, cap 9th, under 1 GiB free, worker above read+reply; 7th live worker refused, separate agent bucket; no fleet 2 h idle end; shared pending ceiling is §13 |
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

## 13. Pending behavior is disabled pending the companion design

This cut is not the pending implementation contract. Identity/state/outcome/readback,
person actions, closure/race tables and the approved Needs-you card follow in the
companion under #2357/#1472; §13.x references above name those deferred sections.
No partial pending behavior or new permission is authorized.

Pending-journal writes, pending-major events and the sticky person-app launch block
ship only with or after the complete reviewed design, #1413 C1 and the protected local
repair gateway with verified restore support. Require `enforce`, an approved presence-
proven App (Windows stays `none` until R4), protected confirmation, signed person commands,
closed repair-action/target bindings and durable replay checks, including on the local pipe.
[Device approval](device-approval.md) §§3.1, 4.6, 7.2 and 7.5 are prerequisites, not a current grant.
Missing gateway, `off`/`observe` or presence-none keeps today's launch rules and writes
no pending records/events or person-app fault latch. Already-written unresolved evidence
stays fail-closed; mode changes/missing signer never downgrade or clear it.

Before enabling, align agent-principals' child scopes/caps/audit/script rights and add
the closed pending person actions to the device-approval/protocol contracts. Product
must approve quarantine bounds/retention and file the linked #2357 irrecoverable data-loss
override decision; no override, deletion or force-clear is authorized here.
