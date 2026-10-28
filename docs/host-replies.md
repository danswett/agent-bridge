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
| Claude Code | Refused: "not supported for claude yet" | agent-ha-bridge types replies into it, but confirms only that *some* prompt was submitted (a `user` entry or a `queue-operation` enqueue), not which. Claude Code takes a long burst as a paste and can swallow the Enter after it, so the bridge presses Enter up to twice more, and must not while a permission prompt is open, which only agent-ha-bridge's own hook status says. No transcript of a queued Claude prompt has been examined here to confirm by identity |
| Codex | Refused: "not supported for codex yet" | agent-ha-bridge types replies into it with no confirmation at all (its Codex entry has no `TranscriptConfirmsInput`), and records having no Codex install to check against |
| Any, on macOS or Linux | Refused, and `sessions.reply` is not offered | Delivery through tmux comes later |

Claude and Codex slot in beside the Copilot planner and verifier once their prompts are
confirmed by identity on a live session.

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
takes the prompt; a sweep at start-up and every 10 minutes deletes older ones. A reply that
types nothing deletes its copies at once. Held files no reply uses go after 15 minutes. The
log says how many files and how many KiB, never their names.

**Differences from agent-ha-bridge.** Images and other files take one path, up to 256 KiB
each, through the end-to-end channel; Home Assistant's image store took images of any size.
The phone shrinks a larger photo or screenshot to fit before sending it (see the PWA), and
the blob store will lift the limit. Only Copilot sessions take replies here, so only they
take attachments; agent-ha-bridge also typed them into Claude Code and Codex.

## Before and after typing

In order, for every reply:

1. Platform, agent-ha-bridge, then `Replies.Check` (blank without attachments, over 16 KiB,
   a leading `/` or `!`, more than 10 attachments or a malformed one).
2. The session is live (`stale` otherwise) and is a Copilot session (`refused` otherwise).
3. One delivery per session (`busy`), shared with answers. Every request's outcome is
   remembered for 15 minutes and returned for a resend, which types nothing.
4. No question or permission prompt in the session as reported (`refused`: "answer that
   first"). Its attachments are held, complete, for this device (`refused` otherwise), and
   are reserved for this reply. The process is the live agent process, and not elevated.
   The attachments are staged (below).
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
