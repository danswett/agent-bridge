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
- Fleet rows carry **only** `machine`, `machineName` (content-only, see the rulings), `node`, `state`, `since`, `secondsInState`, `machineOnline`, plus the closed-set
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

## Engineering rulings (R10), folded in (proposals; see the departures below)

1. **Local pass-through via the host.** The MCP process never holds a relay credential: fleet scope is answered by asking the local host,
   which makes the relay call with its own identity. The server stays local-only on the wire; no new trust edge.
2. **`machineName` is untrusted display text**, returned only with `include: "content"`, never used to route, select or decide anything.
   Routing and identity use the `machine` id, never the name.

Both stay inside the owner CHANGE rule: any departure (a credential in the MCP process, returning `machineName` by default, acting on a
remote session) is an owner CHANGE routed through the Chief, not a builder choice.

## Departures from the existing docs: UNAPPROVED owner CHANGE proposals

This note is a **proposal**. Each point below departs from a canonical document as it stands today. Every one is an **unapproved owner CHANGE
proposal**; none is decided, and **the canonical documents are edited only when the owner approves** (nothing in this PR edits them).

| # | Existing source (as it stands) | What this proposal would change |
|---|---|---|
| 1 | `docs/design/mcp-s4-evidence.md`, cut **C6 / 27**: expose the existing `fleet.list` as an **explicit credentialed read, not a broadened local list**; depends on F, audit #1401 and the #55 account/roster contract | An opt-in `scope: "fleet"` argument on `list_agent_sessions` (the local list gains a mode) answered by the local host as a pass-through. C6 asks for a separate explicit credentialed read, and its dependencies (#1401, #55) are not met by this note |
| 2 | `mcp/README.md` (not in this slice: ..., **other machines**) | The MCP server would list other machines' sessions |
| 3 | `docs/protocol/local-pipe.md` (MCP: **this machine only**; `machine` arguments are refused) | A fleet-scoped list through the local pipe, which is no longer this machine only |
| 4 | `docs/protocol/agent-operations.md` (`machineName` is the relay's display name, **independent of `include`**) | `machineName` returned only with `include: "content"` in MCP output (untrusted display text), where the relay contract returns it regardless |

If the owner prefers the existing C6 shape (a separate credentialed read tool, not a mode of the local list), points 1 to 3 change to follow it and
this note is rewritten; point 4 stands or falls on its own.

## Tests when built (tests first)

Default scope is local and byte-identical to today; `scope: "fleet"` returns only the fields above; `name` absent without
`include: "content"`; no `cwd` in any fleet row under any option; an unknown `scope` is refused; a partial machine is reported
with `complete: false`. Source-scan row in the HA-off evidence set that the fleet path adds no HA reference.

Not verified: that the relay's `fleet.list` reply matches `agent-operations.md` on a live relay; this note was written from the document.
