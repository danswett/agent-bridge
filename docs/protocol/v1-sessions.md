# Bridge Protocol v1: sessions (read-only slice)

The contract between a **Bridge Host** and its clients: the PWA now, and later the
WinUI 3 and SwiftUI apps and the native phone apps. It is how a client sees a machine's
agent sessions. It travels inside the end-to-end Noise channel from spike S4, so the
relay never sees any of it. C# records matching this document are in
`src/AgentBridge.Protocol/Sessions.cs`, and the PWA mirrors them in JavaScript.

Scope of this slice: **read-only**. A host reports sessions, their activity and their
pending questions. Answering, replying, launching and stopping come next; they will add
ops without changing anything here.

## Rules for every message

* JSON, UTF-8, `camelCase` names. A field that is absent and one that is `null` mean
  the same thing. Non-ASCII text is sent as UTF-8, not as `\u` escapes.
* **Clients ignore fields and enum values they don't know.** An unknown value reads as:
  `agent` → `unknown` (shown generically), `status` → `idle`, tool `status` → `done`,
  question `kind` → `form`, field `type` → `text`. Hosts never remove or rename a field
  within a protocol version.
* Times are UTC with milliseconds and a `Z`, always in this exact form:
  `2026-10-28T04:22:43.182Z`. Clients can compare them as strings.
* **Sizes are UTF-8 bytes of the JSON as sent.** No single message exceeds **48 KiB**, so
  it always fits one Noise transport message (65,535 bytes) with room to spare. **Every
  `Session` as sent fits 40 KiB**: in a list page, an event or `session.get`. Each type
  below gives per-field limits. Those alone can't promise the budget (question options,
  history), so a host finishes with the trimming order in
  [Session budget](#session-budget). Long text is cut and flagged, never
  mid-character.
* Text is Markdown where the type says so (agent responses, questions), otherwise plain.
  Clients render Markdown without raw HTML.

## Channel framing

Inside a channel, as in S4:

| Direction | Shape | Meaning |
|---|---|---|
| client → host | `{"id": 7, "op": "sessions.list", "data": {...}}` | A request. `id` is a positive integer unique on this channel |
| host → client | `{"re": 7, "ok": true, "result": {...}}` | Its response |
| host → client | `{"re": 7, "ok": false, "error": "unknown op"}` | A failed request |
| host → client | `{"ev": "session.upsert", "seq": 42, "data": {...}}` | **New:** an event the client subscribed to. It has no `id` or `re` |

`seq` goes up by one for each event on a channel. If a client sees a gap (it shouldn't,
since Noise is ordered), it calls `sessions.subscribe` again.

## Ops

### `hello`

Request `data`: `{}`. Result:

```json
{
  "protocol": 1,
  "machine": { "id": "53d1997f…", "name": "DSWETT-DEV-VM1", "os": "windows", "hostVersion": "0.1.0" },
  "capabilities": ["sessions.read"]
}
```

`machine.id` is the host's device id (S4). `os` is `windows`, `macos` or `linux`.
`capabilities` lists what this host supports, and a client hides anything missing. This
slice has only `sessions.read`.

### `sessions.list`

Request `data`: `{}` for the first page, then `{ "cursor": "<next>" }`. Result:
`{ "sessions": [Session…], "revision": 118, "next": "<cursor>" }`. It lists live
sessions only, most recently updated first, as many as fit in one message (always at
least one). `next` is absent on the last page. Each `Session` here **omits
`activity.history`**; `session.get` includes it.

`revision` is the host's change counter. It changes whenever any session changes. If it
changes between pages, the pages may overlap or miss a session; a client that needs a
consistent list uses `sessions.subscribe`.

### `session.get`

Request `data`: `{ "id": "<session id>" }`. Result: the full `Session`, or
`{ "missing": true }` once the session has ended and been dropped.

### `sessions.subscribe`

Request `data`: `{}`. Result: `{ "revision": 118, "count": 3 }`. Then the host sends:

1. **The snapshot:** one `session.upsert` for each of the `count` live sessions, then
   `sessions.synced` with `{ "revision": … }`. On `sessions.synced` a client replaces its
   list with the sessions it received since subscribing, which drops any it had from
   before that no longer exist.
2. **Live changes, until the channel closes:**
   * `session.upsert`, with a full `Session` (without `activity.history`), whenever
     anything a client shows changes. Hosts coalesce bursts to at most one upsert per
     session every 250 ms, and the latest state always arrives.
   * `session.remove`, with `{ "id": "…" }`, when a session ends and is dropped.

A session can change while the snapshot is being sent; its upsert then simply arrives
again. When a channel closes, the client subscribes again on the new channel.

## Types

### Session

| Field | Type | Notes |
|---|---|---|
| `id` | string | The agent's own session id: the Copilot session-state folder name, or the Claude or Codex session id |
| `agent` | `"copilot"` \| `"claude"` \| `"codex"` | Hosts send only these. A client reads anything else as `unknown` |
| `title` | string? | The session's name if it has one (Copilot's `workspace.yaml` `name`). Otherwise the first user message, cut to 240 bytes |
| `cwd` | string? | Working directory |
| `repository` | string? | `owner/name`, if known |
| `branch` | string? | |
| `model` | string? | The model used for the latest turn, e.g. `claude-opus-5.5` |
| `status` | `"working"` \| `"waiting"` \| `"idle"` | `waiting` exactly when `question` is present |
| `statusSince` | time | When `status` last changed |
| `startedAt` | time? | |
| `updatedAt` | time | When something a client shows last changed. Events that change nothing visible (hook runs, usage records, model internals) do **not** move it, so they never cause an upsert |
| `processId` | number? | The agent's process, when known |
| `activity` | Activity | |
| `question` | Question? | The question the agent is waiting on, if any |

### Activity

| Field | Type | Notes |
|---|---|---|
| `response` | string? (Markdown) | The latest assistant text, at most 16 KiB. When it's longer, the **end** is kept and `responseTruncated` is true |
| `responseTruncated` | bool? | |
| `reasoning` | string? | The latest reasoning text, at most 4 KiB (the end is kept) |
| `currentTool` | ToolCall? | The tool running now, while `working` |
| `history` | ToolCall[]? | Only in `session.get`: the latest 50 tool calls, oldest first |
| `userMessage` | string? | The latest user message, at most 2 KiB |
| `turnCount` | number? | Completed assistant turns |
| `compactions` | number? | Context compactions so far |

### ToolCall

| Field | Type | Notes |
|---|---|---|
| `id` | string | The agent's tool call id |
| `name` | string | Tool name, e.g. `powershell`, `edit`, `Bash` |
| `summary` | string? | One line for display: the tool's `description` argument if it has one, else a short form of its main argument. At most 200 bytes |
| `status` | `"running"` \| `"done"` \| `"failed"` | |
| `startedAt` | time | |
| `endedAt` | time? | |

Tool arguments and results are **not** sent in this slice. They can be large and
sensitive, and no client view needs them yet.

### Question

What an agent is waiting for. The forms from all three agents map onto this one shape.

| Field | Type | Notes |
|---|---|---|
| `id` | string | The id of the tool call that asked it. It stays the same while the question is pending |
| `kind` | `"form"` \| `"approval"` | `approval` is a permission prompt with fixed choices (Codex approvals, Copilot permission requests) |
| `message` | string (Markdown) | What the agent wrote, at most 8 KiB |
| `fields` | Field[] | In order. Empty for a plain message |
| `askedAt` | time | |
| `truncated` | bool? | The host had to cut `message` or drop `fields` to fit the budget. A client shows what it has and says to answer at the machine |

### Field

| Field | Type | Notes |
|---|---|---|
| `name` | string | The key an answer will use |
| `title` | string? | |
| `description` | string? | |
| `type` | `"choice"` \| `"multi"` \| `"boolean"` \| `"text"` \| `"number"` | `choice` takes one of `options`, `multi` any number of them |
| `options` | Option[]? | For `choice` and `multi` |
| `allowOther` | bool? | `choice` and `multi` also accept free text |
| `default` | any? | |
| `required` | bool? | |

### Option

`{ "value": "s6", "label": "S6: TPM-backed device keys (Recommended)" }`. `label` is what
to show; if it's absent, show `value`.

## Session budget

After applying the field limits, a host checks the size of each `Session` it sends. If
it is over 40 KiB, the host trims in this order until it fits, which loses the least for
a reader (`SessionBudget.Fit` in C#):

1. Tool history, oldest first.
2. `reasoning`.
3. `userMessage`.
4. `response`, to its last 4 KiB (`responseTruncated` set).
5. The question's `message`, to its first 2 KiB (`truncated` set).
6. The question's `fields` (`truncated` set).
7. `response` and `currentTool`.

A list page holds as many sessions as fit in 48 KiB, and always at least one, since every
session is at most 40 KiB.

## Where a host gets each field (read-only)

| Agent | Sessions | Activity and status | Question |
|---|---|---|---|
| Copilot CLI | `~/.copilot/session-state/<id>/` with a live `inuse.<pid>.lock`, plus `workspace.yaml` | From `events.jsonl`: `user.message`, `assistant.message` (`content`, `toolRequests`), `tool.execution_start` and `_complete`, `assistant.turn_start` and `_end`, `session.compaction_complete`, `session.model_change` | A `tool.execution_start` with `toolName: "ask_user"`, using its `arguments.message` and `arguments.requestedSchema`. Pending until a `human_response.recorded` or `tool.execution_complete` with the same `toolCallId` |
| Claude Code | The bridge's registration files, and `~/.claude/projects/<slug>/<id>.jsonl` | Transcript `assistant` entries (`text`, `thinking`, `tool_use`) and `user` entries | A `tool_use` named `AskUserQuestion`, pending until a `tool_result` with its id |
| Codex | The bridge's registration files, and `~/.codex/sessions/…/rollout-*.jsonl` | Rollout `event_msg` payloads | Approval requests |

On a machine that still runs agent-ha-bridge, a shadow host **only reads**. It never
opens the hook spool, never writes into a session folder, and never deletes or locks
anything another process uses.
