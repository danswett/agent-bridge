# Efficiency baseline: processes and PWA (#2294, #2487)

Run in PowerShell 7.4+ on a VM or laptop; no administrator rights, debugger,
process writes, restarts or installed tools are needed:

```powershell
pwsh quality/efficiency-baseline.ps1 -DurationSeconds 30 -SampleIntervalSeconds 2 -OutputPath out/baseline.json
```

The host (`agent-bridge-host`) and desktop (`AgentBridge`) are discovered by process
name. Override with `-HostProcessId` / `-DesktopProcessId`; ambiguous, missing or
inaccessible targets report `unavailable.process` with an explicit-ID hint, while
other targets continue. `out/` is already ignored. Results can contain machine
names and executable versions: review before publishing; do not commit captures.

Schema: [efficiency-baseline.schema.json](efficiency-baseline.schema.json), version 1.
The result contains machine, OS, start/end UTC, monotonic elapsed seconds, sampling
round count, and `targets.host`, `targets.desktop`, `targets.pwa`. Each target has
status, process ID, executable product/build version, successful sample count,
CPU seconds per minute, final working-set/private bytes, top ten thread CPU deltas,
GC allocation bytes/second, network metrics and a map of `unavailable` reasons.
CPU rates divide endpoint CPU deltas by measured monotonic time, not sleep counts.
Threads must exist with the same ID **and start time** at both endpoints; short-lived
threads are excluded, so thread totals need not equal process CPU. Any process
read failure or identity change invalidates the interval rather than reporting zero.
An inaccessible executable version or thread counters does not invalidate CPU.

GC/EventPipe collection and per-endpoint host/desktop network capture remain out of scope.
Those process fields are always `null` with reasons, including when tools are installed;
no unsupported metric is inferred. No browser process is mislabeled as the PWA.
Version 1 permits numeric GC rates and a network object with `endpoints` entries:
`endpoint` (role), `bytesInPerMinute`, `bytesOutPerMinute`, `requestsPerMinute`.
Adding collected values is additive within version 1; unknown fields are rejected.
There is no background component or persistent measurement state: it runs wherever
invoked, stops with that invocation, and writes only the requested JSON artifact.

For an already-running Chromium PWA with CDP enabled, supply its **page** WebSocket
endpoint using `-PwaCdpWebSocket`. The tool never enables remote debugging, discovers
other tabs, navigates, launches or closes an app. Without an endpoint, or if attachment
fails, the PWA is null/unavailable with a reason; host and desktop collection continue.
The page's `Performance.getMetrics` supplies renderer main-thread `TaskDuration`
(CPU seconds/minute) and final `jsHeapUsedBytes`, not whole-browser CPU or OS memory.
`Network` events supply one `browser-aggregate` endpoint: encoded completed HTTP
response bytes, exposed upload body bytes, request counts and WebSocket payload
bytes/frame counts, normalized to measured minutes. Outbound headers, WebSocket framing
and browser keepalive traffic are excluded; these are not wire-byte totals or per-role attribution.
An unexposed upload or failed request leaves network null with a reason. No URLs,
headers or payloads are written to the artifact. Attach only to the intended PWA.
CDP observation adds overhead; it is not the uninstrumented process baseline.
The collector runs in the invoking process on any PowerShell 7.4+ machine, including
hosted CI against an existing debug page. Its counters live only in memory. If the
node leaves, collection ends; there is no daemon, shared state or failover obligation.

Tests: `dotnet test tests/AgentBridge.Quality.Tests -c Release --filter FullyQualifiedName~EfficiencyBaselineTests`.
The same dependency-free fixture assertions can run directly with
`pwsh quality/efficiency-baseline.Tests.ps1 -Scenario elapsed` (also `exit`,
`replacement`, `readonly`, `no-survivors`, `partial`, `ambiguous`, `metric-values`, `entrypoint`,
`pwa-metrics`, `pwa-missing`, `pwa-exit`, `pwa-network`, `pwa-cdp`).
Only `entrypoint` uses a real 10 ms window; injected tests never sleep. The AST guard rejects named mutation commands,
`Invoke-Expression` and dynamic call operators; it is a guard, not a proof.
