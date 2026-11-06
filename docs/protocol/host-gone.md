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
app is running, it adds what only it can see (the [witness](#the-apps-witness-slice-2)).

## What the relay can and can't see

The relay sees routing ids, sizes, timing and how each socket ended. It never sees what a
session says: every frame between a host and a device is Noise ciphertext, and every push is
encrypted by the host to the browser's own key (RFC 8291). So:

* **It can't read session state from what it relayed.** Its last relayed frames are opaque, and
  guessing "busy" from their size or rate would be inventing agent state. Never.
* **It can't compose a push.** `PushGateway` only signs and forwards a body a host already
  encrypted; it holds no browser keys.

Both are answered the same way: **the host tells the relay in advance.** While any session on
it is working or waiting, the host keeps a sealed notice armed with the relay, which the relay
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
connection, as #815 specifies.

## Classifying a departure

First match wins. Only row 6 can ever become "host gone".

| # | Departure | Treatment |
|---|---|---|
| 1 | Relay-side end (4008, 4009, 4013, 4029, 1012) | Not a departure of the host. Nothing for #970 |
| 2 | A valid #815 plan was received before it (`host-planned-state`, `phase: planned`, within its `departBy`): `update`, `restart`, `stop`, `sleep`, `shutdown`, `reboot` or `sign-out` | **Deliberate.** #805's planned copy, and #805's no-return push if a reboot, restart or update doesn't come back. Never "host gone", never #970's push |
| 3 | The machine's app witnesses `stopped` or `updating` | **Deliberate,** seen locally. `stopped` as a `stop` plan (no return timer); `updating` exactly as an `update` plan, with #805's return timer and no-return push, so an update that never comes back is still told |
| 4 | `closed` from a host that doesn't advertise `host-gone-v1` | **Unknown.** An older host closes the same way for a deliberate local stop as for anything else. Today's offline |
| 5 | `timeout` with no witness | **Unknown.** The relay can't tell a machine switched off, asleep or offline from a host that hung. Today's offline (#815's "Lost connection") |
| 6 | `closed` or `reset` from a host that advertises `host-gone-v1` (it announces every deliberate departure, so leaving without a plan isn't one); or a witness `not-running` or `not-responding` | **Unplanned, with evidence the bridge stopped while the machine is up.** Pending, then host gone ([The window](#the-window-805-goes-first)) |
| 7 | `reset` from a host that doesn't advertise `host-gone-v1` | **Unknown,** as row 4: an older host's deliberate stop can end in a kill. Today's offline |

`host-gone-v1` is a relay `hello` capability, in #815's `capabilities` list. A host advertises
it only once every path that stops it on purpose tells the relay first, so that a departure
without a plan means it wasn't meant:

* #801's remote stop and restart, and #819's and the person's updates, announce through
  #815's `host-planned-state` (`stop`, `restart`, `update`) before they commit.
* **The app's Stop** writes the stop marker and asks the host to stop, which announces `stop`;
  and, before its fallback that kills a host too slow to stop (`HostManager`), the app sends
  its own witness `stopped` (slice 2). A host killed so is deliberate, not gone.
* **The app's update hook** sends witness `updating` before it hands a host over to the package
  (including a host it doesn't ask to stop first, because its usage probes are busy).
* **`agent-bridge-host stop`** writes the stop marker too (today only `HostManager.MarkStopped`
  does), and the host it stops announces `stop`.

OS sleep, shutdown, reboot and sign-out are best effort in #815, so a machine shut down without
its announcement can read as row 6 with `reset`. That is the one false "isn't running" this
design accepts, and it is true from the person's side: the bridge isn't running. Until the
witness ships (slice 2), an app Stop that has to kill its host can read so too; the push is
slice 3, so no alert goes for it.

## The window: #805 goes first

A departure in row 6 is **pending** for **5 minutes**, from the relay's observed departure
time. Nothing shows and nothing is sent while it is pending: clients keep #269's "Reconnecting…"
and "Offline · last seen" meanwhile. 5 minutes is longer than every way #805 heals a host
without a person:

| #805 heals | Signal | Worst case |
|---|---|---|
| #106: the launcher restarts a host that exited | `reset`, then the host is back | 3, 6 and 12 s back-off, 3 retries, each with a start-up: about 1.5 min |
| #989: a committed departure that never exits restarts itself | `closed` (graceful `host.restart`, exit 75), then the host is back | the 2-minute bound, then a graceful stop (up to about 90 s) and a start-up: about 4 min |
| #805's watchdog (proposed): a hung host is replaced | `timeout` (or witness `not-responding`), then the host is back | 60 s to suspect, 10 s to exit, a start-up with 120 s grace: about 3.5 min |

The host coming back and staying connected for 2 minutes cancels the window: it isn't gone,
whatever happened (a shorter return continues the episode; see Flapping). **SRE confirms
the 5 minutes** against #805's final numbers; the relay keeps it as one constant,
`HostGoneAfter`, never below #805's longest self-restart.

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
| `gone.alert` | `true` when the episode's arm said a session was working or waiting and the caps allowed an alert, whether or not any Web Push notice was armed. The relay sends the notices it holds; a client that gets no Web Push (the Windows app) raises its one toast from this ([ui.md §6.14](../design/ui.md#614-a-machine-whose-bridge-isnt-running-970)), so an account with only Windows apps is told too |

`gone` is absent while pending, for rows 1–5 and 7, and the moment the host is listed online again.
A client that doesn't know `gone` shows today's offline machine. It goes when the host returns,
is forgotten, or the account is signed out or changed. It is not dropped at #815's 24-hour
detail limit: its only extra fact over `lastSeen`, which the roster already keeps, is that the
bridge stopped (an open question for Connection).

## Arming the notice (slice 3)

A relay text control from the host, alongside #815's `host-planned-state`:

```json
{ "t": "host-gone-arm", "v": 1, "seq": 7, "armed": true, "notices": [ { "target": "…", "body": "…", "ttl": 21600, "urgency": "high", "topic": "…" } ] }
```

| Field | Contract |
|---|---|
| `seq` | Strictly increasing within the connection. The relay keeps only the newest; an older one is ignored |
| `armed` | `true`: a session is working or waiting; alert if this host becomes gone, and send these notices. `false`: nothing in flight; `notices` absent. An arm with no notices (a host no browser subscribes to) still counts: it is what `gone.alert` reads |
| `notices` | One per subscribed device, at most 8: #815's envelope exactly. `target` is the device's consent handle from #815's target registration, never a free-form endpoint; `body` is RFC 8291 ciphertext the host sealed to that device's subscription, at most `WebPushCrypto.RecordSize` bytes decoded; `topic` is valid `PushTopic` syntax |

Result: `{ "t": "host-gone-arm-result", "v": 1, "seq": 7, "accepted": true, "noticesArmed": true }`,
or `accepted: false` with #815's closed `error` set. Arming is never a command and never
authority: it only lets the relay send a notice the host already wrote.

**When a host is armed.** Exactly while at least one session it reports has `status` `working`
or `waiting`, or is waiting on a delegate's question (`waitingOn`) (v1-sessions.md, Session):
working, or waiting on the person (a question, an approval or a form). Idle, delegating with
the main agent idle, ending, and sessions that need sign-in are not. The host:

* arms within 5 s of the first session becoming working or waiting, and disarms within 5 s of
  the last one leaving, always: a disarm is never held back, so the host is never armed more
  than 5 s after the work stopped;
* re-seals only when its subscriptions change (the body says nothing that changes with the
  sessions, so the notice needn't be rewritten as they come and go), and coalesces arming and
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
still told. When the host becomes gone with `armed: true` standing, the relay sets
`gone.alert` and claims and sends each notice once through `PushGateway`, under #815's caps:
one host-gone alert per machine an hour, three per account in 24 hours, shared with #805's
no-return pushes. It deletes notices once sent (an episode alerts once), when a newer arm
replaces them, on forget, and on expiry. A failed or uncertain delivery isn't retried.

**When the episode ends** (the host has been back for 2 minutes), after the relay sent notices,
the relay tells the host which:
`{ "t": "host-gone-sent", "v": 1, "since": "…", "targets": ["…"] }`. The host then sends those
devices a silent `hostBack` push itself, live, on the same tag and topic, which replaces the
alert in place (v1-sessions.md). Waiting the 2 minutes means a host that dies again straight
away never leaves a phone saying "running again". The relay sends nothing on its own when the
host returns: it holds no sealed "back" notice.

## The app's witness (slice 2)

When the machine's Windows app is running, it knows what the relay can't: whether the host
process is there at all, whether the launcher is still trying, and whether it was stopped on
purpose. It reads them from the same signals #805 and #989 use:

| `state` | Read from |
|---|---|
| `running` | The host process is there and answers its local pipe |
| `restarting` | The launcher (`agent-bridge-hostw`) is alive without its host: #106's back-off, or a restart under way (`host.restart`, exit 75, #989's departure-stuck restart) |
| `stopped` | The profile's stop marker (`host.stopped`, `HostLauncherContract.StopMarker`) is there |
| `updating` | The app is applying an update, or handing the host over to one |
| `not-responding` | The host process is there but hasn't answered its pipe for 60 s (#805's suspect threshold) |
| `not-running` | None of the above: no host, no launcher trying, no stop marker, no update |

```json
{ "t": "host-witness", "v": 1, "host": "0123456789abcdef0123456789abcdef", "state": "not-running" }
```

The app sends one when its local host's state changes and when it connects, at most one a
minute otherwise, and sends `stopped` as it stops its host (before any fallback that kills a
host too slow to stop) and `updating` before it hands a host over to an update, so the relay
has them before the socket ends. The relay takes it only from the app device the host itself last declared as
its own (`{ "t": "host-local-app", "v": 1, "app": "<device id>" }`, sent by the host on each
connection, learned from the app over the local pipe), in the same account, and only about
that host. A witness never makes an online host offline. `running` cancels a pending window
(the host is alive and only its relay link is down: "Reconnecting…", not gone); `stopped` and
`updating` make the departure deliberate (row 3: `updating` as an `update` plan, with #805's
return timer); `restarting` holds the window; `not-running` and
`not-responding` are evidence, and turn a `timeout` into row 6.

## Privacy

This adds plaintext metadata to the relay, which Connection approves before code:

* **The arm bit and when it changes:** when a machine has a session working or waiting. The
  relay already sees traffic timing that suggests as much; this makes it explicit, for the
  account's own hosts only. No session id, count, title, agent or state is in it.
* **The witness state** of a host, from its own app.
* **`gone` in presence,** to the account's own devices only.

None of it is logged, put in a metric label or exposed outside the account. Notices follow
#815: no endpoint, push body, target handle or socket tag in logs; errors reduced to fixed
categories. Metrics: `relay.host.gone` (counter, `evidence`, `alert`), and
`relay.host.gone.cancelled` (counter, `by=returned|planned|witness`), no identities.

## Who does what

| | #805 (SRE): heal | #970 (Daily): tell |
|---|---|---|
| Planned departure (#815 plan) | Announces; shows the planned state; pushes if a reboot, restart or update doesn't return | Nothing |
| A host that exits (crash) | #106 restarts it | Waits 5 min; if it isn't back, gone |
| A host stuck after committing a departure | #989 restarts it after 2 min | Waits; it comes back |
| A hung host | #805's watchdog replaces it, or holds off (a delivery in flight) | Waits; gone if it doesn't come back (with a witness, `not-responding`) |
| A deliberate stop | Stop marker; the host announces `stop` | Nothing |
| A person wants it back | #801's remote restart (the remedy; not built yet) | Shows the state; offers #801's Restart once it exists. Never "go to the machine" |

One departure never gets both pushes: a departure with a plan is #805's, one without is #970's.

## Slices

1. **Relay detection and the line:** the `reset`/`timeout` split, classification, the window,
   `gone` in presence, and both clients' line (ui.md §6.14). No push.
2. **The witness:** `host-local-app`, `host-witness`, the app's local readers, and the app's
   `stopped` and `updating` before it stops or updates its host.
3. **The push:** `host-gone-arm`, the relay's dispatch, `gone.alert`, `host-gone-sent` and the
   host's `hostBack`; the Windows app's toast from `gone.alert`. Needs #815's target
   registration. Comes after the witness, so a deliberate stop that ends in a kill never alerts.

## Tests (to write with the code)

* "A host that crashes and is restarted within five minutes is never shown as gone."
* "A host still away five minutes after a reset is shown as gone, since its departure."
* "A host that announced an update or a stop is never shown as gone."
* "A machine that times out without a witness is offline, not gone."
* "An app that witnesses its host restarting holds the window until it ends or fifteen minutes pass."
* "An app that witnesses its host running cancels a pending window."
* "A witness from an app the host didn't declare is ignored."
* "A host that flaps within two minutes keeps its first departure as since."
* "A gone host with nothing armed sends no push."
* "A gone host that was armed sends each notice once, however many relays or restarts."
* "A host that dies again before it re-arms keeps the arm its episode began with."
* "A host still starting never disarms before its first full list."
* "An armed host with no browser subscribed still sets alert, so the Windows app toasts."
* "A disarm is sent within five seconds, whatever the coalescing."
* "A host that comes back is told which devices to send hostBack only after two minutes connected."
* "An app's Stop that kills its host is deliberate, not gone."
* "An update witnessed by the app that never comes back gets #805's no-return notice, not #970's."
* "A replaced socket's departure never makes its replacement gone."
* "A relay restart is never read as the host going."

## Open questions

Asked on the PR: Product's confirmation of rows 4–7 (in particular that a `timeout` doesn't
push), SRE's confirmation of the window and the hand-off, Connection's of the privacy and the
`dropped` split, and Creative's words.
