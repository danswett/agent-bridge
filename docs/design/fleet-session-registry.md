# Fleet session registry and name lookup

Status: proposed design note (slice 3a of the Home Assistant retirement epic, #1396). Docs only.
Revised after Engineering and Trust review (PR #1422). Feeds #1400 (roster fields), #1412 (agent
list operation), #1399 (name lookup) and #1406 (one client protocol). It does not touch the
messaging cut-over (#728), which stays first.

All line references are to `main` at 796fbcce1.

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
online. Nothing else. A session with no name set by a person or the session is listed without
one; the prompt text and the folder name are never used as a name.

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

**What is true today, and what is not promised.** No host signing key exists yet. Device keys can
only agree on a secret; they cannot sign (agent-principals.md:122). The host signing key
(`hostSign`) arrives with roster version 2 (agent-principals.md:371-385, section 8.10). Until it
does, **the relay can invent, change, hide or replay any record, and no client can tell.** So
until `hostSign` exists a record is only a display hint, like presence is today: clients may show
it labelled "unverified", but name lookup never returns "found" from it and no message is ever
routed by it. When `hostSign` exists, hosts sign their records, clients verify them, and a forged
or altered record is rejected. Even then the relay can still read the list, hold it back, or show
a copy up to 60 seconds old. The list is kept in relay memory only: never in the stored roster,
never in logs, never in metrics.

**Names are not trusted text.** A session name can be set by the session itself, or by anything
running as the same user on that machine, so it is display text from an untrusted source. It never
carries an instruction to an agent: an agent that looks up a name gets back only typed fields
(session handle, machine, state, time), and sees a name itself only when it asks for names on
purpose, which marks it as having read untrusted content.

**Decisions needed** (details in "Decisions", below):

1. Owner and Trust: accept that session names and coarse states become relay-visible metadata.
2. Owner: whether each machine can switch sharing off, and the default (proposed: on for
   own-account, with a per-machine off switch).
3. Engineering and Trust: names are untrusted display text and agents get them only as marked
   content (proposed), or not at all.
4. Engineering: records stay unverified until `hostSign` (roster v2) exists; nothing may rely on
   them for routing before then. Confirm, and decide whether roster v2 must land before slice 2.
5. Engineering: confirm the build order, the resized slices and the numeric limits.

## What exists today

* The relay roster lists hosts only: device id, name, last seen ([roster](../protocol/roster.md):4, 11,
  41, 50). It says "Nothing about a session is in it" (roster.md:11). The relay admits a device on
  token, public key, kind and name only (`RelayHub.AdmitAsync`, `RelayHub.cs:211-236`; kinds `host`,
  `app`, `pwa`, `phone`, `test`, `RelayHub.cs:14`; person kinds `app`, `pwa`, `phone`,
  `RelayHub.cs:17`), and caps a machine name at 64 characters (`RelayHub.cs:455`).
* An account holds at most 32 devices (`MaxDevicesPerAccount`, `RelayOptions.cs:16`).
* One WebSocket frame, text or binary, may be at most `Wire.MaxFrame` = 1 + 16 + 9 + 65,535 =
  65,561 bytes (`Wire.cs:13-17`, `Noise.cs:34`). A larger frame is not buffered (`Wire.cs:63-73`);
  the relay closes the socket with `frame-too-large` (`RelayHub.cs:245-250`) and the client side
  does the same (`RelayClient.cs:381`). Inbound text frames are also rate limited, 200 a second
  (`RelayOptions.cs:15`, `RelayHub.cs:253-261, 405`).
* Session facts travel only inside the Noise channel between a client and each host
  (`sessions.list`, `session.get`, `sessions.subscribe` in
  [v1-sessions](../protocol/v1-sessions.md)). A client needs a channel to every host.
* A session's `title` today is the Copilot `workspace.yaml` `name`, else the first user message
  (`SessionTracker.cs:304-309`; v1-sessions.md:1725); Claude's is its agent title, else the first
  user message; Codex's is the first user message. HA builds the visible name from the title for
  Copilot and from the working-folder leaf for Claude and Codex (`HaPayloads.cs:45-57`), and
  already neutralises template markup in it because a name once reached a push title that way
  (`HaPayloads.cs:25-31`). Copilot's name is a line in a file under the user's profile
  (`Workspace.cs:34`, `ShadowWitness.cs:155`): the session or any same-user process can change it.
* Agents reach their own host over the local pipe (`agent.hello`, reply scope `read`,
  [local-pipe](../protocol/local-pipe.md):149-166). From inside an agent session the pipe allows
  only a fixed list of read ops (local-pipe.md:101). The MCP `list_agent_sessions` tool covers this
  machine only. Agent-to-agent messages use a roster node id of the form `agent_bridge_` plus 16
  lowercase hex digits (local-pipe.md:185-194) and are accepted only from pinned own machines
  (agent-lite.md:11-12, 76).
* Keys that exist: device keys are P-256 ECDH-only, so the only signed command is a
  designated-verifier MAC (agent-principals.md:122, 376-382). The one signing key in the product
  is the AP5-lite agent signing key, held in a host's own pinned-peer list (`own-peers.json`) and
  used only between pinned hosts, on the machines where `agentMessaging.remote` is on
  (agent-lite.md:11-12, 45-51). A PWA, phone or app has no such list. Host signing (`hostSign`)
  needs roster version 2 and is not built (agent-principals.md:378-385).
* The fleet scripts get names, states and node ids by reading HA sensors.

## Options

**A. Host-published registry through the relay (recommended).** One small list per machine, held
by the relay in memory, delivered to the account's other devices. Gives one cheap read (a client
needs one socket, not a channel to every host), works while a machine's own channel is down, and
meets the 5 s and 60 s targets. Costs: the relay learns names and coarse states, and until
`hostSign` exists the records are unverified.

**B. End to end only.** Every reader opens a channel to every host and calls `sessions.list`.
Nothing new leaks and everything is already authenticated by the channel. Costs: every reader,
including a script or HA component, must hold a Noise identity and reach every host; an offline
machine gives no answer at all; N machines means N channels. That is the path #1396 is moving
away from. Keep it as the fallback for full detail (`session.get`) and as the only way to get a
verified answer before `hostSign` exists.

Recommendation: A for the registry, B unchanged for anything richer than the allow-list.

## The registry record

The host publishes one snapshot of its own sessions. All members are required unless noted.

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
      "state": "waiting", "since": "2026-11-09T08:32:10Z" }
  ],
  "sig": "<base64url>"
}
```

* **Allow-list.** Exactly `v`, `machine`, `epoch`, `seq`, `at`, `truncated`, `total` (only when
  truncated), `sessions` and `sig`, and per session `id`, `node`, `name` (optional), `state`,
  `since`. An unknown member is dropped by the host and refused by the relay. No `cwd`,
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
* **truncated and total.** `truncated: true` when the host has more live sessions than it may
  publish; `total` is then the real count. See "Size and paging".
* **sig.** Optional in `v: 1`. Absent until `hostSign` exists; see "Signing".
* **machine online.** Not in the host's record: the relay adds it from its own socket knowledge
  (see "Freshness and replay").

## Size and paging

The hard limit is 65,561 bytes per WebSocket frame (`Wire.cs:17`); a bigger frame kills the
socket (`RelayHub.cs:245-250`). So no frame may ever be built from "all machines". The limits:

| Limit | Value | Why |
|---|---|---|
| Sessions per machine snapshot | 64 | Worst case below |
| One session entry | at most 230 bytes of JSON (16 + 29 + 64 + 7 + 20 characters plus keys) | 64 x 230 = 14,720 bytes |
| One snapshot (`record`) | 16,384 bytes (16 KiB) | Entries plus about 500 bytes of header and signature fit |
| One relay frame | 24,576 bytes (24 KiB), always one machine | Under 40% of 65,561 |
| Machines per account | 32 (`RelayOptions.cs:16`) | At most 32 machine frames plus one `known` frame |
| `known` frame | 32 entries of about 150 bytes, under 8 KiB | Offline machines, no sessions |
| `registry-get` | one in 2 s per socket; sooner is refused `slow-down` | A get returns up to 33 frames |

* **One machine per frame.** A `registry-get` is answered with one `registry` frame per machine,
  then one `known` frame, each tagged with the request `id` and `part` and `of`. There is no
  aggregate frame, so the account total can reach about 530 KiB over time without any single frame
  coming near the limit.
* **A put over the cap.** The relay checks size before parsing. A `registry-put` frame over
  24 KiB or a record over 16 KiB is refused with `too-large` as a text reply; the socket stays
  open and the old record stays in place. (A frame above 65,561 bytes still closes the socket,
  as today, but a conforming host never builds one.)
* **More than 64 sessions.** The host publishes the 64 with the most recent state change, sets
  `truncated: true` and `total` to the real count (both signed once signing exists). Nothing is
  silently dropped: see "Names and lookup" for how `fleet.find` reports an incomplete list.
  Hitting the cap is logged on the host as a count, never a name.

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
relay -> {"t":"registry","id":3,"part":3,"of":3,"known":[{"machine":"…","name":"…","lastSeen":"…"}]}
relay -> {"t":"registry-changed","machine":"…","online":true,"record":{…}}   one machine, on change
```

* **Opt-in, not a capability flag.** The relay `hello` carries only token, key, kind and name
  (`RelayHub.cs:216-225`), so there is no hello capability to announce. Instead a client opts in by
  sending `registry-get` or `registry-subscribe`; a client that never does is never sent a registry
  frame, so old clients are unaffected. (An earlier draft called this `fleet.registry` in `hello`;
  that does not exist.)
* The relay validates shape, allow-list and size, keeps the newest accepted record per machine in
  memory, and forwards. It does not interpret names or states.
* `known` carries offline machines with last-seen time and **no sessions** (see "Offline").

## Freshness and replay (5 s and 60 s)

* A host sends a new snapshot when any listed field changes, debounced to at most one per 2 s,
  plus a heartbeat every 20 s even if nothing changed. A heartbeat is a new snapshot: `seq` rises
  and `at` is the current time. The relay pushes each accepted change at once. Expected lag:
  under 3 s, with a hard target of 5 s.
* The relay marks a machine offline the moment its host socket closes, and sets `online: false`
  after at most 60 s without a heartbeat for half-open sockets. The row then shows offline within
  1 minute.
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
  `forgotten` list, roster.md:157) the relay drops its record at once and a reader deletes its
  cache for it and refuses any later record from that machine id.

## Offline machines and the cache

An offline machine appears in the list (name, `online: false`, last seen) so a reader can say
"machine is off". Its sessions are not served as live: the relay drops them from memory when the
host goes offline. A host that reconnects sends a full snapshot first.

A reader may keep its last copy of a machine's record as a **cache**, with a maximum age of 24 h
(then deleted). Cached records are always marked `stale: true` and `online: false` in `fleet.list`
and are never returned as live by `fleet.find`. The cache exists for two reasons only: showing
"this session was on machine X, last seen at …", and the name-shadow check in "Names and lookup".

## Names

A session name is **display text from an untrusted source**. This section says where one comes
from, who can set one, and how it is marked.

* **Where a name comes from.** A host publishes a name only from a name that was set on the
  session: Copilot's `workspace.yaml` `name` (`Workspace.cs:34`, `SessionTracker.cs:306`) and
  Claude's agent title (`SessionTracker.cs:307`). It never publishes the first user message, a
  prompt, a summary, a branch, or the working-folder leaf. Today's fallback to the first user
  message (`SessionTracker.cs:306-308`, v1-sessions.md:1725) and the folder-leaf name for Claude
  and Codex (`HaPayloads.cs:50-54`) are for the host's own use and are not copied into the
  registry. A Codex session has no settable name yet, so it is listed without one until it has.
* **Who can set it.** The person (by renaming the session), the session itself (it can write its
  own `name:` line), and any other process of the same user on that machine. That is the same
  trust as HA's sensor names today. The registry does not make a name more trustworthy; it only
  stops prompt text and folder names from becoming names.
* **Filter.** The host publishes a name only if, after trimming, it is 1 to 64 characters of
  `A-Za-z0-9 _.+()-` (ASCII only; no control characters, no markup, no `|`). Otherwise the session
  is listed without a name. This keeps markup and control characters out of every client. It is
  not a defence against a sentence of plain words: **a name that passes the filter can still say
  anything in 64 plain characters.**
* **Marked as untrusted.** Every client renders a name as plain text, never as markup or a
  template (HA's component must neutralise template markers as HA does today, `HaPayloads.cs:25-31`).
  In the agent surfaces (see "Who may read") the name is not part of the typed result; it is
  returned only on an explicit content request, which taints the reading session under
  agent-principals.md §5.8 (agent-principals.md:258-263, 333-335). So reading a name is
  never "non-tainting".
* **Not an identity.** A name is never a key. Routing uses `node` and `machine` only.

## Names and lookup

The stable key is `node` (`agent_bridge_<16 hex>`, local-pipe.md:194), the id used by `agent.send`.
`id` (16 hex) is the session id. Lookup is a filter over the same list, not a second source of
truth.

```
{ "op": "fleet.find", "data": { "name": "SG Quality Lead", "machine": "<optional device id>" } }
-> { "result": "found",      "session": { "id", "node", "machine", "state", "since" } }
-> { "result": "ambiguous",  "candidates": [ { "id", "node", "machine", "state", "since",
                                               "online", "verified" } … ] }
-> { "result": "offline",    "machines": [ { "machine", "lastSeen" } … ] }
-> { "result": "unverified", "candidates": [ { "id", "node", "machine", "state", "since" } … ] }
-> { "result": "incomplete", "machines": [ "<device id>" … ] }
-> { "result": "notFound" }
```

Every `session` and candidate holds typed fields only; **the name is never echoed back**.

* **`found`** only when exactly one record matches, that record is **verified** (valid signature,
  see "Signing"), **live** (within 60 s, machine online) and no other record matches, including
  records in the reader's 24 h cache for an offline or stale machine. Otherwise it is one of the
  other results. `found` is never produced from an unverified, stale or offline record.
* **`ambiguous`.** Two or more live matches, or one live match plus a cached match on a machine
  that is offline or stale (the usual way a name is spoofed: the real host is off, so the impostor
  is the only match; the offline original is still listed, flagged `online: false`). The caller
  picks by `node` and machine; it is never silently the first.
* **`offline`.** The name is known only on machines that are offline or stale. Never `found`.
* **`unverified`.** The only matches are unverified records (every record, until `hostSign`). They
  may be shown to a person labelled unverified. **`agent.send` and any script that acts on the
  answer must not use them.**
* **`incomplete`.** Some live machine has `truncated: true`, so the list may hide a match or a
  duplicate, and there is no match to return. Also returned instead of `found` when a truncated
  machine is present, because a duplicate could be hidden. The caller re-asks with `machine` set,
  or reads that machine end to end (`sessions.list`).
* **Exact match**, case-sensitive, trimmed. Never fuzzy. A caller that wants a fragment search
  filters `fleet.list` itself.
* **Callers pin.** A caller that is about to act, `agent.send` above all, passes the `machine` and
  `node` it resolved and keeps them; `agent.send` takes the machine and session explicitly
  (local-pipe.md:185-194) and does not take a name. Names are for finding a node once; they are
  not claimed per machine, because without `hostSign` there is nothing to enforce a claim with,
  and a person can pin a machine today. Per-machine name claims are deferred until roster v2.
* Own account only. An unsigned request or another account's is refused with a readable reason.
* Who runs it: the same op on every client. On the local pipe it is served by the host from its
  registry snapshot, so an agent gets an answer when the relay is unreachable (stale-marked, so
  `offline` or `unverified`, never `found`).

## Who may read, and how a client authenticates

| Reader | How it reads | Needs |
|---|---|---|
| Person device (`app`, `pwa`, `phone`) | `registry-get`, `registry-subscribe` and `registry-changed` over its relay socket | Being admitted to the account (today's pairing) |
| A person's host | Same, so a host can resolve names for `agent.send` | Same, as a `host` device |
| Agent | Pipe ops `fleet.list` and `fleet.find` on its own host | An `abat1_` agent credential whose scope is `read` (local-pipe.md:166); refused with a readable reason otherwise |
| MCP client | Wraps the pipe ops as tools (`list_agent_sessions` gains a fleet mode) | Same as agent |
| HA component or third party | Same frames as a person's device, as a paired non-person client | Pairing from S6 (own design note); read-only; no private path |

An agent never holds a relay socket. Its host answers from the registry it already holds. The two
ops are **added to the in-session allow-list** in local-pipe.md:101 (today that list is `hello`,
`sessions.list`, `session.get`, `sessions.subscribe`, `usage.*`, `launch.options`, `host.status`,
`host.subscribe`), under the `sessions.read` capability that `hello` already offers there; any
other caller on the pipe is refused them as it is refused other acting ops.

The HA component is "just a client": it pairs as a non-person read-only client, opts in with
`registry-get` and `registry-subscribe`, the same frames the PWA uses. It gets no extra field and
no extra op. This is what makes HA optional: removing it changes nothing for anyone else. How a
non-person client is paired and revoked is out of scope here (S6) and is the one open dependency
for S8.

**Agent reads and taint.** [agent-principals](agent-principals.md) treats free-text titles as
content that taints a coordinator that reads them, and gives agents typed projections instead
(agent-principals.md:258-263, 333-335). A session name is free text, so:

* `fleet.find` returns only typed fields (`id`, `node`, `machine`, `state`, `since`, flags). The
  caller supplied the name; it is not read back. This is a typed, non-tainting projection.
* `fleet.list` for an agent returns the same typed fields **without** `name`. Passing
  `include: "content"` adds `name`, is an explicit request, and taints the caller like the other
  content reads. The MCP tool states in its description that names are untrusted text.
* The character filter above does not change this: it removes markup, not instructions.

## Signing

**What is true today.** There is no host signing key. Without one a record cannot be verified by
anyone, and **the relay can forge, alter, hide or replay records, and map any name to any `node`,
without any client noticing.** The only checks that exist apply to messages, not to the registry:
a receiving host accepts `agent.send` only from a pinned own machine whose key matches its device
id (agent-lite.md:11-12, 76), so a forged record can at worst misdirect a message to one of the
sender's own pinned machines. It cannot make a message reach another account or an unpinned
machine. That is the only protection, and it is not enough to rely on a name.

**What a record is worth before `hostSign`.** A display hint, equal to a machine's presence today.
Clients may show it, labelled "unverified". `fleet.find` returns `unverified`, never `found`, and
scripts do not act on it. A person who needs a verified answer uses the end-to-end path (`sessions.list`
over the Noise channel, Option B), which does authenticate the host.

**What changes when `hostSign` exists** (roster v2, agent-principals.md:378-385; tracked in #391):

* The host signs the canonical bytes of `record` (without `sig`), with its `hostSign` key, under a
  fixed context string; `epoch`, `seq` and `at` are in the signed bytes.
* A reader checks: the key is the one in the roster `add` entry for that machine id, `seq`/`epoch`
  order, and `at` within 60 s. A record that fails is dropped and never shown as live.
* Then, and only then, a reader may set `verified: true`, and `fleet.find` may return `found`.
* The signature gives: detection of a forged or altered record, and refusal of a replay older than
  the last `seq` or older than 60 s. It does not stop the relay withholding or delaying a record,
  or showing a reader an old complete snapshot within the 60 s window.

**Is there an interim?** The AP5-lite agent signing key (agent-lite.md:11-12) exists only on hosts
that pinned each other and is not available to the PWA, phones or the apps, so it cannot give those
clients a verified answer; using it for records would also be a new use of that key. This note
does not propose it. If Engineering wants a host-only verified answer sooner, that is a separate
decision (Decision 4); the default here is to wait for roster v2.

## What leaks, and the limits

| Item | Before | After | Mitigation |
|---|---|---|---|
| Machine names, last seen | Relay sees | Same | Unchanged |
| Session name | Never | Relay sees (cap 64, optional) | Only set names; filter; per-machine off switch (Decision 2); not stored, not logged |
| Session state and since | Never | Relay sees | Coarse enum only |
| Count of sessions per machine | Never | Relay sees | Capped at 64, `total` when more |
| Prompts, questions, paths, repo, branch, model, first message, folder | Never | Never | Allow-list test on host and relay |
| Storage | Machine blob, 30 s writes | Registry is **not** in the blob | Memory only; lost on relay restart, rebuilt from host heartbeats within 20 s |
| Logs and metrics | No account, device or name | Same | A test greps for a seeded name |

## Per client change

| Client | Change |
|---|---|
| Relay (`AgentBridge.Relay`) | `RosterFields.cs` allow-list and in-memory registry; frames above, paged one machine per frame; same-account fan-out only; host-only put (#1400) |
| Host (`AgentBridge.Host`) | Publishes snapshots (name sources and filter, epoch, seq, truncation); signs once `hostSign` exists; serves `fleet.list` and `fleet.find` on the pipe (#1412, #1399); adds both to the in-session allow-list (local-pipe.md:101) |
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
| 0 | This note (3a) | S | none | Docs checks |
| 1 | `fleet-registry.json` vectors and the records spec in roster.md (shape, allow-list, epoch and seq, truncation, paging, error codes, signing layout) | M* | 0 | C# writes and replays vectors; PWA and Apple mirror files assert the same bytes; a README table row |
| R | **Roster v2 and `hostSign`** (agent-principals.md §8.10, #391). Not part of this epic's slices; needed for `verified` and for `found` | L, tracked in #391 | none | Its own suite |
| 2a | #1400 part one, relay: allow-list, host-only put, in-memory registry, `registry-get` paged one machine per frame, size and rate refusals | L* | 1 | Allow-list pin (seeded prompt, path and token never appear); other account never sees it; not in blob, log or metrics; unknown member refused; over 16 KiB refused with the socket open; a 64-machine-equivalent get never builds a frame over 24 KiB; stale `seq` and old epoch refused |
| 2b | #1400 part two, relay: `registry-subscribe` and `registry-changed`, same-account fan-out, offline within 60 s, forgotten machines dropped | M* | 2a | Offline within 60 s with the clock faked; fan-out stays in the account; count equals the host's; forgotten machine's record gone |
| 3a | Host publisher: debounce, heartbeat, epoch and seq, truncation with `total`, per-machine off switch | L* | 1 | Debounce and heartbeat timing with a fake clock; restart gives a new epoch that readers accept; 65 sessions give 64 and `truncated`; off switch sends an empty list |
| 3b | Host name source and filter (set names only; never prompt or folder), signing hook that stays off until `hostSign` | M* | 3a | Name filter table; a seeded prompt and a seeded folder name never appear; Codex listed without a name; records carry no `sig` until `hostSign`, and verify against the vectors once it exists |
| 4 | #1412 agent op `fleet.list` on the pipe, scope check, in-session allow-list entry, MCP tool (after the #1401 audit log) | L | 2a, 3a, #1401 | No read scope refused with a readable reason; only own account; allow-list pinned; no `name` unless `include: "content"`, which taints; audited with the agent principal; works from the cached snapshot (marked stale) |
| 5 | #1399 `fleet.find`: all six results, cache of 24 h, shadow check, `machine` filter | M* | 4 | Found only from one verified live match; a stale or offline match is never found; an impostor beside an offline original gives ambiguous; unverified records give `unverified`; truncated gives `incomplete`; duplicate gives ambiguous; unsigned and other-account refused; the name is never echoed; a script checks every current `SG <role>` name on all four machines with HA blocked |
| 6 | PWA adoption | M | 2a, 2b | Vector mirror passes; fleet view from one socket; offline machine shown stale; unverified label |
| 7 | Windows app adoption | M | 2a, 2b | Count matches the registry; fleet view works without per-host channels |
| 8 | Apple adoption | M | 2a, 2b | Vector mirror passes; same view tests |
| 9 | Fleet messenger and reply-session switch (fleet repo) | S | 5, R | Resolve every name without HA; ambiguity, `unverified` and `incomplete` refused loudly; sends pin machine and node |
| 10 | Conformance cases for the registry in the shared suite (#1407) | S | 2b, 5 | One suite, run against every client and the HA component |
| 11 | 24 h dual run against HA, then S8 | n/a | 2a to 9 | At most 5 s lag, zero missing sessions, 100% of live names resolved |

Issues to resize when this note is accepted: #1400 splits into 2a and 2b (both L/M, was one L);
#1399 grows from S to M; #1412 stays L. The epic's seven-day estimate for 3b is unchanged only if
`hostSign` (R) is built elsewhere in parallel.

Until R lands, slices 1 to 8 still ship and the fleet view works, labelled "unverified"; `found`
cannot be returned, so slice 9 (the scripts) cannot switch off HA name lookup before R. That makes
roster v2 a real dependency of #1399's acceptance criterion "correct with HA blocked"; see
Decision 4.

Order: 1 first; 2a and 3a in parallel; then 2b, 3b, 4; 5; clients 6 to 8 any order after 2b; 9 and
10 after 5 and R. Slices 2a, 2b, 3a, 3b, 4 and 5 touch sensitive paths: both independent receipts
and a Trust review. Android waits on its client; a Linux client follows the same vectors.

## Decisions

1. **Relay-visible names and states** (Owner, Trust). Recommended: accept, for the account's
   own devices only, limited to the allow-list above.
2. **Per-machine off switch** (Owner). Recommended: on by default for own accounts, with a
   host setting (proposed `fleet.share`) that sends an empty list. A machine that is off shows
   as online with no sessions, not as missing.
3. **Names are untrusted display text** (Engineering, Trust). Recommended: published only from a
   set name, never prompt or folder; ASCII filter; agents get typed fields from `fleet.find` and
   `fleet.list`, and names only on `include: "content"`, which taints. Alternative: never give
   agents names at all.
4. **Signing and routing** (Engineering). Recommended: records are unverified until `hostSign`
   (roster v2, #391) exists; `fleet.find` returns `found` only for verified live records; no
   routing from unverified records; no interim key. Decide whether roster v2 is scheduled before
   slice 2 ships or in parallel, and whether Engineering wants a host-only interim.
5. **Transport shape and limits** (Engineering). Recommended: dedicated `registry-*` frames, opt-in
   by `registry-get` or `registry-subscribe`, one machine per frame, 64 sessions, 16 KiB per
   record, 24 KiB per frame, one get per 2 s. Confirm the numbers.
6. **Non-person client pairing** (Owner, tracked in S6). Needed before S8, not before slices 1 to 9.

## What Trust should review

* The allow-list and its pinning test (host and relay both), including that a prompt and a folder
  name can never become a name.
* That the registry is memory-only and absent from the blob, logs and metrics.
* Same-account fan-out, host-only put, and refusal of cross-account requests.
* The name rules, the "untrusted display text" marking, and that `fleet.find` never echoes a name.
* That the relay's power is stated honestly: today it can forge, alter, hide and replay records and
  no client can tell; after `hostSign` it can read, withhold and replay within 60 s, not forge.
* That `found` and `agent.send` are never produced or driven by an unverified, stale or offline
  record.

## Not in this note

The messaging cut-over (#728), the audit log (#1401), script credentials (#1402, #1414),
pairing of non-person clients (S6), roster v2 and `hostSign` (#391) and the client protocol as a
whole (#1406, `docs/design/client-protocol.md`). This note must fit that frame: the registry is a
few ordinary frames and two ordinary ops, with the same auth for every caller.
