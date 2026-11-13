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
  "machines": [ { "machine", "state", "machineName"?, "lastSeen"? } ]
}
```

- Only the caller's own account is listed; the relay sends no other account's machines, and the request carries no account or principal.
- Only those fields come back: no prompt or question text, paths or tokens. `name` is a person's text, so it is returned only with `include: "content"`. The additive `machines` field does not change the existing `sessions` projection; older clients may ignore it.
- `machineName` is the relay's existing machine display name, independent of `include`: at most 64 raw UTF-16 code units, then trimmed. Control, format, unpaired surrogate and line/paragraph separator characters (Cc, Cf, Cs, Zl, Zp) are rejected before trimming; ordinary Unicode and valid emoji pairs stay. Missing, non-string, empty, overlong or invalid values are omitted, never guessed. It is display text, not an identity; `machine` remains the ID.
- `machines` includes the caller's account-scoped registry and `known` machines, ordered by machine ID. Its `state` is `online` or `offline`. An expired registry record stays offline; a known-only machine uses the relay's supplied socket `online` flag, so a connected host without a record remains online, without invented sessions. A missing flag from an older relay is treated as offline. Registry liveness and supplied metadata win regardless of frame order; `known` fills only missing last-seen metadata and missing display names on machines with no sessions. A registry machine with sessions keeps its original display name, including omission, so session and lookup replies stay unchanged. `known` never overrides a registry machine's liveness.
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

Names come only from host-owned `FleetNames`: explicit person-only `session.rename`
(blank clears), or the existing fixed SG-name rule for a new host-created launch.
Copilot `workspace.yaml` `user_named` and every title, summary, prompt, folder or HA label
are never sources. Enrollment must say: **"The relay sees this label."** Choose a
public-safe role label; the trimmed 1-64 ASCII `A-Za-z0-9 _.+()-` filter cannot prove
ordinary words are non-private. Names remain untrusted content, only explicitly included
in `fleet.list`; `fleet.find` never echoes them or grants agent rename authority.

This **intentionally changes the implemented ambiguous-on-verified-duplicates branch**.
Today in practice every live name match is `unverified` (unless `incomplete` wins):
`RelayFleetRoster.Parse` never sets `Verified` (`src/AgentBridge.Host/Fleet/FleetList.cs:13,50-103`).
Only verified fixtures reach today's `ambiguous`/`found` branches (`FleetList.cs:185-204`).
For eligible duplicates on distinct machines, future callers see `found` for one strictly
newest process only when every live match verifies and carries a valid signed host-observed `startedAt`.
Equal, missing or unprovable ordering returns an explicit `ambiguous` refusal, never a
chosen session. Offline/stale predecessors do not compete. Never order by state `since`,
record `at`, `seq`, epoch, arrival or node text.
One legacy unsigned same-name machine blocks selection during rollout. The planned wire
start uses UTC `YYYY-MM-DDTHH:mm:ssZ`, floored whole seconds; it is not exposed to agents.
Cross-machine selection needs independently trusted start-time error bounds <=60 seconds
per host and a gap strictly >122 seconds to every competing live match. Closer pairs or
missing historical clock evidence remain ambiguous; fresh snapshot time is not that evidence.
If any host contributes multiple live matches, S-N3 stays ambiguous, even in mixed-host
lookups and with a signed whole-second gap >122 s. A gap alone is no same-host ordering
proof; no evidence contract is defined here. A separate accepted contract/owner/tests must unlock it.
The approved single-host newest-verified-live rule is deferred by this ambiguity hold; Product decides its successor contract.
S-N1 uses the host's private local-name/scoped-relay data inside the existing App/HostCli
rename path, never agent-only fleet ops. Its pre-save read has a 2 s monotonic deadline;
expiry yields unknown, cancels the read and ignores late results without retry. Unknown does not
block saving; caller cancellation during the check aborts. Only save success returns the warning; save failure
returns the normal error, not a saved result. Static disclosure is before the call.
Same-user tampering can still squat a name; the warning-result fixture must fail on today's id/name-only response.
The warning/status is no uniqueness proof.

`startedAt` stays identical when only the host restarts, and renews for a resumed/replaced
session process. The four workstreams (six PRs, each <=400 lines) require a host-restart test
with a still-live session process, not an inferred filesystem or transcript time.
S-N2a/S-N4 own restart; null observation omits the field. No S-N slice enables routing.
S-N3 needs #1776's scoped signing/key/seq/epoch/at checks plus separately accepted owners
for retained roster/cache, reader revocation, creation-order/clock evidence and trusted
start-time error bounds. Those extras are unowned blockers here, not assigned to #1776.
#1776's separately enrolled host signing and independently trusted
reader state gate verification; no interim key. Current unsigned matches stay
`unverified`, without a routable node. Incomplete-before-unverified precedence,
authenticated account scope, optional machine filter and caller machine/node pinning
remain unchanged.
