# Live app roster: RED slice and remaining scope

Assignment: https://github.com/danswett/agent-bridge/issues/2857.
Parent: https://github.com/danswett/agent-bridge/issues/1396.
Baseline: `e6ca350221afe9523742579e34091c79312e46d0`.

The delivered PWA `HostSession` and Windows `MainViewModel` already build their
lists from each host's session store, over `sessions.subscribe` and ordered
`session.upsert`, `session.remove`, `sessions.synced` events. Neither list path
directly reads HA entities. Both deliberately retain removed sessions in an
Ended group. This inspection does not explain the owner's 241-row observation.

The relay holds `SessionRegistry` in memory and serves `registry-get` after
`registry-put`; main has no registry subscription/change event. Its signed
metadata reader is not the app's session-detail store. Do not substitute unsigned
relay rows for authenticated host detail or invent a registry change feed.

This tests-only slice exercises both clients' existing list projections:
host snapshots without an HA client, immediate removal from every list group
(including pinned rows and snapshot reconciliation), and preservation of another
silent host's stale rows. It is not end-to-end relay or HA-off acceptance.

GREEN wiring is deferred until the contracts and implementation in
https://github.com/danswett/agent-bridge/issues/2642 and
https://github.com/danswett/agent-bridge/issues/2643 land. Engineering must confirm
the roster subscription, ordering and stale-host interface before client wiring.
Follow-on cuts: consume that interface in both clients; retire the superseded
Ended-list expectations; then prove HA-blocked multi-host removal and stale-host
behavior in hosted acceptance. No runtime source, host, relay or deployment changes
are included here.

## Focused RED evidence

Windows: four cases compile and run; two pass and two fail because `Ended` still
contains the removed row. PWA: four cases run; two pass and two fail for the same
retained Ended row. No GREEN child or hosted/live acceptance is claimed.

From the repository root (restore uses only the approved internal feed):

```powershell
dotnet restore .\tests\AgentBridge.App.Tests\AgentBridge.App.Tests.csproj --source https://packagefeedproxy.microsoft.io/nuget/v3/index.json -p:NuGetAuditSources=https://packagefeedproxy.microsoft.io/nuget/v3/index.json -m:1 --disable-parallel
dotnet test .\tests\AgentBridge.App.Tests\AgentBridge.App.Tests.csproj --no-restore -m:1 --filter FullyQualifiedName~LiveRosterListTests --logger 'console;verbosity=normal'
node --test --test-concurrency=1 .\pwa\test\unit\live-roster-list.test.mjs
```
