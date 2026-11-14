# Efficiency baseline: process-only slice (#2294)

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

This bounded slice does **not** close #2294. GC/EventPipe collection, per-endpoint
host network capture and browser/PWA HAR/CDP collectors are follow-up slices.
Those fields are always `null` with reasons, including when tools are installed;
no unsupported metric is inferred. No browser process is mislabeled as the PWA.
Version 1 permits numeric GC rates and a network object with `endpoints` entries:
`endpoint` (role), `bytesInPerMinute`, `bytesOutPerMinute`, `requestsPerMinute`.
Adding collected values is additive within version 1; unknown fields are rejected.
There is no background component or persistent measurement state: it runs wherever
invoked, stops with that invocation, and writes only the requested JSON artifact.

Tests: `dotnet test tests/AgentBridge.Quality.Tests -c Release --filter FullyQualifiedName~EfficiencyBaselineTests`.
The same dependency-free fixture assertions can run directly with
`pwsh quality/efficiency-baseline.Tests.ps1 -Scenario elapsed` (also `exit`,
`replacement`, `readonly`, `no-survivors`, `partial`, `ambiguous`, `metric-values`, `entrypoint`).
Only `entrypoint` uses a real 10 ms window; injected tests never sleep. The AST guard rejects named mutation commands,
`Invoke-Expression` and dynamic call operators; it is a guard, not a proof.
