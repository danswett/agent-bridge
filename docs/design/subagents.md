# Sub-agents: how a session and its delegates compose

The design #84 asks for: one model for a session and its sub-agents, the rules for what a
client shows, and the additive protocol that carries it. It supersedes #20.

- What it looks like: [ui.md](ui.md) §2 and §6.3.
- The order of work: [ui-plan.md](ui-plan.md).

*Status: the owner answered §8 on 2026-11-01, all as proposed. S1 (#173) implements §2,
§4 and §5 for Copilot, settling the four review points recorded on #173 (§5); the wire
contract is [v1-sessions.md](../protocol/v1-sessions.md). S5 (#175) implements §7's Home
Assistant and shadow parts. S2 (#174) reads Claude's and Codex's delegates (§3), verified
against recorded transcripts.*

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
| `id` | The agent's own id: Copilot `agentId`, Claude `agentId` (its sidechain's), Codex thread id |
| `parent` | The id of the agent that started it; `null` for the main agent's direct sub-agents |
| `kind` | `background` (runs past its starter's turn) or `task` (part of its starter's turn) |
| `name` | Short label: the `task` call's description, else its agent type; cleaned and capped by the host (§6) |
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
| **Claude Code** | Verified on 2.1.316 (recorded shapes: `tests/fixtures/claude`). In the session's own transcript: an `Agent` `tool_use` (`description`, `prompt`, `subagent_type`) whose `tool_result` entry carries `toolUseResult` `{isAsync: true, status: "async_launched", agentId, description, prompt, resolvedModel}`; 2.1.316 launched every `Agent` call that way (`requestShape: background`). A `Bash` call with `run_in_background: true` gets `toolUseResult.backgroundTaskId`. Each one's end is a `<task-notification>` (`<task-id>`, `<tool-use-id>`, `<status>completed</status>`, `<summary>`, `<result>`): queued at once as a `queue-operation` `enqueue` for every agent at every depth, then delivered as a `user` entry (`origin.kind: "task-notification"`, `turnOrigin: "task_notification"`, `promptSource: "system"`; a typed prompt is `human`/`typed`) to whoever started it, the main agent or (for a nested one, the queue's `remove`, `delivered_to_agent`) the sub-agent. The queue carries no such mark, and a prompt typed while the agent works is queued too, so only a marked delivery (or a `remove` delivering a notice queued earlier) is taken as an end. An agent notifies only once it has no live background agents of its own, and again each time a message (`SendMessage` with `to`) has woken it. Sidechain entries are no longer in the session's transcript: each sub-agent's are in `<session id>/subagents/agent-<agentId>.jsonl` (every entry `isSidechain: true` with its `agentId`), beside `agent-<agentId>.meta.json` (`agentType`, `description`, `toolUseId`, `spawnDepth`, `requestShape`, `finished`, and `parentAgentId` for a nested one). A nested `Agent` call is the starter's own sidechain entry, and its start is written nowhere else | Full. `id` is the `agentId`, `kind` `background`. `name` and `task` are `toolUseResult`'s `description` and `prompt`, `model` its `resolvedModel`; a nested agent's come from its `meta.json` (`description`, `parentAgentId` for `parent`) and the first entry of its own transcript (when it started, and its prompt), with no `model`. A notice ends the run, `done` for `completed` and `failed` for any other status, leaving the agent `idle` (a message can wake it); it also ends any run still going under that agent. The notice delivered to the main agent starts a turn of its own: never the person's message, and the sub-agent's result in it never reaches the session's text, nor does anything else a sub-agent writes. `run_in_background` commands count in `background.shells` until their notice |
| **Codex** | Verified on 0.166.0 with `multi_agent` v1 (recorded shapes: `tests/fixtures/codex`). Each sub-agent is a thread with its own rollout and lock; its `session_meta` has `source.subagent.thread_spawn` (`parent_thread_id`, `depth`, `agent_nickname`, `agent_role`), `parent_thread_id` and `thread_source: "subagent"` (#62 keeps it from being a session). The session's own rollout records each multi-agent tool call as an `item_completed` of type `CollabAgentToolCall`: `tool` (`spawn_agent`, `wait`; Codex's schema also has `send_input`, `resume_agent`, `close_agent` and others), `sender_thread_id`, `receiver_thread_ids`, `receiver_agents` (`thread_id`, `agent_nickname`), and `agents_states` per thread (`pending_init`, `running`, `interrupted`, `shutdown`, `not_found`, `{"completed": text}`, `{"errored": text}`); `spawn_agent`'s carries `prompt` and `model`. A sub-agent that stops tells the session in a `user` message `<subagent_notification>{"agent_path": thread id, "status": …}</subagent_notification>`. Its own words are only in its own rollout | Full for the session's own sub-agents. `id` is the thread id, `kind` `background` (it runs beside its starter's turn and takes more input until closed), `name` its `agent_nickname`, `task` and `model` the spawn's. `completed` ends its run `done`, with its text as `line`; `errored` and `interrupted` `failed`; it is then `idle`. `send_input` and the like wake it, `close_agent`, `shutdown` and `not_found` end it. A sub-agent's own sub-agents are in its rollout, which the host does not read: neither listed nor counted (no recording has one) |

Recorded on 2026-11-01 PT by the shadow soak on a disposable rehearsal VM, and redacted into
those fixtures: every text, id, path and time replaced, the structure kept. What no recording
showed is read from the shapes they do show and, for Codex, its own schema: a Claude notice
other than `completed`, `SendMessage` waking a Claude agent, a Codex `errored` or
`interrupted` state, `send_input` and `close_agent`. Each has a test built on the recorded
entries, and is the first thing to check against the next recording.

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

// Subagent (lists): id, parent, kind, name (≤ 60 chars and 120 B, cleaned: §6), task (≤ 512 B, start kept), model,
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
  agent by its id alone, never by its `name`. End session ends everything.
- **No action may reach the main agent while appearing to target a sub-agent.** Every
  sub-agent action is labelled with "main agent".
- **A sub-agent's `name` is its own words.** A prompt-injected sub-agent picks it, so clients
  never show it as sent and never quote it into a reply. They show the safe label of
  [ui.md §6.3](ui.md#63-delegates-84): bidi, zero-width and other invisible characters
  removed, whitespace collapsed, cut to 60 characters, the id's start when nothing is left,
  and `Delegate “<name>”` for a name that reads as "Main agent", look-alike letters included.
  A line that says a sub-agent asks or works always puts the word "Delegate" before its label
  ("Delegate *name* asks"), so no name, however spelt, reads as "Main agent asks".
- **The host sends `name` already cleaned (#263).** A prompt-injected sub-agent picks its
  name, and not every reader is a client that cleans it (Home Assistant's `subagents`
  attribute, its question text and pushes, future clients). So the host applies the clients'
  rules (ui.md §6.3) once, where it makes the name, for every agent (`DelegateName.Clean`):
  1. control, bidi (U+202A–202E, U+2066–2069, U+200E/200F, U+061C), format (zero-width
     space and joiners, BOM, soft hyphen, tags) and other default-ignorable characters
     (variation selectors, Hangul fillers) go, and any run of whitespace, line and paragraph
     separators included, becomes one space;
  2. longer than 60 characters or 120 bytes, it is cut at a whole character with "…";
  3. with nothing left, the next source is used (display name, then agent type), else
     `name` is absent and clients name the delegate by its id;
  4. a name that reads as "Main agent" after folding case, compatibility forms, marks and
     common look-alike letters is sent as `Delegate “<name>”` (its own quotes made `'`),
     within the same caps. A name already in that form is left as it is, so cleaning again
     changes nothing.

  Every line the host or the HA connector writes that says a sub-agent asks (the host's push
  titles, HA's question text and push titles) puts the word "Delegate" before the cleaned
  name (`DelegateName.Titled`), as the clients do: the look-alike list cannot be complete
  ("Ma1n agent" passes it), and the main agent's own lines never have the word. The HA
  connector cleans again with the same function, and clients keep their own cleaning and
  their "Delegate" prefix as defence in depth.
  The shadow (#40) compares no sub-agent names, since agent-ha-bridge lists none, so a cleaned
  name never reads as a mismatch.

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
  limit, and the entries make way for the card's own text within Home Assistant's 16 KiB.
  The card draws neither yet.
- A sub-agent's question is armed and answered through the session's own question path.
  Its name leads the question text ("Delegate *name* asks:"), and the decision entity gains `agent`
  and `agent_name` beside it. Its pushes are titled "Delegate *name* asks · …" and keep the session's
  tag.
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
