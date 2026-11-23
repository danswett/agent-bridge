# Agent operations

Operations an agent calls on the local pipe after `agent.hello`, on its own credential. Each is refused with a readable `refused: ...` reason when the connection has no agent credential or the credential's scope is too low.

| Operation | Scope | What it does |
| --- | --- | --- |
| `agent.send` | read+reply | Sends text to one session; see [local-pipe.md](local-pipe.md#agentsend-ap5-lite). |
| `agent.receipt` | read+reply | Asks what the first attempt of one of the agent's own unconfirmed `agent.send` calls became on the target; see [local-pipe.md](local-pipe.md#agentreceipt-receipt-lookup). Scripts are refused, so a script's unconfirmed send gets no lookup. |
| `fleet.list` | read | Lists the sessions and online/offline machines of the agent's own account, with optional machine display names. |
| `fleet.find` | read | Finds one live session of the agent's own account by session ID; unsigned matches answer `unverified` except for messaging-only `trust: "own-account"` below. |

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
- Sessions also carry optional observed evidence, following [the canonical contract](https://github.com/danswett/agent-bridge/issues/2688): `stale` (host-threshold boolean), `attention: {kind}`, `openAsks` (count), `unsentLength` (count), `unsentHash`, `observedAt`, `lastReplyAt`, `provenance`, and `senderVerified`.
- Attention kinds are `none`, `question`, `approval`, `trust`, `permission`, `unknown`. A future kind reads as `unknown`; consumers must refuse action on unknown attention. Ingress rejects unknown kinds as `bad-record`.
- Counts are integers from 0 through 2147483647. Evidence timestamps are UTC ISO-8601 instants ending in `Z`, with up to seven fractional digits; invalid timestamps fail the read. Missing evidence stays absent, never guessed or defaulted.
- Provenance is a bounded object with required `source` (`host-observed` or `relay-supplied`) and optional observed `sendMarker` (boolean) and `unsentHash`. Hashes are exactly 64 lowercase hex characters, produced only by per-host keyed HMAC-SHA256, never plain SHA256 or composer text. Supplied provenance/composer hashes must match.
- `senderVerified` is `true`, `false` or `"unknown"` only when verified or explicitly observed as unknown; provenance is emitted only when observed. Neither is inferred from `DriverPrincipal`. Malformed, duplicate, oversize or unlisted evidence is `bad-record` at ingress (the existing whole-record size cap still returns `too-large`). No producer capture is added by this projection change; legacy rows remain unchanged.
- `owner`, `pr` and `finished` carry the host roster's outcome evidence across hosts ([v1-sessions.md](v1-sessions.md), #2643): `{"state":"unknown"}`, `{"state":"known"}` with exactly `principal` (1 to 128 printable ASCII), `number` (a positive integer) or `value` (boolean) respectively, and for `pr` only `{"state":"none"}`. Hosts publish only known or none values; absent means unknown, never none or false. The relay must accept these members before any host publishes a known value, because older relays refuse unknown members.
- `machineName` is the relay's existing machine display name, independent of `include`: at most 64 raw UTF-16 code units, then trimmed. Control, format, unpaired surrogate and line/paragraph separator characters (Cc, Cf, Cs, Zl, Zp) are rejected before trimming; ordinary Unicode and valid emoji pairs stay. Missing, non-string, empty, overlong or invalid values are omitted, never guessed. It is display text, not an identity; `machine` remains the ID.
- `machines` includes the caller's account-scoped registry and `known` machines, ordered by machine ID. Its `state` is `online` or `offline`. An expired registry record stays offline; a known-only machine uses the relay's supplied socket `online` flag, so a connected host without a record remains online, without invented sessions. A missing flag from an older relay is treated as offline. Registry liveness and supplied metadata win regardless of frame order; `known` fills only missing last-seen metadata and missing display names on machines with no sessions. A registry machine with sessions keeps its original display name, including omission, so session and lookup replies stay unchanged. `known` never overrides a registry machine's liveness.
- `complete` is true only when the machine is online and an untruncated registry snapshot has been received, including an explicitly empty snapshot. Offline, truncated and online-known-only machines report false. A reader may treat a name as absent only when every machine reports `complete: true`.
- `lastSeen` is the existing relay metadata timestamp, serialized like session `since`. Missing or null metadata omits the field; no current-time or other default is invented. Invalid timestamps fail the read explicitly. No new relay disclosure or persistence is added.
- A machine that is offline or has not reported lately has no fresh record. Its last accepted rows are kept in memory and listed with `machineOnline: false` and `stale: true` (#2643), for at most 24 hours. A refused out-of-order record also leaves them stale. They drop at once when the machine leaves the relay's `registry` and `known` lists, the signed device roster, or this host's relay connection. Once the host holds a pinned, verified device roster, only its serving hosts are read; without a pin, rows pass unverified as before. A pin that is unreadable, another account's or not yet verified refuses the read instead (#3262). `fleet.find` verification, matching and result precedence are unchanged; stale rows are never live, and a known-only machine has no session to match.
- Each accepted call invokes the `AuditFleetList` hook with the agent's principal. It records nothing until the audit log (#1401) is assigned to it.
- Reads the relay registry including its terminal `known` metadata (see [roster.md](roster.md)); needs a relay connection. The transport's default registry read remains records-only; the fleet reader explicitly includes `known`.

## `fleet.find`

The table lists implemented selector branches. The [own-account lite exception](agent-lite.md) allows fresh present-host matches to route messaging with `trust: "own-account"`, without roster verification; off or kill refuses this routing. Other unsigned matches stay `unverified` (or `incomplete`). Engineering approved the
[#2360 replacement contract](../design/fleet-session-registry.md#approved-replacement-contract-2360-not-implemented)
as a future change; signed replacement selection remains unimplemented.

```json
{ "id": 4, "op": "fleet.find", "data": { "sessionId": "agent_bridge_1234567890abcdef" } }
```

The exclusive `sessionId` accepts a live node, its bare 16 lowercase hex digits, or a full canonical UUID published by the roster and bound to that node. No UUID truncation, name or substring matching. Supplying `name` or `machineName` refuses, even beside a valid ID. Optional `machine` is an exact device ID (32 hex digits; publishers use canonical lowercase and comparisons are exact), never a display name. Replies retain the following trust/completeness precedence:

| `result` | Meaning | Body |
| --- | --- | --- |
| `incomplete` | A live machine in scope published a truncated list, so a duplicate could be hidden | `machines`: ids |
| `unverified` | A live unverified match without own-account messaging trust exists, even beside a verified match | `machines`: `{ machine, online }`, no node |
| `ambiguous` | Two or more verified or own-account messaging live matches, including on one machine | `candidates`: `{ id, node, machine, state, since, online, model?, trust? }` |
| refusal | Unknown, stale or offline ID; nothing routable | Readable `refused:` reason naming the requested ID |
| `found` | One verified live match or one own-account messaging live match, with or without a `machine` filter | `session`: `{ id, node, machine, state, since, online, model?, sessionId?, trust? }` |

- Every machine object, session and candidate above also carries optional `machineName` under the same rules as `fleet.list`; `incomplete` remains an array of IDs. A display name is never a machine pin.
- Every result carries `fleetScope`, the authenticated relay account ID captured with the roster. Request `account`/`fleetScope` fields are ignored; missing authenticated scope refuses explicitly. Reconnect during the read refuses rather than mixing scopes.
- ID-only lookup supersedes #2151/#2574's name lookup ([#3607](https://github.com/danswett/agent-bridge/issues/3607)); offline matches do not create a live duplicate. `model` is carried only when the authenticated roster supplies a string, never inferred from caller data or names. Missing model is omitted, so model-dependent callers fail closed.
- `found.session.sessionId`, when present, is the full lowercase hyphenated UUID (`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`). The publisher carries the source session ID, only to a relay whose `ready` advertises the `rosterSessionId` capability ([roster.md](roster.md)), and the finding host verifies `node == "agent_bridge_" + sessionId.Replace("-", "")[..16]` before exposing it. Missing, noncanonical or mismatched values are omitted; consumers must refuse an unverified ID, never truncate or prefix-match a caller UUID. Existing 16-hex `id` and `node` are unchanged; no role ID or name-derived value is used.
- Until #1776's signing and trusted reader integration lands no record is roster-verified. The temporary own-account exception returns `session.trust: "own-account"` (also `trust` on fresh `fleet.list` session rows) for messaging only; it never grants launches, stop or remote manage. Registry account and presence come from one connection snapshot and must still agree with the host's current identity account when the read completes; a switch refuses `notSameAccount`. Actual sends still check the same account and channel key matching the device id. Other unsigned matches remain `unverified`, without a routable node.
- IDs are exact and untrimmed. A rename or successor reusing a name cannot retarget a lookup; names remain display text. Invalid selectors refuse before auditing or reading the roster. Role-ID routing and claims remain paused.
- Same account, scope and refusal rules as `fleet.list`. Each accepted call invokes the same `AuditFleetList` hook.

### Historical name enrollment and replacement proposal (#2360)

The [canonical #2360 design](../design/fleet-session-registry.md) owns the complete
enrollment, disclosure, warning, ordering, dependency and failing-first test contracts.
Only explicit host-owned names publish; no inferred fallback or agent rename.
After incomplete/unverified checks, same-host live duplicates stay ambiguous; cross-host proof is specified there.
Name-based routing is superseded by ID-only lookup; this proposal grants no routing or role-claim authority. #1776 and the design's unowned reader/clock prerequisites
still gate roster verification and replacement selection: no interim key and no unsigned `found` beyond the messaging-only own-account exception above.
