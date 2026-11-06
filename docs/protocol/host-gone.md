# Proposed relay extension: a machine whose host is gone (#970)

**Spec for review, 2026-11-06 PT. Not implemented.** Owner: Daily, with SRE for detection
(Product, [#689 6689863886](https://github.com/danswett/agent-bridge/issues/689#issuecomment-6689863886)).
Product's design is binding ([#800 6676984805](https://github.com/danswett/agent-bridge/issues/800#issuecomment-6676984805),
[ORDER CHANGE 6](https://github.com/danswett/agent-bridge/issues/696#issuecomment-6689818401)):
the relay notices, the person sees "*machine*'s bridge isn't running · since *time*", and one
push goes, at the Needs you tier, only if a session there was working or waiting when the host
went. The words and screens are [ui.md §6.14](../design/ui.md#614-a-machine-whose-bridge-isnt-running-970);
the push is [v1-sessions.md, `hostGone`](v1-sessions.md#host-gone-970).

This builds on #805's draft contract ([#815](https://github.com/danswett/agent-bridge/pull/815),
`docs/protocol/host-liveness.md` there): its `host-planned-state` announcement, its relay
offline observation (`presence.known[].offline`), its lifecycle store and its consent-bound
pre-sealed notices. It adds no second planned-state message, store or push path. Where #815
changes, this follows it. Nothing here restarts a host: healing is #805's
([#989](https://github.com/danswett/agent-bridge/pull/989) and its watchdog), and the remedy a
person presses is #801's remote restart.

## Who notices, and why the relay

Pushes, toasts and Home Assistant notices all come from the host, so a host that has stopped
can't say it needs the person. The Windows app can't either when it isn't running, and when it
is running it no longer restarts a dead host (#923's automatic start was removed; F2 only
reconnects to a live one, [#800 6676990317](https://github.com/danswett/agent-bridge/issues/800#issuecomment-6676990317)).
The relay is the only party that outlives the host, so the relay decides. When the machine's
app is running, it adds what only it can see (the [witness](#the-apps-witness-slices-1a-and-2)).

## What the relay can and can't see

The relay sees routing ids, sizes, timing and how each socket ended. It never sees what a
session says: every frame between a host and a device is Noise ciphertext, and every push is
encrypted by the host to the browser's own key (RFC 8291). So:

* **It can't read session state from what it relayed.** Its last relayed frames are opaque, and
  guessing "busy" from their size or rate would be inventing agent state. Never.
* **It can't compose a push.** `PushGateway` only signs and forwards a body a host already
  encrypted; it holds no browser keys.

Both are answered the same way: **the host tells the relay in advance.** While any agent on
it (a delegate included) is working or waiting, the host keeps a sealed notice armed with the relay, which the relay
releases only if the host goes and doesn't come back ([Arming](#arming-the-notice-slice-3)).
The relay never learns which sessions, how many, or what they were doing: it learns one fact,
that the host said "if I vanish now, tell them", and when it said so.

## How a host's socket ended

The relay records, for the host's current connection only (#815's generation rule: a replaced
socket never writes over its replacement), one of:

| `outcome` | What the relay observed | Today in `RelayHub` |
|---|---|---|
| `closed` | The host sent a WebSocket close frame (normally 1000 or 1001) | `closed` |
| `reset` | The host's side of the TCP connection closed or reset without a close frame (`WebSocketError.ConnectionClosedPrematurely`, or a reset socket): the machine's network stack is up and the process holding the socket is gone | `dropped` |
| `timeout` | No pong within `KeepAliveTimeout` (30 s after a 20 s ping, `RelayApp`): the machine went silent (off, asleep, network gone, or the host hung) | `dropped` |
| relay-side | The relay closed it: refusal, rate or size limit (4008, 4013, 4029), replaced (4009), or draining (1012) | as now |

This splits #815's `dropped` into `reset` and `timeout`, which the relay can already tell apart
from the exception its receive throws. Older clients read an outcome they don't know as lost
connection, as #815 specifies; the vectors test it (v1-sessions.md,
[Test vectors](v1-sessions.md#test-vectors)).

### Through the ingress: checked on staging before slice 1

The relay never sees the host's own TCP connection: it sees Azure Container Apps' Envoy
ingress's upstream connection ([relay-deploy.md](../relay-deploy.md)). So "`reset` means the
machine is up and the process is gone" is an inference through a proxy, and **slice 1 isn't
built on it until staging shows it holds** (Connection's condition, #1022 6691184855). With a
disposable host on the staging relay, each case run several times:

1. **Kill the host process** (the machine stays up and online): reads as `reset`.
2. **Pull the machine's network, and separately suspend the machine:** reads as `timeout`, never
   `reset`.
3. **Restart the ingress, or roll a new revision:** every socket ends at once, and the
   mass-end guard below classifies them all as relay-side.

**Mass-end guard.** When many of the relay's host sockets end within a few seconds of each
other (the threshold is set from case 3; for example, at least 5 hosts and a quarter of those
connected, within 5 s), the relay classifies every one of those departures as relay-side
(row 1), not as the hosts going. One host never ends with its own process in step with dozens of
others, and the cost of the guard is that a real crash in that same moment is reported as
offline only.

**If case 2 ever reads as `reset`,** `reset` stops being evidence: row 6 then takes only a
witness (`not-running` or `not-responding`) for a `reset` too, and the line can appear only
on a machine whose app is running. The results go on #970 with the relay image they ran on.

## Classifying a departure

First match wins. Only row 6 can ever become "host gone".

| # | Departure | Treatment |
|---|---|---|
| 1 | Relay-side end (4008, 4009, 4013, 4029, 1012), or one of many host sockets ending together (the [mass-end guard](#through-the-ingress-checked-on-staging-before-slice-1)) | Not a departure of the host. Nothing for #970 |
| 2 | A valid #815 plan was received before it (`host-planned-state`, `phase: planned`, within its `departBy`): `update`, `restart`, `stop`, `sleep`, `shutdown`, `reboot` or `sign-out` | **Deliberate.** #805's planned copy, and #805's no-return push if a reboot, restart or update doesn't come back. Never "host gone", never #970's push |
| 3 | The machine's app, declared by the host for the current episode, witnesses `stopped` or `updating` | **Deliberate,** seen locally. `stopped` as a `stop` plan (no return timer); `updating` exactly as an `update` plan, with #805's return timer and no-return push, so an update that never comes back is still told |
| 4 | `closed` from a host that doesn't advertise `host-gone-v1` | **Unknown.** An older host closes the same way for a deliberate local stop as for anything else. Today's offline |
| 5 | `timeout` with no witness | **Unknown.** The relay can't tell a machine switched off, asleep or offline from a host that hung. Today's offline (#815's "Lost connection") |
| 6 | `closed` or `reset` from a host that advertises `host-gone-v1` (it announces every deliberate departure, so leaving without a plan isn't one; `reset` only while the staging check above holds); or a witness `not-running` or `not-responding` (slice 2) about a host that advertises it | **Unplanned, with evidence the bridge stopped while the machine is up.** Pending, then host gone ([The window](#the-window-805-goes-first)) |
| 7 | `reset` from a host that doesn't advertise `host-gone-v1` | **Unknown,** as row 4: an older host's deliberate stop can end in a kill. Today's offline |

`host-gone-v1` is a relay `hello` capability, in #815's `capabilities` list. A host advertises
it only in a build where every path that stops it on purpose tells the relay first, so that a
departure without a plan means it wasn't meant, and each path has a test that shows it
(Connection's condition):

* #801's remote stop and restart, and #819's and the person's updates, announce through
  #815's `host-planned-state` (`stop`, `restart`, `update`) before they commit. Tests: "A remote
  stop announces stop before the host leaves." "A remote restart announces restart before the
  host leaves."
* **The app's Stop** writes the stop marker and asks the host to stop, which announces `stop`;
  and, before its fallback that kills a host too slow to stop (`HostManager`), the app sends
  its own witness `stopped` (slice 1a). A host killed so is deliberate, not gone. Test: "The
  app's Stop announces stop, and witnesses stopped before it kills a slow host."
* **The app's update hook** sends witness `updating` before it hands a host over to the package
  (including a host it doesn't ask to stop first, because its usage probes are busy). Test:
  "The app's update hook witnesses updating before the package replaces the host."
* **`agent-bridge-host stop`** writes the stop marker too (today only `HostManager.MarkStopped`
  does), and the host it stops announces `stop`. Test: "agent-bridge-host stop writes the stop
  marker and the host announces stop."

**Accepted false positives,** each true from the person's side (the bridge isn't running):

* OS sleep, shutdown, reboot and sign-out are best effort in #815, so a machine shut down
  without its announcement can read as row 6 with `reset`.
* **A Velopack apply run outside the app** (#238, #974): `Update.exe` run by hand, a setup run
  over the installed app, or an uninstall force-stops the host with no announcement and no
  witness. A reinstall that comes back within the 5 minutes is never shown; an uninstall reads
  as "isn't running", which is true.
* Not a deliberate Stop: Product doesn't accept that one (6691661234). Every stop path
  announces first and the app witnesses `stopped` before it kills a slow host, both in slice 1a,
  before the line exists ([Slices](#slices)).

## The window: #805 goes first

A departure in row 6 is **pending** for **5 minutes**, from the relay's observed departure
time. Nothing shows and nothing is sent while it is pending: clients keep #269's "Reconnecting…"
and "Offline · last seen" meanwhile. 5 minutes is longer than every way #805 heals a host
without a person:

| #805 heals | Signal | Worst case |
|---|---|---|
| #106: the launcher restarts a host that exited | `reset`, then the host is back | 3, 6 and 12 s back-off, 3 retries, each with a start-up: about 1.5 min |
| #805's watchdog (proposed): a hung host is replaced | `timeout` (or witness `not-responding`), then the host is back | 60 s to suspect, 10 s to exit, a start-up with 120 s grace: about 3.5 min; SRE commits to 4 min or less |

A committed departure that never exits (#989) isn't here: it announced its plan before it
committed, so it is a planned departure (row 2), #805's and not #970's, whose no-return push
covers it if it doesn't come back.

The host coming back and staying connected for 2 minutes cancels the window: it isn't gone,
whatever happened (a shorter return continues the episode; see Flapping). **The timings stand**
(SRE, #1022 6691373792): 5 minutes, the 2-minute flap and the 15-minute `restarting` cap. The
relay keeps the 5 minutes as one constant, `HostGoneAfter`, never below #805's longest
self-restart, and any change to it, or to #805's watchdog past 4 minutes, goes through that
constant.

**#805's return timer counts from the socket's end** (SRE): a planned restart's 60 s and an
update's 90 s start when the host's connection ends, not when it announced, since #989's
2-minute bound and a slow stop both pass while the host is still connected.

* **Still trying.** While the app witnesses `restarting` (the launcher is alive and waiting to
  start its host again, or the host's own restart is under way), the window doesn't end. It
  ends at most 15 minutes after the departure even so: #106 and #805 both give up long before.
* **Flapping.** A host that comes back and goes again within 2 minutes continues the same
  episode: the window still counts from the episode's first departure, and "since" is that
  departure's time. So a crash loop is told about in 5 minutes, not never. A host that stays
  connected for 2 minutes ends the episode. A machine whose network flaps has `timeout`
  departures, which never become host gone.
* **A relay restart** (1012, deploy or drain) isn't a departure. A host that doesn't return
  within 3 minutes of the relay starting has no observed departure: its state is unknown, not
  gone, and the relay never invents a departure time from `lastSeen` (#815). A pending window
  persisted before the restart resumes from its stored start.

When the window ends with the host still away and the evidence still row 6, the host is
**gone**: the relay sets `gone` in presence and, if the episode's arm says work was in flight,
sends its notices once and marks the episode `alert`.

## `presence`: `gone`

In #815's `offline` object on a `known` entry:

```json
{
  "id": "0123456789abcdef0123456789abcdef",
  "name": "DEV-VM2",
  "kind": "host",
  "lastSeen": "2026-11-06T16:35:52Z",
  "offline": {
    "v": 1,
    "since": "2026-11-06T16:35:52Z",
    "outcome": "reset",
    "gone": { "v": 1, "since": "2026-11-06T16:31:07Z", "evidence": "reset", "alert": true }
  }
}
```

| Field | Meaning |
|---|---|
| `gone.since` | The episode's first departure, as the relay observed it (UTC). What "since *time*" shows. Earlier than `lastSeen` when the host flapped (above) |
| `gone.evidence` | `closed`, `reset` or `witness`: why the relay believes the bridge stopped. Not shown; for diagnostics and tests |
| `gone.alert` | `true` when the episode's arm said an agent was working or waiting and the caps allowed an alert, whether or not any Web Push notice was armed. The relay sends the notices it holds; a client that gets no Web Push (the Windows app) raises its one toast from this ([ui.md §6.14](../design/ui.md#614-a-machine-whose-bridge-isnt-running-970)), so an account with only Windows apps is told too |

`gone` is absent while pending, for rows 1–5 and 7, and the moment the host is listed online again.
A client that doesn't know `gone` shows today's offline machine. It goes when the host returns,
is forgotten, or the account is signed out or changed. It is not dropped at #815's 24-hour
detail limit: its only extra fact over `lastSeen`, which the roster already keeps, is that the
bridge stopped, so "since" stays true (agreed by Connection, #1022 6691184855).

## Arming the notice (slice 3)

A relay text control from the host, alongside #815's `host-planned-state`:

```json
{ "t": "host-gone-arm", "v": 1, "seq": 7, "armed": true, "notices": [ { "target": "…", "body": "…", "ttl": 21600, "urgency": "high", "topic": "…" } ] }
```

| Field | Contract |
|---|---|
| `seq` | Strictly increasing within the connection. The relay keeps only the newest; an older one is ignored |
| `armed` | `true`: an agent in a session is working or waiting (below); alert if this host becomes gone, and send these notices. `false`: nothing in flight; `notices` absent. An arm with no notices (a host no browser subscribes to) still counts: it is what `gone.alert` reads |
| `notices` | One per subscribed device, at most 8: #815's envelope exactly. `target` is the device's consent handle from #815's target registration, never a free-form endpoint; `body` is RFC 8291 ciphertext the host sealed to that device's subscription, at most `WebPushCrypto.RecordSize` bytes decoded; `topic` is valid `PushTopic` syntax |

Result: `{ "t": "host-gone-arm-result", "v": 1, "seq": 7, "accepted": true, "noticesArmed": true }`,
or `accepted: false` with #815's closed `error` set. Arming is never a command and never
authority: it only lets the relay send a notice the host already wrote.

**When a host is armed.** Exactly while any agent in any session it reports is working or
waiting, delegates included (Product, #1022 6691211514: losing many delegates silently is the
case that matters). From v1-sessions.md's fields, a session counts when:

* its `status` is `working` or `waiting` (the main agent is working, or a question, an approval
  or a form waits on the person), or it is waiting on a delegate's question (`waitingOn`);
* any of its `subagents` has `status` `working` or `waiting`; or
* `background.agents` is above 0: delegates running in the background, including any the list
  doesn't name (§2.2 rule 8).

Background commands alone (§2.2 rule 5), an idle session with nothing running under it,
`ending`, and a session that needs sign-in don't count. The host:

* arms within 5 s of the first session starting to count, and disarms within 5 s of the last
  one stopping, always: a disarm is never held back, so the host is never armed more
  than 5 s after the work stopped;
* re-seals when its subscriptions change (the body says nothing that changes with the
  sessions, so the notice needn't be rewritten as they come and go), and at least every
  3 hours while it stays armed, inside the arm's 6-hour expiry; it coalesces arming and
  re-sealing (never disarming) to at most one change every 10 s, the trailing state winning;
* sends its arm state, armed or not, within 5 s of each new relay connection, but a host that
  has just started sends it only once it has read every session it finds (its first full list):
  a host still starting never disarms the arm its episode began with;
* never stays armed while a planned departure (#815) is committed: a host that announced its
  departure disarms in the same breath.

**What the relay does with it.** It keeps the newest arm in #815's lifecycle store (so a relay
restart doesn't lose it), for the **episode**, not the connection: a new connection replaces
the arm only when it sends its own `host-gone-arm`. So a host in a start-up crash loop, which
dies again before it re-arms, keeps the arm it had when the episode began, and the person is
still told. **An arm expires at its notices' `ttl`, 21600 s (6 h) after the relay received
it,** notices and all, and `gone.alert` ignores an expired arm: a host that finished its work
cleanly, restarted much later and crashed before its first full list must not alert from an
arm hours old. (A host that stays armed re-sends its arm, freshly sealed, at least every 3
hours, so a long piece of work never lapses.) When the host becomes gone with an unexpired
`armed: true` standing, the relay sets
`gone.alert` and claims and sends each notice once through `PushGateway`, under #815's caps:
one host-gone alert per machine an hour, three per account in 24 hours, shared with #805's
no-return pushes (SRE: to the person they are the same alert). An alert is an episode, not a
device: it counts once when at least one of its notices was sent or `gone.alert` was set (an
account with only Windows apps counts too), and a sent #805 no-return push counts once; one the
caps suppressed never counts. It deletes notices once sent (an episode alerts once), when a newer arm
replaces them, on forget, and on expiry. A failed or uncertain delivery isn't retried.

**When the host comes back, and when the episode ends.** One notice on return, never two
(Creative, #1022 6691202710, 6691268328); the mechanism is Connection's (6691258807), by tag:

1. **As soon as the host reconnects,** not after 2 minutes, the relay tells it which devices
   were sent the alert:
   `{ "t": "host-gone-sent", "v": 1, "since": "…", "targets": ["…"], "tag": "<machine id>:host", "topic": "…" }`.
   The relay sends it on every reconnect of a host that advertises `host-gone-v1`, with empty
   `targets` (and no `since`, `tag` or `topic`) when its episode wasn't alerted, so the host
   can tell "nothing was sent" from "not told yet". Until it has it (at most 10 s; past that, as
   empty), the host holds any `restored` push, including one queued while its relay link was
   down, so the replacement below can't be overtaken.
2. **If the return brings #780's `restored` notice,** whatever its cause (a crash, or a reboot
   whose announcement didn't get out), the host sends it to each device in `targets` on the
   **gone** notice's tag and topic, so it replaces the alert in place; every other device gets it
   on `<machine id>:~restore`, as today. No `hostBack` follows in this episode. So "You can reach
   its sessions again" is never shown when the bridge's going took sessions. A `restored` decided
   after the episode ended (#780 judges for up to 10 minutes) still goes on the gone tag to
   `targets`. The **relay** keeps `targets` until the host confirms that a `restored` or a
   `hostBack` went (`{ "t": "host-gone-closed", "v": 1, "since": "…" }`), or 6 hours after the
   episode began, whichever is first, and sends them again in `host-gone-sent` on every
   reconnect meanwhile. So a host that dies between the episode's end and its restore check
   still replaces the alert when it comes back.
   **It alerts** as a Needs you notice does today, with the question sound (`silent` absent),
   never silently, because the person still has something to answer (Creative, 6691268328).
3. **Otherwise,** once the host has been back for 2 minutes (the episode ends) **and** #780 has
   finished judging with nothing to restore, it sends those devices a silent `hostBack` itself,
   live, on the same tag and topic, which replaces the alert in place (v1-sessions.md). Waiting
   for both means a host that dies again straight away never leaves a phone saying "running
   again", and a `restored` never follows a `hostBack`.
4. **Withdrawing a `restored` notice** uses whichever tag it went on: the host remembers that tag
   per device until the notice is withdrawn. Once #780's withdrawal has taken it away, nothing
   about the gone alert comes back: the alert was replaced, not stacked, so withdrawing the
   restore notice leaves that tag empty, and neither the relay nor the host sends the gone alert
   or a `hostBack` for that episode again (Creative, 6691268328). #780 defines no withdrawal of
   its push today (ui.md §6.12); whenever it does, it follows this rule.
5. **The Windows app** toasts at most once per episode (per `gone.since`, which a flap doesn't
   change), however often `gone` clears and is set again. It removes the toast once the host has
   been listed online for 2 minutes (the episode's end), or replaces it with the restore toast
   if the return brings one, so that is the only notice left.
The relay sends nothing on its own when the host returns: it holds no sealed "back" notice.

## The app's witness (slices 1a and 2)

When the machine's Windows app is running, it knows what the relay can't: whether the host
process is there at all, whether the launcher is still trying, and whether it was stopped on
purpose. It reads them from the same signals #805 and #989 use. Slice 1a ships the binding and
the two deliberate states, `stopped` and `updating`; slice 2 the rest:

| `state` | Read from |
|---|---|
| `running` | The host process is there and answers its local pipe |
| `restarting` | The launcher (`agent-bridge-hostw`) is alive without its host: #106's back-off, or a restart under way (`host.restart`, exit 75, #989's departure-stuck restart) |
| `stopped` | The profile's stop marker (`host.stopped`, `HostLauncherContract.StopMarker`) is there |
| `updating` | The app is applying an update, or handing the host over to one |
| `not-responding` | The host process is there but hasn't answered its pipe for 60 s (#805's suspect threshold) |
| `not-running` | None of the above: no host, no launcher trying, no stop marker, no update. A launcher that has given up (#106's retries spent, #805's cap reached) reads so: the launcher gets no relay identity of its own (SRE: a credential surface for one bit) |

```json
{ "t": "host-witness", "v": 1, "host": "0123456789abcdef0123456789abcdef", "state": "not-running" }
```

The app sends one when its local host's state changes and when it connects, at most one a
minute otherwise, and sends `stopped` as it stops its host (before any fallback that kills a
host too slow to stop) and `updating` before it hands a host over to an update, so the relay
has them before the socket ends. The relay takes it only from the app device the host itself last declared as its own
(`{ "t": "host-local-app", "v": 1, "app": "<device id>" }`, sent by the host on each
connection), in the same account, and only about that host. The binding's conditions
(Connection, #1022 6691184855):

* **The host learns the app's device id only from the app's own authenticated local-pipe
  connection,** behind the caller guard that refuses agents and their descendants (#67), and
  never from a file, the environment or anything else an agent could write.
* **The relay drops the binding when that app device leaves the roster** (forgotten, or the
  account signed out or changed), and when the host declares another.
* **`stopped` and `updating` count only from the device declared for the current episode,**
  the one the host declared on the connection the episode began from. A forged one would hide a
  real "gone", so a declaration made after the episode began (by a host that came back briefly)
  doesn't apply to it.

A witness never makes an online host offline. `running` cancels a pending window
(the host is alive and only its relay link is down: "Reconnecting…", not gone); `stopped` and
`updating` make the departure deliberate (row 3: `updating` as an `update` plan, with #805's
return timer); `restarting` holds the window; `not-running` and
`not-responding` are evidence, and turn a `timeout` into row 6.

## Privacy and retention

This adds plaintext metadata to the relay, approved by Connection with the conditions below
(#1022 6691184855):

* **The arm bit and when it changes:** when a machine has an agent working or waiting. The
  relay already sees traffic timing that suggests as much; this makes it explicit, for the
  account's own hosts only. No session id, count, title, agent or state is in it. Acceptable
  only because it stays within the account and is never kept as a series.
* **The witness state** of a host, from its own app.
* **`gone` in presence,** to the account's own devices only.

**Retention.** Per host, the relay keeps only the **current** value of each of these,
overwritten in place, with no history:

* the arm and its notices: expire at 21600 s; notices deleted once sent;
* the witness state, and the app device the host declared (for the current episode, and the
  latest): dropped when that app leaves the roster or the host declares another;
* the episode's start and `gone`: `gone` lasts until the host returns or is forgotten;
* the episode's sent `targets` (handles only): kept until the host confirms its `restored` or
  `hostBack` went (`host-gone-closed`), or 6 hours after the episode began, then deleted;
* the alert caps' counters (one an hour per machine, three a day per account), in #815's store
  and shared with its no-return notice: a count per window, each expiring with its window (at
  most 24 h), no other time kept.

No time is kept beyond the current episode's start and the caps' windows. All of it is deleted
on forget, sign-out and account change. [relay-deploy.md](../relay-deploy.md#host-gone-state-970-proposed-not-built)
records it beside the roster's retention.

None of it is logged, put in a metric label or exposed outside the account. Notices follow
#815: no endpoint, push body, target handle or socket tag in logs; errors reduced to fixed
categories. Metrics: `relay.host.gone` (counter, `evidence`, `alert`),
`relay.host.gone.cancelled` (counter, `by=returned|planned|witness`) and
`relay.host.mass_end` (counter, the mass-end guard firing), with fixed labels only and no
identities. SRE adds `relay.host.departures` (counter, by `outcome`: `closed`, `reset`, `timeout`,
relay-side) and `relay.host.gone.short` (counter, gone episodes that end within 10 minutes): the
false-alarm signal to watch from slice 1b until slice 3 pushes.

## Who does what

| | #805 (SRE): heal | #970 (Daily): tell |
|---|---|---|
| Planned departure (#815 plan) | Announces; shows the planned state; pushes if a reboot, restart or update doesn't return | Nothing |
| A host that exits (crash) | #106 restarts it | Waits 5 min; if it isn't back, gone |
| A host stuck after committing a departure (planned: it announced) | #989 restarts it after 2 min | Nothing; #805's no-return push if it doesn't come back |
| A hung host | #805's watchdog replaces it, or holds off (a delivery in flight) | Waits; gone if it doesn't come back (with a witness, `not-responding`) |
| A deliberate stop | Stop marker; the host announces `stop` | Nothing |
| A person wants it back | #801's remote restart (the remedy; not built yet) | Shows the state; offers #801's Restart once it exists. Never "go to the machine" |

One departure never gets both pushes: a departure with a plan is #805's, one without is #970's.

## Slices

In this order, confirmed by Engineering (#1022 6691640819). **A deliberate stop never reads
"Bridge isn't running"**, not even before the full witness (Product, 6691661234): slice 1's stop
paths announce first, so slice 1 is split, and the line can't land before the announcements.

**Before slice 1 starts:** #989 landed, #815 accepted, and Connection's three staging checks
through the ingress run, with their results on #970
([Through the ingress](#through-the-ingress-checked-on-staging-before-slice-1)).

- **1a. Every deliberate stop announces first, and lands first.** The host announces `stop`,
  `restart` or `update` through #815's `host-planned-state` before it leaves, on every path:
  the app's Stop, the app's update hook, #801's stop and restart (when #801 lands; until then
  it has no path to cover), and `agent-bridge-host stop`, which also writes the stop marker. The
  app sends the two **deliberate** witness states, `stopped` before any fallback that kills a
  host too slow to stop and `updating` before it hands a host to an update, with the
  `host-local-app` binding under Connection's conditions. One test per path
  ([`host-gone-v1`](#classifying-a-departure)). A host on a 1a build advertises `host-gone-v1`;
  nothing else does.
- **1b. The relay line,** after 1a has landed: the `reset`/`timeout` split, the **mass-end guard**
  and **"a relay restart is never read as the host going"** (both ship here, not later),
  classification, the window, `gone` in presence, the `presence` vectors (v1-sessions.md, Test
  vectors), the departure and short-episode counters, and both clients' line (ui.md §6.14).
  Only a host advertising `host-gone-v1` can be shown as gone, so every host the line can name
  announces its deliberate stops. No push; a `timeout` stays "Offline".
- **2. The rest of the witness:** `running`, `restarting`, `not-running` and `not-responding`,
  read from the app's local signals (the launcher, the pipe).
- **3. The push:** `host-gone-arm`, the relay's dispatch, `gone.alert`, `host-gone-sent` and
  `host-gone-closed`, the host's `hostBack` and its `restored` on the gone tag; the Windows
  app's toast from `gone.alert`; the built strings that say "bridge" for #780's `restored` (ui.md
  §6.12). Needs #815's target registration **merged**, not just accepted.

**Reviews** (Engineering): every slice gets an independent review from two model families at
full depth. Slices that decide who may send a witness or hold sealed notices (1a and 2, the
witness; 3, sealed notices, target handles and caps) also get a security review from both
families.

## Tests (to write with the code)

**Detection**
* "A host that crashes and is restarted within five minutes is never shown as gone."
* "A host still away five minutes after a reset is shown as gone, since its departure."
* "A host that announced an update or a stop is never shown as gone."
* "A committed departure that never exits is planned, and is never shown as gone."
* "A planned restart's return timer starts when its socket ends, not when it announced."
* "A machine that times out without a witness is offline, not gone."
* "Many hosts ending within a few seconds are read as relay-side, not as hosts going."
* "A host that flaps within two minutes keeps its first departure as since."
* "A replaced socket's departure never makes its replacement gone."
* "A relay restart is never read as the host going."
* "An older client reads reset, timeout and an unknown outcome as lost connection."

**Deliberate stops (one per path, before a host advertises `host-gone-v1`)**
* "A remote stop announces stop before the host leaves."
* "A remote restart announces restart before the host leaves."
* "The app's Stop announces stop, and witnesses stopped before it kills a slow host."
* "The app's update hook witnesses updating before the package replaces the host."
* "agent-bridge-host stop writes the stop marker and the host announces stop."
* "A host that doesn't advertise host-gone-v1 is never shown as gone."
* "An update witnessed by the app that never comes back gets #805's no-return notice, not #970's."

**The witness**
* "An app that witnesses its host restarting holds the window until it ends or fifteen minutes pass."
* "An app that witnesses its host running cancels a pending window."
* "A witness from an app the host didn't declare is ignored."
* "The host takes the app's device id only from the guarded pipe, never from an agent."
* "The relay drops a witness binding when that app leaves the roster."
* "A stopped or updating witness counts only from the app declared for the current episode."
* "An app whose launcher has given up witnesses not-running."

**Arming and the push**
* "A gone host with nothing armed sends no push."
* "A gone host that was armed sends each notice once, however many relays or restarts."
* "An arm counts while any delegate in a session is working or waiting."
* "A host that dies again before it re-arms keeps the arm its episode began with."
* "A host still starting never disarms before its first full list."
* "An arm older than its six-hour ttl never alerts: a host that finished cleanly, restarted and crashed before its first full list sends nothing."
* "An armed host with no browser subscribed still sets alert, so the Windows app toasts."
* "A disarm is sent within five seconds, whatever the coalescing."
* "The caps, shared with #805's no-return push, count one per alerted episode, not per device, and never a suppressed one."

**Return**
* "A crash return replaces the gone alert with the restored notice in place, and sends no hostBack."
* "A return with nothing to restore sends hostBack after two minutes."
* "A device the gone alert didn't reach gets the restored notice on ~restore."
* "A restored notice that replaces the gone alert alerts as Needs you, never silently."
* "Once the restored notice on the gone tag is withdrawn, nothing about the gone alert comes back."
* "A restored notice queued while the relay link was down waits for host-gone-sent, then replaces the alert."
* "A reboot restored notice in an alerted episode replaces the alert too."
* "hostBack waits until restore judging has found nothing, and a restored never follows a hostBack."
* "The Windows app removes its gone toast when the host is back, leaving only a restore toast."
* "The Windows app toasts once per episode, however often a flapping host clears and sets gone."
* "A host that restarts after the episode ended but before restore judging finished still replaces the alert."
* "A restore after the bridge's crash says bridge, never host, in its row mark, banner title and push title."

**Retention**
* "Forget, sign-out and an account change delete a host's arm, witness and gone."
* "No arm, witness, target or episode time reaches a log or a metric label."

## Review

- **Connection** (#1022 6691184855, 6691258807): approved with conditions, all folded in: the
  staging checks and mass-end guard, retention, `gone` until return or forget, the arm's expiry,
  the witness binding, `host-gone-v1`'s per-path tests and the Velopack false positive, the
  `presence` vectors, and the return mechanism.
- **Product** (6691211514): matches the design. A `timeout` doesn't push in slice 1; delegates
  count as work in flight; the banner shows under All; a gone machine stays out of Needs you's N
  and the badge; the row drops the name; the shutdown false positive is accepted.
- **Creative** (6691202710, 6691268328): words accepted, with one notice on return and
  "bridge" for #780's copy; Connection's return mechanism accepted, with its two requirements
  (the replacing restore notice alerts; nothing comes back after its withdrawal). All folded in.
- **SRE** (6691373792): the hand-off is right. The timings stand (#805's watchdog kept at
  4 minutes or less; `HostGoneAfter` the one constant); a stuck committed departure is planned
  and #805's; #805's return timer counts from the socket's end; caps shared, counting pushes
  sent; no relay identity for the launcher; the `dropped` split, subject to the staging check,
  with departure and short-episode counters.
- **Engineering** (6691640819): the slicing is confirmed, with its prerequisites; the guard and
  the relay-restart rule ship in slice 1; slice 3 needs #815's target registration merged; two
  model families review every slice, with a security review from both for the witness and the
  push.
- **Product** (6691661234): the deliberate-stop gap is closed, not accepted: slice 1 is split so
  every stop announces first (1a) before the line (1b).