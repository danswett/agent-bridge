# Person host CLI transport and caller contract (C1)

Contract and review vectors for [#2489](https://github.com/danswett/agent-bridge/issues/2489),
the C1 cut in [the S4 inventory](host-cli-s4-evidence.md).
**Sensitive.** Docs only: no new executable command, operation grant, person
credential, device kind or claim of CLI action parity.
Source baseline: `8639839ca352c4430739ddfd3cf770d9a67f5219`.

## Reusable transport, not an arbitrary-operation command

| Existing code | Reuse and limit |
| --- | --- |
| [LocalEndpoint.For](../../src/AgentBridge.Local/LocalEndpoint.cs) | Derives the selected local profile's pipe/socket; not an account-wide machine selector. |
| [LocalClient](../../src/AgentBridge.Local/LocalClient.cs) | Connect, `HelloAsync`, typed `CallAsync`, host refusals, cancellation and timeouts. Connect/hello does not prove person intent. |
| [LocalChannel](../../src/AgentBridge.Local/LocalChannel.cs) | Framing, per-channel request IDs, progress, responses and events. Progress is not a final outcome; a timeout is not proof nothing was sent. |
| [PeerPolicy](../../src/AgentBridge.Local/PeerPolicy.cs) | Windows same-user/elevation checks and connection-time process ancestry; not a new person credential. |
| [CallerGuard](../../src/AgentBridge.Host/CallerGuard.cs) | Recognizes agents before App/HostCli, checks complete ancestry and the exact installed image. Ordinary classifications may be cached for five seconds. |
| [Program](../../src/AgentBridge.Host/Program.cs), [AgentCommands](../../src/AgentBridge.Host/AgentCommands.cs) | Existing narrow rename/adopt/revoke callers demonstrate connect, hello and typed requests; migration/rollback use the narrow restart exception. |
| [LocalApi](../../src/AgentBridge.Host/LocalApi.cs) | Authoritative per-operation caller checks; never replace these with client-side trust or capability display. |

Reuse these libraries behind named, validated verbs in later cuts, not an
`op <arbitrary-string> <json>` escape hatch or a raw injection/HA fallback.
The existing `sessions`, `get`, `watch` and `usage` commands read sources directly;
they neither authenticate a person nor establish an anti-agent transcript boundary.
Do not invoke provider usage collectors merely to qualify this contract.

## Person invocation and fresh ancestry

A future person-action wrapper runs in the person's own terminal, outside an agent
session. Before sending, it must fail closed on agent ancestry, a session marker,
unknown/incomplete ancestry, an invalid profile or an unsupported operation.
An absent marker, a familiar filename, OS file access and `hello` capabilities are
not positive proof of person invocation.

Use a new local connection for an invocation: the host captures the connecting
process chain at accept time, with process IDs and start times. Request a fresh
caller decision at the action boundary; do not inherit an earlier read's decision.
`Classify(fresh: true)` refreshes classification against known agents, not the
connection's captured OS ancestry. It cannot alone make an old chain fresh.
An action implementation must preserve complete, current ancestry evidence.
If that evidence cannot be established, refuse rather than fall back to `Other`.
Unix socket ancestry is currently unknown; connectivity is not HostCli eligibility.

This is a requirement for later person-action adapters, not a claim that every
current session op already enforces it. Today's `LocalApi` rejects Agent callers
for ordinary acting ops; `Other` is not uniformly refused there. Closing that
exposure for a new wrapper needs its own bounded implementation and tests.
Same-user ancestry checks remain safety rails, not a malware isolation boundary.

The host rechecks its own caller, capability, read-only/G5, target and delivery
guards. A person's terminal does not override them. An agent's existing
`agent.hello` principal is never promoted to person authority, even with reply scope.
Current adoption/credential commands keep their existing separate contract;
C1 does not mint, claim, broaden or borrow a credential.

## Operation and transport allow-list

This list maps routes, not new runnable CLI syntax. Only existing concrete verbs
are executable now; each proposed wrapper below requires its named follow-up.
Reject any unlisted operation in a future wrapper before connecting or sending.

| Operation / purpose | Route and disposition |
| --- | --- |
| `hello`, `sessions.list`, `session.get`, `sessions.subscribe`, `usage.*` reads | Existing local pipe reads; direct-source CLI reads remain distinct. A read grants no action or person authority. |
| `session.rename`, `agent.adopt`, `agent.revoke` | Existing narrow Windows HostCli/App paths, with fresh caller checks; preserve their validation and scope, do not generalize them. |
| `host.restart` | Existing HostCli migration/rollback exception only; not permission for `host.stop`, settings, sign-in or account access. |
| `session.stop` | Pipe-capable person session action, not host management. A CLI adapter is C2, retaining the stop reconciliation and its gaps. |
| Ordinary `session.answer` | Pipe-capable; C3 and C5 must separately implement approval/form interactions with exact question identity and existing refusal fences. |
| `session.reply`, `attachment.start`, `attachment.chunk`, `attachment.discard` | Pipe-capable person reply/staging; C11/C12 retain composer/question, ownership, size/hash and confirmation guards. Not an approval bypass. |
| Escaped Approve / folder-trust confirmation | No raw local-pipe confirmation forwarding; verified person-device route or retained gap belongs to C4. Ordinary Deny is not an escaped confirmation grant. |
| `manage.*`, `update.*`, `push.*` | Relay-only boundaries; a person CLI transport/identity is not established by C1. Local `host.update.report`/`depart` remain App-only, not a substitute. |
| Account machine Forget | Relay roster person-device route only (`app`, `pwa`, `phone`), with its own account/offline/confirmation checks. Host/agent/test identities cannot substitute; C7 retains the CLI route gap. |
| Other ops, including launch, history and focus | No new CLI surface in C1. Existing clients retain their own contracts; never let an agent assert person focus to suppress notifications. |

Do not obtain `host.relay.account`, copy a host key/token, impersonate the App or
invent a `cli` device kind to fill relay-only gaps. Host sign-in is not person-device
enrolment. No app-only host-management privilege follows from being HostCli.

## Stable target, request and outcome

Pin the selected local profile and `hello` machine ID/run (`machine.instance`),
then the full session ID and current question ID where required. Names, titles,
list positions and an old notice are not identities. Unknown identity or a changed
host run requires explicit re-resolution, never automatic retargeting.
Preserve the host's missing/stale/answered outcomes; do not answer a replacement.

For answer/reply, keep the domain `request` UUID with the exact selected target
and payload for that intent. It is not LocalChannel's numeric `id`, which is
per connection. A new selection or answer is a new intent, not reuse of that UUID.
Any recovery using the same UUID must obey the operation's retained-outcome and
host-run rules in [v1 sessions](../protocol/v1-sessions.md); never assume a new
host remembers a reply. Do not generate a fresh UUID to retry ambiguous typing.
Render `delivered`, `refused`, `stale`, `busy` and `unconfirmed` distinctly where
the operation defines them. Show host reasons; refusal/unknown is not success.
No automatic resend, alternative transport or direct-injector fallback follows
from progress, disconnect or an unconfirmed outcome.

## Acceptance vectors (reviewed, not executed)

Symbols `S1`, `Q1`, `Q2`, `R1` and `R2` are fixture labels, not wire IDs or receipts.
These are requirements for future adapters, not assertions that missing verbs ship.

| Vector | Input / condition | Required observation |
| --- | --- | --- |
| P1 | Person terminal; exact host image; fresh complete non-agent ancestry; permitted local action | Eligible for that action's later adapter only; host guards still decide. |
| P2 | Agent launches the genuine host CLI as a child | Refused before person-action send; no use of App/HostCli identity or credential. |
| P3 | No process chain, or only part readable | Unknown ancestry; non-success with reason, no action and no `Other` fallback. |
| P4 | Unknown Unix socket ancestry or copied/renamed host executable | No person-action eligibility inferred from socket access or filename. |
| P5 | Agent discovered after an earlier read/hello | Fresh action classification refuses; cached read eligibility cannot authorize it. |
| P6 | Agent session marker present; marker absent but ancestry unknown | Neither permits a person action; absence is not evidence. |
| T1 | Different OS user/elevation, missing host, invalid profile | Explicit connection/profile failure, no action or default-profile substitution. |
| T2 | Unknown op or app-only `host.settings`/`host.relay.account` requested | Refused; no arbitrary-op command or relay credential returned. |
| T3 | Local request for update, remote management, push or Forget | No pipe bypass; report missing supported person-device CLI route. |
| T4 | Escaped Approve/trust payload supplied as an ordinary answer | No raw confirmation forwarding; preserve C4's required verification/gap. |
| I1 | Read S1/Q1, then Q1 answered or replaced by Q2 | Explicit answered/stale treatment, no send to Q2 or another session. |
| I2 | Host run, machine or profile differs after reconnect | Re-resolve explicitly; no reuse of prior selection/permission as current. |
| I3 | R1 used for S1/Q1; payload/target changes | Do not reuse R1 for new intent; require a fresh selection and R2. |
| I4 | Progress received; response lost or `unconfirmed` | Unknown/explicit unconfirmed, not delivered; no automatic retry with a new UUID. |
| G1 | Capability absent, G5/read-only, truncated/at-machine question | Preserve refusal; no raw typing, HA fallback or agent approval. |

The future wrapper runs only on its invoking machine, with no designated fleet
node or new daemon. Closing it/disconnecting provides no background delivery proof.
Target/draft state is invocation-local; existing host outcome retention remains
authoritative, not a new shared credential or queued-delivery store.

Review checks P1-P6, T1-T4, I1-I4 and G1 plus every allow-list row.
No executable behavior changes, so no artificial red test is claimed.
The existing hosted `docs-links` suite is the link check; local tests/builds are
not run without clearance. Both model families read each changed file in full.
C1 closes this contract cut only, not the implementation cuts or S4 live proof.
