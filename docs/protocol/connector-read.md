# Connector read contract, version 1 (draft)

Bridge owns the connector API; Home Assistant is its first external consumer, not
a dependency of this contract. Refs #388 and #519. This first executable slice
describes **existing application reads**, not a released connector enrollment API.
The machine-readable index is [connector-read-v1.json](connector-read-v1.json).
Its `requestSchema` is a JSON Schema 2020-12 document for requests in this profile.
The remaining fields index operations, result/event records, limits and fixtures.

## Boundary and versioning

The application protocol is [Bridge Protocol v1](v1-sessions.md), over an already
authenticated, authorized, encrypted channel. The index's `version` versions this
connector profile; `protocol` is the negotiated Bridge protocol. Unknown fields
and enum values follow the existing protocol's malformed-record rules, not a new
strict decoder. The request schema describes conforming writers, not everything
an older host happens to accept. Do not use it as an authorization validator.
The shared record vectors remain the payload contract's worked examples; this
slice does not yet supply JSON Schemas for every nested result record.

Within version 1, additive host fields and capabilities must remain readable.
Removing/renaming an operation, changing its meaning, or requiring previously
optional client data needs a deliberate version/compatibility decision. A
connector checks `hello.protocol` and `sessions.read` before reading sessions.
Capabilities describe features, **not permission grants**.

Only `hello`, `sessions.list`, `session.get` and `sessions.subscribe` belong to this
profile. There is no answer, approval, folder trust, reply, launch, administration,
attachment, push or usage grant. Each connector needs its own owner-approved,
independently revocable service identity and selected-host read authorization.
The identity, issuance, storage, roster/freshness and revocation design remains
with #519/#388 and #55/#391; this contract does not bypass those gates.

## Reading and recovery

Responses correlate by `re` to a channel-unique positive request `id`. This
cross-language writer profile uses IDs no greater than 9007199254740991 (the
JavaScript safe-integer limit); this does not narrow the host's existing decoder.
A missing
session/sub-agent is `{ "missing": true }`, not a missing or empty fleet.
`session.get` may include an `agent` only when the host exposes sub-agents.
`sessions.list` pages can span different revisions; use a subscription for a
consistent snapshot. List/upsert records omit activity history; get includes it.

A subscription response precedes its snapshot. Collect upserts into a replacement
set and publish it only on `sessions.synced`, including discovery-held state.
Subsequent events modify that set. Event sequence belongs to the channel; the
first is the baseline. A gap requires a fresh subscription, not applying a partial
stream as authoritative. Re-subscribing replaces the old feed and continues the
sequence. Reconnect starts a new channel and snapshot; closing the channel ends
the feed. Unavailable/disconnected is not an authoritative empty snapshot.

## Disposable reference fixture

[connector-read-conversations-v1.json](connector-read-conversations-v1.json) records results and
snapshot events from the **actual `SessionService` and `SessionSource`**, using
synthetic agent files and a capturing `IProtocolChannel`. It does not record Noise,
relay admission or a physical transport's envelope serializer. The mock wraps those
payloads in the documented envelopes. The existing
[record vectors](vectors/README.md) additionally cover malformed/unknown fields,
sub-agents, discovery holds and must-refuse payloads.

Its `hello` is recorded from a host with no pinned peers, so it lists every capability
that host offers, `sessions.patch` and `sessions.history` among them. A connector's
`service` peer is offered `sessions.read` alone and is never served `session.history`
([v1-history.md](v1-history.md), Security); the profile uses neither.

Run the dependency-free, stdio-only reference mock:

```powershell
node tools/connector-conformance/mock-host.mjs
```

Send one request JSON object per line. Each response/event is one output line.
Use the request data in the conversation fixture; its session/machine names and
IDs are invented. The mock supports only those recorded cases, not arbitrary
sessions, filesystem discovery or all host behavior. Repeated subscribes replay
the same snapshot with continuing event sequence. Unsupported cases return an
explicit mock-fixture error. There is no listener, key, credential or live host.

Connector authors can feed the mock's output into their normal decrypted-channel
reader. Passing the mock tests is **not full connector certification**: pagination,
live mutation/gap recovery, cancellation, revocation/restart/restore, encrypted
transport and the final admission contract still need independent conformance
scenarios and real-host validation before production delivery.

Host-side fixture drift is checked by `ConnectorReadVectorTests`. Regenerate
deliberately with `UPDATE_PROTOCOL_VECTORS=1` and that class's test filter, then run
the mock's tests (`node --test tools/connector-conformance/mock-host.test.mjs`).
No HA runtime, installation, legacy removal or live cutover is authorized here.
