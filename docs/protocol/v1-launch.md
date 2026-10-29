# Bridge Protocol v1: launching

Starting a new agent session on a machine from another device, or resuming one that has
ended, as agent-ha-bridge's launch card does: machine, agent, workspace, profile, model,
effort, context, permissions, an opening prompt, or a session to resume. It adds two ops to
[v1-sessions.md](v1-sessions.md), whose rules hold here too (JSON, sizes, unknown fields and
values, request UUIDs). C# records are in `src/AgentBridge.Protocol/Launch.cs`; the PWA
mirrors them in `pwa/js/launch.js`. How a Windows host carries a launch out is
[host-launch.md](../host-launch.md); its settings are [host-config.md](../host-config.md).
Resuming here a session that ended on another of the account's machines is a launch with a
`transfer` ([v1-transfer.md](v1-transfer.md)).

Nothing a client sends can widen what a machine allows. A session starts only in a folder a
person approved **at that machine**, and every value is checked against what the machine
offered. The relay sees none of it.

## Capability

| Capability | Ops |
|---|---|
| `sessions.launch` | `launch.options`, `session.launch`. Left out while the machine cannot start sessions: agent-ha-bridge running for the user (as for `sessions.answer`), launching turned off in its config, a host that is elevated or not on the person's desktop, or a platform without it yet. A client then offers no Launch |

A host with `sessions.launch` and no approved workspace still offers it, so a client can say
how to approve one.

## `launch.options`

Request `data`: `{}`. Result, what `session.launch` can name on this machine now:

```json
{
  "agents": [
    {
      "id": "agency", "label": "Agency", "kind": "copilot",
      "models": ["gpt-6.1-sol", "auto", "claude-opus-5.5"],
      "efforts": ["none", "minimal", "low", "medium", "high", "xhigh", "max"],
      "contexts": ["default", "long_context"],
      "profiles": ["home", "work"],
      "defaults": { "model": "gpt-6.1-sol", "profile": "home" },
      "attachments": true
    },
    { "id": "codex", "label": "Codex", "kind": "codex", "models": ["gpt-6.1-sol"], "efforts": ["low", "high"], "contexts": ["400000"], "needsPrompt": true }
  ],
  "workspaces": [ { "label": "Bridge", "path": "C:\\Users\\me\\repos\\bridge", "isolate": true } ],
  "resumable": [
    { "id": "0f3c…", "agent": "copilot", "launcher": "agency", "title": "Fix the flaky test - bridge", "cwd": "C:\\Users\\me\\repos\\wt\\bridge-20261028-131443", "workspace": "Bridge", "updatedAt": "2026-10-28T04:22:43.182Z" }
  ],
  "defaults": { "agent": "agency", "workspace": "Bridge", "permissions": "ask" },
  "busy": "Busy: Claude in Bridge is waiting for a person to answer whether to trust that folder, …"
}
```

| Field | Notes |
|---|---|
| `agents` | The launchers installed on the machine, in the order its `auto` default prefers: `agency` (Copilot sessions with an Agency profile), `copilot`, `claude`, `codex`. `kind` is the session's `agent` once it runs. A client reads an `id` it does not know as one it cannot launch |
| `agents[].models`, `efforts`, `contexts` | The values the launcher takes, at most 64 each. None of them is the agent's own default, which a client offers too and sends as `""` |
| `agents[].profiles` | Agency only: the profiles it lists (or the subset the machine offers) |
| `agents[].defaults` | What the machine starts it with when a launch does not say: `model`, `effort`, `context`, `profile`. Absent means the agent's own |
| `agents[].attachments` | The opening prompt can carry attachments (Copilot and Agency, with `sessions.attach`) |
| `agents[].needsPrompt` | It creates its session only on a first message (Codex), so a launch must carry a prompt |
| `workspaces` | The folders approved at the machine that exist now, at most 64. `label` is what a launch names; `isolate` means each new session starts in a git worktree of its own |
| `resumable` | Sessions that can be resumed, newest first, at most 50 (12 by default): ended (never live), and only those whose folder is in an approved workspace or a worktree the host made for one. None of an agent whose discovery is held ([below](#while-discovery-is-held)). `launcher` resumes it; `title` is at most 120 characters, unique in the list |
| `defaults` | `agent` and `workspace` a launch gets when it names none, and `permissions`, `ask` or `allowAll` |
| `busy` | Present when a launch now would be `busy`, saying why |

The result fits 40 KiB: a host drops the oldest resumable sessions, then the longest model
lists, until it does.

## `session.launch`

Request `data`:

```json
{
  "request": "2b6e1f0a-7c3d-4e5f-8a9b-0c1d2e3f4a5b",
  "agent": "agency",
  "workspace": "Bridge",
  "profile": "work",
  "model": "gpt-6.1-sol",
  "effort": "",
  "permissions": "ask",
  "prompt": "Read docs/design.md, then implement the next slice.",
  "attachments": [ { "id": "4c1d7e2a-9b3f-4e6a-8c5d-0f1e2d3c4b5a" } ]
}
```

| Field | Notes |
|---|---|
| `request` | A fresh UUID per press, with exactly `session.answer`'s semantics: every outcome is remembered for at least 10 minutes and returned for a repeat, which starts nothing; a repeat of a launch still starting waits for it. After `busy` or `refused`, a new press uses a new `request` |
| `agent` | A launcher `id` from `launch.options`. Absent: the machine's default; for a resume, the launcher the session lists |
| `workspace` | A workspace `label`. Absent: the machine's default. Not with `resume` |
| `resume` | A `resumable` session's `id`, to reopen it in its own folder, instead of starting a new one |
| `transfer` | With `sessions.transfer`: `{ from, session, command }`, a session from another machine of the account, brought here and resumed under a new id in `workspace`, which it then requires ([v1-transfer.md](v1-transfer.md)). Not with `resume` |
| `profile` | Agency only: one of its `profiles`. Absent: its default |
| `model`, `effort`, `context` | One of the agent's values, or `""` for the agent's own default over the machine's. Absent: the machine's default (`agents[].defaults`) |
| `permissions` | `ask` (permission prompts as usual) or `allowAll` (launched without them: Copilot `--allow-all`, Claude `--dangerously-skip-permissions`, Codex `--ask-for-approval never`). Absent: `defaults.permissions` |
| `prompt` | The opening prompt, at most **16 KiB** of UTF-8. Optional, except for an agent with `needsPrompt` |
| `attachments` | Optional, for an agent with `attachments`: files the same device sent with `attachment.start` and `attachment.chunk` first, at most 10, as for a reply ([Attachments](v1-sessions.md#attachments)) |

**The prompt goes whole.** A launch takes the prompt on the agent's command line, in one
message: there is no 255-character limit and no second channel. 16 KiB keeps the request
(at most 20 KiB in all) well inside a message. A longer handover goes as an attachment, which
the agent reads as a file, or as a reply once the session runs. As for a reply, every control
character, line breaks included, becomes a space (one line on a command line; an Enter would
cut it), and a prompt whose first character is `/` or `!` is `refused`, since the agent would
run it as a command. A host also refuses a prompt that does not fit the agent's command line
on that machine (Windows takes 32,767 characters, and an npm-installed agent, which runs
through `cmd.exe`, 8,191), saying so.

`LaunchProtocol.Check` in C# is the validation every host applies first. Names are at most
256 bytes; `model`, `effort` and `context` must look like an agent's own values (letters,
digits, `.` `_` `-` `:` `[` `]`, not starting with `-`), since they reach a command line.

Result: `{ "outcome", "reason", "session", "launched" }`:

| `outcome` | Meaning | Started? |
|---|---|---|
| `started` | The session registered: it is live on the host, and `session` is its `id`. It arrives through `sessions.subscribe` as any other, normally before this result | Yes |
| `unconfirmed` | The agent's process was started, but the host cannot say its session runs: it had not registered within the host's wait (60 s; a first launch that has to sign in can take longer), or the agent is asking a question in its window that only a person at the machine answers (whether to trust the folder; Codex's hook review). `reason` says which; `session` is set when the id is known in advance (Copilot, Agency, Claude, a resume). Never retried | Yes |
| `failed` | The host tried, and the agent did not run: it could not be started, or its process exited before its session registered | No session |
| `busy` | A session launched earlier on this machine is waiting for a person to answer whether to trust its folder, or the host's discovery is held ([below](#while-discovery-is-held)). `reason` says which. Nothing was started | No |
| `refused` | Nothing was started: an invalid request, a value the machine does not offer (`reason` lists what it does), no approved workspace, a session to resume that is not on the list or is running, its folder no longer approved, worktree isolation that failed or is at its cap, a session from another machine that could not be brought (the reason says why, the other machine's words included), a prompt too long for the command line, attachments the agent cannot take, launching off, agent-ha-bridge active for the user, an elevated host or one not on the desktop, or a platform without launching | No |

`reason` is present for anything but `started`. `launched` says what was settled on, after the
machine's defaults: `{ agent, workspace, resume, cwd, worktree, profile, model, effort,
context, permissions, transfer }`, where `cwd` is where the agent starts (a new worktree for an
isolated workspace, with `worktree: true`), and `transfer`, for a session brought from another
machine, is `{ from, session, bytes }` with `resume` its new id here. A client treats an outcome it does not know as
`unconfirmed`: something may have started.

**Trust is answered only at the machine.** An agent may ask, before its session registers,
whether to trust the folder (Copilot's "Confirm folder trust", Claude's "Do you trust this
folder?" and its Bypass Permissions warning). A host reads the agent's window to see it, says
so in `unconfirmed`, and never types into it. For the next two minutes, while the question
shows, other launches on that machine are `busy`, so no later press is ever taken as its
answer and nothing new starts over it, as agent-ha-bridge does. Once a person answers at the
machine the session registers and appears like any other.

**One start at a time.** A host starts one launch at a time (worktree, then the agent's
process); a second press waits its turn. Each launch is bound to its session by the id it was
given (Copilot, Agency and Claude take one; a resume keeps its own). Codex picks its own id,
so its launch is bound to a new Codex session that runs in the process the launch started, or
one under it. Only when that cannot be told is a session started after the launch enough, and
then only if no other Codex launch could claim it; no session is ever bound to two launches.
While a launch's agent runs, the session it was started on is not resumable: it is left out of
`resumable`, and a resume of it is `refused`, even after its launch has answered.

### While discovery is held

A host whose last discovery pass could not account for everything says so on `sessions.list`
and `sessions.synced` (`discoveryHeld`, [v1-sessions.md](v1-sessions.md#discovery-held)). A
live session it could not see may be the one a person asks to resume, and two agents on one
transcript would corrupt it (§6.3 invariants 6 and 10), so while an agent is held:

* `launch.options` offers none of that agent's sessions in `resumable`;
* a resume of one, or of any session not on the list while anything is held, is `busy`,
  saying what holds it ("Held: session discovery is waiting on an unrecognised copilot process
  (pid 4120)…"). A press a minute or two later goes ahead once the hold has cleared;
* a new Codex session is `busy` too, since its launch is bound to the first new Codex session
  to appear, which could be one that was running unseen;
* no worktree is reclaimed before an isolated launch: the launch goes on to the cap as if
  nothing were finished. The reclaimer asks again after reading which trees are in use, so a
  hold that begins during that read keeps them too.

The hold is checked again just before the agent is started, after any worktree has been made,
so one that began in those seconds is `busy` as well; the worktree's reservation and any staged
attachments are released.

A new session of any other kind decides nothing from an absence, and starts as usual.
agent-ha-bridge refused every launch while discovery was incomplete, which kept machines
from launching anything for a day at a time. Here the unaccounted-process grace keeps holds
short ([v1-sessions.md](v1-sessions.md#discovery-held)), and the decisions held are only those
that read an absence.
