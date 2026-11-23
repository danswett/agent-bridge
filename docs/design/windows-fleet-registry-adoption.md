# Windows app: fleet view from the session registry (slice 7)

**Design only. No code until S-N1 is accepted.** Part of #1396 (HA retirement) and #2462; slice 7 of
[fleet-session-registry.md](fleet-session-registry.md). No protocol change, no new field, and no change to who may read what:
the Windows app is a person device and already may send `registry-get`, `registry-subscribe` and read `registry-changed`
([roster.md](../protocol/roster.md#session-registry-each-machines-sessions)).

## Today

* `Fleet` (`src/AgentBridge.App.Core/Machines.cs`) builds one `Machine` per host in relay presence. Each `Machine` owns a
  `HostConnection` with its own channel and its own `sessions.subscribe`, so the fleet view is N channels deep.
* A machine's session count is computed from that machine's own session store (`MachineRows.cs`). If a channel is not open there is
  no count. An offline machine keeps its rows for `Fleet.OfflineKeep` (15 minutes) and is then listed from the relay's `known` list.
* `RelayClient` (`src/AgentBridge.Transport`) has `RegistryGetAsync` and handles `registry` frames, including the `slow-down` refusal.
  Nothing in `AgentBridge.App` or `App.Core` calls it. It has no `registry-subscribe` and does not handle `registry-changed`.

## Change

1. **Relay client.** `RelayClient` sends `registry-subscribe` and then `registry-get` once per socket (the get's reply proves the
   subscription is in place), reads `registry-changed`, and hands each frame to a reader. It resyncs with `registry-get` after any
   reconnect, as the relay closes a subscriber that stops reading (1013).
2. **One reader, not a second port.** The host's `FleetRegistryReader` (slice RC) holds the order rule (same epoch: `seq` must not
   fall, equal accepted; a new epoch needs a later `at`), the 60 s live window, offline-stale, purge on `removed` and on a get that
   no longer lists a machine, and the 24 h cache. The app must not re-implement it. The reader moves, unchanged, into a project both
   the app and the host already reference (`AgentBridge.Host.Core` is the candidate; the code PR confirms). The slice 10 vector
   (`docs/protocol/vectors/fleet-registry-reader.json`) is replayed against it for the Windows client, which turns the four registry
   conformance rows from Missing to a recorded result.
3. **Rows.** A machine row's count comes from the registry when no channel of its own is open. Precedence: the channel's live count,
   then a live registry copy, then a stale registry copy. This is the rule the PWA uses (`fleet.js`), so the two clients agree.
   A row from a registry copy is labelled **unverified** (and **stale** when past the 60 s window) until hostSign (#1776) lands.
   The label is text, not colour alone.
4. **Channels stay for detail.** Opening a machine, answering a question, stopping or launching still use that machine's channel.
   What goes away is only the need to hold a channel to every host just to show a count. A fleet view with no per-host channels
   works, which is the slice's acceptance bar.
5. **Counts match.** The fleet summary ("N machines, M online") and each row's session count come from the same reader snapshot, so the
   count equals the registry's, and equals what the PWA shows for the same machine.

## What does not change

* Enrollment and rename. S-N1 owns the Windows entry point for naming a session (disclosure before, warning after). This slice adds no
  second rename path and sets nothing as verified; it consumes S-N1's names as the registry reports them.
* Offline handling by the roster (`known`, Forget, the 15 minute keep) stays as it is. The registry adds a stale copy where a machine
  just dropped; it does not change when a machine is forgotten.
* Own-account exception (Nov 20) stays temporary. Nothing here depends on it or makes it permanent.
* `AgentBridge.HomeAssistant` is untouched. HA stays an optional client (S8); the slice 11 dual run stays the gate before S7.

## Tests (named now, written first in the code PR)

* `tests/AgentBridge.App.Tests`: `FleetTests` gain cases for a machine with no open channel shown from a live registry copy, a stale
  copy, and none; the channel's count wins when open; the label text for unverified and stale; the fleet view with every channel
  closed. `FakeHost`/the fake relay gain `registry` and `registry-changed` frames.
* `tests/AgentBridge.Transport.Tests`: the client sends subscribe before get, applies changes in order, and resyncs after reconnect;
  a `slow-down` reply is retried after its interval, never in a loop.
* The shared vector replay for the Windows client, and the moved reader's own tests unchanged.
* `AgentBridge.exe --self-test` (`SelfTest*.cs`): one machine-tab row from a registry copy, labelled.

## Size and order

Two code PRs after S-N1: (a) relay client plus the reader move (about 250 lines), (b) rows and labels in `Fleet`/`MachineRows`
(about 250 lines). Each at most 400 changed lines against its parent, tests first, sensitive (reads the relay roster on a person
device), read by Quality with a second model. If the reader move would exceed that, the move goes first on its own.

## Settled: a stale copy may stand in for a count

When a machine's channel has failed, a stale registry copy may stand in for its count, labelled **stale** and **unverified**. This is not a
new decision: it follows the rule the PWA already ships (`pwa/js/fleet.js`, slice 6), so the two clients agree. Ruled by the Engineering
Lead (R9, Nov 23, 2026); no owner step.
