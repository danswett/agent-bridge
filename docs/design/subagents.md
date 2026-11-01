# Sub-agents: how a session and its delegates compose

The design #84 asks for: one model for a session and its sub-agents, the rules for what a
client shows, and the additive protocol that carries it. It supersedes #20.

- What it looks like: [ui.md](ui.md) §2 and §6.3.
- The order of work: [ui-plan.md](ui-plan.md).

*Status: the owner answered §8 on 2026-11-01, all as proposed. S1 (#173) implements §2,
§4 and §5 for Copilot, settling the four review points recorded on #173 (§5); the wire
contract is [v1-sessions.md](../protocol/v1-sessions.md). S5 (#175) implements §7's Home
Assistant and shadow parts.*

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
| `status` | `working`, `idle` (a background agent between runs, which a message can wake), `waiting` (its own question) or `ended` (it can never run again) |
| `outcome` | With `idle` and `ended`: how its latest run went, `done` or `failed`, for the `done` and `failed` glyphs (ui.md §2.1). Absent when the host never saw it end |
| `startedAt`, `endedAt` | |
| `line` | Its latest reasoning or response |
| `currentTool`, `history` | As for a session, but its own |
| `shells` | Background commands it started, counted here and not on the session (agent-ha-bridge#261) |

**What stays the session's own:** `status`, `userMessage`, `model`, `turnCount` and
`compactions` are the main agent's, as today. The session's `response` and `reasoning`
stay last-writer-wins for older clients; the main agent's own are kept beside them (see §5).

**Lifetime:**
- A woken agent (`write_agent`, or a message stamped with its `agentId`) is the same entry
  and works again; it never gets a new entry.
- An ended entry stays until the session ends. Clients fold ended entries into "N
  finished".
- **Counts:** `background.agents` is the number of entries with `kind: background` that
  are running (`working` or `waiting`), at any depth. An `idle` one waits to be woken and
  is not counted, so an idle session never reads "Delegating" for good.

## 3. Sources per agent

| Agent | What exists | Model mapping |
|---|---|---|
| **Copilot CLI** | Sub-agent events in the session's `events.jsonl`, stamped with a top-level `agentId`. `subagent.started` carries `toolCallId`, `executionMode`, `model` and, for a nested one, `parentId` (the starter's `agentId`). The starter's `task` tool call (same `toolCallId`) carries the description and prompt. `BackgroundWork` tracks runs by id (v1-sessions.md, Background work) | Full. `name` and `task` come from the `task` call's arguments. `model` comes from the sub-agent's own `subagent.started`, `subagent.configured`, `session.model_change` or `assistant.message`, never copied to the session (#73). `parent` comes from `parentId`, else the stamping on the `task` call that started it (a call stamped with an `agentId` means nested). Copilot gives sub-agents no `ask_user` (none in 33,966 sub-agent tool calls); their permission prompts are attributed through the tool call they gate. Recorded shapes: `tests/fixtures/copilot/subagents.jsonl` |
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
  // #20: who wrote the top-level response and reasoning, each on its own; a field left
  // out (or the whole object absent) is the main agent's
  "source": { "response": "agent-3a91", "reasoning": "agent-c204" },
  // the main agent's own text for each field whose top-level text is not it: a field
  // `source` attributes, or one absent where the host only skimmed the main agent's
  "own": { "response": "…", "responseTruncated": true, "reasoning": "…", "at": "…" }
}

// Subagent (lists): id, parent, kind, name (≤ 120 B), task (≤ 512 B, start kept), model,
// status, outcome?, startedAt, endedAt?, line (latest reasoning or response, ≤ 300 B,
// end kept), currentTool?, shells
```

- **Older clients** keep reading top-level `response` and `reasoning`, which stay
  last-writer-wins. The only extra is that `source` says whose they are.
- **New clients:**
  - for each of `response` and `reasoning`, render `own`'s while `source` names a sub-agent
    for it, otherwise the top-level one, or `own`'s when that is absent, so the main
    agent's answer never disappears;
  - render the sub-agent's words only from `subagents[]` or `session.get`.
- **Full detail for one sub-agent:** `session.get { id, agent }` returns its `response`
  (≤ 16 KiB), `reasoning` (≤ 4 KiB) and `history` (latest 50). Lists never carry these.
- **`SessionBudget.Fit`:** every field here is trimmed before any field an older client
  reads, so a session with sub-agents never shows an older client (or the HA connector) less
  than it would without them. In this order:
  - drop finished entries (`idle` and `ended`);
  - drop every `line`;
  - drop every `task`;
  - cap the list at 8 entries and set `subagentsMore`;
  - drop `own.reasoning`;
  - cut `own.response` to 4 KiB, then drop it, keeping `own.responseTruncated`;
  - drop every entry's `currentTool`;
  - drop the remaining entries, counted in `subagentsMore`.

  Only then the older steps, from tool history on, exactly as for the same session without
  these fields. `own` cannot simply stay whole: 16 KiB of emoji is 48 KiB of JSON.
- **Changes that move `updatedAt`:** an entry starting, ending, or changing status or
  outcome. A `line` changing does **not** move it, so busy delegates don't flood upserts.
  The latest line arrives with the next change, or on `session.get`.
- **Questions from sub-agents:** a Question gains an optional `agent` field. The host sets
  `answerAtMachine` unless it can prove where the keys land. For a Copilot `ask_user` that
  would need the pending call stamped with that `agentId`, no other prompt pending, and a
  delivery path that types into a sub-agent's call; the host's refuses stamped calls today,
  so S1 marks every sub-agent `ask_user` `answerAtMachine` (and Copilot gives sub-agents
  none, §3). A Copilot permission prompt is the CLI's one dialog whoever asked, and keeps
  the proof it already has (`CopilotPermission`), so it is answered as the main agent's is.

**The review points recorded on #173, as settled in S1:**
1. *Terminal outcome:* `outcome` (`done` or `failed`) on `idle` and `ended` entries.
2. *What counts as running:* only `working` or `waiting` background entries; `idle` ones
   stay listed for later wakes.
3. *Per-field provenance:* `source.response` and `source.reasoning` are separate, and
   `own` carries the main agent's text for the fields `source` attributes, and for one the
   host has only from its skim of the transcript (the top-level field then absent).
4. *Budget for `own`:* its field limits are the legacy ones, `own.responseTruncated` is
   its cut signal, and it is trimmed before any legacy field, with the rest of the
   sub-agent fields (§5), so older clients never lose text to it.

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
- `response` and `reasoning` publish the main agent's own text (`own` for a field `source`
  attributes to a sub-agent), so the HA card never shows a delegate's line as the main
  agent's. The activity line, the trail and the "finished" push follow the same rule.
- A new `subagents` attribute on the activity sensor carries name, status and line for at
  most 5 entries, and `subagents_more` counts the rest. Each field keeps its protocol
  limit, so five stay a few KiB however many delegates there are. The card draws neither yet.
- A sub-agent's question is armed and answered through the session's own question path.
  Its name leads the question text ("*name* asks:"), and the decision entity gains `agent`
  and `agent_name` beside it. Its pushes keep the session's tag.
- **agent-ha-bridge keeps its behaviour** (the owner's decision, 2026-11-01): its card shows
  whichever agent wrote last, a delegate's text included. This is a known, intentional
  difference between the two cards, not something either side will converge on.

Details: [ha-connector.md](../ha-connector.md), stage 1 and stage 2.

**Shadow (#40):** the top-level `response` stays last-writer-wins (§5), and that is what the
shadow compares with the daemon's, so a real parser mismatch is never waived. What changed
is what the HA connector projects. While the two sides agree on the latest writer's text
and it is a sub-agent's, the host's card shows the main agent's instead. The shadow labels
that `main-agent-only (#175)`, as an intentional known difference
([host-shadow.md](../host-shadow.md)). `reasoning` is not compared.

## 8. Questions for the owner (decided)

The owner answered all three on 2026-11-01, as proposed:

1. **Finished delegates stay until the session ends**, not only until the main agent's next
   prompt.
2. **A delegate's question pushes the phone**, with the same tag as the session's, so the
   newer of the two questions replaces the older.
3. **No per-delegate Stop.** It is left to the main agent ("Ask the main agent to stop it").
