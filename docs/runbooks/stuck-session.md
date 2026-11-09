# A session stuck on "did not complete / did not acknowledge ... within 120s" (#687)

## What it is

Copilot CLI has its own timer for events it hands to its session host. When the host does not
complete or acknowledge one within 120 s, the CLI logs, for example,
`session host did not complete the session.background_tasks_changed event within 120s` or
`session host did not acknowledge the pending_messages.modified event within 120s`. From then
on every `session.send` to that session fails with the same message, and nothing recovers it
until the CLI process is restarted with `--resume`. This is Copilot CLI's behaviour (the finding
and evidence are on #687); the bridge's hooks are not the cause. A background agent that finishes
while the session is wedged can lose its result.

## What the bridge does (slice 1, #687)

- **Detects.** The host already marks the session `Agent error` (a Copilot `session.error` that
  ends the turn) or `Not responding` (working, nothing written for longer than a turn goes
  quiet; #800). Both are treated as stuck, except an error with its own class (rate limit,
  authorisation, quota) and any session with a question open. An `Agent error` must last 5
  minutes first (`StuckErrorAfter`), so a one-off error the next prompt clears sends nothing.
- **Tells the owner.** The Home Assistant connector sends "Copilot on *machine* looks stuck" to
  the phones and push service that take sign-in notices: one per session, at most hourly, and it
  is withdrawn when the session recovers. The host log records each notice and withdrawal.
- **Does not restart anything.**

## Manual recovery (the owner, at the machine or over RDP)

Do this only for the one session named in the notice, and only when it is not working
(nothing in it has moved for several minutes, no question card is open).

1. Note the session id: Details on the session's card, or `~/.copilot/session-state/` (one folder
   per session; the newest by modified time is usually it).
2. Close that session's console window. Do not touch any other session.
3. In a normal terminal on the same machine, resume it with the same id:

   ```powershell
   copilot --resume <session id>
   ```

   For an Agency session: `agency copilot --resume <session id>`.
4. Check the card goes back to Working or Idle and the notice clears on the phone. A background
   agent that finished while it was wedged may have lost its result: ask the session to look at
   what that agent was doing (its branch or PR).

## Planned: guarded restart (slice 2, not built)

One "Restart this session" action in the app and on the phone, for a session already shown stuck.
The host would refuse it while the session is working, has an open question, or its old process
still writes anything on a second check a minute later, and would record the decision (restart or
refusal, with the reason) in the host log. It would start the same agent with `--resume <id>` in
a normal window. The action types and starts processes for a remote device, so it is a sensitive
change (`src/AgentBridge.Host.Delivery/**`, AGENTS.md rule 5) and ships in its own PR.
