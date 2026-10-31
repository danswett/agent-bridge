# Roster: the account's machines, online or not

The relay remembers each account's hosts, so a device can list a machine that is offline,
with when it was last seen, and a person can forget one that is gone (#50; agent-ha-bridge's
status card did both through Home Assistant). Implemented by `AgentBridge.Relay`
(`MachineRoster`, `RelayHub`), `AgentBridge.Transport` (`RelayClient.Known`,
`RelayClient.ForgetAsync`), the app (`Fleet`, `OfflineMachineViewModel`) and the PWA
(`js/relay.js`, `js/connection.js`, `js/forget.js`, `renderMachines`).

Everything here is relay text control messages, outside any channel, as for the blob store
([blobs.md](blobs.md)). Nothing about a session is in it.

## What is recorded, and by whom

* **Hosts only.** When a socket of kind `host` is admitted, the relay records the device id
  it proved and the name it gave in `hello` (control characters dropped, at most 64
  characters), and when it was seen. When its last socket ends, the relay records that time
  as when it was last seen. While it stays connected, the relay refreshes that time in
  storage every 10 minutes.
* **Nothing else adds or changes an entry.** No message names a machine to record: a host
  records itself, by being admitted, and nothing more. Apps, PWAs, phones and test devices are
  never recorded.
* **One account.** The account is always the one the socket's token proved. A device reads
  only its own account's roster, in its own presence, and a forget only ever touches its own
  account's roster. Another account's machine is "not found", the same as one that never
  existed.
* **Bounded.** At most 64 machines per account; past that, the offline machine seen longest
  ago goes. An account's roster is written at most once every 30 seconds, however often its
  hosts come and go, plus once when the relay stops (after it has drained, awaited within the
  shutdown timeout). A device that only reads, such as a phone opening the app, writes nothing.

## `presence`: `known`

A relay with a roster adds `known` to every `presence` message: the account's machines that
are **not** connected now, most recently seen first.

```json
{
  "t": "presence",
  "devices": [{ "id": "…", "name": "Desk", "kind": "host", "since": "2026-10-31T17:02:11Z" }],
  "known": [{ "id": "3f9a…", "name": "Travel laptop", "kind": "host", "lastSeen": "2026-10-28T08:15:40Z" }]
}
```

| Field | Meaning |
|---|---|
| `id` | The machine's device id, 32 lowercase hex characters |
| `name` | What it called itself when it last connected |
| `kind` | Always `host` |
| `lastSeen` | When it was last connected (ISO 8601, UTC) |

A relay without a roster (an older one, or one configured with `Relay:Roster:Backend` `off`)
sends no `known`, and a client then lists only what is online and offers no Forget. Nor does a
relay that has not yet read the account's stored roster (the store was down when the device
connected): `known` is absent, never an empty list a client would take as every offline machine
forgotten, and arrives in a later `presence` once read. A client infers that a machine was
forgotten (from another device) only from two `known` lists in a row. A client that predates the
roster reads `devices` and ignores `known`.

## `forget` and `forget-result`

```json
{ "t": "forget", "id": 7, "machine": "3f9a…" }
{ "t": "forget-result", "id": 7, "machine": "3f9a…" }
{ "t": "forget-result", "id": 7, "machine": "3f9a…", "error": "online" }
```

`id` is the device's own request number, echoed back. On success the relay sends `presence`
to every device of the account, without the machine in `known`.

| `error` | Why |
|---|---|
| `not-allowed` | The device is not a person's: only kinds `app`, `pwa` and `phone` may forget a machine. A host is where an agent acts, so a host never can, and neither can a test device or any kind added later until it is put on that list |
| `online` | The machine is connected now. It can only be forgotten while it is gone |
| `not-found` | The account has no such machine (or it was already forgotten, or it is another account's) |
| `bad-request` | `machine` is not a device id |
| `rate-limited` | More than 10 forget requests in a minute on the account, from all its devices together, however often they reconnect |
| `unavailable` | The relay has not been able to read the account's stored roster yet |
| `disabled` | The relay keeps no roster |

A forgotten machine that connects again is the account's again, and is listed as offline the
next time it leaves, as agent-ha-bridge's Forget behaved.

Clients ask for a forget only after **two presses**: the first arms that machine's Forget for
5 seconds (the button reads "Press again to forget"), the second sends it. An arm lapses, and
goes when the machine leaves the offline list (back online, or forgotten from another device);
on the PWA, pressing another machine's Forget moves the arm there. Once forgotten, the PWA also drops what it had learnt
about the machine (its status line and usage, including any look at it still on its way),
leaves it if it was the one being viewed, and closes the notifications it had shown from it;
it does the same for a machine forgotten from another of the account's devices (one that leaves
`known` without coming online). Push subscriptions are kept by each host, so a
forgotten machine holds its own until it is uninstalled; if it ever connects again, it is
listed again and its notifications resume.

## Storage

In memory by default, so a restart forgets every offline machine (online ones are recorded
again as they reconnect). With `Relay:Roster:Backend` `azure` the relay keeps one JSON blob
per account in its own storage account (`infra/modules/roster-store.bicep`; see
[relay-deploy.md](../relay-deploy.md#machine-roster)), reached by the app's managed identity
with no key.

* **Names.** `v1/<hex>.json`, where `<hex>` is an HMAC-SHA256 of the account id under a key
  derived from `Relay:TokenKey`, so a name says nothing about whose roster it is. A stored
  roster requires the token key: a per-process key could not find it again after a restart.
  Rotating the token key (which signs everyone out anyway) orphans every roster; they then age
  out under the lifecycle rule.
* **Contents.** `{ "v": 1, "machines": [{ "id", "name", "kind", "lastSeen" }], "forgotten": { "<id>": "<when>" } }`.
* **Two relays at once.** Writes are conditional on the blob's ETag, so a draining relay and
  its replacement cannot lose each other's changes: a write against a copy that has changed is
  refused, and the writer reads, merges and writes again. The merge is three-way, against the
  copy the writer last read or wrote: a machine both have keeps its latest sighting; one only
  storage has is added unless this relay let it go since; one only this relay has is kept unless
  storage has dropped it (forgotten or capped by the other relay) and this relay has not seen it
  since. So a relay holding an old copy never brings back a machine forgotten elsewhere. Forgets
  are also stored, at most 256 per account for a day, to settle a forget on one relay against
  the same machine seen on the other at the same moment: a machine shows only if seen after it
  was last forgotten.
* **Ageing out.** A roster whose hosts have all been offline for 180 days is deleted by the
  storage account's lifecycle rule. A roster left with no machines and no forgets is deleted
  by the relay when it next writes it.
* **When the store is down.** Admission waits at most 5 seconds for an account's roster, then
  goes on without it. The writer reads it again every few seconds while any of the account's
  devices is connected, and sends them `presence` once it has it; and it reads it before
  writing anything, so a roster is never overwritten by what one relay happened to see.

Logs and metrics carry no account, device, name or blob name: `relay.roster` counts loads,
writes and forgets by outcome only.
