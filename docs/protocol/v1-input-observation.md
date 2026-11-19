# Coordinator input observation v1

Contract for [the host dependency](https://github.com/danswett/agent-bridge/issues/2652)
of [the shadow classifier](https://github.com/danswett/slipgates-fleet/issues/724).
Slice 2a defines only the content-free contract and pure classifier
(`AgentBridge.Host.InputObservation`). It adds no producer, pipe operation,
capability, host wiring or permission. Those belong to slice 2b.

## Authoritative sources

For Copilot's visible input, the existing source is a screen read for the pinned
session process: [ReplyService.InputBoxAsync](../../src/AgentBridge.Host.Delivery/ReplyService.cs)
calls the injector's `ReadScreenAsync`, then
[CopilotInputBoxReader.Read](../../src/AgentBridge.Host.Delivery/Copilot/CopilotInputBox.cs).
It checks for a unique, complete, bottom-anchored box and its mode/footer.
Only a positively identified, safely readable main box can establish `occupied`
or `empty`; missing rows, ambiguous boxes, shell/vim refusals and incomplete reads
are not empty. A draft's raw `Problem`, `Content` and screen rows MUST NOT escape.
Session idle/working status and transcript silence prove neither input state.
Other agents' authoritative coordinator-input adapters remain **UNKNOWN**.

The current authoritative, live, session-correlated queued-receipt source is
**UNKNOWN**. [DeliveryTimeline.Typed](../../src/AgentBridge.Host.Delivery/DeliveryTimeline.cs)
records keys typed and whether a turn was working, not queue acceptance.
[DeliveryProgress](../../src/AgentBridge.Protocol/Sessions.cs) defines `queued`,
but a code's existence, `typed`, `working`, a send attempt or an old transcript
entry is not a live queued receipt. The reserved source code `queuedReceipt`
requires an independently validated receipt for this session, still pending and
fresh. Until a producer proves that source, it MUST report unknown, not queued.

## Projection and freshness

Using `ProtocolJson.Options`, the payload has only these fields:

| Field | Meaning |
|---|---|
| `session` | Canonical session UUID; an empty UUID cannot establish a positive state. |
| `source` | Closed code: `unknown`, `copilotScreen`, `queuedReceipt`. |
| `ageMs` | Nonnegative elapsed milliseconds since evidence was observed, rounded down; omitted when missing or future-dated. |
| `state` | `unknown`, `occupied`, `empty`, `queued`; never interchangeable. |
| `reason` | `uncertain`, `observed`, `queuedReceipt`, `stale`, `missing`, `ambiguous`, `refused`, `unavailable`. |

`Classify` accepts only identity, closed source/evidence codes and times, never
input text, paths, screen rows, exception details or free-form reason strings.
It performs no reads or effects and does not authenticate evidence supplied to it.
The eventual producer must bind the evidence to the requested live session and
establish source authority before calling it; caller assertions are not evidence.

Fresh positive `copilotScreen` evidence yields occupied or positively empty;
fresh validated `queuedReceipt` evidence yields queued, never empty.
All missing, ambiguous, refused, uncertain, unavailable or unrecognized evidence
yields unknown. Missing identity yields unknown/missing. Otherwise-positive
evidence with no observation time yields unknown/missing; a future time or
nonpositive freshness limit yields unknown/uncertain. Age greater than the
producer's explicit positive `maxAge` yields unknown/stale; equality is fresh.
Freshness is compared before millisecond rounding. Wrong/unknown source and
positive evidence combinations yield unknown/uncertain; unknown source codes
are projected as `unknown`. A producer must not refresh age on cached evidence.

Slice 2b's local read must retain the existing caller/credential gates; an
authorization refusal remains a refusal, never an empty observation. No send,
submit, clear, retry, repair or launch operation is authorized by this contract.
It implements neither a queue nor the unsupported-input-box gap.
