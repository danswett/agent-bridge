# Fleet grants: CLI contract

**Status: proposed contract only, brief 263, November 21, 2026 (Pacific Time).**
Docs only. It enables no code, grant or rollout. It fixes one
surface so the fleet wrappers and slices G5, G6, G9 and G10 of
[fleet-grants.md](fleet-grants.md) can be built in parallel. fleet-grants.md owns
authority, caps and storage; this file owns names, shapes, exit codes and refusal
codes. Tracking: https://github.com/danswett/agent-bridge/issues/3237.

## Owner decisions recorded here

- **Nov 21, 2026, 12:47 PT:** grants may launch Allow-all sessions, policy-bounded (`allowAllLaunch`, default false).
- **Nov 21, 2026, 12:50 PT:** own-account remote launch/stop use a signed fleet-action
  envelope (`FleetActionEnvelope v1`) under the existing own-peer trust; the receiver-side grant is the authority; no
  separate roster requirement; the account-wide kill switch stays.

## Verbs

All verbs are `agent-bridge-host` subcommands and take `[--profile <name>]`, which
is always the **host profile** on every verb. The `grants` and `fleet` verbs read
JSON (`--json`) and write exactly one JSON object to stdout on every exit, including
bad-argument (2) and unavailable (6) exits, which carry a `reason`; human text also
goes to stderr. `launches status|list` are unchanged and keep their own output
(see [Status read](#status-read)).

| Verb | Caller | Purpose |
|---|---|---|
| `grants issue --file <path>` / `--stdin` | Person only | Issue the grant document (fleet-grants.md, "Grant shape"); returns the receiver receipt |
| `grants revoke <id>` | Person only | Revoke with a durable tombstone |
| `grants list [--json] [--machine <node id>]` | Person, or the agent for its own subject | Receiver-side counters and state; read only, never acts |
| `fleet launch --grant <id> [--target <node id>] --request <uuid> ...` | Agent subject | Launch under a grant, local or remote |
| `fleet end --grant <id> --session <session-id> [--target <node id>] --request <uuid>` | Agent subject | Stop under a grant, local or remote |
| `launches status --request <uuid> --json` | Same user | Existing read of an outcome ([host-launch.md](../host-launch.md), #3533); wrappers always pass `--json` |

`grants issue` and `grants revoke` keep guard 67 and the person confirmation path:
an agent or script credential, or untraceable ancestry, exits 4 before any signing
or mutation. A grant cannot call either. Phone issuance is a later slice.

`fleet launch` and `fleet end` omit `--target` for the local host. `--target` is an
exact node (device) id, never a machine name; the local node id counts as local.
They do not replace the person `stop <session> [--confirm <token>]` verb, which is
unchanged. They supersede the `launch --grant` and `stop --grant` forms in
fleet-grants.md for agent use. `fleet end` calls pipe op `fleet.end` with the agent's own session credential only (a script, app or other caller is refused, `person-required` is surfaced, and no grant is created or bypassed); `still-running` may accompany exit 7.

## Request ids

- `--request` is a UUID the **caller generates fresh for each new operation**.
  Required; a missing or non-UUID value exits 2.
- Never reuse an id for a different payload; the receiver binds id to caller, grant,
  generation, action and normalized payload. A changed binding refuses `duplicate-id`.
- The only reason to resend an id is to ask again about the **same** operation
  (see [Uncertain outcomes](#uncertain-outcomes-never-auto-retry)). The same id with
  the same binding returns the recorded outcome and acts at most once.
- Dedupe memory is bounded per owner: **30 days and 4096 ids** (owner decision,
  Nov 20, 2026; https://github.com/danswett/agent-bridge/issues/2743). A full store
  refuses new admissions `duplicate-store-full`; it never evicts an unexpired binding.
- Remote envelopes differ: each carries a fresh nonce and short expiry, and a replay
  is refused even if the request id is remembered. A retry uses a new envelope with
  the same request id. Stop bindings last through grant expiry plus the envelope window.

## Input shapes

`fleet launch` takes flags, or `--json <path|->` with the same fields. Unknown
fields, enum values and versions exit 2.

```json
{
  "version": 1,
  "request": "3f1c0a52-8d0e-4b8a-9a63-0c1e5f7a2b10",
  "grant": "b7e5a0de-4f3c-4a52-8e1f-2d9c6a1b7c33",
  "target": "<node id, optional>",
  "name": "SG Builder 263 CLI Contract",
  "workspace": "<approved workspace identity or label>",
  "agent": "<agent id>",
  "model": "<launch-tuning id, e.g. opus[1m]>",
  "launchProfile": "<launch profile>",
  "effort": "<optional>",
  "context": "<optional>",
  "permissions": "ask",
  "isolated": false,
  "prompt": "<text, sent whole, never truncated>"
}
```

- The launch profile is `--launch-profile` / `launchProfile`; `--profile` is never
  the launch profile (it is the host profile on every verb).
- Omitted `model`, `launchProfile`, `effort`, `context` resolve only to the grant's
  recorded snapshot. Omitted `permissions` resolves to `ask` only if `ask` is inside
  the grant-policy intersection (as the Fleet/Grants evaluator computes it), else it
  refuses `scope`; it never resolves to `allowAll` unless
  [allowAllLaunch](#allowalllaunch) permits it.
- `name` passes the existing fixed name gate (`FleetLaunchNames`), a format and
  collision rule, not identity proof. Resume, restore and transfer are out of version 1.

`fleet end`:

```json
{ "version": 1, "request": "<uuid>", "grant": "<uuid>",
  "target": "<node id, optional>",   "session": "<full session id or 16 lowercase hex>" }
```

  `session` is resolved **only among sessions launched under the same `grant`** (the
  receiver's own launch records, G3). Either form matches; an id outside that set,
  including a live session of another grant or none, refuses `scope`.

## Output shapes

One JSON object on stdout for every exit, including refusals.

```json
{
  "version": 1,
  "request": "<uuid>",
  "action": "launch",
  "grant": "<uuid>",
  "generation": 1,
  "target": "<node id or local>",
  "outcome": "started",
  "session": "<16 lowercase hex or null>",
  "sessionId": "<full session id or null>",
  "permissions": "ask",
  "registered": false,
  "reason": null,
  "uncertain": false,
  "remaining": { "hour": 7, "day": 40, "live": 3 },
  "time": "2026-11-21T20:55:00Z"
}
```

- `outcome`: `started`, `accepted` (queued, unconfirmed), `ended`, `refused`, `failed`
  or `unconfirmed`.
- `registered` (launch only, else false) is true once the receiver's launch record
  shows the session registered; confirmed means `started` and `registered` true.
- `sessionId` is the full id from the receiver's own launch record, next to the
  16-hex `session`; both are null until started. `fleet end` takes either.
- Exits 2 and 6 emit the same object with `outcome` `refused` or `failed` and a
  `reason` (for example `bad-argument`, `unavailable`); fields unknown at that point
  are null.
- `reason` is null on success, else one refusal code below.
- `uncertain` is true exactly when `outcome` is `accepted` or `unconfirmed`.
- `remaining` comes from the receiver's durable counters, never the caller's. It is
  absent when the grant was not resolved.
- Prompt text, tokens, secrets and raw command lines never appear in output or audit.

`grants list --json` returns `{ "version": 1, "grants": [ { "id", "generation",
"state", "expiresAt", "actions", "subject", "caps", "spent": { "hour", "day" },
"live", "pending", "lastOutcome", "stale" } ] }`. `state` is `active`, `expired`,
`revoked` or `revocation-pending`; an offline receiver's counters set `stale: true`.

## Exit codes

They extend the host CLI's existing launch-status exits so one wrapper can map both.

| Exit | Meaning |
|---|---|
| 0 | `ended`, or a launch whose outcome is `started` **and** `registered` is true; list/read succeeded |
| 2 | Bad arguments, missing or non-UUID `--request`, unknown field |
| 3 | Unknown: no record for this request (status read only) |
| 4 | Guard 67 / caller classification: person-only verb or untraceable ancestry |
| 5 | Refused or failed, with a refusal `reason`; no side effect |
| 6 | Unavailable or invalid: pipe, host, audit or state not usable (stderr stage/reason) |
| 7 | **Uncertain**: accepted or unconfirmed; a side effect may have begun |

## Refusal codes

Refusals exit 5 unless noted. Each is a stable lowercase kebab-case string.

| `reason` | When |
|---|---|
| `grant-missing` | No grant with that id for this subject, audience and generation |
| `grant-expired` | Past `expiresAt`, before `notBefore`, or the clock moved backwards |
| `grant-revoked` | A tombstone exists |
| `grant-exhausted` | Hourly, daily or live cap (including reservations) would be exceeded |
| `scope` | Workspace, agent, model, launch profile, effort, context, permission, stop target or action outside the grant or narrowed by host policy |
| `name-gate` | The fixed name gate refused: invalid or colliding name |
| `duplicate-id` | Same request id with a different binding |
| `duplicate-store-full` | The 4096-id store is full of unexpired bindings |
| `target-offline` | The target node is unreachable; nothing was sent that could have acted |
| `kill-switch` | Account-wide kill switch, `launch.enabled` off or equivalent off state |
| `peer-not-trusted` | The target is not a trusted own peer (same-account relay presence bound to the device key) |
| `person-required` | The existing stopper needs person confirmation; the agent never obtains `--confirm` |
| `audit-unavailable` | Audit intent could not be written; no action taken |
| `policy` | Another receiving-host launch safety gate refused |
`target-offline` is returned only when delivery provably did not happen; if the
envelope may have arrived, the result is exit 7. `kill-switch` and `peer-not-trusted`
never fall back to another route.

## Uncertain outcomes: never auto-retry

Exit 7 (`accepted`, `unconfirmed`, or a lost response on a remote call) means the
launch or stop may have happened. The reservation stays held and consumes hour and
day capacity. Rules:

1. **Never retry automatically** and never retry with a fresh request id: a fresh id
   is a new operation and can start a second session.
2. Resolve with `launches status --request <uuid>` (on the target host for remote);
   a launch is confirmed only when it reports outcome `started` **and** `registered`
   true. Wrappers always pass `--json`.
3. `unknown` (exit 3) never authorizes a relaunch. Only a person or a lead issues a
   new operation with a new id.
4. Resending the **same** id only learns the recorded outcome; it never acts twice.

## Status read

Launch outcomes are read through the existing, read-only `agent-bridge-host launches status --request <guid> --json` and `launches list --json`
([host-launch.md](../host-launch.md), #3533). Unlike the fleet verbs they keep their
own output: without `--json` they print human text, `launches list --json` returns a
JSON **array**, and bad-argument or unavailable exits are stderr only. Wrappers must
pass `--json` and handle the array. Fields `request`, `outcome`, `session`,
`permissions`, `registered`, `time`; exits 0, 3, 5, 6, 7 unchanged. The fleet verbs add
no second status path. That store keeps outcomes 15 minutes; the 30-day grant dedupe
record still returns the recorded outcome for a resend. Stop has no status read in
version 1: resend the same id.

## allowAllLaunch

Owner decision, Nov 21, 2026, 12:47 PT: grants may launch Allow-all sessions.

- The grant document has `allowAllLaunch`, a boolean, **default false**. Absent means
  false. It is part of the person-confirmed digest and shown in the app summary.
- A request may use `permissions: "allowAll"` only if the grant has
  `allowAllLaunch: true` **and** the receiving host's current policy permits Allow-all
  for that origin. Either one false refuses `scope`.
- Policy bounds the grant, never the reverse: a grant cannot reinterpret `ask` limits
  or exceed agent launch ceilings. The owner approved grants launching Allow-all
  (12:47 PT); the Allow-all policy extension is in scope of G5 (brief 266). Before G5
  lands, host policy refuses Allow-all even with the flag set; after G5, the grant
  flag AND receiver policy are both required.
- With the flag false or policy off, omitted permissions resolve to `ask` only if
  `ask` is inside the grant-policy intersection, else `scope`; the result
  reports the `permissions` actually used.

## Remote authority

Owner decision, Nov 21, 2026, 12:50 PT.

- Remote `fleet launch` / `fleet end` with `--target <node id>` use a **new signed
  envelope, `FleetActionEnvelope v1`** (EnvelopeBody v1 cannot carry these fields).
  It carries account, source device, subject instance, receiver audience, action
  (`fleet.launch` or `fleet.end`; stop is included), grant id, generation, request
  id, nonce, short expiry and canonical payload hash. A v1 messaging envelope is
  **never accepted** for fleet actions. G9 (brief 269) implements launch with this
  envelope and G10 (brief 270) adds `fleet.end`.
- The receiver applies **exactly the trust gate agent messaging applies today**:
  same-account relay presence bound to the device key, plus the signed envelope.
  "Pinned own peers" (owner, Nov 21, 12:50 PT) means this existing own-peer trust;
  there is no new pin step (owner, Nov 19, 22:05 PT).
- The **receiver-side grant is the authority.** The receiver runs its own grant gate
  in the order of fleet-grants.md, "Receiver order"; the sender never decides
  caps, scope or stop targets.
- **No separate roster requirement** applies to own-account grant-authorized remote
  launch or stop. This supersedes the earlier prerequisite in fleet-grants.md ("remote
  agent/roster authentication") for these two actions only. Issuing grants remotely
  keeps its person-device and roster gates; lite presence alone still authorizes
  nothing beyond the existing messaging trust above.
- The **account-wide kill switch is kept** and refuses `kill-switch` at the sender
  and again at the receiver.
- Own account only: no cross-account federation, no HA fallback or HA press. An
  unknown or untrusted peer refuses `peer-not-trusted`. Envelope replay is refused; a
  retry carries a new envelope and the same request id.

## Wrapper mapping

Fleet wrappers become thin, policy-only callers of these verbs.

| Fleet wrapper | Becomes |
|---|---|
| `launch-remote.ps1` | `fleet launch --target <node id>` (omit `--target` for local) |
| `end-one.ps1` | `fleet end --session <id>` |
| `end-many.ps1`, `end-sessions.ps1` | One `fleet end` per session, each with its own fresh `--request`; never one request id for many sessions |

Wrapper rules, all mandatory:

- Policy only: the wrapper chooses grant, target and name; the receiver enforces caps, counters and duplicates.
- **Never an HA fallback** on any refusal, offline, unavailable or exit 7.
- **Never synthesize a grant**, forge a subject or reuse a person's grant; a missing
  grant goes to the lead.
- **Never click, replay or fabricate a person confirmation**, including `--confirm`.
  `person-required` is surfaced, not bypassed.
- Never retry exit 7 automatically or with a fresh id; surface the request id.
- Pass the exit code and JSON through unchanged. Multi-session wrappers report each
  result; one refusal neither stops nor retries the others.