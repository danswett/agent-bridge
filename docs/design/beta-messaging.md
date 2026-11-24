# Beta messaging between a person's agents (program C, slice C3)

Part of program C (beta messaging), slice C3; builds on AP5-lite (#728) and the native relay send (#1233). Companions:
[agent-lite.md](../protocol/agent-lite.md) (the runbook), [agent-principals.md](agent-principals.md) (AP5-lite slices L1 to L7),
[mcp-fleet-mode.md](mcp-fleet-mode.md) (the MCP server's reach), [fleet-tools-to-host](https://github.com/danswett/slipgates-fleet/blob/main/docs/fleet-tools-to-host.md)
in slipgates-fleet (why send and find move into the host). **Status: design only; this PR changes no code, setting or default.**
Of the canonical documents named here, this PR edits only mcp-fleet-mode.md, agent-lite.md and host-config.md, each by a pointer or the owner quote.

## 1. The owner decision

Owner decision, November 23, 2026, 18:20 PT, an ask to the Chief, option "Same person agents only", relayed by the Engineering Lead:

> An agent may message only agent sessions of the same signed-in person, on any of their machines; nothing crosses people.

This is the beta default. It is the owner change for **remote messaging only**. It does not cover any other remote action: launch, stop,
approvals, folder trust, keys and settings stay as their documents write them.

## 2. What "same person" means here

A person is the account a host is signed in to (the numeric account id the relay authenticated). Two hosts are the same person when both
are signed in to the same account id. That is what AP5-lite already enforces, so the rule is not new trust; it is a named, tested scope:

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
| `off` | No agent sends or receives over the relay. The kill switch (`agents lite kill`) writes this and still wins |

- **Where it lives:** `agentMessaging.scope` in the host's `config.json`, next to the keys it replaces. Read afresh for each message, so a
  change holds from the next message and needs no restart.
- **Legacy keys, read forever, never written by an upgrade:** `agentMessaging.remote: "own-account"` means `same-person`; `"off"` means `off`.
  When both are present, `off` from either wins. An absent key is `same-person` (the Nov 19 default). Any other value, a section that is
  not an object and an unreadable file are `off`, as today. `allowAllTargets`, `allowAllTargetSessions` and `allowAllLaunchedTargets`
  are unchanged and are not scopes: they decide which of the person's sessions accept text, not whose.
- **Who may change it:** a person at this machine, through the app (slice PR4) or `config set` run by a person. Never an agent: it is
  refused inside an agent session like `security.roster` is (#67), and no message route reaches it. A scope that cannot be read is `off`.
- **Audit:** every accepted send and every refusal keeps the existing attempt and terminal records (local-pipe.md, `agent.send`),
  with the closed outcome codes below; nothing logs text.

### Closed refusal codes

No new code is needed for the rule. A message from another person is `notSameAccount`; the scope being off is `off`; both are in
`LiteReceiver` and `agent.send` today. PR2 adds the test that proves each is returned for the case that earns it, and nothing returns
free text. A code a client does not know is shown as a generic refusal.

## 4. Sends from an agent: the MCP server

Today `reply_to_agent_session` and `list_agent_sessions` act on this machine only ([mcp-fleet-mode.md](mcp-fleet-mode.md)). Slice PR5
gives them the person's other machines, through the host and nothing else:

- `list_agent_sessions` takes the opt-in `scope: "fleet"` already proposed there. Rows are the allowlisted `fleet.list` rows; `name`
  and `machineName` are untrusted text, shown only with `include: "content"`; no `cwd`.
- `reply_to_agent_session` takes an optional `machine` (the exact 32-hex device id from a fleet row). With it, the MCP server asks the
  **local host** for `agent.send`; without it, the call is exactly today's local reply. The MCP process holds no relay credential.
- Outcomes are the five of `agent.send` (`delivered`, `refused`, `undeliverable`, `unconfirmed`, `off`), unchanged; `unconfirmed` is
  never resent, and nothing falls back to Home Assistant.
- Only messaging is widened. `launch_agent_session`, `read_launch_result`, `get_launch_options`, stop, approvals and keys stay local
  and as written (section 1).

## 5. The .NET MCP server

The Node server (`mcp/`) is replaced by a .NET one so the installer ships it without a Node runtime. Same tool names, same arguments,
same outcomes, same refusals; the existing `mcp/test` cases become the contract the port is tested against (tests first: each case
runs against both servers until the Node one is deleted). Pipe discovery (`AGENT_BRIDGE_PIPE`, then the SID and profile rules),
`AGENT_BRIDGE_TOKEN` in `agent.hello`, the capability-listed tools, "no host, no tools" and the unattributed-caller limits do not change.
The Node server is deleted in the slice after parity is shown, not in the same one.

## 6. Slices, order and what each needs

| Slice | Owner | Delivers | Needs from here |
|---|---|---|---|
| PR1 | Pairing-Notify | This design; the quote in mcp-fleet-mode.md; a pointer in agent-lite.md and host-config.md | Nothing |
| PR2 | Pairing-Notify | `agentMessaging.scope` read in `AgentMessagingSettings`/`HostSettings`, enforced on send and receive; tests for each section 2 case; legacy keys map | Sections 2 and 3 |
| PR3 | Installer builder | Setup registers and repairs the Slipgates MCP server for each agent CLI the app already sets up (Claude, Copilot), reversibly, and ships it in the app pack. **Registers the .NET server only after it ships; until then it must not register the Node one** | Section 5 (the command and arguments to register, and that it needs no Node) |
| PR4 | Settings builder | A Settings control where a person sees and changes the scope (`same-person` / `off`), explaining in plain words who an agent may message; person-pressed only | Section 3 (key, values, legacy mapping, never an agent) |
| PR5 | Pairing-Notify | MCP fleet list and remote reply (section 4), in the .NET server | PR2, the .NET server |
| PR6 to PR8 | Pairing-Notify | The .NET server: tool surface and pipe client; the contract tests against Node; delete Node | Section 5 |

Each slice ships off or behind its gate and is under the 400-line cap against its parent. PR3 and PR4 can start once this design merges;
PR3's registration step waits for the .NET server, PR4 does not.

## 7. Tests first (what each slice proves)

- **PR2:** another account's envelope is `notSameAccount`; the same account from another machine is delivered to a fenced target;
  `scope: off`, a kill, an unknown value, a non-object section and an unreadable file send and receive nothing; `remote: "off"` wins
  over `scope: same-person`; an absent key is `same-person` only with a signed-in host and a present same-account peer; an agent session
  cannot change the scope; a stale value is not cached across messages.
- **PR5:** a fleet list returns only the allowlisted fields; a remote reply goes through `agent.send` and the local host's audit; an
  unattributed caller, a caller without `reply`, and a host that is off are refused; no Home Assistant reference appears in the path.
- **PR6 to PR8:** every `mcp/test` case passes against the .NET server, plus a launch, stop and approval tool still never reaches another machine.

## 8. Not in this design

Messaging to another person, federation, any remote launch, stop, approval or key action, the signed roster as the gate (#55 replaces
the lite trust when it lands), the scheduled-job identity question, and moving `reply-session.ps1` and the fleet scripts onto this path
(fleet-tools-migration-plan.md, step 1, which waits on HA removal).
