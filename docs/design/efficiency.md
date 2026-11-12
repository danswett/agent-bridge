# Efficiency: idle near zero, small memory, quiet network (#2293)

Engineering design for the efficiency epic. Design only: no code changes here. Evidence so far is
#2283 (the host spinning a core in `KeepAwakeService.WaitAsync`) and #2284 (the desktop app at
about 1.9% of the machine while idle). Related: #597 (reply speed), #2225 (updates without polling),
#1248 (CI cost), [design.md §4.7 and §5.9](../design.md#59-load-model-measured-2026-10-27)
(bandwidth and load) and [quality/perf.md](../../quality/perf.md) (perf-hook and bench-micro).

## 1. Goals and non-goals

**The owner's rule:** every app (the host, the desktop app and the PWA) uses a tiny amount of
memory, sends tiny payloads with no chatter, and uses almost no CPU unless active work is
happening, while the experience stays elegant and polished.

Goals:
- Idle means near zero: no spinning, no polling loops, no timer that fires when nothing can change.
- Each component has a written budget, a measured baseline, and a gate that fails when the budget
  is broken.
- Active work costs only what the work needs, and stops when the work ends.

Non-goals:
- Making active work faster. That is #597.
- Relay scale and egress cost. That is [relay-scale-out.md](relay-scale-out.md) and §5.9; deltas and
  focus are already committed there and are not re-decided here.
- Visible changes without Creative's review. Anything a person can see (refresh rates, relative
  times, animation) is flagged in this document and goes to Creative before it ships.
- Machine-specific fixes. A fix must be right on every machine, not tuned to one.

## 2. Baseline method

### 2.1 What idle means

| Component | Idle state | Variants |
|---|---|---|
| Host | Running, signed in, connected to the relay, at least 3 minutes after start. No session is working: no transcript writes, no hook events, no question or delivery pending, no launch, no update downloading, no app or PWA command in flight | 0 tracked sessions; 20 idle tracked sessions. Keep-awake on and off. HA connector off (on as a separate variant) |
| Desktop app | Running against an idle host, no input, the same fixture of sessions, nothing working | **Hidden**: closed to the tray, process alive. **Visible**: window shown, sessions page, not focused by input |
| PWA | Signed in, the same fixture, nothing working | **Visible**: tab in front, sessions list shown. **Hidden**: tab in the background for at least 15 seconds (after `QUIET_AFTER_MS`) |

"Idle" is a state of the product, not of the machine. Live fleet updates from other machines can
still arrive on a real fleet; the lab fixture has none, so its idle is strict.

### 2.2 Machines

- **Reference laptop class:** Windows 11 x64 laptop, hybrid cores, 16 or more logical CPUs,
  balanced power plan, measured on AC and on battery. This is the class that showed #2283 at 98% of
  one core when VMs did not, so a VM-only number is never enough.
- **VM class:** the lab's Windows x64 VM ([quality/lab/README.md](../../quality/lab/README.md)), and
  Linux x64 for the host. This is where gates run.
- Later, the same method on an Apple silicon Mac for the macOS app and on an iPhone for the PWA
  (manual, ring 4).

No machine names, account names or network details go into results.

### 2.3 What is measured, and how

Everything is on the **real clock**. Fake clocks prove logic, but a fake clock that moves only when
the test advances it cannot show a spin like #2283, which only appeared because real time moves
between two reads. A stepping fake clock, which moves forward a little on every read, can; §5's
per-PR timer count uses one. It does not replace the real clock: CPU, network and memory are gated
on the real clock on every pull request that reaches the host, app, PWA or protocol (§5), and
again, more precisely, every night.

Two forms of the procedure in §2.4 use these measures. The **full form** (3 minute warm-up, 5
minute windows, repeated) is for the baseline, the nightly gate and the laptop. The **per-PR
short form** fits a pull request: 60 s of warm-up that is not counted, then 3 samples of 30 s, and
the median of the 3, with the control process measured in the same window. Its memory phase then
runs 10 short activity cycles and samples memory at rest after each (§5.1). It is coarser, so it is
judged with a noise margin (§5), never against the bare budget.

| Measure | How | Notes |
|---|---|---|
| CPU seconds per minute | Process CPU time at the start and end of the window (Windows `Process.TotalProcessorTime`, Linux `/proc/<pid>/stat`, macOS `proc_pid_rusage`), divided by wall minutes | 0.6 s/min is 1% of one core. Also report percent of one core and of the machine |
| Thread attribution | Per-thread CPU time sampled every 10 s, so threads that start or end inside the window are counted; top 5 threads with names | Never measure CPU while tracing: #2284 showed EventPipe itself cost 2.7 s in 2 minutes. Stacks are a separate run, only when over budget |
| Wakeups per second | Windows: `\Thread(<process>/*)\Context Switches/sec` summed; Linux: voluntary and involuntary context switches from `/proc/<pid>/task/*/status`; PWA: timer and animation-frame callbacks counted by an init script | Also count timers created per second (in-process meter, slices 3a and 3b), the measure that would have caught #2283 directly |
| Working set and private bytes | Sampled every 10 s. **Startup**: the highest value in the first 60 s. **Steady**: the median of the last 5 minutes | PWA: JS heap used (CDP `Performance.getMetrics`) |
| GC allocation rate | `GC.GetTotalAllocatedBytes` and GC counts from the in-process meter, read at start and end | Not `dotnet-counters`: it attaches EventPipe and perturbs CPU |
| Network | Bytes in and out, messages and requests per minute, **per endpoint role** (relay socket, release relay, GitHub, usage APIs, peers). Host and app: the in-process meter (slice 3c). PWA: Playwright's request and WebSocket frame events | Endpoints are named by role, never by URL or address. Payload content is never recorded. WebSocket keepalive frames are counted apart from messages (§3) |
| Battery (laptop only) | One hour on battery, SRUM energy estimate per app (`powercfg /srumutil`) | Manual, recorded but not gated |

### 2.4 Procedure

1. Quiet check: machine-wide CPU under 3% for 30 s before starting; otherwise wait, up to 5 tries.
2. Start the component (or attach to a running one), then **warm up 3 minutes**. Startup memory is
   taken during the first 60 s.
3. **Measure 5 minutes.** Record machine-wide CPU alongside; a run where the rest of the machine
   used more than 5% of it is discarded and repeated.
4. **Repeat 3 times** (5 for a gate verdict) and report the median, with min and max.
5. Measure a **control process** in the same window: an empty .NET console awaiting forever on the
   same runtime (for the PWA, a blank page). Report the component's numbers and the control's, so
   runner noise can be read off.
6. The 24-hour memory reading uses the same sampling, one sample a minute, with the 20-session
   fixture and a scripted burst of activity every hour.

### 2.5 Recording

Each run writes one JSON file, schema `agent-bridge.efficiency/1`:

```json
{
  "schema": "agent-bridge.efficiency/1",
  "component": "host | app | pwa",
  "version": "0.1.<run>", "commit": "<sha>",
  "machine": { "class": "laptop | vm | mac | phone", "platform": "win-x64", "logicalCpus": 20,
               "power": "ac | battery", "clock": "real" },
  "scenario": { "name": "idle-20", "window": "hidden | visible | none", "sessions": 20,
                "features": { "keepAwake": false, "homeAssistant": false } },
  "warmupSeconds": 180, "windowSeconds": 300,
  "runs": [ {
    "startedAt": "<UTC>",
    "cpuSecondsPerMinute": 0.21, "machineCpuPercent": 1.4, "wakeupsPerSecond": 1.8,
    "timersCreatedPerSecond": 0.3,
    "workingSetMb": { "startupMax": 0, "steadyMedian": 0 },
    "privateMb": { "startupMax": 0, "steadyMedian": 0 },
    "allocKbPerSecond": 3.1, "gcPerMinute": 0.2,
    "threads": [ { "name": "<thread name or role>", "cpuSeconds": 0.1 } ],
    "net": [ { "endpoint": "relay-socket", "bytesOut": 0, "bytesIn": 0, "messages": 3 } ]
  } ],
  "control": { "cpuSecondsPerMinute": 0.01, "wakeupsPerSecond": 0.5 },
  "median": { "cpuSecondsPerMinute": 0.21 },
  "budget": "<budget file version>", "verdict": "pass | fail | record"
}
```

Results are **artifacts, not committed**: CI keeps them as `efficiency-<component>-<platform>` for
90 days, and a local run writes under the user's temp folder. What is committed is the budget file
(slice 11a) and a summary row per release in the weekly quality report. Times shown to people are
Pacific; the file stores UTC.

## 3. Budget proposal

Every number below is a **PROPOSAL** until the baseline (slice 4) lands and the owner approves it.

| Measure | Host idle | App hidden | App visible | PWA visible | PWA hidden |
|---|---|---|---|---|---|
| CPU s/min (median) | 0.3 (0.5% of a core) | 0.1 | 0.5 | 0.3 main-thread busy | 0 after rest |
| Wakeups per second | 4 | 2 | 10 | 1 timer callback | 0 after rest |
| Startup memory | 120 MB working set | 200 MB working set | same | 30 MB JS heap | same |
| Steady memory | 80 MB private | 120 MB private, 60 MB working set | 160 MB private | 20 MB JS heap at 50 sessions | not higher than visible |
| 24 h growth | 5% | 5% | 5% | 5% over a 2 h soak | n/a |
| Growth over 10 PR cycles (§5.1) | 3 MB private | 3 MB private | 5 MB private | 2 MB JS heap | n/a |
| Allocation | 50 KB/s | 10 KB/s | 50 KB/s | n/a | n/a |
| Idle network | 1 KB/min, 4 messages/min, 6 HTTP requests/hour | 0.5 KB/min, 3 messages/min | same as hidden | 2 KB/min, 5 frames/min | 0 bytes, 0 requests |
| Keepalive frames | 6 a minute each way today; 3 after slice 10 | none (no relay socket) | none | 3 pings in and 3 pongs out a minute, answered by the browser | none (socket closed) |

Rationale:
- The one clean number is #2284's unprofiled capture: 0.312 CPU s/min with the window visible (an
  older build, on a VM). The bad cases are #2283's 59 CPU s/min on the laptop and about 10 CPU s/min
  on VMs. The app-visible budget of 0.5 leaves that clean case about 60% headroom rather than
  failing it by a few hundredths; the baseline (slice 4) replaces it. Budgets of 0.1 to 0.5 sit
  about 20 to 600 times below the bad cases, so a spin can never pass a gate, even with noise.
- Hidden is stricter than visible: a hidden window has nothing to draw. A hidden PWA, after its
  rest (§2.1), has nothing to do at all, so its budget is zero. A zero budget is judged by exact
  counts (no timer, frame or request), with CPU held only to a noise floor (§5.1).
- Network: messages count application messages only. Keepalive is its own row: both ends of the
  relay socket ping every 20 s and each ping is answered, so an idle host today sends 6 control
  frames a minute (3 pings, 3 pongs) and receives 6; one side pinging (H10, slice 10) halves that.
  Keepalive bytes do count toward the KB/min budget. The PWA's frame count is data frames: the
  browser answers the relay's pings itself and the page cannot see them. Beyond keepalive, anything
  sent while idle is chatter. Six HTTP requests an hour covers update and usage checks at their
  current periods.
- Memory: there is no measurement yet. These are typical for a .NET service and a WinUI 3 app and
  will be replaced by the baseline plus 25% headroom. The per-PR cycle growth is small on purpose:
  10 cycles of the same work should leave memory where it was, so anything that keeps a few MB per
  cycle is a leak, not noise.

**Ratchet rule.** Budgets only tighten. An improvement is measured against the median of the 7
nightly runs before the change. When a change makes the nightly median at least 20% better than
that for three nightly runs in a row, the budget becomes the smaller of the current budget and the
new median times 1.25, by a pull request. So the ratchet can never raise a budget: one far above the
new median drops to it plus 25%, and one already close stays. A budget is raised only by a pull
request that names the cause, with the owner's approval. A regression that is accepted on purpose
is the same: the pull request says why.

## 4. Design rules for engineers

1. **No real-clock retry loops.** Never loop on "read the clock, make a timer, read again". Make one
   delay to the due time, await it, then read the clock and decide (the #2283 fix).
2. **Await to a due time,** not a fixed tick. If the next thing is due in 9 minutes, sleep 9
   minutes. A tick that wakes to find nothing to do is a wakeup wasted.
3. **No timers when a feature is off.** A disabled feature (keep-awake, the HA connector, usage
   probes) starts no task and no timer. "Off but still looking" needs an event, not a loop.
4. **Events over polling.** File watchers, process-exit events, channel messages and
   `hello.changed` first; a slow safety pass (minutes, not seconds) only where an event can be lost.
5. **Coalesce and back off.** Bursts collapse into one pass. Retries back off with jitter and stop.
   Every wait honours cancellation, and every loop has a test that it ends promptly on stop.
6. **Push, not poll.** Across machines, the side that knows about a change sends it (as #2225 does
   for releases). A client never asks every minute "anything new?".
7. **Small payloads.** Send deltas, not snapshots; compress large bodies; never resend what the
   other side has. Each message type has a size budget (§5).
8. **Visibility first.** The PWA and the desktop app pause everything a person cannot see: no
   timers, no fetches, no subscriptions for a hidden view. On return, catch up once.
9. **Refresh at the rate the eye needs.** A relative time like "5 min ago" changes once a minute,
   so it is redrawn when it changes, not every second. Animations run only during active work, on
   the compositor, and stop under reduced motion. **Creative reviews any change a person can see.**
10. **Measure before and after.** A change that adds a timer or a poll says, in its pull request,
    how often it fires while idle and why.

## 5. Gates in CI

#2293 requires that an idle CPU or idle network regression fails the pull request, measured on the
real clock. Real-clock numbers on hosted runners are noisy ([quality/perf.md](../../quality/perf.md)),
and every runner minute is shared (#1248). So each pull request gets a short real-clock gate that is
judged with a stated margin, beside exact counted checks, and the precise and slow gates run nightly
and before a release:

| Gate | Where and when | Clock | Fails when | Cost |
|---|---|---|---|---|
| Idle timer count | Ring 1, per PR, in the existing test jobs when host, app or PWA code changes | Stepping fake clock, counted | An idle host or app composition creates or fires more timers per simulated hour than the budget file allows; a hidden PWA fires any timer or sends any frame after its rest (its budget is zero, §3) | Seconds, no new job |
| Payload size | Ring 1, per PR, in the protocol job | n/a | A protocol vector for a message type is over its byte budget | Seconds |
| Real-clock idle gate | Ring 1, per PR, a job in `ci.yml` on Linux (host, PWA) and Windows (host, app), on pull requests whose change reaches the paths below | Real | CPU: the median, less the control, is over the spin ceiling (at once), or over the budget plus its margin on the run and on one rerun. Network: idle messages, bytes or requests over budget plus tolerance on the run and on one rerun; a hidden PWA counts any timer, frame or request after its rest; any message observed is over its type's byte budget. Memory: growth over 10 activity cycles is over 10 times its ceiling (at once), or over its ceiling on the run and on one rerun; steady memory is over budget plus margin | About 15 runner minutes for a host-only PR, up to about 21 when app and PWA paths change too; about 9 to 12 minutes of wall clock |
| Idle budget | Ring 3, nightly on the lab VM | Real | The median of 5 full-form runs is over budget (no margin) and a rerun of 3 agrees | Inside the nightly window |
| Memory soak | Ring 3 nightly (2 h) and weekly (24 h) on the lab VM: the long-horizon check behind the per-PR memory verdict | Real | Steady memory over budget, or growth over the budget's rate | Lab time only |
| Laptop check | Ring 4, before promotion to stable | Real | Over budget on the reference laptop, on AC and battery | Manual, minutes |

### 5.1 The per-PR real-clock idle gate

**Which pull requests.** It is a ring 1 suite in [quality/catalog.yml](../../quality/catalog.yml)
with `select: affected`, so it runs when a change reaches any of these paths:
`src/AgentBridge.Host*/` (host, host core, delivery, launcher), `src/AgentBridge.App*/`,
`src/AgentBridge.Local/`, `src/AgentBridge.Transport/`, `src/AgentBridge.Protocol/`,
`src/AgentBridge.Relay/`, `pwa/`, the shared build files (a runtime or package update can change
idle cost), and the gate's own files (the harness, the budget file and the job). A docs-only or
unrelated change does not pay for it. Like the rest of the slow subset, it runs once a pull request
is ready for review and on each later push, not on drafts.

**What it runs.** It reuses the job's build and the 20-session idle fixture of §2.1, with
keep-awake and the HA connector off. The Windows job measures the host and the desktop app
together, first with the app hidden and then visible. The Linux job measures the host with the PWA
visible, then hidden. Each variant is the short form of §2.3: 60 s warm-up not counted, 3 samples
of 30 s, the median, and the control process (an empty .NET process, or a blank page for the PWA)
in the same window.

**CPU verdict.** The number judged is the component's median less the control's.
- Over the **spin ceiling**, 20 times the larger of the component's budget and the 0.15 s/min
  margin floor (6 s/min for the host's 0.3), fails at once. A spin is not noise, and nothing near it
  can pass.
- Within the budget plus its **margin**, passes. The margin is a PROPOSAL of 50% of the budget, and
  never less than 0.15 s/min, so the host passes up to 0.45 s/min. A pass above the bare budget is
  reported as "within margin", so the trend is visible.
- Between the two is **borderline**: the variant is measured once more (3 more samples), and fails
  only if that median is also over budget plus margin. There is one rerun, never best-of-many.

**Hidden PWA verdict.** Its budget is zero, so it is judged by counts, not by a CPU number.
Counting starts 5 s after its rest ends, so the step that closes the socket is not caught on the
edge. From then on, through the 90 s window, an init script and Playwright count timer callbacks,
animation frames, WebSocket frames and requests, and every one of them must be exactly zero: no
tolerance and no rerun, since a count is not noisy. Main-thread CPU, less the blank page's, is
still measured, but only as a noise check under the same rule as above with a zero budget: up to
the 0.15 s/min floor passes (so 0.01 s/min is fine), over it is borderline and rerun once, and
over 3 s/min (20 times the floor) fails at once. The fake-clock hidden-zero test (slice 11c) and
this real-clock count check the same thing in two ways.

**Network verdict.** Counts are far less noisy than CPU, so they are judged over the whole 90 s
window rather than by median. Application messages and bytes per minute may be up to 25% over
budget, and at least one message, since a periodic message or a keepalive can fall either side of
the window's edge; keepalive frames are judged against their own row of §3. The hourly HTTP budget
cannot be read in 90 s, so the window may hold at most one request beyond those the warm-up
excludes; the hourly rate is judged nightly. A hidden PWA is judged by its counts (above). Every
message seen is also checked against its type's byte budget, which catches a real payload growing
where the protocol vectors did not. Over tolerance gets the same one rerun as CPU.

**Memory verdict.** An idle window of 90 s cannot show a leak, so memory has its own phase, run
after the CPU and network window on the same warm process. Which components it covers depends on
the change: the host on every selected pull request (in the Linux job), the desktop app when the
change reaches `src/AgentBridge.App*/`, and the PWA when it reaches `pwa/`; a change to the
protocol, the transport, the relay or the shared build files runs all three.
- **Cycles.** A scripted cycle does a small, fixed piece of real work and returns to idle: a
  session starts, sends 20 hook events, asks a question that is answered, and ends; the app opens
  and closes that session's view; the PWA opens it and goes back to the list. One cycle runs
  untimed as a warm-up, then 10 more. After each, the component rests 5 s and its working set and
  private bytes (JS heap for the PWA) are sampled: 10 samples. The control process is sampled the
  same way.
- **Growth.** Growth is the median of the last 3 samples less the median of the first 3, in private
  bytes (JS heap for the PWA), judged against the cycle-growth row of §3: 3 MB for the host. Working
  set is not judged for growth, since the operating system trims it at will.
- **Steady memory.** The median of the last 3 samples, working set and private bytes, must be
  within the steady budget of §3 plus a margin, a PROPOSAL of 25%.
- **Verdict.** Growth more than 10 times its ceiling (30 MB for the host) fails at once. Growth
  over its ceiling, or steady memory over budget plus margin, gets one rerun: 10 more cycles on
  the same process, judged the same way. A real leak keeps growing and fails it; a one-time cache
  filling up does not, and passes. There is one rerun, never more.
- **Control and not measured.** If the control's private bytes move more than 1 MB across its 10
  samples, or a sample is missing, the phase is retried once and then fails as "not measured". A
  cycle that fails to complete is a test failure, not "not measured".

**Hosted-runner noise, honestly.** An idle process's own CPU time is not inflated by neighbours on
the same host the way wall time is, but it still varies: runner CPUs differ in speed, and the
runtime does background work (tiered compilation, GC) that a 60 s warm-up does not fully finish.
Memory varies too: the garbage collector decides when to return memory, so private bytes after a
cycle carry a few MB of noise, which the medians of 3 and the rerun on the same process absorb.
That is why the per-PR gate uses a margin, a median, a control and one rerun, while the nightly gate
judges the bare budget with the full form. Before any of this, a quiet check (§2.4) runs; a run
whose control process is over 0.1 s/min, or whose quiet check fails, is retried once, and if it
fails again the job fails as **"not measured"**. It never passes unmeasured, and a recurring "not
measured" goes on the flake list like any other flake. The gate **never widens a budget, a margin
or a ceiling by itself**: all of them live in the budget file and change only by a pull request
with the owner's approval. Slice 4 checks the proposed CPU margin and memory ceilings against 10
runs per hosted platform; if the spread there is wider, the owner chooses between a wider margin
and a larger runner, and the budget itself does not move.

**Cost.** The CPU and network window costs about 6 runner minutes on Linux and 6 on Windows per
selected pull request (two variants each, with start-up and teardown). The memory phase adds
about 3 runner minutes per component it covers (11 cycles of about 15 s): about 3 for a host-only
change, up to about 9 when the app and the PWA are covered too. So a selected pull request costs
about 15 to 21 runner minutes, and the parallel Linux and Windows jobs take about 9 to 12 minutes
of wall clock. A rerun adds about 2 minutes for CPU or network and about 3 for memory. Drafts,
docs-only changes and unrelated paths cost nothing.

### 5.2 What stays nightly and before release

The precise gates stay off the pull request because they need time no pull request can spend: the
nightly idle budget (full form, median of 5, no margin, against the absolute budget and the rolling
median of the last 7 nights, flagging a 25% jump even under budget), the 2 h and 24 h memory soaks,
which stay the long-horizon check for leaks too slow for 10 cycles to show, and the laptop check.
The ratchet of §3 reads only nightly medians; per-PR runs never move a budget.

How the counted checks stay exact: the timer count runs on a stepping fake clock, where every read
of the time moves it forward 1 ms, as real time moves between two reads. A loop like #2283's, which
makes a timer again whenever the clock moved since it last read it, then makes a new timer on every
pass until the wait is due (hundreds of thousands for a wait of minutes), and the count goes far
over the ceiling with no wall-clock noise. A fake clock that moves only when the test advances it
would hide that loop (§2.3); the real-clock idle gate catches what the counting misses.

The idle gate is not in `perf.yml`. That workflow runs only on each push to main and by hand, and
its header and [quality/perf.md](../../quality/perf.md) say nothing there gates, which stays true.
Slice 12a adds a line to quality/perf.md pointing to the idle gate.

Failure handling: a ring 1 failure, the real-clock idle gate and its memory verdict included, fails
the pull request's checks and so blocks the merge like any test. A confirmed ring 3 failure opens
(or updates) one issue with the JSON artifact, the commit range since the last pass, and the top
threads; it blocks promotion to `beta` but never blocks unrelated merges. A gate that flakes goes
on the flake list like any test (`quality/publish-flakes.ps1`).

## 6. Idle-timer audit

**Method.** For each component, list every place that waits on time: `Task.Delay`,
`PeriodicTimer`, `TimeProvider.CreateTimer`, `DispatcherTimer`, `DispatcherQueueTimer`,
`CompositionTarget.Rendering`, `Thread.Sleep`, and in the PWA `setInterval`, `setTimeout` loops and
`requestAnimationFrame`. For each, record: does it run while idle, its period, whether it stops when
its feature is off or its view is hidden, and whether an event could replace it. Then confirm with
the meter (timers created and fired per owner) on the real clock. The list below comes from a grep
of `main` on 2026-11-12 and is **unverified** except where marked. The host part of this audit is
#2295 (docs only); it filed #2297 for H2, #2296 for H3 and #2298 for H5.

### 6.1 First inventory, ranked

| # | Where | What runs while idle | Suspicion |
|---|---|---|---|
| H1 | `Host.Core/DevBox/KeepAwakeService.cs:213-235` (`:230`), started at `:170` | Timer created and cancelled in a loop until due; started even when off or not a Dev Box | **Verified** by #2283 stacks; fix in flight |
| H2 | `Host/SignIn/AgentSignInService.cs:68` | A 250 ms repeating timer from construction, sign-in flow or not: 4 wakeups a second forever | High |
| H3 | `Host.Core/SessionSource.cs:342`, `:376`; `SessionSourceOptions.cs:34` | Wakes every 1 s with file watchers on, and each timed wake is a discovery pass that lists processes | High |
| A1 | `App/App.xaml.cs:194-204`; `App.Core/MainViewModel.cs:615` | A 1 s timer from start, never stopped when hidden: `Tick`, timing reports, tray tip; `Tick` appears in #2284's samples | High (desktop) |
| H4 | `Host.Core/Intake/HookIntake.cs:258` (`SweepInterval`, `:25`) | Spool sweep every 2 s with a watcher on | Medium |
| H5 | `Host.Core/Load/ProcessLoad.cs:96-111` (`:51`) | Process sampler every 5 s on thread-pool threads, observed or not | Medium |
| H6 | `Host.Core/Usage/UsageMonitor.cs:95` (`UsageOptions.cs:52`, `:36`, `:40`) | 15 s tick while the idle probe period is 15 minutes; network probes | Medium (network) |
| H7 | `Host/UsagePeers.cs:84` (`:46`, `:49`) | 30 s tick; asks each peer every 5 minutes over the relay, so chatter grows with fleet size | Medium (network) |
| A2 | `App/SessionView.History.cs:104-112` | A per-frame `Rendering` handler that stays until the scroll reaches the end | Medium |
| P1 | `pwa/js/fleet.js:98` (`:28`) | Fleet probe every 60 s while visible | Medium (network) |
| P2 | `pwa/js/ui/delegates-view.js:519` (`:272`) | Hidden: re-arms a 10 s timer instead of waiting for `visibilitychange`; visible: fetches every 10 s | Medium |
| H8 | `Host/SessionService.cs:517` (`:481`) | `HelloCheck` repeating every 10 s once armed | Low |
| H9 | `Host.Core/Restore/RestoreRecorder.cs:770` (`:166`) | Every 5 s while a candidate is undecided | Low |
| H10 | `Transport/RelayClient.cs:269`; `Relay/RelayApp.cs:150` | Both ends ping every 20 s; one side would do | Low (network) |
| A3 | `App.Core/HostConnection.cs:323` (`:68`) | Re-asks every 10 s a host that lacks `hello.changed` | Low (old hosts) |
| A4 | `App/App.xaml.cs:248`; `App.Core/AutoUpdater.cs:72` | 1 min tick; update check every 30 min | Low; #2225 owns |
| H11 | `HomeAssistant/HaConnector.cs:341` (`:29`, `:204`) | 1 s tick and 10 s fleet load, only with HA on | Low (opt-in) |

Bounded, active-work only (to confirm each stops when its work ends): `AnswerService.cs:59` and
`ReplyService.cs:40` (250 ms), `LaunchService.cs:77` (500 ms), `LiteKill.cs:145`,
`AgentLiteSender.cs:70`, `CodexUsageProbe.cs:434`, `LifecycleCoordinator.cs:178` (25 ms while
quieting), `LocalClient.cs:96` (25 ms while connecting), `HostWatch.cs:48` (after an update),
`MoveStripView.xaml.cs:46`, `pwa/js/ui/launch-view.js:980` (250 ms for 15 s). Long periods, fine
as they are: `AttachmentStore.cs:139` and `AgentReadinessService.cs:34` (10 minutes).

Prior art to keep and gate: the PWA closes its socket 15 s after it is hidden
(`pwa/js/connection.js:181`, #610), and the glyph animations (`pwa/app.css:979-980`) run only for
working and delegating sessions, on the compositor, and stop under reduced motion.

## 7. Slices

Each slice is 400 changed lines or fewer, starts with a failing test, and is one PR. Slices 3, 7, 11
and 12 are split into lettered parts to keep to that, so the numbers the existing issues use stay
the same. **2F** marks a sensitive slice (host core, update path, credentials, the wire, CI or a
new project file): two model families read every changed file whole.

| # | Slice | Depends on | Issue | Review |
|---|---|---|---|---|
| 1 | Baseline harness: a measuring tool that attaches to a process and writes `agent-bridge.efficiency/1` (CPU, threads, wakeups, memory, control process, quiet check). Tests with a synthetic idle and a synthetic busy child | none | #2294 | 2F (new project file) |
| 2 | #2283: `KeepAwakeService` awaits to the due time; no scheduler when off or not a Dev Box; prompt stop | none (in flight) | #2283 | 2F |
| 3a | Host meter: a counting `TimeProvider` (timers created and fired, per owner), allocated bytes and GC counts, in the host's composition; read over the local pipe's diagnostics | 1 | none yet | 2F |
| 3b | App meter: the same counting `TimeProvider`, allocation and GC counts in the desktop app's composition, readable by slice 1's tool | 3a | none yet | 1F |
| 3c | Network meter: bytes and messages per endpoint role in the host and the app, keepalive frames counted apart | 3a, 3b | none yet | 2F (the wire) |
| 4 | First baseline: host, app and PWA on the laptop and VM classes, before and after slice 2; #2284 on the reported version; mark each row of §6.1 verified or cleared, starting from #2295's host audit; 10 short-form runs per hosted platform to check §5.1's CPU margin and memory ceilings; replace §3's numbers, still PROPOSAL | 1, 2, 3a, 3b, 3c | none yet | 1F (docs) |
| 5 | Host: the sign-in sweep timer only while a flow is open (H2) | 3a | #2297 | 2F (credentials) |
| 6 | Host: discovery driven by watcher and process-exit events, with a slow safety pass (H3) | 3a, 4 | #2296 | 2F |
| 7a | Host: process-load sampling only while a consumer needs it (H5) | 3a, 4 | #2298 | 2F |
| 7b | Host: the hook intake safety sweep backs off when idle (H4) | 3a, 4 | #2301 | 2F |
| 7c | Host: the usage monitor awaits its next due probe instead of a 15 s tick (H6, wakeups only; what it probes and when is slice 10's) | 3a, 4 | none yet | 2F |
| 7d | Host: `HelloCheck` and the restore recorder await due times and stop once settled (H8, H9) | 3a, 4 | none yet | 2F |
| 8 | Desktop app: nothing ticks while hidden; relative times redrawn when they change; the `Rendering` handler bounded (A1, A2, A3) | 3b, 4 | none yet | 1F + Creative |
| 9 | PWA: no timer while hidden in the delegates view; fleet changes pushed, with a slow safety probe (P1, P2); clock-counted Playwright tests | 1 | #2300 | 1F + Creative |
| 10 | Network: one keepalive side (H10), usage and peer chatter by change rather than period (H6, H7), with #2225's release hints | 3c, 4; #2225 slices 3 to 5 | none yet | 2F (wire, update path) |
| 11a | Per-PR gate, host: the committed budget file, and the host idle timer count on the stepping fake clock (§5), its ceiling starting at today's counts; catalog row | 2, 3a | none yet | 2F (CI) |
| 11b | Per-PR gate, app: idle timer count for the app's visible and hidden compositions, hidden at zero | 3b, 8, 11a | none yet | 2F (CI) |
| 11c | Per-PR gate, PWA: the hidden-zero test across every view: after its rest, no timer fires and no frame is sent | 9, 11a | none yet | 2F (CI) |
| 11d | Per-PR gate, payloads: a byte budget per message type, checked over the protocol vectors | 11a | none yet | 2F (CI, wire) |
| 12a | Per-PR real-clock idle gate, host CPU: the `ci.yml` job on Linux and Windows with its catalog row and path filter (§5.1); short form, control process, quiet check, spin ceiling, margin, one rerun and "not measured"; margins and the spin multiple added to the budget file; a line in quality/perf.md pointing to it. Until the owner approves §3, it fails only over the spin ceiling and reports the budget comparison | 1, 11a | none yet | 2F (CI) |
| 12b | Per-PR real-clock idle gate, desktop app CPU: the Windows job measures the app hidden, then visible, beside the host | 8, 12a | none yet | 2F (CI) |
| 12c | Per-PR real-clock idle gate, network: host and app messages, bytes, keepalive frames and requests from the meter, with §5.1's tolerance, and each observed message against its type's byte budget | 3c, 12a, 12b | none yet | 2F (CI, the wire) |
| 12d | Per-PR real-clock idle gate, PWA: visible main-thread CPU and frames, and the hidden verdict of §5.1 (exact zero counts after rest, CPU held to the noise floor), in the Linux job | 9, 11c, 12a | none yet | 2F (CI) |
| 12e | Per-PR memory verdict, host: the scripted activity cycle for the host fixture, 10 samples at rest, growth and steady verdicts, the control, one rerun on the same process and "not measured" (§5.1); cycle-growth ceilings and the memory margin added to the budget file. Failing first: `Memory_verdict_fails_when_growth_stays_over_its_ceiling_through_the_rerun`, beside `Memory_verdict_passes_when_a_one_time_cache_stops_growing_in_the_rerun` and `Memory_verdict_reports_not_measured_when_the_control_moves` | 1, 12a | none yet | 2F (CI) |
| 12f | Per-PR memory verdict, app and PWA: their cycle steps, and covering each only when the change reaches its paths. Failing first: `Memory_phase_covers_the_app_only_when_the_change_reaches_its_paths` and `Pwa_memory_cycle_fails_when_the_js_heap_keeps_growing` | 12b, 12d, 12e | none yet | 2F (CI) |
| 12g | Nightly idle budget on the lab VM (ring 3), full form with the rolling-median check, the failure issue, and the report row; the only input to the ratchet | 4, 12a, owner approval of §3 | none yet | 2F (CI) |
| 12h | Memory soak on the lab VM: nightly 2 h and weekly 24 h, the long-horizon check behind 12e and 12f | 12g | none yet | 2F (CI) |

**Order.** 1 and 2 start now, in parallel; 9 can start once 1 lands. Then 3a, 3b and 3c, then 4
(baseline, building on #2295's host audit). Then the host fixes 5, 6 and 7a to 7d in parallel (5
needs only 3a). Then 8 (it needs 4's desktop attribution), then 10. Gates: 11a can land as soon as
2 and 3a do, with today's counts as its ceiling, and each host fix lowers its own count in the
budget file in the same PR. 11b waits for 8 and 11c for 9, since neither hidden-zero check can pass
before those fixes; 11d needs only 11a. The per-PR real-clock gate follows the same rule: 12a can
land right after 11a, failing pull requests on a spin from the start, and judges the budget plus
margin once the owner approves §3; 12b waits for 8, so the app is not held to a hidden spin
ceiling of 3 s/min while its 1 s tick (A1) still runs; 12c and 12d follow. The memory verdict
follows the same pattern: 12e can land right after 12a, failing a pull request at once on growth
of 10 times the ceiling, and judges the ceiling and steady budget once the owner approves §3; 12f
follows 12b and 12d. Then the nightly 12g and the soaks in 12h.

## 8. Open question for the owner

Routed through the Chief.

1. **Measuring on your laptop.** The reference laptop class is the only one that showed #2283, and
   AGENTS.md forbids tests on your machines without your say-so. May SRE run slice 1's tool,
   read-only and without tracing, for about 30 minutes on your laptop (on AC, then on battery) for
   slice 4 and for ring 4 before each stable promotion? **Recommended default:** yes, read-only
   counters only, scheduled with you; no install, setting or host restart.
