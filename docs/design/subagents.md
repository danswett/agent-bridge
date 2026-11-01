# Sub-agents: how a session and its delegates compose

The design #84 asks for: one model for a session and its sub-agents, the rules for what a
client shows, and the additive protocol that carries it. It supersedes #20.

- What it looks like: [ui.md](ui.md) §2 and §6.3.
- The order of work: [ui-plan.md](ui-plan.md).

*Status: proposed 2026-11-01, awaiting the owner's sign-off.* The owner's three open
questions are in §8.

**Words.** The UI says *delegate*; specs and code say *sub-agent*. The *main agent* is the
session's own agent.

## 1. Problem

Today every field decides on its own how a sub-agent affects it, mostly last-writer-wins,
inherited from agent-ha-bridge:
- `response` and `reasoning` "may be a subagent's" (v1-sessions.md, Activity).
- `background` carries only counts.

So a delegate's thinking replaces the main agent's answer on the card, nothing says who
wrote a line, and the card flips between agents when several run (#84, Symptoms).

## 2. Model

A session is a tree: the main agent plus sub-agents. Each sub-agent may start sub-agents
of its own.

| Field | Meaning |
|---|---|
| `id` | The agent's own id: Copilot `agentId`, Claude sidechain id, Codex thread id |
| `parent` | The id of the agent that started it; `null` for the main agent's direct sub-agents |
| `kind` | `background` (runs past its starter's turn) or `task` (part of its starter's turn) |
| `name` | Short label: the `task` call's description, else its agent type |
| `task` | The prompt it was given |
| `model` | Its own model, never copied to the session |
| `status` | `working`, `idle` (a background agent waiting to be woken), `waiting` (its own question) or `ended` |
| `startedAt`, `endedAt` | |
| `latest` | Its latest reasoning or response |
| `currentTool`, `history` | As for a session, but its own |
| `shells` | Background commands it started, counted here and not on the session (agent-ha-bridge#261) |

**What stays the session's own:** `status`, `userMessage`, `model`, `turnCount` and
`compactions` are the main agent's, as today. The session's `response` and `reasoning`
become the main agent's own (see §5 for how older clients are kept working).

**Lifetime:**
- A woken agent (`write_agent`, or a message stamped with its `agentId`) is the same entry
  and works again; it never gets a new entry.
- An ended entry stays until the session ends. Clients fold ended entries into "N
  finished".
- **Counts:** `background.agents` is the number of entries with `kind: background` that
  are not `ended`, at any depth.

## 3. Sources per agent

| Agent | What exists | Model mapping |
|---|---|---|
| **Copilot CLI** | Sub-agent events in the session's `events.jsonl`, stamped with a top-level `agentId`. `subagent.started` carries `toolCallId` and `executionMode`. The starter's `task` tool call (same `toolCallId`) carries the description and prompt. `BackgroundWork` already tracks runs by id (v1-sessions.md, Background work) | Full. `name` and `task` come from the `task` call's arguments. `model` comes from the sub-agent's own `session.model_change` or `assistant.message`, which `CopilotParser` currently drops (#73). `parent` comes from the stamping on the `task` call that started it (a call stamped with an `agentId` means nested) |
| **Claude Code** | `Task` tool calls in the transcript, plus sidechain entries (`isSidechain: true`), which `ClaudeParser` skips today. Background Bash via `run_in_background` | **To verify against a recorded transcript:** whether sidechain entries carry a stable per-task id, and whether nested `Task` calls are distinguishable. Until then, rule 8: a count from open `Task` calls, no entries |
| **Codex** | Sub-agent threads as separate rollouts and locks (#62); their `session_meta` names a parent thread | **To verify:** the thread-to-parent link and the status events. Until then, rule 8 |

Everything above that is marked "to verify" becomes a recorded fixture under
`tests/fixtures` before any parser relies on it, as #84's acceptance criteria require.

## 4. Status precedence

First match wins. The text is in [ui.md](ui.md) §2.2.

1. The session's own question → `waiting`.
2. A sub-agent's question → `waiting`, with `waitingOn` naming the sub-agent.
3. The main agent's turn is running → `working`.
4. Main agent idle, a background sub-agent not ended → `idle` with `background`, shown as
   delegating.
5. Main agent idle, background commands only → the same, shown as "N background commands".
6. Otherwise → `idle`.

Rules 3–6 are exactly today's behaviour. Rule 2 is new and needs `Session.waitingOn` (§5).

## 5. Protocol (v1, additive)

```jsonc
// Session, new fields
"subagents": [ Subagent ],        // absent when none; running first, then most recently ended
"subagentsMore": 3,               // entries dropped by the cap, if any
"waitingOn": "agent-c204",        // rule 2 only: the sub-agent whose question is pending
"activity": {
  "source": { "agent": "agent-3a91" },          // #20: who wrote top-level response/reasoning; absent = the main agent
  "own": { "response": "…", "reasoning": "…", "at": "…" }  // the main agent's latest, sent only while `source` is set
}

// Subagent (lists): id, parent, kind, name, task (≤ 512 B, start kept), model,
// status, startedAt, endedAt?, line (latest reasoning or response, ≤ 300 B, end kept),
// currentTool?, shells
```

- **Older clients** keep reading top-level `response` and `reasoning`, which stay
  last-writer-wins. The only extra is that `source` says whose they are.
- **New clients:**
  - render `own` while `source` is set, so the main agent's answer never disappears;
  - render the sub-agent's words only from `subagents[]` or `session.get`.
- **Full detail for one sub-agent:** `session.get { id, agent }` returns its `response`
  (≤ 16 KiB), `reasoning` (≤ 4 KiB) and `history` (latest 50). Lists never carry these.
- **`SessionBudget.Fit`:** after "tool history" (step 1), trim `subagents` in this order:
  - drop ended entries;
  - drop every `line`;
  - drop every `task`;
  - cap the list at 8 entries and set `subagentsMore`.
- **Changes that move `updatedAt`:** an entry starting, ending, or changing status. A
  `line` changing does **not** move it, so busy delegates don't flood upserts. The latest
  line arrives with the next change, or on `session.get`.
- **Questions from sub-agents:** a Question gains an optional `agent` field. The host sets
  `answerAtMachine` unless it can prove where the keys land. For Copilot that means the
  pending `ask_user` call is stamped with that `agentId` and no other prompt is pending.

The spec text goes into `docs/protocol/v1-sessions.md` in the same PR as the host change.

## 6. Interaction rules

- **Replies only ever go to the main agent.** No client offers to type to a sub-agent.
- **No per-sub-agent Stop.** "Ask main agent to stop it" pre-fills a reply naming the
  agent. End session ends everything.
- **No action may reach the main agent while appearing to target a sub-agent.** Every
  sub-agent action is labelled with "main agent".

## 7. Notifications and Home Assistant

**Pushes and toasts:**
- "Finished" fires on the main agent's turn only.
- "Question" fires for the session's and a sub-agent's questions alike. The title names the
  sub-agent, and both use the same tag, so the newer replaces the older.

**HA connector (single-slot entity attributes, 16 KB limit):**
- `response` and `reasoning` publish `own` when present, so the HA card never shows a
  delegate's line as the main agent's.
- A new `subagents` attribute carries name, status and line for at most 5 entries.
- Whether agent-ha-bridge adopts the same rules or records them as known differences is
  decided in the HA slice (ui-plan.md, slice S5).

**Shadow (#40):** once the host stops last-writer-wins, response mismatches against the
daemon become intentional differences, and are documented in host-shadow.md.

## 8. Open questions for the owner

1. Do finished delegates stay until the session ends, or only until the main agent's next
   prompt? *Proposed: until the session ends.*
2. Does a delegate's question push the phone, or only appear in Needs you? *Proposed:
   push, with the same tag as the session's.*
3. Is a per-delegate Stop worth a Copilot spike, or do we leave it to the main agent?
   *Proposed: leave it to the main agent.*
