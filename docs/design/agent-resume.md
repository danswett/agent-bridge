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
display text. This change records, at launch, the launch's `role` (the validated role name the request already carries, in
`LaunchProvenance`), and adds a person-set host setting `agents.resume.roles` (the role names that count as coordinators on
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

## Rules (each is a test)

1. **Provenance, not a name.** Only a session the host's own launch record shows an agent started through the fleet route
   (`LaunchProvenance`, every record for the session `Grant == "fleet"`). Unlike stop, the process is gone, so the record is
   matched by session id alone (a new `WasFleetLaunched`); an unfinished or unreadable record refuses (fail closed).
2. **Refused:** an unknown id; a session only a person launched; any session another person's host record names (nothing
   crosses people: this host's record is one person's); a derived (16-hex) id; a session already running (`running`; no
   force, no kill-and-restart, stop it first); the caller's own session; a kill switch, read-only host, excluded caller, session, fleet name or workspace (the lists fleet.end honours), or a workspace not approved.
2a. **Own lane (owner decision, leads only).** A coordinator resumes only workers launched by a coordinator of its own ROLE. At launch the host
   records, on a fleet worker launched by an agent session, the launching coordinator's recorded role (the lane), never its session id, so a
   one-for-one successor with the same role keeps its workers. A resume keeps the lane it already had. `fleet.resume` refuses with `lane`
   (and an audit row) a worker whose recorded lane differs from the caller's role, and a worker with no recorded lane (a launch by a person or
   a script, or an older record), fail closed. A worker launched on another machine's grant records none here, so it is not resumable here.
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
   (`scope`, `policy`, `kill-switch`, `bad-argument`, `audit-unavailable`) plus `running`, `busy` and `lane`; a launcher failure is
   `unconfirmed` / `failed`.

## Known limits (not built in this change)

- **Rule 4's live-limit slot.** A resume does not take a slot under the host's live agent-launch limit: that gate is tied to the
  person-launch policy and is a follow-up. Double launches are still prevented (a running session refuses, one start per
  session, one request id resumes once); the shared builder cap (26) is the dispatcher's, outside the host.
- **Rule 5's durable intent row.** A repeat of a request id is answered from memory; after a host restart the session is simply
  running, which refuses. Not an op the gate audit knows; a follow-up if Quality wants it.
- **Sessions launched before this ships have no recorded role,** so nobody can resume them by this op (fail closed) until
  they are relaunched by a person; the owner resumes those by hand meanwhile.
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
