# Agent operations

Operations an agent calls on the local pipe after `agent.hello`, on its own credential. Each is refused with a readable `refused: ...` reason when the connection has no agent credential or the credential's scope is too low.

| Operation | Scope | What it does |
| --- | --- | --- |
| `agent.send` | read+reply | Sends text to one session; see [local-pipe.md](local-pipe.md#agentsend-ap5-lite). |
| `fleet.list` | read | Lists the sessions of the agent's own account, across its machines. |

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
