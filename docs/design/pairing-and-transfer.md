# Pairing and session transfer on every client

**Proposed design; docs only, #1418, 2026-11-11 PT.** This completes the S6 design
scope of [#1396](https://github.com/danswett/agent-bridge/issues/1396), not its code
or live acceptance. [One client protocol](client-protocol.md) (#1406) supplies the
operations and caller boundaries. No PWA-only or HA-only path is introduced.
The [parity pairing row](../parity.md#17-security-identity-and-pairing) remains missing
until runtime enrollment and the HA-free acceptance run are proved.

## Authority and security rules

The decided [device-approval design](device-approval.md), especially sections 4,
6 and 7, and [devices.md](../protocol/devices.md) own enrollment, presence,
probation, freshness and revocation. [Agent principals](agent-principals.md),
sections 3 and 8, own agents and connector identity. This note preserves them:
account sign-in, relay presence, a claimed kind/name or a matching code alone
never grants authority. A host verifies its own trust state on every action.

| Caller | How it joins; code approval | Who can approve; resulting authority |
|---|---|---|
| Person's client | Generates its own `dh` and `sign` keys; mutual code ceremony below; verifies its exact signed roster add | Existing active, non-probationary, presence-proven person's device signs. `presence: none` clients can join but cannot approve or authorize transfer |
| Machine host | Joins as `host`, never as the person's app; same mutual ceremony, with code 2 entered by the person at that machine | Same person approver; host receives only host authority. Host `hostSign` binds statements, never roster entries or person commands |
| Session agent | Does **not** pair, get a device code or join the roster; its already approved host issues a process-bound credential at launch/adoption | Person controls host policy/adoption. `agent.hello` authenticates locally; no pairing, roster approval or transfer scope |
| Optional HA backend | Distinct connector key and selected-host read grant; never enroll as `app`, `pwa`, `phone`, host or agent | Person approves its full key and host scope. Production connector enrollment remains gated below; no person signing key is lent to it |
| Person using an HA card | A separately paired person-client channel/key uses the same mutual ceremony and person commands | Same qualified person approver/checks as any other client; HA login or service events never prove a person acted |

**No security-rule change is proposed.** In particular, no agent or unattended
connector can approve a code, genesis, trust prompt, permission or transfer.
If implementation needs a new authority or enrollment rule, stop that slice and
route the delta through Product's one-screen owner packet before building it.
This note itself needs two different-model-family reviews of the whole note.

## Mutual code ceremony

1. New device N signs in and proves possession of its own key. It is pending,
   not approved. In the enforcing design it sees no sessions, machines, roster,
   usage, blobs or pushes; enrollment traffic alone is permitted.
2. Approved person device A opens the enrollment Noise XX channel to N. Reuse
   the commit/offer/reveal exchange and framing in devices.md; account, channel
   handshake and fresh nonces bind its transcript T. A checks key possession;
   a host's v2 descriptor/proof follows the reviewed roster-version switch.
3. N shows six-digit code 1. The person types it at A; spaces are dropped,
   exactly six ASCII digits are required and comparison is constant-time.
   The screen labels name/kind/platform as claims, shows the prescribed
   16-character key identifier and says to approve only a device held now.
4. Only after code 1 matches does A show code 2. N shows the account/genesis
   fingerprint; the person checks it and types code 2 at N. This authenticates
   A to N too: a relay terminating two channels cannot reuse either transcript.
5. After N confirms code 2, A obtains fresh presence proof and signs the exact
   roster `add`, including T and N's descriptor. N verifies the chain, approver,
   its own keys/kind/presence and T before accepting. A moved head requires
   re-verification and a new person signature, not silent re-signing.
6. Reconnect as approved only after that verified add; normal probation and
   host freshness/revocation checks still apply. Code digits are not credentials.

One attempt per approver and joiner lasts **five minutes**. An expiry terminates
it, including an otherwise matched exchange left open; reconnect never extends
it. Wrong/malformed codes refuse without adding a device. Three wrong code-1
entries block that pending device at A for **15 minutes**; three wrong entries
within an hour lock A's enrollment for 15 minutes. Three wrong code-2 entries
end N's attempt; three ended attempts within an hour lock joining for 15 minutes.
Persist/restore those local counters and blocks; restart cannot reset limits.
Relay limits remain eight pending devices and 20 attempts per account per hour.

An expired, ended, consumed or different-channel transcript cannot approve an
add. Replaying a signed add cannot create another enrollment; already approved
and previously removed keys are explicitly refused. Fresh attempts use fresh
nonces, not a saved code (six-digit values can coincidentally repeat).
Show the refusal at both ends; logs contain outcome/attempt tags, never codes,
nonces, QR secrets, tokens or private keys. Existing mutual QR proofs remain an
alternative under device-approval section 4.3, not a weaker one-tap bypass.

App and host enrollments are separate entries even on one machine. The app
reads the host public key over its app-only local pipe, shows the fingerprint
and accepts code 2; A signs each entry with its own presence proof. A headless
host uses the existing proposed `devices join` terminal flow, refused inside an
agent session. This design does not authorize an agent to enter either code.
Genesis/recovery stay explicit person actions; an empty/unavailable relay never
means permission to bootstrap a replacement roster.

## Optional HA enrollment boundary

The backend generates and protects its own persistent connector key. Existing
agent-principals section 8 requires full-key approval at the selected host, a
local revocable/expiring read grant, and a distinct key-bound admission
credential. Its initial operations are only `hello`, `sessions.list`,
`session.get`, `sessions.subscribe`, using the restricted read projection.
Renewal proves the same key and cannot extend the grant. Revocation closes its
channels/subscriptions; restore cannot silently reactivate an old grant.

**Not implemented or authorized here:** the connector roster kind, enrollment
wire format/claim, production trust anchor and mixed-fleet enforcement gate.
They remain #388/#1419 work, not permission to insert an unknown kind into
today's strict parser. Any code-assisted connector enrollment must bind the
full key/account/selected host/grant and retain person approval; a short code
alone cannot replace full-key approval or supply person authority. Until those
decisions and code land, production pairing is unavailable, not a fallback to
an HA token or a person's account credential. The HA card's separate person
client must complete the ordinary code ceremony before any person action.
HA is optional later, never a dependency of pairing or transfer.

## Session transfer: a fork, not a move

Use [v1-transfer.md](../protocol/v1-transfer.md) and
[host-transfer.md](../host-transfer.md): the person chooses an ended source
session, a paired target, an installed compatible launcher and an explicitly
approved target workspace. Each client reads the same `launch.options`;
`sessions.transfer.serve` and `sessions.transfer` advertise host support.

The person approves a command naming the source/session and target. The target
receives `session.launch` with `transfer` and carries the unchanged command to
the source's `session.transfer`. Both machines must be active rostered hosts;
the signer must be an active, non-probationary, presence-proven person's device,
not either carrier/target host, and must still be in presence. Agents and
connectors may neither request nor approve this; a launch grant is not transfer.

**Enforcing target, not a claim about today's runtime:** #55 R6 replaces the
legacy CommandMac/self-declared-kind check with the presence-signed person
command. Source checks account/generation, roster heads/freshness, target and
destination, lifetime (at most ten minutes, clients issue five), future skew
(at most two minutes), and durable single-use replay state before any packing.
An unreadable/unwritable replay store refuses. No compatibility MAC fallback
is allowed in enforce mode; the existing spec documents that runtime gap.

Only the allowlisted session transcript/artifacts move, sealed for the target
through the blob store. Never provider credentials, device/person/agent keys,
policy, hooks, repository/worktree contents or source workspace authority.
Target needs its own provider sign-in; a resumed agent gets a new local
credential, never the source token. Target validates hash, file names/types,
compressed/unpacked limits and transcript, installs under a **new ID**, rewrites
the supported ID/workspace metadata, verifies installation, then resumes.
The original is unchanged; handoff-note adapters do not authorize a live move.

Refuse live/starting or no-longer-resumable sessions, unpaired/revoked/wrong-account
hosts or signer, stale trust, changed files, tampered/oversized bundles,
unsupported agent or missing workspace/capability. Preserve folder-trust
refusals; transfer never answers one. Partial failure is explicit, never an empty
successful resume. Keep private staging cleanup and blob deletion as specified.
Retry the **same launch request and command**: wait up to 12 minutes, resend only
within 20 minutes; host retains outcomes at least 25 minutes after settlement.
A lost outcome is uncertain, not consent to mint a second transfer.

## Every client, one protocol

| Client | Pairing required | Transfer required |
|---|---|---|
| PWA | Same code screens and roster vectors; WebCrypto keys. iOS synced passkeys do not qualify for approval | Same launch/transfer ops; `presence: none` cannot sign; show the gap and use a qualified paired person device, never downgrade |
| Windows | Separate app/host entries; approval only after the external-presence mechanism is proved | Same ops and target workspace picker; required person command, not the local pipe |
| Mac | Same ceremony; qualifying Touch-ID-only key, else `none` | Same ops/person checks; parked implementation is missing evidence |
| iOS | Same ceremony; native presence-proven key, unlike an unqualified browser credential | Same ops; native presence confirmation before signing; parked work remains a gap |
| Android | Same ceremony when native client ships; qualify device-bound presence under existing rules | Same ops and person checks; Android PWA proof is not native-client certification |
| Linux later | Same ceremony when shipped; approval remains unavailable without a proven presence mechanism | Same ops; no software-key or terminal bypass of person checks |
| Optional HA UI | Separate paired person channel; backend enrollment remains the gated service flow above | Person UI uses the same ops/confirmation; backend service transfer always refused |

Capabilities in host `hello` describe features, not grants; use #1406's negotiated
intersection. Missing enrollment, presence or transfer support is an explicit
gap/refusal, not an HA fallback or a PWA-only exception.

## Placement, state and refusal evidence

Clients run on their own devices; each host serves its sessions, none is a fleet
coordinator. Relay-protected account roster state survives a serving node leaving.
Devices retain verified roster heads, protected keys, lockouts and replay state
locally as their security contracts require; client caches grant nothing. Losing
a host makes its sessions unavailable, not transferable by another host using
its keys. Other paired hosts keep working; a restarted device re-verifies trust.

Pin the following in #55 vectors and the single #1407 ClientConformance suite,
then exercise pairing and a two-host transfer without HA in the existing #736
disposable lab. Record per-client pass/fail/missing; code is not live proof.

| Refusal case | Required assertion |
|---|---|
| Expired/wrong code | No add/read/action; wrong-code lockout and five-minute deadline survive restart |
| Wrong account/approver | No trust from an unrelated genesis, channel, key, generation or presence-less signer |
| Replayed code/add/command | Old T cannot approve a fresh attempt; duplicate add grants nothing; transfer executes once across restart and lost outcome |
| Transfer to unpaired machine | Refused before packing/blob/launch, also for removed source, target or signer |
| Agent/service relabels itself | No pairing approval or transfer, even claiming `pwa` or presenting a host/agent key |
| Tampered bundle/busy source | No installed empty session, no source modification, no folder-trust answer |

## Proposed code slices (estimates include focused failing-first tests)

Each row is a separate file area/PR after this design is accepted; no estimate is
permission to exceed 400 changed lines. Reuse merged #55 work; split further if
needed. Each security/authority change needs two exact-head review families.

| Slice | Area; predecessor | Changed lines |
|---|---|---|
| P1 | Roster negative vectors for expiry/account/replay; existing #55 exchange | 100-180 |
| P2 | Host enrollment transport and persisted lockouts; P1, #55 verifier/version switch | 150-200 |
| P3 | PWA mutual-code UI/adapter tests; P2 | 150-200 |
| P4 | Windows app/host join UI tests; P2 and proven presence support for approvals | 150-200 |
| T1 | Source person-command/replay refusal tests and enforcement; #55 R6 | 150-200 |
| T2 | Target paired-host checks and lost-outcome tests; T1 | 100-180 |
| C1 | Conformance pairing/transfer driver cases per shipped client; P3/P4/T2, #1407 | 150-200 each |
| H1 | HA connector enrollment contract/vectors only; #388/#1419 decisions, no production issuance | 100-180 |
| L1 | Disposable two-host HA-free acceptance evidence adapter; C1, existing #736 runner | 100-180 |
| Later | Mac/iOS/Android/Linux adapters, one client/area per PR when unparked/shipped; P2/T2 | 150-200 each |
