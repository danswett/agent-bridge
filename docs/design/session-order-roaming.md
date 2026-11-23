# Roaming order and read state (#759)

Status: design, build-ready for slice S1. Sync slices S2 to S5 wait for the signed roster to be
enforced (`device-approval.md`). Storage decided by Engineering on #759 (comment 6755248456);
this document says what the person sees and exactly what roams. UI rules extend `ui.md` §6.1.

**CHANGE C1-C6, proposed Nov 21, 2026, 22:26 PT; owner approval required.**
The continuation sections below extend [seamless recycling](https://github.com/danswett/agent-bridge/issues/1504),
the [group decision](https://github.com/danswett/agent-bridge/issues/767) and
[roaming](https://github.com/danswett/agent-bridge/issues/759), not a new design or epic.
They are not built and do not lift the role-ID implementation pause or sync prerequisites.

CHANGE index: C1 = scoped/general presentation identity (§11); C2 = verified launch
succession (§12); C3 = migration/retention (§13); C4 = visibility/undo/forget (§14);
C5 = safe row projection (ui.md); C6 = canonical reconciliation and slice planning (§15).
Implementation slice IDs below are PC*, not owner-approval CHANGE IDs.

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
   changes only on this device. It is never sent anywhere. The one exception is not the order: a
   device that has notification filters on sends the **set** of its pinned session ids (no order,
   no groups, no unpinned rows) to its own account's hosts inside `push.subscribe`, so a host can
   filter pushes by pin (owner decision, recorded in
   [v1-sessions.md](../protocol/v1-sessions.md#pushsubscribe)).
3. Pins belong to an order. Pinning in one order does not pin in the other.
4. A session an order has never placed goes to the top of Sessions once, newest first when
   several arrive together. It then stays.
5. The active item is per device and never roams.
6. Which order is active is per device and never roams.
7. Groups (#767): names and membership roam with Your order, one set for the account.
   **Collapsed or expanded stays on each device.** This device's order keeps its own copy of
   group membership from the moment it is copied.
8. **CHANGE C6 (canonical reconciliation):** sorting stays within each section/group.
   Person moves cross expanded group edges as
   `ui.md` §6.1 already specifies; they never implicitly cross Pinned.

## 3. What roams

| Thing | Roams | Record |
|---|---|---|
| Your order: row positions, pins | yes | order record |
| Group names and membership (#767; at most 32 groups) | yes | order record |
| Stable keys, follow/adoption/forget registers (CHANGE C1-C4) | with their containing order only | versioned order record; local-only orders never roam |
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
and 30 writes a minute per account, coalesced. Each synced order (§9) is its own sealed object,
so Your order never shares its 64 KiB with another order; 8 orders of at most 64 KiB each stay
inside the 1 MiB account limit.

Byte budget for one order object (sealed size, after JSON and encryption overhead): at most 100
bytes per row on average and **500 rows at most**, about 50 KiB; 32 groups (the `ui.md` limit) of up
to 40 characters, about 6 KiB at worst; the remaining 8 KiB is for tombstones. A row key is a
machine id plus a session id in today's schema; CHANGE C1's versioned stable key and metadata
are specified in §§11-14, not fields already shipped. The marker record is separate and sized by Engineering (about 60 KiB
at 1,500 sessions).

If an order would exceed 64 KiB: tombstones older than 30 days go first, then rows of ended
sessions, oldest first. If it still does not fit, keep the newest change on this device, show "Not
fully synced" (§6), and drop nothing else silently. Rows past 500 live only on this device and sort
below the synced rows in their group.

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

Built in S1a (#1583) as `pwa/js/order-merge.js` (pure: no UI, storage or network), with every vector
in `pwa/test/unit/order-merge.test.mjs`. Ties the rules leave open (the same device and counter with
different values) resolve to a tombstone first, then by the value's JSON, so the merge stays the
same in either order.

S1w ports the pure merge to `src/AgentBridge.App.Core/OrderMerge.cs`. Both clients consume
`tests/fixtures/order-merge.json`, including convergence, tombstones and forward-only markers.
Live registers normalize missing values to null. JSON ties spell non-finite numbers as null,
including nested values; row-key string conversion retains JavaScript's NaN and Infinity spelling.
This adds no Windows UI, persistence or sync integration.

G1 (#1614) keeps group-name and membership registers in the PWA's local order record, using that
merge schema and increasing counters. `SessionOrder` exposes create, rename, delete and membership
operations; group headers and move menus follow in G2/G3. Pinning keeps membership, Ended remains
separate, and membership expires after 30 days without listing the session. This runs only in the
client: a host leaving does not delete the record. Nothing syncs yet, and no host holds group state.

The groups redesign's G1 (#2073) adds `groups.js` helpers over that same `SessionOrder`:
non-mutating name validation, shared with create/rename, and counts of current unpinned live
rows. It adds no storage key or membership record; edits still use the existing order API.

G2 (#1615) draws those groups between Pinned and Sessions. Collapse is kept only in the device's
local order, outside the group registers. Collapsed groups retain visible needs-you rows; search
and Needs you temporarily expand matching groups without changing the saved collapse.

G3a (#1859) wires Move to group, New group and Remove from group into the PWA row menu,
using the same local records. The chooser checks current membership and uses a sheet on phones;
desktop name entry is inline at the foot of the groups. Pin and Unpin retain membership.
Touch entry and drag/group-edge moves remain separate G3 slices. Nothing syncs yet.

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
   has been on screen for about 1 s. The dwell reads an injected clock; tests advance it and never sleep.
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

## 11. Stable presentation identity (CHANGE C1)

Use the existing [ROLE ID design](role-ids.md), not a name, truncated node ID, provider
conversation ID or a second lineage registry. A role here is a person-adopted logical slot;
the same mechanism covers any recycled session, not only coordinators or a named group.
This extends that design's fleet-only enrollment scope to optional personal continuity slots.
Do not require every unrelated session to acquire a role or grant role-message privileges.

In a versioned order object, a stable row key is `r:<roleId>`; the sealed object's authenticated
account and roster generation supply its scope. An unadopted row retains a typed legacy
machine/session key. A new execution has its own SESSION GUID and host binding; these locate
the current occupant, never the person's pin, manual position or group membership.
Read markers, drafts, pending sends, approvals and execution history remain session-bound.

Each applicable order retains its existing pin/position and membership registers for that
one row. This includes Your order, This device's order and later named orders, even inactive
ones. Preserve every stored membership; existing cardinality remains one user group per row
per order. Different orders can choose different groups. Pinned rows retain hidden membership.
Group IDs/names/order, collapse, sort choice and active-order choice do not change on recycle.
No automatic group creation, role-name grouping or special treatment of any group is added.

## 12. Verified continuation, not a launch self-claim (CHANGE C2)

Consume the committed, person-approved succession link specified in
[role-ids.md: presentation continuity](role-ids.md#presentation-continuity-change-c1-c2).
The link binds exact predecessor and successor activations, scoped ROLE ID, assignment
version/epoch and opaque launch request ID; its person-visible effect is inheritance or separation.
Names, prompt text, a host-only signature and a launch/stop grant cannot supply consent.
The signed registry/role ledger publishes identity evidence, never private preferences.
Hosts publish execution bindings and proof references; independently enrolled clients verify
them against that ledger. A session summary is a hint, not an authority or a preference write.

Only a committed higher assignment with confirmed predecessor stop can change the occupant.
A prepared successor, expired claim, incomplete snapshot, duplicate current claim, replay,
wrong account/generation or unknown stop outcome cannot take the saved slot.
Historical lower epochs are not competitors and cannot switch the row back.
Two candidates at one current epoch freeze the slot with an explicit identity-conflict state;
no latest-arrival/name tie-break. Show candidates in details without giving them the pin/groups.
Retain preferences during a vacancy, offline interval or conflict; do not fabricate liveness.

For verified inheritance, replace the execution binding behind the stable row in one model
transaction, without generating new pin/move/group writes. Repeated observations are idempotent.
The clients that missed the launch resolve the same committed lineage on reconnect. Local
orders work without preference sync; another device has only its own saved choices until sync.
When the role authority is unavailable, keep a stale read-only slot, not an offline takeover.
No holder or client machine is the continuity coordinator: durable identity lives in the
replicated role ledger; preferences live locally now and in sealed account records with sync.

Ordinary same-provider-ID resume keeps today's legacy behavior until adopted. A fresh
activation, cross-host transfer/fork, rename or copied conversation alone proves no succession.
Unlinked sessions remain independent, unpinned/ungrouped new rows under the existing default.
Cross-host approved succession preserves manual rank, not a false machine label/filter:
Machine sort and machine tabs use the successor's actual verified host.

## 13. Adoption, migration and retention (CHANGE C3)

After adoption, even a same-provider-ID manual resume needs explicit Approve continuation
through the existing person role-assignment UI to occupy that slot. Disclose this at adoption:
"Manual resumes stay separate until approved as a continuation." A retained legacy alias
never auto-matches a new activation. The slot keeps its choices while approval is pending;
this intentional post-adoption change is C3, not a regression hidden behind migration.

The person explicitly adopts an existing session into a continuity slot. The approved link
names its full verified execution tuple and legacy locator; never infer an alias from titles.
For each order, atomically rekey its saved pin/position/membership registers to the stable key,
preserving counters and values, and persist a migration marker plus the source alias.
Merge aliases before row registers using the existing counter/deviceTag rules. An existing
stable register or newer user edit wins over an older imported value; no launch-time snapshot
may overwrite a subsequent unpin, move or group deletion. Resolve deleted groups as §5 says.
Crash before commit leaves the legacy record intact; crash after commit replays no import.
Keep the original readable backup; failed persistence keeps the existing safe sort fallback.

Old clients keep their versioned local records and cannot write the new roaming object.
Enable the new sealed-record schema only after negotiated client/trust support; do not let an
old client's whole-object write erase stable rows, detach choices or migration markers.
Alias/cutover records remain while their stable row is retained. Forgotten/retired aliases
cannot be rediscovered from old host snapshots; importing old schema requires explicit person
adoption again, not background migration. Cross-generation/account adoption is never automatic.

Stable preferences do not expire after 30 days without a listed execution. Keep vacant,
unavailable and retired slots until the person forgets them; a retired ROLE ID is never reusable.
The existing 30-day rule remains for legacy session memberships. Role-authority retirement
tombstones are permanent security records, separate from disposable preference tombstones.
Use the existing 500-row/32-group/64-KiB/1-MiB limits. Stable rows and migration/detach metadata
count toward them; do not silently evict saved continuity choices. If the object does not fit,
keep the local change and show Not fully synced. New-schema vectors must measure sealed bytes;
the old 100-bytes-per-row estimate is not proof that the added metadata fits.

## 14. Person visibility and undo (CHANGE C4)

Before approving a launch-time link, the person sees predecessor, successor, role and actual
hosts, and "Keep pins, positions and user groups in each order". A launch result alone changes
nothing. After commit, row details show "Continues <predecessor>", the verified assignment and
launch receipt; labels render as plain text. No public log contains group names or preferences.

"Treat this session separately" is a person-only preference action, for the selected order or
all existing orders. Store a mergeable `follow=false` register on the stable row and give the
successor an ordinary session row with new-session defaults. Keep the stable row's saved
choices dormant; never re-pin the predecessor or change holder authority as an undo side effect.
The opt-out persists through later successors and sync/reload. "Follow this identity again"
sets `follow=true` and resumes the stable choices; disclose that independent row choices are
not copied over, then remove its independent presentation alias atomically.
Neither action revokes/reassigns the role, ends a session, grants permissions, or retargets a
draft. Undoing the operational handover is a separate person-approved role-management action.

Manage continuity also offers "Move saved choices to this session" and "Forget saved slot",
for the selected order or all existing orders, with the scope disclosed before confirmation.
Moving is an explicit preference choice: select a verified independent session or separately
enrolled new role, preview replacing its existing choices, then move pin/rank/membership and
tombstone the source in one durable transaction per order. A shared operation ID makes retries
idempotent; all-order work reports any pending order, not global atomic success. No old role
is resurrected or assigned; a retired/conflicting/stop-unconfirmed slot can be left behind
without changing operational custody or its unresolved reservations.
Forget tombstones the slot's preference registers and prevents background re-import.
Both actions free its logical row quota immediately. Retained alias/forget tombstones still
count as bytes and are compacted only after admitted new-schema writers acknowledge the
cutover; a removed writer cannot write again. Missing acknowledgements show the waiting
devices in Manage continuity and retain Not fully synced if needed; only a person may remove
a device through existing device management. No silent eviction or fabricated acknowledgement.

Account switching clears projections immediately. Sealed sync contains IDs, registers and
person-entered group names only, never titles, paths, prompts, history or host credentials.
Agents/hosts cannot write these records or promote themselves into someone's saved pins.

## 15. Projection and bounded follow-on slices (CHANGE C5-C6)

UI behavior belongs to [ui.md: verified successors](ui.md#verified-successors-change-c4-c5).
Apply binding changes under its input/focus holds; old replies/answers never silently attach
to the new occupant. Most recent/Machine remain derived views, not exact-rank promises.
Slot states are active, awaiting successor (vacant/quiescing), unavailable (stale/incomplete),
identity conflict, retired and detached (follow=false); "paused" adds no authority state.
Every retained slot consumes one of 500 logical rows, including dormant ones, not one per
successor. Ended remains execution history: an adopted old activation is available in slot
details, not a duplicate Ended/pinned/group row. Legacy unadopted Ended/30-day behavior stays.

CHANGE C6 also reconciles the stale group-move sentence in §2 and "sort roams" wording in
ui.md with the already-decided roaming rules; it does not newly enable roaming sort choice.
These slices extend the existing issues above and the ordered role-ID and roaming slices. No code,
rollout, automatic end or new session is authorized. Each budget is additions plus deletions,
including tests/docs; split before exceeding it, never consume a parent/carry allowance.

Sensitivity follows the current Fleet register; no historical Opus requirement is authority.
Role-ID numbers below reference that canonical doc's existing ordered slices.

| Slice | Maximum WHOLE | Prerequisites | Sensitive | Failing-first acceptance |
|---|---|---|---|---|
| PC1 identity/assignment schema vectors | 220 | Owner approves CHANGE C1-C6; role-ID 1 | yes | Typed IDs, continuity signed-body fields and opaque/salted request commitment; old schema cannot overwrite |
| PC2a negotiated launch preparation/proof | 240 | PC1; role-ID 3-4,6; existing host launch | yes | Capability/refusal before dispatch; independently authorized prepared activation; exact request/proof; unsupported receiver launches nothing |
| PC2b person link approval/commit/publication | 240 | PC2a; role-ID 2,5-9,14 | yes | Launched/existing-activation variants; assignment/CAS/approval; competing successors, replay, expiry, unknown stop, no automatic reapproval |
| PC3 pure preference migration/merge | 240 | PC1; existing JS/C# merge ports | no | Pin/rank/all orders/arbitrary groups; late edits/delete; duplicate/out-of-order link; crash; byte limits; resume alias cannot auto-adopt |
| PC4 PWA local projection/management | 240 | PC2b,PC3; role-ID 9,12; PWA groups | yes | Pinned/unpinned/manual resume, hidden membership, conflict/retired/reload, detach/rejoin/move/forget, no changed answer target |
| PC5a Windows verified continuity reader | 220 | PC1; role-ID 4,6,9 | yes | Full proof/epoch/account validation; incomplete, stale, split-brain and legacy publication refuse |
| PC5b Windows projection/management | 240 | PC2b,PC3,PC5a; existing Windows group model/UI | yes | Shared vectors and interaction cases; failed save fallback; actual-host filter; safe manual-resume approval |
| PC6 sealed-record sync integration | 240 | PC4,PC5b; signed roster and existing roaming S2-S4 | yes | Missed recycle/concurrent edits/detach converge; offline/mixed clients; forget cutover acknowledgement; no host-readable preferences |
| PC7 Fleet launch-lead adapter | 220 | PC2b; role-ID 9-10,14 | yes | Structured predecessor/request, not prompt; lost result cannot inherit/relaunch; no inherited grants |
| PC8 other-client parity, one client per PR | 220 | PC2b,PC3; that client's groups and verified reader | yes | General groups/pin/rank/management/conflict/target cases; no live test-agent sessions |
