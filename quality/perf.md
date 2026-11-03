# Performance: perf-hook and bench-micro

Two suites record how fast the hot paths are (quality/README.md §3.2). They record and gate
nothing, since hosted runners vary too much for a threshold to mean anything. A run fails only
when something is broken: a benchmark throws, or the hook answers wrongly. `perf.yml` runs both
on every push to `main` and on demand (Actions, Perf, Run workflow), on `ubuntu-latest` and
`windows-latest`, and keeps the results as the run's `perf-<os>` artifact for 90 days.

## perf-hook: the hook every agent waits on

`tests/AgentBridge.PerfHook` starts a published `agent-bridge-host-hook` as an agent does: the
event goes in on stdin, and the reply comes back on stdout. It does this many times, over four
scenarios taken in turn, so a moment of noise on the machine falls on all of them alike:

| Scenario | What it is |
|---|---|
| warm, host up | The same copy again and again, with a fresh heartbeat, so each event is spooled |
| warm, host down | No heartbeat, so each run gives only the fixed reply |
| cold, host up / down | Each run is the first start of a fresh copy, as after an install or update |

The event is a Copilot `permission` request (391 bytes), the hook Copilot runs before every tool
call, so it is the one agents wait on most. Each run must exit 0 with the reply `{}` and must
spool exactly when the host is up. If not, the run stops, because timing the wrong path is worse
than timing nothing.

It is a console tool rather than an opt-in test. It writes numbers and asserts none, it takes
the run count and the output path as arguments, and it has to be pointed at a published NativeAOT
build. `dotnet test` passes over it (`IsTestProject` false).

Nothing real is touched. Each copy of the hook runs from a folder the tool makes under `--work`
(the temp folder by default), next to a pointer to a runtime folder of its own, as
`HookProcessTests` does. The tool deletes the folder when it finishes. The host is stood in for by its heartbeat, which
is all the hook reads of it. For "up" the tool writes the heartbeat before each run and empties
the spool after it, as the intake would. For "down" there is no heartbeat.

"Cold" means a new executable, not a cold disk: the OS's file cache still holds the bytes just
copied. On a Windows machine with real-time scanning, most of the cold cost is the scan of the
new file the first time it runs: about 140 ms locally when the baseline was taken, against
about 25 ms on a hosted runner.

Run it locally. On Windows, NativeAOT's linker needs `vswhere` on `PATH`:

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/AgentBridge.Hook -c Release -r win-x64 -o $env:TEMP\perf\hook
dotnet run -c Release --project tests/AgentBridge.PerfHook -- --hook $env:TEMP\perf\hook\agent-bridge-host-hook.exe --out $env:TEMP\perf\perf-hook.json
```

The options are `--runs` (warm runs per scenario, default 50), `--cold-runs` (default 20),
`--warmup` (runs discarded first, default 3) and `--work` (where the copies go). It prints a
Markdown table and writes JSON:

* `replyMs` is the time from starting the process to the end of its output, which is when the
  agent has its answer.
* `exitMs` is the time to the process having exited.
* Each has `p50`, `p95` and `max`, by nearest rank, so every number is one that was measured.
* Both include the cost of starting a process, which the agent pays too.

## bench-micro: BenchmarkDotNet over the hot paths

`tests/AgentBridge.Benchmarks` is in `AgentBridge.slnx`, so it is built with everything else,
but `dotnet test` passes over it. The fixtures are generated, the same bytes every run, and
sized from design.md §5.9.

An upsert in its list form is 2,700 bytes typically (`Typical`), 7,300 at p99 (`P99`), and
`Oversized` is everything at its limit. `Oversized` is 65,607 bytes before `SessionBudget.Fit`
and 31,884 after it, inside the 40 KiB budget.

| Class | Times |
|---|---|
| `NoiseHandshakeBenchmarks` | A whole XX handshake, both sides, as a channel opens |
| `NoiseTransportBenchmarks` | One transport message encrypted and one decrypted, at each upsert size |
| `ProtocolBenchmarks` | A `session.upsert` serialized as the host sends it, and parsed as a client reads it |
| `UpsertBuildBenchmarks` | `SessionSource.Commit`: the get and list forms fitted to the budget, and the JSON compared |
| `SessionTrackerBenchmarks` | One session's share of a discovery pass when its transcript has not changed |
| `TranscriptFirstReadBenchmarks` | The first read of a 21 MB Copilot transcript: the last 4 MiB parsed, the rest skimmed |
| `TranscriptAppendBenchmarks` | One 12 KB turn appended, then read and parsed |
| `SessionOrderBenchmarks` | The app arranging 300 sessions over three machines, in each sort |
| `MarkdownBenchmarks` | A reply rendered to the app's Markdown model, at 1.5 KB and at the 16 KiB cut |

Transcript tailing and upsert building are internal to `AgentBridge.Host.Core`. They are reached
through an `InternalsVisibleTo` granted to this assembly alone.

The transcript and tracker benchmarks open a real file each time, as the host does. Real-time
scanning makes those opens far dearer on a developer's Windows machine than on a hosted runner.
In the first baselines, `SessionTrackerBenchmarks.Unchanged` took 331 µs locally and 49 µs on
`windows-latest`, so compare those two only against a baseline from the same kind of machine.

To run it locally (Release only; BenchmarkDotNet refuses a Debug build), from the repository's
root, where BenchmarkDotNet looks for the project it builds each benchmark from:

```powershell
dotnet run -c Release --project tests/AgentBridge.Benchmarks -- --filter '*' --summary $env:TEMP\perf\bench-micro.json
```

BenchmarkDotNet restores the projects it generates, too: a machine that restores through a
mirror rather than nuget.org sets `$env:RestoreSources` to the mirror's index for the run.

`--filter '*Noise*'` runs one class, and `--job short` gives a quick, rough look. Every other
BenchmarkDotNet option works too. BenchmarkDotNet's own reports go to
`BenchmarkDotNet.Artifacts/` (or `--artifacts <folder>`). `--summary` also writes the small JSON
a baseline is kept as. For each benchmark it records the mean, median, p95 and standard deviation
in nanoseconds per operation, and the bytes allocated per operation. It also records the fixture
sizes, so a change to the fixtures is not mistaken for a change in speed.

## Baselines

The baselines are `quality/baselines/<suite>/<platform>.json`, named as `catalog.yml` names
platforms (`win-x64`, `linux-x64`). `environment.runner` in each file says where it was measured
(`local` or `github-actions`). The first `win-x64` files are from a local run on a developer's
Windows machine, and the first `linux-x64` files are from the first Perf run on `ubuntu-latest`.

Compare like with like: a local run against a local baseline, a hosted run against a hosted one,
on the same platform. Even then, read anything under about 10% (hook p95 under about 20%) as
noise, unless a rerun shows the same.

A baseline changes only by pull request, and the pull request says why. If a change makes
something slower on purpose, it updates the baseline itself, with the reason in its description.
If the code is unchanged and the numbers have drifted (new runner images, a new .NET), the
pull request says that, and replaces the file with the new run's JSON. That JSON comes from
`--out` or `--summary` locally, or from a Perf run's artifact.

Ring 3 is to compare on a quiet lab machine, failing at more than 10% slower than the rolling
baseline when a rerun confirms it (quality/README.md §3.2). Until that machine exists, these
files are the reference a person reads a run against.
