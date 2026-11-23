# Busy sends to Claude: flag-on qualification plan (draft)

Part of #2568 and #3856. Status: DRAFT for the SRE Lead and the owner; nothing here is run or approved yet.
Design: [message-delivery.md](../message-delivery.md), "Busy sends to Claude: let Claude Code queue". This plan says what SRE runs, on
disposable sessions, before the owner turns `agentMessaging.claudeBusyQueue` on; what counts as a pass; and how it is rolled back.

## Scope and rules

- **Disposable sessions only.** Launched by the host for this test, on a host with `agentMessaging.remote` set to `own-account`. No live
  session is a target. Load and stress tests never run on live machines (Phase 1 owner decision); this plan has none: the heaviest case is
  one long turn and a handful of sends.
- **The flag is the only switch.** Turning it on is the owner's act after this plan passes. It is set on the disposable host only, by the owner or a person at that machine, never from inside an agent session: host management, config changes included, is refused there (AGENTS.md guard #67, exit 4). The setting is `agentMessaging.claudeBusyQueue` true, with `agentMessaging.remote` set to `own-account` the same way ([host-config.md](../host-config.md)); it is read afresh, no restart. The later arming act is made the same way, at the machine.
- **Evidence, not impressions.** Every case records the sender's exit code, the host status record, and the relevant transcript rows
  (request id, never message text beyond the test words). A case with no evidence is not a pass.
- **No machine names, tokens or message text** go in the report; use ids and "host A", "session 1".

## Preconditions (all must be true; record the versions)

1. These have landed on the disposable host's build: the composer rule (#3865), the unconfirmed mapping (#3869), the queued stage (#3876),
   the status store and depth cap (#3926), the late-taken check (#3952), the active watch (#3968), and the derived slot, Claude-restart
   release and arm keeper (#3973); the session-end release (#3977), the option-2 gate (#3901) and the receiver rate refund (#3937).
2. The fleet tools have the exit-6 mapping and its caller fixes, and the sender cap (slipgates-fleet #1148, #1182).
3. The Claude Code version under test is recorded (the verified signals were seen on 2.1.312). If it differs, cases H1 to H5 are run first.
4. The disposable host is run with `laneDelivery` both off and on (cases are repeated for both; see F).

## Cases

Each case lists the action, then the expected result. "Held" means the status store record has `held` true.

**A. Admission and typing**
- A1. Claude is in a long tool turn (at least 3 minutes). Send one message. Expect: exit 6 within 10 s (the sender's acknowledgement wait), an `enqueue` row with the request
  id in the transcript, record `queued`, no Home Assistant send.
- A2. Claude idle, empty box. Send. Expect: exit 0, a `user` row, record `delivered`, slot free.
- A3. Claude goes from busy to idle while the send is in flight. Expect: the message arrives once; the result is `queued` or `delivered`,
  never `unconfirmed` because of the flip.
- A4. Control, flag off, Claude busy. Expect: refused `busy`, nothing typed, the sender retries as before.

**B. Composer guard (owner decision a)**
- B1. A person's half-typed draft in the box. Expect: refused `composerNotEmpty`; the draft is untouched and nothing was typed.
- B2. A `/` line, a `!` line, a partial `Press up to edit`, the hint plus extra words, the hint in the wrong case, a real suggested next
  prompt. Expect: each refused, nothing typed. Capture each real screen as a fixture.
- B3. A person's own prompt already queued, so the box shows exactly `Press up to edit queued messages`. Expect: a relay send is admitted
  (this is the only non-empty box that is).

**C. Depth cap and the held slot**
- C1. While A1's message is held, send a second. Expect: exit 1 with code `queueHeld`, no retry, no Home Assistant send even with the
  opt-in fallback; the first message is unaffected.
- C2. When Claude takes the first message, send again. Expect: admitted.
- C3. Hold past 120 s. Expect: the sender had exit 6 at the start and hears nothing new; the record becomes `unconfirmed` (`watchEnded`)
  and stays held; a second send is still `queueHeld`; when Claude later takes it the record becomes `delivered`, the transcript shows exactly
  one taken row, and the slot frees. No resend at any point.
- C4. Hold past 10 minutes (a 15-minute turn). Expect: the message Claude finally takes is shown by the host as the agent's, not the
  person's (the arm keeper). Record the shown driver.
- C5. A full status store (`statusFull`) is not run live: it refuses before any key and is never `busy`, and the unit tests cover it, so there is no live fail condition.

**D. Restarts**
- D1. Restart the host while a message is held. Expect: the next send is refused `queueHeld` (derived from the transcript), nothing is
  typed again; when Claude takes the message the next send is admitted. A later-taken message is shown as the person's (known limit);
  record it.
- D2. Restart Claude while a message is held (it must be re-registered as a launch). Expect: the next send is admitted; the old record is
  `unconfirmed` with reason `claudeRestart`. This also proves or disproves "a restart drops Claude's queue" (H3).
- D3. End the session while a message is held. Expect: the record becomes `unconfirmed` with reason `sessionEnded` and is not held, no timer is left for it (the arm keeper stopped), and no further sends can reach the session. Fail: a record still held after the session is gone.
- D4. A person empties Claude's queue at the machine while a relay message is held. Expect: the message is never taken, the slot stays held, a second send is `queueHeld`, and only a Claude restart or the session ending (D2, D3) frees it. Record the transcript rows the clearing writes (`dequeue`/`remove`): that is H2. Fail: the slot frees on a `remove` that has not been proven.

**E. Sender behaviour**
- E1. Exit 6 is not resent by the stall alarm or the wake pump (dry run): they record it and move on.
- E2. `unconfirmed` (exit 2) is never resent, with or without the opt-in fallback.
- E3. Busy retries do not lock the pair: 20 busy refusals in a row to one target, then a free target; no `rate` refusal; the sender stops
  at the first `rate`; with 6 attempts to the pair already in its send log it sends nothing.

**F. Lane interaction**
- F1. `laneDelivery` on: a Copilot target goes through the lane while a Claude target takes the busy-queue path (owner option 2), in the
  same run. Expect: each as designed; nothing about one changes the other.
- F2. `laneDelivery` on, flag off: a Claude target is refused `notReplyTarget`, as today.

**G. Safety**
- G1. Search the host log, the status record and the send log for the test words: none appear. The status record holds ids, a state and
  times only.
- G2. Never an Esc or ctrl+c from the relay: check the injector's recorded keys for every case.
- G3. The kill switch (`liteKill`) while a message is held: sends stop at once; the held record is unaffected.
- G5. Turn the flag off while a message is held, with no restart (the rollback). Expect, as the Rollback section says: new sends to Claude take the old busy refusal and never touch the status store; the held message is still taken by Claude once; its record stays held (nothing looks for the taken entry without a send); turning the flag back on and sending finds the slot still held until that send's check sees the prompt taken or Claude restarted, then admits. Record the shown driver of the message taken while the flag was off.
- G4. A person types the plain words of a relay message (without the sender line) into an idle Claude: it is not shown as the agent's.

**H. Assumptions to prove (add a real-transcript fixture for each, or keep the conservative rule)**
- H1. The `enqueue` row, and the later `user` entry with `promptSource: "queued"` carrying the request id (shape on this Claude version).
- H2. What a person's Esc or queue edit does to a relay message in Claude's queue (`dequeue`/`remove` rows). Until proven, a `remove`
  never frees the slot.
- H3. Whether a Claude restart drops its queue (D2).
- H4. Claude Code's own queue cap and TTL: how many messages queue, and whether one expires.
- H5. How long Claude takes to record an `enqueue` row after the keys. The replier waits up to 60 s for it and returns as soon as it sees it, but the sender gives up at 10 s and reports exit 2. Record the distribution under a long tool turn and an idle one. If sends are often confirmed after 10 s, a message that was accepted is reported uncertain, and a shorter confirm window for this path or a longer sender wait needs its own slice.

## Pass bar

All of the following, on at least three runs of each case on at least two disposable sessions:

1. **No duplicate delivery:** every message appears in the transcript exactly once.
2. **No typing over a person:** every B case leaves the box exactly as the person had it.
3. **No Home Assistant send** after `queued`, `unconfirmed` (exit 2), `queueHeld` or `statusFull`.
4. **No wrong attribution:** a person's words are never shown as the agent's; a late-taken relay message is shown as the agent's while the
   host has not restarted (C4), and as the person's after a restart (D1), as the design says.
5. **No `rate` lockout** in E3, and no refusal code other than the ones named.
6. **Every held slot is accounted for:** each held slot in C and D ends by consumption (the taken entry), a Claude restart or the session ending, and the report records how long each took. A person emptying Claude's queue is **not** a recovery: the relay does not see a `remove` as freeing the slot until H2 proves it, so case D4 is run and its result, a slot that stays held, is reported plainly to the owner as a known limit.
7. **G1 to G4 clean.**
8. **H1 to H5 each resolved:** proven with a fixture added to the repo, or the dependent rule stays as the design has it and the owner is
   told which one.

Any duplicate delivery, any typing over a draft, or any misattribution is a **fail** that blocks the owner's decision; the flag stays off.

## Rollback

`config set agentMessaging.claudeBusyQueue false` (read afresh, no restart). Lite then refuses a busy Claude `busy` again and senders behave
as today. The status store is in memory and goes with the host run; there is nothing to migrate or replay. **With the flag off and no restart:** new sends take the old busy refusal and never use the store; a watch already running ends at its 120 s deadline; the arm keeper of a held send keeps running, because neither it nor the watcher reads the flag, until the record is no longer held; nothing looks for a taken prompt without a send, so a held record stays held until the session ends, or Claude restarts and the flag is on for the next send, or the host restarts. Turning the flag back on finds those slots still held. A message taken in the meantime is still shown as the agent's while its keeper runs (the arm lapses only after a host restart). Case G5 runs this. A relay message already in
Claude's queue stays there (the relay never removes it); the person at the machine can clear it. The report says which messages those were.

## Reporting

The SRE Lead reports one receipt per case (action, exit code, status record, transcript rows by request id) and a one-line verdict against
the pass bar. The owner decides on the flag after reading the verdict; this plan does not turn it on.
