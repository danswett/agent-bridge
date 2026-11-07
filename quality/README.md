# Quality: the test harness, its rings and its plan

*Proposed 2026-11-02, Pacific. Status: phase 1 under way, tracked in #357. Merged so far:
test categories (#375), CodeQL, dependency review and workflow lint (#361), coverage and
timing (#363), the selector and `quality/run.ps1` (#362), CI's plan, selected jobs and
`quality-gate` (#436), CI throughput: drafts run the fast ring, CodeQL per language, caches,
fewer jobs and a sharded PWA (#459), and the nightly flake hunt (#509). §1 describes the
starting point.*

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
  App tests keep a UI thread per test, but start only `maxParallelThreads` collections
  at once (one per core by default). #660 verified that their custom runner bypassed
  xunit's collection limit; the cause of the simultaneous CI stalls remains unproven.
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
   VM checks run only by hand. The clean-VM lab (§6) has an agreed contract
   ([lab/README.md](lab/README.md)) but no run: only a provisioning draft that refuses to
   apply exists.
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
| **1 PR gate** | Every push to a PR that isn't a draft, and marking one ready for review | < 15 min wall | Selected suites on every OS they declare, plus the sentinels (`protocol`, CodeQL, dependency review). Shipped-binary checks (AOT, container, package) when reached. axe for PWA changes. Diff coverage | GitHub-hosted runners | Merge (one required check: `quality-gate`) |
| **2 Main** | A merge to `main` that changes what decides selection, or whose last passed commit can't be found, before publish; any other merge to `main` runs ring 1 for everything since that commit (§4.7) | < 30 min | **Everything** hosted, on every platform: upgrade by delta, WinUI and Apple UI tests, a11y scans, benchmarks recorded, SBOM and attestation, container scan, relay staging smoke | Hosted runners and staging | Publishing to the `dev` channel |
| **3 Nightly / candidate** | Nightly on `main`'s head, and on demand for a candidate | < 3 h | Lab VMs: install the dev build on clean Windows x64 and Arm64, Linux and macOS. End to end with simulated agents, then real agents. Agent drift. Perf on quiet machines against baselines. Relay load and DAST on staging. Fuzzing | Lab (§6), staging | Promotion to `beta` |
| **4 Release** | Before promotion to `stable`, or relay production | Hours to days | Migrate and roll back from the S0 snapshot. Shadow soak gate. Relay production smoke. iPhone PWA and screen-reader checklists. A release-readiness report | Lab, real devices, the owner | Promotion to `stable`; relay production approval |

**Drafts run the fast ring** (#459). Runners are what every session waits for, and a work in
progress pushes often. So a push to a draft PR runs, in CI:

- ring 0's selected suites on Linux only, with ring 0's in-process test filter (no `Process`,
  `Network` or `HomeAssistantIntegration` tests);
- the catalog check (the plan job);
- actionlint (Workflow lint) when a workflow changes.

There's nothing on Windows, no browsers, no CodeQL and no dependency review, and the
workflows HA contract drift, IaC scan and Relay (its template build, image and staging) wait
too. A draft's result says plainly that it's partial: the plan's summary opens with it, and
`quality-gate` adds a notice. Marking the PR ready for review runs ring 1 and those workflows on
the same head, and so does every later push while it stays ready. A draft's gate reports as
`quality-gate (fast ring)`, never as the required `quality-gate`, so its green can't stand in for
ring 1's on the same head: nothing merges on the fast ring alone.
A docs-only draft runs just `plan` and the gate.

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
- **Contract** (partial). Records and signed commands have one set of protocol vectors under
  `docs/protocol/vectors/`: every Protocol v1 record, valid and must-refuse, and the signed
  command envelope. C# and the PWA replay them (#454), and Swift should too once `apple-core`
  exists, so a field renamed in one client fails in all of them. The Noise transcripts are still
  in `spikes/s2-noise-interop`, and the channel framing (`{id, op, data}` and its replies and
  events, in `AgentBridge.Transport`) has no vectors yet.
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
| Supply chain | CodeQL (C#, JS, Actions, later Swift). Dependency review on PRs, plus `dotnet list package --vulnerable` and `npm audit`; every npm lockfile pins its packages by SHA-512 (#650). Secret scanning with push protection and a gitleaks pre-commit hook (a leaked signing key once forced a rotation). Trivy on the relay image. PSRule on `infra/`. actionlint and zizmor on workflows. SBOM and build provenance per artifact |

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
- **Flakes:** the nightly flake hunt (exists, ring 3) runs every .NET test project 10 times on
  each hosted OS and files what it finds (§5).
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
   `docs/protocol/examples`) belongs to that project too. A suite that ships a project rather
   than testing one names it in `builds:` (`app-package`: `src/AgentBridge.App`), and whatever
   that project's build takes in selects it the same way: the protocol's example sessions,
   embedded in App.Core for the demo the app's self-test runs, once changed the app without
   selecting its package (#487). For example, a change to
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

   Folders, references and `triggers`/`global_triggers` match in any case, as the Windows
   build does: it compiles `src/agentbridge.host/X.cs` into `AgentBridge.Host`, which an exact
   match left to `app-package`'s `src/**` trigger alone, without Host's tests. `no_test_paths`
   stay exact, since they take tests away: a path spelled otherwise is selected like any other.
3. **Everything else:** each suite's `triggers` globs in the catalog (`pwa/**`, `apple/**`,
   `infra/**`, `build/**` and so on).
4. **Always:** the sentinels (`select: always` at that ring). Everything runs on
   `global_triggers` (the solution, NuGet config, workflows and their actions, `quality/` itself,
   and the selector and its tests). When every
   file is in `no_test_paths`, only the catalog's `docs-links` suite runs; there's no hidden
   docs logic in the selector.
5. **Fail safe:** a file that no rule claims runs the full ring. A ring-1 check fails if any
   tracked file has no rule, so new folders get a rule when they're added.
6. **Output:** JSON with the suites, the OS matrix and **why**, each suite naming the files
   that pulled it in. The PR summary shows that table, so a reviewer can see what didn't
   run and why.
7. **GitHub mechanics:** ci.yml's `plan` job checks the catalog, then runs the selector through
   `quality/plan.ps1`: ring 1 on a pull request and on `main`, and on a draft the fast ring
   (`-FastRing`: ring 0, Linux only, §2).

   **Main: first attempts coalesce, re-runs run apart.** First attempts of pushes to `main` share
   the concurrency group `ci-main`, which never cancels a run in progress: at most one of them
   runs and one waits, and a newer waiting push run cancels the one waiting before it starts. A
   re-run (attempt 2 on) never joins `ci-main` but gets its own run-id group, like a run by hand,
   so it runs beside them and can't take a waiting push run's place and leave that push's commit
   untested (#938's review). GitHub promises no
   order between push runs, and nothing relies on one: the merge rule passes over a replaced
   run only when the newest run's commit builds on its commit, and otherwise, or when the newest
   run itself was replaced, holds main not green until that run is re-run (item 9).
   So a main run's change is everything since the last commit main's CI
   passed on: the plan reads `ci.yml`'s passed push runs on `main` (the newest 100, through the
   `actions: read` token), takes the newest whose commit is an ancestor of this one, fetches
   the whole history and diffs from there, so the changes of commits whose own run was replaced,
   or went red, are tested with this one. A base older than needed only tests more; none found,
   or the runs unreadable, runs the whole ring, never "no change". A docs-only push to `main`
   runs what docs select (`light`'s link check, say), not the app or the product's builds.
   Pull requests keep cancelling a superseded run, and a run by hand its own group.

   **What decides selection doesn't select for itself (#487).** Before the selector is built,
   the plan job compares the change (`HEAD^1..HEAD` on a pull request; on `main`, since its last
   passed commit) with a fixed list in ci.yml: `.github/`, `quality/`,
   `tools/AgentBridge.Quality/`, `tests/AgentBridge.Quality.Tests/`, `build/`, and any
   `Directory.*.props` or `.targets`, `*.sln`/`*.slnx`, `global.json` or `nuget.config`. A ready
   pull request or a push to `main` that touches one runs the whole ring: its `whole-ring` output, written by a step
   of its own that nothing from the change runs in, makes every gated job and every
   suite-gated step run, `product` test the whole solution on all three runners, and the plan
   select ring 2 (with ring 1's choice beside it). Otherwise a selector or catalog under change
   would choose its own tests: a bug in `ChangedFiles`, or one line moving `quality/**` into
   `no_test_paths`, could pass a gate that ran nothing. A draft keeps its fast ring, whose gate
   isn't the required check. The paths come from `git diff -z`, NUL-separated, and any path that
   still looks quoted or holds a control character counts as touched: one-per-line output quotes
   such a path, and `tools/AgentBridge.Quality/a"b.cs` once left the whole ring off.
   `.github/CODEOWNERS` asks the owner to review changes to every path on that list.

   **What this doesn't close.** On `pull_request` GitHub runs the pull request's own ci.yml, so a
   change to `.github/**` can edit this list, the whole-ring step or quality-gate's checks along
   with anything else. Such a change stays under the standing rule: an independent
   workflow review, and a check that its plan ran ring 2, before it merges. The structural guard
   is #524's `merge-rule` workflow, which runs on `pull_request_target` from the base branch's
   code, so a pull request can't rewrite it.

   The check wants every job in every workflow file named
   in a suite's `where:`, so a temporary workflow (a CI loop, say) fails it, and with it the plan:
   nothing else runs and `quality-gate` fails until the catalog lists it. Loop runs belong on a
   throwaway branch with no pull request, triggered by `push` to that branch (`workflow_dispatch`,
   for reruns with `gh workflow run --ref`, fires only once the file has run or is on `main`), or
   listed in the catalog with `status: planned`. It also checks ci.yml's wiring to the plan, which
   only names hold together: every job whose `if:` reads the plan has exactly the one gate shape
   (its own output, and it runs in the whole ring whatever the plan says); the plan
   step's `JOBS`, the jobs gated on the plan and the plan's outputs, bound to the steps that
   compute them, agree; every check job in ci.yml is in `quality-gate`'s needs (all but the
   delivery jobs: `publish-gate`, `publish`, `attest`, `relay-image-sarif`, each pinned to the one
   job it follows), and its `if:` is
   `always()`; `publish-gate` needs `quality-gate` and requires its success as a top-level
   condition, and `publish` needs `publish-gate` with exactly the condition
   `!cancelled() && needs.publish-gate.result == 'success'`, its concurrency exactly
   group `publish-prerelease`, `cancel-in-progress: false` and `queue: max` (item 7), and no
   other concurrency setting `queue`; the plan's suites and
   whole-ring reach steps only as `SUITES` and `WHOLE_RING`; and each step that reads the plan's
   suites names suites the catalog has, whose `where:` names that job explicitly, in the one
   condition shape that runs in the whole ring whatever the plan selected, with an
   OS guard only for an OS its job runs on (`product`'s runners are pinned to all three there).
   The plan writes the "what runs and why" table to the run's summary, and outputs:
   - a true or false per job, from the selected suites' `where:`. So the catalog, not ci.yml,
     says which suites a job runs, and moving a suite moves its trigger. On a pull request and
     on `main` a job runs only when it's true or the whole ring runs;
   - the selected suites, which gate the steps inside `product` (the AOT injector and hook,
     the build scripts, the relay container) and inside `light`: when the whole ring runs,
     every one of those steps runs;
   - `product`'s test projects, tested through a solution filter, still under
     `dotnet-coverage` with TRX and `--blame-hang`. When the whole ring runs, it's
     `AgentBridge.slnx` as before. The whole solution is always built, since projects no
     test references (the benchmarks) are compiled nowhere else on a pull request;
   - `product`'s runners, from its selected suites' `platforms` (all three when the whole
     ring runs; only Linux on a draft);
   - the test filter `product` adds: ring 0's on a draft, none otherwise (ci.yml passes it
     on only for a draft pull request, whatever the plan says).

   The jobs, and what each one's for (#459):
   - `light` holds the small Linux checks on one runner: `design-gen`, `a11y-contrast`,
     `pwa-unit` with its coverage pass, `pwa-site-csp`, `playwright-apt` and `docs-links`. Each step runs when its
     suite is selected, and every selected one runs even after another fails. It isn't folded
     into `plan`, since a failure there would skip every job that needs the plan.
   - `pwa-ui` runs Playwright on four runners: Chromium in one, WebKit (about twice as slow) in
     three `--shard`s, with `--fully-parallel` so a shard splits by test rather than by file.
     `pwa-e2e` runs `test/e2e`, in both browsers, in the one job that builds the dev stack. The
     shards set `PWA_SKIP_E2E`, so `test/e2e` is left out of them rather than skipping itself;
     together the five run every spec. The one PWA job these replace took a median of 15
     minutes. Every job that runs `playwright install --with-deps` (each `pwa-ui` leg, `pwa-e2e`
     and `spikes-linux`) first downloads Playwright's system packages through
     `.github/actions/playwright-system-packages`, and if apt's first mirror hasn't finished in
     five minutes it fetches the rest from the runner's Canonical fallback,
     `https://archive.ubuntu.com` (main run 41927187905 lost a WebKit shard to that mirror at
     100 kB/s, and main run 41952998347 `pwa-e2e`). Each of those jobs gets 35 minutes, so a
     fallback download still leaves time for the tests; the browsers' own download, on a cache
     miss, is unchanged. To prove that fallback once without waiting for a slow mirror, once this
     `workflow_dispatch` trigger is on `main` (until then a branch's run by hand may not
     start): make a new commit of its own on a throwaway branch from `main` (an empty one,
     `git commit --allow-empty`, with the usual trailers), push the branch, never open a pull
     request at that commit, and run
     `gh workflow run ci.yml --ref <branch> -f force-apt-fallback=true`. The plan job fails any
     run by hand, before checkout, that doesn't ask for the fallback, runs on `main` or a tag,
     or runs `main`'s head or any open pull request's: its checks, `quality-gate` among them,
     land on the commit, and the merge rule reads the latest run per head, so on a pull
     request's head they would stand in for its own run. One started there by mistake still
     leaves a failed CI run on that head, and since the merge rule takes the newest run,
     re-running the pull request's older run doesn't clear it: stop and report it, and recover
     only as Quality or Engineering authorizes. Each `pwa-ui` leg then takes the Azure mirror
     off before downloading anything (the action's `force-apt-fallback`; `pwa-e2e` and
     `spikes-linux` download as before), says in its log that it was forced and not timed out,
     fails if apt still fetches a package from Azure, and counts the packages that came from
     `https://archive.ubuntu.com`. The WebKit legs are the proof: one that counts none fails;
     Chromium's, whose packages the runner may already have, only warns. Read each WebKit leg's
     count and its `Get:` lines before calling the fallback proven. Those two checks, the Azure
     fetch and the Canonical count, belong to the forced proof alone: a fallback after a real
     five-minute timeout, on any run, is the one above, unchanged (#817 first proposed failing
     that one on an Azure fetch too, and dropped it). A run by hand runs every
     job (the whole ring), but is never a release and writes no cache.
   - `spikes-linux` is S2's and S4's checks, on one runner since both install the same browsers.
     It runs only when `spikes/` changes, like `spikes` on Windows.
   - Caches (#468): NuGet packages for `product`, `ha-integration` and `spikes`, keyed by job,
     runner and every `*.csproj`, `Directory.*` file, `nuget.config`, `global.json` and the
     tool manifest; Playwright's browsers for `spikes-linux`, `pwa-ui` (per browser) and
     `pwa-e2e`, keyed by Playwright's version. Only `main` saves a cache, so a pull request can't
     fill the repository's 10 GB or seed what main restores; a pull request restores main's, and
     the nearest older NuGet key when its projects differ. `app` and `publish` restore nothing:
     the package main publishes is built from what the feeds serve.
   - CodeQL on a pull request analyses only the languages the change reaches (`codeql.yml`
     `languages`, #464): C# for `.cs`, `.csproj`, `Directory.*` and the like; JavaScript for
     scripts, pages, npm manifests and `pwa/`; Actions for `.github/`. `main` and the weekly
     run analyse every language. CodeQL and dependency review skip drafts and run on ready for
     review; CodeQL, Dependencies, Workflow lint and Apple clients each cancel a superseded run
     of the same pull request, as ci.yml does. Only pull requests: these workflows group each
     push run on `main` by its own run id, so none is cancelled. ci.yml's push runs on `main`
     never cancel a run in progress either, but a first attempt still waiting can be replaced in
     `ci-main` (item 7). That matters beyond publishing: a cancelled push run would be the latest
     finished one merge-rule's main_green reads, and holds every pull request as if main were red
     unless it's provably such a replaced waiting run (item 9).

   A final `quality-gate` job (`if: always()`) needs all of them. It fails if any failed or was
   cancelled, and if any job the plan selected didn't succeed: when the whole ring runs, that's
   every job; otherwise, on `main` as on a pull request, every job the plan gave true (and a
   plan that passed must have given each job, and `whole-ring`, exactly true or false). A job the plan
   skipped is fine. It's the required check (on `main` since 2026-11-03, with `merge-rule` beside
   it in main's ruleset, item 9), so skipped jobs never leave a pull request waiting.
   `publish-gate` needs `quality-gate` and its success, rather than a second copy of its list
   (#512), so on `main` every ci.yml check job the plan selected must have run and passed before
   a release publishes, and a selection miss, or any red suite, is caught first. It also needs
   this run to have built the app (`quality-gate`'s `app` output, both pinned by the catalog
   check): a main run whose change left
   the app alone publishes nothing. Release order doesn't rest on the order main's runs go in,
   which GitHub doesn't promise: `publish` runs one at a time, and inside it
   `publish-prerelease.ps1` publishes each version (the run number) once, never one older
   than any published, and never from a commit that doesn't hold the latest release's commit
   (a run number says nothing of commit order; behind it publishes nothing, and diverged or
   unreadable stops publishing). So a run publishes even after `main` has moved on (an app
   change followed by a docs-only commit is released by the first's run), unless main's head,
   read under the lock, has since changed the app by main's own ring-1 selector: then main's
   later run publishes both, since publishing the older first could leave the newer's lower run
   number refused (an unreadable or off-main head stops publishing). One reaching
   `publish` after a newer release publishes nothing. `publish` runs exactly when
   `publish-gate` succeeded (`!cancelled()`, pinned by the catalog check), since the implicit
   `success()` would carry a skipped check's skip down to it. `publish-prerelease` keeps every waiting
   publish (`queue: max`: up to 100, and one arriving to a full queue is cancelled, never one
   already waiting), since by default the next to arrive replaces the one waiting and a re-run
   of an old run could leave a newer run's app unpublished; GitHub doesn't promise the order
   waiting publishes start in, so the checks above keep the order. actionlint doesn't know
   `queue` yet: workflow-lint.yml lets that one finding through there alone (#951). CodeQL,
   perf and the relay
   workflow run apart and don't hold a release.
8. **Keeping the selector honest:** a whole-ring run (ring 2, every gated job) also says in its
   summary which jobs ring 1 chose for the same change. For every ring-2
   failure, a nightly job asks whether ring 1's selection for that PR would have included
   the failing test. A **selection miss** is a bug in the catalog. It gets an issue, and the
   rate is a tracked metric (target: zero).
9. **The merge rule, enforced (#452):** main's ruleset ([`ruleset-main.json`](ruleset-main.json))
   requires `quality-gate` from GitHub Actions and `merge-rule` from the merge-rule GitHub App,
   plus resolved threads, squash merges and no force pushes or deletion, with no bypass, and asks
   Copilot to review each push. Auto-merge has been on since 2026-11-04 00:50 PT: sessions turn
   it on once their pull request is ready, pinned to its head, and GitHub merges when both are
   green.
   - `merge-rule` is a commit status that main's copy of `.github/workflows/merge-rule.yml` posts
     on each open pull request's head, from `agent-bridge-quality merge-rule` (`MergeRule.cs`) and
     the rules in [`merge-rule.yml`](merge-rule.yml): main green, that is the latest finished
     push run of `ci.yml` on main (at its latest attempt) passed, unless the pull request has the
     `fixes-main` label; not a draft; no `hold` or `do-not-merge`
     label; a completed Copilot review whose commit is the head (#706: Copilot posts an error,
     a billing failure, a diff too large to review or a review that didn't run in full as a
     review by the same bot on the same commit, so only one that opens with its overview
     counts, and anything else fails, naming why, even an overview with findings below a
     notice that the agentic review timed out); CodeQL's latest run on the head green and
     code scanning's `CodeQL` check not failed; every other check run and status on the head
     passed (success, skipped or neutral), since `apple.yml`, `codeql.yml`, `relay.yml`,
     `dependencies.yml`, `workflow-lint.yml` and `perf.yml` sit outside `quality-gate`, each
     workflow counting by its latest run on the head; and, when a changed path is in
     `sensitive_paths` (or is one GitHub couldn't decode, or holds a control character), or the
     title starts with `Security` or the `security` label is on, an `independent-review` status
     on the head whose latest from a listed poster is success. The files are those of the exact
     commit judged (`compare/main...<sha>`), not the pull request's latest. Pending while
     Copilot, CodeQL or a check is on its way, failure otherwise, with the missing piece named in
     its description and the run's summary. Pull requests that
     share a head get the worst of their verdicts, since a status belongs to the commit.
   - Main must be green, since auto-merge doesn't pause while main's CI is red and would keep
     landing pull requests on a broken main, which makes the breakage hard to untangle. Each run
     reads main once: `ci.yml`'s push runs on main, newest first, judged by the latest that
     finished, so a newer one still going doesn't free a red main until it passes; a finished run
     being re-run counts as not green (its last finished attempt was almost always red). A run
     cancelled while still pending, replaced in `ci-main` by a newer push before any job of it
     started (a first attempt with a newer run listed, whose jobs, every one read, have no
     runner and no steps, and whose commit the newest run's commit builds on, by GitHub's
     compare), tested nothing, and the newest run tests its change, so it is passed over for the
     finished run before it, red or green; any other cancelled run (the newest included), or one
     whose jobs or compare can't be read, still counts as not green: re-run it. A run that passed
     doesn't vouch for a later commit (by main's compare from it) whose run, older by id since
     runs aren't promised to be created in push order, hasn't passed, nor for one whose run isn't
     found: a later commit with no run on the first page of 20 is looked for on up to four more,
     and one still not found (further back, or never run, as a commit inside a push of several)
     holds main not green until a run on main's head passes. While it
     isn't green, every pull request fails, its description starting `Main is red (CI <sha>)`;
     main unread, or with no finished run, fails closed. The fix itself gets the `fixes-main`
     label (`main_green.exempt_labels`) from the merge coordinator, since labels take triage
     rights: it skips this check and nothing else, the independent review included. CI finishing
     on main starts a run, so verdicts follow main to red and back.
   - The standing rule (owner decision 2026-11-05, #955): Copilot reviews only the security PRs
     Quality spends the monthly included requests on. A PR labelled `copilot-waived`
     (`copilot_waiver.labels`; only the merge coordinator adds it) may merge without a Copilot
     review if it has a success independent-review receipt on its head from a listed poster. A PR
     that needs that receipt (a sensitive path, a security mark, or an incomplete file list) also
     needs a second success receipt, `copilot_waiver.second_review` (`independent-review-2`), from
     a listed poster on the same head, and the two receipts' descriptions must each name a model
     whose family the other doesn't share (`claude-opus-5.5` and `gpt-6.1-sol`, say). A sensitive
     file in `copilot_waiver.one_review_paths` (`docs/status.md`, whose row the receipt checks
     against its decision) doesn't call for the second. A completed Copilot review on the head is
     itself the other family, so with one there the label changes nothing. A failed second receipt
     from a poster blocks like a failed first one, whether or not the label is on. Without what it
     needs, the label fails and names the missing receipt; it lifts nothing else, and the verdict
     says `Copilot review waived (label)`.
   - A pull request can't loosen its own gate (#487, Finding 1): every trigger
     (`pull_request_target`, `status`, `workflow_run` of every other workflow, `check_suite`, a
     10-minute `schedule`, `workflow_dispatch`) runs main's copy of the workflow, which checks out
     and builds main and reads the pull request only through the API. Each run judges every open
     pull request, one run at a time, so a run starting turns a head pending, a late red flips the
     verdict, and no stale read lands last.
     Changing the rule means changing `quality/`, `.github/` or the tool,
     all sensitive, so it takes an independent review and only applies once merged. Reviews reach
     it through a relay workflow (`merge-rule-review.yml`), since `pull_request_review` runs the
     pull request's copy. The schedule is the backstop, Dependabot's pull requests included.
   - CodeQL is judged here, not by a `code_scanning` rule: since #464 a pull request whose
     languages CodeQL skips uploads no results, and that rule would wait for them forever.
     A CodeQL run that ended `skipped` (a pull request opened seconds before `ready_for_review`,
     say) fails closed, and stays failed until CodeQL runs again on the head: start one with
     `gh workflow run codeql.yml --ref <branch>`, or close and reopen the pull request.
   - Locally, `dotnet run --project tools/AgentBridge.Quality -c Release -- merge-rule --pr N`
     (or `--all`) prints the verdict and posts nothing; only `--post` posts. Run it from a
     freshly fetched `origin/main` checkout (`git fetch origin`, then `git switch --detach
     origin/main` in a worktree kept for it), never a pull request's worktree: it builds and
     reads that checkout's evaluator and `quality/merge-rule.yml`, so a pull request's own would
     judge it. It prints the commit they came from, and refuses unless that's `origin/main`'s
     with no local changes to them (`--allow-non-main` judges anyway, with a warning).
   - A head whose facts can't be read gets `error`, which fails the required check like failure,
     so an earlier success doesn't stand; a hold label, read with the open list, still fails it.
     When the open list itself stops partway, no head gets success from that run: a held pull
     request on it could be among the unread.
   - Budget: `GITHUB_TOKEN` has 1,000 GraphQL points an hour for the repository. A run costs
     about one point per page of the open list plus one per pull request that isn't already
     failing (15 points and 13 REST calls with 25 open on 2026-11-03), plus one REST call for
     main's CI, and prints what it spent. While main is red, every pull request but a
     `fixes-main` one is already failing, so a run costs less.
     Runs go one at a time, and an event-driven run waits 45 seconds first so a push's burst of
     events folds into the one run queued behind it: at about 2.5 minutes a run, at most 24 an hour,
     so under 400 points an hour at worst and nearer 150 usually (the schedule's 6 plus events).
     Below 200 points left, a run warns in its log.
   - The receipts and the status are honest-mistake guards for sessions sharing the owner's
     account, not a defence against that account: it can post any status. A workflow added by a
     same-repo pull request could, with `statuses: write`, post its own `merge-rule` from GitHub
     Actions, which the ruleset couldn't tell from main's. So auto-merge stayed off, and the merge
     coordinator merged by hand, until a GitHub App with only `statuses: write`, whose key only
     `main` can reach (an environment), posted `merge-rule` and the ruleset was pinned to that App
     ([docs/merge-rule-app.md](../docs/merge-rule-app.md)); both hold since 2026-11-04.

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

**Behind a firewall that blocks api.nuget.org**, set `AGENT_BRIDGE_NUGET_SOURCE` to a NuGet
mirror's `index.json` before running it:

```
$env:AGENT_BRIDGE_NUGET_SOURCE = 'https://<mirror>/nuget/v3/index.json'
pwsh quality/run.ps1 -Explain
```

run.ps1 then writes `tools/AgentBridge.Quality/obj/nuget.mirror.config` (git ignores `obj/`),
with the mirror as the only package source and the only audit source, and every restore it does
uses that file: the selector's build and each suite's. NuGet's vulnerability audit stays on and
reads the mirror. Overriding only the package sources (`--source`) isn't enough: audit still
tries nuget.org from `nuget.config`, and its NU1900 warning is an error here. Unset, nothing
changes, and the committed `nuget.config`, which CI uses, keeps nuget.org.

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
  - The CardTests chunked large-file case includes phase evidence in its existing timeout
    (#513): attachment start, chunk bytes/count and timings, reply receipt, client-observed
    acknowledgement, and terminal UI state. The last phase distinguishes pipe progress from
    UI settlement; it does not extend the wait or replace delivered/byte assertions.
    UI settlement uses the view model's status/tone, independently of the delivery state.
- **Flakes:** a test that passes and fails on the same code is flaky. It's reported, never
  silently retried to green. The flake record is the `flaky` label and its issues, one per test,
  each with its run links and counts.
  - **The nightly flake hunt** (`flake-hunt`, ring 3, [flake-hunt.yml](../.github/workflows/flake-hunt.yml),
    #509) finds them before they turn a pull request or `main` red. At about 02:30 PT it runs
    every .NET test project 10 times on `ubuntu-latest`, `windows-latest` and `windows-11-arm`.
    Each run tests the whole solution at once, as `product` does, since the flakes come from CI's
    own load: a first try that ran one project at a time on a runner of its own saw none in 330
    runs, on a day CI hit five. Each run leaves a TRX file per project, and
    `agent-bridge-quality flakes` then classifies every test, per OS:
    - **flaky:** failed some runs and passed others. It gets an issue labelled `flaky` with the
      project, the test, the failures per OS, the first error and the run link. When one is
      already open (opened by the hunt and marked for the test, or filed by hand by the
      repository's owner with `Flaky` in its title and the test's `Class.Method` in it), the hunt
      comments the new counts on it and labels it `flaky`. Anyone else's issue is never taken for
      a test's record, whatever it says (#542);
    - **failing:** failed every run on some OS. That's a real failure, not a flake: it gets an
      issue labelled `bug`, and the run fails;
    - **passing:** never failed.

    A run in which a project leaves no results, or dotnet test fails with no test failed (a
    crash, or a hang that `--blame-hang` ended), also fails the hunt, and is named in its summary.
    No more than 10 new issues are opened in one night;
    more than that is something systemic, read once in the summary. Only the `report` job may
    write issues: it runs on `main` only and executes `quality/publish-flakes.ps1` from `main` on
    the plan, read as data. The summary also gives the runner minutes the night cost.
  - **To confirm a fix** on every OS, run it by hand, narrowed and longer, from a branch, where it
    only says what it would file:
    `gh workflow run flake-hunt.yml --ref <branch> -f runs=50 -f projects=AgentBridge.Host.Tests -f filter="FullyQualifiedName~AnswerFlowTests"`.
  - Nothing is skipped without the owner's say-so (AGENTS.md: never weaken tests).
  - The `--blame-hang` setting stays in place.
- **The weekly report** (`weekly-report`, [weekly-report.yml](../.github/workflows/weekly-report.yml))
  is how the flake record gets read, so flakes are fixed rather than re-run. Every Monday at 08:00
  PT, `agent-bridge-quality report` reads the past 7 days from GitHub and the workflow comments it
  on the quality epic, #357, as short tables:
  1. **Flakes:** open `flaky` issues ranked by the hunt's failures this week, with their OSes and
     when last seen; new and fixed ones; `main`'s CI runs that went red, split into flakes (green
     on a rerun of the same commit) and those still red, with how many of those the next commit's
     run turned green (most likely flakes no one reran); and the 5 tests that failed most, from the
     logs of up to 30 failed CI test jobs (half `main`'s, half pull requests', newest first) and
     the hunt's tallies.
  2. **Throughput:** pull requests open, opened, merged and closed; the median and 90th
     percentile from open to merge and from ready to merge; those open more than 7 days.
  3. **Where open pull requests are stuck:** counted by the first item `merge-rule`'s description
     names (draft, held, Copilot review, independent review, a red check, CodeQL...), passing over
     a red `main`, which it names first on every one; then, for one `merge-rule` passed,
     conflicts, unresolved threads or a red `main`; and per head-branch
     prefix (`codex/`, `perf/`, `quality/`...), the open ones, how deep they stack, and their
     merges this week, to see whether pull requests per session and stack depth slow merging.
  4. **CI health:** the median wall time of the last 50 completed pull request runs of `ci.yml`,
     and their 3 slowest jobs.

  Its cost is bounded and printed at the end: a few paginated GraphQL queries and REST lists, a
  jobs list for each earlier attempt of a run rerun to green (every such `main` run, and up to 15
  pull requests'), and at most 30 job logs. Only the `post` job may write issues,
  only on `main`. A pull request that changes the reporter or the workflow runs it as a dry run,
  and `gh workflow run weekly-report.yml --ref <branch>` (a dry run by default, and always off
  `main`) does too: either writes the report to the job summary only, and
  `dotnet run --project tools/AgentBridge.Quality -c Release -- report` prints it locally.
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

The contract agreed on #357 and #736 (ownership, v1 and v2 coverage, `result.json`, outcomes,
lifecycle, evidence) and what of it exists are in [lab/README.md](lab/README.md), which wins
where this section differs. Of the pieces below, only a draft of the provisioning step exists
(`quality/lab/provision.ps1`), and it refuses to apply.

- **`quality/lab/fleet.yml`:** the machines, by role. Each image is built once and
  snapshotted as S0; every run makes its own disk from S0 and deletes it after. v1 needs
  `win-x64` and native `win-arm64` only.

  | Role | Image | Used by |
  |---|---|---|
  | `win-x64` | Windows 11 x64 | E2E, agent drift, perf, rehearsal |
  | `win-arm64` | Windows 11 Arm64 (Azure Cobalt or Ampere) | E2E |
  | `win10` | Windows 10 22H2 x64 | Compatibility |
  | `linux-x64`, `linux-arm64` | Ubuntu 24.04 | Host E2E, agent drift |
  | `ha` | VM-W's role: disposable Home Assistant and Mosquitto | HA E2E and soak |
  | `perf` | A fixed-size VM kept only for benchmarks | §3.2 comparisons |
  | `macos` | See §9: hosted `macos-latest`, a self-hosted Mac mini, or EC2 Mac | macOS host and app E2E |
- **`quality/lab/run.ps1 -Subscription <id> -Role <r> -Build <artifact> -Scenario <s> -Relay <staging|ppe|url>`:**
  make the run's VM from S0, install the build under test from the CI artifact, run the
  scenario, collect artifacts (logs, transcripts, Hyperloop recordings), then delete the VM.
  The subscription and relay are always named, never defaulted. It runs from a workflow
  through OIDC, like `relay.yml`, with a `lab` environment and its own principal scoped to
  `rg-agent-bridge-lab` only. Nothing in it can reach the owner's machines, Home Assistant or
  broker.
- **Accounts:** for v1, `Slipgates-lab-bot`, a dedicated, revocable GitHub account that is
  never the owner's identity, which the owner alone signs in to the relay by GitHub device
  flow once per lab VM, or once on a golden image if one is separately approved (owner
  decision B3, its credential plan superseded by Trust's choice A,
  [lab/README.md §8](lab/README.md#8-outcomes-retries-and-credentials)). No GitHub token for
  it is a `lab` environment or vault secret. For v2, test accounts for each agent, with
  per-agent caps (the soak already caps Codex). No credential is ever printed, as AGENTS.md
  requires.
- **Cost guard:** cost and age alerts on the resource group (SRE). Every run deletes its
  resources in a `finally`, but that is best effort, so a durable sweeper deletes run-owned
  resources past their expiry (at most 4 hours) before any run may start, by exact name from
  the inventory each run is registered in before its first create
  ([lab/README.md §4](lab/README.md#4-machines-and-their-lifecycle)).
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

State (2026-11-04): the lab's contract is agreed and written down
([lab/README.md](lab/README.md)); Platform owns items 1, 2 and 4, SRE the PPE, cut and
promotion they feed (#590, #451). Built: the nightly flake hunt (item 6, #509). Partial: a
draft of item 1's provisioning step that refuses to apply, checked against a stand-in `az`
(`lab-scripts`); `lab-e2e-agents` only as the shadow soak. Unbuilt: the rest of items 1–5.
What blocks the first run is listed in [lab/README.md §10](lab/README.md#10-before-anything-runs).

1. `quality/lab/`: fleet, `run.ps1` and the `lab` OIDC environment, starting from the
   rehearsal scripts. Add Linux, Arm64 and Windows 10.
2. Simulated agents (`tests/AgentBridge.AgentSim`) and `lab-e2e-simulated` on every OS.
3. `agent-drift` nightly.
4. `lab-e2e-agents`: the soak driver as a scenario runner on test accounts.
5. `perf-host` corpus replay and `perf-relay-load` on staging; `relay-dast`; fuzz targets.
6. The flake record (the nightly flake hunt, done in #509, read each Monday in the weekly
   report on #357) and the selection-miss report.

**Phase 4: releases (weeks 8–10)**

State (2026-11-04): promotion is unbuilt. SRE owns the cut, freeze and promotion (Dev to Beta,
#451) and PPE (#590). Lab results count toward Beta1 only as v2 ([lab/README.md §3](lab/README.md#3-coverage)).

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
5. **Required checks.** Decided 2026-11-03 (#357, #452): `main` requires `quality-gate`, then
   also `merge-rule`, which holds the rest of the merge rule and CodeQL (§4.9), through a
   ruleset with no bypass, and sessions use auto-merge, on since 2026-11-04 00:50 PT now that the
   merge-rule App posts `merge-rule` (§4.9). Open: when each advisory gate (diff
   coverage, axe, perf) becomes required.
6. **Lab budget.** Decided 2026-11-04 (#357 B1): $75 a month for `rg-agent-bridge-lab`, with
   both Windows roles nightly. Execution and spend are not yet authorized
   ([lab/README.md §10](lab/README.md#10-before-anything-runs)).
