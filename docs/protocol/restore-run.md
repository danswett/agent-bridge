# Restoring offered sessions from a client (#948)

**PROPOSED, not built.** Design first: nothing here is implemented until Product, Creative,
Trust and Engineering accept it on its PR. Field and op names are the proposal's, not a shipped
contract. Folder trust is out of scope (#947): this op never answers it.

After a reboot or a crash the host either restores what died, or only **offers** it
([v1-sessions.md, What went](v1-sessions.md#hello); [host-launch.md](../host-launch.md#restoring-after-a-reboot-or-a-crash)).
Today an offered session is resumed one at a time from Launch, or not at all: §6.12's notice says
"*n* sessions can be restored at the machine" and offers no button (#696 rule 5: no RDP). With
`sessions.restoreOnStart` merely **off**, the host could restore them, and Product's call on #790
is that the notice should offer **Restore** from any client, with a confirmation. This page is the
host's side of that: one op, `restore.run`, that restores what the notice offered, through the
same resume path a start-up restore takes. The client's side is ui.md §6.12, *Restore from a
client*.

## Capability

| Capability | Ops |
|---|---|
| `sessions.restore.run` | `restore.run`: restore, idle, the sessions `machine.restore.offered` names, as a person asked from one of their devices |

Offered only when all of these hold, and taken away (with `hello.changed`) when one stops holding:

- the host offers `sessions.restore` and `sessions.launch` (launching on, not elevated, on the
  person's desktop);
- the machine is not read-only: not `delivery.readOnly`, and agent-ha-bridge is not running for
  this user (design G5). Under G5 the notice stays text only;
- the channel is a **person's device**: through the relay from a device the relay lists as `pwa`
  or `app` (until #55 that is the kind the device gave, as `update` reads it,
  [v1-update.md](v1-update.md#who-may-ask)), or on the local pipe from **the app** beside the host
  ([local-pipe.md](local-pipe.md), "The app"). Never to a channel inside an agent session, to
  anything else of the user's on the pipe, to a pinned peer (`host` or `service`), or to the Home
  Assistant connector.

Like every capability it is advisory: the op checks each of these again when it arrives, and
again before each session (below). A client that does not see it offers no button.

## `restore.run`

Request `data`:

```json
{
  "request": "6f0d2c1e-8a4b-4c3d-9e2f-1a2b3c4d5e6f",
  "offer": "2026-11-04T16:02:11.000Z",
  "sessions": ["0f3c…", "7a21…"],
  "progress": true
}
```

| Field | Notes |
|---|---|
| `request` | A fresh UUID per confirmed press, with exactly `session.answer`'s semantics, as `session.launch`'s: every outcome is remembered for at least 10 minutes and returned for a repeat, which starts nothing; a repeat of a run still under way waits for it (and hears its progress so far, as a repeated reply does). After an overall `busy` or `refused`, a new press uses a new `request` |
| `offer` | The `machine.restore.at` of the notice the confirmation was made from. It names this run's offer: a host that started again since has a new one, and the request is refused whole, "the offer has changed; look at it again", so nothing a person did not see is restored |
| `sessions` | The ids the confirmation listed, 1 to 20, in the offer's order, no repeats. Each must be in `machine.restore.offered` now; one that is not (resumed from Launch meanwhile, restored from another device, seen live again or let go) is answered `refused`, "no longer offered", and the rest go on |
| `progress` | Optional. `true` from a client that wants a frame as each session is tried (below) |

**Which sessions (recommended: all that are named, in one press).** A client sends every id the
notice names, which the confirmation lists; it has no per-session checkboxes. One decision, one
button and one confirmation keep a phone short; choosing among them is what Launch's resume list
is for, and every offered session is there too. The op takes a list rather than "all" so the host
restores exactly what the person saw, never one judged or added after they looked. Sessions the
notice counts but does not name (`offeredMore`) are not restored from here: the confirmation says
to resume them from Launch (open question P3).

**Which offers.** Those whose notice `why` is `off` (the setting is off) or `pending` (kept for a
person: a restore that was refused or failed, or died again too soon). Not `readOnly`: the
capability is not offered then, and the op is refused. A `why` a host does not know is not one it
sends. Whether a client shows the button for `pending` as well as `off` is open question P2; the
host takes both.

### What the host does

1. **The whole request first.** Refused, with nothing tried, when the caller is not a person's
   device (an agent on the pipe hears the pipe's usual refusal, naming its session), the machine
   is read-only (the reason, as `readOnly` says it), launching is off, the request is invalid,
   `offer` is not this run's, or the device has made 3 presses in the last 10 minutes (as
   `update`'s limit, [v1-update.md](v1-update.md#how-often)), and, as `RestoreAsync` refuses
   them, while the host is stopping or the app's Stop marker is set (#986). `busy`, with nothing
   tried, while another `restore.run` is under way on this machine (from any device), or while
   the restore record cannot take a stamp (`RestoreAsync`'s busy: a record that could not start,
   or a write that failed).
2. **Then each listed session, one at a time, newest first,** through the same path as a start-up
   restore and #800's: `ISessionRestorer.RestoreAsync` with `By` naming the device
   (`"pwa 1a2b3c4d"`, `"app"`), the offer's `cause` and `since`, and the entry's `midTurn` and
   `questionLost`. So every rule a restore has holds, unchanged, and nothing here adds a path:
   - stamped in the record **before** its launch (on its owed entry, or in `stamps` for one the
     record holds no entry of), so a session that dies with the host is known as restored and
     is not restored again;
   - the agent's `extraArgs` checked first (#837): one carrying a prompt flag, a positional token
     or anything off the agent's allowlist refuses that session, naming the option and never a
     value;
   - every check a resume from Launch meets: agent-ha-bridge and read-only looked at again,
     `launch.enabled`, discovery held (`busy`), the folder still approved, not already running or
     being restored, and `busy` while a folder-trust or Bypass Permissions question waits for a
     person;
   - the Stop marker looked at again with the pipe open before any agent is resumed (#988);
   - no prompt: the session opens **idle** and nothing is typed. Replies to it are refused while
     its window has not been seen ready (`LaunchHeld`), and it is marked `restored`, with
     Continue when it went mid-turn, only once it has been (#855);
   - its settings are what this host launched it with, each kept only while `launch.options`
     still offers it, and one started by hand asks permission, never Allow all.
3. **A `busy` stops the run.** The first session answered `busy` (a folder-trust question waiting,
   discovery held) ends it: every one after it is answered `busy` too, "not tried: *reason*", and
   stays offered. Launches would be `busy` for the same reason, and a press is never queued to run
   later on its own. `unconfirmed` (started, but its session had not registered, or a question
   waits in its window) does not stop the run unless it leaves launches `busy`, as a folder-trust
   question does for two minutes.
4. **Progress.** With `"progress": true` the host sends one frame as each session's attempt ends,
   before the response, as [Progress](v1-sessions.md#progress) frames are: `re`, a `progress`
   object and no `ok`. A run takes up to about a minute a session (a resume waits 60 s for its
   session to register), so a client waits for the response as long as frames keep coming, and
   resends the same `request` after 90 s with none.

```json
{"re": 9, "progress": {"session": "0f3c…", "outcome": "started", "done": 1, "of": 2}}
{"re": 9, "ok": true, "result": {"outcome": "partial", "sessions": [
  {"id": "0f3c…", "outcome": "started"},
  {"id": "7a21…", "outcome": "refused", "reason": "its folder is no longer approved at the machine"}]}}
```

### Result

`{ "outcome", "reason", "sessions": [{ "id", "outcome", "reason" }] }`.

Per session, `outcome` is `session.launch`'s ([v1-launch.md](v1-launch.md#sessionlaunch)), with its
meaning: `started` (registered and seen ready: it is restored), `unconfirmed` (started; check at the
machine; never retried), `failed` (tried, and the agent did not run), `busy` (nothing started; it
stays offered) or `refused` (nothing started; it stays offered, or was no longer offered). `reason`
is present for anything but `started`, in words a client shows as sent.

Overall `outcome`:

| `outcome` | When |
|---|---|
| `restored` | Every listed session `started` |
| `partial` | At least one `started` or `unconfirmed`, and at least one did not start |
| `none` | Each was tried, or answered, and none started |
| `busy` | Nothing tried: another run is under way, or the record cannot take a stamp. `reason` says which |
| `refused` | Nothing tried: the whole request was refused. `reason` says why |

A client reads an overall `outcome` it does not know as `partial`, and a session's as
`unconfirmed`: something may have started.

## What changes in the record

- **A session restored from a client is consumed from the offer,** as a resume from Launch
  consumes it today ("unless it is resumed or seen live again"): once its restore starts, it leaves
  `offered`, `restored` counts it, and `hello.changed` tells every channel, so another device's
  notice and confirmation shrink with it. One that dies again within 10 minutes is a loop, as any
  restore's: offered again, counted in `loops`, and not restored by the host again.
- **One that does not start stays offered** with the time it was first offered: a press neither
  renews nor shortens the 7 days an owed offer is kept (`OwedFor`), and the notice still goes 24
  hours after the host started, or with this run. A person can restore from a client only what the
  current notice offers; an owed offer still kept after the notice has gone is resumed from Launch,
  as today.
- **With `sessions.restoreOnStart` off,** what died is still not kept owed, and a later start still
  offers none of it: that is what off means. What changes is this run only: for as long as the
  notice offers them (this run, 24 hours at most), the host keeps the sessions it **said** (their
  entries and launch settings, from the record it read at start) so that `restore.run` can resume
  them with the settings they had, and keeps their folders from the worktree reclaimer for that
  time, as an owed offer's. Restored, one is stamped in `stamps` as a restore on request is, and
  leaves the offer.
- **With the setting off because `config.json` could not be read** (`RestoreKept`), what died is
  kept owed, as when it is on, and `restore.run` takes it as a `pending` offer.

## Logging

Each run is logged once, with the device, the offer and each session's outcome
(`restore: run by pwa 1a2b3c4d of 2 offered (2026-11-04T16:02:11Z): 0f3c… started, 7a21… refused
(its folder is no longer approved at the machine)`), and each refusal of the whole request at most
once a minute per device. Never a title, a prompt or a folder.

## Records

When built, the request and result are C# records beside `RestoreNotice`
(`src/AgentBridge.Protocol`), mirrored in the PWA's `protocol.js`, with vectors in
`vectors/sessions.json`, and they join v1-sessions.md's strict fields: `RestoreRunSession` by its
`id` (identity), so a result with an entry that cannot be read is refused whole rather than read
as a session that did nothing.

## Tests to write with the code

Host (`AgentBridge.Host.Core.Tests`, `AgentBridge.Host.Tests`):

- restore.run is offered to a person's device through the relay and to the app on the pipe, and
  to nothing else
- an agent on the pipe that sends restore.run is refused with its session named, and nothing is
  restored
- beside agent-ha-bridge restore.run is not offered, and one that arrives is refused with the
  read-only reason
- a run restores each listed session through the restore path, stamped before its launch, newest
  first
- a session restored from a client opens idle, with nothing typed and replies held until its window
  is seen ready
- an extra argument that carries a prompt refuses that session and the rest of the run goes on
- the app's Stop marker set while a run waits refuses what has not started and starts nothing more
- a busy session ends the run, and those after it are answered busy and stay offered
- a repeat of the same request returns the first outcome and restores nothing twice
- a second run from another device while one is under way is busy
- an offer from an earlier run of the host is refused whole
- an id no longer offered is refused and the others are restored
- a restored session leaves the offer and every channel is told with hello.changed
- a session that does not start keeps its place and its seven days in the offer
- with restoreOnStart off a run restores what this run said, with the settings it was launched with
- with restoreOnStart off what a run did not restore is not offered by the next start
- with restoreOnStart off the folders of what was said are kept from the reclaimer while the notice
  offers them
- a session restored from a client that dies within ten minutes is offered again as a loop and is
  not restored by the host
- a device that has pressed three times in ten minutes is refused
- progress frames say each session's outcome before the response

Protocol and PWA (`AgentBridge.Protocol.Tests`, `pwa/test/unit`, `pwa/test/*.spec.mjs`):

- a restore.run result whose session entry cannot be read is refused whole
- an unknown overall outcome reads as partial and an unknown session outcome as unconfirmed
- the notice offers Restore only with sessions.restore.run, an offer whose why is off, and a machine
  that is not read-only
- under agent-ha-bridge the notice says the sessions can be restored at the machine and offers no
  button
- the confirmation lists the offered titles cleaned and capped, and says how many more it leaves to
  Launch
- Restore sends every listed id with the notice's offer, and a lost response resends the same
  request
- while a run is under way the notice says how far it has got and the button cannot be pressed again
- a partial run says which sessions were restored and why each other one was not
- a refused run says why and leaves the offer as it was

Windows app (`AgentBridge.App.Tests`): the same notice states, the ContentDialog's focus starting on
Cancel, and the pipe run as the app.

## Additions to `machine.restore`

- `offered[].loop: true` on a session that is offered because it died again within 10 minutes of
  a restore (one of those `loops` counts), so a confirmation can say so before a person restores it
  once more. Absent otherwise; a client that does not know it ignores it.
- `restored` counts the sessions restored this run, at start or from a client.

Nothing else in the notice changes, and nothing is added to the `restored` push.

## Open questions

**Product**

1. P1, all named or a chosen subset? Recommended: all the notice names, in one press; picking is
   Launch's.
2. P2, the button for `why: pending` too (a restore that was refused or failed, or a loop), or only
   for `off` as #790 decided? The host takes both; recommended: both, with loops marked in the
   confirmation.
3. P3, `offeredMore` (past 20, or left out for size) is not restored from here; the confirmation
   says to resume them from Launch. Acceptable?
4. P4, with the setting off, an offer lasts this run and 24 hours at most, and a host restart
   forgets it (off keeps nothing across starts). Acceptable, or should off keep the offer owed for
   7 days like `pending`?
5. P5, a run ends at the first `busy` rather than waiting and trying again on its own. Acceptable?

**Creative**

1. C1, the words in ui.md §6.12's proposed Copy rows: the button, the confirmation's title, body,
   list lines and buttons, the running line and the outcome lines.
2. C2, the confirmation lists at most 5 titles, then "and *m* more". Is 5 right on a phone?
3. C3, focus starts on Cancel, since this acts on another machine. Agreed?

**Trust**

1. T1, who may run it: a device the relay lists as `pwa` or `app`, and the app on the pipe. The kind
   is self-asserted until #55, as for `update`. What something holding the account's token could
   then do is resume, idle and with the settings they had, sessions that ran on that machine in the
   last 24 hours, which `session.launch` with `resume` already lets such a device do one at a time.
   Is that acceptable, with 3 presses per device in 10 minutes?
2. T2, the op adds no path: it calls the start-up restore's own (`RestoreAsync`), with its stamps,
   extra-argument check, Stop marker and `LaunchHeld`. Is there a check a person's press should meet
   that a start-up restore does not?
3. T3, it lets a remote device act on a machine, so its PR is sensitive and takes the independent
   review (and, under `copilot-waived`, the second family's).

**Engineering**

1. E1, slices: (1) host and protocol: the op, the capability, the record changes, vectors; (2) PWA;
   (3) Windows app. Each after this spec is accepted.
2. E2, the long run: progress frames and a 90-second resend rather than a new event.

## Not in this

- **Folder trust (#947).** A restore that meets the question is `unconfirmed` and ends a run as
  `busy` for those after it; the notice says so in the host's words. Answering it stays a person's
  decision at the machine, or through `launch.trust.answer` where the machine allows it.
- **Restoring automatically from a client.** No client presses Restore on start, on reconnect or on
  a timer, as for Continue.
- **Home Assistant.** Read only, as today: no entity, notification action or payload runs this.
