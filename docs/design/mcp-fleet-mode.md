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

## Owner decision on messaging scope (Nov 23, exact time not supplied)

The owner answered an ask_user question from Chief S18, relayed by Chief S18 to Quality R9, choosing the option labelled "Same person's
agents only (Recommended)". `docs/owner-decisions.md` (slipgates-fleet, Nov 23) records it in the record's own words, not the owner's:
"an agent may message only agent sessions of the SAME signed-in person, on any of that person's machines; nothing crosses people. This
records the default only, not the code." Design: [beta-messaging.md](beta-messaging.md).

What it decides is **whom agents may message by default**. It does not decide any MCP tool. The text above it, which this proposal
would change **for reply only**, is:

> **Read only.** Fleet mode lists; it does not reply to, stop or launch anything on another machine. Those stay local-only and keep their existing authorization.

and the "Not proposed" bullet "Acting on a remote session (reply, stop, launch) from the MCP server." Under the decision's substance,
a **reply to a same-person session on another machine** is the one item proposed to move out of "Not proposed" (the departure table below,
row 5, for the Chief to confirm); stop and launch, keys and every other bullet stay owner CHANGES. `scope: "fleet"` (a read) and
`machineName` stay Engineering proposals with their own open rows 1 to 4.

## Not proposed, each an owner CHANGE if wanted

- Returning `cwd`, titles, repository, branch or any question text for another machine's sessions.
- Acting on a remote session (reply, stop, launch) from the MCP server. **Amended for reply only (departure row 5, for the Chief to confirm):** a reply to a same-person session on another machine; stop and launch stay here.
- A new relay field or op for this; the existing `fleet.list` is enough.
- A default of fleet scope.

## Engineering rulings (R10), folded in (proposals; see the departures below)

1. **Local pass-through via the host.** The MCP process never holds a relay credential: fleet scope is answered by asking the local host,
   which makes the relay call with its own identity. The server stays local-only on the wire; no new trust edge.
2. **`machineName` is untrusted display text**, returned only with `include: "content"`, never used to route, select or decide anything.
   Routing and identity use the `machine` id, never the name.

Both stay inside the owner CHANGE rule: any departure (a credential in the MCP process, returning `machineName` by default, acting on a
remote session) is an owner CHANGE routed through the Chief, not a builder choice.

## Contributor-workflow questions (docs/contributor-workflow.md, "nothing tied to a machine")

1. **Where does it run?** Nothing new runs anywhere. The MCP server is the per-client local process it already is; the fleet list is
   answered by the **existing host pipe op** `fleet.list` on whichever host the client is already connected to (any one live node
   is enough). No watcher, scheduler, cache or designated machine is added.
2. **What happens when that node leaves?** The MCP process ends with its client; another client starts its own. If the host it talks to leaves,
   the call fails with a typed refusal (never a guess, never a stale answer); the other machines are unaffected and, when a host is back
   or another is used, the same call works. A machine that has left shows as `machineOnline: false` or `complete: false`, not as empty.
3. **Where does its state live?** **Nowhere new.** The MCP process stores nothing and caches nothing; the relay's account-scoped registry
   is the only source, and the audit record belongs to the host's audit log (#1401), not to the MCP process.

## Hardening rules for the proposal (each follows the existing contracts; none is an additional departure)

- **The op is the host's existing pipe op.** `fleet.list` needs a connection that has passed `agent.hello` with at least the `read` scope
  (`local-pipe.md`, `agent-operations.md`). An **unattributed connection is refused**: it is never served locally, never served from a
  cached copy and never answered through Home Assistant.
- **Exact row allowlist.** A fleet row is exactly `machine`, `machineName` (see next point), `node`, `state`, `since`, `secondsInState`,
  `machineOnline`, `name` (content only) and, when the relay sends them, the closed-set `attention.kind`, `openAsks` and `observedAt`. A
  machine row is exactly `machine`, `state`, `machineName`, `lastSeen`, `complete`. Any other member the host returns is dropped by the server,
  not passed on.
- **`machineName` and `name` are plain text.** They render as text only: no markup, no link, no formatting. The MCP server **strips** control,
  format, line/paragraph-separator and bidirectional characters from both before returning them (the relay already rejects most for
  `machineName`; the server does not rely on that), caps their length, and never uses either to route, select or decide.
- **Audit.** Each accepted fleet list is audited by the host through the existing `AuditFleetList` hook with the agent's principal (it records
  nothing until #1401 is assigned to it; this proposal does not change that).
- **Role limits are enforced by the host**, as for every other agent op: the MCP server does not decide who may list, and a role or scope the
  host refuses is refused to the MCP caller unchanged.

## Terms

- **R10** is the **Engineering Lead**: the Slipgates Engineering Lead session that reviews and routes builder work, and sends owner CHANGE
  proposals to the owner through the Chief. It is a role, not a machine or a person.

## Departures from the existing docs: UNAPPROVED owner CHANGE proposals

This note is a **proposal**. Each point below departs from a canonical document as it stands today. Every one is an **unapproved owner CHANGE
proposal**; none is decided, and **the canonical documents are edited only when the owner approves** (this note's own text is edited here, and the two table rows below name the canonical documents the `machine` argument would change; no canonical document other than this one gets a rule change).

| # | Existing source (as it stands) | What this proposal would change |
|---|---|---|
| 1 | `docs/design/mcp-s4-evidence.md`, cut **C6 / 27**: expose the existing `fleet.list` as an **explicit credentialed read, not a broadened local list**; depends on F, audit #1401 and the #55 account/roster contract | An opt-in `scope: "fleet"` argument on `list_agent_sessions` (the local list gains a mode) answered by the local host as a pass-through. C6 asks for a separate explicit credentialed read, and its dependencies (#1401, #55) are not met by this note |
| 2 | `mcp/README.md` (not in this slice: ..., **other machines**) | The MCP server would list other machines' sessions |
| 3 | `docs/protocol/local-pipe.md` (MCP: **this machine only**; `machine` arguments are refused) | A fleet-scoped list through the local pipe, which is no longer this machine only |
| 4 | `docs/protocol/agent-operations.md` (`machineName` is the relay's display name, **independent of `include`**) | `machineName` returned only with `include: "content"` in MCP output (untrusted display text), where the relay contract returns it regardless |
| 5 | `docs/protocol/local-pipe.md`, "The MCP server" (it maps `reply_to_agent_session` to `session.reply`, this machine only; a `machine` argument is refused) | `reply_to_agent_session` takes an optional `machine` and, with it, the local host sends through `agent.send` (a reply to a same-person session on another machine). **For the Chief to confirm** that the Nov 23 messaging-scope decision covers it |
| 6 | `mcp/README.md` ("Not in this slice: ... other machines"; a `machine` argument is ignored) | The same `machine` argument, in the .NET server (beta-messaging.md section 4). slipgates-fleet `fleet-tools-to-host.md` ("MCP local only") needs a matching edit once row 5 is settled |

If the owner prefers the existing C6 shape (a separate credentialed read tool, not a mode of the local list), points 1 to 3 change to follow it and
this note is rewritten; point 4 stands or falls on its own.

## Tests when built (tests first)

Default scope is local and byte-identical to today; `scope: "fleet"` returns only the fields above; `name` absent without
`include: "content"`; no `cwd` in any fleet row under any option; an unknown `scope` is refused; a partial machine is reported
with `complete: false`. Failure and audit tests: a connection that has not passed `agent.hello` is refused (and not served locally or by Home Assistant); one with no `read` scope is refused; the audit hook is called once per accepted call with the principal and not on a refusal; a relay or host failure returns a typed error and never falls back to a local list; a member outside the row allowlist is dropped; control and bidirectional characters in `machineName` and `name` are stripped. Source-scan row in the HA-off evidence set that the fleet path adds no HA reference.

Not verified: that the relay's `fleet.list` reply matches `agent-operations.md` on a live relay; this note was written from the document.
