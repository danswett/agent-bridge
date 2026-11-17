# Relay-side APNs registration binding

**Revised proposal; specification only.** Based on A1/A2 head
`3e0197ddadc1ad7bcef84606d742dc2c13fdc1a4` and merged design PR #2437. No
APNs implementation, provider credentials, rollout, or device delivery is
authorized.

## Trust boundaries and relay identity

Do not change general relay admission: existing phone and host connections
remain governed by current behavior. `RelayDeviceEnrollment` is checked only by
APNs handlers; an unenrolled device can still use existing non-APNs operations.
Enrollment records keyed by `(accountId, deviceKeyThumbprint)` retain the
device public key, ID, role, generation, and status. Each host/phone creates a
nonexportable P-256 key; the relay challenge binds account, key thumbprint, and
role, and the device signs it. `hello.kind` and the shared GitHub account token
never identify or enroll a device.

`EnrollHost` requires owner approval through the deployment control plane plus
host proof-of-possession. `EnrollPersonDevice` requires a user-approved one-use pairing grant signed by
an active enrolled host plus phone proof-of-possession. These are the
only enrollment writers; WebSocket frames and account tokens cannot enroll or
promote devices. Revocation increments generation. `DeviceRoster` is an
untrusted, observe-only account log, not role authority.

`apns-enroll-challenge` returns a short-lived nonce bound to account, role, and
device-key thumbprint; `apns-enroll` carries the key, nonce signature, and
person pairing grant when applicable. Relay verifies the key proof and grant
against the enrolled host key; the shared GitHub token is not device proof.

### Proof-signing key and host trust

The relay signs proofs as JWS ES256 with a dedicated P-256 private key from
managed secret storage. Hosts validate relay TLS and pin the public key in owner-controlled deployment
configuration alongside the relay URL. `ready` advertises only the key ID.
Reject unknown keys, invalid signatures, and unsupported algorithms. Rotate by
distributing the new pin first, accepting both IDs during signing rollover,
then retaining the old verifier until receipts are reissued or revoked. Missing
key or enrollment control plane disables APNs.

## Azure state and rollout overlap

Require `Backend=azure` and a dedicated APNs Blob container using the
`AzureRosterStore` ETag pattern in `Roster.cs`. One opaque account-keyed blob
holds enrollment, authorization, generation, connection-fence, and limiter
state. Missing config/storage disables APNs readiness, not general relay
service; APNs never uses memory or raw account IDs in blob names.
Use `If-Match`/`If-None-Match`, retry conflicts up to five times, then fail
closed with `relay-busy`; unknown schemas and store errors fail closed too.

Cap each account at 64 enrolled devices, 512 active/reserved/recently revoked
registration records, and 64 pending grants (also enforce per-device/host
limits). Prune revoked records only after all five-minute proofs expire.
Preflight serialized writes at 240 KiB, below `AzureRosterStore`'s 256 KiB
empty-body boundary; refuse enrollment or APNs mutations at either cap with
`state-capacity`. Treat an oversized-read empty body as corruption, never as an
empty account; do not overwrite it. Never evict active state or write an
oversized blob.

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

`ready` advertises `capabilities.apnsRegistration` to phones and hosts only
when enroll/authorize/revoke, register/restore/unregister/send handlers, strict parser,
Azure state, proof key, target map, and provider boundary are ready. New clients
check the flag before APNs requests and return `relay-unsupported` immediately.
Roll out store/control plane first, then host/phone clients with device-key
enrollment. Existing devices remain APNs-disabled until explicitly approved and
enrolled; ordinary connections and non-APNs operations remain unchanged.

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

Each host connect increments a shared per-host `hostConnectionFence` by ETag
CAS; the accepted generation is attached to that connection. A bounded,
thread-safe process-memory map keyed by `(accountId, hostId, tokenHash)` holds
authorization ID, registration generation, target, source/host generations,
and provider token bytes. At the relay, raw tokens exist only in transit and
this map; never persist or log them. Clear buffers on replacement, revoke,
disconnect, and shutdown where supported.

On reconnect, the host restores active registrations from `PushStore` using
each token and signed receipt; relay verifies the active record and binds to
the new fence. `RelayClient` blocks sends until restore succeeds.

Every send performs one ETag-guarded read/write that checks the stored active
authorization ID and registration generation, enrollment generations, and
shared host fence against the live binding, then reserves the limiter. Only a
committed reservation reaches provider I/O. If revoke commits first, every
relay copy refuses the stale binding; if a send reservation wins first, that
already-accepted in-flight send may complete, but no later reservation can.

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

Active/reserved registrations cap at 16 per host and 512 per account;
replacements reserve a slot. Exceeding a cap returns `registration-limit`.
Missing, foreign, revoked, expired, un-restored, or disconnected bindings
return `unbound-registration` without revealing state or calling the provider.

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
`registration-limit`, `registration-id-conflict`, `state-capacity`, `stale-registration`,
`connection-ended`,
`notice-expired`, `payload-too-large`, `rate-limited`, `relay-unsupported`,
and `relay-unavailable`.

## Tests-first implementation cuts

Each cut is a tests-first change, estimated at 180-220 changed lines including
tests/docs; split before 250. A4/A5 ownership and order follow merged #2437:
A4 is provider transport; parent A5 owns host sending. No cut enables delivery.

| Cut / estimate / owner | Scope and first failing tests | Depends on |
|---|---|---|
| A3b1a — APNs storage infra / ~190 / A3 | Own Bicep container, relay managed-identity Blob Data Contributor scoped to it, and required Azure config. Test missing backend/role fails APNs readiness, never memory. | This spec accepted |
| A3b1b — Azure state store / ~210 / A3 | Implement per-account ETag adapter, five retries, record limits, 240-KiB preflight, generation/fence state. Test conflicts, size refusal, and oversized read fails closed. | A3b1a |
| A3b1c — enrollment control plane / ~210 / A3 | Own owner-approved host enrollment and host-signed phone pairing grants with device PoP; persist/revoke records. Test wrong key, replayed nonce, and invalid grant. | A3b1b |
| A3b1d — APNs identity gate / ~190 / A3 | Gate APNs handlers on relay-owned roles/generations; preserve ordinary admission. Test hello spoof, unenrolled devices, and non-APNs access. | A3b1c |
| A3b2 — proof-key trust / ~210 / A3 | Add managed-secret ES256 signer, pinned host key, rotation, and ready key ID. Test unknown key, invalid signature, pinning, and rollover. | A3b1b |
| A3c — relay frame parser / ~200 / A3 | Own strict APNs DTO parser. Test duplicate/unknown properties, types, malformed IDs, and bounds. | A3b1d |
| A3d1 — phone authorization / ~200 / A3 | Issue five-minute signed proofs and enforce pending caps. Test enrollment, cap, replay, expiry, and wrong host. | A3b2; A3c |
| A3d2 — revoke and generation race / ~210 / A3 | Cancel pending grants and fence activation. Failing-first: active A, authorize B, opt-out revokes B, delayed register B returns `stale-registration`. | A3d1 |
| A3e — authorization receipts / ~210 / A3 | Persist active/revoked metadata and receipts without raw tokens; replace tokens and enforce 16/512 caps. Test replay after revoke and replacement counts. | A3d2 |
| A3f1 — register and restore / ~210 / A3 | Own relay register/restore, durable receipt activation, and restore-before-send. Test reconnect without app launch and receipt replay. | A3e |
| A3f2 — ordered control queue / ~190 / A3 | Serialize register/revoke/send/unregister per connection. Test received order and register-versus-close. | A3f1 |
| A3f3 — shared connection fence / ~210 / A3 | Own ETag host-fence increments, replacement teardown, and binding cleanup across relay processes. Test old connection loses fence after cross-process reconnect. | A3f2; A3b1b |
| A3g1 — shared send authorization / ~220 / A3 | Check stored authorization/generation and host fence in same ETag CAS as limiter reservation. Two-process test: revoke on A, send on B refuses; test both CAS orders. | A3f3; A3b1b |
| A3g2 — send policy and capability / ~210 / A3 | Own targets, payload bounds, silent Done, expiry clamp, and capability gate. Test old-relay refusal, invalid sends, and 24-hour clamp. | A3g1; A1a |
| A3h — RelayClient methods / ~210 / A3 | Own enrollment challenge/enroll plus authorize/register/restore/send methods and typed capability results. Test unsupported relay and restore-before-send. | A3g2; A3b1c |
| A2c — enrollment client rollout / ~210 / A2 owner | Ship host/phone PoP enrollment calls for existing installs; host approval/pairing only, no token-based promotion. Test upgrade enrollment and unchanged non-APNs sessions. | A3h |
| A2a — authorization publication / ~210 / A2 owner | Own `RemoteAuthorization.PersonOperations`, `PushStore` token/ID/proof persistence, and subscribe publication. Test persistence before publish. Separate child; A3 does not edit #2747. | A2c |
| A2b — host replacement and restore / ~210 / A2 owner | Own receipt persistence, reconnect restore, token replacement, and pending/active/revoking states. Test closed-phone restore and opt-out of pending B. | A2a |
| A1a — `ApnsPush.cs` wire/parser / ~220 / A1 owner | Own strict parser fields for registration ID, proof/receipt, sound, and priority. Test duplicate fields, round-trip, and Done silence. | This spec accepted |
| A4 — provider transport / ~210 / parent #2437 owner | Add APNs signing/HTTP2, effective-expiry headers, retries, 410 invalidation, and readiness. Test injected transport and invalidation. | A3g2; A1a |
| A5 — host send method / ~210 / parent #2437 owner | Parent cut owns `IPushSender`/`PushNotifier` APNs send, policy mapping, and typed refusal handling; no A3 duplicate. Test policy, disconnect, and unsupported capability. | A2b; A3h; A4; A1a |

Two independent reader families and Chief acceptance of this revised spec are
required before any A3 implementation. This note does not authorize provider
provisioning, production rollout, physical-device proof, or edits to the A1/A2
branch.
