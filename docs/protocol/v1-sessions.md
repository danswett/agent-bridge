# Bridge Protocol v1: sessions

The contract between a **Bridge Host** and its clients: the PWA now, and later the
WinUI 3 and SwiftUI apps and the native phone apps. It is how a client sees a machine's
agent sessions, answers their questions and replies to them. It travels inside the end-to-end Noise
channel from spike S4, so the relay never sees any of it. C# records matching this
document are in `src/AgentBridge.Protocol/Sessions.cs`, and the PWA mirrors them in
JavaScript.

Scope so far: a host reports sessions, their activity and their pending questions
(`sessions.read`), can take an answer to a question (`sessions.answer`), can take a
reply: a prompt typed into a session (`sessions.reply`), with files and images sent ahead
of it (`sessions.attach`, see [Attachments](#attachments)), can end a session
(`sessions.stop`), and notifies a client's devices when a session asks or finishes, even
with the app closed (`push`, see [Notifications](#notifications)). It can also launch or
resume a session (`sessions.launch`, in [v1-launch.md](v1-launch.md)). Files too large for
the channel go through the relay's blob store (`sessions.attach.blob`, in
[blobs.md](blobs.md)). A session's earlier turns are paged back with `session.history`
(`sessions.history`, in [v1-history.md](v1-history.md)).
History cursors can become stale as new turns push them outside the current newest-500 window;
clients restart paging without `before` and merge by turn id.

## Rules for every message

* JSON, UTF-8, `camelCase` names. A field that is absent and one that is `null` mean
  the same thing. Text in the Basic Multilingual Plane, accented letters and CJK
  included, is sent as raw UTF-8, not as `\u` escapes. Characters outside it, which are
  most emoji, **are** escaped as a surrogate pair (`\uD83D\uDE00`): 12 bytes of JSON for 4
  bytes of text. Clients must read both forms.
* **Clients ignore fields and enum values they don't know.** An unknown value reads as:
  `agent` → `unknown` (shown generically), `status` → `idle`, tool `status` → `done`,
  question `kind` → `form`, field `type` → `text`, `driver` → `person`; an unknown `class` reads as absent. Hosts never remove or rename a field
  within a protocol version.
* Times are UTC with milliseconds and a `Z`, always in this exact form:
  `2026-10-28T04:22:43.182Z`. Clients can compare them as strings.
* **Sizes are UTF-8 bytes of the JSON as sent.** No single message exceeds **48 KiB**, so
  it always fits one Noise transport message (65,535 bytes) with room to spare. **Every
  `Session` as sent fits 40 KiB**: in a list page, an event or `session.get`. Each type
  below gives per-field limits, which count the UTF-8 bytes of the text itself, not its
  JSON form. Those limits alone can't promise the budget: escaping makes the JSON larger
  than the text (an emoji triples, and quotes and control characters grow too), and
  question options and history have no total. So a host finishes with the trimming
  order in [Session budget](#session-budget), which measures the JSON as sent. Long text
  is cut and flagged, never mid-character.
* Text is Markdown where the type says so (agent responses, questions), otherwise plain.
  Clients render Markdown without raw HTML.
* **A record a client can't fully read is read as below** ([Malformed records](#malformed-records)),
  never by failing the whole connection.

## Malformed records

A host that is broken, older than its spec or hostile may leave a required field out or send a
value of the wrong JSON type (a number where a string belongs, a string where a boolean or number
belongs, an object where a list belongs). A string holding half a surrogate pair (`"\uD800"`) is
not text a client can rely on reading, and counts as the wrong type. Absent and `null` already mean the same thing. A client
reads every host record (results and event data alike) by these rules (#446):

1. **A field of the wrong type reads as absent.** A required one, missing or unreadable, takes its
   type's default: `""`, `0`, `false`, `[]`, an empty record, the time
   `0001-01-01T00:00:00.000Z`, or for an enum the value an unknown one reads as (above), never
   its first. An enum value has to be a string; an unknown string reads as the table above says.
2. **An entry of a list that can't be read** (`null`, the wrong type, or a record refused under 3)
   **is left out**, and the list is kept.
3. **A record is refused, never defaulted, when a strict field is missing (where required) or of
   the wrong type.** These are the fields it is filed or acted on under (*identity*), and the
   ones a person decides on or that prove who asked (*security*). A strict list is refused whole
   if any entry can't be read, an entry of a list within it included: a question is never shown
   with a choice left out, and an escaped approval's mark `[5, null, 6]` is never read as `[5, 6]`.
4. A refused record held by another record's field reads as that field absent: a session whose
   question is refused is kept, without a question. A refused record in a list is left out
   (rule 2). Only a refused **top-level** record refuses its message: the client drops that one
   result or event, logs it (once for each kind of message is enough), and carries on reading
   the channel. One bad message never ends a client's event stream.

Strict fields (`required` where the record requires them; any other is refused only when present
with the wrong type):

| Record | Identity | Security |
|---|---|---|
| `HelloResult` | `protocol` | |
| `MachineInfo` | `id` | |
| `Session` | `id` | |
| `Subagent` | `id` | |
| `ToolCall` | `id` | |
| `Activity` | | `source`, `userMessageFrom` |
| `ActivitySource` | | `response`, `reasoning` |
| `Question` | | `id`, `kind`, `message`, `fields` (whole), `agent`, `answerAtMachine`, `truncated`, `escaped` |
| `EscapedText` | | `shown`, `marks` (whole), `approve` |
| `Field` | `name` | `type`, `title`, `description`, `options` (whole), `allowOther` |
| `Option` | | `value`, `label` |
| `DiscoveryHeld` | `reason` | `agents` (whole) |
| `SessionList`, `SessionsSynced`, `DiscoveryChanged` | | `discoveryHeld` |
| `SessionsSynced` | `live` (whole) | |
| `SessionPatch` | `id`, `base`, `set`, `append`, `subagents` | |
| `SubagentsPatch` | `set`, `remove`, `order` (each whole) | |
| `session.remove` data | `id` | |
| `UsageWindow` ([v1-usage.md](v1-usage.md)) | `percent` | |
| `UsageReader` | `id` | |
| `AgentUsage` | `readBy` | |
| `LaunchAgent` ([v1-launch.md](v1-launch.md)) | `id` | |
| `LaunchWorkspace` | `label` | |
| `ResumableSession` | `id`, `launcher` | |
| `RestoreSession`, `RestoreRunSession` | `id` | |
| `LaunchDefaults`, `LaunchedWith` | | `permissions` |
| `LaunchedTransfer` | `from` | |
| `TrustQuestion` | | `id`, `agent`, `kind`, `folder`, `root`, `rootNote`, `warning`, `answerable` |
| `SignedCommand` ([v1-transfer.md](v1-transfer.md)) | | `body`, `by`, `mac` |
| `TransferResult` | | `blob`, `size`, `sha256`, `agent`, `launcher`, `files` |
| `ManageCommand` (#801) | | `command` |
| `ManageHaTokenRequest` (#801, held until #55) | | `command`, `secret` |
| `UpdateCommand` ([v1-update.md](v1-update.md), #1159) | | `now` |
| `HistoryResult` ([v1-history.md](v1-history.md)) | `before`, `start` | |
| `HistoryTurn` | `id` | `prompt` |
| `HistoryPrompt` | | `from` |
| `HistoryPart` | `id` | `outcome` |
| `DelegateEvent` | `agent` | |

So, for example, a session with no `status` reads as `idle`, one whose `title` is a number has
no title, and a `ToolCall` with no `id` is left out of `history`. A question whose `message` is a
number, or one of whose options has no `value` or a `label` that is not text, is not shown at
all, while its session is: a choice is never shown without the words it was asked with, or under
its bare value instead. A field or option with no `title`, `description` or `label` at all is
read as usual. Launch
options whose `defaults.permissions` can't be read are refused whole, so nothing is launched with
a permission mode the client guessed. A session whose `activity.source` or `activity.userMessageFrom` can't be
read keeps an empty `activity` (rule 4: the required field takes its default): read as absent, a
delegate's text would be shown as the main agent's, or a scheduled or agent-sent prompt as the
person's own words.

The shared vectors ([vectors/](vectors/README.md)) work these rules through for every record a
host sends: each `missing-…` and `wrong-type-…` vector is either valid, with what it reads as, or
refused because a strict field is involved. A `sessions.synced` or `discovery.changed` whose
`discoveryHeld` can't be read is refused whole: read as absent, a hold the host still has would
read as lifted. Every part of a `session.patch` is strict, and `live` is: a patch read past a part
it could not read would leave the client's session silently unlike the host's, and a `live` read
with an id left out would drop a session that is still running.

This is how clients read what hosts send. Hosts read what clients send (`session.answer`,
`session.reply`, `session.launch`, `launch.trust.answer`, a signed command's body) strictly, as
before: a request with a required field missing or of the wrong type is refused. The machine's
own management records on the local pipe (`host.status` and the like,
[local-pipe.md](local-pipe.md)) are read strictly too; the app refuses such a message the same
way, without ending its stream. C#: `Received.Read<T>` and `[Strict]` in
`src/AgentBridge.Protocol/Received.cs`.

## Test vectors

External read-only connectors can start with the versioned
[connector read profile](connector-read.md), its machine-readable operation index
and disposable stdio mock. This is an application-contract test tool, not enrolled
connector delivery or permission to connect a production service.

Valid and refused examples of every record in this protocol, and of the signed command, are in
[`vectors/`](vectors/README.md), as JSON that the C# records generate. The C# tests and the PWA's
unit tests replay the same files, so a field renamed or a limit moved in one implementation fails
in the other. Where they disagree today, the files list it as a known difference with its issue.
After a deliberate change, regenerate them with `UPDATE_PROTOCOL_VECTORS=1` as
[vectors/README.md](vectors/README.md#regenerating) says, and commit them with the change.

**Proposed for #970 slice 1b (not generated yet):** the relay's `presence` (roster.md) gets
vectors of its own, `vectors/presence.json`, so older clients' fallback is tested. `known[]`
entries with `offline.outcome` set to each value a relay may send, `closed`, `reset`,
`timeout`, `rate-limited`, `too-large`, `relay-restarting` and `unknown`, and to #815's older
`dropped` and an unknown future value. A client that predates a value reads it as lost
connection (`reads`: no reason shown, "Offline" as today). They include an entry with
`offline.gone` (`since`, `evidence`, `alert`) and one whose `gone.v` is unknown (read as
absent), so an unknown field never makes a client claim the bridge stopped
([host-gone.md](host-gone.md)).

## Channel framing

Inside a channel, as in S4:

| Direction | Shape | Meaning |
|---|---|---|
| client → host | `{"id": 7, "op": "sessions.list", "data": {...}}` | A request. `id` is a positive integer unique on this channel |
| host → client | `{"re": 7, "ok": true, "result": {...}}` | Its response |
| host → client | `{"re": 7, "ok": false, "error": "unknown op"}` | A failed request |
| host → client | `{"re": 7, "progress": {...}}` | How far a request that asked for progress has got, before its response. It has `re` and no `ok`, and never ends the request ([Progress](#progress)) |
| host → client | `{"ev": "session.upsert", "seq": 42, "data": {...}}` | **New:** an event the client subscribed to. It has no `id` or `re` |

`seq` belongs to the channel, not to a subscription. It goes up by one for each event the
host sends on that channel, starting at 1, and a later subscribe continues from where it is
rather than restarting. So a client expects 1 first and then one more each time, after
subscribing again too: a first event above 1, or any other jump, is a gap (#427; a client
that took its first event, or the first after subscribing again, as the baseline missed
events lost just then). If it sees a gap (it shouldn't, since Noise is ordered),
it calls `sessions.subscribe` again.

### Opening a channel: XX, or IK

A channel's frames inside the relay's data frames are `[kind:1][channel id:8][body]`. Kinds 1, 2 and 3
are `Noise_XX_P256_AESGCM_SHA256`'s three handshake messages, 4 a transport message, 5 a close. The
prologue binds both device ids and the channel id. Each side's handshake payload introduces it,
`{"name": "…", "kind": "host"}`, and the key that answers must hash to the device id it was reached at.

**IK (#603).** XX takes a round trip and a half, and the client's `hello` another round trip after it.
An opener that already holds the responder's static key can use `Noise_IK_P256_AESGCM_SHA256`
instead (same suite, pattern `<- s … -> e, es, s, ss` then `<- e, ee, se`): kind 6 is its first
message, kind 7 the reply, and the channel is open after one round trip.

* A responder that takes IK says so in each introduction it gives (XX's second message, IK's reply):
  `"ik": ["hello"]`, listing the ops it answers from an IK first message (possibly none). An opener
  keeps that responder's key, which the XX proved and which hashes to its id, and opens its next
  channel to it with IK. An introduction without `ik` makes it forget the key.
* An opener's first message may carry the request it will make first:
  `{"name": …, "kind": …, "early": {"op": "hello", "data": {}}}`. Only an op the responder listed
  goes in it. The reply then carries the answer, as a response without `re`:
  `"early": {"ok": true, "result": {…}}`. The channel's first request, if it is exactly that op
  with that data, takes that answer without going to the host. Any other first request is sent,
  and the answer is dropped.
* An IK first message is encrypted only to the responder's static key, and a relay can replay it.
  So a responder answers from it only the ops it listed (a host lists `hello`, which reads), after
  the same authorization as any request. Anything else is asked again once the channel is open,
  never run from it. Everything after the first message is forward-secret, as in XX.
* A responder that cannot read an IK first message closes the channel (kind 5). An opener whose IK
  gets that, or no reply within 5 s (an older responder ignores kinds it does not know), forgets
  the key and opens with XX. An older opener never sends IK, so older peers see nothing new beyond
  the `ik` field in an introduction.

Round trips from an open relay connection to the answer of the first request: 2 with XX (the
first time, or to an older host), 1 with IK. `ChannelTests` in `tests/AgentBridge.Transport.Tests`
and `pwa/test/unit/channels.test.mjs` count them. Interop vectors: [`vectors/noise-ik-p256.json`](vectors/README.md).

### Deflate

Each message above is one Noise transport message whose plaintext is its JSON. From #603 a
larger one may go deflated instead: the byte `0x01`, then the JSON as raw DEFLATE (RFC 1951, no
zlib header; `CompressionStream("deflate-raw")` in a browser). It is compressed first and then
encrypted, each message on its own with no dictionary carried over from another. Session JSON
deflates to well under half.

* A peer says it inflates in its handshake introduction, `"deflate": true`, beside `name` and
  `kind`. A message goes deflated only to a peer that said so. Older peers say nothing, and get
  JSON as before.
* A sender deflates only messages of 512 bytes or more, and sends the deflated form only when it
  is smaller.
* The limit on a message is unchanged: its JSON must fit one transport message, deflated or not.
  A receiver refuses one that inflates past that. It also refuses one that does not inflate at
  all, and either ends the channel, as a frame that fails to decrypt does.

Round trips are unchanged. Fewer bytes cross, which matters most for the first snapshot over a
slow link.

## Ops

### `hello`

Request `data`: `{}`, or `{ "changes": true }` to be told when what hello says changes (below). Result:

```json
{
  "protocol": 1,
  "machine": { "id": "53d1997f…", "name": "DSWETT-DEV-VM1", "os": "windows", "hostVersion": "0.1.0" },
  "capabilities": ["sessions.read"]
}
```

`machine.id` is the host's device id (S4). `os` is `windows`, `macos` or `linux`. `machine` also carries `dev` and `sessions`, for a status line ([v1-usage.md](v1-usage.md#machine-status)), and `readOnly` while the machine takes nothing from other devices: because its config makes it read-only (`delivery.readOnly`, [host-config.md](../host-config.md)), or because agent-ha-bridge runs there for the same user (design G5). It is the reason, in words, which a client shows on the machine's status line and on its read-only questions. Absent otherwise. The `readOnly` agent-ha-bridge gives and the capabilities it takes away (below) come from one look at it, at most 2 s old, shared by hellos in that time (#607). Nothing is acted on from that look: an answer, reply, stop, launch or update looks again immediately before it acts, and is refused with the reason if agent-ha-bridge started meanwhile. `machine.instance`, when present, names this run of the host: it is new each time the host starts and the same on every connection to that run, so a client can tell a reconnect from a restart before sending a request again that only the first run remembers (a reply or a launch, `session.launch`, whose outcome was lost). Answer outcomes are also remembered across restarts as described below.

**What went (`machine.restore`).** A host that offers `sessions.restore` keeps a record of the sessions it sees live (#780, [host-launch.md](../host-launch.md#restoring-after-a-reboot-or-a-crash)), and when it starts again says in `machine.restore` what a machine reboot, a crash of the host, or its clean restart took with it, for this run of the host and at most 24 hours:

```json
"restore": { "at": "2026-11-04T16:02:11.000Z", "cause": "reboot", "since": "2026-11-04T15:58:40.512Z", "restored": 0, "loops": 0, "waiting": 0, "why": "pending",
  "offered": [ { "id": "0f3c…", "agent": "copilot", "title": "Pick the session store", "midTurn": true }, { "id": "7a21…", "agent": "claude", "questionLost": true } ] }
```

| Field | Notes |
|---|---|
| `at` | When this run of the host started and compared |
| `cause` | `reboot` (the machine started again, or the person signed out, so every agent went with it), `crash` (the host stopped without saying so, on the same boot) or `restart` (it stopped cleanly, on the same boot, and sessions went while it was away). A client reads a cause it doesn't know as `crash` |
| `since` | When the earlier run last wrote down what was live: about when the sessions went |
| `restored`, `loops` | Sessions restored, idle, through the resume path, and seen ready, and those not restored again because they died again within 10 minutes of a restore (offered, and among `offered`) |
| `waiting` | Sessions still being judged: their agent's discovery is held ([Discovery held](#discovery-held)), or its hooks' endings are not yet taken in, so whether they still run, or ended, cannot be told yet. They are never read as gone; once judged, `restore` changes and `hello.changed` says so |
| `why` | Why the sessions in `offered` are offered rather than restored: `pending` (kept for a person to resume from Launch: the restore was refused or failed, or it died again too soon), `readOnly` (the machine takes no launches now, for example beside agent-ha-bridge, design G5; kept until it does) or `off` (`sessions.restoreOnStart` is off, [host-config.md](../host-config.md); only said). A client reads a value it doesn't know as `pending` |
| `offered` | The sessions that died, newest first, at most 20: `id`, `agent`, `title`, `midTurn: true` if a turn was running when it went, `questionLost: true` if a question or approval was waiting in it, which went with it. Never the question's words, a prompt or a response. The notice stays within 8 KiB as JSON: past that, titles are shortened (to 60 bytes), then dropped, then the last names are left out and counted in `offeredMore`; the counts, `cause` and the times are never trimmed. The `restored` notification is made from the same notice |
| `offeredMore` | How many more died than `offered` names: past 20, or left out to keep the notice within 8 KiB. Absent when none |

Absent when nothing went, or once the run is 24 hours old. Nothing is started from it: a session in `offered` is resumed by a person from Launch (`session.launch` with `resume`, [v1-launch.md](v1-launch.md)), with every rule a resume has. **Proposed, not built (#948):** a person's device may also restore every session the notice names in one confirmed press, `restore.run` ([restore-run.md](restore-run.md)), through the same path a start-up restore takes. A client that does not know `restore` ignores it. It names sessions, so a peer that may not read them (one pinned as a `host`, [trusted peers](../host-config.md#trusted-peers-disposable-host-foundation)) is not told it.

**Changes (`hello.changed`).** What hello says changes without a new connection: agent-ha-bridge starting or stopping (G5) takes answering, replying, stopping and launching away or gives them back, with `readOnly`; `update` comes and goes. A client used to ask hello again every 10 s to see it, on every connection: about 36 hellos a minute at a host from the account's other machines' Windows apps (#640). Of a host that offers `hello.changed`, a client asks `{ "changes": true }` in its hello, and from then on, for as long as that channel is open, the host sends the event

```json
{"ev": "hello.changed", "seq": 12, "data": {}}
```

once whenever `machine.readOnly`, `machine.restore` or `capabilities` would differ from what it last told that channel, and the client asks hello again (with `changes` again, which is what it was last told from then on). The event carries nothing more on purpose: what a channel's hello says depends on who is asking (a pinned peer's role, an agent on the pipe), and only hello itself says it. For the same reason what is compared is what that channel's hello would say, after its role or caller kind has shaped it, so a channel hears only of a change it could see: a `service` peer, whose hello offers only `sessions.read`, hears nothing when `update` comes or goes (#651). The host looks every 10 s, as often as clients asked before; other `machine` fields (`sessions`, say) changing alone send nothing. A client that asked still asks hello again now and then (the Windows app: every 5 min of another machine's host; its own host, which it also asks `host.status`, every 10 s over the pipe as before), in case it missed a change; one that did not ask, or a host that does not offer `hello.changed` (which ignores `changes`), is asked as before. Like every event it is numbered in the channel's `seq`. Nothing is acted on from it: every op is checked afresh when it arrives.

`capabilities` lists what this host supports, and a client hides anything missing:

| Capability | Ops |
|---|---|
| `sessions.read` | `sessions.list`, `session.get`, `sessions.subscribe` |
| `sessions.history` | `session.history`: a session's earlier turns, a page at a time, for the conversation pane ([v1-history.md](v1-history.md)). Offered to a person's devices and the app; never to the HA connector or an agent on the pipe |
| `sessions.patch` | `sessions.subscribe` takes `patch` and `since`: live changes as `session.patch`, only what changed ([Patches](#patches-sessionpatch)), and a reconnect to the same run of the host sent only the sessions that changed meanwhile ([Resuming](#resuming-without-a-snapshot)). Without it, or without asking, a client gets whole upserts and whole snapshots as before |
| `hello.changed` | `hello` takes `{ "changes": true }`, and the host then sends `hello.changed` on that channel when its `readOnly` or capabilities change ([Changes](#hello)). Without it, a client asks hello again to see a change |
| `sessions.focus` | `sessions.focus`: the client says whether it is on screen and which session it shows. Live changes to the others then come together at most every 12 s, and a device on screen is not notified of finished turns ([sessions.focus](#sessionsfocus)). With `openQuestion` and `idleMs` it also says which question's card it shows and how long since the person's last input, and while the person uses a device the host holds back some pushes ([While the person uses a device](#while-the-person-uses-a-device-1190)). Without it, or until a client says, every change goes at once |
| `sessions.answer` | `session.answer`. A host leaves it out when it cannot answer at all, for example while agent-ha-bridge's daemon runs for the same user, or on a platform without delivery yet. A client then shows questions read-only and says to answer at the machine |
| `sessions.reply` | `session.reply`. Left out on the same terms as `sessions.answer`. A client then offers no reply box |
| `sessions.progress` | `session.reply` and `session.answer` take `"progress": true`, and then report `received` and `typed` before their result, which carries `timings` ([Progress](#progress)). Offered with `sessions.reply` or `sessions.answer` |
| `sessions.attach` | `attachment.start`, `attachment.chunk`, `attachment.discard`, and `attachments` on `session.reply` ([Attachments](#attachments)). Offered only with `sessions.reply`, by a host that can stage files for the agent. Without it a client offers no way to attach |
| `sessions.attach.blob` | `attachment.start` takes a `blob`: files over 256 KiB, up to 100 MiB, through the relay's blob store ([Large files](#large-files-through-the-blob-store)). Offered with `sessions.attach` on a channel through a relay, while the host is connected to one that has a blob store. Without it a client keeps to 256 KiB |
| `sessions.attach.direct` | `attachment.chunk` takes files up to 100 MiB, not only 256 KiB. Offered with `sessions.attach` on the local pipe only ([local-pipe.md](local-pipe.md)), where the bytes cost the relay nothing |
| `sessions.stop` | `session.stop`. Left out on the same terms as `sessions.answer`. A client then offers no End session |
| `sessions.approve` | Copilot permission prompts are approved or denied with `session.answer` ([Copilot permission prompts](#copilot-permission-prompts)). Offered only with `sessions.answer`, by a host whose `permissions.remoteApproval` is on (the default). Without it, every Copilot permission prompt is `answerAtMachine`, and a client says approving from the phone is turned off on that machine |
| `sessions.approveEscaped` | Approve on an approval with `escaped` may go from this device, as a two-step confirmation ([Escaped approvals](#escaped-approvals)). Offered with `sessions.answer` on a channel through the relay from a person's device (the PWA or the Windows app) that this machine approved, while its `permissions.escapedApproval` is `confirm`. Advisory: every answer is checked again. Without it, a client offers Deny only for such an approval and says why |
| `push` | `push.subscribe`, `push.unsubscribe`, `push.list`: Web Push notifications of questions, finished turns and sessions that need sign-in ([Notifications](#notifications)). Left out by a host whose `push.enabled` is off ([host-config.md](../host-config.md)): it then fails `push.subscribe` saying it doesn't send notifications, removes nothing on `push.unsubscribe` and lists no devices |
| `usage` | `usage.get`, `usage.subscribe`: how much of each agent's allowance is gone ([v1-usage.md](v1-usage.md)) |
| `usage.shared` | `usage.get` and `usage.subscribe` take `{ "shared": true }` and add the readings this host keeps from the account's other machines, each with `readBy` ([v1-usage.md](v1-usage.md#shared-readings)). Without asking, a client gets the host's own report as before |
| `sessions.launch` | `launch.options`, `session.launch`: starting or resuming a session in a workspace approved at the machine ([v1-launch.md](v1-launch.md)) |
| `sessions.transfer` | `session.launch` takes a `transfer`: a session that ended on another of the account's machines, brought here and resumed as a copy ([v1-transfer.md](v1-transfer.md)). Through a relay with a blob store only |
| `sessions.transfer.serve` | `session.transfer`, host to host: this machine hands its ended sessions to the account's other machines on a command a person's device signed ([v1-transfer.md](v1-transfer.md)). Through a relay with a blob store only |
| `sessions.restore` | `hello`'s `machine.restore`: what a reboot or a crash took, offered to resume ([What went](#hello)), and the `restored` notification ([Notifications](#notifications)). Without it, a client says nothing of sessions that went
| `sessions.restore.run` | **Proposed, not built (#948).** `restore.run`: a person's device restores the sessions `machine.restore` offers, after a confirmation ([restore-run.md](restore-run.md)). Offered with `sessions.restore` and `sessions.launch`, only to a person's device (through the relay as `pwa` or `app`, or the app on the pipe), and never on a read-only machine (G5). Without it, a client says the sessions can be restored at the machine |
| `update` | `update.get`, `update.subscribe`, `update.check`, `update.install`: the machine's updates, seen and asked for from a person's other devices, installed by its Windows app ([v1-update.md](v1-update.md)). Through the relay only, and never by a read-only machine |
| `manage` | `manage.get`, `manage.subscribe`, `manage.restart`: a person's device restarts this host, each press a command it signed for this machine, leaving only through #805's quiet lease ([host-lifecycle.md](../host-lifecycle.md)); with `manage.hooks`, `manage.hooks.repair` repairs the hooks of an agent this installation set up (#801). Through the relay only, to a `pwa` or `app` device that is not this machine's own app, never by a read-only machine or one with `permissions.remoteManage` off. `manage.stop`, `manage.config.*` and `manage.haToken.set` are defined but refused `needs-roster` until the signed device roster (#55), and their capabilities are never offered. See [Managing a machine](#managing-a-machine) |
| `diagnostics` | `diagnostics.get`: "Diagnose this machine", the content-free journal and a closed health snapshot, reviewed before it is copied ([v1-diagnostics.md](v1-diagnostics.md)). Through the relay only, and only to a device pinned here as a person |

### `sessions.list`

Request `data`: `{}` for the first page, then `{ "cursor": "<next>" }`. Result:
`{ "sessions": [Session…], "revision": 118, "next": "<cursor>", "discoveryHeld": {…} }`. It
lists live sessions only, most recently updated first, as many as fit in one message (always
at least one). `next` is absent on the last page. `discoveryHeld` is present while the host's
discovery is held ([below](#discovery-held)). Each `Session` here **omits
`activity.history`**; `session.get` includes it.

`revision` is the host's change counter. It changes whenever any session changes. If it
changes between pages, the pages may overlap or miss a session; a client that needs a
consistent list uses `sessions.subscribe`.

### `session.get`

Request `data`: `{ "id": "<session id>" }`. Result: the full `Session`, or
`{ "missing": true }` once the session has ended and been dropped.

With `"agent": "<sub-agent id>"` as well, the result is that one entry of the session's
`subagents` in full: the [Subagent](#subagent) with its own `response` (at most 16 KiB, the end
kept, `responseTruncated` set when cut), `reasoning` (at most 4 KiB) and `history` (its latest
50 tool calls, oldest first), within the session budget (oldest tools go first, then
`reasoning`, then `response` to its last 4 KiB). `{ "missing": true }` when the session has
gone or has no sub-agent by that id. Lists never carry these fields. A host before
sub-agents ignores `agent` and returns the session, so a client asks only of a host whose
sessions carry `subagents`.

### `sessions.subscribe`

Request `data`: `{}`. Of a host that offers `sessions.patch`, a client may also ask for
patches, to resume, or both:

```json
{ "patch": true, "since": { "instance": "9a8b7c6d5e4f30211203f4e5d6c7b8a9", "revision": 118 } }
```

A host without `sessions.patch` ignores both. Result: `{ "revision": 118, "count": 3 }`, with
`"resumed": true` when the host resumes ([Resuming](#resuming-without-a-snapshot)). Then the
host sends:

1. **The snapshot:** one `session.upsert` for each of the `count` live sessions (resuming: for
   each that changed), then `sessions.synced` with `{ "revision": …, "discoveryHeld": {…} }`
   (resuming: and `live`). On `sessions.synced` a
   client replaces its list with the sessions it received since subscribing, which drops any
   it had from before that no longer exist, and takes `discoveryHeld` (absent: nothing holds
   discovery) as the host's hold.
2. **Live changes, until the channel closes:**
   * `session.upsert`, with a full `Session` (without `activity.history`), whenever
     anything a client shows changes. Hosts coalesce bursts to at most one upsert per
     session every 250 ms, and the latest state always arrives. A client that asked for
     `patch` gets a `session.patch` instead ([Patches](#patches-sessionpatch)), and never a
     live `session.upsert`.
   * `session.remove`, with `{ "id": "…" }` (to a client that asked for `patch`, with the
     `revision` a patch carries too), when a session ends and is dropped. A session
     a host is ending (`session.stop`) is first upserted with `status: "ending"`, then
     removed once its process has gone.
   * `discovery.changed`, with `{ "discoveryHeld": {…} }` when the host's discovery becomes
     held or what holds it changes, and `{}` when it clears. Sent once per change.

A session can change while the snapshot is being sent; its upsert then simply arrives
again. When a channel closes, the client subscribes again on the new channel.

A channel has at most one subscription. Calling `sessions.subscribe` again on the same
channel replaces the existing one: the host answers with a new `{revision, count}`, sends
a complete snapshot ending in `sessions.synced`, then carries on with live changes. It
never runs two subscriptions side by side, so each change arrives once. This is how a
client recovers from a gap. Live upserts sent before the new response are covered by
the snapshot. Until its `sessions.synced` the client's list may be missing changes, so the
PWA sends that machine nothing meanwhile, and the Windows app no answer, reply or stop for
another machine's sessions, as with a new channel's first snapshot (#427).

Which `sessions.synced` ends a snapshot (#427): only one received after the response to the
latest `sessions.subscribe`, with no gap in `seq` since that response. The host stops the old
subscription before it answers, so a `sessions.synced` received before the response is the old
subscription's (its snapshot may still have been under way when the gap was seen), and taking
it would end the new snapshot with only some of its sessions. A gap after the response,
including one that `sessions.synced` itself reveals, means that snapshot can't be whole: the
client subscribes again rather than taking it. Likewise only upserts received after the
response count towards the new snapshot: an earlier subscription's upsert of a session that
ended before the new snapshot was taken would otherwise keep it on the list.

#### Patches (`session.patch`)

A whole upsert is 1 to 40 KiB, sent again for any change: a sub-agent's status, a new
`updatedAt`. Measured on 2026-11-03 (#597), a session idle for 40 s was upserted three times at
21.7 KB, 16.6 KB of it `subagents` that had not changed. So a client that asked for `patch` gets
each live change as only what changed:

```json
{
  "id": "4f0c2a1e-9d3b-4e6f-8a7c-5b1d2e3f4a5b",
  "base": 41,
  "revision": 131,
  "set": { "status": "idle", "statusSince": "2026-10-28T04:26:13.182Z", "updatedAt": "2026-10-28T04:26:13.182Z", "activity": { "turnCount": 5, "currentTool": null } },
  "subagents": { "set": [{ "id": "agent-1", "status": "ended", "outcome": "done", "endedAt": "2026-10-28T04:26:13.182Z", "shells": 0, "currentTool": null }], "remove": ["agent-2"], "order": ["agent-3", "agent-1"] },
  "append": { "activity": { "response": " The docs build too." } }
}
```

| Field | Notes |
|---|---|
| `id` | The session |
| `base` | The `seq` of the event on this channel that last gave the client this session: an upsert of the snapshot, or an earlier patch. **Absent: the patch is the whole session**, applied to nothing, and it replaces whatever the client holds of it as an upsert would. A host sends the whole session when it has sent none of it on this subscription, and whenever that is smaller |
| `revision` | Every change the host had made up to this revision has now been sent on this channel ([Resuming](#resuming-without-a-snapshot)). At most the host's `revision`, and lower while another session's change waits out its 250 ms. Absent from a host that cannot say |
| `set` | A JSON merge patch ([RFC 7396](https://www.rfc-editor.org/rfc/rfc7396)) of the session as an upsert carries it: an object merges into the field's object member by member, `null` removes a field, and anything else (a list included) replaces the field whole. Holds `subagents` only whole, or `null` |
| `subagents` | Entries of `subagents`, by `id`, so a change to one does not resend the other nineteen: `remove` drops the ids it names; each entry of `set` merges (RFC 7396) into the entry with its `id`, or is added at the end if there is none; then `order`, when present, is every id in the order the list now has. A list left empty is absent |
| `append` | Text that grew: the shape of `set`, each leaf a string added to the end of the string the field holds. Never inside `subagents` |

A client applies `set`, then `subagents`, then `append`, to the session as it last **received**
it (the JSON, not what it made of it), and then reads the result exactly as it reads an upsert
([Malformed records](#malformed-records)). A patch therefore gives exactly the session that an
upsert of the same moment would. The shared vectors check this for C# and the PWA:
`SessionPatch` in [vectors/sessions.json](vectors/sessions.json) for each rule, and
[vectors/session-patches.json](vectors/session-patches.json) for the recorded Copilot, Claude and
Codex sessions in `tests/fixtures`, patched as a host sends them. C#: `SessionPatches.Diff` and
`Apply` in `src/AgentBridge.Protocol/SessionPatches.cs`; the PWA: `applyPatch` in
`pwa/js/session-patch.js`.

**A patch the client cannot apply** is dropped whole, never applied in part, and the client
subscribes again, as after a gap in `seq`: one whose `base` is not the `seq` it holds that session
from on this channel (it holds the session from another event, holds none, or holds it from an
earlier subscription), a `set` or `append` that is not an object, text appended to a field that is
not text, a `subagents` applied to a list that is not entries with distinct text ids, an entry of
`subagents.set` without one, or an `order` that does not name each entry once. Its list is not whole
again until that subscription's `sessions.synced`, as #427 says above. A client also subscribes
again when it cannot read a patch at all (a strict field below). That `base` names an event, and
not a version of the session, is what lets a client find out: it learns nothing of a session from
a patch whose upsert it dropped as unreadable (Malformed records, rule 4), and applying the next
patch to what it had before would have shown a session the host never had.

A host sends nothing for a change that leaves the session as an upsert carries it unchanged (only
its tool history moved, say). An `append` is used only where the old text is all still there at the
start of the new: a `response` cut to its last 16 KiB has moved, and is `set`. Hosts choose
freely between a patch and a whole session; a client applies either.

#### Resuming without a snapshot

A reconnect took a whole snapshot, and #427 holds everything a client sends that machine until it
ends. A client that held the list whole on an earlier channel to the same run of the host asks for
only what changed since, with `since`:

| Field | Notes |
|---|---|
| `instance` | The `machine.instance` of the `hello` the list came from |
| `revision` | The latest revision the client's whole list is at: the `revision` of the `sessions.synced` that made it whole, then of each `session.patch` and `session.remove` received after that `sessions.synced` with no gap in `seq` since. A gap stops it moving: what came before the gap still all arrived, so the revision held then is still true |

The host **resumes** when `instance` is this run's and `revision` is no later than its own. It
answers `resumed: true` and a `count` of the sessions that follow: one `session.upsert` for each
live session whose state is not what it was at that revision (one that started since included),
then `sessions.synced` with `live`, the id of every live session, those not sent included:

```json
{ "revision": 131, "live": ["4f0c2a1e-9d3b-4e6f-8a7c-5b1d2e3f4a5b", "b7e1c2d3-0000-4000-8000-000000000001"] }
```

Anything else gets the full snapshot, as without `since`, and no `resumed`: another instance (the
host restarted, and its revisions started again from 0), a revision it has not reached, or a `live`
of more than 32 KiB. A run's revisions only go up, and its sessions are all in memory, so a host
needs no history to resume: a session that changed since has a later revision, and one that ended
is missing from `live`.

**When the list is whole again.** Which `sessions.synced` ends the snapshot is exactly as above
(#427): only one received after the response to the latest `sessions.subscribe`, with no gap in
`seq` since that response. On the one that ends a resumed snapshot, the client's list becomes, for
each id in `live`, the session as upserted since the response, or else as it held it before; a
session it held that is not in `live` has ended and is dropped. Until then the PWA sends that
machine nothing, and the Windows app no answer, reply or stop for its sessions, as for any snapshot.
If an id in `live` is one the client neither holds nor was sent, or `live` cannot be read, the list
can't be whole, and the client subscribes again without `since`.

A client gives `since` only for a list that is whole: never after it dropped a `session.upsert` or a
`session.patch` it could not read, or could not apply a patch, until a full snapshot has made its
list whole again; and only while it has `instance` and a `revision` from this run of the host. The
patches that follow a resumed snapshot are on its upserts, or whole (no `base`): a host remembers
nothing of what it sent on an earlier channel.

### `sessions.focus`

Each live change woke a phone's radio: while a session works, its upsert or patch goes every 250 ms
to 4 s, and with the app open the radio barely rested (#610: the largest battery cost, estimated at
50 to 100 mAh an hour). Most of those were of sessions nobody had open. Of a host that offers
`sessions.focus`, a client says what it shows, and says it again whenever that changes:

```json
{ "visible": true, "open": "4f0c2a1e-9d3b-4e6f-8a7c-5b1d2e3f4a5b", "openQuestion": "toolu_01Hx7", "idleMs": 4200 }
```

| Field | Notes |
|---|---|
| `visible` | Required. Whether the app is on screen now (the page is visible, the window not minimised) |
| `open` | The session it shows, if any. Absent or `null` on the list, or another machine's session |
| `openQuestion` | Optional string: the id of the question the card of the `open` session shows, if it has one on screen. Absent means none, or unknown, and is never read as "every question". Only with `open`. A host holding a question push for that id may treat it as read, and must not treat any other question's as read |
| `idleMs` | Optional integer ≥ 0: milliseconds since the person's last input in the app (pointer, key, touch, wheel), measured when the request is sent. An age, not a time, so the clocks of phone and host need not agree: the host keeps `receivedAt - idleMs`. Absent means unknown, and a host must treat it as **not active**, never as 0 |

A client sends the focus when its page's visibility, open session or `openQuestion` changes (once with `"visible": false` when it goes out of sight), and after every `sessions.subscribe`. While `visible` it also says it again on the first input after a spell without any, and about once a minute while input goes on, so a host's window of recent input stays fresh; never more than once in 2 s on input alone, and nothing periodic while idle or hidden (#1190).

Result: `{}`. A request without `visible`, or with a field of the wrong type, fails as a request, as does one past 20 on a channel in 10 s: a client says it when its page or session changes, not more.
It is for a person's devices: a pinned `service` or `host` peer is neither offered nor allowed it.
What it says holds for the channel, across `sessions.subscribe` on it, until it is said again or
the channel closes; a new channel starts with nothing said, and a client says it again after
subscribing (it may send both at once). Until a channel has said anything, nothing below applies
and every live change goes at once, as without the capability.

**How a host reads `openQuestion` and `idleMs` (#1190).** `idleMs` is a whole number of
milliseconds; the host takes 0 to 86,400,000 (a day), and reads any other whole number, negative or
larger, as unknown. One that is not a whole number (`1.5`) fails the whole request, as any
field of the wrong type does, and nothing of it is kept: `sessions.focus needs visible, open and
openQuestion only as strings, and idleMs only as a whole number`. `openQuestion` without `open` is
ignored. The host uses the two only to hold back pushes from a person using one of their devices
([While the person uses a device](#while-the-person-uses-a-device-1190)); what it says about the
channel's live changes is as above. On the local pipe, a process inside an agent session is refused
`sessions.focus` ([local-pipe.md](local-pipe.md#who-may-manage-the-host)), so it cannot hold back
the person's pushes; over the relay only the person's paired devices reach it.

**The open session at once, the rest together.** A live `session.upsert` or `session.patch` of the
`open` session, while `visible`, goes at once. Of every other session (all of them while not
`visible`), the first live change after a quiet spell goes at once too; the ones that follow within
12 s of it wait for the end of those 12 s and go together, only each session's latest state, and
the next 12 s start then. A session that becomes `open` has its waiting change sent at once. A change a person must see is never held, open or not, visible or not: compared with what the channel was last sent of that session, a `question` that appeared, went or changed (its `id`, what it asks, its `decision` choices, `answerAtMachine`), a `status` entering `waiting` or `ending`, or a new `waitingOn`; and the first change of a session not yet sent on this subscription (a resumed snapshot leaves out what the client already holds, a pending question included). It goes at once and replaces anything held of the session (#697).
`session.remove`, `discovery.changed` and the snapshot are never held; a removal drops the change
held for that session. So a client is told of every session at most every 12 s, and of the one it
shows as before.

**Patches and resuming still hold.** A held change is only the latest state, sent as any other:
a patch's `base` is the event that last gave the client that session, as always. While a change is
held, no `session.patch` or `session.remove` says a `revision` past it, so a client that resumes
from the revision it holds is still sent that session ([Resuming](#resuming-without-a-snapshot));
the last held change to go says the revision of everything read since.

**Notifications.** A device with a subscription on a channel that last said `"visible": true` is
on screen and sees a turn finish: it is not sent `finished` for it ([Notifications](#notifications)).

A client that hides should still close its subscription after a while, and rely on notifications
(#610); `"visible": false` covers the time before that.

### Discovery held

```json
"discoveryHeld": { "reason": "an unrecognised copilot process (pid 4120)", "agents": ["copilot"] }
```

A host finds sessions by looking at the agents' processes, lock files and registrations. When
a pass cannot account for everything it looked at, a session missing from it may be one it
could not see rather than one that ended, so the host **holds** whatever it would decide from
an absence, and never reads the failed look as "every session ended" (design §6.3 invariant 6;
agent-ha-bridge's "session discovery uncertain … absence-based work is held"). While an agent
in `agents` is held:

* its sessions are not removed for being missing, and those the host has go on updating;
* none of them is offered for resume, a resume is `busy`, and no worktree is reclaimed
  ([v1-launch.md](v1-launch.md#while-discovery-is-held));
* the Home Assistant connector retires no session's entities, of any agent
  ([ha-connector.md](../ha-connector.md#lifecycle)).

A hold survives a restart of the host (#147). The serving host keeps the sessions it lists in
`sessions-listed.json` in its profile's folder: each one's id, agent, pid and process start time,
and never a path, which is found again as discovery finds it. To the first pass after it starts,
their processes count as known, so one whose runtime cannot be read holds its agent as it would
have before the restart. That pass brings back each of them of an agent it holds, as long as its
own process is still running (the same pid, started at the same time) and no session found has
that process now.
A session such a pass cannot see, which only the hold was keeping, is therefore still listed
after a restart, as it was before one. It is then kept like any other session a hold keeps, until
a complete pass decides. A complete first pass is believed, as ever, and so is a registration
that says the session ended, even when its runtime cannot be read.

Sessions of the other agents are removed and resumed as usual. `reason` says what holds it, in
words for a person, at most 512 bytes; a client shows it as it is ("discovery held: …", as the
Home Assistant status line did). An agent is held when:

| | `reason` says |
|---|---|
| Its processes could not be listed at all | `the copilot processes could not be listed` |
| The runtime (`node`, `bun`) a live session ran in could not be read this time, by pid and start time; or a Copilot lock or a registration names a runtime that could not be read and was written by it, not by an earlier process with its pid | `a copilot process that could not be read (pid 4120)` |
| An agent process no session or registration accounts for, for its first 2 minutes. A registration accounts only for the process that wrote it, not a later one given its pid. A session registers within seconds, so one still unaccounted for after that is not a session: the host carries on without it and logs that once. One whose start time cannot be read is never carried on without, since a new session given its pid could not be told from it. A process that only starts the agent's own executable and waits on it is counted as that executable, not as one more, while the executable runs: npm's node loader (`npm-loader.js`, `codex.js`), and VS Code's copilot shim (which Agency runs), known by its program's folder, `copilot-shim\copilot.exe`, when its child is the real CLI, given the same arguments, and the shim started no later than that child. A real CLI that starts another is never taken for a launcher. Scout's embedded `copilot --headless`, Codex's `app-server`, a host's Claude usage probe, and the `claude auth status` this host started before one ([v1-usage.md](v1-usage.md)) are nobody's session and never hold | `an unrecognised copilot process (pid 4120)` |
| A registration (Claude, Codex) that cannot be read (its details included), is a link, is not named for its own session, names no process, or changed while it was read | `a claude registration changed while it was read` |
| An unended Codex registration that names no process (its hooks fired before they could tell which Codex was theirs) holds for 2 minutes after its `Updated` (the file's write time when it has none) and while the Codex processes cannot be listed. Past that, it holds only while a Codex process nothing accounts for could be its session and have written it, and for at most an hour after its `Updated`. Such a process is an interactive one by its subcommand (none, a prompt, `resume` or `fork`; a server's name as a prompt or an option's value does not count, nor does a node or bun preload named codex; under npx or bunx the first argument named codex or naming `@openai/codex` at any version is Codex, as is the command in npx's `-c`), or one whose command line could not be read, never a server (`app-server`, `mcp-server`) or `exec` run; it is no launcher (npx, bunx, `pnpm dlx`, a version manager's shim: a `codex` whose own folder is `shims`, or Volta's `bin`; not a Codex that started another through its shell tool) of an interactive Codex executable it started, which is the session if any is and is counted itself (what started what is read up the executable's ancestry only as far as each process started no later than the one below it, so a reused parent pid ends it, and a process whose start time cannot be read launched nothing); and it started no later than one second after the registration's `Updated` (the slack the host allows elsewhere for start times the OS rounds; a wall clock stepped back by more than that between the Codex starting and its hook firing can let the grace end while it still waits), or its start time cannot be read. The reason then names that process, and the host logs once that it holds for it. Past both, no running Codex is taken for its session (one that exited before its `SessionEnd` left it behind): the host carries on without it and logs that once. A session left waiting on its hooks-trust question for more than the hour is carried on without too. A Claude registration that names no process holds as above | `a codex registration names no process`, or past the grace `a codex registration names no process (codex pid 960 could have written it)` |
| Claude Code's own `sessions/<pid>.json` for a running Claude process (of its generation) that cannot be read whole, unless that process is a host's usage probe known by its exact options, which is set aside before its file is read. One that says the run is not interactive (SDK, print mode), or whose process is a host's usage probe (which Claude Code 2.1.316 records as interactive), accounts for its process as nobody's session | `a claude session file could not be read (pid 4120)` |
| A session discovery found running whose transcript could not be read when it was first found, until it can | `a claude session that could not be read` |
| A session, registration or Codex thread-lock folder that is there but cannot be read or listed, or is a link (a thread lock only while a Codex terminal runs). Only the OS saying a folder is not there is absence | `copilot's session folders could not be listed` |

The next complete pass clears the hold with no restart, and removes what has really gone. A hold is said as soon as it begins, and said to clear (or to let an agent go) only once the sessions that pass found are listed, so a client never sees a session neither listed nor held; a pass that does both names the new agent at once and keeps the old one until then. An
agent that is not installed (no registration folder, and for Claude no `sessions` folder) is never held.

### `session.answer`

Answers a session's pending question. Request `data`:

```json
{
  "id": "<session id>",
  "question": "<question id>",
  "request": "6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b",
  "fields": { "answer": "s6" }
}
```

| Field | Notes |
|---|---|
| `id` | The session |
| `question` | The `question.id` the client was shown. The host answers only that question: if the session is no longer asking it, nothing is typed |
| `request` | A fresh UUID per answer the person gives. A host remembers the outcome of each request, **whatever it was** (`stale`, `busy` and `refused` included), for 15 minutes, across host restarts, and returns it again for a repeat, typing nothing more. A repeat that arrives while the first copy is still being delivered waits for it and gets the same outcome, never `busy`. A client resending after a lost response or a reconnect therefore never answers twice. After `busy` or `refused`, a client that tries again uses a new `request` |
| `fields` | One value per field, by `name` (below). Omit a field to leave it unanswered; `required` fields must be present |
| `text` | Instead of `fields`, for a question with no fields: the free-text answer |
| `progress` | Optional, `true` from a client that saw `sessions.progress`: it hears the answer's progress before its result ([Progress](#progress)). Absent, nothing changes |
| `confirm` | With `{ "decision": "approve" }` on a question with `escaped`, and nowhere else: the command the person's device signed when they pressed the confirmation ([Escaped approvals](#escaped-approvals)) |

A field's value, by type:

| `type` | Value |
|---|---|
| `choice` | One option's `value`, or `{ "other": "<text>" }` when `allowOther` |
| `multi` | An array of option values, which may also hold one `{ "other": "<text>" }` when `allowOther` |
| `boolean` | `true` or `false` |
| `text` | A string |
| `number` | A number |

Free text, in `text` or `other`, is at most 4 KiB of UTF-8; the whole request is at most
16 KiB. A host collapses control characters and line breaks to spaces before typing.
Free text of nothing but what shows as nothing (white space, control and format characters,
default-ignorables such as U+3164, marks with no base; see `session.reply`) is empty, and
`refused` as such.
A field's `constraints` (below) are checked too: a value outside them is `refused`, with why.
An empty or blank value for a `required` `text` field is `refused` as needing an answer: `""`,
or text that is empty once its control characters (Cc) are made spaces, as a host types them,
and JavaScript's `trim()` is applied (so `"   "`, `"\u0085"` and `"\u001b "` alike). Typing
nothing records no answer, and blank text might be recorded trimmed to nothing.
`Answers.Check` in C# is the validation every host applies before typing anything.

Result: `{ "outcome": "<outcome>", "reason": "<text>", "stage": "<stage>", "code": "<code>" }`,
with `reason` present for anything but `delivered`, `stage` only on `unconfirmed` (below),
`code` only on a `refused` whose reason is one of the [answer reasons](#answer-reasons), or on a
`stale` from a part-answered form the host reset (`formReset`), and
`timings` for a request that asked for progress ([Progress](#progress)):

| `outcome` | Meaning | Typed? |
|---|---|---|
| `delivered` | The host typed the answer and the agent's transcript recorded it | Yes |
| `unconfirmed` | The host typed it, or may have done so before stopping, but could not confirm the agent recorded it. It never retries on its own; the client says to check the conversation | Yes or unknown |
| `stale` | The session is no longer asking that question: answered at the machine or by another device, or gone | No |
| `busy` | Another answer or a reply to this session is being delivered | No |
| `refused` | The host will not type it: an invalid answer, a question the client should not answer remotely (`truncated` or `answerAtMachine`), a prompt the host has already typed an answer into once, an elevated or otherwise unreachable session, an agent or platform without delivery, or agent-ha-bridge active for this user | No |

A client treats an outcome it doesn't know as `unconfirmed`, never as `delivered`. A question
still visible after its prompt was claimed is listed `answerAtMachine`, not answerable remotely.

An `unconfirmed` answer also carries `stage`, how far it is known to have got
([#467](https://github.com/danswett/agent-bridge/issues/467)):

| `stage` | Meaning |
|---|---|
| `typed` | The keys were typed into the prompt, or may have been before the host stopped, and the session has recorded nothing for that question since. It may not have been submitted: Copilot's form once took an answer's last Enter as a new line in its text field |
| `recorded` | The session recorded an answer to that very question, but not the one typed (or the call ended in an error) |

### Answer reasons

Some reasons a client says in fixed words of its own, the same in every client: on a field, as
`otherUnavailable`, and on a refusal, as its `code` beside a `reason` that is the same
sentence (so a client that knows neither still says why). `formReset` is said only on a
`stale` answer, beside a `reason` that is the host's own account of it, never on a field. A
client that doesn't know a code reads it as absent: no disabled Other, and the `reason` as usual.

| Code | Sentence | A client shows |
|---|---|---|
| `otherNotSendable` | "Your own answer can't be sent from here yet. Pick an option, or answer at the machine." | On a field: Other, its label unchanged, disabled, and the sentence as one muted line under the choices and as their accessible description. On a refusal: "Not sent · " and the sentence |
| `formReset` | "The session moved on from this question. Your answer was not delivered." | On a `stale` answer only: the host had left the form part answered, cancelled it (one Esc), and the session asked something else or nothing again, so none of the answer reached the agent ([#1123](https://github.com/danswett/agent-bridge/issues/1123)). "Not sent: " and the sentence, never "answered elsewhere" |

`otherNotSendable` is a **temporary exception to #876** ("offer Other exactly when the CLI
does"), decided by Product for #510: Copilot CLI 1.0.105 offers Other on a lone option list,
but the host cannot yet prove that its pinned input box is empty. The read-only preparation
in [#908](https://github.com/danswett/agent-bridge/issues/908) does not remove this exception
or enable typing ([host answers](../host-answers.md)).

Accepted by the session is `delivered`, never a stage. A stage a client does not know reads as
`typed`; an older host sends none. In the same host run, and after a restart while the host
still keeps the answer's words ([host answers](../host-answers.md), step 6: at most a day), a
repeat of a request whose outcome was `unconfirmed`
reads the session's record of that same question again, typing nothing: an answer recorded
since (submitted at the machine, or recorded late) comes back `delivered`, and from then on
stays so, and one recorded differently comes back `recorded`. It never answers a newer
question. A client may therefore send the same request again after a reconnect, within the
10 minutes the host remembers it, to learn what came of a `typed` answer.

Typing into a live session is a single writer's job, and the host serializes it: one
answer or reply per session at a time (`busy` otherwise), checked against the transcript
immediately before typing and confirmed by identity, never by clock, after.

**One attempt per prompt.** Once a host has typed any key into a prompt, it never types
into that prompt again, whatever the request and across restarts: nobody can say what a
half-typed prompt holds, and a second answer would land on top of the first. Only an
attempt that typed nothing (the session's console could not be reached) leaves the prompt
answerable. A host records the attempt and request before the first key, and refuses when it cannot.
If it restarts with a claim but no recorded outcome, a resend within 15 minutes by the request's
own sender is `unconfirmed`, not `refused`; the question is listed `answerAtMachine`. A planned host stop excludes new
deliveries and waits up to 15 seconds for typing and confirmation; on timeout the host stays
running rather than leaving a worker to type after it exits.

The same answer sent again for such a prompt under a different request UUID by the **same
sender** (a phone that was reloaded keeps its UUIDs in memory only) is that request sent
again, not a second attempt: while the host still keeps the first answer (as long as the
prompt's claim, across restarts), it gets the
first outcome and nothing is typed. An `unconfirmed` one is read again from the session as
above, however long after, so it becomes `delivered` once the session has recorded it; while
nothing is recorded it stays `unconfirmed`, with a reason that says only that nothing was
typed again because the request repeats an answer already typed once; that holds for every
repeat of an unconfirmed answer, under its own UUID or another, and whatever its stage (which
still says how far the first got). A request UUID belongs to the sender, session and question it
was first admitted for: replayed by another sender, or under another question, it is a request
of its own, and nothing of the first's outcome or progress is given. Only a *different* answer, or the same words from **another sender**,
is `refused` for a prompt already typed into, and the two are refused alike, so that no
sender can learn what a first answer said by guessing it. A sender is the channel's peer (a
device); the host has no person-level identity, so a second device of the same person gets the
plain refusal. A host keeps the first answer's sender, hash and outcome with its record of
attempts, so all of this holds across a restart as before it: a known request UUID replayed
after a restart by another sender, or under another session or question, gets exactly what an
unknown UUID with the same answer would (the plain refusal, or `stale`), never the first's
outcome. While an answer is `unconfirmed` the host keeps its words, privately, to read the
session again; when it cannot keep them safely (it already keeps as many as it may, or its
private folder could be read by others) it refuses the answer before typing anything, with the
reason "the machine can't keep this answer safely until it's confirmed, so nothing was typed;
answer at the machine" (no `code`; a client shows the reason as it shows any refusal's).

What a host answers, by agent. agent-ha-bridge is the baseline; answering Copilot
permission prompts goes beyond it, at the user's request:

| Agent | Prompt | `kind` | Answered | `delivered` when |
|---|---|---|---|---|
| Copilot CLI | `ask_user`: free text, or a form of `choice`, `multi`, `boolean`, `text` and `number` fields | `form` | Yes, for a form the host can read on screen; free text, and a form it cannot, is `answerAtMachine` ([host answers](../host-answers.md)). Not with Other words for now: a lone list has `otherUnavailable` (`otherNotSendable`, a temporary exception to #876), since the host cannot yet prove its pinned box empty | Its `human_response.recorded` (or, for free text, its completion) holds the answer typed |
| Copilot CLI | Permission request for a shell command, path access, a file edit or read, a URL, an MCP tool or a custom tool | `approval` | Yes, with `sessions.approve`: approve once or deny, nothing that grants more | Its `permission.completed` for that request records `approved` (approve), or `denied-interactively-by-user` with no message or `cancelled` followed by the session's abort of the turn (deny) |
| Copilot CLI | Any other permission request (hooks, memory, workflows, extensions), a request to run outside the sandbox, one whose details cannot be read, or any on a host with `permissions.remoteApproval` off | `approval` | No: `answerAtMachine` | |
| Claude Code | `AskUserQuestion`: one to four questions, each a `choice` or a `multi` of its options | `form` | Yes, but not with free text (no field has `allowOther`), and a `multi` needs at least one option | The `tool_result` for its `tool_use` id records every question's answer as typed |
| Claude Code | Permission prompt (from agent-ha-bridge's hook) | `approval` | No: `answerAtMachine` | |
| Codex | Approval recorded by its `PermissionRequest` hook (agent-ha-bridge's, or this host's intake) | `approval`: `Allow **<tool>**?` and the whole command in a fence longer than any run of backticks inside it. One whose marker holds only the hook's 300-character preview, or whose command is past 16,384 characters, is `truncated` (#209). Its `id` is `<DecisionId>#<digest>`: the marker's generation and 16 lowercase hex digits of a digest of what is shown (tool, whole command, whether it was cut, the hook's preview line, the message, and whether it is `truncated` or answered at the machine), so a generation Codex lists again with another command is another question (#958) | Yes, unless `truncated`; the host rebuilds the question from the marker as it reads immediately before typing and types nothing unless its id is still the one answered | Never: Codex records nothing that says how an approval was answered, so a typed one is `unconfirmed` |
| Codex | Approval seen only in the rollout, or one agent-ha-bridge already typed into | `approval` | No: `answerAtMachine` | |

#### Copilot permission prompts

A Copilot permission prompt's `message` carries in full what it would allow: the command,
the path or paths, the file and its diff (or, without one, its whole new contents; an edit
showing neither is `answerAtMachine`), the URL, or the tool with its arguments, and the
intention Copilot gave. One too large to send whole is `truncated`, and refused like any
truncated question. The intention, and an MCP tool's title or a custom tool's description, are
the agent's own words, sent as literal text (#212): every character that would start Markdown
(code, emphasis, a link, an image or an address made a link, raw HTML, an entity, a table, a
heading, quote, list or rule at a line's start) is backslash-escaped and a line's leading
whitespace dropped, so they render as the words they are and can never open a fence that the
command's own fence would then close. What is approved is always in fenced code after them, the
fence longer than any run of backticks inside it; a custom tool with no arguments has its
description, all it shows of what runs, in that block as written instead (shared vectors:
`tests/fixtures/approvals/literal-intentions.json`). Its `id` is `<requestId>#<digest>`: Copilot's request id and a hash of
the message as built from Copilot's own words, before anything in it is escaped for display.
An answer is bound to both, so the host refuses (`stale`) if that request is
no longer waiting or now reads differently from what the client was shown.

What a person reads must be what runs (#220). In the `message`, every character that is not
drawn where it runs is a visible escape (`\u202E`, or `\u{E0041}` past the first plane): format
characters such as bidi controls and zero-width spaces and joiners, controls other than newline
and tab, private-use and unassigned code points, unpaired surrogates, U+2028 and U+2029, and
every default-ignorable code point; so is every right-to-left letter (`\u05D0`), which reorders
what is drawn around it without any control. Approve is still offered for such a prompt, as the
owner's "escape and allow" rule has it (#253, decision 3), now only through the escaped-command
confirmation ([Escaped approvals](#escaped-approvals), #803), and not while other permission
requests wait, when the listed one is `answerAtMachine` (below, #690). The digest stays over the original text
(and over a tool's name as written, where it held such a character or right-to-left letters, and
over whether the prompt is held at the machine for a reason the request itself gives: a kind no
host drives, details that cannot be read, or a request to run outside the sandbox). It is not over
`answerAtMachine` set for any other reason, `permissions.remoteApproval` off or another request
waiting that it cannot be told from (#690), which can clear under the same `id`. So a prompt holding a real U+202E and
one holding the six characters `\u202E` never share an id. An ordinary prompt's digest is a hash of
`<kind>\n<message>`; one with more to it, or with a NUL in its message, is hashed in a
length-prefixed form starting with a NUL, so no message can spell another prompt's identity (a
custom tool described as `run\n\0at-machine` is not one described as `run` run outside the
sandbox). A message over the size limit is escaped only as far as it is sent, and is `truncated`.
A tool's arguments, written as JSON, give a carriage return, a backspace and a form feed as
`\u000D`, `\u0008` and `\u000C`, never JSON's `\r`, `\b` and `\f`, so a client reads them as the
characters they are; and a client reads a pair of surrogate escapes (`\uD83D\uDE00`, as JSON writes
a character past the first plane) as the one character it is, an emoji not hidden, a tag still
hidden. These rules hold for every agent's
`approval`, and a client escapes an older host's approval that still carries such a character.
Every card draws an approval as its source: the PWA's and the Windows app's exactly as written
(#429, #323), Home Assistant's in one code block nothing in it can close
([ha-connector.md](../ha-connector.md)). Home Assistant's and the app's cards still answer in the
terminal one with a character reference such as `&#x202E;` that a Markdown renderer would decode
to such a character (with or without a semicolon, with any number of leading zeros, or an HTML5
name such as `&ZeroWidthSpace;`, or `&nbsp;` and the other spaces' names since #546 hid them). The
Needs you lane
never offers one inline, its escapes included, and a card says what its escapes stand for
("Contains hidden characters", "Contains right-to-left text") above it. Those two cards also hold
at the machine an approval Markdown would not draw as written (the agent's own words closing the
command's fence, a link, an image or HTML, text after a block, or a line in a block shaped as a
fence; `ApprovalShape`), and both say a `truncated` approval is cut before they judge its shape,
since one cut usually ends inside its block. These rules cover characters only, not Markdown:
whether an approval's prose can be drawn as Markdown (a link or image hiding what runs) is #212
for Copilot's intention, and #209 for a Codex approval's command, which a host now sends whole in a
fence longer than any run of backticks inside it. An older host sent it as Markdown prose
(`Needs approval: <command>`), the hook's 300-character preview cut to 297 characters and `...`
with nothing marking it `truncated`, so every client answers a Codex approval not in the new form
(`ApprovalShape.IsFencedCommand`) at the machine: the PWA draws it exactly as written, and the app
and Home Assistant cards show it as its source.

The answer is `{ "decision": "approve" }` or `{ "decision": "deny" }`, nothing else.
Approve is the prompt's plain "Yes", once: no option that grants more ("don't ask again",
"remember this path", "approve for the session", read-only directory access) can ever be
chosen remotely. Deny is the prompt's "No" with no message to Copilot, taken by Escape as at
the machine, which also **stops Copilot's turn**: the command does not run, and Copilot does
not go on to try something else unattended. The session is then idle, and a reply can tell
it what to do instead. A deny takes no text, so nothing can be typed into the prompt's
message box.

Copilot can have several permission requests waiting at once, one for each of a batch of
parallel tool calls, and draws one of them at a time. The host lists the earliest still
waiting, with `waiting` saying how many there are, and the next as a new question once it is
answered. Immediately before typing, the host checks the transcript (that request still
waiting and reading as shown, and no `ask_user` waiting beside it), then reads the session's
screen and finds the option by its label, never by its position, under rows showing what the
prompt is for, and checks the transcript again. While others wait, the screen, not the order
asked, says which request is shown, identified positively as Copilot CLI 1.0.105 draws it:
no waiting request holds a line-drawing character, a tab or a character drawn other than as
it runs, in a value or a property name (an argument's name included); the options are inside one round frame whose top edge is on screen; every row in it
above the options is this request's dialog (its title, the rule, each part of its body, a
box's lines one to a row exactly as given, and its question) and nothing else; and the
request is the earliest still waiting, with no other waiting request that would be drawn the
same once all white space is gone. Only shell and path prompts are identified so. Copilot
draws a waiting shell or other request first, then a hook's, then a path request, then a URL
request, each in the order asked, so the earliest is drawn only when nothing waiting draws
ahead of it: a path request asked before a shell one is `answerAtMachine` until the shell one
is answered. A listed prompt that cannot be told apart so is `answerAtMachine` while that holds:
another waiting request is drawn ahead of it, its kind is not shell or path, its own text has a
run of spaces that can wrap onto a blank row, any waiting request's text (its own included)
holds a line-drawing character, a tab, or a character a host shows escaped or a reference to
one (`&#x202E;`), another waiting
request has nothing drawn to check the screen against (a read naming no path), or another
would be drawn the same. So an approval listed with `escaped` while others wait is
`answerAtMachine`, Deny included, until it waits alone (unless its only escapes are in a path
request's access kind, which its dialog never draws as written): the confirmation below is not
offered for it. It is answerable again under the same `id` once it can be told apart. Otherwise
the answer is `refused`, naming the other where its dialog is the one drawn ("the machine is
showing a different permission (path access, request ed05e9d7)"), and the key is held from
the frame's top edge. It is never retargeted to the request on screen. The key goes
only if those rows and the options, and which is highlighted, still read the same at the
moment it is typed.
`permissions.remoteApproval` in the host's own settings turns this off on a machine
([host-answers.md](../host-answers.md)).

#### Escaped approvals

An approval holding a character a host shows as an escape (a bidi control, a zero-width or other
hidden character, a control but newline and tab, a right-to-left letter; #220) carries
`escaped`, set by the host from what it actually escaped (#803). Clients never infer it from
`\u` text, which an agent can write itself.

A Copilot permission prompt listed with `escaped` while other permission requests wait is
`answerAtMachine` until it waits alone, since the host cannot tell such text apart on the
session's screen, unless its only escapes are in a path request's access kind
([Copilot permission prompts](#copilot-permission-prompts), #690): everything
below gives way to that, so no client offers the confirmation or Deny for it, Home Assistant's
Deny included, and a `session.answer` for it is `refused`.

```json
"escaped": { "shown": "<64 hex>", "marks": [[12, 6], [40, 6]], "approve": "confirm" }
```

| Field | Meaning |
|---|---|
| `shown` | SHA-256, lower-case hex, of the question's `message` as sent, in UTF-8. The question id binds what the agent asked (a Copilot permission prompt's `<requestId>#<digest>`, a Codex approval's `<DecisionId>#<digest>`); `shown` binds what the person read |
| `marks` | The host's own escapes in `message`: `[start, length]` in UTF-16 code units, in order, **one mark for each escape** (`\uXXXX` with four upper-case hex digits, or `\u{XXXXX}` past the first plane), adjacent ones too, and nothing else in it. Text an agent wrote itself is never in one. A client checks each mark against that grammar and draws every character of the message; a question whose marks don't check is held at the machine for Approve, as a malformed `escaped` is. At most 512; a question with more carries the first 512 and `approve` `tooMany`, and is not `truncated` for it, so Deny still goes |
| `approve` | `confirm`: Approve takes the confirmation below. `machine`: this machine's `permissions.escapedApproval` keeps Approve at the machine. `tooMany`: more escapes than `marks` carries, so what a person reads is not described exactly and Approve is taken only at the machine, whatever the policy; Deny goes as for any approval. A client that reads any other value holds Approve at the machine |

**Deny** goes as for any approval, from every surface, Home Assistant included.

**Approve** is `{ "decision": "approve" }` plus `confirm`, a [signed command](v1-transfer.md#the-signed-command)
the device signs when the person presses the sheet's own button, never before:

```json
{ "v": 1, "action": "session.approve:escaped", "request": "<the answer's request>",
  "principal": { "kind": "user", "device": "<signer device id>" },
  "target": { "machine": "<this machine>", "session": "<session id>",
              "question": "<question id>", "shown": "<escaped.shown>" },
  "to": "<this machine>", "issuedAt": 0, "expiresAt": 0 }
```

A host takes it only:
* through the relay, on a channel from the device that signed it. Never its pipe, which agents
  and the MCP server reach, nor its Home Assistant connector, whose users include agents;
* from a device the relay lists now as a person's (the PWA or the Windows app), not the machine
  itself, and one this machine approved: `permissions.escapedApprovalDevices`, or while that is
  absent `launch.remoteFolderTrustDevices` while `launch.remoteFolderTrust` is on ([host-config.md](../host-config.md));
* with a MAC for this machine, `action` `session.approve:escaped`, `to` and `target.machine` this
  machine, `target.session` and `target.question` the answer's (the question's `id` as listed: for
  a Codex approval `<DecisionId>#<digest>`, never the bare `DecisionId`, #958), `request` the answer's own,
  `target.shown` equal to `escaped.shown` and to the digest of the message the host would send
  now, computed again, and a lifetime of at most 5 minutes;
* while `escaped.approve` is `confirm`, and never for a `truncated` question.

It checks all of this when the answer arrives and again once the answer's turn to type has come,
so a device removed from the list, or a confirmation past its lifetime, meanwhile stops it before
any key. A command changed meanwhile is another question, with another id: a Codex approval is
rebuilt from its marker as it reads immediately before typing, never from the session's listing,
and one that no longer rebuilds to the id answered is `stale` (#958). A `confirm` is good only for
the escaped message it was signed for: an Approve carrying one is `refused` when the question it
answers has no `escaped`, as when its digest differs.
Everything else about the approval is as above. A resend of the same answer carries the same
command and gets the first outcome; a new press is a new request and a new command. Anything
missing or wrong is `refused` with the reason, nothing typed: an older client's Approve with no
`confirm` is refused saying the approval needs the escaped-command confirmation, and Deny still
goes from it.

A budget that has to cut the message keeps `escaped` with it: the marks still inside, `shown`
of what is now sent, and the question `truncated`, which is never confirmed.

### `session.reply`

Sends a session a prompt, typed into the agent's own input as if at the machine, whether
the session is idle or still working. Request `data`:

```json
{
  "id": "<session id>",
  "request": "0b7e3c1d-5f2a-4c6e-8d9b-1a2b3c4d5e6f",
  "text": "Looks good. Open the pull request."
}
```

| Field | Notes |
|---|---|
| `id` | The session |
| `request` | A fresh UUID per reply the person sends, with exactly `session.answer`'s semantics: every outcome is remembered for at least 10 minutes and returned for a repeat, which types nothing; a repeat of a reply still being delivered waits for it |
| `text` | The prompt: at most 16 KiB of UTF-8. Not blank, unless the reply has attachments |
| `attachments` | Optional. Files and images for the agent, in order: `[{ "id": "<attachment>" }]`, at most 10, each sent whole by **this device** with `attachment.start` and `attachment.chunk` first ([Attachments](#attachments)). Absent, `null` and `[]` mean none. A host without `sessions.attach` refuses a reply that has any |
| `progress` | Optional, as for `session.answer` ([Progress](#progress)) |

**Line breaks become spaces.** Every control character, line breaks and tabs included,
and the Unicode line and paragraph separators are typed as spaces, so a reply always
reaches the agent as one line. This is agent-ha-bridge's behaviour, kept exactly: its
reply path (`Get-CopilotInjectableText`) replaces every control character with a space,
because an Enter inside the words would submit part of the reply. A client may let the
person type several lines, and says they will arrive as one.

A reply whose first visible base character is `/` or `!` is `refused`: Copilot CLI and
Claude Code would run it as a slash command or a shell command on the machine, not give it
to the agent, and nothing could confirm it. Whatever shows as nothing in front of it is
passed over first (`Invisible` in C#, `INVISIBLE` in the PWA): white space, control
characters, format characters (Cf: U+FEFF, U+200B to U+200D, U+2060, bidi marks, tags),
default-ignorable code points (Hangul fillers such as U+3164, variation selectors, U+034F),
combining marks, unassigned code points and U+2800. Agents drop these: Copilot CLI trims its
input with JavaScript's `trim()`, which removes U+FEFF, so `"\uFEFF/clear"` ran as `/clear`,
and Claude Code strips the zero-width, filler, variation-selector and tag characters before it
sends ([#448](https://github.com/danswett/agent-bridge/issues/448)). A reply of nothing but
those characters, with no attachments, is `refused` as empty.

Result: `{ "outcome", "reason" }`, the outcomes of `session.answer`, with `timings` for a
request that asked for progress:

| `outcome` | Meaning | Typed? |
|---|---|---|
| `delivered` | The host typed the reply, and the agent's transcript then recorded a prompt from the person (not a subagent's, a scheduled one or any with another source) whose text is the reply as typed, whitespace runs aside, **with every attachment attached**. Each recorded prompt confirms one reply only | Yes |
| `unconfirmed` | The host typed it but saw no such prompt: the agent recorded something else, or the reply without all its files, the session ended, or nothing came within the host's wait. A session that is working takes a prompt typed now at its next step (Copilot records it then, as `delivery: "steering"`), so a host waits for up to a minute. Never retried | Yes |
| `stale` | The session has ended, or its process has | No |
| `busy` | An answer or another reply to this session is being typed | No |
| `refused` | The host will not type it: an invalid reply (above), **a question or permission prompt pending in the session** (the words would answer it, and Enter accept it; answer it first, at the machine for one marked `answerAtMachine`), a transcript the host cannot read to rule that out, an attachment the host does not hold (expired, removed, sent by another device, still arriving, or gone with an earlier reply) or cannot hand over, an elevated or otherwise unreachable session, an agent or platform without delivery yet, or agent-ha-bridge active for this user | No |

Copilot CLI, Claude Code and Codex sessions take replies. A Claude Code or Codex reply is
typed only into the agent's input box as its screen shows it, since without
agent-ha-bridge's hooks a host cannot otherwise see their permission prompts and questions;
it is `refused` when that box is not on screen, or for Codex already has words in it. Codex takes only
images as attachments; a Codex reply with any other file is `refused`.

A reply is never typed while the session is asking anything, which a host checks both in
what it reports and, immediately before typing, in the transcript itself. The per-session
serialization is shared with answers, but it ends once the reply's keys have gone: a
question the agent asks while the host waits for its prompt to be recorded can be answered
at once.

### Progress

A reply to a working session can take a minute to settle, and a client that hears nothing
until then can only say "sending" ([#598](https://github.com/danswett/agent-bridge/issues/598)).
So a host that offers `sessions.progress` reports how far a `session.reply` or
`session.answer` has got, to a client that adds `"progress": true` to its `data`. Without
it, nothing about the exchange changes: one response, with the result as above.

With it, the host sends zero or more frames on the channel **before** the response, each
with the request's `re`, a `progress` object and **no `ok`**:

```json
{"re": 7, "progress": {"stage": "received"}}
{"re": 7, "progress": {"stage": "typed", "working": true}}
{"re": 7, "ok": true, "result": {"outcome": "delivered", "timings": {"typed": 352, "recorded": 1190, "confirmed": 1361}}}
```

| `stage` | When | A client shows |
|---|---|---|
| `received` | As soon as the host has the request, before any check (so a request refused at once still hears it) | Sent |
| `typed` | Every key has gone, so the outcome will be `delivered` or `unconfirmed`. `working` is `true` when the session was working: the agent takes the prompt at its next step, as Copilot's `steering` | Typed, or Queued when `working` |

A client ignores a `stage` it doesn't know, and a progress frame never completes its
request, whatever else it holds. A request refused before any key went hears only
`received`. A repeat of a request still being delivered (the same `request` UUID) hears its
own `received`, then the stages the first one has reached, at once, and the rest as they come;
one sent after the outcome hears `received` and, if the keys went, `typed`. Nothing follows the
response.

`timings` in the result: milliseconds after the host received the request, by the host's
clock. Absent fields were not reached, or are not known. For a repeat, they count from the
repeat's own arrival, and a stage already reached by then reads `0`.

| Field | When |
|---|---|
| `typed` | The last key went |
| `recorded` | The agent's own timestamp for the prompt or answer it recorded, where its transcript has one (Copilot replies and `ask_user` answers, Claude Code and Codex replies) |
| `confirmed` | The host matched that record to the request: only for `delivered` |

The same framing runs on the local pipe ([local-pipe.md](local-pipe.md)). The host also logs
every reply and answer's stages in milliseconds, progress asked for or not
([host-replies.md](../host-replies.md#timings)).

### Attachments

Files and images go with a reply, as they can be pasted or attached at the machine. A
client sends each file to the host first, in small chunks inside the channel, then names
them in `session.reply`. Each file is at most **256 KiB** this way (100 MiB on the local
pipe, `sessions.attach.direct`). A larger one, up to **100 MiB**, goes through the relay's
blob store where the host offers `sessions.attach.blob`
([Large files](#large-files-through-the-blob-store)); a reply names it the same way, by `id`.

**What the agent is given (Copilot).** The host writes each file into a private folder
(below) and types the reply as agent-ha-bridge does: `@<path>` for each file, in order,
then the text, one line, then Enter: `@C:\…\attachments\9f2c…-screenshot.png What do you
see?`. Copilot CLI attaches a file a prompt mentions that way. A path can't hold
whitespace (nothing quotes it), so a host whose folder has a space in its path refuses
replies with attachments. The text rules above apply to the text alone, so a reply of
files only is allowed, and one whose text starts with `/` or `!` is still refused.

**Names and types.** Any file is accepted. A host keeps a safe form of the name: the
last path segment, letters, digits, `.`, `-` and `_` (anything else becomes `-`), a stem
of at most 48 characters, and an extension only if it is 1 to 12 letters or digits.
Copilot decides how to read a file from its extension, so for PNG, JPEG, GIF, WebP,
HEIC/HEIF and PDF the host goes by the file's own bytes: it gives them the matching
extension (a pasted `image` becomes `image.png`), and refuses a reply whose file is named
as one of these formats but is not one. `type` is informational only.

#### `attachment.start`

Request `data`:

```json
{
  "attachment": "4c1d7e2a-9b3f-4e6a-8c5d-0f1e2d3c4b5a",
  "name": "Screenshot 2026-10-28.png",
  "type": "image/png",
  "size": 183204,
  "sha256": "n4bQgYhMfWWaL-qgxVrQFaO_TxsrC4Is0V1sFbDwCgg"
}
```

| Field | Notes |
|---|---|
| `attachment` | A fresh UUID for this file, the client's. Starting again with the same id and the same `name`, `size` and `sha256` resumes it (after a reconnect, say) |
| `name` | 1 to 255 bytes of UTF-8. A pasted image with no name is sent as `image.png` or similar |
| `type` | Optional MIME type, at most 127 bytes |
| `size` | Bytes, 1 to 262,144; up to 104,857,600 with `blob`, or on the local pipe |
| `sha256` | The SHA-256 of the whole file, base64url without padding (43 characters) |
| `blob` | Optional, with `sessions.attach.blob`: `{ "id": "<ticket>", "key": "<32 bytes, base64url>" }`, a file already sealed and uploaded for this host ([Large files](#large-files-through-the-blob-store)). Absent: the file comes in chunks |

Result: `{ "received": 0, "complete": false }`: how many bytes the host has, and whether
that is the whole file. A file a typed reply has already used reads as complete, so a
client resending after a lost response sends nothing again. A host holds at most 32
attachments at once across devices; one more fails.

#### `attachment.chunk`

Request `data`: `{ "attachment": "<id>", "offset": 11264, "data": "<base64>" }`. `data` is
standard base64 of at most **11 KiB** (11,264 bytes), which keeps the request under 16 KiB
as sent: a chunk never holds an answer or a reply back by more than one small record. A
client sends one chunk at a time per file, each at the `received` the host last gave, and
waits for its response. A chunk the host already has (a resend) changes nothing; one at
any other offset fails, saying the offset expected.

Result: `{ "received": 22528, "complete": false }`. When the last byte arrives, the host
checks the size and SHA-256: `complete: true` if they match. If they don't, the request
fails and the host drops the file; the client starts it again with a new id.

#### `attachment.discard`

Request `data`: `{ "attachment": "<id>" }`. Result: `{ "discarded": true }`, or `false` for
an id the host does not hold for this device. A client sends it when the person removes a
file before sending, so the host frees it at once rather than when it expires.

**Failures.** `attachment.*` requests that fail a check (a field out of range, a wrong
offset, a hash mismatch, too many held) fail as requests (`ok: false`) with the reason, as
`push.*` do. A host without `sessions.attach` answers them `unknown op`.

#### Lifetime and privacy

* **One device's.** An attachment belongs to the device that sent it (the channel's peer).
  Another device can neither add to it, use it, see it nor remove it.
* **Held until used.** A host keeps a file in memory while it arrives and until a reply
  uses it; one over 256 KiB (from the blob store, or on the local pipe) is written instead,
  as it arrives, to `incoming/` in the private folder below, never a shared temporary
  folder. A host holds at most 1 GiB of those at once. One no reply uses within
  **15 minutes** of its last chunk (or of the last `attachment.start` that asked about it)
  is dropped, and its file deleted.
* **Staged only to be typed.** Just before typing, the host writes the reply's files into a
  folder of its own profile that only the user can open: on Windows a DACL that inherits
  nothing and grants only the user, as owner; elsewhere mode `0700`, files `0600`. Names
  start with a random 128-bit prefix, so nothing is ever overwritten. A folder that can't be
  made private, or is a link, is never written to and the reply is `refused`.
* **Gone after use.** Once the reply is typed (`delivered` or `unconfirmed`), its files leave
  the host's memory and `incoming/` and cannot be used again. The staged copies stay **24 hours**, since a
  working session reads the file only when it takes the prompt, then are deleted by a sweep
  that also runs when the host starts. A reply that types nothing (`refused`, `busy`,
  `stale`) deletes its staged copies at once and leaves the files held, so the person can
  send again with a new request.
* **Never in the clear outside the two ends.** Chunks travel inside the end-to-end channel,
  so the relay sees only frames of about 15 KiB. A host never logs names or contents, only
  counts and sizes.

A client sends a reply's files before the reply, and again (from what the host has) on
every resend of it, so a reply resent after the host restarted still finds them.

#### Large files through the blob store

With `sessions.attach.blob`, a file over 256 KiB goes through the relay's blob store
([blobs.md](blobs.md)) instead of in chunks:

1. The client asks its relay for a slot for the host's device (`blob-put`, the sealed and
   padded size), seals the file with a fresh key bound to the ticket's id, and uploads it
   straight to the URL it was given. It never passes through the relay.
2. It sends `attachment.start` with `blob: { id, key }`, and the file's `name`, `size`,
   `type` and `sha256` as for any attachment. The key travels only here.
3. The host fetches the blob itself, on its own relay connection (only it, the device the
   blob was put for, can), opens it as it arrives into `incoming/`, and checks the size and
   SHA-256. It then deletes the blob from the store, whether it was right or not.
4. The client sends `attachment.start` again, unchanged, until it reads `complete: true`
   (or fails), then names the attachment in `session.reply` as usual. `received` counts the
   bytes the host has opened, for progress.

A blob that does not open, does not match its SHA-256 or cannot be fetched fails the next
`attachment.start` for it once, with the reason, and is dropped; the client seals and
uploads it again under a new id. A host that has no relay with a blob store just then
fails `attachment.start` with a `blob` and stops offering `sessions.attach.blob`.
`attachment.chunk` for an attachment started with a `blob` fails.

### `session.stop`

Ends a session, as agent-ha-bridge's End session button does. Request `data`:

```json
{
  "id": "<session id>",
  "request": "3d5e7f90-1a2b-4c3d-8e4f-5a6b7c8d9e0f",
  "confirm": "q2vU3dJ7n0x-AbC_9zYw1A"
}
```

| Field | Notes |
|---|---|
| `id` | The session |
| `request` | A fresh UUID per press, with exactly `session.answer`'s semantics: every outcome is remembered for at least 10 minutes and returned for a repeat, which does nothing more; a repeat of a press still being acted on waits for it |
| `confirm` | Only on a second press: the token the first press's `confirm` outcome gave. At most 64 characters of `A–Z a–z 0–9 - _` |

**Two presses when the session is not idle, enforced by the host.** A session that is
`working`, `waiting`, or `idle` with `activity.background` (delegating) has a turn or work in
flight that ending it would throw away. The first press only arms: the host answers
`confirm` with a token, and ends nothing. A second press within the window (10 s, measured on
the host from when it armed) carrying that token ends it, if the session's process is still
the very one the first press saw, by id and start time. Anything short of that (no token, a
lapsed or unknown one, one from another session, a process that has been replaced) is
another first press: a new `confirm` and token, and nothing ended. Every device and client
can send `session.stop`, so this is the host's rule, never only a client's. An `idle` session
with nothing in the background ends on one press; a token sent for one is ignored. The host
checks the session again immediately before it acts, and one that has started working or
asking since the press arrived needs a second press after all (`confirm`). Several
first presses can be waiting at once (two devices); any one confirms, and confirming spends
them all.

Result: `{ "outcome": "<outcome>", "reason": "<text>" }`, with `reason` present for
anything but `ended`, and:

| `outcome` | Meaning | Also |
|---|---|---|
| `ended` | The session's process has exited. The host removes the session (`session.remove`) as soon as it sees that, normally before this result arrives | `forced: true` when it did not exit on its own and the host terminated it |
| `confirm` | Press again to end it. `reason` says what a second press interrupts ("this session is still working; the confirmation lapses in 10 s"), led by why an earlier press did not confirm, if one was sent | `confirm` (the token) and `confirmSeconds` (the window) |
| `stale` | The session, or its process, had already gone | |
| `busy` | An answer or a reply to this session is being typed, or another press is ending it already | |
| `refused` | The host will not end it: an invalid request, an elevated session or another user's, a process whose start time it cannot read (so cannot tell from a later one with the same id), the host itself, a platform without it yet, or agent-ha-bridge active for this user. Refused on the first press, before anything is armed | |
| `failed` | The host tried, and the session's process is still running (it could not be terminated, or agent-ha-bridge started meanwhile) | |

A client treats an outcome it doesn't know as `failed`, never as `ended` or `confirm`; a
`confirm` without a token it can send back is `failed` too.

**How a host ends it.** Gracefully first, as agent-ha-bridge does: `/exit` typed into the
agent's own input, so the CLI writes its transcript and releases its lock, and the session
stays resumable. If it has not exited within 12 s, the agent's process alone (never its
tree) is terminated. A session asking anything, in what the host reports or in its
transcript, is terminated without `/exit`, which would land in the question's input. From
the moment it starts until the outcome, the session is upserted as `ending`, and the host
holds it against answers and replies (`busy`), which share the per-session serialization.
The terminal window or tab the agent ran in is left open: the person may have other work in
it.

### `push.subscribe`

Registers this device for notifications from this host. A client sends it on every
channel it opens to a host while notifications are on, so a host that dropped the
subscription, or a browser that renewed it, is brought up to date. Request `data`:

```json
{
  "endpoint": "https://web.push.apple.com/QGuQyavXutnMhZ…",
  "keys": { "p256dh": "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV…", "auth": "BTBZMqHH6r4Tts7J_aSIgg" },
  "app": "https://danswett.github.io/agent-bridge/app/",
  "device": "iPhone · Agent Bridge"
}
```

| Field | Notes |
|---|---|
| `endpoint`, `keys` | Exactly what the browser's `PushSubscription.toJSON()` gives, for a subscription made with the relay's VAPID key (`GET /v1/push/key`). `endpoint` is https; `p256dh` is a base64url P-256 point, `auth` 16 base64url bytes |
| `app` | The client's own URL (https): where a tap on a notification opens it |
| `device` | Optional. How `push.list` names this device; the channel's peer name otherwise |

Result: `{ "subscribed": true, "count": 2 }`, `count` being how many devices the host now
notifies. A host keeps **one subscription per device** (the channel's peer id) and per
`endpoint`: subscribing again replaces the device's own, and an endpoint registered by a
new device id (a reinstalled app) replaces the old one. It keeps at most 16, dropping the
oldest. A request that fails the checks above fails as a request (`ok: false`) with the
reason.

**Only devices of the host's own account.** A push goes straight to a device, around the
relay's account-scoped routing, so the host keeps the account boundary itself: it takes
`push.*` only on a channel whose relay connection is signed in to the host's account and
lists the peer among that account's devices, and refuses anything else. It saves
subscriptions with the account they belong to, and drops them all if the profile signs
in as another account.

### `push.unsubscribe`

Request `data`: `{}` or `{ "endpoint": "…" }`. Removes this device's subscription, and the
one with `endpoint` if given (whoever holds an endpoint can already send to it). Result:
`{ "removed": true }`. A host that is offline when a device turns notifications off
learns it from the push service instead: it drops a subscription the service answers
404 or 410 for.

### `push.list`

Request `data`: `{}`. Result: the devices this host notifies, oldest first:

```json
{ "devices": [ { "device": "4f0e…", "name": "iPhone · Agent Bridge", "service": "web.push.apple.com", "since": "2026-10-28T04:22:43.182Z", "current": true } ] }
```

`current` marks the calling device. Endpoints are never sent back: one is a capability to
notify that device.

### Managing a machine

**New (#801 slice A).** A person's phone, browser or Windows app restarts a machine's host, or
repairs the hooks of an agent that machine's installation set up, so nobody has to sign in to
it for either. The design record, with the open and answered questions, is draft
[#821](https://github.com/danswett/agent-bridge/pull/821); this section is the contract.

| Op | Request `data` | Result |
|---|---|---|
| `manage.get` | `{}` | `ManageState` |
| `manage.subscribe` | `{}` | `ManageState`, then a `manage.state` event with the `ManageState` whenever it changes |
| `manage.restart` | `ManageCommand` | `ManageResult` |
| `manage.hooks.repair` | `ManageCommand`; its body's `args` is `{ "agent": "copilot" \| "claude" \| "codex" \| "agency" }` | `ManageResult` |
| `manage.stop`, `manage.config.get`, `manage.config.set` | `ManageCommand` | `ManageResult`, always refused `needs-roster` until #55 |
| `manage.haToken.set` | `ManageHaTokenRequest`: `{ "command", "secret" }` | The same; the request is refused before it is read, so the secret is never parsed |

`ManageCommand` is `{ "command": SignedCommand }`: the [signed command](v1-transfer.md) a
person's device made for this machine, whose body's `action` is the op. It lives at most
**2 minutes** from `issuedAt` (a transfer's lives 10). The body's `args` and `contentSha256`
are signed with the rest (the MAC covers the whole body; the vector
`a-hook-repair-with-args` in [signed-command.json](vectors/signed-command.json)), and are
optional, so a transfer's bytes are unchanged. An action takes only the arguments it defines
(Connection, Q-C4 on #886): a restart none, a repair `agent` alone, neither a
`contentSha256`. Anything else is refused `bad-args`.

**Authorization.** Through the relay only: every `manage.*` op is refused on the local pipe,
whoever asks ([local-pipe.md](local-pipe.md)), so the #67 guard on `host.restart` is
untouched. The signer must be the device sending it, connected now, of kind `pwa` or `app`
(Connection, Q-C1 on #886), and not this machine's own app (the device id the app gave in
`host.relay.account`), which is refused `local-device` and may not read `manage.get` either.
**Until #55**, that kind is the one the device declared to the relay in its presence (as
`update.*` and folder trust read it; Connection, Q-C7): any device signed in to the account
can claim `pwa`. #55's roster binding replaces it with the kind the owner approved; the
bounds below are what hold until then. A machine with `permissions.remoteManage` off
([host-config.md](../host-config.md)), or read-only, offers none of it and refuses all of it,
reads included; the switch and read-only are looked at again just before a repair runs and
before a restart commits.

**A restart** leaves only through #805's quiet lease ([host-lifecycle.md](../host-lifecycle.md)):
`TryAcquireQuiet`, then `AnnouncePlanned`, then `CommitDeparture`; refused at once
(`not-quiet`, `busy`, `announce-failed`, `changed`) when any step fails, never forced or
carried out later. It is refused `changed` when its quiet lease ran out before the commit (#885), and
while the #800 agent sign-in entrypoint is on,
since a restart cancels a pending sign-in (Connection, Q-C8). The owner's 2026-11-07 decision on
#1159 ("I need a way to force updates to happen from my phone ... I want to be able to accept the
temporary downtime of an update") supersedes "never carried out later" for an update a person
presses **Update now** for, and for nothing else: that press waits for its download and up to a
minute for the same lease, never goes over a busy session its confirmation didn't cover (a
machine that became busier meanwhile keeps the download and installs at its next quiet moment;
Product, #1161), lives at most 15 minutes from its signature (then it is dropped, spending no
restart and cancelling no sign-in), is still never forced past the lease, and has its own
allowance of 1 restart in 6 hours (owner, #1161), spent first, then counts against the same cap
below ([v1-update.md](v1-update.md#update-now-1159); the host's half built, the app's not yet).
At most **2 committed restarts
every 6 hours** per machine until #55 (`rate-limited`), and 3 presses of each kind per device
every 10 minutes. Once committed, the result is `accepted` with the planned-state
`transition`; the host settles its usage probes (#238), then hands over to its replacement
(#239); a stop that comes meanwhile wins, and no replacement is started. The new host reports `done` (with `tookSeconds`) or `failed` in `last`; a restart a stop won over is `failed` (`changed`), however soon the host is started again.

**A repair** runs only for an agent whose hook ownership record names this profile (else
`unavailable`), and is only ever Settings' Set up / Repair, never a removal. `done` or
`failed`, with `restartRequired`.

The owner's other devices get a quiet notice of each restart and repair (Q-O3): a `finished`
push of class `done`, silent, kept in the tray, naming the machine, what, and which device;
never a value. A restart waits up to 3 s for it to be sent before it leaves.

`ManageResult`: `{ "request"?, "outcome": "accepted" | "done" | "unchanged" | "refused" | "failed",
"reason"?, "detail"?, "restartRequired"?, "transition"?, "state"?: ManageState }`. An unknown
outcome reads as `failed`, an unknown reason as a refusal with its `detail`. A press sent again
gets its first answer and is never acted on twice. A refusal before the press is verified as a
person's carries no `state`.

`ManageState`: `{ "phase": "idle" | "restarting" | "repairing", "pending"?: { "request", "kind",
"by", "at", "transition"? }, "restarts": { "left", "nextAt"? }, "last"?: { "request", "kind",
"outcome", "by", "at", "reason"?, "tookSeconds"? } }`. `by` is the device's kind and short id
(`pwa 1a2b3c4d`). Vectors: [manage.json](vectors/manage.json).

## Notifications

A host with `push` notifies every subscribed device when one of its sessions needs the
person or has something to say, whether or not the app is open, the same moments
agent-ha-bridge notifies a phone through Home Assistant. While the person uses one of their
devices, it holds back a finished turn, and a question whose card a device in use shows
([While the person uses a device](#while-the-person-uses-a-device-1190)).

### Notification class (#913)

**New (#913).** One notification policy for every client: the host decides which tier each
session is in, and the Windows app, the PWA and Home Assistant follow it rather than rules of
their own (#253, #610, #754 and #764 were each one client's). The session carries its tier in
`class`, and each push the tier of what it says in `data.class` ([The payload](#the-payload)).

| `class` | Tier | The host sets it when (first match wins) |
|---|---|---|
| `fyi` | | `status` is `ending`: the person asked for the session to end |
| `needsYou` | Needs you: interrupts | a `question` is pending, the session's own or a delegate's (`waitingOn`), a form or an approval alike, one marked `answerAtMachine` included; or `attention` is present: `signInNeeded`, `error` (an errored turn) or `notResponding`; or `restored` is present with `cause` `crash` and nobody has acted on the session since its restore (no prompt since it, and no turn started after it). `restored` after a reboot or a host restart gives no class: those sessions are `needsYou` only through their own state |
| `done` | Done: waits | `status` is `idle` with a non-empty response of the main agent's own: `own.response` while `source.response` names a delegate, `response` otherwise (the same text a `finished` push quotes). Delegates still running in the `background` do not change it, as they do not hold back a `finished` push |
| `fyi` | FYI: never notifies | anything else: working, or idle (delegating or not) with nothing of the main agent's own said |

**A session restored after a crash (#780)** is `needsYou` from its restore (`classSince` is
`restored.at`) until the person acts on it: a prompt arrives after the restore (Continue, any reply,
or one typed at the machine), or a turn starts after it. It is also no longer `needsYou` for it once
`restored` goes (after 24 hours). After a reboot or a host restart its
`restored` gives it no class (Product on #975): its `class` comes from `status`, `question` and
`attention` like any other session's, and it is restored idle, so `done` if its last turn left an
answer of the main agent's own, else `fyi`. Neither changes the Needs you list, its count or the
badge, which follow ui.md; `class` is the notification tier only.

The wire names are the owner's tier names from #913 in this protocol's camelCase, as
`signInNeeded` is, so the spec, the code and ui.md say each tier with one word. They are short
because every session carries one.

`classSince` says when the session entered its present tier, the start of the episode, for the
reminder to count from; no client shows it. While `class` stays the same, `classSince` stays
what the host last sent, whatever changes under it: a delegate's question arriving beside the
session's own, one question answered as the next is asked, working and idle within `fyi`. It is
never worked out again per question, so a new question while the session is already `needsYou`
does not restart the reminder. When `class` changes, or the host sends a session for the first
time (after the host starts, or when it finds the session), it is taken from the fields that
put the session in the tier: for `needsYou` the earliest of `question.askedAt`,
`attention.since` and, for a restore after a crash, `restored.at` that are present, the oldest
thing waiting on the person; for `done` and
`fyi` it is `statusSince` (for `ending`, when the end was asked for). A snapshot, a list or a
resumed subscription carries the same `classSince` the host last sent; only a restart of the
host can start the episode again from those fields.

* **A state, not an event.** `class` says which tier the session is in now. A client alerts
  for a change it sees live: a session entering `needsYou`, or a new reason within it (another
  question `id`, another `attention.kind`), and entering `done` with a new `turnCount`. What a
  snapshot, a list or a resumed subscription gives it is not news, as a session already idle
  when the host starts is not. A push is decided by the host's rules below (once per question,
  once per finished turn, the 10-minute collapse) and carries its tier in `data.class`.
* **Delegates roll up.** A delegate never has a class of its own and never notifies on its own.
  Its question is its parent session's (`question` with `agent`, and `waitingOn`), so the parent
  is `needsYou`, and its notice keeps the session's tag and is titled
  "Delegate *name* asks · *title*", as now. A delegate's progress, its words, its error and its
  finishing never change the parent's class: only the main agent's own answer makes a session
  `done`, and an error in the parent's own turn makes it `needsYou`.
* **One notification per session, replaced in place, withdrawn when answered.** Every notice
  about a session shares its tag, `<machine id>:<session id>`
  ([Replacing and collapsing](#replacing-and-collapsing)), so a device shows at most one per
  session, the newer replacing the older, never stacked. When a session leaves `needsYou`
  because its question was answered (on any device, or at the machine) or its attention went, a
  client withdraws that session's notification. For a push, the host sends `resolved` to the
  devices that were shown the question, which replaces it silently ("no longer waiting"): Web
  Push must show something. A session ended with its question pending is `fyi`, and its
  question's notice is withdrawn the same way, with "· session ended".
* **The class is not the delivery rules.** Watch and Mute, the device in use, grouping a burst,
  the reminder after 15 minutes and agent-driven turns (#489's `userMessageFrom`, and `driver`)
  decide who is alerted for a tier, and how (ui.md §7.0). They never change the tier: a Muted
  session's question is still `needsYou`, and a turn an agent drove is still `done` (only its
  push is stopped). Mute still lets every approval and sign-in through, an approval marked
  `answerAtMachine` included: the person has to act, and its notice keeps "waiting at the
  machine". A host groups a burst of questions and sends the reminder in its pushes
  ([Bursts and the reminder](#bursts-and-the-reminder-913)); the rest is later #913 work.
* **Done is silent everywhere** (the owner's answer B), older clients and Home Assistant
  included. The host sends every `finished` push with `silent: true`, and a client also keeps a
  `done` notice silent whatever `silent` says, as a second safeguard. A Done notice still shows
  (on the lock screen, for a Watched session); it never interrupts.
* **The reminder** (ui.md §7.0 rule 5): once per session and episode, 15 minutes after its
  `needsYou` alert, and never repeated. A new question while the session is already `needsYou`
  replaces its notification in place and does not restart the reminder; answering everything
  ends the episode and cancels it. For a question, the host sends it as a push
  ([Bursts and the reminder](#bursts-and-the-reminder-913)). A client times its own only for
  what it alerts for itself from the session (a toast while the app runs, or `attention`, which
  has no push yet), counted from `classSince`, and adds none to a push.
* **Additive and optional.** An older client ignores `class`, `classSince` and `data.class`, as
  any field it does not know. A newer client reading a session or a push without them (an older
  host) keeps today's behaviour: the rules in [When a host notifies](#when-a-host-notifies), and
  its own. A `class` value it does not know, or one of the wrong type, reads as absent, so it
  keeps today's behaviour then too: read as `needsYou`, a newer host could interrupt people with
  something an older client can't explain. `class` changes only with the fields it comes from, and `classSince` only with `class`, so
  they never move `updatedAt` or send an upsert on their own, and the
  [session budget](#session-budget) never trims them.

**What a host classes today:** every session it sends, a session restored after a crash
included, and its `question`, `finished` and `signIn` pushes, and a `restored` push after a crash
(and, proposed, any `restored` that replaces a `hostGone`, [Host gone](#host-gone-970)).
Not by the host:
* **A machine offline unexpectedly (#805)**, which its host cannot say. Each client classes it
  `needsYou` from the relay's presence, once #269's grace period has passed, so a short drop
  never is. #805 owns the threshold.
* **A machine whose host is gone (#970, proposed)**, which its host cannot say either. The
  relay decides it ([host-gone.md](host-gone.md)): only after a departure the host didn't
  announce, with evidence the bridge stopped, and 5 minutes for #805 to heal it first. The
  relay then sends the host's pre-sealed `hostGone` notice if one was armed
  ([Host gone](#host-gone-970)), and clients show the machine's state (ui.md §6.14).

  **How the two sit side by side.** Neither replaces the other; each governs its own signal,
  and #970 changes nothing of #805's, its threshold included:
  * A machine **with `offline.gone`** is #970's. Its alert is the one `hostGone` notice (or
    `gone.alert`'s toast), at the `needsYou` tier: it interrupts, with the question sound, once
    per episode. A client doesn't class that machine again under #805's rule, so one departure
    never alerts twice, and the shared caps count it once. It is **not counted** in Needs you's N,
    its filter or the app badge, which count questions and approvals only (owner decision,
    2026-11-05; Product, #1022); its banner and its row line are how it shows.
  * Every other machine that is offline unexpectedly is #805's, as above: one that was lost
    without evidence the bridge stopped (a `timeout`, a machine switched off or asleep), and
    one whose planned reboot, restart or update didn't return (#805's no-return push,
    which #970 never sends). #970's pending window sends and shows nothing, and takes nothing
    from #805's rule while it runs. To keep "never alerts twice" true, #805's threshold for a
    machine lost with no plan is to be no shorter than `HostGoneAfter`; until SRE confirms that
    on #805, a machine #805 alerted before it became gone is the one open overlap.
  * A machine whose host announced its departure (#805's plan) is never #970's.

  `needsYou` here is the tier, the alert's strength, in both rules; neither makes a machine a
  member of Needs you's N. If #805 meant its classing to count a machine in N or the badge, that
  conflicts with the 2026-11-05 owner decision for every machine, and is for SRE to settle on #805.

`signInNeeded` pushes as the `signIn` notice of #800, at most once an hour per machine, agent
and account (ui.md §6.13). The rest of `attention` has no push of its own yet. Being `needsYou`,
it will push: an errored turn once per turn, its body's first line "`<machine>` · stopped: *reason*", a rate limit
or quota included. `notResponding` alerts **once per episode**: an episode is a stretch in which
the session is `needsYou`, beginning at `classSince`. If the session recovers and stops
responding again within 2 minutes, that is the same episode: its notification is replaced
silently, not alerted again. A recurrence after more than 2 minutes is a new episode and may
alert. Each episode gets the one reminder at 15 minutes, never repeated. Telling episodes apart,
and the reminder for `attention`, are the clients' to time; the host only sends `attention` and
`classSince`, and neither reminds nor groups what it does not push.
Home Assistant's notifications are to follow `class` too, `done` only for a Watched session, and
silently (ui.md §7.0; later #913 work).

### When a host notifies

| `kind` | When | Once per |
|---|---|---|
| `question` | A session has a pending `question`, still pending 2 s later. A question already pending when the host starts is notified too, as agent-ha-bridge's daemon does after a restart. Within a burst it goes in the `group` instead. Still pending 15 minutes after the session's first question notice of the episode, it goes once more with `data.reminder` ([Bursts and the reminder](#bursts-and-the-reminder-913)) | Question `id`, for the life of the session: further upserts, or the question briefly reading as pending again after an answer is typed, do not repeat it. The reminder: once per episode |
| `finished` | A session went from `working` or `waiting` to `idle` with a non-empty response of the main agent's own (a delegate's latest text does not count), and is still idle 3 s later. Not to a device that is on screen now, subscribed to the host on a channel whose [`sessions.focus`](#sessionsfocus) last said `"visible": true`, unless a question notice of that session is still on it (the `finished` replaces it). Nor, while the person uses any of their devices, to any of them, with the same exception ([While the person uses a device](#while-the-person-uses-a-device-1190)). Waiting on a sub-agent's question (`waitingOn`) counts if the main agent's turn was running when that wait began, or its own response changed from before the wait (for a session the host found already waiting, from what it read at startup): answering a delegate while the main agent stayed idle ends no turn. A turn that carries on within that (the person is at the machine) is not notified. A session already idle when the host starts is not news. A sub-agent finishing is never notified. Nor is a turn that ended on an error or a failed sign-in (`attention` `error` or `signInNeeded`): it is idle with the previous turn's answer still beside it, which a `finished` would quote as the new one's; one already due or held for the session is withdrawn while that attention lasts. A `finished` that would have replaced an answered question and does not go (withdrawn so, or refused by a device) sends that question's `resolved` instead to the devices still showing it, unless it is asked again | Finished turn: the same `turnCount` and response seen ending again is not repeated |
| `restored` | With `sessions.restore`: the host started again after a reboot or a crash took sessions with it ([What went](#hello)). Sent once every session it saw live is judged and those that died are restored, or 10 minutes after it started if some are still being judged then; not at all if none of them died. It says how many were restored and counts those offered, naming only the sessions that went mid-turn or with a question waiting and leaving the rest to Launch; sessions an earlier start already offered stay in `machine.restore` but are not notified again. To every subscribed device | Start of the host
| `resolved` | A question that was notified is no longer pending: answered anywhere, or the session ended. Sent only to devices that were shown the question, and not to the device that answered it with `session.answer` (it was there). A `finished` that follows within 2 s is sent instead. Also withdraws a `group` once none of its questions is pending | Question |
| `group` | **New (#913 rule 3).** A question of another session comes due within 30 s of one that went as itself, while that one (or one already grouped) is still pending: see [Bursts and the reminder](#bursts-and-the-reminder-913) | Burst |
| `signIn` | **New (#800).** Sessions of the machine need sign-in: `attention` `signInNeeded`, except while its `recovery` is `pending` (signed in, waiting for the session to show it). Sent as soon as the first of them does, with no delay, to every subscribed device, on screen or not: a turn that ended on a 401 is `idle` with the previous turn's answer beside it, and nothing else tells the person every one of those sessions has stopped. Then again each hour while any still does, counted from when one reached a device, a restart of the host included (one that never went is made again by the next host). Once none does, one still waiting to be sent is withdrawn, and the next failure is notified at once. Never one per session, and never for an `error` or `notResponding` (ui.md §6.13): the row says those | Machine, agent and account, per hour |

A device is sent at most one `finished` per session every 10 minutes. One that follows sooner
waits until the 10 minutes are up, a newer one replacing it, and is sent only if the session is
still idle with no question then and the device is not on screen; a `question` or `resolved`
replaces it at once, as always. Answering or replying to the session from a device (with
`session.answer` or `session.reply`) ends this wait for that device: the person is waiting for the
turn they started. Agents that finish turn after turn on their own sent 88% of all pushes (#610).

#### While the person uses a device (#1190)

A device is in use while a channel of it subscribed
to the host last said in [`sessions.focus`](#sessionsfocus) that it is `visible`, with an `idleMs`
that puts its last input (the host's clock when it was received, less `idleMs`) under 2 minutes
before the push would go. Then:

- a `finished` goes to none of the person's devices, the one in use or any other, unless it
  replaces a question notice still on that device;
- a `question` (its reminder too) still goes, since the person may be in another session, unless
  a device in use shows its very card (`open` is its session and `openQuestion` its id): then it
  goes to none of them but a device still showing an earlier question's notice of that session,
  which it replaces. Its reminder still comes due 15 minutes on and is judged the same way;
- `resolved`, `group`, `signIn` and `restored` go as before.

This is judged as each push goes, not when it was made, and no delay is added to wait for a
`sessions.focus`: one that reaches the host first wins. A device that never said, gave no
`idleMs`, or is hidden, is not in use, and is sent what it was before. Nothing already shown is
withdrawn for this. The host's log names each push held back so: `dropped=in-use`,
`dropped=card-on-screen`, or with `-elsewhere by=<device>` when another device was the one in use
([Notifications in the log](../host-config.md#notifications-in-the-log)).

A question answered before its 2 s are up is never notified. A notice that cannot be sent
because the host's relay connection is down is sent when it is back; a newer notice about
the same session replaces it first, so a device gets only the latest. A host sends at
most 20 pushes a minute (the relay allows 30 per account, across all its hosts) and holds
the rest, keeping only the latest per device and session, until there is room. A push
service answering 429 or 5xx is retried with backoff; 404 or 410 means the subscription
is gone, and the host drops it.

### Bursts and the reminder (#913)

**New (#913 host slice 2, rules 3 and 5).** Only questions and approvals (a delegate's included):
the items the Needs you list shows (ui.md). A `signIn` push is never grouped and keeps its own
hourly repeat; `error` and `notResponding` have no push yet, so a host neither groups nor reminds
them, and the `restored` notice is its own.

* **A burst groups** (rule 3). The first question goes as itself, after its usual 2 s and no
  later: nothing waits for a window. It opens a **30 s** window. A question of another session
  that comes due while the window is open, and while the first or one already grouped is still
  pending, does not go as itself: it joins the machine's one `group` notice, made when the second
  arrives (alerting, as a question does) and replaced in place, silently, as more join within the
  window or as they are answered. A device whose alerting group had not reached it when it was
  replaced (the relay down, the rate limit, a retry) gets the replacement alerting instead: each
  device hears a burst once. The first question keeps its own notice, which the group does
  not replace: it carries the approve or answer controls, and its session's later notices
  (`resolved`, `finished`, its next question) replace whatever its tag shows. A session whose own
  question notice is on devices is not grouped either: its next question replaces that. A
  question alone in its window, or one after it closed, goes as itself. The group counts the
  first question and those grouped while they are pending. Once none is, a `resolved` withdraws it
  from the devices it reached. A later burst while it is still out replaces it, and alerts.
* **One reminder** (rule 5). A session's first question notice of a `needsYou` episode, as
  itself or in a group, arms its reminder for **15 minutes** later. If the session is still
  `needsYou` then, with a question pending that was notified, that question's own notice goes
  once more, with `data.reminder: true`: the same title, body, tag, topic and `navigate`,
  alerting. It goes to the devices the first notice went to, each still subscribed under the same
  pairing; it is not grouped. It is cancelled when the session leaves `needsYou` (its question
  answered on any device or at the machine, or withdrawn) or ends, a reminder still on its way to
  other devices included: each is checked again as it goes. Never repeated: a further
  question in the same episode brings no second. Rule 4 (only the device in use alerts) narrows
  it later.
* **A restart of the host** loses a reminder still to come and an open window. The host notifies
  a question already pending when it starts, as always, which counts a new 15 minutes. Each goes
  as itself, never grouped, under its session's tag, so its answer withdraws it as any question's
  does, and none opens a window: they are not a burst, and the next live question goes as itself
  too. A group out before the restart is not withdrawn by the next host, and is replaced by the
  next burst.
* **An older client** reads `group` as `question`, an unknown kind. It names no session or
  question and opens the machine, so it shows the words and nothing to answer. It ignores
  `reminder` and shows the reminder as the question's notice again, replacing it in place.

### Host gone (#970)

**Proposed, not implemented.** The one notice a host writes for after it has gone. A host that
has stopped can't send anything, so while any agent in its sessions, a delegate included, is working or waiting it keeps a
sealed `hostGone` notice armed with the relay (`host-gone-arm`, [host-gone.md](host-gone.md#arming-the-notice-slice-3)),
one per subscribed device, and disarms it within 5 s of none being so. The relay sends it,
unread, only if the host goes without announcing it and isn't back within 5 minutes
([host-gone.md](host-gone.md#the-window-805-goes-first)). The relay never learns what the
sessions were doing; the host never claims more than it saw when it sealed the notice.

| `kind` | When | Once per |
|---|---|---|
| `hostGone` | Sent by the **relay**, sealed by the host in advance: the host went, unannounced, with any agent in its sessions working or waiting by its last arm (a delegate included, [host-gone.md](host-gone.md#arming-the-notice-slice-3)), and wasn't back 5 minutes later. Never for a machine that was simply off with nothing in flight, nor for a departure the host announced (#805's), nor while #805 is still restarting it | Episode. At most one alert an hour per machine and three a day per account, shared with #805's no-return notice: an episode counts as one alert when at least one of its notices was sent or its `gone.alert` was set, never when the caps suppressed it |
| `hostBack` | Sent by the **host**, live, once it has been back for 2 minutes and #780 has finished judging with nothing to restore, to the devices the relay said were sent `hostGone` (`host-gone-sent`), silently, replacing the `hostGone` in place. **Only if no `restored` notice went in this episode:** a return's `restored`, of any cause, replaces the `hostGone` instead (below), so the person gets one notice on return, never two | Episode |

* **Tag** `<machine id>:host`, and **topic** the first 24 bytes of SHA-256 of
  `agent-bridge-push|<machine id>|host`, base64url: a `hostBack` replaces the `hostGone`, and a
  phone that was offline gets the latest of the two.
* **One notice on return** (Creative and Connection, #1022). The relay sends `host-gone-sent`
  (`since`, `targets`, and the gone `tag` and `topic`) as soon as the host reconnects, with empty
  `targets` when its episode wasn't alerted; the host holds any `restored` push until it has it
  (at most 10 s). A `restored` notice of any cause ([When a host notifies](#when-a-host-notifies)),
  even one decided after the episode ended, then goes to each device in `targets` on the gone tag
  and topic, replacing the alert in place, and to every other device on `<machine id>:~restore`
  as today; no `hostBack` follows. On the gone tag it alerts at Needs you, never silently
  (`silent` absent, `data.class` `needsYou`, whatever its cause). Withdrawing it uses whichever
  tag it went on, which the host remembers per device until it is withdrawn; after that, nothing
  about the `hostGone` comes back ([host-gone.md](host-gone.md)).
* **TTL and urgency:** `hostGone` 6 h, `high`; `hostBack` 6 h, `normal`.
* **Payload:** `title` "*machine*'s bridge isn't running" (`hostBack`: "*machine*'s bridge is
  running again"); `body` "It stopped while sessions there were working or waiting on you."
  (`hostBack`: "You can reach its sessions again."), the words of ui.md §6.14. Sealed before the
  host went, so never a time, a count, a session's title or any agent's words. `navigate`: the
  subscription's `app` with `host` only, which opens that machine's tab. `silent`: absent on
  `hostGone`, which has the question sound; `true` on `hostBack`. `data.class` `needsYou` on
  `hostGone`, absent on `hostBack` (it withdraws rather than notifies). `data.machine` the
  machine's id; no `data.session` or `data.question`. `data.at` is when the host sealed it, not
  when it went: a client shows the notification's own time.
* **A client that doesn't know the kinds** reads them as `question`: with no session or question
  in `navigate`, it opens the machine with nothing to answer, as for `restored`.

### Replacing and collapsing

Every notice about one session on one machine shares:

* a **tag**, `<machine id>:<session id>`: the device replaces the session's notification
  in place. A `resolved` notice turns a stale question into "no longer waiting"; a
  `finished` one replaces the question it followed. A `signIn` notice's is
  `<machine id>:signin:<agent>:<first 8 hex of SHA-256 of the account>` (of the empty string when
  the host does not know the account), one per machine, agent and account, so its hourly repeat
  replaces it. A `group` notice's, and its withdrawal's, is `<machine id>:~needsyou`, one per
  machine.
* a **topic** (RFC 8030 `Topic`), the first 24 bytes of SHA-256 of
  `agent-bridge-push|<machine id>|<session id>` (for `signIn` and `group`, the tag's part after the
  machine id in place of the session id), base64url (32 characters; Apple refuses
  other lengths, spike S5 finding 6): the push service replaces an undelivered push with
  the newer one, so a phone that was offline gets one notice per session, not a backlog.

| `kind` | TTL | Urgency |
|---|---|---|
| `question`, `resolved` | 24 h | `high`, `normal` |
| `finished` | 6 h | `normal` |
| `signIn` | 6 h | `high` |
| `restored` | 24 h | `normal` |
| `group` | 24 h | `high`; `normal` when it changes silently or is withdrawn |
| `hostGone` (#970, proposed) | 6 h | `high` |
| `hostBack` (#970, proposed) | 6 h | `normal` |

### The payload

What the host encrypts is a [Declarative Web Push](https://webkit.org/blog/16535/meet-declarative-web-push/)
message, which Safari 18.4+ shows without running the service worker, and which a client's
service worker shows from the same fields everywhere else:

```json
{
  "web_push": 8030,
  "notification": {
    "title": "Pick the session store",
    "body": "DSWETT-DEV-VM1 · waiting on you\nWhich database should the session store use?",
    "navigate": "https://danswett.github.io/agent-bridge/app/?host=53d1997f…&session=0f3c…&question=toolu_01",
    "tag": "53d1997f…:0f3c…",
    "lang": "en",
    "data": { "v": 1, "kind": "question", "machine": "53d1997f…", "session": "0f3c…", "question": "toolu_01", "at": "2026-10-28T04:22:43.182Z" }
  }
}
```

| Field | Notes |
|---|---|
| `title` | The session's `title`, or "Copilot session" (by agent) without one. A sub-agent's question (Question `agent`) is titled "Delegate *name* asks · *title*", with the sub-agent's `name` from `subagents` (cleaned as in [Subagent](#subagent); "A delegate asks · *title*" without one); it keeps the session's tag, so the newer of the session's and its sub-agents' questions replaces the older. `signIn`: "*Agent* on *machine* needs sign-in" (ui.md §6.13). `group`: "Needs you", never changed by its updates, and its withdrawal's too. At most 120 bytes |
| `body` | A first line naming the machine and what happened, then the text. `question`: "`<machine>` · waiting on you" ("waiting for your approval" for an `approval`, "waiting at the machine" for one marked `answerAtMachine`), then the question's `message` ("This session is waiting for an answer." if empty). `finished`: "`<machine>` · finished", then the first 880 characters of the main agent's own response (`own.response` while `source.response` names a sub-agent, else `response`), as agent-ha-bridge's preview, "…" if cut. `resolved`: "`<machine>` · no longer waiting", or "· session ended". `signIn`: "*n* sessions affected" ("1 session affected"); the title names the machine. `group`: "`<machine>` · *n* sessions waiting on you" ("1 session waiting on you" once the rest are answered); its withdrawal "`<machine>` · no longer waiting". Creative owns these words (#913, 6690989275). Never the account, a code, a URL or any of the agent's words. Whitespace runs are collapsed to one space for a lock screen. At most 1 KiB |
| `navigate` | The subscription's `app` with `host`, `session` and, for `question` and `resolved`, `question`. `signIn`: `host` and `signin=<agent>` only, never `session` or `question` (#800 6646515428): an older client, which reads the kind as `question`, opens the machine and no question view or answer controls. `group` and its withdrawal: `host` and `needsyou=1`, built as `restored`'s (any `host`, `session` and `question` in `app` replaced), opening Needs you; an older client opens the machine. A client opens that machine and session, and the question ready to answer |
| `silent` | `true` for `resolved`, which replaces a question without alerting again, for a `group` changed in place after its first push, and for `finished`: Done never alerts (#913), so a client that predates `data.class`, and Safari's Declarative Web Push, which shows without the service worker, play nothing for it. Absent for `signIn`, which has the question sound (owner, 2026-11-04, ui.md §6.13) |
| `data.kind` | `question`, `finished`, `resolved`, `signIn`, `restored` or `group` (**new, #913**) (and, proposed, `hostGone` and `hostBack`: [Host gone](#host-gone-970)). A client reads a kind it doesn't know as `question`. A `group` has no approve or answer controls (#253) and no agent text (#263): `data.session` is empty, `data.question` absent, `data.sessions` its count; its withdrawal is a `resolved` with its tag, `data.session` empty. A `restored` notice is titled `<machine> restarted` (a reboot) or `<machine> · the bridge stopped` (Creative, #1022: "bridge", never "host", in copy; the built string changes in #970 slice 3), its body counts the sessions that went and names those mid-turn or with a question lost; its `navigate` has only `host` (the subscription's `app` with any `host`, `session` and `question` it carried replaced, not appended to; the rest of its query kept), its tag is `<machine id>:~restore` (proposed, #970: to a device that was sent a `hostGone` in this episode, the gone notice's tag and topic instead, [Host gone](#host-gone-970)), and its `data.session` is empty and `data.question` absent, so an app that reads it as a question (one from before `restored`, whose service worker adds no actions) shows these words and opens the machine, with nothing to answer |
| `data.machine`, `data.session`, `data.question` | The machine's device id, the session's `id`, the question's `id` (not for `finished`). Neither `session` nor `question` for `signIn`, which is about the machine |
| `data.agent`, `data.sessions` | `signIn`: whose sign-in it is (`copilot`, `claude`, `codex`), and how many of the machine's sessions need it. `group`: `sessions` only, how many sessions it stands for |
| `data.class` | **New (#913).** The notice's [notification class](#notification-class-913): `needsYou` for `question`, `group` and `signIn`, and for `restored` after a crash; `done` for `finished`, never `needsYou` (a turn that ends with a question pending sends that question as its own `question` notice). Absent on `resolved`, which withdraws a notice rather than being one, on a `restored` after a reboot or a restart of the host (proposed, #970: except one that replaces a `hostGone` on its tag, which is `needsYou` whatever its cause), and from an older host, whose push a client handles as today. Unknown reads as absent |
| `data.reminder` | **New (#913 rule 5).** `true` on a question's one reminder ([Bursts and the reminder](#bursts-and-the-reminder-913)); absent otherwise. A client may word or sound it as a reminder; one that ignores it shows the question again |
| `data.at` | When the host sent it |

The JSON as encrypted is at most **3 KiB**: under RFC 8291's one 4096-byte record (3993
bytes of plaintext) and Apple's 4 KiB cap. The host cuts the body, then the title, never
mid-character, until it fits (`PushNotice.Fit` in C#). A client must show something for
any push, even an unreadable one: iOS revokes notifications from an app whose push shows
nothing.

### Privacy

Nothing a notice says reaches the relay or the push service (Apple, Google, Mozilla,
Microsoft) in plaintext. The host encrypts each push to the subscribing browser's own key
(RFC 8291, aes128gcm); the relay only adds its VAPID signature and forwards the
ciphertext to an allowlisted push service. They learn that a push of some size went to an
endpoint at some time, and the push's `Topic`, `TTL` and `Urgency` headers: the topic is a
hash, which names no session. The subscription's keys reach only the host, over the
end-to-end channel. The device shows the notice on its lock screen.

A relay is not trusted to tell the truth about who is in an account. In legacy mode,
a hostile one could present its own device as the account's, register a subscription
or open a channel and read `sessions.list`. This is a baseline exposure, not a safe
authorization model for scoped clients.

The default-off [trusted-peer floor](../host-config.md#trusted-peers-disposable-host-foundation)
also authorizes standing push access. Enforcing mode binds subscriptions to a full
person key, trust epoch and grant generation, and rechecks at enqueue, delivery and
retry. Revocation, expiry and reset remove unauthorized subscriptions and queued
notices; old subscriptions cannot be revived by reapproval. Enforcing startup drops
legacy subscriptions without that binding, requiring authorized re-registration.
A push already handed to its sender cannot be recalled. This host-only foundation
is for disposable evaluation, not production service enrollment.

## Types

### Session

| Field | Type | Notes |
|---|---|---|
| `id` | string | The agent's own session id: the Copilot session-state folder name, or the Claude or Codex session id |
| `agent` | `"copilot"` \| `"claude"` \| `"codex"` | Hosts send only these. A client reads anything else as `unknown` |
| `title` | string? | The session's name if it has one (Copilot's `workspace.yaml` `name`). Otherwise the first user message, cut to 240 bytes |
| `cwd` | string? | Working directory |
| `repository` | string? | `owner/name`, if known |
| `branch` | string? | |
| `model` | string? | The model used for the latest turn, e.g. `claude-opus-5.5` |
| `status` | `"working"` \| `"waiting"` \| `"idle"` \| `"ending"` | `waiting` exactly when `question` is present, unless `ending`. `ending`: the host is ending the session (`session.stop`); it is removed once its process has gone, or shows its own status again if that fails. A client that does not know `ending` reads it as `idle` |
| `statusSince` | time | When `status` last changed |
| `startedAt` | time? | |
| `updatedAt` | time | When something a client shows last changed. Events that change nothing visible (hook runs, usage records, model internals) do **not** move it, so they never cause an upsert |
| `processId` | number? | The agent's process, when known |
| `activity` | Activity | |
| `question` | Question? | The question the agent is waiting on, if any |
| `driver` | `"person"` \| `"agent"`? | Who drives the current turn ([Driver](#driver)). Absent means `person`, and a host sends it only as `agent`. A client that does not know a value reads it as `person` |
| `driverPrincipal` | string? | With `driver` `agent`, which agent, when the host knows: at most 128 bytes, plain text, shown as secondary detail only. Absent otherwise |
| `subagents` | [Subagent](#subagent)[]? | The sub-agents the session started, at any depth ([design/subagents.md](../design/subagents.md)): running ones (`working`, `waiting`) first, newest start first, then the most recently finished. At most 20, and fewer if the [session budget](#session-budget) trims them. Absent when there are none. Finished ones stay until the session ends. Only Copilot sends them so far |
| `subagentsMore` | number? | Entries left out of `subagents`, by the host's limit or the budget. Absent when none are |
| `restored` | object? | Set on a session its host restored, idle, after a reboot or a crash (#780), once its window has been seen ready (never on one that may be asking whether to trust its folder), for that run of the host and at most 24 hours: `at`, `cause`, `midTurn` (it went while working, and no prompt has arrived since the restore), `questionLost` (a question was waiting in it) and, with `midTurn` only, `continue`: the words a client offers to send as an ordinary `session.reply` when a person presses Continue, with no more trust than any reply; the host never sends that reply itself. Hosts set `restored` on each session they restore ([What went](#hello)); after a crash, it makes the session `needsYou` until the person acts on it, and after a reboot or a host restart it does not change the session's `class` ([Notification class](#notification-class-913)). `midTurn` and `continue` go once a prompt arrives after the restore. A question the interrupted run left waiting is not shown as `question` (`questionLost` says it went) |
| `waitingOn` | string? | Status precedence rule 2: the id of the sub-agent whose question is `question`, while the session has none of its own. Absent otherwise |
| `attention` | [Attention](#attention)? | **New (#800).** What is wrong with the session that `status` cannot say: its agent needs signing in again, its turn ended on an error, or it stopped responding. Absent when nothing is |
| `class` | `"needsYou"` \| `"done"` \| `"fyi"`? | **New (#913).** The session's notification tier, which every client follows instead of its own rules: [Notification class](#notification-class-913). Absent from an older host; a client then keeps today's behaviour. A client that does not know a value reads it as absent |
| `classSince` | time? | **New (#913).** When the session entered its present tier, the episode's start, which the 15-minute reminder counts from. Kept while `class` is unchanged (a further question does not move it); on a change of `class`, for `needsYou` the earliest of `question.askedAt`, `attention.since` and, after a crash, `restored.at`, otherwise `statusSince` ([Notification class](#notification-class-913)). Present with `class`. Never shown |

**Status precedence** with sub-agents (first match wins): the session's own question
(`waiting`); a sub-agent's question (`waiting`, with `waitingOn`); the main agent's turn
running (`working`); the main agent idle with background agents or commands still running
(`idle` with `background`, shown as delegating); otherwise `idle`. Only the second is new,
and it was already `waiting` for a sub-agent's permission prompt before hosts said whose it
was.

### Phone sign-in (#800 slice 2, enabled per machine by #972)

`sessions.signin` is **off by default** and turned on **per machine**, by the machine's own
`config.json`: `permissions.phoneSignIn: true` ([host-config.md](../host-config.md#phone-sign-in)),
set from the Windows app's Settings ("Let my devices sign Copilot in on this machine", a
`host.settings` change over the local pipe, which an agent session may not make) or by
`agent-bridge-host config set permissions.phoneSignIn true` at the machine. It is read when the host
starts. A host advertises `sessions.signin`, and says `signIn: "phone"` on a session's
`signInNeeded` attention, only while it is on **and** the host has an adapter that can run for
that agent: today Copilot alone, on Windows alone, with Copilot's `.exe` found when the host
started. Off, or for another agent, `signin.start` and
`signin.cancel` return `refused` and no attention says `phone`. Devices cannot change the setting
yet; that waits for #801's settings path. The capability is offered only to a channel the
person authority below accepts: a device the machine's **device roster pins as a person's**. And
no attention says `phone` while the roster pins nobody (#972 slice 2), so no client offers a Sign
in every device would be refused; a client still offers it only with the capability.

**The person pin (#972 slice 2).** The pin is #55's signed roster entry
([device-approval.md](../design/device-approval.md)), read from the machine's verified copy of
the account's roster through one interface, `IPersonDeviceRoster`: `State` (`None`, `Ready`
or `Unreadable`), `Find(key, account)`, `Pinned(account)`, `StillPinned(device)` and
`Changed`. Trust's #55 slice A implements it on its verifier; until then the host's is
`NoPersonRoster`, which pins nobody, so **no device is offered sign-in on any machine yet**. A
device is pinned only when all of these hold (Trust's conditions on #972, 6700902097):

* the roster is `Ready`. `None` and `Unreadable` both pin nobody, and `StillPinned` is false on
  any doubt, a stale roster (device-approval.md §6.3) included;
* `Find` matches the channel's full static key and relay account, and its device id is that
  key's. The roster's own kind (`app`, `pwa` or `phone`, `RosterKinds.IsPerson`) and presence
  (`key` or `webauthn`) decide; **the channel's self-declared kind is never an input**;
* the device is past probation and confirmed (§3.4): one that isn't is not returned at all;
* the machine is not read-only, and, where `trustedPeers` enforcement is on, the channel's
  trusted-peer role is `person`. **A #402 trusted-peer `person` pin no longer opens sign-in**:
  it is still needed there for the channel to act at all, and never enough.

A grant is bound to the roster entry's `generation` and `add` hash, which change when the
device is removed or added again. `Changed` fires on every change, a removal heard from another
host included, and the host looks at every flow at once, as well as every 250 ms: a flow whose
device is no longer pinned ends `cancelled` and its code is purged, and a re-added device is a
new grant that never joins the old flow.

* `signin.devices {}` (capability `signin.devices`, **new**) returns who can sign an agent in on
  this machine, read-only: `{state: "none" | "ready" | "unreadable", devices: [{name, kind,
  platform, thisDevice?}]}`, from the same roster and the same rules, `thisDevice: true` on the
  asking device's own entry. Never a device id, key, generation or presence; the name and
  platform are the roster's (the enrollee's words), with control characters removed. Offered
  through the relay only, wherever `sessions.signin` itself is turned on (whether or not the
  asking device is pinned), and only to a `person` role where `trustedPeers` enforcement is on:
  `service` and `host` peers are refused it. A device shows it on its Machines page (ui.md §6.6);
  the app beside the host has the same in its status (`signInDevices`,
  [local-pipe.md](local-pipe.md)). Nothing adds or removes a device here: adding is #55's
  enrollment, and removing a single device waits for #55's Remove.

Copilot's adapter (`CopilotSignInAdapter`) runs the CLI's own `copilot login --device-code`,
unpatched, under Engineering's conditional approval (#800 6648319937): **the machine opens a
browser and copies the code**. There is no replacement OAuth client and no installed-CLI patch.
It runs `copilot login --device-code --host https://github.com --log-level none` (the CLI's
supported option, so login never writes its default debug log), with the host-resolved absolute
`.exe` (never a `.cmd` shim, which would need a shell), `COPILOT_HOME` set to the host's own
Copilot home, standard input closed (so the CLI's plaintext-storage question is answered no and
its keychain persistence and organization-policy checks stand), inherited token variables and
`COPILOT_DEBUG*` overrides removed by name without examining their values, and the process and
anything it starts in a Windows job that ends them together (a browser the login started fresh
closes when the flow ends). Both output streams stay in the
host process: standard error is drained and discarded by the runner, and standard output is
read only by the adapter, which turns the CLI's own lines into the private runner's frames:

| CLI line (1.0.105), in this order and nothing else | Frame |
|---|---|
| `To authenticate, visit <verificationUri> and enter code <userCode>` | `code`, with the URL and code checked as below |
| `Waiting for authorization...` | none |
| `Signed in successfully as <account>.` (printed only once the credential is stored and the organization policy allows it) | `done`, `persisted` and `policyAllowed` true; the account text is discarded |

Any other line, a line out of order or one over the runner's limits ends the flow `refused`,
without saying what it was; a nonzero exit is `failed`. The CLI prints no expiry, so the code's
`expiresAt` is the host's own 10-minute flow deadline, shorter than GitHub's 15-minute device
codes. Upstream follow-up for a no-browser, no-clipboard mode:
[github/copilot-cli#5358](https://github.com/github/copilot-cli/issues/5358).

The request/event contract is:

`account`, `signIn` and `recovery` are structured host metadata, never copied or inferred
from CLI prose, messages or displayed account strings. An account with no structured
identity remains absent. A requester can select a known identity but cannot supply one
to publish. The private adapter accepts no account/sign-in/recovery fields; unexpected
fields fail closed. Recovery values come only from the host's typed lifecycle.

* `signin.start {request: UUID, agent: "copilot", account?: string}` returns
  `{outcome: "started" | "busy" | "refused", flow?: UUID, reason?: string}`.
  The host resolves the effective credential-store identity from its own state,
  serializes that store, binds UUID receipts to target and authenticated device generation,
  and starts only after returning the response. A matching explicit request can reattach
  the same person key/generation and replay its unexpired code; a reconnect alone cannot.
  Terminal receipts precede active-failure validation, so the original requester can
  retrieve `signin.done` after real model proof clears attention.
  A nonterminal retry that can no longer attach (for example after a newer auth error)
  is refused immediately, not acknowledged as busy with no later event.
* `signin.code {flow, agent, userCode, verificationUri, expiresAt}` is sent only to
  explicit, still-authorized participants over their encrypted channels, never the
  session feed, hello, push, HA, transcript, log or a prompt. The code is uppercase ASCII
  `XXXX-XXXX` (letters/digits), and the URL must equal
  `https://github.com/login/device` byte for byte. No browser link supplied by a peer
  or near-match URL is accepted. A queued private event rechecks authorization at
  encryption, before spending a nonce.
* `signin.cancel {flow}` is accepted only from a participating person binding.
  `signin.done {flow, outcome, resumed, continue}` has constant outcomes
  `signedIn | expired | cancelled | failed | refused` and never carries private output.
  Cancel, expiry, revocation/G5 and shutdown purge the retained code and abort the
  contained helper. Slots/receipts, participants, output, waits and cleanup are bounded.
  Launch authorization is checked after source-state waits and again by the factory
  at the actual launch, including after any queue.
  Terminal overlay updates are best-effort and bounded: a stalled source cannot hold
  cancellation or cleanup hostage. A timed-out process cleanup stays quarantined until
  disposal actually succeeds.
  Send deadlines cancel the underlying encrypted send as well as its wait, including
  terminal delivery and receipt replay. Lifetime expiry reports `expired` whether the
  worker or sweep observes it first; explicit cancellation before expiry stays `cancelled`.
* A current roster **person** pin (above) is required, and, where trusted-peer enforcement is
  on, a `person` role too. A self-claimed kind is never an input. Service/host roles,
  removed/re-added roster entries, a roster that is absent, unreadable or stale, read-only G5
  machines and unapproved/default configurations get no capability.

The private runner accepts **adapter frames, not guessed Copilot console fixtures**:
one strict JSON line with exactly `kind: "code"`, `userCode`, `verificationUri`,
`expiresAt`, then a `done` line with exactly `kind`, `outcome: "signedIn"`,
`persisted: true`, `policyAllowed: true`, and successful process exit. Duplicate,
unknown, malformed/invalid-UTF-8, trailing or oversized output fails closed. The
adapter supplies `expiresAt` (above) and the runner caps it at the host deadline.
Stdout/stderr never enter the generic command runner or a logger; errors expose only
constant outcomes. The only concrete process factory is Copilot's adapter above.

`signin.done signedIn` sets **pending**, not recovered: `signInNeeded` remains until
the existing parser sees real main-agent model output after the latest auth failure.
Prompts, turn boundaries, CLI error notices and login/resume acknowledgements do not
clear it. An old callback cannot overwrite a newer flow/failure. Host-owned silent
recovery has the same checking → pending → model-proof contract and initiates no
device flow or approval. Its injectable evidence source is unavailable by default
and may accept **positive main-model evidence only**, for the same host-resolved
credential scope. There is no status provider, credential-file inspection or auth
probe. Even proof from another matching session only makes this session pending:
its own real main-model reply is required to clear its attention.
Dead-session resume/reload is an interface dependency on **#780 Host B**; the supplied
stub starts/types nothing and claims no resumed session ids, so `signin.done` lists none in
`resumed` or `continue` yet. No signed-in push is emitted. Ordinary "finished" pushes on an
auth failure are already suppressed (#845, with #841).

### Attention

**New (#800).** What is wrong with a session that its `status` cannot say, shown over the
status rather than instead of it.

```json
"attention": {
  "kind": "signInNeeded",
  "since": "2026-11-04T23:45:05.000Z",
  "agent": "copilot",
  "provider": "github",
  "message": "Copilot needs you to sign in again (401)."
}
```

| Field | Type | Notes |
|---|---|---|
| `kind` | `"signInNeeded"` \| `"error"` \| `"notResponding"` | A client reads an unknown kind as `error` |
| `since` | time | When it began: the error's time, or for `notResponding` the last event's |
| `agent` | `"copilot"` \| `"claude"` \| `"codex"`? | The session's agent, repeated so the per-machine roll-up and the push read the same way |
| `provider` | string? | `signInNeeded`: whose sign-in it is, `github`, `anthropic` or `openai` |
| `account` | string? | `signInNeeded`: the account, when the host knows it from metadata (the login the CLI reports), never from a token. Absent when unknown |
| `message` | string? | One plain-text sentence the **host composes** from `kind`, `errorClass`, `statusCode` and `provider` alone, at most 300 bytes: "Copilot needs you to sign in again (401).", "Copilot hit a rate limit (429).", "Copilot stopped with an error." **No part of the agent's own error text is ever in it** (#841): redacting arbitrary text never closed, and this field reaches every client and HA. A client may show it in Details, as text and never as a link; its row words come from `kind` and `errorClass` (ui.md §6.13) |
| `errorClass` | `"auth"` \| `"rate"` \| `"quota"` \| `"model"` \| `"network"` \| `"other"`? | `error`: what sort, from the agent's structured fields, never its wording. Unknown reads as `other` |
| `retryAt` | time? | `error`: when the agent said it can be tried again (a rate limit, a quota) |
| `signIn` | `"phone"` \| `"machine"` \| `"unknown"`? | `signInNeeded`: `phone` when this host can relay the agent's own device-code sign-in to a person's device; `machine` only when the host knows it can be signed in nowhere else. Absent while not known; a host never sends `unknown`. A value a client does not know reads as `unknown`, which claims nothing: no sign-in from the phone is offered on it, and it is not said to be the machine's only |
| `recovery` | `"checking"` \| `"signingIn"` \| `"pending"` \| `"failed"`? | `signInNeeded`: the host is checking for a still-valid stored credential, a person has started signing in, the agent is signed in again and the host is waiting for the session to show it (`pending`), or the check found none (`failed`). Absent otherwise. Unknown reads as `failed` |
| `lastEventAt` | time? | `notResponding`: the transcript's last event |

* **`status` stays truthful.** A turn that ended on an error or a sign-in failure has ended, so
  the session is `idle` (or `waiting` if a question is pending), with `attention` beside it. A
  prompt sent while it still needs sign-in shows `working`, still with `signInNeeded`, until the
  agent answers or fails again. `notResponding` is a session still `working`. A client shows
  "Needs sign-in", the error's words or "Not responding" over the status, ranks it with the
  sessions that need the person, and never shows it as plain idle or with a stale age.
* **It goes**, and `session.patch` carries `"attention": null`:
  * `error`: when the next turn starts: a prompt or a turn start, not context the CLI adds within
    a turn (memory, a skill) or its own notice of the error.
  * `signInNeeded`: only when the model is seen answering, in a message of its own words or a
    tool it asked for, dated after the latest sign-in failure. A prompt, a turn starting or
    ending, a hook and anything the bridge typed are written whether the agent can reach its
    model or not, so none of them clears it: on VM1 (#800) a session ended a turn between two
    sign-in failures 51 ms apart. A sign-in alone never clears it: once one finishes
    (`signin.done` `signedIn`, when `sessions.signin` comes) or a still-valid credential is found,
    it stays `signInNeeded` with `recovery` `pending` until that same evidence, and no newer
    failure, shows the session can reach its model. A host that did not read part of a long
    transcript (past its skim budget) keeps a failure it read before that part: bytes never read
    are no evidence. Only what it does read after them clears it, by the same evidence, and a
    newer failure there moves `since`.
* A change to it moves `updatedAt`. It is a few hundred bytes and never trimmed by the
  [session budget](#session-budget).
* **Older clients** ignore it and read the status, so a signed-out session reads as idle to them.
  The [`signIn` push](#when-a-host-notifies) reaches them all the same: they read its kind as
  `question`.

**What a host sends today.** Copilot only, from the `session.error` record that ends a turn,
read by its structured fields (VM1's sign-in failures, #800, carry all three):

* `signInNeeded` (provider `github`) when `remediation` is `"sign_in"`, `errorType` is
  `"authorization"` or `statusCode` is 401;
* `error` for any other, with `errorClass` `auth` for status 403, `rate` for 429 and `other`
  otherwise, except while the session needs sign-in, which another error does not replace;
* nothing for an `errorType` of `"model_call"`, which the CLI retries within the turn, or
  `"hook"`, which ends nothing. A delegate's error is its own, not the session's.

`notResponding`, for any agent's session, from the host's own watchdog (#800 P0, part C): the
session is `working` and its transcript has had no event of any kind (a model call, a hook or a
delegate's tool counts) for **10 minutes**, or **30 minutes** while a tool, a delegate or a
background shell is running. A Claude Code or Codex delegate that is still working writes its own
transcript instead (Claude's beside the session's, Codex's a rollout of its own thread): its last
event there, read by its timestamp and never the file's write time, counts too, and one the host
cannot check (its transcript not found or unread, or more working than are listed) means the
session is never called not responding. `since` and `lastEventAt` are the time of the session's own last event. Not over a
question pending (the session is `waiting`) or another `attention`. It goes with the next event.
Copilot's sign-in failure is written as `assistant.turn_end`, then `session.error` a few
milliseconds later; read between the two, the session is briefly `idle` with no `attention`, which
is why a `finished` push waits 3 s and is withdrawn once the `attention` comes.

`message` comes from one table (`AgentErrors.Template`), with the agent's name and the status
code when it is an HTTP status (100 to 599):

| `kind` / `errorClass` | `message` |
|---|---|
| `signInNeeded` | "*Agent* needs you to sign in again (*status*)." |
| `error` / `auth` | "*Agent* was not authorised (*status*)." |
| `error` / `rate` | "*Agent* hit a rate limit (*status*)." |
| `error` / `quota` | "*Agent* has used up its quota (*status*)." |
| `error` / `model` | "*Agent*'s model returned an error (*status*)." |
| `error` / `network` | "*Agent* couldn't reach its service (*status*)." |
| `error` / `other` | "*Agent* stopped with an error (*status*)." |
| `notResponding` | "*Agent* isn't responding." |

Copilot CLI follows the error that ends a turn with a notice of its own, an `assistant.message`
that starts "⚠️ Turn error:" and quotes the error's words. Those words can hold a credential, a
path or an address, so the host never shows them (#846). In `activity.response` (or
`own.response`) the notice reads "⚠️ Turn error: " followed by the same sentence as `message` for
that turn's error, or "Copilot stopped with an error." when no error was read before it in the
turn. Clients, HA and a `finished` push therefore never carry any of the agent's error text.

Claude Code and Codex send none yet: their records are read only once a real, redacted sample of
each has been captured. `recovery`, `signIn` (`phone`, [above](#phone-sign-in-800-slice-2-enabled-per-machine-by-972)),
the `signIn` notice and the `sessions.signin` capability are in place for Copilot; `account`, the
per-machine roll-up in `hello` and the `signedIn` notice follow in later slices of
#800; their shape is in [#800](https://github.com/danswett/agent-bridge/issues/800#issuecomment-6646501884),
and this section grows with each.
### Driver

Whether a person or an agent is driving the session: agent-ha-bridge's purple edge, drawn in
this project's clients as the violet diamond (ui.md §2.2 rule 7). A client keeps the session's
status glyph and words, adds the `driven-badge` diamond to the glyph, turns the glyph's active
colour (working, delegating) to the agent role, and puts the status words in agent colour, so a
session left under an agent's control still says so while it is idle. It says so in words too
("Driven by an agent"), never by colour or shape alone.

* **A turn is a prompt started.** An agent's turn finishing is not a new turn, so a session left
  with an agent stays the agent's while it is idle. A prompt that starts with nobody's input waiting
  for it, or after that input's 600 s lapsed, was typed at the machine, and is the person's.
* **A reply or a launch** made through the host shows the session as its sender's at once, and the
  next prompt to start within 600 s (for a launch, its opening prompt) is theirs.
* **An answer** continues the turn it was asked in: that turn is the answerer's from the moment it
  is delivered, and nothing is left waiting for the next prompt, which is whoever's it is. An
  answer typed at the machine is not seen as input, so it takes nothing back until the person's
  next prompt, as on agent-ha-bridge (#134).
* **Taken back** by the person: an answer or reply from any of the person's devices (or a press by a
  person on the Home Assistant card) shows the session as the person's at once. Each request does
  so once: a resend, which the host answers from the outcome it remembers without typing anything,
  takes nothing back, and nor does an answer or reply confirmed only after an agent's input
  arrived meanwhile, which came later.
* **Who is an agent.** Agents have no principal of their own on the pipe or relay yet (design
  §5.5; the pipe refuses them anything but reading, #44), so today the only agent principal a host
  sees is a press, answer, reply or launch on its Home Assistant cards made by an account listed
  in `homeAssistant.agentUserIds` ([ha-connector.md](../ha-connector.md)), or a reply-card publish
  that names `driver: agent`. `driverPrincipal` is then `homeassistant:<user id>` for the account
  (absent for a publish, which names no account). Once agents have their own principal (#45), it
  is set from theirs the same way.
* It is kept in memory, as agent-ha-bridge keeps it: a host that restarts, and a session that goes
  and comes back, starts again as the person's.
* A change of driver is something a client shows, so it moves `updatedAt` and sends an upsert. It
  is never trimmed by the session budget.

### Activity

| Field | Type | Notes |
|---|---|---|
| `response` | string? (Markdown) | The latest assistant text, at most 16 KiB. When it's longer, the **end** is kept and `responseTruncated` is true. It may be a subagent's, including a background agent's while the session itself is idle, as on agent-ha-bridge's card; `source.response` then names it. `status`, `userMessage` and `model` are always the session's own |
| `responseTruncated` | bool? | |
| `reasoning` | string? | The latest reasoning text, at most 4 KiB (the end is kept). Like `response`, it may be a subagent's; `source.reasoning` then names it |
| `currentTool` | ToolCall? | The tool running now, while `working` |
| `history` | ToolCall[]? | Only in `session.get`: the latest 50 tool calls, oldest first |
| `userMessage` | string? | The latest user message, at most 2 KiB |
| `userMessageFrom` | `"person"` \| `"schedule"` \| `"agent"` \| `"other"`? | **New (#489).** Who wrote `userMessage` when it was not the person typing: `schedule`, a prompt the agent's own scheduler sent; `agent`, one another agent sent, including a reply an agent sent through the bridge (`driver: agent` for the turn it started); `other`, anything else that is not someone typing. Absent means the person, and so does `person`. An unknown value reads as `other`, never as the person's. Absent whenever `userMessage` is |
| `userMessageLabel` | string? | **New (#489).** A short plain-text label for a `userMessageFrom` that is not the person's, at most 64 bytes: a schedule's number (`"4"`, shown as "Scheduled prompt · #4"). Absent whenever `userMessage` is |
| `turn` | string? | **New (#292).** The id of the latest turn, which `userMessage`, `reasoning`, `currentTool` and `question` belong to: the same id [`session.history`](v1-history.md#live-turns-and-history) gives it, so a client merging live activity with history pages draws it once. Absent before the first prompt, when the host could not read where the latest turn began, and after a record that may have started a turn but could not be read, until the next readable prompt. Never trimmed by the session budget |
| `responseTurn` | string? | **New (#292).** The id of the turn the main agent's own latest reply (`own.response`, else `response`) was written in: `turn` once the latest turn has replied, an earlier turn's until then, so a client never draws the last turn's reply under a new prompt ([v1-history.md](v1-history.md#live-turns-and-history)). Absent with no such reply or when its turn is not known. Never trimmed |
| `turnCount` | number? | User prompts whose turn has finished: the prompts sent, less one while a turn is still in progress (`working` or `waiting`). Not the agent's own model rounds: Copilot writes `assistant.turn_end` after every round of tool calls, and those are not counted |
| `compactions` | number? | Context compactions so far |
| `background` | Background? | Work the session started that is still running after its own turn. Absent when there is none |
| `source` | `{ "response"?: string, "reasoning"?: string }`? | Which sub-agent wrote the top-level `response` and `reasoning`, each on its own, by its `subagents` id: a main-agent reply can sit beside a delegate's reasoning. A field left out is the main agent's own. Absent while both are. Older clients ignore it and keep the last-writer-wins text |
| `own` | `{ "response"?, "responseTruncated"?, "reasoning"?, "at"? }`? | The main agent's own text for each field whose top-level text is not it: one `source` names a sub-agent for, or one that is absent while the host has the main agent's from the part of the transcript it only skimmed (a host starting after delegates filled what it parses). 16 KiB and 4 KiB, the end kept, as above; `at` is when the main agent last wrote either. A field `source` names but `own` lacks means the main agent has none, unless `own.responseTruncated` is set, which means the [session budget](#session-budget) dropped its response. So a newer client shows, for each of `response` and `reasoning`, `own`'s when `source` names an agent for it, otherwise the top-level one, or `own`'s when that is absent; and never shows a delegate's words as the main agent's. Absent when it would be empty |

### Background

`{ "agents": 2, "shells": 1 }`. Present only while at least one is above zero, and then
both are sent.

| Field | Type | Notes |
|---|---|---|
| `agents` | number | Background agents still working: Copilot `task` agents started in background mode, including one woken again by a later message, at any depth. One between runs (a `subagents` entry `idle`) is not counted |
| `shells` | number | Background shell commands still running: started async, or started sync and left running in the background |

**`status` is unchanged by it.** A session whose own turn has ended is `idle`, truthfully;
`background` says why it is not finished. A client shows a session that is `idle` with
`background` as **delegating**: the `delegating` glyph, and the status text of
[ui.md](../design/ui.md) §2.2 rules 4, 5 and 8 ("Delegating to 2 agents", "1 background
command"). An older client ignores the field and shows `idle`, as before. A change in
either count is something a client shows, so it moves `updatedAt` and sends an upsert.
Counts are never trimmed by the session budget.

### Subagent

One agent the session started, from `subagents` ([design/subagents.md](../design/subagents.md)
§2). The UI calls it a *delegate*.

| Field | Type | Notes |
|---|---|---|
| `id` | string | The agent's own id (Copilot's `agentId`) |
| `parent` | string? | The `id` of the sub-agent that started it; absent when the main agent did. A client lists it under its parent |
| `kind` | `"background"` \| `"task"` | `background` runs past its starter's turn and can be woken by a message; `task` is part of its starter's turn. Unknown reads as `task` |
| `name` | string? | Its label: the starter's description of the job, else its display name, else its agent type. An agent chooses it, so the host cleans it ([design/subagents.md](../design/subagents.md) §6): no control, bidi, format (zero-width, BOM, tags) or other default-ignorable characters, whitespace collapsed to single spaces, at most 60 characters and 120 bytes (cut with `…`), and `Delegate “<name>”` for one that reads as "Main agent". Absent when nothing is left. Clients still clean it themselves |
| `task` | string? | The prompt it was given, at most 512 bytes, the **start** kept |
| `model` | string? | Its own model, never the session's |
| `status` | `"working"` \| `"idle"` \| `"waiting"` \| `"ended"` | `waiting`: a question of its own is pending. `idle`: a background agent between runs, which a message can wake. `ended`: it can never run again (a task agent done, its own abort or failure, or the session's CLI exiting). Unknown reads as `idle` |
| `outcome` | `"done"` \| `"failed"`? | With `idle` and `ended` only: how its latest run went, for the `done` and `failed` glyphs (ui.md §2.1). Absent when the host never saw it end (only its start was read, or its CLI was replaced). Unknown reads as `done` |
| `startedAt` | time? | When it first started |
| `endedAt` | time? | With `idle` and `ended`: when its latest run ended |
| `line` | string? | Its latest response or reasoning, whitespace collapsed, at most 300 bytes, the **end** kept. A `line` changing alone does not move the session's `updatedAt` or send an upsert; the latest arrives with the next change, or from `session.get` |
| `currentTool` | ToolCall? | Its running tool, while `working` or `waiting` |
| `shells` | number | Background commands it started that are still running, while it runs. They count here and never in the session's `background.shells` (agent-ha-bridge#261) |
| `response`, `responseTruncated`, `reasoning`, `history` | | `session.get { id, agent }` only |

An entry starting, ending, or changing `status` or `outcome` moves the session's `updatedAt`.
A woken agent is the same entry working again, never a new one.

### ToolCall

| Field | Type | Notes |
|---|---|---|
| `id` | string | The agent's tool call id |
| `name` | string | Tool name, e.g. `powershell`, `edit`, `Bash` |
| `summary` | string? | One line for display: the tool's `description` argument if it has one, else a short form of its main argument. At most 200 bytes |
| `status` | `"running"` \| `"done"` \| `"failed"` | |
| `startedAt` | time | |
| `endedAt` | time? | |

Tool arguments and results are **not** sent in this slice. They can be large and
sensitive, and no client view needs them yet.

### Question

What an agent is waiting for. The forms from all three agents map onto this one shape.

| Field | Type | Notes |
|---|---|---|
| `id` | string | The id of the tool call that asked it, or for a Copilot permission prompt `<requestId>#<digest>` ([Copilot permission prompts](#copilot-permission-prompts)), or for a Codex approval its hook recorded `<DecisionId>#<digest>` (#958). Either digest covers what the question shows, so different content is a different id. Clients treat every id as opaque. It stays the same while the question is pending |
| `kind` | `"form"` \| `"approval"` | `approval` is a permission prompt with fixed choices (Codex approvals, Copilot and Claude permission prompts). It always has exactly one `choice` field, named `decision`, with the options `approve` ("Approve once") and `deny`, so a client can style them. An answer to one is `{ "decision": "approve" }` or `{ "decision": "deny" }` |
| `message` | string (Markdown) | What the agent wrote, at most 8 KiB |
| `fields` | Field[] | In order. Empty for a plain message |
| `askedAt` | time | |
| `truncated` | bool? | The host had to cut `message` or drop `fields` to fit the budget. A client shows what it has and says to answer at the machine |
| `answerAtMachine` | bool? | No host can answer this prompt from another device: a Copilot permission request of a kind no host drives (or any, on a host without `sessions.approve`), a Claude permission prompt, a Codex approval seen only in its rollout or already typed into once, or a Copilot permission request listed while others wait that the host cannot tell from them on the session's screen ([Copilot permission prompts](#copilot-permission-prompts)). That last can clear under the same `id` once it can be told apart, so a client reads the flag afresh on every update rather than once per question. It still has its fields, so a client can say what it asks, but a client shows no controls for it, says to answer it at the machine ("Waiting on you · answer at the machine", ui.md §2.2), and a `session.answer` for it is `refused` |
| `answerAtMachineReason` | string? | Optional, display-only reason, not an answering capability. `"shape"` accompanies `answerAtMachine: true` only for a Copilot `ask_user` with no fields after reading its schema, or with a present, non-null `choices` (the legacy format, including an empty list or blank string beside a valid schema). PWA and Windows cards then show "Answer at the machine · *machine*" followed by "This kind of question can only be answered at the machine.", both in Needs you's text colour and accessible reading order, without a live region. Absent, malformed or unknown reasons keep the client's existing single line; clients never infer a reason from fields. Read afresh on every update, even with the same question `id`. Row status, attention, HA wording, `otherUnavailable`, delivery refusals and answer guards are unchanged |
| `escaped` | object? | An approval the host showed any character of as an escape: `shown`, `marks` and `approve` ([Escaped approvals](#escaped-approvals)). Approve on it takes the escaped-command confirmation; Deny does not. Neither goes when it is also `answerAtMachine`, as one listed while other Copilot permissions wait usually is ([Copilot permission prompts](#copilot-permission-prompts), #690). Absent when nothing was escaped |
| `agent` | string? | The sub-agent that asked, by its `subagents` id, when it is not the session's own question (`waitingOn` names it too). A Copilot permission prompt is the CLI's one dialog whoever asked, and is answered the same way. A sub-agent's `ask_user` is always `answerAtMachine`: delivery types only into the main agent's, so nothing proves where the keys would land. And while a permission prompt or a sub-agent's question waits beside it, the session's own Copilot `ask_user` is `answerAtMachine` too, since the CLI may be showing the other one; a host also checks the transcript for a pending permission prompt just before typing an answer |
| `waiting` | int? | How many permission prompts the session has waiting, this one included, when more than one is (#690). Copilot asks one for each of a batch of parallel tool calls and shows them one at a time; the host lists the earliest still waiting, so this is always the first of them. A client says so ("1 of 4 permissions waiting"). The next comes as a new question with its own `id` once this one is answered, so an answer to this one never reaches it. Absent with one |

### Field

| Field | Type | Notes |
|---|---|---|
| `name` | string | The key an answer will use |
| `title` | string? | |
| `description` | string? | |
| `type` | `"choice"` \| `"multi"` \| `"boolean"` \| `"text"` \| `"number"` | `choice` takes one of `options`, `multi` any number of them |
| `options` | Option[]? | For `choice` and `multi` |
| `allowOther` | bool? | `choice` and `multi` also accept free text |
| `otherUnavailable` | string? | A reason code: the agent offers Other here and this machine can't send it yet ([Answer reasons](#answer-reasons)). Only where `allowOther` is not `true`. Display only: a client shows Other disabled, with the reason's sentence; it never enables Other or changes what an answer may hold |
| `default` | any? | |
| `required` | bool? | |
| `constraints` | FieldConstraints? | What the agent's schema allows of the value (#766). Absent when it says nothing a host checks |

### FieldConstraints

A Copilot `ask_user` field's JSON Schema constraints, for the input it draws: bounds for a
`number`, length, pattern and format for `text`, how many may be ticked for a `multi`. Copilot
CLI checks none of them on that form and records whatever is typed, so a host checks the answer
against them (`Answers.Check`, and again on what it would type) and `refused`s one outside
them before any key. A client may check them first; one that cannot read them sends the answer
and lets the host refuse it. Not strict: a reader that cannot read them reads the field without.

| Field | Type | Notes |
|---|---|---|
| `minimum`, `maximum` | number? | Inclusive bounds |
| `exclusiveMinimum`, `exclusiveMaximum` | number? | Exclusive bounds |
| `multipleOf` | number? | Greater than 0 |
| `integer` | bool? | The value is a whole number (the schema's `"type": "integer"`) |
| `minLength`, `maxLength` | int? | In UTF-16 code units, as JavaScript counts them |
| `pattern` | string? | An ECMAScript pattern, as `new RegExp(pattern)` reads it: no flags, unanchored. At most 1,024 characters |
| `format` | string? | `email`, `uri`, `date` or `date-time`, checked as Copilot CLI does (`^[^\s@]+@[^\s@]+\.[^\s@]+$`; `^https?://.+`; `YYYY-MM-DD`; `YYYY-MM-DDTHH:MM` with optional seconds, fraction and offset `Z`, `z`, `±HH:MM` or `±HHMM`), dates as real dates. Other formats are not carried |
| `minItems`, `maxItems` | int? | How many options a `multi` takes |

Numbers are compared exactly as written, never as doubles; a host carries only bounds a
JavaScript client holds exactly. Text is checked unless it is empty (`""`, no answer); text of
only white space is checked like any other. It is checked both as typed and as trimmed with
JavaScript's `trim()`, and refused if either form breaks a length, `pattern` or `format` limit:
Copilot CLI's form passes text on as typed, but a trim where it is recorded is not ruled out,
and `"  ab"` is 4 characters typed and 2 trimmed. A host checks the text it would type, line
breaks flattened. A field whose constraints a host cannot check exactly (a bound JavaScript would
hold as another number, a length that is not a whole number, a pattern it cannot read as
JavaScript does, or patterns past the 250 ms one question's patterns share), or whose pre-filled
default breaks them (checked so too), makes its question `answerAtMachine`. So does a `text`
field, required or not, whose pre-filled default is blank (non-empty, but blank as above, or
nothing but what shows as nothing, as free text is judged: `"\u200B"`, `"\u2800"`, `"\u0301"`):
that default is the only answer it can be given from another device, a client leaves blank text
out of an answer, and a required field's is refused as blank. A default with something visible
in it, such as `"a\u200Bb"`, is answered as usual. So does a `text` or `number` field
whose pre-filled default no answer can match: a `text` field's that is not a string, holds half
a surrogate pair (a client sends it as U+FFFD) or is longer than `text` takes (4 KiB), a
`number` field's that is `null`, not plainly a number, or one JavaScript would hold as another
number; a form whose smallest answer exceeds 16 KiB in the largest envelope a host takes (a
256-character session id, a request id, `progress`), counting each pre-filled `text` or `number`
default (a number as the number it holds, written the longer of the ways JavaScript and .NET
write it), and every other default and every required field
at its smallest (a required `number` at 0, or at the number nearest 0 that its bounds,
`multipleOf` and `integer` allow, past an exclusive bound; a required `text`
at its `minLength`, which past 4 KiB no answer can meet); and a form with
half a surrogate pair anywhere in its schema, which a host cannot read. Required `text` that
must match a `pattern` or `format` is counted at its `minLength` (or one character): a host
does not solve a pattern for its shortest match, so such a form may be offered and its answer
refused as larger than 16 KiB, before any key. So does a
`choice` whose schema, or a `multi` whose `items`, has text constraints. A client checking a
`pattern` reads it as JavaScript does, without flags: `\d` and `\w` are ASCII.

### Option

`{ "value": "s6", "label": "S6: TPM-backed device keys (Recommended)" }`. `label` is what
to show; if it's absent, show `value`.

## Session budget

After applying the field limits, a host checks the size of each `Session` it sends. If
it is over 40 KiB, the host trims in this order until it fits, which loses the least for
a reader (`SessionBudget.Fit` in C#). Everything a client that predates sub-agents ignores
goes first, so such a client, and the HA connector, never sees less of a session because
of fields it doesn't read:

1. `subagents`: finished entries (`idle` and `ended`), then every `line`, then every
   `task`, then all but the first 8; each entry dropped is counted in `subagentsMore`.
2. `own.reasoning`.
3. `own.response`, to its last 4 KiB (`own.responseTruncated` set), then the whole of it,
   keeping `own.responseTruncated`.
4. Every entry's `currentTool`.
5. The remaining entries, counted in `subagentsMore`.

Then the session's older fields, trimmed exactly as for the same session without any of
the above, and further only if the few ids and flags left (`source`, `waitingOn`,
`subagentsMore`, a question's `agent`) still overflow:

6. Tool history, oldest first.
7. `reasoning`.
8. `userMessage`, with `userMessageFrom` and `userMessageLabel`.
9. `response`, to its last 4 KiB (`responseTruncated` set).
10. The question's `message`, to its first 2 KiB (`truncated` set).
11. The question's `fields` (`truncated` set).
12. `response` and `currentTool`.

`own` cannot simply stay whole: 16 KiB of emoji is 48 KiB of JSON on its own. A field
`source` names with `own.responseTruncated` set but no `own.response` was the main agent's,
dropped for the budget.

A list page holds as many sessions as fit in 48 KiB, and always at least one, since every
session is at most 40 KiB.

## Where a host gets each field

| Agent | Sessions | Activity and status | Question |
|---|---|---|---|
| Copilot CLI | `~/.copilot/session-state/<id>/` with a live `inuse.<pid>.lock`, plus `workspace.yaml` | From `events.jsonl`: `user.message`, `assistant.message` (`content`, `toolRequests`), `tool.execution_start` and `_complete`, `assistant.turn_start` and `_end`, `session.compaction_complete`, `session.model_change`, and `session.error` for [`attention`](#attention) | A `tool.execution_start` with `toolName: "ask_user"`, using its `arguments.message` and `arguments.requestedSchema`. Pending until a `human_response.recorded` or `tool.execution_complete` with the same `toolCallId`. A `permission.requested` (its `promptRequest`, else its `permissionRequest`) is an `approval`, pending until the `permission.completed` with the same `requestId` |
| Claude Code | The bridge's registration files, else Claude Code's own `~/.claude/sessions/<pid>.json` (an interactive CLI, its pid running, written no earlier than that process started); and `~/.claude/projects/<slug>/<id>.jsonl` | Transcript `assistant` entries (`text`, `thinking`, `tool_use`) and `user` entries | A `tool_use` named `AskUserQuestion`, pending until a `tool_result` with its id. Claude Code 2.1.312 writes that `tool_use` only once the question is answered, so without agent-ha-bridge's `PreToolUse` hook a waiting question is not seen. A permission prompt, which only agent-ha-bridge's hook reports (its status `waiting`, newer than the transcript, over a running tool), is an `approval` with that tool's id, `answerAtMachine` |
| Codex | The bridge's registration files, else (Windows) `~/.codex/thread-writer-locks/<id>.lock`, as the Restart Manager reports who holds it open. The lock must be held by exactly one running `codex.exe`, and that one must be an interactive terminal: its command line read, with no subcommand (a prompt at most), `resume` or `fork`, and no `app-server` or `mcp-server` argument. That is a Codex run with `--no-daemon`, not `exec`, `review`, `migrate-rollouts` and the like. A lock shared with the daemon or with an unreadable Codex names nobody. Each decision is checked again after 30 s. A terminal holding several locks is the session of the one whose rollout `session_meta` is its own top-level thread (`id` and `session_id` equal, `thread_source` "user") with an event since it started, else of none. A lone lock whose rollout names another thread (a sub-agent's) is not a session either. A registration agent-ha-bridge marked ended keeps its thread from being found this way; and `~/.codex/sessions/…/rollout-*.jsonl` | Rollout `event_msg` payloads | An approval agent-ha-bridge's hook recorded in `<id>.approval.json` beside the registration: an `approval` whose `id` is the marker's `DecisionId` (its generation), `#`, and a digest of what it shows (#958), `answerAtMachine` once the marker records agent-ha-bridge's own attempt (`DeliveryAttempted`). An `exec_approval_request` or `apply_patch_approval_request` in the rollout with no marker is an `approval` keyed by its `call_id`, `answerAtMachine` |

Copilot, Claude Code and Codex report `background` and `subagents`, each from what its own
transcript says (below, and [design/subagents.md](../design/subagents.md) §3). A host
older than `subagents` sends counts alone.

On a machine that still runs agent-ha-bridge, a shadow host **only reads**. It never
opens the hook spool, never writes into a session folder, and never deletes or locks
anything another process uses.

### Background work (Copilot)

Read from the same `events.jsonl`. Every rule below was checked against real transcripts
(85 background agents and 535 background shells; the evidence is in the pull request
that added `background`).

* **Agents are tracked by id, never counted.** One starts with `subagent.started` whose
  `executionMode` is `background`; its own events carry its `agentId` at the top level.
  It is working until its own turn bookkeeping says it has stopped: an
  `assistant.turn_end` stamped with its `agentId` whose turn's **latest** reply asked for
  no tools (a reply asking for tools, their results, then a final reply with nothing to run
  is a turn that ends the run, #84). A later `user.message` stamped with its `agentId` (a
  message sent to it) makes it work again.
  Its own `abort` or `session.shutdown` (stamped with its `agentId`) ends its run and
  nothing else. `subagent.completed` (by `toolCallId`) and a `system.notification` of kind
  `agent_idle` (by `kind.agentId`) also end a run, but neither is enough alone:
  `subagent.completed` is written once, after the first run only, and again for every
  agent at shutdown (only the first counts); `agent_idle` arrives when the parent next
  takes notifications, which was up to 90 minutes late, and for 3 of 44 later runs never.
  A late one can arrive after a message has woken the agent again, so each is spent on a
  run that already ended without one, and only a notice no run is owed ends the current
  run.
* **Shells, likewise by id.** A main-agent `tool.execution_complete` whose whole result is
  `<command started in background with shellId: X>` or `<command with shellId: X moved to
  background by the user…>`, or whose last line is `<command with shellId: X is still
  running…>`, starts one. A last line `<shellId: X completed with exit code N>`, a whole
  result `<command with id: X stopped>`, or a `system.notification` of kind
  `shell_completed` with `kind.shellId` ends it. Markers anywhere else in a result are
  quoted text (a command printing a transcript, say) and are ignored. Detached shells
  (`started in detached background`) are services meant to outlive the session and are
  not counted. A subagent's shells are its own work, covered by its agent, and do not
  report their ends to the session.
* **Nothing outlives its CLI process.** `session.shutdown` ends everything, and work
  that began before the session's current process started is dropped: a CLI that was
  replaced can never report back what it started.
* **Sub-agent entries** (`subagents`) come from the same events, for every agent, `task`
  (sync) ones included. Checked against the CLI 1.0.105 transcripts on one machine; the
  recorded shapes are in `tests/fixtures/copilot/subagents.jsonl`.
  * `subagent.started` is stamped with the new agent's `agentId` and carries `toolCallId`,
    `executionMode`, `agentDisplayName`, `agentType`, `model`, and `parentId`: the
    `agentId` of the sub-agent that started it, absent for the main agent's. A `task`
    tool call stamped with an `agentId` says the same.
  * `name` is the starting `task` call's `description` argument, else
    `agentDisplayName`, else the agent type. `task` is its `prompt` argument, else the first
    `user.message` stamped with the agent's id, which is that prompt.
  * `model` is `subagent.started`'s, then its own `subagent.configured`,
    `session.model_change` and `assistant.message` (#73: never the session's).
  * The starting `task` call, the first stamped `user.message`, `subagent.started` and the
    model events are read from the skimmed part of the transcript too, so an agent started
    before the parsed window keeps its name, task, parent and model. A model is dropped
    when the host could not read everything before the window, as the session's is. The
    latest 64 sub-agent tool calls are read there too, so a permission prompt in the window
    for one whose call is not is still that agent's.
  * An agent of a CLI process that has since been replaced is `ended`: mid-run with no
    `outcome` or `endedAt`, since nothing will say how it went; between runs keeping both.
  * A run ends as for background agents above, with outcome `done`. A run whose start
    the host only skimmed still gets the `outcome` and `endedAt` of the event in the window
    that ends it (a `task` call completing, `subagent.completed`, a late `agent_idle`). A task agent is
    `ended` when its run ends, or when its `task` call completes (`failed` if the call
    failed), or when the main agent's turn is aborted (`failed`) while it was that turn's:
    a task agent the main agent's `task` started, or one nested only under such agents. One a
    background agent started belongs to that agent's run, and the main agent's abort leaves
    it alone. `subagent.failed` or the
    agent's own `abort` ends it `failed`, even when no run of it was seen going, and clears
    any question of its still pending; the task agents it was itself waiting on (down to a
    background one) end `failed` with it, as does a failed `task` call's, with their
    questions. Its own
    `session.shutdown` ends it, `failed` only if mid-run. The session's
    `session.shutdown` ends every entry: `failed` if it was mid-run.
  * Its shells are read from its own tool results with the same markers, skimmed ones
    included, and count on its entry while it runs; their ends come back only in its own
    results (never a `shell_completed` notification to the session).
  * The main agent's own text (`own`) is read from the skimmed part of the transcript too, so
    a host starting after delegates filled the parsed window still has it.
  * **Questions:** a sub-agent's `permission.requested` is written unstamped, naming the tool
    call it gates; that call's `agentId` says whose it is. A `tool.execution_start` of
    `ask_user` stamped with an `agentId` would be that agent's question, but Copilot gives
    its sub-agents no `ask_user`: none of 33,966 sub-agent tool calls in two months of
    transcripts on that machine was one.

### Background work (Claude Code)

Read from the session's own transcript, and for a nested delegate from the files beside it.
Checked against a Claude Code 2.1.316 recording (two delegates, one nested, and a background
command); the shapes are in `tests/fixtures/claude`.

* **Delegates.** A `user` entry whose `toolUseResult` has `status: "async_launched"` and an
  `agentId` (an `Agent` call's result) starts one: `kind` `background`, `name` and `task`
  its `description` and `prompt`, `model` its `resolvedModel`. 2.1.316 launches every
  `Agent` call that way; a result of any other shape lists nothing.
* **Background commands.** A `toolUseResult.backgroundTaskId` (a `Bash` call with
  `run_in_background`) starts one, counted in `background.shells`.
* **Ends.** A `<task-notification>` naming the agent or command by its `<task-id>`, once
  Claude has delivered it: a `user` entry whose content is one and which Claude marks as its own
  (`origin.kind` `task-notification` or `turnOrigin` `task_notification`), or the queue's
  `remove` with `reason` `delivered_to_agent` (for one delivered to a sub-agent) of a notice
  queued earlier. The person can type the same text, and a prompt typed while the agent works
  is queued the same way: a `user` entry not so marked is their prompt, and a `queue-operation`
  `enqueue` (written the moment the agent stops, with no mark of who wrote it) ends nothing by
  itself, but dates the end its delivery confirms. One delivered after a message woke the agent
  since it was queued is of the run before, and the agent is still working. An agent's run ends `done` for `<status>completed</status>` and
  `failed` for any other, and it is `idle`; any run still going under it ends `done` too,
  since Claude notifies an agent only once none of its own is live. A `SendMessage` `tool_use`
  whose `input.to` is an agent's id wakes it, a nested one whose record is read only later
  too. A notice delivered to the main agent starts a
  turn of the main agent's (`working`), but is neither `userMessage` nor a prompt counted in
  `turnCount`, and the delegate's result in it is not the session's text.
* **Nested delegates.** Only `<session id>/subagents/agent-<agentId>.meta.json` says one
  exists and who started it (`parentAgentId`, `description`, `agentType`, `toolUseId`,
  `requestShape`); the first entry of `agent-<agentId>.jsonl` beside it says when it started
  and its prompt. Read once a delegate is known, each again only when it changes, at most
  256 of them (the first by name), and listed only under a delegate that is listed. It ends as above (its notice
  is queued in the session's transcript too), or when the delegate above it ends. One that
  started before the delegate above it began its current run belongs to an earlier run, which
  was notified only once it had stopped: with its end not read, it is listed with no `outcome`
  and not counted. A message
  the delegate above it sends it is in that delegate's own transcript, which is not read (and
  was not recorded): one woken that way stays listed as it stopped.
* Starts and ends are read from the skimmed part of the transcript too. Where the host could
  not read everything before the window, a run the skim saw start is listed with no
  `outcome` and not counted, since its end may be in what was not read, and no delivery is
  taken as owed across what was not read. Nothing a delegate
  writes is in the session's transcript, so `activity.source` is never set.

### Background work (Codex)

Read from the session's own rollout. Checked against a Codex CLI 0.166.0 recording
(`multi_agent` v1, one sub-agent thread); the shapes are in `tests/fixtures/codex`.

* **Sub-agents.** An `item_completed` event whose item is a `CollabAgentToolCall` with
  `tool: "spawn_agent"` starts one per `receiver_thread_ids` entry: `kind` `background`,
  `name` its `receiver_agents[].agent_nickname`, `task` and `model` the item's `prompt` and
  `model`. A `sender_thread_id` other than the session's own thread names its `parent`.
* **States.** Every such item's `agents_states` says how each thread stood:
  `pending_init` and `running` working; `{"completed": text}` ends the run `done`, the text its
  `line`; `{"errored": text}` and `interrupted` end it `failed`; it is then `idle`, open for
  more input. `shutdown` and `not_found` end it. A `user` message
  `<subagent_notification>{"agent_path", "status"}</subagent_notification>`, marked by its
  `internal_chat_message_metadata_passthrough.content_item_kinds`
  `multi_agent.subagent_notification`, says the same (the same text without that mark is the
  person's); after
  a `wait` that already said so it is spent on that run, never ending one input has started
  since.
  `send_input`, `resume_agent`, `send_message` and `followup_task` wake their receivers;
  `close_agent` ends them.
* Read from the skimmed part of the rollout too, with the same rule as Claude's where not
  everything before the window was read. A sub-agent's own words and its own
  sub-agents are only in its own rollout, which the host does not read: what it says never
  reaches the session's text, and its sub-agents are neither listed nor counted.
