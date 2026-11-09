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
  Never an **unattended** reply into an allow-all session, and never a launch that isn't
  explicitly `ask` (§5.2, §5.3). The owner's opt-ins (§5.7) add a per-message approval, a
  same-account setting the owner enables knowingly, a time-limited coordination group the owner
  approves with presence, and read-only cross-account access.
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
| Agents have **no principal** on the pipe or the relay. A process inside an agent session may only read (`hello` offers `sessions.read` and `usage` only), and acting ops are refused with "answering, replying, ending and launching sessions are for the person" | `src/AgentBridge.Host/LocalApi.cs:22-26`, `:44-47`; local-pipe.md:95 | The pipe is the place to add an authenticated agent principal. Revision 14 goes further than today's refusal: every caller that is neither the app nor a session-bound agent connection gets typed status only, with no acting ops (§3.4), including manually started sessions (decision 3) |
| The pipe already knows **which session a caller is inside**: by the caller's process ancestry, matched by process id *and* start time, or by `AGENT_BRIDGE_SESSION` in its environment | `src/AgentBridge.Local/AgentAncestry.cs:12-41`; `Launchers.SessionVariable` (`src/AgentBridge.Host.Delivery/Launch/Launchers.cs:34`); local-pipe.md:95-112 | Credential binding can be checked against ancestry, not only against a bearer token (§3.4) |
| A launched agent gets the host's environment plus `AGENT_BRIDGE_SESSION`. "No agent principal yet (#45)" | host-launch.md:68 | `AGENT_BRIDGE_TOKEN` (design.md:495, §5.7 table) is the planned replacement for `AGENT_HA_AGENT_TOKEN`; this note defines it (§3.3) |
| The only agent principal a host recognizes is a Home Assistant user in `homeAssistant.agentUserIds`: `driverPrincipal` is `homeassistant:<user id>` | v1-sessions.md:868-873; `SessionDrivers.HomeAssistantPrincipal` (`src/AgentBridge.Host.Core/SessionDrivers.cs:71-72`) | The driver machinery (arm, 600 s window, take-back) is reused unchanged. Only the principal's source changes |
| `driver` and `driverPrincipal` (≤ 128 bytes, plain text, "secondary detail only") are in the protocol | v1-sessions.md:832-833, :845-877 | Additive fields carry the structured sender (§6) |
| #489 / PR #501 adds `activity.userMessageFrom` (`person`, `schedule`, `agent`, `other`) and `userMessageLabel` (≤ 64 bytes). It marks a bridge reply as `agent` when its driver is an agent, by the prompt's record and text. #501's security review asks that no agent-supplied field can set or clear it | PR #501; #489 comments | A verified envelope is the source for `agent`. Nothing in the envelope's free text can set it (§6.3) |
| Host-to-host channels already exist through the relay: usage peers every 5 minutes, and transfers | `src/AgentBridge.Host/UsagePeers.cs`; v1-transfer.md:154-160; `ChannelHub.OpenAsync` (`src/AgentBridge.Transport/ChannelHub.cs:149`) | Agent commands ride the same kind of channel (§4.6) |
| Device keys are **P-256 ECDH-only** (TPM, WebCrypto non-extractable, Secure Enclave key agreement). The only "signed command" is a designated-verifier MAC from static-static ECDH, `CommandMac` | `src/AgentBridge.Transport/CommandMac.cs:18-31`; `SignedCommands.cs:9-77`; v1-transfer.md:87-147; #391 §1 | "Signs with its device key" needs a choice: a MAC that only the target can check, or a host signing key (§4.2, open question Q1) |
| The replay memory for signed commands is **in memory** and forgets across a restart | `CommandMemory`, `SignedCommands.cs:79-101`; #391 §7.2 (R6 makes it durable) | Agent commands use #391 R6's durable replay store (§4.4) |
| The relay routes **only within one account**. It takes `kind` from the device's own `hello`, and tokens are bearer tokens not bound to a key | `RelayHub.cs:13-16`, `:160-185`, `:249` ("Routing never leaves the sender's account"); `Tokens.cs:9-45` | Single-account messaging needs no relay change. Federation needs a relay change (§7.3) |
| A reply is never typed while the session is asking anything. Folder trust is answered only by a person's device through the relay, never from the pipe | v1-sessions.md:460, :468; v1-launch.md:139-147, :191-232 | Decision 1's "never approvals or trust" is already enforced by the existing paths. This design adds no path around them, and a policy check on top (§5.2) |
| design.md §5.6 grants local `agent@session` "read, reply, **answer**, launch with `ask` permissions" | design.md:495 | It conflicts with decision 1, which omits answer. This note follows the decision (Q3) |
| design.md §13 item 8: "Decide whether remote agent actions need a per-target grant, defaulting to off for launch with allow-all" | design.md:1356-1358 | Answered by §5. Allow-all is never available to an agent as a launch (§5.2). As a reply target it needs a per-target grant: a per-message, presence-signed approval, or the owner's own-account setting (§5.3, §5.7) |
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
| `depth` | How many distinct sessions the conversation driving this session's current turn has reached (§5.4) | The host's driver state |

`F` is #391 §3.2's length-prefixed framing: each field as a 4-byte big-endian length, then the
bytes.

**Display string.** `driverPrincipal` becomes `agent:<machine>/<session>`, at most 75 ASCII bytes
(6 + 32 + 1 + 36), which fits v1-sessions.md:833's 128-byte limit. It is an identifier. What a
person reads ("from copilot on *DSWETT-HOME*") is built from verified structured fields
(§6.1, §6.3).

### 3.2 Who gets one (decision 3)

| Session | Credential? | How |
|---|---|---|
| **Launched by the host**: `session.launch` from any device, a launch by an agent, a resume, a transfer, or `agent-bridge-host launch` | **Yes**, at launch | The host mints it before it starts the process, and puts it in the child's environment (§3.3). A Codex launch, whose id isn't known in advance, is bound when the launch binds its session (v1-launch.md:153-156) |
| **Adopted** | **Yes**, at adoption | A person at the machine adopts a live session the host discovered (§3.5) |
| **Manually started** (typed in a terminal, never adopted) | **No** | Its processes are unattributed pipe callers: **typed status only, and no acting ops** (§3.4), superseding local-pipe.md:95. It has no remote reach, and no attribution, because there is nothing to attribute |
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
  * a launch made **by an agent** can never widen its sender's scope, and inherits its
    conversation `chain`, in which the launched session counts as one more distinct session (§5.4).
* **Expiry.** A token is valid while its session's bound process lives, and for at most
  `agents.credentialDays` (default **7**, Q42). A longer-lived session is renewed with one press
  at the machine, or **remotely** from a person's device with presence. A remote renewal is
  for the **same live process** (id and start time), never increases the scope, rechecks the
  policy, and never resurrects a credential whose process has ended.

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

   **This check is a safety rail, not a security boundary.** It stops the accidental and the
   casual cases: a token pasted into another session, a helper that didn't try. It doesn't stop
   a same-user process that tries, because Windows lets one:
   * **spoof its parent:** `CreateProcess` with `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` names any
     process the user can open as the parent, so a new process can appear inside the session's
     ancestry, and it inherits that parent's Job too;
   * **join the Job:** a process that can open or duplicate a handle to the session's Job object
     (same user, default DACL) can `AssignProcessToJobObject` itself, or a child.

   So the boundary for same-user code is the one §1.3 and local-pipe.md:121-130 already draw: it can do what
   the user can. The rail's value is attribution and audit, and turning a token leak into
   something that needs deliberate tooling. The host could harden it (a Job with a restrictive
   DACL, a launch under a per-session restricted token), but v1 doesn't claim it.
3. **The session's process is still the bound one** (id and start time), so a recycled pid can't
   inherit it.

**The residuals.** Parent-PID spoofing and Job joining, above. And **subprocess inheritance**:
every process the agent runs inherits the token and is
inside the session's ancestry and Job: a build script, a test runner, an `npm install` hook. Any of
them can act as the agent, within the agent's scope. That is inherent to giving a session a
credential, and it is why scope, budgets and the §5.3 exclusions matter more than the token.

On success the connection is an **agent connection** for its lifetime. Its `hello` capabilities
are the credential's scope (`sessions.read`, `agent.reply`, `agent.launch`, `agent.remote`), and
each later op is attributed to the principal without resending the token.

**Unattributed callers get typed status and nothing else** (revisions 13 and 14, superseding
local-pipe.md:93-98).
* **Who they are.** Every pipe caller except the app and a session-bound agent connection:
  * a pipe caller inside an agent session without `agent.hello`;
  * the host's command line;
  * anything else of the user's (local-pipe.md:98's "the sessions ops as before");
  * HTTP MCP (§3.6).
* **Reads.** Taint (§5.8) can only follow a read the host can attribute to a session, so every
  **content-bearing** read an agent could consume requires a session-bound agent connection:
  `sessions.list` with titles, `session.get`, `session.read` and `sessions.subscribe`. Unattributed
  callers get only the **typed, non-tainting projections**: `sessions.status`, `session.status`,
  and `launch.options`, which agents and unattributed callers only ever get without titles
  (§5.2, §5.8).
* **No acting ops.** Unattributed callers can't reply, launch, answer, stop, transfer or send
  attachments. Today `LocalApi.cs:14-15` and `:44-47` refuse acts only for `CallerKind.Agent`, so
  a non-agent same-user process, HTTP MCP included, could act **as the person**: into an
  allow-all session, with no gate, O, taint, prefix or budget. A tools-only prompt-injected agent
  would be enough. Acting now needs either the app (a person client) or a session-bound agent
  connection, which is attributed and goes through §5.
* **The app** keeps full rights. It is a person client, identified as local-pipe.md:96
  describes. Same-user code that imitates the app is the §1.3 residual.
* **The host's command line has three explicit exceptions**: `agent-bridge-host launch`,
  `agents adopt` and `agents revoke`. Each is allowed only under local-pipe.md's full-ancestry
  check (not inside an agent session, its whole ancestry read, its executable the host's own,
  local-pipe.md:97, :108-111). `launch` from the CLI is a person's launch, and the CLI offers no
  other acting op.
* This is a protocol change, shipped in AP8.

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
  else. As in step 2, this is a safety rail: a same-user process that spoofs its parent into the
  adopted session's tree gets the token. **Windows only** until step 2's peer identification
  lands on macOS and Linux.
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
* **Typed by default.** `list_agent_sessions` returns the typed, non-tainting projection (§5.8),
  and a new `get_agent_status` returns one session's. Content (`list_agent_sessions` with
  `include: "content"`, and `read_agent_session`) is an explicit request, and taints the caller
  with whatever it reads.
* **Local and remote targets.** `list_agent_sessions` lists this host's sessions and, through
  `agent.machines` (the roster's active hosts, after #55), other machines. A tool naming another
  machine becomes `agent.remote` on the pipe. The local host signs and sends it (§4) and returns
  the target's result unchanged. The MCP server never holds a key, a relay token or the device
  key.
* **HTTP mode** (`127.0.0.1:8808`, #45). It isn't a child of one session, so it can't be
  attributed by ancestry. Recommended: no agent principal over HTTP in v1 (Q8). As an
  **unattributed** caller it gets **typed status only, and no acting tools** (§3.4): no reply,
  launch, answer or stop. Otherwise a clean session could read a tainted one over HTTP and then
  use O, or a prompt-injected agent could act as the person through it. In v1 HTTP mode ships
  read-only, with typed status as its only tools.
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
    "prev": "<base64url SHA-256 of the envelope that drove this sender's turn; absent at the root>",
    "depth": 3,
    "messages": 2,
    "via": [
      { "account": "…", "machine": "…", "session": "<the conversation's first sender>" },
      { "account": "…", "machine": "…", "session": "<this sender>" }
    ]
  },
  "provenance": {
    "origins": [ { "account": "…", "category": "external", "firstAt": 1793299000 } ],
    "oEligible": false,
    "window": "<window id, when sent under a G window>"
  },
  "action": "session.reply",
  "target": {
    "account": "<target account id>",
    "machine": "<target host device id>",
    "session": "<target session id>",
    "instance": "<12-byte base64url>",
    "link": "<federation link id; absent within one account>",
    "repo": "<GitHub repository id of a repo-scoped link; absent otherwise>"
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
| `account` | The issuer's account. In v1 it must equal the target host's own account (§7.9). An account id is 32 lowercase hex, or 1 to 20 ASCII digits with no leading zero (a numeric GitHub id); the same grammar applies to `chain.via[].account`, `provenance.origins[].account` and `target.account`. Device, machine, session and generation ids stay 32 lowercase hex |
| `generation` | The issuer's roster generation (#391 §3.2). A command from another generation (a reset roster) is refused |
| `heads` | The issuer's current roster heads, as #391 person commands carry them (#391 §7.2) |
| `principal` | §3.1, plus `add`: the hash of the issuing host's add entry. Principals are keyed by **account + generation + add hash**, because device ids aren't namespaced across accounts and a copy shares its original's id (§8.10, V3, V7). `machine` must be the channel peer that delivered it: a host never relays another host's envelope as its own (§4.6) |
| `chain` | §5.4. `via` is the conversation's **distinct** sending sessions in first-seen order, this message's sender included, so it holds at most `maxDepth` entries however many messages the conversation has, or at most the group's member count while every entry and the target are members of one verified coordination group (§5.7 C). `depth` is the number of distinct sessions **including the target**. `messages` counts every agent message, repeats included, under the per-conversation budget. `prev` is the hash of the envelope that drove this sender's turn. Each host **rebuilds** `via` from the envelope it received and signs it inside its own envelope; the target never trusts it as claimed but recomputes `depth` and a floor for `messages` from the signed `prev` chain (§5.4, §4.7 step 10). **Across accounts** the envelope carries no `prev`, and `via` holds the sender alone, so internal session ids and other hops' envelopes never reach the peer; the receiver treats the peer's `chain` as opaque and re-roots it at the link (§7.5). `anchor` (`{ link, envelope, account, session }`: the link, the hash of the link envelope, and the peer account and session) replaces `prev` in an envelope whose chain began at a link; it is signed by the host that verified that link envelope |
| `provenance` | **Mandatory, host-authored** (§5.8). `origins` is the sending session's full retained origin set, or the single value `"unknown"`. `oEligible` is `true`, `false` or `"unknown"`: the issuing host's attestation that the sender passes every O exclusion it can see (§5.7 O). `window` names the G window the message claims. **Only a host that implements AP8's taint tracking** may advertise `agent.provenance` (versioned, v1) or send an explicit origin set; an AP5-era host sends `"unknown"`. The target allows O only when `oEligible` is `true` **and `origins` is exactly `[]`**, and **pauses the window** when `origins` include one outside the window's fixed origin set. Anything missing, legacy or `"unknown"` becomes the sticky origin `unknown:<machine>` on whatever receives it (§5.8), fails closed for O and windows, and still allows per-message G with the prompt saying "origins unknown". **Trust:** within one account, a host's provenance is trusted, so a compromised own host can claim a clean, O-eligible sender (§10.1). Across accounts it is **never** used to authorize anything: the receiver always adds `external:<peer account>` itself, shows the peer's listed origins only as the peer's claim, and drops any that name the receiver's own account |
| `action` | `sessions.status` and `session.status` (typed, non-tainting, §5.8), `sessions.list`, `session.get`, `session.read` (§4.7), `sessions.subscribe` (only for `principal.kind: "person"`, §7.12; agents follow a session with `session.read`'s wait), `session.reply`, `launch.options`, `session.launch`, `launch.result`. **Nothing else verifies.** In particular `session.answer`, `launch.trust.answer`, `session.stop`, `session.transfer`, `attachment.*`, `update.*`, `push.*` and `host.*` are not in the action set, so the envelope cannot express them. A `principal.kind: "person"` across a link (§7.12) may use only `sessions.status`, `session.status`, `sessions.list`, `session.get`, `session.read` and `sessions.subscribe` |
| `target` | `account` and `machine` must be the target host's own. `session` is required for `session.get`, `session.read` and `session.reply`. `instance` is required for `session.reply` and optional for reads (§4.5). Absent for `sessions.list`, `launch.options` and `session.launch`. `link` and `repo` are absent within one account. Across accounts `link` is required, and `repo` is required for a repo-scoped link: both must equal the link the channel was admitted under (§7.3, "Link context binding"), so a relay can't make a command run under another link's scope |
| `dataSha256` | Binds the op's `data` (the reply text, the launch settings and prompt) to the signature: the SHA-256 of the exact bytes of the inner op's `data` JSON, which travel base64url-encoded beside the envelope (§4.6), so neither host re-serializes anything. Beside, not inside, so the envelope stays under 2 KiB and the 16 KiB reply and 20 KiB launch limits (v1-sessions.md:430; v1-launch.md:105-115) are unchanged |
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
  exactly `session.reply`'s and `session.launch`'s `request` semantics today (v1-sessions.md:429;
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
| The **target host restarted** (`machine.instance` changed, v1-sessions.md:173) | Nothing for the command: `instance` is computed from the session's process, not the host's run. A reply to a session whose process lived through the restart is still delivered |
| The **session was resumed** (new process, same id) | `instance` differs, and `session.reply` is refused as `stale`, saying so. The issuer re-reads (`session.get` returns the new `instance`) and decides again. A message meant for one conversation is never typed into its successor unseen. This is the same rule `session.stop` already applies with id and start time (v1-sessions.md:619-628) |
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
* **Op.** Two new host-to-host ops: `agent.command` and `agent.chainLink` (below). The first:

  ```json
  { "id": 9, "op": "agent.command", "data": { "command": { "body": "…", "sig": "…" }, "data": "eyJpZCI6Ii4uLiIsInRleHQiOiIuLi4ifQ" } }
  ```

  The inner `data` is the exact UTF-8 bytes of the inner op's request `data` (here
  `{"id":"…","text":"…"}` for `session.reply`), base64url without padding. Base64 adds a third
  to a 16 KiB reply, which still fits v1's 48 KiB message. The result is the inner op's result, unchanged (`delivered`, `unconfirmed`, `busy`, `notIdle` (§5.2), `held` (§5.7), `refused`,
  `stale`, …; a launch's `started`, …). A refusal is `{ ok: false, error }` with a reason in
  words, which the issuing host passes back to the agent.
* **Capability.** `agent.command` in `hello` (host-to-host channels only), and only while the host
  is in roster `enforce` mode. A host without it is never sent one. A client channel (`app`,
  `pwa`, `phone`) never gets it.
* **`agent.chainLink`**, so a target can recompute a chain (§5.4):

  ```json
  { "id": 10, "op": "agent.chainLink", "data": { "hash": "<prev hash>", "citing": { "body": "…", "sig": "…" } } }
  ```

  * **Who serves it, and where.** Only on an **own-account, `enforce`-mode, host-to-host channel
    between active, fresh, rostered hosts**: the same gate as `agent.command`. Never on a guest
    or link channel, never to a client, connector or agent connection. It is **internal-only** in
    the op classification (§9.4).
  * **What the requester must present.** `citing` is the envelope whose `prev` is `hash`: the one
    the requester received, or one it obtained by an earlier `agent.chainLink` in the same walk.
    The server answers only if `citing` verifies, was issued by the server itself
    (`principal.machine`), is in its own account, and has `prev` == `hash`. It then returns the
    cited envelope (body and signature, never the op's data bytes). For a cited `session.launch`
    it also returns its own signed `launch.result` record `{ request, session, instance }`, so the
    walker can bind the launch to the session it created.
  * **Bounds.** A walk stops after `conversationMessages` links, and each fetch has a 5 s
    timeout. Either limit, a refusal or a link that doesn't verify gives `chain-unverifiable`.
    Hosts keep the envelopes they sent and received for a conversation for
    `conversationMinutes`, and cache the envelopes they have verified per `root`.
* **Provenance on results, and its capability.** Taint has to cross machines in both directions
  (§5.8):
  * **Commands** carry `provenance` in the signed body (§4.3).
  * **Every content-bearing result** (`sessions.list` with titles, `session.get`, `session.read`
    and its wait results) carries `provenance: { origins }`: the union of the origins of every
    session whose content it includes, signed by the **target** host's `hostSign` over the
    result's hash.
  * The issuing host **merges those origins into the reading session before it hands over any
    content**. A clean coordinator on M1 that reads a tainted session on M2 is tainted on M1.
  * Typed projections carry no content and no provenance.
  * Both ends advertise `agent.provenance` (v1) in `hello`, and both enforce it. Only a host
    that implements AP8's taint tracking may advertise it. A peer without it gets typed reads
    only, and its commands count as `unknown` provenance: the sticky origin `unknown:<machine>`,
    O and window delivery refused, per-message G allowed.
  * **Pending waits are rechecked.** A `session.read` that waits for the next response is bound
    to the read grant's generation. Narrowing the grant (to status only, an exclusion, a
    revocation) cancels outstanding waits, and scope and projection are rechecked immediately
    before each delayed result is emitted, for own-account and cross-account reads alike.
  * **Subscriptions merge taint per event.** For a local agent's `sessions.subscribe`, each
    event that carries content merges the subscribed session's current origins into the reader,
    including when that session becomes tainted mid-stream.
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
    eligibility, chain and budget, and rates. **The chain**, under 4.2(a), is recomputed, not
    taken as claimed (§5.4): T walks the signed `prev` links with `agent.chainLink` (§4.6), at
    most `conversationMessages` links with a 5 s timeout per fetch, back to the root, to an
    envelope carrying an `anchor` (accepted on its issuing host's signature, never walked past),
    or to an envelope it has already verified for that `root`, and checks that `via` is exactly the deduplicated list of their senders
    plus this one, and computes `depth` (distinct sessions, `target.session` included) and a
    floor for `messages` from that chain. A missing or unverifiable link, or a `via` that differs,
    is `chain-unverifiable`; `depth > maxDepth` is `chain-too-deep`, except that while `via` and
    the target all lie within one verified coordination group the bound is that group's member
    count (§5.7 C); the larger of the claimed,
    recomputed and locally counted `messages` above the budget is `conversation-budget`.
    **Provenance** is part of it: O only with `origins == []` and `oEligible: true`; a window
    delivery only when `origins` are within the window's fixed set, otherwise the window pauses
    and the message becomes a per-message hold; and the target session inherits the origins on
    delivery, with missing, legacy or `"unknown"` provenance inherited as the sticky origin
    `unknown:<machine>` (§4.3, §5.8). Across accounts T adds `external:<peer>` itself.
11. **The op's own checks**, unchanged: a reply is refused while a question or permission prompt
    is pending (v1-sessions.md:460, :468), a launch only in an approved workspace with the
    offered values (v1-launch.md:16-18), `busy` while a trust question waits (v1-launch.md:139-147),
    no `/` or `!` commands (v1-sessions.md:440), control characters collapsed, read-only machines and agent-ha-bridge
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

**The framing, from the adversarial PM/Security review (2026-11-03, revision 11).**
* **Forced-ask is an optimisation, not the main path.** No signal of an organisation's effective
  policy has been established, and isolation can't be certified (§5.3). So for most people,
  including everyone on a managed Copilot, Claude or Codex plan, agent messages travel through
  the **per-message gate (G)** of §5.7. **G, with quiet holds, batches, windows and workflow
  budgets, is the product** and gets first-class UX. The own-account setting (O) is a narrow,
  clean-workspace mode beside it (§5.8; owner decision, Q34).
* **Authorizing an action never cleanses its provenance.** An approved message, launch or
  forward still says where its content came from (§5.8).
* **A new process is not a clean context.** Taint follows whatever context the agent retains:
  across person-driven turns, resume, transfer and compaction (§5.8).
* **Hygiene isn't a boundary.** The provenance prefix (§6.4), look-alike filtering (§6.3) and
  storing no names (§7.11.10) reduce confusion and exposure. None of them is a security boundary;
  the host's checks are.

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
    "local":  { "read": "any", "reply": "forced-ask", "gate": "quiet", "allowAllTargets": false, "coordinationGroups": false, "launch": { "workspaces": "approved", "permissions": "ask", "agents": ["<default>"], "profiles": ["<default>"] } },
    "remote": {
      "fromMachines": "any",
      "read": "any",
      "reply": "forced-ask",
      "gate": "quiet",
      "allowAllTargets": false,
      "coordinationGroups": false,
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
    "launchesPerMachinePerHour": 10,
    "gateWindowPerMinute": 2,
    "allowAllPerTargetPerMinute": 3
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
| `inbound.*.read` | `any` \| `agent-launched` \| `none` | Which sessions on this machine an agent may read. `any` is any live session. `agent-launched` is only sessions an agent launched through the bridge. `none` is none. **Default: `any`** |
| `inbound.*.reply` | `forced-ask` \| `agent-launched` \| `none` | Which sessions an agent may reply to. `forced-ask` is every session the host launched in forced-ask mode with no grant or mode change since (§5.3), which is the widest possible value. `agent-launched` is the subset an agent launched. `none` is none. **Default: `forced-ask`** (Q37). Adopted and manually started sessions are never reply targets, because their mode is unknown (§5.3) |
| `inbound.*.gate` | `off` \| `quiet` \| `push` | Whether a reply or launch into a session that **isn't provably forced-ask** (allow-all or unknown-mode, §5.3) is **held** for the owner's per-message, presence-signed approval instead of refused (§5.7 G). `quiet` holds are counted on Needs-you and never pushed; `push` allows at most one aggregated, text-free push an hour. **Default: `quiet`** for the account's own agents; cross-account G is per link (`expose.gate`). Never offered to connectors |
| `inbound.*.allowAllTargets` | `false`, or `{ workspaces: "same" \| "any" }` | Whether **this account's own** agents may reply into allow-all and unknown-mode sessions on this machine **without** a gate (§5.7 O). Set in `inbound.local`, it covers agents on this machine; in `inbound.remote`, agents on the account's other machines, which needs `workspaces: "any"` since `"same"` never matches across machines. Never for another account, a connector, a federated or `homeassistant:` principal, or a chain with a cross-account hop. **Default: `false`** in both. When enabled, the app proposes `inbound.local` with `workspaces: "same"`, and shows the escalation warning |
| `inbound.*.coordinationGroups` | `false`, or `{ maxHours: 4..24, perMemberPerDay, perGroupPerMinute }` | Whether this machine's sessions may be members of an owner-approved **standing coordination group** (§5.7 C), and the ceilings on any group's length and budgets here. Set only at this machine. Honoured only in roster `enforce` mode; `inbound.remote` waits for #55 and AP5. Never for another account, a connector, a federated or `homeassistant:` principal. **Default: `false`** in both. When enabled, the app proposes `{ maxHours: 24, perMemberPerDay: 200, perGroupPerMinute: 10 }` |
| `inbound.*.launch` | `false`, or `{ workspaces: "approved" \| [labels], permissions: "ask", agents: [ids], profiles: [names] }` | Where an agent may launch. The request must state `permissions` **explicitly** (§5.2). An **unattended** launch is `ask` only. `allowAll` is possible only as a **new gated launch** (§5.7 G): the person approves the exact manifest, prompt, base commit and fresh worktree with presence, so it stays a person's decision, as design.md §5.6 requires even from a phone. `agents` and `profiles` are **allowlists**, empty by default except the machine's default agent and profile, each checked not to grant allow-all (§5.2) |
| `inbound.remote.fromMachines` | `any` \| `[add hash]` | Which of the account's machines may send. `any` means any active, non-probationary rostered host. A list names **add entries**, which the app picks by machine name, never bare device ids. A machine that is re-enrolled or displaced has a new add hash, so it drops out until the owner re-selects it (§8.10, V3, V4) |
| `inbound.exclude` | session ids and workspace labels | Never reachable by any agent, local or remote. A person can mark a private session or workspace this way |
| `limits.*` | integers | §5.4, §5.5 |
| `label` | `prefix` \| `none` | Whether delivered text carries the provenance prefix (§6.4). `prefix` is forced for cross-account |
| `links` | `[]` | Federation (§7). Must be empty in v1 |
| `agentMessaging.remote`, `agentMessaging.allowAllTargets` | `"off"` \| `"own-account"`; `false` \| `true` | The **temporary AP5-lite mode** (#728), in the host's `config.json`, not this policy. Owner-decided 2026-11-08 for the owner's own agents on the owner's own account only, until the signed roster (#55) lands, when it is deleted or folded into AP5. **Default: `"off"` and `false`**; an unknown value is off. `allowAllTargets` counts only while `remote` is `"own-account"`; `false` sends allow-all targets to Home Assistant. Read per message, so a change needs no restart ([host-config.md](../host-config.md)) |

**Defaults** (Q2, Q37). Decision 2 is "any session on the owner's account, subject to a
per-machine policy". So the defaults let any session be **read** from the owner's other machines,
and let **replies** go to sessions the host launched in forced-ask mode (§5.3). Launch is
allowed in approved workspaces with an explicit `ask` only. The policy is the only way to narrow
it, and the app shows it as plain choices ("Agents on your other machines can: read ✓, reply to
sessions started by Agent Bridge ✓, launch in approved folders ✓"). The gate (§5.7 G) is on in
`quiet` mode, so own agents' messages to sessions that aren't provably forced-ask wait for you
instead of failing. The own-account setting (O), coordination groups (C) and the link opt-ins
are listed separately, off, each with its warning. Until a host is in roster
`enforce` mode, `inbound.remote` is ignored and treated as `none` (decision 4).

### 5.2 Allowed actions

| Action | Agent may? | Enforced by |
|---|---|---|
| `sessions.list`, `session.get`, `session.read` (wait for change), `launch.options`, `launch.result` | Yes, if `read` permits. `launch.options` reaches agents **without titles**: each `resumable[]` entry is a reader-generated handle plus typed fields (agent, model, status, last active), never its free-text `title` (v1-launch.md:50-52, :66), which a tainted or external launch could set | Policy. Sessions not readable are left out of lists, and are `not-found` when named |
| `session.reply` (text only) | Yes, if `reply` permits, and only into a target §5.3 allows, or through an owner's opt-in of §5.7 (a held, approved message, or the own-account setting) | Policy, §5.3, then the existing reply checks (v1-sessions.md:413-470), **then the agent-reply delivery guard below** |
| `session.launch` (new, or resume) | Yes, if `launch` permits, with **explicit** `permissions`: `"ask"` for an unattended (provably forced-ask) launch, or `"allowAll"` only as a **new** gated launch the person approves with presence (§5.7 G) | Policy, the launch rules below, then `LaunchProtocol.Check` and the machine's offered values |
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
   (`status: idle`, no `activity.background`, no question). That is re-checked immediately before
   the Enter (step 3). A target that isn't idle gets `refused` with `code: "notIdle"`, and nothing
   is typed.
   * `busy` isn't used for this, because it means "an answer or another reply is being typed"
     (v1-sessions.md:454).
   * `code` is an **additive field** on reply results, a protocol change to v1-sessions.md's
     outcome table. A client that doesn't know a code shows `refused` with its `reason`.
   * Nothing is queued in v1 (Q35). The agent retries after the turn ends, within its rate limits.
   * **Claude:** its permission prompts and `AskUserQuestion` aren't in the transcript until
     they're answered (v1-sessions.md:1026). So for Claude the transcript can't show a prompt is
     pending, and **its safety rests on the screen guard** below. The same holds for any agent
     whose pending prompts the host can only see on screen.
2. **An empty, normal-mode composer.** Before typing, one screen read must positively show the
   input box **empty** and in **normal prompt mode**. Otherwise the reply is refused, and nothing
   is typed. Refused cases include:
   * a box holding any draft: the person's half-typed words, or a suggestion;
   * Copilot's shell mode (`!`), any other mode marker, or a mode indicator the host can't read.
     (A reply that *starts* with `/` or `!` is already refused, v1-sessions.md:440. This covers a
     composer already in that mode.)

   This stops an agent's words being **appended** to a draft such as `! echo `, where `; <cmd>`
   and Enter would run a command.
3. **A screen guard on the words and on the Enter.**
   * Each `KeySend`, the burst *and* the separate Enter, carries `Expect`: the input-box rows as
     read on screen immediately before (`KeySend.Expect`, `src/AgentBridge.Host.Delivery/KeyPlan.cs:20`,
     as Claude's and Codex's first send already do, host-replies.md:108-110).
   * **Before the Enter**, the host reads the screen again. It requires the **complete composer
     to equal exactly the authorized message**: prefix and words, nothing before or after, normal
     mode, the session still idle. Only then does the Enter go, held to those rows.
   * If anything differs, the Enter isn't pressed, the host clears what it typed if it can, and
     the outcome is `unconfirmed` (never retried).
   * This is required for **every agent kind, including Copilot**, which has no screen guard for
     replies today. An **input-box reader for Copilot** is an AP1 deliverable, since host-replies.md
     describes none (host-replies.md:19-23). An agent whose input box can't be read positively
     gets `refused`.
4. **Escaping.**
   * A leading option key (a digit, `y`, `n`, or any character the agent's prompts bind to an
     option) is typed after a leading space. With the provenance prefix on, the message starts
     with `[` anyway.
   * A mention can't be quoted (host-replies.md:73). So a **token-initial `@`** (at the start, or
     after whitespace) in an agent's words is typed as **U+FF20 FULLWIDTH COMMERCIAL AT** (`＠`),
     which reads as an at sign but isn't a mention.
   * AP1 vectors prove that per agent and version. Where that isn't proven, a reply with a
     token-initial `@` is refused.
   * That way the words can't select an option or attach a file the sender didn't send as an
     attachment (host-replies.md:69, :146).
5. **The same gap was found in answers.** PR #510 (#467)'s security reviews found the transcript
   check running before the `WorkerInjector.One` semaphore, the worker start and `EnterDelayMs`,
   with sends that don't set `KeySend.Expect`. Agent replies must not repeat it: the guard is
   checked *at* the write, not before the queue.

**Launch rules for agents.**
* `permissions` must be present. **A launch or resume without it is refused**, never filled from
  the machine's `defaults.permissions`, which may be `allowAll` (v1-launch.md:101).
  * `ask` is the only value for an **unattended** launch.
  * `allowAll` is accepted **only as a new gated launch** (§5.7 G): held until the person approves
    its exact manifest, prompt and fresh worktree with presence. That was the adversarial
    review's agreed item 8.
* **Every agent launch is a provably forced-ask launch** (§5.3 (A)): a host-owned configuration
  home, project sources off or absent, no managed policy, allowlisted flags and a scrubbed
  environment. Anything else is **held as a gated launch** for the owner's presence-signed
  approval of its full manifest and exact prompt (§5.7 G, from AP8; refused before then). An
  agent launch carries the agent's own prompt, which runs at once, so this check is its only
  guard.
* **Agency is never used** for an agent launch, and `agent: "auto"` resolving to Agency
  (host-launch.md:65) is refused: Agency adds flags and profile config the host can't show keep
  forced-ask (host-launch.md:29-30).
* `agent` and `profile` must be on `inbound.*.launch`'s allowlists. A profile, custom agent or
  `launch.agents.<id>.extraArgs` (host-config.md:95) that can't be shown to keep forced-ask is
  refused (Q36).
* A **resume** is a forced-ask launch too, and is refused if forced-ask can't be applied to it.
  **A resume is never a gated launch:** a gated launch always creates a new session in a fresh,
  host-made worktree. So a gated launch's "Include in @coordinator's window" checkbox can never
  add an existing session, such as a deployment session, to a window.
* A launched session **inherits the launcher's taint** (§5.8), whatever kind of launch it is.
* `transfer` is refused: on the pipe for agent connections, and absent from the action set.
* AP1 tests each of these (§11.2).

### 5.3 Sessions an agent can't reach, whatever the policy

These are hard-coded rules, not policy settings. No grant, link or profile overrides them, except
the owner's three opt-ins of §5.7, each of which keeps §5.2's delivery guard. **They restrict the
target only.** A sender's own mode never matters: an allow-all session's agent can message
forced-ask targets like any other (§5.7).

**"Allow-all" means any configuration in which typed words can make a side-effecting tool run
without a fresh, per-action prompt.** Side-effecting means running a command, writing or editing
a file, fetching from the network, or calling an MCP tool that isn't read-only. Reading files the
agent may already read by default is not counted; that exposure is stated in §7.8. The host
**cannot infer** "not allow-all" from what it sees: modes and grants come from too many places.
Three review rounds in a row found sources a list had missed. So the design no longer rests on
listing them (Q47):

* **(A) Isolation, not enumeration.** A session is **provably forced-ask** only if the host
  launched it with a **host-owned configuration home** holding nothing but what the host wrote,
  with project sources switched off or absent, and with no managed policy that could apply. The
  allowlist is then "what the host wrote", not "everything we remembered to check".
* **(B) The gate as the general fallback.** Every other target (allow-all, unknown, adopted,
  manually started, a session whose configuration changed, an agent whose grants the host can't
  see) receives an agent's message **only** if the target's owner approves that exact message
  with presence (§5.7 G), or enabled own-account allow-all targets (§5.7 O). Otherwise it is
  refused. Forced-ask becomes an optimisation (no prompt needed), not the only safety boundary.

**What isolation has to neutralise.** The table is the inventory that (A) is checked against, not
a list the host reads one by one:

| Source | Examples |
|---|---|
| Launch flags | Copilot `--allow-all`, `--allow-all-tools`, `--allow-all-paths`, `--allow-all-urls`, `--allow-tool`, `--yolo`; Claude `--dangerously-skip-permissions`, `--permission-mode bypassPermissions` or `acceptEdits`, `--allowedTools`; Codex `--ask-for-approval never`, `--dangerously-bypass-approvals-and-sandbox` |
| Machine config and wrappers | `launch.agents.<id>.extraArgs` (host-config.md:95). **Agency**, which `auto` prefers (host-launch.md:65), adds its own flags and profile config to Copilot's command line (host-launch.md:29-30) |
| The personal configuration home | **Claude** (`~/.claude`): `settings.json` (`defaultMode`, `permissions.allow`, which **merge** across scopes), agents, skills, commands, plugins, hooks, MCP servers. **Codex** (`~/.codex`): `config.toml` (`approval_policy`, inline `[hooks]`, MCP servers), `rules/*.rules`, `hooks.json`, plugins. **Copilot** (`~/.copilot`): `settings.json` (`allowedUrls`, saved mode), `permissions-config.json` and its legacy extensionless predecessor, hooks, skills, legacy commands, agents, plugins, MCP servers |
| Repository configuration | **Copilot:** `.github/copilot/settings.json` and `settings.local.json` (inline `hooks`, `allowedUrls`, `enabledPlugins`), `.github/skills`, `.github/agents`, `.github/hooks`, legacy commands. Saved permissions for a linked worktree are scoped to its **main checkout**. **Claude:** `.claude/` (settings and local settings, agents, skills, commands), `.mcp.json`. **Codex:** `.codex/config.toml`, `.codex/hooks.json` and rules, in a trusted project |
| Skills and commands with `allowed-tools` | **Invoking one pre-approves its tools.** Copilot CLI 1.0.105 handles `skill.invoked` by `addApprovedRules(allowedTools)` and re-adds them on resume, with no `permission.requested` recorded; Claude's skills and commands do the same. An agent's reply only has to ask for the skill |
| Subagents | Claude agent files (`.claude/agents/`, `~/.claude/agents/`) with `permissionMode: acceptEdits`, `auto` or `dontAsk`, and their own hooks. Copilot custom agents |
| Hooks | Claude: a `PreToolUse` hook can return `allow`, and any hook runs a command. **Codex: on by default**; `hooks.json`, inline `[hooks]`, the repository's and plugins' hooks merge, and a `PermissionRequest` hook can approve. Copilot: `sessionStart` and `userPromptSubmitted` hooks run commands on every message, and permission hooks can approve |
| MCP and app tools | Tools that skip approval by annotation. A server's own read-only declaration isn't proof |
| Managed and server policy | **Claude:** a managed-settings file, MDM (a macOS profile, Windows policy registry keys), the policy helper, and **server-managed settings** from the claude.ai console, which the host can't read from disk. **Copilot:** managed settings and managed `permissions.allow`, and an organization's `defaultPermissionMode: "assisted"`; Copilot has **no flag that forces manual mode**, so interactive mode isn't manual mode. **Codex:** managed configuration and requirements files |
| Environment | The agent inherits the host's environment (host-launch.md:68, :95). `COPILOT_HOME`, `CLAUDE_CONFIG_DIR` and `CODEX_HOME` move the configuration home |
| Modes | Copilot's assisted or autopilot modes. Claude's Shift+Tab cycle |
| In-session grants | Copilot's "Yes, and don't ask again" and "remember these paths" (host-answers.md:85-88). Claude's "don't ask again", for the session or persisted. Codex's "always allow", which writes a rule file. A resume may restore session-scoped approvals and skill rules |
| Runtime switches and reloads | `/allow-all`, `/yolo`, `/permissions`, Codex's `/approvals`. **Claude reloads** permissions and hooks when a settings file changes, and loads one **created** mid-session, a new `.claude/` included. **Copilot reloads** hooks, MCP servers and agents on a plugin change, and `/skills reload` loads new skills |

**(A) in detail: a provably forced-ask launch.**

**Unknown means unknown, per adapter and version.** Forced-ask is claimed for an agent version only
with all three of:
* a **source inventory** for that version;
* **positive evidence of the effective policy**;
* **adversarial launch, resume and runtime tests**.

File-access traces (Process Monitor and the like) are **regression tests, not proofs**: they show
what one run touched, not what another configuration would load. No signal of an organisation's
effective policy is established today, so **managed (corporate) Copilot, Claude and Codex use G or
O** (§5.7). For a given launch, all of these hold, or it isn't forced-ask:

1. **A host-owned configuration home** per agent kind: `COPILOT_HOME`, `CLAUDE_CONFIG_DIR` and
   `CODEX_HOME` point at `<host data>/agent-homes/<agent>/`. It holds only:
   * **host-written configuration**, deny by default: Claude `defaultMode: "default"`, no `allow`,
     `disableBypassPermissionsMode`, no hooks, plugins, agents, skills or MCP servers; Codex
     `approval_policy = "untrusted"`, `sandbox_mode = "workspace-write"`, `features.hooks = false`,
     no rules, MCP servers or trusted projects; Copilot no `allowedUrls`, plugins, hooks, skills,
     agents or saved approvals;
   * **the sign-in credential only**, never the rest of the personal home. **The OS credential
     store is preferred** (Copilot's keychain entry or documented token variable). A **copy**
     of Claude's `.credentials.json` or Codex's `auth.json` doesn't work: both rotate refresh
     tokens, so a copy diverges from the original and one of them stops signing in. Where a CLI
     can only keep a credential file in its configuration home, AP1 must show a working
     arrangement (the CLI reading the OS store, or a single shared credential file referenced
     rather than copied). Otherwise that adapter isn't forced-ask and uses G or O;
   * the CLI's own **state** (session history, logs), in the subdirectories it uses for them.

   **Before every launch** the host checks the home against its manifest: every host-written
   file by hash, every state directory by name, and nothing else. Anything else, such as a file
   a session or another process wrote, makes the host **reset** the home to the manifest
   (moving the stray entries to an audited quarantine). The built-in skills and agents a CLI
   ships with are inventoried by AP1 per version; one with `allowed-tools` refuses the launch.
2. **Project sources off.** Claude `--setting-sources` with none of project or local; Codex's
   project configuration doesn't load because the host-owned home trusts no project, and
   `-c features.hooks=false` is passed too. Where a CLI has no switch, the host **refuses the
   launch if the workspace, or the main checkout of a linked worktree, contains a repository
   extension path** for that agent: Copilot `.github/copilot/`, `.github/skills/`,
   `.github/agents/`, `.github/hooks/` and legacy commands; Claude `.claude/` and `.mcp.json`
   unless AP1 shows `--setting-sources` excludes each; Codex `.codex/` unless AP1 shows an
   untrusted project's are ignored.
   * **The known gaps** also refuse the launch until AP1 shows them neutralised:
     * Copilot also loads `.claude/skills`, `.agents/skills` and `~/.agents/skills`;
     * Codex also loads `.agents/skills` from the working directory up to the repository root,
       `$HOME/.agents/skills`, `/etc/codex`, a parent directory's `.codex/config.toml`, and
       cloud-managed requirements;
     * `CLAUDE_CONFIG_DIR` doesn't contain everything (anthropics/claude-code #81278, #79952,
       #97680).
   * Setting `HOME` and `USERPROFILE` to the host-owned home as well narrows the personal sources
     further. It can't suppress system or cloud policy, which is why step 3 exists.
   * **Imports.** Only instruction files (`copilot-instructions.md`, `AGENTS.md`, `CLAUDE.md`), and
     only for adapters whose source inventory passes. They carry text, not authority. No skills
     and no MCP servers are imported. An import that defeats the proof moves the session to G or O.
     An **O sender's** host-owned home has **no imports at all** (§5.7 O).
3. **No managed or server policy may apply.** The host refuses if a managed-settings file, an MDM
   profile or a policy registry key for that agent exists, or if the signed-in account is one
   whose organization can set permissions server-side (Claude Team or Enterprise, Copilot
   Business or Enterprise), unless AP1 finds a positive in-CLI signal of the effective policy.
   A policy that can change at runtime is treated as unreadable.
4. **Flags.** Copilot: an **allowlist** (session id, model, effort, context, the prompt), no
   `extraArgs`, never Agency (Q36). It has no manual-mode flag, so the effective mode is proved
   by step 3 and by the on-screen mode indicator at first idle, before any delivery. Claude:
   `--permission-mode default`. Codex: `-a untrusted -s workspace-write -c features.hooks=false`,
   no `--profile`.
5. **A scrubbed environment:** the host's environment minus each agent's configuration and
   permission variables, plus `AGENT_BRIDGE_*`, with the three home variables set to the host's.
6. **Fail closed.** A scope that is unreadable, unparseable or ambiguous refuses the launch:
   **unreadable is not absence**, as for the policy (§5.1) and the credential store (§3.4).

**Agent launches are exposed for all three agents.** A launch's prompt runs at once, so the launch
check is its only guard. An agent launch goes ahead unattended only as a provably forced-ask
launch. Anything else is a **gated launch** (§5.7 G), held for the owner's approval of its full
manifest and exact prompt; before AP8 it is refused.

**Staying forced-ask.** Before every delivery the host checks that nothing has changed since
launch:
* **Watches on directories, not files.** It watches the host-owned home, the workspace's and main
  checkout's extension paths (including their **creation**), and every managed path, plus the
  CLI's plugin, skill and agent sets where it records them. **Any new or changed entry** makes
  the session `unknown`. A list of the files read at launch would miss a file created later.
* **Grants.** Any permission prompt the host didn't answer itself once-only counts as a possible
  grant (remote approval never picks a persistent one, v1-sessions.md "Copilot permission
  prompts"). The on-screen mode indicator, read in the composer guard's screen read (§5.2
  step 2), still shows the forced mode.
* **What each agent records** (v1-sessions.md:1021-1027):
  * **Copilot** writes `permission.requested` and `permission.completed` to `events.jsonl`, a real
    signal, including grants made at the machine. A skill's pre-approval isn't recorded there,
    which is why skills are excluded by step 1 rather than detected.
  * **Claude** gives agent-bridge **no signal**: its prompts aren't in the transcript, and only
    agent-ha-bridge's hook ever saw them. A session-only "don't ask again" is invisible; a
    persisted one is caught by the watches. So a Claude session stays `unknown` unless AP1 finds
    a signal, and reaches agents through (B).
  * **Codex** records `exec_approval_request`, and its per-turn approval policy only **after** a
    turn is submitted, too late. A Codex session stays `unknown` unless AP1 finds an earlier
    signal, and reaches agents through (B).

**Residuals.**
* **The launch-time race.** Between the host's check and the CLI loading its configuration, a
  same-user process can change a file. That is same-user code (§1.3).
* **Mid-turn changes.** A configuration change during a turn an agent's message already started
  can't be withdrawn from the CLI, so new authority may apply to that turn. The host marks the
  session `unknown` at once, audits it, and puts a notice in the person's Needs-you lane ("the
  configuration changed during an agent-driven turn") with **Stop**. Only same-user code can make
  such a change: another process, or the target's own agent where its mode lets it write without
  a prompt (Codex in `workspace-write` can write the workspace, which is why an untrusted
  project's `.codex/` must be shown ignored, and why AP1 has a vector for it).
* **What isolation costs the person** (Q47). Forced-ask sessions don't have the person's own
  skills, MCP servers, plugins, custom agents, hooks or saved approvals, and a workspace carrying
  repository extension paths can't have one at all. Their history lives in the host-owned home,
  so they appear in the bridge but not in the CLI's own resume list. The person's ordinary
  sessions are untouched. Forced-ask sessions are a kind the person or an agent chooses for
  agent-to-agent work; everything else is reached through the gate.

**The rule.**
* **A session is an unattended reply target only if** it was a provably forced-ask launch under
  (A), and nothing above has made it `unknown` since.
* **Anything else is `unknown`**, including any session the host didn't launch, adopted and
  manually started ones, and every session of an agent or version whose AP1 vectors don't show
  the host can see its grants and mode changes before delivery. An unknown session gets an
  agent's message only through (B).
* **The consequence in v1** (Q45): Claude and probably Codex sessions are never unattended reply
  targets, and on an organization-managed account Copilot sessions may not be either. With the
  gate on, every one of them can still receive a message its owner approves.
* **The person's own grants make a session unreachable unattended.** "Don't ask again" at the
  machine, or `/allow-all`, makes it `unknown` until it is relaunched (Q44). That is deliberate,
  and the gate still applies.
* Allow-all and unknown sessions are **excluded from every `expose` set** (§7.4) and every
  connector scope (§9.3), unless a link's `allowAllRead` opt-in exposes them for reading only
  (§5.7 R). They may be **read** within the same-account read policy. A **reply** into one is
  refused unless the target's owner opted in: the gate (§5.7 G) or the own-account setting
  (§5.7 O). Q34 records the owner's decision.
* AP1 ships **isolation (A)**: the host-owned homes and their manifests, the project-source
  switches and refusals, the managed-policy refusal, the flags, the scrubbed environment, and
  the directory watches. It ships **mandatory refusal vectors** for every row of the table, for
  all three agents, including: a pre-seeded user allow rule, hook, rule file and skill with
  `allowed-tools`; each repository extension path; a managed file and a policy registry key;
  Copilot's `defaultPermissionMode: "assisted"`; and, mid-session, an added hook, skill, settings
  file or `.claude/` directory, a plugin change, and `/skills reload`, each making the session
  `unknown` (§11.2).
The other hard-coded exclusions:
* A session that is **working** or asking anything: replies are `refused` with
  `code: "notIdle"` (§5.2).
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

* A turn the **person** drove (typed, replied or answered a question from a device, or launched by
  a person; approving a tool or permission prompt doesn't count, §5.4 "A person resets it"):
  the session's next outbound message starts a **new conversation**: `root` = its own `request`,
  no `prev`, `messages` 1, `via` = [the sender], and `depth` 2 (the sender and its target).
* A turn an **agent message** drove: outbound messages **continue that message's conversation**:
  `root` and `rootAt` unchanged, `prev` = the hash of that incoming envelope, `messages` = its
  `messages` + 1, and `via` = its `via` with the sender appended **only if not already there**.
* A session **launched by an agent** starts in the launcher's conversation. The launch envelope
  is a hop like any other, and the launched session's first message names it as `prev`. Since a
  `session.launch` has no `target.session`, that link is bound by the target host's signed
  `launch.result` record (`{ request, session, instance }`, §4.6), which must name the session
  that sent the next message.

**Depth counts distinct sessions, the destination included.** `depth` = the number of distinct
sessions in `via` ∪ {the target}. A message to a session already in `via` doesn't increase it;
only reaching a new session does. With `maxDepth` 3:

| Message | `via` after it | Sessions reached | `depth` | Result |
|---|---|---|---|---|
| A → B | [A] | A, B | 2 | Allowed |
| B → C | [A, B] | A, B, C | 3 | Allowed |
| C → D | [A, B, C] | A, B, C, D | 4 | **Refused** (`chain-too-deep`) |
| C → A | [A, B, C] | A, B, C | 3 | Allowed: A is already in the conversation |
| A ⇄ B, 19 more times | [A, B] | A, B | 2 | Allowed until `messages` reaches 20 (`conversation-budget`) |

So `via` is bounded by `maxDepth` and never grows in an A ⇄ B conversation, while every message
still counts against the separate `messages` budget.

**Inside a coordination group** (§5.7 C), while `via` and the target all lie within one verified
group, `depth` is bounded by the group's member count instead of `maxDepth`, so a group of four
can chain A → B → C → D. The first hop to a non-member is checked against `maxDepth` as usual,
and is refused (`chain-too-deep`) when the conversation has already reached more sessions.

**Rules**, checked by the issuer before signing and again by the target (step 10):

| Rule | Default | Effect |
|---|---|---|
| `depth ≤ maxDepth` | 3 | Distinct sessions, the destination included (above). A → B → C is allowed. C reaching a fourth session D is refused (`chain-too-deep`), unless all four are members of one verified coordination group (above). Replying back to a session already in `via` doesn't add depth, so A ⇄ B conversations are unaffected |
| `messages ≤ conversationMessages` | 20 | At most 20 agent messages, repeats included, along any one `prev` path and in each receiving host's own count for that `root`. The issuer checks its own count before signing. The target enforces the largest of the claimed `messages`, the length of the verified `prev` chain, and its own count of messages it has received for that `root`. **Residual:** a conversation that fans out across several hosts can reach about 20 messages per host in total, bounded by the per-pair and per-origin rates (§5.5). Past that: `conversation-budget`, and the message tells the agent that a person must reply to continue |
| `now − rootAt ≤ conversationMinutes` | 60 | An agent conversation older than an hour ends the same way |
| A launch's `depth ≤ maxDepth` | 3 | A launch is a hop to a new session, counted like any other destination. A person-driven session (A launching L: 2 sessions), or one an agent messaged directly (A → B, B launching L: 3), may launch; deeper ones may not, so launches can't fan out into a tree. A launched session reporting back to its launcher adds nothing, since the launcher is already in `via` |
| `allowAgentLaunchedToLaunch` | false | A session an agent launched can't launch further sessions at all, by default |

**A person resets it.** Any person input to a session (a reply, or an answer to a question the
agent asked, from any of the person's devices, or typing at the machine) takes the session back (v1-sessions.md:863-867) and ends its
part in any conversation. It resets the **chain budget only, never taint** (§5.8). **Approving a
tool or permission prompt is not such input**: it doesn't reset the chain, just as it doesn't
reset the workflow budget (below). Its next outbound message is a fresh root. This is the same "until the
person takes it back" rule as the purple edge, so the budget is easy to explain: "agents can go
back and forth 20 times on their own; then one of them needs you".

**Who is trusted for the chain.** `via` is **rebuilt by each host**, never forwarded as given:
the issuing host takes the verified envelope that drove its sender's turn, appends the sender if
it is new, and signs the result inside its own envelope, whose `principal` binds that hop's
sender. Deduplication shortens the list, not the evidence: every hop's sender is still bound by
the signature on that hop's envelope, and chain identity (`root`, `rootAt`), provenance and taint
travel and are checked as before (§4.3, §5.8). **The target recomputes rather than trusting the
list.** Under 4.2(a) it walks `prev` back to the root, to an anchored envelope (§7.5), or to an
envelope it has already verified for that `root`, fetching each with `agent.chainLink` from the
host that issued the citing envelope (§4.6), within that op's bounds. Each link must verify, have
the same `root`, and be addressed to the session that is the next link's sender: by
`target.session`, or for a `session.launch` by its signed `launch.result`. From that chain it
computes the distinct sessions and a floor for `messages`, and refuses (`chain-unverifiable`)
if a link is missing or doesn't verify, or if the claimed `via` differs from the recomputed one.
So a host can't drop an earlier entry to reset depth: the dropped session is still in the signed
chain. Under (b), the pairwise MAC can't be checked by a third host, so a compromised host can
lie about the chain. Either way, a compromised host, which could also start a fresh `root`, is
bounded by the target's per-origin rates (§5.5). **Across accounts** nothing is walked:
outbound envelopes carry no `prev` and a `via` of the sender alone, and the receiver re-roots at
the link with a signed `anchor` (§7.5).

**Workflow budget.** A coordinator running a long piece of work needs more than 20 messages. The
person grants it a **workflow budget** with presence (for example 150 agent messages over 8
hours), shown on its card with a notice at 80% and a one-tap, presence-confirmed top-up.
* The per-conversation and per-pair caps still apply alongside it; the budget is an aggregate
  ceiling, not an exemption.
* Schedules and tool approvals don't reset it.
* Running out gives `budgetExhausted`, with words the agent can relay to the person ("I've used
  this workflow's 150 messages; top it up on DESK to continue").

**Drivers.** A **payload-free timer** (a wake-up with no text) keeps the session's taint state
unchanged. An **immutable, owner-approved schedule** may keep own-account eligibility for O.
Text from Home Assistant or a connector adds **untrusted-automation influence** (§5.8).

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
* **Grants and provenance.** Every entry also records its **origin** (the retained external
  origins and categories, §5.8), its **authorizer** (the person command that approved a hold,
  window, batch or gated launch, or the setting that allowed it), the **grant** used (direct,
  G, a window, O, a coordination group as `group:<id>@<gen>`, a link's status or content grant),
  the **outcome**, and, for a read, the
  **projection served**. Never the content. A read served to a person device is marked
  **served**, not "read by a human": the host can't know whether anyone looked.
* **Credential events.** Issue, adoption, rejection (wrong session, wrong process), revocation and
  its cause.
* **Shown.** The app gets an "Agent activity" view per machine (later slice) that filters the log:
  "copilot on *DESK* (their session "Fix the flaky test") replied to *Release notes* here,
  14:05 PT". The sender's title is quoted and secondary, as in §6.2.
* **Verifiable.** Under 4.2(a), the person's app can re-verify any inbound entry against the
  roster.

### 5.7 Reaching allow-all sessions: the gate and the owner's opt-ins

**Owner decision** (coordinator, 2026-11-03, 19:59 PT), shaped by the adversarial PM/Security
review (revision 11).
* The owner mostly runs sessions in allow-all, and wants cross-session messaging to work with
  them. §5.3's restriction is on the **target** only.
* **A sender's own mode never matters:** an allow-all session holding a credential (§3.2) can
  read, launch, and reply into forced-ask targets like any other.
* What §5.3 forbids is an agent's words arriving, **unattended**, in a session that will run them
  without a prompt. Since forced-ask is rarely provable (§5), the paths below are how agent
  messaging normally works.
* Each keeps §5.2's delivery guard (idle only, an empty normal-mode composer, `Expect` on the
  words and the Enter, escaping), which there protects the **timing**, not the mode.

**(G) The per-message gate, for every target that isn't provably forced-ask** (§5.3 (B)):
allow-all, unknown, adopted, manually started, a session whose configuration changed, and
Claude, Codex and organisation-managed sessions. Same account and cross-account.
* **Settings.** `inbound.*.gate` (§5.1) is `off`, `quiet` or `push`. The own-account default is
  **`quiet`**: a hold runs nothing without the owner's presence, so holding is safe to leave on.
  Cross-account G stays **opt-in per link** (`expose.gate`, §7.4).
* **Holding.** A reply that passes every check except §5.3's target rule isn't refused: the target
  host **holds** it.
  * The sender gets the additive outcome `held`, with a `hold` id. A resend with the same
    `request` returns the current outcome to an own-account sender. An **external** sender gets
    `held` on every resend until the common deadline, whatever has happened to the hold, so a
    resend can't reveal the outcome early (below).
  * The replay record (§4.4) is written at hold time, so the envelope's 120 s expiry is checked at
    acceptance and the held copy can't be replayed.
  * The chain budget, workflow budget and rates (§5.4, §5.5) are charged at hold time, so a denied
    message still costs the sender.
* **A bounded queue** of held messages:
  * Each hold has an **absolute expiry**: 60 minutes for a quiet hold, 5 to 15 minutes for a
    pushed one. Approving it doesn't restart the clock.
  * A hold is for its exact text. New text means a new hold, and the person's own input to the
    target cancels its pending holds.
  * An approved hold waits for the target's next idle, up to that expiry. At delivery the host
    **rechecks everything**: policy, link revocation, taint, the target instance and §5.2's
    guard. It types once, and never retries a delivery that came back `unconfirmed`.
* **Notifications. No push per hold.** This is normative. Quiet holds are counted on Needs-you
  and never pushed. A person can opt in to **at most one aggregated push per hour**, carrying a
  generic count and **no text** ("3 agent messages are waiting"). Quiet hours are per device. A
  hold that expires overnight is dropped, so there's no morning backlog: the morning shows a
  count, not a queue.
* **The prompt**, in the Needs-you lane as item kind `agentMessage`:
  * Heading: "**@alex's agent wants to run something on DESK**". It shows the immediate sender's
    **verified label** (§6.3) **and the retained external origins** (§5.8).
  * **A deterministic, explicitly partial summary**, such as "contains these commands, among
    other text", listing what looks like a command, URL or file path. It never offers a model's
    reassurance.
  * The **full text** is one tap away, exactly as it will be typed, prefix and escaping included,
    never truncated (up to 16 KiB), with hidden characters revealed (`NeedsYou.Reveal`).
  * Buttons: **Deny** and **Authorize to run**, Deny first, with nothing pre-selected, plus the
    window choice below. It says plainly: "Authorizing makes this your own instruction to a
    session that can run commands on *DESK* without asking."
  * **Reducing authority needs no biometrics:** mute (for an hour, or until morning) and pause.
    "**Block replies from @alex**" turns off `expose.reply` for that link through an
    authenticated, replay-protected person-device command; **widening** anything needs presence.
    * Mute, pause and block are **person-device commands only**. An agent, a connector or Home
      Assistant can't issue them, since they could hide holds the person ought to see.
    * They **never suppress** the mid-turn configuration-change notice (§5.3 residuals) or any
      safety notice; they quiet holds only.
  * It never suggests a broader grant as an escalation.
* **The approval is a person command**, signed with **presence** on a person's device (#391 §7.2),
  over `F("agent-bridge gate v1", {hold, target: {account, machine, session, instance}, sender:
  {account, generation, add, session, instance}, origins, textSha256, decision, window?, request,
  expiresAt})`.
  * The host checks: a person-kind signer; presence; the hold still pending; the exact
    `textSha256`; the sender principal; the retained origins; the target instance; and the
    request in the durable replay store (single use).
  * So neither a relay nor a host can swap anything. Agent connections, connectors, the
    `homeassistant:` press path and any path without presence can't produce one.
  * **Approval keeps provenance:** the delivered text still names its external origins, and the
    target inherits the sender's taint (§5.8).
* **Batch approval.** One presence signature can cover an **immutable, bounded batch** (at most 20
  holds), and binds as much per item as a single approval does: each item's text hash, target
  instance and **origins**.
  * A batch has **one sender**, and covers held messages only: **never gated launches**.
  * It never mixes tainted and untainted items.
  * Every text is shown, and each item is delivered idempotently on its own.
* **Windows** ("Authorize @sender to run things in these sessions for N minutes", N one of 5, 15,
  30 or 60):
  * A window covers a **signed, fixed set of target instances**, which the person can pick many
    of at once from a workspace. The set **never grows on its own**.
  * A **tainted** sender's window is a distinct **G delegation grant** for a fixed set of origins.
    A new origin appearing in the sender's taint pauses it. It is never O.
  * **The expansion fold:**
    * A gated launch carries an "**Include in @coordinator's window**" checkbox, unticked by
      default, bound to that launch's resulting instance.
    * The first hold to a target outside the window offers "**Authorize and add this session to
      the window**".
    * Expansion is bound to the window, sender, instance and origin restrictions. It never
      includes descendants automatically, never extends the expiry, and never resets a budget.
  * Only the target owner's presence-signed approval opens or expands a window. **A sender can't
    open, extend or see one.**
  * A window is revocable at once, and ends when a covered session ends or gets a new instance,
    when the target's mode or grants change, or when the link is suspended or tombstoned.
  * Window deliveries skip the prompt but nothing else. They are rate-limited
    (`limits.gateWindowPerMinute`, default 2), and each is audited.
* **A gated launch, in v1.** A **new** launch that isn't provably forced-ask, including one that
  requests `allowAll`, is **held for presence approval** instead of refused (§5.2). A resume is
  never a gated launch. The launched session inherits the requester's taint (§5.8).
  * The approval binds the **full manifest** (agent and version, flags, profile, permissions,
    model, effort, environment changes), the **exact prompt**, the destination (a **fresh worktree
    the host makes**), an expiry and the requester's taint.
  * **It binds the base.** Today the worktree is made at launch from a freshly fetched
    `origin/<default>` (host-launch.md), so repository hooks, `.mcp.json`, `.claude/settings.json`,
    `.github/hooks` and `AGENTS.md` could change between approval and launch. A collaborator with
    write access, or a tainted agent of the owner's that pushed, could then make an approved
    `allowAll` launch run code nobody saw. So:
    * when the launch is **held**, the host resolves the workspace and the **base commit SHA**;
    * both go into the presence-signed approval;
    * the source report is computed over **that exact tree**;
    * the worktree is created at exactly that SHA, with **no re-fetch**;
    * if that commit is no longer available locally, the launch is refused.
  * **It binds the untracked local state too.** Some of what runs isn't in any commit:
    * the git common directory's `hooks/`, its `config` (allowlisted keys only, hashed; anything
      else refuses, below) and `info/attributes`, which matter during the host's own
      `git worktree add` (host-launch.md:132-133) and later;
    * the main checkout's untracked `.github/copilot/settings.local.json` and
      `.claude/settings.local.json`, since a linked worktree's permissions are scoped to the main
      checkout (§5.3).

    The host hashes all of these into the approval and the source report, and rechecks them at
    launch, refusing on any mismatch.
  * **Git runs under a closed, host-generated configuration.** Three review rounds tried to
    enumerate every way Git can run a program: configuration keys, includes, helper files,
    `insteadOf` rewrites to `ext::`, LFS transfer agents, environment variables such as
    `GIT_SSH_COMMAND` and `GIT_ASKPASS`, and Git's own built-in defaults. Each round found more.
    A list of what to refuse can't be shown complete, so the safe set is **positive** instead:
    Git sees only configuration the host wrote or explicitly allowed, and anything else refuses
    the launch. It applies in two places:
    * **Every Git command the host runs for a launch**, not only `worktree add`. host-launch.md's
      isolation path runs `git rev-parse` (host-launch.md:122), reclaims finished worktrees with
      status, untracked and ignored checks, `read-tree` and `rm` (:142-214), runs
      `git fetch origin` (:128), resolves the base, then `worktree add`, `worktree lock` and the
      marker write (:133-136). Each of those can run a planted `core.fsmonitor`, clean filter,
      `remote.<name>.uploadpack`, `core.sshCommand`, credential helper or ssh `ProxyCommand`. So
      all of them run under the closed system and global configuration, the rebuilt environment,
      and the hooks and fsmonitor pins (below), with `GIT_LFS_SKIP_SMUDGE=1` for `worktree add`.
    * **For an agent-requested launch**, gated or forced-ask, the order is fixed:
      1. **First, before any other Git command,** the host locates the repository itself, by
         reading the workspace's `.git` file or directory, its `gitdir` and its `commondir`, and
         **parses** the common directory's `config` and the workspace's own `config.worktree`
         directly (reclaimed worktrees' files are handled below). Any key outside the allowlist
         (below) refuses the launch there, so a tainted agent that plants `core.fsmonitor`,
         `remote.origin.uploadpack` or a `filter.<x>.smudge` and then asks for a launch gets a
         refusal with nothing run, even one the person would have denied.
         * **The parser accepts a strict subset of Git's syntax**, because the danger is the host
           seeing less than Git does. A file must be UTF-8 with no BOM, CR or NUL byte. A section
           header stands alone on its line, as `[section]` or `[section "subsection"]`; the legacy
           `[section.subsection]` form refuses. A subsection may escape only `\"` and `\\`; any
           other `\` in it refuses. Every other line is blank, a whole-line `#` or `;` comment that
           doesn't end in `\`, or `name = value` with an unquoted value containing no `"`, `\`,
           `#` or `;`. Leading spaces or tabs, and spaces or tabs around `=`, are allowed, since
           Git writes keys as `<TAB>name = value`. Anything else refuses: a key on a header's line,
           a continuation, an inline comment, a quoted value.
         * **Git's own parser cross-checks it.** For each file, the host runs
           `git config --file <f> --list --null`, without `--includes`, which executes nothing,
           under the closed environment with `GIT_DIR` pointed at an empty host-owned directory, so
           the repository's own configuration isn't loaded before step 1 has accepted it. The host
           compares (key, value) pairs split on the NUL and newline separators, never on `=`,
           since a subsection can contain one. Any difference refuses the launch.
         * A file the parser can't read completely (an unreadable one, an unknown syntax) also
           refuses.
      2. The self-check (below), then repository resolution and reclaiming, all under the closed
         configuration.
      3. **The fetch**, the one step that needs a helper, runs under the **session-side** closed
         configuration: the protected credential helper and the generated ssh config, never the
         user's own. It is `git fetch --no-recurse-submodules`: Git otherwise recurses on demand,
         and a `.gitmodules` `fetchRecurseSubmodules=true` forces it, into a submodule repository
         whose own configuration the parser never read (an `ext::` remote URL with
         `protocol.ext.allow=always`, say). Where the fetch fails (a remote the user reaches only
         through a jump host, say),
         the failure is logged and the local base is used, as host-launch.md:128 already does.
         That is no weaker than a fetch: `remote.*.fetch` and `origin/HEAD` are writable by an
         agent anyway. The protection is the SHA and the source report the person approves.
      4. **For a gated launch, the base is resolved at hold time** by
         `git rev-parse --verify <base>^{commit}` under the same closed configuration, right after
         that fetch, and the SHA goes into the approval. Launch time runs no second fetch. The
         approval screen says where the base came from: "fetched from origin at 22:41", or "the
         local `origin/main`; the fetch failed: <reason>".
      5. `worktree add`, `worktree lock` and the marker write.
    * **For a person's own launch**, the common directory's `config` is the person's, as today,
      and so is the fetch: it uses the user's own Git, ssh and helpers. Every other host Git
      command still runs under the closed system and global configuration and the rebuilt
      environment. A linked worktree's `config.worktree` is **not** the person's: an agent may
      have written it, and Git in the main checkout never reads it. So reclaiming follows the
      rule below for every launch.
    * **Reclaiming, for every launch, a person's own included.** Reclaiming runs status,
      untracked and ignored checks, `read-tree`, `rm` and `checkout-index` **inside** each
      finished worktree (host-launch.md:150-172, :194-202), and Git finds that worktree's
      administration directory from its in-tree `.git` file. An agent can rewrite that file to
      point at a directory of its own, with a `commondir` back to the real repository and a
      `config.worktree` holding `filter.x.clean=<payload>`, and the common configuration, the
      workspace's files and the self-check never see it. So:
      * the host runs Git in a reclaimed worktree **only** if its `.git` file names
        `<common>/worktrees/<id>` (compared by file identity, §5.8) **and** that directory's
        `gitdir` names the worktree's `.git` back. Otherwise it keeps the worktree and runs
        nothing there;
      * that worktree's `config.worktree` passes the same parser, cross-check and allowlist as
        step 1, or the worktree is kept and nothing is run there;
      * **the tracked-entry check comes first.** Before any `status`, `rm` or `checkout-index`,
        the host reads `git ls-files --stage -z` and keeps the worktree, running nothing more,
        unless every entry is a regular file (mode `100644` or `100755`) at stage 0. A gitlink
        would make `status` and `rm` recurse into a submodule in a child process that follows
        `sub/.git`, not `GIT_DIR`, and a `filter.x.clean` in a redirected submodule
        administration directory would run. agent-ha-bridge's reclaimer uses the same order
        (agent-ha-bridge `hooks/session-launch.ps1:883-888`, before `status` at :974). The host
        also passes `--ignore-submodules=all` and pins `submodule.recurse=false`;
      * every Git command in it runs with `GIT_DIR=<common>/worktrees/<id>`,
        `GIT_COMMON_DIR=<common>` and `GIT_WORK_TREE=<the worktree>` set by the host, so Git never
        follows the in-tree `.git` pointer. The host sets the same three variables, to what the
        parser resolved, for every Git command it runs anywhere in a launch. With gitlinks
        refused and submodule recursion off, Git then reads exactly the files that were parsed.
    * This **supersedes host-launch.md**'s "Isolation" (:118-136) and "Reclaiming finished
      worktrees" (:142-214) for agent-requested launches. That document is updated when AP8
      ships, as local-pipe.md is (§3.4).
    * **The launched session, for every gated launch.** A gated launch is by definition one that
      isn't provably forced-ask, which §5.3 treats as allow-all, so the session-side pins, helper
      entries and rebuilt environment apply to all of them, not only to those that request
      `allowAll`. A provably forced-ask session approves each command itself and keeps the
      user's own Git.
    * **System and global configuration aren't read.** The host sets `GIT_CONFIG_NOSYSTEM=1` and
      `GIT_CONFIG_GLOBAL=<a host-generated file>`. The user's `~/.gitconfig`, its includes,
      aliases, helpers and rewrites never load. The host copies only `user.name` and
      `user.email` from it, as values.
    * **The host's artifacts are generated per launch**, in a fresh directory in the host's
      private store: the global configuration file, the empty hooks directory and the ssh config.
      For a gated launch they're hashed into the approval. Immediately before the first host Git
      command, again before `worktree add`, and again before a gated session starts, the host
      checks that the hooks directory is still empty and the files still match, and refuses
      otherwise.
    * **The host-generated file** holds everything that names a program, and nothing else does:
      * `core.hooksPath` set to an empty host directory, and `core.fsmonitor=false`;
      * `core.editor` and `sequence.editor` set to `:`, which Git treats as "no editor", and
        `core.pager=cat`, which Git treats as "no pager". So Git's built-in defaults (`vi`,
        `less`) never run;
      * `protocol.allow=never`, with only `https` and `ssh` allowed, so `ext::`, `fd::`, `file`
        and any `<transport>::` helper are refused by Git itself. Because Git consults
        `protocol.<name>.allow` before the generic `protocol.allow`, the pins also set
        `protocol.ext.allow`, `protocol.file.allow`, `protocol.git.allow` and
        `protocol.http.allow` to `never` at command scope, so a per-protocol allow in a
        repository's configuration can't override them;
      * `submodule.recurse=false` and `fetch.recurseSubmodules=false`, pinned at command scope for
        **every** host Git command in a launch, not only reclaiming. **The host never runs Git
        inside a submodule for an agent-requested launch**: the fetch passes
        `--no-recurse-submodules`, `worktree add` checks no submodule out, and reclaiming refuses
        gitlinks before `status` (below);
      * `credential.helper` reset, then set to a protected helper by absolute path (Git
        Credential Manager under Program Files, say) if one exists, else none;
      * `core.sshCommand` set to a protected `ssh` by absolute path with
        `-F <a host-generated ssh config>`. With `-F`, OpenSSH reads neither the user's
        `~/.ssh/config` nor the system `ssh_config`;
      * `commit.gpgSign=false` and `tag.gpgSign=false`, so `gpg` never runs;
      * the standard `filter.lfs.*` entries with `git-lfs` by absolute protected path, and
        nothing else under `filter`, `lfs`, `url` or `protocol`.
    * **The generated ssh config** is translated by **parsing** the user's files, following
      `Include`, never by running `ssh -G`, which evaluates `Match exec`. It goes through an
      allowlist of non-program directives: `Host`, `Match`, `HostName`, `User`, `Port`,
      `IdentityFile` and `UserKnownHostsFile` outside every unit, `IdentitiesOnly`,
      `IdentityAgent`, `StrictHostKeyChecking`. Every other directive is dropped, never
      translated: `ProxyCommand`, `ProxyJump`, `Match exec`, `LocalCommand`, `KnownHostsCommand`,
      `PKCS11Provider`, `SecurityKeyProvider` and the rest. A `Match` line is copied only if
      **every** criterion is `host` or `originalhost`, or a negation of one. Any other criterion,
      even in a compound line such as `Match host *.corp exec "…"`, drops the **whole block**,
      directives included. The generated file is frozen at hold time and shown in the source
      report, so later edits to the user's files change nothing.
    * **Repository configuration is allowlisted, not screened.** The common directory's `config`
      and any `config.worktree` may hold only:
      * `core.repositoryformatversion`, `core.bare`, `core.filemode`, `core.symlinks`,
        `core.ignorecase`, `core.logallrefupdates`, `core.autocrlf`, `core.eol`, `core.longpaths`
        and `core.precomposeunicode`, as booleans or their documented values;
      * `extensions.worktreeConfig` and `extensions.objectFormat`;
      * `remote.<name>.url`, `remote.<name>.pushurl`, `remote.<name>.fetch`,
        `remote.<name>.prune`, with URLs in `https://`, `ssh://` or scp form only;
      * `branch.<name>.remote`, which must name a configured remote (never a URL),
        `branch.<name>.merge` and `branch.<name>.rebase`;
      * `submodule.<name>.url`, under the same URL rule, and `submodule.<name>.active`;
      * `user.name` and `user.email`, as values;
      * data-only keys that common tools write: `remote.<name>.gh-resolved` (GitHub CLI) and
        `branch.<name>.vscode-merge-base` (VS Code);
      * `lfs.repositoryformatversion`.

      The allowlist is versioned. A key is added only after review shows Git never treats its
      value as a program, a path to load, or a URL rewrite.

      **The presence of any other key refuses the launch**, includes and `includeIf` among
      them. The allowed keys are hashed into the approval and rechecked at launch, along with
      `info/attributes` and a tracked `.lfsconfig`, which may hold only `lfs.url` and
      `lfs.fetchinclude`/`lfs.fetchexclude`. `GIT_CONFIG_COUNT` pins the host file's entries
      above repository scope as well.
    * **The environment is rebuilt, not filtered.** Git's environment for `worktree add` and for
      the session starts from a host allowlist (`PATH`, `HOME`, `SystemRoot`, `TEMP`, locale and
      terminal basics, and `SSH_AUTH_SOCK`, which names a socket and runs nothing). **Every**
      `GIT_*`, `SSH_*` (but `SSH_AUTH_SOCK`), `GNUPG*`/`GPG_*`, `GIT_LFS_*`/`LFS_*`, `EDITOR`,
      `VISUAL`, `PAGER` and `LESS*` variable is dropped. The host then sets its own:
      `GIT_CONFIG_NOSYSTEM`, `GIT_CONFIG_GLOBAL`, `GIT_CONFIG_COUNT` and its pairs,
      `GIT_EXEC_PATH` to the protected Git installation's exec path, `GIT_TERMINAL_PROMPT=0`,
      `SSH_ASKPASS_REQUIRE=never`, and `GIT_LFS_SKIP_SMUDGE=1` for `worktree add`. `PATH` is
      rebuilt with the protected Git and OpenSSH directories first, and refused if it holds an
      empty, `.` or relative element or a unit directory. Any variable the host sets that names a
      program names one of the protected executables above. None of them contains shell
      metacharacters, so Git's `sh -c` path never sees a value the host didn't write.
    * **"Protected"** means the user's effective access, not ownership: on Windows, the
      effective DACL rights of the user's token on the file and every directory above it; on
      macOS and Linux, root ownership and no group or other write on the path.
    * **The Git version floor** is **2.32**, when `GIT_CONFIG_GLOBAL` and `GIT_CONFIG_SYSTEM`
      arrived: an older Git silently ignores them and loads `~/.gitconfig` after all. The floor is
      also "a currently maintained, security-patched release": the host compares the exact
      executable's `git --version` with a minimum shipped in its own signed release data, set to
      the oldest Git release line still receiving security fixes, and refuses below it.
    * **It verifies itself.** Version checks aren't enough, so the host runs
      `git config --list --show-scope --show-origin` with the **exact Git executable and the
      rebuilt environment** that will be used:
      * **before its first other Git command for a launch**, in the main checkout (the common
        directory), since the new worktree doesn't exist yet. It refuses unless every entry comes
        from the host's file, the command-scope pins or a repository key. For an agent-requested
        launch the repository keys must be allowlisted; for a person's own launch any repository
        key is accepted, since that configuration is the person's;
      * **again before a gated session starts**, in the new worktree, with the same rule as an
        agent-requested launch. A provably forced-ask session isn't checked, because it keeps
        the user's own Git.
    * **LFS content** isn't fetched during `worktree add` (`GIT_LFS_SKIP_SMUDGE=1`). A later
      `git lfs pull` uses the same closed configuration, so a custom transfer agent,
      `lfs.standalonetransferagent` or `lfs.extension.*` can't load: none of them is in an
      allowed scope.
    * **The cost.** For an agent-requested launch, the host's fetch can't use the user's SSH
      proxies, jump hosts or extra credential helpers, so it may fall back to the local base. In a
      gated launch's session, Git has none of the user's aliases, `insteadOf`
      rewrites, extra credential helpers, signing, SSH proxies or jump hosts, or custom diff and
      merge tools. A repository whose own configuration holds anything outside the allowlist (a
      custom merge driver, a `[include]`) can't have **any agent-requested launch**, gated or
      forced-ask: the person launches it themselves. The source report lists what was dropped.
      **Accepted by the owner (2026-11-03, 23:19 PT)** as the trade-off for the closed
      configuration: agent-requested and gated launches run Git without the user's aliases, URL
      rewrites, extra credential helpers, signing or SSH proxies, and a repository with
      non-allowlisted configuration is refused; the person launches it themselves, in `ask` mode if
      they prefer.
    * **The residual window.** The host checks its artifacts and the configuration immediately
      before use, but same-user code can still change them in the instant between the check and
      Git reading them (§1.3).
  * **Residuals**, shown in the source report's "can't know" part:
    * an allow-all session can unset those variables, point `GIT_CONFIG_GLOBAL` elsewhere or run
      git with `-c` itself. The closed configuration stops a planted program from running
      unseen, not a session determined to run one;
    * the shared repository configuration can be edited **after** launch. The host binds what
      launches, not what the session's later Git calls read, though the pinned entries outrank
      repository scope;
    * a process that rebuilds its own environment, or a Git client outside the session, doesn't
      get the closed configuration.
  * **It binds the unit's taint.** If the unit's contamination generation changed between
    approval and launch (§5.8), the launch is refused.
  * Its **source report** separates what the host **verified**, **observed** and **can't know**.
    It never claims to list everything that will load.
  * **Gated launches are never batched.** Each is approved on its own screen, so its manifest,
    base and source report are always in front of the person.
  * A **cross-account** gated launch can never request `allowAll`: a link's launch grant is
    `ask` only (§7.4).
  * The screen says: "**Allow-all lets this run code with your account's privileges. A fresh
    worktree is organisation, not containment.**"
  * A launch an external party requested keeps external provenance (§5.8).
* **What the sender learns.**
  * An own-account sender gets precise codes: `gateDenied`, `gateExpired`, `gateCancelled`.
  * An **external** sender gets one code, `notDelivered`, for deny, expiry, mute and block alike,
    reported at a **common deadline** (the hold's absolute expiry), so timing doesn't reveal
    which. The real reason stays in the owner's audit, and a blocked body isn't kept longer than
    needed. This makes the cases hard to tell apart, not perfectly indistinguishable.
* **A refusal nudge**, at most weekly, when own agents' messages keep being refused: "Agents
  couldn't reach 4 of your sessions this week", with the choices in a neutral order (**Keep off ·
  Ask me each time · Allow my own agents (same repo)**) and none marked recommended. It is
  suppressed when the refusals came from tainted or externally chained senders.
* **Revocation at use.** Link and policy state are rechecked before a queued delivery, a window
  delivery, every subscription event and each delayed `session.read` result (§4.6). Revoking cancels holds, windows and pending launches.
* **Not for connectors in v1.** A connector reply into a session that isn't provably forced-ask is
  refused outright, with no hold (§9.6, Q26).

**(O) Own agents into own allow-all sessions, without a gate.** `inbound.*.allowAllTargets`
(§5.1).
* It covers allow-all **and unknown-mode** sessions, since §5.3 treats unknown as allow-all.
* Off by default, and set only at the machine.
* **Never** for: another account's principal, a connector's, a federated or a `homeassistant:`
  principal; a message whose chain has a cross-account hop (§5.4 `via`, §7.5); a **tainted**
  sender, including one tainted only by reading or being launched by a tainted session; a
  sender or target in a **tainted workspace unit** (§5.8), or anywhere on the machine under the
  `"any"` scope while any unit is tainted; or a sender that **loaded workspace instruction
  files** (below). An immutable, owner-approved schedule may keep a session eligible (§5.4).
  A sender or target whose conversation predates the running host instance is never eligible: it
  carries `unknown:<machine>` (§5.8 Durability, Q53).
* **The instruction-file rule applies to the sender only.**
  * A sender is O-eligible only if the host launched it **with a host-owned configuration home**
    (§5.3 (A)), so personal-scope sources don't load: `~/.copilot/copilot-instructions.md`,
    `~/.claude/CLAUDE.md`, `~/.codex/AGENTS.md`, personal custom agents and skills.
  * And with workspace instruction-file loading (`AGENTS.md`, `CLAUDE.md`,
    `copilot-instructions.md`) **provably off**, by a CLI switch AP1 vectors prove for that agent
    and version, and no workspace custom agents or skills.
  * **Unknown counts as loaded**, so a session the person started, or one whose CLI has no such
    switch, can't send through O.
  * It doesn't apply to **targets**: a target reading its own repository's instructions is the
    disclosed repository-read residual (§5.8). If it applied to targets, O would never work in a
    repository with an `AGENTS.md`, the owner's included.
  * The consequence for the owner (Q34): in a repository with instruction files, an O sender has
    to be a host-launched coordinator with those files switched off. Everything else goes
    through G and windows.
* **The risk, shown in the setting:** "Any agent of yours, including one in an ask-mode session or
  one following instructions injected into it, could get commands run without a prompt through
  another of your sessions that runs in allow-all." The owner turns it on knowingly.
* **Scope**, two choices:
  * **Machines:** set in `inbound.local`, same machine only; set in `inbound.remote` too, any of
    the account's non-probationary hosts (AP5).
  * **Workspaces:** `"same"` or `"any"`.
    * `"same"` is **stricter than taint's containment**. It holds only when **all** of the
      sender's writable roots lie inside **one git unit** (as `unitsOf` in §5.8 identifies it)
      and the target is in **exactly that unit**. Containment never counts for O: a sender rooted
      at `~/repos`, in any non-git directory, or with an `--add-dir` outside its unit, spans units
      and fails `"same"`.
    * **`"same"` never matches across machines.** A unit is per machine, and its identity is a
      local file identity, so a remote sender can't be shown to be in the target's unit. Remote O
      (`inbound.remote`) needs `"any"`.

  **Recommended when enabled: same workspace, same machine** (`inbound.local` only).
* Chain depth, conversation and workflow budgets and rates apply unchanged, with a separate,
  lower per-target rate for allow-all targets (`limits.allowAllPerTargetPerMinute`, default 3).

**(C) The standing coordination group (#912).** A named set of the owner's own sessions that
may send each other `session.reply` text, unattended, allow-all members included, for a bounded
time the owner approves with presence. It is a time-limited, owner-approved form of G's windows
and O. The owner's decisions are H1, H2, M1 and H3 (2026-11-05, 11:32 PT) and (a)–(c)
(11:35 PT). Product decided M2 (2026-11-07). Engineering's review (#912) set M3–M7 and L1–L3.
Nothing is built before #55 and AP5 (AP11, §11.2).

* **What the grant allows (M3).** `session.reply` text from one member into another, and
  nothing else. It goes through §5.2's delivery guard unchanged. Every **Never** row in §5.2
  stays.
  * **Never through it:** launch or resume, transfer, update, forget, settings, pairing,
    attachments, `push.*` or `host.*`. Never approvals, folder trust, forms (`ask_user`) or stop.
  * **Never for** another account's principal, a connector's, a federated or a `homeassistant:`
    principal. Never for a chain with a cross-account hop.
  * **Managing the group** (approve, add, remove, revoke, resume after a restart) is a person
    command from a person device only. It is never an agent, a connector or an HA press.
* **Who may be a member (L1).** A member is bound to its account, machine and session id, and only
  a session holding a credential (§3.2) can be one.
  * A transfer drops the member.
  * Any resume drops the member, under the same session id or a new one. It rejoins only when
    the owner adds it again with presence, as for a new session.
  * A session whose process outlived a host restart wasn't resumed. It stays a member, paused
    until **Resume them** (below).
* **Per-machine opt-in (M1, owner decision).** `inbound.*.coordinationGroups` (§5.1) is off by
  default and set only at the machine, with machine-set ceilings on length and budgets. It is
  honoured only in roster `enforce` mode. Cross-machine use waits for #55 and AP5.
* **Taint pauses a member.** A member must be clean as O requires: no retained origin of any
  category (`external:<account>`, `automation:<connector>`, `forwarded`, `unknown:<machine>`),
  and not in a tainted workspace unit or on a machine whose machine-wide state is tainted (§5.8).
  A member that becomes tainted is paused at once, as a sender and as a target. Unlike O, the
  sender's instruction-file rule doesn't apply: that is the residual the owner accepted (H2).
* **Restarts pause members (H1, owner decision).** Q53 stands. After any host restart, an update
  included, every member on that machine is paused, because each now carries `unknown:<machine>`
  and the machine-wide state is `unknown:<machine>` (§5.8 Durability).
  * **The person sees** a counted Needs-you item, "*Machine* restarted. Its group members are
    paused", with **Resume them**. That is one presence-signed person command that lists each
    member with its current taint, and re-admits them under the same group and generation for the
    rest of the group's time. It is never automatic.
  * **What Resume vouches for.** Only the `unknown:<machine>` that **this restart** caused: the
    origin the current instance gave a conversation that predates it, and the machine-wide state
    it set at its own start. The host records both against the new **instance id**, and **Resume
    them** binds that instance id, the group and its generation. A Resume signed for an earlier
    instance never matches, and another restart pauses the members again.
  * **Workspace units.** A running session that this restart tainted passes `unknown:<machine>`
    to its workspace units, and from them to every resident (§5.8), so an ordinary member's unit
    is tainted too. Every `unknown:<machine>` keeps its **cause and instance tag** (restart,
    with the instance id, or store loss, legacy, no record) through the merge into a unit and the
    inheritance into residents (§5.8). The group's admission check looks at origins, not at a
    unit's tainted flag: it passes over a unit's origin only when its tag matches one Resume
    covers (this instance's restart origin, or one an earlier verified Resume covered). An
    `unknown:<machine>` with any other tag, or with no tag, is never passed over. A unit holding
    any other origin, from any resident, keeps the member paused. The unit itself stays tainted;
    only a unit clear lifts it.
  * **What it doesn't.** It clears no taint: the origin stays, and O and every other check still
    see it. It only lets the group's admission check pass over that one origin. A member stays
    paused if it or its unit has any other origin, or an `unknown:<machine>` from any other
    cause: an unreadable, missing or failed store (which taints everything), legacy or missing
    provenance, no record in the store, or an earlier instance that no Resume covered. The machine
    clear isn't needed, and still isn't possible while allow-all members run.
  * **A second restart.** The restart's origin is permanent (§5.8), so a member resumed once still
    carries an earlier instance's `unknown:<machine>` after the next restart. A Resume also passes
    over that origin when an earlier Resume covered it for this member, group and generation. The
    host keeps each Resume as its signed person command and verifies the signature again; a
    record in the store alone vouches for nothing.
  * **The residual, and its disposition (Product decision, #912).** A restored store can't be
    detected (§5.8), and the design has no rollback counter or host-signed epoch for the taint
    store: Q53 (owner decision, 2026-11-03) declined the off-machine anchor that would be one. So
    if the store is rolled back to before a member's tainting read, the record that would keep
    it paused is gone, and **Resume them** can re-admit a member that read hostile content.
    Product's #912 decision accepts this residual for the group, under H1 (owner decision,
    2026-11-05), which chose re-admission with presence over ending the group at every restart.
    Q53 accepts running without an off-machine anchor, with O still failing closed after a
    restore; it does not accept re-admitting a possibly compromised conversation. No new
    mechanism is added here. The confirm says so in words: "The machine can't prove what these
    sessions read before it restarted."
  * **Open follow-up.** Make Resume's restart exemption require a rollback-detecting anchor (the
    signed, monotonic taint sequence of Q53 (b)) if one is ever adopted.
* **Chains (M2, Product decision).** The grant applies to a message only when the host's
  recomputed `via` (§5.4), the sender included, lies **entirely within the group's members**. A
  person-driven turn starts with `via` = [the sender], so it qualifies. Otherwise the message falls
  back to the per-sender path, G, exactly as if there were no group.
  * `maxDepth` (§5.4) applies unchanged to any hop that leaves the group.
  * Inside the group, depth is bounded by the members.
* **Budgets (L2).** 200 messages a day per member and 10 a minute across the group, with the
  machine's ceilings as the limit. Changing them happens inside the same signed approval.
  * `allowAllPerTargetPerMinute` stays 3, and `perPairPerMinute` and `perTargetPerMinute` apply
    unchanged.
  * Inside the group these budgets take the place of §5.4's per-conversation budget (20 messages,
    60 minutes).
  * A pair that keeps messaging back and forth raises a loop notice on Needs-you.
* **Signed, generation-bound, fail-closed (M4).** The grant and its revoke are signed roster
  `set grants` entries, signed by a person device (#391's person kinds), bound to the group id and
  its generation, and honoured only under V5's 1-hour freshness (§8.10).
  * A host suspends the grant at start-up until its roster state has synced.
  * A revoke binds the group and generation, so an old revoke can't end a new group and an old
    grant can't outlive its revoke.
  * Same-machine pipe delivery is rechecked against the same state before each message.
* **Verification (M5).** Hosts verify the grant with #391's person-kind roster entries, presence
  and the durable replay store. Never with `RemoteAuthorization`'s helpers, which fail open when
  their check is disabled. An exit test proves refusal with that floor off. Delivery is a new
  `agent.command` path; `session.reply` is never added to the Host role.
* **Attribution (M6).** A message delivered under the grant sets the additive
  `driverFrom.grant: "group:<id>@<gen>"` (§6.1), only from the verified grant, and is audited per
  message (§5.6). After a restart the driver shows as unknown or agent, never as the person.
* **The HA route (M7).** Once a member's machine runs the grant, members aren't driven over HA.
  Product sets the order HA is retired per machine. The HA route stays as the fallback until the
  grant ships, and gains no authority from this.
* **Expiry.** 8 hours by default, 4 or 24 hours otherwise, and 24 hours at most (owner decision
  (a)). Renewal is in the app only, never pushed (c): the expiry item and **Approve again** are
  never pushed and never ride the hourly aggregate.

**What the person sees.** This is revision 3 of the coordination-group sheet (#912), as written.

1. **Approval sheet: "Let these sessions message each other".**
   * **How it opens:** a Needs-you item (`coordinationGroup`), "Approve today's coordination
     group". It's never pushed on its own; it rides the hourly aggregate (§5.7 G). A renewal
     (below) doesn't: it is never pushed, on its own or in the aggregate (owner decision (c)).
   * **The opening statement (H3), first and in body type:** "Any of these sessions, or anything
     that misleads one of them (a GitHub comment, a web page, a file), can get commands run
     **without asking you** in the sessions marked **Allow all**, on every machine in the group,
     until *time*."
   * **The residual (H2):** shown once, directly under it, and never folded away: "Members read
     instruction files and issue comments. Text there can steer them."
   * **Then, in smaller type:** "They can send each other messages. They can't approve anything,
     trust folders, answer forms or stop sessions."
   * **Members:** one row per session, showing its verified label (§6.3), machine and agent; an
     **Allow all** badge where it applies; its current taint at that moment; and a checkbox, with
     the accessible name "Include *title* on *machine*".
   * **Ticks:** nothing is ticked by default. "Same as yesterday" ticks yesterday's members, and
     the person still presses Approve (L3).
   * **Rows shown disabled, each with its reason:**
     * A tainted session (§5.8): "Read outside content. It can't join."
     * A machine that isn't opted in (M1): "*Machine* doesn't allow coordination groups. Turn it
       on at that machine."
     * A machine outside roster `enforce` mode: "Needs roster enforcement on *machine*."

     The sheet never offers a way to turn any of these on from here. Nothing cross-machine is
     offered before #55 and AP5.
   * **Until:** a time shown in local time; 4 h, 8 h (the default) or 24 h (the maximum).
   * **Limits:** "200 messages a day each · 10 a minute across the group". Changing them happens
     inside the same signed approval. Each Allow-all target is limited to 3 a minute
     (`allowAllPerTargetPerMinute`); that limit isn't shown, because it can't be edited here.
   * **Buttons:** **Not now** (the default, what Esc does, and where focus starts), then
     **Approve group**, which needs presence.
2. **While the group is active.**
   * **Needs you's standing row:** "Coordination group · *n* sessions · until *time*", with
     **Revoke**. It isn't counted in the badge (owner decision (b)).
   * **Each member's row** gets a "Coordination group" chip, in words and not only a glyph.
   * **Attribution (M6):** "From @sender · coordination group" appears only when the verified
     grant says so. After a restart, an unverified driver reads "From an agent". It never reads as
     yours.
   * **Revoke** takes effect at once, with no presence and no undo. The toast reads: "Group ended.
     These sessions need your approval to message each other again." While the revoke is still
     reaching machines (M4), the row reads "Revoking · ended on *n* of *m* machines", and any
     machine that hasn't confirmed is named.
3. **Exceptions.**
   * **A member becomes tainted:** a counted item, "*Session* was paused from the group: it read
     outside content", with **Remove** and **Keep paused**. Re-admitting it means approving again.
   * **The group expires:** an item, "Coordination group ended at *time*", with **Approve again**.
     It reopens the sheet with "Same as yesterday" available, which still needs an explicit tick
     of Approve and shows each member's current taint (L3). This item is in the app only. It is
     never pushed and never counted in the hourly aggregate (c).
   * **A host restarts, an update included (H1):** that machine's members are paused. A counted
     item reads "*Machine* restarted. Its group members are paused". **Resume them** is one tap
     that needs presence, and its confirm lists each member with its current taint. It is never
     automatic.
   * **A session resumes, under the same id or a new one, or a new session starts:** a resumed
     member leaves the group, and it, like any new session, joins only when the owner adds it
     with presence (L1).

**Capture states, at implementation:** none, pending, active, member paused, paused after
restart, revoking *n* of *m*, ended and revoked; a machine row disabled because it isn't opted
in, and one disabled because it isn't in `enforce` mode. Each in light, dark and high contrast,
on phone and desktop.

**(R) Read-only cross-account access to allow-all sessions.** Per link, `expose.allowAllRead`
(§7.4, §7.11.5). Off by default.
* Linked accounts' agents may **read** (`sessions.list`, `session.get`, `session.read`, whose wait
  form is how agents follow a session) the owner's allow-all and unknown-mode sessions **in the
  pinned workspaces**. The person view uses `sessions.subscribe` (§7.12). Both are under §7.4's
  projection, and the reader becomes tainted (§5.8).
* **Never reply** through it. A reply into those sessions still needs G, with `expose.gate` on.
* **The disclosure, shown in the link dialog:** "Their agents could read what your allow-all
  sessions say, including command output. Transcripts can contain secrets."

**(S) Sandboxed allow-all.** An OS-enforced restriction of the filesystem, credentials, IPC and
network egress around an allow-all session.
* It isn't a prerequisite for reads or for cross-account G.
* **Immediately after AP9**, as AP10 (owner decision, Q50).
* Removing the gate on the strength of a sandbox would need **separately demonstrated
  boundaries**, not this design's assertion.
### 5.8 Provenance and taint

**Taint follows retained context, not processes.**
* **What sets it.** A session becomes **tainted** when content it may act on arrives from
  outside the owner's own agents and people:
  * **cross-account free-form content**, from a reply **or a read** (a collaborator's transcript
    the agent read is as influential as one sent to it);
  * **Home Assistant and connector text**, recorded as **untrusted-automation influence**, with
    no invented foreign identity;
  * **forwarded scout output** (below);
  * **transitively, any content-bearing Bridge read of a tainted session**, of any account and
    over any transport (`sessions.list`, `session.get`, `session.read` and its wait results,
    the person view's subscription): the reader takes on the read session's origins. A clean
    coordinator that reads its own scout after the scout read @alex is tainted by @alex. Only
    the typed projections (`sessions.status`, `session.status`, and `launch.options` without
    titles, below) are exempt, because they carry no free text;
  * **inheritance at launch**: any session an agent launches inherits the launcher's taint,
    whether forced-ask or gated, new or resumed. The prompt may embed what the launcher read.

  The host enforces both: it records the reader's new origins as part of serving the read, and
  the launched session's origins as part of the launch record. Neither depends on the agent
  reporting what it read.
* **What it records.** Taint is kept per origin account and category (`external:<account>`,
  `automation:<connector>`, `forwarded`), with the time it was first and last set.
* **What it survives.** It survives anything that keeps the context: person-driven turns,
  resume, transfer, compaction, and a new process or instance that reloads the conversation.
  **A new process is not a clean context.**
* **What clears it.** Only two things:
  * an **adapter-demonstrated context removal**, proved by a version-specific vector;
  * a **fresh conversation** (below).

  Never a person's reply, a timer, a resume or a compaction.

**Durability.** Taint that a restart forgets fails open: a tainted coordinator would come back
clean after a host update, and a reset generation would let an old clear match again. So:
* **What is persisted.** Every session's origins, every unit's origins and contamination
  generation, the machine-wide "every unit" state, and a persisted **generation high-water
  mark**. All of it lives in the host's private store.
* **When.** It is flushed before content is handed to a reader, before a launch starts and before
  a delivery is typed, so nothing acts on taint that isn't on disk yet.
* **Failure.** An unreadable or missing store means **everything is tainted** with
  `unknown:<machine>`. Unreadable is not absence.
* **The store can't prove itself.** Origins, generations and the high-water mark all live in one
  private store, and a restore of the whole store can't be detected from the restored files
  (host-config.md:228-231). Back the store up while a coordinator is clean, let it read hostile
  content, restore the store and restart only the host, and the CLI process lives on with a
  matching credential and a clean record. So **persisted state can only add taint, never vouch
  for cleanliness**. The anchor is the **running host instance**:
  * Each host start draws a random **instance id**. "Clean" is something only the current
    instance can say, from what it has observed itself.
  * **Every conversation that existed before this instance started**, whether its process
    outlived the previous host or it's resumed later, gets `unknown:<machine>` as **its own
    origin, permanently**. So does any conversation the store has no record of. A first AP8
    start counts as one where every existing conversation predates the instance, since pre-AP8
    conversations may hold Home Assistant text or AP1-era reads.
  * **"Started under this instance"** means one of two things: this instance **launched** it as a
    new conversation, or it **observed an adapter-proven fresh start** (Claude's `SessionStart`
    with `source: startup` and an empty transcript, say, proved by an AP1 vector for that
    version). A discovered or adopted session, and a terminal `--resume` or `--continue`, even
    one that forks a new id, is neither, so it gets `unknown:<machine>`. "No record" is never
    read as "record it now as clean".
  * At every start the machine-wide state is set to `unknown:<machine>` (below), since a
    rolled-back store may have lost a unit's taint from a session that has since exited.
  * Clears bind the instance id and the generation, and the generation is re-based above any
    value the store has held. A clear signed for an earlier instance never matches.
  * **The cost.** The machine-wide `unknown:<machine>` merges into residents like unit taint, so
    after any host restart, an update included:
    * every session started after the restart and **before the machine clear**, a fresh
      coordinator included, keeps `unknown:<machine>` for good;
    * the machine clear is refused while any unconfined tainted session runs, which includes
      every pre-restart allow-all session **and any allow-all session started since**;
    * every open window **pauses**, because its senders gain an origin outside the window's set,
      and needs re-approval.

    So O after a restart takes this order: end the unconfined sessions, clear the machine, then
    start the coordinator and its targets. G keeps working, with "origins unknown" labels on
    older conversations and windows re-approved. The owner accepted this cost (Q53); an
    off-machine anchor that would avoid it stays a possible later design.

**`unknown` is an origin, and it sticks.** Provenance that is missing, legacy or `"unknown"`
(§4.3) becomes the origin `unknown:<machine>` on whatever receives it: a reader, a target, a
launched session. It is never cleaned up by a later explicit set, and it fails O like any other
origin.
**What a tainted sender may do.**
* It may use the gate (G), a gated launch, or a direct reply to a **provably forced-ask** target.
  The receiver then **inherits** the taint.
* It may **never** use the own-account setting (O).
* **Approval authorizes execution and keeps the origin label.** `driverFrom` (§6.1) and the
  prefix (§6.4) name the external source, and the gate prompt shows the retained external origins
  beside the immediate sender (§5.7).

**"Start a fresh coordinator", one tap.** It is a **person-only** action (a person device, never an
agent, connector or Home Assistant). It starts a fresh conversation from:
* the immutable prompt **a person originally supplied**, with its provenance. The provenance
  comes from the **host's launch record with a person-kind principal**, never from classifying
  the transcript. A prompt that came through G from an agent or an external source doesn't
  qualify;
* plus a typed, host-built **state block**: session ids, statuses and typed status facts, below.

It carries no transcript, summary, resume or subagent memory. "Clean" means **no retained
external Bridge content**. It doesn't mean immune to injection through the repository or `gh`.

**Workspace taint: content laundered through files.** A tainted session that can write files can
leave instructions or notes for its siblings, such as an `AGENTS.md`, which agents read from disk
and the host can't see. So taint also attaches to **workspace units**.

* **One function, `unitsOf(paths)`,** identifies units for both taint and O's `"same"` scope
  (§5.7 O), so they can't drift apart. Taint uses it with containment and overlap, the safe
  direction; O's `"same"` uses the stricter "exactly one unit" (§5.7 O). It is closed under path
  containment:
  * a git checkout's unit is its **git common directory**: the main checkout and every worktree
    of it, including host-made ones;
  * **submodules** belong to their superproject's unit;
  * a **non-git directory** (such as `~/repos`, or an approved folder holding several
    repositories) is a unit of its own, and also **contains** the unit of every repository nested
    under it;
  * a nested independent repository is its own unit, and is contained by any directory above it.
* **A unit's identity is a file identity**, not a path: the volume and file id of the git common
  directory (or of the folder, for a non-git unit), from `GetFinalPathNameByHandle` and
  `FILE_ID_INFO` on Windows, or the device and inode elsewhere. So junctions, symlinks, `subst`,
  `\\?\` forms, case and 8.3 short names can't make one repository look like two. **Network
  paths** (UNC, mapped drives) are excluded from O altogether.
* **A session's writable roots** are its working directory plus every added directory
  (`--add-dir`, `/add-dir`, Codex writable roots) the host can see. The session taints **every
  unit those roots contain or overlap**. A session whose writable roots the host **can't fully
  see** counts as unconfined (next).
* **A tainted session not proven confined** to its writable roots, meaning any allow-all or
  unknown-mode session or one with unseen roots, can write anywhere the user can. For O, it
  taints the **whole machine**: a sticky machine-wide state that covers every unit, **including
  units first seen later**, until a person clears the machine (a presence-signed clear bound to
  the machine's own contamination generation, as for units below).
* **When a unit becomes tainted.** While a tainted session runs, or has run, with a writable root
  in the unit (the host can't tell whether it wrote anything), the unit carries that session's
  origins.
* **Every resident inherits it.** When a unit becomes tainted, or its origins expand, the host
  merges the origins into **every session in it**, including ones already running. A session
  launched there starts with them. Inherited origins are the session's own from then on: they
  persist after the unit is cleared, and travel through reads, replies and launches like any
  other taint. An `unknown:<machine>` origin keeps its cause and instance tag (§5.7 C) through
  every merge and inheritance; two with different tags stay separate entries, and one that has
  lost its tag counts as the store-loss cause.
* **Clearing** is a **presence-signed person-device command** ("I've checked these checkouts on
  DESK").
  * It binds the canonical unit id and the unit's monotonic **contamination generation**, which
    increments on every tainted residency, origin expansion and membership change.
  * The host compares and clears **atomically**, under the same lock it uses for launches and
    taint propagation. A tainted session that started, wrote and exited between the person's
    review and the clear's execution has bumped the generation, so the clear is refused.
  * It is also refused while a tainted session still runs there.
  * It clears the unit, not the sessions that already inherited. **No clear ever lifts a
    session's own origins**, `unknown:<machine>` included.
  * It binds the **instance id** as well (Durability, above), so a clear signed before a restart
    never applies after it.
  * **The machine clear** works the same way, bound to the instance id and the machine's
    generation. It is also refused while **any unconfined tainted session** runs on the machine.
* **O and imports.**
  * O is refused to and from every session in a tainted unit.
  * With O's `"any"` workspace scope, O is refused on the machine while **any** unit there is
    tainted.
  * The instruction-file rule for O senders is in §5.7 O.
* **Residuals**, disclosed like `gh` and repository reads:
  * a session reading a tainted unit's files by path from **outside** that unit, through a G
    message rather than O;
  * files a tainted session wrote outside its writable roots that reach another machine (a synced
    folder, a push) and are read there;
  * reading another session's **transcript files** on disk;
  * a tainted agent on another machine pushing a file that a clean checkout here pulls;
  * a person clearing a unit without checking it.
**Typed reads of your own sessions, which don't taint.** Any coordinator that lists sessions with
their titles is tainted almost at once, so the host also offers **typed projections of list and
get** for the account's own sessions: `sessions.status` and `session.status`. They have the same
constraints as the cross-account status below: bounded enums, booleans, counts and timestamps,
with opaque handles the reader generates, and no titles or free text. `launch.options` is served
the same way to agents and unattributed callers: resumable sessions as handles and typed
fields, never titles (§5.2). They are **the default
read path for coordinators**: the MCP server's `list_agent_sessions` and `get_agent_status`
return them unless the agent asks for content explicitly (§3.6). They are also all an
unattributed caller gets (§3.4).

**What this means for O.** Taint now follows content reads, launches and workspaces, and O is
refused for any of them. So **O is a specialised, clean-workspace mode**: a coordinator that
reads only typed status, works in an untainted repository, and doesn't load workspace instruction
files. **The gate (G) and its windows are the general path** for agents to reach allow-all
sessions. That is what the adversarial review concluded, and the owner should see it (Q34).
**Typed `session.status`, which doesn't taint.** A collaborator's agent can coordinate on status
without importing free-form content:
* **Bounded fields only:** enums, booleans, counts and timestamps, with **opaque handles the
  reader generates**. No titles, branch names, check names, error text or peer-supplied hashes.
* **The reader's side fetches PR and CI state** from GitHub, bound to the pinned repository, the
  reader's assigned work items, and the expected PR, head SHA and required checks.
* **It guides scheduling only.** It never authorizes a merge, a deploy or the use of a credential.
* **Owner decision** (Q52): hosts hold no GitHub token, so v1 fetches through the **reader's
  app**, and a broker comes later as its own design. CI state is unavailable while no person
  device of the reader is online.
**The badge.** A tainted session's card reads "**External Bridge content retained · @welshofer ·
14:05**", with its categories. **Residual:** the bridge can't taint what the agent reads through
`gh` or the repository itself.

**`replyPath`.** For each session, the host computes, per requesting principal and including
taint: `direct | gate | gate(tainted) | own-allow-all | refused(<reason>)`. It is advisory, and
never reveals a session the principal can't see. Cards show it as "Agents: direct / ask you /
blocked".

**The scout template.** A scout (an agent sent to read a collaborator's sessions or a repo)
reports **typed, host- and GitHub-observed facts separately from its recommendations**. It has no
`approved` or `nextAction` fields that another agent could mistake for authority. Forwarding a
scout's output to another session is a gate action titled "**Approve forwarding this external
content**": provenance is kept, and the receiver becomes influenced.

---

## 6. Display and attribution

### 6.1 Protocol fields (additive)

| Field | On | Value |
|---|---|---|
| `driver` | `Session` | `agent`, unchanged (v1-sessions.md:832) |
| `driverPrincipal` | `Session` | `agent:<machine>/<session>` (§3.1), replacing `homeassistant:<id>` for native agents. HA-connector presses keep theirs |
| `driverFrom` (new) | `Session` | `{ account?, login?, repo?, machine, machineName, session, title?, agent, federated?, depth, origins?, grant? }`: who drives, for display. `machineName` is the **target host's** view of the sender's roster name (signed by a person's device, #391 §3.2), never a name the envelope carries. `title` is the sender's session title as the envelope's issuer gave it (cut to 120 bytes, hidden characters made visible as `NeedsYou.Reveal` does). `account`, `login` and `repo` (the link's repository, §7.11) only when it is another account's (§7). `login` is the receiving side's own resolution of the T2-verified user id (`GET /user/{id}` by its app, recorded in its link statement and refreshed daily), never a name the peer supplied. `origins` lists the sender's **retained external origins** (§5.8), kept even when the person authorized the message through the gate. `grant` is `"group:<id>@<gen>"` only when the message was delivered under a verified standing coordination group (§5.7 C), and is audited; after a restart an unverified driver shows as unknown or agent, never as the person |
| `taint` (new) | `Session` | `{ origins: [{ account?, category, firstAt, lastAt }] }`: the session's own retained external content (§5.8), for the account's own clients. It drives the badge "External Bridge content retained" |
| `replyPath` (new) | `Session` | `direct` \| `gate` \| `gate(tainted)` \| `own-allow-all` \| `refused(<reason>)`, computed per requesting principal (§5.8). Advisory; it never reveals a hidden session |
| `activity.userMessageFrom` | `Activity` | `agent` (PR #501), set **only** from a verified agent command or a local agent connection, never from anything the sender's text contains (the #501 security review's requirement) |
| `activity.userMessageLabel` | `Activity` | Built **only from verified fields** (§6.3): `"<agent> on <machineName>"`, for example `"copilot on DESK"`; across accounts `"<agent> on <link name> · @<T2 login>"`. Cut to 64 bytes. Clients show it as "Sent by copilot on *DESK*". The sender's `title` is never in the label: older clients that show only the label must not show an unverified title as the sender |

Older clients read `driver` and `driverPrincipal` as today and ignore `driverFrom`. The
`homeassistant:` principal, `SessionDrivers`' arm window (600 s) and take-back rules are reused
unchanged. Only the principal and its source change.

### 6.2 What the person sees

* **The edge.** The violet `driven-badge` diamond and agent colour (ui.md:54, rule 7 at :79),
  exactly as for any agent-driven session, now set from `principal.kind == agent`, which is exact
  (design.md:503-506).
* **The words.** "Driven by an agent" becomes "Driven by an agent on *DESK*", from verified fields
  only (§6.3). The conversation shows the agent's prompt as #489 specifies (left-aligned,
  collapsed, diamond label), labelled "Sent by copilot on *DESK*". The sender's title appears only
  as a secondary line, quoted and marked as theirs: *Their session: "Fix the flaky test"*. Tapping
  the label opens the sending session when it's visible to this device, where its title is shown
  from this account's own state.
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

  A peer's host `name` (§7.3) is never shown as identity, and descriptors carry no login at all
  (§7.11.3). The sender's
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
* Built by the target host. One line, since replies are one line (v1-sessions.md:433-439).
* Cross-account adds `· external: @alex` (the T2 login) and is **mandatory** (`label: none` is
  ignored for it).
* PR #501's matching of an agent's prompt by its record and text is done on the text **as typed**,
  prefix included, which the host knows.
* Whether same-account messages carry the prefix is Q6 (recommended: yes, default `prefix`).
* **Retained origins.** When the sender is tainted (§5.8), the header also names the retained
  external origins, even when the person approved the message through the gate: `· carries
  content from @alex` or `· carries automation text`. Authorizing never cleanses provenance.
* **The prefix and the look-alike filtering are hygiene, not a security boundary.** A model may
  ignore them, and a sender's words may still persuade. What bounds an injected instruction is §5: the
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
| Data leaving | Stays with the same person (on other machines) | **Disclosure to a third party**. Organisation policy may forbid it, but no detection of it exists yet (S8 is still open, design.md:1338-1343), so the person decides (Q51) |
| Inbound text | Instructions from your own agents (still injectable) | Instructions from someone else's agents, to be treated as **untrusted input** |
| Default reach | Any session (decision 2) | **Nothing**, until both owners consent to a scope |
| Launch | In approved workspaces: `ask` unattended when provably forced-ask, otherwise a gated launch, which may request `allowAll` with the person's approval (§5.7 G) | Opt-in per link, one workspace, `ask` only. A cross-account gated launch can never request `allowAll` |
| Relay | One untrusted operator, who sees one account's metadata | Up to two, and the target's operator learns that A and B are linked |

### 7.2 Establishing trust: options

| Option | How | For | Against | Verdict |
|---|---|---|---|---|
| **(F1) Pairwise link, invitation with key exchange** | The two owners each make a **link** on a presence-proven device. They exchange **link descriptors** and compare a **link code** derived from both. Each side then records the link in its own account, signed by its own person's device | Each side's trust stays inside its own roster. Revocable by either side alone. No change to #391's acceptance rule. Works across relays, operators and self-hosting | One exchange per pair of people. More than two parties need a link per pair | **Recommended** |
| (F2) Shared project or space object | A signed, append-only membership log for "project X", co-signed by each member account's person devices. Hosts of each member subscribe | Natural for many parties. One place for scope | A second roster-like consensus object across accounts, with forks, removals and seniority *across people*. That is everything #391 solves, but between parties with no common senior device | **Later**, as a layer over F1 links ("space = named set of links sharing a workspace mapping") |
| (F3) Cross-signing roster roots | A's roster includes B's genesis (or vice versa), so A's hosts trust B's whole roster | Simple verification | Transitive and unscoped: every approval mistake or compromise on B's side becomes A's, and #391's seniority and probation don't compose across people. Revocation of one B device needs A to track B's roster semantics | **Rejected** |
| (R1) Relay-to-relay peering | RA and RB route frames to each other. Hosts talk through their own relay | Each host keeps one connection | Needs operator-to-operator trust and authentication, a server-to-server protocol, and abuse controls on both. Both operators see the metadata. Fails when either side is LAN-only or self-hosted without peering | **Rejected for v-full**; possible later optimization |
| (R2) Host-to-host, **guest on the target's relay** | A's host connects to RB as a **guest** admitted by B's link (not by GitHub sign-in to B's account), and opens Noise channels to B's linked hosts | Only RB (the target side's operator) sees the cross-account metadata, and RA sees none. Works when RA == RB with the same mechanism. End to end, as today | A second outbound WSS per linked relay. RB must admit a narrowly scoped guest | **Recommended** |
| **(F4) Repo-based contributor discovery** (owner direction, 17:19 PT) | Two contributors to the same GitHub repository who both use the bridge are **suggested** to each other in their apps. Identity is proven by the contributor's GitHub-registered signing key, and membership by GitHub's own access check. Only people with write access **and** recent activity in the repo are suggested (Q22). Both owners accept, and the result is an F1 link **scoped to that repository** | A natural scope (the repo's sessions), audit and revocation tied to repo membership. No out-of-band exchange. Discoverable | Trust is rooted in GitHub accounts, not in a voice-compared code. Needs a GitHub repo, push access and an SSH signing key. Publishing anything has privacy costs | **Recommended as the default way to establish an F1 link.** Every link is repo-scoped. Explicit invitation (F1 with a link code) remains the fallback. §7.11 |

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
  "account": "<A's account id>",
  "relay": "https://relay.example",
  "genesis": "<A's roster genesis entry hash>", "generation": "<A's generation>",
  "linkKey": "<P-256 ECDSA public key, SEC1, base64url>",
  "revokers": ["<P-256 public keys that may only revoke this link>"],
  "repo": "<GitHub repository id, for a repo-scoped link>",
  "singleMachine": false,
  "expiresAt": 1795900000
}
```

* `account` is the sender's claim. The descriptor carries **no name**: no login, display name or
  email. The receiving side shows a GitHub login only as GitHub resolves the T2-verified numeric
  user id **at display time** (`GET /user/{id}`, §7.11.3), or else the name its own person typed
  at link time (§6.3).
* `linkKey` is a **dedicated per-link signing key** created on the A person's device that makes
  the link. It signs A's later statements *about this link* (below). It isn't a roster key, so
  B never needs to understand A's roster rules. A's own hosts accept it because A's person's
  device signed a `link.create` person command naming it (#391 §7.2 machinery, presence
  required).
* `revokers` are keys pinned at link time that may sign **only** this link's tombstone and, for
  an online revoker device, its freshness attestation (below). Never a host addition or a rotation:
  the `sign` keys of A's other presence-capable person devices, and optionally an offline
  recovery key. The link code covers them, so they are as pinned as `linkKey` itself.
  * **At least one is required, off the custodian's machine.** In the common setup A's laptop is
    both the custodian and the only host, so a thief who passes Windows Hello there holds
    `linkKey` and `hostSign` together. A revoker on a **separate** device (A's phone) or an
    **offline recovery key** (printed, or on a hardware key) is what still lets A end the link.
    A's app refuses to make a link without one. B's app refuses a descriptor whose `revokers`
    is empty, or repeats `linkKey` or a listed host's `hostSign`. B can't see where a key lives;
    keeping it off the custodian's machine is A's app's rule, and in A's interest.
* `repo` is the immutable numeric repository id (not the name), for a repo-scoped link (§7.11).
  It is part of the link's identity, bound into every channel and command (below).
* `singleMachine` (Q46) says A's side has no attester off the custodian's machine (below). It is
  in the descriptor, so the link code and T2 cover it, and it is **immutable for the link id**:
  changing it means a new link.
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

**Peer host statements.** Each side publishes to the other the **hosts it exposes to this link**:
`{link, repo, version, hosts: [{id, add, dh, hostSign, name, custodianMachine}], issuedAt,
expiresAt}`, signed by its `linkKey` **and co-signed by an attester**, under its own domain label
`F("agent-bridge link-attest v1")` (§8.10 V2). An **attester** is:
* a listed host that is **not on the custodian's machine** (`custodianMachine: false`), with its
  `hostSign`; or
* a `revokers` device that is online, such as A's phone app, which co-signs automatically
  (keeping authority needs no presence, as in §7.11.4) under the same label.

A co-signature from a host on the custodian's own machine **doesn't count**, since one stolen
machine holds both keys. A's app sets `custodianMachine` honestly, and changing it on a listed
host is a host change that needs B's person (below).

**Single-machine owners** (Q46). If A has no attester (one machine, and only an offline recovery
key), A's app sets `singleMachine: true` **in the descriptor**. B accepts a co-signature from a
host on the custodian's machine **only if `singleMachine` was pinned true at acceptance**; a
statement can't switch it on later. B's app says so before acceptance: "@alex's side of this link lives on one machine.
If it is stolen, only their recovery key can end the link before it expires." The residual below
then applies until that recovery key's tombstone arrives or the link expires.

**Clock checks.** An attester refuses to co-sign a statement whose `issuedAt` is more than 120 s
from its own clock. B refuses a statement whose `issuedAt` is more than 120 s in its future
(`TransferProtocol.Skew`, as §4.4). So a thief can't pre-date statements to outlive a removal.
* It **minimizes disclosure.** B sees only the machines A chose to expose, not A's roster,
  devices, kinds or platforms. The listed `name` is A's claim, shown as A's label for that
  machine, never as verified identity (§6.3).
* It is **self-contained to verify.** A's statement verifies under A's `linkKey`, pinned by the link
  code or by repo discovery's T2 proof, and under an attester key B has already confirmed or
  pinned for this link. B's hosts don't need A's roster.
* **The co-signature is what a thief with only the custodian's key can't produce.** An attester
  co-signs a statement only if, in A's roster as it currently holds it: the device whose
  `link.create` command named this `linkKey` is still **active**, every listed host is active, and
  no tombstone for the link is known. So when A removes the custodian device, A's attesters stop
  co-signing, and a thief holding **only** the stolen `linkKey` can't renew the 24 h freshness
  (§7.5 step 4). The link then lapses within 24 h even if no tombstone reaches B.
* **The residual.** A thief holding the `linkKey` **and** one confirmed attester's keys (a second
  stolen host, or the stolen phone), or the custodian's machine of a `singleMachine` link, can keep
  renewing. That lasts until a tombstone from another host or a revoker arrives (below), or the
  link expires (30 days, §7.4). It is not bounded at 24 h.
  * A **renewal mandate** doesn't lengthen this: the peer renews under its mandate only after a
    fresh **presence-signed** statement from this side (§7.4), which the thief can't make
    without passing the custodian's presence check.
  * A thief who **can** pass it keeps the link until a tombstone or the mandate's **final
    expiry**. The link dialog says so: "If @alex's devices are stolen and unlocked, this link
    could last until <final expiry> unless they revoke it."
* **Co-signing grants nothing else.** A `hostSign` link attestation is never an entry signer, a
  cosigner or a person command in either roster (§8.10 V2), can't add a host (only B's person
  can, below), and can't rotate `linkKey`.
* **A host that is new to the link needs B's person to confirm it.** A statement version that adds
  a host B hasn't accepted before is held. B's app asks "@alex wants to add *alex-desktop* to the
  danswett/agent-bridge link. Allow its agents?", and B's person confirms with presence.
  **Any key change for a listed host**, `dh` or `hostSign`, is treated the same way: as a new host
  that needs that confirmation. Only removing a host applies at once. So a stolen `linkKey` can't
  quietly add an attacker's host or swap a listed host's keys for its own.

**Link context binding.** With two links between the same pair of hosts (two repositories, say),
a relay could otherwise deliver a command on the channel admitted for one link and have it run
under the other link's expose set. So the link id and, for a repo-scoped link, the immutable `repo`
id are bound three times:
* into the **guest channel**: the Noise prologue of a guest channel includes `link` and `repo`
  beside both device ids (design.md:452-454), so a handshake under the wrong link fails;
* into **every signed command**: `target.link` and `target.repo` (§4.3);
* into **every statement**: `link` and `repo` are signed fields.

Both hosts check that all three agree, and with the link record each holds. A mismatch is
`refused` and audited as `link-mismatch`. Neither host ever infers the link from the peer pair.

**Link-key custody, rotation and revocation** (Q39).
* **Custody.** `linkKey` is created on the presence-proven person's device that makes the link,
  in the same tier as its roster `sign` key: the Secure Enclave on iPhone, iPad and Touch-ID Macs;
  the TPM on Windows after #391's R4; a non-extractable WebCrypto key for a PWA. It signs only
  with presence. A device that can't hold it at that tier can't make links.
* **Revocation authority, pinned at acceptance.** B honours a tombstone for the link signed by
  **any** of: A's pinned `linkKey`; the `hostSign` of any host B has confirmed for this link
  (under a tombstone domain label); or a pinned `revokers` key from A's descriptor. All of these
  were fixed by the link code or B's confirmation, so B can verify each one without A's roster.
  The mirror holds for A. A revocation key can only end the link or keep an already-confirmed
  statement fresh; it can never widen anything.
* **A link tombstone ends it permanently.** `{link, repo, tombstone: true, at}`. Both sides' hosts
  honour it **at once and permanently** for that link id. After a tombstone nothing about that link
  is renewed, rotated or re-listed, by `linkKey` or anything else; reconnecting is a new link,
  with a new id and a new link code or T2 proof.
* **Tombstone delivery doesn't depend on RB.** It is a small signed blob, so it travels by every
  path that exists:
  * any A host's guest channel to B's hosts;
  * the repo directory (§7.11.2), as a tombstone entry B's app checks on its daily re-check;
  * out of band: A's app shows it as a QR code and a copyable text block, which B's person pastes
    into B's app ("@alex revoked this link");
  * B's own relay registration, when RB is honest.

  If none of these reach B, the missing co-signature still ends the link within 24 h, except in
  the residual above.
* **Removing the custodian device revokes the link.** When the device that holds `linkKey` is
  removed from A's roster (#391 §6), A's hosts and attesters treat every link that key signs for
  as revoked, stop co-signing, and **each host sends a tombstone signed by its own `hostSign`**,
  which B verifies against the pinned host list. A's client also sends one signed by a `revokers`
  key when it can. A tombstone from the custodian's own machine counts too: only renewal is
  restricted to attesters off that machine.
* **Rotation needs both people.** A's person rotates `linkKey` from a presence-proven device of A.
  The new key is endorsed by a statement signed by the old key, co-signed by a listed A host
  (which checks a person command for it, #391 §7.2), **and confirmed by B's person with
  presence** ("@alex's link key changed. Accept?"). Until then B keeps the old key. If the old key
  is lost, or a tombstone exists, it is a re-link.
**Guest admission (relay change).** RB gains one admission kind:

* B's person's device registers the link with RB: `{link, guests: [A host dh keys], reach:
  [B host ids], expiresAt}`, on its admitted socket.
* An A host connects to RB with `hello { kind: "guest", link, repo, pub }`, then the usual
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
| **Expose: sessions** | **None visible** | Only **shared** sessions: those launched in the link's **workspace** (`expose.workspaces: ["Project X"]`), or explicitly shared by the person ("Share with @alex's agents" on a session card). For a repo-scoped link, the link's workspace is the workspaces **pinned** to the repo at acceptance, and worktrees the host made for them (§7.11.5). **Allow-all and unknown-mode sessions are not exposed** (§5.3), unless the link's `allowAllRead` opt-in exposes those in the pinned workspaces for **reading only** (§5.7 R). Everything else is `not-found`, the same as nonexistent |
| **Expose: status** (recommended preset) | None | Typed `session.status` only (§5.8): bounded enums, booleans, counts, timestamps and reader-generated handles. **No titles**, no free text; a person-view display projection can add titles as its own grant (§7.12). It doesn't taint the reader. This is the preset the dialog offers first |
| **Expose: content** | None | Free-form reads under the projection below. The dialog warns: "**@alex and their AI provider may process and retain what their agents read, under their own policies. Read-only access and later revocation don't retract copies.**" It taints the reader (§5.8) |
| **Expose: actions** | None | `read` (status or content, above), `reply`. `launch` opt-in, only into the link's workspace, `ask` only, counted against `launchesPerMachinePerHour` / 4. `gate` opt-in: replies to allow-all or unknown sessions in the link's workspaces are held for this owner's per-message approval (§5.7 G). The own-account setting (§5.7 O) never applies to a link |
| **Expose: content projection** | When content is allowed: `id`, `title`, `agent`, `status`, `statusSince`, `activity.response` (≤ 16 KiB), `waiting: bool`, `driverFrom` | Nothing more in v-full. **Never** `cwd`, `userMessage` (it may hold the owner's private instructions), `reasoning`, tool `history`, `subagents`, `question` content, usage, attachments, or `repository`/`branch` unless the link names that repository. Titles are content, never status; a person view shows them only under a separately authorized display projection (§7.12) |
| **Reach: which of my sessions may send** | **None** | Only sessions launched in the link's workspace, or adopted for the link explicitly. So an agent working in a private repository with access to secrets can't be talked into posting to the collaborator: it has no reach |
| **Reach: actions** | None | `read`, `reply`, `launch` (if the peer exposes it) |
| **Expiry** | 30 days | Renewable with presence, or within a **renewal mandate**: a longer period (for example 6 months) the owner approves up front, inside which the app refreshes the link without presence while membership and activity stay fresh (§7.11.4). **Each side renews under its mandate only after a fresh presence-signed statement from the other side** (within the last 30 days), so a thief holding keys and the victim's GitHub session, but unable to pass the custodian's presence check, can't ride the peer's mandate. A promised final expiry is never extended silently. A link that expires stops both directions on each side's hosts without the other's help. **A mandate needs presence somewhere:** if both sides rely on mandates and neither person signs with presence within 30 days, renewal stops at 30 days and fails closed. Each side renews on the **other's** fresh statement, so a mandate keeps a link alive only if **each** person makes one presence-signed statement a month; the app asks for that one tap when it is due. The mandate's value is that the tap needs no review of scope, not that it removes presence |

Consent is **asymmetric and local**: each owner's hosts enforce that owner's statement and nothing
the other side says. B can expose read to A without A letting any of A's sessions send.

### 7.5 Verification at a target in another account

§4.7's checks, with these changes:

1. **Channel.** The peer is a guest connection whose static key is a host listed in a **live,
   unexpired, untombstoned** link's peer host statement, and T is listed in this side's own expose
   statement. The channel's bound `link` and `repo` equal the envelope's `target.link` and
   `target.repo` and the link record's (§7.3, "Link context binding").
2. **Account.** `account` is the link's peer account, and `target.account` is T's.
3. **Signature** under the listed host's `hostSign` key, on a guest channel whose static key is that host's listed `dh` (§8.10, V4). Principals are keyed by A's account, generation and add hash, and the full key (V7).
4. **Freshness of the peer.** T needs a peer host statement whose `issuedAt`, on T's clock, is no
   older than **24 h** and not more than 120 s in T's future, signed by the peer's `linkKey` **and
   co-signed by a peer attester** off the custodian's machine, or by that machine's host for a
   `singleMachine` link (§7.3). `linkKey` alone never renews freshness, so a thief with only the
   custodian's key can't keep a link alive after the peer removes that device; §7.3's residual
   covers a thief with an attester's keys too. Otherwise the link is stale: nothing flows (Q9).
5. **Policy:** this side's expose statement (sessions, actions, projection) and the per-link
   rates, **half** the single-account defaults. **Taint** (§5.8): the receiving session becomes
   tainted by the external origin, whatever drives it later. **Re-rooting at the link.** T treats
   the peer's `chain` as opaque and never walks it. Having verified the link envelope (steps 1-4),
   T starts a local chain for the receiving session R: a new `root` (the link envelope's hash),
   `rootAt` = when T received it, `messages` 1, and `via` = [the peer's sender, R]. R's next
   message carries a signed `anchor` naming the link, the link envelope's hash and the peer
   account and session in place of `prev`. Same-account hosts accept the anchor on T's signature
   and never walk past it. So `maxDepth` across accounts is **3 sessions**: the peer's sender, R,
   and **one** further session S within this account, reached only through G or into a provably
   forced-ask target, which inherits the taint. S reaching a fourth session X is
   `chain-too-deep`. Never through O: `via` keeps the external entry, which is how O sees "a chain
   with a cross-account hop" (§5.7 O).
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
* **Revocation at use.** Every host rechecks the link before a queued delivery, a window
  delivery, each subscription event and each delayed `session.read` result, whose wait is
  cancelled when the read grant narrows (§4.6, §5.7 G), and a revocation cancels that link's holds,
  windows and pending launches. The app shows **which hosts have acknowledged** the revocation
  ("Revoked · DESK ✓ · LAPTOP pending") rather than promising an instant global effect.
* **What revoking does and doesn't do**, said in the dialog: "Revoking stops new access. What
  @alex's sessions already read stays in their transcripts, and their AI provider may keep it
  under its own policies. We can't retract copies." Where the directory keeps entries (§7.11.10)
  and where transcripts live (each person's own machines and providers) are stated separately.
* **A stolen or removed person's device of A holding `linkKey`.** Its removal from A's roster
  revokes every link it holds the key for. A's hosts stop co-signing and each sends a tombstone
  under its own `hostSign`, which B verifies against the hosts it confirmed (§7.3). Before removal,
  a thief holding the key and passing its presence check can re-sign statements, but can't add a
  host, change a listed host's keys or rotate the key without B's person (§7.3). After removal a
  thief holding **only** the key can't renew freshness, because no attester co-signs, so the link
  ends within 24 h even if no tombstone arrives. A thief who also holds an attester's keys, or the
  whole machine of a `singleMachine` link, renews until a tombstone from another host or a
  revoker arrives, or the link expires (§7.3, "The residual"). That is why a revoker off the
  custodian's machine is required.
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
| **Corporate data** | **No organisation-policy detection exists yet.** design.md:1338-1343 ("Corporate policy (S8)") is a parked question, not a mechanism, and §5.3 notes there is no effective-policy signal. So on a managed machine **the person's consent is the only control** (owner decision, Q51). When S8 produces a detection, a policy actually configured or enforced on the machine will win: links refused, and the policy named. A personal consent button never overrides an organisation-enforced restriction |

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
admission. F3: host enforcement, projection and taint, the **status** and **content** grants and typed `session.status` (§7.4, §5.8), `notDelivered`, revocation acknowledgements, including `allowAllRead` and the link's `expose.gate`
(§5.7, after AP8). F4: app and PWA link pages and the external marker, with the allow-all opt-ins
and their warnings (§7.11.5). F5: spaces as named sets of links and presets (optional), with no future members and no transitivity, built **after** F7. F6: repo discovery (§7.11):
SSH-signed descriptors, the members-only directory, membership freshness and the suggestion UI.
F6 depends on F1-F4, because it produces F1 links, and on the **directory store** (§7.11.10): the
`directory` container in design.md §5.8's Cosmos DB account, the `directory-store.bicep` stamp
module with managed-identity RBAC and no keys, and the Key Vault secret and data key. F7: the
**person view** (§7.12): the proxied `kind: "person"` read across a link, `collaborator.removed`,
`Session.external`, and the app's person dimension and "Involving external agents" filter. It
depends on F3 and F4. Its exit vectors: a revoked, suspended or expired link hides that person's
sessions at once; no reply, answer, approve, launch, stop or transfer control is shown, and each
is refused by both hosts; a collaborator's session never enters Needs-you, notifications, badges
or counts; a removed person device can't read through its host.

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
| **(D4)** A **members-only directory on a relay**, keyed by the repo's immutable id: opted-in members' signed descriptors, visible only to callers who prove push access to that repo, stored blinded and encrypted (§7.11.10) | Key, and an endpoint sealed per accepted peer, for fellow members only | Members. **No GitHub token is ever sent to a directory** other than the owner's own sign-in relay (below) | Only the directory relay's operator and fellow members learn who uses the bridge on R, and where | Yes, with a **rendezvous relay** (below) | **Recommended**, for endpoints |

**Recommended discovery: D2 for candidates, plus D4 for keys and sealed endpoints.** The apps
narrow suggestions to people active in the repository (§7.11.4, Q22); the directory gates on
write access only.

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
   gets **no directory**, and its members use invitations (§7.3). There is no fallback in which
   any GitHub user can list entries.
   * **The installation is Metadata-only**, and the directory gates on **write access only**.
     Checking activity would need Contents and Pull requests read (commits, `history`, reviews,
     private pull-request search), and that would let the relay operator read every
     participating private repository's source, contradicting design.md §5.1. So the activity
     filter (§7.11.4) runs **in the apps only**, with each person's own token.
   * **The privacy cost:** a dormant writer can still list the repo's opted-in entries. That is a
     privacy cost only, never authority: a listing is not a suggestion and not a link, and every
     link still needs both people's consent (§7.11.6, §7.11.7).
4. **Registration and query.** With identity and membership proven, the app publishes its owner's
   signed descriptor, stored under blinded keys derived from `(repo id, user id)` and encrypted
   (§7.11.10), kept for at most 30 days, and reads the
   repo's entries. It then intersects them with its own step 1 candidates. An entry whose user
   isn't a current collaborator in the app's **own** GitHub check, or isn't **active** in the
   app's own activity check (§7.11.4), is ignored. Every descriptor's T2 proof is verified by the
   app, never trusted from the directory.
5. **The rendezvous relay** is the owner's **own** sign-in relay by default. When both owners use
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
6. The directory is **untrusted for authenticity**. It can hide entries (denial of service), but
   can't forge one: §7.11.3's proof is checked by the apps, never by the directory.

#### 7.11.3 Identity: proving "this endpoint and key belong to GitHub user X"

The **repo descriptor** is §7.3's link descriptor plus:

```json
{
  "github": { "userId": 1234567 },
  "repo":   { "id": 987654321 },
  "sealKey": "<P-256 ECDH public key, for endpoints sealed to this person>",
  "proof":  { "kind": "ssh", "sig": "-----BEGIN SSH SIGNATURE-----…" }
}
```

**No names** (owner decision, §7.11.10): no login, display name, email or repository name. Apps
resolve the login from the numeric user id with `GET /user/{id}` **at display time**, and a
suggestion shows what GitHub returns now; the repository's name comes from the app's own
workspace or the API. The descriptor in the directory also carries no `relay` by default: the
endpoint travels sealed to `sealKey` after both people accept (§7.11.7).

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
  anyone whose PR was merged once. Write access stays **the only membership test for a link**.
* **Who is suggested** (owner decision, Q22): **write access now AND recent activity in that
  repository by BOTH people**, within a per-repo window (`activityWindowDays`, default **90**).
  Activity is a **suggestion filter only, never an authority grant**: it narrows who is shown and
  who can list the directory, and nothing else.
  * **Activity** is any of, in that repository, within the window: a commit on the default
    branch **authored or co-authored** by the user, a **pull request opened**, or a **pull request
    review submitted**.
  * **Both sides.** Each app checks the **other** person's activity with its own GitHub token, as
    with write access, and checks **its own owner's** too. While its own owner is dormant, the app
    withdraws the owner's directory entry and shows no suggestions for that repo, so a dormant
    member appears in nobody's list. Each app applies its own owner's window, so a suggestion
    shows on a side only when both people are active by that side's setting.
  * **Public repositories.** Read access, forks, stars and one-off past contributions never
    qualify: none of them is write access now. A fork has a **different repository id**, so its
    activity and its collaborators never match the upstream repo's.
  * **How the app checks**, cheapest first:
    1. **GraphQL** `user(login) { contributionsCollection(from, to) { commitContributionsByRepository,
       pullRequestContributionsByRepository, pullRequestReviewContributionsByRepository } }`,
       filtered to the repository id. One query per person covers all three kinds, and GitHub
       credits co-authored default-branch commits there. It costs about one point of GraphQL's
       5,000 points per hour. A user who hides private contributions shows only a
       `restrictedContributionsCount` without the repository, so for a private repository the
       answer may be "can't tell", and the app falls back to REST.
    2. **REST**, per repository: `GET /repos/{o}/{r}/commits?author={login}&since={start}&per_page=1`
       on the default branch; `GET /search/issues?q=repo:{o}/{r}+type:pr+author:{login}+created:>={start}`;
       and `...+reviewed-by:{login}+updated:>={start}`, confirmed by the review's `submitted_at`.
       Commits and reviews use the core limit (5,000 per hour); search is limited to **30
       requests per minute**. REST can't see co-authorship directly, so it counts a commit whose
       `Co-authored-by` trailer names the user's GitHub noreply address (`<id>+<login>@users.noreply.github.com`).
  * **Caching.** Results are cached per `(repo id, user id)` for 24 h, positive and negative,
    and refreshed by the daily re-check (below). Only people who are **already both** write
    collaborators and opted-in directory entries are checked, so an organization with hundreds
    of writers costs a handful of queries per repository per day. "Can't tell" counts as
    **not active** for suggestions, never the reverse.
  * **The directory doesn't check activity** (§7.11.2): its installation is Metadata-only, so the
    filter is the apps'. That needs **Contents** and **Pull requests** read on the person's
    selected repositories in the app's own token (Q38). Exhausting the search limit makes the
    answer "can't tell", which counts as not active: suggestions fail closed.
  * **Activity lapsing on an existing link** keeps the link: membership is unchanged, so nothing
    is suspended. It only stops new suggestions, and that user's app withdraws their directory
    entry.
    The directory still carries tombstones and re-checks for existing links whatever the activity
    (§7.3). The link shows "@alex: no activity in danswett/agent-bridge for 90 days" so the
    person can revoke it if they want.
* **Re-checked** at least every 24 h while the app runs, on every suggestion and acceptance, and
  before any statement version is signed. The **same check re-fetches the peer's GitHub signing
  keys** (`/users/{login}/ssh_signing_keys` by the pinned user id) and confirms the key behind
  the peer's T2 proof is still registered. A removed key suspends the link at once, as lost
  membership does. A changed key is a new identity (§7.11.3). **Residual:** a key the peer
  removes from GitHub after compromise stays trusted until this side's next check, at most 24 h
  plus the 72 h host confirmation window, unless the peer also sends a tombstone (§7.3).
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

* **Setup is a prerequisites wizard.** It checks the GitHub sign-in, the token's repository
  permissions (Q38), an SSH signing key on GitHub, and a second device or recovery key (§7.3),
  and fixes what it can with the person watching.
  * It **never registers an SSH signing key silently**: adding one to GitHub is a step the person
    confirms.
  * A phone-only person approves setup through their **paired desktop**, which holds the key; the
    private key is never copied to the phone.
  * Scope comes from **named presets** the person picks explicitly ("Status only",
    "Read and ask me", "Custom"). None is pre-selected.
* **Opt-in per repository** (Settings → Collaboration → "Find collaborators who use Agent Bridge
  on danswett/agent-bridge"). Nothing is registered or queried for a repo until its owner opts in.
  The same page sets the repo's **activity window** (default 90 days, §7.11.4).
* **A suggestion**, shown only inside the app (a quiet badge on the Collaboration page, never a
  push or toast), reads:

  > **@alex** also uses Agent Bridge on **danswett/agent-bridge**. ✓ Identity signed with
  > @alex's GitHub SSH key · write access and active in the last 90 days, confirmed today.
  > Connect? Agents in this repo's sessions could read and reply to each other.
  > [Review and connect] [Not now] [Never for this repo]

* **Declining is silent.** "Not now" and "Never for this repo" tell the other side nothing, and
  can't be told apart from not using Agent Bridge at all.
* **Accepting** opens §7.4's scope with the repo choices (below), shows the optional link code,
  and signs §7.3's link statement with presence. It starts with a **consequence preview**:
  "6 of your sessions in this repo run allow-all; @welshofer sees 0 of them. 3 sessions would be
  visible as status." The preview updates as the person ticks options, and nothing is ticked for
  them. **Nothing connects until both owners have
  accepted in their own apps.** The other side sees "@danswett accepted. Review and connect?",
  never an automatic connection.
* **Repo scope** (the choices in the dialog):
  * **Nothing is pre-ticked** (Q40), as in §7.4 and #391's "pre-ticks nothing". The person ticks
    **status** (the recommended preset) or **content**, with content's provider-retention
    warning (§7.4), and/or reply. Launch is a separate opt-in.
  * **Organisation policy.** **No detection exists yet** (S8 is still open), so on a managed
    machine the person decides, as on any other (Q51), and the dialog says so: "Agent Bridge
    can't check your organisation's policy. Make sure sharing is allowed." When S8 produces a
    detection, a policy actually configured or enforced will win: the link refused, with the
    policy named. The app never invents an organisational reason. "External sharing is disabled on this machine" appears only when the
    person turned federation off there. A personal consent button never overrides an
    organisation-enforced restriction.
  * **Read** covers @alex's agents **and @alex in their own app** (§7.12): "@alex and their
    agents can read these sessions."
  * **Allow-all sessions** (§5.7), two further opt-ins, unticked, each with its warning:
    * "**Let @alex's agents read my allow-all sessions** in these workspaces" (`allowAllRead`):
      "Their agents could read what those sessions say, including command output. Transcripts
      can contain secrets."
    * "**Ask me before each message** @alex's agents send to an allow-all session"
      (`expose.gate`): "Each one waits for your approval on your device. Approving makes it your
      own instruction to a session that runs commands without asking."

    Without the second, @alex's agents can't reply into those sessions at all.
  * **The workspace-to-repo mapping is pinned at acceptance** (Q41). The dialog lists the
    workspaces whose remote resolves to the repo id **now**, and the person confirms which ones
    the link covers. The link statement records them by workspace label and path.
    * Scope is **never derived per message** from live git settings: an agent can't widen it by
      editing a remote.
    * If a pinned workspace's remote changes, the link is **suspended** on this side (nothing in
      or out) until the person re-confirms the mapping with presence. A new workspace whose
      remote starts resolving to the repo is not added until the person adds it. Scope is never
      derived per message from live git settings.
  * **expose** only sessions whose working directory is in a pinned workspace, or a worktree the
    host made for one. Allow-all and unknown-mode sessions only through the two opt-ins above
    (§5.3, §5.7).
  * **reach** from the same set of sessions only, so an agent in another repo can't post to
    @alex at all.
  * The projection, taint and limits of §7.4-§7.5 apply unchanged.
* **Revocation:** on demand by either owner (one press, unilateral, §7.7). Automatically on lost
  membership (§7.11.4). Automatically on descriptor expiry (30 days, renewed by the app with a new
  SSH signature, within the link's renewal mandate, §7.4, so the key's continued registration on
  GitHub is rechecked). And on "Never for
  this repo".
* **Audit:** the link history (accepted, verified, suspended, revoked, each with its cause),
  GitHub's own membership record, and the per-message audit of §5.6.
* **Batch linking.** Several collaborators on one repo can be linked from a **fixed, visible list**:
  each is an exact pairwise grant that both people accept. **Spaces** (§7.10 F5) only group links
  and presets: no future members, no transitivity, and they come **after** the person view (F7).
* **The supported audience.** No birth dates are collected by default. Whether minors are a
  supported audience is an **owner decision before public launch** (Q51); GitHub's age terms
  alone aren't sufficient.

#### 7.11.6 Repository threats

| Event | Effect | Why |
|---|---|---|
| **Repo write access compromised**, or a fork PR's branch read from a local checkout | An attacker rewrites `.agent-bridge/rendezvous` to point at **their** relay. Had apps sent GitHub bearer tokens to the directory, they would have leaked them before any approval. That is why no token is ever sent there (§7.11.2). The pointer is read only from the default branch through the API, and the origin is pinned with the owner's confirmation, so a change is a visible prompt. What remains: an owner who accepts the new origin gives that relay their T2 directory signatures (audience-bound, useless elsewhere) and their descriptor (endpoint and per-repo link key), and the relay learns who opted in. They **can't** create a descriptor for another user | T2 needs that user's registered signing key. Directory signatures are bound to the audience and the repo (§7.11.2) |
| **An agent edits a workspace's git remote** to widen a link's scope | No widening: the scope is the workspaces pinned at acceptance. A remote change **suspends** the link until the person re-confirms | §7.11.5 |
| A compromised maintainer **adds an attacker as a collaborator** | The attacker becomes a *suggestion* | Consent is the gate. The suggestion names the account, and accepting is never automatic. Default scope is the repo only, launch off |
| **Fork** | No effect | Descriptors and directory entries bind the **immutable repo id**. A fork has a different id, and its owner isn't a collaborator upstream. Renames and transfers keep the id |
| **A contributor leaves** | Suspended within 24 h (their side's check) and 72 h at the latest (host freshness) | §7.11.4 |
| **A dormant member with write access** (one of hundreds of org writers who never touch the repo) | Isn't **suggested**: the apps' activity filter excludes them (§7.11.4). **Can still list** the directory's entries, since the directory gates on write access only (§7.11.2). Their own entry is withdrawn by their app while they're dormant | A privacy cost only: a listing is neither a suggestion nor a link, entries carry no names, and endpoints are sealed by default (§7.11.7) |
| **Read access, a fork, a star, or a one-off past contribution** to a public repo | Never qualifies | None is write access now, and a fork's repository id never matches (§7.11.4) |
| **Faked activity**: a writer pushes a commit whose author or `Co-authored-by` names a dormant member, or opens a PR to look active | At most a **suggestion**, or a directory listing for a writer who could have been active anyway | Activity is a filter, never authority. Write access and both owners' consent still gate every link |
| **Activity lapses on an existing link** | The link stays, and no new suggestions are shown | Membership is unchanged (§7.11.4) |
| **@alex's GitHub account compromised** | A new key means a new identity, so a re-verify prompt, never silent. New links with @alex are possible for the attacker | The T2 residual. T4 per link mitigates it |
| **A login renamed or recycled** | No effect | Bound to the numeric user id |
| **The directory relay lies** | It can hide entries | It can't forge them (T2 is verified by the apps) |
| **Storage-level access** (Cosmos DB, a backup, or someone with data-plane access but not Key Vault) | Blinded keys, ciphertext, sizes and timestamps. No repo, user, key, endpoint or name | Keyed HMAC indexes and AEAD at rest (§7.11.10) |
| **The directory secret leaks** | With storage access, keys become **linkable**: an attacker can test candidate repo ids and user ids, which are small integers, and learn who registered on which repo. Entries stay encrypted unless the data key leaks too, and stay **signed and unforgeable** either way | Rotate the secret (dual-read, re-key) and the data key (§7.11.10) |
| **The data key leaks too** | Descriptors are readable: user ids, per-repo keys, repo ids. No names, and sealed endpoints stay sealed to their peers | Rotate; the 30-day TTL bounds what an old copy holds |
| **A stale region replays an old descriptor** | Loses: newest signed `issuedAt`, then hash, wins, and a withdrawal is a signed tombstone that outlives it | §7.11.10 item 4 |
| **A person wants their data gone** | Withdrawal hides it at once; on-demand removal hard-deletes it in every region; backups age out within 7 days | §7.11.10 item 6 |
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
days. They carry **no names** (§7.11.3), and link keys are per-repo, so they don't link across
repos. A plain hash of the repo id wouldn't help, since repo ids are small sequential integers
anyone can enumerate; the directory instead indexes by a **keyed** HMAC under a secret it holds
(§7.11.10), which hides ids from anyone who reads its storage without that secret, but not from
the operator. What the **directory operator** can learn is that GitHub user id X uses the
bridge on repo R. For the production relay, that is the project's operator. A team that won't
accept that uses the invitation path (§7.3).

**Endpoints are sealed by default** (owner decision, §7.11.10). An entry carries the descriptor
**without `relay`**. When both people have accepted, each app leaves its endpoint in the
directory **sealed to that one peer** (ECDH to the peer's `sealKey`, AEAD, bound to the link id),
so neither the operator, storage nor other members ever see it. When both use the same relay,
nothing needs sealing at all. Publishing an **unsealed** endpoint, readable by every member who
can list the directory, is an explicit opt-in ("show my relay to everyone with write access to
this repo"), with the warning that a self-hosted relay's address may be a home network.

**Large organizations.** The directory gates on write access only (§7.11.2), so in a large
organization's repository **every** writer, including hundreds of dormant ones, can list every
opted-in member's entry: the numeric user id, keys and expiry, and an unsealed endpoint only for
those who opted into one. The activity filter only decides whom the apps **suggest**, not who can
see an entry. The opt-in dialog says so, and shows the number of people who **can actually see
the entry**, the write-access count ("visible to the 214 people with write access to
contoso/platform; Agent Bridge suggests only those active there in the last 90 days").
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
* **Establishment** is F4 discovery when it can verify (a GitHub repo, push access and recent
  activity on both sides, the directory's GitHub App installed, and T2 available). Otherwise it is a **repo-scoped invitation**: F1 with the link code, naming
  the repository by its normalized remote URL (`dev.azure.com/…`, `gitlab.com/…`). For those, the
  membership check is unavailable, so revocation is manual, and the dialog says so.
* If the owner prefers a single *mechanism*, it is F4 only. That excludes Azure DevOps
  collaborations until an equivalent membership and key check exists there (Q19).

#### 7.11.9 How it fits the roster, principals and envelope

Nothing in §4's envelope or §7.5's verification changes, except that two checks are added and the
envelope's `target.repo` and the guest channel's bound `repo` must equal `repo.id` (§7.3, "Link
context binding"). The chain B's host walks for an agent command from A's host:

1. Envelope `sig` → A host's `hostSign` key, listed in **A's peer host statement**, signed by A's
   `linkKey` (§7.3).
2. A's `linkKey` → **A's repo descriptor**, whose T2 proof verifies under an SSH signing key that
   GitHub lists for `github.userId` (checked by B's app at acceptance and each renewal, and
   recorded in B's link statement), and re-checked daily (§7.11.4).
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


#### 7.11.10 Directory storage (owner decision)

**Owner decision** (coordinator, 2026-11-03, 20:10 PT, Q48). Where and how D4 entries are kept.

1. **No names.**
   * A stored entry carries **no login, display name, email or repository name** (§7.11.3).
     Apps resolve a login from the numeric user id at display time.
   * The **numeric GitHub user id** stays, only **inside the signed descriptor**. It can't be
     dropped: it is what a peer resolves GitHub's signing keys from to check T2 (§7.11.3), and it
     equals the relay account id (`RelayApp.cs:199`), which the envelope's `account` binds.
   * It is **pseudonymous personal data**: anyone can turn it into a login through GitHub's
     public API. So it is handled as personal data: the entry is **encrypted at rest** with an
     application key (AEAD under a Key Vault key, `directory-data`, with the entry key as
     associated data) on top of Cosmos DB's own encryption, and the plaintext id is **never an
     index, a log field or a metric dimension**.
2. **Blinded indexing.**
   * The partition key is `HMAC(directorySecret, repoId)` and the item id is
     `HMAC(directorySecret, repoId ‖ userId)`, both over fixed-width encodings.
   * `directorySecret` is a **versioned Key Vault secret**, shared by every stamp. Each entry
     records its key version (`kid`).
   * **Rotation** has three steps:
     1. publish version *k+1*;
     2. **dual-read**: writes use *k+1*, and reads query both *k* and *k+1* and merge by the
        newest-wins rule below, while a background job re-keys each entry (decrypt, recompute
        both keys, write under *k+1*, delete the *k* item);
     3. retire *k* once no item uses it. The 30-day TTL bounds how long that can take.
   * Raw repo ids and user ids **never appear in keys, logs or metrics**. A log line carries the
     blinded key or nothing.
3. **Endpoints sealed by default** (§7.11.7). An entry has no `relay`. After both people accept,
   each app leaves its endpoint sealed to the peer's `sealKey`. An unsealed endpoint is the
   opt-in.
4. **One directory for every region stamp.**
   * **Azure Cosmos DB (NoSQL)**, as design.md §5.8 already specifies for the account directory
     (design.md:605, multi-region writes): one container, `directory`, in that account, shared by
     every stamp now and as regions are added, partitioned on `/pk` (item 2). A new region joins
     by adding its location to the account, with no data move.
   * **The writer must be the caller.** The directory accepts a registration, renewal or
     withdrawal only if the descriptor's `github.userId` equals the caller's user id proven by
     step 2's T2 challenge (§7.11.2), and the repository id equals the one proven by step 3's
     App check.
   * **Expiry and withdrawal.** Per-item `ttl` is the descriptor's `expiresAt` minus now (30 days
     at most). Withdrawal writes a **signed tombstone** (signed by the descriptor's `linkKey`, with
     a newer `issuedAt`), whose own TTL is 31 days, longer than any descriptor it replaces.
   * **Conflicts.** Cosmos's last-writer-wins policy on a **custom path**, `/order` =
     `issuedAt × 2^20 + the first 20 bits of the descriptor hash` (well inside 2^53), so the
     **newest signed descriptor wins, then the higher hash**. It is never last writer by region
     clock. Every write is also conditional (on the etag) and refused unless its `issuedAt` is
     newer than the stored item's and at most 120 s in the directory's future. So a stale region
     can't resurrect a withdrawn entry: its older descriptor loses to the tombstone everywhere.
   * **Consistency.** **Session** consistency (strong isn't available with multi-region writes).
     After registration, the registering request's session token gives **read-your-writes** in
     that region, so the app's immediate re-read sees its own entry. A member reading through
     another region sees it once replicated, typically within seconds. That is acceptable,
     because suggestions are not time-critical, entries are self-authenticating, and apps
     re-check daily (§7.11.4).
   * **Not the roster's pattern.** Today the relay keeps rosters per region in Blob storage
     (`AzureRosterStore`, `Roster.cs:121`), and a second region is a separate relay URL
     (relay-deploy.md:358-363). A per-stamp directory would hide members on one stamp from
     members on another, so the directory must not follow that pattern.
   * **Infrastructure.**
     * **The shared Cosmos DB account is managed in one place**: a global Bicep module,
       `directory-account.bicep`, deployed once, owns the account, its `locations` list, the
       database and the container. Adding a region is one edit to that list.
     * Each stamp's `directory-store.bicep`, beside `roster-store.bicep` and `blob-store.bicep`,
       only **references** the existing account and assigns its own identity's roles. It never
       sets `locations`, so two stamps can't fight over the account's regions.
     * The stamp's managed identity gets the Cosmos DB Built-in Data Contributor role, scoped to
       that container, with `disableLocalAuth: true`: **no keys**.
     * Key Vault access is `get` on `directory-secret` and `unwrapKey` on `directory-data`.
   * **Self-hosted relays** may use a single-node store (an embedded database or a file) with the
     same blinded keys, AEAD, TTL sweep and newest-wins rule, and no replication.
5. **Who sees what.**
   * The **relay operator** sees a caller's repo id and user id while it checks the GitHub App
     and T2, and can decrypt entries, because it serves them to members. It doesn't log either id.
   * **Cosmos DB, or anyone reading storage or a backup without Key Vault access**, sees blinded
     keys, ciphertext, sizes, timestamps and TTLs. From those it can count entries per blinded
     repository, but learns no repo, user, key or endpoint.
   * Sealed endpoints are readable only by the peer they were sealed to.
6. **Retention and deletion.**
   * **Withdrawal** (turning off discovery for a repo, "Never for this repo", lost membership or
     dormancy) writes the signed tombstone at once, which hides the entry everywhere; the TTL then
     removes both.
   * **On demand**, "Remove me from collaborator discovery" in the app signs a withdrawal for
     every repository the person ever registered.
   * **Where the repository list comes from.** Entries are keyed only by HMACs of
     `(repo, user)`, so the directory can't find a user's entries without the repository ids. It
     therefore keeps one more item per user: the **user index**, keyed by
     `HMAC(directorySecret, "user" ‖ userId)` and encrypted like the entries. It holds the
     blinded entry keys that user has registered, and is updated on every registration and
     withdrawal. A person who has lost their keys signs in to the relay with GitHub, and the
     directory reads that index and deletes every entry it lists. Either way, the directory **hard-deletes** the entries and their
     tombstones once the tombstones have replicated to every region.
   * **Backups.** Cosmos DB's backup keeps deleted entries for its retention window, set to
     **7 days** of continuous backup for this container, and no other copy exists. A restore is
     for disaster recovery only, and is followed by re-applying every tombstone and deletion
     recorded since the restore point.
   * Nothing about an entry reaches logs or metrics beyond blinded keys and counts (item 2).
   * **No names is hygiene, not a boundary** (§5). The numeric id is still personal data, and the
     operator can still resolve it.


### 7.12 Collaborators in the person's own app (owner decision)

**Owner decision** (coordinator, 2026-11-03, Q49). The **person**, not only their agents, sees a
linked collaborator's exposed sessions in their own app, read-only, with a person filter beside
machines, and can filter their own sessions to those involving external agents.

**What the person sees.**
* Under a link where the other side granted **read** (§7.4), the person's app (the PWA and the
  Windows app) lists that collaborator's **exposed** sessions: those in the
  pinned workspaces, never allow-all or unknown ones unless the other side ticked `allowAllRead`
  (§5.7 R). The person can open the transcript view.
* Under a **status** grant the person sees typed status only (§5.8). **Titles** appear only if the
  exposing owner also grants the **person-view display projection**, which is separate from both
  status and content (§7.4).
* Content is **exactly** what an agent's read under that link gets: §7.4's projection, §7.5's
  rates, and the same exclusions. The exposing side audits it like an agent read.
* **No reply, answer, approve, launch, stop or transfer controls** on a collaborator's session,
  ever, in v1. The host refuses those actions too: they aren't in the person-across-link action
  set. A person replying into a collaborator's session would be a separate, future grant
  (deferred, Q49).
* The exposing side's link dialog says so: "**@alex and their agents** can read these sessions"
  (§7.11.5).

**Which principal reads: proxied through the reader's own listed host.** Two choices:
* **(a) The person's device as a direct guest.** The link statement would list the reading
  side's person devices, and B's hosts would admit and verify foreign person devices. That means
  a new key list in every statement, more disclosure (B learns A's devices), and a second
  revocation path for devices B can't check against A's roster.
* **(b) Proxied by one of the reader's own listed hosts** (recommended, chosen). A's person device
  asks one of **A's hosts listed for this link**, over A's own account channel, which A's roster
  authenticates. That host makes the read across the link with the principal
  `{ kind: "person", account: A, device: <add hash of the person device> }` in a §4.3 envelope it
  signs with its `hostSign`. Presence isn't needed for reads.

Why (b):
* **Verification is the same as for agent reads.** B verifies A's listed host exactly as for an
  agent's command (§7.5): channel, link binding, signature, fresh co-signed statement.
* **Revocation is the same.** A tombstone, a suspension or an expired statement stops person
  reads with the agent reads.
* **On A's side,** the host proxies only for a device that is **active in A's roster**, so
  removing that device stops it at once.
* **Nothing new to disclose.** B never learns A's devices beyond an opaque add hash in its audit,
  and never needs a foreign device key.

**Person-kind only.**
* A's host resolves the **active person-kind add** from the authenticated caller on its own
  channel. It never takes a device id from the request, and refuses a host, agent or connector
  caller.
* Home Assistant and other connectors get **no** access to a collaborator's content through
  this. That would need a separate grant from the **exposing** owner.
* Proxied results **never enter the host's own session inventory** that is served to agents,
  the local pipe or connectors. They go to the requesting person device only.

**The action set across the link** for `kind: "person"` is `sessions.status`, `session.status`,
`sessions.list`, `session.get`,
`session.read` and `sessions.subscribe`, with the subscription relayed by A's host to the device.
B's hosts refuse any other action from a person principal, and A's host refuses to proxy one.
B caps live subscriptions per link (4 by default) and charges reads to the link's rates.

**Filters and UI.**
* **A person dimension.** The session list gains **Mine**, then each linked collaborator by
  **verified login**: resolved from the T2-verified user id at display time (§7.11.3), or the name
  set locally at link time. Machines nest under each person.
* **Never mixed in.** Collaborators' sessions sit in their own section with the outlined
  diamond and the word "**External**" (§6.2), never colour alone (ui.md's accessibility rule).
  They are **excluded** from Needs-you, notifications, badges and counts, so another person's
  questions never page you.
* **"Involving external agents."** A filter on your **own** sessions: any external principal
  **sent, read or was refused** there within a window (1 h, 24 h or 7 days). The host derives it
  from its audit log (§5.6) and reports it to the account's own clients only, as an additive
  `Session.external: { lastAt, kinds: ["sent", "read", "refused"] }`.
* **Revocation hides at once.** On a tombstone, suspension, expiry or a lost read grant, A's host
  ends the proxied subscriptions and tells the device `collaborator.removed { link }`. The app
  drops that person's sessions from view immediately, cached transcripts included.

**Follow-ups for the UI owner** (the Agent Bridge UI session). This note doesn't change other
documents:
* **ui.md:** the person dimension and grouping; the external section's outline treatment and
  marker; the exclusion from Needs-you, badges and counts; and the "Involving external agents"
  filter.
* **design.md:** the client views list (a read-only collaborator view), and `Session.external`.

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
| Restart and restore | Hash records bound to pid and start time. A session's process can outlive the host, so a store-only restore can leave a restored record matching a live process: the conversation is then `unknown:<machine>` (§8.4), but the restore also undoes an owner's `agents revoke` or narrowed scope while that process runs, a residual of same-user store access (§1.3) | Host store fails closed when unreadable. A restore needs an owner trust reset and re-approval: no automatic reactivation of old grants (#394; #407's store generations) | Statement versions are monotone, and an older version is refused | #391 §6.3 freshness, §7.2 replay floor |
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
version mark for links. Otherwise it waits for an owner reset.

**Taint** (§5.8) follows the same rule in the other direction, and can't rely on the store's own
high-water mark, which rolls back with it (host-config.md:228-231). Persisted taint only ever adds
taint. Cleanliness is vouched for only by the running host instance: every conversation that
predates it carries `unknown:<machine>` permanently, the machine-wide state starts as
`unknown:<machine>`, and clears bind the instance id. A **restored agent credential** is not safe
by construction: its bound process can outlive the host, so a store-only restore leaves a live
process with a matching credential. That is why the instance rule taints the conversation, not
the credential.

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
| `F("agent-bridge link-attest v1", statement)`, by an attester: a host's `hostSign` off the custodian's machine, or a revoker device's key | An attester's co-signature of its side's peer host statement (§7.3) | An entry, cosign or person command, adding a host, or rotating `linkKey`. It only keeps an already-confirmed statement fresh |
| `F("agent-bridge link tombstone v1", link, repo, at)`, by `linkKey`, a confirmed host's `hostSign` or a pinned `revokers` key | Ending a link permanently (§7.3) | Anything else. A `revokers` key verifies nothing but this and the link-attest co-signature |
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
  account and credential administration, `agent.command`, `agent.chainLink`, and every agent op of §4.
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
kind, cross-machine agent messaging (AP5), and federation (F1-F7, which need person commands for
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
| **V7** | **Cross-account.** Entries bind `account` and `generation`. A host trusts only a roster it was enrolled into, and `roster.sync` and the relay serve only the same account's approved devices. So a foreign host can't fetch, verify or stay fresh on another account's roster | Depends on the model (below). Under **(A)**: none. Under **(B)**: a pinned foreign genesis fingerprint, a read-only foreign-roster path, cross-account head attestation and revocation propagation | Principals keyed by **account + generation + add hash** (and the full `hostSign` key) everywhere, since device ids aren't namespaced and copies share ids (§4.3) | F1-F7 |

**V7, the two cross-account verification models.**

| | **(A) Statement-based** (§7.3, recommended) | **(B) Foreign roster** (the review's path) |
|---|---|---|
| How B verifies A's hosts | A's **peer host statement** lists the exposed hosts by full `dh` and `hostSign` key (with their add hashes), signed by A's per-link key. That key is pinned by the link code (§7.3) or by repo discovery's GitHub-key proof (§7.11.3). A's own client checks the statement against A's roster before signing it | B pins A's **genesis fingerprint**, exchanged out of band (it fits repo discovery, §7.11: the descriptor already carries `genesis` and `generation`, under the T2 proof). It then fetches and verifies A's roster through a new **read-only foreign-roster path** |
| Revocation propagation | A's client re-signs the statement on any removal or displacement (automatically, with presence). B requires a statement signed by `linkKey` **and co-signed by an A attester** off the custodian's machine within 24 h (§7.5), and honours a tombstone from `linkKey`, any confirmed A host or a pinned revoker at once (§7.3) | A's removals reach B through roster sync. That needs cross-account head attestation and freshness |
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
| **Never in any profile** | `session.answer` on any approval; `launch.trust.answer`; `host.*`; roster, account and credential administration; `pairing.*`; `update.*`; `push.*`; `session.transfer`; `agent.command`; `agent.chainLink` | Enforced by the host's classification test (§9.4) |

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
  internal-only, or never-for-connectors. `agent.command` and `agent.chainLink` are
  internal-only. A new op that isn't classified fails the build, in the
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
| **A prompt-injected agent with tools only** (MCP and file tools in an `ask` session, no unattended shell) | Holds its own credential and follows hostile instructions | Answer an approval or folder trust anywhere. Launch with allow-all, or without an explicit `ask`. Reply into an allow-all, unknown-mode or working session (§5.3), unless the target's owner approves that exact message or enabled own-account allow-all targets (§5.7). Have its words or Enter land in a prompt that opens mid-delivery (§5.2's guard). Stop, transfer, update, change settings, adopt, or widen its own scope. Reach excluded sessions. Exceed depth, budget or rates. Appear as the person | Read and reply to sessions the policy allows, and launch `ask` sessions in approved workspaces, within budgets. Each act is attributed, audited and shown violet. That is the owner's chosen reach (decision 2). If the owner enabled `allowAllTargets` (§5.7 O): get commands run without a prompt through an allow-all session in that scope, the risk the setting's warning states |
| **A prompt-injected agent with a shell** (it can run commands, after the person approved them or because its own session is allow-all) | All of the above, plus it runs processes as the user | Make another session's agent act through the bridge without a reply that passes §5.2 and §5.3 at the target | Everything the user can, locally: it is same-user code (§1.3, local-pipe.md:121-130). That includes using another session's token if it can read it, by spoofing its parent or joining that session's Job (§3.4: the ancestry check is a safety rail, not a boundary). Its subprocesses inherit its token (§3.4) |
| **Another session's agent, or a detached helper**, on the same machine, holding a stolen token (from a readable environment, or a transcript that printed it) | Same OS user | Use it casually: on Windows the token is accepted only from a process inside the bound session's ancestry or Job object, read in full and failing closed, and `AGENT_BRIDGE_SESSION` proves nothing (§3.4). Outlive the session: the token dies with its process | On macOS and Linux nothing, because there are no agent credentials until peer identification exists (§3.4). On Windows, a helper that deliberately spoofs its parent (`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`) or joins the session's Job passes the rail: that is a **safety rail, not a boundary**, and such code is same-user malware (below) |
| **A stolen person's device** (unlocked, with its presence check passable) | Holds `sign`, maybe a `linkKey` | Beyond #391 §2's bounds: add or re-key hosts on a federation link, or rotate its key, without the peer person's confirmation (§7.3). Renew a link after its removal, holding only that device: the attesters stop co-signing (§7.3) | Until removed: what #391 allows a person's device, including drive sessions (#391 §7.3), and re-sign statements for links it holds the key for. Removal revokes those links, and each host sends a tombstone under its own `hostSign` (§7.3, §7.7). If the device is also a listed host or attester (the single-laptop setup), it renews its links until a tombstone from the required separate revoker arrives, or they expire |
| **A stolen host** (the machine, or its keys) | Holds `dh`, `hostSign` and the account token | Act as another machine. Pass person-only checks. Reach targets whose policy excludes it | Until removed and the removal is fresh at targets (1 h, V5): send agent commands as its own sessions, within targets' policy. Partition residual as for a removed host |
| **A manually started or adopted session** | A manual one has no credential | A manual one: act as an agent anywhere (decision 3). Either: be a reply target, since its mode is unknown (§5.3) | A manual one: read typed status locally, and nothing else (§3.4) |
| **A configuration change in a target** (a hook, skill, settings file, `.claude/` directory or plugin added mid-session, or `/skills reload`) | Same-user code, or the target's own agent where its mode lets it write | Keep the session an unattended reply target: the directory watches make it `unknown` before the next delivery (§5.3) | Add authority to a turn an agent's message already started: the host marks it `unknown`, audits it and offers the person **Stop** (§5.3 residuals). Win the race between the host's launch check and the CLI's load (same-user) |
| **An unattributed same-user process** (HTTP MCP, a script, a detached helper) | The user's rights on the pipe | Act as the person: reply, launch, answer, stop or attach. Unattributed callers get typed status and no acting ops (§3.4). Read content and stay untainted | Read typed status. Imitate the app: the §1.3 same-user residual |
| **Taint across machines** (a clean coordinator on M1 reads a tainted session on M2, or a coordinator is tainted after its window opened) | Its host's signed channel | Stay clean: content results carry signed origins that the issuing host merges into the reader before handing them over. Keep using a window or O: commands carry signed provenance, and missing or legacy provenance fails closed for O and windows (§4.3, §4.6) | Use per-message G, with "origins unknown" shown, against a peer without `agent.provenance` |
| **A member of a standing coordination group** (§5.7 C), prompt-injected through an instruction file or issue comment it read (the residual the owner accepted, H2) | A clean member, holding its own credential, inside an approved group | Send outside the group without G. Approve, trust folders, answer forms, stop, launch or transfer. Act for another account, a connector or HA. Keep sending after taint, a restart, expiry or a revoke (M4) | Get commands run without asking in the group's allow-all members, within the budgets, until the group ends or is revoked: the risk statement the owner approved (H3) |
| **Titles through `launch.options`** | A tainted or external launch that set an injected title | Reach a clean coordinator: agents and unattributed callers get `launch.options` as handles and typed fields, never titles (§5.2) | — |
| **A delayed clearance** (a tainted session starts, writes and exits between the person's review and the clear) | Same-user code, or an agent of the owner's | Have the clear erase it: the clear binds the unit's contamination generation and is applied by an atomic compare under the propagation lock (§5.8) | Wait for the person to clear without checking |
| **Git running a program in a host-made worktree or a gated session** (a `core.fsmonitor`, `remote.<name>.uploadpack` or `core.sshCommand` planted before an agent's launch request and run by the host's reclaim, fetch or hold; a finished worktree's `.git` redirected to an administration directory with its own `config.worktree` filter; a config line Git reads but a naive parser misses; a submodule's own `ext::` remote reached by a recursive fetch, a global or include-defined filter on a Git too old to honour `GIT_CONFIG_GLOBAL`, a hook dropped into the host's hooks directory, a hook in the common directory, an include, `core.fsmonitor`, a filter or LFS transfer agent, a helper or its SSH or gpg configuration edited after approval, an `insteadOf` rewrite to `ext::`, an inherited `GIT_SSH_COMMAND` or `GIT_ASKPASS`, Git's default editor or pager, a main checkout's untracked `settings.local.json`) | Same-user code, or an agent with write access to the repository | Run during an approved launch, or during any host Git command for a launch (repository resolution, reclaiming, fetch, base resolution, `worktree add`, lock), unseen: for an agent-requested launch the host parses the repository configuration itself, in a strict subset of Git's syntax cross-checked by `git config --file`, and refuses non-allowlisted keys before any other Git command, and fetches only under the closed configuration with `--no-recurse-submodules`, never running Git inside a submodule; reclaiming, for every launch, runs Git only in worktrees whose `.git` and administration directory point at each other and whose `config.worktree` passes the allowlist, with explicit `GIT_DIR`, `GIT_COMMON_DIR` and `GIT_WORK_TREE`; Git runs under a **closed, host-generated configuration**, for every worktree the host makes and in every gated session, at Git 2.32 or later and a maintained release, and self-verified with `git config --list --show-scope --show-origin` before use; the host's artifacts are generated per launch, hashed and rechecked (no system or global configuration; program-naming entries only from the host's file, by absolute protected path; a frozen, translated ssh config with no program directives; `protocol.allow=never` but `https` and `ssh`, with per-protocol `never` pins and submodule recursion pinned off; editor `:` and pager `cat`); repository configuration may hold only allowlisted non-program keys for any agent-requested launch, hashed and rechecked; the environment is rebuilt from an allowlist; `worktree add` skips LFS smudge (§5.7 G) | Reduced, not removed: an allow-all session can undo the configuration or environment itself; repository configuration can change **after** launch; same-user code can race the check-then-use window; a process that rebuilds its environment, or a Git client outside the session, doesn't get it |
| **Taint lost on restart or rolled back** (a host update, a crash, a restored backup of the store while a session's process lives on, a lost store followed by a person's clear) | Same-user code, or an ordinary update | Come back clean: persisted taint only adds taint; every conversation that predates the running host instance, or that the store doesn't know, carries `unknown:<machine>` permanently; the machine-wide state starts as `unknown:<machine>` at every start; clears bind the instance id and never lift a session's own origins (§5.8, §8.4) | O stops after every host restart until unconfined sessions end, the machine is cleared, and the coordinator and targets are then started fresh; open windows pause for re-approval (§5.8, Q53). Same-user code holding the host's keys can forge any local state (§1.3) |
| **`unknown` provenance laundered** (a legacy or AP5-era host's message reaching a forced-ask target that then uses O) | A peer without `agent.provenance`, or one sending `"unknown"` | Leave the target clean: `unknown:<machine>` is a sticky origin, O needs `origins == []`, and only AP8 hosts may send explicit sets (§4.3) | Be delivered through per-message G, labelled "origins unknown" |
| **One repository as two units** (a junction, symlink, `subst`, `\\?\` path, case or 8.3 name) | Same-user code, or an agent with a shell | Split a tainted unit from its clean twin: units are identified by file identity, network paths are excluded from O, and an unconfined tainted session taints the whole machine, units first seen later included (§5.8) | — |
| **A pending wait outliving its grant** (a collaborator's `session.read` waiting when the owner narrows to status-only) | A reader with a wait in flight | Receive content after the narrowing: waits are bound to the read grant's generation, cancelled on narrowing, and rechecked before each delayed result (§4.6) | — |
| **Same-user malware** | Reads keys, environments and stores | | Everything the user can, as today (§1.3). The audit log and signatures make its agent-attributed acts visible, not impossible |
| **A compromised host** | Holds its `dh` and `hostSign` keys | Act as another machine's agents (it can't sign as their hosts). Pass person-only checks (#391 §7.2). Reach a machine whose policy excludes it (`fromMachines`) | Sign as any principal *on itself*, invent sessions, and lie about chains under 4.2(b). Bounded by targets' per-origin rates and policy, and visible in their audit. Removed via #391. Across machines in one account, its `provenance` is trusted, so it can claim a clean, `oEligible` sender and use **remote O** into targets that allow it (§4.3) |
| **A removed host** | Keys still held | Be accepted by any host that has applied the removal, or by a target without a head attestation from another device in the last hour (§8.10, V5) | Inside a relay-maintained partition, unbounded (#391 §6.3). Shown as machines that never confirm the removal |
| **A hostile approver senior to a host** (#391 §3.3) | Can displace the host's enrollment with its own descriptor | Impersonate that machine's agents. A displacing copy lacks the machine's `dh` private key, so it can't carry envelopes (§8.10, V4) | Deny service to the genuine machine, and be announced |
| **A newly enrolled host** | Approved, within 72 h | Send cross-machine agent commands during its probation, unless a person allows it at the target (§8.10, V5) | Act locally |
| **An allow-all session** on any machine | Runs anything typed into it without a prompt | Receive any agent's or connector's reply **unattended**, local, remote or cross-account (§5.3, Q34). Receive a connector's at all in v1 | Be read, within the read policy. Receive a reply its owner approved, message by message or through a window (§5.7 G). Receive own agents' replies if the owner enabled `allowAllTargets` (§5.7 O) |
| **Prompt fatigue** at the gate | A stream of held messages | Get one through without a person's presence-signed approval. Pile up: holds have an absolute expiry (60 minutes quiet, 5 to 15 pushed), there's no morning backlog, and holds are charged to the sender's rates and budgets (§5.7 G) | Tire the person into approving. Mitigated by Deny first, nothing pre-selected, no push per hold, at most one text-free aggregated push an hour, batches and windows instead of repeated identical prompts, and the summary that never reassures |
| **Social engineering through the message text** ("Dan asked me to run this", a forged header) | The sender controls every word | Hide anything: the full text is shown, never truncated, hidden characters revealed, the sender's label from verified fields only (§6.3) | Persuade the person. The prompt says that approving makes it their own instruction to an allow-all session |
| **Message substitution** after approval (a relay, a host, or a resend with new text) | Holds or relays the bytes | Deliver different text, to a different session or instance, or from a different sender: the approval signs `textSha256`, the target instance and the sender principal, and is single-use in the durable replay store (§5.7 G) | Deny service by dropping the approval |
| **Window abuse** | An open window from one sender to a fixed set of target instances | Open, extend or expand it (only the target owner's presence-signed approval can), grow it automatically (no descendants, no expiry extension, no budget reset), keep it when a new origin appears in a tainted sender (it pauses), widen it to other senders, or outlive either session's instance, a mode change or a revocation | Send up to `gateWindowPerMinute` (2) messages a minute until it ends, each typed under the guard and audited |
| **Own-account escalation** through `allowAllTargets` | An ask-mode or prompt-injected agent of the owner's | Use it when it is off (the default), from another account, a connector, `homeassistant:`, a chain with a cross-account hop or a tainted sender, or outside its workspace and machine scope (§5.7 O) | When on, run commands without a prompt through a sibling allow-all session in scope. The owner accepts this knowingly |
| **Taint laundering** (an agent resumes, compacts, transfers or restarts to shed what it read from a collaborator; or a clean session reads a tainted own-account session; or a tainted session launches a fresh one) | An agent that retained external content, and its own-account siblings | Shed the taint by resuming, compacting, transferring or restarting: it survives anything that keeps the context (§5.8). Pass it to a sibling **through the Bridge** untainted: every content-bearing read taints the reader transitively, and a launched session inherits the launcher's taint. Use O while tainted, from a tainted workspace unit, or after an unattributed read (unattributed callers get typed status only, §3.4) | Hand content to a sibling **through files**. Within the workspace unit (every checkout and worktree of the repository on this machine) every resident inherits the taint, and O is refused there; O senders must have workspace instruction files provably off. But files read by path from outside the unit, other sessions' transcript files, a pushed file pulled on another machine, and `gh` or repository reads stay a disclosed residual (§5.8) |
| **Forwarding a scout's findings** | A scout that read a collaborator's sessions | Pass its output on as clean: forwarding is a gate action, "Approve forwarding this external content", that keeps provenance and taints the receiver (§5.8). Smuggle authority in fields like `approved` or `nextAction`: the template has none | Influence the receiver once the person approves forwarding |
| **Batch approval abuse** | Many holds approved at once | Add, swap or reorder an item after signing: the batch is immutable, bounded (20) and binds each item's text, sender and target instance (§5.7 G) | Get the person to approve a long batch without reading; every text is shown |
| **A stale queued message** | An approved hold waiting for idle | Arrive after its absolute expiry, after the person typed into the target, under a revoked link, or into a new instance: all are rechecked at delivery (§5.7 G) | Arrive within its expiry, after the context that justified it moved on a little |
| **A gated launch** | A held launch request, possibly from a tainted or external requester | Change the manifest, prompt or destination after approval, or shed external provenance (§5.7 G) | Run with the account's privileges once approved. The screen says a fresh worktree is organisation, not containment |
| **The relay** | Untrusted | Read, forge, replay or splice envelopes (Noise, signature, durable replay, channel-peer = signer) | Deny or delay service, observe host-to-host sizes and timing |
| **Clock faults** | A host's clock is wrong | Make a used command run again (durable record, high-water mark) | Make commands fail with `clock` until fixed |
| **Agent loops** | Two well-meaning agents ping-pong | Run unattended past 20 messages or 60 minutes per person-started conversation | Use up to those budgets |

### 10.2 Cross-account

All of 10.1, plus §7.8, and the repository-specific events of §7.11.6. In addition:

| Party | Must not be able to | Can (residual) |
|---|---|---|
| **A stolen person's device of the peer**, holding the link's `linkKey` | Add a host, change a listed host's keys, or rotate `linkKey`, without this side's person confirming it (§7.3). Renew freshness after the peer removes the device, holding only that device: renewal needs a co-signature by a peer attester off the custodian's machine, which the attesters refuse once the custodian is removed (§7.3, §7.5). Pre-date statements: `issuedAt` more than 120 s ahead is refused. Outlast a tombstone: one signed by `linkKey`, any confirmed peer host or a pinned revoker is honoured at once and permanently, and can arrive without RB (§7.3) | Before the peer removes the device: re-sign statements for already-confirmed hosts, and remove hosts. After removal: at most the remaining 24 h of the last co-signed statement, unless a tombstone arrives first. If the thief also holds an attester's keys, or the whole machine of a `singleMachine` link, **until a tombstone from another peer host or a revoker arrives, or the link expires** (30 days). This is not bounded at 24 h. Under a renewal mandate, renewal also needs a fresh presence-signed statement, so the bound is the mandate's final expiry only for a thief who passes the custodian's presence check (§7.3, §7.4) |
| **A collaborator's agent targeting an allow-all session** | Reach it unattended: allow-all and unknown sessions are never exposed for replies, and the own-account setting never applies to a link (§5.3, §5.7 O) | Read it, if the owner ticked `allowAllRead` (§5.7 R). Get a message typed into it that the owner approved through the gate, if `expose.gate` is on (§5.7 G) |
| **Exfiltration through `allowAllRead`**: a collaborator's agent reading allow-all sessions | Read outside the pinned workspaces or past §7.4's projection, or reply through it | Read what those sessions say, including command output and any secret in it. The link dialog says so (§7.11.5) |
| **Cross-account social engineering at the gate**: a collaborator's agent's message, shown for approval | Appear as the owner's own agent: the prompt carries the external marker and the T2 login (§6.2) | Persuade the owner, as for the same account |
| **An external sender probing outcomes**: telling a deny from a mute, a block or an expiry | Learn which: every case is one `notDelivered`, reported at a common deadline (§5.7 G) | Infer something from patterns over time; the design doesn't promise perfect indistinguishability |
| **Content retention by the collaborator's AI provider** | Be retracted by revoking: copies already read stay in their transcripts and provider (§7.7) | Keep what was read under content grants. Status grants carry no free text (§7.4) |
| **A collaborator's connector** (their Home Assistant, say) | Reach this side's content through their person view: person-kind only, results never enter their host's agent, pipe or connector inventory (§7.12) | — |
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
| **AP1** | **Smallest first slice: local agent principals, Windows only** (§3.4). It covers:<br>• credentials issued at launch (`AGENT_BRIDGE_TOKEN`, hashed store with process binding, a Job object per launch);<br>• `agent.hello` with **ancestry or Job membership read in full, failing closed**, never the environment variable. It is a **safety rail, not a boundary**: parent-PID spoofing and Job joining are stated residuals (§3.4);<br>• **isolation for forced-ask launches** (§5.3 (A)): a host-owned configuration home per agent (`COPILOT_HOME`, `CLAUDE_CONFIG_DIR`, `CODEX_HOME`) with a manifest checked and reset before every launch; project sources switched off, or the launch refused when the workspace or main checkout has a repository extension path; refusal when managed, MDM or server policy may apply; the Copilot flag allowlist with no Agency or `extraArgs`, Claude's `--permission-mode default`, Codex's `-a untrusted -s workspace-write -c features.hooks=false`; a scrubbed environment; failing closed on any unreadable or ambiguous scope;<br>• **directory watches** on every scope, including the creation of new extension paths, and the CLI's plugin, skill and agent sets;<br>• in-session grant and mode-change **detection**: Copilot from `permission.requested`/`permission.completed`, the others `unknown` unless a vector proves a signal before delivery;<br>• a **Copilot input-box reader** (host-replies.md:19-23 has none today), so the composer can be read before typing and before Enter (§5.2);<br>• the `agents` policy (`enabled`, `issue`, `inbound.local`, `exclude`, `limits`);<br>• local `session.reply`, `session.launch` and reads as an agent connection;<br>• chains, budgets and rates for local targets, and the audit log;<br>• `driverPrincipal = agent:<machine>/<session>`, `driverFrom`, the escaped prefix, and `userMessageFrom = agent` from the verified source;<br>• `agent-bridge-host agent list\|read\|reply\|launch` for tests, and the federation stub fields (§7.9).<br>**Exit criteria, each with a test that fails without it:**<br>• **an allow-all or unknown-mode session is never a reply target** (§5.3), including a session switched to allow-all after launch. The §5.7 opt-ins come later (AP8, AP9), so AP1 has none;<br>• **an agent launch or resume without an explicit `permissions: "ask"` is refused**, as are a non-allowlisted `agent` or `profile`, a resume of an allow-all session, and a `transfer`;<br>• **agent replies are delivered only to idle sessions** (`notIdle` otherwise, re-checked before Enter), **with `KeySend.Expect` on both the words and the Enter for every agent kind**, Copilot included. A prompt opened between the check and each key types nothing;<br>• a **non-empty or shell-mode composer is refused**, and Enter is sent only when the **complete** composer equals the authorized message exactly (§5.2);<br>• a session launched in forced-ask mode becomes `unknown` after an in-session "don't ask again" grant or a runtime mode switch (§5.3, Q44);<br>• **the mandatory refusal vectors of §5.3, for all three agents**: pre-seeded user allow rules, hooks, rule files and skills or commands with `allowed-tools`; subagents with a `permissionMode`; every repository extension path (in the workspace and in a linked worktree's main checkout); a managed file and a policy registry key; Copilot's `defaultPermissionMode: "assisted"`; Codex hooks with `features.hooks=false` unproven; the known gaps (Copilot `.claude/skills`, `.agents/skills`, `~/.agents/skills`; Codex `.agents/skills` up to the repository root, `$HOME/.agents/skills`, `/etc/codex`, a parent `.codex/config.toml`, cloud-managed requirements; `CLAUDE_CONFIG_DIR` leaks); a managed (corporate) account, which goes to G or O; a copied credential file; an `auto` launch that would pick Agency. Each refuses the forced-ask launch. File-access traces are kept as **regression tests, not proofs**;<br>• **mid-session vectors**: adding a hook, a skill, a settings file or a `.claude/` directory, a plugin change, and `/skills reload` each make the session `unknown`; a Codex agent writing `.codex/` into an untrusted workspace gains nothing;<br>• leading option keys and `@path` are escaped;<br>• a token from another session or a plain detached helper is refused, as are `AGENT_BRIDGE_SESSION` alone and a partial ancestry read;<br>• a manual or adopted session isn't a reply target, and a manual one gets no credential;<br>• an agent can't answer an approval or folder trust;<br>• a forged header in a title or body (including confusables) is neutralized;<br>• depth counts distinct sessions with the destination: A → B → C is allowed and C → D is refused (`chain-too-deep`) at `maxDepth` 3, a launch counts its new session, and an A ⇄ B conversation keeps `via` at two entries and stops only at the 20-message budget (`conversation-budget`); the person's reply resets them;<br>• a resumed session's old token is dead | #501 merged | **No** (decision 4 holds) |
| AP2 | #45's MCP server on AP1 (stdio), with the old tool names and outcomes | AP1 | No |
| AP3 | Adoption on Windows: app action, CLI, `agent.credential` | AP1 | No |
| AP4 | Envelope library: encoding, signing (a) or MAC (b), strict parser, shared positive and negative vectors | #391 R1 (+ roster v2 V1-V2 if (a), §8.10) | Library only |
| AP5 | Cross-machine: `agent.command` host-to-host, verification §4.7, `inbound.remote`, `outbound`, durable replay via R6, `instance` on `Session`, `agent.outcome`, `session.read` wait, and the **v1 wire format of signed provenance** on commands and content results (§4.3, §4.6). AP5 doesn't advertise `agent.provenance`; AP8 does, once it implements taint. **Exit tests** (transport only; the taint and O tests are in AP8 and AP9): provenance is signed and verified on commands and on content results; a result without signed origins is never handed over; an AP5-era host sends `"unknown"`, never an explicit origin set, and doesn't advertise `agent.provenance`; **the chain across machines**: the target recomputes `via` and `depth` from the signed `prev` links, so an envelope whose `via` drops an earlier session, cites a `prev` addressed to another session or another `root`, or cites one that is missing or doesn't verify is refused (`chain-unverifiable`), and A → B → C → D across three machines is refused at D with `chain-too-deep`; an A ⇄ B conversation across two machines runs to the `messages` budget with `via` at two entries, and the target enforces the larger of the claimed, recomputed and locally counted `messages`; **`agent.chainLink`**: it is served only on own-account, `enforce`-mode host-to-host channels, a request on a guest or link channel, or from a client, connector or agent connection, is refused, a request without a verifying `citing` envelope issued by the server is refused, and a walk longer than `conversationMessages` links or a fetch that times out gives `chain-unverifiable`; a launch link binds through the target host's signed `launch.result`, and one naming another session is refused; a tool-approval answer doesn't reset the chain; **across accounts**: an outbound envelope carries no `prev` and a `via` of the sender alone, external → R → S is allowed and S → X is refused (`chain-too-deep`), S's host accepts R's `anchor` without walking past it, and `via` keeps the external entry, so O from R or S is refused | AP4, #391 R3 + R6, hosts in `enforce` | **Yes** |
| AP6 | Clients: "from *X* on *Y*" in the app and PWA (extending #503/#504), Settings for the policy, the Agent activity view | AP1 (local), AP5 (remote) | n/a |
| AP7 | Re-enable `inbound.remote` defaults per machine during the owner's cutover (#391 R8) | AP5, R8 | Yes, live |
| AP8 | **The gate, as the main path** (§5.7 G, §5.3 (B)), and **provenance** (§5.8). It covers:<br>• `inbound.*.gate` (`off`/`quiet`/`push`, default `quiet`), holds with absolute expiry, the bounded queue, `held` and the `gateDenied`/`gateExpired`/`gateCancelled` codes;<br>• the Needs-you `agentMessage` item with its deterministic partial summary and full text;<br>• the presence-signed `gate v1` approval, batches, windows over fixed instance sets with the expansion fold, and G delegation grants;<br>• **gated launches**, with the manifest, the **base commit** bound at hold time, the source report over that tree, and a fresh worktree at exactly that commit;<br>• mute, pause and block without biometrics, and the refusal nudge;<br>• taint tracking, **transitive through every content-bearing read and every launch**, with in-host Home Assistant presses (Q43) recorded as `automation:homeassistant`, **workspace-unit taint** merged into every resident, presence-signed clearing, `driverFrom.origins`, `taint`, `replyPath`, the badge, and "Start a fresh coordinator";<br>• the typed, non-tainting `sessions.status` and `session.status` for own sessions, as the MCP default; **unattributed callers (HTTP MCP, the pipe without `agent.hello`, the CLI apart from its three exceptions, anything else) limited to typed status, with no acting ops**, superseding local-pipe.md:93-98, and host-launch.md's "Isolation" (:118-136) and "Reclaiming finished worktrees" (:142-214) for agent-requested launches; `launch.options` without titles for agents; workspace units by `unitsOf` with containment; generation-bound clearing; gated launches bound to the base commit and the hashed local git state, with Git under a closed, host-generated configuration and a rebuilt environment for every host-made worktree and every gated session;<br>• workflow budgets;<br>• audit of origin, authorizer, grant and outcome.<br>**Exit tests:**<br>• an approval for different text, sender, origins, target or instance is refused, and so is a replayed one;<br>• an approval without presence, or from an agent connection, a connector or the `homeassistant:` path, is refused;<br>• a hold expires at its absolute deadline even when approved late, and the person's input cancels it;<br>• **no hold is ever pushed individually**;<br>• a batch can't change after signing;<br>• a window never grows except by a presence-signed expansion, and pauses on a new origin;<br>• taint survives resume, compaction, transfer and a person's reply;<br>• **external → A → a same-account `session.read` by B → B is tainted with A's origins**, and the same through `session.get`, a list and a wait result;<br>• **a content read over HTTP MCP, or over the pipe without `agent.hello`, is refused, and typed status is served instead**;<br>• typed status taints nothing;<br>• a tainted session's forced-ask or gated launch produces a tainted session;<br>• **a tainted session in worktree W1 taints the unit**: a clean resident already running in the main checkout W0 inherits its origins, and C reading that resident inherits them too;<br>• clearing a unit needs presence, and is refused while a tainted session runs there;<br>• **a push to the default branch after a gated launch's approval doesn't change what launches**, and a missing base commit refuses it;<br>• a batch containing a gated launch is refused;<br>• a batch with two senders, or mixed tainted and untainted items, is refused;<br>• an external sender's resend returns `held` until the common deadline;<br>• mute, pause or block from an agent, a connector or Home Assistant is refused, and none of them hides the mid-turn configuration-change notice;<br>• a gated launch can't change after approval, and a cross-account gated launch requesting `allowAll` is refused;<br>• a connector reply into allow-all is refused with no hold;<br>• delivery still runs §5.2's guard;<br>• **an unattributed caller (HTTP MCP, the pipe without `agent.hello`, a script) can't reply, launch, answer, stop or attach**, and HTTP-mode MCP exposes no acting tools; the CLI gets only `launch`, `agents adopt` and `agents revoke`, and **`agent-bridge-host launch` run from inside an agent session is refused** by the full-ancestry check;<br>• `launch.options` reaches an agent or unattributed caller with no titles;<br>• **unit containment**: a tainted session whose working directory is a non-git parent taints every nested repository's unit; a submodule is in its superproject's unit; an `--add-dir` root taints the unit it falls in; a tainted allow-all session taints every unit on the machine for O;<br>• **across machines**: external → A on M2, then a content read of A by B on M1, taints B on M1 before the content is handed over; a coordinator tainted mid-window is paused at a remote destination; a peer without `agent.provenance` gets typed reads only, and its commands give the target `unknown:<machine>`;<br>• **`unknown` sticks**: a forced-ask target that received `unknown` provenance carries `unknown:<machine>` and stays ineligible after later clean messages;<br>• **durability**: after a host restart or update a tainted coordinator is still tainted; a deleted taint store makes everything `unknown:<machine>`; an old clear doesn't match after a restart;<br>• **rollback**: restoring **both** the taint store and its high-water mark from a backup taken while a coordinator was clean, then restarting only the host while the coordinator's process lives on, leaves the coordinator `unknown:<machine>` and O refused;<br>• **clear after store loss**: after the store is lost and a person clears the machine, a resumed pre-loss conversation still has `unknown:<machine>`; the first AP8 start treats every existing conversation the same way; the machine clear is refused while an unconfined tainted session runs;<br>• **canonicalisation**: one repository reached through a junction, a symlink, `subst`, a `\\?\` path or an 8.3 name is one unit; a unit first seen after a tainted allow-all session ran is tainted;<br>• **a pending wait is cancelled** when the read grant narrows mid-wait, own-account and cross-account;<br>• **a subscription** merges taint per event, including when the subscribed session becomes tainted mid-stream;<br>• **a delayed clearance is refused**: a tainted session that starts, writes and exits after the person reviewed but before the clear executes bumps the generation;<br>• **a gated launch is refused** when the common directory's hooks, its allowed configuration keys, `info/attributes`, `.lfsconfig`, or the main checkout's untracked `settings.local.json` changed after approval; `worktree add` runs no hook, and the launched session's `git status` runs no hook or monitor added later;<br>• **closed configuration**: a repository whose `config` or `config.worktree` holds any key outside the allowlist (an `include`, `core.fsmonitor`, a non-LFS `filter`, `core.gitProxy`, `remote.*.vcs`, `remote.*.uploadpack`, `url.*.insteadOf`, `protocol.*.allow`, `lfs.customtransfer.*`, `lfs.standalonetransferagent`, `lfs.extension.*`, `mergetool.*.path`, `sendemail.smtpServer`) refuses the launch, as does a `.lfsconfig` with other keys or a remote URL not in `https://`, `ssh://` or scp form; in the launched session, a `~/.gitconfig` `url."ext::sh -c …".insteadOf` rewrite with `protocol.ext.allow=always`, a global `credential.helper` script, a `~/.ssh/config` `ProxyCommand` or `Match exec`, and a `gpg.conf` program option all run **nothing**, before or after being edited post-approval;<br>• **environment**: a launch inheriting `GIT_SSH_COMMAND`, `GIT_SSH`, `GIT_ASKPASS`, `SSH_ASKPASS`, `GIT_EDITOR`, `GIT_PAGER`, `GIT_EXTERNAL_DIFF`, `GIT_PROXY_COMMAND`, `GIT_CONFIG_PARAMETERS`, `GIT_CONFIG_GLOBAL`, `GIT_EXEC_PATH` or `GNUPGHOME` pointing at a user-writable helper, which is then edited between approval and launch, runs none of them, in `worktree add` or in the session; with no configured helper, Git's default editor and pager don't run; `PATH` with `.`, an empty or relative element, or a unit directory refuses the launch;<br>• **version and self-check**: a Git older than 2.32, or below the shipped maintained-release floor, refuses the launch; a global or include-defined `filter.<x>.smudge` naming a writable script runs nothing during the host's `worktree add` or in the session; the self-check (`git config --list --show-scope --show-origin` under the exact executable and environment) refuses any entry from another origin;<br>• **scope**: an agent's forced-ask launch into a repository whose `config` holds a planted `filter.<x>.smudge` is refused; a person's own launch still runs no global filter, hook or monitor during `worktree add`; a gated launch requesting `ask` gets the same closed session configuration as one requesting `allowAll`;<br>• **host artifacts**: a `post-checkout` hook dropped into the host's empty hooks directory, or a `ProxyCommand` added to the generated ssh config, after approval refuses the launch; translating a `~/.ssh/config` with `Match exec`, or a compound `Match host *.corp exec "…"`, never runs it and drops the whole block;<br>• **every host Git command**: with `core.fsmonitor`, `remote.origin.uploadpack` or `core.sshCommand` planted in `.git/config` (or a `config.worktree`), an agent's launch request is refused by the parser before any Git command, so nothing runs during repository resolution, reclaiming, the fetch or the hold, even when the person would have denied it; with a clean repository, reclaiming and the fetch run under the closed configuration, a fetch needing the user's jump host falls back to the local base, and a gated launch's base SHA is the one resolved at hold time; a person's own launch fetches with the user's own Git; the pre-add self-check runs in the main checkout and accepts repository keys only for a person's own launch; a forced-ask session isn't self-checked;<br>• **reclaiming**: a finished worktree whose `.git` is redirected to `<wt>\.cache\gd` (with a `commondir` back to the repository, `extensions.worktreeConfig=true`, a `config.worktree` holding `filter.x.clean`, `* filter=x` in `.gitattributes` and a touched tracked file) is kept with nothing run, and so is one whose own `.git/worktrees/<id>/config.worktree` holds `filter.x.clean` directly, both for an agent's launch and for a person's own; every host Git command carries the parser-resolved `GIT_DIR`, `GIT_COMMON_DIR` and `GIT_WORK_TREE`; a finished worktree whose index holds a gitlink to a submodule with a redirected `.git` and a `filter.x.clean` is kept after `ls-files --stage`, with no `status`, `rm` or `checkout-index` run;<br>• **submodules in the fetch**: in a launch workspace with a populated submodule, `.gitmodules` `fetchRecurseSubmodules=true`, and the submodule's own configuration holding `remote.origin.url=ext::<cmd>` and `protocol.ext.allow=always`, an agent's launch request that the person then denies runs no child fetch and no command; the host's fetch carries `--no-recurse-submodules`, every host Git command carries the `submodule.recurse`, `fetch.recurseSubmodules` and per-protocol `never` pins, and no host Git command runs inside a submodule;<br>• **parser fidelity**: a key on a header's line (`[filter "x"] clean = …`), a comment ending in `\`, the legacy `[remote.origin]` form, a CR, BOM or NUL byte, and a quoted `#` or `;` each refuse an agent's launch, Git's own `<TAB>name = value` layout is accepted, a subsection containing `=` cross-checks correctly, and a file where `git config --file <f> --list --null` disagrees with the host's parse refuses it;<br>• **base source**: the approval screen shows whether the base was fetched or is the local ref after a failed fetch, with the reason; a `branch.<name>.remote` holding a URL refuses the launch;<br>• **LFS**: `worktree add` on an SSH remote with LFS runs no SSH and no smudge; a later `git lfs pull` in the session loads no custom transfer agent;<br>• **instance**: a session adopted, discovered or resumed from a terminal (`--resume`, `--continue`, a forked id) after a host start is `unknown:<machine>`; a fresh coordinator started after a restart but before the machine clear stays `unknown:<machine>` after the clear; an allow-all session started after the restart blocks the clear; an open window pauses after a restart | AP1, AP6, #391 person commands with presence (§7.2) and R6. Remote targets also AP5 | Same machine first; remote with AP5 |
| AP9 | **Own agents into own allow-all sessions** (§5.7 O): `inbound.*.allowAllTargets`, its warning in Settings, workspace scope. **Exit tests:**<br>• off by default;<br>• refused for another account's, a connector's, a federated or `homeassistant:` principal, and for a chain with a cross-account hop;<br>• refused for a tainted sender, including taint from a read (**external → A → read by B → O from B refused**), from a launch, and from the workspace unit;<br>• refused to and from a tainted unit, and anywhere under `"any"` while any unit is tainted;<br>• **refused for a sender that loaded workspace instruction files, or whose loading is unknown; allowed for a target that loads them**;<br>• refused for a sender without a host-owned configuration home (personal instruction files, custom agents or skills loaded);<br>• refused when the sender's signed `provenance` is missing, legacy or `unknown`, `oEligible` isn't `true`, or `origins` isn't exactly `[]`;<br>• **remote O**: external → A on M2, read by B on M1, then O from B is refused; `"same"` never matches a sender on another machine;<br>• **`"same"` is exact**: a sender rooted at a non-git parent, or with an `--add-dir` in another unit, fails `"same"`; a network path is refused;<br>• refused outside the workspace scope;<br>• `allowAllPerTargetPerMinute`, depth and budget apply; the guard still applies | AP1 and **AP8's taint tracking** (O can't ship before taint, because O's exclusions depend on it). `inbound.remote`: AP5 | Local first; remote with AP5 |
| AP10 | **Sandboxed allow-all** (§5.7 S): OS-enforced restriction of the filesystem, credentials, IPC and network egress. **Immediately after AP9 (owner decision, Q50).** It doesn't remove the gate without separately demonstrated boundaries | AP9, a separate design and review | Local |
| AP11 | **Standing coordination group** (§5.7 C): `inbound.*.coordinationGroups`, the approval sheet (which leads with the risk statement and the residual, and is never shown without them), the standing row, **Revoke**, **Resume them**, `driverFrom.grant`. **Exit tests:**<br>• off by default, set only at the machine, and refused outside roster `enforce` mode;<br>• only `session.reply` text into members; approvals, folder trust, forms, stop, launch, transfer and attachments refused, and every §5.2 **Never** row holds;<br>• refused for another account's, a connector's, a federated or `homeassistant:` principal, and for a chain with a cross-account hop;<br>• a tainted member is paused as sender and target, and taint from a read pauses it (**external → A → read by B → B paused**);<br>• after a host restart, an update included, every member on that machine is paused until **Resume them**, which clears no taint (H1); Resume passes over only the `unknown:<machine>` this instance's start caused and binds its instance id; a member with that origin from an unreadable or failed store, legacy provenance, no store record or an earlier instance stays paused, and a Resume signed for an earlier instance is refused; a clean member whose unit carries only this restart's origin from a running resident resumes, and one whose unit also carries another origin stays paused; a member whose unit holds an `unknown:<machine>` merged from a resident tainted by a store loss stays paused after **Resume them**, and so does one whose unit's `unknown:<machine>` came from an earlier instance no Resume covered; after a second restart, a member an earlier verified Resume covered can be resumed again;<br>• the grant applies only when the recomputed `via` lies within the members; otherwise G, with `maxDepth` on hops that leave the group (M2); a group of four chains A → B → C → D, and that conversation's first hop to a non-member is refused (`chain-too-deep`);<br>• budgets 200 a day per member and 10 a minute across the group, `allowAllPerTargetPerMinute` 3, and a loop notice;<br>• grant and revoke are signed, generation-bound roster `set grants` entries honoured only under V5 freshness; suspended at start-up until synced; an old revoke can't end a new group and an old grant can't outlive its revoke (M4);<br>• **refused with `RemoteAuthorization`'s floor off** (M5);<br>• `driverFrom.grant` set only from the verified grant; after a restart the driver shows as unknown or agent, never the person (M6);<br>• the expiry item and **Approve again** are never pushed and never ride the hourly aggregate (c);<br>• a transfer drops the member; **a resume under the same session id is refused as a member** until the owner adds it again, as is a new or newly resumed id (L1) | AP5, AP8 (taint), #55 roster v2 (V5 and `set grants`) | Local first; remote with AP5 |
| F1-F7 | Federation (§7.10), with repo discovery (§7.11) as F6 and the person view (§7.12) as F7 | AP5, owner approval, separate review. F6 also needs the Cosmos DB directory store (§7.11.10) | Cross-account |
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
   **Decided by the owner (2026-11-07): (a) `hostSign`.** The standing coordination group (§5.7 C) relies on it.
2. **Q2. Default remote policy when AP5 ships.** (a) Read any session. Reply to sessions the host
   launched in forced-ask mode (§5.1 `reply: forced-ask`, §5.3). Launch with an explicit `ask` in
   approved workspaces. All of it from any of the account's non-probationary hosts. (b) Read only.
   (c) Everything off until set per machine. **Recommended: (a)**, the widest reach decision 2
   allows once §5.3 holds. `allowAll` is never expressible for agents (design.md §13 item 8).
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
   HTTP can't be bound to a session by ancestry. HTTP callers get typed status only: no content
   reads and no acting tools (§3.4).
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
15. **Q15. Corporate machines and federation.** **Answered by Q51** (owner decision): the user
    decides, and a managed machine isn't refused for being managed. No organisation-policy
    detection exists yet; S8 (design.md:1338-1343) is still an open question, not a mechanism.
    Once it produces one, a policy actually configured or enforced will win.
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
    directory on a rendezvous relay (D4), gated by GitHub push access through a **Metadata-only**
    GitHub App installation, and expiring after 30 days. The activity filter (Q22) runs in the
    apps only. No directory, and so invitations, where the repository hasn't installed the
    directory's GitHub App. The only optional repo file is a non-personal `.agent-bridge/rendezvous` relay pointer.
    Per-contributor files in the repo (D1) are offered only in private repos, as an explicit
    opt-in.
22. **Q22. What counts as a contributor.** **Decided by the owner (2026-11-03, 19:47 PT).** A
    collaborator is **suggested** only with **write access now AND recent activity in that
    repository by both people**: authored or co-authored default-branch commits, pull requests
    opened, or reviews submitted, within a per-repo window (default 90 days). Each app checks
    both people with its own token. The directory, Metadata-only, gates on write access alone,
    so a dormant writer can list entries, a privacy cost only (§7.11.2). Activity
    is a suggestion filter only: **write access stays the membership test for the link**, and
    lapsed activity keeps an existing link but stops new suggestions. No client-side directory
    fallback: a repository without the directory's GitHub App uses invitations (§7.11.2,
    §7.11.4).
23. **Q23. Who operates the directory for the production relay, and is the operator learning
    "user X uses the bridge on repo R, through relay E" acceptable?** **Recommended:** yes, the
    production relay hosts it, and that disclosure is stated in the opt-in dialog. Its GitHub App
    asks for **Metadata only**, so the operator can never read a repository's contents or pull
    requests through it. A team that objects uses the invitation path, or its own rendezvous
    relay.
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
    §5.2's idle-only, screen-guarded delivery and §5.3's allow-all exclusion, as agents do, but
    **not** §5.7's gate or own-account setting: a connector reply into an allow-all or unknown
    session is refused, with no hold. Whether a later `act` profile may use the gate is its own
    question for that design. Approvals
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

34. **Q34. Replies into allow-all sessions.** **Decided by the owner (2026-11-03, 19:59 PT, with
    the defaults confirmed at 21:12 and 21:14 PT), §5.7.**
    * The restriction is on the **target** only; a sender's mode never matters.
    * An **unattended** reply into an allow-all or unknown-mode session needs one of the paths
      below, each keeping §5.2's delivery guard.
    * **(G) The per-message gate**, same account and cross-account.
      * **Own-account G is on by default in `quiet` mode** (owner decision, 21:14 PT): a hold runs
        nothing without the owner's presence, and is never pushed.
      * The owner approves each exact message with presence, in a batch, or through a window
        over a fixed set of target instances.
      * A new launch that isn't provably forced-ask is held as a **gated launch** rather than
        refused.
      * Cross-account G is opt-in per link. Never for connectors in v1.
    * **(O) Own agents into own allow-all sessions without a gate**: per machine, off by default.
      * It is never for another account, a connector, a federated or `homeassistant:` principal,
        a chain with a cross-account hop, or a **tainted** sender, including taint from a read,
        a launch or the **workspace unit** (§5.8).
      * Its sender must have been launched with **workspace instruction files provably off**.
        So in a repository with an `AGENTS.md`, the owner's included, only a host-launched
        coordinator with those files switched off can send through O.
      * The recommended scope is the same workspace on the same machine, with the escalation risk
        shown and accepted.
      * **O is therefore a specialised, clean-workspace mode. G and its windows are the general
        path** (§5.8).
      * An O sender's host-owned home has **no imports at all**, and `"same"` never matches across
        machines.
      * **Owner decision (2026-11-03, 21:47 PT): keep O narrow.** Asked about the trade-off, the
        owner kept the instruction-file rule for senders. G, with quiet mode and windows, is the
        everyday path. Not to be reopened.
    * **(R) Read-only cross-account access** to allow-all sessions in a link's pinned workspaces,
      per link, with the content-exposure warning. Replies still need G.
    * **(S) Sandboxed allow-all**: **decided for immediately after AP9** (AP10, Q50). It is not a
      prerequisite for reads or federation, and doesn't remove the gate without separately
      demonstrated boundaries.
35. **Q35. Replies into working sessions.** **Recommended:** refused as `notIdle`, idle only
    (§5.2), re-checked before Enter. That is a new additive refusal code, since `busy` already
    means an answer or reply is being typed (v1-sessions.md:454).
    A **direct** reply into a forced-ask session is not queued. A message **held by the gate**
    is: once approved, it waits for the target's next idle up to the hold's absolute expiry,
    approval doesn't restart the clock, the person's own input cancels it, and everything is
    rechecked at delivery (§5.7 G). That bounds the "context changed" risk a general queue would
    have.
36. **Q36. Agent launches with `permissions` or `profile` absent.** **Recommended:** refuse.
    `permissions` must be explicit, never filled from the machine's defaults: `ask` for an
    unattended launch, or `allowAll` only as a new gated launch approved with presence (§5.7 G). `agent` and
    `profile` must be on the policy's allowlists, and any agent, profile or model setting the host
    can't positively show to be non-allow-all is refused (§5.2). Agency, including `auto`
    resolving to it, is refused for agent launches, since it adds its own flags and profile
    config (host-launch.md:29-30, :65).
37. **Q37. Manually started or private sessions as targets.** **Recommended:** readable by default
    (decision 2), but **never reply targets**. Only forced-ask launches are reply targets
    (`reply: forced-ask`, §5.3), so manually started and adopted sessions, whose mode is unknown,
    can't be. `inbound.exclude` makes a private session unreadable too.
38. **Q38. GitHub token type, scopes and storage, for repo discovery.** **Recommended:**
    * a **GitHub App user-to-server token** from the Agent Bridge GitHub App, read-only, on the
      repositories the person selects: **Metadata** (the collaborators check), **Contents** read
      and **Pull requests** read (the activity filter, §7.11.4), and **Checks** or **Commit
      statuses** read (the typed status's CI state, Q52). It never holds write permissions, and the directory's own installation stays Metadata-only;
    * expiring and refreshed;
    * stored only on the **person's** devices (DPAPI, Keychain, IndexedDB with a non-extractable
      wrapping key), never on hosts or relays;
    * sent only to GitHub, and to the owner's own sign-in relay during sign-in (§7.11.2).
39. **Q39. Link-key custody, rotation and revocation.** **Recommended:** §7.3:
    * custody in the presence-proven device's hardware tier, signing with presence;
    * statements need `linkKey` **and** an attester's co-signature: a listed host **off the
      custodian's machine**, or an online revoker device. Attesters co-sign only while the
      custodian device is active in their roster, and check `issuedAt` against their clock. A
      thief with **only** the stolen key can't keep a link alive past 24 h; a thief with an
      attester's keys too can, until a tombstone or the link's 30-day expiry. A renewal mandate
      renews only after a fresh presence-signed statement from the other side, so it extends
      this only for a thief who also passes the custodian's presence check, up to the mandate's
      final expiry, which the dialog shows;
    * at least one revoker **on a separate device, or an offline recovery key**, is required
      before a link can be accepted;
    * a revocation authority pinned at acceptance: `linkKey`, any confirmed peer host's
      `hostSign`, or a `revokers` key from the descriptor, so the peer can verify a tombstone
      without the other roster;
    * a tombstone is permanent, ends the link's id, and travels by the guest channel, the
      directory and an out-of-band paste as well as RB;
    * automatic revocation when the custodian device leaves the roster, with a tombstone from
      each host;
    * rotation, a new host and **any** key change for a listed host all need the other person's
      confirmation.
40. **Q40. A repo link's default scope.** **Recommended:** nothing pre-ticked, matching §7.4's
    "None". The person ticks read and/or reply, and launch separately.
41. **Q41. The workspace-to-repo binding.** **Recommended:** pinned at acceptance by the person,
    by workspace label and path. A remote change suspends the link until the person re-confirms.
    Never derived per message from live git settings (§7.11.5).
42. **Q42. `credentialDays` and sub-agents.** **Recommended:** 7 days, then one press at the
    machine, or a presence-confirmed remote renewal for the same live process with no scope
    increase and no resurrection (§3.3). Sub-agents share the parent session's credential, since they are the
    same process tree and the same principal. The audit records the sub-agent where the host can
    tell. Per-sub-agent scopes are a later option.
43. **Q43. The in-host Home Assistant press path after AP1.** Presses by
    `homeAssistant.agentUserIds`, attributed `homeassistant:<user id>`. **Recommended:** keep
    it unchanged as the G9 compatibility path until #319 retires it, labelled in `driverFrom` as
    `kind: "homeassistant"`, distinct from native agents. No new feature uses it, and agents are
    pointed at AP1 and AP2 instead. **Its influence is untracked today:** text pressed in from
    Home Assistant is untrusted-automation influence (§5.8). AP8 records it as
    `automation:homeassistant` taint, and O (AP9) depends on AP8, so a session driven that way
    can never use O.
44. **Q44. The person's own grants and mode switches make a session unreachable by agents.** With
    forced-ask launches (§5.3), a "Yes, and don't ask again" the person gives in a session, a
    persisted Claude allow rule, or a runtime `/allow-all`, `/yolo` or `/permissions` change makes
    that session `unknown`, so no agent can reply into it again. (a) Accept that. (b) A
    per-session opt-in ("let agents reply here even though I allowed tools"), presence-confirmed.
    **Recommended: (a), unreachable**, with no opt-in in v1. The person can see why on the
    session card ("Agents can't reply here: you allowed tools without asking") and can start a
    fresh forced-ask session. An opt-in would be an allow-all reply target by another name.
45. **Q45. Unattended replies into Claude and Codex in v1.** §5.3 makes a session an unattended
    reply target only when it is provably forced-ask **and** the host can see every grant and
    mode change before delivery. Copilot records `permission.requested`/`permission.completed`;
    Claude gives agent-bridge no signal; Codex records its approval policy only after a turn is
    submitted. So **Claude and probably Codex sessions are never unattended reply targets** in
    v1, and on an organization-managed account Copilot sessions may not be either (§5.3 (A)
    step 3). (a) Accept that, and reach them through the gate (§5.7 G, Q47 (B)): every Claude or
    Codex session can receive a message its owner approves, and the owner's own agents reach
    their own sessions in scope with no prompt if `allowAllTargets` is on (§5.7 O). (b) Allow them
    on the launch check alone, accepting that a grant made at the machine is invisible.
    **Recommended: (a).** (b) reopens the unattended-execution path the reviews closed. Claude and
    Codex join the unattended path when AP1 vectors find a pre-delivery signal that adds no
    authority to the session. A hook in the host-owned home wouldn't qualify: that home has no
    hooks by design (§5.3 (A)), and a hook runs commands.
46. **Q46. Links held on a single machine.** If A's only machine is both the link custodian and
    its only host, a thief who passes that machine's sign-in holds `linkKey` and `hostSign`
    together and renews the link until a tombstone arrives or it expires (§7.3, "The residual").
    (a) Require an **attester off the custodian's machine** (a second host, or the phone app as an
    online revoker) to make a link. (b) Also allow a `singleMachine` link with only an offline
    recovery key, shown as such to the other person before acceptance. **Recommended: (b), with
    (a) the default the app steers to.** A revoker off the machine is always required (§7.3), so
    revocation always has a path, and the other person decides with the risk in front of them.
47. **Q47. How forced-ask is established: isolation, the gate, or both.** Three rounds of
    review each found configuration sources a list had missed (§5.3). (A) **Isolation**: a
    host-owned configuration home per agent holding only host-written configuration and the
    sign-in credential, project sources switched off or the launch refused, and refusal when
    managed or server policy may apply. (B) **The gate as the general fallback**: any target that
    isn't provably forced-ask gets a message only with the owner's presence-signed approval of
    that exact message (§5.7 G). (C) Keep enumerating sources. **Recommended: (A) and (B)
    together.** Isolation makes the unattended path rest on "what the host wrote"; the gate makes
    forced-ask an optimisation rather than the only boundary, and gives Claude, Codex and
    organization-managed accounts a v1 path (Q45). The costs, accepted with it: forced-ask
    sessions lack the person's own skills, MCP servers, plugins, custom agents, hooks and saved
    approvals; a workspace with repository extension paths can't have one; and their history
    lives in the host-owned home, visible through the bridge rather than the CLI's own resume
    list. Agent **launches** that aren't provably forced-ask are **gated launches** in v1 (§5.7 G).
    After the adversarial review, the recommendation stands, with forced-ask treated as an
    **optimisation**: unknown stays unknown per adapter and version, and managed (corporate)
    accounts use G or O (§5).
48. **Q48. Directory storage.** **Decided by the owner (2026-11-03, 20:10 PT), §7.11.10.**
    * No names are stored: no login, display name, email or repository name.
    * The numeric user id stays only inside the signed descriptor, encrypted at rest, and is
      treated as pseudonymous personal data.
    * Keys are blinded with a keyed HMAC under a rotatable Key Vault secret shared by every
      stamp, read from both versions during rotation.
    * Endpoints are sealed per peer by default.
    * One Cosmos DB (NoSQL) container is shared by every region stamp, with multi-region writes.
      The newest signed descriptor wins; withdrawals are signed tombstones; expiry uses TTL;
      reads use session consistency.
    * Deletion is withdrawal plus TTL, or on demand.
    * Self-hosted relays may use a single-node store.
49. **Q49. Collaborators in the person's own app.** **Decided by the owner (2026-11-03), §7.12.**
    * The person sees a linked collaborator's exposed sessions read-only, with a person filter
      ("Mine", then each collaborator) and machines nested under it.
    * Those sessions are kept out of Needs-you, notifications, badges and counts.
    * Your own sessions get an "Involving external agents" filter.
    * Reads are proxied through one of the reader's own listed hosts, as a `kind: "person"`
      principal.
    * **Person-kind only:** the host resolves the person's device from the authenticated caller,
      connectors (Home Assistant included) get no collaborator content without a separate grant
      from the exposing owner, and proxied results never enter the host's inventory for agents,
      the pipe or connectors (revision 11).
    * **Deferred:** the person replying into a collaborator's session, which would be its own
      future grant, presence-signed and separately reviewed.
50. **Q50. When to build sandboxed allow-all (S).** **Decided by the owner (2026-11-03, 21:12 PT):
    immediately after AP9** (AP10, §5.7 S). It is not a prerequisite for reads or for
    cross-account G, and it removes the gate only with separately demonstrated boundaries.
51. **Q51. The supported audience, and managed machines.** **Decided by the owner (2026-11-03,
    21:12 PT).**
    * **Adults only**, stated in the terms. No birth dates are collected (§7.11.5).
    * **On a managed machine, the user decides.** **No organisation-policy detection exists
      yet** (S8 is still open, and §5.3 has no effective-policy signal), so the person's consent
      is the only control, and the absence of a detected policy never refuses a link. When S8
      produces a detection, a policy actually configured or enforced will win, with links refused
      and the policy named. A personal consent button never overrides an
      organisation-enforced restriction (§7.8, §7.11.5). Forced-ask is unaffected: managed
      accounts still reach agents through G and O (§5.3).
52. **Q52. Where typed status gets PR and CI state.** **Decided by the owner (2026-11-03, 21:12
    PT):** the **reader's app** fetches it with the person's own token in v1, so CI state is
    unavailable while no person device of the reader is online. A broker comes later, as its own
    design. Either way status guides scheduling only, and never authorizes a merge, a deploy or
    the use of a credential (§5.8).
53. **Q53. O after a host restart: accept the cost, or anchor taint off the machine?** **Decided by
    the owner (2026-11-03, 23:19 PT): (a), accept the cost, with no off-machine anchor in v1.**
    The taint store can't detect its own rollback (§5.8 Durability), so in AP8 only the running host
    instance vouches for cleanliness. After every host restart, an update included, every older
    conversation carries `unknown:<machine>`, and so does every session started before the
    person's machine clear. That clear waits until every unconfined (allow-all) session has ended,
    pre-restart or not, and every open window pauses for re-approval. O then needs the order
    "end unconfined sessions, clear the machine, start the coordinator and targets". G keeps
    working, with labels and re-approved windows.
    * (a) **Accept it** for AP8 and AP9. O is already a narrow clean-workspace mode by the owner's
      Q34 decision, and G with quiet mode is the everyday path.
    * (b) **An off-machine anchor**, kept as a possible later design: a monotonic taint sequence,
      signed with the host's device key and held by the relay or countersigned by a person device under the #391 roster floor,
      checked before content is handed over and on host start. An older or unverifiable sequence
      enters recovery. It would let surviving sessions keep their state across a restart, at the
      cost of a relay round trip on the first tainting read and recovery whenever the anchor is
      unreachable at start.

    **Decided: (a).** (b) stays a possible later design, to be reconsidered only if O proves
    unusable after updates.
    Members of a standing coordination group (§5.7 C) pause on the same restart, and resume only with the person's **Resume them** (H1).

## AP5-lite, slice L2: the lite envelope profile and agent signing key (#728, #1350)

A temporary, off-by-default mode for a person's own agents on their own account, with no roster. Library only: nothing sends or receives yet.

- `LiteProfile` (AgentBridge.AgentEnvelope) seals and checks a `session.reply` envelope. It carries all-zero generation and head markers, provenance origins "unknown", `oEligible` false, the sender's account, and a lifetime of at most 120 seconds. `Check` refuses a missing marker, an account or target account that is not the receiver's, any other action, a non-false `oEligible`, an expired or future-issued envelope, a bad signature and a `dataSha256` that does not bind the text, each with its own code. `RequireRoster` refuses the markers, so a lite envelope can never pass the roster path.
- `AgentSignKey` (AgentBridge.Host) is a per-host P-256 key made only when asked and only with the mode on. It lives in the host's private folder, DPAPI-protected on Windows, is never logged or returned, and a damaged or tampered file is refused rather than replaced. The roster path (#55) replaces it with `hostSign` (V1).
- Account id grammar (#1385): the envelope accepts an account id of 32 lowercase hex, or 1 to 20 ASCII digits with no leading zero, on `account`, `chain.via[].account`, `provenance.origins[].account` and `target.account` only. The real host account is the numeric GitHub id. Every other id field is still 32 lowercase hex.

## AP5-lite, slice L2b: pinned own-peers and local owner commands (#728, #1369)

- `own-peers.json` lists the owner's other hosts this host trusts in lite mode: device id, account id, full signing key. It is signed with this host's agent signing key, so a hand-edited or unsigned file trusts nobody, and every read goes to the file, so a removal needs no restart.
- Only `agent-bridge-host peers pin <pin-code>` at this machine adds a peer. The code comes from `peers show` on the other host, signed by its key; pinning prints device id and fingerprint and needs `yes`. `peers list` and `peers clear [<device-id>]` view and remove. Pin, clear and show are refused inside an agent session; no message route reaches them.
- Mode off: nothing is read, the commands refuse (`clear` still works). A peer of another account is refused and ignored. Limits: a process of the same user can read the signing key as it can any host-local file, and a code's account is the other host's claim, backed by the person's fingerprint check and the AP1 credential on every message. L3 looks senders up with `OwnPeers.TrustedKey(deviceId)`.
