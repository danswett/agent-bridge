# Sealed order and marker records (#759, slice S2)

Status: **proposal**, for review before any code. What the person sees, and what roams, is
[session-order-roaming.md](../design/session-order-roaming.md) §§2-5 and §9; this file is the byte
level. The relay's store for these records is slice S3 (#3044), the epoch key and its wrap are K1
([devices.md](devices.md)), and sealing and opening them in code is K2. **This slice holds no relay
code and no key code.** The worked examples are [vectors/order-records.json](vectors/order-records.json).

The relay sees ciphertext, a size, a version and a time, nothing else. Hosts never hold the key.

## 1. Records

| Kind | Id | Holds | Written |
|---|---|---|---|
| `order` | `yours` (Your order); a named order is `o-` and 16 lowercase hex characters (later, at most 8) | the order record (§3) | rarely, coalesced |
| `marker` | `all` | the marker record (§4) | in batches |

A relay name is `records/<account>/<kind>/<id>`; the id is `[a-z0-9-]`, at most 32 characters. This
device's order, the active order, a group's collapse and the active item never leave the device: no
record. Each synced order is its own object, so Your order never shares its 64 KiB with another.

## 2. The sealed object

The body the relay stores for one record, **at most 65,536 bytes in all** (the relay checks the
size of what it is sent, never the plaintext). All integers are big-endian.

| Bytes | Field |
|---|---|
| 0-3 | `ABSR` |
| 4 | format, `0x01`; a reader refuses any other |
| 5 | `0x00`; a reader refuses any other |
| 6-21 | epoch id, 16 bytes (K1) |
| 22-37 | writer: the writing device's id, 16 bytes (the 32 hex characters of [roster.md](roster.md)) |
| 38-45 | counter: the writer's seal counter for this record, 8 bytes; never goes backward for one writer |
| 46-77 | predecessor: SHA-256 of the sealed object this writer last read for this record; 32 zero bytes if none |
| 78-89 | nonce: 12 random bytes, fresh for every seal |
| 90- | AES-256-GCM ciphertext, then its 16-byte tag |

So the object is 106 bytes more than the plaintext. **AAD** is the bytes
`"abr1" 00 <account> 00 <kind> 00 <id> 00` (the account as the relay names it, ASCII) followed by
bytes 0-77 of the header, so the epoch, writer, counter, predecessor and the record's own name are all
authenticated: an object moved to another record, account, epoch or writer fails to open. The record
key (HKDF from the epoch key) and the refusal of a replayed or backward counter are K1's and K2's;
the vectors give the record key as an input.

## 3. The order record (plaintext)

Canonical JSON, UTF-8: no insignificant whitespace, object keys in UTF-16 code unit order at every
level, integers only (no fraction or exponent). It carries the merge state of §5 of the design
(`pwa/js/order-merge.js`, `OrderMerge.cs`) in a compact form, because the merge form does not fit:
a row with its register, device tag and key is about 230 bytes there, so 500 rows with 125 group
memberships and 32 groups come to about 114 KB and only about 340 rows fit 64 KiB; the form below
holds the same state in about 43 KB (measured, vector `limits`).

```jsonc
{ "v": 1,
  "d": ["<device tag, 32 lowercase hex>", …],  // the writers of the registers below, by index
  "h": ["<host id, 0 to 64 characters>", …],   // the hosts of the row keys, by index
  "r": { "<h>\n<session id>": [counter, d, "<fractional key>", pinned] },  // pinned 0 or 1
  "m": { "<h>\n<session id>": [counter, d, "<group id>" | null] },         // null: in Sessions
  "g": { "<group id>": [counter, d, "<name>"] },
  "x": { "r": { "<h>\n<session id>": [counter, d, day] }, "m": { … }, "g": { … } } }  // tombstones
```

- A register is the design's `(value, counter, deviceTag)`: `counter` is an integer from 0 to 2^53-1
  and `d` an index into `d`. The merge rules are unchanged (higher counter, then higher device tag
  compared as bytes, a tombstone before a value on a full tie), and the expansion to and from the
  merge form is exact: the host index and device index are replaced by the strings they name.
- A key lives in its live map or in `x`, never both. `day` is the UTC day number (milliseconds / 86,400,000,
  rounded down) on the writer's clock when it wrote the tombstone; it is read only to age the tombstone
  (§5), never to order anything.
- A row key is the host index (decimal), a line feed and the session id (1 to 128 characters, no
  control character). A fractional key is 1 to 32 characters of `[0-9a-z]`, compared as strings. A
  group id is a lowercase UUID, 36 characters. A group name is 1 to 40 characters, trimmed, with no
  control character; duplicates differing in case can arise from a merge and are not refused here.
- `d` has unique entries, at most 64; `h` unique entries, at most 64. An entry no register names is
  refused. The legacy key is the only key form of version 1 (the stable `r:<role>` key of CHANGE C1
  needs `v` 2, and a reader of `v` 1 refuses it, so an old client cannot overwrite it).

## 4. The marker record (plaintext)

```jsonc
{ "v": 1, "h": ["<host id>", …], "t": { "<h>\n<session id>": <turn> } }   // turn: integer ≥ 0
```

Merged by the maximum turn per key; a marker for a session with no row is kept. At most 1,500
entries. Row keys and host entries follow §3.

## 5. Limits, aging and what goes first

| Limit | Value |
|---|---|
| Sealed object | 65,536 bytes; 1 MiB and 30 writes a minute for an account (S3d, S3b) |
| Live rows (`r`) | 500; live memberships (`m`) 500; live groups (`g`) 32 |
| Group name | 40 characters |
| Tombstone age | 30 days: a writer leaves a tombstone out once `today - day > 30` |

If a record would exceed 65,536 bytes: tombstones older than 30 days go first, then rows of ended
sessions, oldest first (with their membership registers). If it still does not fit, the writer keeps
the change on this device, shows "Not fully synced", and drops nothing else. Rows past 500 live only
on this device. A row left out of an object is not deleted: it is not a tombstone, and a device that
holds it keeps it. The vectors give the ended rows and their day as an input (`ended`); which sessions
are ended is a client's knowledge, never in the record.

## 6. A reader refuses

A reader refuses, and uses nothing of, an object or plaintext that: is over 65,536 bytes or under 106;
has another format, a non-zero byte 5, or a bad tag for its AAD; opens to JSON that is not canonical
(refused, so every writer gives one byte string for one record); has `v` other than 1, a key outside
the tables above, a register of another shape, an index out of range, a key in both a live map and
`x`, a value out of range, more than the limits of §5, or a field it does not know.
