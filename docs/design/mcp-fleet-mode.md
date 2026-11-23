# MCP fleet mode: design note (docs only, nothing built)

Status: **proposal for the Engineering Lead; no code, no behaviour change.** Part of #1396.
Today the MCP server's `list_agent_sessions` lists **this machine only** (it calls the local `sessions.list`); there is no way to
ask "which sessions exist across my machines". This note proposes how a fleet listing could be added without widening what the
server already refuses to show. Any departure from the rules below is an **owner CHANGE**, not a builder choice.

## The source

The relay's `fleet.list` (`docs/protocol/agent-operations.md`): `sessions[ {machine, machineName?, node, state, since, secondsInState,
machineOnline, name?} ]` and `machines[ {machine, state, machineName?, lastSeen?, complete} ]`. Only the caller's own account is
listed; no prompt or question text, paths or tokens come back. `name` is a person's text and is returned only with
`include: "content"`. The optional registry evidence members (`attention`, `openAsks`, `observedAt`; #4086) ride on the same record and
are closed-set values, never text.

## Proposed shape (one tool, one option)

- Keep `list_agent_sessions` as is (local, its documented default). Add an **opt-in `scope: "fleet"`** argument; absent means local.
  No separate tool, so a client that never passes it sees no change.
- Fleet rows carry **only** `machine`, `machineName`, `node`, `state`, `since`, `secondsInState`, `machineOnline`, plus the closed-set
  evidence members when the relay sends them. Machine completeness (`complete`) is returned so a partial machine is not read as empty.
- **`name` is untrusted text** (a person or agent chose it). It is returned only with `include: "content"`, as the relay already
  requires, and the tool description says so; the server never treats it as an instruction and never uses it to pick a target.
- **`cwd` stays hidden by default** (the owner decision recorded in `mcp-s4-evidence.md`); fleet rows have no `cwd` at all, and
  `include: "content"` does not add one, because `fleet.list` does not carry it. A fleet row is not a way around the local default.
- **Read only.** Fleet mode lists; it does not reply to, stop or launch anything on another machine. Those stay local-only and
  keep their existing authorization.

## Not proposed, each an owner CHANGE if wanted

- Returning `cwd`, titles, repository, branch or any question text for another machine's sessions.
- Acting on a remote session (reply, stop, launch) from the MCP server.
- A new relay field or op for this; the existing `fleet.list` is enough.
- A default of fleet scope.

## Open questions for the Engineering Lead

1. Does the MCP server hold (or may it be given) a relay credential for `fleet.list`, or must it go through the local host as a
   pass-through? The second keeps the server local-only on the wire; the first is a new trust edge.
2. Is `machineName` acceptable as returned, or is it also untrusted text to withhold without `include: "content"`?

## Tests when built (tests first)

Default scope is local and byte-identical to today; `scope: "fleet"` returns only the fields above; `name` absent without
`include: "content"`; no `cwd` in any fleet row under any option; an unknown `scope` is refused; a partial machine is reported
with `complete: false`. Source-scan row in the HA-off evidence set that the fleet path adds no HA reference.

Not verified: that the relay's `fleet.list` reply matches `agent-operations.md` on a live relay; this note was written from the document.
