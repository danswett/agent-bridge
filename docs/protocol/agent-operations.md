# Agent operations

Operations an agent calls on the local pipe after `agent.hello`, on its own credential. Each is refused with a readable `refused: ...` reason when the connection has no agent credential or the credential's scope is too low.

| Operation | Scope | What it does |
| --- | --- | --- |
| `agent.send` | read+reply | Sends text to one session; see [local-pipe.md](local-pipe.md#agentsend-ap5-lite). |
| `fleet.list` | read | Lists the sessions of the agent's own account, across its machines. |
| `fleet.find` | read | Finds one session of the agent's own account by exact name; answers `unverified` until records are signed. |

## `fleet.list`

```json
{ "id": 3, "op": "fleet.list", "data": { "include": "content" } }
```

`include` is optional. Reply: `{ "sessions": [ { "machine", "node", "state", "since", "secondsInState", "machineOnline", "name"? } ] }`.

- Only the caller's own account is listed; the relay sends no other account's machines, and the request carries no account or principal.
- Only those fields come back: no prompt or question text, paths or tokens. `name` is a person's text, so it is returned only with `include: "content"`.
- A machine that is offline or has not reported lately has no fresh record, so its sessions are not listed (`machineOnline` is true for every row returned).
- Each accepted call invokes the `AuditFleetList` hook with the agent's principal. It records nothing until the audit log (#1401) is assigned to it.
- Reads the relay registry (see [roster.md](roster.md)); needs a relay connection.

## `fleet.find`

```json
{ "id": 4, "op": "fleet.find", "data": { "name": "SG Quality" } }
```

Optional `"machine"` (1 to 64 of letters, digits, `.`, `_`, `-`) limits the match to that machine. Replies, per [the design](../design/fleet-session-registry.md) ("Names and lookup"), the first rule that applies:

| `result` | Meaning | Body |
| --- | --- | --- |
| `incomplete` | A live machine in scope published a truncated list, so a duplicate could be hidden | `machines`: ids |
| `unverified` | A live match exists and records are unsigned (no `hostSign` yet) | `machines`: `{ machine, online }`, no node |
| `ambiguous` | Two or more verified live matches, or one plus a match on an offline machine | `candidates`: `{ id, node, machine, state, since, online }` |
| `offline` | Matches only on offline machines | `machines`: `{ machine }` |
| `unpinned` | One verified live match and no `machine` given | `machines`: `{ machine, online, verified }` |
| `found` | One verified live match on the given `machine` | `session`: `{ id, node, machine, state, since, online }` |
| `notFound` | Otherwise | none |

- Until `hostSign` exists no record is verified, so today a live match answers `unverified` and `found` is not returned: a name alone, or an impostor machine of the same account beside an offline original, never yields a routable node. **Deviation from issue #1399**, which asked for a plain found/ambiguous/not-found; the design governs.
- The name is trimmed, then matched exactly and case-sensitively; it is refused if empty or over 64 characters (before any audit entry). It is never echoed back, so the call does not taint the caller with session text.
- Same account, scope and refusal rules as `fleet.list`. Each accepted call invokes the same `AuditFleetList` hook.
