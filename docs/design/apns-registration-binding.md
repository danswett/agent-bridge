# Relay-side APNs registration binding

**Revised proposal; specification only.** Based on A1/A2 head
`3e0197ddadc1ad7bcef84606d742dc2c13fdc1a4` and merged design PR #2437. No
APNs implementation, provider credentials, rollout, or device delivery is
authorized.

## Trust boundaries and relay identity

The relay must derive device role from a new relay-owned, durable
`RelayDeviceEnrollment` record, never from the client's `hello.kind`. Its
primary key is `(accountId, deviceKeyThumbprint)`; fields are `deviceId`,
`role` (`host` or `person`), monotonically increasing `generation`, status,
creation time, and revocation time. `deviceId` is derived from the public key
that proved possession during the relay handshake. A missing, revoked, or
mismatched record is refused; changing `hello.kind` cannot change the role.

The record is written only by the owner-controlled enrollment control plane.
Host enrollment requires an explicit owner-authorized `EnrollHost` control-plane
operation with an account ID and device public key, after verifying the host
deployment; it writes the key thumbprint and assigned device ID. The host
proves possession of that key during the relay handshake. No WebSocket frame,
account token, or client can create or promote the record. Person-device
enrollment requires an account-authorized device-pairing operation through the
same control plane; the phone proves possession of its device key. If that
proof or account approval is unavailable, the relay refuses the person role.
Every write and revocation increments `generation` transactionally. Relay admission and APNs
authorization read this record; the signed `DeviceRoster` remains an
untrusted, observe-only account log and `MachineRoster` remains presence
history. Neither grants a role. If the durable enrollment store is unavailable,
the relay fails closed and does not advertise APNs.

### Proof-signing key and host trust

The relay signs authorization proofs as JWS ES256 using a dedicated P-256
private key loaded at startup from the deployment's managed secret store. The
key is not in source, app settings, logs, or the database. Hosts validate the
configured relay URL's TLS certificate and receive the matching public key
through the same owner-controlled deployment configuration that supplies the
relay URL. `ready` carries only the
`authorizationProofKeyId`; it is a key selector, not a trust bootstrap.
Hosts reject proofs with an unknown key ID, an invalid signature, or an
unsupported algorithm.

For rotation, deploy the new public-key pin to hosts first, then configure the
relay to sign with the new key while accepting the old and new IDs; retire the
old pin only after all pending proofs have expired/revoked and every active
receipt has been reissued under the new key or its authorization revoked.
Successful reconnect restore returns and persists a current-key receipt. A
missing signing key or unavailable enrollment store prevents the relay from
advertising APNs.

An APNs authorization must originate at the enrolled person device, not from a
host assertion that a token belongs to a device. The relay therefore creates a
short-lived authorization only from a person device's own authenticated relay
connection, and binds it to the currently enrolled host and exact token hash.
This APNs-specific device-to-host enrollment is created only by that direct
person-device request; the relay does not trust a host-declared owner ID. The
host must also verify that the authorization's person-device ID matches
the peer on the existing encrypted A2 channel before publishing the binding.
This proves which enrolled app supplied the token; APNs does not sign tokens,
so the relay cannot independently prove Apple's issuance.

`ready` advertises `capabilities.apnsRegistration` to both phone and host
clients, with protocol version, `maxPayloadBytes: 4096`, and
`authorizationProofKeyId`. The relay advertises it only after the phone
authorize/revoke handlers, host register/restore/unregister/send handlers,
strict frame parser, durable stores, proof key, fixed target map, and provider
boundary are all ready. Before that, the field is absent. New phone and host clients check it
before sending any APNs request and return `relay-unsupported` immediately;
in particular a phone's first authorize to an older relay cannot hang or time
out.

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
generations, host, target, registration ID, and SHA-256 of canonical token
bytes. Pending
authorizations are capped at 8 per person device and 64 per host; exceeding
either returns `authorization-limit`. Pending claims and state are durable
metadata rows in the same authorization store, with expiry and no token bytes.
An exact active tuple returns its current receipt, and an exact pending tuple
returns the same pending proof; `id` is only response correlation. A changed
token or target for the same registration cancels its pending proof before
replacement. The JWS contains `iss=relayId`,
`aud=hostId`,
`sub=personDeviceId`, authorization ID, account, both enrollment generations,
target, registration ID, token digest, and five-minute expiry; it is signed
with the pinned JWS key. The authorization is single-use for activation, but
a completed registration gets a separate durable receipt.
If the hash or target differs from the active authorization
with the same account, host, source device, and registration ID, the relay
immediately revokes the old authorization and removes its binding while
reserving that same slot for the replacement. Expiry or failure leaves the
old token disabled, never live and counted beside the new token. A different
source device cannot take over a registration ID.
If the same source device authorizes the same token and target under a new
registration ID, the relay atomically revokes the old ID and reserves its slot
for the new one; the old receipt cannot restore it.

The app passes the authorization proof and token with `push.apns.subscribe` to
the host over the existing encrypted channel. A2 verifies the signature and
claims against the authenticated `PushPeer.Device`, account, host, target,
registration ID, and token digest, then applies existing own-account/person
device policy. A mismatch returns `registration-proof-invalid`. The host
persists the token and proof as pending before publishing. On success, A2
returns the durable receipt (including its `authorizationId`) to the app for
later direct revocation:

```json
{"t":"apns-register","id":22,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","token":"<APNs hex token>","authorizationProof":"<relay-signed five-minute proof>"}
```

The register frame travels only on the authenticated, TLS-protected relay
connection. The relay checks the current host role, active enrollment
generations, proof signature and claims, pending authorization state, and
token digest before activation. Under the connection lifecycle gate, one
durable-store transaction consumes the pending authorization and creates an
active `RelayApnsAuthorization` record containing account, host and its
enrollment generation, source device and its enrollment generation,
registration ID, target, token hash, and receipt generation—never the raw
token. The returned receipt is a relay-signed JWS with the same identity and
token claims plus `receiptGeneration`. It does not expire with the
five-minute pending proof; it is valid only while its durable authorization
record is active. A replay of an already-consumed proof with identical claims
and token returns its active receipt, even after the pending-proof expiry; an
expired or revoked, never-consumed proof cannot activate. `apns-register` must contain exactly one of
`authorizationProof` or `registrationReceipt`. Only after commit does the relay install the
process-memory binding; if commit fails, it installs nothing. It returns
`apns-register-result` with a
signed, durable `registrationReceipt`. The host marks its local record active
only after this receipt is durably saved. An uncertain reply closes the
connection; the host retains its pending request and retries idempotently
after reconnect.

The receipt is bound to the active durable authorization record and remains
valid until revoke or replacement. The A2 `PushStore` persists the APNs token,
registration ID, and receipt in platform-protected storage. After every host
reconnect, the host re-publishes each saved registration before enabling APNs
sends:

```json
{"t":"apns-register","id":24,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","token":"<APNs hex token>","registrationReceipt":"<relay-signed active receipt>"}
```

The relay verifies the receipt signature, active record, and token digest, then recreates the
in-memory binding for the new host connection. Thus delivery resumes without
the phone app reopening. A failed restore leaves that registration disabled
and visible as a typed failure; the host does not send until restore succeeds.
An idempotent retry returns the same receipt; a successful restore after key
rotation returns a newly signed receipt under the current key.

A token or target change uses the same registration ID and a fresh phone
authorization. The relay disables the old authorization and binding as soon as
that authorize request detects the change. On successful registration it
activates the new authorization in the reserved slot. It refuses replacement
if the proof's source device differs from the active registration owner. If
authorization expires or replacement fails, the old registration remains
revoked, the pending slot is released, and no stale token remains active.
`RemoteAuthorization.PersonOperations` must include `push.apns.subscribe`,
`push.apns.unsubscribe`, and `push.apns.list`.

## Binding, lifetime, revocation, and races

The relay adds a durable, transactional `RelayApnsAuthorization` store,
backed by a local persistent database for the single replica. It retains
pending/active/revoked authorization IDs, account, host and its enrollment
generation, source device and its enrollment generation, registration ID,
target, token hash, expiry, and receipt generation; it never stores the raw
token. Use SHA-256 over canonical
A1-validated token bytes as the stable token hash. APNs tokens are
high-entropy opaque values; the hash supports deduplication and restore without
being reversible. The store is local to the single relay replica. If it is
unavailable, registration and capability advertisement fail closed.
Enforce transactional uniqueness for active `(account, host, tokenHash)` and
for `(account, host, registrationId)` across active and reserved states; cap
checks and replacements use the same transaction.

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

The host revokes by naming the active `authorizationId` from the receipt, not
only the registration ID:

```json
{"t":"apns-unregister","id":24,"authorizationId":"<current receipt authorization ID>","registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod"}
```

The enrolled source person device may also send `apns-revoke` on its own
authenticated relay connection, naming the selected host, registration ID,
and active `authorizationId` from its subscribe result:

```json
{"t":"apns-revoke","id":25,"hostId":"<enrolled host device>","registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","authorizationId":"<current receipt authorization ID>"}
```

In one store transaction, either revoke marks the durable authorization
inactive and cancels all pending authorizations for that
account/device/host/registration ID. Under the same lifecycle gate, the relay
then removes the current memory binding before acknowledging. The relay derives
person identity from the phone connection and requires it to match the
recorded source device. It revokes only if `authorizationId` is still current;
a stale request cannot revoke a replacement registration. Repeating a revoke
for an already-revoked authorization is a successful no-op and cannot affect a
newer authorization. The host persists a revoking state and retries the same
authorization ID before restoring registrations after reconnect, so an
uncertain acknowledgement cannot resurrect a revoked binding.

Every pending proof includes the source enrollment generation. Device/host
role revocation increments that generation and atomically cancels matching
pending authorizations and active records before acknowledging revocation.
Registration activation checks the current record and generation in the same
transaction, so a proof issued before revoke cannot install afterward.

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

Use an atomic rolling 60-second limiter in the single relay process:
20 sends per host and 30 per account. Validation refusals do not consume quota.
The deployment is capped at `maxReplicas: 1`; do not add a shared limiter/store
for this cut. Before raising the replica count, APNs must fail startup or stop
advertising its capability until a replicated authorization ledger, atomic
limiter, and cross-replica binding design are reviewed and implemented.

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

Each cut is a separate tests-first change, estimated at 180-220 total changed
lines including tests and its own documentation, with a hard split-at-250
maximum. These are estimates, not permission to exceed 250; split a cut before
crossing the limit. No cut enables production provider credentials or delivery.

| Cut / estimate / owner | Scope and first failing tests | Depends on |
|---|---|---|
| A3b1 — enrollment store / ~200 / A3 | Add the durable `RelayDeviceEnrollment` schema, owner-controlled writer, and server-derived role/generation checks. Test hello spoofing, missing/revoked records, key/account mismatch, and fail-closed store errors. | This spec accepted |
| A3b2 — proof-key trust / ~210 / A3 | Add managed-secret ES256 signer, pinned host public-key config, key rotation, and `ready` key ID. Test unknown key, invalid signature, pre-provisioned pin requirement, rotation overlap, and absent-key capability refusal. | A3b1 |
| A3c — strict frame parser / ~190 / A3 | Own the APNs JSON parser and typed DTO validation for authorize/register/unregister/revoke/send. Test duplicate and unknown properties, wrong types, malformed IDs, and boundary-sized tokens/payloads. | A3b1 |
| A3d — phone authorization / ~210 / A3 | Add direct `apns-authorize`, five-minute signed proofs, 8-per-device/64-per-host pending caps, enrollment-generation checks, and revoke-time cancellation. Test unauthorized role, cap refusal, replay, expiry, and revoke racing an in-flight register. | A3b2; A3c |
| A3e — durable authorization ledger / ~210 / A3 | Add active/revoked records and durable receipts without raw tokens; implement same-ID token replacement, receipt validation, 16/512 registration caps, and idempotent revoke. Test token rotation, wrong-source replacement, receipt replay after revoke, and counts. | A3d |
| A3f — relay binding lifecycle / ~220 / A3 | Own RelayHub register/restore binding installation, ordered control queue, active-generation gate, replacement cancellation, and memory-token cleanup. Test reconnect restore without app launch, register-vs-close, replacement while old loop unwinds, and no send before restore. | A3e |
| A3g — relay send and capability gate / ~220 / A3 | Add send validation, fixed targets, 4096-byte serialization, Done-silent policy, relay-clock expiry clamp, 20/30 limiter, and a capability gate for phones and hosts. Test old-relay phone refusal, absent-handler/provider capability, 24-hour clamp, and no provider call for invalid sends. | A3f |
| A3h — RelayClient methods / ~210 / A3 | Own `AuthorizeApnsAsync`, `RegisterApnsAsync`, `UnregisterApnsAsync`, `RestoreApnsRegistrationsAsync`, `SendApnsAsync`, capability parsing, typed results, and send gating. Test missing capability returns immediately and restore completes before send. | A3g |
| A2 follow-up — host publication / ~215 / A2 owner | Own `RemoteAuthorization.PersonOperations`, PushStore token/ID/proof/receipt persistence, authorize-to-subscribe publication, reconnect restore, token-change replacement, and pending/active/revoking states. Test failed save, receipt uncertainty, closed-phone reconnect, and no stale token delivery. Separate child; not an edit to the existing #2747 branch by A3. | A3h |
| A3i — host send method / ~190 / A3 | Own the `IPushSender`/`PushNotifier` APNs method, notice-class/sound/priority mapping, forced silent Done, and handling of typed relay refusals. Test notice policy, connection loss, and unsupported capability. | A2 follow-up |
| A4 — provider transport / ~210 / assigned owner | Add APNs signing/HTTP/2 transport, headers using effective expiry, bounded retries, typed results, safe 410 invalidation of the durable authorization, and provider readiness for the capability gate. Test with injected signer/handler and verify 410 prevents reconnect restore. | A3i |

Two independent reader families and Chief acceptance of this revised spec are
required before any A3 implementation. This note does not authorize provider
provisioning, production rollout, physical-device proof, or edits to the A1/A2
branch.
