# Trust-chain slice contract (hostSign, capability evidence, reader core)

Status: Engineering source of truth for builders of the #1776 chain slices. It adds no
behaviour. Where it repeats a rule, the owning text wins: `docs/protocol/devices.md`
(transition and capability sections), `docs/design/fleet-session-registry.md` (freshness,
removal, cache), `docs/protocol/agent-principals.md` section 8.10. Production stays OFF:
no flag, setting or caller boolean enables v2, and `acceptHostSign` stays `false`.

## Who owns what

| Slice | Owns | Does not own |
|---|---|---|
| #2371 C1 | Host reader verification: host signature, key from the roster add, schema, 60 s, in-memory `seq`/epoch order | Subscription, removal, cache, wiring |
| #2462 / #2380 C2 | Reader core: removal, cache purge, 24 h cache, subscription handling | Signature checks (C1), wiring |
| #2386 | Wiring: forward the signer in `FleetPublication`, compose the gated reader in `Program` | The verifier itself |
| #2381 part A | Storage only: snapshot v2 on disk, consumed-record store, 32-record bound | Challenge issue, verification, aggregate, positive transition |
| #2410 part B | In-memory outstanding challenge, nonce issue, single-use consumption through the part A store, verification-wait budget | Storage layout, positive transition |
| #2288 2b-2 | Positive transition (after A, B, and purpose-key enrollment) | Everything above |
| #2385 / #2378 | Design only | Code |

Order: #2371, then #2462 and #2386; #2381 part A, then #2410, then #2288. A builder whose
predecessor is not on main builds against the interface below, carries only the inert
signature, counts those lines, and says so in its packet. It never duplicates a predecessor.
A slice that cannot start because a predecessor is absent waits; it does not fold the
predecessor into its own 400 lines.

## C1 interface (#2371)

```text
RelayFleetRoster(Func<RosterVerifier?>? verifiedRoster = null, TimeProvider? time = null)
IReadOnlyList<FleetMachine> Read(IEnumerable<JsonElement> frames)
```

`Read` returns only records that verify against the pinned roster: signature by `hostSign`
of the machine's active add, schema, `at` within 60 s of the reader's clock, and order
(`seq` strictly greater within an epoch; a new epoch only with a later `at`). With no roster
or no key for the machine, the record is unverified and never `found`. Nothing is persisted.

## C2 interface (#2462 / #2380), internal to `AgentBridge.Host.Fleet`

```text
FleetRegistryCache(TimeProvider time)
void   Accept(VerifiedRecord record)            // only records C1 verified
void   Remove(string machine)                    // registry-changed {removed:true}
void   Reconcile(IReadOnlySet<string> listed)    // a registry-get: machines in `registry` plus `known`
void   Purge()                                   // drops entries older than 24 h
IReadOnlyList<CachedRecord> Served()             // each with Stale and Online
```

Acceptance cases (tests first, fake clock):

1. A verified live record is served `stale: false`; a record older than 60 s is served `stale: true, online: false` and never live to `fleet.find`.
2. `Remove(m)` drops the entry at once and clears history for `m`: the next valid record of any epoch with any higher `seq` is accepted after `removed`.
3. Without `removed`, a reconnecting machine keeps its epoch and needs a higher `seq`; an old epoch is refused after a newer one was seen; a restart (new epoch, `seq` 1, later `at`) is accepted.
4. `Reconcile(listed)` drops every cached machine not in `listed`; a machine in `known` only is kept as `online: false` with no sessions.
5. `Purge()` keeps an entry at 24 h minus one tick and deletes it at 24 h plus one tick; a stale entry is never returned as live at any age.
6. A lower `seq` in the same epoch never replaces a newer record and cannot resurrect a removed machine.
7. An unsigned or unverified record is never accepted, cached or `verified`.
8. A frame for a machine of another account is not applied.
9. No registry frame is applied before the reader opts in with `registry-get` or `registry-subscribe`.

Not in C2: signature checks, PWA and app readers, ListReceived (#2374), live relay deploy.

## Snapshot v2 storage (#2381 part A)

File: the existing `roster-transition.json`, owner-only, non-link, at most 256 KiB, written
under the existing file lock by atomic private temporary file plus rename. Strict I-JSON,
duplicate and unknown members refused, depth 16.

Version 2 members, in this order:

```text
v (2), account, generation, genesis, heads, acceptHostSign (must be false), highWater,
floor, highestIssuedAt, requests, capHighestIssuedAt, consumed, evidence
```

* The first ten members keep their v1 meaning. `requests` stays the person-command replay set (at most 512) and is never shared with `consumed`.
* `capHighestIssuedAt`: RFC 3339 UTC nanosecond string or `null`; ratchets only; the highest `issuedAt` of any consumed record.
* `consumed`: at most 32 records, each exactly `request` (lowercase UUID), `nonce` (base64url, 32 bytes), `consent` (base64url, 32 bytes), `issuedAt`, `expiresAt` (RFC 3339 UTC). Unique by `request` and by `nonce`.
* `evidence`: `null`, or exactly `collection` (base64url of the exact collection bytes, at most 132,096 decoded bytes), `frontier` and `verifiers` (base64url, 32 bytes each), `expiresAt`. Stored evidence is evidence only, never an outstanding challenge or a freshness attestation.
* Larger than 256 KiB is `transition-store`.

Counting and pruning for the 32 records: a record is live until `highWater` is strictly later
than `expiresAt + 120 s` (equality does not prune). Pruning runs under the same lock and the
same atomic write as an insert. With 32 live records an insert is refused `consumed-full`; a
live record is never evicted. A duplicate `request` or `nonce` is refused `replay` and
consumes nothing. The marks (`highWater`, `floor`, `capHighestIssuedAt`) only ratchet and are
written with every record; a clock reset raises `floor` to at least `capHighestIssuedAt` and
never clears records.

Migration and downgrade: a v2 reader loads a v1 file as v2 with empty `consumed`, null
`capHighestIssuedAt` and null `evidence`, and writes v2 only on its next successful commit. A
v1 binary reading v2 reports `pin-unreadable` and repairs nothing. No writer overwrites a
newer schema with an older one. Identity mismatch stays `pin-mismatch`, damage stays
`pin-unreadable`, a failed write stays `transition-store`; none is repaired and none affects
ordinary v1 roster verification. The original pin is never written.

Internal API to part B (on `HostRosterReader`; nothing public, no flag):

```text
string? ConsumeCapability(CapConsumed record, Instant now)   // null on success, else a fixed code
string? StoreEvidence(CapEvidence evidence)
CapEvidence? ReadEvidence()
```

`ConsumeCapability` takes the trusted `now` (`max(wall clock, highWater)`) from the caller. It
enforces uniqueness, the 32 bound, pruning and the marks, and nothing else: it verifies no
signature and issues no challenge. Part B keeps the outstanding challenge only in memory, so a
restart never resurrects a nonce, and calls `ConsumeCapability` once per collection.

Part A tests first: v1 file loads as v2; v2 round trip; unknown or duplicate member; v1 binary
refuses v2; 32 live records accepted and the 33rd `consumed-full`; prune kept at
`expiresAt + 120 s`, pruned one tick later; duplicate request and duplicate nonce `replay`;
marks never regress; reset raises the floor and clears nothing; oversize `transition-store`;
failed write leaves the old file; `acceptHostSign: true` refused.

## Part B (#2410) and positive transition (#2288)

Part B builds on part A: begin, commit and reset of verifier-support state, the
verification-wait budget, and single-use consumption. It may not widen the snapshot schema and
waits for part A on main.

#2288 additionally needs purpose-key enrollment: `VerifierSupport.cs` currently maps every
person to a null support key, so no person can supply `capSign` evidence. Until the
descriptor extension and enrollment slice (#2385 design, then its code slice) lands, 2b-2
has no implementable cut and stays blocked; it must not use a caller flag, a generic `sign`
key or a fixture-only authority.
