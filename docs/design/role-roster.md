# Agent-readable role roster (#2378)

Status: proposed design only, under #1396. No operation, storage, caller permission,
rollout or transition is implemented or enabled by this note. Engineering and both
independent reader families must clear the design before its code cuts are assigned.
Label-to-role selection here is superseded by [role-ids.md](role-ids.md)
([#3334](https://github.com/danswett/agent-bridge/pull/3334)); the registry projection remains.

## One registry, one read-only view

A role roster is a typed projection of the existing account-scoped, signed fleet
session registry, not a second registry of people, sessions or machines. It answers
"which verified live session currently carries this role label?", not "who is
authorized to hold this office?". A signature authenticates the publishing host's
assertion, not the rightful ownership of an untrusted display label.

The owning contracts remain [fleet-session-registry.md](fleet-session-registry.md)
for names, completeness, freshness, ordering, removal and cache;
[devices.md](../protocol/devices.md) for roster enrollment and capability evidence;
and [agent-principals.md section 8.10](agent-principals.md#810-required-changes-to-55s-design-a-roster-v2)
for purpose-key authority. Nothing here changes their verification rules.
The issue's older "name-only never found" premise is superseded by #2151:
a unique verified live name can be found without a caller's first machine pin.
This does not make the name an identity or relax verification.

**Binding decision.** Use the existing host-owned fleet name as the role label.
The person assigns/clears it using the existing App/HostCli `session.rename` path,
with its disclosure and caller refusals; the existing fixed new-session launch
rule remains its narrow exception, not a new role-holder setter. An agent, script
or role holder acting as an agent cannot rename itself or claim a role.
Workspace `name`, `user_named`, transcript titles, prompts and folder names
are never role-binding sources. Same-user file tampering remains inside the
documented host-state safety-rail limitation.

Use a reviewed, fixed vocabulary of role tokens and exact public fleet labels in
the client/protocol implementation, not a mutable role-to-node map. For example,
`quality` selects exactly `SG Quality Lead`, and `engineering` selects exactly
`SG Engineering Lead`. Case, trimming and the name filter follow the registry
contract; no aliases, fuzzy matching or inferred succession. Adding a token/label
pair requires a reviewed protocol change. Assigning an existing label to another
session uses the existing person path, not an edit to a central roster.

No persisted role holder, machine pin, session id, node id or replacement lineage
is introduced. The machine is obtained from the verified result and pinned by the
caller for its immediate action, not copied into a file for future resolution.

## Proposed agent surface

Extend the existing `fleet.list` read surface with an explicit role projection;
keep its ordinary typed and `include: "content"` modes unchanged. This shape is a
proposal for the future protocol/vector cut, not an available tool:

```json
{"op":"fleet.list","data":{"view":"roles","roles":["quality","engineering"]}}
```

`roles` is a nonempty array of distinct reviewed tokens, at most the vocabulary
size. Unknown/duplicate tokens, wrong types, unknown role-view options, content
inclusion, and caller-supplied account/generation/machine/node selectors refuse
before the registry read. A role read is fleet-wide; filtering to a caller-chosen
machine must not conceal a competing role holder.

The result has `fleetScope` and one row per requested token in request order.
Every row has the token as `role` and a fixed `result`. Only `found` has `session`,
with the existing typed lookup fields `id`, `node`, `machine`, `state`, `since`,
and optional authenticated `model`. No other row exposes session/node candidates.
Neither session names nor machine display names are returned in this projection.
Echoing a fixed requested token is not reading untrusted registry text.
The protocol cut must pin the exact JSON shapes and refusal order in vectors.

Require the existing `hello`, `agent.hello`, read scope, script-operation checks,
attempted audit before reading, and fleet-read completion handling. MCP only wraps
that operation; it holds no relay credential and has no separate resolver.
The host internally matches labels; it never returns free text as instructions.
Reading names remains a separate explicit content read with the existing taint
rules. A role result grants no credential, scope, command or send authority.

Take one authenticated scoped snapshot for the whole request, not one relay read
per role. Capture the connection/account and pinned roster identity with that
snapshot; a connection, account, generation or trusted-pin change before completion
refuses the whole read. Never combine rows from different snapshots or accounts.
Cancellation/unavailable scope is an explicit error, never an empty success.
Return refused rows, rather than omitting missing roles or claiming all roles found.

## Verification, replacement and refusal

Consume the Engineering chain interfaces, without duplicating their owners:

| Owner | Interface consumed by the role projection |
|---|---|
| #2371 C1 | `RelayFleetRoster(Func<RosterVerifier?>? verifiedRoster = null, TimeProvider? time = null)`; `Read(IEnumerable<JsonElement> frames)` returns `IReadOnlyList<FleetMachine>` |
| #2462 / #2380 C2 | `FleetRegistryCache(TimeProvider time)`; `Accept(VerifiedRecord)`, `Remove(string machine)`, `Reconcile(IReadOnlySet<string> listed)`, `Purge()`, `Served()` |
| #2386 | Composes the gated reader and publication signer in the host; this design adds no second wiring path |

C1 owns schema, active enrolled host add/key/signature, the reader-clock 60-second
window and in-memory seq/epoch ordering. No roster or enrolled key means unverified,
never `found`. C2 accepts only C1-verified records, applies opted-in subscription
events/removal/reconciliation, and serves explicit `Stale`/`Online` state with
24-hour purge. Known-only machines have no sessions. The projection cannot infer
verification from relay `online`, a key's existence, cached text or display names.

The machine/key binding is the person-approved enrollment in `devices.md`: the
active host descriptor's `hostSign` and binding proof tie that purpose key to its
account, generation and DH identity. C1 resolves that enrolled key, never a key
supplied by the registry record. The person-set session label is then covered by
that host's registry signature; it is not a new signed-roster role member.
Person `capSign` enrollment is already designed by #2385 / PR #2408, but neither
that purpose key nor its verifier-support evidence signs or assigns a session role.

Resolve each label using the shared lookup selector over that snapshot, with its
completeness and verification diagnostics retained, not reconstructed from only
the successful C1 records. Dropping rejected or missing records must not turn a
partial view into a complete, verified absence or a unique match.

| Condition, in shared selector order | Role row |
|---|---|
| A potentially matching live machine's snapshot is truncated/incomplete | `incomplete`, no session ids |
| A live matching record is unverified, including beside verified matches | `unverified`, no session ids |
| More than one verified live match without approved ordering proof | `ambiguous`, no candidates |
| Matches exist only offline/stale | `offline`, no session ids |
| Exactly one verified live match in a complete scoped read | `found`, typed session |
| Complete scoped read has no match | `notFound`, no session ids |

Unknown coverage is not `notFound`. A live machine with no readable record or a
failed verification that prevents inspecting its names makes coverage incomplete;
do not discard it merely because the remaining signed records have one match.
A machine known offline contributes no live session, as in the owning contract.
Completeness describes the reader's view, not proof the relay disclosed everything:
the existing bounded replay/withholding and same-host metadata limits remain.

**Replacement decision.** No new tie-break. Until #2360 S-N3 and its independent
clock/order prerequisites are implemented, verified live duplicates stay ambiguous.
The approved future shared selector may pick a strictly newest cross-host process
only with all signed starts, trusted error bounds and a gap greater than 122 seconds.
Equal/missing/unprovable order refuses; any same-host live pair stays ambiguous
under its existing hold. Do not rank by arrival, state `since`, snapshot `at`,
seq, epoch, lexical id or a self-declared successor. One live match with only
offline/stale predecessors follows the ordinary unique-match rule.

The result is a point-in-time assertion, not a lease. An acting caller pins its
returned machine/node and uses the existing `agent.send` authorization; it must
not silently re-resolve to a replacement on a send failure. Future resolution is
another explicit read. No fallback to HA names, local node files, generic signing
keys, unsigned records or last-known role holders is added.

## State and failure boundaries

Run the projection on any connected host already serving the agent read API.
There is no designated machine, new watcher, poller, service or background writer.
If that host leaves, another authorized connected host can perform a fresh read;
its normal independently trusted roster pin and reader state are prerequisites,
not state recovered from a role map.

Existing host-owned names remain in their current protected per-host setting;
signed registry records remain relay-memory data and C1/C2 reader-memory/cache data
under the owning retention/removal rules. Relay restart rebuilds publication from
host heartbeats; reader restart cannot manufacture freshness or verified holders.
`Remove` purges immediately and clears ordering history; reconciliation purges
unlisted machines, and known-only entries carry no sessions. The role view never
keeps a longer-lived result to undo a removal or 24-hour purge.

Snapshot-v2 `roster-transition.json` (#2381 part A) is NOT a role store.
Its `consumed` records and `evidence` retain their capability/replay meanings;
`ConsumeCapability`, `StoreEvidence` and `ReadEvidence` do not supply live sessions.
Stored verifier-support evidence neither proves current role ownership nor
substitutes for C1 verification/C2 liveness. This slice adds no snapshot member,
role pin, nonce, outstanding challenge or disk write.
Part B (#2410) and positive transition (#2288) retain their separate ownership.
Production stays OFF: `acceptHostSign` remains `false`; no role option, caller
boolean, fixture key or setting can enable v2 or bypass purpose-key enrollment.

## Dependencies and proposed code cuts

This design can be reviewed now. Activation waits for #2360's person-set name
publication and #1776's enrolled hostSign/signing/trusted reader chain:
#2371 C1, #2462 / #2380 C2, and #2386 wiring, with their trusted roster and
revocation prerequisites. Protected custody or merged design is not live proof.
The positive-transition prerequisites, including capability-purpose enrollment
following #2385, remain separate; this note releases none of them.
#1754's name-resolution check is not a role-id inventory or a verification gate.

Each code cut requires a separate Engineering grant and tests first, at most
250 whole changed lines including tests; split further rather than copy a
predecessor. An absent dependency is a wait, not permission to implement it here.

| Cut | Depends on | Failing-first acceptance cases and deliverable |
|---|---|---|
| R1 protocol/vectors | Accepted design, shared selector contract | Exact role-view shapes/vocabulary/order; unknown/duplicate/type/content/scope-selector refusals; unchanged legacy reads; no free-text or ids on refused rows |
| R2 host projection | R1, C1, C2, #2386, names | One scoped read for many roles; unique verified live found; incomplete, rejected/missing record, unverified, ambiguous, stale/offline and notFound cases; no-hello/scope/script/audit/cancellation refusals |
| R3 reader lifecycle integration | R2, C2 lifecycle fixtures | Fake-clock 60 s and 24 h edges; remove/reconcile/known-only purge; replay/epoch/reconnect; changed connection/account/generation/pin refuses; no cached holder resurrection |
| R4 MCP/client adapter | R2, R3 | Typed parity, no name echo/content bypass, ids only on found, immediate machine/node pinning; transport/refusal surfaced; no per-role reads, automatic send or fallback map |
| R5 replacement conformance | R2, independently authorized S-N3 | Current duplicate ambiguity; future both-order cross-host proof/gap cases; equal/missing/skew and every same-host pair ambiguous; older/offline never wins |

Review both this note and the owning security contracts whole. Required review
checks include label-not-authority, absence/completeness truth, no second store,
two-family exact-head independence, and no positive transition or rollout claim.
