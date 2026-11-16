# Agent operations

Operations an agent calls on the local pipe after `agent.hello`, on its own credential. Each is refused with a readable `refused: ...` reason when the connection has no agent credential or the credential's scope is too low.

| Operation | Scope | What it does |
| --- | --- | --- |
| `agent.send` | read+reply | Sends text to one session; see [local-pipe.md](local-pipe.md#agentsend-ap5-lite). |
| `fleet.list` | read | Lists the sessions and online/offline machines of the agent's own account, with optional machine display names. |
| `fleet.find` | read | Finds one session of the agent's own account by exact name, with optional machine display names; answers `unverified` until records are signed. |

## `fleet.list`

```json
{ "id": 3, "op": "fleet.list", "data": { "include": "content" } }
```

`include` is optional. Reply:

```text
{
  "sessions": [ { "machine", "machineName"?, "node", "state", "since", "secondsInState", "machineOnline", "name"? } ],
  "machines": [ { "machine", "state", "machineName"?, "lastSeen"?, "complete" } ]
}
```

- Only the caller's own account is listed; the relay sends no other account's machines, and the request carries no account or principal.
- Only those fields come back: no prompt or question text, paths or tokens. `name` is a person's text, so it is returned only with `include: "content"`. The additive `machines` field does not change the existing `sessions` projection; older clients may ignore it.
- `machineName` is the relay's existing machine display name, independent of `include`: at most 64 raw UTF-16 code units, then trimmed. Control, format, unpaired surrogate and line/paragraph separator characters (Cc, Cf, Cs, Zl, Zp) are rejected before trimming; ordinary Unicode and valid emoji pairs stay. Missing, non-string, empty, overlong or invalid values are omitted, never guessed. It is display text, not an identity; `machine` remains the ID.
- `machines` includes the caller's account-scoped registry and `known` machines, ordered by machine ID. Its `state` is `online` or `offline`. An expired registry record stays offline; a known-only machine uses the relay's supplied socket `online` flag, so a connected host without a record remains online, without invented sessions. A missing flag from an older relay is treated as offline. Registry liveness and supplied metadata win regardless of frame order; `known` fills only missing last-seen metadata and missing display names on machines with no sessions. A registry machine with sessions keeps its original display name, including omission, so session and lookup replies stay unchanged. `known` never overrides a registry machine's liveness.
- `complete` is true only when the machine is online and an untruncated registry snapshot has been received, including an explicitly empty snapshot. Offline, truncated and online-known-only machines report false. A reader may treat a name as absent only when every machine reports `complete: true`.
- `lastSeen` is the existing relay metadata timestamp, serialized like session `since`. Missing or null metadata omits the field; no current-time or other default is invented. Invalid timestamps fail the read explicitly. No new relay disclosure or persistence is added.
- A machine that is offline or has not reported lately has no fresh record, so its sessions are not listed (`machineOnline` is true for every row returned from the relay). `fleet.find` verification, matching and result precedence are unchanged; a known-only machine has no session to match.
- Each accepted call invokes the `AuditFleetList` hook with the agent's principal. It records nothing until the audit log (#1401) is assigned to it.
- Reads the relay registry including its terminal `known` metadata (see [roster.md](roster.md)); needs a relay connection. The transport's default registry read remains records-only; the fleet reader explicitly includes `known`.

## `fleet.find`

The table lists implemented selector branches; production matches stay `unverified`
(or `incomplete`). Engineering approved the
[#2360 replacement contract](../design/fleet-session-registry.md#approved-replacement-contract-2360-not-implemented)
as a future change; this documentation PR does not implement it or enable routing.

```json
{ "id": 4, "op": "fleet.find", "data": { "name": "SG Quality" } }
```

Optional `"machine"` (1 to 64 of letters, digits, `.`, `_`, `-`) limits the match to that machine. Replies, per [the design](../design/fleet-session-registry.md) ("Names and lookup"), the first rule that applies:

| `result` | Meaning | Body |
| --- | --- | --- |
| `incomplete` | A live machine in scope published a truncated list, so a duplicate could be hidden | `machines`: ids |
| `unverified` | A live unverified match exists, even beside a verified match; #1776 verification is not integrated today | `machines`: `{ machine, online }`, no node |
| `ambiguous` | Two or more verified live matches, including on one machine | `candidates`: `{ id, node, machine, state, since, online, model? }` |
| `offline` | Matches only on offline machines | `machines`: `{ machine }` |
| `found` | One verified live match, with or without a `machine` filter | `session`: `{ id, node, machine, state, since, online, model? }` |
| `notFound` | Otherwise | none |

- Every machine object, session and candidate above also carries optional `machineName` under the same rules as `fleet.list`; `incomplete` remains an array of IDs. A display name is never a machine pin.
- Every result carries `fleetScope`, the authenticated relay account ID captured with the roster. Request `account`/`fleetScope` fields are ignored; missing authenticated scope refuses explicitly. Reconnect during the read refuses rather than mixing scopes.
- Name-only lookup is canonical (#2151); offline matches do not create a live duplicate. `model` is carried only when the authenticated roster supplies a string, never inferred from caller data or names. Missing model is omitted, so model-dependent callers fail closed. No publisher or relay allow-list change supplies model today.
- Until #1776's signing and trusted reader integration lands no record is verified, so today a live match answers `unverified` and `found` is not returned: a name alone, or an impostor machine of the same account beside an offline original, never yields a routable node. Protected key custody (#1782) alone is insufficient. **Deviation from issue #1399**, which asked for a plain found/ambiguous/not-found; the design governs.
- The name is trimmed, then matched exactly and case-sensitively; it is refused if empty or over 64 characters (before any audit entry). It is never echoed back, so the call does not taint the caller with session text.
- Same account, scope and refusal rules as `fleet.list`. Each accepted call invokes the same `AuditFleetList` hook.

### Approved future name enrollment and replacement behavior (#2360)

The [canonical #2360 design](../design/fleet-session-registry.md) owns the complete
enrollment, disclosure, warning, ordering, dependency and failing-first test contracts.
Only explicit host-owned names publish; no inferred fallback or agent rename.
After incomplete/unverified checks, same-host live duplicates stay ambiguous; cross-host proof is specified there.
This is not implemented. #1776 and the design's unowned reader/clock prerequisites
still gate verification and routing: no interim key and no unsigned `found`.
