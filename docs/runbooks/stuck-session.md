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

- **Detects, from data and not from a timer.** A session is stuck on the first confirmed
  evidence: the wedge the host recognised from the CLI's own did-not-acknowledge/did-not-complete
  error (`AgentErrors.HostWedgeMessage`, #1516), or `Not responding` as the host already decides it
  (#800). Any session with a question open is not stuck. Other errors (rate limit, authorisation,
  quota, an error of no known cause) show on the status only. (The host's own "unconfirmed reply
  on an idle session" evidence is used by the guarded resume in #1520, not by this notice.)
- **Tells the owner.** The Home Assistant connector sends "Copilot on *machine* looks stuck" to
  the phones and push service that take sign-in notices: one per stuck episode, withdrawn when the
  session takes a new event or acknowledgement. Only the recognised wedge is said to "not recover
  by itself"; a session that is only `Not responding` is not, as it may pass. The host log records
  each notice and withdrawal.
- **Flood guard.** A session that gets stuck again within an hour of its last notice is not told
  again; that is logged as a `FAULT` line, so a suppressed notice is never silent.
- **Where it moves.** The notice rides the HA connector, which #1396 retires; it moves to the
  Slipgates push path then, with the same rules.
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
