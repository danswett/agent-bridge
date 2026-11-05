# How the host answers questions

`session.answer` ([contract](protocol/v1-sessions.md#sessionanswer)) types a person's answer
into the agent's own prompt, as if it had been typed at the machine. The prompt stays
answerable at the terminal the whole time; whichever answer lands first wins. This page is
how the host does it and what it refuses.

## What can be answered

| Agent | Question | Answered |
|---|---|---|
| Copilot CLI | `ask_user` with no form: free text | Yes: the words, then Enter |
| Copilot CLI | `ask_user` form (`requestedSchema`): `choice` (`type: string` with `enum` or `oneOf`), `boolean`, `multi` (`type: array`, items `anyOf` or `enum`), `text`, `number`, any number of fields | Yes |
| Copilot CLI | Other words for a question that is one option list | Yes (`allowOther` is set on exactly that shape) |
| Copilot CLI | Other in a form of several fields, or in a checkbox list | Refused: no walk reaches it |
| Copilot CLI | The older `question` / `choices` format | Refused: agent-ha-bridge never drove it, and no current build writes it |
| Copilot CLI | Permission prompts: a shell command, path access, a file edit or read, a URL, an MCP tool, a custom tool | Yes, beyond agent-ha-bridge (below): approve once or deny. Refused, `answerAtMachine`, when `permissions.remoteApproval` is off. A character not drawn where it runs and a right-to-left letter are shown escaped (#220); such an approval carries `escaped`, and Approve on it takes the two-step confirmation from a person's device this machine approved, through the relay only, or is held at the machine by `permissions.escapedApproval` (#803, [v1-sessions.md](protocol/v1-sessions.md#escaped-approvals)). Deny goes as for any approval. While other permission requests wait, such a character in the text Copilot was given for any of them, the listed one's own included, makes the listed one `answerAtMachine`, Deny included, unless it is only in a path request's access kind (#690, below) |
| Copilot CLI | Other permission prompts (hooks, memory, workflows, extensions), a request to run outside the sandbox, one whose details cannot be read | Refused, `answerAtMachine` |
| Claude Code | `AskUserQuestion`, one to four questions, each one option (`choice`) or several (`multi`, at least one, the first nine rows) | Yes |
| Claude Code | Free text in `AskUserQuestion` ("Type something") | Refused: no field offers `allowOther` (below) |
| Claude Code | More than four questions, a question without options, two questions with the same text | Refused, `answerAtMachine` |
| Claude Code | Permission prompts | Refused, `answerAtMachine`: agent-ha-bridge's hook reports them and offers only its reply box, best effort |
| Codex | An approval its `PermissionRequest` hook recorded (`<id>.approval.json`) | Yes: approve or deny, always `unconfirmed` (below). Its message is the whole command, fenced; a marker holding only the hook's 300-character preview (agent-ha-bridge's hook, an older host) or a command past 16,384 characters is `truncated`, and refused like any truncated question (#209). One whose `tool_input` holds anything but the command and its description (an MCP tool's `host`, a `workdir`), or a network approval (`description` `network-access <target>`), is answered at the machine (#338). One the host escaped anything in carries `escaped`, and Approve on it takes the same two-step confirmation as a Copilot permission prompt's, which names the approval's `<DecisionId>#<digest>` id (#803, #958) |
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

* **Free text:** the words, then Enter as a write of its own once they show on screen (as for a form's text field, below).
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
  * text and number: the words (a number as plain decimal, below), then, only once the session's
    screen shows them, Enter as a write of its own (#467). Typed 120 ms before their Enter,
    as option lists are, the words and the Enter reached a busy CLI as one paste, and Copilot
    1.0.105's form took the Enter as a new line in the text field: the answer sat typed but
    never submitted. Showing on screen proves the CLI has taken the words in as input of
    their own. How often the words' end already shows is counted on the field they go to:
    once the screen has changed from how it stood before the keys that open that field, and
    reads the same twice running (two reads in a row, not two reads with another between).
    Counted before, a label of the new field reading like the words could pass for them, and
    the same words given to the field before could hide them.
  * Every answer, a single list or Yes/No included, goes field by field, each field its own
    writes, never batched with another's:
    its keys, held to the field they were planned for, then its Enter alone once the screen
    has settled from them (so the change awaited is the form moving on, not an option list's
    highlight repainting). The next field's keys wait for the form to move off the screen the
    Enter was pressed on. Each write is held to those rows (`KeySend.Expect`): the injector
    types nothing of it unless they still read exactly so, in the same attach, just before
    its first key, since a permission prompt can open over the form at any moment and Enter
    picks whatever it highlights (sec-510). The last checks before typing (step 5:
    agent-ha-bridge, the call still unanswered, no other prompt waiting, the process) are made
    again before each write, and once more when the injector holds the typing slot
    (`KeyPlan.Proceed`). A refused write stops everything after it.
  * The rows a field's keys or Enter are held to must show the form at that field
    (`CopilotFormScreen`, from 1.0.105's `MIo`, `Uxn`, `Da` and `Ft`), each part below the
    last: exactly one "Copilot needs information." row (an MCP server's elicitation is headed
    with its own name, and may have a field of the same name), the question's own first line
    (as much of it as the row holds where it wraps), with more than one field the tab bar
    naming exactly this form's fields in order, the field's own heading (`<title ?? name>:`,
    on a row of its own; only the field being answered is drawn), and under it the
    "ctrl+d decline" and "esc cancel" hints, with the field's place by "tab next" and
    "shift+tab prev" when there is more than one; and no numbered "1. Yes" row of a
    permission prompt, bare or between a box's sides. A screen that has settled
    or moved on is not enough: a permission prompt that opens before its transcript entry is
    written reads just as steady, and keys held to its own rows would go into it, the Enter
    approving what it highlights (#510 review). Anything else stops the answer there, as
    below. A field whose title is not a non-empty string has no heading to look for, so is
    never typed into this way.
  * It fails closed: such an answer is not typed at all into a screen the host cannot
    read, nor words made only of spaces and line-drawing characters, which leave nothing to
    look for (`refused`, nothing typed); and a field or words that do not show, or a screen
    that does not settle, within 3 s, a screen that changed under a write, or a question no
    longer asked, leave it there, with nothing more pressed (`unconfirmed`, stage `typed`,
    for the person to finish at the machine). An Enter sent into a screen nobody has read is
    how #467 happened. What remains: keys reach the CLI in the order they were written, so a
    count taken too early can only send the words' Enter early, behind the words, which is
    reported `typed`, not `delivered`; rows are compared whole, so an animated form would be
    refused rather than typed into; a form drawn taller than the console's window, its
    heading or hints scrolled out of sight, is stopped rather than typed into, and so is a
    form whose tab bar is too wide to name every field.
  * An optional text field left
    out is skipped with Enter; an option list cannot be left out, since Enter commits
    whatever its caret is on.
  * text and number with a schema `default` (#662): Copilot opens the field with the default
    already typed in, and keys typed there are added to it (`90` typed into a field that
    opened with `90` was recorded as `9090`). Nothing known here clears it, so an answer
    that is the default, as the prompt shows it, types nothing and only the field's Enter goes,
    keeping it; any other answer to the field, or none, is `refused` whole before a key goes:
    `answer at the machine (this field has a pre-filled default)`. A text field's `null` or
    empty default fills nothing in, and is typed as before; a number field's `null` default
    opens reading `null`, so it is refused like any other. Numbers are compared exactly, and
    one that cannot be (an exponent too large to hold, or no finite value) is refused. Copilot
    is JavaScript and holds every number as the nearest double, so a default, an answer or a
    number option it would hold as another number (`9007199254740993` becomes
    `9007199254740992`, `1e-400` becomes `0`) is refused before a key goes. A number is typed
    as plain decimal (`1e5` as `100000`): the number box drops a key that leaves its text not
    a number, and `1e` is not one; one longer than 40 characters is refused. A number
    field's string default is recorded by the CLI as `Number(text)`, so `"3"` is kept as `3`;
    only a string that is plainly a JSON number is read so. Seen in a real session for a number field; a text field is
    assumed to open the same way. Claude's and Codex's prompts take no typed field values, so
    have no such field.
  * A field's schema constraints (#766): Copilot CLI 1.0.105 draws `ask_user`'s form with
    `enforceSchemaConstraints` off, so it checks none of them and records whatever is typed.
    The host checks the answer before any key, on what it would type (text with its line breaks
    flattened, a number as the plain decimal typed), and refuses one outside them: a number's
    `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf` and `integer`
    type, compared exactly; text's `minLength` and `maxLength` in UTF-16 units, `pattern` as
    JavaScript reads it and `format` `email`, `uri`, `date` and
    `date-time` with the CLI's own checks (`date-time`'s offset `Z`, `z`, `+HH:MM` or `+HHMM`; dates as real dates, where the CLI's Date.parse rolls `2026-02-30` over); a checkbox list's `minItems` and `maxItems`. A
    field whose constraints cannot be checked exactly (a bound JavaScript holds as another
    number, a pattern .NET would read differently, which includes lookbehinds, named groups,
    back-references, `\b` and `\B`, `^` or `$` followed by a quantifier (no JavaScript pattern at
    all), a quantified lookahead, a class escape beside a class's hyphen other than one first or last in the class and letter escapes such
    as `\a`), whose pre-filled default breaks them, or that carries any keyword beyond annotations and those
    checked for the input the CLI draws (an allow-list: `enum`, `oneOf`, `anyOf`, `allOf`, `not`, `const`,
    `if`, `dependent*` or an unknown keyword on a number, text or Yes/No field; on a list of choices,
    or a checkbox list's items, anything but its options, each of their own `type`; on the form, anything
    but its fields, `required` and `additionalProperties`), is answered at the machine. The CLI draws a
    number with an `enum` as a box taking any number, and a string with only `anyOf` as a text box. `\d`, `\w`
    and `\s` are written out as JavaScript's sets, ASCII digits and word characters and Unicode
    white space. All of one question's patterns share 250 ms, building and matching them alike, so an
    agent's many slow patterns cannot hold up the reading of every session: once it is spent,
    every pattern left counts as one that cannot be checked, and the question is answered at the
    machine (or the answer refused so). Text is checked both as typed and as trimmed
    (JavaScript's `trim`), and refused if either form breaks a limit: the form passes it on as
    typed, but a trim where it is recorded is not ruled out, and "  ab" is 4 typed and 2 trimmed.
    Text of only spaces, tabs or no-break spaces is checked so too, and an empty or blank answer
    to a required text field is refused, since it records no answer or might. Blank there counts
    control characters too: the host types them as spaces (`InjectableText.Collapse`), so
    `"\u0085"` would go as a space that `trim()` empties (`FieldConstraints.BlankOnceTyped`, which
    the PWA mirrors). A required text field whose default is blank so is therefore answered at
    the machine: that default is the one answer it can be given from another device, and it is
    blank. An empty default opens nothing and stays answerable. Whether Copilot trims a text box's words before recording them
    is not settled: its form passes them on as typed (`Fxn`, and `Uxn`'s accept step), but the
    native layer after it was not traced, which is why both forms are checked.
  * An option list whose `default` is present but is not one of its options, `null` included,
    opens on its Other box (`Dxn` in 1.0.105), where the walk from the top row ends a row off;
    it is refused, and shown to be answered at the machine. So are a checkbox list with no
    options (no `items` object, or one naming none) and one whose default does not name its
    options.
  * A Yes / No field whose `default` is not `true`, `false` or `null` (#768): ask_user's form
    is drawn without schema constraints, which gives its Yes / No list (`Oxn`) a third row,
    Other, and a text default opens on it. Walked from there, Yes recorded No and No reopened
    Other. It is refused, and shown to be answered at the machine.
  * Every field is read as Copilot CLI draws it, which is by its `type` alone (`cli-main.js`
    `tle` in 1.0.105): a `number` or `integer` is a number box even with an `enum` (whose Up
    and Down add or take one, so walking it as a list recorded a different number), and a
    `string` with only `anyOf` is a text box. Options come from `oneOf` before `enum`, and a
    checkbox list's from `items.anyOf` before `items.enum`, each by its `const`. A field the CLI
    would draw as something its schema does not mean (options with no `type`, a `type` it does
    not know, an option with no `const`) is never typed into, and the question is shown to be
    answered at the machine.
* **Other** (a lone option list): Up × (N−1), Down × N as one burst with no Enter, 400 ms
  for Other to hand over the input, then the words, and Enter once they show on screen (agent-ha-bridge #240:
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
and the client says to check the conversation. agent-ha-bridge has the same exposure.

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
shutdown) after it, reading exactly as shown (`stale` otherwise), and no `ask_user`
waiting (`refused`). A `permission.requested` written again for a request already waiting,
with what it asks now, replaces that request's question: the one it revises is no longer
listed or counted, and the request counts as asked again, after the others waiting, both in
the listing and in the transcript read just before typing. Copilot can have several permission requests waiting at once, one for
each of a batch of parallel tool calls, and draws one at a time; the session lists the
earliest, saying how many wait (#690). While others wait, the screen, not the order
asked, says which is drawn, and it is identified positively, never by a probe (agent-written
text, which another request's command can end in or its diff hold). The key goes only if:
* no waiting request holds a line-drawing character (U+2500-U+257F), a tab or a character
  drawn other than as it runs (`ShownText.Misleads`), in any value or any property name at any
  depth (an MCP or custom tool's dialog can draw an argument's name), so nothing the agent wrote
  can draw a part of Copilot's frame or hide what it draws;
* the options are inside one round frame whose top edge is on screen;
* every row in it above the options is this request's dialog as Copilot CLI 1.0.105 draws
  it (`CopilotDialog`): the title, a rule, each part of the body a blank row apart (a box's
  lines one to a row, exactly as given, so a wrapped line is never taken for two), and the
  question, with nothing else. Only the shell prompt (the intention, the command in a box,
  "Do you want to run this command?") and the path prompt ("Allow path access", the paths
  and any read-only directories in boxes, "Do you want to allow this?") are known so;
* every other waiting request has something drawn to check the screen against (a read naming
  no path has nothing), and none would be drawn the same (its title, body and question alike
  once all white space is gone: a box wraps a long line hard at its width, so one line on two
  rows reads like two lines, and trailing white space is not drawn);
* and this request is the earliest still waiting.

A listed prompt the host cannot tell from the others so is `answerAtMachine` while that holds:
another waiting request is drawn ahead of it (below), its kind's dialog is not one the host
knows, its own text has a run of spaces that can wrap onto a blank row, any waiting request's
text (its own included) could draw part of the frame or hide what is drawn, another waiting
request has nothing drawn to check, or one would be drawn the same
(`CopilotDialogs.Identifiable`, which mirrors the checks above). So an approval with `escaped`
listed while others wait is `answerAtMachine` (an escape is such text), unless its only escapes
are in a path request's access kind, which its dialog never draws as written: no confirmation is
offered, and Approve and Deny are both refused, until it waits alone. Being held so is not in the digest, so it is
answerable again under the same id once it can be told apart. Otherwise it is `refused` ("the machine is showing a different permission (path access,
request ed05e9d7)" when another's dialog is the one drawn, or why the screen cannot say),
and never typed for the request on screen instead: answer it at the machine. The key is
held from the frame's top edge. The transcript is checked so before the screen is read and
again after, so what the screen showed was this request while it was still waiting, and no
request that arrived meanwhile is drawn in its place. Each transcript check reconstructs the
pending requests and their order before formatting and hashing only those still waiting;
completed edits are not described again. Text parts of the dialog compare with all whitespace
removed on both sides, so a hard-wrapped URL or token still matches, but any changed non-whitespace
character does not. Boxed lines keep their exact, row-by-row comparison.
Copilot 1.0.105 keeps a queue for each
sort of prompt, in the order asked, and draws a waiting shell or other request (edit, read,
MCP, tool) first, then a hook's, then a path request, then a URL request, whichever was asked
first (`cli-main.js`: `GCn`'s queues, drawn in `cxo`'s order; `CopilotDialogs.DrawOrder`). So
the earliest waiting request is listed as answerable only when nothing waiting draws ahead of
it: a path request asked before a shell, edit, read or MCP one is `answerAtMachine` until that
is answered, and one asked before a URL request is not. A
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
`PermissionRequest` hook recorded in `<id>.approval.json`. The question's id is
`<DecisionId>#<digest>`: the marker's generation, and a digest of what the person is shown
(the tool, the whole command, whether it was cut, the hook's preview line, the message, and
whether it is truncated or answered at the machine), built the way a Copilot permission
prompt's is. The generation alone does not bind the command: Codex can list the same
`DecisionId` again with another command, plain, cut or escaped, and an Approve keyed by the
generation alone was typed into a command the person never saw (#958). An answer naming any
other id, a bare `DecisionId` included, is `stale`; a listed id that does not parse strictly
is refused. Immediately before
typing the host reads the marker again, rebuilds the question from that fresh read, never
from the cached listing, and types nothing unless it names this session, holds the same
generation, rebuilds to exactly the id answered, and records no attempt of
agent-ha-bridge's own (`DeliveryAttempted`). Missing, another generation or another
content is `stale`; unreadable, or anything else its own sender would reject, is
`refused`. The host only reads the
marker: it never writes, renames or removes it, so its attempt is recorded in its own
ledger (below), not in agent-ha-bridge's file. That attempt is keyed by the generation, not
the whole id, so a generation is typed into once whatever it reads as later.

An Approve on an approval carrying `escaped` also carries its confirmation (#803,
[v1-sessions.md](protocol/v1-sessions.md#escaped-approvals)), signed for the whole id. The host
checks it when the answer is taken, and again at the typing slot only once the fresh marker has
rebuilt to exactly that id, so it is checked against what the person confirmed and never
against the cached listing; what that second check can still catch is the confirmation's own
standing (its device taken off the list, its lifetime passed).

Codex records nothing that says how an approval was answered: the hook removes the marker
on the next event however it was answered, and the rollout does not name the approval. So
a typed approval is always `unconfirmed`, as agent-ha-bridge logs it ("native approval is
not confirmed"), and the client says to check the conversation.

Codex sessions are known to this host through agent-ha-bridge's hook registrations, or,
for a Codex run with `--no-daemon` (every launch from this host), from the terminal that
holds its thread's writer lock; but an approval is answerable only through the marker that
hook writes, and Codex 0.165 writes no approval request into its rollout at all (seen
live: the prompt on screen, nothing in the rollout), so without the hook an approval is not
seen. Claude sessions are found from registrations too, or else from Claude Code's own
`~/.claude/sessions/<pid>.json`; but Claude Code 2.1.312 writes an `AskUserQuestion`
`tool_use` into its transcript only once the question has been answered, so a waiting
question is seen only through agent-ha-bridge's `PreToolUse` hook (seen live: the question
on screen, the transcript silent until it was answered at the terminal). Answering them
therefore needs its Claude or Codex adapter installed (with its
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
8. Up to 15 s, the transcript after the point read in step 5 is watched for that call, looked
   at again as soon as the file watcher hears it written and every 250 ms whatever it hears,
   as for replies. For
   Copilot, `delivered` only when the session recorded the answer typed: the structured
   `human_response.recorded` content field by field (choices by value, words with
   whitespace runs collapsed, lists as sets), or, for free text only, the rendered
   `User responded: …`. A form, even of one field, needs the structured record: its
   rendering trims, renders `a=x, b=y` (equally `a="x, b=y"`), and cannot tell options
   `x`, `x ` and `pick=x` apart. A different record, an error, a skip, the session
   ending, or nothing is `unconfirmed` with the reason. Claude and Codex as above. An
   `unconfirmed` reason states only the fact (a refusal may say "answer at the machine"), and
   an exception's text goes to the host's log only, as for replies
   ([host-replies.md](host-replies.md), "What a reason says").
   Every `unconfirmed` answer says its `stage`: `typed` when nothing was recorded for the
   call (the keys reached the prompt, but it may not have been submitted), `recorded` when
   something else was (#467).
9. A repeat of a request settled `unconfirmed` (a client's resend after a reconnect) reads
   the same call's record again from the same point, and types nothing. An answer recorded
   since, the one typed (submitted at the machine, or recorded late), makes the request
   `delivered` from then on; another makes it `recorded`. Only that call is read, so a newer
   question is never answered, and nothing is ever typed twice. That holds however the
   answer came to be `unconfirmed`, an interrupted injection included. A Copilot record whose
   completion has not followed it gets a second from when a read first found it, as it does
   while first watched, for a completion saying the call failed; after that the record alone
   decides. That second is waited out within the read, so the one resend a client makes per
   reconnect settles it.
   Copilot and Claude; Codex records nothing to read.

Progress and timings work as for replies ([host-replies.md](host-replies.md#progress-and-timings)):
`received` before step 1, `typed` (never `working`: a question holds the session) once step
7's keys have all gone, and one `answer <id> timings (ms after …)` log line per answer.
`recorded` is the agent's timestamp on Copilot's `human_response.recorded` (or the call's
completion) for an `ask_user`; other prompts' records have none the host reads yet, so it is
absent for them.

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
