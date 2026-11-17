# Relay-side APNs registration binding

**Proposed contract; specification only.** This note is based on the A1/A2 head
`3e0197ddadc1ad7bcef84606d742dc2c13fdc1a4` and narrows the next step before
an APNs send path is implemented. No provider credentials, APNs transport, or
delivery behavior is implemented or authorized here.

## Security invariant

The relay may send to an APNs token only when that exact token is actively bound
to the account and host device authenticated on the sending connection. A host
frame cannot choose its account or host identity. A token registered by another
host in the same account, another account, or an unauthenticated device is not
sendable. The relay returns a typed refusal and makes no provider request when
that proof is absent.

The APNs token is a bearer routing identifier. The relay necessarily receives
it during registration and needs it in memory to form an APNs request, but the
token is never accepted from a send frame, returned to a caller, or written to
logs, metrics, traces, or durable relay storage.

## Authenticated host frames

All frames below travel on the existing TLS WebSocket after relay authentication.
Only a connection whose server-derived kind is `host` may register, revoke, or
send. The relay derives `accountId` from the validated account and `hostId` from
the authenticated connection's device ID. Neither is accepted from frame data.
The host may act only for its own registration; a phone, PWA, or host-supplied
account/host field cannot confer authority.

### Register and refresh

After an APNs subscription has passed the host's existing account/person-device
checks and has been durably saved, the host sends:

```json
{"t":"apns-register","id":12,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","token":"<APNs hex token>"}
```

`registrationId` is 16 random bytes encoded as unpadded base64url, newly
generated for each token replacement and stable for that saved registration
across host reconnects. `target` is a deployment-
configured identifier, not a bundle ID, topic, URL, APNs origin, or environment
chosen by the caller. `token` uses the A1 parser's bounded hexadecimal form.
Unknown or duplicate fields, wrong JSON types, malformed IDs/tokens, oversized
frames, and unknown targets are refused before storage. The relay acknowledges
only after the binding is installed:

```json
{"t":"apns-register-result","id":12,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","error":null}
```

The A2 publication path is required but is not present at the assigned head.
`PushNotifier` must publish after durable local persistence, publish each
existing registration after every authenticated relay reconnect, and retain a
failed publication for retry. `PushStore` must report persistence failure
instead of allowing the host to publish a registration it did not save.

### Revoke

On unsubscribe, replacement, or loss of the local grant, the host sends
`apns-unregister` with the old `registrationId` and target. Revoke is idempotent.
A stale revoke must not remove a newer replacement: the relay removes only the
matching registration ID for the authenticated account/host. The host retains
a revocation tombstone until acknowledged; after reconnecting to the same
account, it sends pending revocations before publishing active registrations.
For replacement, it may register the new ID first, then revoke the old ID.
An account change closes the old authenticated connection, which removes all
its bindings; a new account cannot send cleanup frames as the old account.
Closing or replacing a host connection removes all bindings owned by it.

### Send

The host sends the encrypted notice and an opaque registration reference; it
does not send an APNs token:

```json
{"t":"apns-send","id":13,"registrationId":"MDEyMzQ1Njc4OWFiY2RlZg","target":"prod","keyId":"opaque-id","ciphertext":"<base64 encrypted record>","expiration":1790000000,"collapseId":"<bounded value>"}
```

The relay resolves the reference only under the current connection's
`(accountId, hostId)` identity and requires the frame target to equal the
binding's stored target. A token hash in the frame is not authority.
The relay builds the complete APNs JSON body from the validated fields; the
outer alert is generic and the session/question content remains encrypted.
`expiration` is bounded by the existing notice expiry; `collapseId` is at most
64 UTF-8 bytes. Provider headers, JWT authentication, HTTP/2 behavior, and APNs
response handling belong to the later provider-transport slice.

Register, unregister, and send results use corresponding
`apns-register-result`, `apns-unregister-result`, and `apns-send-result` types,
echo the request ID, and carry either a typed error or the operation's bounded
result. Every request and result has a correlation ID. Refusals use stable codes
such as `host-required`, `invalid-frame`, `unknown-target`,
`unbound-registration`, `payload-too-large`, `rate-limited`, and
`relay-unavailable`. Missing, foreign-host, foreign-account, revoked, and
disconnected registrations all return `unbound-registration` so the result
does not reveal another binding's state. No refusal falls through to Web Push
or another provider.

## Relay binding storage and lifetime

The first implementation should use a bounded, thread-safe, process-memory
registry. The key is `(accountId, hostId, tokenHash)`; it has one active registration ID
and target. Rebinding the same token under a different target requires revoking
the old binding first. The value contains the configured target, server-
generated connection generation, and the APNs token bytes needed by the
provider. Compute
`tokenHash` as HMAC-SHA-256 over the canonical token bytes using a random
process-local 256-bit key that is discarded at shutdown; do not use a plain
token hash as an externally visible identifier.

The relay does not persist raw APNs tokens. Keep token bytes only in relay
process memory, do not include them in exception text, and clear the byte buffer
when removing or replacing a binding where the runtime permits. The registry
has strict per-host and per-account entry caps. A register that would exceed
either cap is refused without evicting an unrelated live registration. If future operation
requires durable bindings, that is a new design requiring authenticated
encryption at rest and key rotation; plaintext persistence is not an allowed
fallback.

A binding belongs to the exact authenticated connection generation that
registered it. Its lifetime is that connection: disconnect, replacement, drain,
explicit revoke, or account/host change removes its entries.
The host replays only locally saved, currently authorized registrations after
the next authenticated connection. The relay never restores tokens from disk.
Registration refresh is idempotent for the same registration ID and identity;
reusing that ID with a different token or target is refused. Reconnect creates
a new connection generation and must re-register before sending.

Registration and send operations must be serialized or made atomic per
`(accountId, hostId, registrationId)` so a concurrent revoke or replacement
cannot race a send into using a stale token. A send that has already passed
binding validation may complete; subsequent sends after revoke/disconnect
must fail closed.

## Target, payload, and rate boundaries

The relay's deployment configuration owns a finite `target` map to APNs
environment, bundle topic, and the fixed Apple sandbox or production origin.
Frame data cannot override those values or provide a URL, port, topic, provider
credential, or redirect. The gateway checks the selected target on both
registration and send.

Validate a send frame against a strict schema before lookup or provider I/O.
Reject unknown/duplicate properties, invalid identifiers, invalid base64,
malformed ciphertext, unsupported target, and missing/revoked bindings. Serialize
the complete outgoing APNs JSON once as UTF-8 and measure those exact bytes,
including JSON structure and escaping. Accept at most 4096 bytes; refuse a
larger body as `payload-too-large`. Never truncate ciphertext or silently omit
fields to fit.

Apply independent per-host and per-account send limits atomically before
provider I/O. The account limit must be shared across relay replicas; a local
per-process counter is not sufficient. Validation refusals do not consume send
quota. If the shared limiter is unavailable, refuse with `relay-unavailable`
rather than sending without the limit. The rates and burst window are
deployment policy and must be configured, bounded, and covered by tests.
The shared atomic account limiter is a prerequisite for A3d if no existing
cross-replica limiter can provide this guarantee; a per-process substitute is
not an acceptable implementation.

## Privacy and observability

The relay may observe authenticated account/host identity, target, registration
ID, token hash, payload size, timing, outcome, and rate-limit bucket. Do not log
the raw token, ciphertext, complete request/response body, APNs URL path,
provider authorization, or user/session/question content. Metrics use bounded
outcome labels only; never use account IDs, host IDs, registration IDs, or
token hashes as labels. Error text returned to the host contains only the
stable refusal code.

## Tests-first implementation cuts

Each cut is a separate, sensitive code change under 400 changed lines,
including tests and docs. Write failing focused tests first; no cut enables
production provider credentials or device delivery.

| Cut | Scope and first failing tests | Depends on |
|---|---|---|
| A3b — host publication | Extend A2 persistence results and relay client frames. Test publish-after-save, no publish on save failure, retry after reconnect, replacement/unsubscribe tombstones, account change, and token-free logs. | A1/A2 head; this contract accepted |
| A3c — relay bindings | Add strict host-only register/revoke frames and the connection-scoped `(account, host, tokenHash)` registry. Test wrong kind, forged identity fields, cross-account/host isolation, idempotency, stale revoke, connection replacement/disconnect cleanup, caps, and no token persistence. | A3b |
| A3d — send boundary | Add strict send-frame validation, fixed target mapping, binding lookup, serialized 4096-byte budget, and atomic per-host/per-account rate checks. Test unbound/revoked sends make no provider request, exact 4096/4097-byte boundaries, malformed/unknown fields, and limiter outage. | A3c |
| A4 — provider transport | Separately implement APNs signing and HTTP/2 transport, required APNs headers, bounded retries, typed provider results, and safe 410 invalidation. Test with injected signer/handler only; no real APNs credentials. | A3d |

Two independent reader families must review the full sensitive spec and the
Chief must accept it before any A3 implementation begins. Acceptance of this
note does not authorize provider provisioning, production rollout, physical-
device proof, or a change to the A1/A2 branch.
