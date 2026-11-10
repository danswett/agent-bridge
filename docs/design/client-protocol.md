# One client protocol for Slipgates

**Status: proposed design, docs only (#1406), 2026-11-10 PT.** This is the common
frame for [#1396](https://github.com/danswett/agent-bridge/issues/1396), not a new
API, permission grant, live parity result or cutover authorization. Public
third-party API delivery remains parked in
[#1398](https://github.com/danswett/agent-bridge/issues/1398).

The PWA is not special. PWA, Windows, Mac, iOS, Android and Linux when shipped must
offer the same parity capabilities. An optional HA client and a future third-party
client use the **same operations, authentication and limits**, with no extra
privileges. A caller's authority, not its client brand, determines what may run.
Missing functionality is a recorded gap, never evidence of parity.

The inventory below maps all 34 rows of the approved
[retirement inventory](https://github.com/danswett/slipgates-fleet/blob/main/docs/ha-retirement-parity.md).
The epic is the public gate record. Apple *machines* are outside the current
retirement cohort; Mac and iOS *clients* are not excused by that decision. Native
Android and Linux clients adopt this contract when shipped, not as new build work
in this slice.

## Placement and state

* Each participating host serves the protocol for its own sessions. Clients run
  on the person's devices; the optional component runs in the HA installation.
  No collector, publisher or client is assigned to a named machine.
* Session truth, outcomes, credentials and policy remain with their authoritative
  host/device under the existing protection and retention rules. The relay holds
  protected account/roster state and the bounded, memory-only registry described
  in [fleet-session-registry.md](fleet-session-registry.md); it is not an approval
  authority or a store for decrypted session content.
* If a host leaves, its sessions become unavailable/stale, not empty or completed.
  Another live node can read the remaining fleet; it cannot impersonate the lost
  host or inherit its keys. Registry memory rebuilds from host snapshots after a
  relay restart. Client caches are disposable and never grant authority.
  Removing HA changes only that client's view, not messaging, pairing or approval
  delivery for the other clients.

## Caller authority

Use [Bridge Protocol v1](../protocol/v1-sessions.md) over the existing local pipe
or authenticated encrypted host channel. Relay admission/GitHub account membership
alone is not permission to act. Pairing, key verification, revocation and person
confirmation remain governed by [devices.md](../protocol/devices.md) and
[agent-principals.md](agent-principals.md); this note does not claim their pending
runtime enforcement is live.

| Caller | Available operations, subject to current host policy | Never inferred from the client |
|---|---|---|
| Person on a paired first-party client | Reads; question answers, approvals, replies, stop, launch/resume, updates, pairing, transfer and forget where advertised and authorized | A capability or device-kind string is not a grant; required person signatures/confirmations still apply |
| Person using an HA companion card or third-party UI | The same operations under the same person-device checks; no weaker confirmation or larger limits | An HA login, service event or assertion that a person clicked is not a Slipgates person signature |
| Session agent | Own-account typed reads on its local host after `agent.hello`; `agent.send` only with its reply scope; `session.launch` only after the scoped-launch implementation and owner sign-off | Never answer questions/approvals or folder trust, stop sessions, update/manage hosts, pair devices, transfer, mint scopes or impersonate the person |
| Unattended script | Only independently minted, revocable script-credential operations once implemented under [script-credentials.md](script-credentials.md) (#1414) | No borrowed session or person key; the separately reviewed dispatcher launch grant is not an ordinary agent right |
| Non-person HA component or third-party service | Read-only selected-host profile after approved service enrollment; no action grant from this note | No approval, answer, reply, launch, update, pairing approval or transfer by relabeling itself `app`/`pwa` |
| Unauthenticated, revoked, wrong-account or unattributed remote caller | No protected reads or actions; explicit refusal | Neither an empty successful result nor a privileged compatibility fallback |

The HA backend and a person using its card are different principals. To offer a
person action, the card must use a separately paired person-client channel/key and
the standard confirmation, not proxy it through the component's service key.
That is a requirement for #1419, **not an implemented enrollment mechanism**.
Until its enrollment/confirmation design is approved and implemented, that card
is read-only. The same restriction applies to a third-party service UI.

Agent tools are adapters, not a second API. They authenticate on the local host
pipe and use the operations/projections in
[agent-operations.md](../protocol/agent-operations.md). `agent.send` is the current
bounded own-agent messaging adapter; it does not give a service or person client
an agent credential. Agent launch uses `session.launch`, not an agent-only launch
verb, with the narrower checks in [agent-launch-scope.md](agent-launch-scope.md).
An agent `ask` tool requests a question for the person; it never answers one.
Its missing implementation stays a gap, not an exemption.

## Operation inventory

**Clients column:** `All` means PWA, Windows, Mac, iOS, Android and Linux when
shipped, plus the optional HA person-facing client and future third-party
person-facing clients when offered. Each must support the capability for an
authorized person. `Agent` means the narrower credential/projection above, not all
operations in that row. `Host` is infrastructure, not a new UI permission.
Intentional drops and unresolved inventory are explicit rather than invented ops.
Names below reuse operation contracts, not evidence that every operation is live.
Unimplemented work is marked **proposed**, **planned** or **gated**.

| Row | Parity capability | Common operation or explicit disposition | Required clients/callers |
|---|---|---|---|
| 1 | Whole-message reply and acknowledgement | `session.reply`; agents use scoped `agent.send`, preserving its narrower message limit/outcomes | All; Agent |
| 2 | Machine presence | Relay `presence` and `known` roster, with freshness/offline state | All; Agent via host |
| 3 | Fleet sessions, status and name lookup | `registry-get`; **planned** `registry-subscribe`/`registry-changed`; `fleet.list`/`fleet.find` agent projections; full detail via `session.get` | All; Agent |
| 4 | End session | `session.stop`; host owns idle/confirmation policy and target-process checks | All; never Agent |
| 5 | Questions and permission approvals | `session.answer`, question id and typed fields; approval uses the same op with the host's additional person checks | All; never Agent/service |
| 6 | Launch, resume and tuning | `session.launch`; scoped agent launch is gated by #1404/#1413 and the owner-approved design | All; Agent only within its launch grant |
| 7 | Launch choices and outcomes | `launch.options`; request-correlated launch result/readback, not registry text or a guessed model | All; scoped Agent |
| 8 | Update availability, check and install | `update.get`, `update.subscribe`, `update.check`, `update.install`; local app uses its existing updater | All, person-only install |
| 9 | Provider allowances and extra accounts | `usage.get`, `usage.subscribe`, `shared` where offered; preserve source, account, freshness and errors | All; Agent reads |
| 10 | Detailed-activity toggle | Intentional drop: no relay toggle; not a new setting operation | None; documented exclusion |
| 11 | Matching-code pairing and session transfer | Signed-roster enrollment under #55/#1418; `session.transfer` and `session.launch` with `transfer` | All; person approves, hosts execute |
| 12 | Credentials, scopes and action audit | Existing authentication; audit view/read is proposed in #1401, issuance remains person/host-only | All show authorized audit; Host enforces |
| 13 | Apple hosts in retirement cohort | Out for now by owner decision; no extra operation and no Mac/iOS client exemption | Host exclusion only |
| 14 | Unlisted owner-built HA dependencies | S0 inventory remains unresolved until each item is mapped or explicitly retired; no generic execute-automation API | Owner decision; evidence remains missing |
| 15 | Forget machine and upkeep | Roster `forget`/`forget-result` for presence cleanup; signed-roster removal for trust revocation, never confused with forget | All; person only |
| 16 | Question and finished-turn notifications | `push.subscribe`, `push.unsubscribe`, `push.list` and protocol notification events; tap opens the current question/turn | All notification-capable clients |
| 17 | Multi-field questions | `session.answer` fields by name; retire HA's twelve-dropdown encoding, not the form capability | All; no agent answers |
| 18 | MCP list, read and ask | `sessions.list`, `session.get`, `sessions.subscribe`; fleet projections above; ask creation remains proposed (#45) | Agent adapters; All use the same permitted operations |
| 19 | Agent-started marker and write attribution | Session `driver`/`driverPrincipal` plus proposed audit read; actor comes from verified credentials, never request text | All display; Host attributes |
| 20 | Agent credential distribution | Host mints session credentials at launch; separate script issuance in #1414; no client operation that reads/export secrets | Host/person administration only |
| 21 | Legacy daemon and hook migration | Explicit migration/rollback procedure, not a remote agent management permission | Host, person-operated migration |
| 22 | Launch-helper launch ledger and idle end | `launch.options`, `session.launch`, correlated outcomes, `session.stop` and proposed audit read | Person CLI/client; no unattended person impersonation |
| 23 | Dispatcher launch and registration check | Same launch operations under the separate fleet grant, then the verified fleet registry | Credentialed dispatcher; gated, not ordinary Agent |
| 24 | Live-board roster snapshot | Same **planned registry subscription** and verified typed projection; no HA state scan or fixed collector machine | Client/service within read grant |
| 25 | Pump wake | Scoped `agent.send` using the canonical lookup, not an HA reply script | Credentialed agent/script once supported |
| 26 | Messenger lookup, busy check and send | `fleet.find`, typed status, scoped `agent.send`; preserve refusal/uncertainty instead of guessing delivery | Credentialed agent/script once supported |
| 27 | Dashboard and cards | `sessions.subscribe`, `session.get`, usage/update subscriptions and ordinary action ops above | All, including optional HA presentation |
| 28 | Notification buttons and withdrawal | Same push events, question identity and `session.answer`; withdrawal when answered/replaced/ended | All notification-capable clients |
| 29 | Reply images/files | `attachment.start`, `attachment.chunk`, `attachment.discard`, blob path when offered, then `session.reply` | All within attachment grant; no HA image-store side door |
| 30 | Cross-machine session list | Same registry operations as row 3, not an additional publication authority | All; Agent via host |
| 31 | HA helper creation/liveness repair | HA-specific helpers drop; presence freshness remains row 2; no remote helper-creation op | Host/client presentation, no new grant |
| 32 | Owner-built scripts/automations/helpers | Inventory-only until purpose and replacement/drop are approved; nothing runs arbitrary HA services | Owner decision; missing evidence stays visible |
| 33 | Non-fleet home automation | Outside Slipgates scope; leave alone; an undiscovered fleet dependency reopens S0 | None unless S0 finds a dependency |
| 34 | Per-machine agent HA token | Replace with row 20 credentials only after verified migration; no token-export operation | Host/person administration only |

Question conformance includes free text, single/multi-select, empty selection where
allowed, multi-field forms and "Other" only where the agent supports it. Cancel
and unsupported agent input modes remain explicit implementation gaps, not
silently successful answers. The host's answer-at-machine/escaped-approval rules
cannot be weakened for a notification, HA card or third-party renderer.

`fleet.find` is the canonical lookup: exact name, person-confirmed machine pin,
fresh verified record and the documented result precedence. No static session
addresses or machine rosters belong in clients or fleet configuration. A title in
`workspace.yaml` is not a verified fleet name. Unverified, ambiguous, incomplete,
offline or unpinned results never become routable success. Names are untrusted
display content; typed agent projections do not echo them.

Detailed prompt, activity, history, model and path data stays on authorized
encrypted host reads; it must not be added to relay registry metadata to fill a
UI gap. Non-person reads remain limited by
[connector-read.md](../protocol/connector-read.md), not everything a person's
`session.get` could return.

## Capability negotiation

**Existing:** host `hello` returns protocol version and caller-filtered
`capabilities`; `hello.changed` asks the client to refresh them. Capabilities are
features, **not permission grants**.
Only `registry-put` and `registry-get` are implemented in the relay. Registry
reads are opt-in through `registry-get`, not a new relay-hello flag.

**Planned registry change feed:** `registry-subscribe` and `registry-changed`
are not implemented; [roster.md](../protocol/roster.md) records them as later
work (registry design slice 2b). The subscription portions of rows 3, 24 and 30
and the optional-HA target depend on that future feed, not current
`registry-get` responses.

**Proposed additive client announcement (implementation after design review):**
the encrypted host `hello` request gains optional
`client: { v: 1, capabilities: [<capability names from the inventory>] }`.
The response acknowledges the announcement version and accepted names. This is
per channel, discarded on disconnect and resent on reconnect; it carries no
account, principal, credential or authority. Stable names and the acknowledgement
wire shape must be pinned in shared vectors before any client sends it.
Do not add this object to the relay's admission hello or claim it exists today.

Effective UX is the intersection of implemented client features, host features,
caller grants, platform support and current policy. The host rechecks authority
for every command; a client claiming all features gains no rights. Unknown
additive capability names are ignored, never used to authorize an operation.
An unsupported protocol/announcement version gets an explicit incompatibility,
not an invented supported set.

A legacy client omitting the announcement has **unknown** support, not full
support. Existing compatible reads continue, but that client cannot be certified
complete by silence. A client lacking a required capability shows a named gap.
For an unknown question/unsafe approval shape it shows read-only status and the
host's reason; it never substitutes a plain reply, automatic choice or truncated
approval. No operation is rerouted through HA to hide a capability gap.

The relay forwards encrypted host traffic without interpreting the announcement.
Its registry allow-list and opt-in behavior stay unchanged. Push starts only
after an authorized explicit subscription; notification support claims do not
enroll a destination or grant answer rights.

| Client/platform limit | Equal-capability behavior required |
|---|---|
| PWA, including iOS Web Push without option buttons | Push opens the exact current question in the client; the full form/approval is rendered there and answered normally |
| Windows notification buttons | Use the same question id, person checks and first-answer-wins rule; opening the card remains available |
| Mac/iOS native background delivery not yet proved | Record missing coverage; implement/test platform push or the approved equivalent, never mark parity from an app-open-only test |
| Android native client not yet shipped | Browser PWA results are PWA results, not native Android certification; native client later passes the same suite |
| Linux client later, notification services vary | Same protocol/UI capabilities when shipped; a missing notifier is a visible gap until a tested equivalent or explicit waiver |
| HA/third-party service has no person key | Read-only service view; open separately paired person-client UI for actions, with no transfer of the service credential into person authority |

## Conformance and retirement

The single **ClientConformance suite (#1407)** is the required certification
runner, planned in `tools/AgentBridge.Rehearsal/ClientConformance.cs`, with client
drivers rather than separate definitions of parity. It is not implemented by
this note. Existing protocol vectors and connector-read fixtures are inputs,
not substitutes for client/UI/live evidence.

For each named client/version and inventory capability it emits exactly one
result: `pass`, `fail`, `notSupported` or `missing`, plus protocol version,
implementation head, run identifier, reason and an evidence link. A live step
needs a recorded run; an absent driver, skipped step, unavailable machine or
missing evidence yields `missing`. Conflicting/duplicate results fail validation.
`notSupported` never passes a required row; only an explicit approved exclusion
or owner waiver makes a row non-required, with that decision linked. Host-only,
intentional-drop and unresolved-inventory rows retain their explicit disposition.

Run the same cases for each shipped required client, the HA client when offered
and third-party implementations before claiming conformance. Include wrong
account, unauthenticated/revoked key, service-as-person, forged actor, over-limit,
duplicate request, stale/replaced question, simultaneous answers, unsafe/truncated
approval, host restart, subscription gap, lost outcome and unsupported capability.
Assertions pin both successful outcomes and refusals, including no extra
privileges for HA, agents or third parties.

Wire limits come from the operation specs, not the renderer: e.g. launch prompts
remain 16 KiB UTF-8 (20 KiB total request), agent messaging retains its 4,096
UTF-16-unit limit, and registry snapshots retain 64 sessions/16 KiB records with
24 KiB frames and one get per two seconds. Attachment, reply, update and rate
limits remain their existing contracts. Retries reuse documented request identity
and outcome rules; an unconfirmed outcome is never "not sent" or permission to
duplicate a side effect. No HA-specific cap or privileged bypass is added.

**Current blocker:** registry publication is not yet live. Publisher core #1791
is merged, but host wiring is later work and
[fleet #238](https://github.com/danswett/slipgates-fleet/issues/238) remains open.
HA remains the explicit rollback until the **24-hour dual run** in
[fleet-session-registry.md](fleet-session-registry.md), slice 11, passes: at most
5 seconds lag, zero missing sessions, all person-set names resolved, with the
signing/reader/pinning prerequisites met. Merged read code is not that evidence.

The broader #1396 gate also remains: evidence per required row/client, zero HA
fallbacks over 500 messages / 50 launches / 30 approvals / 20 stops in one run,
HA-off rehearsal on one machine then the retirement cohort exercising every row
three times, and owner sign-off. Counters need named sources; missing launch,
approval or stop counters are blockers. Optional HA client use after inversion
is not a legacy fallback and must be distinguished in the evidence.
This PR does not disable HA, remove credentials or authorize removal.

## HA connector changes

[ha-connector.md](../ha-connector.md) now distinguishes the optional-client target
from the retained compatibility implementation. #1419 must replace host-private
MQTT/service action ingress with ordinary authenticated client operations, use
subscription-driven entities/cards, preserve verified snapshot/gap recovery,
and keep service reads separate from person actions. It must not restore a
permanent host publisher, use HA user ids as Slipgates person identity, or turn a
mobile-app event into an approval signature.

Keep all compatibility fixtures, credentials and G5/G6/G7/owner-only management
guards until independently verified reversible cutover. Removal needs later
bounded PRs and the retirement gate. #1418 owns non-person enrollment/transfer,
#1419 owns Python channel/key storage/reconnect and HA presentation; neither may
grant more than the caller table. #1407 proves the same rows for the HA client.

## Decisions and open questions

* Engineering: pin announcement capability names, version/acknowledgement shape
  and compatibility vectors; choose implementation slices of at most 400 lines.
* Engineering/Trust: #1418/#1419 must specify non-person enrollment/revocation
  and the separately paired person UI path, without giving unattended components
  a person credential. Until then the component remains read-only.
* Engineering: reconcile the inventory's working-session refusal expectation
  with existing `session.stop` two-step non-idle confirmation. No policy change
  is authorized here; the suite must test the approved host rule, not invent one.
* Engineering: link signing, publisher wiring and reader acceptance evidence;
  keep fleet #238 and the slice-11 dual run open until proved.
* Owner, through the existing decision process: material enrollment/authority
  changes, launch-design choices, unresolved S0 items, explicit client waivers
  and final retirement sign-off still need recorded decisions. This note grants
  none of them and does not ask an agent to answer trust or permission prompts.

Owner paging/alerting is parked and excluded. Existing question/turn notification
parity above does not authorize an alarm, escalation path or owner page.
