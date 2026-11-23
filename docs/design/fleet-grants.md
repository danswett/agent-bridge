# Person-issued fleet launch and stop grants

**Status: design contract; implementation in progress, brief 246, updated November 21, 2026 (Pacific Time).**
No code, grant issuance, policy change, rollout or tool retirement is authorized
by this document. Implementation needs separately assigned briefs and review.
Tracking: https://github.com/danswett/agent-bridge/issues/3237.
G1 landed: pure grant validation and policy-intersection evaluation only; no authority enabled.
G2 adds `GrantStore` Issue/Revoke/TryGetUsable APIs only, with a 1 MiB/256-intent bound
and no eviction. Every generation requires its matching intact audit intent/terminal;
an incomplete transaction or replay refuses all use rather than repairing/resetting state.
Issuance uses full G1 validation against the receiver's required known-options snapshot.
Retired audit coverage refuses, including when the store is missing; it cannot reset tombstones.
G5a completes counter receipts: exact duplicates return their recorded reservation,
outcome and usage without a second admission, including after grant revocation/expiry.
Refused/failed terminals retain hourly/daily spend but release live capacity; settled
bindings remain 30 days after completion, while pending/uncertain/live bindings stay.
At 4096 retained requests or the existing store-size/intent bound, new admissions refuse full.
G7 core adds `App.Core.Grants` receiver contracts and the issuance view model: the
receiver prepares the exact document/digest, the app displays its fields and narrower
policy, and only a matching authenticated receiver receipt makes the row Active.
The receiver interface is a trusted verification boundary, not a caller-set "signed"
flag: implementations must authenticate receipts/counters and recheck person ancestry
before signing through G4's writer. G7b supplies that production adapter and WinUI
wiring separately; this core has no issuing transport, signing key or enabled UI.
G8 read/revoke contracts require verified snapshots and a confirmation bound to
account/profile/receiver/id/generation/action; G4's unsigned CLI tombstone is not proof.
Grants require ONE audit rotation per VM, done automatically by its first grant issue (owner Nov 21 21:41 PT,
superseding the person-signed command first proposed for #3566); no cutoff is inferred and heads are never trusted.
- Trigger: only `GrantStore.Issue`, inside the G4 person-confirmed issuance (same caller guard and confirmation),
  and only when the store would accept that grant; launch, stop, revoke, list and reads never rotate. Agent or
  script ancestry and a wrong confirmation are refused before the store opens, so nothing rotates.
- Rotate: grant history not proved absent (a store, a `grant.*` row as the audit reader parses it, an incomplete
  read, any retained boundary, or gated rows without heads) refuses, so budgets, revocations, tombstones and the
  boundary are never reset. A stream that does not fully verify with the host key (a broken chain with or without
  heads, a genesis MACed by another key) also refuses. Each stream moves to `audit/retired/<stream>-<request>.jsonl`; its fresh stream's first
  record is a genesis: `previousSha256` = the retired tail plus `rotation` {retired name, length, SHA-256, record}.
  The record holds a UUID request, reason `automatic-first-grant`, the grant id, time, each stream's length,
  SHA-256 and `TailHash` tail, and an HMAC under this host's device key. The issue result prints one notice line.
- Verify: a genesis is only ever a stream's first, newline-terminated record and its MAC and reason verify; a
  retired file still present must match its length and SHA-256. A missing one is accepted: grants never read
  retired history. Grants need both streams bound to one genesis, plus the existing adopted, retained, chain and
  sequence checks; otherwise a profile with no grant history is `RotationRequired` ("rotation-required") and any
  other is AuditIncomplete. A second rotation is refused.
- Crash safety: `audit/rotation.pending` journals the MACed record first; the next issue, or any audit write,
  archive or bootstrap with the device key, finishes that rotation, reusing a matching retired file, or refuses.
  A torn genesis, mismatched retired file or unverifiable journal stays `RotationRequired`.
- Native launch/stop gates and the launch preflight work before and after; gate sequences restart at 1.
Residuals: rolling back every file (store, streams, heads) to a post-rotation point still needs the keyed or
external anchor (#3569); anything holding this host's device key could compute a genesis (#3569); retention
pruning the genesis day refuses grants; an operation in flight across the rotation stays unresolved.

## Authority and existing boundaries

Owner contract: an agent may initiate launch or stop only through a
person-issued, capped, audited, revocable grant issued from the app.
Answering questions, replying and trusting prompts remain person-only for
this grant: it conveys no read, reply, answer or trust authority.
Existing separately authorized agent text messaging is unchanged.

The current stop verb dispatches from `Program.cs` to `StopCommands` and refuses
agent ancestry before opening the pipe (guard 67, exit 4). A person's
`stop <session> [--confirm <token>]` path remains unchanged. A grant adds a
separate authenticated agent path, never a way to classify an agent as a person.
Host stop/restart, settings, sign-in, adoption and credential management retain
guard 67. A script principal is not a session agent and cannot use these grants.

Reuse these mechanisms, not parallel launchers or stopping code:

- [Host policy](../host-config.md): `launch.*`, `agents.enabled`,
  `agents.launch.enabled`, inbound/exclude and outbound limits.
- [AP1 design](agent-principals.md): session credential and process-generation
  binding; [lite](../protocol/agent-lite.md): own-account messaging only.
  Lite presence alone does **not** authorize remote launch or stop.
- `src/AgentBridge.Host/GateLaunchAudit.cs`: durable request binding, intent
  before launch, terminal outcome and incomplete-audit refusal.
- `src/AgentBridge.Host/Fleet/FleetLaunchNames.cs`: fixed SG name validation
  and pending/live collision checks. Display names are not caller credentials.
- https://github.com/danswett/agent-bridge/pull/3526 at
  `8f986677448e58f14816974699db5bf26e2e7e58`: proposed `LiteLaunchLog`,
  private atomic persisted records, device-derived MAC and exact pid/start-time
  checks. It is a dependency, not a claim that it has landed.

This contract supersedes the older AP1 design's unconditional prohibition on
agent stop only for the explicit granted path. It does not silently expand AP1
launch permissions or implement the paused role-identity design.

## Grant shape

Versioned immutable authorization document; host-owned mutable usage is separate.
Unknown versions, fields, identifiers or enum values refuse, not ignore.

| Field | Contract |
|---|---|
| `id`, `version` | Random UUID; version 1; never a bearer credential |
| `account` | Signed-in Slipgates account identifier, not a display login |
| `issuer` | Person account, issuing app/host device id, person authorization proof, issued time and device-authenticated document digest |
| `audience` | Exact receiving host device id and profile; no machine-name routing |
| `subjects` | Explicit agent principal bindings: account, source host, full session id, credential id and process instance |
| `roles` | Optional exact display role names with person-approved bindings to the same subject tuples; not names alone |
| `actions` | Nonempty subset of `launch`, `stop` |
| `scope` | Exact machines, workspace identities/labels, agent ids, models, profiles, effort/context and permission modes |
| `stopTargets` | Default: sessions this exact subject launched under this grant; optionally enumerated existing session/process bindings approved by the person |
| `caps` | Strict positive JSON integers: at most 1000 hourly, 5000 daily, 100 live/reserved; hourly <= daily; no unlimited/zero/null interpretation |
| `notBefore`, `expiresAt` | Default lifetime 8 hours; hard maximum 24 hours; no agent renewal |
| `generation` | Immutable issuance revision; changing scope/caps/subjects means revoke and issue a new id |

The app shows both the approved role name and exact subject. Role successors do
not inherit authority: a person must approve a new binding/grant. An agent cannot
self-name into a role; `FleetLaunchNames` is a display/collision rule, not proof.
Wildcard subjects, machine names, workspace descendants and arbitrary executable
paths/arguments are excluded. Child sessions receive no grant automatically.

All scope selections are explicit snapshots of receiving-host launch options.
Missing model, profile, effort, context or permissions on an agent request resolve
only to a value recorded in that snapshot; no newly widened host default is inherited.
Omitted permissions use `ask` only when in the grant-policy intersection, otherwise refuse; never implicit `allowAll`.
Other omitted choices resolve in recorded grant order within the policy intersection, not receiver-default order.
Model, effort and context identifiers reuse the launch-tuning grammar, including `opus[1m]`.
Times normalize to UTC; issuance must leave a representable 24-hour lifetime horizon.
Stops check exact target authority before launch-only choices; narrowed launch options do not prevent stopping.
Canonical approved workspace identity and isolation policy are checked again;
an isolated worktree derives only from that approved workspace.
Resume, restore and transfer are not delegated in version 1.

**Effective authority is the intersection**, not the union, of the grant,
authenticated caller credential and current receiving-host policy:
`launch.enabled`, approved workspaces, available agents/models/profiles,
inbound launch policy, excludes, applicable outbound/depth rules, read-only,
kill/off and all existing launch safety gates. Host settings cannot be widened
by a grant, CLI flag, request payload or remote device.
Admission persists min(document caps, receiver-policy snapshot); dispatch rechecks the narrowed snapshot and current policy, and a missing snapshot after restart refuses.

Current inbound launch supports explicit `ask`, and agent launch ceilings are
3/origin/hour, 10/machine/hour and 6 live. Keep those narrower limits; a grant
cannot reinterpret them as `allowAll` or exceed them. Owner approval Nov 21,
12:47 PT puts the Allow-all receiver-policy extension in G5, not a separate G11.
Explicit `allowAll` requires both the person-confirmed `allowAllLaunch` flag
(default false) and current receiver policy for that origin; before G5 lands, it refuses.
`launch.permissions` is a default today, not an invented
global ceiling. The effective allowed set must be computed from actual policy.

Issuance requires all three cap values; 8 hours is the only lifetime default.
The UI suggests limits no higher than the receiver's current policy and explains
that other grants consume shared origin/machine limits. Daily and live caps apply
to reservations as well as proven launches; a stop never refunds hourly/daily use.

## Placement, persistence and crash contract

Every receiving host runs its own grant gate; the sending host is never the
authority over receiver policy, budgets or a stop target. State lives beside
that profile's config in a host-owned `fleet-grants.bin`, not a repository,
agent environment, shared worktree or HA entity. One profile writer owns it.

Use the persisted-launch-record approach from the prerequisite PR: bounded
versioned data, private folder/file ACL, temporary file in the same directory,
flush to disk and atomic replacement. Authenticate bytes with a distinct
device-key-derived MAC domain, not the lite launch-log key/domain.
The person document's existing device-command authentication is verified before
host storage; the storage MAC protects that accepted document and mutable state.
An agent signing operation or relay self-declared `kind: person` is not issuer proof.

Store accepted grants, durable revocation tombstones, request bindings,
hour/day admission times, pending/live process bindings and reconciliation
watermarks. Use the existing authenticated audit as the write-ahead record:
issue, revoke and reservation intents are durable before state installation or
side effects. Recovery reconciles both stores under the single receiver lock.
Missing/incomplete audit, mismatched generations, bad MAC, unreadable/oversized
store or failed write means **no usable grant**, with an explicit diagnostic.
An older valid-MAC file must not resurrect a grant or refund counters: reconstruct
from retained audited intents/tombstones or refuse when reconciliation is incomplete.
Do not copy `LiteLaunchLog`'s empty-on-load behavior into quota accounting.

At launch admission, persist the reservation and audit intent before starting a
process. Keep the reservation when a crash leaves registration uncertain.
On restart, reconcile against launch request records and exact pid plus process
start time, never a display name or pid alone. Release live capacity only after
proven process exit or proven no-process failure; unknown means occupied.
An uncertain admission still consumes hour/day capacity. Safe pre-admission
refusals consume no launch quota; distinct attempts retain existing attempt limits.

Clock windows are rolling durations, not Pacific calendar midnight resets.
Persist the last observed time; backwards clock movement refuses new grant use
until trustworthy time catches up. Counter refusals for observed expiry durably
advance that watermark before returning; restart and clock rollback cannot
restore authority. Known reservation refusals retain their binding for watermark
persistence. Unchanged same-time counter operations add no intents. Expiry, caps
and kill are checked again at the actual execution slot, not only when queued.
A large forward jump expires rather than extends authority. UI dates/times follow
the viewer's local-time convention.

There is no coordinator tied to a particular VM. A receiver leaving stops actions
there; its quota allocations stay spent/reserved and are not silently moved.
Any eligible host may receive a separately person-approved grant.
For a multi-machine app action, create distinct audience-bound child grants with
partitioned hour/day/live caps whose sums do not exceed the displayed total.
No child may spend another child's allocation; no automatic redistribution.
The app aggregates signed receiver counters and labels offline readings stale.
This avoids pretending separate hosts can enforce a shared counter without consensus.

## Person issuance, visibility and revocation

Windows app: **Fleet > Grants > New grant**. Select receiving machines, explicit
live subjects (or approved role-to-subject bindings), actions, launch scope,
stop-target exceptions, all caps and expiry. Show a plain summary, per-machine
allocations, effective narrower policy and whether any chosen action is unavailable.
The person confirms the exact digest on the trusted person path; the device
authenticates the request to each receiver. No unattended `capSign`, prompt text
or agent-triggered UI automation may stand in for that person authorization.

The receiving host checks issuer/account/device authorization and person context,
validates scope, persists and audits issuance, then returns id/generation/digest.
The UI shows **Active** only on that verified receipt, never on submission alone.
Remote app issuance, unlike grant-authorized acting, retains its person-device/roster gates;
same-account relay presence or a grant held by another agent is insufficient.

### Local binding trust and sealed byte layout

The approved existing trusted local App pipe is the trust root for `grant.binding`,
not the Seal alone. The Host returns its existing HostSign public-key metadata only
after verifying it against the active roster with `AcceptHostSign` and collision
checks; otherwise the operation refuses. The App gets no new roster or identity-sign
API. The Seal protects byte integrity; it does not authenticate a co-delivered key
independently of that trusted pipe. No host-key opening, new key, HostSign substitution
or relay fallback is introduced by this contract.

`FleetGrantBindingRequest(Receiver, Profile, Request)` names a fresh nonempty GUID.
The reply is `FleetGrantBindingResult(Binding, Proof)`, with `ReceiverKey` added to
`Binding`: the existing receiver static DH public key (relay `PeerKey`), not HostSign.
Both key texts are canonical unpadded base64url of 65-byte uncompressed P-256 points.
The receiver's `Machine.Id` is `Binding.Receiver = Ids.Device(ReceiverKey)`.
`Context` binds account, profile, receiver, action **`grant.binding`**, and that GUID.

`FleetGrantBindingProof.Bytes` uses the existing roster `Framing.F`: each field is
preceded by its unsigned 4-byte big-endian byte length. Its fixed-order fields are:
ASCII domain `agent-bridge fleet receiver binding v1`, UTF-8 account, UTF-8 profile,
UTF-8 `Machine.Id`, UTF-8 roster generation (32 lowercase hex), raw `ReceiverKey`
(65 bytes), HostSign key id (raw 32-byte SHA-256 of its 65-byte public point),
and UTF-8 freshness GUID in lowercase `D` form. These exact bytes are the body of
`FleetGrantProof.Seal`, whose receiver-proof domain and full `Context` also frame
the signature input. `FleetGrantBindingProof.Verify` verifies that Seal against the
HostSign metadata authenticated by the pipe and checks the exact layout before
returning `ReceiverKey` for the existing `SignedCommand` MAC.

Missing, malformed, unsealed or unverifiable keys refuse, without fallback. Adapters
retain the full binding metadata for that `Machine.Id` from binding through confirm;
any account, profile, roster generation or key change requires a new binding/attempt,
never substitution into an in-flight confirmation. The receiver remains the sole
dispatcher/writer and retains all person-device, roster and signer gates.

### Authenticated issuance rotation receipt

Canonical `App.Core.Grants.GrantReceipt` requires an explicit `bool Rotated` with
`JsonRequired` and no optional/default constructor argument. Missing or wrong-type
flags refuse. Its value comes **only** from the successful B325 PR2 `Issue`
`result.Rotated` (https://github.com/danswett/agent-bridge/pull/3638), never inferred
from audit state, a log or `RotationRequired`. The contract has no compile dependency
on PR2 and implements no producer or rotation policy. Until PR2 and G4 are available,
the receiver producer reports unavailable, not synthesized `false`.

`FleetGrantIssueReceiptResult(Receipt, Proof)` carries exactly the camel-case JSON
fields `id`, `generation`, `receiver`, `digest`, `rotated`. Missing, duplicate,
unknown or wrong-type fields refuse before sealing. `FleetGrantReceiptProof.Bytes`
uses fixed-order `Framing.F` (u32 big-endian byte length preceding each field):
ASCII `agent-bridge fleet issue receipt v1`, UTF-8 grant UUID in lowercase `D` form,
positive generation as 8-byte big-endian Int64, UTF-8 receiver `Machine.Id`,
raw SHA-256 document digest (32 bytes), and **one rotation byte: 0=false, 1=true**.
These are the exact `FleetGrantProof.Seal` body bytes; its outer domain and full
`grant.confirmIssue` Context bind account, profile, receiver and fresh request GUID.

`FleetGrantReceiptProof.Verify` authenticates the Seal with the already trusted
HostSign point and checks the exact body before returning receipt JSON for canonical
parsing. An absent proof or any unsealed change to `rotated` refuses; tampering with
the sealed rotation byte fails signature verification. The App may show the approved
rotation notice only on its matching authenticated **Active** receipt with
`Rotated=true`, never from preparation, submission or a missing producer. Builder 343
alone supplies the actual result and Seal; 344 owns the adapter/notice. All other
`RotationRequired` states retain their existing fail-closed behavior.

Host CLI person path: proposed `agent-bridge-host grants issue` and
`agent-bridge-host grants revoke <id>`. It uses the same receiver writer and
person confirmation, not direct file editing. Guard 67 plus complete caller
classification refuse agent/script credentials or untraceable ancestry; refusal
occurs before signing or mutation. A grant cannot invoke either command.
Phone issuance/revocation is a later separately reviewed person-device slice.

G4 CLI: `grants issue <document.json>`, `grants revoke <id> <generation>` and
`grants list`, each with optional trailing `--profile <name>`. Issue consumes the
strict G1 document with explicit subject tuples, scope, all three caps, times and
expiry; it supplies no cap/lifetime defaults. The existing signed-in Windows
identity is required; no key is provisioned. Issuer account/device and proof/digest
are replaced by the verified local person path. The document's issued time must
not be in the future or leave an expired grant. Its complete normalized summary
is displayed; the person types the exact SHA-256 digest in an unredirected console.
HostCli requires a direct desktop (`explorer.exe`) parent; shell-parent mutations,
including revoke, deliberately refuse with exit 4. No script or bypass flag qualifies.
The digest excludes only issuer proof/digest and includes `allowAllLaunch`, an
explicit boolean defaulting false. The receipt repeats that digest and boolean.
This field conveys no Allow-all authority until G5 validator/policy wiring lands.
Current local policy admits only `ask`, isolated approved workspace labels and
offered tuning choices common to all selected agents; no stop-target exceptions.
Policy/ancestry are rechecked after confirmation. Revoke acknowledges only after
the audited tombstone is installed. Listing uses existing read locks and verifies
MAC/audit without creating files, repairing state or changing ACLs; unavailable
state refuses. No counters are claimed by the list.

The list/details screen shows issuer, subjects, actions/scope, expiry, spent
hour/day counts, pending/live sessions, last caller/outcome, and remaining budget.
Counters come from the receiver, never an agent's report; read is not an acting
grant. Audit details omit prompt text, tokens, secrets and raw command lines.

Revoke takes the same person path and persists an audit intent and tombstone
before acknowledging success. New reservations and queued operations refuse.
Revocation and admission serialize at the receiver: a side effect already begun
may finish; revoke does not kill previously launched sessions automatically.
Return any in-flight request ids so the UI does not promise undo.
Revoke-all on several machines reports each acknowledgment separately.
An offline receiver is **Revocation pending**, not revoked; no future-use
guarantee exists there until its receipt or the maximum 24-hour expiry.
The app never reissues the same id to repair missing state.

### Sealed grant.prepareRevoke prepared value and person-confirm RPC

`grant.prepareRevoke` consumes `FleetGrantCall` with the caller's complete Context
and Args exactly `{id,generation}` (`FleetGrantPrepareRevokeRequest`); omit Command.
`FleetGrantPrepareRevokeResult(Prepared,Proof)` replies as `{prepared:{challenge,preview},proof}`.
Prepared reuses `FleetGrantPrepared`/`FleetGrantChallenge`; preview is exactly
id/generation/receiver/account/profile/action/document/digest. All fields are required.
Prepare Context action is grant.prepareRevoke; challenge/preview action is grant.confirmRevoke.
Caller retains Context and requested id/generation, never substitutes reply fields as expectations.
Challenge nonce is nonempty and distinct from the prepare RPC GUID; all selector/binding fields match.
`FleetGrantPrepareRevokeProof.Seal/Verify` take an injected receiver/caller TimeProvider.
Expiry must be after that clock's current instant, with no new skew grace; malformed/default expiry refuses.
`Parse(raw)` checks 128 KiB before JSON, rejects missing/extra/duplicate/wrong-case/type/null recursively.
Verify returns a snapshotted typed prepared value only after the trusted HostSign/body/context checks.
Document is exact complete normalized G1 text, opaque to this contract rather than a new policy parser.
Digest is lowercase SHA-256 of `Framing.F(Framing.Label("agent-bridge fleet revoke digest v1"),
account, profile, receiver, action grant.confirmRevoke, challenge nonce, id, generation, expiry, document)`.
Order is fixed; strings/document are exact UTF-8, GUIDs lowercase D, generation signed Int64 big-endian,
expiry invariant round-trip O. No reserialization while hashing; invalid Unicode refuses.
The digest excludes its own field and is not issuer.digest. `DigestBytes` exposes this exact preimage.
Seal body is `Framing.F(Framing.Label("agent-bridge fleet prepare revoke v1"), digest preimage, raw digest)`.
Matching preview/challenge fields bind transitively through that preimage; document counts once.
The COMPLETE body is <=30 KiB; oversize refuses without truncation, including multibyte document text.
Caller retains the verified, person-reviewed tuple plus document/expiry through confirmation.
Cached verified DATA may supply reviewed content but never manufacture a challenge or its expiry.
Immediately before confirm, BOTH caller and receiver use `ConfirmContext` with their injected clock,
using the caller's retained verified preparation or receiver's stored preparation, never unverified reply data.
It rechecks digest/binding/expiry/nonce and returns `FleetGrantRevokeContext` with the exact supplied Context;
it does not verify person intent, sign, rewrite Context, or authorize mutation.
`grant.confirmRevoke` consumes `FleetGrantCall{context,args:Confirmation,command:SignedCommand}`.
RPC, person SignedCommand, outer proof and receipt.action are all grant.confirmRevoke, with exact signed args.
It returns `{receipt,proof}` below; producer/dispatch/person/host gates are still separate and unavailable here.
The standalone `{request,proof}` helper is NOT the RPC request or a person command.

### Sealed grant.confirmRevoke request and receipt

`FleetGrantRevokeProof.SealRequest/VerifyRequest` bind `FleetGrantConfirmation` (challenge, id, generation, digest) to caller-retained `FleetGrantRevokeContext`.
GUIDs: nonempty lowercase D; generation: positive Int64; digest: 64 lowercase hex; approved tuple must match; challenge differs from fresh RPC GUID.
Outer context is the caller's account/profile/receiver/action `grant.confirmRevoke`/request, never reply-supplied.
Seals enable no producer, UI, dispatch, policy or authority; existing person/host/confirm gates remain. `SealReceipt/VerifyReceipt` return a sealed snapshot/typed `FleetGrantRevokeOutcome`.
Success requires exactly id/generation/receiver/account/profile/action/digest/problem/finalState/tombstone/counters/inFlightRequests, with none/revoked/true; only success may use none.
Counters use the complete DATA schema, nonnegative counts and required nullable lastCaller/lastOutcome.
In-flight ids: explicit, canonical, unique, ordinal ascending; [] proves none; refusals require exactly the first nine fields, no counters/tombstone/in-flight data. Closed problem codes 0..8:
none, unavailable, notFound, generation, auditIncomplete, writeFailed, personRequired, policy, other.
Refused proves no mutation begun; unconfirmed/no receipt/offline stay **RevocationPending**, never revoked.
Success means audited durable tombstone, not submission, undo or killed sessions.
`ParseRequest/ParseReceipt` accept raw UTF-8 whole envelopes (request/receipt + proof);
128 KiB is checked before JSON parsing; strict per-branch camelCase required fields reject
unknown/duplicate/missing/wrong-case/type/null (except the two diagnostics). Framing domains:
`agent-bridge fleet revoke request v1` and `agent-bridge fleet revoke receipt v1`.
Request body: domain, challenge, id, generation, digest; receipt: domain, retained challenge, id, generation, receiver, account, profile,
action, digest, problem byte, state byte (revoked=0/refused=1/unconfirmed=2), framed tail.
Success tail: true byte, canonical typed DATA counters UTF-8 JSON, framed ordered ids; refusal tail empty. Framing.F, UTF-8 strings, signed Int64 big-endian; body <=30 KiB, no truncation.
Verify returns only verified values or InvalidDataException.

### Sealed grant.read problem

A `grant.read` reply carries a REQUIRED closed `Problem` inside the seal:
`FleetGrantReadProblem` = None 0, RotationRequired 1, Unavailable 2, Other 3.
The reply JSON is exactly `{"problem":"<name>"}` with the exact, case-sensitive
names `none`, `rotationRequired`, `unavailable`, `other`; a missing, null, number,
unknown, extra, duplicate or differently cased name or value refuses. There is no default,
and the diagnostic `GrantReadResult.Problem` string, an audit line or an unsigned
flag never supplies it.

`FleetGrantReadProof.Seal(reply, context, signer)` returns
`FleetGrantReadResult(Reply, Proof)`. The binary body is
`Framing.F("agent-bridge fleet read problem v1", problem byte)`. The outer
`FleetGrantProof` context (account, profile, receiver, action `grant.read`, fresh
request) is the caller-retained one, never one echoed by the reply.
`FleetGrantReadProof.Verify(result, trustedHostSign, expected)` returns the typed
problem only after the signature and the exact body match; anything else throws
`InvalidDataException`. First issuance may be enabled only when the verified value
is `RotationRequired` and G7 readiness holds; None, Unavailable, Other and every
refusal never qualify. This contract has no producer: until the receiver seals it
the value is unavailable, not None.

The App adapter implements `IVerifiedGrantReader` and internally constructs the sealed,
read-only `VerifiedGrantRead` from its verified problem, computed G7 readiness and list
data. The VM handles no contexts, keys or proof verification. Its separate `CanIssueFirst`
requires typed RotationRequired plus G7Ready; list/revoke remain unavailable, and New grant
uses the normal person prepare/confirm path. The notice describes first-issue rotation,
not a completed rotation: only the verified receipt's `Rotated` confirms that. Without
the adapter the reader returns Unavailable/G7Ready=false; production stays unavailable
until G4 + PR2 land and AcceptHostSign is ON. Cached list/first-issue gates also require
the receiver id captured for that read; a changed-receiver reply is discarded, retaining only stale rows.

### Sealed paginated grant.read DATA

`FleetGrantReadDataResult(Page, Proof)` and `FleetGrantReadDataProof.Seal/Verify/Parse`
are DATA-only contracts, distinct from the required read-problem seal; neither
substitutes for the other. Verify returns a typed, snapshotted `FleetGrantReadDataPage`
or `InvalidDataException`. No producer, UI, dispatch, policy or authority is enabled.
The required camelCase page fields are offset, requestedGeneration, generation,
observedAt, grants, next, total. Rows are exactly document/counters/stale, reusing
the normalized full G1 grant document and App counters: every constructor field
is required, including explicit expiresAt/roles/stopTargets/allowAllLaunch.
Only counters.lastCaller/lastOutcome permit explicit null; missing, unknown,
duplicate, wrong-case, wrong-type and other null fields refuse recursively.

Offsets and sealed total are 0..256; pages contain 0..16 complete id-ascending,
nonduplicate rows and at most a 30 KiB framed body. Fewer rows may fit the byte cap;
oversize refuses, never truncates. Let end=offset+rowCount: end<=total;
next=-1 iff end==total, otherwise next=end>offset. Empty final pages are allowed,
empty continuations and premature/missing end markers refuse.
Caller retains `FleetGrantReadDataContext(Context, Offset, Generation, Total)`.
Initial selectors are (0,0,-1), where -1 means the total is not yet known.
Verified DATA generation must be >0. Continuations retain positive generation,
positive offset and the same verified total; changed generation/total refuses.
Outer Context is the caller's account/profile/receiver/action grant.read/fresh GUID.
Document account/audience match it; a reply never supplies the expected context.

Body: `Framing.F(Framing.Label("agent-bridge fleet read data v1"), offset,
requestedGeneration, generation, observedAt, total, framed rows, next)`.
Numbers are signed 64-bit big-endian and observedAt invariant round-trip `O` UTF-8.
Each row frames canonical UTF-8 document JSON, canonical counters JSON, stale byte.
Canonical JSON uses the normalized G1/App record property order, camelCase, strict
typed values, explicit fields/nulls and ordered arrays, independent of input object
property order. Producers must keep all rows/counters/total in a generation immutable;
consumers retain verified selectors and do not automatically mix/restart snapshots.

### Sealed grant.prepareIssue prepared value

`FleetGrantPrepareIssueResult(Prepared, Proof)` carries `FleetGrantPrepared`
(challenge and preview) with a `FleetGrantSignedPayload`.
`FleetGrantPrepareIssueProof.Seal(prepared, context, signer)` snapshots it;
`Verify(result, trustedHostSign, expected)` returns that typed snapshot only
after checking the host signature and exact body, otherwise `InvalidDataException`.
`Parse(reply)` accepts only camelCase JSON with every constructor field present;
unknown, duplicate, null, wrong-case and wrong-type fields refuse at every level.
The preview is exactly the App's nine fields: id, generation, receiver, profile,
document, digest, allocation (hourly/daily/live), effectivePolicy, unavailableActions.
Document is opaque exact text, not a grant-policy validator or an issuance decision.

Body order under `Framing.F(Framing.Label("agent-bridge fleet prepare issue v1"), ...)`:
challenge request/nonce, id, generation, account, profile, receiver, action, digest,
expiry; then preview id, generation, receiver, profile, document, digest,
hourly/daily/live, effectivePolicy, and a framed ordered unavailableActions list.
GUIDs use `D`, numbers signed 64-bit big-endian, digests decoded lowercase hex,
expiry the exact round-trip `O` representation, and other strings UTF-8.
Challenge action must be `grant.confirmIssue`; outer context action must be
`grant.prepareIssue`. Account/profile/receiver match the caller-retained context;
preview id/generation/receiver/profile/digest match the challenge.
The outer request is the caller's fresh RPC GUID; an equal challenge nonce refuses.
Verify authenticates expiry but is not a clock/policy check: the consumer must
refuse an expired challenge before signing or confirming it.
Read and receipt domains cannot be substituted, even if re-signed for this context.
This is contract-only: no producer, App/UI, dispatch, policy, or authority is enabled.

## Agent use and receiver order

Proposed command forms (not commands implemented by this PR):

```text
agent-bridge-host launch --grant <id> [existing explicit launch options]
agent-bridge-host stop --session <full-id> --grant <id>
```

The local pipe authenticates `AGENT_BRIDGE_TOKEN` against the AP1 credential
record and positively binds caller ancestry/job membership to the exact live
process instance. Environment session id alone is not proof. Invalid/missing
agent authentication never falls back to the person stop path.
The grant id selects host state; no grant secret travels or is exported.

For remote targets the local host uses a signed `FleetActionEnvelope v1`,
binding account, source device, subject instance,
receiver audience, action, grant id/generation, request id, nonce, expiry and
canonical payload hash. Per owner Nov 21, 12:50 PT, receiver checks existing
own-peer messaging trust (same-account relay presence bound to the device key)
and the signed fleet-action envelope; the receiver-side grant is authority.
No separate roster gate applies to launch/stop; the account-wide kill switch stays.
Own-account only; no cross-account federation or HA launch/stop fallback.

Remote subject attestation (G9b; owner approval requested before G9b2 code). The source host runs the AP1
session-credential and process-tree check locally, then signs; the envelope `subject` is the SHA-256 digest of
the canonical tuple (account, source host, session, credential, process instance). The receiver cannot inspect
a remote process, so it matches that digest to a grant `subjects` entry, requires the tuple's account to be its
own and its source host to equal the envelope's source device and differ from its own device, and never calls
the local `IsLive` credential check for a remote subject. A local source host never takes this path. Trust is
the existing own-peer trust (owner, Nov 21, 12:50 PT). The `hostSign` suspension is TEMPORARY (owner): own-account
messaging trust applies now; a signed roster is built and turned on later, and this section is revisited then.

Restart replay (owner-approved warm-up). The envelope replay cache is in memory. Every envelope expires within 120 s, so
after a host start the receiver refuses all `fleet.launch` envelopes until 120 s plus the 30 s clock skew has passed
(about 2.5 minutes); any envelope sealed before the restart has expired by then, so it cannot replay. Expect remote
launches to be refused with `policy` for about 2.5 minutes after a host restart; senders get a refusal, not a queue, and
retry with a fresh request. No store or new file is added. Noted alternative, not chosen: a durable (sourceDevice, nonce)
file that survives restart and removes the pause, at the cost of a private atomic store and its recovery rules.
The sender's envelope `generation` is diagnostic only: the receiver fences on its own grant generation (as local launches do).

Receiver order:

1. Validate transport/account/audience and agent identity; reject replay,
   malformed/expired envelope and unsupported action before side effects.
2. Load verified grant/revocations; match exact subject, action and target scope.
   Re-read current host policy and apply narrower scope, kill/off and safety gates.
3. Under the receiver transaction lock, bind request id to caller, grant,
   generation, action and normalized payload. A changed binding refuses.
4. Reconcile pending/live records; check rolling caps and attempt/depth limits.
   Persist admission plus audit intent before any worktree/process/stop action.
5. At the queued execution slot recheck grant validity, policy and exact process
   generation. Use the existing launcher/name gate or stopper; never raw kill.
6. Persist terminal outcome and updated counters; return a typed result with
   request id, reason, grant id and remaining budget. Failed terminal persistence
   returns unconfirmed, not success, and retains its durable intent/reservation.

Launch duplicate memory follows the host-native 30-day/4096-request contract:
https://github.com/danswett/agent-bridge/issues/2743.
An identical request returns its recorded outcome (possibly unconfirmed), never
launches again or spends twice; a conflicting request refuses. Full replay stores
refuse new admissions rather than evict unexpired bindings.
Remote envelope replay is rejected even if the underlying operation is remembered;
an authenticated retry uses a fresh envelope with the same operation request id.
Stop request dedupe retains bindings through grant expiry plus the envelope window;
the stop target also carries exact process-generation fencing at execution.
Never retry an uncertain launch/stop with a fresh operation id automatically.

Stop is allowed only for a subject's own grant-launched process, unless the person
explicitly named that existing process in `stopTargets`. Grant action `stop` does
not grant force termination or release protected/held/working-session constraints.
If the existing stopper requires person confirmation, return `personRequired`;
no agent may obtain or consume `--confirm` to bypass that requirement.
No ask/answer/reply/trust op, key/sign-in/config/update, grant issuance, delegated
grant creation or permission escalation can be carried by this grant.

## Audit and threats

Extend the existing audit entry's `Grant` from the legacy fleet marker to the
exact grant id and record verified caller principal, action, request id, ingress,
audience/generation, payload binding, cap decision, intent and terminal outcome.
Every use, including refused attempts, writes the existing audit; unavailable
audit refuses execution. Issue/revoke and reconciliation are recorded there too.
Grant counters derive from durable admissions, not a mutable UI number.

| Threat | Control and remaining boundary |
|---|---|
| Stolen session id or role name | Id/name is not authentication; AP1 token plus exact process binding, approved subject tuple and receiver account checks |
| Replayed envelope / lost response | Signed payload/audience/short expiry, durable operation binding and reservation; fresh envelope retry cannot create a second side effect |
| Grant id/document theft | Id is not a secret capability; subject/audience binding plus person-device verification and host MAC; agent cannot import or issue it |
| Runaway or prompt-injected agent | Hour/day/live caps including pending work, shared origin/machine ceilings, attempt/depth limits, expiry, person revoke and existing kill gates |
| JSON edit, truncation or old-file rollback | MAC, bounded strict parse and audit reconciliation; corrupt/missing state never resets budgets or revocations |
| Restart / pid reuse / stale role successor | Durable admissions, exact pid/start-time fence and explicit new person binding; no inherited successor grant |
| Revocation race / disconnected receiver | Serialized execution slot checks, durable tombstone and per-receiver receipt; offline stays pending and expires within 24 hours |
| Cross-machine quota multiplication | Separate audience grants with person-signed partitioned caps; no borrowing or minting child grants |
| Caller disguised as person | Complete caller guard, agent credentials retain classification, person-bound confirmation; relay kind alone never proves person |

Same-user malware/OS administrator compromise and stolen authorized person-device
custody are outside AP1's containment guarantee. File MACs cannot defend an
attacker who controls the host key and all audit/custody files. Do not advertise
these grants as a sandbox. A compromised relay/account is not cured by lite trust;
remote acting still requires existing own-peer trust, a signed fleet-action envelope
and receiver-side grant checks, with the account-wide kill switch; no separate roster gate.

## Ordered implementation slices

Each row is a separate PR, **at most 400 WHOLE changed lines including tests/docs**.
Split a row further before implementation if measured scope cannot fit; no inherited
exception. Assignments and rollout need separate briefs and review; this contract is not a go for code.

| Order | Bounded deliverable and prerequisite | Focused acceptance |
|---|---|---|
| G1, first briefable | Versioned grant/subject/scope/cap validator and pure intersection evaluator; no storage, pipe handler or authority enabled | Missing caps, >24h, names-only subjects, unknown fields and wider policy refuse; explicit narrower choices pass |
| G2 | Private MAC store and audit-backed issue/revoke recovery; after G1 and persisted-launch dependency | Corrupt/foreign-key/oversize/write failures and rollback refuse; restart retains expiry/tombstones |
| G3 | Durable reservations, rolling counters and launch/process reconciliation; after G2 | Hour/day/live boundaries, competing requests, pending crash, pid reuse, backwards time; no refund on uncertain outcome |
| G4 | Person-only local issue/revoke CLI and receiver receipts; after G2/G3 | Agent/script/unknown ancestry denied before mutation; exact person digest and revoke acknowledgment |
| G5 | Local grant launch pipe/CLI adapter through existing queue, name gate and audit; after G3/G4 and AP1 launch authentication; includes receiver Allow-all policy (Nov 21, 12:47 PT) | Scope, exclusions, duplicate payload binding, execution-time revoke and failed audit; explicit Allow-all requires grant flag AND receiver policy, default false |
| G6 | Local grant stop adapter; after G5. G6a: receiver adapter and non-confirming, non-terminating agent stopper; G6b: durable stop replay, audit metadata and CLI contract reconciliation, stacked on G6a. CLI/queue/launch wiring stays with G5b2. | Own launches only, exact persisted exception, canonical approved workspace (including managed isolation provenance) and exclusions at preparation/dispatch; after the Ending await and at the injector slot, grant/current policy and intent audit finish before the final protected-state and process-identity check before keys; graceful timeout stays running and never calls Terminate; person stop unchanged |
| G7 | Windows app issuance summary/person confirmation; after G4 | Explicit subjects/caps, effective policy, signed receiver receipt; no unattended issue |
| G8 | Windows app list/counters/revoke and pending/offline state; after G7 | Receiver counters, tombstone receipt, in-flight visibility and stale-state labeling |
| G9 | Remote grant launch via signed FleetActionEnvelope v1; after G5 and existing own-peer messaging trust, no separate roster gate (Nov 21, 12:50 PT) | Wrong account/audience, envelope replay, stolen id, offline target and kill switch; receiver-side grant and same caps as local |
| G10 | Remote grant stop and multi-receiver issuance partitions; after G6/G8/G9 | Generation/target fence, quota sums, no borrowing, partial issue/revoke receipts |
| G11 | Fleet thin wrappers after app/local/remote acting proof; Allow-all policy belongs to G5, not a separate extension. Two slices: A launch-remote (after G9c2 and Host-transport evidence), B end wrappers (after G10); see [fleet-grants-cli.md](fleet-grants-cli.md) "G11 slices" | Policy-only wrappers call host verbs with explicit permissions; never HA fallback or person-confirmation automation; no wrapper is removed (G12) |
| G12 | Retire wrappers/tools sync after approved cutover evidence; phone grant UI remains later | No surviving launch/end HA submissions; preserve separately authorized messaging and all person guards |

Retirement targets are Fleet `launch-remote.ps1` launch-submit/MQTT/HA presses,
`end-one.ps1`, `end-many.ps1`, `end-sessions.ps1` ending presses and their launch
mutex/duplicate/counter implementations once receiver-host equivalents are proven.
First become thin policy wrappers, then remove in the umbrella's S5/S6 order.
Do not remove `reply-session`, messaging HA fallback or lookup scripts as a side
effect of this design; those have separate umbrella slices.
No wrapper may synthesize a grant, click a person confirmation or auto-retry an
unconfirmed request. Quality/SRE owns disposable-VM cutover evidence separately.

### G9 wire and cutover rows (HA retirement, #1396)

`FleetActionEnvelope v1` fields: `v`, `action` (`fleet.launch`; G10 adds `fleet.end`), `account`,
`sourceDevice`, `subject`, `audience` (receiver node id), `grantId`, `generation`, `requestId`, `nonce`,
`expiresAt` and `payloadSha256` (SHA-256 of the canonical launch request, base64url). It is signed by the
source device key and never accepted as an EnvelopeBody v1 message. G9 limits: expiry at most 120 s, a
128-bit random nonce, and a receiver replay cache keyed by (sourceDevice, nonce) kept until expiry plus
clock skew. An offline target refuses with the local exit codes; it never queues and never falls back to HA.
The G9 PR splits envelope (sign/verify, replay) from receiver (grant gate, launch) if over the PR cap.

Cutover order, each step gated on Quality/SRE Host-transport evidence, never on this doc alone:

1. G9 lands the remote launch path; `launch-remote.ps1 -Transport` default changes from `HA` to `Host`
   in a separate small Fleet PR (HA remains selectable only until row 4).
2. The Fleet dispatcher's FleetLaunch stops its three HA calls and calls the host verb.
3. G10/G11 follow for end and wrappers.
4. G12 removes the HA launch path: `launch-remote.ps1` HA/MQTT presses and the HA connector's
   `launch_options` and `new_session_result` entities (HaConnector.Launch, HaContract.Launch), with tests.

No open decision is required to brief G1. Remote acting requires existing own-peer
messaging trust, a signed fleet-action envelope and the receiver-side grant, not a roster;
remote issuance keeps its person-device/roster gates. G5 owns the Allow-all extension
under Nov 21, 12:47 PT approval, never authority inferred from messaging cutover. Questions or scope changes
route to the Chief, never directly to the owner from a builder.
