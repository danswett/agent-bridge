# Stable role IDs and fenced session delivery

Status: proposed design, not implementation, enrollment or rollout authority.
Role-ID code is paused (owner, Nov 19, 17:58 PT, two-track plan); no role-ID code is on main.
Canonical home: [existing lookup issue](https://github.com/danswett/agent-bridge/issues/1399),
under [fleet parity](https://github.com/danswett/agent-bridge/issues/1396).
Owner decisions, Nov 19, 2026 PT: GUID addressing, names display-only (12:10);
unique SESSION ID and successor-stable ROLE ID per fleet session (12:12).

## Decisions

Owner decisions, Nov 19, 2026, 14:55 PT:
1. Q1 holder claims are person-signed assignments only: a person-approved relay role-authority key plus an enrolled host key possession proof; self-appointed or unsigned claims are rejected.
2. Q2 succession needs per-successor owner approval plus a confirmed stop of the old holder; no autonomous or time-based takeover; an unknown-outcome never counts as stopped.
3. Q3 a new SESSION GUID per activation, a stable ROLE GUID across approved resume, move and host recovery; a compromised role is retired permanently.

## Existing work and the missing trust

[The earlier role-roster design](role-roster.md) and its
[closed design issue](https://github.com/danswett/agent-bridge/issues/2378)
project person-set names from the signed session registry; they do not prove role ownership.
This proposal supersedes their label-to-role selection only, not registry verification,
scope, taint, revocation or caller authorization. It is not a claim that rollout happened.
[Host signing](https://github.com/danswett/agent-bridge/issues/1776) remains a prerequisite.
[Fleet ID sends](https://github.com/danswett/slipgates-fleet/pull/996) explicitly deferred
roles because no trusted, published stable-role holder claim exists.
[Fleet identity/quota](https://github.com/danswett/slipgates-fleet/issues/383) and its
[dispatcher identity slice](https://github.com/danswett/slipgates-fleet/issues/387)
concern GitHub App credentials/quota roles, not session-address ownership.
Do not equate their role strings, installations or tokens with a ROLE ID.

## 1. Identity format and minting

Use two distinct typed fields, `sessionId` and `roleId`, each a full random
UUIDv4, canonical lowercase hyphenated GUID text. Validate version/variant and reject
nil, truncated, malformed or wrong-field values; never derive either from a name.
An enrolled host mints the SESSION ID once per execution activation, before publication.
It is a bridge identity, separate from a provider's conversation/resume identifier.
Even resuming that conversation creates a new activation and therefore a new SESSION ID.
Host restart retains it only for the provably same still-running process; otherwise
reattachment refuses. Persist provider/process bindings; cloning state is not adoption.

The person creates a role through an authenticated person UI; the role authority mints
its ROLE ID once using a cryptographic RNG and a durable conditional insert.
Each fleet session gets an assigned ROLE ID; successors inherit it, never the SESSION ID.
IDs are never reused; role retirement retains tombstones. Duplicate session GUID
bindings refuse registration; account/generation is pinned and cross-account sends refuse.
Reject name hashes, sequential/local counters, truncated GUIDs and GitHub token-role IDs:
they conflate display, location, lifecycle or credential authority.
Current short session addresses are legacy transport locators, not full SESSION GUIDs.
Never expand them into GUIDs or guess a locator from GUID prefixes.

## 2. Published holder claim and authority

Require a person-approved, purpose-specific relay role-authority signing key.
This explicitly adds online assignment authority to the relay, unlike today's untrusted
registry relay, as decided in Q1; existing relay TLS is not enrollment.
A presence-gated person `sign` command enrolls the authority key and pins its public
key, account, roster generation and allowed role operations; no implicit new key purpose.
Authority enrollment/rotation, holder approval and person-initiated revocation require
explicit new role-management person-command actions, not today's fixed action set or
`accept-host-sign`. Follow [Devices: Signing keys](../protocol/devices.md#signing-keys)
and [Vetoes, person commands and head attestations](../protocol/devices.md#vetoes-person-commands-and-head-attestations):
`sign` signs `F("agent-bridge person command v1", bodyBytes)`, with presence role `command`.
Require an ACTIVE enrolled person device in the pinned account/generation, neither
probationary nor held, with its recorded presence: `key`, or a verified `webauthn`
assertion per [Presence assertions](../protocol/devices.md#presence-assertions).
`presence: none`, agents and host-only signatures cannot authorize these operations.
Apply the normal action/receiving-target, signer, signature, presence, `host-key`,
heads and roster-freshness checks. Require the receiving channel's fresh 32-byte
`nonce`, an unused `request`, positive lifetime at most 10 minutes, at most 2 minutes
future skew, unexpired time and issue time above the persisted floor.
Use `now = max(local wall clock, durable highWater)` for all time checks.
Persist/flush the replay record before acting; retain it through `expiresAt` plus
2 minutes by the durable wall-clock high-water mark, persisting the highest accepted
`issuedAt` too. These are receiver obligations, not guarantees of the stateless parser.
As [Signed verifier-support evidence](../protocol/devices.md#signed-verifier-support-evidence-2372-design-2366-implementation)
specifies, `capSign` is unattended, not person consent. It can NEVER enroll or rotate
the authority key, revoke authority or approve a holder; neither can any other
unattended statement signature. Domain separation does not supply person presence.
Hosts prove session possession with enrolled `hostSign`; that signature alone cannot
assign a role. Do not reuse the AP5-lite key or let a host self-appoint.

Publish one canonical signed claim through the existing registry's role projection:
`version, account, generation, roleId, holderEpoch, sessionId, hostAdd, hostKeyId,
bindingProof, assignmentDigest, state, issuedAt, expiresAt, authorityKeyId, signature`.
`hostAdd` is the active enrolled host add, not a machine display name.
The host proof signs the scoped role/session/host/epoch tuple under a new purpose context.
A person `sign` assignment under those same command gates binds that exact tuple,
expected predecessor epoch, `assignmentVersion`, `nonce`, `issuedAt` and `expiresAt`.
`assignmentVersion` is strictly monotonic per role, distinct from the claim-format
`version` and holder epoch; reject any value not greater than the durable accepted
version. Assignments require a fresh 32-byte nonce and a positive lifetime at most
60 seconds, checked again at commit; an expired uncommitted approval needs fresh
person approval. Expiry limits acceptance, not an already committed holder's tenure.
Only the authorized relay publisher commits/signs it; readers never trust a record's
self-supplied key. Verify pinned delegation, assignment, active host add and both proofs.
No-holder/revoked states are authenticated records too, not inferred missing JSON.
Claims expire within 60 seconds; renewal requires fresh possession/liveness and a current
unchanged assignment check. Expiry is not permission to change holders.

The durable role ledger is the authoritative assignment extension of the existing
registry, not a competing session inventory. Session liveness stays in the signed
[session registry](fleet-session-registry.md). Claims reference those session records.
Reject relay-only unsigned assertions, host-only signatures and person signatures
without online fencing: respectively they lack provenance, ownership or currentness.

## 3. Atomic succession and the actual send fence

Require per-role linearizable transactions shared by all authority replicas:
`active(epoch E) -> quiescing(E) -> active(E+1)` or `revoked(E+1)`.
The owner approves each exact successor; agents may request, never authorize, transfer.
Compare-and-swap includes account/generation, role, old epoch, expected old session
and assignment version; atomically persist the higher version and consumed request/nonce
with the assignment. Restart, key rotation and roster changes never reset role versions.
Two successor requests cannot both commit; losers must obtain new owner approval.
Quiescing first closes old-epoch admission, cancels unissued sends and drains every
issued delivery reservation to authenticated `delivered` or `notDelivered`.
The old host persists its fence and confirms the old holder stopped, not just quiesced. Only then may the
authority atomically activate the ready successor and publish its higher epoch.

Each role send reserves a unique request ID against the exact current binding.
The receiving host obtains online authorization for that reservation immediately
before terminal injection and retains it until injection's terminal outcome is durable.
Transfer waits for those reservations, including delayed/in-flight transport requests;
a timeout, expired claim or lost response never proves that injection cannot occur.
Hosts durably deduplicate reservation outcomes and reject replay after restart.
An uncertain reservation or missing confirmed old-holder stop blocks activation,
potentially indefinitely. Never expire a lock and assume the old holder stopped.
Thus no old-holder injection can occur after successor activation, and no request
can be delivered to both holders. An earlier delivered message cannot be recalled.
This trades availability for safety, as decided in Q2; unknown-outcome never counts as stopped.
It assumes conforming trusted hosts/authority: a compromised host cannot be made
to forget plaintext already received, and no cryptographic design can promise that.

## 4. Resolution and sender behavior

Extend `fleet.find` with an exclusive `roleId` selector; never combine name and role
selectors or accept a caller-selected account. Return typed `found` only with one
verified live holder plus account/generation, SESSION GUID, host add, epoch and proof.
No proof, incomplete coverage or unknown authority state is a refusal, not notFound.
Names remain separate explicitly requested, untrusted display content.
Pin the returned tuple for this attempt; send it with the role and request ID.
The authority and receiving host recheck the complete tuple at admission/injection,
including revocation, online session binding and ordinary message authorization.
The existing `agent.send` ignores unknown fields: adding a client field is NOT a fence.
Require negotiated fenced-send support and a new validated protocol variant;
old clients/hosts cannot downgrade a role send into an ordinary session send.

The [Fleet ID-send race](https://github.com/danswett/slipgates-fleet/pull/996):
name lookup selects A; B takes that name while A remains live; a wrapper converts
the name to A's ID and later checks only `ExpectedNode=A`. A matches itself and
receives a message intended for the new holder. Preserve the compatibility fix:
reject names or revalidate the original name/binding at send, never just A's liveness.
That recheck is not atomic role succession; signed epochs plus receiver fencing are.
A binding change returns `staleClaim`; no automatic retarget or resend to a successor.
An uncertain outcome retains its request/binding and never creates a second send.

## 5. Resume, host moves and compromise

Keep role identity in the protected replicated relay ledger; hosts keep
only their session bindings, proofs and durable delivery/fence journal.
Resolver/sender code runs on any authorized connected host; authority replicas run
in the relay service. No named machine, agent session or watcher is the coordinator.
When a client node leaves, another independently enrolled client can resolve afresh.
When the holder leaves, its role becomes unavailable; an unconfirmed stop blocks
replacement rather than silently granting the role to a different machine.
Relay restart recovers ledger, epochs, assignment versions, command replay records,
tombstones and unresolved reservations before serving sends; absent durable state
or loss of quorum refuses all role operations.

Resume or host move uses a new SESSION GUID, the same ROLE GUID, and the same atomic
owner-approved successor procedure; copied workspace labels confer no ownership.
Revoke a compromised host add, its claims and outstanding unissued permits; block
delivery on every check and retain unresolved issued permits until proven terminal.
Approved host recovery retains an uncompromised role only after owner-approved
rebind, fresh enrolled-host proof and confirmed old-holder stop. A compromised role
is retired permanently; authority compromise suspends routing and requires person-approved
authority re-enrollment and affected-role retirement. Mint replacement ROLE GUIDs, never reset epochs or resurrect tombstones.
Authority re-enrollment, key rotation and person-approved revocation/retirement use
section 2's active-person `sign`, presence, freshness, nonce and durable replay gates.
Rotation also needs pinned delegation continuity; a relay-provided new key alone
is refused. `capSign` cannot supply that continuity or authorize recovery.
Roster-generation change invalidates all old claims/delegations.

## 6. Failure contract: all routing fails closed

| Condition | Result; forbidden shortcut |
|---|---|
| Authenticated vacant/revoked role | `noHolder`/`revoked`; no cached predecessor |
| Two claims for one current epoch, or split-brain authority | `conflictingClaims`; no newest/name/arrival tie-break |
| Expired, replayed, unsigned, wrong-scope/key or unverified claim | `staleClaim`/`unverified`; no candidate address |
| Unreadable/partial registry, missing proof or unknown liveness | `incomplete`/`unavailable`; not verified absence |
| Relay unavailable, quorum lost or restart recovery incomplete | `unavailable`; no offline permit or HA fallback |
| Transfer or delivery outcome uncertain | `quiescing`/`unconfirmed`; no timeout takeover or duplicate send |

Return explicit reasons through existing audit/notification paths; no success-shaped
empty roster. Valid older signed epochs are historical, never competing live holders.

## 7. Migration and ordered implementation slices

Use additive, default-off negotiation, then explicit person enrollment of each
role and existing session; do not infer assignments from matching labels.
Rename/display-name changes update display only, preserving both GUIDs and the epoch.
The person-only rename path keeps its existing disclosure and caller refusals.
Session-addressed replies stay session-pinned, never silently upgraded to role delivery.
Retire name-address configuration only after callers explicitly select IDs;
keep legacy name lookup display-only/deprecated, with no role-send fallback.

Below are ordered WHOLE-line budgets including tests/docs, each strictly below 250;
split further before exceeding its budget. They extend the linked existing work,
not new issues or grants. Q1-Q3 are decided; implementation still requires dependency completion and explicit rollout authority.
Repos: Bridge = [agent-bridge](https://github.com/danswett/agent-bridge); Fleet = [slipgates-fleet](https://github.com/danswett/slipgates-fleet).
Sensitive means credentials, auth, trust, protocol, approval rendering or activation identity: every Yes requires a `claude-opus-5.5` builder plus two independent, distinct-family readers.
Slice 1 is first. After 1, slices 2 and 3 can run in parallel; after 9, 10 and 12 can run in parallel; after 8, 14 can run alongside 9-13.

| Order | Repo | Slice and maximum WHOLE lines | Depends on | Sensitive | Acceptance test |
|---|---|---|---|---|---|
| 1 (first) | Bridge | Protocol types/vectors, 180 | None | Yes | Distinct GUID fields, canonical claim bytes; wrong versions/purposes refuse |
| 2 | Bridge | Person authority enrollment, 220 | 1 | Yes | Pinned person delegation; wrong account/generation/key and agent setters refuse |
| 3 | Bridge | Host activation identity, 220 | 1 | Yes | Resume/restart mints new SESSION GUID; proven same-process continuity and cloned-state refusal |
| 4 | Bridge | Signed session GUID publication/reading, 220 | 3, existing hostSign chain | Yes | Verify legacy locator mapping and enrolled host proof; never truncate GUIDs |
| 5 | Bridge | Durable role ledger/CAS, 220 | 2 | Yes | Mint/tombstone/recovery, concurrent assignment and quorum-loss refusal |
| 6 | Bridge | Claim signing/publisher/verifier, 220 | 4-5 | Yes | Reject unsigned/self-appointed claims, wrong assignment/host proof, stale and conflicting claims |
| 7 | Bridge | Durable stop/reservation core, 240 | 5-6 | Yes | Drain/crash/lost acknowledgement: require per-successor owner approval and confirmed stop; unknown-outcome blocks takeover |
| 8 | Bridge | Fenced-send host/protocol integration, 240 | 7 | Yes | Injection interleavings, delayed old send, dedupe; no legacy downgrade |
| 9 | Bridge | `fleet.find`, role projection and MCP, 220 | 6,8 | Yes | Typed unique holder, refusal parity, scoped proof and no name echo |
| 10 | Fleet | Messenger, `find-nodes`, `reply-session`, 240 | 8-9 | Yes | Explicit role/session selectors; A/B name-transfer regression and stale-binding refusal |
| 11 | Fleet | Dispatcher roster/planner/send adapter, 220 | 10 | Yes | Replace name regex/Target-name routing with verified IDs; preserve existing pins/caps and reject stale assignments |
| 12 | Bridge | PWA verified role display/resolver, 220 | 6,9 | Yes | Copy separate GUIDs, plain-text labels, stale/revoked UI; no implicit assignment authority |
| 13 | Bridge | PWA role-send binding, 220 | 8,12 | Yes | Session reply/answer drafts stay pinned; changed holder requires new action; uncertain request never retargets |
| 14 | Bridge | Person assignment/recovery approval UI, 240 | 2,7-8 | Yes | Per-successor owner approval and confirmed stop; resume/move/host recovery preserves ROLE, renews SESSION; compromised ROLE stays retired |
| 15 | Fleet | Explicit name-to-ID configuration migration, 220 | 10-14 | Yes | Person-enrolled mappings only; rename leaves GUIDs/epoch unchanged; legacy names display-only, rollback cannot restore name-send fallback |
