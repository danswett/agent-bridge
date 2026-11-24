# Sealed roaming records (#759, slice S2)

The wire format of the two records that roam a person's order, pins, groups and read markers
([design](../design/session-order-roaming.md)): what is sealed, how, and what the plaintext holds.
**Spec and vectors only.** No client, host or relay code reads it yet. It does not turn sync on and
it adds no route. The relay stores ciphertext; **hosts never hold the account key**. Out of scope:
creating, wrapping and removing the account key (S4, waits for the signed roster to be enforced,
[device-approval.md](../design/device-approval.md)), the relay route, quotas and admission (S3), host
read markers (S5), and the client's move from its local arrays to registers (sync integration).

## 1. Slots

A record lives at the relay under the name `slot = records/<account>/<kind>/<id>`: `account` is the
authenticated account id (1 to 20 ASCII digits), `kind` is `order` or `marker`, and `id` is
`[a-z0-9-]{1,32}`. Your order is `order/your-order` (later named orders take a 32-hex id, at most 8);
read markers are `marker/markers`. The relay wraps the body in its own version header for
compare-and-set; that header is the relay's and outside this spec. **The sealed body is at most
65,536 bytes**, the design's 64 KiB per sealed object.

## 2. The sealed object

| Offset | Bytes | Field |
|---|---|---|
| 0 | 4 | Magic, ASCII `ABS1` |
| 4 | 1 | Kind: 1 `order`, 2 `marker`; must match the slot's `kind` |
| 5 | 1 | Schema version, 1 |
| 6 | 2 | Key generation, big-endian: which account key sealed it |
| 8 | 12 | Nonce, fresh random per seal |
| 20 | n | AES-256-GCM ciphertext of the framed plaintext, then the 16-byte tag |

* **Key:** `HKDF-SHA256(ikm = account key for that generation (32 bytes), salt = "agent-bridge roaming v1", info = slot)`,
  32 bytes. One key per slot, so a record moved to another slot or account cannot open.
* **Associated data:** bytes 0 to 7, so the kind, schema and generation cannot be changed.
* **Framed plaintext:** a 4-byte big-endian length, the canonical JSON (UTF-8), then zeros up to a
  bucket of 1,024, 2,048, 4,096, 8,192, 16,384, 32,768 or **65,500** bytes. The relay sees a bucket,
  not the session count. The JSON is at most 65,496 bytes.
* **Opening checks, in order, and the code each failure gets:** over 65,536 bytes `too-large`; short or
  wrong magic `bad-magic`; schema byte above 1 `unsupported-version`; schema 0 or kind not the slot's
  `wrong-kind`; no key held for the generation `locked`; ciphertext not a bucket plus tag `bad-length`;
  tag fails (wrong key, slot, account or any changed byte) `bad-seal`; length prefix past the body
  `bad-length`; non-zero padding `bad-padding`; bad UTF-8 or JSON or schema `bad-schema`; a body
  whose `v` is above 1 `unsupported-version`.
* **A device never writes over a record it cannot open** (`locked`, `unsupported-version`, `bad-seal`):
  it keeps its local copy, shows "Not fully synced" (§6 of the design), or "Waiting for another of your
  devices" for `locked`. A newer schema is read-only to an older client, so it cannot erase new fields.

## 3. Order record, plaintext v1

Canonical JSON: members sorted by key, no whitespace, only `"`, `\` and U+0000 to U+001F escaped
(`\b \t \n \f \r`, else `\u00xx` lowercase), everything else literal UTF-8, no lone surrogates. Writers
emit it so the vectors are byte-exact; readers accept any valid JSON of this shape.
`{"v":1, "d":[tag...], "g":[[id, counter, dev, name?]...], "r":{rowKey:{"p":[...],"m":[...]}}}`

| Part | Rule |
|---|---|
| `d` | Device tags, at most 255, each 16 bytes as 32 lowercase hex. A register names its tag by index `dev`, so 32 characters are not repeated per register. A writer drops tags no register uses. |
| `g` | Groups, sorted ascending by `id` (`[0-9a-z-]{1,36}`, a UUID today), no repeats. `[id, counter, dev, name]` is live; `[id, counter, dev]` is a tombstone. A name is 1 to 40 characters, already trimmed. At most 32 live. |
| `r` | One entry per row key, with `p` (position) and/or `m` (membership). A row key is 1 to 200 characters of printable ASCII or one newline: today a host id, a newline and a session id. At most 500 live positions. |
| `p` | `[counter, dev, key, pinned]` live, `[counter, dev]` tombstone. `key` is the fractional key, 1 to 32 of `0-9A-Za-z`, compared bytewise; `pinned` is 0 or 1. |
| `m` | `[counter, dev, group]` live, `[counter, dev]` tombstone. `group` is the index of an entry in `g`, or `null` for Sessions. A writer whose group has no record (a purged tombstone) writes `null` and keeps the counter. |

A `counter` is a whole number from 0 to 2^53-1. These decode to the merge's own shape (`pwa/js/order-merge.js`,
`tests/fixtures/order-merge.json`): `rows`, `membership` and `groups` maps of `(value, counter, deviceTag)` or tombstone.
Sealing changes no merge rule (markers keep `max(turn)`): higher counter wins, then the higher tag as bytes, relay time never. A device
**reads, merges with its local copy, raises its counter above every counter it has seen, then writes** against the
relay's version. A stale or replayed older record merges as no change. A deleted group's sessions show in Sessions.
Tombstones older than 30 days go first when a record is full (design §4). Plaintext holds ids, registers and
person-entered group names only, never titles, paths, prompts or history.

## 4. Marker record, plaintext v1

`{"m":{sessionKey:turn},"v":1}`: a session key as for a row key, a turn from 0 to 2^53-1, at most **800**
entries. Merge is `max(turn)` per session, and a marker for a session with no row is kept.

## 5. Measured sizes

The design budgets the order at "about 50 KiB" and the markers at "about 60 KiB at 1,500 sessions", without
measuring. The vectors measure real key lengths (16-hex host id, newline, 36-character session id):

* **Order, 500 rows each in a group with a UUID id, 32 groups of 40 characters:**
  55,347 JSON bytes, inside the 65,496 limit. Without the group table (a UUID repeated per row) it was
  73,507 bytes and did not fit, which is why `m` holds an index.
* **Markers:** 800 entries with 7-digit turns are 52,013 bytes. **1,500 sessions would not fit in one
  record** (about 97,500 bytes). S2 caps one record at 800; reaching 1,500 needs a second marker id or
  shorter keys, which the design must choose (flagged in the PR).

## 6. Vectors

[`vectors/roaming-records.json`](vectors/README.md): one fixed key, generation and nonce; an order and a
marker record with canonical JSON and sealed bytes; two group merges (rename against delete, each winner)
sealed and opened; 23 refusals with their codes; and the size measurements above. The PWA test
`pwa/test/unit/roaming-records.test.mjs` is the reference oracle and regenerates the file with
`UPDATE_PROTOCOL_VECTORS=1`. A C# replay belongs with the Windows consumer, not this slice.
