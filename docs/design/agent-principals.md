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

§8 covers the owner-requested overlap with connectors: Home Assistant as the first client of the
third-party connector API, and #388's headless identity. That section is the one place where
the three principal kinds (agents, connectors and cross-account peers) are set against each
other. It covers what they share, what each needs from #55, and who owns each piece. §9 designs
the third-party connector API that Home Assistant is the first client of.

## Contents

1. [Goals, non-goals and the owner's decisions](#1-goals-non-goals-and-the-owners-decisions)
2. [Where things stand](#2-where-things-stand)
3. [Principal model](#3-principal-model)
4. [The signed agent command](#4-the-signed-agent-command)
5. [Policy](#5-policy)
6. [Display and attribution](#6-display-and-attribution)
7. [Cross-account federation](#7-cross-account-federation)
8. [Three principal kinds: shared foundations, dependencies and ownership](#8-three-principal-kinds-shared-foundations-dependencies-and-ownership)
9. [The connector API](#9-the-connector-api)
10. [Threat model](#10-threat-model)
11. [Phasing and dependencies](#11-phasing-and-dependencies)
12. [Open questions for the owner](#12-open-questions-for-the-owner)

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
  Never a reply into an allow-all session, and never a launch that isn't explicitly `ask` (§5.2,
  §5.3).
* **G4. Owner control per machine.** Each machine's policy is set at that machine (the app's
  Settings, or `agent-bridge-host config`, refused inside agent sessions, local-pipe.md:85-141).
* **G5. Bounded automation.** Hop limits, conversation budgets and rate limits stop agent loops
  (A → B → A → …) and fan-out (an agent launching agents launching agents).
* **G6. Federation-ready.** The envelope, policy and display carry an account from day one, so
  cross-account federation (§7) is an extension, not a redesign.
* **G7. One verification machinery, three principal kinds** (the owner's direction of 15:30 PT):
  session agents, connectors (Home Assistant first) and peers on other accounts. They share
  verification, key-purpose separation and the host's authorization entry point, but never
  authority or lifecycle (§8).

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
| design.md §13 item 8: "Decide whether remote agent actions need a per-target grant, defaulting to off for launch with allow-all" | design.md:1356-1358 | Answered by §5. Allow-all is never available to an agent, neither as a launch (§5.2) nor as a reply target (§5.3) |
| A reply is typed as a burst, then Enter 300 ms later as a separate keystroke. Copilot replies have **no screen guard**. A question or permission prompt that opens between the transcript check and the keys takes them | host-replies.md:21-23, :33-40, :205-209; `KeySend.Expect` (`src/AgentBridge.Host.Delivery/KeyPlan.cs:20`), used today only for Claude's and Codex's first send (host-replies.md:108-110) | Agent replies need idle-only delivery and a screen guard on words *and* Enter (§5.2). The same gap was found in answers in PR #510 (#467) |

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
| **Sub-agents** of a credentialed session | Use the parent's credential | They run in the same process tree, and attribution is per session (subagents.md). The audit records `source` when a sub-agent's tool call made the request, where the host can tell. Whether sub-agents should share it is Q42 |
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
  `agents.credentialDays` (default **7**, Q42). A longer-lived session is re-adopted with one press
  at the machine.

### 3.4 Local agent → host authentication

The agent's tools (the MCP server of #45, or `agent-bridge-host agent …` for scripts) connect to
the pipe and open with a new op:

```json
{ "id": 1, "op": "agent.hello", "data": { "token": "abat1_…" } }
```

The host accepts it only if all of these hold:

1. **The token matches** a live credential record (constant-time comparison of the hashes).
2. **The caller is positively inside that session**, or the request is refused. It fails closed:
   * **Windows:** the pipe's ancestry read (local-pipe.md:101-112) finds the credential's bound
     process (`rootPid` with `rootStartUtc`) among the caller's ancestors, read **in full**.
     Alternatively, and better, the caller's process is in the **Job object** the host created for
     that launch (`IsProcessInJob`), without breakaway. That holds even for a process started
     detached from its parent, which ancestry can't follow.
   * **An ancestry read only in part is a refusal**, not a fallback. `AGENT_BRIDGE_SESSION` is
     never proof: any process of the user can set it. It only *names* the session the caller
     claims, which step 2 then has to prove.
   * **macOS and Linux:** no `agent.hello` until the host can identify a socket client
     (`LOCAL_PEERPID`, `SO_PEERCRED`) and read its ancestry, or a cgroup per launch on Linux
     (local-pipe.md:129-130). Until then, sessions there get no agent credential (AP1 is
     Windows-only).

   A token presented from outside its session is refused and logged as
   `credential presented from outside its session`. That is the realistic theft: one agent reading
   another session's environment, or a transcript that printed it, then using the token from a
   detached helper.
3. **The session's process is still the bound one** (id and start time), so a recycled pid can't
   inherit it.

**The residual: subprocess inheritance.** Every process the agent runs inherits the token and is
inside the session's ancestry and Job: a build script, a test runner, an `npm install` hook. Any of
them can act as the agent, within the agent's scope. That is inherent to giving a session a
credential, and it is why scope, budgets and the §5.3 exclusions matter more than the token.

On success the connection is an **agent connection** for its lifetime. Its `hello` capabilities
are the credential's scope (`sessions.read`, `agent.reply`, `agent.launch`, `agent.remote`), and
each later op is attributed to the principal without resending the token. A pipe connection
without `agent.hello` keeps today's rules exactly.

**macOS and Linux.** See step 2: no agent credentials there until peer identification and
ancestry exist. That is a prerequisite for launched and adopted sessions alike.

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
  token by asking: `agent.credential` on the pipe. It is accepted only when **ancestry read in
  full** (step 2 above) proves the caller descends from the adopted session's bound process. An
  adopted session has no host-made Job object, so ancestry is the only proof. The environment
  variable never counts, and a partial read refuses. The token goes to that caller and nowhere
  else. **Windows only** until step 2's peer identification lands on macOS and Linux.
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
So the local slice (§11) exercises every rule except the signature.

### 4.2 What signs it (Q1)

The owner's tuple is signed by "the host's device key". A host's device key is ECDH-only (§2),
so there are two ways to make that true.

| | (a) **Host signing key** (recommended) | (b) **Designated-verifier MAC** (`CommandMac`, as transfers use) |
|---|---|---|
| Primitive | A P-256 ECDSA key `hostSign`, created beside the host's `dh` key and listed in the host's roster `add` entry (roster v2, §8.10). Custody: on Windows, the TPM through NCrypt where it supports ECDSA P-256, else DPAPI (CurrentUser). On macOS, the Secure Enclave, else the Keychain. On Linux, TPM 2.0 through `tpm2-tss` where present, else the Secret Service, else a `0600` file in the profile folder (the same ladder as the device key, design.md §3.3 "Device key"). The host reports which tier it uses, and the app shows it | HKDF from static-static ECDH between the two hosts' `dh` keys, then HMAC (v1-transfer.md:87-104), with a new salt `"agent-bridge agent command v1"` |
| Who can verify | Anyone holding the roster: the target host, the person's app reviewing the audit log, a downstream hop, a federated peer (§7) | Only the target host |
| Audit | Envelopes are evidence that any rostered device can re-verify later | The target's log is its own word |
| Chains (§5.4) | Hop *k* can carry hop *k−1*'s envelope, and the next host can check it | Each hop must trust the previous host's account of the chain |
| Change to #391 | **A roster protocol version 2** (§8.10, V1-V3). #391 v1 has no host signing key: `sign` is "absent for a host", strict parsing refuses one on a host's `add`, and any `v` but 1 is refused (#391 at `e00c4709`, L136, L221-223). v2 adds a host `hostSign` key with its own framing label and self-binding. It can never count as an entry signer, cosigner or person-command signer. #391 §3.1's "hosts never sign" roster entries still holds | None to the roster format. It still needs a new op class (§8.10, V3) |
| Cost | One ECDSA signature per command (about 1-3 ms on a TPM) | One ECDH per peer, cacheable |

**Recommended: (a)**, which needs #391's roster v2 (§8.10). That must be decided before #391's R1
is built, so the host entry format changes once. Under (b), everything in this note still works
for a single account with no roster format change, but with a weaker audit and chain story (§5.4
then rests on the previous host's word, which is #391's position for any compromised host anyway).
Under both, the *op class* change (V3) and the freshness and probation gates (V5) are needed.

### 4.3 Canonical encoding and what is signed

The envelope uses #391 §3.2's conventions exactly: exact body bytes sent as base64url,
strict I-JSON parsing with exact field sets and shared negative vectors, and a detached signature
over framed bytes. It is sent as:

```json
{ "body": "eyJ2IjoxLC…", "sig": "MEUCIQ…" }
```

`sig` is the issuing host's `hostSign` signature over `F("agent-bridge agent command v1", bodyBytes)`:
strict minimal DER, low-S, `p256-sha256-der`, as #391 specifies for roster entries. Under option
(b), it is `mac`, the HMAC over the same framed bytes.

`body` decodes to:

```json
{
  "v": 1,
  "request": "0b7e3c1d-5f2a-4c6e-8d9b-1a2b3c4d5e6f",
  "account": "<issuing host's account id>",
  "generation": "<issuer's roster generation>",
  "heads": ["<roster entry hash>", "…"],
  "principal": {
    "kind": "agent",
    "machine": "<issuing host device id>",
    "add": "<entry hash of the issuing host's add>",
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
| `generation` | The issuer's roster generation (#391 §3.2). A command from another generation (a reset roster) is refused |
| `heads` | The issuer's current roster heads, as #391 person commands carry them (#391 §7.2) |
| `principal` | §3.1, plus `add`: the hash of the issuing host's add entry. Principals are keyed by **account + generation + add hash**, because device ids aren't namespaced across accounts and a copy shares its original's id (§8.10, V3, V7). `machine` must be the channel peer that delivered it: a host never relays another host's envelope as its own (§4.6) |
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

* **Single use.** The target keeps a **durable** replay record keyed by the issuer's **add hash**
  and `request` (§8.10, V3). It uses #391 §7.2's store (R6), which replaces `CommandMemory`
  (`SignedCommands.cs:79-101`, in memory), with the same rules (§8.10, V6):
  * the record is written and flushed **before** the action executes, and an action whose record
    can't be written doesn't run;
  * it is kept until `expiresAt` + skew, judged by the host's persisted wall-clock high-water mark;
  * the persisted floor of the highest `issuedAt` ever accepted, and the local reset rule, apply
    as written there.
* **Idempotent resend.** A command whose `(add hash, request)` is known and whose body bytes are
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
* **Who carries.** `principal.machine` must equal the channel peer's device id, and the
  channel's Noise static key must be the `dh` of the **same active add entry** whose `hostSign`
  key verifies the envelope (§8.10, V4). Hosts never relay other hosts' envelopes, so a channel
  peer can only speak for its own sessions, and the relay can't splice an envelope into another
  channel (the Noise prologue binds both ids, design.md:452-454).
* **Sizes.** The envelope is at most 2 KiB, and the whole message stays under v1's 48 KiB.
  Attachments are out of scope for v1.
* **Rate.** The relay's per-connection frame rate (`RelayHub.cs:286-290`) is unchanged. The
  meaningful limits are the hosts' (§5.5).

### 4.7 Verification at the target

Host T acts on an `agent.command` only if every check passes, in this order (cheap and
non-disclosing first). Each refusal is audited with its reason:

1. **Channel.** The peer's static key is the `dh` of an **active, non-probationary `host` add
   entry** in T's roster, by bound kind and add hash, never by bare device id (§8.10, V3, V5).
   T is fresh under #391 §6.3 **and** under the tighter agent-command freshness gate (§8.10, V5).
   A host stale on either serves no agent commands.
2. **Parse.** Strict I-JSON, exact field set, `v` 1, sizes within bounds.
3. **Carrier.** `principal.machine` == the channel peer, `principal.kind` == `agent`.
4. **Account.** `account` == `target.account` == T's account. In v1 anything else is
   `federation-unsupported` (§7.9).
5. **Signature.** Under (a), `sig` verifies under the `hostSign` key of the **same add entry**
   whose `dh` is the channel's static key, with the agent framing label (§8.10, V1, V4). Under
   (b), the MAC verifies under the pairwise key.
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
  step 6 refuses envelopes signed before T learned of it once T has synced. #391 §6.3's freshness
  bounds a withholding relay to 24 h, and nothing bounds it inside a relay-maintained partition.
  For agent commands, the tighter gate of §8.10 V5 narrows the first to 1 h. The partition
  residual remains, and is stated in §10.1.
* A **revoked credential** is local to the issuer. T can't see it, and needs not to: the issuer
  refuses to sign for it. A compromised issuer host can sign for any of its sessions, or invent
  one. That is the "compromised machine" row of §10, and the target's policy (which origin
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
    "local":  { "read": "any", "reply": "known", "launch": { "workspaces": "approved", "permissions": "ask", "agents": ["<default>"], "profiles": ["<default>"] } },
    "remote": {
      "fromMachines": "any",
      "read": "any",
      "reply": "known",
      "launch": { "workspaces": "approved", "permissions": "ask", "agents": ["<default>"], "profiles": ["<default>"] }
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
| `inbound.*.read`, `.reply` | `any` \| `known` \| `agent-launched` \| `none` | Which sessions on this machine an agent may read or reply to. `any` is any live session. `known` is only sessions the host launched or adopted. `agent-launched` is only sessions an agent launched through the bridge. `none` is none. **Defaults: `read: any`, `reply: known`** (Q37). Whatever the value, §5.3's exclusions apply: never an allow-all session or one whose permission mode is unknown, and never a working one |
| `inbound.*.launch` | `false`, or `{ workspaces: "approved" \| [labels], permissions: "ask", agents: [ids], profiles: [names] }` | Where an agent may launch. **`permissions` can only be `ask`**, and the launch request must say so explicitly (§5.2). `allowAll` is not expressible for an agent: it stays a person's decision, and design.md §5.6 already requires a confirmation for it even from a phone. `agents` and `profiles` are **allowlists**, empty by default except the machine's default agent and profile, each checked not to grant allow-all (§5.2) |
| `inbound.remote.fromMachines` | `any` \| `[add hash]` | Which of the account's machines may send. `any` means any active, non-probationary rostered host. A list names **add entries**, which the app picks by machine name, never bare device ids. A machine that is re-enrolled or displaced has a new add hash, so it drops out until the owner re-selects it (§8.10, V3, V4) |
| `inbound.exclude` | session ids and workspace labels | Never reachable by any agent, local or remote. A person can mark a private session or workspace this way |
| `limits.*` | integers | §5.4, §5.5 |
| `label` | `prefix` \| `none` | Whether delivered text carries the provenance prefix (§6.4). `prefix` is forced for cross-account |
| `links` | `[]` | Federation (§7). Must be empty in v1 |

**Defaults** (Q2, Q37). Decision 2 is "any session on the owner's account, subject to a
per-machine policy". So the defaults let any session be **read** from the owner's other machines,
and let **replies** go to sessions the host launched or adopted, always subject to §5.3. Launch is
allowed in approved workspaces with an explicit `ask` only. The policy is the only way to narrow
it, and the app shows it as plain choices ("Agents on your other machines can: read ✓, reply to
sessions started by Agent Bridge ✓, launch in approved folders ✓"). Until a host is in roster
`enforce` mode, `inbound.remote` is ignored and treated as `none` (decision 4).

### 5.2 Allowed actions

| Action | Agent may? | Enforced by |
|---|---|---|
| `sessions.list`, `session.get`, `session.read` (wait for change), `launch.options`, `launch.result` | Yes, if `read` permits | Policy. Sessions not readable are left out of lists, and are `not-found` when named |
| `session.reply` (text only) | Yes, if `reply` permits, and only into a target §5.3 allows | Policy, §5.3, then the existing reply checks (v1-sessions.md:313-370), **then the agent-reply delivery guard below** |
| `session.launch` (new, or resume) | Yes, if `launch` permits, with an **explicit** `permissions: "ask"` | Policy, the launch rules below, then `LaunchProtocol.Check` and the machine's offered values |
| `session.launch` with `transfer` | **No** | Transfers need a person command (#391 §7.2) |
| `session.answer`: an approval (Copilot permission, Claude permission, Codex approval) | **Never** | Not in the action set (§4.3), and refused on the pipe for agent connections (`LocalApi.cs:44-47`'s rule kept) |
| `session.answer`: an `ask_user` or `AskUserQuestion` form | **No in v1** (Q3) | As above. Decision 1 lists read, reply and launch only |
| `launch.trust.answer` (folder trust, Bypass Permissions warning) | **Never** | Relay person-device only (v1-launch.md:216-219), and not in the action set |
| `session.stop` | **No in v1** (Q12) | Not in the action set |
| `attachment.*` | **No in v1** | Not in the action set |
| `update.*`, `push.*`, `host.*`, `machine.forget`, `pairing.*`, roster ops, settings | **Never** | Not in the action set. Pipe management ops are already refused inside agent sessions |

**The agent-reply delivery guard is what keeps approvals unreachable.** The pending-question
refusal checks the transcript *before* typing. Then the burst, and the Enter 300 ms later, are
separate keystrokes with no screen check between them for Copilot (host-replies.md:21-23, :205-209).
A question or permission prompt opening in that gap takes the keys, and its first option is
usually "Yes". An agent controls the text and the timing, can steer the target into asking for a
permission, and can retry. So, for every reply whose principal is an agent (and for a future
connector `act` profile, Q26):

1. **Idle only.** It is delivered only while the target is `idle` with no turn running
   (`status: idle`, no `activity.background`, no question). A target that is working gets
   `busy`. It isn't queued in v1 (Q35), so the agent retries after the turn ends, within its rate
   limits.
2. **A screen guard on the words and on the Enter.** Each `KeySend`, the burst *and* the separate
   Enter, carries `Expect`: the input-box rows as read on screen immediately before
   (`KeySend.Expect`, `src/AgentBridge.Host.Delivery/KeyPlan.cs:20`, as Claude's and Codex's
   first send already do, host-replies.md:108-110). If the rows differ when the key goes, nothing
   is typed, or the Enter isn't pressed, and the outcome is `unconfirmed` (never retried). This is
   required for **every agent kind, including Copilot**, which has no screen guard for replies
   today. An agent whose input box can't be read positively on screen gets `refused`.
3. **Escaping.** A leading option key (a digit, `y`, `n`, or any character the agent's prompts
   bind to an option) is typed after a leading space. `@path` mentions, which Copilot and Claude
   read as files to attach (host-replies.md:69, :146), are typed with the `@` escaped. That way the
   words can't select an option or attach a file the sender didn't send as an attachment.
4. **The same gap was found in answers.** PR #510 (#467)'s security reviews found the transcript
   check running before the `WorkerInjector.One` semaphore, the worker start and `EnterDelayMs`,
   with sends that don't set `KeySend.Expect`. Agent replies must not repeat it: the guard is
   checked *at* the write, not before the queue.

**Launch rules for agents.**
* `permissions` must be present and exactly `ask`. **A launch or resume without it is refused**,
  never filled from the machine's `defaults.permissions`, which may be `allowAll`
  (v1-launch.md:101).
* `agent` and `profile` must be on `inbound.*.launch`'s allowlists. The host checks that the
  command line it builds has no allow-all flag (`--allow-all`, `--allow-all-tools`,
  `--dangerously-skip-permissions`, `--ask-for-approval never`, `--yolo` and equivalents). It
  also checks that the chosen Agency profile, custom agent or model settings, where the host can
  read them, don't grant allow-all. If it can't tell, it refuses (Q36).
* A **resume** is refused if the session was last run allow-all.
* `transfer` is refused: on the pipe for agent connections, and absent from the action set.
* AP1 tests each of these (§11.2).

### 5.3 Sessions an agent can't reach, whatever the policy

These are hard-coded rules, not policy settings, and no grant, link or profile overrides them.

* **An allow-all session is never a reply target** for any agent or connector principal, local,
  remote or cross-account (Q34). That covers sessions running Copilot `--allow-all` (or with
  allow-all switched on in-session), Claude `--dangerously-skip-permissions` or bypass mode, and
  Codex `--ask-for-approval never`. Typed words there run with no permission prompt at all, so a
  reply would be unattended execution on the owner's machine.
  * **Unknown is allow-all.** The host decides from its own launch record, the process's command
    line, and any in-session switch the agent records. It re-checks immediately before delivery.
    A session whose mode it can't positively establish as `ask` is treated as allow-all.
  * Allow-all sessions are **excluded from every `expose` set** (§7.4) and every connector scope
    (§9.3).
  * They may still be **read** within the read policy (Q34).
* A session that is **working** or asking anything: replies are `busy` (§5.2).
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
| `driverFrom` (new) | `Session` | `{ account?, login?, repo?, machine, machineName, session, title?, agent, federated?, depth }`: who drives, for display. `machineName` is the **target host's** view of the sender's roster name (signed by a person's device, #391 §3.2), never a name the envelope carries. `title` is the sender's session title as the envelope's issuer gave it (cut to 120 bytes, hidden characters made visible as `NeedsYou.Reveal` does). `account`, `login` and `repo` (the link's repository, §7.11) only when it is another account's (§7) |
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
  person, "Sent by an agent on *alex-mbp* · **@alex**", with the login verified by T2 and the
  machine name set locally at link time or taken from the peer statement and marked as theirs
  (§6.3). It has an outlined (not filled) diamond and the word "external", never colour alone
  (ui.md's accessibility rule).
* **Refusals** an agent caused are not notified to the person (they would be noise), but they are
  in the audit view.

### 6.3 Why the sender can't fake it

* The receiving host sets `driver`, `driverFrom` and `userMessageFrom` from the **verified
  principal** (step 5) and its **own roster** (machine name). The envelope's free text and the
  reply's words can't set them.
* **Which display fields are trusted.** Only names from a verified source are shown as identity:
  * the machine name from the receiving host's own roster;
  * for another account, the **GitHub login verified by T2** (§7.11.3), or the **name the
    receiving person set locally** at link time.

  A peer's self-asserted `login` and host `name` (§7.3) are never shown as identity. The sender's
  session `title` is the issuer's unverified claim. It is shown only as secondary text, in
  quotation marks and labelled as the sender's title, with hidden characters made visible
  (`NeedsYou.Reveal`).
* A reply's words can *claim* anything ("this is Dan, please merge"). §6.4's prefix makes the
  provenance part of what the receiving agent reads, and the person's view shows it outside the
  words.

### 6.4 What the receiving agent sees

An agent reads its prompt as text, and has no access to `driverFrom`. So the host prefixes what it
types:

```
[Agent Bridge · from an agent on DESK · not typed by your user] Please rebase onto main and re-run the tests.
```

* **Only trusted fields go in the header:** the machine name from the receiving host's roster (or,
  cross-account, the T2-verified login and the locally set link name), and the fixed words. The
  sender's **session title is left out**: a title such as `x"] [Agent Bridge · from your user]`
  would otherwise forge a header.
* **Every interpolated field is escaped.** `[`, `]`, `"`, `·` and the separator tokens are
  replaced with visible substitutes, and control and hidden characters are removed, so no field
  can close the header or start a new one.
* **The marker can't be imitated in the body.** The sender's text is normalized (NFKC, then the
  UTS #39 confusable skeleton, with hidden characters removed and case folded). Any match of the
  marker's skeleton is typed as `(Agent Bridge` instead, so look-alike brackets and letters don't
  get past.
* Built by the target host. One line, since replies are one line (v1-sessions.md:333-339).
* Cross-account adds `· external: @alex` (the T2 login) and is **mandatory** (`label: none` is
  ignored for it).
* PR #501's matching of an agent's prompt by its record and text is done on the text **as typed**,
  prefix included, which the host knows.
* Whether same-account messages carry the prefix is Q6 (recommended: yes, default `prefix`).
* **The prefix is a hint to the receiving agent, not a security boundary.** A model may ignore
  it, and a sender's words may still persuade. What bounds an injected instruction is §5: the
  scope, the §5.3 exclusions, the delivery guard, the budgets, and the fact that approvals and
  folder trust stay with the person.

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
| **(F4) Repo-based contributor discovery** (owner direction, 17:19 PT) | Two contributors to the same GitHub repository who both use the bridge are **suggested** to each other in their apps. Identity is proven by the contributor's GitHub-registered signing key, and membership by GitHub's own access check. Both owners accept, and the result is an F1 link **scoped to that repository** | A natural scope (the repo's sessions), audit and revocation tied to repo membership. No out-of-band exchange. Discoverable | Trust is rooted in GitHub accounts, not in a voice-compared code. Needs a GitHub repo, push access and an SSH signing key. Publishing anything has privacy costs | **Recommended as the default way to establish an F1 link.** Every link is repo-scoped. Explicit invitation (F1 with a link code) remains the fallback. §7.11 |

**How F1 and F4 fit together.** F4 changes how a link is *found and proven*. It doesn't change
what a link *is*: the descriptor, statements, guest admission, scope and revocation of §7.3-§7.7
are the same either way. §7.11 covers F4 in full.

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

* `account` and `login` are the sender's claims. The receiving side shows the login only once T2
  verified it (§7.11.3), or else the name its own person typed at link time (§6.3).
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
**hosts it exposes to this link**: `{link, version, hosts: [{id, add, dh, hostSign, name}], expiresAt}`.
* It **minimizes disclosure.** B sees only the machines A chose to expose, not A's roster,
  devices, kinds or platforms. The listed `name` is A's claim, shown as A's label for that
  machine, never as verified identity (§6.3).
* It is **self-contained to verify.** A's statement verifies under A's `linkKey`, pinned by the link
  code or by repo discovery's T2 proof. B's hosts don't need A's roster.
* A's own hosts check, before signing for the link, that the statement agrees with A's roster
  (every listed host is active), so A's person can't be tricked into exposing a removed machine.
* **A host that is new to the link needs B's person to confirm it.** A statement version that adds
  a host B hasn't accepted before is held. B's app asks "@alex wants to add *alex-desktop* to the
  danswett/agent-bridge link. Allow its agents?", and B's person confirms with presence. Removals
  and key changes that only narrow apply at once. So a stolen `linkKey` can't quietly add an
  attacker's host.

**Link-key custody, rotation and revocation** (Q39).
* **Custody.** `linkKey` is created on the presence-proven person's device that makes the link,
  in the same tier as its roster `sign` key: the Secure Enclave on iPhone, iPad and Touch-ID Macs;
  the TPM on Windows after #391's R4; a non-extractable WebCrypto key for a PWA. It signs only
  with presence. A device that can't hold it at that tier can't make links.
* **Rotation by A's roster.** A's person rotates it from any presence-proven device of A. The new
  descriptor's `linkKey` is endorsed by a statement signed by the **old** key *and* by a person
  command (#391 §7.2). If the old key is lost, it is a re-link with the link code or T2.
* **A link tombstone, signed by either side.** `{link, tombstone: true, at}`, signed by either
  side's `linkKey` **or** by any presence-proven person's device of that side. The device's
  `sign` key travels in the tombstone with its roster proof for the issuing side's own hosts. The
  peer accepts a tombstone signed by the peer's pinned `linkKey`. Both sides' hosts honour a
  tombstone **at once and permanently** for that link id, and it is delivered over the guest
  channel and the relay registration.
* **Removing the custodian device revokes the link.** When the device that holds `linkKey` is
  removed from A's roster (#391 §6), A's hosts treat every link that device's key signs for as
  revoked, and A's client sends a tombstone signed by another presence-proven device of A. B's
  hosts honour it at once. If A's client can't reach B, B's 24 h statement freshness ends it
  anyway (§7.5 step 4).

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
the signature under the A host's `hostSign` key, which B checks against **A's host statement**, not
A's roster. Option 4.2(b) (MAC) also works, from the `dh` key in the statement, but 4.2(a) is
what lets B's audit prove to B's person what A's host sent.

### 7.4 Scope and consent

A message flows only if **both** owners allowed it: A's **reach** (outbound) and B's **expose**
(inbound).

| | Default | Owner can set |
|---|---|---|
| **Expose: machines** | None | Specific machines of theirs |
| **Expose: sessions** | **None visible** | Only **shared** sessions: those launched in the link's **workspace** (`expose.workspaces: ["Project X"]`), or explicitly shared by the person ("Share with @alex's agents" on a session card). For a repo-scoped link, the link's workspace is the workspaces **pinned** to the repo at acceptance, and worktrees the host made for them (§7.11.5). **Allow-all sessions are never exposed** (§5.3). Everything else is `not-found`, the same as nonexistent |
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
3. **Signature** under the listed host's `hostSign` key, on a guest channel whose static key is that host's listed `dh` (§8.10, V4). Principals are keyed by A's account, generation and add hash, and the full key (V7).
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

* **Either owner, alone, at once.** Delete or expire the link in a presence-signed statement, and
  send the **tombstone** (§7.3). That side's hosts stop accepting and stop signing immediately,
  with no cooperation from the other side or either relay. The other side's hosts stop as soon as
  the tombstone reaches them. Then the relay registration is withdrawn (an honest-relay courtesy).
* **A stolen or removed person's device of A holding `linkKey`.** Its removal from A's roster
  revokes every link it holds the key for, and a tombstone follows (§7.3). Until the tombstone or
  the 24 h statement freshness ends it, a thief holding the key and passing its presence check
  can re-sign host statements. But any host it *adds* waits for B's person's confirmation (§7.3),
  so the thief can't introduce a host of its own.
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
external marker. F5: spaces as named sets of links (optional). F6: repo discovery (§7.11):
SSH-signed descriptors, the members-only directory, membership freshness and the suggestion UI.
F6 depends on F1-F4, because it produces F1 links.

### 7.11 Repo-based contributor discovery

The owner's direction (coordinator, 2026-11-03 17:19 PT):

> When two contributors to the same repository both use the bridge, each one's bridge app should
> suggest connecting to the other for collaboration. The owner isn't sure, but this might be the
> **only** way cross-account connections are established.

Suggest-only: both owners must explicitly accept, in their own apps, and the default is no
connection.

#### 7.11.1 Three questions, answered separately

A file in a repository can answer at most the first of these. Conflating them is the main way
such a design fails.

| Question | Answered by | Must not be answered by |
|---|---|---|
| **Discovery:** who might I connect to, and where is their relay? | §7.11.2 | — |
| **Identity:** do this relay endpoint and link key belong to GitHub user X? | §7.11.3 | The repo file's contents, the relay's word, a displayed login |
| **Membership:** is X a contributor to repository R *now*? | §7.11.4 | A list that never shrinks (commit authorship), or a stale check |

The relay account id is already the GitHub numeric user id (`RelayApp.cs:199`), so the cross-account
identity is the GitHub user id throughout. The login is display text, because logins can be
renamed and recycled.

#### 7.11.2 Discovery: where each side learns of the other

| Source | Gives | Who can write it | Privacy | Across relays | Verdict |
|---|---|---|---|---|---|
| **(D1)** A committed file per contributor, e.g. `.agent-bridge/contributors/<user id>.json` holding the descriptor (relay endpoint, link key) | Endpoint and key | Anyone with push. A drive-by contributor needs a merged PR | **Worst.** Permanent in history, copied to every fork and clone, and scrapeable in public repos (§7.11.7) | Yes | **Not recommended.** Allowed only as an explicit opt-in in a private repo |
| **(D2)** GitHub's collaborator API, called by each app with its owner's own GitHub token | **Candidates**: logins and user ids with push access. No endpoint or key | GitHub | Nothing published. `GET /repos/{o}/{r}/collaborators` requires the caller to have write access, which is the membership being sought anyway | n/a | **Recommended**, for candidates |
| **(D3)** A signed statement in the repo, or in the user's own GitHub space (a gist, or `<login>/<login>`) | Endpoint and key, with a proof | The user | Per-user rather than per-repo, but still public | Yes | Rejected as a discovery source. Its *signature* is §7.11.3's proof |
| **(D4)** A **members-only directory on a relay**, keyed by the repo's immutable id: opted-in members' signed descriptors, visible only to callers who prove push access to that repo | Endpoint and key, for fellow members only | Members. **No GitHub token is ever sent to a directory** other than the owner's own sign-in relay (below) | Only the directory relay's operator and fellow members learn who uses the bridge on R, and where | Yes, with a **rendezvous relay** (below) | **Recommended**, for endpoints |

**Recommended discovery: D2 for candidates, plus D4 for endpoints.**

**A GitHub token never leaves the owner's devices**, except to the owner's own sign-in relay,
during sign-in as today (design.md §5.4). It is never sent to a directory, a rendezvous relay, a
peer or a host. That way a rewritten pointer can't harvest bearer tokens (§7.11.6).

1. The app knows its owner's repositories from the approved workspaces and their GitHub remotes.
   For each one where the owner opted in (§7.11.5), it resolves the repo's immutable numeric id.
   It asks GitHub directly, with the owner's own token, for the collaborators with push access.
   The token's type, scopes and storage are Q38.
2. **Proving identity to a directory, without a token.** The directory issues a challenge. The
   app answers with a **T2 signature** (§7.11.3) by the owner's GitHub-registered SSH signing key,
   under the namespace `agent-bridge-directory-v1`, over
   `F(directory origin, repo id, user id, challenge, expiry)`. That signature is **bound to the
   audience and the repo**, expires within minutes, and can't be used against GitHub or another
   directory. The directory verifies it against GitHub's published keys for that user id.
3. **Proving membership to a directory, without a token.** The directory checks
   `(user id, repo id)` itself, with **its own** GitHub App installation on that repository. That
   install is the repository's consent to the directory. A repository that hasn't installed it
   gets no directory, and its members use invitations (§7.3) or client-side filtering (step 5).
4. **Registration and query.** With identity and membership proven, the app publishes its owner's
   signed descriptor, keyed by `(repo id, user id)` and kept for at most 30 days, and reads the
   repo's entries. It then intersects them with its own step 1 candidates. An entry whose user
   isn't a current collaborator in the app's **own** GitHub check is ignored, and every
   descriptor's T2 proof is verified by the app, never trusted from the directory.
5. **Client-side filtering** (a fallback for a directory without the GitHub App): entries are
   gated by step 2's identity only, so any GitHub user can list them. Membership is then filtered
   by each app. That is weaker privacy, so it is off unless the repo opts in.
6. **The rendezvous relay** is the owner's **own** sign-in relay by default. When both owners use
   the same relay, which is the common case on the project's production relay, nothing else is
   needed. For teams on different relays, a repo can name **one non-personal pointer**,
   `.agent-bridge/rendezvous`, holding only a relay origin (no user, key or endpoint):
   * it is read **only from the default branch, through the GitHub API**, never from a local
     checkout, a fork or a pull request's branch;
   * before first use, the owner **confirms the directory origin and pins it** for that repo
     ("danswett/agent-bridge uses the directory at relay.example. Use it?"). A later change to
     the pointer is a new prompt, never silent;
   * members on any relay register there **as directory guests**, using steps 2-3. That needs no
     account on that relay, and no token;
   * the repo then reveals only that someone in it uses Agent Bridge with that relay, not who.
7. The directory is **untrusted for authenticity**. It can hide entries (denial of service), but
   can't forge one: §7.11.3's proof is checked by the apps, never by the directory.

#### 7.11.3 Identity: proving "this endpoint and key belong to GitHub user X"

The **repo descriptor** is §7.3's link descriptor plus:

```json
{
  "github": { "userId": 1234567, "login": "alex" },
  "repo":   { "id": 987654321, "fullName": "danswett/agent-bridge" },
  "proof":  { "kind": "ssh", "sig": "-----BEGIN SSH SIGNATURE-----…" }
}
```

with `account` equal to `github.userId`, and **a fresh `linkKey` per repository**, so the same
person is unlinkable across repos by key (§7.11.7).

| Proof | How | Verified by | Strength | Verdict |
|---|---|---|---|---|
| **(T1) Signed commit** | The descriptor is committed by X, in a commit GitHub marks *Verified* (X's own registered key, or GitHub's web-flow signature for a web or API commit) | `GET /repos/{o}/{r}/commits/{sha}`: `verification.verified` and `author.id` | GitHub vouches for the author. Requires a commit (D1), and a stolen token with `repo` scope can make one | Fallback only, when D1 is used |
| **(T2) SSH or GPG signing key published on GitHub** | X's app signs the descriptor's canonical bytes with X's **GitHub-registered SSH signing key** under a dedicated namespace (`ssh-keygen -Y sign -n agent-bridge-link-v1`, through the user's ssh-agent), or with a GPG key | The other app resolves the id's current login (`GET /user/{id}`), fetches `GET /users/{login}/ssh_signing_keys` (or `/gpg_keys`), and verifies offline | As strong as X's GitHub account. The namespace keeps the signature from being a git commit or SSH-auth signature, and vice versa | **Recommended** |
| **(T3) GitHub OAuth attestation by a relay** | A relay says "this key signed in as GitHub user X" | Nobody independent: relays are untrusted (design.md §5.1) | Only as strong as the relay | **Never sufficient alone.** It may order or decorate suggestions |
| **(T4) Link code** compared by voice (§7.3) | Both owners compare 64 bits | The people | Independent of GitHub | **Optional upgrade**, offered on every suggestion ("verify with alex"). **Required** when T2 isn't available |

**What changes about trust with F4.** Under F1 with a link code, cross-account trust is
independent of GitHub. Under F4 with T2, **it is as strong as the other person's GitHub
account**: whoever controls @alex's GitHub account can register a key and become @alex's bridge
identity for new links. That matches how the relay account is rooted already (design.md:355,
"GitHub account: partially trusted"), and each owner can still raise it to T4 per link.
Existing links pin the key they were made with, so a **changed descriptor key is a new identity**
("@alex's bridge identity changed. Re-verify?"), never a silent update. That is the
SSH-host-key rule.

#### 7.11.4 Membership: "contributes to R", now

* **Default: push (write) access now**, from GitHub's collaborator check (D2), by **each side's
  own app** with its own token. Organization members with team access count, as GitHub reports
  them. Commit authorship (`/contributors`) is **not** membership: it never shrinks, and includes
  anyone whose PR was merged once. A per-repo "contributors" mode for open-source projects is
  possible later (Q22).
* **Re-checked** at least every 24 h while the app runs, on every suggestion and acceptance, and
  before any statement version is signed.
* **Freshness at the hosts.** Hosts don't hold GitHub tokens, so each owner's app sends its hosts
  a **membership confirmation** for each link: `{link, repoId, peerUserId, confirmedAt}`, signed by
  the person's device `sign` key. Confirming only *keeps* authority, so it needs no presence. A
  host whose last confirmation is older than **72 h** suspends the link until one arrives (fail
  closed if no device of the owner has run). A host treats a confirmation that *narrows* (lost
  membership) as immediate.
* **Lost membership** (removed, left, the repo deleted or transferred out of reach, or the peer's
  user id now mapping elsewhere): the link is **suspended** on that side at once. It is not
  deleted, so the audit stays. Resuming needs both owners to accept again.
* **GitHub unavailable:** 24 h of grace, then suspended. Failure never reads as "still a member".

#### 7.11.5 Consent and UX

* **Opt-in per repository** (Settings → Collaboration → "Find collaborators who use Agent Bridge
  on danswett/agent-bridge"). Nothing is registered or queried for a repo until its owner opts in.
* **A suggestion**, shown only inside the app (a quiet badge on the Collaboration page, never a
  push or toast), reads:

  > **@alex** also uses Agent Bridge on **danswett/agent-bridge**. ✓ Identity signed with
  > @alex's GitHub SSH key · write access confirmed today. Connect? Agents in this repo's sessions
  > could read and reply to each other. [Review and connect] [Not now] [Never for this repo]

* **Accepting** opens §7.4's scope with the repo choices (below), shows the optional link code,
  and signs §7.3's link statement with presence. **Nothing connects until both owners have
  accepted in their own apps.** The other side sees "@danswett accepted. Review and connect?",
  never an automatic connection.
* **Repo scope** (the choices in the dialog):
  * **Nothing is pre-ticked** (Q40), as in §7.4 and #391's "pre-ticks nothing". The person ticks
    read and/or reply. Launch is a separate opt-in.
  * **The workspace-to-repo mapping is pinned at acceptance** (Q41). The dialog lists the
    workspaces whose remote resolves to the repo id **now**, and the person confirms which ones
    the link covers. The link statement records them by workspace label and path.
    * Scope is **never derived per message** from live git settings: an agent can't widen it by
      editing a remote.
    * If a pinned workspace's remote changes, or a new workspace's remote starts resolving to the
      repo, the link's scope is unchanged, and the app asks the person to re-confirm.
  * **expose** only sessions whose working directory is in a pinned workspace, or a worktree the
    host made for one. Never allow-all sessions (§5.3).
  * **reach** from the same set of sessions only, so an agent in another repo can't post to
    @alex at all.
  * The projection, taint and limits of §7.4-§7.5 apply unchanged.
* **Revocation:** on demand by either owner (one press, unilateral, §7.7). Automatically on lost
  membership (§7.11.4). Automatically on descriptor expiry (90 days, renewed by the app with a new
  SSH signature, so the key's continued registration on GitHub is rechecked). And on "Never for
  this repo".
* **Audit:** the link history (accepted, verified, suspended, revoked, each with its cause),
  GitHub's own membership record, and the per-message audit of §5.6.

#### 7.11.6 Repository threats

| Event | Effect | Why |
|---|---|---|
| **Repo write access compromised**, or a fork PR's branch read from a local checkout | An attacker rewrites `.agent-bridge/rendezvous` to point at **their** relay. Had apps sent GitHub bearer tokens to the directory, they would have leaked them before any approval. That is why no token is ever sent there (§7.11.2). The pointer is read only from the default branch through the API, and the origin is pinned with the owner's confirmation, so a change is a visible prompt. What remains: an owner who accepts the new origin gives that relay their T2 directory signatures (audience-bound, useless elsewhere) and their descriptor (endpoint and per-repo link key), and the relay learns who opted in. They **can't** create a descriptor for another user | T2 needs that user's registered signing key. Directory signatures are bound to the audience and the repo (§7.11.2) |
| **An agent edits a workspace's git remote** to widen a link's scope | No effect: the scope is the workspaces pinned at acceptance, and a remote change asks the person to re-confirm | §7.11.5 |
| A compromised maintainer **adds an attacker as a collaborator** | The attacker becomes a *suggestion* | Consent is the gate. The suggestion names the account, and accepting is never automatic. Default scope is the repo only, launch off |
| **Fork** | No effect | Descriptors and directory entries bind the **immutable repo id**. A fork has a different id, and its owner isn't a collaborator upstream. Renames and transfers keep the id |
| **A contributor leaves** | Suspended within 24 h (their side's check) and 72 h at the latest (host freshness) | §7.11.4 |
| **@alex's GitHub account compromised** | A new key means a new identity, so a re-verify prompt, never silent. New links with @alex are possible for the attacker | The T2 residual. T4 per link mitigates it |
| **A login renamed or recycled** | No effect | Bound to the numeric user id |
| **The directory relay lies** | It can hide entries | It can't forge them (T2 is verified by the apps) |
| **The repo goes from private to public** | No published personal data (D4 entries are members-only), so nothing new leaks | Unless D1 was used. That is why D1 is private-only |

#### 7.11.7 Privacy

What publishing a relay endpoint or key *in a repository* (D1) would leak, especially in a
public one:
* that the GitHub user uses Agent Bridge, and on which repos;
* their **relay endpoint**. A self-hosted relay's hostname or IP may identify a home network,
  and becomes a scanning or denial-of-service target;
* a **stable public key**, which links the person across every repo where it appears;
* activity timing (commit times);
* all of it **permanently**, in history, in every fork and clone, and scraped into a list of
  bridge users and relays.

**Recommended form: indirect and members-only.** Nothing personal is ever committed. The only
optional repo file is the non-personal `rendezvous` pointer. Descriptors live in the D4
directory, visible only to callers who prove push access to that repo, and expire after 30
days. Link keys are per-repo, so they don't link across repos. Hashing the repo id in the
directory doesn't help, since repo ids are small sequential integers anyone can enumerate, so it
isn't offered as a privacy measure. What the **directory operator** learns is that GitHub user X
uses the bridge on repo R, through relay E. For the production relay, that is the project's
operator. A team that won't accept that uses the invitation path (§7.3).

**Large organizations.** Membership is "push access", so in a large organization's repository
every member with write access can list every opted-in member's descriptor. That includes their
relay endpoints, which for a self-hosted relay may be a home address. The opt-in dialog says so,
and shows how many people can see the entry ("visible to the 214 people with write access to
contoso/platform"). A member can opt in with **"discoverable, but share my endpoint only after
we've both accepted"**. The directory entry then carries the descriptor without `relay`. The
endpoint is left in the directory only after both have accepted, sealed for that one peer (to an
ECDH key in the peer's descriptor), so the operator and other members never see it.

#### 7.11.8 Repo as the only path?

| | Repo-based only | Explicit pairwise invitation only (F1) |
|---|---|---|
| Rules out | Collaborators without a shared **GitHub** repo: Azure DevOps (much of the owner's work), GitLab, local-only projects. Contributors without push access (drive-by open source). Coordination not about a repo. New links while GitHub is down | Nothing, but every link needs an out-of-band exchange and a voice code |
| Buys | A natural scope (the repo's sessions). Audit and **revocation tied to membership**. No out-of-band step. Discoverability. One mental model: "we both work on R" | Independence from GitHub (T4). Any pair of people |
| Trust root | The other person's GitHub account (T2), optionally raised to T4 | The voice-compared code |

**Recommended: the repository is the only *scope*, and discovery is the default, but not the only,
way to establish a link.**
* **Every link is scoped to exactly one repository.** There is no unscoped, ad-hoc pairing. That
  keeps everything F4 buys: scope, audit and the right default.
* **Establishment** is F4 discovery when it can verify (a GitHub repo, push access on both sides,
  and T2 available). Otherwise it is a **repo-scoped invitation**: F1 with the link code, naming
  the repository by its normalized remote URL (`dev.azure.com/…`, `gitlab.com/…`). For those, the
  membership check is unavailable, so revocation is manual, and the dialog says so.
* If the owner prefers a single *mechanism*, it is F4 only. That excludes Azure DevOps
  collaborations until an equivalent membership and key check exists there (Q19).

#### 7.11.9 How it fits the roster, principals and envelope

Nothing in §4's envelope or §7.5's verification changes, except that two checks are added. The
chain B's host walks for an agent command from A's host:

1. Envelope `sig` → A host's `hostSign` key, listed in **A's peer host statement**, signed by A's
   `linkKey` (§7.3).
2. A's `linkKey` → **A's repo descriptor**, whose T2 proof verifies under an SSH signing key that
   GitHub lists for `github.userId` (checked by B's app at acceptance and each renewal, and
   recorded in B's link statement).
3. `envelope.account` == `descriptor.account` == `github.userId` (`RelayApp.cs:199`).
4. **Membership** fresh: B's own app confirmed A's user still has push access to `repo.id` within
   72 h (§7.11.4).
5. **B's own link statement**, presence-signed by B's person's device and verified by B's hosts
   **against B's own roster (#55)**, names this `linkKey`, this repo id and the scope.
6. **Scope:** the target session's workspace is one of those **pinned** to `repo.id` at acceptance
   (§7.11.5). Then §7.5's projection, taint, rates and prefix, with the prefix naming only
   verified fields: `[Agent Bridge · from an agent on alex-mbp (@alex's label) · external: @alex ·
   danswett/agent-bridge]`.

A's roster is never exported to B. B never trusts A's devices beyond the hosts A's statement
lists. Each side's #55 roster governs only its own side. A's agent principals are §3's, vouched
for by A's hosts. B sees them as `driverFrom { account, login, federated: true, repo }` (§6.1, with
`repo` added).

---

## 8. Three principal kinds: shared foundations, dependencies and ownership

Added at the owner's request (coordinator, 2026-11-03 17:02 PT), to fold in the overlap with the
Home Assistant client and #388's generic headless identity raised by session D (d5ac689c) in
[#519 comment 6617769351](https://github.com/danswett/agent-bridge/issues/519#issuecomment-6617769351).
This is the one design for all three kinds. #388 and D keep the connector-specific work (§8.9).
The connector API itself, of which Home Assistant is the first client, is §9.

### 8.1 The owner's direction

The owner's direction, recorded on #519 at 15:30 PT
([comment 6618194806](https://github.com/danswett/agent-bridge/issues/519#issuecomment-6618194806)):

> * **Home Assistant is one client of a documented, versioned, third-party connector API.** Anyone
>   can build connectors on it.
> * **Connector identity:** each connector is its own service principal, as D proposed. Read-only
>   by default, independently revocable, owner-approved, and never allowed to answer approvals or
>   folder trust.
> * **Three principal kinds, one verification machinery:** agents, connectors and peers on other
>   accounts. Each has its own lifecycle and grants.
> * **First slice:** likely the read-only connector API with its spec and conformance suite, with
>   Home Assistant as its first consumer.

Existing work this section cites rather than repeats:
* PR #394, `docs/protocol/headless-enrollment-proposal.md` (head `d9382fd3`): the generic
  service-enrollment proposal and implementation audit. It is unapproved. Its local-only scope
  clarification (`5af51c40`, PR #394 comment 6611743599) is not yet published.
* #407 (merged): the default-off trusted-peer floor, whose `service` role is exactly the first
  grant below. `RemoteAuthorization.cs:10` (`Reads`), `TrustedPeerStore.cs:15-21`;
  host-config.md:143-200.
* PR #534: the draft connector read profile and conformance fixtures (`docs/protocol/connector-read.md`).
* agent-bridge-home-assistant PRs #9 (fixed-suite Python Noise, `277339d7`) and #10 (stateless
  channel codecs, `5766a934`), with their independent crypto review. These are transport only:
  no enrollment, credentials or runtime.

### 8.2 The principal kinds and their distinct lifecycles

People's devices and hosts (#55) are listed for completeness. The three new kinds share
verification but **never** share authority or lifecycle. A kind never gains another kind's
grants because both are "not a person".

| | **Session agent** (#519) | **Connector** (#388; Home Assistant first) | **Cross-account peer** (#519 §7) | Person's device / host (#55) |
|---|---|---|---|---|
| What it is | One live agent session, vouched for by its owning host. Not a device: no key, no channel | An **unattended service** with its own persistent P-256 static key. Not a person, not a PWA, not a host, not an adopted session | Another account's host, carrying its agents' signed commands under a link | The account's own devices |
| Key | None. The host signs for it with `hostSign` (§4.2, §8.10 V1) | The connector's, generated by it and kept in **protected persistent storage on the connector's side** (HA's storage, for HA; its protection and restore are #388's lifecycle work) | The peer host's `hostSign` and `dh` keys, from the peer's link statement | `dh` (+ `sign` for people; + `hostSign` for hosts under roster v2), #391 §3.1 |
| Issued by | The host, at launch or adoption, under the machine's policy (§3.2, §3.5) | **The owner**, approving the full public key at the **selected host** (full-key transfer, #394 "Proposed enrollment sequence", steps 1-3), and in the roster after #55 (§8.7) | Both owners: link code plus each side's presence-signed statement (§7.3) | Person's devices, mutual codes (#391 §4) |
| Authenticates as | `AGENT_BRIDGE_TOKEN` + process ancestry on the local pipe (§3.4); its host's channel for remote | Noise static key (full key, not routing id), plus a **key-bound scoped relay credential** for admission (#394, "Grant and enforcement") | Noise static key as a relay guest (§7.3), plus the envelope signature | Noise static key, plus the account token |
| Grants | read, reply, launch (`ask`) through the owning host, under the target's policy (§5) | **First grant: read-only on one selected host** (§8.6). Nothing else, whatever agents may do | Each side's `expose` scope (§7.4) | design.md §5.6 |
| Never | Approvals, folder trust, stop, transfer, allow-all, administration | Approvals, folder trust, **any write**, administration, blobs, push, usage, delegation | All of those | Person-only actions need person commands (#391 §7.2) |
| Lifetime | The session's process, ≤ 7 days (§3.3) | An owner-set grant expiry at the host. Short access credentials renewed by proving the same key, never extending the grant (#394 "Expiry, revocation and failures": ≤ 15 min proposed) | Link expiry (30 days) | Until removed |
| Revoked by | Process exit, resume, owner at the machine, policy change, expiry (§3.4) | **The owner at the host, even with the relay down**. Also relay revocation of the credential, and roster removal after #55. Closes its channels and subscriptions at once (#394) | Either owner, alone (§7.7) | Roster removal (#391 §6) |
| Restart and restore | Hash records bound to pid and start time. A restored record is dead because its process is gone (§3.3, Q4) | Host store fails closed when unreadable. A restore needs an owner trust reset and re-approval: no automatic reactivation of old grants (#394; #407's store generations) | Statement versions are monotone, and an older version is refused | #391 §6.3 freshness, §7.2 replay floor |
| Rotation | A new process gets a new credential | A new key is a new enrollment and generation; the old one is revoked | A new link statement version | Remove and re-add |
| Attribution | `driver: agent`, `driverFrom` (§6) | Reads only. Logged as `service@<device id>` (#394's label). **Never** changes `driver` or `userMessageFrom` | `driverFrom` with the external marker (§6.2) | `driver: person` |
| Unattended | No: tied to a live session | **Yes** | n/a | No |

### 8.3 Transport authentication is not operation authorization

Each layer proves one thing, and only the host's last two layers authorize.

| Layer | Proves | Authorizes |
|---|---|---|
| Relay admission (account token, or a scoped or guest credential) | The key's holder had a credential the relay issued | **Nothing at a host.** It is routing and abuse control only, and an honest-relay narrowing at most. A displayed `kind` or `name` authorizes nothing (`RelayHub.cs:169-171`; #391 §1) |
| Noise handshake | The peer holds the static key it presents, and the channel is between those two ids (design.md:452-454) | Nothing by itself |
| **Principal resolution** at the host | The host looks the **full** authenticated static key up in its own trust state (the roster after #55; #407's pins before), on **every** request, in both channel directions (#394, "Owner-approved host-only foundation") | Determines the *kind*: person, host, connector, peer guest, or unknown (none) |
| **Grant and per-op allowlist** | The kind's grant, at this host | The op, or refusal. An unknown op is denied |
| **Nested principal** | For `agent.command`: the envelope's agent principal, evaluated against the agent policy (§4.7). The carrier host's own grant authorizes only the `agent.command` op itself | The inner op. A principal inside a request **never inherits the carrier's authority** (#394's nested-principal rule) |
| The op's own checks | Unchanged (§4.7 step 11) | |

### 8.4 Shared foundations: the reuse matrix

The **Owner** column is the issue proposed to own the shared piece (§8.9). **#55?** says
whether the foundation needs #55/#391 to be *built*. **Now** is the work that can proceed today
without it.

| Foundation | Agents use it for | Connectors use it for | Cross-account peers use it for | Owner | #55? | Now |
|---|---|---|---|---|---|---|
| **Device and roster trust** | Verifying the issuing host's `hostSign` key, its add entry, probation and revocation (§4.7 steps 1, 5, 6). Needs **roster v2** (§8.10, V1-V5, V7) | Production trust anchor for the connector key (§8.7, Q16 (a)) | Each side's own hosts: a link is recorded against the own roster (§7.3) | #55 | **Is** #55, plus v2 (§8.10) | #391 review and decisions. **#391 is not ready** (sixth review: F1-F3 being fixed; owner decisions open) |
| **Principal and key-purpose separation** | `"agent-bridge agent command v1"` and the host `hostSign` key's purposes (§4.2, §8.5, §8.10 V1-V2) | Connector key purposes: Noise, admission proof, claim proof (#394) | `"agent-bridge link v1"`, link keys | Shared registry, kept by #55 (§8.5) | No (a document and vectors) | Yes: the label registry and negative vectors |
| **Owner-controlled issuance** | Launch or adoption at the machine (§3.2, §3.5) | Full-key approval at the selected host (#394 steps 1-3) | Link code plus a presence-signed statement on each side | Per kind: #519, #388, #519 | Agents' local issuance: no. Connector production: §8.7. Links: yes (person commands) | Agent issuance: AP1. Connector: disposable hosts only (#407) |
| **Narrowly scoped host policy**, at one entry point | `agents` policy (§5) | `connector` grant: exact allowlist (§8.6) | `links[].expose` (§7.4) | One host authorization module, extending #407's `RemoteAuthorization`: device kinds #55, connector grants #388, agent policy #519 | The module: no (#407 exists). Its roster-backed lookup: yes | Yes, behind #407's floor on disposable hosts |
| **Expiry and renewal** | Credential ≤ process, ≤ 7 days | Grant expiry at the host. Access credential ≤ 15 min, renewal by proving the key, no widening (#394) | Link 30 days, statements 24 h freshness | Per kind | No | Design and tests |
| **Replay and lost-reply rules** | Durable `(add hash, request)` store and `agent.outcome` (§4.4) | Reads are idempotent. Subscriptions recover with snapshot and seq (v1-sessions.md "Channel framing"). Enrollment claims bind request id, key, scope and host, and a lost reply never enrolls twice (#394 steps 3, 5) | As agents | Durable store: #55 (R6). Claim rules: #388 | Durable store: **yes** (R6) | Connector claim design: yes |
| **Durable revocation and freshness** | Credential deletion (local). Host removal via roster (§4.7) | Host-side grant generations, durable and fail-closed (#407). Relay revocation across every serving deployment (#394). Roster removal after #55 | Unilateral link removal. Peer statement freshness (§7.5) | #55 (roster freshness). #388 (grant and credential revocation). #519 (links) | Roster freshness: yes. Host-side grant revocation: no (#407) | Yes, for host-side grants |
| **Restart, rotation and rollback** | §8.2 row | §8.2 row: restore requires an owner trust reset | §8.2 row | Per kind, with a common rule (below) | No | Yes |
| **Old-validator and mixed-fleet rejection** | Agent tokens never reach the relay, and `Tokens.Validate` rejects them structurally (one part, no `.`: `Tokens.cs:33-36`). `agent.command` goes only to hosts advertising it in `enforce` (§4.6) | Scoped credentials use a separate signing-key domain and a structurally distinct encoding, so unmodified older account-token validators reject them. Tested against the audited-base validator. Production gate: every account-reachable host enforces (#394 "Scope and trust boundary") | Guest credentials: same framework. Old hosts never get guest channels | Credential framework: #388 (Q17). Mixed-fleet gate: #55 (the R8 cutover) | Gate: satisfied by #55's cutover, or by Q16 (b)'s manual gate | Rejection tests: yes |
| **Relay credential purposes** (account, scoped connector, guest, bound account) | Not used (local only) | Scoped connector credential | Guest credential (§7.3) | #388, reviewed by #55 (Q17) | No for design. Production deploy needs owner approval | Design and staging |

**Common rule for restart and rollback.** No restored or stale state ever *grants* more than the
live state did. Unreadable is not absence: it fails closed. A restored grant, pin, credential
record or link statement is honoured only if its generation or version is not older than the
host's durable high-water mark, which is the #407 store generation for connectors and the
version mark for links. Otherwise it waits for an owner reset. Agents are safe by construction,
since a restored credential's bound process no longer exists.

### 8.5 Signing and credential domains

A restricted service or agent credential must never act as an unrestricted account token, and no
signature made for one purpose may verify for another. Each key, label and credential below has
exactly one purpose, and each verifier checks it.

| Item | Purpose | Cannot become |
|---|---|---|
| Relay account token (HMAC under `Relay:TokenKey`, `Tokens.cs:9-45`) | Account admission | Anything else. #391 §7.6 binds it to a key |
| **Scoped connector credential** (#394) | Admission of one connector key, to its permitted hosts, for its expiry | An account token: a **separate signing-key domain** and **structurally distinct encoding**, so the audited-base `Tokens.Validate` rejects it. It can't choose `app`, `pwa` or `host`, authorize another key, reach account-management endpoints or mint another credential (#394 step 6) |
| **Guest link credential** (§7.3) | Admission of listed peer host keys, routed only to the link's hosts | Same framework as the connector credential. A guest has no account on the target relay |
| `AGENT_BRIDGE_TOKEN` (`abat1_…`) | Local pipe only, one session | Never sent to any relay or channel. Structurally invalid as an account token (`Tokens.cs:33-36`). Useless from another session (§3.4) |
| `F("agent-bridge roster entry v1" / "roster cosign v1" / "person command v1" / "presence v1" / "roster head v1", …)` | #391 | — |
| `F("agent-bridge agent command v1", body)`, host `hostSign` key (roster v2, §8.10) | Agent envelopes (§4.3) | A roster entry or person command: a different label, and those verifiers require a person kind (#391 §3.2, §7.2) |
| `F("agent-bridge host key binding v1", account, generation, dh)`, by `hostSign` | The `hostSignProof` in a v2 host add (§8.10, V1) | Anything else. It proves possession of `hostSign`, not of `dh`, which is why carrier binding (V4) is also required |
| `F("agent-bridge session instance v1", …)` | A hash, not a signature (§3.1) | — |
| `F("agent-bridge link v1", …)`, per-link key | Link codes and peer host statements (§7.3) | Anything in the peer's own roster |
| `"agent-bridge command v1"` (`CommandMac` salt) | Person transfer and folder-trust MACs, retired by #391 R6 | Agent commands use their own salt under Q1 (b) |
| Connector admission and claim proofs | #394 step 4: a separate purpose label binding request id, key, account, host and purpose | Today's `Wire.ProofMac` alone (#394, "Grant and enforcement") |

The registry of labels is shared. Adding one is a reviewed change with negative vectors proving
each verifier refuses the others' bytes.

### 8.6 The connector's first grant (Home Assistant)

* **Same account, one selected host, read-only**, enforced **by that host**: `hello`,
  `sessions.list`, `session.get` and `sessions.subscribe`. That is #407's `service` role exactly
  (`RemoteAuthorization.cs:10`), and #534's read profile.
* `hello` advertises only the intersection of the host's capabilities and the grant.
  Subscriptions belong to the grant, and end on revocation or expiry (#394).
* **Denied:** every other op, including unknown future ones. That covers `session.reply`,
  `session.answer` (approvals included), `launch.*`, `launch.trust.answer`, `session.stop`,
  transfers, `attachment.*` and blobs, `push.*`, `usage.*`, `update.*`, `host.*`, roster,
  account and credential administration, `agent.command`, and every agent op of §4.
* **Not inherited from agents.** That agents may reply and launch gives connectors nothing. A
  later connector write grant (action parity) is a separate reviewed design with its own
  attribution (#388's "automation" kind), not a reuse of the agent policy.
* **Never an authority signal:** relay routing, presence, displayed `kind` or name, or an HA
  user or automation label.
* **Attribution:** reads are logged as `service@<device id>`, and never touch `driver`,
  `driverFrom` or `userMessageFrom`.

### 8.7 Dependencies: what needs #55/#391, and what doesn't

**Agents** (owner decision 4, unchanged): local agent messaging (AP1-AP3) needs nothing from #55.
Cross-machine messaging (AP5) needs #391 R1, R3 and R6, hosts in `enforce`, and the roster v2
changes of §8.10. Under Q1 (a) that is V1-V6; under Q1 (b), V3, V5 and V6. There is **no**
interim signed-in-device or trusted-peer route (Q11).

**Connectors.** Host-side enforcement of the read-only grant needs no roster: #407's
`RemoteAuthorization` already resolves the full key per request and limits `service` to the four
reads. What production issuance needs is a **trust anchor that also covers the rest of the
account's peers**. On an enforcing host, every other peer (each person's device, each host) must
also be known by full key, or a relabelled key or lying relay gets past a service-only check.
And **every account-reachable host must enforce**, or an older host trusts any admitted key as a
person (#394, "Scope and trust boundary"). Two routes:

| | (a) **Roster anchor** (recommended for production) | (b) **Local pins only**: #407's floor as the production anchor |
|---|---|---|
| What | The connector is enrolled in the #55 roster as a new kind, `connector` (a roster v2 change, like the host `hostSign` key, §8.10), approved by a presence-proven person's device. The **per-host grant** stays a local record at the selected host, so the roster says *what* the key is, and the host says *what it may read here* | The owner pins the connector's full key as `service` at the selected host, *and* pins every person's device and host at every account-reachable host, which all run in `permissions.trustedPeers` enforcing mode |
| Mixed-fleet gate | Met by #391's cutover (R8): every host enforces the roster | Met only by manually pinning everything everywhere, verified over authenticated channels (#394) |
| Revocation reach | Account-wide, announced on every device (#391 §6) | Per host, by hand |
| Status | Needs #391 R1 + R3 + R8, and the `connector` kind | A **proposed architecture decision**, not an authorized exception. #407 is approved only for disposable-host evaluation (host-config.md:145-148) |

The **grant record** is anchor-agnostic in both routes: the full key, `connector` role, grant id
and generation, allowlist, expiry and revocation. So moving from (b) to (a) re-anchors a grant
rather than re-enrolling. Q16 asks the owner to choose.

**What proceeds now, independently of #55** (design, test, disposable-only):
* the read profile and conformance suite (#534), which is also the owner's likely first slice;
* the Python Noise transport and codecs (HA #9, #10);
* host-side connector grant enforcement and revocation-owned subscriptions behind #407, on
  disposable hosts;
* the relay credential framework's design, encodings and old-validator rejection tests, on
  staging only;
* the label registry and its negative vectors (§8.5);
* AP1-AP3 (local agents).

**What waits for #55/#391:** production connector issuance under (a), the `connector` roster
kind, cross-machine agent messaging (AP5), and federation (F1-F5, which need person commands for
link statements).

**What waits for the owner** whichever route: any production relay deploy of a new credential
purpose, decommissioning or re-keying older relay deployments that share the account-token key
(#394), and Q16.

### 8.8 The federation boundary

* **Connectors are same-account only.** A link (§7) exposes a side's shared sessions to the
  *peer's hosts carrying agent envelopes*. It never admits a connector, and a connector never uses
  a link. A connector for another account's sessions would be that account's own connector.
* **What connectors and federation share** is only the relay credential framework (the guest
  credential is a further purpose), the label registry and the host's single authorization entry
  point. All three are designed so purposes can be added. **None of them needs federation to be
  built first**, so the connector's first grant doesn't wait on federation.
* Federation's read projection (§7.4) may later be a scope offered to connectors as well. It is
  not part of the first grant.
* Repo-based discovery (§7.11) doesn't change this boundary. It finds and proves *peer
  accounts' hosts* for links. Connectors are never suggested, listed in the directory, or
  reachable through a link.

### 8.9 Ownership: shared versus separate

| Piece | Shared or separate | Proposed owner |
|---|---|---|
| Signed roster, device kinds, freshness, durable replay store (R6), account-token binding (#391 §7.6), the mixed-fleet cutover (R8) | Shared | **#55** (PR #391) |
| **Roster v2** for the other kinds (§8.10): host `hostSign` and its binding proof (V1), key-purpose rules (V2), the host-signed agent op class (V3), displacement and probation state exposed to policy (V4, V5), and the `connector` kind (Q16). The cross-account path (V7 (B)) only if chosen | Shared, requested by #519 and #388 | **#55**, decided before R1 is built. #391 is not ready |
| Signing-label registry and negative vectors (§8.5) | Shared | **#55**, kept with #391's framing |
| One host authorization module (principal resolution, grants, per-op allowlist, nested principals), extending #407 | Shared | **#55** for device kinds. **#388** adds connector grants, **#519** the agent policy and link scopes |
| Relay credential framework: purposes, key domains, encoding, old-validator rejection, revocation across deployments | Shared | **#388** (first consumer, and #394 specifies it), reviewed by #55. #519 adds the guest purpose |
| Connector enrollment (full-key transfer, approval UX, claim), connector grants, connector key storage, the read profile and conformance suite (#534), the Python transport (HA #9, #10), HA integration | Separate | **#388** / agent-bridge-home-assistant#2 (D) |
| **The connector API as product** (§9): `connector-api.md`, profiles and versioning, schemas, vectors, conformance suite, reference SDKs, host contract tests | Separate from agents. Product, not Home Assistant | **#388**, as the API's owner, with D's #534 as its start (Q29). The Home Assistant connector is its first consumer, in agent-bridge-home-assistant#2 |
| Agent credentials, `agent.hello`, adoption, envelope, chains and budgets, prefix, `driverFrom`, MCP (#45), links and federation | Separate | **#519** (with #45 for the MCP server) |
| Attribution taxonomy: `driver` and `driverFrom.kind` (`agent`; later `automation` for connector writes; `external`) | Shared vocabulary | **#519**, with #388 adding `automation` if write grants are ever approved |

### 8.10 Required changes to #55's design: a roster v2

These come from the **sixth independent review of #391** (`docs/design/device-approval.md` at
`e00c4709`, relayed by the coordinator at 17:45 PT). Each item is either a prerequisite #519
takes from #391 as written, or a change #519 **requires** of #391's design: a **roster v2**.

**#391 is not ready.** Its review findings F1-F3 are being fixed, and its owner decisions (#391
§11) are open. Nothing here assumes the changes are accepted. They are requests to #391's owner,
to be decided before #391's R1 is built, so the format changes once.

| # | Finding (#391 at `e00c4709`) | Required change to #55 (roster v2) | What #519 does with it | Needed by |
|---|---|---|---|---|
| **V1** | **Hosts have no signing key.** `sign` is "absent for a host" (L136), strict parsing refuses it on a host's add (L221-223), and `v` other than 1 is refused. The `dh` key can't make third-party-verifiable signatures | **Entry `v: 2`** for host adds: `hostSign` (P-256 ECDSA, `hostSignAlg: p256-sha256-der`) and `hostSignProof`, a signature by `hostSign` over `F("agent-bridge host key binding v1", account, generation, dh)`. The proof shows possession of the key and binds it to that `dh`. The enrollment transcript (#391 §4.2) covers `hostSign`, so the approver saw the joining host present it. A v1 verifier refuses a v2 entry, so v2 is switched on per roster, **one way, by a person's device, after every rostered verifier advertises v2 support** (the same pattern as #391's relay mode). It is never inferred | Q1 (a): envelopes signed with `hostSign` under `F("agent-bridge agent command v1", …)` (§4.2-§4.3) | AP4, AP5, federation. **Not** under Q1 (b) |
| **V2** | **Key-purpose separation** | Verifiers never accept a `hostSign` signature as an entry `sig`, a `cosig` or a person-command signature. Those resolve the signer's **`sign`** field, which a host doesn't have, and require a person kind. Both new labels go in the registry (§8.5), with negative vectors: a `hostSign` signature over entry or person-command bytes, under every label, is refused | It makes "never approvals or folder trust" **structural**: those need person commands (#391 §7.2), and a host key can never make one | With V1 |
| **V3** | **No op class for host-signed session operations.** #391 §7.1 gives session ops only to person kinds, and hosts only host-to-host ops (L1335-1336) | A new row in #391 §7.1, **"host-signed agent operations"**: `agent.command` on a host-to-host channel. The carrier is an **active, non-probationary host add entry, by bound kind and add hash**. The inner op is from the agent action set (§4.3) and is evaluated by the target's agent policy. Policy keys on the signer's **add entry and bound kind, never the bare device id** | §4.7 step 1; `fromMachines` as add hashes (§5.1); replay and audit keyed by add hash (§4.4, §5.6) | AP5 (under both Q1 options) |
| **V4** | **Displacement substitutes descriptors.** A hostile approver senior to a host can displace its enrollment with its own descriptor (#391 §3.3, L221-256). With host signing keys, that becomes attributable impersonation of a named machine, visible only in the announcement | #391 exposes in its computed state which host adds **displaced** another (and which were displaced), for policy consumers | **(i) Carrier binding:** an envelope is accepted only on a Noise channel whose static key is the `dh` of the same add whose `hostSign` verifies it (§4.6). A copy shares the victim's `dh` *public* key, but its maker lacks the private key, so it can't deliver anything. Displacement becomes denial of service against the genuine host (its key no longer matches the winning add), not impersonation. **(ii)** A **displacing add is probationary** for agent commands whatever its age (V5), until a person confirms it at a target. **(iii)** Policy keyed by add hash drops a re-enrolled machine from `fromMachines` lists. **(iv)** Chain evidence (§5.4) signed by a displacing add is treated as unverified. **(v)** The app says so: "Agent messages from *DESK* paused: *DESK* was re-enrolled by *Kitchen laptop*". Residual: an approver that also holds the machine's `dh` private key has compromised that machine already (§10.1) | AP5 |
| **V5** | **Revocation latency.** Freshness against a withholding relay can lag up to 24 h, and is unbounded inside a partition (#391 §6.3). A removed host could keep sending agent messages meanwhile. A new host acts at once | #391 exposes per-add **probation** state (72 h by the verifier's persisted monotonic clock, #391 §3.4) for host adds as well, as a policy input | **A #519 freshness gate:** a target accepts agent commands only with a head attestation from an active device other than the issuer within **1 h** (Q31). Usage peers every 5 minutes and the local app make that normal. Otherwise it refuses with `stale`. **Probation:** a host add in probation, or a displacing one (V4), may not send cross-machine agent commands. The owner can allow one sooner only **at the target machine** ("Allow agents from *DESK* now", #67-guarded, Q32), because #391's `confirm` is offered only after 72 h. **Partition:** unbounded, stated in §10.1. The per-machine emergency switch `agents.enabled: false` stops agent messaging locally at once | AP5 |
| **V6** | **Replay** | None: reuse #391 §7.2's durable replay store and its floor | §4.4: written and flushed before execution, the high-water mark, the highest-`issuedAt` floor and the reset rule, keyed by add hash and `request` | AP5 (R6) |
| **V7** | **Cross-account.** Entries bind `account` and `generation`. A host trusts only a roster it was enrolled into, and `roster.sync` and the relay serve only the same account's approved devices. So a foreign host can't fetch, verify or stay fresh on another account's roster | Depends on the model (below). Under **(A)**: none. Under **(B)**: a pinned foreign genesis fingerprint, a read-only foreign-roster path, cross-account head attestation and revocation propagation | Principals keyed by **account + generation + add hash** (and the full `hostSign` key) everywhere, since device ids aren't namespaced and copies share ids (§4.3) | F1-F6 |

**V7, the two cross-account verification models.**

| | **(A) Statement-based** (§7.3, recommended) | **(B) Foreign roster** (the review's path) |
|---|---|---|
| How B verifies A's hosts | A's **peer host statement** lists the exposed hosts by full `dh` and `hostSign` key (with their add hashes), signed by A's per-link key. That key is pinned by the link code (§7.3) or by repo discovery's GitHub-key proof (§7.11.3). A's own client checks the statement against A's roster before signing it | B pins A's **genesis fingerprint**, exchanged out of band (it fits repo discovery, §7.11: the descriptor already carries `genesis` and `generation`, under the T2 proof). It then fetches and verifies A's roster through a new **read-only foreign-roster path** |
| Revocation propagation | A's client re-signs the statement on any removal or displacement (automatically, with presence). B requires a statement or another A host's attestation within 24 h (§7.5) | A's removals reach B through roster sync. That needs cross-account head attestation and freshness |
| Disclosure to B | Only the exposed hosts | A's whole roster: every device, name, kind and platform |
| Change to #391 | None beyond V1 | Serving `roster.sync` and relay storage across accounts, foreign freshness, and #391's acceptance rule (quarantine, seniority) run over a foreign roster |
| Weakness | The per-link key, and A's client staying online to re-sign | Disclosure, and #391's complexity duplicated across accounts |

**Recommended: (A)**, with B recording A's `genesis` and `generation` from the descriptor. A
reset of A's roster then invalidates the link, which needs re-linking. (B) is a fallback if the
owner wants roster-level verification of the other side (Q30).

**What proceeds without roster v2:**
* AP1-AP3: local agents.
* The envelope's spec and negative vectors (AP4 as design), and the policy, chains, attribution
  and audit.
* The connector API's C1 (§9.7).
* Federation and repo-discovery design.
* Under **Q1 (b)** (designated-verifier MAC), even cross-machine single-account agent messaging
  needs only V3, V5 and V6, with no format change. V4 is moot there, because a copy can't make
  the MAC without the `dh` private key. That is the fallback if v2 slips.

**What waits for it:** host-signed envelopes (Q1 (a)), third-party-verifiable audit and chains,
and federation's host keys in peer statements.

---

## 9. The connector API

Added at the owner's direction (coordinator, 2026-11-03 17:31 PT): **Home Assistant is one client
of a documented, stable, versioned third-party connector API**, not a special case. Anyone may
build a connector on it (dashboards, chat bots, automation tools), and the Home Assistant
integration is the first. This section is the API's design. §8 already covers how connectors
relate to agents and peers, their lifecycle (§8.2) and their first grant (§8.6), and this section
doesn't repeat that. It builds on D's draft read contract, PR #534
(`docs/protocol/connector-read.md`, `connector-read-v1.json`, the conversation fixtures,
`tools/connector-conformance/mock-host.mjs` and `ConnectorReadVectorTests`), which becomes the
API's first profile.

### 9.1 One wire protocol, separately versioned profiles

**Recommended: the connector API is a published profile of Bridge Protocol v1, not a second
protocol.**

| | One protocol, connector **profiles** (recommended) | A separate connector protocol |
|---|---|---|
| Implementation | One `SessionService`, one set of records, one set of vectors (v1-sessions.md "Test vectors") | A second server surface to build, review and keep consistent |
| Drift | Can't drift in payloads: a profile *selects* ops and records, and the host's contract tests pin the selection (§9.4) | A translation layer whose behaviour can diverge from the app's and PWA's |
| Stability | Each profile has its own version and deprecation policy, so the internal protocol can grow (additive fields, new ops for the app) without moving what connectors see | Same goal, at higher cost |
| Security | Separation comes from the **principal and grant**, enforced at the host per message (§8.3), not from a different wire format | No stronger: a separate format doesn't authorize anything |

The app and PWA keep using the whole protocol. A connector uses only the ops in the profiles its
grant names.

**Profiles.**

| Profile | Ops and events | Status |
|---|---|---|
| `read` v1 | `hello`, `sessions.list`, `session.get`, `sessions.subscribe`. Events `session.upsert`, `session.remove`, `sessions.synced`, `discovery.changed` (#534's index) | First slice (§9.7) |
| `usage` v1 (later) | `usage.get`, `usage.subscribe` | Separate grant |
| `act` v1 (later, Q26) | Candidates, each its **own** grant: `session.reply`, `session.stop`, `launch.options` and `session.launch` (`ask` only) | Needs its own design and review (Q18), with `automation` attribution. Any reply act has §5.2's idle-only, screen-guarded delivery, and §5.3's allow-all exclusion |
| **Never in any profile** | `session.answer` on any approval; `launch.trust.answer`; `host.*`; roster, account and credential administration; `pairing.*`; `update.*`; `push.*`; `session.transfer`; `agent.command` | Enforced by the host's classification test (§9.4) |

Whether connectors may ever answer `ask_user` *forms* (not approvals) is part of Q26. Note the
consequence for parity: agent-ha-bridge let Home Assistant's phone notifications answer
approvals. Under this API they can't, and the native app and PWA are where approvals are
answered.

**Versioning and negotiation.**
* A connector's `hello` names the profiles it speaks:
  `{"connector": {"profiles": [{"name": "read", "versions": [1]}], "sdk": "agent-bridge-py/0.3"}}`.
  The host answers with the profiles it **grants this principal**, at the highest common version:
  `{"connector": {"profiles": [{"name": "read", "version": 1}]}}`. A profile neither granted nor
  supported is simply absent. A connector with nothing in common gets `hello` with no profiles,
  and every other op is refused with `not-granted`.
* `capabilities` stay what they are today: features, **not permissions** (#534, "Boundary and
  versioning"). A connector reads both.
* **Within a profile's major version:** additive only. New optional fields and enum values
  (clients ignore unknown ones, v1-sessions.md "Rules for every message"), and new events a
  connector must tolerate. A new *op* is a new profile minor version, which the connector must
  negotiate before it is sent.
* **A major version** is needed for removing or renaming an op or field, changing a meaning, or
  making optional data required.
* **Deprecation:** a host supports the current and the previous major version of each profile
  for at least **12 months** after the new major ships. During that time `hello` marks the old
  one `{"deprecated": true, "sunset": "2028-01-31"}`, and the app shows connectors that still
  use it. After sunset the old version is refused with `unsupported-version`, naming the
  supported ones.
* The profile index (`connector-api.json`, §9.4) carries each profile's version, status
  (`draft`, `stable`, `deprecated`) and sunset date.

**Transport.** Noise over the relay (and LAN direct, design.md §5.2, when it exists), as for any
device. There is **no plaintext REST or webhook gateway**. It would need either an inbound port
on the host or a relay that reads content, and the design rejects both (design.md §5.1, §5.3).
The local pipe is not a connector transport: it carries same-user rights, not a principal
(local-pipe.md:121-130). Implementing Noise P-256 is the price of entry, so the project
publishes **reference SDKs**, each held to the shared vectors: .NET (`AgentBridge.Transport`),
JavaScript (the PWA's `noise-p256.js`) and Python (agent-bridge-home-assistant #9 and #10, with
the owner's approval of 2026-11-03 for that route, #388).

### 9.2 Connector identity, enrollment and approval

A connector is a **service principal** (§8.2): its own P-256 key, generated by it and kept in its
own protected storage, unattended, and approved and revocable by the owner. There is **no
central registry or store** of connector *products*. What the owner approves is one installation
of one connector, with one key, for one account.

**Enrollment** (after #55, route (a) of §8.7; Q16):

1. **Request.** The connector generates its key and shows an enrollment request in its own UI
   (Home Assistant's config flow, a bot's setup page): a QR code and a code.
   `{ v, request, key, name, vendor, version, homepage, profiles: [{name, version}] }`. Every field
   but `key` and `request` is the connector's **claim**, displayed as such.
2. **Approval** on a presence-proven person's device (#391 decision 3(a)), using #391 §4.2's
   **mutual, transcript-bound code exchange**, with the connector as the joining device. It shows
   code 1, which the person types into the app. The app shows code 2, which the person types into
   the connector's UI. A QR code does both, as in #391 §4.3. That is the "separately reviewed
   transcript-bound commit/reveal pairing" that #394 ("Proposed enrollment sequence", step 2)
   accepts in place of a full-key transfer, so no new pairing protocol is introduced.
3. **What the person decides**, in one screen:
   * the **display name**, pre-filled from the claim and editable ("Home Assistant (house)"). What
     the person confirms is what is signed and shown, never the connector's later runtime claim;
   * **which hosts** it may reach (none pre-ticked; #394 "selected host");
   * **which profiles**: `read` only by default. Anything else is shown with what it allows, in
     words;
   * **scope** within a host: all sessions (default), or only some workspaces, with
     `inbound.exclude` sessions never visible (§5.1);
   * the **grant expiry**: 1 year by default, with a reminder 14 days before, renewed with
     presence (Q27).
4. **Recording.** A roster `add` of kind `connector` (an amendment to #391, Q16), signed with
   presence and announced on every device (#391 §6.4). For each selected host, a **grant
   record**: `{connector key, grant id, generation, profiles, scope, expiry}`, signed as a person
   command for that host (#391 §7.2) and stored durably by the host (#407's store discipline).
5. **Credential.** The selected hosts' relay issues the connector's **key-bound scoped relay
   credential** (§8.5; #394 steps 3-6). It is never an account token, and it is routed only to
   the granted hosts.

Before #55, the same steps run against #407's local pins on **disposable hosts only** (§8.7).

**Third-party connectors serving many users** (a hosted chat bot) hold **one key per owner
account**, enrolled separately by each owner. There are no multi-tenant keys, and no key that
reaches two accounts (§8.8).

### 9.3 Grants, scope, rate limits, audit and display

**Grants** are per connector and per host. The host evaluates them on **every request and every
outgoing event**. Events for sessions outside the scope are never sent, and a session leaving the
scope is sent as `session.remove`. The default is the `read` profile, all sessions, on the
selected hosts only. A narrower *projection* (titles and status only, no `activity` text) is a
later scope option for dashboards that don't need content (Q28).

**Rate limits**, per connector per host, enforced by the host. The relay limits frames per
credential as an honest-relay backstop.

| Limit | Default |
|---|---|
| Channels open at once | 2 |
| Subscriptions at once | 2 |
| Requests | 120 per minute |
| `session.get` | 60 per minute |
| Bytes sent to it | 200 MiB per day (relay egress, design.md §4.7) |

Past a limit, the request is refused as `rate-limited` with `retryAfter`. After 10 refusals in
10 minutes, the host closes the connector's channels for 5 minutes and says so in the app.

**Audit.** Each host records, per connector:
* channel open and close, and the negotiated profiles;
* hourly counts per op;
* refusals, with the op and reason;
* grant changes and revocations with their cause.

It records **no session content**. Enrollment, renames and removal are roster events, announced on
every person's device. The app's **Connectors** page lists each connector with its hosts,
profiles, scope, expiry, last seen, requests today and refusals, and has **Revoke** beside each.

**Display.**
* A `read` connector never drives a session, so it changes nothing on a card.
* The machine's detail lists "Read by **Home Assistant (house)** · 2 min ago", with a connector
  glyph distinct from the agent diamond.
* Under a future `act` grant, its acts are attributed as `driverFrom { kind: "connector", name }`
  and shown as "via **Home Assistant (house)**". That attribution is never `driver: agent` and
  never purple, so the person can tell a connector from an agent at a glance (Q18).

### 9.4 The contract: spec, vectors, conformance and drift tests

The API is product. These live in this repository under the API's owner (§8.9):

| Artefact | Where | What |
|---|---|---|
| **Spec** | `docs/protocol/connector-api.md` (umbrella: principles, profiles, versioning, identity, grants, errors, limits), with one doc per profile (`connector-read.md`, from #534) | The normative text |
| **Machine-readable index** | `docs/protocol/connector-api.json` → `connector-read-v1.json` (#534) | Profiles, versions, status, sunset, ops, events and limits. **A JSON Schema (2020-12) per request, result and event record**, generated from the C# records in `AgentBridge.Protocol` so they can't disagree. #534 has the request schema; the record schemas are the next step |
| **Vectors** | `docs/protocol/vectors/connector/`, in the existing `vectors/` style and regenerated with `UPDATE_PROTOCOL_VECTORS=1` | Valid and must-refuse messages per profile, version negotiation, refusals (`not-granted`, `unsupported-version`, `rate-limited`, `revoked`), enrollment requests and codes (from #391's vectors), and #534's conversations |
| **Conformance suite** | `tools/connector-conformance/` | A **reference host** any connector can run, plus scenarios and a report (below) |
| **Host contract tests** | `tests/AgentBridge.Host.Tests` | Drift and authorization (below) |

**The conformance suite.** #534's stdio mock replays recorded conversations. The suite grows it
into a reference host. It runs the real `SessionService` over synthetic sessions, behind a real
Noise responder on loopback with the connector's grant, so a connector exercises its real
transport. Scenarios:
* paging, and paging across a revision change;
* subscribe, snapshot and `sessions.synced`;
* a sequence gap and resubscription;
* a discovery hold;
* malformed and unknown fields and values;
* `missing` sessions;
* an out-of-grant op (`not-granted`);
* an unsupported version;
* rate limiting with `retryAfter`;
* **revocation mid-subscription** (the channel closes with `revoked`, and the connector must stop
  showing data as live);
* grant expiry;
* a host restart (a new snapshot, never an empty fleet);
* an unavailable relay (never an empty fleet).

A connector runs `node tools/connector-conformance/run.mjs --connector "<command>"` and gets a
report naming each scenario. Passing it is self-attested conformance, not certification by the
project. Real-host validation still applies for anything production (#534's caveat).

**Host contract tests**, so the API can't drift:
* **Payload drift:** `ConnectorReadVectorTests` (#534) regenerates the conversations from the
  real host and fails on any difference.
* **Op classification:** every op the host serves must be classified as in exactly one profile,
  internal-only, or never-for-connectors. A new op that isn't classified fails the build, in the
  spirit of `quality/catalog.yml`, which lists every suite the repository runs. That is how
  `session.answer` approvals, `launch.trust.answer` and `host.*` can never quietly become
  reachable.
* **Authorization matrix:** for each profile grant, every op is driven through the real dispatch
  entry points in both channel directions. Only that profile's ops succeed, and events outside
  scope are never sent.
* **Freeze:** a profile's schemas and op list are hashed into the index. Any change to a
  `stable` profile must be additive (checked against the previous schema), or come with a version
  bump.

**Boundary.** Bridge core owns the API, its spec, vectors, conformance suite and reference SDKs.
**Home Assistant's entities, cards, MQTT and config flow belong to the Home Assistant connector**
(danswett/agent-bridge-home-assistant), outside Bridge core, as D established. Today's in-host
Home Assistant connector (`AgentBridge.HomeAssistant`, the G9 migration compatibility path) is
**not** a connector-API client. It runs in the host process with the host's own authority. It
remains until its retirement under #319 and #389 (#525-#527), and nothing here changes it.

### 9.5 Security

* **A connector is untrusted third-party code.** Its claims (name, vendor, version, requested
  profiles) are display text until the owner confirms them. Its key is the only thing that
  identifies it.
* **It never answers approvals or folder trust:** no profile contains them, and the
  classification test (§9.4) enforces that.
* **It never gets an account-wide token.** Its relay credential is key-bound, purpose-scoped and
  in a separate signing domain that older validators reject (§8.5; #394 "Grant and enforcement").
* **The host enforces every grant**, per message, at every dispatch entry, in both directions.
  The relay, presence, displayed kind and name authorize nothing (§8.3).
* **Revocation is immediate:**
  * **At the host** (the app's Revoke, or `agent-bridge-host connectors revoke`, at the
    machine): durable first, then the connector's channels close and its subscriptions end
    within a second. That works with the relay down (#394).
  * **From any person's device:** a roster removal (after #55), which every online host applies
    at once and an offline host applies before it serves again (#391 §6.2). Plus relay
    revocation of the credential.
  * **Expiry:** enforced on live channels, not only at admission (#394).
* **No standing access survives revocation:** subscriptions are owned by the grant (#394, and
  #407's revocation-owned subscriptions).
* **Data minimization:** scope and projection (§9.3), and no content in audit logs.

### 9.6 Relationship to agents and federation

Agents, connectors and other-account peers are the three principal kinds of §8, sharing one
verification machinery (§8.3-§8.5) with separate lifecycles and grants (§8.2). The reuse matrix
(§8.4) and the dependencies (§8.7) cover connectors, and the API adds no new shared foundation.
Its own pieces (profiles, negotiation, conformance, SDKs) belong to the connector owner (§8.9).
A connector is never an agent, never drives a session under `read`, and is never reachable
through a federation link (§8.8).

### 9.7 Phasing: the smallest first slice

**C1, the smallest first slice: the read-only connector API, with Home Assistant as its first
consumer.**

| # | Slice | Depends on | Production? |
|---|---|---|---|
| **C1** | `connector-api.md` and `connector-api.json` around #534's `read` v1. Profile negotiation in `hello`. Record schemas generated from C#. The op classification, authorization-matrix and freeze tests. The reference host on loopback Noise, and conformance scenarios except enrollment. The `read` profile enforced through #407's `service` role on **disposable hosts** | #534 merged, #407 (merged) | **No**: disposable hosts only |
| C2 | Python SDK on the shared vectors (HA #9, #10). The Home Assistant connector reads through C1's reference host and a disposable real host | C1 | No |
| C3 | Enrollment (§9.2) with mutual codes, the Connectors page, revocation, rate limits and audit | #391 R1 + R3 (+ the `connector` kind, Q16), or the owner's Q16 (b) decision | Behind the owner's approval |
| C4 | The relay's scoped connector credential (§8.5) | Q17. Staging first; production with the owner's approval | Production relay |
| C5 | Production read for Home Assistant on the owner's selected host | C2-C4, the mixed-fleet gate (§8.7), independent security review | **Yes** |
| C6 | Later profiles: `usage`, then `act` (Q26) | C5, a separate design | — |

C1 needs nothing from #55 and doesn't connect anything in production. That is why it comes
first. **Production Home Assistant (C5) waits for the trust anchor (Q16), the relay credential
(Q17) and the mixed-fleet gate.**

---

## 10. Threat model

In addition to design.md §5.1 (:348-361) and #391 §2.

### 10.1 Single account

| Party | Assumption | Must not be able to | Can (residual) |
|---|---|---|---|
| **A prompt-injected agent with tools only** (MCP and file tools in an `ask` session, no unattended shell) | Holds its own credential and follows hostile instructions | Answer an approval or folder trust anywhere. Launch with allow-all, or without an explicit `ask`. Reply into an allow-all, unknown-mode or working session (§5.3). Have its words or Enter land in a prompt that opens mid-delivery (§5.2's guard). Stop, transfer, update, change settings, adopt, or widen its own scope. Reach excluded sessions. Exceed depth, budget or rates. Appear as the person | Read and reply to sessions the policy allows, and launch `ask` sessions in approved workspaces, within budgets. Each act is attributed, audited and shown violet. That is the owner's chosen reach (decision 2) |
| **A prompt-injected agent with a shell** (it can run commands, after the person approved them or because its own session is allow-all) | All of the above, plus it runs processes as the user | Use another session's token (§3.4 step 2). Make another session's agent act without a reply (§5.3 still applies to targets) | Everything the user can, locally: it is same-user code (§1.3, local-pipe.md:121-130). Through the bridge, only its own scope. Its subprocesses inherit its token (§3.4) |
| **Another session's agent, or a detached helper**, on the same machine, holding a stolen token (from a readable environment, or a transcript that printed it) | Same OS user | Use it: on Windows the token is accepted only from a process positively inside the bound session's ancestry or Job object, and `AGENT_BRIDGE_SESSION` proves nothing (§3.4). It dies with its process | On macOS and Linux nothing, because there are no agent credentials until peer identification exists (§3.4). On Windows, code that injects into the session's own process tree is same-user malware (below) |
| **A stolen person's device** (unlocked, with its presence check passable) | Holds `sign`, maybe a `linkKey` | Beyond #391 §2's bounds: add hosts to a federation link without the peer person's confirmation (§7.3) | Until removed: what #391 allows a person's device, including drive sessions (#391 §7.3), and re-sign statements for links it holds the key for. Removal revokes those links and sends tombstones (§7.3, §7.7) |
| **A stolen host** (the machine, or its keys) | Holds `dh`, `hostSign` and the account token | Act as another machine. Pass person-only checks. Reach targets whose policy excludes it | Until removed and the removal is fresh at targets (1 h, V5): send agent commands as its own sessions, within targets' policy. Partition residual as for a removed host |
| **A manually started session** | No credential | Act as an agent anywhere (decision 3). Be a reply target by default (`reply: known`, Q37) | Read locally, as today (local-pipe.md:95) |
| **Same-user malware** | Reads keys, environments and stores | | Everything the user can, as today (§1.3). The audit log and signatures make its agent-attributed acts visible, not impossible |
| **A compromised host** | Holds its `dh` and `hostSign` keys | Act as another machine's agents (it can't sign as their hosts). Pass person-only checks (#391 §7.2). Reach a machine whose policy excludes it (`fromMachines`) | Sign as any principal *on itself*, invent sessions, and lie about chains under 4.2(b). Bounded by targets' per-origin rates and policy, and visible in their audit. Removed via #391 |
| **A removed host** | Keys still held | Be accepted by any host that has applied the removal, or by a target without a head attestation from another device in the last hour (§8.10, V5) | Inside a relay-maintained partition, unbounded (#391 §6.3). Shown as machines that never confirm the removal |
| **A hostile approver senior to a host** (#391 §3.3) | Can displace the host's enrollment with its own descriptor | Impersonate that machine's agents. A displacing copy lacks the machine's `dh` private key, so it can't carry envelopes (§8.10, V4) | Deny service to the genuine machine, and be announced |
| **A newly enrolled host** | Approved, within 72 h | Send cross-machine agent commands during its probation, unless a person allows it at the target (§8.10, V5) | Act locally |
| **An allow-all session** on any machine | Runs anything typed into it without a prompt | Receive any agent's or connector's reply, local, remote or cross-account (§5.3, Q34) | Be read, within the read policy |
| **The relay** | Untrusted | Read, forge, replay or splice envelopes (Noise, signature, durable replay, channel-peer = signer) | Deny or delay service, observe host-to-host sizes and timing |
| **Clock faults** | A host's clock is wrong | Make a used command run again (durable record, high-water mark) | Make commands fail with `clock` until fixed |
| **Agent loops** | Two well-meaning agents ping-pong | Run unattended past 20 messages or 60 minutes per person-started conversation | Use up to those budgets |

### 10.2 Cross-account

All of 10.1, plus §7.8, and the repository-specific events of §7.11.6. In addition:

| Party | Must not be able to | Can (residual) |
|---|---|---|
| **A stolen person's device of the peer**, holding the link's `linkKey` | Add a host to the link without this side's person confirming it (§7.3). Survive the peer's removal of the device: the tombstone is honoured at once (§7.3) | Re-sign statements for already-accepted hosts until the tombstone arrives or 24 h statement freshness lapses (§7.5) |
| **A collaborator's agent targeting an allow-all session** | Reach it: allow-all sessions are excluded from every `expose` set, without exception (§5.3) | — |
| **A rewritten `rendezvous` pointer** | Collect GitHub tokens: none are ever sent (§7.11.2). Switch directories silently: the origin is pinned (§7.11.2) | Collect T2 directory signatures and descriptors from owners who accept the new origin (§7.11.6) |

The essential difference is that the counterpart **owner** is a party
the design must not trust: consent is per side, scope is minimal by default, the projection hides
what wasn't meant to be shared, external provenance is always visible to both person and agent,
and revocation is unilateral.

### 10.3 Connectors

#394's threat assumptions ("Scope and trust boundary") and §9.5 apply, and are not repeated here.
In addition:

| Party | Must not be able to | Can (residual) |
|---|---|---|
| **A compromised connector** (for example a Home Assistant install, with its key) | Write anything; reach a host other than its selected one; act as a person, host or agent; use an agent's or peer's grants | Read what its grant allows on that host until revoked. Revocation at the host works without the relay |
| **A stolen scoped credential** without the key | Be admitted (the credential is key-bound), or act as an account token anywhere, including on an older relay build (§8.5) | Nothing |
| **The relay** labelling a key `connector`, `app` or `pwa` | Gain anything at an enforcing host: authority is the host's full-key lookup (§8.3) | Deny service |
| **An older, non-enforcing host** on the account | Be reached by a connector under production issuance: the mixed-fleet gate (§8.7) must be met first | Before the gate, it keeps today's baseline gap for *any* admitted key. The gate is why |

---

## 11. Phasing and dependencies

### 11.1 Dependencies

| Dependency | Needed for | Why |
|---|---|---|
| **#55 / PR #391** signed roster: R1 library, R3 host gate and freshness, R6 durable replay. **Not ready**: the sixth review's F1-F3 are being fixed, and owner decisions are open | Anything cross-machine (decision 4) | Verifying the issuing host, revocation, freshness, durable single use |
| **Roster v2** (§8.10): V1-V2 and V4 under Q1 (a); V3, V5 and V6 under either option; V7 per Q30 | Cross-machine agents, federation | To be decided before R1 is built |
| **#489 / PR #501** `userMessageFrom` | Attribution of delivered prompts | The field and its parser rules; this design supplies the verified source |
| **#45** MCP server | Agent-facing tools | The consumer of the pipe ops. Not a blocker for the local slice, which can be exercised from `agent-bridge-host agent …` |
| **#46 / #134** driver | Done for the machinery (`SessionDrivers`) | Reused unchanged |
| **#388** headless identity, and connectors | Not a blocker for agents, in either direction | Connectors are a distinct principal kind with their own lifecycle and a read-only first grant. They share verification machinery, not authority (§8). Their dependencies are in §8.7, and ownership is in §8.9 |
| **Peer-pid on macOS/Linux** | Adoption, and the ancestry half of token binding, there | local-pipe.md:129-130 |

### 11.2 Slices

Each slice is one PR, security-reviewed independently before merge.

| # | Slice | Depends on | Crosses machines? |
|---|---|---|---|
| **AP1** | **Smallest first slice: local agent principals, Windows only** (§3.4). It covers:<br>• credentials issued at launch (`AGENT_BRIDGE_TOKEN`, hashed store with process binding, a Job object per launch);<br>• `agent.hello` with **positively verified ancestry or Job membership, failing closed**, never the environment variable;<br>• the `agents` policy (`enabled`, `issue`, `inbound.local`, `exclude`, `limits`);<br>• local `session.reply`, `session.launch` and reads as an agent connection;<br>• chains, budgets and rates for local targets, and the audit log;<br>• `driverPrincipal = agent:<machine>/<session>`, `driverFrom`, the escaped prefix, and `userMessageFrom = agent` from the verified source;<br>• `agent-bridge-host agent list\|read\|reply\|launch` for tests, and the federation stub fields (§7.9).<br>**Exit criteria, each with a test that fails without it:**<br>• **an allow-all or unknown-mode session is never a reply target** (§5.3), including a session switched to allow-all after launch;<br>• **an agent launch or resume without an explicit `permissions: "ask"` is refused**, as are a non-allowlisted `agent` or `profile`, a resume of an allow-all session, and a `transfer`;<br>• **agent replies are delivered only to idle sessions** (`busy` otherwise), **with `KeySend.Expect` on both the words and the Enter for every agent kind**, Copilot included. A prompt opened between the check and each key types nothing;<br>• leading option keys and `@path` are escaped;<br>• a token from another session or a detached helper is refused, as are `AGENT_BRIDGE_SESSION` alone and a partial ancestry read;<br>• a manual session gets nothing, and isn't a reply target by default;<br>• an agent can't answer an approval or folder trust;<br>• a forged header in a title or body (including confusables) is neutralized;<br>• depth and budget refuse, and the person's reply resets them;<br>• a resumed session's old token is dead | #501 merged | **No** (decision 4 holds) |
| AP2 | #45's MCP server on AP1 (stdio), with the old tool names and outcomes | AP1 | No |
| AP3 | Adoption on Windows: app action, CLI, `agent.credential` | AP1 | No |
| AP4 | Envelope library: encoding, signing (a) or MAC (b), strict parser, shared positive and negative vectors | #391 R1 (+ roster v2 V1-V2 if (a), §8.10) | Library only |
| AP5 | Cross-machine: `agent.command` host-to-host, verification §4.7, `inbound.remote`, `outbound`, durable replay via R6, `instance` on `Session`, `agent.outcome`, `session.read` wait | AP4, #391 R3 + R6, hosts in `enforce` | **Yes** |
| AP6 | Clients: "from *X* on *Y*" in the app and PWA (extending #503/#504), Settings for the policy, the Agent activity view | AP1 (local), AP5 (remote) | n/a |
| AP7 | Re-enable `inbound.remote` defaults per machine during the owner's cutover (#391 R8) | AP5, R8 | Yes, live |
| F1-F6 | Federation (§7.10), with repo discovery (§7.11) as F6 | AP5, owner approval, separate review | Cross-account |
| C1-C6 | Connector API (§9.7). C1 is the read-only first slice | C1: #534, #407. Production (C5): §8.7 | C5 only |

**Why AP1 first.** It delivers the owner's main ask (native, attributed agent messaging) for
sessions on one machine, which is where most agent coordination already happens (the owner's VM
and desk machine each run several sessions). It needs nothing from #55, respects decision 4, and
exercises every rule except the signature, so AP5 adds transport and verification to a policy and
attribution model already proven live.

**Across all three kinds.** The owner's 15:30 PT direction makes the read-only connector API (#534,
under #388) the likely first slice overall. That is C1 in §9.7, which depends only on #534 and
the merged #407, on disposable hosts. It and AP1 share no unbuilt prerequisite (§8.7), so they can
proceed in parallel. The shared pieces (§8.9) are each built once, by their owner, and consumed by
the others.

---

## 12. Open questions for the owner

Each has a recommendation. None is decided by this note.

1. **Q1. What "signs with its device key" means.** (a) Hosts get a P-256 ECDSA `hostSign` key
   through a **roster v2** (§8.10, V1-V2), used only under the agent-command framing label.
   (b) A designated-verifier MAC from the existing ECDH key, as transfers use, with no roster
   format change. **Recommended: (a)**, which gives verifiable audit, verifiable chains and clean
   federation. It must be decided before #391 R1 is built. If v2 slips, (b) lets single-account
   cross-machine messaging proceed with only V3, V5 and V6.
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
9. **Q9. Federation trust establishment and transport.** **Recommended:** F1 links, each scoped
   to one repository and established by repo discovery (F4, §7.11) where it can verify, or by a
   repo-scoped invitation with the 64-bit link code otherwise. Each side records its own
   presence-signed statement with a dedicated per-link key. Host-to-host guest connections on the
   target's relay (R2). Reject cross-signing rosters (F3). Spaces later (F2). A stale peer
   statement (> 24 h) stops the link entirely, not only writes.
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
16. **Q16. The connector's production trust anchor** (§8.7). (a) The #55 roster, with a new
    `connector` kind approved by a presence-proven person's device, plus a local per-host grant.
    (b) #407's local pins as the production anchor before #55, which needs every person's device
    and host pinned by full key at every account-reachable host, and all of them enforcing.
    **Recommended: (a) for production**, with (b) kept to disposable and staging hosts as #407
    already allows. Choosing (b) for production is a separate architecture decision for the
    owner, with its mixed-fleet gate verified first. The grant record is the same under both, so
    (b) → (a) is a re-anchor, not a re-enrollment.
17. **Q17. Who owns the relay credential framework** (purposes, key domains, encodings,
    old-validator rejection, revocation across deployments). **Recommended: #388**, its first
    consumer, whose #394 proposal already specifies it, reviewed by #55. #391 §7.6's bound
    account tokens and #519's guest credentials are further purposes in the same framework.
18. **Q18. A connector write grant later** (replying from Home Assistant, say). **Recommended:**
    a separate reviewed design with its own `automation` attribution, never derived from the
    agent policy. Out of scope for the first grant.

Repo-based contributor discovery (§7.11):

19. **Q19. Is the repository the only path to a cross-account connection?** (a) The repository is
    the only *scope*, with discovery as the default way to establish a link and a repo-scoped
    invitation (link code) as the fallback. (b) Discovery is the only mechanism, which excludes
    Azure DevOps, GitLab and contributors without push access. (c) Invitations only.
    **Recommended: (a)** (§7.11.8).
20. **Q20. Proof that a descriptor belongs to GitHub user X.** **Recommended:** an SSH or GPG
    signature by a key X has registered on GitHub, with the namespace `agent-bridge-link-v1`
    (T2). A relay's OAuth attestation is never sufficient alone. Offer the link code on every
    suggestion, and require it when T2 isn't available. A changed key is a new identity.
21. **Q21. Where descriptors are published.** **Recommended:** nowhere public. A members-only
    directory on a rendezvous relay (D4), gated by GitHub push access and expiring after 30
    days. The only optional repo file is a non-personal `.agent-bridge/rendezvous` relay pointer.
    Per-contributor files in the repo (D1) are offered only in private repos, as an explicit
    opt-in.
22. **Q22. What counts as a contributor.** **Recommended:** push access now, checked by each
    side's own app every 24 h and confirmed to its hosts within 72 h. Commit authorship never
    counts. A per-repo "merged contributors" mode for open source is a later option, off by
    default.
23. **Q23. Who operates the directory for the production relay, and is the operator learning
    "user X uses the bridge on repo R, through relay E" acceptable?** **Recommended:** yes, the
    production relay hosts it, and that disclosure is stated in the opt-in dialog. A team that
    objects uses the invitation path, or its own rendezvous relay.
24. **Q24. Suggestions' reach.** **Recommended:** the app only, as a quiet badge on the
    Collaboration page: never a push, a toast or a phone notification. Opt-in per repository, with
    "Never for this repo", and nothing connects until both owners accept.

The connector API (§9):

25. **Q25. One protocol with connector profiles, or a separate connector protocol?**
    **Recommended:** one protocol (Bridge Protocol v1) with separately versioned profiles,
    selected by grant and pinned by host contract tests. Major versions are supported for at
    least 12 months after their successor ships. There is no plaintext REST gateway, and
    reference SDKs exist in .NET, JavaScript and Python (§9.1).
26. **Q26. An `act` profile later: which acts, and may connectors answer `ask_user` forms?**
    **Recommended:** later, each act its own grant (`session.reply`, `session.stop`, launch with
    `ask` only), with `automation` attribution, and **no** answering of forms in v1. A reply act gets
    §5.2's idle-only, screen-guarded delivery and §5.3's allow-all exclusion, as agents do. Approvals
    and folder trust are never available in any profile. This means Home Assistant no longer
    answers approvals as agent-ha-bridge did; the app and PWA do.
27. **Q27. Default grant expiry for a connector.** **Recommended:** 1 year, with a reminder 14
    days before and renewal with presence. Access credentials stay short (#394), and revocation is
    immediate whatever the expiry.
28. **Q28. A content-free projection for dashboards.** **Recommended:** yes, as a later scope
    option (title, status, attention only). The first grant is the full `read` profile as D
    proposed.
29. **Q29. Who owns the connector API?** **Recommended:** #388, with its scope widened from
    "headless identity" to "connector API (product)", building on D's #534. Alternatively, a new
    issue that #388 and agent-bridge-home-assistant#2 depend on. Either way, Home Assistant
    specifics stay in the Home Assistant connector repository.

The roster v2 requirements (§8.10), from the sixth independent review of #391:

30. **Q30. How a target verifies another account's hosts.** (A) Peer host statements signed by
    a per-link key, which is pinned by the link code or the GitHub-key proof. (B) A pinned foreign
    genesis, plus a read-only foreign-roster path in #391. **Recommended: (A)**, recording the
    peer's `genesis` and `generation` so a reset invalidates the link. (B) discloses the whole
    roster and duplicates #391's acceptance rule across accounts.
31. **Q31. The freshness window for accepting agent commands.** **Recommended: 1 hour**: a head
    attestation from an active device other than the issuer, against #391's general 24 h.
    Refuse with `stale` otherwise. Usage peers every 5 minutes make this normal. The partition
    residual is stated, not solved.
32. **Q32. Probation for agent messaging.** **Recommended:** a host add in its 72 h probation, or
    one that displaced another, sends no cross-machine agent commands. A person can allow it
    sooner **at each target machine** only, because #391's `confirm` is offered after 72 h.
33. **Q33. Roster v2 activation.** **Recommended:** a one-way, per-roster switch by a person's
    device, offered only after every rostered verifier advertises v2 support, never inferred.
    Hosts keep v1 behaviour (no host-signed agent commands) until then.

Decisions the reviews of `f3559996` asked to make explicit:

34. **Q34. Replies into allow-all sessions.** **Recommended: never**, for any agent or connector
    principal, local, remote or cross-account. It is a hard-coded rule (§5.3), not a setting, and
    an unknown permission mode counts as allow-all. Reading them stays within the read policy. If
    the owner ever wants an exception, it would be a per-session switch set at that machine, for
    same-account local agents only, and it defaults to **no**.
35. **Q35. Replies into working sessions.** **Recommended:** refused as `busy`, idle only (§5.2).
    Host-side queuing, delivered at the next idle with the guard re-run, is a later option. It
    isn't in v1, because a queued message can arrive after the context that justified it has
    changed.
36. **Q36. Agent launches with `permissions` or `profile` absent.** **Recommended:** refuse.
    `permissions` must be explicitly `ask`, never filled from the machine's defaults. `agent` and
    `profile` must be on the policy's allowlists, and any agent, profile or model setting the host
    can't positively show to be non-allow-all is refused (§5.2).
37. **Q37. Manually started or private sessions as targets.** **Recommended:** readable by default
    (decision 2), but **reply targets only if the host launched or adopted them** (`reply: known`).
    `any` is an explicit per-machine choice, still subject to §5.3. `inbound.exclude` marks a
    private session unreadable too.
38. **Q38. GitHub token type, scopes and storage, for repo discovery.** **Recommended:**
    * a **GitHub App user-to-server token** from the Agent Bridge GitHub App, read-only:
      repository metadata, and the collaborators check on repositories the person selects;
    * expiring and refreshed;
    * stored only on the **person's** devices (DPAPI, Keychain, IndexedDB with a non-extractable
      wrapping key), never on hosts or relays;
    * sent only to GitHub, and to the owner's own sign-in relay during sign-in (§7.11.2).
39. **Q39. Link-key custody, rotation and revocation.** **Recommended:** §7.3:
    * custody in the presence-proven device's hardware tier, signing with presence;
    * rotation endorsed by the old key and a person command;
    * a tombstone either side can sign, honoured at once;
    * automatic revocation when the custodian device leaves the roster;
    * new hosts in a peer statement need the other person's confirmation.
40. **Q40. A repo link's default scope.** **Recommended:** nothing pre-ticked, matching §7.4's
    "None". The person ticks read and/or reply, and launch separately.
41. **Q41. The workspace-to-repo binding.** **Recommended:** pinned at acceptance by the person,
    by workspace label and path, and re-confirmed on any remote change. Never derived per message
    from live git settings (§7.11.5).
42. **Q42. `credentialDays` and sub-agents.** **Recommended:** 7 days, then one press at the
    machine to re-adopt. Sub-agents share the parent session's credential, since they are the
    same process tree and the same principal. The audit records the sub-agent where the host can
    tell. Per-sub-agent scopes are a later option.
43. **Q43. The in-host Home Assistant press path after AP1.** Presses by
    `homeAssistant.agentUserIds`, attributed `homeassistant:<user id>`. **Recommended:** keep
    it unchanged as the G9 compatibility path until #319 retires it, labelled in `driverFrom` as
    `kind: "homeassistant"`, distinct from native agents. No new feature uses it, and agents are
    pointed at AP1 and AP2 instead.
