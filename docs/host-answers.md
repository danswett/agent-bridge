# How the host answers questions

`session.answer` ([contract](protocol/v1-sessions.md#sessionanswer)) types a person's answer
into the agent's own prompt, as if it had been typed at the machine. The prompt stays
answerable at the terminal the whole time; whichever answer lands first wins. This page is
how the host does it and what it refuses.

## What can be answered

| Agent | Question | Answered |
|---|---|---|
| Copilot CLI | `ask_user` with no form or list: free text, or a `requestedSchema` with no fields the host can read (empty, not an object, or with a property that is not an object) | Refused, `answerAtMachine`: there is no form on screen to hold its keys to (below), or one the host cannot read |
| Copilot CLI | `ask_user` form (`requestedSchema`): `choice` (`type: string` with `enum` or `oneOf`), `boolean`, `multi` (`type: array`, items `anyOf` or `enum`), `text`, `number`, any number of fields | Yes |
| Copilot CLI | Other words for a question that is one option list | Refused for now, before any key, and shown disabled with why (`otherUnavailable`: `otherNotSendable`; the refusal carries that `code` and sentence). The pinned box cannot yet be proved empty (below). A temporary exception to #876 |
| Copilot CLI | Other in a form of several fields, or in a checkbox list | Refused: no walk reaches it |
| Copilot CLI | The older `question` / `choices` format (any present, non-null `choices`, an empty list or blank string beside a `requestedSchema` included) | Refused, `answerAtMachine`: agent-ha-bridge never drove it, and no current build writes it |
| Copilot CLI | Permission prompts: a shell command, path access, a file edit or read, a URL, an MCP tool, a custom tool | Yes, beyond agent-ha-bridge (below): approve once or deny. Refused, `answerAtMachine`, when `permissions.remoteApproval` is off. A character not drawn where it runs and a right-to-left letter are shown escaped (#220); such an approval carries `escaped`, and Approve on it takes the two-step confirmation from a person's device this machine approved, through the relay only, or is held at the machine by `permissions.escapedApproval` (#803, [v1-sessions.md](protocol/v1-sessions.md#escaped-approvals)). Deny goes as for any approval. While other permission requests wait, such a character in the text Copilot was given for any of them, the listed one's own included, makes the listed one `answerAtMachine`, Deny included, unless it is only in a path request's access kind (#690, below) |
| Copilot CLI | Other permission prompts (hooks, memory, workflows, extensions), a request to run outside the sandbox, one whose details cannot be read | Refused, `answerAtMachine` |
| Claude Code | `AskUserQuestion`, one to four questions, each one option (`choice`) or several (`multi`, at least one, the first nine rows) | Yes |
| Claude Code | Free text in `AskUserQuestion` ("Type something") | Refused: no field offers `allowOther` (below) |
| Claude Code | More than four questions, a question without options, two questions with the same text | Refused, `answerAtMachine` |
| Claude Code | Permission prompts | Refused, `answerAtMachine`: agent-ha-bridge's hook reports them and offers only its reply box, best effort |
| Codex | An approval its `PermissionRequest` hook recorded (`<id>.approval.json`) | Yes: approve or deny, always `unconfirmed` (below). Its message is the whole command, fenced; a marker holding only the hook's 300-character preview (agent-ha-bridge's hook, an older host) or a command past 16,384 characters is `truncated`, and refused like any truncated question (#209). One whose `tool_input` holds anything but the command and its description (an MCP tool's `host`, a `workdir`), or a network approval (`description` `network-access <target>`), is answered at the machine (#338). One the host escaped anything in carries `escaped`, and Approve on it takes the same two-step confirmation as a Copilot permission prompt's, which names the approval's `<DecisionId>#<digest>` id (#803, #958) |
| Codex | An approval seen only in the rollout, or one agent-ha-bridge has already typed into | Refused, `answerAtMachine` |

The no-fields and legacy-choices listings also carry `answerAtMachineReason: "shape"`.
Only that reason adds the card's second line, "This kind of question can only be answered at
the machine.", after "Answer at the machine · *machine*". It changes no delivery rule or
`otherUnavailable` refusal; absent and unknown reasons keep the existing single line.

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

* **Free text** (an `ask_user` with no form or list): not typed. It is listed `answerAtMachine`
  and refused before any key: the host types only into a form it can see on screen (below),
  and 1.0.105 draws no form for it whose rows the keys could be held to.
* **A form,** field by field in schema order, each committed with its own Enter, a write of
  its own once the screen has settled from the field's keys (below), 240 ms before the next
  field:
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
    the same words given to the field before could hide them. They are counted only in that
    field's input, the rows strictly between its heading and the hints, read as one: 1.0.105
    draws a text field's input between two rules with no edge down either side, its first row
    "❯ " and the rows a long answer wraps onto indented, so the words' end can run across the
    wrap. Across the whole screen, a
    field titled "Data" over hints starting "enter" read "ae" across the two, so the words
    "ae" typed between them never showed more often and their Enter was never pressed (#510
    review). Read in less of the screen, words only ever show less, never where they are not.
    In an option list's rows, an empty Other box's placeholder ("Type your answer...", whole
    or cut short with an ellipsis) is read as the empty box it is, so words it already reads
    ("answer") show once they replace it; words that read exactly as the placeholder never
    show, and get no Enter. A text or number box draws no placeholder, so words reading so
    there are the answer.
  * Every answer, a single list or Yes/No included, goes field by field, each field its own
    writes, never batched with another's:
    its keys, held to the field they were planned for, then its Enter alone once the screen
    has settled from them (so the change awaited is the form moving on, not an option list's
    highlight repainting). The next field's keys wait for the form to move off the screen the
    Enter was pressed on. Each write is held to those rows (`KeySend.Expect`): the injector
    types nothing of it unless they still read exactly so, and every row below them is blank
    (`ScreenCheck.BlankFrom`: a console that grew after the read, with a prompt in the rows it
    gained, is refused), in the same attach, just before its first key, since a permission
    prompt can open over the form at any moment and Enter picks whatever it highlights
    (sec-510, Copilot 4478363609). The last checks before typing (step 5: agent-ha-bridge, the
    call still unanswered, no other prompt waiting, the process) are made again before each
    write, and once more when the injector holds the typing slot (`KeyPlan.Proceed`). Asks are
    told apart by their call ids: any ask other than this question waiting (a delegate's, whose
    form can read the same) refuses it before the first key and stops it after, as does the
    question being marked for the machine (`answerAtMachine`) once the slot is taken. A
    delegate that aborted, shut down or failed asks nothing any more, nor does a task
    (synchronous) delegate whose call has returned, as the session's own parser has it; a
    background delegate's run completing is not its end, since it can wake and ask again. A
    main-agent turn that ends on an error (a `session.error` other than a model call's or a
    hook's) ends the main agent's own asks and permission requests, never a delegate's, as the
    parser's `FailTurn` does. The call and the prompts waiting are kept up to date from what the
    transcript has gained since the call was validated (`CopilotCallWatch`,
    `CopilotPermissionWatch`), not searched again with that slot held; a transcript that can
    no longer be read, or is not the file read (shorter than what was read, another file in its
    place by its volume and file index on Windows or device and inode on Linux, or other bytes
    in its first 4 KiB or the 4 KiB before where the read stopped: `JsonlMark`), stops it, and
    nothing read of it is carried over. A check that fails stops
    it as any other reason does: once keys have gone, the answer is typed and unconfirmed, and
    read back later. A refused write stops everything after it.
  * The rows a field's keys or Enter are held to must show the form at that field
    (`CopilotFormScreen`, from 1.0.105's `MIo`, `Uxn`, `Da` and `Ft`), each part below the
    last: exactly one form heading, a "Copilot needs information." row right under the
    frame's top rule and one column in from it (an MCP server's elicitation is headed with its
    own name, and may have a field of the same name; the question, a field's title or an
    option reading the same are the form's own, and never counted, Copilot 4478963305), its
    frame closed by the same rule with nothing drawn under it (the CLI draws a prompt in place
    of its input box and footer, so another form or an input under it is refused, Copilot
    4479698033, and everything below is read inside the frame), the whole question under it
    (its lines, each wrapped onto as many rows as it takes: each row, read as drawn from the
    question's column, must be the question's next characters exactly, its spaces as they
    are (the question itself read with its runs of white space as one, or as written), a
    space being missing, or drawn at the next row's start, only where a row breaks. 1.0.105's
    renderer, `gVr`, breaks a line wherever Unicode line
    breaking allows, so with no space at a break the break must be one it allows: after a
    hyphen, a dash or a slash, beside a wide character, or inside a word longer than the
    frame is wide, and the next row then starts at the question's column, so "ag-" over
    " test-ops" is refused. A question broken after "ag-" was refused on a steady screen every time it
    was answered, #1123, #848; read with every space left out, "Now here" read as "Nowhere",
    #1137 review. What a break hides stays unseen: two questions that differ only by a space
    at a break, such as "ag-test-ops" and "ag- test-ops" broken after the hyphen, or "Now here"
    and "Nowhere" broken inside a word too long for the row, draw the same rows; the tab bar,
    the field's heading and the hints must still match exactly), with more than one field the tab
    bar naming exactly this form's fields in order, on one row read exactly as `Da` draws it
    (titles as written, brackets included, each ` <title> ` with a column between; the field
    being answered may be in the bar's own brackets; the bar fits the console's width less
    four columns, within the frame, so it is not taken across rows), the field's own heading on a row
    of its own (`<title ?? name>:` for a list, a Yes/No or a multi-select; the title alone,
    with no colon, for a text or number box, `Fxn` and `Lxn`; only the field being answered
    is drawn), and under it the hints
    row (the last row ending "esc cancel" or "esc clear", with the rows it wrapped from, every part one of
    the form's hints, its spaces read as drawn but a space allowed missing where a row breaks,
    so a narrow window that breaks a hint, or a word of one, across rows still reads, #910,
    while "enteraccept" in a row is not "enter accept"): "enter accept", "ctrl+d decline" and
    Esc's, with the field's
    place by "tab next" and "shift+tab prev" when there is more than one, read from that row
    alone, since the question or the words typed may say them too. Esc's hint is "esc
    cancel", or from Copilot CLI 1.0.106 "esc clear" while a text or number box holds
    something (Esc then empties the box and cancels nothing); a list saying "esc clear" is
    not the form. Read as "esc cancel" alone, every typed answer's Enter was withheld on
    1.0.106 (#1123). 1.0.106 also draws a strip of its own above the form ("Current
    Sessions Issues Pull requests Gists"), outside the frame, not read. And no permission prompt
    drawn against the form: one ending just above its frame or drawn under it, read by its
    structure (options numbered from 1 without a gap, exactly one under the pointer or
    "Current selection:", then its key hints and its box's edge when it has them, bare or
    between a box's sides), or a numbered "1. Yes" row inside the frame below its hints. An
    agent's numbered list in the session's output above the form is not one: it has no
    pointer, and read as one it refused every answer under it. An option, or words typed,
    reading "1. Yes" are the form's own. An `ask_user` with no form
    schema is refused, `answer it at the machine`: how 1.0.105 draws one cannot be told from
    its code (its elicitations require a schema), so there is no form to hold the keys to.
    So is a form with two fields of the same heading the tab hints cannot tell apart (both
    between the first and the last): the tab bar marks the field being answered by colour
    alone, which the screen read does not carry, so a repaint while the CLI still showed the
    first could pass for the second. Headings are compared exactly as drawn, their ends
    trimmed, a list's colon taken with its title as the CLI draws them together, so with no
    tab bar "Pick  one:" or "Pick one:" with a no-break space is not "Pick one:" (#1137
    review). A title whose spaces the host cannot be sure how the CLI draws (white space at
    its ends, a run of spaces, or a space of another kind, such as a no-break space) is
    refused before the first key, as is a title of
    white space alone, drawn as a blank row or a lone colon, or with a line break or another
    control character, which splits its heading across rows while the tab bar's first row
    still reads as the labels. All, like a `requestedSchema`
    encoded as JSON twice (read once, it is text, not a form), are listed `answerAtMachine`,
    so no client offers what would be refused; a field heading the
    console wraps mid-word is refused rather than typed into (#848). With more than one
    field, the tab bar read at the first field, before any key, vouches for every later
    field's width and characters: one that fits holds every label, so no later heading can
    wrap, and a character the console draws other than as written is drawn so in the bar
    too, and refused there. A bar too wide for the console shows only the labels around the
    field being answered, between arrows ("←" when labels are hidden before them, "→" when
    after, `Da`'s `Hfn` window). Read as exactly such a window at that field, it is refused
    with nothing typed and named (`bar-window`), telling the person the window is too narrow
    for phone answers (widen it, or answer at the machine): a form
    that differs from the question only in a hidden title reads the same, and each field's
    own heading is read only once the keys reach it, after earlier fields are already filled
    (#1137 review). A six-field form with long titles at 102 columns showed two labels and
    "→", and was refused as no form at all, unnamed (#1123). Sessions the host launches open
    250 columns wide so such a bar shows whole (host-launch.md, Width); the screen read takes
    each row at the console buffer's full width, the width the CLI draws to. Copilot CLI 1.0.106's bar
    (`_l`, window `kHi`) shows the most labels that fit around the field (any such run, each
    read), and where not even one fits, the field's label alone, however long, wrapped over
    as many rows as it takes with the arrows beside its first row: read across those rows
    (words in order, a space or none at each break), it is the bar's window too, refused as
    `bar-window`, never taken as the bar. Three titles of full sentences at 120 columns were
    refused so live as the form gone (`bar@29`). A bar that reads as neither the whole bar
    nor a window at this field is refused (`bar`) as "this window is too narrow, or the
    question's titles too long, to check which field is open", not as the form gone: with
    the question's heading and message above it, it seldom is.
    What cannot be known beforehand (a permission prompt opening over a later field, or a
    long field scrolling the form's heading off a short window) still stops the keys there:
    the answer is then `unconfirmed`, and its reason names the fields that were answered,
    the one typed into but not submitted, if any, and that the rest were not typed, for the
    person to finish the form at the machine. A screen that has settled
    or moved on is not enough: a permission prompt that opens before its transcript entry is
    written reads just as steady, and keys held to its own rows would go into it, the Enter
    approving what it highlights (#510 review). Anything else stops the answer there, as
    below. A field whose title is not a non-empty string has no heading to look for, so is
    never typed into this way.
  * One read is never the end (#1123): a read that does not show the form at the field the
    next keys go to is read again, every poll interval for up to 2 s (`FormRecheck`), and
    the keys go held to the first read that does; read in the middle of a redraw (streaming
    output, the conversation scrolling), the form can be half drawn. A write the worker
    refused because the screen changed, nothing of it typed, is tried again within the same
    2 s held to a fresh read, but only when that read shows the form at the same field with
    the field itself (its options and pointer, or what is typed into it) drawn exactly as in
    the rows the write was held to: what changed was elsewhere on the screen, and the keys do
    the same there. A field drawn otherwise gets nothing more. Each refusal logs which check
    failed (`heading`, `bottom-rule`, `under`, `message`, `bar`, `bar-window`, `field`,
    `field-heading`, `hints` or `prompted`, with the row) and the screen's outline (its row count, widest
    row, blank rows, where its rules and form headings are), never what any row says.
  * A form left part answered is not left so if the host can help it (#1123; design #1135,
    section 4). The delivery waits, holding no keys and not the typing slot, while the screen
    is read every second (`ResumePoll`) for up to 60 s (`ResumeWait`); the prompt's own
    delivery slot stays held, so nothing else is typed into the session meanwhile. Only a read
    that shows this very form at the field the keys stopped at, the question still asked and
    no other prompt open, lets anything more go, and then only from that field on, every
    check made as for a first pass: no field is typed twice. A field whose keys went but not
    its Enter gets only the Enter, and only once the screen shows exactly what those keys left
    there: an option list drawn as its walk left it, the same row highlighted, or the words
    typed showing in its input whole and exactly as typed, every character compared
    (`CopilotFormScreen.Holds`; only a row-wrap break read as the question's are, never just
    their last characters, so words edited at the machine to another answer that ends the
    same get no Enter). The words are read from the column right after the input's "❯ ", so a
    leading space added or removed is a different answer. A screen read does not show a row's
    trailing spaces (the injector trims the window's padding), so words typed with one are
    never entered on resume (the form is reset), and a row showing a space after the words is
    not them; an edit at the machine that only adds spaces after the words is the one change
    a screen read cannot show. Words the input cannot be read whole in (scrolled or clipped) are not
    entered: the form is reset instead. A field whose keys may have gone in part, or whose
    walk went and never settled, or a pass whose keys were cut short, has nothing to check
    that by, and is not resumed, but is still given the whole wait before any Esc. A permission prompt
    over the form is waited out, read from the transcript (a pending permission over this
    still-open call, no other question asked), even while the session's listing shows that
    approval as its question or the form as answered at the machine; the question no longer
    asked ends it. What a resume checks is
    kept in memory for that delivery only: after a host restart, a resent answer finds the
    prompt in the attempt ledger and types nothing (its words kept while it is unconfirmed,
    step 6 below, are only read again, never resumed or typed).
  * Before the first key goes into a text or number field, on a first pass and a resume alike
    (the first field included), the screen must show that field as it opens
    (`CopilotFormScreen.Untouched`): its input's "❯ " alone, or, for a field that opens with
    its default typed in, exactly that default. Keys typed into a box add to what is there,
    so a "9" typed at the machine and an answer's "0" would be submitted as "90"; a field
    showing anything else gets no keys (`refused` on a first pass with nothing typed; on a
    resume nothing is typed, and the form is reset as below). An option list or a Yes/No
    needs no such check: its keys walk from the top whatever row the pointer was left on,
    and nothing typed stays in it. A checkbox list's ticks are not read (see "What no plan
    can see"). Spaces typed at the machine into an empty box are the one thing this check
    cannot see: a screen read trims each row, so a box holding only spaces reads as its
    "❯ " alone. No key is known to clear a box (none has been proven against the CLI, so none
    is guessed at, Ctrl+U included), so instead, once the answer's words show in the box, a
    first pass presses their Enter only if they start the input at the column right after
    the "❯ " (`CopilotFormScreen.Begins`): spaces before them show then, and the words are
    left unsubmitted (`unconfirmed`; a resume's whole-contents check refuses them too, so the
    form is reset). This holds for a number box as well, although the CLI records one as
    `Number(text)`, which would ignore the spaces and record the same number. Only spaces
    after the words stay unseen (above).
  * Not resumed within `ResumeWait` (or after three resumes that each stopped again), the form
    is reset: one Esc (the form's own `esc cancel`), held to rows that show this very form at
    one of its fields with that hint; a screen showing anything else gets nothing, not even
    the Esc, nor does a field whose hints say `esc clear` (1.0.106: the Esc would only empty
    the box), and the
    answer stays part answered as above. Cancelled, the session may ask the same question
    again: only once the transcript positively shows the old call closed, if it does within
    60 s (`ResetWait`), its arguments exactly the ones answered (the whole `ask_user` call
    compared in order, property by property, so any change to the message, a title, an
    option, a constraint or the order of the fields fails it), the same answer is typed into
    it afresh, once, for the same card and request, every check made again from its first
    field; that one is never reset again. A different question retires the card, and so does
    none within 60 s once the old call is positively closed: `stale`, "this question is out
    of date: ...", with nothing typed into whatever the session shows next. A form not seen
    to close (still asked after 60 s, a permission prompt drawn over it, or a transcript that
    cannot say) leaves it `unconfirmed` (`resume=uncancelled`), for the person to finish at
    the machine. `ResumeWait` zero turns all of this off.
  * Every answer to a Copilot form logs one line (`CopilotAnswerTrace`), whatever it ended
    in, with no word of the question, the screen or the answer: `answer-attempt v=1
    request=<8> session=<8> cli=<version> fields=<n> shape=<kind[options]+...>
    reads=<n> rereads=<n> steady=yes|no retries=<n> check=<name@row|none> taken=<k>/<n>
    stages=slot:<ms>,typed:<ms>,confirmed:<ms> total=<ms> outcome=<outcome>
    resume=<-|resumed|held|gone|redone|redone-<outcome>|replaced|stale|uncancelled> resumes=<n>`, the last two
    saying what became of a form left part answered and how many resumes typed into it (a
    form answered afresh that stopped again keeps what became of that, `redone-held` say,
    never plain `redone`). Its
    keys keep their order and names; new ones are only added at the end, so what reads it
    (#1132's feedback reports) keeps reading it.
  * It fails closed: such an answer is not typed at all into a screen the host cannot
    read, nor words made only of spaces and line-drawing characters, which leave nothing to
    look for (`refused`, nothing typed); and a field or words that do not show, or a screen
    that does not settle, within 3 s, a screen that changed under a write and did not show the
    field as it was within 2 s, or a question no
    longer asked, leave it there, with nothing more pressed (`unconfirmed`, stage `typed`,
    for the person to finish at the machine). An Enter sent into a screen nobody has read is
    how #467 happened. What remains: keys reach the CLI in the order they were written, so a
    count taken too early can only send the words' Enter early, behind the words, which is
    reported `typed`, not `delivered`; rows are compared whole, so an animated form would be
    refused rather than typed into; a form drawn taller than the console's window, its
    heading or hints scrolled out of sight, is stopped rather than typed into, and so is a
    form whose tab bar is too wide to name every field (`bar-window`).
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
    opens reading `null`, so it is refused like any other. A field whose default no answer can
    keep (a text field's that is not a string, such as `5` or `true`, is blank once typed or
    shows as nothing (`Invisible.Blank`: `"\u200B"`, `"\u2800"`, `"\u0301"`), which the PWA
    leaves out of an answer, as the app does white space, holds half a surrogate pair, which they
    send as U+FFFD, or is longer than one answer's text, 4 KiB; a number field's that is
    `null`, not plainly a number, or not held exactly) makes its question answered at the
    machine, rather than offered with every answer refused (#882, #921). So does a form whose
    smallest answer is over 16 KiB in the largest envelope a host takes (a 256-character
    session id): each pre-filled text or number default (a number as the number it holds,
    written the longer of the ways JavaScript and .NET write it, as the PWA and the app send
    it), every other default and every required field at its smallest
    (the shortest option, the fewest ticks, `true`, `0` or the number nearest 0 that its bounds,
    `multipleOf` and whole-number type allow, past an exclusive bound, or text
    of its minimum length; past 4 KiB, which no answer's text can be, the form is answered at
    the machine). Accepted as it is: required text that must match a `pattern` or
    `format` is counted at its minimum length, since a pattern is not solved for its shortest
    match. Such a form can be offered and its answer refused as too large; it fails closed,
    refused whole before any key, saying so. A form holding half a surrogate pair anywhere in
    its schema, which the host cannot read, is answered at the machine; such a pair in the
    question's message, in any tool call's arguments, or in any other text a transcript line
    carries, is shown as U+FFFD.
    Numbers are compared exactly, and
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
* **Other** (a lone option list): refused before any key for now. The walk is planned as
  agent-ha-bridge drove it (Up × (N−1), Down × N as one burst with no Enter, 400 ms for Other
  to hand over the input, then the words and Enter; agent-ha-bridge #240: Down on the last
  option opens Other at once, and an empty Enter returns to the last option), but in a live
  1.0.105 session that Down turns the Other row into a label ("Other (type your answer
  below)", `Dxn` with `onExternalFreeform`) and the words go into a box of the session's own
  (`bOe` in `pinnedFooterContent`), drawn above or below the form by the footer's mode. No
  key here is held to that box, so the host does not type into it (#510 review). The field is
  listed with `otherUnavailable: "otherNotSendable"`, so the PWA and the Windows app show Other
  disabled with "Your own answer can't be sent from here yet. Pick an option, or answer at the
  machine."; an Other answer sent anyway is refused with that sentence and `code`. This remains
  a temporary exception to #876. #908 prepares a read-only recognizer for the captured
  below-form rail box, but it does not authorize keys: the captured empty box's placeholder
  could also be a draft with the same text, and a blank row could hide trailing spaces. Other
  footer layouts and wrapped boxes are unrecognized. A later capture must establish how to
  prove the box empty before the interim can be removed.

Typed words have every control character (C0, C1, CR, LF, tab, escape) and line or
paragraph separator collapsed to a space first, and a lone surrogate replaced. The worker
writes each UTF-16 code unit as its own key down and key up with the character in
`UnicodeChar`, so an emoji is its surrogate pair as two key events and nothing passes
through a code page.

**What no plan can see.** The keys assume the prompt as the transcript describes it. Input
somebody left in it at the terminal without submitting (boxes ticked in a checkbox list,
words half-typed into a text field) is in no transcript, so the keys add to it: ticking
`b` after `a` was ticked locally submits both. The caret position is normalized (above);
ticks cannot be, short of reading the screen. Words in a text or number field can: such a
field is read before its first key, and one not as it opens gets none (above, "as it
opens"). Such an answer is never
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
  rows of its console, each at the buffer's full width, read-only) and takes the last block of numbered rows, numbered
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
shutdown, nor, for the main agent's own request, a turn that ended on an error, as the parser's `FailTurn` has it) after it, reading exactly as shown (`stale` otherwise), and no `ask_user`
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
   whatever it was (`stale`, `refused` and `busy` included, but not a refusal because the host
   is stopping, below), is remembered for 15 minutes
  and returned for a resend, which types nothing. So is the outcome of a prompt's first
   answer, for any request that says the same thing (the same fields and text) for the
   same session and question, from the same sender (the channel's peer; a second device
   gets the plain refusal, and so does the same text from any other sender, so a guess
   cannot be told from a miss): a phone that reloaded sends it under a
   new UUID, and gets the first outcome instead of the refusal in step 6 (#1093). An
   `unconfirmed` one is read again from the session each time (step 9), so it turns
   `delivered` once recorded; while it is not, the retry (under the same UUID or a new one, at
   any stage) is told only that nothing was typed again, because it repeats an answer already
   typed once (`DeliveryReasons.AnswerNotConfirmed`, shown by the clients inside their own
   unconfirmed line); the stage still says how far the first got. A call that arrives while
   the first is still being admitted joins that one delivery: there is one delivery per prompt.
   A request id belongs to the sender, session and question it was first admitted for
   (decided with the admission itself): another sender, or the same id under another question,
   replaying it gets the answer a request of its own would, never the first's outcome or
   progress. That
   record is kept while the prompt's claim is, in the host's memory and, since #1159 slice 0, in
   the attempt ledger (step 6), so it survives a restart: a host that starts finds a prompt's
   first answer there, with its sender, hash and outcome, and while it is `unconfirmed` reads it
   again as above, so a retry is told the same before a restart as after it. It never covers an
   answer that typed nothing, nor a different answer. A request UUID's own outcome is also kept
   in the ledger for its 15 minutes, with the sender, session and question it was admitted for;
   after a restart, a UUID the ledger knows (by its receipt, or by a claim with no outcome yet)
   asked for by anyone else, or under another session or question, is a request of its own as
   above, so it gets exactly what an unknown UUID with the same answer would and cannot be
   probed. An entry from before slice 0 has no owner and is nobody's.
4. The session's process is still the live agent process; it does not run elevated
   (integrity level High or above, or unreadable, is refused).
5. One answer is typed at a time across sessions, and its turn is taken before these last
   checks: agent-ha-bridge again, the question still not `answerAtMachine`, then the agent's
   own record: for Copilot, the tool call's start is there and no `human_response.recorded`,
   `tool.execution_complete` or session abort/shutdown follows it, it is still among the asks
   waiting (a turn that ended on an error closes it with nothing on its call), and no other
   prompt waits (another ask, by its call id, or a permission request: `refused`), all made
   again before each later write (above) (for a permission prompt, the request as above, then
   the screen); for Claude, the `AskUserQuestion` `tool_use` is there, in the
   session's own entries, with no `tool_result` or interrupt after it; for Codex, the
   approval marker as above (`stale` otherwise). Identity, never time.
6. The prompt is claimed in the host's attempt ledger, `answer-attempts.json` in its
   profile folder, written through to disk. A prompt already claimed (by any request,
   before or after a restart; the same answer sent again gets the first outcome before it
   gets here, step 3) or a ledger that cannot be read or written is `refused`:
   agent-ha-bridge's rule that a prompt gets one attempt (`Start-CopilotDecisionAttempt`,
   `DeliveryAttempted`), since nobody can say what a half-typed prompt holds. A claimed
   question is listed as `answerAtMachine`. If the host stopped after claiming but before
   recording the outcome, a resend of that request within 15 minutes is `unconfirmed`:
   keys may have gone, and the host must not say it refused or type them again.
   A failed write is retryable: it refuses that request, the next one re-reads the file and keeps every
   claim held in memory, and only a corrupt file latches refusal until restart. The ledger's
   refusal reasons can name a question's label or field names (never answer text), and its folder's
   ACL lets the Codex sandbox group read it; the ledger holds no answer's words. Each claim and
   receipt keeps the sender, session and question it was admitted for (step 3), and a claim keeps
   a SHA-256 hash of the answer (its fields and text) and the answer's first result (outcome and
   stage), updated as a read settles it (step 9).

   While a Copilot form's or Claude answer is `unconfirmed`, the claim also keeps what reading
   it again needs (the agent, transcript path, call or tool-use id and read offset) and a key to
   the answer's words, which are kept apart, on Trust's conditions (#1161): in
   `private\answer-words.json` under the profile folder, a folder whose ACL is protected and
   grants this user and SYSTEM alone (0700, and 0600 for the file, elsewhere), checked before
   every write; the text as sent and nothing else, written to a temporary file in that folder and
   renamed over the old one. The words go at the first of: any result but `unconfirmed`; the
   claim released; its session or question gone (after one last read of its record, so an
   answer recorded at the machine still settles); and 24 hours after they were typed. A host
   that starts deletes leftover temporary files, words past those exits and words no claim
   refers to. A delete that fails fails closed: the claim no longer has words, so a retry is told
   only that nothing was typed again and is never read from them; the delete is tried again at
   every write, before every bound check and at every start, and until it succeeds the words
   still count against the bounds. At most 32 answers' words are kept, 16 KiB each and 256 KiB in
   all; an answer past a bound, or while the folder can be read by anyone else, is `refused`
   before any key with `DeliveryReasons.CannotHoldAnswer` ("the machine can't keep this answer
   safely until it's confirmed, so nothing was typed; answer at the machine"), and nothing kept is
   evicted or cut. Copilot permission prompts and Codex approvals are not read again, so they
   keep no words. The words go nowhere else: not to a log, an error message, a reply or the
   relay (the canary test in `AnswerFlowTests`).
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
9. A repeat of a request settled `unconfirmed` (a client's resend after a reconnect), in the
   same host run or, while its words are kept (step 6), after a restart, reads
   the same call's record again from the same point, and types nothing. An answer recorded
   since, the one typed (submitted at the machine, or recorded late), makes the request
   `delivered` from then on; another makes it `recorded`. Only that call is read, so a newer
   question is never answered, and nothing is ever typed twice. That holds however the
   answer came to be `unconfirmed`, an interrupted injection included. A Copilot record whose
   completion has not followed it gets a second from when a read first found it, as it does
   while first watched, for a completion saying the call failed; after that the record alone
   decides. A form's completion found before its structured answer waits for that answer, up
   to the 15 s a first watch allows, from when a read first found the record. Those waits are
   made within the read, so the one resend a client makes per reconnect settles it.
   Copilot and Claude; Codex records nothing to read.

A planned stop blocks new deliveries and waits up to 15 seconds for admitted typing and its
confirmation to finish. If that wait expires the host stays running; it does not abandon the
worker, and a restart that was waiting goes on. A resend of an already admitted answer waits for its
outcome even during the stop. A new request refused because the host is stopping typed nothing,
so it is not remembered (in memory or in the attempt ledger): if the stop is then refused, the
same request sent again is delivered. A worker that failed unexpectedly is ended, and stops are
refused until its exit is proven, so its keys can never land in a later question across a
restart; a failure whose worker cannot be proven gone refuses stops for the rest of that host run.

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
visible rows, each at the console buffer's full width (the width the agent draws to, wider
than the window where a screen clamps it). `agent-bridge-injector.exe <pid> --widen <n> <created>`
types nothing either: only while the process is still the one created then (held open
throughout), it makes the console's buffer at least n columns wide (a launch's console,
host-launch.md, Width) and prints the width. It is a separate process because Windows ends every process attached to
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
