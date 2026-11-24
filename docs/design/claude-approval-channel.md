# The authenticated channel for a Claude permission-prompt decision (#1003, #3847)

Status: **design, and PR A of four (the host-side broker only); sensitive.** Builds on the hook-side contract in
[claude-remote-approval.md](claude-remote-approval.md) (agent-bridge #4277), which has no allow path because a decision file is unauthenticated: any
process of the same user can forge it (#3847: an unauthenticated local file never drives an answer). This note is the channel that fixes that.

## The idea: the decision never touches a file

The host holds each pending Claude prompt **in memory**. It can be resolved only by a **person's answer that arrived by the relay-authenticated
`sessions.answer` path** (the principal the host already audits as `person:<device id>`, with the escaped-command and truncated rules of #836). The
hook asks the host over the local pipe and gets the verdict in the pipe reply. There is no file for an agent to write or replay; the only input to the
decision is an authenticated device's signed answer.

## PR A (this change): `ClaudeApprovalBroker`, host side, in memory

`Register(session, request, command)` returns an awaitable verdict (`allow`, `deny`, or none). `Resolve(principal, session, request, verdict)`:
- accepts only **`person:` plus a 32-hex device id**, the relay form; `person:local` (this machine's pipe), any agent, and anything malformed is
  refused as `Principal` and the prompt keeps waiting;
- is **one-shot**: the answer is used up, so it cannot settle the next identical prompt; the same prompt registered again ends the earlier wait with no
  verdict;
- gives **no verdict after the deadline** (100 s, below the hook's timeout) and a late answer is `Unknown`;
- refuses a command longer than the 300-character preview as `Truncated` (the person would see a prefix of what they approve);
- is **bound to the command shown** (#958): the answer carries the digest (lower-case SHA-256 hex) of the exact text the person was shown, and an answer
  for any other command is refused as `Shown` and does not settle the prompt; a second `Register` for a session and request that is still waiting is
  **refused** (it returns nothing), never a replacement, so the prompt on screen cannot be swapped under the person. The text passed to `Register` and
  digested is the text **after the surface's escaping**, which can be longer than the raw command; the broker does no escaping, so PR B and C must pass
  exactly what the person sees, and the 300-character rule applies to that text;
- checks the deadline itself when an answer arrives, so a delayed timer callback cannot let a late answer through; a wait that is not finite and positive
  is refused when the broker is built;
- refuses a verdict other than exactly `allow` or `deny`; `Withdraw(session)` ends a session's waits with no verdict.

**Not in this PR:** no pipe op, no hook change, no registration, no tracker listing, no app or push; **nothing constructs the broker** (a test pins
that no other source mentions it), and the `permissions.claudeApproval` setting does not exist yet, so the feature is off.

## What the broker does not show (stated plainly)

That `person:<id>` really is a person is the existing relay and device-grant authentication of `sessions.answer`, which this reuses and does not
re-prove. The local pipe's `person:local` is refused here on purpose: a local caller is not shown to be a person, so a person answers through the
relay or at the machine. That a hook asking over the pipe is the real hook of that session is PR B's job.

## Next PRs (not started)

- **B (the pipe op, this change; the hook client is C): `approval.wait`.** Request `{ "event": "<the hook's raw PermissionRequest event, JSON text, at most 16 KiB>" }`;
  reply `{ "verdict": "allow" | "deny" | "none", "reason"?: one word }`, and **none means the hook shows Claude's own prompt at the machine, never an allow**. The
  host, not the hook, reads the session, tool and input out of the event, derives the request id (16 hex digits of SHA-256 over `session
tool
input`, the hook's
  `ClaudeApproval.RequestId`), and builds the text shown (`tool: command`, or the raw input when there is no command, after `ShownText.Reveal`, as the app shows it),
  which the broker digests: the answer is bound to what the host showed, and the hook cannot describe a command that differs from the real input. The caller check is
  the one `agent.hello` uses (a live session credential and the host's own classification of the caller as an agent, #4287, #4302, #4303), reused, not a new pid walk,
  and the event's `session_id` must be that credential's session. Reasons with `none`: `bad-event` (not JSON, not an object, no `session_id` or `tool_name`, not
  `hook_event_name` `PermissionRequest`, no text or over 16 KiB), `caller` (another session's event), `busy` (a prompt for it is already waiting; never replaced),
  `truncated` (shown cut, so never answerable; at once, nothing registered), `timeout` (no answer by the broker's 100 s, or the hook went away). A refusal of the
  caller itself, or the op not being offered (off), is an error reply, which the hook reads as none. **Off:** the op is offered only if the host is handed it; nothing hands
  it today and the setting does not exist, so it replies "isn't available on this host". Still to close in C: the hook verifying the pipe server is the host.
- **C: the listing and the surfaces.** The tracker lists the prompt as answerable (not `atMachine`) when readable and not truncated; `host-answers.md`,
  the app and push follow; the setting `permissions.claudeApproval` (default off) appears here.
- **D: the live check** from #4277's body, then turning it on.
