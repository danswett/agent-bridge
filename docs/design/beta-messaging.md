# Beta messaging between a person's agents (program C, slice C3)

Part of program C (beta messaging), slice C3; builds on AP5-lite (#728) and the native relay send (#1233). Companions:
[agent-lite.md](../protocol/agent-lite.md) (the runbook), [agent-principals.md](agent-principals.md) (AP5-lite slices L1 to L7),
[mcp-fleet-mode.md](mcp-fleet-mode.md) (the MCP server's reach), [fleet-tools-to-host](https://github.com/danswett/slipgates-fleet/blob/main/docs/fleet-tools-to-host.md)
in slipgates-fleet (why send and find move into the host). **Status: design only; this PR changes no code, setting or default.**
Of the canonical documents named here, this PR edits only mcp-fleet-mode.md, agent-lite.md and host-config.md, each by a pointer or the owner quote.

## 1. The owner decision

Owner decision, Nov 23, exact time not supplied. The owner answered an ask_user question from Chief S18, relayed by Chief S18 to Quality
R9, choosing the option labelled:

> Same person's agents only (Recommended)

`docs/owner-decisions.md` in slipgates-fleet (Nov 23, "default agent-messaging scope for beta") records it in its own words, which are the
record's words and not the owner's:

> an agent may message only agent sessions of the SAME signed-in person, on any of that person's machines; nothing crosses people. This records the default only, not the code.

So the owner decided the **default scope of whom agents may message**: the same signed-in person's agent sessions, on any of that
person's machines. It is not an owner decision about any MCP tool, setting name or installer step, and it does not cover any other remote
action: launch, stop, approvals, folder trust, keys and settings stay as their documents write them. Where this design goes beyond the
record (the setting, the MCP arguments, the port), it is an Engineering proposal, and says so.

## 2. What "same person" means here

A person is the account a host is signed in to (the numeric account id the relay authenticated). Two hosts are the same person when both
are signed in to the same account id. That is what AP5-lite already enforces, so the rule is not new trust; it is a named, tested scope.

**What proves it, and what does not.** "Same person" is proven only by the relay's account authentication, relay presence, and a channel
key that matches the sender's device id. There is no signed roster and no per-device approval in this mode, so a stolen relay login is
"the same person". [agent-lite.md](../protocol/agent-lite.md) says the roster, durable duplicate memory and a full audit log are needed
before anyone beyond the owner uses lite messaging; this design does not satisfy that. Whether the owner accepts this residual for
beta testers is **not recorded** in `owner-decisions.md`, and is an open question for the Chief, not a settled one. The signed roster
(#55) replaces this trust when it lands. The rule is:

1. **Sender host:** a present host of the receiver's own account on the relay, on a channel whose device static key authenticates it.
2. **Envelope:** `account`, and the target's account, equal the receiver's account (`LiteProfile.Check`, refusal `notSameAccount`).
3. **Sender:** an agent session with an AP1 credential on its host. A person typing, a connector, a script without `read+reply`, Home
   Assistant and the HTTP MCP server are not agent senders and are refused as today.
4. **Target:** an agent session on any of the person's machines, reached by exact host device id and exact session node, and still subject
   to the receiver's own fences (`notReplyTarget`, `busy`, `composerNotEmpty`, `rate`, allow-all rules). A scope never removes a fence.
5. **Nothing crosses people.** A different account, a federated peer (F1 to F7) and a guest or link channel are refused. There is no list
   of other people to add in the beta; adding one is a new owner decision and a new scope value, not a setting.

## 3. The scope setting

One person-held setting replaces owner-only AP5-lite as the thing a person sees and changes.

| Value | Meaning |
|---|---|
| `same-person` | The rule in section 2. **The beta default.** |
| `off` | No agent sends or receives over the relay. The kill switch (`agents lite kill`) still wins |

- **Where it lives:** `agentMessaging.scope` in the host's `config.json`, next to the keys it replaces. Read afresh for each message, so a
  change holds from the next message and needs no restart.
- **Legacy keys, read forever, never written by an upgrade:** `agentMessaging.remote: "own-account"` means `same-person`; `"off"` means `off`.
  When both are present, `off` from either wins. An absent key is `same-person` (the Nov 19 default). Any other value, a section that is
  not an object and an unreadable file are `off`, as today. `allowAllTargets`, `allowAllTargetSessions` and `allowAllLaunchedTargets`
  are unchanged and are not scopes: they decide which of the person's sessions accept text, not whose.
- **Kill and re-enable.** `agents lite kill` writes `agentMessaging.remote: "off"` and a kill epoch (`lite-kill-epoch.json`), not `scope`.
  Re-enabling is unchanged: `config set agentMessaging.remote own-account`, run by a person. After a recorded kill only an **explicit**
  value turns messaging back on, never an absent key; an explicit `scope: "same-person"` counts as that explicit value only when
  `remote` is not `"off"` (off from either key wins, so setting `scope` alone does not undo a kill). `allowAllTargets`,
  `allowAllTargetSessions`, `allowAllLaunchedTargets`, `laneDelivery` and `claudeBusyQueue` still need that explicit on.
- **Who may change it:** a person at this machine, through the app (slice PR4) or `config set` run by a person. "Never an agent" is the #67
  refusal inside an agent session (`security.roster` has the same), which is a safety rail and not a security boundary
  ([local-pipe.md](../protocol/local-pipe.md)). The setting is safe because **no value wider than `same-person` exists**: a later, wider value
  (another person, a group) must not be added as a setting, only by a new owner decision and a new design. A scope that cannot be read
  is `off`.
- **Not widened by policy.** `agents.inbound.remote` and `agents.outbound.remote` (host-config.md) are a separate remote knob; neither
  widens `scope`, and `scope: "off"` stops sends whatever they say.
- **Audit:** every accepted send and every refusal keeps the existing attempt and terminal records (local-pipe.md, `agent.send`),
  with the closed outcome codes below; nothing logs text.

### Closed refusal codes

No new code is needed for the rule. A message from another person is `notSameAccount`; the scope being off is `off`; both are in
`LiteReceiver` and `agent.send` today. PR2 adds the test that proves each is returned for the case that earns it, and nothing returns
free text. A code a client does not know is shown as a generic refusal.

## 4. Sends from an agent: the MCP server

Today `reply_to_agent_session` and `list_agent_sessions` act on this machine only ([mcp-fleet-mode.md](mcp-fleet-mode.md),
[local-pipe.md](../protocol/local-pipe.md), `mcp/README.md`). **Engineering proposal, not the owner's words:** slice PR5 gives them the
person's other machines, through the host and nothing else. The record covers only the default scope (section 1); these tool changes
stay departures that [mcp-fleet-mode.md](mcp-fleet-mode.md) lists. The Chief has confirmed row 5: a `machine` argument that targets the
same person's sessions on any of that person's machines is inside the owner's Nov 23 scope (PR5a, built). The fleet list (`scope:
"fleet"`, PR5b) is not settled and waits for the owner.

- `reply_to_agent_session` takes an optional `machine` (the exact 32-hex device id from a fleet row). With it, the MCP server asks the
  **local host** for `agent.send`; without it, the call is exactly today's local reply. This is the one change the owner decision's
  substance (messaging same-person sessions on other machines) points at.
- `list_agent_sessions` takes the opt-in `scope: "fleet"` already proposed in mcp-fleet-mode.md. That is a read surface with its own
  open owner CHANGE items there (the C6 dependencies #1401 and #55, and `machineName` returned only with `include: "content"`); this design
  does not settle them. Rows are the allowlisted `fleet.list` rows; `name` and `machineName` are untrusted text; no `cwd`.
- **Whose authority.** The MCP process holds no relay credential and acts only as the agent: it is the **host binary** (section 5),
  started by the agent CLI as a stdio child of the session, so the host classes it by ancestry as that agent, with the agent's own
  `AGENT_BRIDGE_TOKEN`, and only agent ops. It never uses app-only pipe ops or a person's authority.
- Outcomes are the five of `agent.send` (`delivered`, `refused`, `undeliverable`, `unconfirmed`, `off`), unchanged; `unconfirmed` is
  never resent, and nothing falls back to Home Assistant.
- Only messaging is widened. `launch_agent_session`, `read_launch_result`, `get_launch_options`, stop, approvals and keys stay local
  and as written (section 1).

## 5. The .NET MCP server

**Engineering ruling (R10): port, do not bundle Node.** A clean beta machine, a Copilot-only one for example, may have no Node, and a
bundled runtime would make us the owner of its security patches. The MCP server becomes a **.NET stdio server shipped in the app
pack, started as a subcommand of the host binary** (never of the app: an app process risks being classed as the app, and must not carry
its authority), and its tools are thin wrappers over existing host ops, as
[fleet-tools-to-host.md](https://github.com/danswett/slipgates-fleet/blob/main/docs/fleet-tools-to-host.md) (slipgates-fleet) already says
for send and find: one audited host implementation, an MCP tool for agents. The Node server (`mcp/`) **keeps working for the fleet
until the .NET one has parity, then is retired**; the port never changes it.

Same tool names, arguments, outcomes and refusals. The existing `mcp/test` cases are the contract (tests first: each case runs against
both servers until Node is deleted). Pipe discovery (`AGENT_BRIDGE_PIPE`, then the SID and profile rules), `AGENT_BRIDGE_TOKEN` in
`agent.hello`, capability-listed tools, "no host, no tools" and the unattributed-caller limits do not change. Node is deleted in the
slice after parity is shown, not in the same one.

**Parity list** (each must pass the shared contract cases before Node is retired):

| Tool | Host op | Note |
|---|---|---|
| `list_agent_sessions` | `sessions.list` | cwd hidden by default; `scope: "fleet"` is PR5 |
| `read_agent_session` | `session.get` | |
| `reply_to_agent_session` | `session.reply` | `machine` argument is PR5 |
| `launch_agent_session` | `session.launch` | omitted role sends `default` (README) |
| `read_launch_result` | `launch.status` | |
| `get_launch_options` | `launch.options` | |
| `read_agent_usage` | `usage.get` | only with the exact `usage` capability |
| `end_agent_session` | `fleet.end`, sending `{version: 1, request, grant: all-zero UUID, session}`; never `session.stop` | **Proposed in agent-bridge PR 4286 (C4-LaunchStop, open when this was written; today Node has seven tools and a test that the surface has no stop tool, #4063); the .NET server must carry it too once it merges. PR7 does not start until PR 4286 has merged.** Input `{session_id: full UUID, request?: UUID}`; a fresh random UUID `request` by default. Needs the `sessions.launch` capability and an agent principal; not listed without a principal. Local refusals, with no pipe call: `session_id` missing or not a full UUID (16-hex short ids refused), a `request` that is not a UUID. Output `{id, request, outcome, reason?}`, `outcome` one of `ended`, `refused`, `unknown`. `unknown` (pipe unavailable, or the host reports unconfirmed) means resend the SAME `request`, never a new one. Host reasons, as `refused` plus `reason`: scope, policy (dirty or unpushed git), kill-switch, bad-argument, duplicate-id, audit-unavailable. All rules are the host's. PR6 to PR8 take its contract cases from PR 4286 |

Port size: see section 6. Its PRs come before PR3's registration step and are sent to the Engineering Lead as their own estimate.

## 6. Slices, order and what each needs

| Slice | Owner | Delivers | Needs from here |
|---|---|---|---|
| PR1 | Pairing-Notify | This design; the record's words and the departures in mcp-fleet-mode.md; a pointer in agent-lite.md and host-config.md | Nothing |
| PR2 | Pairing-Notify | `agentMessaging.scope` read in `AgentMessagingSettings`/`HostSettings`, enforced on send and receive; tests for each section 2 case; legacy keys map | Sections 2 and 3 |
| PR3 | Installer builder | Setup registers and repairs the Slipgates MCP server for each agent CLI the app already sets up (Claude, Copilot), reversibly, and ships it in the app pack. **Registers the .NET server only after it ships; until then it must not register the Node one** | Section 5 (the command and arguments to register, and that it needs no Node) |
| PR4 | Settings builder | A Settings control where a person sees and changes the scope (`same-person` / `off`), explaining in plain words who an agent may message; person-pressed only | Section 3 (key, values, legacy mapping, never an agent) |
| PR5 | Pairing-Notify | MCP fleet list and remote reply (section 4), in the .NET server | PR2, the .NET server |
| PR6 to PR8 | Pairing-Notify | The .NET server as a subcommand of the host binary: PR6 pipe client and `agent.hello` (about 380 lines); PR7 tool surface and stdio server (about 390, with `end_agent_session`); PR8 the shared contract tests against Node (about 390). Then a delete-Node PR, deletions only. Estimate about 1,400 whole lines in all | Section 5 |

Each slice ships off or behind its gate and is under the 400-line cap against its parent; if PR7 (the eighth tool) runs over, it splits
into the tool surface and the stdio server. PR4 can start once this design merges. **PR3 registers a server in the agent CLIs' own
configuration**, which changes what every agent session can do and edits files those CLIs own, so it waits for the Chief's or owner's
approval as well as for the .NET server. It writes no token or credential into the registration (the token stays the session's
`AGENT_BRIDGE_TOKEN`), and it respects `launch.workerMcp` (launched workers keep no MCP server unless listed). PR4's control only chooses
between `same-person` and `off`; it needs the app's `host.settings` path extended to this key, which PR4 names.

## 7. Tests first (what each slice proves)

- **PR2:** another account's envelope is `notSameAccount`; the same account from another machine is delivered to a fenced target;
  `scope: off`, a kill, an unknown value, a non-object section and an unreadable file send and receive nothing; `remote: "off"` wins
  over `scope: same-person`; an absent key is `same-person` only with a signed-in host and a present same-account peer; an agent session
  cannot change the scope; a stale value is not cached across messages; after a recorded kill an absent key and a `scope: "same-person"` alone stay off, and only an explicit `remote: "own-account"` re-enables.
- **PR5:** a fleet list returns only the allowlisted fields; a remote reply goes through `agent.send` and the local host's audit; an
  unattributed caller, a caller without `reply`, and a host that is off are refused; no Home Assistant reference appears in the path.
- **PR6 to PR8:** every `mcp/test` case passes against the .NET server; the server is refused every app-only pipe op and acts only with the agent's own credential; plus a launch, stop and approval tool still never reaches another machine.

## 8. Not in this design

Messaging to another person, federation, any remote launch, stop, approval or key action, the signed roster as the gate (#55 replaces
the lite trust when it lands), the scheduled-job identity question, and moving `reply-session.ps1` and the fleet scripts onto this path
(fleet-tools-migration-plan.md, step 1, which waits on HA removal).
