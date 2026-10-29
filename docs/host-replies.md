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
| Copilot CLI | Yes | agent-ha-bridge has long typed replies into it, and its transcript records every prompt (`user.message`) with the text as typed, which confirms one by identity |
| Claude Code | Yes, typed only into its empty input box as seen on screen | Its transcript records a prompt typed while idle as a `user` entry whose content is the line as typed, and one typed while it works as a `queue-operation` `enqueue` with that content (2.1.312, seen live), which confirms one by identity. agent-ha-bridge confirmed only that *some* prompt was submitted |
| Codex | Yes, typed only into its empty input box as seen on screen | Its rollout records every prompt, idle or mid-turn, as an `item_completed` `UserMessage` with the line as typed (0.165, seen live), which confirms one by identity. agent-ha-bridge typed with no confirmation at all |
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
types only into an **empty input box that is positively on screen**, with the first send
held to those rows (`KeySend.Expect`), so nothing is typed if they change in between:

- Claude Code 2.1: a row starting `>` at the left edge between two full-width rules; empty,
  or showing "Press up to edit queued messages". A permission prompt shows indented options
  under one rule; an `AskUserQuestion` shows tabs between its rules and "Esc to cancel"
  beneath. Neither is taken for the box.
- Codex 0.165: the last row starting `›` with "? for shortcuts" in the footer below it,
  which Codex shows only while nothing is typed (idle or working; the box's own text is one
  of several placeholders). An approval's first option also starts `›`, but has no such
  footer and ends "Press enter to confirm or esc to cancel".

Otherwise the reply is `refused` ("input box is not on its screen", or "already typed in
... input box"), and nothing is typed. Words already in the box would be sent with the reply.

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
twice. A prompt that has taken the box's place, or words someone typed, stop it.

**Attachments.** Claude reads a file its prompt mentions as `@<path>`, image or text, and
records an `attachment` entry with `filename` set to the path for each (seen live), so the
line is Copilot's; a reply is `delivered` once every staged path has been read (for a queued
reply, once Claude takes it). Codex does not read `@<path>` (left as words, seen live): it
attaches an image when a paste is exactly a path to one, showing `[Image #n]`. So each image
path goes as a paste of its own, 500 ms apart, then the words and Enter, and the record must
list each as a `local_image`. Codex is sent PNG, JPEG, GIF and WebP only; a reply with any
other file is `refused` before anything is typed, and its files are kept.

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
   reply. The process is the live agent process, and not elevated. The attachments are
   staged (below).
5. The single typing turn is taken, then, immediately before typing: agent-ha-bridge again,
   the session again, and its transcript: no `ask_user` from any agent without its
   completion or `human_response.recorded`, and no `permission.requested` without its
   `permission.completed`, since the last abort or shutdown. The whole transcript counts,
   since a subagent's question stops only that subagent and other work can write any
   amount after it (and the session source does not report a subagent's question): it is
   read from the start once per session, before the typing turn, and from where it was
   left on every later read. A transcript that cannot be read is `refused`, never taken to
   mean nothing is asking. The point read up to is where confirmation starts.
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
   something else was recorded instead.

**What no check can see.** Words somebody left half-typed in the CLI's input are in no
transcript; the reply is added to them, and the prompt recorded differs, so the outcome is
`unconfirmed`. agent-ha-bridge has the same exposure. A question the agent opens in the
moments between the transcript check and the keys landing would take the words; the window
is the worker's start-up, about the same as for an answer.
