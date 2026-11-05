# Proposed v1 extension: host planned state and offline observation

**Draft, #805 phase 1; not implemented.** See
[design and safety argument](../design/host-liveness.md).
#801 (`sre/801-remote-host-mgmt`) consumes this single announcement for stop and
restart; it must not introduce another planned-state message. Connection
(`eb29d6d8`) reviews this contract before code; Engineering supplies the
independent sensitive review. Nothing here authorizes a remote management action.

## Transport and negotiation

These are relay JSON text controls, like [roster presence](roster.md), **not**
Noise session ops or the local host-management API. Protocol v1 remains v1.
Add optional `capabilities: ["host-planned-state-v1"]` to relay `hello`; `ready`
echoes the supported intersection. The ready capability requires durable
lifecycle storage/expiry and observation support; a disabled/unavailable store
must not advertise a guarantee it cannot keep. New clients send controls only
after the intersection contains this capability. Older relays ignore the hello
addition; older clients ignore additional ready/presence fields. An unknown
version is refused as an unsupported extension, not interpreted as v1.

Host lifecycle metadata is plaintext to the relay. No session contents, account
login/name, IP, actor or command request ID are added. Account/device ownership
comes **only** from the authenticated socket, never a message-supplied target.
Only an admitted, proven current-generation `host` may publish for itself.
Proof establishes the key holder, not person authorization or an attestation of
power state. #801 must separately authorize the person's action over Noise.

## The one announcement: `host-planned-state`

```json
{
  "t": "host-planned-state",
  "v": 1,
  "transition": "94df652f-a455-49a2-8806-8c37d466b158",
  "seq": 1,
  "phase": "planned",
  "reason": "reboot",
  "expectedReturnSeconds": 300
}
```

| Field | Contract |
|---|---|
| `v` | Integer 1. Extension version, independent of host/app build. |
| `transition` | Random UUID, not a session ID, device ID or command UUID. Same value in #801's E2E result, which may privately carry attribution. |
| `seq` | Positive safe integer, strictly increasing within this admitted connection. An identical resend is idempotent and gets the original acknowledgement. Same sequence with different data is refused. |
| `phase` | `planned` or `cancelled`; cancellation uses the same transition, a greater sequence and no reason/duration/envelopes. No cancellation can revive a completed transition. |
| `reason` | Required for planned: `sleep`, `shutdown`, `reboot`, `update`, `sign-out`, `restart`, `stop`. No free text. `restart` means host restart, not OS reboot. |
| `expectedReturnSeconds` | Only for reboot/restart/update, integer 30–900. Defaults 300/60/90 respectively. No return promise for sleep/shutdown/sign-out/stop. |
| `notices` | Optional bounded array of pre-sealed generic no-return push envelopes, described below. Never needed for offline reason to work. |

At most one active transition per device. Planned intent must be followed by
disconnect within **120 seconds** of relay receipt or expires without a return
timer. Repeating the message cannot extend that deadline. No overlapping
transition can replace or postpone an armed return deadline. Limit lifecycle
changes to six/minute/device and 30/minute/account across reconnects, and charge
them against the existing frame limiter too. Metadata without notices is capped
at 1 KiB; the entire control at 64 KiB, at most eight notices. Refuse malformed,
unknown reason/phase, excessive or unauthorized messages with a bounded
extension error; existing frame/rate violations retain their normal close codes.

```json
{
  "t": "host-planned-state-result",
  "v": 1,
  "transition": "94df652f-a455-49a2-8806-8c37d466b158",
  "seq": 1,
  "accepted": true,
  "receivedAt": "2026-11-05T11:11:50Z",
  "departBy": "2026-11-05T11:13:50Z"
}
```

On refusal `accepted: false` and
`error: unsupported|not-allowed|bad-request|conflict|rate-limited|unavailable`;
omit receipt/deadline. No reflective raw error text. A success means durable
state/expiry work has committed, **not** that the OS departed or a push was sent.
Durable cancellation is acknowledged the same way. An identical replay returns
the original receipt/deadline. Product-initiated stop/restart/update on a capable relay waits up to
five seconds for success before committing departure; on failure abort/refuse
with `announce-failed`. If the capability is absent, #801's independently
authorized restart/stop still proceeds with legacy offline presentation and
its cached E2E command result, without a relay reason or no-return push.
OS power/session events are best effort and cannot be held indefinitely.
If an authorized action aborts after success, #801 sends cancellation and answers
refused over E2E; if cancellation cannot arrive, departBy bounds stale intent.

Lifecycle state is not a command, authorization grant, remote stop request or
reason to suppress any existing auth/transport failure.

## Generation, close-code and expiry rules

The relay assigns an internal fresh generation to each admitted socket and
atomically associates its pending transition with that generation. On
disconnect, only the slot's current generation may commit offline state. A new
authenticated host connection clears active intent, pending notices and
offline presentation immediately. An old socket's finally block must not undo
that; fix its observation/roster side effects together, not just dictionary
removal. Preserve a minimal transition tombstone for replay/dedup up to 24 hours.

Record only numeric observed close code (or null), direction
`relay|peer|none`, observed UTC disconnect time, fixed outcome category and the
bounded plan. Never retain/log peer-controlled close reason text. A final
cleanup close 1000 must not overwrite an earlier relay close/refusal or a
dropped-connection observation. Pre-proof refusals are aggregate-only; even a
claimed device key must not poison an existing proven device's history.
Post-proof capacity refusal may report a connection error to that client but
must not mark a still-online device offline.

Existing definitions in [`Wire.CloseCodes`](../../src/AgentBridge.Transport/Wire.cs)
and behavior in [`RelayHub`](../../src/AgentBridge.Relay/RelayHub.cs):

| Code | Meaning; offline interpretation |
|---|---|
| 4000 | Protocol/bad hello. Not "host crashed"; never record unproved identity. |
| 4001 | Token/auth failed. No inference of deliberate sign-out. |
| 4003 | Key proof failed. Not an authenticated host announcement. |
| 4008 | Frame rate limited. Transport/refusal diagnostic, not a planned departure. |
| 4009 | Replaced by a new connection for that device. Never overwrite the replacement with offline/old plan. |
| 4013 | Frame too large. Not "host hung". |
| 4029 | Account's concurrent device limit (default 32). Not shutdown or missing-machine evidence. |
| 1012 | **Relay service restarting/draining**. Preserve existing client reconnect/backoff/spread semantics; never label the host rebooting or start a host no-return incident solely from it. |

A planned product departure normally closes 1000. No new close code is needed.
Only a current plan followed by an ordinary peer closure (1000/1001) or missing
close frame within departBy is eligible as planned offline. Relay enforcement
codes, replacement and service drain take precedence; expire/cancel the intent
rather than allowing it to mask those errors. Abrupt connection loss with a
valid plan is still only **host-announced**, not proof of cause. Missing close
frames are null, not an invented 1006 close sent by the peer.

For reboot/restart/update, compute:
`returnDue = offlineSince + expectedReturnSeconds + graceSeconds`.
Grace is relay-fixed: 300/120/210 respectively; defaults yield 10/3/5 minutes.
A plan acknowledgement persists only pending intent, departBy and notice references.
The eligible disconnect atomically persists offlineSince and returnDue as a second
durable transition. A due-time worker claims dispatch separately. If a relay dies
before observing/persisting that disconnect, do not invent its time from a stale
lastSeen or dispatch from an unarmed plan: expire pending intent and report unknown
coverage after reconciliation. Persistent-store failure leaves timer coverage unknown,
not an acknowledged no-return incident silently counted as handled.
A missing return changes the planned treatment to overdue, even if notices are
disabled. Sleep/shutdown/sign-out/stop have no returnDue.
All detailed observation/intent/tombstone state expires within 24 hours of
offlineSince; do not renew it from reads, failed pushes or repeated plans.
Delete immediately on account deletion/forget. Roster's lastSeen can outlive
these fields. No timestamp from the device controls retention or timer authority.

## Presence extension for both clients

Within the existing ordered, account-scoped `presence.known` entries:

```json
{
  "id": "0123456789abcdef0123456789abcdef",
  "name": "Desk",
  "kind": "host",
  "lastSeen": "2026-11-05T11:12:00Z",
  "offline": {
    "v": 1,
    "since": "2026-11-05T11:12:00Z",
    "closeCode": 1000,
    "closeSource": "peer",
    "outcome": "closed",
    "planned": {
      "transition": "94df652f-a455-49a2-8806-8c37d466b158",
      "reason": "reboot",
      "receivedAt": "2026-11-05T11:11:50Z",
      "returnDue": "2026-11-05T11:22:00Z",
      "expiresAt": "2026-11-06T11:12:00Z"
    }
  }
}
```

`offline`/`planned` are optional; absent means unknown/no reliable plan, not
"network error". `outcome` uses a fixed enum (`closed`, `dropped`, `rate-limited`,
`too-large`, `relay-restarting`, `unknown`); unknown fields/reasons/versions fall
back to lost connection. `returnDue` is absent for non-return states.
The latest presence wins; normal online devices take precedence over known/
offline data, with no stale cache reattaching a prior plan. No notices/endpoints
or actor attribution appear in presence.

UTC wire times are formatted Pacific/PT by both clients. If an ungraceful relay
restart left only stale roster lastSeen, omit exact `since`/use an unknown
observation rather than inventing the outage start. Client copy, update
90-second optimistic treatment and stale-cache handling are specified in the
[design](../design/host-liveness.md#relay-storage-and-client-presentation).
This never substitutes for #800's own auth/recovery state.

## Durable storage bounds

Use an explicit versioned lifecycle sidecar to the roster, not an undocumented
addition that roster v1 may discard. Scope by the roster's opaque HMAC account
storage key and proven device ID. Same managed-identity storage access,
conditional ETag merge, encryption at rest and deletion semantics; no public
lookup, no identities/addresses in logs or exported metric labels.
Store one active observation/transition per retained host (maximum 64/account).
Separate transient generation identity from persisted transition version so
restarting the relay does not make old pending plans authoritative.

Budget at most 64 KiB per device record including envelopes, 4 MiB/account,
and initial global limits of 10,000 account records/64 MiB cached state; a
streaming expiry worker must evict cold accounts rather than preload all plans.
Bound reads before allocation and cap concurrent writes/timers. When metadata
cannot be retained, refuse new plans with unavailable; do not falsely acknowledge.
Drop optional notices first at their quota, explicitly report notices unarmed,
and keep a reason-only plan if its durable metadata fits. Persist event changes,
not every heartbeat. Coalesce ordinary observations under the roster's existing
30-second cadence; a deliberate announcement/cancel/dedup claim needs its own
bounded awaited write and is admission/rate limited. Exact global operating
limits and store transaction layout need Connection/SRE review.

## Pre-sealed no-return notices and consent

New functionality, **not present in today's PushGateway**: the recipient person
registers an expiring relay notification target for its authenticated device,
with endpoint ownership/consent bound to that device/account and verified
against its current approved-person grant. A hello `kind` claim alone is not
person authorization. Registration/revocation must be designed with Connection's
auth state and existing E2E `push.subscribe` flow before phase 2; without it,
no-return push remains disabled. The host receives opaque target handles over
that E2E subscription path; a revoked handle cannot be reused.

Each optional `notices` entry is
`{target, body, ttl:3600, urgency:"low", topic}`: target is an opaque consent
handle, body is base64 RFC 8291 ciphertext (at most existing
`WebPushCrypto.RecordSize` decoded bytes), and topic satisfies existing
`PushTopic` syntax. Endpoint is resolved from current target registration,
not accepted as a free-form URL in the plan. Handle, topic and transition are
private routing/dedup metadata, not metric labels or log fields.
Envelopes contain no browser private/auth keys. The host seals the generic
notice to the already subscribed recipient using the existing Web Push crypto.

Decrypted payload extension:
`{v:1, kind:"host-no-return", transition:<uuid>, expiresAt:<UTC>,
body:"A machine has not returned after a planned restart. Open Agent Bridge to check."}`.
The host computes expiry as the intended due time plus one hour and the two-minute
maximum departure window; relay additionally enforces one hour from actual due.
Client suppresses expired notices and reconciles online/cancelled state when it
can; notification delivery racing a reconnect can still occur and conveys
only a past failure to return, never present authority.

The relay schedules at returnDue, durably claims each eligible recipient once,
then resolves its current consent, rechecks online state, quiet schedule and
caps, and calls the existing VAPID `PushGateway.SendAsync` path. Extend `ready`
with optional `hostNoReturnPush: true` only when this consent/scheduling path
is configured; state support alone must not imply push support. The result adds
`noticesArmed: true|false` when notices were requested; accepted reason metadata
can coexist with false. No target registration, VAPID or envelope means no push.

Existing HTTPS/host/port allowlist, maximum encrypted record and 30/account/minute
limit remain mandatory. Additional caps: one/device/hour, three/account/24 hours,
eight recipients/transition and one dispatch/transition/recipient. Claims,
quiet deferral and cap counters survive handover; failed/uncertain HTTP delivery
does not retry the same notice. Delete payload after send/cancel/expiry and
retain only a bounded dedup tombstone through the record TTL.
Sweep without needing an online socket for that account. Reconcile return and
storage generations after relay restart before dispatching overdue notices;
allow three minutes for devices to return after relay drain/outage.

No push payload, subscription endpoint, routing identity, IP or exception URL
may be logged. The existing gateway's raw exception-message logging must not
be copied into this path. This design adds no home-network or command actor
metadata to service logs and no VAPID/key change.
