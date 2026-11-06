# How the host sends replies

`session.reply` ([contract](protocol/v1-sessions.md#sessionreply)) types a prompt into the
agent's own input, as if it had been typed at the machine, the way agent-ha-bridge's reply
box does. It uses everything answers use ([host-answers.md](host-answers.md)): the injector
worker, the agent-ha-bridge guard (G5), the elevation guard, request memory, and one
`DeliveryGate` shared with `AnswerService`, so an answer and a reply can never type into
one session at once.

## Which agents

| Agent | Replies | Why |
|---|---|---|
| Copilot CLI | Yes, typed only into its input box as seen on screen, and only while it is empty and outside shell mode | agent-ha-bridge has long typed replies into it, and its transcript records every prompt (`user.message`) with the text as typed, which confirms one by identity |
| Claude Code | Yes, typed only into its input box as seen on screen | Its transcript records a prompt typed while idle as a `user` entry whose content is the line as typed, and one typed while it works as a `queue-operation` `enqueue` with that content (2.1.312, seen live), which confirms one by identity. agent-ha-bridge confirmed only that *some* prompt was submitted |
| Codex | Yes, typed only into its input box as seen on screen, and only while it is empty | Its rollout records every prompt, idle or mid-turn, as an `item_completed` `UserMessage` with the line as typed (0.165, seen live), which confirms one by identity. agent-ha-bridge typed with no confirmation at all |
| Any, on macOS or Linux | Refused, and `sessions.reply` is not offered | Delivery through tmux comes later |

## The keys (Copilot)

Ported from agent-ha-bridge (`hooks/decision-inject.ps1`, `Send-CopilotSessionPrompt`):
the words as one burst, 300 ms, then Enter as a keystroke of its own. The CLI takes a burst
as a paste, and only a separate Enter submits it.

**One line, always.** agent-ha-bridge replaces every control character with a space
(`Get-CopilotInjectableText`: "it can never submit an extra line into the CLI"), and so does
this host (`InjectableText.Collapse`, the same one answers use; it also turns the Unicode
line and paragraph separators into spaces and a lone surrogate into U+FFFD). The bridge's
own injector notes that a newline inside a paste burst is inserted literally rather than
submitting, which might allow multi-line replies one day, but the bridge never relied on it,
so neither does this.

**A working session.** agent-ha-bridge types a reply whatever the session is doing; only an
open question or approval stops it (`Test-DaemonReplyBoxFree`). Copilot CLI accepts a prompt
typed while it works and records it when it takes it: as a `user.message` with
`delivery: "steering"` just after the next `assistant.turn_start`, that is once the tools
running now have finished (239 of the 267 steering prompts in this machine's transcripts sit
right after a turn start), or `queued` after the turn ends. One typed while idle is recorded
at once (`delivery: "idle"`). The host therefore waits up to 60 s for the prompt, and frees
the session for answers as soon as the keys have gone.

**The input box (#825).** A reply is typed only into Copilot's own input box, read off the
screen (`Copilot/CopilotInputBox.cs`), and only while that box is empty and outside shell
mode. Words a person left in the box would be sent with the reply, and in shell mode Enter
runs what the box holds as a shell command. agent-ha-bridge typed without looking; so did
this host until #825. From CLI 1.0.105's own rendering code, the box is drawn one of two
ways: by default as a "rail" (`╻`, `┃` per line, `╹`) beside a `▄`/`▀` box with no prompt
glyph, and with the prompt frame off as a row between two `─` rules led by `❯ ` (or `! ` in
shell mode). Shell mode shows as that glyph, as the empty box's placeholder "Execute shell
command", or as the footer's "Shell" label or "esc exit shell mode" hint. The reply is
`refused`, with nothing typed:

* when the box holds anything, Unicode spaces included (no-break, figure, narrow no-break,
  ideographic): only ASCII spaces, the box's and the window's padding, are trimmed, by the
  worker reading the screen and by the reader. A draft of ASCII spaces alone, or of tabs (the
  CLI draws a tab as four spaces), cannot be told from padding, and reads as empty; idle, the
  footer then drops "/ commands", and the reply is refused all the same. The requesting
  client's reply result says how many
  characters, and shows what, escaped (`ShownText.Reveal`) and cut to 500 characters. That
  text goes nowhere else: the host's log and Home Assistant (which keeps it in its history)
  get the fixed reason and the length only (`ReplyReasons.Shared`), since a draft can hold a
  pasted password or token;
* in shell mode;
* in vim's normal mode (the footer's "-- NORMAL --"), where typed keys are commands;
* when the screen cannot be read, or does not show exactly one box, rail and plain counted
  together, at the bottom: its bottom edge directly over Copilot's footer row (the sidebar,
  mode, commands, shell, or file and issue hints), with nothing but blank rows under that.
  The CLI draws everything else above its input area, and code in the agent's output is
  drawn as plain text, so the agent can print rows that look like the box and its footer;
  printed above the real box, or above a dialog in its place, they are not anchored there.
  An empty box must also show "/ commands" in the footer, which the CLI draws only while
  the box is empty. While a turn runs, the footer's place holds the turn's own row (spinner,
  "esc interrupt" or another Esc hint, "ctrl+q enqueue"; `LMe`'s activity layout) instead,
  never with "/ commands": that row is taken for the footer when it carries Esc's or the
  enqueue hint, the box's own rows decide whether it is empty, and its vim and shell labels
  are read as in the idle footer, so a reply can still steer a working session. In a
  window under 80 columns (measured by the box's own bottom edge, which spans the frame) the
  turn's row leaves those labels out, and a box holding only spaces in shell mode draws as
  empty, so a reply to a working session there is refused ("too narrow to check its input
box; reply once the turn ends, or widen it"). With the
  CLI's hints hidden (`hideToggleHelp`), the idle footer is drawn empty and a reply to an
  idle session is refused; a turn being cancelled shows no hints either, and is refused.

Whenever the screen shows exactly one box, refused or not, the reader also describes it
(`CopilotInputBoxState`, for #804's Clear and send): its rows, its full text and how many
lines it draws, whether it is in shell mode, its vim mode as the footer shows it (none,
insert or normal), whether it is the prompt's own box at the bottom over the footer, and
whether it is all on screen. A rail box taller than the window,
its top edge scrolled away, is described as cut off; it is refused as before. A plain box
cut off the same way cannot be told from output, so it is not described.

The box is read when the reply arrives, so a draft or shell mode is refused at once, and
read again once the typing turn has come, after the other checks. The words are held to
that read's box rows (`KeySend.Expect`), and to the footer under them (`KeySend.Checks`):
in the same attach, just before the first key, the worker reads the box's rows again, which
must read exactly so, and the footer, which must still be there (one of its own hints or
labels) and show neither shell mode ("Shell", "shell", "esc exit shell mode") nor vim's
normal mode ("-- NORMAL --"), with nothing but blank rows under it to the window's bottom, as
the reader required: the agent can print rows that read like the box and its footer, and
with only those rows held, a real box holding a draft, or a permission dialog, drawn under
them afterwards passed. The box's rows alone cannot say so: '!' then a space enters
shell mode with only a space in the box, which draws as an empty box, and Esc into vim's
normal mode changes only the footer. In a window under 80 columns (the box's bottom edge)
the footer must also not have become a running turn's row (Esc's hint, the enqueue hint):
there that row leaves out the mode labels, so a turn starting after the read would hide
them, and it is refused as one running at the read is. Anything else in the footer (its
spinner, its hints) may change. If a check fails, nothing is typed and the reply is `refused` ("input box
changed just as the reply was to be typed"). The Enter is a send of its own, after
Copilot's 300 ms pause, held to the box's bottom edge and to the same footer checks, and
to the box's first text row not starting with '!' (the frame-off box's shell glyph): if
any fails (a dialog drawn in the input area's place, Home then '!' while the words were
typed, Esc), no Enter goes, and the reply is `unconfirmed`, its words left in the box for
the person to send or clear. A box that grows down the screen with the words (a short
screen, a long reply) moves its edge too, and is reported the same way; one that grows up a
full screen moves its first text row, which then leaves the '!' check to the footer's. What
no check can close is the moment between the worker's read and the CLI taking the key: a
mode changed at the machine in that moment is not seen. The
screens the tests read are in `tests/fixtures/screens/copilot-input-box.json`, built from
the captured live screen and the CLI's rendering code, each naming its source.

## Attachments (Copilot)

Ported from agent-ha-bridge (`hooks/daemon-replies.ps1`: `New-BridgeAttachmentPrompt`,
`Save-BridgeReplyFile`, `Get-BridgeAttachmentFileName`, `Remove-BridgeStaleAttachment`).
The phone sends each file ahead of the reply (`attachment.start`, `attachment.chunk`); the
`AttachmentStore` holds it in memory for that device, checks its SHA-256, and stages it
only when a reply that names it is about to be typed.

**Large files.** A file over 256 KiB, up to 100 MiB, comes one of two ways, and is held on
disk, never in memory or a shared TEMP:

* From a device through the relay, through the relay's **blob store**
  ([protocol/blobs.md](protocol/blobs.md)): the phone seals it with a key of its own,
  uploads it straight to Azure Storage, and names it in `attachment.start` with that key.
  The host fetches it on its own relay connection (`RelayBlobs`, at most two at once),
  opens it record by record into `<staging>\incoming\<32 hex>.part`, checks its size and
  SHA-256, and deletes the blob from the store, whether it checked out or not. A failure is
  said once, to the next `attachment.start` for it.
* From the app on this machine, in chunks over the **local pipe**
  (`sessions.attach.direct`), written to the same `incoming\` file as it arrives, hashed as
  it goes.

At most 1 GiB of these are held at once. Staging copies one into the staging folder like any
other; once typed, or dropped, its `incoming\` file is deleted, and the sweep deletes any
there no held attachment is using (all of them at start-up).

**How Copilot gets the file.** Exactly as agent-ha-bridge does it: the staged path as an
`@<path>` mention for each file, in order, then the words, trimmed, as one line, typed as
any reply is (one burst, then Enter). Copilot CLI records such a prompt with the mentions
in `content` and one `data.attachments` entry (`type: "file"`, `path`, `displayName`,
`mentionIndex`) for each file it attached, as agent-ha-bridge's replies with images in this
machine's transcripts show. A mention cannot be quoted, so a path with
whitespace is refused rather than sent with some files missing, as there.

**Staging.** `<profile>\attachments` (by default `%LOCALAPPDATA%\AgentBridge\default\attachments`),
narrowed by `PrivateFolder` the way agent-ha-bridge's `Protect-BridgeSecretFile` narrows its
own: a protected DACL granting only the user (as owner) full control, inherited by the
files; mode 0700 and 0600 elsewhere. A link, or a folder that stays readable by anyone else,
is never written to. File names are `<32 random hex>-<safe name>`, the safe name as
agent-ha-bridge makes it (letters, digits, `.`, `-`, `_`; a 48-character stem; a short
alphanumeric extension), with one addition: for PNG, JPEG, GIF, WebP, HEIC/HEIF and PDF the
extension comes from the file's bytes, since Copilot goes by it. agent-ha-bridge gets the
same from Home Assistant's image store, which only takes real images and falls back to
`.png`.

**Cleanup.** The staged copies of a typed reply stay 24 hours (agent-ha-bridge's
`Remove-BridgeStaleAttachment` cutoff), because a working session reads a file only when it
takes the prompt; a sweep at start-up and every 10 minutes deletes older ones. It deletes only
files named as the store names them, and nothing at all when the folder has been replaced by
a link. A reply that
types nothing deletes its copies at once. Held files no reply uses go after 15 minutes. The
log says how many files and how many KiB, never their names.

**Differences from agent-ha-bridge.** Images and other files take one path each way: up to
256 KiB through the end-to-end channel, and up to 100 MiB through the blob store, end to end
encrypted; Home Assistant's image store took images of any size, and other files only up to
256 KiB. Against a relay without a blob store the phone keeps to 256 KiB and shrinks a larger
photo or screenshot to fit (see the PWA). Claude Code and Codex take attachments too (below).

## Claude Code and Codex

The keys are agent-ha-bridge's (`Invoke-DaemonReply`): one line, 300 ms, then Enter on its
own. What guards them is new. agent-ha-bridge's hooks tell it when a permission prompt, an
`AskUserQuestion` or a Codex approval is waiting; this host registers no hooks
([app-windows.md](app-windows.md)), and a reply typed into one of those would answer it
(words pick an option; Enter accepts the highlighted one). Each of them takes the input
box's place on screen, so the host reads the screen first (`AgentReplies/Composer.cs`) and
types only into an **input box that is positively on screen**, with the first send
held to those rows (`KeySend.Expect`), so nothing is typed if they change in between. The
rows are held as the worker reads them, with only the window's padding (ASCII spaces)
trimmed: Claude Code draws its prompt as the pointer and a no-break space, and held with
that space trimmed away, every Claude reply read as a box that had changed and was refused:

- Claude Code 2.1: a row starting `>` at the left edge between two full-width rules. A
  permission prompt shows indented options under one rule; an `AskUserQuestion` shows tabs
  between its rules and "Esc to cancel" beneath. Neither is taken for the box. After a turn
  Claude suggests a next prompt in the empty box, as text a screen read cannot tell from
  typed words, so the box is typed into whatever it shows: words someone left in it would go
  with the reply, and Claude's record of them would leave the reply `unconfirmed`. One
  exception: words starting with `/` or `!` (after whatever shows as nothing) are a command
  held for Enter, which a reply would complete and run. Claude strips invisible characters
  from what it is given and leaves the rest in its box unsent, so a reply `"\u3164/"`
  could leave a bare `/` there ([#448](https://github.com/danswett/agent-bridge/issues/448)).
  A box holding one is refused ("holds a command that a reply would run").
- Codex 0.165: the last row starting `›` with "? for shortcuts" in the footer below it,
  which Codex shows only while nothing is typed (idle or working; the box's own text is one
  of several placeholders). An approval's first option also starts `›`, but has no such
  footer and ends "Press enter to confirm or esc to cancel".

Otherwise the reply is `refused` ("input box is not on its screen", for Codex "already
typed in Codex's input box", or a box holding a command), and nothing is typed.

**Confirmed by identity.** Claude: a `user` entry (not meta, not a subagent's, no
`tool_result`) whose content is the line, or, typed while it works, the `queue-operation`
`enqueue` with that content; the `user` entry Claude writes when it later takes a queued
prompt (`promptSource: "queued"`) repeats it and confirms nothing. Codex: an
`item_completed` `UserMessage` whose text is the line (an older `user_message` event is read
too). Each record confirms one reply. Anything else is `unconfirmed`.

**Enter again.** Claude takes a long burst as a paste and can swallow the Enter after it
(the reason for `Confirm-DaemonClaudeSubmit`); Codex was seen live to make it a new line in
the box instead. If nothing is recorded within 1.5 s, the host reads the screen and presses
Enter alone, held to the box's rows, only if the box still holds exactly this reply; at most
twice. A box still empty or showing only the start of the reply is looked at again (Codex
takes typed keys in slowly while it streams output, seen live). A prompt that has taken the
box's place, or other words in it, stop it.

**Attachments.** Claude reads a file its prompt mentions as `@<path>`, image or text, and
records an `attachment` entry with `filename` set to the path for each (seen live), so the
line is Copilot's. An image too large to go inline gets no such entry; Claude reads it with
its `Read` tool instead (seen live with 290 KiB), and a successful `Read` of the path counts
the same. That `Read` is outside Claude's working directories, so Claude may first ask at the
machine to allow it, which without hooks the card does not show; the reply is then
`unconfirmed` unless it is allowed within the wait. A reply is `delivered` once every staged path has been read (for a queued
reply, once Claude takes it). Codex does not read `@<path>` (left as words, seen live): it
attaches an image when a paste is exactly a path to one, showing `[Image #n]`. Keys that
reach it together are one paste, though: a path and the words sent 500 ms after it were seen
live taken as words. So each image path goes as a paste of its own only once the box shows
the ones before it as `[Image #n]`, and the words and Enter once it shows them all (each
waited for up to 10 s; if the box shows a path as words instead, typing stops there and the
reply is `unconfirmed`). The record must list each as a `local_image`. Codex is sent PNG,
JPEG, GIF and WebP only; a reply with any other file is `refused` before anything is typed,
and its files are kept.

## Before and after typing

In order, for every reply:

1. Platform, agent-ha-bridge, then `Replies.Check` (blank without attachments, over 16 KiB,
   a leading `/` or `!`, more than 10 attachments or a malformed one).
2. The session is live (`stale` otherwise) and is a Copilot, Claude Code or Codex session (`refused` otherwise).
3. One delivery per session (`busy`), shared with answers. Every request's outcome is
   remembered for 15 minutes and returned for a resend, which types nothing.
4. No question or permission prompt in the session as reported (`refused`: "answer that
   first", or, for one marked `answerAtMachine`, "answer it there first"). Its attachments
   are held, complete, for this device (`refused` otherwise), and are reserved for this
   reply. The process is the live agent process, and not elevated. A session this host
   launched, resumed or restored whose window has not been seen ready is `refused`: it may
   still show a startup question, folder trust above all, that Enter would answer, and a resume's
   transcript already exists, so the check below never catches it (`LaunchService.HeldForReply`,
   #780). Once following has ended unready, it stays so while that agent runs (the launcher, or the
   process it handed the session to), until a prompt of the person's is in its transcript after the
   launch (Copilot; a turn count would not do: it leaves out the turn that prompt starts, and a new
   CLI's `session.resume` ending a killed turn counts one with nobody having typed) or, for an agent
   that registers only once ready, it registers. The attachments are staged (below).
5. The single typing turn is taken, then, immediately before typing: agent-ha-bridge again,
   the session again, and its transcript: no `ask_user` from any agent without its
   completion or `human_response.recorded`, and no `permission.requested` without its
   `permission.completed`, since the last abort or shutdown. One left open when a new CLI
   resumed the session (`session.resume`, with no end written for it) still counts, since the
   new CLI may show it again without writing it again: until it completes, an abort or shutdown,
   a question the session itself asks since (a subagent's is its own, #855), or a prompt typed at the machine (`refused`, saying it was resumed). The whole transcript counts,
   since a subagent's question stops only that subagent and other work can write any
   amount after it (and the session source does not report a subagent's question): it is
   read from the start once per session, in the background as soon as the session is found
   and again as the session changes or its file is heard written (a 96 to 169 MB `events.jsonl` took 0.3 to 0.6 s, which the first
   reply after a start used to spend before typing, #599), and from where it was
   left on every later read, so the read before typing covers only what was written since. A transcript that cannot be read is `refused`, never taken to
   mean nothing is asking. A Copilot session with no `events.jsonl` yet (its folder there,
   and no transcript ever read for it) is refused with its own reason. Copilot 1.0.105
   creates the file only with the first prompt, and it registers the session while its
   folder-trust question may still be on screen. There, typed words are dropped and Enter
   answers "Yes" (seen live: a reply trusted the folder). Without a transcript, the host
   cannot tell that question from an idle prompt, and only a person answers it. So the
   first prompt is sent at the machine, or given at launch. The point read up to is where
   confirmation starts.
6. The worker types it. Nothing written is `refused`, and the staged files are deleted with
   the attachments kept for another try; a worker that stopped after its first write is
   `unconfirmed`, never retried. Either way once a key may have gone the attachments leave
   memory. The session is released for answers.
7. Up to 60 s, the transcript after that point is watched for a main-agent `user.message`
   with no `source`, whose content equals the typed words with whitespace runs collapsed.
   Each such prompt confirms one reply (claimed by its line's offset), so two replies of the
   same words need two prompts. `delivered` on one whose `attachments` list every staged
   path; `unconfirmed` on one that lacks any (Copilot leaves a mention it could not read as
   text); a `session.shutdown`, or the wait running out, is `unconfirmed`, saying whether
   something else was recorded instead. The transcript is looked at again as soon as the
   session source's file watcher hears it written, and every 250 ms whatever it hears (a
   watcher drops events). Polling alone left 0.66 to 0.86 s between the prompt being recorded
   and `delivered` (#599). The same goes for Claude Code and Codex replies, and for answers
   ([host-answers.md](host-answers.md)).

**What no check can see.** Words somebody left half-typed in the CLI's input are in no
transcript; the reply is added to them, and the prompt recorded differs, so the outcome is
`unconfirmed`. agent-ha-bridge has the same exposure. A question the agent opens in the
moments between the transcript check and the keys landing would take the words; the window
is the worker's start-up, about the same as for an answer.

## Progress and timings

A client that saw `sessions.progress` and sends `"progress": true` hears the reply's stages
before its result ([v1-sessions.md, Progress](protocol/v1-sessions.md#progress)): `received`
as `SessionService` takes the request, before step 1, and `typed` once every key of step 6 has
gone, with `working` when the session was working then (it takes the prompt at its next step,
as Copilot's `steering`). The result then carries `timings`: `typed`, `recorded` (the agent's
own timestamp on the prompt that confirmed it) and `confirmed`, in ms after receipt. A resend
of a reply still being delivered hears the stages it has reached. Frames are queued without
waiting, so a slow or closed channel never holds up the typing.

### Timings

The host log's timestamps are to the second, too coarse to say where a reply's second went
(#597). So every reply, and every answer ([host-answers.md](host-answers.md)), logs one line
of its stages in ms after the request reached the delivery, progress asked for or not,
correlated with the outcome line by the request id's first 8 hex digits. When it reached the
delivery is given in UTC, ISO 8601 with a `Z`, to the millisecond, as diagnostic timestamps
are (the owner's rule on #611): it reads the same on every machine and against agents'
transcripts, which are in UTC too. The log's own prefix stays local time to the second.

```
reply 0b7e3c1d to 99999999: delivered
reply 0b7e3c1d timings (ms after 2026-11-04T06:56:28.441Z): checked 2, turn 3, inject 4-25, typed 25, recorded 1190, confirmed 1361, settled 1361, sent 1362
```

| Stage | When |
|---|---|
| `checked` | The checks before the typing turn passed (steps 1 to 4) |
| `turn` | The single typing turn was taken (step 5) |
| `inject a-b` | The first worker started and the last finished, an Enter pressed again (Claude and Codex replies, Claude's review screen) included |
| `typed` | Every key went; `working` follows it when the session was working |
| `recorded` | The agent's timestamp on the prompt that is the reply |
| `confirmed` | The host matched it (`delivered` only) |
| `settled` | The outcome was known |
| `sent` | Its response left on the channel or pipe of the request that started it (`not sent` if it could not); absent for Home Assistant's, which has none |

A stage not reached is left out: a reply refused at step 1 logs only `settled` and `sent`. The
receipt time is UTC, unlike the log's own local-time prefix, so a line is matched to the lines
around it by its request id, and its times to an agent's transcript as they are, without an offset.
