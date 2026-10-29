# How the host answers questions

`session.answer` ([contract](protocol/v1-sessions.md#sessionanswer)) types a person's answer
into the agent's own prompt, as if it had been typed at the machine. The prompt stays
answerable at the terminal the whole time; whichever answer lands first wins. This page is
how the host does it and what it refuses.

## What can be answered

| Agent | Question | Answered |
|---|---|---|
| Copilot CLI | `ask_user` with no form: free text | Yes: the words, then Enter |
| Copilot CLI | `ask_user` form (`requestedSchema`): `choice` (`enum`, `oneOf`, `anyOf`), `boolean`, `multi` (`type: array`), `text`, `number`, any number of fields | Yes |
| Copilot CLI | Other words for a question that is one option list | Yes (`allowOther` is set on exactly that shape) |
| Copilot CLI | Other in a form of several fields, or in a checkbox list | Refused: no walk reaches it |
| Copilot CLI | The older `question` / `choices` format | Refused: agent-ha-bridge never drove it, and no current build writes it |
| Copilot CLI | Permission prompts: a shell command, path access, a file edit or read, a URL, an MCP tool, a custom tool | Yes, beyond agent-ha-bridge (below): approve once or deny. Refused, `answerAtMachine`, when `permissions.remoteApproval` is off |
| Copilot CLI | Other permission prompts (hooks, memory, workflows, extensions), a request to run outside the sandbox, one whose details cannot be read | Refused, `answerAtMachine` |
| Claude Code | `AskUserQuestion`, one to four questions, each one option (`choice`) or several (`multi`, at least one, the first nine rows) | Yes |
| Claude Code | Free text in `AskUserQuestion` ("Type something") | Refused: no field offers `allowOther` (below) |
| Claude Code | More than four questions, a question without options, two questions with the same text | Refused, `answerAtMachine` |
| Claude Code | Permission prompts | Refused, `answerAtMachine`: agent-ha-bridge's hook reports them and offers only its reply box, best effort |
| Codex | An approval agent-ha-bridge's hook recorded (`<id>.approval.json`) | Yes: approve or deny, always `unconfirmed` (below) |
| Codex | An approval seen only in the rollout, or one agent-ha-bridge has already typed into | Refused, `answerAtMachine` |

These are what agent-ha-bridge answers, with two deliberate gaps and one deliberate
addition. The addition, at the user's request: Copilot's permission prompts, which
agent-ha-bridge only mirrors as an alert. The gaps: Claude's free
text: agent-ha-bridge's only walk to "Type something" presses Down two rows past the
options, Enter, the words and Enter, and its own code marks that path "not re-verified"
(its README says `AskUserQuestion` never ran against a real question); a walk that misses
answers in the person's name. And a Claude multi-select left empty, which agent-ha-bridge
refuses for Claude too. Both can be added once walked live.

macOS and Linux do not offer `sessions.answer` yet (tmux delivery comes later); an answer
sent anyway is refused with "not supported on this platform yet".

## The keys (Copilot)

Ported from agent-ha-bridge (`hooks/decision-inject.ps1`), where each was settled against
live prompts. Every send is one attach-write-detach: a burst of characters, then, after a
pause, Enter as a keystroke of its own (a burst is a paste, and Enter inside it would not
submit). Arrows are the VT sequences `ESC [ A` / `ESC [ B` typed as characters.

* **Free text:** the words, 300 ms, Enter.
* **A form,** field by field in schema order, each committed with its own Enter (120 ms
  after its keys, 240 ms before the next field):
  * choice and boolean: Up × (N−1) to reach the top row wherever the caret was left (Up
    stops at the first option), then Down to the chosen row. A boolean is a Yes / No list.
  * multi: Up × (N−1) to the top row first, then one walk down the checkbox list, Space
    only on rows whose state must change (the default opens some ticked), each toggle its
    own send 350 ms apart; just the walk to the top and Enter when the defaults are the
    answer. An empty answer only where `minItems` allows it. That Up stops at the first
    row of a checkbox list, as it does in a single-choice list, is assumed rather than
    seen live: check it on the test VM.
  * text and number: the words (a number as its JSON text). An optional text field left
    out is skipped with Enter; an option list cannot be left out, since Enter commits
    whatever its caret is on.
* **Other** (a lone option list): Up × (N−1), Down × N as one burst with no Enter, 400 ms
  for Other to hand over the input, then the words, 300 ms, Enter (agent-ha-bridge #240:
  Down on the last option opens Other at once, and an empty Enter returns to the last
  option).

Typed words have every control character (C0, C1, CR, LF, tab, escape) and line or
paragraph separator collapsed to a space first, and a lone surrogate replaced. The worker
writes each UTF-16 code unit as its own key down and key up with the character in
`UnicodeChar`, so an emoji is its surrogate pair as two key events and nothing passes
through a code page.

**What no plan can see.** The keys assume the prompt as the transcript describes it. Input
somebody left in it at the terminal without submitting (boxes ticked in a checkbox list,
words half-typed into a text field) is in no transcript, so the keys add to it: ticking
`b` after `a` was ticked locally submits both. The caret position is normalized (above);
ticks and typed text cannot be, short of reading the screen. Such an answer is never
reported as delivered: the structured record differs, so the outcome is `unconfirmed`
and the client says to check at the machine. agent-ha-bridge has the same exposure.

The keys are planned from the tool call's own arguments in the transcript, not from the
`Question` the client saw, because they depend on things a `Question` does not carry
(which rows open ticked, `minItems`, the older format). If the two
readings of the schema disagree on fields, order, types or option values, nothing is typed.

## The keys (Copilot permission prompts)

Not in agent-ha-bridge; added at the user's request. Copilot CLI (1.0.10x) draws a
permission prompt as a numbered list ("1. Yes", "2. Yes, and don't ask again for …",
"3. No, and tell Copilot what to do differently (Esc to stop)"; or for path access "1. Yes",
"2. Yes, and remember these paths for this session", …, "4. No (Esc)"). The options vary
with the kind and with what Copilot may offer, and the path prompt opens on its "remember"
option, so Enter is never pressed and no option is found by position:

* The host reads the session's screen (`agent-bridge-injector <pid> --screen`: the visible
  window of its console, read-only) and takes the last block of numbered rows, numbered
  from 1 without a gap.
* **Approve once** is the one row whose label is exactly "Yes": Copilot's approve-once in
  every permission prompt. Its digit commits it directly whatever is highlighted. Every
  "Yes, and …" grants more than this request and is never chosen; a prompt without a
  single plain "Yes" is refused.
* **Deny** is a lone Escape, and only when the last row is a "No" labelled as Escape's
  ("(Esc)" or "(Esc to stop)"): both of Copilot's lists then deny with no message. A deny
  takes no text, so nothing can be typed into the "tell Copilot" box.
* Refused, nothing typed: no prompt on screen, more than nine options, a last row that is
  not a "No", or that "No" row highlighted (someone at the machine has its message box
  open, where a digit would be typed as text).
* The prompt must show what it is for above its options: the host looks, within 30 rows,
  for the start of the command (40 characters), a path's or file's last segment, the URL's
  host, or the tool's name, whitespace aside since long lines wrap. Not found is refused.
* The key is sent with every row from there through the options as read: inside the same
  attach, just before writing, the worker reads those rows again and writes nothing unless
  they all still read the same, highlight included (`fail screen 0`, `refused`, and the
  prompt stays answerable). So the "No" row selected at the machine in between stops an
  approval, and so does another prompt with the same options drawn in its place (the next
  path request, say).

Bound to the request. The question's id is `<requestId>#<digest>`, the digest a hash of
the message the client was shown. Immediately before typing, the transcript must show
that `permission.requested` with no `permission.completed` (and no session abort or
shutdown) after it, reading exactly as shown (`stale` otherwise), and no other permission
request or `ask_user` waiting, since the screen shows one prompt at a time and could be
showing the other (`refused`). The transcript is checked so before the screen is read and
again after, so what the screen showed was this request while it was still waiting. A
request to run outside the sandbox, a kind not listed
above, or one whose details cannot be read is `answerAtMachine`.

Delivered when that request's `permission.completed` records `approved` for an approve.
For a deny, it is `denied-interactively-by-user` with no feedback, or what Escape on
"No (Esc to stop)" actually records (seen live with Copilot CLI 1.0.105): `cancelled`
("Session aborted") followed by the session's own `abort` of the turn. A `cancelled` with
no abort after it never counts. Anything else recorded (`approved-for-session`, a message,
the other decision), the session ending, or nothing
within 15 s is `unconfirmed`. The digit was chosen from the plain "Yes" row and checked at
the moment it was typed, which is what makes an approval once; the record confirms the
request was approved.

**Turning it off.** The host's own settings, `config.json` in its profile folder
(`agent-bridge-host whoami` prints the folder), beside the device key:

```json
{ "permissions": { "remoteApproval": false } }
```

It is on when the file or the setting is absent. A file that cannot be read, or a value
that is not `true` or `false`, turns it off, and the host logs why. It is read when
`serve` starts. Off, `hello` leaves out `sessions.approve`, every Copilot permission
prompt is `answerAtMachine`, and an approval sent anyway is refused with "approving Copilot
permission prompts from another device is turned off on this machine".

**Untested live.** Whether the screen reads the same through Windows Terminal (a pseudo
console) as through a classic console is to be checked on a real session before relying
on it; if it cannot be read, approvals are refused and nothing is typed.

## The keys (Claude Code)

Ported from agent-ha-bridge (`Get-BridgeFormPayloads`, `Send-CopilotSessionForm`,
`Complete-DaemonClaudeAnswer`), with the same timings as Copilot's forms. Each question in
order, committed with its own Enter:

* **One option:** Down from the first row to the chosen one, then Enter. The prompt opens
  on its first row and agent-ha-bridge counts from there; whether Up stops at the top of a
  Claude list has not been seen, so the caret is not normalized as it is for Copilot.
* **Several options:** Claude numbers the rows, and a digit toggles its row wherever the
  cursor is. Each chosen row's digit is a send of its own, 350 ms apart (as one burst the
  toggles were dropped), then Down once per row, the options plus "Type something", lands
  on Submit, and Enter presses it. agent-ha-bridge verified this against Claude Code
  2.1.273: `2`, `3` and four Downs recorded "Billing, Search".
* **The review screen.** Claude can end on a screen that needs one more Enter. If nothing
  is recorded for the call 1.5 s after the keys, and the transcript still shows it
  unanswered, the host presses Enter once, with the same last checks as the answer. Never
  twice.

Delivered when the `tool_result` for the call's `tool_use` id records the answer typed:
every question's answer in `toolUseResult.answers` (a multi-select's labels in list order
joined by ", "), or, for a build that does not write that, `"question"="answer"` in the
result's text. A result marked `is_error` (declined), a different answer, an interrupt,
or nothing within 15 s is `unconfirmed`.

## The keys (Codex)

agent-ha-bridge's `Invoke-PendingCodexApprovals`: `y` to approve or `n` to deny, then
Enter 300 ms later, as a reply is typed. The approval is the one agent-ha-bridge's
`PermissionRequest` hook recorded in `<id>.approval.json`, and its `DecisionId` is the
question's id: the generation. Immediately before typing the host reads the marker again
and types nothing unless it names this session, holds the same generation, and records no
attempt of agent-ha-bridge's own (`DeliveryAttempted`). Missing is `stale`; unreadable,
or anything else its own sender would reject, is `refused`. The host only reads the
marker: it never writes, renames or removes it, so its attempt is recorded in its own
ledger (below), not in agent-ha-bridge's file.

Codex records nothing that says how an approval was answered: the hook removes the marker
on the next event however it was answered, and the rollout does not name the approval. So
a typed approval is always `unconfirmed`, as agent-ha-bridge logs it ("native approval is
not confirmed"), and the client says to check at the machine.

Claude and Codex sessions are known to this host only through agent-ha-bridge's hook
registrations, so answering them needs its Claude or Codex adapter installed (with its
daemon stopped, or G5 refuses). Its Codex hook writes the approval marker only after
reaching Home Assistant (`Enter-BridgeAdapterSession`); without that, an approval is seen
at most in the rollout, and answered at the machine.

## Before and after typing

In order, for every answer:

1. Platform, then agent-ha-bridge (below).
2. The session is live and its current question has the id the client sent (`stale`
   otherwise); the question is not `answerAtMachine`; the agent and question kind are
   supported; `Answers.Check` passes.
3. One answer or reply per session at a time, through the `DeliveryGate` that replies
   ([host-replies.md](host-replies.md)) share (`busy` for a different request; a resend of
   the request being typed waits for its outcome instead). Every request UUID's outcome,
   whatever it was (`stale`, `refused` and `busy` included), is remembered for 15 minutes
   and returned for a resend, which types nothing.
4. The session's process is still the live agent process; it does not run elevated
   (integrity level High or above, or unreadable, is refused).
5. One answer is typed at a time across sessions, and its turn is taken before these last
   checks: agent-ha-bridge again, then the agent's own record: for Copilot, the tool call's
   start is there and no `human_response.recorded`, `tool.execution_complete` or session
   abort/shutdown follows it (for a permission prompt, the request as above, then the
   screen); for Claude, the `AskUserQuestion` `tool_use` is there, in the
   session's own entries, with no `tool_result` or interrupt after it; for Codex, the
   approval marker as above (`stale` otherwise). Identity, never time.
6. The prompt is claimed in the host's attempt ledger, `answer-attempts.json` in its
   profile folder, written through to disk. A prompt already claimed (by any request,
   before or after a restart) or a ledger that cannot be read or written is `refused`:
   agent-ha-bridge's rule that a prompt gets one attempt (`Start-CopilotDecisionAttempt`,
   `DeliveryAttempted`), since nobody can say what a half-typed prompt holds.
7. The worker types the plan. Nothing written (it could not start, attach or open the
   console input) is `refused`, and releases the claim. Anything after its first write that
   is not a clean finish (the worker died, hung, or the console closed) is `unconfirmed`,
   never retried, and the claim stands.
8. Up to 15 s, the transcript after the point read in step 5 is watched for that call. For
   Copilot, `delivered` only when the session recorded the answer typed: the structured
   `human_response.recorded` content field by field (choices by value, words with
   whitespace runs collapsed, lists as sets), or, for free text only, the rendered
   `User responded: …`. A form, even of one field, needs the structured record: its
   rendering trims, renders `a=x, b=y` (equally `a="x, b=y"`), and cannot tell options
   `x`, `x ` and `pick=x` apart. A different record, an error, a skip, the session
   ending, or nothing is `unconfirmed` with the reason. Claude and Codex as above.

## agent-ha-bridge (design G5)

While agent-ha-bridge's daemon is active for this user the host offers no `sessions.answer`
in `hello` (computed per `hello`) and refuses every answer with the reason, checked when the
request arrives and again just before typing. There is no override. It is active when any
of these is found:

* a mutex in this logon session's `Local\` namespace (`\Sessions\<n>\BaseNamedObjects`,
  listed with `NtQueryDirectoryObject`) named `AgentBridgeDaemon[_<hex id>]` (the daemon),
  `CopilotBridgeSupervisor[_<hex id>]` (its supervisor) or `CopilotBridgeDaemon[_<hex id>]`
  (older installs);
* the same names opened directly for every scheduled task named `AgentBridgeDaemon_<id>`,
  `AgentBridgeDaemon` or `CopilotBridgeDaemon`, plus the unsuffixed names;
* such a task in the Running state.

If neither the namespace listing nor the task scheduler can be read, it counts as active.

## The injector worker

`agent-bridge-injector.exe <pid>` reads the plan on stdin and types it, reporting
`writing <n>` (flushed) before every console write, so the host always knows whether a key
may have landed. A send may carry screen rows and their text, written only if every row
still reads so. `agent-bridge-injector.exe <pid> --screen` types nothing and prints the
visible window's rows. It is a separate process because Windows ends every process attached to
a console when that console's window closes (spike S1). It ignores Ctrl+C, re-registering
after each attach. One worker runs at a time. The host only reads everything else: it
never writes, locks, renames or deletes anything the agents or agent-ha-bridge own.

## Deploying on Windows

```powershell
dotnet publish src/AgentBridge.Host -c Release -r win-x64 --self-contained false -o <dir>
dotnet publish src/AgentBridge.Injector -c Release -r win-x64 -o <dir>   # NativeAOT; needs the VS C++ tools
<dir>\agent-bridge-host.exe sign-in --relay <url>
<dir>\agent-bridge-host.exe serve
```

`agent-bridge-injector.exe` must sit beside `agent-bridge-host.exe`; the host is published
without it (a NativeAOT executable is self-contained and cannot be a reference of a
framework-dependent one), so publish both into the same folder. Without it every answer is
refused, saying so. The host logs at start whether answering is on, and why not.
