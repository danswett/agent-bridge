# Roaming order and read state (#759)

Status: design, build-ready for slice S1. Sync slices S2 to S5 wait for the signed roster to be
enforced (`device-approval.md`). Storage decided by Engineering on #759 (comment 6755248456);
this document says what the person sees and exactly what roams. UI rules extend `ui.md` §6.1.

## 1. What the person gets

One order of sessions on all of their devices, one read state, and a way to keep a different
order on one device. Nothing the host sends moves a row. Sync is off until the person switches
it on.

## 2. The Sort menu

Two radio groups.

| Group | Items | Accessible name |
|---|---|---|
| 1 | **Your order** (default) | "Your order, on all your devices" once sync is on; "Your order, on this device" before |
| 1 | **This device's order** | "This device's order, only on this device" |
| 2 | **Most recent** | unchanged (rules on #628) |
| 2 | **Machine** | unchanged |

Rules:
1. Before sync is on, group 1 shows only **Your order**, labelled and named "on this device". This
   is all slice S1 ships. "This device's order" appears when sync is switched on.
2. This device's order starts as a copy of Your order at that moment, pins included, then
   changes only on this device. It is never sent anywhere.
3. Pins belong to an order. Pinning in one order does not pin in the other.
4. A session an order has never placed goes to the top of Sessions once, newest first when
   several arrive together. It then stays.
5. The active item is per device and never roams.
6. Which order is active is per device and never roams.
7. Groups (#767): names and membership roam with Your order, one set for the account.
   **Collapsed or expanded stays on each device.** This device's order keeps its own copy of
   group membership from the moment it is copied.
8. Moves stay within a row's group, as today (`ui.md` §6.1).

## 3. What roams

| Thing | Roams | Record |
|---|---|---|
| Your order: row positions, pins | yes | order record |
| Group names and membership (#767) | yes | order record |
| Sort choice | no, per device | local |
| This device's order and the active order | no | local |
| Collapsed state of a group | no | local |
| Active item | no | local |
| Read markers for sessions a host does not own (an offline machine) | yes, forward only | marker record |
| Read markers for a host's own sessions | held by that host via `sessions.read {session, turn}` from a paired client only | host |

Hosts never hold the account key. The #1190 send-time rule reads the host's local marker.

## 4. Records and limits

Two sealed records in the roster storage container, ciphertext only to the relay: the order
record (written rarely) and the marker record (batched). Limits: 64 KiB per sealed object, 1 MiB
and 30 writes a minute per account, coalesced.

If the order record would exceed 64 KiB: keep the newest change on this device, show "Not fully
synced" (§6), and drop nothing silently. Oldest tombstones are collected first. The order record
keeps at most 1,500 rows and 8 groups.

## 5. Merge (the function S1 builds, with test vectors)

State per row is a register: `(value, counter, deviceTag)`. A delete is a tombstone with the same
fields. For two registers of one row, the higher `counter` wins; equal counters, the higher
`deviceTag` wins (compared as bytes). Relay time is never used. A device raises its counter above
every counter it has seen before writing.

- Row position: a value plus a fractional key, so two inserts between the same neighbours order by
  `(key, deviceTag)` and never collide.
- Group membership is a register per session. Group name is a register per group. A deleted group
  tombstones the group; its sessions fall back to Sessions in their place.
- Read marker: `max(turn)`. Never moves backward. Merging a marker for a session with no row is kept.

Required vectors: concurrent move of the same row on two devices; insert at the same position;
delete against move; rename against delete of a group; a stale write arriving after a newer one;
equal counters; read marker from two devices; merge is the same in either order and repeating it
changes nothing.

## 6. Quiet states (S2 to S4, with sync)

Sync is one Settings item, "Sync order and read state across your devices".
- No trusted device online: second line "Waiting for another of your devices". Not disabled, no
  banner.
- Relay withholding or behind, or a record too large: second line "Not fully synced". No toast, no
  blocking.
- A removed device falls back to its local copy, labelled as in S1. No recovery flow in the first
  release.

## 7. Remote changes in the list

A remote change never moves a row under the pointer, during a drag, or with a row menu open, and
never while a row is grabbed (#1209). It applies as one batch when the pointer leaves the list or on
refresh, with `shift` motion; instant with reduced motion. Focus stays on its row. Nothing is
announced.

## 8. Read state (visible behaviour)

1. A session is read when it is open, visible and focused and the newest main-agent message (#521)
   has been on screen for about 1 s.
2. Reading never answers a Needs-you item.
3. The unread dot and Activity update fade with `value`. No row move, no announcement.
4. A session seen for the first time starts read.
5. No "Mark as unread". No setting.
6. Most recent does not re-sort on a read.
7. An offline machine's marker may arrive late; the row shows its last known state.

## 9. Later slices (not S1)

"New order…" (a copy, never empty), "Manage orders…" (rename, duplicate, delete). Names are 1 to 40
characters, unique ignoring case; "Most recent" and "Machine" are reserved. At most 8 synced
orders. An order deleted elsewhere: this device falls back to Your order with the one-time list-head
note "*Name* was deleted on another device".

## 10. Slices

| Slice | What | Sensitive |
|---|---|---|
| S1 | Local order and read records, merge function with vectors, the Sort menu rules above, "Your order, on this device" | no |
| S2 | Protocol doc and vectors for the sealed records, including groups | yes |
| S3 | Relay route and limits | yes |
| S4 | Key creation, wrapping, removal; waits for roster enforcement | yes |
| S5 | Host `sessions.read` and the #1190 check | yes |

Acceptance for sync ("the same order on another device in a few seconds") cannot be met before the
signed roster is enforced; its date is unset.

Owner defaults, unless told otherwise: shared-member trust is accepted for preferences; no
recovery in the first release; sync is off until switched on. The account-purge procedure is not
decided (same unknown as #744).
