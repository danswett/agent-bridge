# Quality: the test harness, its rings and its plan

*Proposed 2026-11-02, Pacific. Status: phase 1 under way, tracked in #357. Merged so far:
test categories (#375), CodeQL, dependency review and workflow lint (#361), coverage and
timing (#363), the selector and `quality/run.ps1` (#362), and CI's plan, selected jobs and
`quality-gate` (#436). §1 describes the starting point.*

This folder tracks every test this repository runs, where and when each one runs, and what
it costs. It also holds the plan for growing the tests into a harness we can repeat, from a
developer's inner loop to a release. [catalog.yml](catalog.yml) lists every suite, existing and
planned, and is the source of truth the tooling below reads.

| Read | For |
|---|---|
| [catalog.yml](catalog.yml) | Every suite: kind, ring, platforms, runner, triggers, command, baseline, status |
| §1 | What exists today, and the gaps |
| §2 | The rings: what runs when |
| §3 | The kinds of test, and the plan for each |
| §4 | Choosing tests from what changed |
| §5 | Coverage, timing, flakes, and where results go |
| §6 | The lab: disposable Windows, Linux and macOS machines |
| §7 | Release channels and promotion |
| §8 | The plan, in phases |
| §9 | Decisions for the owner |

## 1. Where we are (2026-11-02)

The base is strong. CI already tests what ships, not just the code:

- **.NET:** 2,569 xUnit tests in 10 projects on Windows x64, Windows Arm64 and Linux. They
  take 2 min 45 s on one machine, build included. The three slowest projects take most of
  that: Host.Delivery (1 min 46 s, with real console injection), HomeAssistant (1 min 9 s)
  and Host (1 min 6 s).
- **Binaries as they ship:** the NativeAOT injector types into a real console, and the NativeAOT
  hook is started fresh for each event. The relay container runs read-only, as it ships. The
  packaged WinUI app runs its `--self-test` (UIA, every theme resource), Setup.exe installs
  silently, and the installed copy self-tests again. Each runs on x64 and Arm64.
- **Integration:** the HA connector runs against a disposable Home Assistant and Mosquitto.
  A daily job records agent-ha-bridge's contract again and reports drift.
- **PWA:** 26 Node unit files, then 22 Playwright specs in Chromium and WebKit against the
  published build's CSP, and end to end through the dev stack's relay.
- **Design:** generated files are checked against `design/`, and every theme is checked for
  contrast.
- **Relay:** smoke-tested on staging with a throwaway account, two Noise devices, push and
  blobs. Production is gated on approval.
- **Live:** the migrate-and-rollback rehearsal on Azure VMs ([parity-checklist.md](../docs/parity-checklist.md),
  automated in part by `agent-bridge-rehearsal`), and the shadow soak on VM-M driving real
  Copilot, Claude and Codex sessions.

The gaps this plan closes:

1. **No selection.** Every PR runs everything (CI's 11 jobs, whatever it touched). A
   docs-only PR waits for the Arm64 app package. Locally there is no command smaller than
   `dotnet test AgentBridge.slnx`.
2. **No rings.** Every merge to `main` is a pre-release that installed apps take within
   30 minutes. So PR CI is the only gate before real machines run the code. The live and
   VM checks run only by hand.
3. **No coverage, no timing history, no flake record.** #101, #129, #241 and #266 were each
   found and chased by hand.
4. **Security is review-only.** There's no CodeQL, dependency scanning, secret scanning,
   container or IaC scanning, or fuzzing. That's despite a threat model ([design.md §5.1](../docs/design.md))
   that names parsers fed by untrusted parties.
5. **Performance is measured once, not guarded.** The §5.9 load model, the 40 KiB upsert
   budget and the hook's latency all have numbers, but no test fails when they regress.
6. **Accessibility is partial.** Contrast and some ARIA are covered. There is no axe scan, no
   Axe.Windows scan, no keyboard-only pass and no screen-reader checklist.
7. **macOS, iOS and Linux live.** The Apple clients' SwiftPM tests (about 35 files on
   `codex/apple-*`) and the macOS tmux delivery tests have no CI. The Linux host has unit
   tests but no live run since `rehearsal-l` was deleted.
8. **Agents drift.** Copilot CLI, Claude Code and Codex change their transcript formats in
   new releases. Today we find out on a real machine.

## 2. The rings

Each ring runs everything in the rings below it, plus its own suites. The rule is
**fast and selected near the developer; complete and real near the user.**

| Ring | When | Budget | What runs | Where | Blocks |
|---|---|---|---|---|---|
| **0 Inner loop** | On demand, and as a pre-push hook | < 60 s typical | The suites a change reaches (§4), unit and component only, on this machine's OS; secret scan of staged files; design check if `design/` changed | Developer machine or agent session | Nothing; it's advice |
| **1 PR gate** | Every push to a PR | < 15 min wall | Selected suites on every OS they declare, plus the sentinels (`protocol`, CodeQL, dependency review). Shipped-binary checks (AOT, container, package) when reached. axe for PWA changes. Diff coverage | GitHub-hosted runners | Merge (one required check: `quality-gate`) |
| **2 Main** | Every merge to `main`, before publish | < 30 min | **Everything** hosted, on every platform: upgrade by delta, WinUI and Apple UI tests, a11y scans, benchmarks recorded, SBOM and attestation, container scan, relay staging smoke | Hosted runners and staging | Publishing to the `dev` channel |
| **3 Nightly / candidate** | Nightly on `main`'s head, and on demand for a candidate | < 3 h | Lab VMs: install the dev build on clean Windows x64 and Arm64, Linux and macOS. End to end with simulated agents, then real agents. Agent drift. Perf on quiet machines against baselines. Relay load and DAST on staging. Fuzzing | Lab (§6), staging | Promotion to `beta` |
| **4 Release** | Before promotion to `stable`, or relay production | Hours to days | Migrate and roll back from the S0 snapshot. Shadow soak gate. Relay production smoke. iPhone PWA and screen-reader checklists. A release-readiness report | Lab, real devices, the owner | Promotion to `stable`; relay production approval |

Escalations within ring 1:

- **Security-sensitive paths** (`security_sensitive: true` in the catalog: Transport, Local,
  Relay, signed commands, authorization) add the security suites. They also label the PR
  `security-review`, which records AGENTS.md rule 5 rather than relying on memory.
- **Labels** let a person ask for more on a risky PR: `test:full` runs ring 2 on the PR,
  and `test:lab` runs ring 3's simulated end to end on the PR's build.
- **Unknown paths** that no rule matches run the full ring. A selector must never decide
  "nothing to test" by mistake.

## 3. The kinds of test

Each suite in the catalog has one `kind`. The plan for each kind follows.

### 3.1 Functional

- **Unit and component** (exists). Keep the house style: names read as sentences, each test
  fails without its change, and no test is ever weakened to get green.
  - Add a `Category` trait only where a project mixes speeds: `Process` for tests that start
    real processes (InjectorIntegrationTests, HookProcessTests), `Network` for those that bind
    sockets. Ring 0 can then skip `Process` in the slow projects. Untagged means unit.
- **Contract** (partial). One set of protocol vectors (JSON records, Noise transcripts, signed
  command envelopes) under `docs/protocol/vectors/`, run by C#, the PWA and Swift alike. S2
  proved the method for Noise; extend it to every record so a field renamed in one client
  fails in all three.
- **Shipped binaries** (exists). Keep testing the AOT injector, the AOT hook, the container,
  the package and the installer as they ship. Add **upgrade by delta** from the last
  published release (`app-upgrade`), since every installed copy updates that way and only a
  fresh install is tested today.
- **Integration** (exists for HA). Add `agent-drift` in ring 3: the ha-contract-drift method
  applied to the agents. Each night it installs the newest Copilot CLI, Claude Code and
  Codex in a lab VM, records a scripted session's transcripts and hook payloads, and runs
  host-core's parsers over them. It fails and opens an issue when a format moves.
- **End to end, simulated** (planned, ring 3, and ring 1 by label). Add simulated agents
  under `tests/AgentBridge.AgentSim`, growing `AgentBridge.ConsoleEcho`. Each one writes
  real-format transcripts (replayed from the recorded corpus, design.md §6.4), runs the real
  hook, and accepts typed answers. The installed app and host then run every parity
  scenario (E1–E18) on every OS with no agent account, no allowance and no nondeterminism.
- **End to end, real agents** (partial, ring 3). Generalize the shadow soak driver
  (`agent-bridge-rehearsal soak`) from "compare with agent-ha-bridge" to "run the scenario
  list and check the outcome". It runs nightly on the lab with test accounts and per-agent
  caps, as the soak already does for Codex.
- **UI**:
  - **WinUI:** grow the `--self-test` path into a UI Automation suite (`winui-ui`, FlaUI) on
    hosted runners. It covers every page, keyboard-only use, light, dark and high contrast,
    and pictures compared with a tolerance. Hyperloop on lab VMs records what a person would
    see for the release report.
  - **Apple:** XCUITest against a demo host (`apple-ui`), on hosted macOS runners and the
    iOS simulator.
  - **PWA:** Playwright, as now.

### 3.2 Performance

Hosted runners vary too much to gate on absolute numbers. So ring 2 records and ring 3
compares, on a lab machine kept for that and nothing else.

| Suite | Measures | Gate |
|---|---|---|
| `perf-hook` | The hook's start-to-reply time, cold and warm, with the host up and down. Every agent waits on it at every event | Ring 1, against a budget set from the first baseline, with generous headroom |
| `bench-micro` | BenchmarkDotNet: Noise, protocol encoding, upsert building, transcript tailing, session ordering, markdown | Ring 3: more than 10% slower than the rolling baseline, confirmed on rerun |
| `perf-host` | Replays the 449-session corpus (design.md §5.9): start time, idle and busy CPU and memory, discovery time, frames and bytes per minute, every upsert within 40 KiB | Ring 3, against §5.9's table |
| `perf-relay-load` | Simulated users at §5.9's load, scaled up (S9): connections, frames/s, p95 latency, replica memory, blobs, a rolling restart under load | Ring 3 on staging; ring 4 before a production relay change |
| `perf-clients` | PWA Lighthouse budgets and a 300-session list; WinUI and Apple cold start and scrolling 300 sessions (the Apple branch's fixtures) | Ring 3 |

Baselines live in `quality/baselines/` as small JSON files, one per suite and platform, and
change only by PR. A regression is accepted on purpose by updating the baseline in the PR
that causes it, with the reason in the PR. How to run `perf-hook` and `bench-micro`, read their
numbers and update a baseline is in [perf.md](perf.md).

### 3.3 Security

Mapped to the threat model, so each untrusted party has tests aimed at it:

| Threat (design.md §5.1) | Tests |
|---|---|
| The relay operator and network: can't read, forge, replay or reorder | Transport tests (exists). Plus a tagged `Security` suite: replayed, reordered, truncated and cross-account frames refused. Relay DAST with ZAP on staging (`relay-dast`). Fuzzing relay frames and Noise messages |
| A stolen GitHub account: can register, not act | Roster and signed-command tests (exists). Plus device approval with a matching code once #55 lands |
| Other processes of the same user | Local pipe ACL tests (exists, `local-api`). Fuzzing the pipe's framing |
| Agents (prompt-injected) | Delegate-name cleaning, bidi and invisible characters (#220, #263). The §6.3 invariants. Folder trust never answered remotely. Fuzzing transcript lines and hook stdin |
| Supply chain | CodeQL (C#, JS, Actions, later Swift). Dependency review on PRs, plus `dotnet list package --vulnerable` and `npm audit`. Secret scanning with push protection and a gitleaks pre-commit hook (a leaked signing key once forced a rotation). Trivy on the relay image. PSRule on `infra/`. actionlint and zizmor on workflows. SBOM and build provenance per artifact |

Tag every test that enforces a threat-model property or a §6.3 invariant:
`[Trait("Threat", "relay")]` or `[Trait("Invariant", "6.3.12")]`. Ring 2 then generates a
traceability table (`quality/traceability.md`) showing which properties have tests and which
have none. It extends the "§6.3 invariants, by step" table in parity-checklist.md from the
manual rehearsal to the automated tests.

### 3.4 Accessibility

- **PWA** (ring 1): `@axe-core/playwright` on every view in light, dark and forced colors. It
  fails on any new serious or critical issue. Also add keyboard-only specs (every action
  reachable, focus visible and in order) and a reduced-motion spec now that U9 adds motion.
- **WinUI** (ring 2): an Axe.Windows scan of each page the self-test visits, with high
  contrast on and off (#240 showed how those transitions break).
- **Apple** (ring 2): `performAccessibilityAudit()` in each XCUITest. Dynamic Type at the
  largest size.
- **Design** (ring 0, exists): contrast for every token pair.
- **People** (ring 4): a 15-minute screen-reader checklist per release (Narrator, VoiceOver on
  Mac and iPhone) in `quality/checklists/`. Automated scans find about a third of real issues.

### 3.5 Reliability and compatibility

- **Upgrade and rollback:** install N-1 and update to N by delta, on both channels and
  architectures. Also roll the relay forward and back with live channels open, and migrate
  then roll back (exists, ring 4).
- **Chaos, in the simulated end to end:** kill the host mid-answer, restart the relay, drop
  the network, fill the disk, change the clock. Every case must hold to the §6.3 invariants
  (an answer is never typed twice, discovery holds rather than reading absence).
- **Soak:** the shadow soak (exists) becomes a standing ring-3 job on the lab, not a one-off.
- **Compatibility matrix:** run the product on Windows 10 22H2 and 11 (x64 and Arm64),
  Ubuntu LTS (x64 and Arm64), macOS current and previous, iOS current and previous, the
  current and previous agent CLI versions, and Chromium and WebKit. Ring 2 covers what
  hosted runners can. Ring 3's lab covers the rest.

## 4. Choosing tests from what changed

One selector, `tools/AgentBridge.Quality` (`agent-bridge-quality select`), serves both ring 0 on a
laptop, through `quality/run.ps1`, and ring 1 in CI, so both agree on what a change needs. Its
`check` command is the completeness check in step 5.

1. **What changed:** `git diff --name-only <merge-base>...HEAD`, plus uncommitted and untracked
   files locally. In CI a pull request's checkout is its merge commit, so the change is
   `HEAD^1..HEAD`: what merging adds to the base, even when the branch is behind.
2. **.NET, derived from the code itself, not hand-kept:** map each changed file to the
   project that owns it. Walk the `ProjectReference` graph backwards to every test project
   that reaches it. A file a project links in from outside its folder (a shared fixture,
   `docs/protocol/examples`) belongs to that project too. For example, a change to
   `AgentBridge.Protocol` reaches every test project but `transport` and `hooks`. A change to
   `AgentBridge.Relay` reaches `transport`, and through it `app-core` and `host`, because
   App.Tests and Host.Tests reference Transport.Tests, which references Relay (plus the relay's
   container and staging suites). The graph today, each project with what it references:

   ```
   Host.Core     -> Protocol, Hooks          Local     -> Protocol, Transport
   Host.Delivery -> Protocol, Host.Core      Relay     -> Transport
   HomeAssistant -> Host.Core                App.Core  -> Protocol, Local, Host.Core
   Host          -> Host.Core, Host.Delivery, Transport, Local, HomeAssistant
   Transport.Tests -> Transport, Relay, RelaySmoke
   App.Tests     -> App.Core, Transport.Tests      Host.Tests -> Host, Transport.Tests
   ```
3. **Everything else:** each suite's `triggers` globs in the catalog (`pwa/**`, `apple/**`,
   `infra/**`, `build/**` and so on).
4. **Always:** the sentinels (`select: always` at that ring). Everything runs on
   `global_triggers` (the solution, NuGet config, workflows, `quality/` itself). When every
   file is in `no_test_paths`, only the catalog's `docs-links` suite runs; there's no hidden
   docs logic in the selector.
5. **Fail safe:** a file that no rule claims runs the full ring. A ring-1 check fails if any
   tracked file has no rule, so new folders get a rule when they're added.
6. **Output:** JSON with the suites, the OS matrix and **why**, each suite naming the files
   that pulled it in. The PR summary shows that table, so a reviewer can see what didn't
   run and why.
7. **GitHub mechanics:** ci.yml's `plan` job checks the catalog, then runs the selector through
   `quality/plan.ps1`: ring 1 on a pull request, ring 2 on `main`. It writes the "what runs
   and why" table to the run's summary, and outputs:
   - a true or false per job, from the selected suites' `where:`. So the catalog, not ci.yml,
     says which suites a job runs, and moving a suite moves its trigger. On a pull request a
     job runs only when it's true; on `main` every job runs whatever the plan says;
   - the selected suites, which gate the steps inside `product` (the AOT injector and hook,
     the build scripts, the relay container);
   - `product`'s test projects, built and tested through a solution filter, still under
     `dotnet-coverage` with TRX and `--blame-hang`. When the whole ring runs, it's
     `AgentBridge.slnx` as before;
   - `product`'s runners, from its selected suites' `platforms` (all three when the whole
     ring runs).

   A final `quality-gate` job (`if: always()`) needs all of them and fails if any failed or
   was cancelled; skipped is fine. It's the check to make required, so skipped jobs never
   leave branch protection waiting (§9: the owner decides when). `publish-gate` still needs
   `product` and `app` to have succeeded, and on `main` they always run, so a selection miss
   is caught before a release publishes.
8. **Keeping the selector honest:** ring 2 runs everything on `main`, and each `main` run's
   summary also says which jobs ring 1 chose for the same change. For every ring-2
   failure, a nightly job asks whether ring 1's selection for that PR would have included
   the failing test. A **selection miss** is a bug in the catalog. It gets an issue, and the
   rate is a tracked metric (target: zero).

Later, once per-test coverage is collected (§5), selection can narrow from projects to
individual test classes in the slow projects. Project-level selection already captures most
of the benefit, because the slow projects sit at the top of the graph.

Ring 0's command, the same on Windows, Linux and macOS (PowerShell 7.4 or later):

```
pwsh quality/run.ps1            # what this change reaches, on this OS, in-process tests only
pwsh quality/run.ps1 -Ring 1    # what the PR gate would run here
pwsh quality/run.ps1 -Explain   # the selection and why, without running
pwsh quality/run.ps1 -Suite pwa-ui,host
```

People and agents follow the same loop: AGENTS.md's Tests section says to run
`pwsh quality/run.ps1` before a push. It skips, with a note, suites that only CI can run, suites
that are planned, and suites whose tool (Node, Swift) isn't installed.

## 5. Coverage, timing, flakes and reports

- **Coverage** (collected since #363, no gates yet): the `dotnet-coverage` tool (Cobertura,
  pinned in `.config/dotnet-tools.json`) around `dotnet test` for .NET, with no test project
  changed. Node's built-in coverage for the PWA's unit tests, later Playwright's V8 coverage,
  and `swift test --enable-code-coverage` for Apple. ReportGenerator merges them per OS per run
  into the `coverage-<os>` artifacts and the job summary. The baseline at #363: 86.3% of lines
  and 76.5% of branches on Windows, 83.8% and 75.1% on Linux (which skips Windows-only tests),
  and 76.4% of lines in the PWA. HostLauncher has no tests.
  - **Gates:** no global percentage, which mostly rewards trivial tests. Instead:
    1. **Diff coverage**: changed lines are at least 80% covered in ring 1. It's advisory for
       the first month, then required.
    2. **A per-project floor, per OS,** that only ratchets up, kept in
       `quality/coverage-floors.json`.
- **Timing:** every `product` run already records each test's duration (TRX), and the job
  summary lists per-project counts and times and the 10 slowest tests. Next, each suite's
  duration is compared with its catalog `baseline`, and one that grows more than 50% slower
  gets flagged in the PR summary.
- **Flakes:** a test that fails, then passes on rerun with no change, is recorded as flaky
  with its run link. It's reported, never silently retried to green. Each flaky test gets an
  issue (label `flaky`) and a row in `quality/flaky.yml` with the issue and date.
  - Nothing is skipped without the owner's say-so (AGENTS.md: never weaken tests).
  - Nightly reruns flaky tests 50 times on each platform to confirm a fix.
  - The `--blame-hang` setting stays in place.
- **Where results go:** artifacts per run. A `quality-data` orphan branch, written only by
  `main`'s and nightly runs, keeps the time series (coverage, durations, flakes, benchmarks,
  selection misses). It's rendered at `https://danswett.github.io/agent-bridge/quality/` by
  the existing Pages workflow. Generated data never lands in `main`, so it causes no churn.
- **Release-readiness report:** ring 4 generates one page per candidate. It lists each
  suite's last result on that build, the lab runs, the soak gate, perf against baseline, open
  `flaky` and `security` issues, and the manual checklists signed off. It's attached to the
  release and linked from docs/status.md.

## 6. The lab: disposable machines

Today `build/rehearsal-azure-vmw.ps1` and `build/shadow-soak-vm.ps1` build and drive two
Windows VMs well: idempotent, `az vm run-command` instead of SSH, snapshots, auto-shutdown,
one secret DPAPI-protected. The lab generalizes them rather than replacing them.

- **`quality/lab/fleet.yml`:** the machines, by role. Each image is built once, snapshotted
  as S0, restored for every run and deallocated after.

  | Role | Image | Used by |
  |---|---|---|
  | `win-x64` | Windows 11 x64 | E2E, agent drift, perf, rehearsal |
  | `win-arm64` | Windows 11 Arm64 (Azure Cobalt or Ampere) | E2E |
  | `win10` | Windows 10 22H2 x64 | Compatibility |
  | `linux-x64`, `linux-arm64` | Ubuntu 24.04 | Host E2E, agent drift |
  | `ha` | VM-W's role: disposable Home Assistant and Mosquitto | HA E2E and soak |
  | `perf` | A fixed-size VM kept only for benchmarks | §3.2 comparisons |
  | `macos` | See §9: hosted `macos-latest`, a self-hosted Mac mini, or EC2 Mac | macOS host and app E2E |
- **`quality/lab/run.ps1 -Role <r> -Build <artifact> -Scenario <s>`:** restore S0, install the
  build under test from the CI artifact, run the scenario, collect artifacts (logs,
  transcripts, Hyperloop recordings), then deallocate. It runs from a workflow through OIDC,
  like `relay.yml`, with a `lab` environment and its own principal scoped to
  `rg-agent-bridge-lab` only. Nothing in it can reach the owner's machines, Home Assistant or
  broker.
- **Accounts:** dedicated test accounts for GitHub and each agent, with per-agent caps (the
  soak already caps Codex). Their tokens are `lab` environment secrets, never printed, as
  AGENTS.md requires.
- **Cost guard:** a nightly budget alert on the resource group. Every run deallocates in a
  `finally`, and a sweeper deallocates anything left running past 4 hours.
- **iOS:** the simulator on macOS runners for XCUITest. Real-device runs are the owner's
  iPhone and a checklist (`pwa-iphone`). A device cloud only if the owner wants one (§9).

## 7. Release channels and promotion

Today every merge to `main` reaches real machines within about 30 minutes, so nothing after
the PR gate can stop a regression. The rings only protect users if releases pass through them:

| Channel | Gets | Promoted when | Who runs it |
|---|---|---|---|
| `dev` | Every merge to `main` (today's behaviour) | Ring 2 green | Lab VMs, and any machine opted in |
| `beta` | The newest `dev` build with a green ring 3 | Nightly, automatically | The owner's migrated machines (ULTRA, DEV-VM1, ARM8) |
| `stable` | A `beta` build with ring 4 signed off | By the owner | Everyone else, and HOME once migrated |

Velopack channels already exist per architecture (`build/app-channels.ps1`). Promotion
re-publishes the same bytes into the next channel's feed, with the same digest; it never
rebuilds. A bad build is stopped by not promoting it, and rolled back by promoting the
previous one. The relay already works this way (staging, then production on approval). This
section only brings the app into line with it.

## 8. The plan, in phases

Each phase is a few PRs, and each PR leaves CI green and useful on its own.

**Phase 0: see what we have (week 1)**
1. This folder: the plan and the catalog (this PR).
2. Coverage and timing collected and uploaded on every run (no gates). A first
   coverage-floor and duration baseline.
3. CodeQL, dependency review, secret scanning with push protection, and gitleaks as an
   opt-in pre-commit hook.
4. `Category` traits on the process-level tests in Host.Delivery and Hooks.

**Phase 1: the inner loop and the PR gate (weeks 2–3)**
1. The selector (`tools/AgentBridge.Quality`) with its tests: graph walk, triggers, fail-safe, explain.
2. `quality/run.ps1` for ring 0.
3. `ci.yml` restructured: `plan`, then selected jobs, then `quality-gate` (#436), with
   `docs-links` (lychee, offline, advisory). Then branch protection moved to `quality-gate`
   alone, once the owner agrees.
4. The catalog completeness check, and diff coverage (advisory).
5. axe in the PWA's Playwright specs, with keyboard-only and reduced-motion specs.
6. actionlint and zizmor on workflows; PSRule on `infra/`.

**Phase 2: main, Apple and the slow kinds (weeks 3–5)**
1. Apple: `apple-core` and `apple-build` on `macos-latest`, once the `apple/` package lands
   on `main`. Coordinate with the contributor on the `codex/apple-*` branches; don't touch
   their branches.
2. `mac-host`: the .NET solution on macOS, including the ManagedTmux tests.
3. `app-upgrade` (delta from N-1), WinUI UIA suite, Axe.Windows, Trivy, SBOM and provenance.
4. `bench-micro` and `perf-hook` recording numbers; `quality-data` branch and the Pages report.
5. Shared protocol vectors run by C#, the PWA and Swift.
6. Threat and invariant traits, and the generated traceability table.

**Phase 3: the lab and nightly (weeks 5–8)**
1. `quality/lab/`: fleet, `run.ps1` and the `lab` OIDC environment, starting from the
   rehearsal scripts. Add Linux, Arm64 and Windows 10.
2. Simulated agents (`tests/AgentBridge.AgentSim`) and `lab-e2e-simulated` on every OS.
3. `agent-drift` nightly.
4. `lab-e2e-agents`: the soak driver as a scenario runner on test accounts.
5. `perf-host` corpus replay and `perf-relay-load` on staging; `relay-dast`; fuzz targets.
6. The flake record and the selection-miss report.

**Phase 4: releases (weeks 8–10)**
1. `dev`, `beta` and `stable` channels with promotion by digest. Migrated machines move to
   `beta` with the owner.
2. The release-readiness report, and the iPhone and screen-reader checklists.
3. Ring 4 before a `stable` promotion or a relay production deploy.
4. Gates flipped from advisory to required one at a time (diff coverage, perf, axe), each
   after a clean fortnight.

**Done looks like:**
- A typical change gets an answer in under a minute locally and under 15 minutes on its PR.
- Every merge is checked on every platform before any machine takes it.
- Every night, clean machines run the installed product end to end, on every OS, with real
  agents.
- No build reaches `stable` without the rings' evidence on one page.

## 9. Decisions for the owner

1. **Channels (§7).** Should migrated machines move from "every merge" to `beta`? It's the
   only way the later rings can protect them. It also slows how fast a fix reaches them, by
   up to a day, unless promoted by hand.
2. **macOS hardware.** Hosted `macos-latest` covers builds, unit tests and the simulator, but
   has no Secure Enclave access for a signed app and no persistent agent sign-in. Options:
   - a self-hosted Mac mini runner (the existing Mac is a production machine and shouldn't
     be one);
   - EC2 Mac (24-hour minimum allocation).
3. **Agent test accounts.** Separate GitHub, Anthropic and OpenAI accounts and allowances for
   the lab, so nightly runs never spend the owner's.
4. **Third-party services.** Everything above can stay within GitHub and Azure. Codecov, a
   device cloud (BrowserStack, for iPhone Safari) and Lighthouse CI's server are optional.
   Each would see code or results from a public repository, which is fine for code but
   worth a decision for anything with tokens.
5. **Required checks.** Moving branch protection to the single `quality-gate` check, and when
   each advisory gate (diff coverage, axe, perf) becomes required.
6. **Lab budget.** A monthly ceiling for `rg-agent-bridge-lab`.
