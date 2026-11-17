# Relay-side APNs registration binding

**Spec only; based on A1/A2 head `3e0197ddadc1ad7bcef84606d742dc2c13fdc1a4` and merged design PR #2437.** No code, credentials, rollout, or delivery is authorized.

## Trust boundaries and relay identity

Only APNs handlers enforce roles; all other admission and operations remain
unchanged. Roles come only from the independently verified signed device roster
(`docs/protocol/devices.md`), not `hello.kind`, `MachineRoster`, or account
token; identity is the connection's proven raw `dh`, not a supplied device ID.

A host's active v2 `hostSignProof` binds its separate `HostSignKey` to account,
roster generation, and exact `dh`; `OpenExisting` fails closed if absent. A
person needs an active #2385 v3 `capSignProof` bound to the same tuple; phones
sign with capSign, never ECDH `dh` (Apple custody is Secure Enclave).
Both descriptors use signed-roster code/QR approval; no deployment control
plane exists. The 32-byte one-use, 60-second challenge binds socket,
`relayId`/origin, account, role, `dh`, and generation. `apns-enroll` signs
`F("agent-bridge apns enrollment v1", relayId, origin, account, generation, deviceId, dh, role, nonce)`
with hostSign/capSign. Relay derives identity from the connection and verifies
the active roster entry; the shared GitHub token authenticates only account.

Only accepted active entries authorize APNs; held, quarantined, and removed
entries do not. `DeviceRoster` and APNs are separate blobs. The authoritative
roster snapshot exposes its Blob ETag and accepted frontier (`generation`,
`heads`); APNs state records both as `rosterFence` with each binding's roster
identity and generation. No cross-blob transaction is assumed.

### Proof-signing key and host trust

`relayId` matches `[a-z0-9][a-z0-9-]{0,62}`, is immutable and unique per
deployment, and is neither URL nor account name. Owner-controlled host config
pins it to a canonical `wss://` origin (lowercase host, no default port,
path, or query) and managed-secret P-256 JWS ES256 public key.
`proofKeyId` is lowercase hex SHA-256 of the raw SEC1 point. `ready` carries:

```json
{"capabilities":{"apnsRegistration":{"v":1,"relayId":"...","origin":"wss://...","proofKeyId":"<64 lowercase hex>"}}}
```

The object is absent unless handlers, store, verifier, and identity binding are
ready. Host checks all fields and actual socket origin against its pin and
refuses to sign or send on mismatch (`relay-identity-mismatch`). Proofs
bind `relayId`/`origin`, with `iss=relayId`; unknown keys/IDs fail closed.
Rotate by pinning new, dual-accepting, then retiring old after receipts renew.

## Azure state and rollout overlap

Require `Backend=azure` and a dedicated APNs Blob container using the
`AzureRosterStore` ETag pattern in `Roster.cs`. One opaque account-keyed blob
holds enrollment, authorization, generation, connection-fence, and limiter
state. Missing config/storage disables APNs readiness, not general relay
service; APNs never uses memory or raw account IDs in blob names.
Use `If-Match`/`If-None-Match`, retry conflicts up to five times, then fail
closed with `relay-busy`; unknown schemas and store errors fail closed too.

Cap each account at 64 enrolled devices, 384 registration records (roughly
450 bytes each, leaving headroom under 240 KiB), and 64 pending grants; enforce
per-device/host limits. Refuse mutations at caps with `state-capacity`; prune
revoked records only after all five-minute proofs expire. Preflight writes at
240 KiB, below `AzureRosterStore`'s 256-KiB empty-body boundary. An oversized
read is corruption, never an empty account; do not overwrite or evict active
state, or write an oversized blob.

Both relay versions in the approximately 90-second deployment overlap use the
same version-1 blob schema and ETag CAS. Registrations and pending grants count
once across versions. Every cap check, generation change, authorization
transition, connection-fence update, and limiter reservation is ETag-guarded.
Store rolling 60-second timestamps for the account and each host. One CAS
prunes expired timestamps, checks limits (20/host, 30/account), and appends the
reservation before provider I/O; provider failures consume quota, validation
refusals do not. Overlapping versions share caps and limiter state. Unsupported
schemas never advertise APNs; deploy never resets state. Keep `maxReplicas: 1`
until a reviewed multi-replica design exists.

Advertise the exact object only when APNs handlers, verifier, parser, Azure
state, key/origin, target map, and provider boundary are ready; clients require
it. Roll out state/verifier, then clients. Legacy devices stay connected but
APNs-disabled until their host v2/person v3 entry is approved in the roster.

## Person-device authorization and A2 publication

The person device obtains a one-use authorization directly from its own
authenticated relay connection:

```json
{"t":"apns-authorize","id":21,"hostId":"<enrolled host device>","registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","token":"<APNs hex token>"}
```

All APNs frames use strict JSON parsing: duplicate properties, unknown
properties, and wrong types are rejected.

The relay derives account and `dh` identity from the authenticated connection.
It requires the phone's active v3 roster entry and the selected host's active
v2 entry in that account, an allowed target, a valid registration ID, and the
A1-bounded hexadecimal token. It creates a cryptographically random,
five-minute pending authorization with a 16-byte unpadded-base64url
`authorizationId`, bound to account, person-device and host enrollment
generations, host, target, registration ID, `registrationGeneration`, and
SHA-256 of canonical token bytes. Pending authorizations are durable metadata
only (never token bytes), capped at 8 per person device and 64 per host.
Exceeding either cap returns `authorization-limit`.

The JWS contains `iss=relayId`, `origin`, `aud=hostId`, `sub=personDeviceId`,
the IDs, both enrollment generations, slot `registrationGeneration`, target,
token digest, and five-minute expiry. An exact active tuple returns its
receipt; an exact pending tuple returns the same proof. A token or ID change in
the same slot increments its generation, cancels old pending proofs, revokes
the old active authorization/binding, and reserves the slot. A different source
device cannot take over a registration ID. A target change selects a distinct
slot; it never replaces the prior target's registration.

The app passes the authorization proof and token with `push.apns.subscribe` to
the host over the existing encrypted channel. A2 verifies the signature and
claims against the authenticated `PushPeer.Device`, account, host, target,
registration ID, token digest, and generation, then applies existing
own-account/person-device policy. A mismatch returns
`registration-proof-invalid`. A2 persists the token and pending proof before
publishing. The proof carries the pending ID and generation so opt-out can
revoke it while publication is in flight; on success A2 saves and returns the
active receipt.

```json
{"t":"apns-register","id":22,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","token":"<APNs hex token>","authorizationProof":"<relay-signed five-minute proof>"}
```

The register frame travels only on the authenticated, TLS-protected relay
connection. The relay requires current host/person enrollment generations and
a matching pending authorization, signature, token digest, and registration
generation. Under the lifecycle gate it commits the active record to Azure,
then installs the memory binding; a failed commit installs nothing. The
returned signed receipt includes the same claims and remains valid only while
the durable authorization and registration generation are active. A replay of
an already-consumed proof returns its receipt; expired/revoked pending proofs
cannot activate. `apns-register` carries exactly one of `authorizationProof`
or `registrationReceipt`. Host state becomes active only after the receipt is
persisted; uncertain responses retry idempotently after reconnect.

The A2 `PushStore` persists token, registration ID, and receipt in
platform-protected storage. After reconnect, the host republishes saved
registrations before enabling sends:

```json
{"t":"apns-register","id":24,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","token":"<APNs hex token>","registrationReceipt":"<relay-signed active receipt>"}
```

Relay verifies receipt signature, active record, token digest, and generation,
then restores the binding. Delivery resumes without the phone app reopening;
failed restore leaves that ID disabled. Retries are idempotent; key rotation
returns a current-key receipt.

`RemoteAuthorization.PersonOperations` must include `push.apns.subscribe`,
`push.apns.unsubscribe`, and `push.apns.list`.

## Binding, lifetime, revocation, and races

`rosterGeneration` comes from the signed roster; `registrationGeneration` is
monotonic per slot; `hostConnectionFence` is monotonic per host. Never compare
or substitute one for another.

The `RelayApnsAuthorization` records live in the per-account Azure blob above,
not on relay disk. Keep pending/active/revoked authorization IDs, enrollment
generations, registration ID, target, token hash, expiry, and receipt
generation; never store the raw token. Enforce one active/reserved record per
`(account, host, source device, target)` slot; its monotonic
`registrationGeneration` owns one ID and token hash. A new ID plus token
atomically revokes the old authorization,
receipt, and binding in that slot in one APNs ETag CAS. Reusing an ID in a
different slot returns `registration-id-conflict`. The A2 `PushStore` key is
`(Device, Target)`, matching source and target. Keep revoked IDs/fences until
all five-minute proofs expire; an active receipt has no five-minute timeout,
but is valid only while its record, generation, and roster fence remain
current. Use SHA-256 over canonical A1-validated token bytes. Storage errors
fail APNs closed.

Each host connect increments a shared per-host `hostConnectionFence` by ETag
CAS; the accepted generation is attached to that connection. A bounded,
thread-safe process-memory map keyed by
`(accountId, hostId, registrationId, target)` holds authorization ID,
registration generation, source/host generations, and provider token bytes.
At the relay, raw tokens exist only in transit and
this map; never persist or log them. Clear buffers on replacement, revoke,
disconnect, and shutdown where supported.

On reconnect, the host restores active registrations from `PushStore` using
each token and signed receipt; relay verifies the active record and binds to
the new fence. `RelayClient` blocks sends until restore succeeds.

Before each APNs CAS, fetch/verify the authoritative roster and compare its
ETag/frontier with `rosterFence`. On mismatch, return `roster-stale`; CAS-revoke bindings for
unaccepted hosts/sources, cancel pending proofs, and advance the fence.
Idempotent reconcile runs at startup, reconnect, and mismatch. A crash before
cleanup leaves a stale fence, so the next operation blocks and reconciles.
Roster read, verification, reconcile, or CAS failure disables APNs for account.

Each send then CAS-checks the current authorization ID/generation, host/source
enrollment generations, roster fence, and host connection fence, and reserves
the limiter. Re-read the roster after reservation and before provider I/O; a
changed ETag/frontier prevents the send and triggers reconcile. The final
matching read is the send's authorization point: a later removal may race only
with that already-accepted in-flight provider request. If revoke commits first,
every relay copy refuses; no later reservation can proceed.

The host revokes by naming the current authorization ID and generation (from
the pending proof when replacement is in flight, otherwise from the receipt),
not only the registration ID:

```json
{"t":"apns-unregister","id":24,"authorizationId":"<current authorization ID>","registrationGeneration":2,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod"}
```

The enrolled source person device may also send `apns-revoke` on its own
authenticated relay connection, naming the selected host, registration ID,
current `authorizationId` from its subscribe result (including a pending
authorization when replacement is in flight):

```json
{"t":"apns-revoke","id":25,"hostId":"<enrolled host device>","registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","authorizationId":"<current authorization ID>","registrationGeneration":2}
```

In one ETag transaction, either revoke verifies the current authorization ID
and generation, increments `registrationGeneration`, revokes it, and cancels
all pending authorizations for that tuple. It then removes the memory binding
under the lifecycle gate before acknowledging. The relay derives person
identity from the phone connection and checks the enrolled source. If token B
is pending after replacing active token A, opt-out names B's pending ID and
generation; naming stale A returns `stale-registration`. Repeating a revoke
for the already-revoked ID is a successful no-op and cannot affect a newer
generation. The host persists pending/active/revoking state and retries the
same current ID after reconnect.

For a token or ID change in a slot, authorize increments the slot generation,
revokes A, and creates pending B. Revoke B increments it again. Register B may
activate only if its proof generation and pending ID still match in the same
CAS; otherwise return
`stale-registration`. Thus if activation wins first, revoke removes B; if
revoke wins first, delayed activation cannot install B. Device/host enrollment
revocation likewise increments enrollment generation and cancels its pending
and active records atomically.

The relay processes APNs control frames in receive order on one per-connection
queue; it must not dispatch register, unregister, and send work as independent
fire-and-forget tasks. The host waits for `apns-register-result` before sending
with that registration ID. Registration installation and connection teardown
share a lifecycle gate: registration commits only if its host generation is
still current. If teardown wins, return `connection-ended` and install nothing;
if registration wins, teardown removes it before the connection is retired.

When a device reconnects with the same account/device ID, the relay marks the
old generation inactive, cancels and closes it, and clears its bindings before
the replacement is made ready. Every register/send handler checks the active
generation. The old loop cannot publish a binding or send after replacement,
even while its socket is still unwinding. The new host restores stored
registrations before any send for those IDs is permitted.

`registrationId` is 16 cryptographically random bytes encoded as unpadded
base64url. Re-registering the same token hash and ID in the same slot is
idempotent. A new ID plus token replaces the one record in its slot atomically;
the old receipt becomes unusable and does not consume another slot. Reusing an
ID for a different source device or target returns `registration-id-conflict`.

Active/reserved registrations cap at 16 per host; all active/reserved/recently
revoked records share the 384-per-account cap. Replacements reserve a slot.
Exceeding a cap returns `registration-limit`.
Missing, foreign, revoked, expired, un-restored, or disconnected bindings
return `unbound-registration` without revealing state or calling the provider.

The five-minute expiry applies to pending authorization proofs only. A receipt
is an active-state credential, invalidated by replacement, revoke, enrollment
change, roster-fence change, or proof-key rotation; it may be reissued after
the relay verifies the current active record.

## Send frame, target, payload, and rate boundaries

The host sends an encrypted notice and an opaque registration reference; it
never sends a token:

```json
{"t":"apns-send","id":23,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","noticeClass":"needs-you","sound":"default","priority":10,"keyId":"opaque-id","ciphertext":"<base64 encrypted record>","expiration":1790000000,"collapseId":"<bounded value>"}
```

`target` must match the binding and a finite deployment map of APNs environment,
bundle topic, and fixed Apple sandbox/production origin. Frame data cannot
choose an endpoint, URL, port, topic, provider credential, redirect, or bundle
ID. The relay uses only the configured mapping.

`noticeClass` is `needs-you` or `done`; `sound` is `default` or `none`;
`priority` is 5 or 10. The host selects these from existing notice policy.
`done` must use `sound: none` and priority 5; any other combination is refused
as `invalid-frame`, and the payload contains no `aps.sound`. Thus Done is
always silent even if a buggy host requests sound. For other notices, the relay
includes sound only when selected by the host's existing quiet/privacy policy.
`expiration` is the host's Unix-seconds notice expiry. The relay uses its own
UTC clock and computes `effectiveExpiration` as the lesser of the requested
expiry and `relayNow + 86,400 seconds`; it rejects an effective expiry at or
before `relayNow` as `notice-expired`. This clamps a fast host clock to the
relay's 24-hour maximum. A slow host clock may cause an early expiry but can
never extend delivery. A4 sends only `effectiveExpiration` to APNs.
`collapseId` is at most 64 UTF-8 bytes. A4 maps
priority, effective expiration, collapse ID, push type `alert`, topic, and
sound to APNs headers/body.

Reject unknown or duplicate properties, wrong types, malformed IDs, invalid
base64/ciphertext, unsupported targets, expired notices, and invalid policy
combinations before provider I/O. Serialize the complete APNs JSON body once
as UTF-8 and measure those exact bytes, including JSON structure and escaping.
Accept at most 4096
bytes; otherwise return `payload-too-large`. Never truncate ciphertext or omit
fields to fit. The outer alert is generic; session/question text stays
encrypted.

Use the Azure blob's ETag transaction for a rolling 60-second limiter:
20 sends per host and 30 per account. The same timestamp reservation is shared
by overlapping relay versions. Validation refusals do not consume quota;
provider failures do. Do not add a separate distributed limiter. Before
raising `maxReplicas: 1`, APNs must stop advertising its capability until a
replicated binding design is reviewed and implemented.

On APNs 410, A4 CAS-revokes only the matching active slot generation and token
hash, then clears its process-memory binding. A concurrent replacement wins
over a stale 410; the invalidation test must prove it cannot revoke the new slot.

## Privacy and observability

The relay may observe authenticated account/host identity, enrolled source
device ID, target, registration ID, token hash, payload size, timing, outcome,
and bounded rate-limit counters. Raw tokens travel only over the authenticated
TLS relay link and live in memory while bound; durable authorization metadata
contains only SHA-256 token hashes. Never log raw tokens, token hashes, proofs,
receipts, ciphertext, complete request/response bodies, APNs URL paths,
provider authorization, or user/session/question content. Metrics use bounded
outcome labels only; never use account, host, device, registration IDs, or
token hashes as labels. Errors contain only
stable refusal codes such as `host-required`, `device-not-enrolled`,
`registration-proof-invalid`, `authorization-expired`, `authorization-limit`,
`authorization-key-unknown`, `unknown-target`, `unbound-registration`,
`registration-limit`, `registration-id-conflict`, `state-capacity`, `stale-registration`, `roster-stale`,
`connection-ended`,
`notice-expired`, `payload-too-large`, `rate-limited`, `relay-unsupported`,
and `relay-unavailable`, `relay-identity-mismatch`.

## Tests-first implementation cuts

The full cut order, external gates, ownership, tests-first cases, and line
estimates are in [the companion cut plan](apns-registration-binding-cuts.md).
The estimates total about 4.7k changed lines; every cut is under 250. No cut
enables delivery. A4 owns provider transport; merged #2437's A5 owns host sending. Two reader families and Chief acceptance are required before A3 code; no
provider provisioning, production rollout, physical-device proof, or A1/A2
edits are authorized.
