# Stable role IDs and fenced session delivery

Status: proposed design, not implementation, enrollment or rollout authority.
Role-ID code is paused (owner, Nov 19, 17:58 PT, two-track plan); no role-ID code is on main.
Canonical home: [existing lookup issue](https://github.com/danswett/agent-bridge/issues/1399),
under [fleet parity](https://github.com/danswett/agent-bridge/issues/1396).
Owner decisions, Nov 19, 2026 PT: GUID addressing, names display-only (12:10);
unique SESSION ID and successor-stable ROLE ID per fleet session (12:12).

## Decisions

Owner decisions, Nov 19, 2026, 14:55 PT; Q1 and Q2 as CHANGED by the owner's answers of Nov 23, 2026, 23:45 PT, which Quality
recorded in [slipgates-fleet #1351](https://github.com/danswett/slipgates-fleet/pull/1351) (`docs/owner-decisions.md`; see
CHANGE S1 below). Q3 is unchanged.
1. Q1 holder claims are person-authorized only, in one of two ways: a person-signed assignment of the exact successor
   (section 2, unchanged), or a person-signed standing grant for the role (S1) plus the successor host's enrolled
   host-key possession proof. Both rest on the person-approved relay role-authority key (slice 2); self-appointed or
   unsigned claims are rejected. **Presence dropped:** under a grant no person is present at each succession (no
   presence-gated per-successor `sign`, no 60-second assignment). **Presence that stays:** a person's presence (`key` or
   `webauthn`, section 2) when the grant is signed and when it is revoked, and when the authority key is enrolled or
   rotated; and the host's possession proof of the successor at every succession.
2. Q2 succession needs a valid standing grant for the role (S1) plus a confirmed stop of the old holder; no autonomous or
   time-based takeover (a grant is used only on the current holder's request, within its bounds, never on a timer);
   an unknown-outcome never counts as stopped. **Dropped:** the owner's approval of each successor. **Stays:** section 3
   whole: close admission, drain every issued reservation, the old host's confirmed stop, the compare-and-swap, one
   pending succession per role. A succession that is not a grant use (another model class, an `existingActivation`
   successor, a role with no valid grant) still needs section 2's person-signed assignment.
3. Q3 a new SESSION GUID per activation, a stable ROLE GUID across approved resume, move and host recovery; a compromised role is retired permanently.

## CHANGE S1 (owner answered Nov 23, 23:45 PT; Quality re-reads before it merges): a standing succession grant per role

**Owner decision, 20:55 PT Nov 23, 2026, relayed by the Chief: replace per-successor owner approval with a standing,
person-signed succession grant per role. The owner then answered the open questions at 23:45 PT (S1.4), recorded by
Quality in slipgates-fleet #1351. This is a design CHANGE and no implementation or rollout grant: role-ID code remains paused.**

It changes one thing: who approves a succession and when. A person signs a bounded grant for ONE role once, instead of
approving every successor each time. It removes none of section 3's safety: the confirmed stop of the old holder, the
drain of every issued reservation, the compare-and-swap, and "an unknown outcome never counts as stopped" are unchanged.

### S1.0 Source, and what does not change

**Source.** (1) Owner decision, 20:55 PT Nov 23, 2026, **relayed by the Chief**: the standing grant per role (the Chief's
relay, not a verbatim owner quote). (2) The owner's answers at 23:45 PT, with their option labels quoted exactly in
slipgates-fleet #1351: "Current lead only (Recommended)", "30 days / 20 uses (Recommended)", "Accept, with visible audit
(Recommended)". Q10's additions on #4391 (a use, the model class, the activation record, inheritance) are written below.

**Unchanged by this CHANGE, stated so Quality can check it:**
- **The fencing.** Section 3's order is untouched: close old-epoch admission, cancel unissued sends, drain every issued
  reservation to authenticated `delivered` or `notDelivered`, the old host persists its fence and confirms the old holder
  STOPPED, and only then the compare-and-swap activates the successor at the higher epoch. Every reservation is still
  checked online at injection (section 3), and no request can be delivered to both holders.
- **The unknown-outcome rule.** An uncertain reservation or a missing confirmed stop blocks activation, potentially
  indefinitely; an unknown outcome never counts as stopped; no timeout, expired claim, lost response or expired grant
  shortens it. No autonomous or time-based takeover exists, with or without a grant.
- **The failure contract** (section 6): every row, and the rule that all routing fails closed.
- **Q3**: a new SESSION GUID per activation, a stable ROLE GUID, a compromised role retired permanently.
- **Both defaults stand:** a grant never carries `presentation: inherit`, and no drain starts before the grant check.

### S1.1 The grant

A succession grant is a person command (section 2's gates) that lets the role ledger accept successors for exactly one
role, within limits, without a fresh person approval for each. Its signed body binds:
`version, account, generation, roleId, grantId, grantVersion, modelClass, maxUses, notBefore, expiresAt, presentation
(always separate), nonce, request, issuedAt`. `grantVersion` is strictly monotonic per role, as `assignmentVersion` is.
The ledger keeps the grant and its consumed count; hosts and clients never hold either.

### S1.2 The checks (Quality's, exactly)

**What counts as a use.** One USE is one appointment ATTEMPT that the ledger ADMITS under the grant: the step in which
every check below passes, the use count is incremented and persisted, and the role moves from `active(E)` to
`quiescing(E)` for that successor, all in one compare-and-swap. It counts whether the appointment is then COMPLETED
(`active(E+1)`) or not (the grant is revoked or expires before activation, the stop is never confirmed, the host fails);
a retry after an admitted attempt is another use, and a use is never refunded. A request REFUSED at the grant check
(any check below fails) never reaches `quiescing`, consumes NO use, and is logged: a refusal costs the owner nothing, so
junk requests cannot exhaust a grant, while an admitted attempt, which closes the old holder's admission, is counted.
Activation consumes nothing further.

A succession under a grant is admitted only if EVERY check below holds in that admission step, and each is read again in
the activation compare-and-swap (so a grant that was revoked, expired, retired or invalidated while the old holder drained
cannot activate; section 3's state then stays as it was). Any failure refuses and leaves section 3's state untouched.

1. **Person-signed.** The grant, and its revocation, are signed by an ACTIVE enrolled person device in the pinned
   account and generation, neither probationary nor held, with recorded presence `key` or a verified `webauthn`, under
   section 2's nonce, replay, lifetime (at most 10 minutes to commit the command), skew and persisted-floor rules.
   `capSign`, `hostSign`, an agent, the relay alone, a role's holder, or `presence: none` can neither create, extend,
   raise the count of, nor revoke one. A request from an agent or host may ask for a succession; it never creates a grant.
2. **One role.** The grant names exactly one `roleId` (full UUIDv4, section 1). No list, wildcard, pattern, name or
   "all roles". The role must exist and not be retired or revoked. A grant for role A has no effect on role B.
3. **One-for-one.** Each use replaces exactly one current holder with exactly one successor: epoch E to E+1, with the
   expected old session, the successor's enrolled-host possession proof and the grant's own `grantVersion` all in the
   compare-and-swap (section 3). Two requests under one grant cannot both be admitted; the loser is refused and consumes
   no use. A use never leaves two holders, and never one holder with two sessions.
4. **Revocable.** A person-signed revoke ends a grant at once. Revocation, role retirement, roster-generation change,
   authority re-enrollment or rotation, and a compromised host or role each end every affected grant. The ledger
   re-reads all of these inside the activation commit, so a grant revoked while a succession is quiescing cannot activate
   it: the role stays `quiescing(E)` and needs section 3's fresh person assignment. No new state is introduced.
5. **Bounded in time and count.** `expiresAt` AND `maxUses` are both required; a grant without both is refused when it
   is signed. The ceiling is the owner's answer (S1.4): at most 30 days and 20 uses per grant. Time uses
   `now = max(local wall clock, durable highWater)`. The consumed count is persisted in the ledger and checked, then
   incremented, in the admission step; a count at `maxUses`, or an `expiresAt` in the past at admission or at
   activation, refuses.
6. **Tied to a CONFIRMED stop of the predecessor.** A use needs everything section 3 needs today: old-epoch admission
   closed, unissued sends cancelled, every issued reservation drained to authenticated `delivered` or `notDelivered`,
   and the old host's persisted fence confirming the old holder STOPPED, not just quiesced. An uncertain reservation or
   a missing confirmed stop blocks activation indefinitely. A grant never shortens, replaces or times out any of it.
   A grant does not authorize the stop itself, nor any launch, end, rename or grant; it cannot borrow launch or stop grants.
7. **Logged, tamper-evident and visible to the owner.** Every grant created, used, refused, revoked, expired or
   exhausted writes a record durably BEFORE it takes effect; if the write fails the use is refused (fail closed). Each
   admitted attempt and each completed appointment writes an ACTIVATION RECORD with at least: **who** (the requesting
   current-holder session and the successor's host add), **role**, **old session**, **new session**, **grant id** and
   **remaining uses**, plus time, both epochs, outcome and reason. It is tamper-evident: each record is signed by the role
   authority and carries the digest of the previous record for that role, so an edit, an omission or a reorder breaks the
   chain, which readers and the person UI verify. It is visible to the owner: the person UI (slice 14) shows each record
   with the grant's remaining uses and expiry, and refusals go through the existing audit and notification paths
   (section 6). Counts and identifiers only; never a prompt, a workspace or a path.
8. **No fan-out.** One grant, one role, one successor per use, no sub-grants. A role's holder, a successor or an agent
   cannot sign, copy, widen, extend or delegate a grant; one role has at most one pending succession at a time. The
   grant authorizes no second role, no second holder, and no continuation of anything but the role it names.
9. **Current holder only** (the owner's "Current lead only"). The appointment is requested by the role's CURRENT holder,
   whose session is the one in the claim being replaced, and its successor is a session that holder itself launched, under
   its own existing launch authorization; the successor's host binds the actual launched activation to that launch request,
   as CHANGE C1-C2's `kind: launched` does. No other agent, host, earlier holder or person-less party can use the grant. An
   `existingActivation` successor is not a grant use and needs a person-signed assignment (Q2).
10. **Same model class.** The grant's `modelClass` and the class recorded for the old holder must be the same class, and
    the successor's class, which its enrolled host proves in its possession proof, must equal both; otherwise it is
    refused and consumes no use. A successor of another class is a new person-signed assignment, not a grant use. The
    class names follow the fleet's model policy; this note lists none.
11. **Inherits nothing.** The successor inherits NOTHING beyond the role's own permissions: not the predecessor's launch
    or stop grants, pending-launch rights, delegations, tokens, session-scoped approvals, workspace or presentation
    (`separate` only), and not this grant: the remaining uses belong to the role's ledger, never to a session. Its
    permissions are exactly what the role carries and what any new session of the role receives from existing authorizations.

### S1.3 What this changes in the text above

- **Q1 and Q2** (Decisions) are rewritten there; section 3's "The owner approves each exact successor" is marked in place:
  a valid grant stands in for that approval, and only for it.
- **Continuity** (CHANGE C1-C2): a grant carries `presentation: separate` only. A succession that inherits presentation
  still needs the per-succession person approval C1-C2 describes, because the person must see the effect first.
- **Slices 7 and 14** (the table) are edited in place; a new **slice 7a** holds the grant's ledger, signing and activation
  records, so slice 7 stays within its budget.

### S1.4 The owner's answers (Nov 23, 23:45 PT, recorded in slipgates-fleet #1351)

1. **Who may use it:** "Current lead only (Recommended)": check 9.
2. **The limits:** "30 days / 20 uses (Recommended)": at most 30 days and at most 20 uses per grant (check 5).
3. **The residual risk:** "Accept, with visible audit (Recommended)": accepted on the condition of check 7.
4. **Defaults that stand:** a grant never carries `presentation: inherit`; no drain starts before the grant check.

### S1.5 Interaction with the person enrollment (slice 2) and the published claim

- **Slice 2 is unchanged and is a prerequisite.** A grant is accepted only under slice 2's pinned person delegation: the
  enrolled authority key, account and roster generation. A wrong account, generation or key refuses, and an agent cannot
  set one, exactly as slice 2's acceptance test says. A grant is signed by the same active enrolled person devices that
  enroll the authority (section 2: presence `key` or `webauthn`, nonce, replay and lifetime gates).
- **New person-command actions.** Section 2 already requires explicit new role-management actions for enrollment,
  holder approval and revocation. The grant adds two of the same kind, create and revoke. Slice 7a defines them; slice 2
  keeps its scope and budget. Neither can be `capSign` or `accept-host-sign`.
- **Everything a grant depends on dies with its enrollment.** Authority re-enrollment, key rotation, roster-generation
  change, a compromised authority, host or role, and retirement each end all affected grants (check 4). Rotation
  still needs pinned delegation continuity (section 5); a grant signed under the old key is not carried over.
- **Q1 and the published claim.** For a succession under a grant the person's signature is on the grant, not on the exact
  successor (Q1 above), so a claim is valid if it carries EITHER a section 2 person-signed assignment OR a valid grant use.
  A host's own possession proof (`bindingProof`) is still required for the successor, and a host still cannot assign a role
  alone. The claim's `assignmentDigest` then binds the grant use: the digest of the grant body, its use number, and the
  exact predecessor and successor tuples; `assignmentVersion` still increases by one per use and never resets. Readers
  verify the pinned grant signature, the use against `maxUses`, the host add and both proofs, as they verify an assignment today.
- **Online authority, residual risk.** **Residual risk: with a grant, the online relay authority can activate a successor with no
  person present (one role, bounded in time and count, requires host proof and a confirmed predecessor stop). The OWNER accepted
  this risk, "Accept, with visible audit (Recommended)", on Nov 23 at 23:45 PT (slipgates-fleet #1351).** Today the relay
  authority acts only after a person has signed the exact successor in the last 60 seconds. Under a grant it can activate a
  successor with no person present, inside the grant's role, time and count. A compromised authority key could therefore use an
  open grant, but only for one role, within the bounds, and only with an enrolled host's possession proof and a confirmed stop
  of the old holder; the visible activation record (check 7) is what makes such a use noticeable, and revoking or letting the
  grant lapse closes it.

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
**[CHANGE S1, owner answered Nov 23, 23:45 PT: a valid standing grant for this role (S1.2) stands in for that per-successor approval, and only for it.]**
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

### Presentation continuity (CHANGE C1-C2)

**Proposed Nov 21, 2026, 22:26 PT; owner approval required; code remains paused.**
[Seamless recycling](https://github.com/danswett/agent-bridge/issues/1504) consumes this
identity authority; [session-order-roaming.md](session-order-roaming.md#11-stable-presentation-identity-change-c1)
owns pins, positions, ALL user groups, migration, privacy and preference-only undo.
Extend optional person enrollment to any personal continuity slot, not just fleet roles.
Reuse ROLE GUIDs and this ledger; no extra lineage ID, name-to-role map or authority is added.

Extend the exact person-signed successor assignment with `continuity`: the predecessor's
`sessionId, hostAdd, holderEpoch, assignmentDigest`, opaque UUID `launchRequestId`,
salted `launchIntentDigest`, and
`presentation: inherit | separate`. Its successor is the existing assignment target tuple.
The person sees both executions/hosts and the presentation effect before presence-gated
approval. Sign these fields under section 2's assignment gates; an old approval lacking
them, an agent request, prompt `PredNode`, grant, or host-only proof authorizes no inheritance.
First adoption explicitly binds the current execution and legacy locator instead of claiming
a predecessor. Only request ID and salted canonical-request commitment reach the authority/
ledger/registry: never the plaintext request, prompt, workspace or path. Salt and plaintext
stay on the person client and receiving host over E2E; the host proof binds tuple, ID and
commitment. The committed link/receipt binds the person-approved assignment digest.

An enrolled target host binds that request to the actual launched activation and proves
possession; launch failure, uncertain result or a different activation cannot fulfill it.
Publish the link through the existing signed registry/role projection only after section 3's
CAS, drain and confirmed stop, not at spawn, rename or "ready" text. Clients verify both
historical predecessor assignment and current successor proof; lower epochs cannot roll back.
Negotiate validated continuity support before launch dispatch; unsupported receivers refuse
an inheritance request rather than ignoring an optional field and returning ordinary success.
These are proposed additive protocol semantics, not fields supported by current v1 launch.
Approval/linkage cannot itself launch, end, rename, grant or silently retarget any session.
Do not redefine [v1 Session.id](../protocol/v1-sessions.md#session): it remains the provider
conversation identifier. A separately typed activation identity and proof/link reference
need reader-before-writer negotiation in both session snapshots and the strict
[relay registry](../protocol/roster.md#session-registry-each-machines-sessions).
The registry's current allow-list/byte limits still apply; unsupported relay connections
omit the proposed projection and cannot prove continuity. Capabilities never grant consent.
[v1 launch](../protocol/v1-launch.md) and transfer keep their present provider-ID/fork
semantics until those separately approved protocol slices land.

Order the operation explicitly:
1. Negotiate continuity support and prepare an opaque request ID/commitment; disclose the
   intended inheritance. A separate existing launch authorization is still required.
2. Launch the fresh activation as prepared, with no inherited role/presentation; the old
   holder remains active. The host proves the actual tuple and request commitment.
3. The person verifies that prepared tuple/hosts/effect and signs the exact successor
   assignment with section 2's presence, nonce, replay, version and 60-second acceptance gates.
4. Quiesce/drain/confirm old-holder stop under section 3; unknown outcome holds indefinitely.
5. Recheck approval at commit. If it expired during drain/stop, obtain fresh person approval
   with new command request/nonce and higher assignment version (same launch request ID).
   Never auto-renew consent. Only then CAS/commit and publish the link/new occupant.

For an independently started/manual-resumed activation, use the explicitly signed
`continuity.kind: existingActivation` variant with null `launchRequestId` and
`launchIntentDigest`; the launched variant is `kind: launched` and requires both.
The enrolled host instead supplies fresh nonce-bound proof of that exact already-running
activation/process and scoped successor tuple. The person chooses Approve continuation;
verify live possession, then follow steps 3-5 above (no launch step, no invented receipt).
Unsupported hosts or ambiguous process binding refuse; neither a provider ID nor a retained
alias substitutes for proof. Both variants require the same per-successor person approval (CHANGE S1: under a standing grant this stays, because a grant never carries `presentation: inherit`),
predecessor-stop confirmation and CAS; neither adopts launch grants or pending-launch rights.

Fleet's current `launch-lead -PredNode` puts a node in prompt text only; role-settings names
are configuration/display labels. A future adapter must pass the structured request/link and
await its committed receipt. It cannot sign person approval or borrow launch/stop grants.
Preferences are client-owned; neither host nor role authority receives group names or order.

### Identity migration and implementation

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
**CHANGE C6 (canonical reconciliation):** sensitive means credentials, auth, trust, protocol,
approval rendering or activation identity. Models and independent-reader requirements follow
the [current Fleet register](https://github.com/danswett/slipgates-fleet/blob/main/docs/pipeline-rules.md),
not the historical Opus builder requirement. This is no implementation or rollout grant.
Slice 1 is first. After 1, slices 2 and 3 can run in parallel; after 9, 10 and 12 can run in parallel; after 8, 14 can run alongside 9-13.

| Order | Repo | Slice and maximum WHOLE lines | Depends on | Sensitive | Acceptance test |
|---|---|---|---|---|---|
| 1 (first) | Bridge | Protocol types/vectors, 180 | None | Yes | Distinct GUID fields, canonical claim bytes; wrong versions/purposes refuse |
| 2 | Bridge | Person authority enrollment, 220 | 1 | Yes | Pinned person delegation; wrong account/generation/key and agent setters refuse (unchanged by CHANGE S1; a standing grant is accepted only under this delegation) |
| 3 | Bridge | Host activation identity, 220 | 1 | Yes | Resume/restart mints new SESSION GUID; proven same-process continuity and cloned-state refusal |
| 4 | Bridge | Signed session GUID publication/reading, 220 | 3, existing hostSign chain | Yes | Verify legacy locator mapping and enrolled host proof; never truncate GUIDs |
| 5 | Bridge | Durable role ledger/CAS, 220 | 2 | Yes | Mint/tombstone/recovery, concurrent assignment and quorum-loss refusal |
| 6 | Bridge | Claim signing/publisher/verifier, 220 | 4-5 | Yes | Reject unsigned/self-appointed claims, wrong assignment/host proof, stale and conflicting claims |
| 7 | Bridge | Durable stop/reservation core, 240 | 5-6 | Yes | Drain/crash/lost acknowledgement: require a valid standing succession grant (CHANGE S1) or, for a succession that is not a grant use, per-successor owner approval, and confirmed stop; unknown-outcome blocks takeover; the use is consumed when the attempt is admitted, never refunded, and a refused request consumes none |
| 7a (CHANGE S1) | Bridge | Standing succession grant: types/vectors, person-signed create/revoke, ledger count, signed hash-chained activation records, 200 | 2, 5, 7 | Yes | Each S1.2 check refuses on its own: unsigned or agent/host/capSign signer, two roles or a wildcard, a non-holder requester, another model class, a second use of one use, a revoked, expired or exhausted grant, a grant with no time or count, an unconfirmed stop, a failed record write, a sub-grant or a grant by a successor; a refused request consumes no use and an admitted one consumes one; a successor inherits nothing; an edited, dropped or reordered activation record breaks the chain |
| 8 | Bridge | Fenced-send host/protocol integration, 240 | 7 | Yes | Injection interleavings, delayed old send, dedupe; no legacy downgrade |
| 9 | Bridge | `fleet.find`, role projection and MCP, 220 | 6,8 | Yes | Typed unique holder, refusal parity, scoped proof and no name echo |
| 10 | Fleet | Messenger, `find-nodes`, `reply-session`, 240 | 8-9 | Yes | Explicit role/session selectors; A/B name-transfer regression and stale-binding refusal |
| 11 | Fleet | Dispatcher roster/planner/send adapter, 220 | 10 | Yes | Replace name regex/Target-name routing with verified IDs; preserve existing pins/caps and reject stale assignments |
| 12 | Bridge | PWA verified role display/resolver, 220 | 6,9 | Yes | Copy separate GUIDs, plain-text labels, stale/revoked UI; no implicit assignment authority |
| 13 | Bridge | PWA role-send binding, 220 | 8,12 | Yes | Session reply/answer drafts stay pinned; changed holder requires new action; uncertain request never retargets |
| 14 | Bridge | Person assignment/recovery approval UI, 240 | 2,7-8 and 7a | Yes | Per-successor owner approval, or under S1 a person-signed standing grant create/revoke view with its remaining uses and expiry and the visible activation records, and confirmed stop; resume/move/host recovery preserves ROLE, renews SESSION; compromised ROLE stays retired |
| 15 | Fleet | Explicit name-to-ID configuration migration, 220 | 10-14 | Yes | Person-enrolled mappings only; rename leaves GUIDs/epoch unchanged; legacy names display-only, rollback cannot restore name-send fallback |

### Slice 1 wire format (additive; fills gaps, changes nothing above)

Engineering lead ruling, Nov 23, 2026 PT: these fill gaps by reusing [devices.md](../protocol/devices.md) conventions; vectors are [role-claim.json](../protocol/vectors/role-claim.json).
- A purposes, as the §Framing label table (devices.md:26-53): the authority signs `F("agent-bridge role claim v1", body)` and refuses any other purpose; `agent-bridge role binding v1` is reserved for the host proof (slice 6), and the vectors show neither it nor `agent-bridge host key binding v1` verifies as the other.
- B `version` is the integer `1`, as person commands' `v` (devices.md:406-421); any other value refuses.
- C bytes: compact UTF-8 JSON, members in §2's order; the body omits `signature`. No HTML escaping (devices.md:529-530); claim strings hold no controls, so only quotes and backslashes are escaped; readers re-encode and compare byte for byte. `account` as entries'; `generation`, `hostKeyId`, `authorityKeyId` 32 lowercase hex and `hostAdd`, `assignmentDigest` 32-byte base64url (§Encodings, devices.md:55-60); `bindingProof`, `signature` strict low-S DER (devices.md:65); `holderEpoch` an integer as `seq` (devices.md:138): the first holder's is 0 and each succession adds one (§3, line 111); times as `at`: RFC 3339 UTC, `Z` only, up to nine fraction digits (devices.md:140, 1262). Time order and lifetime are verifier checks (slice 6).
- D `state` is `active`, `quiescing`, `revoked` or `noHolder`: §3's and §6's words. A reader refuses a claim whose `roleId` is not the role it asked for, or equals its `sessionId`.
