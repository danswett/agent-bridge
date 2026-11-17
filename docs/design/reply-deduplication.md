# Cross-transport reply deduplication (#2819)

Status: proposed design; two independent reader families and owner or Chief acceptance
are required before implementation.
Written 2026-11-17 PT.

## Scope and current gap

This design covers one message addressed to one session when the sender may try both
the relay and Home Assistant (HA) routes. It does not persist message text or make
delivery exactly-once: after an uncertain handoff, the safe result is unconfirmed,
not a retry.

Today the routes do not share an idempotency record:

- `LiteReceiver` remembers `(peer, request)` in process memory for 150 seconds.
- `HaConnector` keeps the last 50 `rid`s per session in its own in-memory `RidLog`.
  Its `HaReply.Request` is separately derived from the HA card stamp.
- `AgentLiteSender` creates a UUID and returns it in `LiteOutcome.Request`.
  `reply-session.ps1 -Rid` accepts that UUID, but its default HA-only id is `rs-...`.
  `LiteSendInput` cannot currently accept an id chosen before the route.

Consequently, a relay timeout followed by an HA fallback can type the same message
twice, and neither route's current request id alone identifies the same attempt across
both transports.

## Decision

Put the authoritative claim ledger in the Slipgates relay service's durable blob store,
not in the receiving machine, Home Assistant, or the relay message-delivery queue
proposed in `docs/message-delivery.md` (that design is not accepted and is out of
scope here). Use a dedicated `reply-claims` container in the existing storage account,
with a two-day lifecycle rule and relay managed-identity access. The relay remains the
only writer; hosts use a relay claim operation authenticated as the receiving host.

Each host uses the same claim operation before either route can type. The key is the
receiving account, receiving machine, target session ID, and the exact request ID;
transport, sending peer, and display-only `sender` are not part of the key. The relay
derives an opaque storage name from those values and stores only a request-key hash,
first-claim time, and state/outcome. It stores no message text or sender label.

For cross-route attempts, allocate one canonical UUID (`D` format) before trying
either route and pass it unchanged: `agent.lite.send` carries it in its signed
envelope; an HA fallback supplies that same value as `rid`. A relay-generated
`LiteOutcome.Request` can be reused as HA's `-Rid`. For HA-first attempts,
`LiteSendInput` must accept an optional caller-provided UUID. HA-only legacy `rid`
values remain valid, but only an identical id is shared across routes.

## Claim contract

The relay keeps one versioned ledger per receiving machine and target session in the
`reply-claims` container. Conditional blob writes use ETags, so concurrent requests
and relay replicas cannot both acquire the same claim:

1. The receiving host asks the relay to claim `(target session, request ID)`. The
   relay validates that the caller is the target machine, then reads the ledger,
   prunes expired entries and checks the key.
2. For a new key, the relay writes `claimed` with the original UTC timestamp using
   compare-and-swap. It returns `acquired` only after that write succeeds. The host
   does not invoke delivery before receiving `acquired`.
3. If an ETag changed, the relay rereads and retries a bounded number of times.
   Finding the key returns its stored state; exhausting retries, missing durable
   storage, or a store error returns unavailable. No local or in-memory fallback is
   permitted.
4. After delivery, the host records `delivered` or `unconfirmed`. A duplicate may be
   reported as delivered only when that durable terminal state says `delivered`.
   Existing `claimed` or `unconfirmed` state returns unconfirmed and never authorizes
   another delivery.
5. The host may delete a claim only when delivery positively proves that no key was
   sent. If that delete cannot be persisted, it keeps the claim and reports
   unconfirmed. An exception, timeout, cancellation after handoff, or lost completion
   acknowledgement is not proof of non-delivery.

Claims expire 24 hours after their first acquisition; duplicate attempts do not extend
that deadline. After expiry, reuse may deliver again, so callers must not retry an id
older than 24 hours. Keep at most 4,096 live claims per target session; never evict a
live claim to make room. A full ledger refuses before typing. The 24-hour logical
expiry plus the two-day blob lifecycle bounds both replay protection and stored data.

If a host or relay restarts after `claimed` was persisted, the next request sees that
claim as uncertain until expiry. It must not infer that delivery did or did not happen.
If the relay store is unavailable before the claim, both relay and HA delivery fail
closed before typing. If it becomes unavailable after typing may have begun, the
sender receives unconfirmed; the existing claim prevents a second route from typing.
The existing in-memory replay tables may remain as caches for acknowledgements, but
they never authorize a delivery or stand in for the durable claim.

## Placement and failure behavior

- **Where it runs:** the claim API and ETag arbitration run in every relay replica;
  the receiving host calls it for both ingress paths.
- **When a node leaves:** relay replicas are interchangeable because the blob store is
  authoritative. A host disconnect stops local delivery; its session is not moved to
  another machine, and no other machine may claim for it.
- **Where state lives:** only in the protected relay `reply-claims` blob container.
  No durable claim state is held on a machine or in HA.
- **Failure mode:** absent, unreadable, corrupt, full, or unavailable durable state
  refuses a new claim. It never means "not seen" and never permits a local fallback.

This introduces a deliberate availability tradeoff: HA replies that participate in
cross-transport deduplication also require the relay claim service. That dependency
must be accepted before code. The claim ledger is distinct from any future protected
store for queued message payloads.

## Tests-first implementation cuts

Each cut starts with its listed tests, records RED, then implements only that cut and
records GREEN. Each cut must stay under 350 whole changed lines.

| Cut | RED cases to add first | Implementation after RED | Estimated changed lines |
|---|---|---|---:|
| 1. Relay claim ledger | Two independent clients race for one key and only one acquires; different sessions do not collide; restart preserves `claimed` and terminal states; expiry is measured from the first claim; full, corrupt, and unavailable storage fail closed; ETag conflicts retry without losing a claim. | Add the bounded versioned ledger and conditional claim/complete/release operations over the durable blob backend. | 250 |
| 2. Host route integration | A Lite and HA request with the same target/id race and only one reaches the replier; durable `delivered` duplicates are acknowledged without typing; pending/unconfirmed duplicates never imply delivery; proven no-key refusals release; claim and completion outages do not type or authorize retry. | Inject one claim client into `LiteReceiver` and `HaConnector`; make the durable result authoritative and keep existing HA acknowledgements truthful. | 300 |
| 3. Route-neutral IDs | Caller-supplied Lite UUID is preserved in the signed envelope and result; relay-generated ID reused as HA `-Rid` is unchanged; HA-only legacy IDs still work. | Add an optional request ID to `LiteSendInput` and document the sender rule to reuse the first route's id on fallback. | 180 |

Focused tests belong with the relay claim-store tests, `LiteReceiverTests`,
`ReplyTests`, and `AgentLiteSenderTests`. No implementation or runtime tests are part
of this design-only issue; those RED/GREEN results are required for the later code
cuts. The claim protocol, store permissions/lifecycle, and relay/HA behavior make this
a sensitive change.
