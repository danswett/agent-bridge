# Agent principals: session-to-session messaging, signed by hosts, and cross-account federation (#519)

**Status: design investigation, not built.** No product code is changed by this note. Next
steps, from #519: an independent security review, then a discussion with the owner. Nothing is
implemented before the owner approves it. It refines design.md
[§5.5](../design.md#55-command-envelope) and [§5.6](../design.md#56-authorization). Where they
disagree, this note is the newer reasoning, and design.md will be updated once the decisions are
made. It builds on the signed device roster proposed for #55 in PR #391,
[`docs/design/device-approval.md`](https://github.com/danswett/agent-bridge/blob/ed9ca862f4cbffc08dc754f9cccd64cb779f00b0/docs/design/device-approval.md)
(revision 2, head `ed9ca862`, not yet merged). In this note, "#391 §n" means a section of that
note, and a bare "§n" means a section of this one.

## Contents

1. [Goals, non-goals and the owner's decisions](#1-goals-non-goals-and-the-owners-decisions)
2. [Where things stand](#2-where-things-stand)
3. [Principal model](#3-principal-model)
4. [The signed agent command](#4-the-signed-agent-command)
5. [Policy](#5-policy)
6. [Display and attribution](#6-display-and-attribution)
7. [Cross-account federation](#7-cross-account-federation)
8. [Threat model](#8-threat-model)
9. [Phasing and dependencies](#9-phasing-and-dependencies)
10. [Open questions for the owner](#10-open-questions-for-the-owner)

---

## 1. Goals, non-goals and the owner's decisions

### 1.1 The owner's decisions (quoted from #519, 2026-11-03, 14:30 PT)

> Sessions must be able to message each other through the **native** protocol and relay,
> attributed to the sending agent. The current workarounds don't meet that: GitHub comments, and
> Home Assistant entity presses made with whichever token is at hand.
>
> **Direction:** full **host-signed agent principals**. An agent asks its local host to act. The
> host signs `{agent, session, machine, action, target, nonce, expiry}` with its device key. The
> target host verifies the signature against the device roster (#55), applies its policy, then
> records and shows the message as agent-driven ("from <session> on <machine>"; `driver=agent`,
> `driverPrincipal`, and #489's `userMessageFrom`).
>
> 1. **Scope:** an agent may **read, reply and launch**. It may **never answer approvals or folder
>    trust**.
> 2. **Reach:** any session on the owner's account, subject to a **per-machine policy** the owner
>    sets.
> 3. **Credential:** only sessions the host **launched or adopted** get an agent credential.
>    Manually started sessions get none.
> 4. **Ordering:** **wait for the #55 roster.** No cross-machine agent messaging before it, and no
>    interim "any signed-in device" trust.
> 5. **Cross-account:** the design must **also provide for reaching sessions on a different
>    account's relay**. The scenario is collaborating on a project with another contributor who
>    runs their own bridge and relay, so that each side's agents can coordinate. It can be stubbed
>    at first, but a **design investigation** is required: how it would work, and what changes
>    about trust.

### 1.2 Goals

* **G1. Native.** An agent in one session can list, read, reply to and launch sessions on any of
  the owner's machines through Bridge Protocol v1 and the relay. No Home Assistant token, MQTT
  topic or GitHub comment is involved (#45's owner clarification, 2026-11-02: "there must be no
  Home Assistant tokens in Agent Bridge").
* **G2. Attributed exactly.** The receiving host knows, from a signature it checks itself, which
  machine and which session sent each message. It shows the message as agent-driven: the violet
  diamond, `driver: agent`, `driverPrincipal`, and `userMessageFrom: agent`. The silent
  wrong-token failure that AGENTS.md records for agent-ha-bridge (the 2026-09-29 blue card) can't
  happen, because an agent only ever holds an agent credential (design.md:503-506).
* **G3. Least authority.** Read, reply and launch only. Never `session.answer` on an approval,
  never `launch.trust.answer`, never `stop`, `transfer`, `update`, `forget`, settings or pairing.
* **G4. Owner control per machine.** Each machine's policy is set at that machine (the app's
  Settings, or `agent-bridge-host config`, refused inside agent sessions, local-pipe.md:85-141).
* **G5. Bounded automation.** Hop limits, conversation budgets and rate limits stop agent loops
  (A → B → A → …) and fan-out (an agent launching agents launching agents).
* **G6. Federation-ready.** The envelope, policy and display carry an account from day one, so
  cross-account federation (§7) is an extension, not a redesign.

### 1.3 Non-goals

* **Containment against same-user malware.** Every process of the user can read the device key
  and every session's environment. The pipe's caller checks are a safety rail, not a security
  boundary (local-pipe.md:121-130), and this design keeps that posture. It is about
  *attribution and scope*, and about bounding what a *stolen* credential or a *prompt-injected*
  agent can do. It is not about stopping a determined local attacker.
* **Agent-to-agent content safety.** The bridge carries the words and labels where they came
  from. It doesn't judge them. Prompt injection is bounded by labels, scope, budgets and
  consent (§5, §7.6), not filtered.
* **Answering any approval or trust question**, from any agent, ever (decision 1). This note does
  not offer a mode that enables it.
* **Interim trust before the roster** (decision 4). Neither "any signed-in device" nor the
  default-off trusted-peer floor (#407, host-config.md:143-200) is used to carry agent commands
  between machines.
* **Federation implementation in v1.** v1 only reserves the fields (§7.9).

---

## 2. Where things stand

| Today | Where | Consequence for this design |
|---|---|---|
| Agents have **no principal** on the pipe or the relay. A process inside an agent session may only read (`hello` offers `sessions.read` and `usage` only), and acting ops are refused with "answering, replying, ending and launching sessions are for the person" | `src/AgentBridge.Host/LocalApi.cs:22-26`, `:44-47`; local-pipe.md:95 | The pipe is the place to add an authenticated agent principal. Today's refusal is the default for any caller without one, including manually started sessions (decision 3) |
| The pipe already knows **which session a caller is inside**: by the caller's process ancestry, matched by process id *and* start time, or by `AGENT_BRIDGE_SESSION` in its environment | `src/AgentBridge.Local/AgentAncestry.cs:12-41`; `Launchers.SessionVariable` (`src/AgentBridge.Host.Delivery/Launch/Launchers.cs:34`); local-pipe.md:95-112 | Credential binding can be checked against ancestry, not only against a bearer token (§3.4) |
| A launched agent gets the host's environment plus `AGENT_BRIDGE_SESSION`. "No agent principal yet (#45)" | host-launch.md:68 | `AGENT_BRIDGE_TOKEN` (design.md:495, §5.7 table) is the planned replacement for `AGENT_HA_AGENT_TOKEN`; this note defines it (§3.3) |
| The only agent principal a host recognizes is a Home Assistant user in `homeAssistant.agentUserIds`: `driverPrincipal` is `homeassistant:<user id>` | v1-sessions.md:768-773; `SessionDrivers.HomeAssistantPrincipal` (`src/AgentBridge.Host.Core/SessionDrivers.cs:71-72`) | The driver machinery (arm, 600 s window, take-back) is reused unchanged. Only the principal's source changes |
| `driver` and `driverPrincipal` (≤ 128 bytes, plain text, "secondary detail only") are in the protocol | v1-sessions.md:732-733, :745-777 | Additive fields carry the structured sender (§6) |
| #489 / PR #501 adds `activity.userMessageFrom` (`person`, `schedule`, `agent`, `other`) and `userMessageLabel` (≤ 64 bytes). It marks a bridge reply as `agent` when its driver is an agent, by the prompt's record and text. #501's security review asks that no agent-supplied field can set or clear it | PR #501; #489 comments | A verified envelope is the source for `agent`. Nothing in the envelope's free text can set it (§6.3) |
| Host-to-host channels already exist through the relay: usage peers every 5 minutes, and transfers | `src/AgentBridge.Host/UsagePeers.cs`; v1-transfer.md:154-160; `ChannelHub.OpenAsync` (`src/AgentBridge.Transport/ChannelHub.cs:149`) | Agent commands ride the same kind of channel (§4.6) |
| Device keys are **P-256 ECDH-only** (TPM, WebCrypto non-extractable, Secure Enclave key agreement). The only "signed command" is a designated-verifier MAC from static-static ECDH, `CommandMac` | `src/AgentBridge.Transport/CommandMac.cs:18-31`; `SignedCommands.cs:9-77`; v1-transfer.md:87-147; #391 §1 | "Signs with its device key" needs a choice: a MAC that only the target can check, or a host signing key (§4.2, open question Q1) |
| The replay memory for signed commands is **in memory** and forgets across a restart | `CommandMemory`, `SignedCommands.cs:79-101`; #391 §7.2 (R6 makes it durable) | Agent commands use #391 R6's durable replay store (§4.4) |
| The relay routes **only within one account**. It takes `kind` from the device's own `hello`, and tokens are bearer tokens not bound to a key | `RelayHub.cs:13-16`, `:160-185`, `:249` ("Routing never leaves the sender's account"); `Tokens.cs:9-45` | Single-account messaging needs no relay change. Federation needs a relay change (§7.3) |
| A reply is never typed while the session is asking anything. Folder trust is answered only by a person's device through the relay, never from the pipe | v1-sessions.md:360, :368; v1-launch.md:139-147, :191-232 | Decision 1's "never approvals or trust" is already enforced by the existing paths. This design adds no path around them, and a policy check on top (§5.2) |
| design.md §5.6 grants local `agent@session` "read, reply, **answer**, launch with `ask` permissions" | design.md:495 | It conflicts with decision 1, which omits answer. This note follows the decision (Q3) |
| design.md §13 item 8: "Decide whether remote agent actions need a per-target grant, defaulting to off for launch with allow-all" | design.md:1356-1358 | Answered by the policy in §5: allow-all is never available to an agent |

---

## 3. Principal model

### 3.1 What an agent principal is

An **agent principal** is one live agent session on one machine of one account, vouched for by
that machine's host. It is not a device: it has no key, never joins a roster and never opens a
channel. It exists only as a claim that a rostered host makes and signs.

| Field | Meaning | Source |
|---|---|---|
| `account` | The account the issuing host belongs to | The host's sign-in (relay `ready`) |
| `machine` | The issuing host's device id (32 hex) | The host's own key |
| `session` | The agent's own session id, as in `Session.id` | The host's session tracker |
| `instance` | The session's current process: `base64url(SHA-256(F("agent-bridge session instance v1", machine, session, pid, processStartUtc))[0..12])`. Stable across host restarts, and new for each process (a resume, or a CLI restarted on the same session id) | The host's process snapshot |
| `agent` | `copilot`, `claude`, `codex`, and the `launcher` (`agency`, …) | The launch record |
| `credential` | The credential's id: 16 random bytes, base64url. It names the grant in logs and audit, and is never the secret | Issued in §3.3 |
| `depth` | How many agent hops led to this session's current turn (§5.4) | The host's driver state |

`F` is #391 §3.2's length-prefixed framing: each field as a 4-byte big-endian length, then the
bytes.

**Display string.** `driverPrincipal` becomes `agent:<machine>/<session>`, at most 75 ASCII bytes
(6 + 32 + 1 + 36), which fits v1-sessions.md:733's 128-byte limit. It is an identifier. What a
person reads ("from *Fix the flaky test* on *DSWETT-HOME*") is built from structured fields
(§6.1).

### 3.2 Who gets one (decision 3)

| Session | Credential? | How |
|---|---|---|
| **Launched by the host**: `session.launch` from any device, a launch by an agent, a resume, a transfer, or `agent-bridge-host launch` | **Yes**, at launch | The host mints it before it starts the process, and puts it in the child's environment (§3.3). A Codex launch, whose id isn't known in advance, is bound when the launch binds its session (v1-launch.md:153-156) |
| **Adopted** | **Yes**, at adoption | A person at the machine adopts a live session the host discovered (§3.5) |
| **Manually started** (typed in a terminal, never adopted) | **No** | It keeps today's read-only rights on the pipe (local-pipe.md:95). It has no remote reach, and no attribution, because there is nothing to attribute |
| **Sub-agents** of a credentialed session | Use the parent's credential | They run in the same process tree, and attribution is per session (subagents.md). The audit records `source` when a sub-agent's tool call made the request, where the host can tell |
| **A session migrated from agent-ha-bridge** (launched by the old daemon) | **No**, until adopted | The host did not launch it, and the old `AGENT_HA_AGENT_TOKEN` in its environment is not honoured (G1) |

### 3.3 Issuing the credential: `AGENT_BRIDGE_TOKEN`

The credential replaces `AGENT_HA_AGENT_TOKEN`, as design.md:495 planned.

* **Format.** `abat1_` followed by 32 random bytes, base64url: 49 ASCII characters. The prefix
  lets secret scanners and the redaction in logs recognize it.
* **At launch.** The host mints the token, stores only `SHA-256(token)` with the binding
  `{credential, session?, launchId, rootPid, rootStartUtc, scope, depth, chain, issuedAt,
  expiresAt}`, and adds `AGENT_BRIDGE_TOKEN` and the existing `AGENT_BRIDGE_SESSION` to the
  child's environment. The token is never logged, never sent over any channel, and never leaves
  the machine. Only the host's own pipe takes it.
* **Store.** The hash records are kept in the profile's private folder (`PrivateFolder`, owner-only
  ACL, DPAPI- or Keychain-wrapped), so a host restart doesn't strip every running session of its
  credential (Q4). A hash can't be presented as a token, so the store holds no secret that
  authenticates. design.md §5.7 ("memory only") is amended in that one respect.
* **Scope.** Taken from the machine's policy at issue (§5.1), narrowed by the launch:
  * a launch can say `"agentScope": "none" | "read" | "read+reply" | "read+reply+launch"`
    (additive to `session.launch`, default: the policy's);
  * a launch made **by an agent** can never widen its sender's scope, and inherits its `depth` + 1
    and its conversation `chain` (§5.4).
* **Expiry.** A token is valid while its session's bound process lives, and for at most
  `agents.credentialDays` (default **7**). A longer-lived session is re-adopted with one press at
  the machine.

### 3.4 Local agent → host authentication

The agent's tools (the MCP server of #45, or `agent-bridge-host agent …` for scripts) connect to
the pipe and open with a new op:

```json
{ "id": 1, "op": "agent.hello", "data": { "token": "abat1_…" } }
```

The host accepts it only if all of these hold:

1. **The token matches** a live credential record (constant-time comparison of the hashes).
2. **The caller is inside that session.** The pipe's existing ancestry read (local-pipe.md:101-112)
   finds the credential's bound process (`rootPid` with `rootStartUtc`) among the caller's
   ancestors. Or, where the ancestry can't be read in full (a process started detached),
   `AGENT_BRIDGE_SESSION` matches and the ancestry doesn't name a *different* live session. A
   token presented from inside another session is refused and logged as
   `credential presented from another session`. That is the realistic theft: one agent reading
   another session's environment, or a transcript that printed it.
3. **The session's process is still the bound one** (id and start time), so a recycled pid can't
   inherit it.

On success the connection is an **agent connection** for its lifetime. Its `hello` capabilities
are the credential's scope (`sessions.read`, `agent.reply`, `agent.launch`, `agent.remote`), and
each later op is attributed to the principal without resending the token. A pipe connection
without `agent.hello` keeps today's rules exactly.

**macOS and Linux.** The host can't trace a socket client yet (local-pipe.md:129-130), so step 2
reduces to `AGENT_BRIDGE_SESSION` matching the credential's session. Adding `SO_PEERCRED` and
`LOCAL_PEERPID` ancestry there is a prerequisite for adoption on those platforms (§3.5), not for
launched sessions.

**Revocation.** A credential is revoked, its record deleted and every agent connection on it
closed when:
* the session's bound process exits, or the session is stopped or ended;
* the session is resumed (the new process gets a new credential);
* the person revokes it at the machine ("Stop this session acting as an agent", in the app, or
  `agent-bridge-host agents revoke <session>`);
* the policy changes in a way that removes the scope (the record is re-scoped, or deleted when
  the scope is empty);
* the store can't be read at host start (fail closed: unreadable is not absence);
* `agents.credentialDays` passes.

A host restart doesn't revoke a credential whose bound process is still the same (Q4). Every
revocation is in the audit log (§5.6).

**Rotation.** The environment of a running process can't be changed, so the bootstrap token isn't
rotated in place. What rotates is the *use*: each `agent.hello` creates a new connection-scoped
grant, and a credential reissued at resume or re-adoption invalidates the old one. A token copied
out of the session is useless once the process ends, and unusable from another session meanwhile.

### 3.5 Adoption

"Adopted" is new in agent-bridge. This note proposes:

* **What.** A person, **at the machine**, gives a live session the host discovered (not launched)
  an agent credential. In the app, it's "Let this session message other sessions…" on the
  session's card. On the command line, it's `agent-bridge-host agents adopt <session>`, refused
  inside agent sessions by the same rule as other config changes (local-pipe.md:108-111). It is
  never offered to a remote device: decision 3 makes it the person's choice about *this* machine.
* **Delivery.** A running process's environment can't be changed, so an adopted session gets its
  token by asking: `agent.credential` on the pipe, accepted only when the ancestry check (step 2
  above) proves the caller is inside the adopted session's bound process. The token goes to that
  caller and nowhere else. **Windows only** until §3.4's peer-pid work lands on macOS and Linux.
* **Scope.** The policy's default for adopted sessions, which the person can narrow in the
  adoption dialog.
* **Why not adopt automatically.** A manually started session may be one the person is using for
  something private. Decision 3 is explicit that those get nothing.

### 3.6 How #45's MCP server fits

The MCP server ships with the host (design.md §3.1, :164). It keeps agent-ha-bridge's tool names
(#45: `list_agent_sessions`, `read_agent_session`, `reply_to_agent_session`,
`launch_agent_session`, `read_launch_result`, `get_launch_options`, and the
`ask_via_home_assistant` alias).

* **stdio** (the normal case). The agent's CLI starts it as a child, so it inherits
  `AGENT_BRIDGE_TOKEN` and `AGENT_BRIDGE_SESSION`, sits under the session's process, and passes
  `agent.hello`. Every tool call goes over that one agent connection.
* **Local and remote targets.** `list_agent_sessions` lists this host's sessions and, through
  `agent.machines` (the roster's active hosts, after #55), other machines. A tool naming another
  machine becomes `agent.remote` on the pipe. The local host signs and sends it (§4) and returns
  the target's result unchanged. The MCP server never holds a key, a relay token or the device
  key.
* **HTTP mode** (`127.0.0.1:8808`, #45). It isn't a child of one session, so it can't be
  attributed by ancestry. Recommended: no agent principal over HTTP in v1. It reads as an
  unattributed local caller does today (Q8).
* **Outcomes** map onto the old statuses as #45 requires. Reading the answer back uses
  `read_agent_session(session, since)`, which waits on the target for the session's `response` to
  change after `since`. That avoids AGENTS.md's "snapshot then poll" trap, in which the first poll
  returns the previous turn's answer.

---

## 4. The signed agent command

### 4.1 Flow

```
agent (session S on machine M) ──pipe: agent.remote{action, target, data}──▶ host M
host M: credential + outbound policy + rate + chain ──sign──▶ envelope E
host M ══Noise channel through the relay══▶ host T: agent.command{E, data}
host T: roster + freshness + signature + replay + policy + action checks ──▶ act
host T ──result──▶ host M ──▶ agent
```

For a target on the **same machine**, nothing is signed. The host applies the same policy, rate
limits, chain rules and attribution to the agent connection directly, and audits it the same way.
So the local slice (§9) exercises every rule except the signature.

### 4.2 What signs it (Q1)

The owner's tuple is signed by "the host's device key". A host's device key is ECDH-only (§2),
so there are two ways to make that true.

| | (a) **Host signing key** (recommended) | (b) **Designated-verifier MAC** (`CommandMac`, as transfers use) |
|---|---|---|
| Primitive | A P-256 ECDSA key `sign`, created beside the host's `dh` key (TPM via NCrypt where it supports ECDSA P-256, else DPAPI or Keychain), listed in the host's roster `add` entry | HKDF from static-static ECDH between the two hosts' `dh` keys, then HMAC (v1-transfer.md:87-104), with a new salt `"agent-bridge agent command v1"` |
| Who can verify | Anyone holding the roster: the target host, the person's app reviewing the audit log, a downstream hop, a federated peer (§7) | Only the target host |
| Audit | Envelopes are evidence that any rostered device can re-verify later | The target's log is its own word |
| Chains (§5.4) | Hop *k* can carry hop *k−1*'s envelope, and the next host can check it | Each hop must trust the previous host's account of the chain |
| Change to #391 | An amendment: a host's `add` entry carries `sign` and `signAlg` (`presence: none`). #391 §3.1 says hosts "never sign" roster entries, and they still never do: framing domain separation (`F("agent-bridge agent command v1", …)`, never `"… roster entry v1"`) means the key's signatures can't be read as roster entries or person commands, and the verifiers of those already require a person kind | None |
| Cost | One ECDSA signature per command (about 1-3 ms on a TPM) | One ECDH per peer, cacheable |

**Recommended: (a)**, decided before #391's R1 is built so that the host entry format includes it
once. Under (b) everything in this note still works for a single account, with a weaker audit
and chain story (§5.4 then rests on the previous host's word, which is #391's position for any
compromised host anyway).

### 4.3 Canonical encoding and what is signed

The envelope uses #391 §3.2's conventions exactly: exact body bytes sent as base64url,
strict I-JSON parsing with exact field sets and shared negative vectors, and a detached signature
over framed bytes. It is sent as:

```json
{ "body": "eyJ2IjoxLC…", "sig": "MEUCIQ…" }
```

`sig` is the issuing host's `sign` signature over `F("agent-bridge agent command v1", bodyBytes)`:
strict minimal DER, low-S, `p256-sha256-der`, as #391 specifies for roster entries. Under option
(b), it is `mac`, the HMAC over the same framed bytes.

`body` decodes to:

```json
{
  "v": 1,
  "request": "0b7e3c1d-5f2a-4c6e-8d9b-1a2b3c4d5e6f",
  "account": "<issuing host's account id>",
  "heads": ["<roster entry hash>", "…"],
  "principal": {
    "kind": "agent",
    "machine": "<issuing host device id>",
    "session": "<agent session id>",
    "instance": "<12-byte base64url>",
    "credential": "<16-byte base64url>",
    "agent": "copilot",
    "launcher": "agency"
  },
  "chain": {
    "root": "<request of the conversation's first agent message>",
    "rootAt": 1793300000,
    "depth": 1,
    "messages": 3,
    "via": [ { "account": "…", "machine": "…", "session": "…" } ]
  },
  "action": "session.reply",
  "target": {
    "account": "<target account id>",
    "machine": "<target host device id>",
    "session": "<target session id>",
    "instance": "<12-byte base64url>"
  },
  "dataSha256": "<base64url SHA-256 of the op's data bytes as sent>",
  "issuedAt": 1793300042,
  "expiresAt": 1793300162
}
```

| Field | Rules |
|---|---|
| `v` | 1. Anything else is refused |
| `request` | A UUID, fresh per command. It is also the **nonce** (§4.4). A resend of the same command reuses it with identical bytes |
| `account` | The issuer's account. In v1 it must equal the target host's own account (§7.9) |
| `heads` | The issuer's current roster heads, as #391 person commands carry them (#391 §7.2) |
| `principal` | §3.1. `machine` must be the channel peer that delivered it: a host never relays another host's envelope as its own (§4.6) |
| `chain` | §5.4. `via` is at most `maxDepth` entries, most recent last. Under (a), each entry may also carry the previous hop's envelope hash, and the target may fetch and check it |
| `action` | `sessions.list`, `session.get`, `session.read` (§4.7), `session.reply`, `launch.options`, `session.launch`, `launch.result`. **Nothing else verifies.** In particular `session.answer`, `launch.trust.answer`, `session.stop`, `session.transfer`, `attachment.*`, `update.*`, `push.*` and `host.*` are not in the action set, so the envelope cannot express them |
| `target` | `account` and `machine` must be the target host's own. `session` is required for `session.get`, `session.read` and `session.reply`. `instance` is required for `session.reply` and optional for reads (§4.5). Absent for `sessions.list`, `launch.options` and `session.launch` |
| `dataSha256` | Binds the op's `data` (the reply text, the launch settings and prompt) to the signature: the SHA-256 of the exact bytes of the inner op's `data` JSON, which travel base64url-encoded beside the envelope (§4.6), so neither host re-serializes anything. Beside, not inside, so the envelope stays under 2 KiB and the 16 KiB reply and 20 KiB launch limits (v1-sessions.md:330; v1-launch.md:105-115) are unchanged |
| `issuedAt`, `expiresAt` | Unix seconds. Lifetime at most **120 s**. Long enough for a relay round trip and a reply's delivery check, and short enough that a captured envelope is useless soon after |

The owner's tuple maps as: `agent` → `principal.agent`/`launcher`; `session` →
`principal.session` + `instance`; `machine` → `principal.machine`; `action` → `action`; `target`
→ `target`; `nonce` → `request`; `expiry` → `expiresAt`. `account`, `heads`, `chain` and
`dataSha256` are added for federation, roster lag, loops and content binding respectively.

### 4.4 Replay protection and clock skew

* **Single use.** The target keeps a **durable** replay record keyed by
  `(principal.machine, request)`. It uses #391 R6's store, which replaces `CommandMemory`
  (`SignedCommands.cs:79-101`, in memory). The record is written and flushed **before** the action
  executes; an action whose record can't be written doesn't run. It is kept until `expiresAt` +
  skew by the host's persisted wall-clock high-water mark (#391 §7.2).
* **Idempotent resend.** A command whose `(machine, request)` is known and whose body bytes are
  identical gets the remembered outcome back, and nothing is typed or launched again. That is
  exactly `session.reply`'s and `session.launch`'s `request` semantics today (v1-sessions.md:329;
  v1-launch.md:94). The same `request` with *different* bytes is refused as `replayed`.
  Outcomes are remembered for at least 10 minutes, so an issuer that lost a result asks with
  `agent.outcome {request}` rather than signing a new command.
* **Skew.** A command is refused if `issuedAt` is more than **120 s** in the target's future
  (`TransferProtocol.Skew`, as transfers use), or if `expiresAt` has passed. Lifetime ≤ 120 s
  plus ≤ 120 s of skew bounds acceptance to 4 minutes of wall clock. Both hosts' clocks being
  wrong in the same direction doesn't widen it, since the target uses only its own clock.
  A target refuses with `clock` and both times, so the issuer can say "check the clocks".
* **Restart.** The durable record and the high-water mark make a command that ran before a
  restart refused after it, including across a wall-clock step back (#391 §7.2's floor rule).

### 4.5 Target selection and binding to a session instance

A command names a **session id and a process instance**. The replacements it must survive:

| What happened | Effect |
|---|---|
| The **target host restarted** (`machine.instance` changed, v1-sessions.md:85) | Nothing for the command: `instance` is computed from the session's process, not the host's run. A reply to a session whose process lived through the restart is still delivered |
| The **session was resumed** (new process, same id) | `instance` differs, and `session.reply` is refused as `stale`, saying so. The issuer re-reads (`session.get` returns the new `instance`) and decides again. A message meant for one conversation is never typed into its successor unseen. This is the same rule `session.stop` already applies with id and start time (v1-sessions.md:519-528) |
| The **session ended** | `stale` |
| The **channel dropped** mid-command | The issuer resends the same envelope (same `request`, same bytes). The target either returns the outcome or runs it once (§4.4) |
| **Another host claims the same machine id** | Impossible to forge: the channel's Noise static key *is* the machine id (design.md:452-454), and the roster lists it |

Reads may omit `instance` ("whatever is live under that id"). Adding `instance` to `Session` is
an additive protocol field: computed by the host, absent for a session whose process it can't
read.

**Discovery.** An agent picks a target from `sessions.list` results, through its host. Session
ids are not secrets, but a target that policy doesn't let this principal see is answered
`not-found`, the same as one that doesn't exist (§5.2), so an agent can't probe for sessions it
may not reach.

### 4.6 How it rides the relay

* **Channel.** Host M opens (or reuses) a Noise channel to host T through `ChannelHub.OpenAsync`,
  as `UsagePeers` and transfers do. In `enforce`, #391 §7.1 gates the channel to **active
  rostered hosts** on both ends, and both must be **fresh** (#391 §6.3). The relay sees frame
  sizes and timing between two of the account's hosts, as it does every 5 minutes for usage today.
* **Op.** One new host-to-host op, `agent.command`:

  ```json
  { "id": 9, "op": "agent.command", "data": { "command": { "body": "…", "sig": "…" }, "data": "eyJpZCI6Ii4uLiIsInRleHQiOiIuLi4ifQ" } }
  ```

  The inner `data` is the exact UTF-8 bytes of the inner op's request `data` (here
  `{"id":"…","text":"…"}` for `session.reply`), base64url without padding. Base64 adds a third
  to a 16 KiB reply, which still fits v1's 48 KiB message. The result is the inner op's result, unchanged (`delivered`, `unconfirmed`, `busy`, `refused`,
  `stale`, …; a launch's `started`, …). A refusal is `{ ok: false, error }` with a reason in
  words, which the issuing host passes back to the agent.
* **Capability.** `agent.command` in `hello` (host-to-host channels only), and only while the host
  is in roster `enforce` mode. A host without it is never sent one. A client channel (`app`,
  `pwa`, `phone`) never gets it.
* **Who carries.** `principal.machine` must equal the channel peer's device id. Hosts never relay
  other hosts' envelopes, so a channel peer can only speak for its own sessions, and the relay
  can't splice an envelope into another channel (the Noise prologue binds both ids,
  design.md:452-454).
* **Sizes.** The envelope is at most 2 KiB, and the whole message stays under v1's 48 KiB.
  Attachments are out of scope for v1.
* **Rate.** The relay's per-connection frame rate (`RelayHub.cs:286-290`) is unchanged. The
  meaningful limits are the hosts' (§5.5).

### 4.7 Verification at the target

Host T acts on an `agent.command` only if every check passes, in this order (cheap and
non-disclosing first). Each refusal is audited with its reason:

1. **Channel.** The peer is an active `host` in T's roster, and T is fresh (#391 §6.3, §7.1). A
   stale host serves nothing here.
2. **Parse.** Strict I-JSON, exact field set, `v` 1, sizes within bounds.
3. **Carrier.** `principal.machine` == the channel peer, `principal.kind` == `agent`.
4. **Account.** `account` == `target.account` == T's account. In v1 anything else is
   `federation-unsupported` (§7.9).
5. **Signature.** Under (a), `sig` verifies under the peer's roster `sign` key with the agent
   framing label. Under (b), the MAC verifies under the pairwise key.
6. **Roster lag.** T holds every head in `heads`, syncing from the peer first (`roster.sync`) if
   not (#391 §3.6). If T's state, after the sync, has the issuer removed, T refuses. If the
   *issuer* is behind T (it lacks a removal T holds), that doesn't matter: T decides on its own
   state.
7. **Lifetime and replay.** §4.4.
8. **Content.** `SHA-256(data bytes)` == `dataSha256`.
9. **Action and target.** The action is in the allowed set. `target.machine` is T. The op's `data`
   names the same session as `target.session`. `instance` matches for a reply.
10. **Policy.** §5: inbound rules for this origin machine and action, the target session's
    eligibility, chain and budget, and rates.
11. **The op's own checks**, unchanged: a reply is refused while a question or permission prompt
    is pending (v1-sessions.md:360, :368), a launch only in an approved workspace with the
    offered values (v1-launch.md:16-18), `busy` while a trust question waits (v1-launch.md:139-147),
    no `/` or `!` commands, control characters collapsed, read-only machines and agent-ha-bridge
    (G5) refusals.

**Revocation at the target.**
* A **removed host**: T closes its channels the moment it applies the removal (#391 §3.7), and
  step 6 refuses envelopes signed before T learned of it once T has synced. The residual is #391
  §6.3's: within a relay-maintained partition, a removed host can keep being served there.
* A **revoked credential** is local to the issuer. T can't see it, and needs not to: the issuer
  refuses to sign for it. A compromised issuer host can sign for any of its sessions, or invent
  one. That is the "compromised machine" row of §8, and the target's policy (which origin
  machines may do what) is the bound.

---

## 5. Policy

### 5.1 The per-machine policy

The policy lives in the machine's `config.json` under `agents`. Like `launch.remoteFolderTrust`,
it is changed only at the machine: in the app's Settings, or with `agent-bridge-host config`,
both refused inside agent sessions (local-pipe.md:85-141; v1-launch.md:212-215). A remote device
can read it (so a client can say why something was refused) but never change it. An unreadable or
malformed policy denies every agent command, inbound and outbound. A setting someone may have
meant as off is never read as on (host-config.md:139-141).

```json
"agents": {
  "enabled": true,
  "credentialDays": 7,
  "issue": { "launched": "read+reply+launch", "adopted": "read+reply" },
  "outbound": {
    "remote": true,
    "allowAgentLaunchedToLaunch": false
  },
  "inbound": {
    "local":  { "read": "any", "reply": "any", "launch": { "workspaces": "approved", "permissions": "ask" } },
    "remote": {
      "fromMachines": "any",
      "read": "any",
      "reply": "any",
      "launch": { "workspaces": "approved", "permissions": "ask", "agents": "any" }
    },
    "exclude": { "sessions": [], "workspaces": [] }
  },
  "limits": {
    "maxDepth": 3,
    "conversationMessages": 20,
    "conversationMinutes": 60,
    "perPairPerMinute": 6,
    "perTargetPerMinute": 12,
    "launchesPerOriginPerHour": 3,
    "launchesPerMachinePerHour": 10
  },
  "label": "prefix",
  "links": []
}
```

| Setting | Values | Meaning |
|---|---|---|
| `enabled` | bool | Master switch for this machine, both directions. `false` means today's behaviour exactly |
| `issue.launched`, `issue.adopted` | `none` \| `read` \| `read+reply` \| `read+reply+launch` | The scope a credential gets at issue (§3.3) |
| `outbound.remote` | bool | Whether this machine's agents may reach other machines at all |
| `outbound.allowAgentLaunchedToLaunch` | bool | Whether a session an agent launched may itself launch (fan-out, §5.4) |
| `inbound.*.read`, `.reply` | `any` \| `agent-launched` \| `none` | Which sessions on this machine an agent may read or reply to: any live session; only sessions an agent launched (via the bridge); none |
| `inbound.*.launch` | `false`, or `{ workspaces: "approved" \| [labels], permissions: "ask", agents: "any" \| [ids] }` | Where an agent may launch. **`permissions` can only be `ask`.** `allowAll` is not expressible for an agent: it stays a person's decision, and design.md §5.6 already requires a confirmation for it even from a phone |
| `inbound.remote.fromMachines` | `any` \| `[device id]` | Which of the account's machines may send. `any` means any active rostered host |
| `inbound.exclude` | session ids and workspace labels | Never reachable by any agent, local or remote. A person can mark a private session or workspace this way |
| `limits.*` | integers | §5.4, §5.5 |
| `label` | `prefix` \| `none` | Whether delivered text carries the provenance prefix (§6.4). `prefix` is forced for cross-account |
| `links` | `[]` | Federation (§7). Must be empty in v1 |

**Defaults** (Q2). Decision 2 is "any session on the owner's account, subject to a per-machine
policy", so the defaults above let any session be read and replied to from the owner's other
machines. Launch is allowed in approved workspaces with `ask` only. The policy is the only way to
narrow it, and the app shows it as plain choices ("Agents on your other machines can: read ✓,
reply ✓, launch in approved folders ✓"). Until a host is in roster `enforce` mode, `inbound.remote`
is ignored and treated as `none` (decision 4).

### 5.2 Allowed actions

| Action | Agent may? | Enforced by |
|---|---|---|
| `sessions.list`, `session.get`, `session.read` (wait for change), `launch.options`, `launch.result` | Yes, if `read` permits | Policy. Sessions not readable are left out of lists, and are `not-found` when named |
| `session.reply` (text only) | Yes, if `reply` permits | Policy, then the existing reply checks (v1-sessions.md:313-370), including the refusal while any question or permission prompt is pending, which is what stops a reply from answering an approval |
| `session.launch` (new, or resume) | Yes, if `launch` permits, `permissions: ask` only | Policy, then `LaunchProtocol.Check` and the machine's offered values |
| `session.launch` with `transfer` | **No** | Transfers need a person command (#391 §7.2) |
| `session.answer`: an approval (Copilot permission, Claude permission, Codex approval) | **Never** | Not in the action set (§4.3), and refused on the pipe for agent connections (`LocalApi.cs:44-47`'s rule kept) |
| `session.answer`: an `ask_user` or `AskUserQuestion` form | **No in v1** (Q3) | As above. Decision 1 lists read, reply and launch only |
| `launch.trust.answer` (folder trust, Bypass Permissions warning) | **Never** | Relay person-device only (v1-launch.md:216-219), and not in the action set |
| `session.stop` | **No in v1** (Q12) | Not in the action set |
| `attachment.*` | **No in v1** | Not in the action set |
| `update.*`, `push.*`, `host.*`, `machine.forget`, `pairing.*`, roster ops, settings | **Never** | Not in the action set. Pipe management ops are already refused inside agent sessions |

### 5.3 Sessions an agent can't reach, whatever the policy

* A session that is **excluded** (`inbound.exclude`).
* The **sender itself** (no self-reply loop through the bridge).
* A session **elevated** or another user's (existing delivery refusals, design.md:498-501).
* While the target host is **read-only** or agent-ha-bridge runs there (G5).

### 5.4 Chains, loops and the hop limit

The risk: agent A messages B, B's turn (driven by A) messages C, C messages A, and the loop runs
unattended, burning tokens and possibly amplifying an injected instruction. The rule has to allow
the normal case, a conversation where A asks B and B answers A, many times.

**Conversation.** Each agent message belongs to a **conversation**, identified by `chain.root`:
the `request` of the first agent message sent from a turn that a **person** drove. Every host
tracks, per session, the chain of the turn that is running (it already tracks the turn's driver
and principal, `SessionDrivers`):

* A turn the **person** drove (typed, replied or answered from a device, or launched by a person):
  the session's next outbound message starts a **new conversation**: `root` = its own `request`,
  `depth` 1, `messages` 1, `via` = [].
* A turn an **agent message** drove: outbound messages **continue that message's conversation**:
  `root` and `rootAt` unchanged, `messages` + 1, `via` = the incoming `via` + the incoming
  sender, and `depth` = the number of **distinct sessions** in `via` plus one.
* A session **launched by an agent** starts in the launcher's conversation, at its depth + 1.

**Rules**, checked by the issuer before signing and again by the target (step 10):

| Rule | Default | Effect |
|---|---|---|
| `depth ≤ maxDepth` | 3 | A → B → C is allowed. C reaching a fourth session D is refused (`chain-too-deep`). Replying back to a session already in `via` doesn't add depth, so A ⇄ B conversations are unaffected |
| `messages ≤ conversationMessages` | 20 | A conversation can't exceed 20 agent messages in total, across all its sessions. Past that: `conversation-budget`, and the message tells the agent that a person must reply to continue |
| `now − rootAt ≤ conversationMinutes` | 60 | An agent conversation older than an hour ends the same way |
| A launch's `depth ≤ maxDepth − 1` | 2 | A launch is a hop. A person-driven session, or one an agent messaged directly, may launch; deeper ones may not, so launches can't fan out into a tree and a launched session always has a hop left to report back |
| `allowAgentLaunchedToLaunch` | false | A session an agent launched can't launch further sessions at all, by default |

**A person resets it.** Any person input to a session (a reply or answer from any of the person's
devices, or typing at the machine) takes the session back (v1-sessions.md:763-767) and ends its
part in any conversation. Its next outbound message is a fresh root. This is the same "until the
person takes it back" rule as the purple edge, so the budget is easy to explain: "agents can go
back and forth 20 times on their own; then one of them needs you".

**Who is trusted for the chain.** The chain is the issuing host's claim. Under 4.2(a), each `via`
entry can carry the hash of the envelope that delivered it, and the target can ask the previous
host for that envelope and verify it, so a compromised host can't launder a long chain into a
short one without other hosts' signatures. Under (b), a compromised host can lie about the chain.
Either way, a compromised host is bounded by the target's per-origin rates (§5.5).

### 5.5 Rate limits

Counted by the **target** host (authoritative) and by the issuer (to fail fast without a round
trip). The windows are sliding, in memory, and reset by a restart, which is acceptable because a
restart also costs the sender time:

| Limit | Default | Key |
|---|---|---|
| `perPairPerMinute` | 6 | (origin machine, origin session) → (target session) |
| `perTargetPerMinute` | 12 | target session, from all agents |
| `launchesPerOriginPerHour` | 3 | origin session |
| `launchesPerMachinePerHour` | 10 | target machine, from all agents |
| reads | 60/min per origin session | `session.read` with a wait counts once per wait |

Refusals are `rate-limited` with `retryAfter` seconds. The MCP server surfaces that to the agent
rather than retrying silently.

### 5.6 Audit log

Every agent command is recorded on **both** hosts, accepted or refused, including local ones and
refusals before signing.

* **Where.** `<profile>/audit/agents-YYYY-MM-DD.jsonl`, owner-only, at most 30 days and 64 MiB
  (oldest first).
* **What.** `at`, `direction` (`out`/`in`/`local`), `request`, `principal` (all fields), `chain`,
  `action`, `target`, `outcome`, `reason`, `dataSha256`, the text's length, and the envelope bytes
  and signature (for `in`). **Not the text**: it is already in the target session's transcript,
  and the audit log shouldn't become a second copy of everything agents say. Q14 offers keeping
  it.
* **Credential events.** Issue, adoption, rejection (wrong session, wrong process), revocation and
  its cause.
* **Shown.** The app gets an "Agent activity" view per machine (later slice) that filters the log:
  "*Fix the flaky test* on *DESK* replied to *Release notes* here, 14:05 PT".
* **Verifiable.** Under 4.2(a), the person's app can re-verify any inbound entry against the
  roster.

---

## 6. Display and attribution

### 6.1 Protocol fields (additive)

| Field | On | Value |
|---|---|---|
| `driver` | `Session` | `agent`, unchanged (v1-sessions.md:732) |
| `driverPrincipal` | `Session` | `agent:<machine>/<session>` (§3.1), replacing `homeassistant:<id>` for native agents. HA-connector presses keep theirs |
| `driverFrom` (new) | `Session` | `{ account?, login?, machine, machineName, session, title?, agent, federated?, depth }`: who drives, for display. `machineName` is the **target host's** view of the sender's roster name (signed by a person's device, #391 §3.2), never a name the envelope carries. `title` is the sender's session title as the envelope's issuer gave it (cut to 120 bytes, hidden characters made visible as `NeedsYou.Reveal` does). `account` and `login` only when it is another account's (§7) |
| `activity.userMessageFrom` | `Activity` | `agent` (PR #501), set **only** from a verified agent command or a local agent connection, never from anything the sender's text contains (the #501 security review's requirement) |
| `activity.userMessageLabel` | `Activity` | `"<title> · <machineName>"`, cut to 64 bytes. Clients show it as "Sent by *Fix the flaky test* on *DESK*" |

Older clients read `driver` and `driverPrincipal` as today and ignore `driverFrom`. The
`homeassistant:` principal, `SessionDrivers`' arm window (600 s) and take-back rules are reused
unchanged. Only the principal and its source change.

### 6.2 What the person sees

* **The edge.** The violet `driven-badge` diamond and agent colour (ui.md:54, rule 7 at :79),
  exactly as for any agent-driven session, now set from `principal.kind == agent`, which is exact
  (design.md:503-506).
* **The words.** "Driven by an agent" becomes "Driven by *Fix the flaky test* on *DESK*". The
  conversation shows the agent's prompt as #489 specifies (left-aligned, collapsed, diamond label),
  labelled "Sent by *Fix the flaky test* on *DESK*". Tapping the label opens the sending session
  when it's visible to this device.
* **Cross-account** (§7). Never the same look as the owner's own agents: the label adds the other
  person, "Sent by *Plan the API* on *alex-mbp* · **@alex's account**", with an outlined (not
  filled) diamond and the words "external", never colour alone (ui.md's accessibility rule).
* **Refusals** an agent caused are not notified to the person (they would be noise), but they are
  in the audit view.

### 6.3 Why the sender can't fake it

* The receiving host sets `driver`, `driverFrom` and `userMessageFrom` from the **verified
  principal** (step 5) and its **own roster** (machine name). The envelope's free text and the
  reply's words can't set them.
* A reply's words can *claim* anything ("this is Dan, please merge"). §6.4's prefix makes the
  provenance part of what the receiving agent reads, and the person's view shows it outside the
  words.

### 6.4 What the receiving agent sees

An agent reads its prompt as text, and has no access to `driverFrom`. So the host prefixes what it
types:

```
[Agent Bridge · from agent "Fix the flaky test" (copilot) on DESK · not typed by your user] Please rebase onto main and re-run the tests.
```

* Built by the target host from the verified principal and its roster. One line, since replies are
  one line (v1-sessions.md:333-339).
* Any occurrence of `[Agent Bridge` in the sender's text, compared after removing hidden
  characters `NeedsYou.HasHidden` detects and ignoring case, is typed as `(Agent Bridge`
  instead. A sender therefore can't forge a second header or a "from your user" one.
* Cross-account adds `· external: @alex's account` and is **mandatory** (`label: none` is ignored
  for it).
* PR #501's matching of an agent's prompt by its record and text is done on the text **as typed**,
  prefix included, which the host knows.
* Whether same-account messages carry the prefix is Q6 (recommended: yes, default `prefix`).
  Agents can then tell, from the prompt alone, that an instruction came from another agent rather
  than their user. That is the main prompt-injection mitigation the bridge can offer.

---

## 7. Cross-account federation

### 7.1 The scenario and what changes

The owner (account **A**, relay **RA**) collaborates on a project with a contributor (account
**B**, relay **RB**: possibly another operator, possibly the same production relay). Each side's
agents should coordinate: A's agent asks B's agent for the API shape, B's agent replies, and each
side may want to start a session on its own machine in response.

**What changes about trust**, compared with one account:

| | Single account | Cross-account |
|---|---|---|
| Who is behind every principal | One person. Every agent works for them | Two people with different interests, either of whom may be careless, compromised or hostile to the other |
| Root of trust | One roster (#391), approved by the person's own presence-proven devices | Two rosters that never merge. Each side trusts only its **own** person's decision to link, and pins the other's identity at that moment |
| Data leaving | Stays with the same person (on other machines) | **Disclosure to a third party**. Corporate policy applies (design.md:1338-1343, "Corporate policy (S8)"): a corporate machine's transcripts may not be shareable at all |
| Inbound text | Instructions from your own agents (still injectable) | Instructions from someone else's agents, to be treated as **untrusted input** |
| Default reach | Any session (decision 2) | **Nothing**, until both owners consent to a scope |
| Launch | `ask` in approved workspaces | Opt-in per link, one workspace, `ask` only |
| Relay | One untrusted operator, who sees one account's metadata | Up to two, and the target's operator learns that A and B are linked |

### 7.2 Establishing trust: options

| Option | How | For | Against | Verdict |
|---|---|---|---|---|
| **(F1) Pairwise link, invitation with key exchange** | The two owners each make a **link** on a presence-proven device. They exchange **link descriptors** and compare a **link code** derived from both. Each side then records the link in its own account, signed by its own person's device | Each side's trust stays inside its own roster. Revocable by either side alone. No change to #391's acceptance rule. Works across relays, operators and self-hosting | One exchange per pair of people. More than two parties need a link per pair | **Recommended** |
| (F2) Shared project or space object | A signed, append-only membership log for "project X", co-signed by each member account's person devices. Hosts of each member subscribe | Natural for many parties. One place for scope | A second roster-like consensus object across accounts, with forks, removals and seniority *across people*. That is everything #391 solves, but between parties with no common senior device | **Later**, as a layer over F1 links ("space = named set of links sharing a workspace mapping") |
| (F3) Cross-signing roster roots | A's roster includes B's genesis (or vice versa), so A's hosts trust B's whole roster | Simple verification | Transitive and unscoped: every approval mistake or compromise on B's side becomes A's, and #391's seniority and probation don't compose across people. Revocation of one B device needs A to track B's roster semantics | **Rejected** |
| (R1) Relay-to-relay peering | RA and RB route frames to each other. Hosts talk through their own relay | Each host keeps one connection | Needs operator-to-operator trust and authentication, a server-to-server protocol, and abuse controls on both. Both operators see the metadata. Fails when either side is LAN-only or self-hosted without peering | **Rejected for v-full**; possible later optimization |
| (R2) Host-to-host, **guest on the target's relay** | A's host connects to RB as a **guest** admitted by B's link (not by GitHub sign-in to B's account), and opens Noise channels to B's linked hosts | Only RB (the target side's operator) sees the cross-account metadata, and RA sees none. Works when RA == RB with the same mechanism. End to end, as today | A second outbound WSS per linked relay. RB must admit a narrowly scoped guest | **Recommended** |

### 7.3 The recommended design: links (F1) over guest connections (R2)

**Link descriptor.** Made by a presence-proven person's device (#391 decision 3(a)), as a person
command–style signed object:

```json
{
  "v": 1,
  "link": "<16 random bytes, base64url>",
  "account": "<A's account id>", "login": "danswett",
  "relay": "https://relay.example",
  "genesis": "<A's roster genesis entry hash>", "generation": "<A's generation>",
  "linkKey": "<P-256 ECDSA public key, SEC1, base64url>",
  "expiresAt": 1795900000
}
```

* `linkKey` is a **dedicated per-link signing key** created on the A person's device that makes
  the link. It signs A's later statements *about this link* (below). It isn't a roster key, so
  B never needs to understand A's roster rules. A's own hosts accept it because A's person's
  device signed a `link.create` person command naming it (#391 §7.2 machinery, presence
  required).
* **Exchange.** Descriptors travel over any channel (a chat, an email, a QR code in a video
  call). They aren't secret.
* **Link code.** Both apps show `code = first 64 bits of SHA-256(F("agent-bridge link v1",
  descA, descB))` in descriptor-sorted order, as four groups of four characters from a word list
  or hex. The owners compare it by voice or video. 64 bits makes grinding a matching descriptor
  infeasible, which a 6-digit code over an untrusted channel would not (an attacker controlling
  the channel could try a million genesis entries). Each side confirms with presence.
* **Recording.** Each side's person's device signs (presence) a **link statement** for its own
  hosts: `{link, peer: descriptor, expose: {…}, reach: {…}, expiresAt, version}`. That is the
  local, authoritative grant (§7.4). Hosts store it beside the roster and apply it the same way
  (#391 §3.7). A higher `version` replaces a lower one.

**Peer host statements.** Each side publishes to the other, signed by its `linkKey`, the
**hosts it exposes to this link**: `{link, version, hosts: [{id, dh, sign, name}], expiresAt}`.
* It **minimizes disclosure.** B sees only the machines A chose to expose, with their names, not
  A's roster, devices, kinds or platforms.
* It is **self-contained to verify.** A's statement verifies under A's `linkKey`, pinned by the link
  code. B's hosts don't need A's roster.
* A's own hosts check, before signing for the link, that the statement agrees with A's roster
  (every listed host is active), so A's person can't be tricked into exposing a removed machine.

**Guest admission (relay change).** RB gains one admission kind:

* B's person's device registers the link with RB: `{link, guests: [A host dh keys], reach:
  [B host ids], expiresAt}`, on its admitted socket.
* An A host connects to RB with `hello { kind: "guest", link, pub }`, then the usual
  proof-of-possession challenge (`RelayHub.AdmitAsync`, `:160-185`). It needs no token and no
  GitHub sign-in on RB.
* RB admits it only if `pub` is a listed guest key of a live link. It routes data frames only
  between that guest and the link's B hosts: no presence beyond them, no push, blobs, roster,
  forget or control messages. It rate-limits per link.
* This is an **honest-relay narrowing**, like #391 §4.5. Hosts never depend on it: B's hosts do
  the real check (§7.5).
* **Same relay.** If RA == RB, the same `link` record lets the relay route an A host to B's hosts
  across accounts, with nothing else changed.

**The envelope across accounts.** It is §4.3's, with `account` = A, `target.account` = B, and
the signature under the A host's `sign` key, which B checks against **A's host statement**, not
A's roster. Option 4.2(b) (MAC) also works, from the `dh` key in the statement, but 4.2(a) is
what lets B's audit prove to B's person what A's host sent.

### 7.4 Scope and consent

A message flows only if **both** owners allowed it: A's **reach** (outbound) and B's **expose**
(inbound).

| | Default | Owner can set |
|---|---|---|
| **Expose: machines** | None | Specific machines of theirs |
| **Expose: sessions** | **None visible** | Only **shared** sessions: those launched in the link's **workspace** (`expose.workspaces: ["Project X"]`), or explicitly shared by the person ("Share with @alex's agents" on a session card). Everything else is `not-found`, the same as nonexistent |
| **Expose: actions** | None | `read`, `reply`. `launch` opt-in, only into the link's workspace, `ask` only, counted against `launchesPerMachinePerHour` / 4 |
| **Expose: read projection** | When read is allowed: `id`, `title`, `agent`, `status`, `statusSince`, `activity.response` (≤ 16 KiB), `waiting: bool`, `driverFrom` | Nothing more in v-full. **Never** `cwd`, `userMessage` (it may hold the owner's private instructions), `reasoning`, tool `history`, `subagents`, `question` content, usage, attachments, or `repository`/`branch` unless the link names that repository |
| **Reach: which of my sessions may send** | **None** | Only sessions launched in the link's workspace, or adopted for the link explicitly. So an agent working in a private repository with access to secrets can't be talked into posting to the collaborator: it has no reach |
| **Reach: actions** | None | `read`, `reply`, `launch` (if the peer exposes it) |
| **Expiry** | 30 days | Renewable with presence. A link that expires stops both directions on each side's hosts without the other's help |

Consent is **asymmetric and local**: each owner's hosts enforce that owner's statement and nothing
the other side says. B can expose read to A without A letting any of A's sessions send.

### 7.5 Verification at a target in another account

§4.7's checks, with these changes:

1. **Channel.** The peer is a guest connection whose static key is a host listed in a **live,
   unexpired** link's peer host statement, and T is listed in this side's own expose statement.
2. **Account.** `account` is the link's peer account, and `target.account` is T's.
3. **Signature** under the listed host's `sign` key.
4. **Freshness of the peer.** T needs a peer host statement version no older than **24 h**
   (re-signed by the peer's link key, or attested by **another** of the peer's listed hosts, the
   same "another device" rule as #391 §6.3). Otherwise the link is stale: reads only, or nothing
   (Q9).
5. **Policy:** this side's expose statement (sessions, actions, projection) and the per-link
   rates, **half** the single-account defaults. `maxDepth` across accounts is **2**: an external
   message may reach one shared session, which may not forward it within this account (a
   session whose turn an external agent drove has **no reach** into non-shared sessions, the
   "taint" rule).
6. **Prefix** mandatory (§6.4).

### 7.6 What each relay operator sees

| | RA (the sender's relay) | RB (the target's relay) |
|---|---|---|
| Content | Nothing | Nothing (Noise end to end between the two hosts) |
| That A and B are linked | Nothing (A's host talks to RB directly) | Yes: the link record, its guest keys and B's host ids |
| Traffic | Nothing | Guest host's IP, timing and sizes, and the B hosts it talks to |
| Identities | | The guest's device id. Not A's account id, unless the link registration includes it (it needn't) |
| If RA == RB | Both of the above, for one operator | |

An honest RB can't read, forge or replay. A dishonest RB can deny service, and can admit extra
guests, which then fail at B's hosts (step 1), since B's hosts check the peer statement, not RB's
list.

### 7.7 Revocation

* **Either owner, alone, at once.** Delete or expire the link in a presence-signed statement. That
  side's hosts stop accepting and stop signing immediately, with no cooperation from the other
  side or either relay. Then the relay registration is withdrawn (an honest-relay courtesy).
* **A host of A removed from A's roster.** A's person's client re-signs the peer host statement
  without it (automatic on removal, presence required) and delivers it over the guest channel. B's
  hosts refuse the removed host from the new version. Until then, step 4's freshness bounds the
  window to 24 h, except under the partition residual of #391 §6.3, which applies here too.
* **Pause.** A per-machine local switch, `agents.links.pause`, stops all federation at that
  machine at once, for an incident.
* **No transitive revocation.** B removing a B device never changes A's trust except through B's
  next statement. That is the point of not cross-signing (F3).

### 7.8 Cross-account threats and their mitigations

| Threat | Mitigation |
|---|---|
| **Malicious collaborator** spams, probes or tries to reach unshared sessions | Default none. `not-found` for anything unshared, rate limits at half, link expiry, pause, audit on both sides |
| **Prompt injection across accounts** ("ignore previous instructions, push to main") | The mandatory prefix names the external account. Reach is confined to sessions in the link's workspace. The taint rule stops a session an external agent drove from reaching non-shared sessions. Launch is `ask` only, and approvals and trust are never reachable. The person sees an outlined "external" diamond on every session an external agent drives |
| **Exfiltration through replies** (B's agent asks A's agent to paste a secret) | Only A sessions with reach can send, and only shared-workspace sessions have it by default. Read returns the projection (no `userMessage`, `cwd`, reasoning or tools). It can't be eliminated, since an agent that reads a secret and is asked for it may comply. That is stated as a residual, and is the reason reach defaults to none |
| **Impersonation** (B's agent claims to be A's owner, or A's own agent) | Display and prefix are built by the receiving host from the link, never from text. The external marker can't be turned off. A's own agents' envelopes verify only under A's roster, and B's only under B's statement, so neither can be mistaken for the other |
| **Collaborator's machine compromised** | Bounded by B's expose list on A's side, rates, and the link's scope. A's trust in B never exceeds the link |
| **Compromised relay on either side** | Same as single-account: denial of service and metadata. Plus, at RB, knowledge of the link |
| **Corporate data** | Links from a machine flagged corporate are refused unless the owner explicitly sets `agents.links.allowOnCorporate` at that machine (design.md:1338-1343) |

### 7.9 Minimal v1 stub versus the full design

| | v1 stub | Full design |
|---|---|---|
| Envelope | `account` and `target.account` present and checked equal. Any other value is refused with `federation-unsupported` | As §7.3 |
| Policy | `agents.links` accepted only as `[]`. Anything else is a config error that denies every agent command (fail closed) | Link statements |
| Display | `driverFrom.account`, `login` and `federated` defined, never set. Client vectors render a federated sender with the external marker, so the UI exists before it is reachable | Live |
| Relay | No change | `guest` admission and link records |
| Projection | Defined in the protocol doc as a named type, unused | Served |
| Tests | A foreign-account envelope is refused, a non-empty `links` denies, and a federated `driverFrom` renders as external | Full matrix |

### 7.10 Full-design slices (after owner approval and a separate security review)

F1: link descriptors, link codes and statements (library and vectors). F2: relay `guest`
admission. F3: host enforcement, projection and taint. F4: app and PWA link pages and the
external marker. F5: spaces as named sets of links (optional).

---

## 8. Threat model

In addition to design.md §5.1 (:348-361) and #391 §2.

### 8.1 Single account

| Party | Assumption | Must not be able to | Can (residual) |
|---|---|---|---|
| **A prompt-injected agent** in a credentialed session | Holds its own credential and follows hostile instructions | Answer an approval or folder trust anywhere. Launch with allow-all. Stop, transfer, update, change settings, adopt, or widen its own scope. Reach excluded sessions. Exceed depth, budget or rates. Appear as the person | Read and reply to sessions the policy allows, and launch `ask` sessions in approved workspaces, within budgets. Each act is attributed, audited and shown violet. That is the owner's chosen reach (decision 2) |
| **Another session's agent** on the same machine stealing a token (from the environment of a process it can read, or a transcript that printed it) | Same OS user | Use it: the ancestry check refuses a token presented from another session, and the token dies with its process (§3.4) | On macOS and Linux, before peer-pid tracing, present it while `AGENT_BRIDGE_SESSION` is spoofed: the same-user residual of local-pipe.md:121 |
| **A manually started session** | No credential | Act as an agent anywhere (decision 3) | Read locally, as today (local-pipe.md:95) |
| **Same-user malware** | Reads keys, environments and stores | | Everything the user can, as today (§1.3). The audit log and signatures make its agent-attributed acts visible, not impossible |
| **A compromised host** | Holds its `dh` and `sign` keys | Act as another machine's agents (it can't sign as their hosts). Pass person-only checks (#391 §7.2). Reach a machine whose policy excludes it (`fromMachines`) | Sign as any principal *on itself*, invent sessions, and lie about chains under 4.2(b). Bounded by targets' per-origin rates and policy, and visible in their audit. Removed via #391 |
| **A removed host** | Keys still held | Be accepted by any host that has applied the removal | The partition residual of #391 §6.3 |
| **The relay** | Untrusted | Read, forge, replay or splice envelopes (Noise, signature, durable replay, channel-peer = signer) | Deny or delay service, observe host-to-host sizes and timing |
| **Clock faults** | A host's clock is wrong | Make a used command run again (durable record, high-water mark) | Make commands fail with `clock` until fixed |
| **Agent loops** | Two well-meaning agents ping-pong | Run unattended past 20 messages or 60 minutes per person-started conversation | Use up to those budgets |

### 8.2 Cross-account

All of 8.1, plus §7.8. The essential difference is that the counterpart **owner** is a party
the design must not trust: consent is per side, scope is minimal by default, the projection hides
what wasn't meant to be shared, external provenance is always visible to both person and agent,
and revocation is unilateral.

---

## 9. Phasing and dependencies

### 9.1 Dependencies

| Dependency | Needed for | Why |
|---|---|---|
| **#55 / PR #391** signed roster: R1 library, R3 host gate and freshness, R6 durable replay | Anything cross-machine (decision 4) | Verifying the issuing host, revocation, freshness, durable single use |
| **#391 amendment: host `sign` key** (Q1 (a)) | Cross-machine under (a) | To be decided before R1 is built |
| **#489 / PR #501** `userMessageFrom` | Attribution of delivered prompts | The field and its parser rules; this design supplies the verified source |
| **#45** MCP server | Agent-facing tools | The consumer of the pipe ops. Not a blocker for the local slice, which can be exercised from `agent-bridge-host agent …` |
| **#46 / #134** driver | Done for the machinery (`SessionDrivers`) | Reused unchanged |
| **#388** headless identity | Not a blocker | Headless clients are *devices* with a `service` role (as #407's floor prefigures). Agent principals are not devices. The shared concern is attribution taxonomy: if #388 adds automation principals, `driverFrom` grows a `kind` (`agent`, `automation`), and `driver` stays `agent` |
| **Peer-pid on macOS/Linux** | Adoption, and the ancestry half of token binding, there | local-pipe.md:129-130 |

### 9.2 Slices

Each slice is one PR, security-reviewed independently before merge.

| # | Slice | Depends on | Crosses machines? |
|---|---|---|---|
| **AP1** | **Smallest first slice: local agent principals.** Credential issued at launch (`AGENT_BRIDGE_TOKEN`, hashed store with process binding), `agent.hello` with ancestry binding, the `agents` policy (`enabled`, `issue`, `inbound.local`, `exclude`, `limits`), local `session.reply` / `session.launch` / reads as an agent connection, chains, budgets and rates for local targets, audit log, `driverPrincipal = agent:<machine>/<session>`, `driverFrom`, prefix, `userMessageFrom = agent` from the verified source, and `agent-bridge-host agent list|read|reply|launch` for tests. Federation stub fields (§7.9). Tests: a token from another session is refused; a manual session gets nothing; an agent can't answer an approval or folder trust; depth and budget refuse; the person's reply resets; a resumed session's old token is dead | #501 merged | **No** (decision 4 holds) |
| AP2 | #45's MCP server on AP1 (stdio), with the old tool names and outcomes | AP1 | No |
| AP3 | Adoption on Windows: app action, CLI, `agent.credential` | AP1 | No |
| AP4 | Envelope library: encoding, signing (a) or MAC (b), strict parser, shared positive and negative vectors | #391 R1 (+ host `sign` if (a)) | Library only |
| AP5 | Cross-machine: `agent.command` host-to-host, verification §4.7, `inbound.remote`, `outbound`, durable replay via R6, `instance` on `Session`, `agent.outcome`, `session.read` wait | AP4, #391 R3 + R6, hosts in `enforce` | **Yes** |
| AP6 | Clients: "from *X* on *Y*" in the app and PWA (extending #503/#504), Settings for the policy, the Agent activity view | AP1 (local), AP5 (remote) | n/a |
| AP7 | Re-enable `inbound.remote` defaults per machine during the owner's cutover (#391 R8) | AP5, R8 | Yes, live |
| F1-F5 | Federation (§7.10) | AP5, owner approval, separate review | Cross-account |

**Why AP1 first.** It delivers the owner's main ask (native, attributed agent messaging) for
sessions on one machine, which is where most agent coordination already happens (the owner's VM
and desk machine each run several sessions). It needs nothing from #55, respects decision 4, and
exercises every rule except the signature, so AP5 adds transport and verification to a policy and
attribution model already proven live.

---

## 10. Open questions for the owner

Each has a recommendation. None is decided by this note.

1. **Q1. What "signs with its device key" means.** (a) Hosts get a P-256 ECDSA `sign` key in their
   roster entry, used only under the agent-command framing label. (b) A designated-verifier MAC
   from the existing ECDH key, as transfers use. **Recommended: (a)**, which gives verifiable
   audit, verifiable chains and clean federation. It must be decided before #391 R1 is built.
2. **Q2. Default remote policy when AP5 ships.** (a) Read and reply to any session, launch `ask`
   in approved workspaces, from any of the account's hosts. (b) Read only. (c) Everything off
   until set per machine. **Recommended: (a)**, matching decision 2, with `allowAll` never
   expressible for agents (design.md §13 item 8).
3. **Q3. May agents answer non-approval questions (`ask_user` forms)?** **Recommended: no in
   v1.** Decision 1 lists read, reply and launch. design.md:495's "answer" for local agents should
   be struck when design.md is updated. Revisit with evidence of need.
4. **Q4. Does a host restart revoke credentials?** (a) No: hashed records survive, bound to the
   session's process id and start time. (b) Yes: memory only (design.md §5.7), and sessions must be
   re-adopted after every host update. **Recommended: (a)**. The store holds no usable secret, and
   (b) would break agent coordination on every update.
5. **Q5. What "adopted" means.** **Recommended:** a person at the machine (app, or the CLI,
   refused inside agent sessions) gives one discovered live session a credential. Windows first.
   Never automatic, and never from a remote device.
6. **Q6. Provenance prefix on same-account messages.** **Recommended: yes by default** (`label:
   prefix`), mandatory cross-account. It is the one signal the receiving *agent* gets.
7. **Q7. Loop limits.** **Recommended:** `maxDepth` 3, 20 messages and 60 minutes per
   person-started conversation, 6 per minute per pair, and launched sessions can't launch by
   default.
8. **Q8. MCP over HTTP.** **Recommended:** no agent principal over HTTP in v1. stdio only, since
   HTTP can't be bound to a session by ancestry.
9. **Q9. Federation trust establishment and transport.** **Recommended:** pairwise links (F1)
   with a 64-bit link code compared by voice or video, each side recording its own presence-signed
   statement, a dedicated per-link key, and host-to-host guest connections on the target's relay
   (R2). Reject cross-signing rosters (F3). Spaces later (F2). A stale peer statement (> 24 h)
   stops the link entirely, not only writes.
10. **Q10. Cross-account launch.** **Recommended:** off by default. Per-link opt-in, only into
    the link's workspace, `ask` only, a quarter of the machine's launch budget.
11. **Q11. Use the trusted-peer floor (#407) as an interim for cross-machine agent messaging?**
    **Recommended: no.** Decision 4 says wait for the roster, and the floor is for disposable
    evaluation (host-config.md:145-148). It may host AP5's tests on disposable hosts only.
12. **Q12. May an agent stop a session?** **Recommended: not in v1** (decision 1). Revisit
    allowing a session to stop sessions *it launched*, which is the useful case.
13. **Q13. Read scope within the account.** **Recommended:** the same `Session` a person's device
    gets (decision 2: "any session"), minus `inbound.exclude`. Cross-account uses the projection
    (§7.4).
14. **Q14. Audit content.** **Recommended:** metadata and hashes only, 30 days. The text stays in
    the target session's transcript. Keeping full text is an opt-in for debugging.
15. **Q15. Corporate machines and federation.** **Recommended:** links refused on a machine the
    owner marks corporate unless allowed at that machine, pending the S8 policy check
    (design.md:1338-1343).
