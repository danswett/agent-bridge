# Agent operations

Operations an agent calls on the local pipe after `agent.hello`, on its own credential. Each is refused with a readable `refused: ...` reason when the connection has no agent credential or the credential's scope is too low.

| Operation | Scope | What it does |
| --- | --- | --- |
| `agent.send` | read+reply | Sends text to one session; see [local-pipe.md](local-pipe.md#agentsend-ap5-lite). |
| `fleet.list` | read | Lists the sessions of the agent's own account, across its machines. |
| `fleet.find` | read | Finds one session of the agent's own account by exact name. |

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

Reply: `{ "result": "found" | "not-found" | "ambiguous", "matches": [ { "machine", "node", "state", "since", "secondsInState", "machineOnline" } ] }`.

- A filter over the `fleet.list` roster, not a second source. The match is exact and case-sensitive.
- `found` has one match, `ambiguous` has every session sharing the name (never a silent pick), `not-found` has none (also the answer for a machine that is offline, since its sessions are not listed).
- The caller supplied the name, so it is not returned: only typed fields come back, and the call does not taint the caller with session text.
- Same account, scope and refusal rules as `fleet.list`; a missing or empty `name` is refused. Each accepted call invokes the same `AuditFleetList` hook.