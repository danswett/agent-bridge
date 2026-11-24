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

`Register(session, request, command)` returns an awaitable verdict (`allow`, `deny`, or none), or null when refused. `Resolve(principal, session, request, shown, verdict)`, where `shown` is the digest, trusts its caller for the principal and uses the wall clock for the deadline:
- accepts only **`person:` plus a 32-hex device id**, the relay form; `person:local` (this machine's pipe), any agent, and anything malformed is
  refused as `Principal` and the prompt keeps waiting;
- is **one-shot**: the answer is used up, so it cannot settle the next identical prompt; the same prompt registered again while the first is live is refused (null) and
  the first keeps waiting: a wait ends only by an answer, `Withdraw` or the deadline;
- gives **no verdict after the deadline** (100 s, below the hook's timeout) and a late answer is `Unknown`;
- refuses a command longer than the 300-character preview as `Truncated` (the person would see a prefix of what they approve);
- is **bound to the command shown** (#958): the answer carries the digest (lower-case SHA-256 hex) of the exact text the person was shown, and an answer
  for any other command is refused as `Shown` and does not settle the prompt; a second `Register` for a session and request that is still waiting is
  **refused** (it returns nothing), never a replacement, so the prompt on screen cannot be swapped under the person. The text passed to `Register` and
  digested is the text **after the surface's escaping**, which can be longer than the raw command; the broker does no escaping, so PR B and C must pass
  exactly what the person sees, and the 300-character rule applies to that text;
- checks the deadline itself when an answer arrives, so a delayed timer callback cannot let a late answer through; a wait that is not finite and positive
  is refused when the broker is built;
- holds at most 4 prompts per session and 256 in all (each up to the wait); a further `Register` is refused (null). A command over the preview is never hashed, and text
  that is not well-formed (a lone surrogate) is refused with `ArgumentException`, never turned into U+FFFD, so two texts cannot share a digest;
- refuses a verdict other than exactly `allow` or `deny`; `Withdraw(session)` ends a session's waits with no verdict.

**Not in this PR:** no pipe op, no hook change, no registration, no tracker listing, no app or push; **nothing constructs the broker** (a test pins
that no other source mentions it), and the `permissions.claudeApproval` setting does not exist yet, so the feature is off.

## What the broker does not show (stated plainly)

That `person:<id>` really is a person is the existing relay and device-grant authentication of `sessions.answer`, which this reuses and does not
re-prove. The local pipe's `person:local` is refused here on purpose: a local caller is not shown to be a person, so a person answers through the
relay or at the machine. That a hook asking over the pipe is the real hook of that session is PR B's job.

## Next PRs (not started)

- **B: the pipe op and the hook client.** The op `approval.wait` takes the event; **the host's caller check is the one `agent.hello` uses** (the
  session's Windows job and the ancestor rule, #4287, extended in #4302 and #4303), reused and not a new pid walk, so orphaned shells and foreign
  callers are treated the same in both places. The hook verifies the pipe server is the host before sending anything. Squatting the pipe name
  or posing as the hook are the risks B must close.
- **C: the listing and the surfaces.** The tracker lists the prompt as answerable (not `atMachine`) when readable and not truncated; `host-answers.md`,
  the app and push follow; the setting `permissions.claudeApproval` (default off) appears here.
- **D: the live check** from #4277's body, then turning it on.
