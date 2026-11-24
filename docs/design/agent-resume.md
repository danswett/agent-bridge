# Agents resume an existing session in place (design)

Part of program C (fleet tools into Slipgates), slice C4; issue number to follow (the Chief publishes it).
Companion to [#2741](https://github.com/danswett/agent-bridge/issues/2741) (an agent ends a session an agent started)
and [host-launch.md](../host-launch.md).

**Owner decision (about 19:52 PT, Nov 23, relayed by Chief S19 and R10):** any coordinator, SRE included, may resume or
restart an EXISTING session; a NEW coordinator still needs the owner. This replaces the v1 rule in
[agent-launch-scope.md](agent-launch-scope.md) (section 3, "agents can never resume", and the `resume` row, "refused in
v1"), which this change edits. Nothing else in that document changes: an agent still cannot launch with Allow all, resume
a person's session, or reach another machine.

## What it is

One host operation, `fleet.resume {version:1, request:<uuid>, session:<full uuid>}`, on the agent's local pipe, beside
`fleet.end` (same pattern: a live agent session credential with `read+reply+launch` scope at depth 0, not adopted; never a
script, the app or the CLI). It resumes the session **in place**: the same session id, the same folder and worktree, so its
open pull requests stay attached. It is the host's existing resume path (`ISessionResumer.ResumeAsync`, the one a restore
after a reboot uses), so every check a resume already meets still holds: agent-ha-bridge, `launch.enabled`, held discovery,
a folder-trust question waiting, the folder still approved, one launch at a time.

## Rules (each is a test)

1. **Provenance, not a name.** Only a session the host's own launch record shows an agent started through the fleet route
   (`LaunchProvenance`, every record for the session `Grant == "fleet"`). Unlike stop, the process is gone, so the record is
   matched by session id alone (a new `WasFleetLaunched`); an unfinished or unreadable record refuses (fail closed).
2. **Refused:** an unknown id; a session only a person launched; any session another person's host record names (nothing
   crosses people: this host's record is one person's); a derived (16-hex) id; a session already running (`running`; no
   force, no kill-and-restart, stop it first); the caller's own session; a kill switch, read-only host or excluded session.
3. **No new rights.** The resume uses what the host recorded for that session, never anything the caller supplies, and
   permissions are always `Ask`. The caller names a session and nothing else.
4. **Counts as a launch.** It takes a live-launch slot under the same agent limits as a launch, and frees it when the
   session ends, so resumes cannot exceed the cap.
5. **Logged and audited.** One audit row per request under the agent's principal and chain (`fleet.resume`: session,
   outcome, closed reason code), a host log line, and a durable intent row first, so a repeat of the same request id returns
   what was recorded and never resumes twice.
6. **Receipt.** `{request, outcome: resumed | refused | unconfirmed, reason?, session}`. Closed reasons reuse the fleet set
   (`scope`, `policy`, `kill-switch`, `bad-argument`, `duplicate-id`, `audit-unavailable`) plus `running`.

## Defaults taken (owner CHANGE if wrong)

- A running session is refused rather than restarted (the decision says "resume or restart an existing session"; restart is
  stop (#2741) then resume, two audited steps).
- Sessions only a person launched stay out of reach (R10: "refused for person-launched-only").
- The MCP wrapper (`resume_agent_session`) follows with the .NET server port; it is a thin wrapper over `fleet.resume`.

## Where it runs, what happens when the node leaves, where state lives

The host on the machine that owns the session (one machine; nothing crosses machines). If the host stops, nothing is
pending: a resume is one request and its intent row is in the host's audit log. State: the launch record and audit log in
the host's own folder, as for launch and stop.
