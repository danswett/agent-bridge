# How the host ends sessions

`session.stop` ([contract](protocol/v1-sessions.md#sessionstop)) ends a session from another
device, the way agent-ha-bridge's End session button does. It uses what answers and replies
use ([host-answers.md](host-answers.md), [host-replies.md](host-replies.md)): the injector
worker to type `/exit`, the agent-ha-bridge guard (G5), request memory, and the same
`DeliveryGate`, so ending a session never types over an answer or a reply.

## What agent-ha-bridge does

From `hooks/daemon-launch.ps1` (`Invoke-PendingStops`, `Test-DaemonStopConfirms`,
`Get-DaemonProcessIdentity`) and `hooks/session-launch.ps1` (`Stop-BridgeCopilotSession`):

* **Two presses when not idle.** A press of `button.<node>_stop` on a session whose status
  is anything but `idle` (working, waiting, background agents or shells, or not yet
  classified) only arms, and the card's status line says "Press End session again to end
  it", with what a second press would interrupt and "the confirmation lapses in 10s". A
  second press within `StopConfirmSeconds` (10) ends it. An idle session ends on one press.
  The daemon enforces it, not the card, because the same button is pressed from phones,
  automations and other agents. A confirmation nobody made in time is disarmed and the card
  says "End session NOT confirmed".
* **The same process.** An arm records the session's pid and that process's start time. A
  second press confirms only if the session's process now has the same pid and the same
  start time; a replaced process, a reused pid or an unreadable start time arms again.
* **Graceful, then forced.** `/exit` is typed into the session's console exactly as a reply
  would be, so the CLI writes its transcript and releases its lock and the session stays
  resumable. If the process is still there after 12 s, `Stop-Process -Force` on that pid
  alone. The same for Copilot, Claude Code and Codex (all take `/exit`).
* **The status reads `ending`,** then the session's entities are retired once it has gone.
* **The window.** Only a console the bridge itself launched for the session is closed
  afterwards (the launcher can outlive the CLI); a terminal the person opened is left
  alone. On macOS the tmux pane's process is the agent, and the Terminal window the bridge
  tagged is closed.

## What this host does

The same rules, with these differences, each deliberate:

| | agent-ha-bridge | This host |
|---|---|---|
| Confirming | Any second press of the shared button within 10 s | The second press carries the token the first press was given (`confirm`), so a press from another device or a resent first press is never taken for the second |
| Process identity | pid and start time, read again on the second press | pid and start time, checked on both presses, and then the process is **held open by handle** from just before `/exit` until it has gone, so the id cannot pass to another process in between and termination can only reach the process that was checked |
| A session asking a question | `/exit` typed into it | Terminated without `/exit`: typed words land in the question's input, and Enter could submit `/exit` as the answer. Asking is read from the session and, for Copilot, from the transcript (a subagent's question is only there) |
| `/exit` that could not be typed | Waits out the 12 s anyway | Terminates at once |
| Who it refuses | Whatever `Stop-Process` refuses | Refused before anything is armed: an elevated process (integrity High or above), another user's (token user SID differs), one whose start time cannot be read, and the host's own process |
| The window or tab | Closes the console the bridge launched | Left open: this host does not launch sessions yet, so every terminal is the person's own |
| macOS and Linux | tmux | Not offered yet (`sessions.stop` left out), as for answers and replies |

## In order, for every press

1. Request UUID (`refused` otherwise). Every outcome is remembered for 15 minutes and
   returned for a repeat, which does nothing more.
2. Platform, agent-ha-bridge, `Stops.Check` (`refused`).
3. The session is live (`stale`), has a known process (`refused`), which is a running agent
   process (`stale`) with a readable start time (`refused`).
4. The process is opened for a moment: still the process that started then (`stale`
   otherwise), not elevated, this user's, not the host (`refused`).
5. Already `ending` from another press: `busy`.
6. Not idle, or idle with background work: unless the press carries a token from a first
   press of this session, within 10 s, against this pid and start time, it arms and answers
   `confirm` with a fresh token. Confirming spends every waiting arm.
7. The session's `DeliveryGate` claim (`busy` if an answer or reply holds it), held until
   the outcome. The session is shown as `ending`.
8. The process is opened and held. The single typing turn is taken; agent-ha-bridge again
   (`refused`); if nothing is asking, `/exit` and, 300 ms later, Enter. The host then waits
   up to 12 s for the process to exit through its handle.
9. Still running: agent-ha-bridge again (`failed` if it has started, nothing terminated),
   then `TerminateProcess` on the handle, and up to 5 s for it to go. Gone: `ended`
   (`forced` if terminated). Still there: `failed`.
10. A pass of the session source, so the session is removed as soon as it has gone; if it
    is still there, it shows its own status again.

## The log

```
end requested for 0a528be9 (pid 41208) while this session is still working; waiting for a second press
end 3d5e7f90 for 0a528be9: confirm (this session is still working; the confirmation lapses in 10 s)
typed /exit into 0a528be9 (pid 41208) -> Typed ok
ended 0a528be9: exited cleanly (pid 41208)
end 6c1d2e3f for 0a528be9: ended
```

A session that ignores `/exit` logs `ended …: did not exit within 12 s; terminated pid …`
and `… : ended (terminated)`; one asking a question logs `… is asking a question, so /exit
is not typed`.
