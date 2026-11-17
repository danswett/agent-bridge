# Cross-transport reply deduplication (#2819)

Status: revised proposed design; the two reader families and owner or Chief must accept
the placement tradeoff before implementation. Written 2026-11-17 PT.

## Scope and current gap

This design covers one message addressed to one local session when its sender may try
both relay and Home Assistant (HA). It prevents a second typing attempt when both
routes reach the same receiving host; it cannot promise exactly-once delivery after an
uncertain handoff.

Today `LiteReceiver` keeps `(peer, request)` in process memory for 150 seconds.
`HaConnector` keeps the last 50 `rid`s in an in-memory per-session `RidLog`; its
internal `HaReply.Request` is separately derived from the card stamp. `LiteDeliver`
also collapses `Delivered` and `Unconfirmed` into `LiteDelivery.Typed = true`, so a
claim writer cannot currently record the difference safely.

## Placement decision

Use one durable, host-local claim ledger shared by that host's `LiteReceiver` and
`HaConnector`, under the host profile in a private `reply-claims` directory. It has no
relay, HA, or network dependency. Both routes terminate at the host that can type into
the target's local session; therefore a relay outage does not block the HA fallback,
and an HA-only host needs neither agent-lite nor a relay connection to use the ledger.
The store is created lazily on the first claim, not only when agent-lite starts.

This is deliberately scoped to local-session claims: every host runs the same code;
no named VM is a fleet-wide home. If a host leaves, its local sessions leave with it
and another host cannot type into them. The on-disk state is not transferred. This
does require owner acceptance of per-host durable operational state as a narrow
exception to the general no-machine-state rule; do not implement it without that
acceptance.

## Identity and content contract

The lookup key is `(target session ID, request ID)` within the receiving host profile.
The key does not include transport, sender peer, or display-only sender text. The
record stores a SHA-256 text hash (exact UTF-8 message text before route-specific
framing/escaping) and, for HA attachments, an ordered hash of attachment metadata
and bytes. These hashes are compared values, not part of the lookup key: reusing an id
with different text or attachments returns `requestReused`, never the old delivered
outcome and never a second delivery. No message text is persisted.

For cross-route attempts, the messenger allocates one UUID (`D` format) before the
first route and reuses it byte-for-byte. Lite carries it in the signed envelope and
returns it in `LiteOutcome.Request`; HA passes it as `rid`. For HA-first attempts,
`LiteSendInput` must accept that caller-provided id. Existing HA-only `rid` values
remain valid. If an HA reply has no `rid`, derive a stable local id from the existing
session and card stamp or Send-press identity (`HaReply.Request`); the reply remains
deliverable and gets HA-local deduplication, but cannot be deduplicated against a
relay attempt that has no matching id. A missing `rid` is not a storage error.

## Durable claim contract

Use a versioned append-only ledger in the private profile directory. A single in-process
gate plus an exclusive OS file lock serializes all writers. Each row has the target
session, request-id hash, text/attachment hashes, generation UUID, first-claim UTC
time, and state (`claimed`, `delivered`, or `unconfirmed`); it contains no raw id or
payload.

On first use, the store creates the private directory, takes the lock, and creates a
versioned empty ledger with `CreateNew`; it flushes that header to disk before trying
the first claim. If another process created it first, reopen and validate it under the
lock. A missing ledger is initialized, not interpreted as corrupt or as a failed
first claim. A malformed/unreadable ledger fails closed; it is never silently reset.

1. `Acquire(session, id, hashes)` prunes expired records under the lock. A new claim
   appends `claimed` with a fresh generation UUID and `Flush(true)` before returning
   `Acquired(generation)`.
2. The same key and hashes return the stored state without delivery. The same key with
   different hashes returns `requestReused`. Full or unavailable storage refuses
   before typing; it never falls back to memory.
3. `Complete(key, generation, outcome)` and `Release(key, generation)` are conditional:
   they change/remove a claim only if the stored generation still matches. A stale
   completion or release returns `stale` and cannot change a newer claim created after
   expiry. Release is allowed only when the delivery result proves no key was sent.
4. Persist `delivered` only after confirmed delivery, and `unconfirmed` whenever keys
   may have gone but delivery is not confirmed. On restart, a surviving `claimed` row
   is uncertain and blocks another attempt; it never implies delivered or not delivered.
   A failed completion after possible typing also reports unconfirmed.

Claims expire 24 hours from their first acquisition; duplicates do not extend expiry.
Keep at most 4,096 live claims per target session and never evict a live entry. At
capacity, refuse before typing. A later reuse after expiry may be delivered again, so
senders must not retry an id older than 24 hours.

## Preserve delivery outcomes

Replace `LiteDelivery.Typed` with an explicit disposition: `Delivered`, `Unconfirmed`,
or `NotSent`. `LiteDeliver` maps `ReplyResult.Delivered` and `ReplyResult.Unconfirmed`
separately; only a proven pre-key refusal is `NotSent`. An exception after handoff is
uncertain, not `NotSent`. `LiteReceiver` completes the claim with the matching state,
releases only `NotSent`, and returns an explicit unconfirmed ack when appropriate.
Neither Lite nor HA may report delivered from a claim alone: only a same-hash,
persisted `delivered` result supports that acknowledgement.

## Placement and failure behavior

- **Where it runs:** the host creates one store per profile and shares it between Lite
  and HA ingress; HA-only hosts create/use it without relay or agent-lite.
- **When a node leaves:** that host's sessions and local claim ledger leave together;
  no other host takes over its sessions.
- **Where state lives:** a private, durable file under the receiving host's profile.
- **Failure mode:** disk full, lock contention beyond the bounded wait, or corrupt
  state refuses a new claim. Relay unavailability alone does not affect HA fallback.

## Tests-first implementation cuts

Each cut adds its tests first and records RED, then implements that cut and records
GREEN. Every individual cut stays below 350 whole changed lines; estimates include
tests and implementation.

| Cut | RED cases to add first | Implementation after RED | Estimate |
|---|---|---|---:|
| 1. Ledger persistence | First use creates a valid ledger and acquires the first claim; concurrent instances acquire one generation; restart restores state; expiry is fixed; corrupt, full and unwritable ledgers fail closed; old-generation release cannot remove a new claim; text-hash mismatch is `requestReused`. | Add private-file journal, initialization, locking, bounded records, and generation-fenced operations. | 280 |
| 2. Local protocol and storage plumbing | Optional caller id survives local pipe parsing; malformed ids refuse; `LiteDelivery` distinguishes delivered/unconfirmed/not-sent; file-backed store is available without relay or Lite configuration. | Add the optional request id and result disposition, initialize the store in host composition, and share it with both routes. | 260 |
| 3. Lite integration | Same-id/same-text duplicate does not type; same-id/different-text refuses; `Unconfirmed` persists as unconfirmed; exceptions do not release; proven no-key refusal releases only the matching generation. | Integrate claim and completion into `LiteReceiver`/`LiteDeliver` with truthful acks. | 280 |
| 4. HA integration | Same claim races across HA and Lite; HA-only host and relay-down fallback work; `rid`-less card retries use the stable card-stamp id; duplicate text mismatch refuses; stale completion/release is harmless. | Integrate the same store into `HaConnector`, preserving its ack presentation and using its generated request id when `rid` is absent. | 300 |
| 5. Messenger | Relay-first timeout and HA-first fallback reuse exactly one UUID; offline and unconfirmed paths do not mint a second id; HA-only sends without `rid` remain supported. | Update the route-selecting messenger (in its owning project) and sender tests to allocate once and pass the id unchanged to Lite and `reply-session.ps1 -Rid`. | 180 |

Focused tests belong with the store tests, `LiteReceiverTests`, `ReplyTests`,
`AgentLiteSenderTests`, and the messenger's route tests. This remains a design-only
issue: no implementation or runtime tests are run here. The store location, relay/HA
delivery behavior, and protocol changes make the eventual implementation sensitive.
