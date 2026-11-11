# Optional Home Assistant client

**Proposed design only, #1419; November 11, 2026 (Pacific).** No runtime,
enrollment grant, live parity result or retirement permission is delivered here.
The [client protocol](client-protocol.md) governs every caller, including HA and
the parked third-party API (#1398). No private HA connector is needed in a host:
Python consumes the ordinary relay/channel protocol; HA owns entities and cards.

The current decisions on [#1396](https://github.com/danswett/agent-bridge/issues/1396)
supersede older time-based gates: #1949's load parity proof, required row evidence,
the approved HA-off rehearsal and final sign-off govern retirement. S8 is optional
and later, not a retirement prerequisite. S0 is complete: no owner-built HA
function must survive. Existing compatibility is unchanged by this note.

## Placement and state

The component runs inside whichever HA installation the person chooses, never
on a named fleet machine. One asynchronous connection manager per config entry
owns its relay socket and authorized host channels. No collector or HA publisher
runs inside Slipgates. HA entity caches are disposable; session truth and grants
remain at their authoritative hosts, protected roster state at the relay.

If that HA node leaves, nothing else in Slipgates changes: messages, approvals,
launches, other clients and fleet discovery continue independently. HA downtime
cannot stall a host; bounded subscription queues disconnect a slow consumer.
Unload cancels tasks, closes channels and clears decrypted caches; it neither
stops sessions nor removes hosts. Removing the integration erases its local
credential store; independent revocation at the host invalidates any stolen copy.
Moving HA or restoring a backup requires re-enrollment, not a cloned identity.

## Python secure channel

Use outbound WSS with certificate verification and the existing binary routing
envelope, then `Noise_XX_P256_AESGCM_SHA256` end to end to the selected host.
Reuse the isolated Python Noise and codec work in
[HA PR 9](https://github.com/danswett/agent-bridge-home-assistant/pull/9) and
[HA PR 10](https://github.com/danswett/agent-bridge-home-assistant/pull/10),
after their own admission/revalidation; both are presently open prototypes,
not an installed SDK. Do not duplicate them or substitute a library's default suite.
`aiohttp` supplies asynchronous WSS; `cryptography` supplies the existing primitives.

Match [channel framing](../protocol/v1-sessions.md#channel-framing) exactly:
P-256 SEC1 point validation, raw ECDH, AES-GCM nonce layout and SHA-256;
the existing prologue binds both device ids and the fresh random channel id.
Verify the host's full public key against approved trust state before revealing
the service static key; its routing hash or relay introduction is insufficient.
Serialize encryption and physical sends together. An uncertain encrypted send,
bad tag, wrong pin or invalid frame ends that channel; never reuse spent nonces.
Implement XX first; IK and negotiated bounded deflate are separate compatible
optimizations, not prerequisites and never permission to downgrade trust.

Relay admission uses a key-bound scoped relay credential for this installation,
account and selected host, not a borrowed person/account or session-agent token.
Admission only routes; the host resolves the full authenticated key and checks
the current grant on every request and outgoing event. An HA user id, kind string,
MQTT event or button press supplies no Slipgates authority.

Keep the service P-256 private key in protected persistent HA-side storage.
Proposed storage implementation: an encrypted record with an independent
OS-keystore wrapping key; if that provider is unavailable, setup refuses rather
than silently writing plaintext. Ordinary HA `.storage` is not claimed encrypted.
Never put private keys or credentials in entities, attributes, diagnostics, logs,
card configuration or exported backups. Scoped access credentials stay in memory;
renewal proves the same service key and cannot extend the approved host grant.
Persist account/generation and full approved host keys in that encrypted record;
keep its generation floor beside the wrapping key, outside the restorable record.
Host-side durable revocation remains authoritative; unreadable state fails closed.
Restore/key loss requires fresh person approval; rotation revokes the old grant.
Whole-HA/keystore rollback cannot be detected from that restored copy alone:
recovery must revoke/re-enroll at the host, never silently reactivate the backup.
HA administrators or malicious code inside HA can still read live decrypted data.

Reconnect uses bounded exponential backoff with jitter (proposed 1-30 seconds),
one attempt per entry, canceled on unload. Recheck trust, expiry and revocation
before opening a new channel; authenticate again and request a fresh snapshot.
Wrong key, revoked grant or expired enrollment yields explicit unavailable/repair
status, not a retry using a person key, automatic pairing or a legacy HA fallback.

## Pairing and authority

[#1418](https://github.com/danswett/agent-bridge/issues/1418) owns S6a and is still
open. This note consumes its future approved non-person enrollment contract;
it does not add a `connector` roster kind or authorize production issuance.
Current [devices](../protocol/devices.md) parsing does not admit that service kind.
Until S6a, scoped admission and enforcing mixed-fleet support land, use synthetic
fixtures/disposable hosts only; account sign-in is never an enrollment shortcut.

The proposed HA config flow generates its own key and starts the shared mutual,
transcript-bound code exchange. A separately paired person device approves the
exact full service key, account, selected host, read scope and grant expiry.
Apply [enrollment limits](../protocol/devices.md#enrollment-values): five-minute
attempt, mutual codes, constant-time comparison and persisted lockouts.
Wrong, expired or replayed codes cannot enroll; wrong-account and canceled attempts
discard pending state. Codes, expiry and lockouts are protocol rules, not HA helpers.
Revocation closes standing channels/subscriptions; a restored grant never reactivates.

The first grant is **read-only, one selected host**:
`hello`, `sessions.list`, `session.get`, `sessions.subscribe`, and nothing else,
under [connector-read](../protocol/connector-read.md). No service launch or approval
authority exists; neither do service reply, answer, stop, transfer, trust, update,
push, usage, attachment, roster or credential-management rights. Unknown ops deny.
Fleet registry reads and usage/audit profiles need separately reviewed grants;
feature advertisement never widens the four-operation read profile.

A companion card offers actions only by opening a **separately paired person
client** at the exact host/session/question. That UI uses its own key/channel and
ordinary host-verified person confirmations; the Python service never proxies
person commands, stores a person key or trusts an HA assertion that someone clicked.
Until that path is implemented, controls stay visibly read-only with the reason.
Person reply/attachment, answer, stop, launch, update, pairing and transfer keep
their normal operations, limits, current-question checks and explicit outcomes.
An unconfirmed action is not retried blindly. Agents never answer trust/permissions.

## Subscription-driven entities and cards

Use [the parity inventory](https://github.com/danswett/slipgates-fleet/blob/main/docs/ha-retirement-parity.md)
row numbers below and [parity.md](../parity.md) for detailed behavior.
These are required target views, not claims that the initial service grant covers
them all. Build an HA push coordinator (`should_poll = false`), not polling.
Entity keys derive from enrolled identity and current protocol session identity;
never keep session/node addresses or a named machine roster in configuration.
`fleet.find` remains the canonical name lookup, with verified fresh records and
person-confirmed machine pins; no guessed, unverified or ambiguous target is routed.

| Card role | Entities/view and parity rows | Subscription source and authority |
|---|---|---|
| status | Machine-online binary sensor, session-count/status and time-in-state sensors; rows 2, 3, 15, 30 | Presence plus host `sessions.subscribe`; future scoped registry feed for fleet aggregation; forget opens person UI |
| session | Session state, agent/model, compactions, background/subagent summary and agent-started marker; rows 3, 19, 27 | `session.upsert`/`session.remove`; trusted actor fields only, never infer identity from text |
| activity | Latest response, tool/reasoning details and freshness; rows 3, 18, 27 | Session events; bounded `session.get` on explicit detail-open, not a timer or relay metadata |
| choices | Pending-question view, complete named form fields and refusal reason; rows 5, 17, 27 | Session events; answers open person UI; no twelve-select workaround or unsafe/truncated approval substitution |
| reply | Reply composer, attachments, stop and delivery outcome; rows 1, 4, 29 | Session events update availability; sending/stop and correlated outcomes live in person UI only |
| launch | Machine choices, resume/transfer and request-correlated launch result; rows 6, 7, 11, 22, 23 | Person UI reads `launch.options`, uses ordinary launch/transfer ops and subscribed session results; no guessed defaults or background options polling |
| usage | Provider/account meters, freshness and errors; row 9 | Person-authorized `usage.subscribe`; backend service cannot use this under its initial read grant |

Update availability/version and check/install controls (row 8) use the person
client's `update.subscribe` and normal update ops; pairing/transfer status (11)
uses S6a's approved events; audit/attribution view (12, 19) awaits its approved
read contract. No invented subscribe verb hides a missing implementation.
Question/turn delivery and withdrawal (16, 28) stay on the paired person client's
normal notification path, not HA mobile-app action events or a service push grant.
Owner paging and alerting remain parked; this slice builds neither.
Rows 10/31 intentionally drop helper creation/detail toggles. Rows 20/21/34 are
host credential/migration work, not HA entities; 14/32 have no retained S0 items,
13 excludes Apple hosts only, and 33's home automation is untouched.
Rows 24-26 belong to independent fleet consumers, never this optional component.

On `sessions.subscribe`, correlate the response, buffer the replacement snapshot
and publish atomically only at `sessions.synced`, including discovery-held state.
Require channel event sequence 1 first, then consecutive increments, including
across re-subscription. A gap or rejected identity-bearing event invalidates the
view until a fresh snapshot; reconnect never resumes an old sequence.
Disconnect/overflow marks entities unavailable, not zero sessions or completed work.
Apply ordinary malformed-record rules; escape names as untrusted display text,
Markdown without raw HTML, and never evaluate templates from session content.
Keep decrypted activity bounded in memory, not persisted as Recorder history:
entity state/attributes hold only minimal status; the authenticated card view
reads current permitted detail without exporting tokens or transcripts to logs.

`registry-subscribe`/`registry-changed` are **not implemented**: registry `get`
alone cannot prove subscription parity. Do not replace the missing feed with
polling. Later scoped registry readers must verify host signatures, epoch/sequence,
60-second freshness and removals; relay metadata never carries prompt/detail text.

## Proposed build slices and proof

Each estimate includes focused failing-first tests; split again before exceeding
400 changed lines (prefer 100-200). No slice grants production access by itself.
All live proofs below use disposable HA/hosts and test identities, not the owner.
S6a owns enrollment rules; the existing HA repository retains implementation.

| Slice | Changed lines | Predecessor | Live proof |
|---|---|---|---|
| B1 Python vector adoption | 100-180 | HA PRs 9/10 admitted and current protocol revalidated | Python exchanges pinned XX frames with a disposable C# peer; wrong pin/off-curve/tamper refuse |
| B2 Async channel driver | 150-200 | B1 | Disposable relay reconnect/send-cancellation/slow-consumer run; no nonce reuse or plaintext leak |
| B3 Config-flow enrollment adapter | 150-200 | S6a and scoped admission implemented; B2 | Pair to one disposable host; wrong/expired/replayed code and wrong account refuse; other hosts denied |
| B4 Protected storage/lifecycle | 120-200 | B3 | Restart, revoke, rotate and restore disposable HA; missing keystore and restored grant fail closed |
| B5 Session entity coordinator | 150-200 | B4 | Snapshot, mutation, sequence-1 gap, removal and host restart reconcile; disconnect never shows empty success |
| B6 One card role per PR, seven PRs | 100-200 | B5; each role's approved read source | Render each role on disposable HA; compare its mapped rows, escaping and missing-capability behavior |
| B7 Paired-person action links | 100-180 | S4/S6a person UI; B6 | Person UI completes an allowed action; service-key attempts refuse, stale question cannot be answered |
| B8 Fleet registry view | 150-200 | Scoped registry grant, signed change feed; B5 | Two disposable hosts join/leave; stale/forged/ambiguous lookup never routes; zero polling |
| B9 Packaging and conformance evidence | 100-200 | B1-B8 and each mapped capability built | Install/unload/remove HA while another client messages/approves; nothing else affected; #1407 results per row |

Both model families must read this whole note at the exact review head.
Outstanding work is the build table and S6a/grant/feed dependencies, not more
design text hidden behind a time gate. Missing drivers or unbuilt capabilities
remain `missing`/`notSupported`, never a passing ClientConformance result.
