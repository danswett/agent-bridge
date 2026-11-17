# Relay-side APNs registration binding

**Revised proposal; specification only.** Based on A1/A2 head
`3e0197ddadc1ad7bcef84606d742dc2c13fdc1a4` and design PR #2437. No APNs
implementation, provider credentials, rollout, or device delivery is authorized.

## Trust boundaries and relay identity

The relay must derive device role from a relay-owned enrollment record, never
from the client's `hello.kind`. The device ID is derived from the public key
that proved possession during the relay handshake. For each `(accountId,
deviceId)`, a `RelayDeviceEnrollment` record supplies the active role and
enrollment generation. A missing, revoked, or mismatched record is refused;
changing `hello.kind` cannot change the role.

Only the owner-authorized relay provisioning path may grant the `host` role.
Ordinary relay frames, account tokens, and `hello.kind` cannot create or change
that record. The record binds the account to the public key/device ID that
proved possession. Person-device roles also come from relay-owned enrollment,
not a client declaration. The signed `DeviceRoster` is an untrusted, observe-only
account log and `MachineRoster` is presence history; neither grants a role.
If the relay cannot verify the enrollment record, it must fail closed.

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

After authenticating a host, `ready` advertises
`apnsGateway: { version: 1, maxPayloadBytes: 4096,
authorizationProofKeyId: "<trusted relay signing key>" }` only when host
enrollment, proof signing, target configuration, and the APNs boundary are
available. An older relay without this flag is unsupported: the host returns
`relay-unsupported` before queueing a frame, rather than waiting for a request
timeout.

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
the A1-bounded hexadecimal token. It computes the token hash and returns a
cryptographically random `authorizationId` bound to
account, host, person device, target, registration ID, token hash, and current
host connection generation. The grant expires after five minutes, is single
use, and carries no caller-supplied identity. Pending token bytes stay in
memory only and are cleared on use, expiry, or disconnect.

The success reply is `apns-authorize-result` with the request ID, authorization
ID, expiry, and a relay-signed authorization proof. The proof covers the
authorization ID, account, person-device ID, host ID, registration ID, target,
SHA-256 of canonical token bytes, host connection generation, and expiry; it
contains no raw token. The proof uses the relay's existing signed-message
mechanism and the key identity advertised in `ready`. A3b owns establishing
that trust; hosts reject unknown keys, unsigned proofs, and expired claims.
Refusals carry a typed outcome only.

The app passes the authorization ID, proof, and token with
`push.apns.subscribe` to the host over the existing encrypted channel. A2
checks that the peer is a person device in the same account, that its device ID
matches the authorization, and that existing own-account/person-device policy
allows the operation. It verifies the relay proof against the authenticated
`PushPeer.Device`, host ID, target, registration ID, and token digest from the
received token. A mismatch returns `registration-proof-invalid`. The host
persists the token as pending, publishes the binding, and marks it active only
after a successful relay receipt; on refusal it clears the pending record and
does not report subscription success. It publishes:

```json
{"t":"apns-register","id":22,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","authorizationId":"<one-use relay grant>"}
```

The frame contains no APNs token, account ID, or device ID. The relay accepts it
only on the current authenticated host connection, verifies and consumes the
grant, and installs the bound token. The relay returns
`apns-register-result` with the request ID and a typed outcome. `PushStore`
must expose persistence failure; the host must not publish a registration it
did not durably save. If the receipt is uncertain, the host closes the relay
connection before clearing pending state, ensuring any accepted relay binding
is also removed. A2 must add `push.apns.subscribe`, `push.apns.unsubscribe`,
and `push.apns.list` to `RemoteAuthorization.PersonOperations`; otherwise the
existing person-role gate refuses enrollment.

The A2 local store may retain a registration, but it is not relay authority.
After a host reconnect, the old one-use grant is invalid and all relay bindings
from the old connection have been removed. The host cannot silently republish
from its local token store: the person device must authorize again. APNs
delivery is unavailable after a relay/host reconnect until that happens. This
connection-scoped lifetime avoids durable relay token storage and stale
revocation tombstones; it is a deliberate availability trade-off for review.

## Binding, lifetime, revocation, and races

The relay keeps a bounded, thread-safe, process-memory registry keyed by
`(accountId, hostId, tokenHash)`. The value contains registration ID, target,
source person-device enrollment ID, host connection generation, and token
bytes needed by the provider. `tokenHash` is HMAC-SHA-256 of canonical token
bytes using a random process-local 256-bit key that is discarded at shutdown.
The raw token is never written to relay storage, logs, metrics, traces, or
errors; it exists only in memory while a binding is live. Clear token buffers
on replacement, revoke, expiry, disconnect, and shutdown where the runtime
permits.

The binding lives only for the exact authenticated host connection generation
that accepted it. The host revokes with:

```json
{"t":"apns-unregister","id":24,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod"}
```

The enrolled source person device may also send `apns-revoke` on its own
authenticated relay connection, naming the selected host and registration ID.
The relay derives its identity from that connection and only removes bindings
whose recorded source device matches. Both forms are idempotent. A stale
unregister cannot remove a newer registration. No revocation tombstone is
kept: if unregister acknowledgement is uncertain, the host closes the relay
connection, which removes every binding on it. Disconnect, replacement,
drain, or process restart also removes bindings. Account, host, or person-device
enrollment revocation immediately disables affected bindings.

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
even while its socket is still unwinding.

`registrationId` is 16 cryptographically random bytes encoded as unpadded
base64url. Re-registering the same token hash, target, and ID is idempotent.
The same token hash and target with a new ID atomically replaces the old ID;
the old ID becomes unusable and does not consume another slot. Reusing an ID
for a different token or target returns `registration-id-conflict`. A target
change requires a fresh person-device authorization and atomically replaces
the old binding.

Initial caps are 16 active registrations per host and 512 per account, counting
unique token hashes. Exceeding either returns `registration-limit`; replacement
of an existing token does not count as an additional registration. Missing,
foreign-account, foreign-host, revoked, expired, or disconnected bindings all
return `unbound-registration`, without revealing another binding's state or
making a provider request.

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
`expiration` is a Unix-seconds integer: it must be at least relay `now` and no
later than relay `now + 86,400`; an already expired value is refused as
`notice-expired`. The host must set it no later than the existing notice
expiry, which it knows before encrypting the notice. `collapseId` is at most 64
UTF-8 bytes. A4 maps priority, expiration, collapse ID, push type `alert`,
topic, and sound to APNs headers/body.

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
advertising its capability until a distributed atomic limiter and a
cross-replica binding design are reviewed and implemented.

## Privacy and observability

The relay may observe authenticated account/host identity, enrolled source
device ID, target, registration ID, token hash, payload size, timing, outcome,
and bounded rate-limit counters. Never log the raw token, ciphertext, complete
request/response body, APNs URL path, provider authorization, or user/session/
question content. Metrics use bounded outcome labels only; never use account,
host, device, registration IDs, or token hashes as labels. Errors contain only
stable refusal codes such as `host-required`, `device-not-enrolled`,
`registration-proof-invalid`, `authorization-expired`,
`authorization-key-unknown`, `unknown-target`, `unbound-registration`,
`registration-limit`, `registration-id-conflict`, `connection-ended`,
`notice-expired`, `payload-too-large`, `rate-limited`, `relay-unsupported`,
and `relay-unavailable`.

## Tests-first implementation cuts

Every code cut is sensitive, written tests-first, targets 180-220 changed lines,
and has a hard maximum of 250 including tests and docs. Split before crossing
250. No cut enables production provider credentials or device delivery.

| Cut and owner | Scope and first failing tests | Depends on |
|---|---|---|
| A3b — relay identity (A3) | Add relay-owned device-role enrollment, trusted proof-key identity, and the `ready.apnsGateway` capability. Test that hello claims cannot promote a phone, unknown/revoked hosts fail closed, role changes close the old connection, and older clients refuse a relay without the capability. | This spec accepted |
| A3c — device authorization (A3) | Add direct person-device `apns-authorize`, one-use five-minute grants and signed proofs bound to host/account/device/token hash/target/registration ID, strict frames, and secure cleanup. Test wrong account/host/device, forged IDs, mismatched token/target/digest, replay, expiry, key rejection, and no raw-token persistence. | A3b |
| A2 follow-up — enrollment publication (A2 owner, separate child) | Extend `RemoteAuthorization.PersonOperations`, persist the registration ID, pass authorization through `push.apns.subscribe`, and publish only after save. Test peer/grant identity match, save failure, and no send without relay capability. This is A2-owned work, not an edit to the existing #2747 branch by A3. | A3c |
| A3d — binding lifecycle (A3) | Add the capped host/account/token-hash registry, registration ID replacement rules, unregister, and current-generation atomicity. Test 16/512 caps, typed refusal, replacement, stale unregister, and cleanup at close/replacement. | A3c; A2 follow-up |
| A3e — relay send boundary (A3) | Add ordered send handling, fixed target mapping, strict schema, exact 4096/4097-byte serialization checks, Done-silent validation, and single-replica 20/30-per-minute limits. Test no provider call for unbound or malformed sends and register-before-send ordering. | A3d |
| A3f — host APNs sender (A3) | Add the host send method to `IPushSender`/`PushNotifier`, map existing notice policy to sound/priority/class, force Done silent, and await relay registration receipts. Test every notice class and refusal/connection loss. | A3e |
| A4 — provider transport (A3/assigned owner) | Add APNs signing and HTTP/2 transport, required headers, bounded retries, typed provider results, and safe 410 invalidation. Test with injected signer/handler only. | A3f |

Two independent reader families and Chief acceptance of this revised spec are
required before any A3 implementation. This note does not authorize provider
provisioning, production rollout, physical-device proof, or edits to the A1/A2
branch.
