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
G4 adds the Windows person-only local CLI and read-only verified listing; no acting adapter or counters.
G5a completes counter receipts: exact duplicates return their recorded reservation,
outcome and usage without a second admission, including after grant revocation/expiry.
Refused/failed terminals retain hourly/daily spend but release live capacity; settled
bindings remain 30 days after completion, while pending/uncertain/live bindings stay.
At 4096 retained requests or the existing store-size/intent bound, new admissions refuse full.

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
until trustworthy time catches up. Expiry, caps and kill are checked again at the
actual execution slot, not only when queued. A large forward jump expires rather
than extends authority. UI dates/times follow the viewer's local-time convention.

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
is displayed; the person types the exact SHA-256 digest in an unredirected terminal.
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
| G11 | Fleet thin wrappers after app/local/remote acting proof; Allow-all policy belongs to G5, not a separate extension | Policy-only wrappers call host verbs with explicit permissions; never HA fallback or person-confirmation automation |
| G12 | Retire wrappers/tools sync after approved cutover evidence; phone grant UI remains later | No surviving launch/end HA submissions; preserve separately authorized messaging and all person guards |

Retirement targets are Fleet `launch-remote.ps1` launch-submit/MQTT/HA presses,
`end-one.ps1`, `end-many.ps1`, `end-sessions.ps1` ending presses and their launch
mutex/duplicate/counter implementations once receiver-host equivalents are proven.
First become thin policy wrappers, then remove in the umbrella's S5/S6 order.
Do not remove `reply-session`, messaging HA fallback or lookup scripts as a side
effect of this design; those have separate umbrella slices.
No wrapper may synthesize a grant, click a person confirmation or auto-retry an
unconfirmed request. Quality/SRE owns disposable-VM cutover evidence separately.

No open decision is required to brief G1. Remote acting requires existing own-peer
messaging trust, a signed fleet-action envelope and the receiver-side grant, not a roster;
remote issuance keeps its person-device/roster gates. G5 owns the Allow-all extension
under Nov 21, 12:47 PT approval, never authority inferred from messaging cutover. Questions or scope changes
route to the Chief, never directly to the owner from a builder.
