# Relay-side APNs registration binding

**Revised proposal; specification only.** Based on A1/A2 head
`3e0197ddadc1ad7bcef84606d742dc2c13fdc1a4` and merged design PR #2437. No
APNs implementation, provider credentials, rollout, or device delivery is
authorized.

## Trust boundaries and relay identity

Do not change general relay admission: existing phone and host connections
remain governed by current behavior. `RelayDeviceEnrollment` is checked only by
APNs handlers; an unenrolled device can still use existing non-APNs operations.
The durable record is keyed by `(accountId, deviceKeyThumbprint)` and stores
`deviceId`, `role`, generation, status, and creation/revocation times. Handshake
proof-of-possession derives `deviceId`; `hello.kind` never grants APNs role.

An owner-authenticated control-plane operation `EnrollHost(accountId,
devicePublicKey)` verifies the deployed host, then writes its host role. An
account-authorized `EnrollPersonDevice` operation verifies phone key possession
and writes its person role. These control-plane operations are the only writers;
WebSocket frames and account tokens cannot enroll/promote devices. Revocation
increments the record generation. `DeviceRoster` remains an untrusted,
observe-only account log, not role authority.

### Proof-signing key and host trust

The relay signs proofs as JWS ES256 with a dedicated P-256 private key from
managed secret storage. Hosts validate relay TLS and pin the public key through
the same owner-controlled deployment configuration that supplies the relay
URL. `ready` advertises only the key ID, never a trust-on-first-use key. Reject
unknown keys, invalid signatures, and unsupported algorithms. Rotate by
distributing the new public pin first, signing with the new key while accepting
both IDs, and retaining the old verifier until all durable receipts are
reissued or revoked. Missing key or enrollment control plane disables APNs.

## Azure state and rollout overlap

Use a dedicated APNs Azure Blob container via the existing `IRosterStore` /
`AzureRosterStore` read-and-conditional-write pattern used by `DeviceRoster`:
one opaque, account-keyed state blob holds enrollment, pending/active/revoked
authorizations, caps, and limiter timestamps. Never use a local database,
memory fallback, or raw account ID in blob names. Updates read the blob and
write with its ETag (`If-Match`; create with `If-None-Match: *`); on conflict,
reload and reapply up to five times, then fail closed with `relay-busy`.
Unknown schema versions or storage errors also disable APNs.

Both relay versions in the approximately 90-second deployment overlap use the
same version-1 blob schema and ETag CAS. Registrations and pending grants count
once across versions. Every cap check, generation change, authorization
transition, and limiter reservation is an ETag-conditional state update.
Store rolling 60-second timestamps for the account and each host in that blob.
After removing timestamps at least 60 seconds old, one CAS checks limits
(20/host, 30/account) and appends the reservation. Only then call the provider;
provider failure conservatively consumes quota. Validation refusals consume
none. Thus overlapping versions share limits; there is no process-local
counter or fallback. A version that cannot read/write this schema does not
advertise APNs. Migrate only after overlap ends; never reset state on deploy.
Keep `maxReplicas: 1` until a reviewed multi-replica design exists.

`ready` advertises `capabilities.apnsRegistration` to phones and hosts only
when authorize/revoke, register/restore/unregister/send handlers, strict parser,
Azure state, proof key, target map, and provider boundary are ready. New clients
check the flag before an APNs request and return `relay-unsupported` immediately;
an older relay therefore cannot time out on a phone's first authorize.

## Person-device authorization and A2 publication

The person device obtains a one-use authorization directly from its own
authenticated relay connection:

```json
{"t":"apns-authorize","id":21,"hostId":"<enrolled host device>","registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","token":"<APNs hex token>"}
```

All APNs frames use strict JSON parsing: duplicate properties, unknown
properties, and wrong types are rejected.

The relay derives account and person-device identity from that connection. It
requires an active person enrollment, an active host enrollment for `hostId`
in the same account, an allowed target, a valid 16-byte registration ID, and
the A1-bounded hexadecimal token. It creates a cryptographically random,
five-minute pending authorization with a 16-byte unpadded-base64url
`authorizationId`, bound to account, person-device and host enrollment
generations, host, target, registration ID, `registrationGeneration`, and
SHA-256 of canonical token bytes. Pending authorizations are durable metadata
only (never token bytes), capped at 8 per person device and 64 per host.
Exceeding either cap returns `authorization-limit`.

The JWS contains `iss=relayId`, `aud=hostId`, `sub=personDeviceId`, the IDs,
both enrollment generations, registration generation, target, token digest,
and five-minute expiry. An exact active tuple returns its receipt; an exact
pending tuple returns the same proof. A token or target change increments
`registrationGeneration`, cancels old pending proofs, revokes the old active
authorization/binding, and reserves its slot. A different source device cannot
take over a registration ID. Reusing the same token/target under a new ID
atomically replaces the old ID and receipt.

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

The receipt is bound to the active durable authorization record and remains
valid until revoke or replacement. The A2 `PushStore` persists the APNs token,
registration ID, and receipt in platform-protected storage. After every host
reconnect, the host re-publishes each saved registration before enabling APNs
sends:

```json
{"t":"apns-register","id":24,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","token":"<APNs hex token>","registrationReceipt":"<relay-signed active receipt>"}
```

The relay verifies receipt signature, active record, token digest, and
generation, then restores the binding for the new host connection. Delivery
resumes without the phone app reopening. Failed restore leaves that ID disabled.
An idempotent retry returns the same receipt; key rotation returns a new
current-key receipt.

`RemoteAuthorization.PersonOperations` must include `push.apns.subscribe`,
`push.apns.unsubscribe`, and `push.apns.list`.

## Binding, lifetime, revocation, and races

The `RelayApnsAuthorization` records live in the per-account Azure blob above,
not on relay disk. Retain pending/active/revoked authorization IDs, enrollment
generations, registration ID, a monotonic generation per
`(account, host, source device, registrationId)`, target, token hash, expiry,
and receipt generation; never store the raw token. Keep revoked IDs and
generation fences until all associated five-minute proofs expire; a receipt
for a revoked record can never restore it. Use SHA-256 over canonical
A1-validated token bytes for deduplication and restore. The active binding
lookup key is `(account, host, tokenHash)`; ETag updates enforce uniqueness
for that key and `(account, host, registrationId)` across active/reserved
records, including cap checks and replacement. Storage errors fail APNs closed.

The actual binding is a bounded, thread-safe process-memory map keyed by
`(accountId, hostId, tokenHash)`. Its value contains registration ID, target,
source enrollment ID/generation, host connection generation, and raw token
bytes needed by the provider. At the relay, raw token bytes are present only in
encrypted transit and this live memory map; never write them to relay storage,
logs, metrics, traces, or errors. Clear token buffers on replacement, revoke,
disconnect, and shutdown where the runtime permits.

The live binding belongs to the exact authenticated host connection
generation. A host reconnect clears that map, then the host restores active
registrations from its local `PushStore` using each token and signed durable
receipt. The relay verifies the active durable record and rebinds the token to
the new generation. `RelayClient` does not permit sends for a registration
until its restore result succeeds. This preserves delivery while the phone app
is closed without persisting raw tokens at the relay.

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

For a token change on a registration ID, authorize increments its generation,
revokes A, and creates pending B at that generation. Revoke B increments it
again. Register B may activate only if its proof generation and pending ID
still match the current state in the same CAS; otherwise return
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
base64url. Re-registering the same token hash, target, and ID is idempotent.
The same token hash and target with a new ID atomically replaces the old ID;
the old receipt becomes unusable and does not consume another slot. Reusing an
ID for a different source device or target returns `registration-id-conflict`.
A token change uses the same ID and a fresh proof from the same source device;
the relay atomically revokes the old authorization, replaces the hash, and
keeps one slot. A target change requires a fresh proof and performs the same
atomic replacement.

Caps are 16 active or reserved registrations per host and 512 per account;
replacements reserve the existing slot. Pending proofs are capped at 8 per
person device and 64 per host. Exceeding a cap returns `registration-limit` or
`authorization-limit`. Missing, foreign-account, foreign-host, revoked,
expired, un-restored, or disconnected bindings return `unbound-registration`,
without revealing another binding's state or making a provider request.

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
`registration-limit`, `registration-id-conflict`, `stale-registration`,
`connection-ended`,
`notice-expired`, `payload-too-large`, `rate-limited`, `relay-unsupported`,
and `relay-unavailable`.

## Tests-first implementation cuts

Each cut is a separate tests-first change, estimated at 180-220 changed lines
including tests and its documentation; split before 250. No cut enables
production provider credentials or delivery.

| Cut / estimate / owner | Scope and first failing tests | Depends on |
|---|---|---|
| A3b1a — Azure APNs state / ~210 / A3 | Add per-account blob schema, ETag CAS/retry, caps, generation fences, and shared rolling-window reservations. Test overlap conflicts, cap races, storage failure, and no memory fallback. | This spec accepted |
| A3b1b — enrollment control plane / ~210 / A3 | Own owner-authenticated `EnrollHost` and account-authorized `EnrollPersonDevice` record creation/revocation. Test authorization, proof of key possession, generation advance, and invalid writer refusal. | A3b1a |
| A3b1c — APNs identity gate / ~190 / A3 | Check relay-owned role/generation only in APNs handlers; preserve ordinary WebSocket admission. Test phone claiming host in hello, unenrolled identities, revoked records, and unaffected non-APNs connections. | A3b1b |
| A3b2 — proof-key trust / ~210 / A3 | Add managed-secret ES256 signer, pinned host public-key config, key rotation, and `ready` key ID. Test unknown key, invalid signature, pre-provisioned pin, overlap, and absent-key refusal. | A3b1a |
| A3c — strict frame parser / ~200 / A3 | Own APNs parser and typed DTO validation for authorize/register/unregister/revoke/send. Test duplicate/unknown properties, wrong types, malformed IDs, and boundary sizes. | A3b1c |
| A3d — phone authorization / ~220 / A3 | Add authorize, five-minute proofs, pending caps, and revoke-time cancellation. Failing-first: active A, authorize B, opt-out revokes B, delayed register B must return `stale-registration`. Test caps, replay, expiry, and enrollment revoke race. | A3b2; A3c |
| A3e — authorization receipts / ~210 / A3 | Add durable active/revoked metadata and receipts without raw tokens; implement replacement, receipt validation, and 16/512 caps. Test token rotation, wrong-source replacement, replay after revoke, and counts. | A3d |
| A3f1 — register and restore / ~210 / A3 | Own register/restore handlers, durable receipt activation, memory binding install, and restore-before-send gate. Test receipt replay, reconnect without app launch, and no send before restore. | A3e |
| A3f2 — ordering and teardown / ~210 / A3 | Own ordered control queue, connection lifecycle gate, replacement-connection fencing, unregister cleanup, and token-buffer clearing. Test register-vs-close, revoke-vs-register, and old loop after replacement. | A3f1 |
| A3g — relay send and capability / ~220 / A3 | Add fixed targets, payload bounds, silent Done, relay-clock expiry clamp, Azure limiter reservation, and phone/host capability gate. Test old-relay refusal, 24-hour clamp, quota overlap, and no provider call on invalid sends. | A3f2; A1a |
| A3h — RelayClient methods / ~210 / A3 | Own authorize/register/unregister/restore/send methods, capability parsing, and typed results. Test immediate unsupported result and restore-before-send. | A3g |
| A2a — authorization publication / ~210 / A2 owner | Own `RemoteAuthorization.PersonOperations`, `PushStore` token/ID/proof persistence, and authorize-to-subscribe publication. Test failed save, invalid proof, and no publication before persistence. Separate child; A3 does not edit #2747. | A3h |
| A2b — host replacement and restore / ~210 / A2 owner | Own receipt persistence, reconnect restore, token-change replacement, and pending/active/revoking states. Test closed-phone restore, opt-out of pending B, receipt uncertainty, and no stale token delivery. Separate child. | A2a |
| A3i — host send method / ~200 / A3 | Own `IPushSender`/`PushNotifier` APNs method, notice/sound/priority mapping, forced silent Done, and typed refusal handling. Test notice policy, disconnect, and unsupported capability. | A2b; A1a |
| A1a — `ApnsPush.cs` wire / ~190 / A1 owner | Own A1 APNs notice wire fields for sound, priority, and silent Done inputs. Test round-trip defaults, policy values, and Done never carrying sound. | This spec accepted |
| A4 — provider transport / ~210 / assigned owner | Add APNs signing/HTTP/2, effective-expiry headers, bounded retries, safe 410 invalidation, and provider readiness. Test injected transport and ensure 410 prevents restore. | A3i; A1a |

Two independent reader families and Chief acceptance of this revised spec are
required before any A3 implementation. This note does not authorize provider
provisioning, production rollout, physical-device proof, or edits to the A1/A2
branch.
