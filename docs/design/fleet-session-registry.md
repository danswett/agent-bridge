# Fleet session registry and name lookup

Status: design note (slice 3a of the Home Assistant retirement epic, #1396);
partially implemented, as recorded below.
Revised after Engineering and Trust review (PR #1422). Feeds #1400 (roster fields), #1412 (agent
list operation), #1399 (name lookup) and #1406 (one client protocol). It does not touch the
messaging cut-over (#728), which stays first.

Implementation update, November 10, 2026 (Pacific), fleet #238: the host now wires the
#1791 publisher core to each authenticated relay connection, retaining its epoch/sequence
and pending failed-put backoff across reconnects. `fleet.share` is passed explicitly; failed puts use bounded monotonic
backoff and content-free host status; invalid IDs are skipped and counted. Read replies
preserve the put path's relaxed escaping and are byte-budget tested. Records are still
unsigned; signing, name-lookup authority and the 24-hour dual run (slice 11) remain open.
This is not deployment or live-parity evidence. HA remains the explicit rollback.

Implementation update, November 11, 2026 (Pacific), #2151: name-only lookup replaces
the first-machine-pin requirement, following fleet #171/#433. A unique verified live
match returns `found`; every result carries `fleetScope`, the authenticated relay
account ID captured with that roster read. Caller-supplied scope/account fields cannot
select another fleet. Missing scope refuses explicitly. Reconnect during a read refuses.
Verification is unchanged: real unsigned records remain `unverified` until `hostSign`.
The typed result carries optional `model` only when supplied by the authenticated
roster, never inferred from the request or name. The publisher and relay allow-list
still exclude model; this does not enable live model-checked broadcasts (#416).
The operation runs on any connected host; nodes leaving are handled by the relay's
existing liveness rules. No placement pin, persistent lookup state or new component is added.

Design update, November 12, 2026 (Pacific), #2360: Engineering approved
[explicit enrollment and signed replacement ordering](https://github.com/danswett/agent-bridge/issues/2360#issuecomment-6838324533).
This is documentation only, not an implementation or deployment claim. The four follow-on
slices below do not enable routing; #1776 remains the independent verification gate.

Source baseline for #2360: `d51108263f399fa1dc4bf81d38b30cf31ff2232e`.
Runtime evidence follows; the PR #1422 plan is historical, not shipped reader/client behavior.

## Summary in plain English

**The problem.** Today only Home Assistant (HA) knows, in one place, which sessions exist on
which machines, what each is doing, and how long it has been doing it. The relay knows machines
only. Scripts and agents that need "which session is `SG Quality Lead`, and is it idle?" ask HA.
The goal is that nothing depends on HA.

**The idea.** Each host tells the relay a short, fixed list of facts about its own sessions. The
relay hands that list to the other devices of the same account. Every client reads it the same
way, with the same operation: the PWA, the Windows and Mac apps, iOS, Android, an agent through
the MCP tools, a future HA component, and any third-party client. There is no private path for
any of them.

**What the relay will see (new).** For each session: a stable session handle, an optional name
(at most 64 plain characters, see "Names"), a coarse state (`working`, `waiting`, `idle`,
`ending`), the time it entered that state, which machine it is on, and whether that machine is
online. The approved #2360 extension adds an optional process-start time for replacement
ordering, not transcript metadata. A session with no name that a person set on the host is listed without
one; prompt text, model-written titles and folder names are never used as a name.

**What it did not see before.** Today the relay sees machine names and last-seen times, never a
session. Session names and states have only ever travelled inside the end-to-end channel. So
this is a real new disclosure: a name such as `SG Quality Lead` and "waiting for 40 minutes"
become visible to the relay operator. It will never see prompt text, question text, paths,
repository, branch, model, transcripts or tokens, and a test pins that.

**Who can read it.** Only devices and hosts of the same account (person devices, and a person's
own hosts). An agent never connects to the relay: it asks its own host, on a credential with the
`read` scope, and gets only its own account's sessions. A client that is not a person (an HA
component, a third party) must be paired as a normal client first and gets the same read-only
view; its pairing is a separate design (S6). Another account's sessions never appear.

**What is true today, and what is not promised.** Protected `hostSign` custody landed in #1782;
registry signing and trusted reader integration remain held under #1776. Device agreement keys
cannot sign (agent-principals.md section 8.10); custody alone does not establish enrolled authority.
Until the signing and verification dependencies land,
**the relay can invent, change, hide or replay any record, and no client can tell.** So
an unsigned record is only a display hint, like presence is today: clients may show
it labelled "unverified", but name lookup never returns "found" from it and no message is ever
routed by it. When #1776's signing and trusted readers land, a forged
or altered record is rejected. Even then the relay can still read the list, hold it back, or show
a copy up to 60 seconds old. The list is kept in relay memory only: never in the stored roster,
never in logs, never in metrics.

**Names are not trusted text.** Enrollment is person-only, but a same-user process can tamper
with host state; caller classification is a safety rail, not proof of identity. A name is
display text from an untrusted source. It never
carries an instruction to an agent: an agent that looks up a name gets back only typed fields
(session handle, machine, state, time), and sees a name itself only when it asks for names on
purpose, which marks it as having read untrusted content.

**Decisions needed** (details in "Decisions", below):

1. Owner and Trust: accept that session names and coarse states become relay-visible metadata.
2. Owner: whether each machine can switch sharing off, and the default (proposed: on for
   own-account, with a per-machine off switch).
3. #2360 confirms existing person-only `session.rename` through the Windows app backend or
   host CLI; the new app enrollment entry point and CLI disclosure belong to slice 1 below.
4. Engineering: records stay unverified until #1776's signing and trusted readers land; nothing may rely on
   them for routing before then. Confirm, and decide whether roster v2 must land before slice 2.
5. Engineering: confirm the build order, the resized slices and the numeric limits.
6. Owner: non-person client pairing (Decision 6 below).
7. Superseded by #2151: name-only lookup is canonical; verification remains required.

## What exists today (#2360 source check)

| Runtime evidence at the baseline | Checked source |
|---|---|
| Host-owned names, durable save, normalized load | `src/AgentBridge.Host/Fleet/FleetNames.cs:8-23,35-54,67-84` |
| Name delegate wired; no title import | `src/AgentBridge.Host/Program.cs:678-679`; `src/AgentBridge.Host/Fleet/FleetPublisher.cs:40-45` |
| App/HostCli-only rename; existing CLI; app control still pending | `src/AgentBridge.Host/LocalApi.cs:34-42`; `src/AgentBridge.Host/Program.cs:162-181`; [local-pipe](../protocol/local-pipe.md#person-set-fleet-names-sessionrename-1777) |
| Workspace parser ignores `user_named`; Claude titles are content | `src/AgentBridge.Host.Core/Parsing/Workspace.cs:6,33-34`; `src/AgentBridge.Host.Core/Parsing/ClaudeParser.cs:138-142` |
| Relay limits and strict current allow-list | `src/AgentBridge.Relay/RelayHub.cs:224-252,510-513`; `src/AgentBridge.Relay/RelayOptions.cs:15-16`; `src/AgentBridge.Relay/RosterFields.cs:18-27,63-80` |
| Parser reads no at/seq/epoch/sig, never sets Verified; live names unverified unless incomplete wins | `src/AgentBridge.Host/Fleet/FleetList.cs:13,50-103,185-204` |
| Both fleet ops need agent.hello/read; signing custody and roster key APIs are not enrollment | `src/AgentBridge.Host/Fleet/FleetList.cs:215-227`; `src/AgentBridge.Host/HostSignKey.cs:10-25`; `src/AgentBridge.Roster/RosterState.cs:35-42`; `src/AgentBridge.Roster/EntryBody.cs:42-45` |

## Options

**A. Host-published registry through the relay (recommended).** One small list per machine, held
by the relay in memory, delivered to the account's other devices. Gives one cheap read (a client
needs one socket, not a channel to every host), works while a machine's own channel is down, and
meets the 5 s and 60 s targets. Costs: the relay learns names and coarse states, and until
#1776's signing and trusted readers land the records are unverified.

**B. End to end only.** Every reader opens a channel to every host and calls `sessions.list`.
Nothing new leaks and everything is already authenticated by the channel. Costs: every reader,
including a script or HA component, must hold a Noise identity and reach every host; an offline
machine gives no answer at all; N machines means N channels. That is the path #1396 is moving
away from. Keep it as the fallback for full detail (`session.get`) and as the only way to get a
verified answer before #1776's signing and trusted readers land.

Recommendation: A for the registry, B unchanged for anything richer than the allow-list.

## The registry record

Target signed record after #1776 and the #2360 extension, not today's wire output.
The current publisher omits `startedAt` and `sig` (`FleetPublisher.cs:45,53-56`).
All members are required unless noted.

```json
{
  "v": 1,
  "machine": "<device id>",
  "epoch": "<16 hex>",
  "seq": 18204,
  "at": "2026-11-09T09:12:44Z",
  "truncated": false,
  "sessions": [
    { "id": "<16 hex>", "node": "agent_bridge_<16 hex>", "name": "SG Quality Lead",
      "state": "waiting", "since": "2026-11-09T08:32:10Z",
      "startedAt": "2026-11-09T08:00:00Z" }
  ],
  "sig": "<base64url>"
}
```

* **Allow-list.** Exactly `v`, `machine`, `epoch`, `seq`, `at`, `truncated`, `total` (only when
  truncated), `sessions` and `sig`, and per session `id`, `node`, `name` (optional), `state`,
  `since` today; the planned contract adds optional `startedAt`, omitted when unobservable.
  Today's relay refuses that field (`RosterFields.cs:26,68`); deploy the reviewed new
  allow-list before its publisher. No other field becomes allowed. No `cwd`,
  `repository`, `branch`, `model`, `question`, `title`, summaries, counts of tokens, or anything
  from a prompt or a folder.
* **name** (optional). Present only when the session has a name that came from an allowed source
  and passes the filter in "Names". Absent otherwise. At most 64 characters.
* **state.** One of `working`, `waiting`, `idle`, `ending`, the v1 session status. `since` is when
  the session entered that state (v1 `statusSince`, v1-sessions.md:1731). "Time in state" is
  `now - since`, computed by the reader, so the record does not change every second.
* **epoch and seq.** `epoch` is 16 random hex digits chosen when the host process starts; `seq`
  starts at 1 in each epoch and rises by one per snapshot. Neither needs to be stored across a
  restart (see "Freshness and replay").
* **truncated and total.** `truncated: true` when the host has more usable live session IDs than it may
  publish; `total` is then the usable count. Invalid IDs are skipped and counted in host status.
  See "Size and paging".
* **sig.** Optional in `v: 1`. Absent until #1776 implements registry signing; see "Signing".
* **machine online.** Not in the host's record: the relay adds it from its own socket knowledge
  (see "Freshness and replay").
* **machine display name.** The relay already sends the machine's `name` beside `record`.
  `fleet.list` rows and `fleet.find` machine/session/candidate objects expose it as optional
  `machineName`, independently of session-name content requests. It is trimmed display text,
  capped at 64 raw UTF-16 code units before trimming. Control, format, unpaired surrogate and
  line/paragraph separator characters (Cc, Cf, Cs, Zl, Zp) are rejected before trimming;
  ordinary Unicode and valid emoji pairs stay. Missing, non-string, empty, overlong or
  excluded-character values are omitted, never guessed.
  This adds no relay disclosure; `machine` remains the ID and optional lookup filter, not the display name.

## Size and paging

The hard limit is 65,561 bytes per WebSocket frame (`Wire.cs:17`); a bigger frame kills the
socket (`RelayHub.cs:261-264`). So no frame may ever be built from "all machines". The limits:

| Limit | Value | Why |
|---|---|---|
| Sessions per machine snapshot | At most 64, also byte-budgeted in the planned publisher | Count ceiling, not a promise that 64 named entries fit |
| One session entry (planned) | Previous 230-byte estimate + 35 bytes for `,"startedAt":"YYYY-MM-DDTHH:mm:ssZ"` = 265 | 64 x 265 = 16,960 bytes, already over 16 KiB before headers |
| One snapshot (`record`) | 16,384 bytes (16 KiB) | Budget actual UTF-8 including header/signature; with a 500-byte header estimate, 59 x 265 + 500 = 16,135 |
| One relay frame | 24,576 bytes (24 KiB), always one machine | Under 40% of 65,561 |
| Machines per account | 32 (`RelayOptions.cs:16`) | At most 32 machine frames plus one `known` frame |
| `known` frame | 32 entries of about 150 bytes, under 8 KiB | Known-machine metadata with socket liveness, no sessions |
| `registry-get` | one in 2 s per socket; sooner is refused `slow-down` | A get returns up to 33 frames |

* **One machine per frame.** A `registry-get` is answered with one `registry` frame per machine,
  then one `known` frame, each tagged with the request `id` and `part` and `of`. There is no
  aggregate frame, so the account total can reach about 530 KiB over time without any single frame
  coming near the limit.
* **A put over the cap.** The relay checks size before parsing. A `registry-put` frame over
  24 KiB or a record over 16 KiB is refused with `too-large` as a text reply; the socket stays
  open and the old record stays in place. (A frame above 65,561 bytes still closes the socket,
  as today, but a conforming host never builds one.)
* **More than 64 usable sessions.** The host publishes the 64 with the most recent state change, sets
  `truncated: true` and `total` to the usable count (both signed once signing exists). Nothing is
  silently dropped: see "Names and lookup" for how `fleet.find` reports an incomplete list.
  Hitting the cap is logged on the host as a count, never a name.
* **Planned byte truncation.** The arithmetic is a conservative estimate, not a serializer
  guarantee. Slice 2 must fit actual signed bytes, mark any count/byte omission `truncated`,
  and use `total > published count`, not today's `total > 64` (`RosterFields.cs:52`);
  change that rule with the extension, with a 64-to-59 case
  and encoding-boundary tests. Today the publisher rejects oversized records rather than
  byte-truncating (`FleetPublisher.cs:58-61`). The 16/24 KiB and 64-session caps do not rise.

## Relay frames

New text frames. `registry-put` is accepted only from a `host` device (`RelayHub.cs:14, 17`) for
its own machine id; a person device (`app`, `pwa`, `phone`) cannot put. The relay only ever answers
a socket from the same account, counts every frame against the frame rate
(`RelayHub.cs:257`), and never sends a registry frame to a socket that has not asked for one.

```
host  -> {"t":"registry-put","record":{…}}              full snapshot of that host (≤ 24 KiB)
any   -> {"t":"registry-get","id":3}                    reply: one "registry" frame per machine, then "known"
any   -> {"t":"registry-subscribe"}                     start receiving registry-changed (until the socket closes)
relay -> {"t":"registry","id":3,"part":1,"of":3,"machine":"…","name":"…","online":true,
          "lastSeen":"…","record":{…}}
relay -> {"t":"registry","id":3,"part":3,"of":3,"known":[{"machine":"…","name":"…","lastSeen":"…","online":false}]}
relay -> {"t":"registry-changed","machine":"…","online":true,"record":{…}}   one machine, on change
relay -> {"t":"registry-changed","machine":"…","removed":true}   machine forgotten or revoked
```

* **Opt-in, not a capability flag.** The relay `hello` carries only token, key, kind and name
  (`RelayHub.cs:216-225`), so there is no hello capability to announce. Instead a client opts in by
  sending `registry-get` or `registry-subscribe`; a client that never does is never sent a registry
  frame, so old clients are unaffected. (An earlier draft called this `fleet.registry` in `hello`;
  that does not exist.)
* The relay validates shape, allow-list and size, keeps the newest accepted record per machine in
  memory, and forwards. It does not interpret names or states.
* `known` carries same-account machine metadata, last-seen time and socket `online` state,
  with **no sessions**. A machine's registry frame takes precedence over this metadata.

## Freshness and replay (5 s and 60 s)

The reader checks/cache below are target RC behavior, not implemented by `FleetList.Parse`.
The relay's own received-time/sequence checks are not independent reader verification
(`RosterFields.cs:110-141`).

* A host sends a new snapshot when any listed field changes, debounced to at most one per 2 s,
  plus a heartbeat every 20 s even if nothing changed. A heartbeat is a new snapshot: `seq` rises
  and `at` is the current time. The relay pushes each accepted change at once. Expected lag:
  under 3 s, with a hard target of 5 s.
* The relay marks a machine's held session record offline when its host socket closes, and sets `online: false`
  after at most 60 s without a heartbeat for half-open sockets. The row then shows offline within
  1 minute.
* A connected host without a registry record is online from its `known` socket state, with no
  sessions. This never overrides an expired registry frame's offline state.
* **One staleness rule, used everywhere.** A reader treats a record as live only when its `at` is
  within 60 s of the reader's own clock (earlier or later) **and** the relay says the machine is
  online. Anything else is stale: its sessions are shown as "last seen at …" and never returned as
  live by `fleet.find`. There is no separate 5-minute window.
* **Order.** Within one `epoch` a reader rejects a record whose `seq` is not greater than the
  last one it accepted from that machine. A new `epoch` is accepted when its `at` is later than the
  `at` of the last record accepted from that machine. So a restarted host (new epoch, `seq` back at
  1) is accepted without any state kept on disk, and an old epoch's records are refused after a
  newer one was seen.
* **A fresh reader.** A reader with no history can be shown an old snapshot, but only one whose
  `at` is within 60 s of its own clock, so a replay is worth at most 60 s of lag. Once signing
  exists the relay cannot change `at`; before then it can show anything (see "Signing").
* **Revoked or forgotten machines.** When a machine is forgotten or removed (the roster's
  `forgotten` list, roster.md:157) the relay drops its record at once and sends every subscribed
  socket `registry-changed` with `removed: true`; a `registry-get` simply omits the machine. A
  reader that gets `removed` deletes its cache for it. It does not refuse the machine forever: a
    forgotten machine that connects again is the account's again (roster.md:128), so the reader
    accepts the next valid record of any epoch once the machine is listed again in `registry` or
    `known`: after `removed` the reader treats the machine as having no history, because a machine
    that merely reconnects keeps its epoch and a higher `seq`. A
    reader that was offline learns it from the next `registry-get`, which no longer lists the
    machine, and drops cache entries for machines not listed in `registry` or `known`.

## Offline machines and the cache

This cache is planned RC work; today's fleet op reads the connected relay, not a disk cache.

An offline machine appears in the list (name, `online: false`, last seen) so a reader can say
"machine is off". Its sessions are not served as live: the relay drops them from memory when the
host goes offline. A reconnecting host can appear online before its full snapshot arrives;
no sessions are invented in that interval.

A reader may keep its last copy of a machine's record as a **cache**, with a maximum age of 24 h
(then deleted). Cached records are always marked `stale: true` and `online: false` in `fleet.list`
and are never returned as live by `fleet.find`. The cache exists for two reasons only: showing
"this session was on machine X, last seen at …", and the `ambiguous` and `offline` results in "Names and lookup".

## Names

A session name is **display text from an untrusted source**. This section says where one comes
from, who can set one, and how it is marked.

* **Where a name comes from.** Only an explicit host-owned **fleet name that a person set**
  (or the fixed launch rule), for Copilot, Claude and Codex alike. Without enrollment a
  session is listed unnamed. #2359 found missing enrollment, not a duplicate/scope failure.
  Transcript/title/summary, first prompt, workspace and HA/folder labels are never sources.
  `user_named` is ignored and has no traceable person setter in the host; never infer consent.
  `FleetNames.Get` supplies publication; see the source table above.
* **Who can set it.** The person, through the existing pipe op `session.rename` (`{ "id", "name" }`,
  empty name clears it). By the pipe's caller table it is allowed only to the app
  (local-pipe.md:102, "Everything") and to the host's own command line
  (local-pipe.md:103); an agent session or any untraced process is refused like other
  acting ops (local-pipe.md:101, 104). #2360 slice 1 owns the Windows app enrollment entry
  point and disclosure/warning on the existing CLI `rename` path; slice 7 consumes it,
  not a second rename implementation. The Mac and iOS apps and the PWA are not app callers
  (local-pipe.md:102) and cannot rename sessions. The owner-approved launch exception accepts
  explicit `fleetName` only for a new host-created session: `SG ` plus one of `Chief of Staff`,
  `Engineering Lead`, `Quality Lead`, `Product Lead`, `Creative Lead`, `Migration Lead`, `SRE Lead`,
  `Reviewer [A-Z]`, or `(Builder|Worker) [0-9]{1,4}` (exact, ASCII). Live local/readable account matches
  are refused; in-flight names stay reserved through late registration. The host logs only name,
  new session id and caller. Agent rename/self-launch refusals remain. Fresh `fleetNames.launchRule`
  in config defaults on when absent; off/invalid/unreadable refuses (App setter: #1832). Names are stored
  beside its other settings, keyed by session id, so it is as strong as the host's state-file
  protection: a same-user process that can write that file can change it (the pipe's agent rail
  is a safety rail, not a boundary, local-pipe.md:161,196). A same-user process can tamper
  with the name store and make a newer process squat a live name after host reload.
  Newest-wins does not authenticate rightful ownership: this lies inside the explicitly
  untrusted same-user metadata boundary, never grants credentials or send authority.
* **Across a resume.** A resumed session gets a new agent credential (local-pipe.md:196-199) and may
  get a new session id. The host carries a fleet name over only when it can link the resumed
  session to the old one by the id it already tracks; otherwise the resumed session has no name
  until a person sets one. The registry never guesses a name.
* **Filter.** The host publishes a name only if, after trimming, it is 1 to 64 characters of
  `A-Za-z0-9 _.+()-` (ASCII only; no control characters, no markup, no `|`). Otherwise the session
  is listed without a name. This keeps markup and control characters out of every client. It is
  not a defence against a sentence of plain words: **a name that passes the filter can still say
  anything in 64 plain characters.**
* **Enrollment disclosure.** Before saving, tell the person: "The relay sees this label.
  Choose a public-safe label, such as a role name. Do not include private task details,
  account names, email addresses, repository names or paths." Recommend fixed public role
  labels; never infer them. The filter excludes path separators, colon and at-sign, but
  cannot prove ordinary words are public-safe. Blank clears the host-owned setting; a
  rejected or failed durable save must not change the published name.
  Slice 1 displays this before the app save and before the CLI invokes rename. It also
  warns on a readable live duplicate: "Another live session uses this label. Lookup may
  select your newer session instead. Choose a different label or rename the other session."
  An unreadable/incomplete duplicate check must say it cannot establish uniqueness, not
  silently claim there is no duplicate. The warning is UX, not tamper-proof protection.
* **Marked as untrusted.** Every client renders a name as plain text, never as markup or a
  template (HA's component must neutralise template markers as HA does today, `HaPayloads.cs:25-31`).
  In the agent surfaces (see "Who may read") the name is not part of the typed result; it is
  returned only on an explicit content request, which taints the reading session under
  agent-principals.md §5.8. So reading a name is
  never "non-tainting".
* **Not an identity.** A name is never a key. Routing uses `node` and `machine` only.

## Names and lookup

The stable key is `node` (`agent_bridge_<16 hex>`, [local-pipe](../protocol/local-pipe.md#agentsend-ap5-lite)),
the id used by `agent.send`.
`id` (16 hex) is the session id. Lookup is a filter over the same list, not a second source of
truth. The host-owned fleet name is the canonical lookup key; routing still uses the
resolved machine/node, never a node ID typed by a person.

```
{ "op": "fleet.find", "data": { "name": "SG Quality Lead", "machine": "<optional device id>" } }
-> { "result": "found",      "fleetScope": "<account id>", "session": { "id", "node", "machine", "state", "since", "model"? } }
-> { "result": "ambiguous",  "fleetScope": "<account id>", "candidates": [ { "id", "node", "machine", "state", "since",
                                               "online" } … ] }
-> { "result": "offline",    "fleetScope": "<account id>", "machines": [ { "machine" } … ] }
-> { "result": "unverified", "fleetScope": "<account id>", "machines": [ { "machine", "online" } … ] }
-> { "result": "incomplete", "fleetScope": "<account id>", "machines": [ "<device id>" … ] }
-> { "result": "notFound",   "fleetScope": "<account id>" }
```

Every `session` and candidate holds typed fields only; **the session name is never echoed back**.
Where a reply includes a machine object, session or candidate, it may also carry `machineName`
as described above. `incomplete` still lists only machine IDs. Display names do not verify a
record or alter any lookup result. Only
verified, live records ever contribute a `node`: `unverified` and `incomplete` answers
list machines and flags, never a node to route to. `model` is omitted when unavailable;
callers requiring model binding must refuse missing/mismatched values, never guess.

**Scope.** With `machine` given, only that machine's records are matched: a same-name session on
another machine does not affect that filtered lookup. Without `machine`, all live machines
in the caller account are matched. The optional machine filter is compatibility, not a
first-pin requirement. Scope comes from the authenticated connection, never request fields.

**Implemented selector precedence.** The following branches exist (`FleetList.cs:185-204`),
but production `Verified` remains false. Verified cases are fixtures/future reader inputs,
not today's live behavior. #2360 changes those branches only after the external reader lands:

1. `incomplete`: any machine that could hold a match (every live machine, or only the one given
   as `machine`) has `truncated: true`. This applies even when a verified match was seen, because a
   duplicate could be hidden.
2. `unverified`: at least one live unverified match, whether or not a verified live match also
   exists.
3. `ambiguous`: two or more verified live matches, including duplicates on the same machine.
4. `offline`: matches exist only on offline or stale machines.
5. `found`: exactly one verified live match in scope, with or without `machine`.
   Offline/stale matches do not create a live duplicate.
6. `notFound`: otherwise.

* **Name-only is not identity proof.** A verified same-account machine can claim another
  session's name while the original is offline. Removing the first pin does not solve
  that limitation. Signature verification remains mandatory; unsigned records never
  yield `found`. Signed name ownership remains separate roster-v2 work.
* **Exact match**, case-sensitive, trimmed. Never fuzzy. A caller that wants a fragment search
  filters `fleet.list` itself.
* **Callers pin.** A caller that is about to act, `agent.send` above all, passes the `machine` and
  `node` it resolved and keeps them; `agent.send` takes the machine and session explicitly
  (local-pipe.md:218-229) and does not take a name.
* Own account only. An unsigned request or another account's is refused with a readable reason.
* Current pipe ops require a connected relay; unavailable or changed connection refuses
  (`FleetList.cs:41-47`). Cached/offline lookup is future RC work, not an offline success today.

### Approved replacement contract (#2360; not implemented)

**This intentionally changes the implemented ambiguous-on-verified-live-duplicates branch,
not today's unsigned live results.** Within the
same authenticated scope and exact, trimmed, case-sensitive name, the newest verified live
process wins only when the reader can prove one strictly newest signed `startedAt`.

| Input case | Today in practice | Current selector with verified fixtures | After the dependent implementation |
|---|---|---|---|
| Two live names, provably separated starts | `unverified` | `ambiguous` | `found` for the strictly newest process |
| Multiple live names, equal/missing/unprovable order or insufficient cross-machine gap | `unverified` | `ambiguous` | Explicit `ambiguous` refusal, no selected session |
| One live name, offline/stale predecessors | `unverified` | `found` | `found`; predecessors never compete |
| Any live unverified match beside verified matches | `unverified` | `unverified` | Unchanged; one legacy unsigned same-name machine blocks rollout selection |
| Live truncated machine in scope | `incomplete` | `incomplete` | Unchanged, before verification/order |
| Offline/stale only, or no match | `offline`, or `notFound` | Same | Same |

`startedAt` is a proposed optional session-entry extension, not in today's relay allow-list.
It is a host-observed process-start instant, bound positively to that session's process,
stable when only the host restarts, and renewed when the session process resumes/replaces.
Unprovable observations are omitted, never invented from transcript/filesystem times.
Never order by state `since`, snapshot `at`, `seq`, epoch, relay arrival or node text.
Valid ordering metadata must pass the shared timestamp contract and signature verification;
every competing live match must have it. A single verified live match needs no tie-break.
Wire form: `YYYY-MM-DDTHH:mm:ssZ`, UTC whole seconds, floored; offsets/fractions/non-calendar
values refuse. Native precision differs
(`src/AgentBridge.Host.Core/Processes/ProcessInspector.cs:11-13,122,190`;
`src/AgentBridge.Local/ProcessChain.cs:162-173`); normalization does not invent missing time.
For different machines, require independently trusted start-time error bounds of at most
60 seconds per host and gap >122 seconds from the newest to every live candidate
(two 60-second errors plus two seconds for flooring).
At/below the gap, without the bound, or with unprovable ordering from clock steps, refuse
`ambiguous`. Snapshot freshness proves no historical accuracy; clock evidence is absent today.
Cross-machine activation is held for that external prerequisite; start time is not exposed to agents.

This changes two distinct live sessions with the same name too: there is no inferred
replacement lineage. A signed host time is still that host's assertion; same-user name-store
tampering or malicious own-host claims are not solved by signing. Names are not exclusive identity.
Equal/missing/unprovable order remains ambiguous, never an arrival-order or lexical pick.
Callers continue pinning the selected machine/node; no retries, rate-limit bypass or paging.

Deploy the strict relay contract and shared vectors before emitting `startedAt`; older
relays refuse unknown members. Legacy records remain readable, but cannot rank duplicates.
The byte-budget/truncation contract above preserves all caps; no silent rollout fallback.

## Who may read, and how a client authenticates

| Reader | How it reads | Needs |
|---|---|---|
| Person device (`app`, `pwa`, `phone`) | `registry-get`, `registry-subscribe` and `registry-changed` over its relay socket | Being admitted to the account (today's pairing) |
| A person's host | Same, so a host can resolve names for `agent.send` | Same, as a `host` device |
| Agent | Pipe ops `fleet.list` and `fleet.find` on its own host | Completed `agent.hello` and read scope (`FleetList.cs:215-227`); otherwise explicit refusal |
| MCP client | Wraps the pipe ops as tools (`list_agent_sessions` gains a fleet mode) | Same as agent |
| HA component or third party | Same frames as a person's device, as a paired non-person client | Pairing from S6 (own design note); read-only; no private path |

An agent never holds a relay socket. Its host performs the connected registry read.
Both pipe fleet ops refuse callers without a completed `agent.hello`, even typed/no-name
requests (`FleetList.cs:215-227`; [local-pipe](../protocol/local-pipe.md#agent-principals-agenthello)).
`include: "content"` is not an exception to that gate. Person devices read relay frames
directly, not by using an unauthenticated fleet pipe op. Rename is a separate App/HostCli
caller-classification path (`LocalApi.cs:34-42`), not agent fleet-read authority.

The HA component is "just a client": it pairs as a non-person read-only client, opts in with
`registry-get` and `registry-subscribe`, the same frames the PWA uses. It gets no extra field and
no extra op. This is what makes HA optional: removing it changes nothing for anyone else. How a
non-person client is paired and revoked is out of scope here (S6) and is the one open dependency
for S8.

**Agent reads and taint.** [agent-principals](agent-principals.md) treats free-text titles as
content that taints a coordinator that reads them, and gives agents typed projections instead
(agent-principals.md §5.8). A session name is free text, so:

* `fleet.find` returns only typed fields (`id`, `node`, `machine`, `state`, `since`, flags). The
  caller supplied the name; it is not read back. This is a typed, non-tainting projection.
* `fleet.list` for an agent returns the same typed fields **without** `name`. Passing
  `include: "content"` adds `name`, is an explicit request, and taints the caller like the other
  content reads. The MCP tool states in its description that names are untrusted text.
* The character filter above does not change this: it removes markup, not instructions.

## Signing

**What is true today.** #1782 supplies protected key custody, not verified registry records.
Until #1776's enrollment, signing and trusted reader dependencies land,
**the relay can forge, alter, hide or replay records, and map any name to any `node`,
without any client noticing.** The only checks that exist apply to messages, not to the registry:
a receiving host accepts `agent.send` only from a pinned own machine whose key matches its device
id (agent-lite.md:11-12, 76), so a forged record can at worst misdirect a message to one of the
sender's own pinned machines. It cannot make a message reach another account or an unpinned
machine. That is the only protection, and it is not enough to rely on a name.

**What a record is worth before #1776 verification.** A display hint, equal to a machine's presence today.
Clients may show it, labelled "unverified". `fleet.find` returns `unverified`, never `found`, and
scripts do not act on it. A person who needs a verified answer uses the end-to-end path (`sessions.list`
over the Noise channel, Option B), which does authenticate the host.

**What changes after #1776** (roster v2, agent-principals.md section 8.10):

* The host signs the canonical bytes of `record` (without `sig`), with its `hostSign` key, under a
  domain-separated registry context; name, id/node, machine, `epoch`, `seq`, `at` and any
  `startedAt` are in the signed bytes.
* The reader uses independently verified, account/generation-pinned roster state:
  `verifier.State.ActiveAdd(machine)` identifies the active host add, whose
  `verifier.Graph[add].Body.Device.HostSign` supplies the key. A key lookup alone is not
  authorization. Check active-add identity/state, key/signature, `seq`/`epoch` order and
  `at` within 60 s before `verified`; altered name/node/start, wrong key and replay must fail.
* Then, and only then, a reader may set `verified: true`, and `fleet.find` may return `found`.
* The signature gives: detection of a forged or altered record, and refusal of a replay older than
  the last `seq` or older than 60 s. It does not stop the relay withholding or delaying a record,
  or showing a reader an old complete snapshot within the 60 s window.

**Is there an interim?** The AP5-lite agent signing key (agent-lite.md:11-12) exists only on hosts
that pinned each other and is not available to the PWA, phones or the apps, so it cannot give those
clients a verified answer; using it for records would also be a new use of that key. This note
does not propose it. #2360 permits no interim key. #1776 remains held behind the person-authorized
v2 transition, enrollment interface #1825 and retained, pinned reader state #1358; custody
alone, observe mode or roster library support is not evidence those gates are deployed.

## What leaks, and the limits

| Item | Before | After | Mitigation |
|---|---|---|---|
| Machine names, last seen | Relay sees | Same | Unchanged |
| Session name | Never | Relay sees (cap 64, optional) | Only person-set names; filter; per-machine off switch (Decision 2); not stored, not logged |
| Session state and since | Never | Relay sees | Coarse enum only |
| Process startedAt (#2360, planned) | Never | Relay sees | Optional process time only; signed before ranking |
| Count of sessions per machine | Never | Relay sees | Capped at 64, `total` when more |
| Prompts, questions, paths, repo, branch, model, first message, folder | Never | Never | Allow-list test on host and relay |
| Storage | Machine blob, 30 s writes | Registry is **not** in the blob | Memory only; lost on relay restart, rebuilt from host heartbeats within 20 s |
| Logs and metrics | No account, device or name | Same | A test greps for a seeded name |

## Per client change

| Client | Change |
|---|---|
| Relay (`AgentBridge.Relay`) | `RosterFields.cs` allow-list and in-memory registry; frames above, paged one machine per frame; same-account fan-out only; host-only put (#1400) |
| Host (`AgentBridge.Host`) | Publishes snapshots (name sources and filter, epoch, seq, truncation); signs through #1776; serves `fleet.list` and `fleet.find` on the pipe (#1412, #1399); adds both to the in-session allow-list (local-pipe.md:101) |
| Windows app | Replace per-host `sessions.list` fan-out for the fleet view with `registry-get`; keep channels for detail. Counts must match the registry. Shows unverified records labelled |
| PWA | `relay.js` handles `registry`/`registry-changed`; `connection.js` unchanged for detail. Mirrors the vectors |
| Apple (Mac, iOS) | Same as PWA in Swift; mirrors the vectors |
| Android | Same when the client exists; adopts the vectors first |
| MCP agent client | `list_agent_sessions` gains the fleet list (typed, no names by default); adds a name-lookup tool over `fleet.find`; stops ignoring `machine` |
| Future HA component (S8) | Pairs as a read-only non-person client; builds entities from `registry`; no private path |
| Fleet scripts | The messenger and reply-session resolve names with `fleet.find`, then `agent.send` by pinned machine and node |

## Build order and slices

Sizes follow the slice template (`.github/ISSUE_TEMPLATE/slice.yml:50-53`): S 51 to 150 changed
lines, M 151 to 250, L 251 to 400; anything over 400 is split. Sizes marked * changed from the
first draft of this note.

| # | Slice | Size | Depends on | Tests |
|---|---|---|---|---|
| 0 | This note (design slice, PR #1422) | S | none | Docs checks |
| 1 | `fleet-registry.json` vectors and the records spec in roster.md (shape, allow-list, epoch and seq, truncation, paging, error codes, signing layout) | M* | 0 | C# writes and replays vectors; PWA and Apple mirror files assert the same bytes; a README table row |
| R | **Roster v2 and `hostSign`** (agent-principals.md §8.10, #391). Not part of this epic's slices; needed for `verified` and for `found` | L, tracked in #391 | none | Its own suite |
| 2a | #1400 part one, relay: allow-list, host-only put, in-memory registry, `registry-get` paged one machine per frame, size and rate refusals | L* | 1 | Allow-list pin (seeded prompt, path and token never appear); other account never sees it; not in blob, log or metrics; unknown member refused; over 16 KiB refused with the socket open; a 64-machine-equivalent get never builds a frame over 24 KiB; stale `seq` and old epoch refused |
| 2b | #1400 part two, relay: `registry-subscribe` and `registry-changed`, same-account fan-out, offline within 60 s, forgotten machines dropped | M* | 2a | Offline within 60 s with the clock faked; fan-out stays in the account; count equals the host's; forgotten machine's record gone |
| 3a | Host publisher: debounce, heartbeat, epoch and seq, truncation with `total`, per-machine off switch | L* | 1 | Debounce and heartbeat timing with a fake clock; restart gives a new epoch that readers accept; 65 sessions give 64 and `truncated`; off switch sends an empty list |
| 3b | Existing host-owned setting/CLI and fixed SG launch rule (`FleetNames.cs:11-54`, `Program.cs:162-181`, `FleetLaunchNames.cs:36-76`); new enrollment UX is #2360 slice 1, not another rename op | L* historical | 3a | Retain source/filter/caller refusals; add disclosure, duplicate warning and squat cases in #2360 slice 1; signing remains #1776 |
| RC | **Reader core** in the host (C#) and the shared test vectors: host as a registry client (`registry-get`, `registry-subscribe`, `removed` handling), 60 s liveness, epoch/seq/at order, 24 h cache and revocation purge, signature and roster-key check once `hostSign` exists | M* | 1, 2b | Fake clock for liveness; old epoch and replayed `seq` refused; restart accepted; a forgotten machine that reconnects with its old epoch and a higher `seq` accepted after `removed`; cache purged on `removed` and on a get that omits the machine; unsigned record never `verified`; shared vectors replay the same in every client; PWA and Apple readers are in slices 6 and 8 |
| 4 | Existing agent-only `fleet.list` (`FleetList.cs:140-154,215-227`); RC cache remains future | L historical | RC, 3a, #1401 | No-hello/read-scope refusals; account isolation; names only as explicit content; audit before read |
| 5 | Existing name-only `fleet.find` (`FleetList.cs:161-204`); #2360 changes only verified duplicate selection after external RC | M* historical | 4, RC | Production live names unverified; current verified fixtures ambiguous; future provably newest found, equal/close/unprovable ambiguous; scope and incomplete/unverified precedence unchanged |
| 6 | PWA adoption, including the PWA reader (JS port of RC) | M | RC, 2b | Vector mirror passes; fleet view from one socket; offline machine shown stale; unverified label |
| 7 | Windows app adoption, including the rename entry point | M | RC, 2b, 3b | Count matches the registry; fleet view works without per-host channels; rename from the app sets a name |
| 8 | Apple adoption, including the Swift reader (port of RC) | M | RC, 2b | Vector mirror passes; same view tests |
| 9 | Fleet messenger and reply-session switch (fleet repo) | S | 5, R | Resolve every name without HA; ambiguity, `unverified` and `incomplete` refused loudly; sends pin machine and node |
| 10 | Conformance cases for the registry in the shared suite (#1407) | S | 2b, RC, 5 | One suite, run against every client and the HA component |
| 11 | 24 h dual run against HA, then S8 | n/a | 2a to 9, RC | At most 5 s lag, zero missing sessions, 100% of person-set names resolved |

Issues to resize when this note is accepted: #1400 splits into 2a and 2b (both L/M, was one L);
#1399 grows from S to M; #1412 stays L; the reader core RC is a new issue. The epic's seven-day
estimate for the host side is unchanged only if `hostSign` (R) is built elsewhere in parallel.

Until R lands, slices 1 to 8 still ship and the fleet view works, labelled "unverified"; `found`
cannot be returned, so slice 9 (the scripts) cannot switch off HA name lookup before R. That makes
roster v2 a real dependency of #1399's acceptance criterion "correct with HA blocked"; see
Decision 4. #2151 removes the caller's machine requirement, not R's verification gate.

### #2360 follow-on slices

Engineering-approved source and tie policy above governs these four workstreams.
Each PR is at most 400 changed lines; workstream 2 is pre-split below.
**No slice enables routing**, establishes `verified`, or replaces #1776. Reader selection
uses only verification established by that independent work. Keep publication display-only
until then.

**External RC prerequisite to slice 3:** #1776 separately delivers canonical
`at/seq/epoch/sig` parsing, enrolled-key/signature checks, freshness/replay, trusted retained
roster/cache/revocation and clock evidence. `FleetList.Parse` does none (`FleetList.cs:50-103`).
RC has its own bounded PRs, not hidden implementation inside the selector slice.

1. **Person enrollment UX.** Use existing person-only `session.rename` for Copilot, Claude
   and Codex, with the relay disclosure above. Tests: explicit save/clear reaches the
   publisher, invalid/failed save does not change publication, agent/script rename refuses;
   seeded `user_named`, titles, summaries, prompts, HA labels, folders and account text are
   never auto-enrolled. App and CLI duplicate warnings are tested. The squat case tampers
   same-user name state, reloads the host and publishes a later same-name process: show the
   acknowledged ownership limit, never new credential/send authority; unsigned still refuses.
2. **Ordering contract/publication, pre-split PRs:** 2a host process binding/publication and
   host-restart test (null start omitted, not invented); 2b relay allow-list/byte-budget
   truncation contract; 2c C#/PWA/Apple shared vectors. Each independently <=400 lines.
   Tests cover flooring, omitted/null observation, resume renewal, legacy records and byte
   limits. Coordinate signed vectors with #1776; no reader-selection activation.
3. **Verified reader selection.** After #1776 verifies signed ordering fields, test newest
   among both-live old/new sessions in both frame orders and across machines. A recent state
   change on the older process cannot win; offline/stale old does not compete.
   Equal/missing/unprovable order or clock steps, cross-machine gaps <=122 seconds and absent trusted
   clock bounds are ambiguous; test skew that would otherwise select the older process.
   Altered/replayed name/start fails verification; unsigned, incomplete and cross-account never yield a routable node.
4. **Guidance and hosted conformance.** Public-safe enrollment guidance and synthetic
   end-to-end host/relay fixtures, including host restart with the session process still
   alive and unchanged `startedAt`. Preserve #2354's additive machine projection, exact
   lookup, content opt-in, authenticated scope and refusal behavior.

Slices 2a and 4 own the required **host-restart acceptance test** for the stability claim:
restart only the disposable host, retain the session process, and assert identical
`startedAt` and unchanged selection despite a new epoch, sequence and snapshot time.
Process replacement must renew; 2a/4 exercise restart, not just a constant vector.
Code needs failing-first hosted assertion-red proof and retained output. No routing/live changes;
fleet wrapper refusal is separate #605. Quality owns gates/arming/merge and exact-head receipts.

Order: 1 first; 2a and 3a in parallel; then 2b, 3b; RC; 4; 5; clients 6 to 8 any order after RC (slice 7 also after 3b); 9
after 5 and R; 10 after 2b, RC and 5. Slices 2a, 2b, 3a, 3b (fleet-name caller policy),
RC, 4 (fleet-read
allow-list, local-pipe.md:101) and 5 touch sensitive paths: both independent receipts and a Trust
review. Android waits on its client; a Linux client follows the same vectors.

## Decisions

1. **Relay-visible names and states** (Owner, Trust). Recommended: accept, for the account's
   own devices only, limited to the allow-list above.
2. **Per-machine off switch** (Owner). Recommended: on by default for own accounts, with a
   host setting (proposed `fleet.share`) that sends an empty list. A machine that is off shows
   as online with no sessions, not as missing.
3. **Names are untrusted display text** (Engineering, Trust). #2360 confirms: a name is published only
   if a person set it on the host on purpose (`session.rename`, app and host CLI only), or a launch-time
   `fleetName` matches the fixed SG list for a new host-created session (agents still cannot self-launch).
   Stored in host state, never read from a transcript (Claude `aiTitle` is
   model-written), `workspace.yaml` (Copilot may fill it from the first prompt), prompt or folder;
   ASCII filter; agents get typed fields from `fleet.find` and `fleet.list`, and names only on
   `include: "content"` after `agent.hello`, which taints. Existing App/HostCli backend permits
   rename; slice 1 owns new app enrollment and CLI disclosure/warning, not a new authority.
4. **Signing and routing** (Engineering). #2360 confirms: records are unverified until #1776's
   roster-v2 enrollment, signing and trusted reader dependencies land; `fleet.find` returns
   `found` only for verified live records. No routing from unverified records and no interim
   key; protected custody alone is insufficient.
5. **Transport shape and limits** (Engineering). Recommended: dedicated `registry-*` frames, opt-in
   by `registry-get` or `registry-subscribe`, one machine per frame, 64 sessions, 16 KiB per
   record, 24 KiB per frame, one get per 2 s. Confirm the numbers.
6. **Non-person client pairing** (Owner, tracked in S6). Needed before S8, not before slices 1 to 9.
7. **Name-only lookup** (#2151, fleet #171/#433). Supersedes the first-machine-pin
   recommendation; a unique verified live name may be found within authenticated account scope.

## What Trust should review

* The allow-list and its pinning test (host and relay both), including that a prompt and a folder
  name can never become a name.
* That the registry is memory-only and absent from the blob, logs and metrics.
* Same-account fan-out, host-only put, and refusal of cross-account requests.
* The name rules, the "untrusted display text" marking, that `fleet.find` never echoes a name, and
  that no agent-written title (Claude `aiTitle`, Copilot `name:`) reaches the relay.
* That current name-only `found` requires one verified live match; #2360's future duplicate
  selection requires all matches verified and one provably newest signed start. Scope,
  incomplete/unverified precedence, no inferred model and name-ownership limits remain.
* That the relay's power is stated honestly: today it can forge, alter, hide and replay records and
  no client can tell; after `hostSign` it can read, withhold and replay within 60 s, not forge.
* That `found` and `agent.send` are never produced or driven by an unverified, stale or offline
  record.

## Not in this note

The messaging cut-over (#728), the audit log (#1401), script credentials (#1402, #1414),
pairing of non-person clients (S6), roster v2 and `hostSign` (#391) and the client protocol as a
whole (#1406, `docs/design/client-protocol.md`). This note must fit that frame: the registry is a
few ordinary frames and two ordinary ops, with the same auth for every caller.
