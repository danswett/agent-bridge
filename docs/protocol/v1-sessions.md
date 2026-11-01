# Bridge Protocol v1: sessions

The contract between a **Bridge Host** and its clients: the PWA now, and later the
WinUI 3 and SwiftUI apps and the native phone apps. It is how a client sees a machine's
agent sessions, answers their questions and replies to them. It travels inside the end-to-end Noise
channel from spike S4, so the relay never sees any of it. C# records matching this
document are in `src/AgentBridge.Protocol/Sessions.cs`, and the PWA mirrors them in
JavaScript.

Scope so far: a host reports sessions, their activity and their pending questions
(`sessions.read`), can take an answer to a question (`sessions.answer`), can take a
reply: a prompt typed into a session (`sessions.reply`), with files and images sent ahead
of it (`sessions.attach`, see [Attachments](#attachments)), can end a session
(`sessions.stop`), and notifies a client's devices when a session asks or finishes, even
with the app closed (`push`, see [Notifications](#notifications)). It can also launch or
resume a session (`sessions.launch`, in [v1-launch.md](v1-launch.md)). Files too large for
the channel go through the relay's blob store (`sessions.attach.blob`, in
[blobs.md](blobs.md)).

## Rules for every message

* JSON, UTF-8, `camelCase` names. A field that is absent and one that is `null` mean
  the same thing. Text in the Basic Multilingual Plane, accented letters and CJK
  included, is sent as raw UTF-8, not as `\u` escapes. Characters outside it, which are
  most emoji, **are** escaped as a surrogate pair (`\uD83D\uDE00`): 12 bytes of JSON for 4
  bytes of text. Clients must read both forms.
* **Clients ignore fields and enum values they don't know.** An unknown value reads as:
  `agent` → `unknown` (shown generically), `status` → `idle`, tool `status` → `done`,
  question `kind` → `form`, field `type` → `text`, `driver` → `person`. Hosts never remove or rename a field
  within a protocol version.
* Times are UTC with milliseconds and a `Z`, always in this exact form:
  `2026-10-28T04:22:43.182Z`. Clients can compare them as strings.
* **Sizes are UTF-8 bytes of the JSON as sent.** No single message exceeds **48 KiB**, so
  it always fits one Noise transport message (65,535 bytes) with room to spare. **Every
  `Session` as sent fits 40 KiB**: in a list page, an event or `session.get`. Each type
  below gives per-field limits, which count the UTF-8 bytes of the text itself, not its
  JSON form. Those limits alone can't promise the budget: escaping makes the JSON larger
  than the text (an emoji triples, and quotes and control characters grow too), and
  question options and history have no total. So a host finishes with the trimming
  order in [Session budget](#session-budget), which measures the JSON as sent. Long text
  is cut and flagged, never mid-character.
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

`seq` belongs to the channel, not to a subscription. It goes up by one for each event the
host sends on that channel, and a later subscribe continues from where it is rather than
restarting. A client doesn't know the starting value, so it takes the first event it
receives as its baseline. If it later sees a gap (it shouldn't, since Noise is ordered),
it calls `sessions.subscribe` again.

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

`machine.id` is the host's device id (S4). `os` is `windows`, `macos` or `linux`. `machine` also carries `dev` and `sessions`, for a status line ([v1-usage.md](v1-usage.md#machine-status)), and `readOnly` while the machine takes nothing from other devices: because its config makes it read-only (`delivery.readOnly`, [host-config.md](../host-config.md)), or because agent-ha-bridge runs there for the same user (design G5). It is the reason, in words, which a client shows on the machine's status line and on its read-only questions. Absent otherwise. `machine.instance`, when present, names this run of the host: it is new each time the host starts and the same on every connection to that run, so a client can tell a reconnect from a restart before sending a request again that only the first run remembers (a reply, an answer or a launch, `session.launch`, whose outcome was lost).
`capabilities` lists what this host supports, and a client hides anything missing:

| Capability | Ops |
|---|---|
| `sessions.read` | `sessions.list`, `session.get`, `sessions.subscribe` |
| `sessions.answer` | `session.answer`. A host leaves it out when it cannot answer at all, for example while agent-ha-bridge's daemon runs for the same user, or on a platform without delivery yet. A client then shows questions read-only and says to answer at the machine |
| `sessions.reply` | `session.reply`. Left out on the same terms as `sessions.answer`. A client then offers no reply box |
| `sessions.attach` | `attachment.start`, `attachment.chunk`, `attachment.discard`, and `attachments` on `session.reply` ([Attachments](#attachments)). Offered only with `sessions.reply`, by a host that can stage files for the agent. Without it a client offers no way to attach |
| `sessions.attach.blob` | `attachment.start` takes a `blob`: files over 256 KiB, up to 100 MiB, through the relay's blob store ([Large files](#large-files-through-the-blob-store)). Offered with `sessions.attach` on a channel through a relay, while the host is connected to one that has a blob store. Without it a client keeps to 256 KiB |
| `sessions.attach.direct` | `attachment.chunk` takes files up to 100 MiB, not only 256 KiB. Offered with `sessions.attach` on the local pipe only ([local-pipe.md](local-pipe.md)), where the bytes cost the relay nothing |
| `sessions.stop` | `session.stop`. Left out on the same terms as `sessions.answer`. A client then offers no End session |
| `sessions.approve` | Copilot permission prompts are approved or denied with `session.answer` ([Copilot permission prompts](#copilot-permission-prompts)). Offered only with `sessions.answer`, by a host whose `permissions.remoteApproval` is on (the default). Without it, every Copilot permission prompt is `answerAtMachine`, and a client says approving from the phone is turned off on that machine |
| `push` | `push.subscribe`, `push.unsubscribe`, `push.list`: Web Push notifications of questions and finished turns ([Notifications](#notifications)). Left out by a host whose `push.enabled` is off ([host-config.md](../host-config.md)): it then fails `push.subscribe` saying it doesn't send notifications, removes nothing on `push.unsubscribe` and lists no devices |
| `usage` | `usage.get`, `usage.subscribe`: how much of each agent's allowance is gone ([v1-usage.md](v1-usage.md)) |
| `sessions.launch` | `launch.options`, `session.launch`: starting or resuming a session in a workspace approved at the machine ([v1-launch.md](v1-launch.md)) |
| `sessions.transfer` | `session.launch` takes a `transfer`: a session that ended on another of the account's machines, brought here and resumed as a copy ([v1-transfer.md](v1-transfer.md)). Through a relay with a blob store only |
| `sessions.transfer.serve` | `session.transfer`, host to host: this machine hands its ended sessions to the account's other machines on a command a person's device signed ([v1-transfer.md](v1-transfer.md)). Through a relay with a blob store only |
| `update` | `update.get`, `update.subscribe`, `update.check`, `update.install`: the machine's updates, seen and asked for from a person's other devices, installed by its Windows app ([v1-update.md](v1-update.md)). Through the relay only, and never by a read-only machine |

### `sessions.list`

Request `data`: `{}` for the first page, then `{ "cursor": "<next>" }`. Result:
`{ "sessions": [Session…], "revision": 118, "next": "<cursor>", "discoveryHeld": {…} }`. It
lists live sessions only, most recently updated first, as many as fit in one message (always
at least one). `next` is absent on the last page. `discoveryHeld` is present while the host's
discovery is held ([below](#discovery-held)). Each `Session` here **omits
`activity.history`**; `session.get` includes it.

`revision` is the host's change counter. It changes whenever any session changes. If it
changes between pages, the pages may overlap or miss a session; a client that needs a
consistent list uses `sessions.subscribe`.

### `session.get`

Request `data`: `{ "id": "<session id>" }`. Result: the full `Session`, or
`{ "missing": true }` once the session has ended and been dropped.

With `"agent": "<sub-agent id>"` as well, the result is that one entry of the session's
`subagents` in full: the [Subagent](#subagent) with its own `response` (at most 16 KiB, the end
kept, `responseTruncated` set when cut), `reasoning` (at most 4 KiB) and `history` (its latest
50 tool calls, oldest first), within the session budget (oldest tools go first, then
`reasoning`, then `response` to its last 4 KiB). `{ "missing": true }` when the session has
gone or has no sub-agent by that id. Lists never carry these fields. A host before
sub-agents ignores `agent` and returns the session, so a client asks only of a host whose
sessions carry `subagents`.

### `sessions.subscribe`

Request `data`: `{}`. Result: `{ "revision": 118, "count": 3 }`. Then the host sends:

1. **The snapshot:** one `session.upsert` for each of the `count` live sessions, then
   `sessions.synced` with `{ "revision": …, "discoveryHeld": {…} }`. On `sessions.synced` a
   client replaces its list with the sessions it received since subscribing, which drops any
   it had from before that no longer exist, and takes `discoveryHeld` (absent: nothing holds
   discovery) as the host's hold.
2. **Live changes, until the channel closes:**
   * `session.upsert`, with a full `Session` (without `activity.history`), whenever
     anything a client shows changes. Hosts coalesce bursts to at most one upsert per
     session every 250 ms, and the latest state always arrives.
   * `session.remove`, with `{ "id": "…" }`, when a session ends and is dropped. A session
     a host is ending (`session.stop`) is first upserted with `status: "ending"`, then
     removed once its process has gone.
   * `discovery.changed`, with `{ "discoveryHeld": {…} }` when the host's discovery becomes
     held or what holds it changes, and `{}` when it clears. Sent once per change.

A session can change while the snapshot is being sent; its upsert then simply arrives
again. When a channel closes, the client subscribes again on the new channel.

A channel has at most one subscription. Calling `sessions.subscribe` again on the same
channel replaces the existing one: the host answers with a new `{revision, count}`, sends
a complete snapshot ending in `sessions.synced`, then carries on with live changes. It
never runs two subscriptions side by side, so each change arrives once. This is how a
client recovers from a gap. Live upserts sent before the new response are covered by
the snapshot.

### Discovery held

```json
"discoveryHeld": { "reason": "an unrecognised copilot process (pid 4120)", "agents": ["copilot"] }
```

A host finds sessions by looking at the agents' processes, lock files and registrations. When
a pass cannot account for everything it looked at, a session missing from it may be one it
could not see rather than one that ended, so the host **holds** whatever it would decide from
an absence, and never reads the failed look as "every session ended" (design §6.3 invariant 6;
agent-ha-bridge's "session discovery uncertain … absence-based work is held"). While an agent
in `agents` is held:

* its sessions are not removed for being missing, and those the host has go on updating;
* none of them is offered for resume, a resume is `busy`, and no worktree is reclaimed
  ([v1-launch.md](v1-launch.md#while-discovery-is-held)).

Sessions of the other agents are removed and resumed as usual. `reason` says what holds it, in
words for a person, at most 512 bytes; a client shows it as it is ("discovery held: …", as the
Home Assistant status line did). An agent is held when:

| | `reason` says |
|---|---|
| Its processes could not be listed at all | `the copilot processes could not be listed` |
| The runtime (`node`, `bun`) a live session ran in could not be read this time, by pid and start time; or a Copilot lock or a registration names a runtime that could not be read and was written by it, not by an earlier process with its pid | `a copilot process that could not be read (pid 4120)` |
| An agent process no session or registration accounts for, for its first 2 minutes. A registration accounts only for the process that wrote it, not a later one given its pid. A session registers within seconds, so one still unaccounted for after that is not a session: the host carries on without it and logs that once. One whose start time cannot be read is never carried on without, since a new session given its pid could not be told from it. Scout's embedded `copilot --headless` and Codex's `app-server` are nobody's session and never hold | `an unrecognised copilot process (pid 4120)` |
| A registration (Claude, Codex) that cannot be read (its details included), is a link, is not named for its own session, names no process, or changed while it was read | `a claude registration changed while it was read` |
| Claude Code's own `sessions/<pid>.json` for a running Claude process (of its generation) that cannot be read whole. One that says the run is not interactive (SDK, print mode) accounts for its process as nobody's session | `a claude session file could not be read (pid 4120)` |
| A session discovery found running whose transcript could not be read when it was first found, until it can | `a claude session that could not be read` |
| A session, registration or Codex thread-lock folder that is there but cannot be read or listed, or is a link (a thread lock only while a Codex terminal runs). Only the OS saying a folder is not there is absence | `copilot's session folders could not be listed` |

The next complete pass clears the hold with no restart, and removes what has really gone. A hold is said as soon as it begins, and said to clear (or to let an agent go) only once the sessions that pass found are listed, so a client never sees a session neither listed nor held; a pass that does both names the new agent at once and keeps the old one until then. An
agent that is not installed (no registration folder, and for Claude no `sessions` folder) is never held.

### `session.answer`

Answers a session's pending question. Request `data`:

```json
{
  "id": "<session id>",
  "question": "<question id>",
  "request": "6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b",
  "fields": { "answer": "s6" }
}
```

| Field | Notes |
|---|---|
| `id` | The session |
| `question` | The `question.id` the client was shown. The host answers only that question: if the session is no longer asking it, nothing is typed |
| `request` | A fresh UUID per answer the person gives. A host remembers the outcome of each request, **whatever it was** (`stale`, `busy` and `refused` included), for at least 10 minutes and returns it again for a repeat, typing nothing more. A repeat that arrives while the first copy is still being delivered waits for it and gets the same outcome, never `busy`. A client resending after a lost response or a reconnect therefore never answers twice. After `busy` or `refused`, a client that tries again uses a new `request` |
| `fields` | One value per field, by `name` (below). Omit a field to leave it unanswered; `required` fields must be present |
| `text` | Instead of `fields`, for a question with no fields: the free-text answer |

A field's value, by type:

| `type` | Value |
|---|---|
| `choice` | One option's `value`, or `{ "other": "<text>" }` when `allowOther` |
| `multi` | An array of option values, which may also hold one `{ "other": "<text>" }` when `allowOther` |
| `boolean` | `true` or `false` |
| `text` | A string |
| `number` | A number |

Free text, in `text` or `other`, is at most 4 KiB of UTF-8; the whole request is at most
16 KiB. A host collapses control characters and line breaks to spaces before typing.
`Answers.Check` in C# is the validation every host applies before typing anything.

Result: `{ "outcome": "<outcome>", "reason": "<text>" }`, with `reason` present for
anything but `delivered`:

| `outcome` | Meaning | Typed? |
|---|---|---|
| `delivered` | The host typed the answer and the agent's transcript recorded it | Yes |
| `unconfirmed` | The host typed it, but could not confirm the agent recorded it (for example the session stopped answering). It never retries on its own; the client says to check at the machine | Yes |
| `stale` | The session is no longer asking that question: answered at the machine or by another device, or gone | No |
| `busy` | Another answer or a reply to this session is being delivered | No |
| `refused` | The host will not type it: an invalid answer, a question the client should not answer remotely (`truncated` or `answerAtMachine`), a prompt the host has already typed an answer into once, an elevated or otherwise unreachable session, an agent or platform without delivery, or agent-ha-bridge active for this user | No |

A client treats an outcome it doesn't know as `unconfirmed`, never as `delivered`. The
session's next `session.upsert` shows the question gone whatever the outcome.

Typing into a live session is a single writer's job, and the host serializes it: one
answer or reply per session at a time (`busy` otherwise), checked against the transcript
immediately before typing and confirmed by identity, never by clock, after.

**One attempt per prompt.** Once a host has typed any key into a prompt, it never types
into that prompt again, whatever the request and across restarts: nobody can say what a
half-typed prompt holds, and a second answer would land on top of the first. Only an
attempt that typed nothing (the session's console could not be reached) leaves the prompt
answerable. A host records the attempt before the first key, and refuses when it cannot.

What a host answers, by agent. agent-ha-bridge is the baseline; answering Copilot
permission prompts goes beyond it, at the user's request:

| Agent | Prompt | `kind` | Answered | `delivered` when |
|---|---|---|---|---|
| Copilot CLI | `ask_user`: free text, or a form of `choice`, `multi`, `boolean`, `text` and `number` fields | `form` | Yes | Its `human_response.recorded` (or, for free text, its completion) holds the answer typed |
| Copilot CLI | Permission request for a shell command, path access, a file edit or read, a URL, an MCP tool or a custom tool | `approval` | Yes, with `sessions.approve`: approve once or deny, nothing that grants more | Its `permission.completed` for that request records `approved` (approve), or `denied-interactively-by-user` with no message or `cancelled` followed by the session's abort of the turn (deny) |
| Copilot CLI | Any other permission request (hooks, memory, workflows, extensions), a request to run outside the sandbox, one whose details cannot be read, or any on a host with `permissions.remoteApproval` off | `approval` | No: `answerAtMachine` | |
| Claude Code | `AskUserQuestion`: one to four questions, each a `choice` or a `multi` of its options | `form` | Yes, but not with free text (no field has `allowOther`), and a `multi` needs at least one option | The `tool_result` for its `tool_use` id records every question's answer as typed |
| Claude Code | Permission prompt (from agent-ha-bridge's hook) | `approval` | No: `answerAtMachine` | |
| Codex | Approval recorded by agent-ha-bridge's hook | `approval` | Yes | Never: Codex records nothing that says how an approval was answered, so a typed one is `unconfirmed` |
| Codex | Approval seen only in the rollout, or one agent-ha-bridge already typed into | `approval` | No: `answerAtMachine` | |

#### Copilot permission prompts

A Copilot permission prompt's `message` carries in full what it would allow: the command,
the path or paths, the file and its diff (or, without one, its whole new contents; an edit
showing neither is `answerAtMachine`), the URL, or the tool with its arguments, and the
intention Copilot gave. One too large to send whole is `truncated`, and refused like any
truncated question. Its `id` is `<requestId>#<digest>`: Copilot's request id and a hash of
the message. An answer is bound to both, so the host refuses (`stale`) if that request is
no longer waiting or now reads differently from what the client was shown.

The answer is `{ "decision": "approve" }` or `{ "decision": "deny" }`, nothing else.
Approve is the prompt's plain "Yes", once: no option that grants more ("don't ask again",
"remember this path", "approve for the session", read-only directory access) can ever be
chosen remotely. Deny is the prompt's "No" with no message to Copilot, taken by Escape as at
the machine, which also **stops Copilot's turn**: the command does not run, and Copilot does
not go on to try something else unattended. The session is then idle, and a reply can tell
it what to do instead. A deny takes no text, so nothing can be typed into the prompt's
message box.

Immediately before typing, the host checks the transcript (that request still waiting and
reading as shown, and no other prompt waiting that the screen could be showing instead),
then reads the session's screen and finds the option by its label, never by its position,
under rows showing what the prompt is for, and checks the transcript again. The key goes
only if those rows and the options, and which is highlighted, still read the same at the
moment it is typed.
`permissions.remoteApproval` in the host's own settings turns this off on a machine
([host-answers.md](../host-answers.md)).

### `session.reply`

Sends a session a prompt, typed into the agent's own input as if at the machine, whether
the session is idle or still working. Request `data`:

```json
{
  "id": "<session id>",
  "request": "0b7e3c1d-5f2a-4c6e-8d9b-1a2b3c4d5e6f",
  "text": "Looks good. Open the pull request."
}
```

| Field | Notes |
|---|---|
| `id` | The session |
| `request` | A fresh UUID per reply the person sends, with exactly `session.answer`'s semantics: every outcome is remembered for at least 10 minutes and returned for a repeat, which types nothing; a repeat of a reply still being delivered waits for it |
| `text` | The prompt: at most 16 KiB of UTF-8. Not blank, unless the reply has attachments |
| `attachments` | Optional. Files and images for the agent, in order: `[{ "id": "<attachment>" }]`, at most 10, each sent whole by **this device** with `attachment.start` and `attachment.chunk` first ([Attachments](#attachments)). Absent, `null` and `[]` mean none. A host without `sessions.attach` refuses a reply that has any |

**Line breaks become spaces.** Every control character, line breaks and tabs included,
and the Unicode line and paragraph separators are typed as spaces, so a reply always
reaches the agent as one line. This is agent-ha-bridge's behaviour, kept exactly: its
reply path (`Get-CopilotInjectableText`) replaces every control character with a space,
because an Enter inside the words would submit part of the reply. A client may let the
person type several lines, and says they will arrive as one.

A reply whose first character, after spaces and control characters, is `/` or `!` is
`refused`: Copilot CLI and Claude Code would run it as a slash command or a shell command
on the machine, not give it to the agent, and nothing could confirm it.

Result: `{ "outcome", "reason" }`, the outcomes of `session.answer`:

| `outcome` | Meaning | Typed? |
|---|---|---|
| `delivered` | The host typed the reply, and the agent's transcript then recorded a prompt from the person (not a subagent's, a scheduled one or any with another source) whose text is the reply as typed, whitespace runs aside, **with every attachment attached**. Each recorded prompt confirms one reply only | Yes |
| `unconfirmed` | The host typed it but saw no such prompt: the agent recorded something else, or the reply without all its files, the session ended, or nothing came within the host's wait. A session that is working takes a prompt typed now at its next step (Copilot records it then, as `delivery: "steering"`), so a host waits for up to a minute. Never retried | Yes |
| `stale` | The session has ended, or its process has | No |
| `busy` | An answer or another reply to this session is being typed | No |
| `refused` | The host will not type it: an invalid reply (above), **a question or permission prompt pending in the session** (the words would answer it, and Enter accept it; answer it first, at the machine for one marked `answerAtMachine`), a transcript the host cannot read to rule that out, an attachment the host does not hold (expired, removed, sent by another device, still arriving, or gone with an earlier reply) or cannot hand over, an elevated or otherwise unreachable session, an agent or platform without delivery yet, or agent-ha-bridge active for this user | No |

Copilot CLI, Claude Code and Codex sessions take replies. A Claude Code or Codex reply is
typed only into the agent's input box as its screen shows it, since without
agent-ha-bridge's hooks a host cannot otherwise see their permission prompts and questions;
it is `refused` when that box is not on screen, or for Codex already has words in it. Codex takes only
images as attachments; a Codex reply with any other file is `refused`.

A reply is never typed while the session is asking anything, which a host checks both in
what it reports and, immediately before typing, in the transcript itself. The per-session
serialization is shared with answers, but it ends once the reply's keys have gone: a
question the agent asks while the host waits for its prompt to be recorded can be answered
at once.

### Attachments

Files and images go with a reply, as they can be pasted or attached at the machine. A
client sends each file to the host first, in small chunks inside the channel, then names
them in `session.reply`. Each file is at most **256 KiB** this way (100 MiB on the local
pipe, `sessions.attach.direct`). A larger one, up to **100 MiB**, goes through the relay's
blob store where the host offers `sessions.attach.blob`
([Large files](#large-files-through-the-blob-store)); a reply names it the same way, by `id`.

**What the agent is given (Copilot).** The host writes each file into a private folder
(below) and types the reply as agent-ha-bridge does: `@<path>` for each file, in order,
then the text, one line, then Enter: `@C:\…\attachments\9f2c…-screenshot.png What do you
see?`. Copilot CLI attaches a file a prompt mentions that way. A path can't hold
whitespace (nothing quotes it), so a host whose folder has a space in its path refuses
replies with attachments. The text rules above apply to the text alone, so a reply of
files only is allowed, and one whose text starts with `/` or `!` is still refused.

**Names and types.** Any file is accepted. A host keeps a safe form of the name: the
last path segment, letters, digits, `.`, `-` and `_` (anything else becomes `-`), a stem
of at most 48 characters, and an extension only if it is 1 to 12 letters or digits.
Copilot decides how to read a file from its extension, so for PNG, JPEG, GIF, WebP,
HEIC/HEIF and PDF the host goes by the file's own bytes: it gives them the matching
extension (a pasted `image` becomes `image.png`), and refuses a reply whose file is named
as one of these formats but is not one. `type` is informational only.

#### `attachment.start`

Request `data`:

```json
{
  "attachment": "4c1d7e2a-9b3f-4e6a-8c5d-0f1e2d3c4b5a",
  "name": "Screenshot 2026-10-28.png",
  "type": "image/png",
  "size": 183204,
  "sha256": "n4bQgYhMfWWaL-qgxVrQFaO_TxsrC4Is0V1sFbDwCgg"
}
```

| Field | Notes |
|---|---|
| `attachment` | A fresh UUID for this file, the client's. Starting again with the same id and the same `name`, `size` and `sha256` resumes it (after a reconnect, say) |
| `name` | 1 to 255 bytes of UTF-8. A pasted image with no name is sent as `image.png` or similar |
| `type` | Optional MIME type, at most 127 bytes |
| `size` | Bytes, 1 to 262,144; up to 104,857,600 with `blob`, or on the local pipe |
| `sha256` | The SHA-256 of the whole file, base64url without padding (43 characters) |
| `blob` | Optional, with `sessions.attach.blob`: `{ "id": "<ticket>", "key": "<32 bytes, base64url>" }`, a file already sealed and uploaded for this host ([Large files](#large-files-through-the-blob-store)). Absent: the file comes in chunks |

Result: `{ "received": 0, "complete": false }`: how many bytes the host has, and whether
that is the whole file. A file a typed reply has already used reads as complete, so a
client resending after a lost response sends nothing again. A host holds at most 32
attachments at once across devices; one more fails.

#### `attachment.chunk`

Request `data`: `{ "attachment": "<id>", "offset": 11264, "data": "<base64>" }`. `data` is
standard base64 of at most **11 KiB** (11,264 bytes), which keeps the request under 16 KiB
as sent: a chunk never holds an answer or a reply back by more than one small record. A
client sends one chunk at a time per file, each at the `received` the host last gave, and
waits for its response. A chunk the host already has (a resend) changes nothing; one at
any other offset fails, saying the offset expected.

Result: `{ "received": 22528, "complete": false }`. When the last byte arrives, the host
checks the size and SHA-256: `complete: true` if they match. If they don't, the request
fails and the host drops the file; the client starts it again with a new id.

#### `attachment.discard`

Request `data`: `{ "attachment": "<id>" }`. Result: `{ "discarded": true }`, or `false` for
an id the host does not hold for this device. A client sends it when the person removes a
file before sending, so the host frees it at once rather than when it expires.

**Failures.** `attachment.*` requests that fail a check (a field out of range, a wrong
offset, a hash mismatch, too many held) fail as requests (`ok: false`) with the reason, as
`push.*` do. A host without `sessions.attach` answers them `unknown op`.

#### Lifetime and privacy

* **One device's.** An attachment belongs to the device that sent it (the channel's peer).
  Another device can neither add to it, use it, see it nor remove it.
* **Held until used.** A host keeps a file in memory while it arrives and until a reply
  uses it; one over 256 KiB (from the blob store, or on the local pipe) is written instead,
  as it arrives, to `incoming/` in the private folder below, never a shared temporary
  folder. A host holds at most 1 GiB of those at once. One no reply uses within
  **15 minutes** of its last chunk (or of the last `attachment.start` that asked about it)
  is dropped, and its file deleted.
* **Staged only to be typed.** Just before typing, the host writes the reply's files into a
  folder of its own profile that only the user can open: on Windows a DACL that inherits
  nothing and grants only the user, as owner; elsewhere mode `0700`, files `0600`. Names
  start with a random 128-bit prefix, so nothing is ever overwritten. A folder that can't be
  made private, or is a link, is never written to and the reply is `refused`.
* **Gone after use.** Once the reply is typed (`delivered` or `unconfirmed`), its files leave
  the host's memory and `incoming/` and cannot be used again. The staged copies stay **24 hours**, since a
  working session reads the file only when it takes the prompt, then are deleted by a sweep
  that also runs when the host starts. A reply that types nothing (`refused`, `busy`,
  `stale`) deletes its staged copies at once and leaves the files held, so the person can
  send again with a new request.
* **Never in the clear outside the two ends.** Chunks travel inside the end-to-end channel,
  so the relay sees only frames of about 15 KiB. A host never logs names or contents, only
  counts and sizes.

A client sends a reply's files before the reply, and again (from what the host has) on
every resend of it, so a reply resent after the host restarted still finds them.

#### Large files through the blob store

With `sessions.attach.blob`, a file over 256 KiB goes through the relay's blob store
([blobs.md](blobs.md)) instead of in chunks:

1. The client asks its relay for a slot for the host's device (`blob-put`, the sealed and
   padded size), seals the file with a fresh key bound to the ticket's id, and uploads it
   straight to the URL it was given. It never passes through the relay.
2. It sends `attachment.start` with `blob: { id, key }`, and the file's `name`, `size`,
   `type` and `sha256` as for any attachment. The key travels only here.
3. The host fetches the blob itself, on its own relay connection (only it, the device the
   blob was put for, can), opens it as it arrives into `incoming/`, and checks the size and
   SHA-256. It then deletes the blob from the store, whether it was right or not.
4. The client sends `attachment.start` again, unchanged, until it reads `complete: true`
   (or fails), then names the attachment in `session.reply` as usual. `received` counts the
   bytes the host has opened, for progress.

A blob that does not open, does not match its SHA-256 or cannot be fetched fails the next
`attachment.start` for it once, with the reason, and is dropped; the client seals and
uploads it again under a new id. A host that has no relay with a blob store just then
fails `attachment.start` with a `blob` and stops offering `sessions.attach.blob`.
`attachment.chunk` for an attachment started with a `blob` fails.

### `session.stop`

Ends a session, as agent-ha-bridge's End session button does. Request `data`:

```json
{
  "id": "<session id>",
  "request": "3d5e7f90-1a2b-4c3d-8e4f-5a6b7c8d9e0f",
  "confirm": "q2vU3dJ7n0x-AbC_9zYw1A"
}
```

| Field | Notes |
|---|---|
| `id` | The session |
| `request` | A fresh UUID per press, with exactly `session.answer`'s semantics: every outcome is remembered for at least 10 minutes and returned for a repeat, which does nothing more; a repeat of a press still being acted on waits for it |
| `confirm` | Only on a second press: the token the first press's `confirm` outcome gave. At most 64 characters of `A–Z a–z 0–9 - _` |

**Two presses when the session is not idle, enforced by the host.** A session that is
`working`, `waiting`, or `idle` with `activity.background` (delegating) has a turn or work in
flight that ending it would throw away. The first press only arms: the host answers
`confirm` with a token, and ends nothing. A second press within the window (10 s, measured on
the host from when it armed) carrying that token ends it, if the session's process is still
the very one the first press saw, by id and start time. Anything short of that (no token, a
lapsed or unknown one, one from another session, a process that has been replaced) is
another first press: a new `confirm` and token, and nothing ended. Every device and client
can send `session.stop`, so this is the host's rule, never only a client's. An `idle` session
with nothing in the background ends on one press; a token sent for one is ignored. The host
checks the session again immediately before it acts, and one that has started working or
asking since the press arrived needs a second press after all (`confirm`). Several
first presses can be waiting at once (two devices); any one confirms, and confirming spends
them all.

Result: `{ "outcome": "<outcome>", "reason": "<text>" }`, with `reason` present for
anything but `ended`, and:

| `outcome` | Meaning | Also |
|---|---|---|
| `ended` | The session's process has exited. The host removes the session (`session.remove`) as soon as it sees that, normally before this result arrives | `forced: true` when it did not exit on its own and the host terminated it |
| `confirm` | Press again to end it. `reason` says what a second press interrupts ("this session is still working; the confirmation lapses in 10 s"), led by why an earlier press did not confirm, if one was sent | `confirm` (the token) and `confirmSeconds` (the window) |
| `stale` | The session, or its process, had already gone | |
| `busy` | An answer or a reply to this session is being typed, or another press is ending it already | |
| `refused` | The host will not end it: an invalid request, an elevated session or another user's, a process whose start time it cannot read (so cannot tell from a later one with the same id), the host itself, a platform without it yet, or agent-ha-bridge active for this user. Refused on the first press, before anything is armed | |
| `failed` | The host tried, and the session's process is still running (it could not be terminated, or agent-ha-bridge started meanwhile) | |

A client treats an outcome it doesn't know as `failed`, never as `ended` or `confirm`; a
`confirm` without a token it can send back is `failed` too.

**How a host ends it.** Gracefully first, as agent-ha-bridge does: `/exit` typed into the
agent's own input, so the CLI writes its transcript and releases its lock, and the session
stays resumable. If it has not exited within 12 s, the agent's process alone (never its
tree) is terminated. A session asking anything, in what the host reports or in its
transcript, is terminated without `/exit`, which would land in the question's input. From
the moment it starts until the outcome, the session is upserted as `ending`, and the host
holds it against answers and replies (`busy`), which share the per-session serialization.
The terminal window or tab the agent ran in is left open: the person may have other work in
it.

### `push.subscribe`

Registers this device for notifications from this host. A client sends it on every
channel it opens to a host while notifications are on, so a host that dropped the
subscription, or a browser that renewed it, is brought up to date. Request `data`:

```json
{
  "endpoint": "https://web.push.apple.com/QGuQyavXutnMhZ…",
  "keys": { "p256dh": "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV…", "auth": "BTBZMqHH6r4Tts7J_aSIgg" },
  "app": "https://danswett.github.io/agent-bridge/app/",
  "device": "iPhone · Agent Bridge"
}
```

| Field | Notes |
|---|---|
| `endpoint`, `keys` | Exactly what the browser's `PushSubscription.toJSON()` gives, for a subscription made with the relay's VAPID key (`GET /v1/push/key`). `endpoint` is https; `p256dh` is a base64url P-256 point, `auth` 16 base64url bytes |
| `app` | The client's own URL (https): where a tap on a notification opens it |
| `device` | Optional. How `push.list` names this device; the channel's peer name otherwise |

Result: `{ "subscribed": true, "count": 2 }`, `count` being how many devices the host now
notifies. A host keeps **one subscription per device** (the channel's peer id) and per
`endpoint`: subscribing again replaces the device's own, and an endpoint registered by a
new device id (a reinstalled app) replaces the old one. It keeps at most 16, dropping the
oldest. A request that fails the checks above fails as a request (`ok: false`) with the
reason.

**Only devices of the host's own account.** A push goes straight to a device, around the
relay's account-scoped routing, so the host keeps the account boundary itself: it takes
`push.*` only on a channel whose relay connection is signed in to the host's account and
lists the peer among that account's devices, and refuses anything else. It saves
subscriptions with the account they belong to, and drops them all if the profile signs
in as another account.

### `push.unsubscribe`

Request `data`: `{}` or `{ "endpoint": "…" }`. Removes this device's subscription, and the
one with `endpoint` if given (whoever holds an endpoint can already send to it). Result:
`{ "removed": true }`. A host that is offline when a device turns notifications off
learns it from the push service instead: it drops a subscription the service answers
404 or 410 for.

### `push.list`

Request `data`: `{}`. Result: the devices this host notifies, oldest first:

```json
{ "devices": [ { "device": "4f0e…", "name": "iPhone · Agent Bridge", "service": "web.push.apple.com", "since": "2026-10-28T04:22:43.182Z", "current": true } ] }
```

`current` marks the calling device. Endpoints are never sent back: one is a capability to
notify that device.

## Notifications

A host with `push` notifies every subscribed device when one of its sessions needs the
person or has something to say, whether or not the app is open, the same moments
agent-ha-bridge notifies a phone through Home Assistant.

### When a host notifies

| `kind` | When | Once per |
|---|---|---|
| `question` | A session has a pending `question`, still pending 2 s later. A question already pending when the host starts is notified too, as agent-ha-bridge's daemon does after a restart | Question `id`, for the life of the session: further upserts, or the question briefly reading as pending again after an answer is typed, do not repeat it |
| `finished` | A session went from `working` or `waiting` to `idle` with a non-empty response of the main agent's own (a delegate's latest text does not count), and is still idle 3 s later. Waiting on a sub-agent's question (`waitingOn`) counts only if the main agent's own response changed from before the wait (for a session the host found already waiting, from what it read at startup): answering a delegate while the main agent stayed idle ends no turn. A turn that carries on within that (the person is at the machine) is not notified. A session already idle when the host starts is not news. A sub-agent finishing is never notified | Finished turn: the same `turnCount` and response seen ending again is not repeated |
| `resolved` | A question that was notified is no longer pending: answered anywhere, or the session ended. Sent only to devices that were shown the question, and not to the device that answered it with `session.answer` (it was there). A `finished` that follows within 2 s is sent instead | Question |

A question answered before its 2 s are up is never notified. A notice that cannot be sent
because the host's relay connection is down is sent when it is back; a newer notice about
the same session replaces it first, so a device gets only the latest. A host sends at
most 20 pushes a minute (the relay allows 30 per account, across all its hosts) and holds
the rest, keeping only the latest per device and session, until there is room. A push
service answering 429 or 5xx is retried with backoff; 404 or 410 means the subscription
is gone, and the host drops it.

### Replacing and collapsing

Every notice about one session on one machine shares:

* a **tag**, `<machine id>:<session id>`: the device replaces the session's notification
  in place. A `resolved` notice turns a stale question into "no longer waiting"; a
  `finished` one replaces the question it followed.
* a **topic** (RFC 8030 `Topic`), the first 24 bytes of SHA-256 of
  `agent-bridge-push|<machine id>|<session id>`, base64url (32 characters; Apple refuses
  other lengths, spike S5 finding 6): the push service replaces an undelivered push with
  the newer one, so a phone that was offline gets one notice per session, not a backlog.

| `kind` | TTL | Urgency |
|---|---|---|
| `question`, `resolved` | 24 h | `high`, `normal` |
| `finished` | 6 h | `normal` |

### The payload

What the host encrypts is a [Declarative Web Push](https://webkit.org/blog/16535/meet-declarative-web-push/)
message, which Safari 18.4+ shows without running the service worker, and which a client's
service worker shows from the same fields everywhere else:

```json
{
  "web_push": 8030,
  "notification": {
    "title": "Pick the session store",
    "body": "DSWETT-DEV-VM1 · waiting on you\nWhich database should the session store use?",
    "navigate": "https://danswett.github.io/agent-bridge/app/?host=53d1997f…&session=0f3c…&question=toolu_01",
    "tag": "53d1997f…:0f3c…",
    "lang": "en",
    "data": { "v": 1, "kind": "question", "machine": "53d1997f…", "session": "0f3c…", "question": "toolu_01", "at": "2026-10-28T04:22:43.182Z" }
  }
}
```

| Field | Notes |
|---|---|
| `title` | The session's `title`, or "Copilot session" (by agent) without one. A sub-agent's question (Question `agent`) is titled "*name* asks · *title*", with the sub-agent's `name` from `subagents`; it keeps the session's tag, so the newer of the session's and its sub-agents' questions replaces the older. At most 120 bytes |
| `body` | A first line naming the machine and what happened, then the text. `question`: "`<machine>` · waiting on you" ("waiting for your approval" for an `approval`, "waiting at the machine" for one marked `answerAtMachine`), then the question's `message` ("This session is waiting for an answer." if empty). `finished`: "`<machine>` · finished", then the first 880 characters of the main agent's own response (`own.response` while `source.response` names a sub-agent, else `response`), as agent-ha-bridge's preview, "…" if cut. `resolved`: "`<machine>` · no longer waiting", or "· session ended". Whitespace runs are collapsed to one space for a lock screen. At most 1 KiB |
| `navigate` | The subscription's `app` with `host`, `session` and, for `question` and `resolved`, `question`. A client opens that machine and session, and the question ready to answer |
| `silent` | `true` for `resolved`, which replaces a question without alerting again |
| `data.kind` | `question`, `finished` or `resolved`. A client reads a kind it doesn't know as `question` |
| `data.machine`, `data.session`, `data.question` | The machine's device id, the session's `id`, the question's `id` (not for `finished`) |
| `data.at` | When the host sent it |

The JSON as encrypted is at most **3 KiB**: under RFC 8291's one 4096-byte record (3993
bytes of plaintext) and Apple's 4 KiB cap. The host cuts the body, then the title, never
mid-character, until it fits (`PushNotice.Fit` in C#). A client must show something for
any push, even an unreadable one: iOS revokes notifications from an app whose push shows
nothing.

### Privacy

Nothing a notice says reaches the relay or the push service (Apple, Google, Mozilla,
Microsoft) in plaintext. The host encrypts each push to the subscribing browser's own key
(RFC 8291, aes128gcm); the relay only adds its VAPID signature and forwards the
ciphertext to an allowlisted push service. They learn that a push of some size went to an
endpoint at some time, and the push's `Topic`, `TTL` and `Urgency` headers: the topic is a
hash, which names no session. The subscription's keys reach only the host, over the
end-to-end channel. The device shows the notice on its lock screen.

A relay is not trusted to tell the truth about who is in an account. A hostile one could
present its own device as the account's and register a subscription, but it could equally
open a channel and read `sessions.list`, so `push` gives it nothing more.

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
| `status` | `"working"` \| `"waiting"` \| `"idle"` \| `"ending"` | `waiting` exactly when `question` is present, unless `ending`. `ending`: the host is ending the session (`session.stop`); it is removed once its process has gone, or shows its own status again if that fails. A client that does not know `ending` reads it as `idle` |
| `statusSince` | time | When `status` last changed |
| `startedAt` | time? | |
| `updatedAt` | time | When something a client shows last changed. Events that change nothing visible (hook runs, usage records, model internals) do **not** move it, so they never cause an upsert |
| `processId` | number? | The agent's process, when known |
| `activity` | Activity | |
| `question` | Question? | The question the agent is waiting on, if any |
| `driver` | `"person"` \| `"agent"`? | Who drives the current turn ([Driver](#driver)). Absent means `person`, and a host sends it only as `agent`. A client that does not know a value reads it as `person` |
| `driverPrincipal` | string? | With `driver` `agent`, which agent, when the host knows: at most 128 bytes, plain text, shown as secondary detail only. Absent otherwise |
| `subagents` | [Subagent](#subagent)[]? | The sub-agents the session started, at any depth ([design/subagents.md](../design/subagents.md)): running ones (`working`, `waiting`) first, newest start first, then the most recently finished. At most 20, and fewer if the [session budget](#session-budget) trims them. Absent when there are none. Finished ones stay until the session ends. Only Copilot sends them so far |
| `subagentsMore` | number? | Entries left out of `subagents`, by the host's limit or the budget. Absent when none are |
| `waitingOn` | string? | Status precedence rule 2: the id of the sub-agent whose question is `question`, while the session has none of its own. Absent otherwise |

**Status precedence** with sub-agents (first match wins): the session's own question
(`waiting`); a sub-agent's question (`waiting`, with `waitingOn`); the main agent's turn
running (`working`); the main agent idle with background agents or commands still running
(`idle` with `background`, shown as delegating); otherwise `idle`. Only the second is new,
and it was already `waiting` for a sub-agent's permission prompt before hosts said whose it
was.

### Driver

Whether a person or an agent is driving the session: agent-ha-bridge's purple edge. A client
draws an agent-driven session with purple in place of its state's colour: a steady purple edge
while it is idle, so a session left under an agent's control still says so, and the same
breathing glow as working or waiting, in purple, while it works or waits (held still for a viewer
who asked for reduced motion). It says so in words too ("Driven by an agent"), never by colour
alone.

* **A turn is a prompt started.** An agent's turn finishing is not a new turn, so a session left
  with an agent stays the agent's while it is idle. A prompt that starts with nobody's input waiting
  for it, or after that input's 600 s lapsed, was typed at the machine, and is the person's.
* **A reply or a launch** made through the host shows the session as its sender's at once, and the
  next prompt to start within 600 s (for a launch, its opening prompt) is theirs.
* **An answer** continues the turn it was asked in: that turn is the answerer's from the moment it
  is delivered, and nothing is left waiting for the next prompt, which is whoever's it is. An
  answer typed at the machine is not seen as input, so it takes nothing back until the person's
  next prompt, as on agent-ha-bridge (#134).
* **Taken back** by the person: an answer or reply from any of the person's devices (or a press by a
  person on the Home Assistant card) shows the session as the person's at once. Each request does
  so once: a resend, which the host answers from the outcome it remembers without typing anything,
  takes nothing back, and nor does an answer or reply confirmed only after an agent's input
  arrived meanwhile, which came later.
* **Who is an agent.** Agents have no principal of their own on the pipe or relay yet (design
  §5.5; the pipe refuses them anything but reading, #44), so today the only agent principal a host
  sees is a press, answer, reply or launch on its Home Assistant cards made by an account listed
  in `homeAssistant.agentUserIds` ([ha-connector.md](../ha-connector.md)), or a reply-card publish
  that names `driver: agent`. `driverPrincipal` is then `homeassistant:<user id>` for the account
  (absent for a publish, which names no account). Once agents have their own principal (#45), it
  is set from theirs the same way.
* It is kept in memory, as agent-ha-bridge keeps it: a host that restarts, and a session that goes
  and comes back, starts again as the person's.
* A change of driver is something a client shows, so it moves `updatedAt` and sends an upsert. It
  is never trimmed by the session budget.

### Activity

| Field | Type | Notes |
|---|---|---|
| `response` | string? (Markdown) | The latest assistant text, at most 16 KiB. When it's longer, the **end** is kept and `responseTruncated` is true. It may be a subagent's, including a background agent's while the session itself is idle, as on agent-ha-bridge's card; `source.response` then names it. `status`, `userMessage` and `model` are always the session's own |
| `responseTruncated` | bool? | |
| `reasoning` | string? | The latest reasoning text, at most 4 KiB (the end is kept). Like `response`, it may be a subagent's; `source.reasoning` then names it |
| `currentTool` | ToolCall? | The tool running now, while `working` |
| `history` | ToolCall[]? | Only in `session.get`: the latest 50 tool calls, oldest first |
| `userMessage` | string? | The latest user message, at most 2 KiB |
| `turnCount` | number? | User prompts whose turn has finished: the prompts sent, less one while a turn is still in progress (`working` or `waiting`). Not the agent's own model rounds: Copilot writes `assistant.turn_end` after every round of tool calls, and those are not counted |
| `compactions` | number? | Context compactions so far |
| `background` | Background? | Work the session started that is still running after its own turn. Absent when there is none |
| `source` | `{ "response"?: string, "reasoning"?: string }`? | Which sub-agent wrote the top-level `response` and `reasoning`, each on its own, by its `subagents` id: a main-agent reply can sit beside a delegate's reasoning. A field left out is the main agent's own. Absent while both are. Older clients ignore it and keep the last-writer-wins text |
| `own` | `{ "response"?, "responseTruncated"?, "reasoning"?, "at"? }`? | The main agent's own text for each field whose top-level text is not it: one `source` names a sub-agent for, or one that is absent while the host has the main agent's from the part of the transcript it only skimmed (a host starting after delegates filled what it parses). 16 KiB and 4 KiB, the end kept, as above; `at` is when the main agent last wrote either. A field `source` names but `own` lacks means the main agent has none. So a newer client shows, for each of `response` and `reasoning`, `own`'s when `source` names an agent for it, otherwise the top-level one, or `own`'s when that is absent; and never shows a delegate's words as the main agent's. Absent when it would be empty |

### Background

`{ "agents": 2, "shells": 1 }`. Present only while at least one is above zero, and then
both are sent.

| Field | Type | Notes |
|---|---|---|
| `agents` | number | Background agents still working: Copilot `task` agents started in background mode, including one woken again by a later message, at any depth. One between runs (a `subagents` entry `idle`) is not counted |
| `shells` | number | Background shell commands still running: started async, or started sync and left running in the background |

**`status` is unchanged by it.** A session whose own turn has ended is `idle`, truthfully;
`background` says why it is not finished. A client shows a session that is `idle` with
`background` as **delegating**: its own style, and the status text "Waiting for
background agents" when `agents` is above zero, otherwise "Waiting for background
commands". An older client ignores the field and shows `idle`, as before. A change in
either count is something a client shows, so it moves `updatedAt` and sends an upsert.
Counts are never trimmed by the session budget.

### Subagent

One agent the session started, from `subagents` ([design/subagents.md](../design/subagents.md)
§2). The UI calls it a *delegate*.

| Field | Type | Notes |
|---|---|---|
| `id` | string | The agent's own id (Copilot's `agentId`) |
| `parent` | string? | The `id` of the sub-agent that started it; absent when the main agent did. A client lists it under its parent |
| `kind` | `"background"` \| `"task"` | `background` runs past its starter's turn and can be woken by a message; `task` is part of its starter's turn. Unknown reads as `task` |
| `name` | string? | Its label: the starter's description of the job, else its agent type. At most 120 bytes |
| `task` | string? | The prompt it was given, at most 512 bytes, the **start** kept |
| `model` | string? | Its own model, never the session's |
| `status` | `"working"` \| `"idle"` \| `"waiting"` \| `"ended"` | `waiting`: a question of its own is pending. `idle`: a background agent between runs, which a message can wake. `ended`: it can never run again (a task agent done, its own abort or failure, or the session's CLI exiting). Unknown reads as `idle` |
| `outcome` | `"done"` \| `"failed"`? | With `idle` and `ended` only: how its latest run went, for the `done` and `failed` glyphs (ui.md §2.1). Absent when the host never saw it end (only its start was read, or its CLI was replaced). Unknown reads as `done` |
| `startedAt` | time? | When it first started |
| `endedAt` | time? | With `idle` and `ended`: when its latest run ended |
| `line` | string? | Its latest response or reasoning, whitespace collapsed, at most 300 bytes, the **end** kept. A `line` changing alone does not move the session's `updatedAt` or send an upsert; the latest arrives with the next change, or from `session.get` |
| `currentTool` | ToolCall? | Its running tool, while `working` or `waiting` |
| `shells` | number | Background commands it started that are still running, while it runs. They count here and never in the session's `background.shells` (agent-ha-bridge#261) |
| `response`, `responseTruncated`, `reasoning`, `history` | | `session.get { id, agent }` only |

An entry starting, ending, or changing `status` or `outcome` moves the session's `updatedAt`.
A woken agent is the same entry working again, never a new one.

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
| `id` | string | The id of the tool call that asked it, or for a Copilot permission prompt `<requestId>#<digest>` ([Copilot permission prompts](#copilot-permission-prompts)). It stays the same while the question is pending |
| `kind` | `"form"` \| `"approval"` | `approval` is a permission prompt with fixed choices (Codex approvals, Copilot and Claude permission prompts). It always has exactly one `choice` field, named `decision`, with the options `approve` ("Approve once") and `deny`, so a client can style them. An answer to one is `{ "decision": "approve" }` or `{ "decision": "deny" }` |
| `message` | string (Markdown) | What the agent wrote, at most 8 KiB |
| `fields` | Field[] | In order. Empty for a plain message |
| `askedAt` | time | |
| `truncated` | bool? | The host had to cut `message` or drop `fields` to fit the budget. A client shows what it has and says to answer at the machine |
| `answerAtMachine` | bool? | No host can answer this prompt from another device: a Copilot permission request of a kind no host drives (or any, on a host without `sessions.approve`), a Claude permission prompt, a Codex approval seen only in its rollout or already typed into once. It still has its fields, so a client can say what it asks, but a client shows no controls for it, gives the session's status as "Waiting at the machine", and a `session.answer` for it is `refused` |
| `agent` | string? | The sub-agent that asked, by its `subagents` id, when it is not the session's own question (`waitingOn` names it too). A Copilot permission prompt is the CLI's one dialog whoever asked, and is answered the same way. A sub-agent's `ask_user` is always `answerAtMachine`: delivery types only into the main agent's, so nothing proves where the keys would land. And while a permission prompt or a sub-agent's question waits beside it, the session's own Copilot `ask_user` is `answerAtMachine` too, since the CLI may be showing the other one; a host also checks the transcript for a pending permission prompt just before typing an answer |

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
2. `subagents`: finished entries (`idle` and `ended`), then every `line`, then every
   `task`, then all but the first 8; each entry dropped is counted in `subagentsMore`.
3. `reasoning`, with `own.reasoning`.
4. `userMessage`.
5. `response`, to its last 4 KiB (`responseTruncated` set), and `own.response` likewise.
6. The question's `message`, to its first 2 KiB (`truncated` set).
7. The question's `fields` (`truncated` set).
8. `response` and `currentTool`, and `own.response`.

`own` is trimmed with the legacy field it stands beside, since keeping either whole could
break the budget alone: 16 KiB of emoji is 48 KiB of JSON. Past step 8, a host drops the
sub-agents' `currentTool`, then the remaining entries (into `subagentsMore`); no real session
comes near that.

A list page holds as many sessions as fit in 48 KiB, and always at least one, since every
session is at most 40 KiB.

## Where a host gets each field

| Agent | Sessions | Activity and status | Question |
|---|---|---|---|
| Copilot CLI | `~/.copilot/session-state/<id>/` with a live `inuse.<pid>.lock`, plus `workspace.yaml` | From `events.jsonl`: `user.message`, `assistant.message` (`content`, `toolRequests`), `tool.execution_start` and `_complete`, `assistant.turn_start` and `_end`, `session.compaction_complete`, `session.model_change` | A `tool.execution_start` with `toolName: "ask_user"`, using its `arguments.message` and `arguments.requestedSchema`. Pending until a `human_response.recorded` or `tool.execution_complete` with the same `toolCallId`. A `permission.requested` (its `promptRequest`, else its `permissionRequest`) is an `approval`, pending until the `permission.completed` with the same `requestId` |
| Claude Code | The bridge's registration files, else Claude Code's own `~/.claude/sessions/<pid>.json` (an interactive CLI, its pid running, written no earlier than that process started); and `~/.claude/projects/<slug>/<id>.jsonl` | Transcript `assistant` entries (`text`, `thinking`, `tool_use`) and `user` entries | A `tool_use` named `AskUserQuestion`, pending until a `tool_result` with its id. Claude Code 2.1.312 writes that `tool_use` only once the question is answered, so without agent-ha-bridge's `PreToolUse` hook a waiting question is not seen. A permission prompt, which only agent-ha-bridge's hook reports (its status `waiting`, newer than the transcript, over a running tool), is an `approval` with that tool's id, `answerAtMachine` |
| Codex | The bridge's registration files, else (Windows) `~/.codex/thread-writer-locks/<id>.lock`, as the Restart Manager reports who holds it open. The lock must be held by exactly one running `codex.exe`, and that one must be an interactive terminal: its command line read, with no subcommand (a prompt at most), `resume` or `fork`, and no `app-server` or `mcp-server` argument. That is a Codex run with `--no-daemon`, not `exec`, `review`, `migrate-rollouts` and the like. A lock shared with the daemon or with an unreadable Codex names nobody. Each decision is checked again after 30 s. A terminal holding several locks is the session of the one whose rollout `session_meta` is its own top-level thread (`id` and `session_id` equal, `thread_source` "user") with an event since it started, else of none. A lone lock whose rollout names another thread (a sub-agent's) is not a session either. A registration agent-ha-bridge marked ended keeps its thread from being found this way; and `~/.codex/sessions/…/rollout-*.jsonl` | Rollout `event_msg` payloads | An approval agent-ha-bridge's hook recorded in `<id>.approval.json` beside the registration: an `approval` whose `id` is the marker's `DecisionId` (its generation), `answerAtMachine` once the marker records agent-ha-bridge's own attempt (`DeliveryAttempted`). An `exec_approval_request` or `apply_patch_approval_request` in the rollout with no marker is an `approval` keyed by its `call_id`, `answerAtMachine` |

Only Copilot reports `background` and `subagents` so far. Claude Code can run Bash with
`run_in_background`, and Codex may have equivalents, but no transcript of either doing
so has been examined yet, so neither sends it.

On a machine that still runs agent-ha-bridge, a shadow host **only reads**. It never
opens the hook spool, never writes into a session folder, and never deletes or locks
anything another process uses.

### Background work (Copilot)

Read from the same `events.jsonl`. Every rule below was checked against real transcripts
(85 background agents and 535 background shells; the evidence is in the pull request
that added `background`).

* **Agents are tracked by id, never counted.** One starts with `subagent.started` whose
  `executionMode` is `background`; its own events carry its `agentId` at the top level.
  It is working until its own turn bookkeeping says it has stopped: an
  `assistant.turn_end` stamped with its `agentId` whose turn's **latest** reply asked for
  no tools (a reply asking for tools, their results, then a final reply with nothing to run
  is a turn that ends the run, #84). A later `user.message` stamped with its `agentId` (a
  message sent to it) makes it work again.
  Its own `abort` or `session.shutdown` (stamped with its `agentId`) ends its run and
  nothing else. `subagent.completed` (by `toolCallId`) and a `system.notification` of kind
  `agent_idle` (by `kind.agentId`) also end a run, but neither is enough alone:
  `subagent.completed` is written once, after the first run only, and again for every
  agent at shutdown (only the first counts); `agent_idle` arrives when the parent next
  takes notifications, which was up to 90 minutes late, and for 3 of 44 later runs never.
  A late one can arrive after a message has woken the agent again, so each is spent on a
  run that already ended without one, and only a notice no run is owed ends the current
  run.
* **Shells, likewise by id.** A main-agent `tool.execution_complete` whose whole result is
  `<command started in background with shellId: X>` or `<command with shellId: X moved to
  background by the user…>`, or whose last line is `<command with shellId: X is still
  running…>`, starts one. A last line `<shellId: X completed with exit code N>`, a whole
  result `<command with id: X stopped>`, or a `system.notification` of kind
  `shell_completed` with `kind.shellId` ends it. Markers anywhere else in a result are
  quoted text (a command printing a transcript, say) and are ignored. Detached shells
  (`started in detached background`) are services meant to outlive the session and are
  not counted. A subagent's shells are its own work, covered by its agent, and do not
  report their ends to the session.
* **Nothing outlives its CLI process.** `session.shutdown` ends everything, and work
  that began before the session's current process started is dropped: a CLI that was
  replaced can never report back what it started.
* **Sub-agent entries** (`subagents`) come from the same events, for every agent, `task`
  (sync) ones included. Checked against the CLI 1.0.105 transcripts on one machine; the
  recorded shapes are in `tests/fixtures/copilot/subagents.jsonl`.
  * `subagent.started` is stamped with the new agent's `agentId` and carries `toolCallId`,
    `executionMode`, `agentDisplayName`, `agentType`, `model`, and `parentId`: the
    `agentId` of the sub-agent that started it, absent for the main agent's. A `task`
    tool call stamped with an `agentId` says the same.
  * `name` is the starting `task` call's `description` argument, else
    `agentDisplayName`, else the agent type. `task` is its `prompt` argument, else the first
    `user.message` stamped with the agent's id, which is that prompt.
  * `model` is `subagent.started`'s, then its own `subagent.configured`,
    `session.model_change` and `assistant.message` (#73: never the session's).
  * A run ends as for background agents above, with outcome `done`. A task agent is
    `ended` when its run ends, or when its `task` call completes (`failed` if the call
    failed), or when the main agent's turn is aborted (`failed`) while it was that turn's:
    a task agent the main agent's `task` started, or one nested only under such agents. One a
    background agent started belongs to that agent's run, and the main agent's abort leaves
    it alone. `subagent.failed` or the
    agent's own `abort` ends it `failed`, even when no run of it was seen going, and clears
    any question of its still pending; its own
    `session.shutdown` ends it, `failed` only if mid-run. The session's
    `session.shutdown` ends every entry: `failed` if it was mid-run.
  * Its shells are read from its own tool results with the same markers, skimmed ones
    included, and count on its entry while it runs; their ends come back only in its own
    results (never a `shell_completed` notification to the session).
  * The main agent's own text (`own`) is read from the skimmed part of the transcript too, so
    a host starting after delegates filled the parsed window still has it.
  * **Questions:** a sub-agent's `permission.requested` is written unstamped, naming the tool
    call it gates; that call's `agentId` says whose it is. A `tool.execution_start` of
    `ask_user` stamped with an `agentId` would be that agent's question, but Copilot gives
    its sub-agents no `ask_user`: none of 33,966 sub-agent tool calls in two months of
    transcripts on that machine was one.
