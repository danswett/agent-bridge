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

The lookup key is `(canonical local session ID, request ID)` within the receiving host
profile. Resolve the incoming target through `ISessionSource` before claiming and use
the returned `Session.Id`, not a route-specific locator. Lite's
`agent_bridge_<16hex>` target and HA's raw session ID for the same live session must
therefore produce the same key. If resolution fails, refuse before claiming or typing.
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
session and card stamp or Send-press identity (`HaReply.Request`); it remains
deliverable and deduplicated locally, but cannot deduplicate a relay attempt without
the same id. A missing `rid` is not a storage error.

## Relationship to existing docs

`docs/protocol/agent-lite.md` currently documents the separate in-memory stores,
relay-only duplicate window, cross-route duplicate risk, and fallback-on-unconfirmed
behavior. Those statements remain current until the Lite and HA integration cuts both
land. Cut 3 updates the relay-side claim, restart and acknowledgement behavior; cut 4
updates the HA fallback, shared-claim and acknowledgement behavior. Both cuts add
their protocol-doc changes and tests before implementation; this design alone does
not change the published protocol.

`docs/message-delivery.md` describes a different protected shared store for queued
payloads, cross-node ownership and leases, plus a host-local injector journal for
handoff evidence. This claim ledger is only for immediate Lite/HA cross-transport
deduplication on the host that can type into the session. It stores no payload,
outbox, queue sequence, lease or injector evidence, and does not replace either
store. `ReplyService`'s outcome cache is also distinct: its key must be
`(canonical session ID, claim generation)`, not the caller's request UUID alone.
If a future delivery-store cut assumes this direct-reply responsibility, it must
explicitly replace or atomically compose with this ledger; do not maintain two
independent authorities for one handoff.

## Durable claim contract

Use a versioned framed journal in the private profile directory, not an indefinitely
append-only file. A single in-process gate plus an exclusive OS file lock serializes
all readers and writers. Each frame has a length prefix, a versioned row, and a
SHA-256 checksum over the exact row bytes. Rows represent `claimed`, `delivered`,
`unconfirmed`, `released`, and expiry events; they contain the canonical session ID,
request-id hash, text/attachment hashes, generation UUID and first-claim UTC time,
never the raw request ID or payload. Release and expiry are tombstones in the journal;
they are physically removed only by compaction. A compacted file is a snapshot of
the current live states.

The durable initialization marker lives outside the replaceable journal directory
but inside the private host profile. On first use, take the lock, create and flush a
valid empty journal, then create and flush the marker with `CreateNew`; do not allow
the first claim until both are durable. If the journal exists but the marker does
not, validate the journal and create the marker before continuing. If the marker
exists but the journal is missing, refuse both Lite and HA delivery for this profile:
an established ledger may have been lost, so an empty replacement could duplicate a
prior reply. Initialization or marker-write failure also refuses before typing.

1. `Acquire(session, id, hashes)` prunes expired records logically under the lock. A
   new claim appends a checksummed `claimed` frame with a fresh generation UUID and
   flushes it before returning `Acquired(generation)`.
2. The same live key and hashes return the stored state without delivery. The same
   key with different hashes returns `requestReused`; a released or expired key may
   be acquired again with a fresh generation. Full or unavailable storage refuses
   before typing; it never falls back to memory.
3. `Complete(key, generation, outcome)` appends `delivered` or `unconfirmed` only if
   the active generation still matches. Repeating the same completion is
   idempotent and returns its persisted outcome. A later read-only confirmation may
   promote `unconfirmed` to `delivered`; `delivered` cannot be reversed.
   `Release(key, generation)` appends a `released` tombstone only on that same match,
   and only when the result proves no key was sent. A stale completion or release
   returns `stale`; it cannot affect a newer generation. A later acquisition after
   release gets a fresh generation.
4. A complete valid frame is required. Under the lock, an incomplete trailing frame
   at EOF (including a partial length or checksum) is truncated to the last valid
   frame and flushed; replay then continues from that boundary. A fully present frame
   with a bad checksum, malformed row, or corruption before EOF is not treated as a
   torn write: fail closed and require operator recovery. A surviving `claimed` row
   is uncertain and blocks another attempt; it never implies delivered or not
   delivered.
5. Compact when expired/released history materially exceeds live state or the journal
   reaches its configured size bound. Under the same lock, write a complete current
   snapshot to a sibling temporary file, flush and validate it, then atomically
   replace the journal on the same volume while retaining the prior valid file as a
   recovery backup until the replacement is verified. Never compact by truncating the
   live journal in place. A failed completion after possible typing reports
   `unconfirmed`.

Claims expire 24 hours from their first acquisition; duplicates do not extend expiry.
Keep at most 4,096 live claims per target session and never evict a live entry. At
capacity, refuse before typing. A later reuse after expiry may be delivered again, so
senders must not retry an id older than 24 hours.
No new HA quota is proposed: the cap is per canonical target and refuses before
typing, while a display sender label is not a safe quota identity. Any additional HA
rate limit must use an authenticated principal and be separately specified.

If the initialization marker exists but the journal is absent, or a complete frame is
corrupt, hold all reply delivery for the affected profile and preserve the marker,
journal, temporary files and backup for diagnosis. An operator stops both ingress
paths, makes a copy of that evidence, and restores a verified same-profile backup
before resuming. If no verified copy exists, keep delivery held until an operator
retires the affected local session identity and confirms the old sessions cannot
receive again; never reset to an empty ledger or infer that prior claims expired.

## Preserve delivery outcomes

Replace `LiteDelivery.Typed` with an explicit disposition: `Delivered`, `Unconfirmed`,
or `NotSent`. `LiteDeliver` maps `ReplyResult.Delivered` and `ReplyResult.Unconfirmed`
separately; only a proven pre-key refusal is `NotSent`. An exception after handoff is
uncertain, not `NotSent`. `LiteReceiver` completes the claim with the matching state,
releases only `NotSent`, and returns an explicit unconfirmed ack when appropriate.
Neither Lite nor HA may report delivered from a claim alone: only a same-hash,
persisted `delivered` result supports that acknowledgement.

For an HA `rid` duplicate, preserve the current ack shape and pin the mapping:
same-hash `delivered` returns `outcome=delivered, duplicate=true`; `claimed` or
`unconfirmed` returns `outcome=unconfirmed, duplicate=true` without typing;
`requestReused`, missing-ledger, corrupt-ledger and storage failures return
`outcome=refused` and do not type. Lite uses a delivered ack only for persisted
`delivered`; uncertain states use an explicit `unconfirmed` ack, and failures remain
refusals. For an HA reply without `rid`, use the existing transient card result; no
rid acknowledgement is fabricated.

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
| 1a. Claim ledger | Fresh initialization creates the journal and marker before the first claim; concurrent instances serialize; restart preserves state; expiry is fixed; same-key hash mismatch refuses; matching-generation release works and stale release cannot clear a newer claim; missing marker/journal combinations behave as specified. | Add the shared per-profile claim state, initialization marker, lock, bounded live-record index and generation-fenced operations. | 280 |
| 1b. Journal recovery | Truncated final frame is dropped and flushed; bad complete checksum and mid-file corruption hold; compaction survives crashes on either side of atomic replace and preserves live generations; operator recovery restores only a verified backup and never silently resets. | Add length/checksum framing, safe tail recovery, atomic snapshot compaction, backup validation and fail-closed recovery. | 300 |
| 2. Local protocol and storage plumbing | Optional caller id survives local pipe parsing; malformed ids refuse; `LiteDelivery` distinguishes delivered/unconfirmed/not-sent; a UUID delivered for session A cannot supply session B's cached result, and a new claim generation cannot reuse an old result; store starts without relay or Lite configuration. | Add the optional request id and result disposition, pass the internal claim generation into `ReplyService`, scope outcome memory by `(Session.Id, generation)`, initialize the store in host composition and share it with both routes. | 300 |
| 3. Lite integration | Same id/text duplicates do not type; mismatched text refuses; `Unconfirmed` persists as unconfirmed; exceptions do not release; Lite's `agent_bridge_<16hex>` and HA's raw id resolve to the same `Session.Id` and claim; only proven no-key refusal releases. | Integrate canonical session resolution and claim/completion into `LiteReceiver`/`LiteDeliver`; update the relay-side duplicate, restart and ack sections of `docs/protocol/agent-lite.md`. | 300 |
| 4. HA integration | HA and Lite race on the same canonical session/key; HA-only host and relay-down fallback work; no-`rid` card/Send-press retries dedupe locally; mismatched content refuses; duplicate and refusal acks match the mapping above; missing/corrupt store holds. | Integrate the shared store into `HaConnector`; update HA fallback and ack behavior in `docs/protocol/agent-lite.md` and the ownership boundary in `docs/message-delivery.md`. | 320 |
| 5. Messenger (cross-repo) | Relay-first and HA-first paths reuse exactly one UUID; offline and unconfirmed fallbacks do not mint another; HA-only sends without `rid` remain supported. | In `danswett/slipgates-fleet` (`messenger/reply-session.ps1`, `messenger/FleetTransport.ps1`, and `messenger/Test-FleetMessenger.ps1`), allocate before route selection and pass the same ID to Lite and HA. Owner: the accepted owner of fleet issue #56, assigned by the Chief before this cut starts. Order: after cuts 2, 3 and 4 are accepted and landed, before enabling cross-route fallback; no messenger change can precede the receiver contract. | 180 |

Focused tests belong with the store tests, `LiteReceiverTests`, `ReplyTests`,
`AgentLiteSenderTests`, and the messenger's route tests. This remains a design-only
issue: no implementation or runtime tests are run here. The store location, relay/HA
delivery behavior, and protocol changes make the eventual implementation sensitive.
