# Agents resume an existing session in place (design)

Closes [#4318](https://github.com/danswett/agent-bridge/issues/4318) (program C, slice C4).
Companion to [#2741](https://github.com/danswett/agent-bridge/issues/2741) (an agent ends a session an agent started)
and [host-launch.md](../host-launch.md).

**Owner decision, as recorded in the Fleet register (docs/owner-decisions.md, "Nov 23, owner, coordinators may resume or
restart existing sessions (about 19:52 PT)"):** the owner answered "Yes" to the Chief's proposal: "(1) any coordinator, SRE
included, may resume or restart an EXISTING session, and may launch builders for its own lane within the shared builder cap
(26); a launch log and the cap prevent double launches. (2) NEW coordinator sessions still need the owner's direct approval.
(3) A resume option is added to the launch tooling, so in-place restarts never need the owner." The record adds: the native
launch path only (program C4), "no ps1 investment"; it "supersedes SRE's 'never launches' rule for the resume and restart of
EXISTING sessions ONLY; it does not let SRE launch new sessions or coordinators."

**Scope guard (Engineering Lead R10):** the decision covers COORDINATORS only (leads, Quality, SRE, the Chief), not every
agent. A builder or any other agent stays refused, as agent-launch-scope.md's `resume` row says today. This change therefore
narrows that row (and the grant-launch refusal in `FleetLaunchGrant.cs`, which stays: a launch request never carries
`resume`) instead of lifting it: only the new `fleet.resume` op, and only for a coordinator caller, may resume.

## What it is

One host operation, `fleet.resume {version:1, request:<uuid>, session:<full uuid>}`, on the agent's local pipe, beside
`fleet.end` (same pattern: a live agent session credential with `read+reply+launch` scope at depth 0, not adopted; never a
script, the app or the CLI). It resumes the session **in place**: the same session id, the same folder and worktree, so its
open pull requests stay attached. It is the host's existing resume path (`ISessionResumer.ResumeAsync`, the one a restore
after a reboot uses), so every check a resume already meets still holds: agent-ha-bridge, `launch.enabled`, held discovery,
a folder-trust question waiting, the folder still approved, one launch at a time.

## Who is a coordinator (by recorded role, never by name)

The host keeps no coordinator marker today: the launch record keeps no launcher principal and no role, and a name is
display text. This change records, at launch, the launch's coordinator role (the optional `coordinatorRole` request field a person's launch
carries, in `LaunchProvenance`; the worker `role` is a different thing, it narrows MCP servers and is not recorded), and adds a person-set host setting `agents.resume.roles` (the role names that count as coordinators on
this machine). A caller is a coordinator only when its own session's recorded role is in that list. The default is an empty
list, so resume is off until the person lists the roles (fail closed; setting `agents.resume.roles` is the owner's step); a caller with no recorded role, or one launched by a
person without a role, is refused. No name, title or fleet-name lookup is ever used.

A role is recorded only by a PERSON launch. The owner decision above, item 2, says NEW coordinator sessions still need the
owner's direct approval and only EXISTING sessions may be resumed or restarted, so an agent never records a coordinator
role on a new child launch (not even a coordinator launching a child with its own role; the launch itself is not refused,
since workers legitimately name a worker role such as `default`, it just records none). The one way a role survives an
agent's action is the same session resumed in place, which keeps the role its own earlier record carries (also across a
reboot restore). A session can never set or raise its own role. Each is a test.

**Known limit:** sessions launched before this ships have NO recorded role, so nobody can resume them by this op (fail
closed) until they are relaunched; the owner resumes those by hand meanwhile.

## Which callers count as a coordinator, and how the host knows

The host decides from facts it recorded itself, never from a name, a title or what the caller says. A caller must (1) reach the
agent's local pipe and present its own session token through `agent.hello` (the host binds the channel to that credential; a
script, the app, the host CLI and any relay peer cannot reach `fleet.resume`), (2) hold a live **session** credential with
`read+reply+launch` scope at depth 0 that was not adopted (the same standing check `fleet.end` and a granted launch use), and
(3) have its **own session's recorded launch role** listed in the person's `agents.resume.roles`.

| Caller | Coordinator? | Why |
|---|---|---|
| A session a person launched with a role the person listed, with launch scope (`agents.issue.launched` is `read+reply+launch`) | **Yes** | The role was recorded by a person's launch; this is the only way a role is recorded |
| The same, with no role recorded, or a role not listed | No (`policy`) | Pre-ship sessions have no role; an empty list means resume is off |
| A fleet worker or builder (read+reply credential, depth 1, launched by an agent or the dispatcher) | No | An agent's launch records no role (owner decision, item 2) |
| A session launched through `fleet launch` by a script, including a lead the dispatcher starts | No | Same: it records no role. Coordinators are started by a person (the owner) |
| An adopted session, a script credential, the app, the host CLI | No | Wrong credential kind, or not on this op; a person resumes with the app's own Launch |
| Another machine | No | `fleet.resume` is on the local pipe only; nothing crosses machines or people |

**How a coordinator gets its role (slice 3, agent-bridge #4433):** the app's Launch screen has a Role box, which sends the new
`coordinatorRole` request field. It is separate from the worker `role` (which narrows MCP servers and would strip a coordinator's tools). Only
a person's launch records it; an agent's or a script's launch carrying it is refused, and it is for a new session only. `fleet.resume` reads it
from the launch record (tested). Until the owner lists a role in `agents.resume.roles` and launches a coordinator with this box, resume stays off
(fail closed). The PWA launch box sends no role yet.

## How it relates to kill-aware messaging (#4403) and the launch caps

- **Kill switches.** Resume obeys the agent and launch switches (`agents.enabled`, `launch.enabled`, not read-only), refusing
  `kill-switch`, as a launch does. It does not read the messaging kill (`agentMessaging`, `LiteKill`): resuming a session is not
  messaging. A resumed worker is then an ordinary session under #4403: after a recorded kill it sends nothing unless
  `LiteKill.IsOn` says messaging is on, and no resume path turns messaging on or changes the kill record.
- **Caps.** A resume takes **no** slot under the host's live agent-launch limit and is not counted by the hourly launch caps yet (that
  gate is bound to the person-launch policy); this is the known limit above and slice 4. What prevents a pile-up meanwhile: a running
  session refuses (`running`), one start per session, one request id resumes once, the host's one-launch-at-a-time start turn, and the
  shared builder cap (26), which is the dispatcher's and counts sessions, not launches. A resumed session is the same session id, so
  it replaces the ended one rather than adding a seat.

## When the session's folder or credential is gone

Nothing is created, guessed or widened; the receipt is `refused` (the host log says why) and the session's launch record is left as it was.
- **Worktree or folder gone** (finished idle worktrees are reclaimed by the host's own rules, docs/host-launch.md): the existing resume path
  refuses with "the original directory must exist and be an approved workspace". Resume never makes a new worktree: that would be a new
  session, which needs a person. The owner relaunches.
- **Credential gone or unissuable:** an old credential is never reused (it was tied to the old process). A fresh read+reply fleet worker
  credential is minted for the new process; if the credential store cannot issue one, the resume refuses with "a read+reply fleet worker
  credential could not be issued" and nothing starts (probe-tested), the old record intact.
- **Recorded launch settings gone:** refuses (`policy`); the machine's defaults are never used (Allow all is never guessed).
- **Agent signed out or account refused:** the launcher's own pre-start refusal (Copilot account check) comes back as `refused`.

## Slices (each under 400 whole lines, tests first)

| # | What | State |
|---|---|---|
| 1 | Launch record keeps the role; `WasFleetLaunched`, `RoleOf`, role guards (#4325) | built, armed |
| 2a | `ResumeFleetAsync` in place, settings restored, `agents.resume.roles`, record superseded (#4325) | built, armed |
| 2b | `fleet.resume` op, audit, pipe wiring, role carried, no role on a child launch (#4360) | built, stacked on #4325; closes #4318 |
| 3 | `coordinatorRole` on the launch request and a Role box in the app; the resume reads it from the record (#4433) | built, stacked on #4360 |
| 4 | Take a slot under the live-launch limit; a durable intent row | to do (known limits) |
| 5 | MCP tool `resume_agent_session`, a thin wrapper over `fleet.resume` (`{session_id, request?}` returning `{id, request, outcome, reason?}`), offered with the `sessions.launch` capability, after the .NET MCP port | after the port |
| later | A combined restart flag (end then resume) only if wanted after the beta | not built by design |

## Rules (each is a test)

1. **Provenance, not a name.** Only a session the host's own launch record shows an agent started through the fleet route
   (`LaunchProvenance`, every record for the session `Grant == "fleet"`). Unlike stop, the process is gone, so the record is
   matched by session id alone (a new `WasFleetLaunched`); an unfinished or unreadable record refuses (fail closed).
2. **Refused:** an unknown id; a session only a person launched; any session another person's host record names (nothing
   crosses people: this host's record is one person's); a derived (16-hex) id; a session already running (`running`; no
   force, no kill-and-restart, stop it first); the caller's own session; a kill switch, read-only host or excluded session.
3. **Never wider than the original.** The resume uses the settings the host recorded when the session was launched
   (agent, profile, model, effort, context, and permissions as launched), never anything the caller supplies, and never
   wider: a setting the machine no longer offers falls back to its default, and settings that cannot be read refuse the
   resume. The "Ask permission only" rule in agent-launch-scope.md (lines 29 and 33) is a design-document line for NEW agent
   launches, not an owner decision; the owner decisions run the other way for fleet sessions (Fleet register, Nov 21, 12:47
   PT: "every fleet launch is `Allow all`"; Nov 15: every launched session explicitly uses `Allow all`; pipeline-rules.md
   line 93). So a resumed Allow-all builder stays Allow-all and can work unattended; a session launched under Ask resumes
   under Ask.
4. **No double launch (live-limit slot NOT built).** A session already running refuses (`running`), the launcher's one-start-per-session guard and
   single-launch gate hold, and one request id resumes once. **Known limit (follow-up):** a resume does not yet take a slot
   under the host's live agent-launch limit; the shared builder cap (26) is the dispatcher's, outside the host.
5. **Logged and audited (durable intent row NOT built).** One audit row per request under the agent's principal and chain (`fleet.resume`: session,
   outcome, closed reason code) and a host log line. A repeat of a request id returns the first answer from memory and
   resumes nothing twice; after a host restart the session is simply running, which refuses. (No durable intent row: the
   resume is not an op the gate audit knows; a follow-up if Quality wants it.)
6. **Receipt.** `{request, outcome: resumed | refused | unconfirmed, reason?, session}`. Closed reasons reuse the fleet set
   (`scope`, `policy`, `kill-switch`, `bad-argument`, `audit-unavailable`) plus `running` and `busy`; a launcher failure is
   `unconfirmed` / `failed`.

## Known limits (not built in this change)

- **Rule 4's live-limit slot.** A resume does not take a slot under the host's live agent-launch limit: that gate is tied to the
  person-launch policy and is a follow-up. Double launches are still prevented (a running session refuses, one start per
  session, one request id resumes once); the shared builder cap (26) is the dispatcher's, outside the host.
- **Rule 5's durable intent row.** A repeat of a request id is answered from memory; after a host restart the session is simply
  running, which refuses. Not an op the gate audit knows; a follow-up if Quality wants it.
- **Sessions launched before this ships have no recorded role,** so nobody can resume them by this op (fail closed) until
  they are relaunched by a person; the owner resumes those by hand meanwhile.
- **A resumed Copilot worker is not narrowed to its role's `launch.workerMcp` list.** Resume restores the recorded launch settings (profile, model, effort, context, permissions) and the worker `role` is not part of them, so a resumed worker keeps the MCP servers its profile gives it, not the narrower set its original role chose. Follow-up if the owner wants it narrowed.
- **`agents.resume.roles` is the owner's step:** empty (the default) means resume is off.
- **No new coordinators:** a coordinator session is only ever started by a person (owner decision, item 2).

## Defaults taken (owner CHANGE if wrong)

- A running session is refused rather than restarted (the decision says "resume or restart an existing session"; restart is
  stop (#2741) then resume, two audited steps).
- Coordinator roles come from the person-set `agents.resume.roles` list (empty = resume off); the host has no other
  source of a role today (see above).
- Sessions only a person launched stay out of reach (R10: "refused for person-launched-only").
- The MCP wrapper (`resume_agent_session`) follows with the .NET server port; it is a thin wrapper over `fleet.resume`.

## Where it runs, what happens when the node leaves, where state lives

The host on the machine that owns the session (one machine; nothing crosses machines). If the host stops, nothing is
pending: a resume is one request and its intent row is in the host's audit log. State: the launch record and audit log in
the host's own folder, as for launch and stop.
