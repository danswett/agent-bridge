# Reply receipt lookup after exit 2

B1 [assignment](https://github.com/danswett/agent-bridge/issues/3098), under [agent messaging](https://github.com/danswett/agent-bridge/issues/728). Documentation only.
Evidence below is pinned to main `e776d16933d653443ed7a4f83fac6e0357ad77a6`.
**No new receipt query is built or authorized here.** Exit 2 remains unconfirmed,
not success or non-delivery. B2 must not automatically resend or fall back.

## 1. What record proves receipt, for how long, and to whom?

HA exposes the latest `reply_ack` on `sensor.<node>_activity`:
`{rid,outcome,at,sender?,duplicate?}` ([creation][ha-ack], [publication][ha-publish]). Matching `delivered` is the host's outcome, not proof the
agent acted on the message. The helper also requires a status transition unless
the session was already working; a duplicate always exits 2 ([helper][ha-helper]).
`unconfirmed` never establishes delivery. An ack cannot override exit 4 (unsent words) or exit 5 (no observed turn).

The connector keeps one latest ack and an ordinal rid log per target session:
**50 ids**, evicting by count, not time-to-live ([HA log][ha-log]).
The log survives a connector pause, not process restart; session retirement removes
it after any in-flight delivery ([retirement][ha-retire]). The ack is retained in HA publication but replaceable;
neither it nor HA history guarantees a receipt retention window. A late missing ack proves nothing.

The helper reads HA state with the configured non-admin agent bearer token
([helper][ha-helper], lines 29-35, 70-94). An authorized HA reader can see the
published slot; neither `sender` text nor that read authenticates per-rid ownership.
Owner/operator inspection uses existing HA permissions. No per-agent receipt-only read boundary is established here.

Relay `LiteAck` is `{status,code,request,killEpoch}`, inline to the sending host ([receiver][relay-receiver]), not a stored, queryable receipt.
Its `delivered` means the delivery interface reports typed: main also maps an
underlying `Unconfirmed` to typed ([delivery][relay-delivery], lines 58-62).
It does not prove a new agent turn. The sender checks the request id, waits 10 seconds and closes the channel ([sender][relay-sender]).
The **150-second**, **4096-id**, memory-only replay store holds ids, not outcomes;
a restart forgets it. Envelope lifetime is at most 120 seconds ([receiver][relay-receiver], lines 82-105; [envelope][envelope]).

The private owner-readable audit targets 30 days / 64 MiB, but `agent.send` records a local attempt UUID different from the relay request UUID
([audit][audit], [local send][local-send], lines 41-57). It is no rid lookup.
`DurableInbox` stores node/rid and delivery timestamps, but has no production callers or receipt endpoint in this snapshot ([inbox][inbox]).
Update (design audit, 2026-11-21): since this snapshot, [#3205](https://github.com/danswett/agent-bridge/pull/3205) wires `DurableInbox` into the HA connector's queued reply ingress (`HaConnector.Inbox.cs`); it is still not a receipt lookup.

## 2. Does a read-only query exist?

**No authenticated sender-scoped lookup by rid is defined on inspected main.**
Existing L3 [send](https://github.com/danswett/agent-bridge/issues/1351) and L4 [receive/ack](https://github.com/danswett/agent-bridge/issues/1352) surfaces:

| Surface | Current request and result; not a receipt lookup |
|---|---|
| Local pipe `agent.send` | `{machine,session,text}` -> `{outcome,code,request}` ([local send][local-send]). |
| Secure host channel `agent.lite.send` | `{envelope,text,killEpoch}` -> inline `LiteAck`; receiver checks same-account/pinned peer, signature and replay ([receiver][relay-receiver]). |
| HA state `GET /api/states/sensor.<node>_activity` | Latest `attributes.reply_ack` only, not a rid-indexed record ([helper][ha-helper]). |
| Relay `registry-put` / `registry-get`, `roster-get`, presence | Session snapshots/device records, not message outcomes ([relay dispatch][relay-dispatch]). |

Registry fields include `lastReplyAt`, but no rid/receipt binding ([allow-list][registry-fields], [evidence fields][session-evidence]).
The host serves `agent.lite.send`, not receipt-get ([host dispatch][host-dispatch]); timestamp, presence or working state is no substitute.

## 3. Idempotency and repeats

HA generates `rs-<UUID>` unless given `-Rid` (1-64 letters, digits, `.`, `_`, `-`).
Uniqueness must cover the target session's remembered history across senders: the log key is rid, **not sender**. Never reuse it for different content.
A remembered completed rid types nothing and republishes its prior outcome with
`duplicate:true`; an in-flight repeat types nothing and awaits the first outcome.
A refusal forgets the rid; eviction/restart ends this duplicate protection
([HA log][ha-log], [duplicate handling][ha-repeat]). The helper's duplicate exit 2
is not upgraded to success, even when the cached outcome says delivered.

Relay generates a fresh UUID per send attempt; replay scope is receiving-host
memory keyed by `peerDeviceId/request`. A remembered repeat returns
`refused/replay`, not the original outcome; full unexpired storage refuses `busy`
([sender][relay-sender], [receiver][relay-receiver]).
**HA and relay do not share a rid namespace or deduplication ledger.**
A cross-transport resend is a new delivery risk, not an idempotent retry.
`AnswerNotConfirmed` describes an answer already typed once, not a receipt API;
shared reply reasons redact drafts ([answer reason][answer-reason], [reply reasons][reply-reasons]).

## PROPOSED smallest contract -- NOT BUILT

PROPOSED: one authenticated read-only lookup with transport, target, rid and original attempt marker; no endpoint name is reserved. The host derives the
caller principal and verifies a recorded send binding, never trusts `sender` text.
An agent reads only rids it sent (D2); owner reads need separate authority. Return a bound original outcome, recording time, receiver epoch and expiry, or an
explicit unknown/forbidden result. Bind payload hash and attempt to prevent an old
ack satisfying a different send. A duplicate marker adds no delivery proof.
Never promote typed-only/unconfirmed evidence into confirmed session receipt.
No send, press, retry, subscription, queue or transport fallback is part of this read.
Retention must cover the whole retry window; unknown retention cannot satisfy it.

PROPOSED placement: serve on any authorized live host, with protected account-scoped receipt state in the relay service, not a named VM. If a node leaves, another host
may read retained state; unavailable/expired state returns unknown, never success.
No such receipt store, authorization rule or availability guarantee is built here.

## 4. Failure table and defaults

| Observation | Fail-closed conclusion and action |
|---|---|
| No ack (timeout, overwritten slot, offline/restarted host, expiry) | Unknown, not proof nothing arrived; stop automatic resend/fallback. |
| Ack refused, including relay replay/busy/off | Not confirmed; preserve refusal, never convert to delivered or switch transports automatically. |
| Ack unreadable, unauthorized, malformed, wrong rid/target/epoch | Unknown; do not broaden access, scrape another principal or assume absent. |
| Stale ack from an earlier try | Not this attempt; reject old `at`/attempt binding, including a prior same-rid refusal. The helper snapshots stale `at` before send ([helper][ha-helper], lines 76-94). |

**Defaults taken:** D1 none assumed; B2 treats exit 2 as final. D2 agent reads only receipts for rids it sent (PROPOSED constraint, not current HA enforcement).
D3 retention shorter than any retry window means no fallback; unknown retention
also fails closed. These defaults grant no retry or implementation authority.
Acceptance checklist: `ReceiptLookup_DesignNote_AnswersAllFourQuestions` was recorded RED before this note. Sensitive: Quality obtains two independent reader families
on the exact head; this note is not a review receipt or rollout of B2.

[ha-ack]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.HomeAssistant/HaConnector.Replies.cs#L129-L139
[ha-publish]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.HomeAssistant/HaConnector.cs#L873-L874
[ha-helper]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/tools/agents/reply-session.ps1#L1-L180
[ha-log]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.HomeAssistant/HaConnector.Replies.cs#L53-L105
[ha-retire]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.HomeAssistant/HaConnector.cs#L724-L726
[relay-receiver]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Host/Lite/LiteReceiver.cs#L11-L105
[relay-delivery]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Host/Lite/LiteDeliver.cs#L58-L62
[relay-sender]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Host/Lite/AgentLiteSender.cs#L35-L145
[envelope]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.AgentEnvelope/EnvelopeBody.cs#L54
[audit]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Host/AgentAuditLog.cs#L28-L44
[local-send]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Host/Lite/LiteAgentSend.cs#L7-L57
[inbox]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.HomeAssistant/DurableInbox.cs#L6-L69
[relay-dispatch]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Relay/RelayHub.cs#L310-L405
[registry-fields]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Relay/RosterFields.cs#L27-L28
[session-evidence]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Protocol/SessionEvidence.cs#L9-L10
[host-dispatch]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Host/SessionService.cs#L267-L268
[ha-repeat]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.HomeAssistant/HaConnector.Replies.cs#L413-L427
[answer-reason]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Host.Delivery/DeliveryReasons.cs#L10-L15
[reply-reasons]: https://github.com/danswett/agent-bridge/blob/e776d16933d653443ed7a4f83fac6e0357ad77a6/src/AgentBridge.Protocol/ReplyReasons.cs#L5-L30
