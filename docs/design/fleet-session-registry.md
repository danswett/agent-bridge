# Fleet session registry and name lookup

Status: proposed design note (slice 3a of the Home Assistant retirement epic, #1396). Docs only.
Feeds #1400 (roster fields), #1412 (agent list operation), #1399 (name lookup) and #1406 (one
client protocol). It does not touch the messaging cut-over (#728), which stays first.

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

**What the relay will see (new).** For each session: a stable session handle, its name (cut to
64 characters), a coarse state (`working`, `waiting`, `idle`, `ending`), the time it entered that
state, which machine it is on, and whether that machine is online. Nothing else.

**What it did not see before.** Today the relay sees machine names and last-seen times, never a
session. Session names and states have only ever travelled inside the end-to-end channel. So
this is a real new disclosure: a name such as `SG Quality Lead` and "waiting for 40 minutes"
become visible to the relay operator. It will never see prompt text, question text, paths,
repository, branch, model, transcripts or tokens, and a test pins that.

**Who can read it.** Only devices and hosts of the same account (person devices, and a person's
own hosts). An agent never connects to the relay: it asks its own host, on a credential with the
read scope, and gets only its own account's sessions. A client that is not a person (an HA
component, a third party) must be paired as a normal client first and gets the same read-only
view; its pairing is a separate design (S6). Another account's sessions never appear.

**What is not promised.** The relay can still read the list, hold it back, or replay an old copy.
The host signs each list so a client can see tampering and reject stale copies, but cannot stop
the relay from withholding it. The list is kept in relay memory only: never in the stored roster,
never in logs, never in metrics.

**Decisions needed** (details in "Decisions", below):

1. Owner and Trust: accept that session names and coarse states become relay-visible metadata.
2. Owner: whether each machine can switch sharing off, and the default (proposed: on for
   own-account, with a per-machine off switch).
3. Engineering and Trust: whether a session name counts as content for agents (it can carry
   instructions). Proposed: restrict names to a plain character set so agents may read them as
   typed data.
4. Engineering: the signature scheme to use before the signed device roster (#55) is live.
5. Engineering: confirm the build order and slice sizes.

## What exists today

* The relay roster lists hosts only: device id, name, last seen ([roster](../protocol/roster.md)).
  It says "Nothing about a session is in it."
* Session facts travel only inside the Noise channel between a client and each host
  (`sessions.list`, `session.get`, `sessions.subscribe` in
  [v1-sessions](../protocol/v1-sessions.md)). A client needs a channel to every host.
* Agents reach their own host over the local pipe (`agent.hello`, scope `read`,
  [local-pipe](../protocol/local-pipe.md)). The MCP `list_agent_sessions` tool covers this
  machine only. Agent-to-agent messages use a roster node id of the form `agent_bridge_<16 hex>`
  ([agent-lite](../protocol/agent-lite.md)).
* The fleet scripts get names, states and node ids by reading HA sensors.

## Options

**A. Host-signed registry through the relay (recommended).** One small list per machine, held by
the relay in memory, delivered to the account's other devices. Gives one cheap read (a client
needs one socket, not a channel to every host), works while a machine's own channel is down, and
meets the 5 s and 60 s targets. Costs: the relay learns names and coarse states.

**B. End to end only.** Every reader opens a channel to every host and calls `sessions.list`.
Nothing new leaks. Costs: every reader, including a script or HA component, must hold a Noise
identity and reach every host; an offline machine gives no answer at all; N machines means N
channels. That is the path #1396 is moving away from. Keep it as the fallback for full detail
(`session.get`).

Recommendation: A for the registry, B unchanged for anything richer than the allow-list.

## The registry record

The host publishes one snapshot of its own sessions. All members are required unless noted.

```json
{
  "v": 1,
  "machine": "<device id>",
  "seq": 18204,
  "at": "2026-11-09T09:12:44Z",
  "sessions": [
    { "id": "<16 hex>", "node": "agent_bridge_<16 hex>", "name": "SG Quality Lead",
      "state": "waiting", "since": "2026-11-09T08:32:10Z" }
  ],
  "sig": "<base64url>"
}
```

* **Allow-list.** Exactly `id`, `node`, `name`, `state`, `since` per session. An unknown member
  is dropped by the host and refused by the relay. No `cwd`, `repository`, `branch`, `model`,
  `question`, `title` text beyond `name`, summaries, counts of tokens, or anything from a prompt.
* **name.** The session's display name, control characters removed, cut to 64 characters
  (same cap as a machine name). Duplicates are allowed (see "Names").
* **state.** One of `working`, `waiting`, `idle`, `ending`, the v1 session status. `since` is when
  the session entered that state (v1 `statusSince`). "Time in state" is `now - since`, computed by
  the reader, so the record does not change every second.
* **machine online.** Not in the host's record: the relay adds it from its own socket knowledge
  (see "Freshness").
* **Size.** At most 64 sessions per machine and 32 KiB per snapshot (the relay already caps 64
  machines per account). Over the cap is `too-many` and the host sends the first 64 by most
  recent change, plus `truncated: true`.
* **Capability.** Clients and hosts announce `fleet.registry` (v1) in `hello`, as they do for
  `sessions.read`. A peer without it is never sent registry frames. This is the one place a
  client declares it understands the registry.

## Relay frames

New text frames, from an admitted device of any kind, counted against the frame rate. The relay
only ever answers a socket from the same account.

```
host  -> {"t":"registry-put","record":{…}}                       full snapshot of that host
any   -> {"t":"registry-get","id":3}
relay -> {"t":"registry","id":3,"machines":[{"machine":"…","name":"…","online":true,
                                                "seq":18204,"at":"…","record":{…}}],"known":[…]}
relay -> {"t":"registry-changed","machine":"…","online":true,"record":{…}}   pushed on change
```

* The relay validates shape and allow-list, keeps the newest `seq` per machine in memory, and
  forwards. It does not interpret names or states.
* A device may only `registry-put` for its own machine id. A person device (`app`, `pwa`,
  `phone`) cannot put.
* `known` carries offline machines with last-seen time and **no sessions** (see "Offline").

## Freshness and lag (5 s and 60 s)

* A host sends a new snapshot when any listed field changes, debounced to at most one per 2 s,
  plus a heartbeat every 20 s even if nothing changed. The relay pushes each accepted change at
  once. Expected lag: under 3 s, with a hard target of 5 s.
* The relay marks a machine offline the moment its host socket closes, and sets
  `online: false` after at most 60 s without a heartbeat for half-open sockets. The row then
  shows offline within 1 minute.
* Every reader treats a record older than 60 s as stale and shows its sessions as "last seen
  at …", not live.

## Offline machines

An offline machine appears in the list (name, `online: false`, last seen) so a reader can say
"machine is off". Its sessions are not served as live: the relay drops them from memory when the
host goes offline, and the reader may keep showing its own last copy marked stale. A host that
reconnects sends a full snapshot first.

## Names and duplicates

* The name is for people and scripts; the stable key is `node` (`agent_bridge_<16 hex>`),
  the id used by `agent.send`. `id` (16 hex) is the session id.
* **Lookup** is a filter over the same list, not a second source of truth:

```
{ "op": "fleet.find", "data": { "name": "SG Quality Lead" } }
-> { "result": "found",     "session": { "id", "node", "machine", "state", "since" } }
-> { "result": "ambiguous", "candidates": [ { "id", "node", "machine", "state", "since" } … ] }
-> { "result": "notFound" }
```

* Exact match on the name, case-sensitive, trimmed. Never a fuzzy match, never silently the first
  of several. Two sessions with the same name on one or two machines return `ambiguous` with all
  candidates, so the caller picks by `node` or machine. A caller that wants a fragment search
  filters `fleet.list` itself.
* Own account only. An unsigned request or another account's is refused with a readable reason.
* Who runs it: the same op on every client. On the local pipe it is served by the host from its
  registry snapshot, so an agent gets an answer when the relay is unreachable (stale-marked).

## Who may read, and how a client authenticates

| Reader | How it reads | Needs |
|---|---|---|
| Person device (`app`, `pwa`, `phone`) | `registry-get` and `registry-changed` over its relay socket | Being admitted to the account (today's pairing) and `fleet.registry` |
| A person's host | Same, so a host can resolve names for `agent.send` | Same, as a `host` device |
| Agent | Pipe ops `fleet.list` and `fleet.find` on its own host | `abat1_` credential with scope `sessions.read`; refused with a readable reason otherwise |
| MCP client | Wraps the pipe ops as tools (`list_agent_sessions` gains a fleet mode) | Same as agent |
| HA component or third party | Same frames as a person's device, as a paired non-person client | Pairing from S6 (own design note); read-only; no private path |

An agent never holds a relay socket. Its host answers from the registry it already holds.
The HA component is "just a client": it pairs as a non-person read-only client, announces
`fleet.registry`, and uses `registry-get` and `registry-changed`, the same frames the PWA uses.
It gets no extra field and no extra op. This is what makes HA optional: removing it changes
nothing for anyone else. How a non-person client is paired and revoked is out of scope here
(S6) and is the one open dependency for S8.

**Agent reads and taint.** [agent-principals](agent-principals.md) treats free-text titles as
content that taints a coordinator that reads them, and gives agents typed projections instead.
A session name is free text. To keep the registry usable by agents, a host publishes a `name`
only if it matches `[A-Za-z0-9 _.+()-]{1,64}` after trimming; otherwise it publishes an empty
name and the lookup cannot find it. Names that pass are treated as typed data and do not taint;
this is Decision 3 and needs Trust sign-off.

## Signing

* The host signs the canonical bytes of `record` (without `sig`) with its host signing key, with
  `seq` increasing and `at` in the signed bytes. Reader checks: key belongs to the machine's
  device id, `seq` is greater than the last seen from that machine, `at` is within 5 minutes of
  its clock.
* Until the signed device roster (#55, [devices](../protocol/devices.md)) is live, readers
  verify against the host key pinned at pairing (the same pinned own-peers model as AP5-lite),
  and a reader that has no key for a machine shows the record marked unverified.
* What the signature gives: a reader detects a forged or altered record, and rejects a replay
  older than the last `seq`. What it does not give: it cannot stop the relay withholding a
  record, delaying it, or showing a reader an old complete snapshot, within the 60 s stale rule.

## What leaks, and the limits

| Item | Before | After | Mitigation |
|---|---|---|---|
| Machine names, last seen | Relay sees | Same | Unchanged |
| Session name | Never | Relay sees (cap 64) | Allow-list; per-machine off switch (Decision 2); not stored, not logged |
| Session state and since | Never | Relay sees | Coarse enum only |
| Count of sessions per machine | Never | Relay sees | Capped at 64 |
| Prompts, questions, paths, repo, branch, model | Never | Never | Allow-list test on host and relay |
| Storage | Machine blob, 30 s writes | Registry is **not** in the blob | Memory only; lost on relay restart, rebuilt from host heartbeats within 20 s |
| Logs and metrics | No account, device or name | Same | A test greps for a seeded name |

## Per client change

| Client | Change |
|---|---|
| Relay (`AgentBridge.Relay`) | `RosterFields.cs` allow-list and in-memory registry; frames above; same-account fan-out only (#1400) |
| Host (`AgentBridge.Host`) | Publishes snapshots; signs; serves `fleet.list` and `fleet.find` on the pipe (#1412, #1399) |
| Windows app | Replace per-host `sessions.list` fan-out for the fleet view with `registry-get`; keep channels for detail. Counts must match the registry |
| PWA | `relay.js` handles `registry`/`registry-changed`; `connection.js` unchanged for detail. Mirrors the vectors |
| Apple (Mac, iOS) | Same as PWA in Swift; mirrors the vectors |
| Android | Same when the client exists; adopts the vectors first |
| MCP agent client | `list_agent_sessions` gains the fleet list; adds a name-lookup tool over `fleet.find`; stops ignoring `machine` |
| Future HA component (S8) | Pairs as a read-only non-person client; builds entities from `registry`; no private path |
| Fleet scripts | The messenger and reply-session resolve names with `fleet.find` instead of HA sensors; then `agent.send` by node |

## Build order and slices

Sizes: S under 150 changed lines, M 150 to 400, L over 400 (must be split).

| # | Slice | Size | Depends on | Tests |
|---|---|---|---|---|
| 0 | This note (3a) | S | none | Docs checks |
| 1 | `fleet-registry.json` vectors and the records spec in roster.md (shape, allow-list, signing, error codes) | S | 0 | C# writes and replays vectors; PWA and Apple mirror files assert the same bytes; a README table row |
| 2 | #1400 relay: allow-list, in-memory registry, frames, same-account fan-out, offline within 60 s | M | 1 | Allow-list pin (seeded prompt, path and token never appear); other account never sees it; not in blob, log or metrics; unknown member refused; stale `seq` refused; offline within 60 s with the clock faked; count equals the host's |
| 3 | Host publisher: debounce, heartbeat, name filter, signing, per-machine off switch | M | 1 | Debounce and heartbeat timing with a fake clock; name filter table; off switch sends an empty list; signature verifies against the vectors |
| 4 | #1412 agent op `fleet.list` on the pipe, scope check, MCP tool (after the #1401 audit log) | M | 2, 3, #1401 | No read scope refused with a readable reason; only own account; allow-list pinned; audited with the agent principal; works from the cached snapshot |
| 5 | #1399 `fleet.find` | S | 4 | Found; not found; duplicate returns ambiguous; unsigned and other-account refused; a script checks every current `SG <role>` name on all four machines with HA blocked |
| 6 | PWA adoption | M | 2 | Vector mirror passes; fleet view from one socket; offline machine shown stale |
| 7 | Windows app adoption | M | 2 | Count matches the registry; fleet view works without per-host channels |
| 8 | Apple adoption | M | 2 | Vector mirror passes; same view tests |
| 9 | Fleet messenger and reply-session switch (fleet repo) | S | 5 | Resolve every name without HA; ambiguity refused loudly |
| 10 | Conformance cases for the registry in the shared suite (#1407) | S | 2, 5 | One suite, run against every client and the HA component |
| 11 | 24 h dual run against HA, then S8 | n/a | 2 to 9 | At most 5 s lag, zero missing sessions, 100% of live names resolved |

Order: 1 first; 2 and 3 in parallel; then 4, 5; clients 6 to 8 any order after 2; 9 and 10
after 5. Slices 2 and 4 touch sensitive paths: both independent receipts and a Trust review.
Slice 3 also touches what a host reveals, so it gets the same treatment. Android waits on its
client; a Linux client follows the same vectors.

## Decisions

1. **Relay-visible names and states** (Owner, Trust). Recommended: accept, for the account's
   own devices only, limited to the allow-list above.
2. **Per-machine off switch** (Owner). Recommended: on by default for own accounts, with a
   host setting (proposed `fleet.share`) that sends an empty list. A machine that is off shows
   as online with no sessions, not as missing.
3. **Names as typed data for agents** (Engineering, Trust). Recommended: restrict to the plain
   character set above; names outside it are not published.
4. **Interim signature** (Engineering). Recommended: pinned host key, upgraded to the signed
   roster (#55) when it is live. Confirm the key and its rotation.
5. **Transport shape** (Engineering). Recommended: dedicated `registry-*` frames, not an
   overload of `presence`, so old clients keep working untouched. Confirm.
6. **Non-person client pairing** (Owner, tracked in S6). Needed before S8, not before slices 1 to 9.

## What Trust should review

* The allow-list and its pinning test (host and relay both).
* That the registry is memory-only and absent from the blob, logs and metrics.
* Same-account fan-out and refusal of cross-account requests.
* The name filter and the no-taint claim for agents.
* That the relay's power is stated honestly: it can read, withhold and replay within the stale
  window; it cannot forge a record.

## Not in this note

The messaging cut-over (#728), the audit log (#1401), script credentials (#1402, #1414),
pairing of non-person clients (S6) and the client protocol as a whole (#1406,
`docs/design/client-protocol.md`). This note must fit that frame: the registry is two ordinary
frames and two ordinary ops, announced by one capability, with the same auth for every caller.
