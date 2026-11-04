# Bridge Protocol v1: session history

*Proposed for #292, 2026-11-04. The conversation pane's history (ui.md §6.4 History, H0–H8,
decided by the owner on 2026-11-04 as H1–H9 on #292) needs earlier turns than `activity`
carries. This is the wire format for them.*

`activity` ([v1-sessions.md](v1-sessions.md#activity)) carries only a session's latest prompt,
response and reasoning. `session.history` pages back through the turns before it, from the
transcript the host already reads, so a client can draw a session's whole conversation as one
column. Every rule of [v1-sessions.md](v1-sessions.md) holds here: JSON and its sizes, times,
[Malformed records](v1-sessions.md#malformed-records), the channel framing and its deflate.
C# records matching this document are in `src/AgentBridge.Protocol/History.cs`.

## What it needs to do

From ui.md §6.4 H8 and #292:

* turns with stable ids, in order, each with its time;
* a page of turns before a given turn (a before-cursor), and whether older ones remain;
* a flag on a turn cut short or unreadable;
* live turns carrying the same ids, so a turn loaded twice is drawn once;
* the start of the session marked ("Start of session · date", H6), and history that stops short
  of it said so ("Start of available history");
* about 20 turns a page and 500 kept (H3); resolved questions and approvals kept as far back as
  history goes (H4);
* a turn is a prompt and everything until the next prompt, scheduled and agent-sent prompts
  included (H7, #489).

## Measured first

Before choosing caps, the transcripts on one heavily used development machine were measured on
2026-11-04 (counts and sizes only; the script read no content into its output). Copilot: 466
transcripts, 3.6 GB, the largest 482 MB. Claude: 1 small transcript. Codex: none on that
machine, so Codex's numbers come from its recorded fixtures only.

| Copilot, per session | median | p90 | p99 | max |
|---|---|---|---|---|
| Turns | 1 | 20 | 138 | 486 |

| Copilot, per turn | median | p90 | p99 | max |
|---|---|---|---|---|
| Transcript bytes | 177 KB | 1.8 MB | 14.6 MB | 64 MB |
| Prompt text | 491 B | 5.9 KB | 7.4 KB | 12.9 KB |
| Response text (all of the main agent's) | 506 B | 2.6 KB | 11.7 KB | 83 KB |
| Reasoning text | 93 B | 7.5 KB | 126 KB | 1.3 MB |
| Tool calls | 5 | 34 | 206 | 2,257 |
| Questions | 0 | 0 | 3 | 13 |
| Approvals | 0 | 3 | 12 | 120 |
| Delegate starts and ends | 0 | 0 | 6 | 36 |

Reading 20 turns from the end of the three largest transcripts: a full parse took 3.3–8.4 s;
reading backwards to the 21st prompt and parsing only that took 86–784 ms (5–21 MB read). No
session had more than 500 turns. So:

* **A page is read backwards from its cursor**, never by parsing the transcript from the start.
* **A turn is mostly bytes no client sees.** Tool results and delegates' records are most of a
  turn's 177 KB; what a client shows is a few KB. Lines are filtered by a byte search before any
  is parsed as JSON, as the tail already does ([TranscriptTail](#what-a-host-reads)).
* **Reasoning is the long tail.** It is cut per part as `activity.reasoning` is, and goes first
  when a turn is over its budget.
* **A page is bounded by bytes as well as turns.** 20 turns of median size fit one message;
  20 at p90 do not. A host sends as many as fit, at least one, and a client asks again.

## Capability

A host that serves history lists `sessions.history` in `hello`'s capabilities. A client that
does not see it sends no `session.history`, and says "Earlier messages aren't available from this
machine" above the oldest message it has (ui.md §6.4 H5). Older clients never send the op and see
nothing new but `activity.turn` and `activity.responseTurn` ([below](#live-turns-and-history)), which they ignore.

## `session.history`

Request `data`, read strictly (a field missing where required or of the wrong type fails the
request, as for every request a client sends):

```json
{ "id": "4f0c2a1e-9d3b-4e6f-8a7c-5b1d2e3f4a5b", "before": "h1.AAECAwQF…", "limit": 20 }
```

| Field | Notes |
|---|---|
| `id` | The session, as in `session.get` |
| `before` | Absent for the newest page. Otherwise the `before` of the page the client last received for this session: the page holds the turns before it. At most 256 bytes |
| `limit` | Turns wanted, 1 to 50. Absent means 20 (H3). A host may send fewer, never more |

The result is one of:

| Result | Meaning |
|---|---|
| A page: `{ "turns": [Turn…], "before"?: …, "start"?: {…}, "partial"?: true }` | See below |
| `{ "missing": true }` | The session is not one this host lists now: it has ended and been dropped, or never was. As `session.get` |
| `{ "stale": true }` | `before` is not one this host can continue from: it is malformed, was made for another session or another transcript, or its turn is no longer where it was. The client keeps what it has drawn, asks again without `before`, and merges the new pages by turn id ([Live turns and history](#live-turns-and-history)) |
| `{ "unavailable": "<why, in words>" }` | The host has no history for this session: its transcript can't be found or read, or this agent's transcripts aren't read for history. At most 256 bytes. A client shows "Earlier messages aren't available from this machine" (H5) and may show the reason as secondary text |

A request that is not an object, has no `id`, or has a `before` or `limit` of the wrong type or
out of range fails as a request (`{"ok": false, "error": …}`), as for any malformed request.

### A page

```json
{
  "turns": [
    {
      "id": "w8Vb3l0V7lH2eQ3cXk1rYA",
      "at": "2026-10-28T04:22:43.182Z",
      "prompt": { "text": "Run the tests" },
      "parts": [
        { "id": "Hq2…0", "kind": "reasoning", "at": "2026-10-28T04:22:45.010Z", "text": "The suite is in tests/." },
        { "id": "Hq2…1", "kind": "tool", "at": "2026-10-28T04:22:45.010Z", "tool": { "id": "call_6", "name": "powershell", "summary": "Run the protocol tests", "status": "done", "startedAt": "2026-10-28T04:22:45.010Z", "endedAt": "2026-10-28T04:23:40.000Z" } },
        { "id": "Zp1…0", "kind": "response", "at": "2026-10-28T04:23:41.500Z", "text": "All **47** tests pass." }
      ]
    }
  ],
  "before": "h1.AAECAwQF…",
  "partial": true
}
```

| Field | Notes |
|---|---|
| `turns` | [Turn](#turn)s, **oldest first**: the newest `limit` turns before `before` (or the newest of the session's, without it) that fit the message. At least one unless there are none before it |
| `before` | The cursor for the page before this one: pass it to get older turns. **Absent when nothing older will be served**, and then `start` is present |
| `start` | Present exactly when `before` is absent: this page reaches the start of what the host serves. `{ "at": time }`: the start of the session, its first record's time, for "Start of session · Mon, Nov 9, 8:31 AM PT" (H6). `{ "limited": true }`, with `at` absent: the host serves no more (the 500-turn limit, or a turn longer than it reads back through; [Caps](#caps)), and a client shows "Start of available history" |
| `partial` | Not everything of this page's turns is here: a turn here is `unreadable` or `truncated` (read only in part, or cut to its caps). A client shows what it has. Absent otherwise |

**The typed states**, never a silent empty list:

* **Empty:** `turns` empty, `start.at` present: the transcript is readable and no prompt has
  been sent yet. "No messages yet" (H5).
* **Partial:** `partial` set: the page's `unreadable` and `truncated` turns say where.
* **Unavailable:** the `unavailable` result.
* **Stale cursor:** the `stale` result.
* **Gone:** the `missing` result.

### Turn

A prompt and everything until the next prompt (H7).

| Field | Type | Notes |
|---|---|---|
| `id` | string | **Strict (identity).** Stable: the same turn has the same id on every page, in `activity.turn` while it is the latest, after the host restarts and as the transcript grows. Opaque, at most 64 bytes. Unique within the session |
| `at` | time | When the prompt was sent: the transcript's own time on it |
| `prompt` | Prompt? | What started the turn. Absent only on an `unreadable` turn |
| `parts` | Part[] | Everything else in the turn, in the order the transcript has it. Empty when there is nothing (a turn just started, or a prompt the agent never answered) |
| `partsMore` | number? | Parts left out: by the per-turn count caps, or by the turn's budget. Absent when none are |
| `truncated` | bool? | Something in the turn was cut or left out: a part's text cut (`truncated` on it), parts left out (`partsMore`), or only the end of a long turn read ([Caps](#caps)). A client ends the turn with "Cut short · see the machine" (H5) |
| `unreadable` | bool? | The host found a record that starts a turn here but could not read it (malformed, or longer than the host reads). It holds only `id` and `at` (`at` is the time of the first record after it that has one). A client draws "A turn here couldn't be read" in its place, keeping the turns around it (H5) |

#### Prompt

| Field | Type | Notes |
|---|---|---|
| `text` | string | The prompt as the agent received it, at most **8 KiB**, the **start** kept |
| `truncated` | bool? | `text` was cut |
| `from` | `"person"` \| `"schedule"` \| `"agent"` \| `"other"`? | **Strict (security).** Who wrote it when it was not the person typing, exactly as `activity.userMessageFrom` (#489): taken from what the agent's own record says, never parsed from the text, or `agent` for a prompt the host itself knew an agent sent through it (a reply or launch, which the transcript does not mark) and so showed as the agent's while it was the latest; the host keeps that for its newest 500 turns while it runs. Absent means the person. An unknown value reads as `other`, never as the person's |
| `label` | string? | As `activity.userMessageLabel`: a schedule's number, at most 64 bytes |

A prompt that can't be read as its `from` says (a `from` of the wrong type) refuses the turn, as
`activity.userMessageFrom` refuses `activity`: read as absent, an agent's prompt would be drawn as
the person's own words. A refused turn is left out of `turns` (Malformed records rule 2), and the
client reads the page without it.

**What is a prompt.** Exactly what makes `activity.userMessage` change today, for each agent
([Where a host gets each part](#where-a-host-gets-each-part)). Context an agent is given that no
one sent (Copilot's memory, a skill's context, a shell's completion notice; Claude's task
notifications; Codex's injected instructions and environment) is not a prompt, starts no turn,
and is not shown: a turn a notice continues is the turn before it. A scheduled prompt or one
another agent sent **is** a prompt, with its `from`.

#### Part

| Field | Type | Notes |
|---|---|---|
| `id` | string | **Strict (identity).** Stable as a turn's id is, unique within its turn, at most 64 bytes |
| `kind` | `"response"` \| `"reasoning"` \| `"tool"` \| `"delegate"` \| `"question"` | What it is. A client **skips a part whose kind it doesn't know** (it reads as `unknown`), and draws the rest |
| `at` | time | When it happened: for a tool, a question and a delegate event, when it started or was asked |
| `text` | string? (Markdown for `response`) | `response`: one message of the main agent's, at most **16 KiB**, the **end** kept, as `activity.response`. `reasoning`: one reasoning block of the main agent's, at most **4 KiB**, the end kept, as `activity.reasoning` |
| `truncated` | bool? | `text` was cut, or a question's `answer` was (to its 2 KiB, or left out to fit the turn) |
| `tool` | [ToolCall](v1-sessions.md#toolcall)? | `tool`: the main agent's call, exactly as in `activity.history`: its name, a one-line summary (at most 200 bytes) and status, **never its arguments or results**. A call the transcript never ended reads `running` while the turn is the latest, and `failed` once a later prompt has started (the turn was abandoned), as `activity.history` does after an interruption |
| `delegate` | [DelegateEvent](#delegateevent)? | `delegate`: one of the main agent's own delegates starting or reporting back (#661) |
| `question` | [Question](v1-sessions.md#question)? | `question`: a question or an approval asked in this turn, exactly as `session.question` carried it while it waited (its `message` at most 8 KiB, the same budget rules), with `answerAtMachine` as it was. Never offered for answering again: a client draws it read-only (H4). A part whose question can't be read (Malformed records rule 3) has no `question`, and a client skips it |
| `outcome` | `"answered"` \| `"approved"` \| `"denied"` \| `"cancelled"` \| `"unknown"`? | **Strict (security).** `question` only. `answered`: a form question got an answer. `approved` and `denied`: an approval's decision, as the agent recorded it. `cancelled`: the turn ended or was interrupted with the question unanswered. `unknown`: the transcript does not say, or it still waits (the question is also `session.question` then). An unknown value reads as `unknown`, never as `approved` |
| `answer` | string? | `question` with `outcome` `answered`: what the agent recorded as the answer, as plain text, at most **2 KiB**, the start kept. A form's fields are written one per line, `name: value`. Absent when the agent's record has none |
| `answeredAt` | time? | When the outcome was recorded |

Response and reasoning parts are **the main agent's own**, never a delegate's: a delegate's words
are in its own inspector (`session.get { id, agent }`), and the history never repeats them as the
main agent's. A delegate's tool calls are likewise not in the main agent's turn. A delegate's
**question or approval** is, with `question.agent` naming it, as `session.question` does.

#### DelegateEvent

| Field | Type | Notes |
|---|---|---|
| `agent` | string | **Strict (identity).** The delegate's id, as in `subagents` |
| `event` | `"started"` \| `"reported"` | `started`: the main agent started it. `reported`: it ended a run and its result reached the main agent (#661: "reported back"). A client skips an event it doesn't know |
| `name` | string? | Its name, cleaned as `Subagent.name` is. Absent when the host did not read its start in this page and the session no longer lists it; a client takes it from the start it holds, or shows "a delegate" |
| `kind` | `"background"` \| `"task"`? | With `started` |
| `outcome` | `"done"` \| `"failed"`? | With `reported`: how the run ended |

Only the main agent's own delegates are listed (a nested delegate's events are its parent's, in its
inspector), as #661 decided. ui.md §6.4 D1–D4 say how a client places, merges and folds them.

### Caps

Every turn as sent fits **40 KiB** of JSON (`HistoryBudget.Fit` in C#, measured as
[Session budget](v1-sessions.md#session-budget) measures), so a page of at least one always fits
the 48 KiB message. A page holds the newest turns before `before` that fit 48 KiB with its
envelope, up to `limit`.

| What | Limit | Kept |
|---|---|---|
| `limit` | 1–50, default 20 | |
| Turns served | the newest **500** of the session | Past them, `start.limited` |
| `prompt.text` | 8 KiB | the start |
| A `response` part's `text` | 16 KiB | the end |
| A `reasoning` part's `text` | 4 KiB | the end |
| `tool` parts in a turn | the latest 50 | the rest counted in `partsMore` |
| `delegate` parts in a turn | the latest 50 | |
| `question` parts in a turn | the latest 20 | |
| `response` and `reasoning` parts in a turn | the latest 50 of each | |
| `answer` | 2 KiB | the start |
| `before` | 256 bytes | |
| `unavailable` | 256 bytes | |

A turn still over 40 KiB is trimmed in this order, each step only until it fits, counting every
part left out in `partsMore` and setting `truncated`:

1. `tool` parts, oldest first.
2. `reasoning` parts, oldest first.
3. `response` parts but the last, oldest first.
4. `delegate` parts, oldest first.
5. Each `question`'s `message` to its first 2 KiB (`question.truncated` set), then its `fields`,
   as Session budget steps 10 and 11 do, then `answer`.
6. The last `response` to its last 4 KiB.
7. `prompt.text` to its first 2 KiB.
8. Should a turn still not fit (only a pathological number of ids and flags could do that), parts
   from the oldest on.

Never trimmed: ids, times, `from`, `label`, `outcome`, flags. A turn's budget is the session
budget's, so a turn never carries more of any one text than `activity` carries of it, except
the prompt (8 KiB here, 2 KiB in `activity.userMessage`; [Security](#security-and-authorization)).

**Reading is bounded too**, so a request costs a host a bounded amount whatever a transcript
holds. A host reads at most 256 MiB of transcript backwards per request, finding the end of the
last whole line included (a line still being written that is longer than that leaves nothing to
page, `start.limited` and `partial`). Going back it searches every line whole for the bytes a
turn's record carries (`"user.message"` for Copilot, `"user"` and no `"tool_result"` for Claude,
`"user_message"` or `"UserMessage"` for Codex: only ever JSON tokens, never text inside a string)
and parses as JSON only the lines that carry them, wherever in the line the writer put them. It
parses at most 32 MiB per request: the prompts it finds count first, and it stops adding older
turns there, at least one. It parses at most 8 MiB of any one turn (a longer turn is read from
its prompt and its **last** 8 MiB, `truncated` set, and what it skipped counted in neither
`parts` nor `partsMore`). A turn whose prompt is more than 256 MiB before the cursor ends the
history there, with `start.limited`. A transcript line longer than 32 MiB is not read, as the tail
does not; one whose first 4 KiB carry a turn's bytes is an `unreadable` turn, which is all the
live parser can see of it too. A host reads at most two pages at once; a further request waits
its turn.

### The cursor

`before` is **opaque and host-issued**. A client stores it and sends it back unchanged with the
same session's id; it never parses, builds or compares one.

A host makes it from where the page's oldest turn starts in the transcript, that turn's id, how
many turns have been served before it, and a fingerprint of the transcript's first record, and
seals it with an HMAC-SHA256 under a key the host keeps in its profile folder
(`history-cursor.key`: 32 random bytes, made once). So it is the same after the host restarts, and
it stays good as the transcript grows. Nothing in it names a path. A client cannot make one that
reads anything but the turns it could page to, nor skip the 500-turn limit. A host that cannot keep
the key uses one for its run only, and its cursors are then stale after a restart.

A host checks every cursor before using it: its MAC, its session, the transcript's fingerprint,
and that the record where it says the turn starts is still that turn's prompt. Any of those
failing (forged, cut, another session's or another host's, a transcript replaced or rewritten, a
key the host has lost) is `{ "stale": true }`, never a page from somewhere else.

## Live turns and history

`activity` gains two fields:

| Field | Type | Notes |
|---|---|---|
| `turn` | string? | **New (#292).** The id of the latest turn: the one `userMessage`, `reasoning`, `currentTool` and `question` belong to. The same id `session.history` gives that turn. Absent before the first prompt, when the host could not read where the latest turn began (a transcript longer than it reads at first sight), and after a record that may have started a turn but could not be read (not JSON, or a line longer than the host reads), until the next prompt it can read: history shows that turn as `unreadable`. Never trimmed by the session budget |
| `responseTurn` | string? | **New (#292).** The id of the turn the main agent's own latest reply was written in: `own.response` when `source.response` names a delegate, else `response`. It is `turn` once the latest turn has replied; until then the reply still showing is an earlier turn's, as today's clients show it. Absent with no such reply, or when the host does not know its turn. Never trimmed |

**The dedup key is the turn id**, within a session. Part ids dedupe within a turn. A client holds
a session's turns in the order the host gave them, keyed by id:

1. **Opening a session** (H2): it subscribes as now, then asks `session.history` without
   `before`. The newest page's last turn is normally `activity.turn`.
2. **Drawing the latest turn:** the turn whose id is `activity.turn` is drawn from the live
   session: `activity` and `question` say its prompt, reasoning and Now line, as today, and its
   parts from the page (tools, delegate events, resolved questions) are drawn with them. Its reply
   is the main agent's live reply only when `responseTurn` equals `turn`; otherwise the latest turn
   has not replied yet, and the reply showing belongs to the turn `responseTurn` names, never under
   the new prompt. Reasoning is drawn only when `source.reasoning` is absent (a delegate's thinking
   is its own). A live field wins over the same field from a page, being newer.
3. **A new turn:** when `activity.turn` changes to an id the client does not hold, the turn goes
   after every turn it holds. It never goes between them: the host never starts a turn before one
   it has served.
4. **Finishing the old one:** the turn that was latest keeps what it was drawn with. A client that
   wants it whole (its tools and delegate events) asks for the newest page again, without
   `before`; each turn on it whose id it holds **replaces that turn in place**, and nothing moves.
5. **Loading earlier** (H1): `before` from the oldest page it holds; the turns go above the oldest
   it holds, in the page's order. One whose id it already holds replaces it in place.
6. **A stale cursor or a restart:** ids are stable, so asking again without `before` and merging by
   id draws each turn once.

`activity.turn` is absent in a host without history and before a session's first prompt. A client
then draws the live exchange after the history it holds, as the newest, and merges it with the
newest page once `turn` appears.

## Where a host gets each part

From the same transcript `activity` comes from ([v1-sessions.md](v1-sessions.md#where-a-host-gets-each-field)),
by the same readers' rules, so a turn's prompt is exactly what `activity.userMessage` was while the
turn was the latest, with the same `from`.

| | Copilot `events.jsonl` | Claude `<id>.jsonl` | Codex rollout |
|---|---|---|---|
| A turn starts | `user.message` of the main agent (no `agentId`) with text, whose `data.source` is not `memory`, `system` or `skill-…`. `from` and `label` from `data.source` (#489) | A `user` entry, not a sidechain's, meta or a compaction summary, with text and no `tool_result`, that is not a task notification Claude marked as one or "[Request interrupted by user…]" | `event_msg` `user_message`, or `item_completed` `UserMessage`, with text |
| `response` | `assistant.message` `content`, main agent's | `assistant` `text` blocks | `agent_message`; `item_completed` `AgentMessage`; an assistant `message` response item; `task_complete`'s `last_agent_message`. Codex writes a reply in more than one of these: the same text is one part |
| `reasoning` | `assistant.message` `reasoningText`, main agent's | `thinking` blocks | `agent_reasoning`; a `reasoning` response item's `summary`; `Reasoning` `summary_text` (only when the user asked for summaries); one part per summary, the same text once |
| `tool` | `tool.execution_start` and `_complete`, main agent's | `tool_use`, ended by its `tool_result` | `exec_command_*`, `patch_apply_*`, `mcp_tool_call_*`, `CommandExecution`, function, custom, shell and web search calls |
| `question` (form) | `ask_user`'s `tool.execution_start`, a delegate's too (with its `agent`, as live; the delegate's call is no `tool` part); `answered` at `human_response.recorded` (its content is the `answer`) or the call's end; `cancelled` when the delegate asking aborts, shuts down or fails | `AskUserQuestion`'s `tool_use`; `answered` at its `tool_result` (its text is the `answer`) | none: Codex asks nothing as a form |
| `question` (approval) | `permission.requested`; `approved` or `denied` from `permission.completed`'s `result.kind` | not in the transcript (agent-ha-bridge's hook only): none | `exec_approval_request`, `apply_patch_approval_request`, always `answerAtMachine`; `approved` when the call then starts, else `unknown` |
| `delegate` `started` | `subagent.started` with no parent, named from its `task` call; not one whose `task` call a delegate made (a nested delegate's start may name its parent only by that call's stamp) | an `Agent` call's `tool_result` with `async_launched` | `CollabAgentToolCall` `spawn_agent` the session itself called |
| `delegate` `reported` | `subagent.completed` (`done`), `subagent.failed` (`failed`), or an `agent_idle` notification (`done`) | a task notification Claude marked as one | a `subagent_notification` that says it completed (`done`), errored or was interrupted (`failed`), or the session's own multi-agent call (a wait) returning that state |
| The start | the first record's time | the first record's time | `session_meta`'s time |

A delegate is reported **once per run within a turn**, as the live list counts runs: a message to
it (Copilot's stamped `user.message`, Claude's `SendMessage`, Codex's `send_input`, `resume_agent`,
`send_message` or `followup_task`) starts another run. A run's end said by the run itself (a
completion, a failure, a Codex wait) and then by a notice in the same turn is one report: the
notice is spent on that run, never on one a message has started since. Each turn decides its
reports from its own records alone, so **a turn reads the same whatever page it is on**: a run's end
said in one turn and the notice the main agent took in a later turn is reported in each, as each
turn heard it. Reports are for the main agent's own delegates that this read saw start, or that
the session still knows (not only those `subagents` lists, which is capped at 20), named as the
session names them where it does.

A question still waiting is in its turn with `outcome` `unknown`, and is also `session.question`,
which is the one a client answers (§5.3's answer guard, #491). A question whose turn ended without
an answer (the user interrupted, the session shut down, another prompt started) is `cancelled`.

**Transcripts a host can't read** are reported, never shown as an empty history: no transcript
yet, or one that can't be found, is `unavailable` ("this session's transcript can't be found"); one
that can't be opened or read part-way is `unavailable` ("this session's transcript can't be read");
a line in a turn that isn't JSON is skipped and the turn marked `truncated`, and a turn-starting
line that isn't is an `unreadable` turn with `partial` on its page. Live `activity.turn` is then
absent until the next prompt the host can read, so nothing after that line is drawn under the
prompt before it.

## Security and authorization

* **Who gets it.** Exactly the principals that may read this session's content today **as a
  person**: a person's paired devices through the relay (role `person`), and the Agent Bridge app
  and the user's other non-agent processes on the local pipe, as `session.get`. **Not** an agent
  on the pipe (CallerGuard's `Agent`): it is refused (`refused: … reading a session's history is
  for the person …`), and its hello omits `sessions.history`, until agents have a principal of
  their own (design §5.5, #45). An agent can read the latest exchange today; a whole past
  conversation of every session on the machine, including words the person pasted into other
  sessions, is more than it needs. Not a `host` peer (another machine), and not a `service` peer.
* **Home Assistant.** The HA connector (`service` role) gets **no history**: `session.history` is
  not among the reads its role allows, a `service` peer's hello never lists `sessions.history`, and
  the [connector read profile](connector-read.md) (`hello`, `sessions.list`, `session.get`,
  `sessions.subscribe`) is unchanged. (Its recording, taken from a host without pinned peers, shows
  the capability in `hello`, as it shows `sessions.patch`; the profile uses neither.) It keeps what
  it has today: the latest exchange in `activity` (with `activity.turn` and `responseTurn`, which it may ignore), the
  question, and the counts.
* **Nothing new in kind.** History is `activity` applied to earlier turns: prompts, the main
  agent's responses and reasoning, tool names and one-line summaries, questions and approvals as
  `session.question` showed them, delegate names. Never tool arguments or results, file contents,
  attachments or blobs (an attachment's file is never read; the prompt's text is what the agent
  received), delegates' own words, hook or usage records, paths of transcripts, or anything from
  another session. Approvals keep `CopilotPermission`'s rendering, drawn as written and escaped.
  The prompt is the one text longer than in `activity` (8 KiB, not 2 KiB), to the same principals.
* **Relay.** The op rides the existing end-to-end channel, so the relay sees only its size. Its
  request is at most 512 bytes and its result one message (at most 48 KiB of JSON, deflated when
  both sides offer it), so it needs no new frame kind or limit. A page costs a host at most the
  reading budget above; a client asks for the next page only as the reader scrolls (H1).
* **Read-only.** A history request types nothing, approves nothing and changes no session. The
  questions it carries are never answerable from it: `session.answer` still answers only
  `session.question`, by its id.

## Versioning

* New: the `sessions.history` capability, the `session.history` op, `activity.turn` and `activity.responseTurn`, and the
  records above. Hosts never remove or rename a field within v1.
* An older client never sends the op, and ignores `activity.turn` and `responseTurn`.
* An older host does not list the capability; a client sending the op anyway gets `unknown op`.
* New part kinds, delegate events or outcomes may be added; a client skips a part or event it
  doesn't know and reads an unknown outcome as `unknown`.

## Shared vectors

[vectors/history.json](vectors/history.json): the request (with and without a cursor, the
limits, malformed ones refused), a page (paging, the start of a session, the 500-turn limit, an
empty history, a partial page with an unreadable and a truncated turn), each part kind, `from`
attribution (and a `from` that refuses its turn), questions and approvals with each outcome,
delegate events, and the `missing`, `stale` and `unavailable` results. `activity.turn` and `responseTurn` are in
[vectors/sessions.json](vectors/sessions.json). C# replays them in `ProtocolVectorTests`, and the
PWA in `pwa/test/unit/protocol-vectors.test.mjs` (until its history view lands, with each record
marked not replayed yet).

## Open questions

Recommended defaults, in force until the owner decides otherwise:

1. **Agents on the pipe.** Recommended: refused, as above, until #45 gives agents a principal.
   Alternative: as `session.get`. *Raised to the coordinator.*
2. **Prompt length.** Recommended: 8 KiB, the start kept (p99 of prompts is 7.4 KB). Alternative:
   2 KiB, as `activity.userMessage`, cutting about one prompt in ten.
3. **Older than 500 turns.** Recommended: the host stops at 500 (`start.limited`), matching what a
   client keeps (H3). No session measured had more than 486.
