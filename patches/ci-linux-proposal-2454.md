# Cutting Linux hosted load on main, and a self-hosted Linux option (#2454)

Proposal only, for Eng R8b and the Chief. Nothing here is built.
The data comes from the jobs API on main's push runs: 23 runs since #3855 (per-commit concurrency) landed at 08:58Z, plus 12 earlier runs. No logs were read.

## 1. What Linux costs per main run today

There are 15 complete, uncancelled main runs. Each ran a median of **16 Linux jobs** for **80 job-minutes**.

| Linux job | Ran on main | Median min | Job-min per run |
|---|---|---|---|
| pwa-ui webkit (3 legs, 4 with the shard patch) | 45/45 | 10.4 | **32.5** |
| pwa-ui chromium (3 legs) | 45/45 | 5.7 | **19.2** |
| product (ubuntu-latest) | 15/15 | 10.9 | 11.4 |
| light | 15/15 | 5.7 | 6.2 |
| ha-integration | 15/15 | 3.4 | 3.5 |
| pwa-e2e | 15/15 | 3.5 | 3.5 |
| spikes-linux | 15/15 | 2.8 | 2.8 |
| plan, relay-image-sarif, quality-gate, publish-gate, sign, attest | ~15/15 each | 0.1 to 0.6 | ~1.1 together |

Queue since per-SHA: Linux jobs wait **4.3 min median, 12.9 p90** (267 jobs), and 19 Linux jobs were still waiting when read. Before per-SHA the median was 0.0. Main is starting about 12 runs an hour at peak (23 in about 1.9 h), with a median of 6 other main runs in flight when one starts, and up to 12 at once. At 80 Linux job-minutes a run, that is roughly **16 Linux runners busy on average**, against the account's hosted concurrency cap. Windows shares that cap; confirm the plan's number before relying on it.

**Every Linux job ran on every main run.** The plan selects nearly everything on main. Its base is "the newest main commit whose CI passed", and with a dozen runs in flight and some red, that base lags several commits, so each run re-tests its neighbours' changes as well as its own.

## 2. Cuts, largest first (Linux stays hosted)

| # | Change | Linux job-min per run | Risk and who decides |
|---|---|---|---|
| A | **Selection base on main: the first parent while its own run is still going.** A main commit whose run is in progress will be tested by that run (per-SHA never cancels), so the next run tests only its own diff. A failed or cancelled parent still carries its diff forward, as now. | From about 80 to the PR-selected set. A PWA-only commit runs ~52, a docs-only one ~1 (estimate; the change mix decides) | Medium: plan.ps1 is the gate planner. Quality signs the argument; one plan-log read can confirm whether main is running the whole ring or a wide diff today. |
| B | **WebKit off the push path.** It is already the post-merge tier and doesn't gate merges (#3209). Run it hourly on main's head, and on the release train before a publish, instead of on every push. | -32.5 (-40%) | A WebKit break is found within the hour, not within the run. **Owner decision.** |
| C | **Chromium on main only when selected**: this follows from A. A main push with no PWA change skips the three legs. | -19.2 on non-PWA commits | Low, once A is in. |
| D | **Fold the tiny serial tail into fewer jobs.** quality-gate, then publish-gate, then sign, then attest each wait about 4 min for a hosted slot to run for under a minute. publish-gate becomes a step at the end of quality-gate; sign and attest become one job. | About 0.3 job-min, but up to about 8 min less wall time on main's tail | Low; CiPlan pins the job structure, so the change includes its pin update. |
| E | **Merge spikes-linux into light** (both short, no Docker). | -0.5 (one setup) and one fewer queue slot | Low. |

Recommended order: **D and E** (small, safe), then **A** (biggest structural win, with Quality's sign-off), then **B** only if the owner wants WebKit hourly. A and C together keep coverage the same and remove re-testing, not tests.

## 3. A self-hosted Linux option ("Windows now, Linux later")

- **Which jobs:** the PWA UI legs only (chromium and webkit, about 52 of the 80 job-min). They need no Docker, no secrets, and no network beyond npm and Playwright's mirror. product (ubuntu), ha-integration and pwa-e2e (Docker, dev stack) stay hosted.
- **Which machine: a dedicated Linux VM, not WSL on VM7.** WSL shares the VM7 host's CPU with its 8 Windows runners, and by default it mounts the Windows drives (`/mnt/c`) and can start Windows processes. On a machine that also runs fleet sessions, a job could read their files. Turning that off in `/etc/wsl.conf` (`[automount] enabled=false`, `[interop] enabled=false`) helps, but a separate VM is a cleaner boundary and doesn't compete with ab-ci.
- **How many:** at peak, about 12 runs/h × 52 PWA job-min ≈ 10 runners busy on average. Start with **3 VMs × 4 runners (12)**, 4 vCPU per runner: Playwright uses 2 workers per leg, and a third made WebKit slower. Run `ab-ci-capacity` (#3878) against the Linux label too, and add a VM if the queue grows.
- **Expected win:** the PWA legs' 52 job-min per run leave the hosted pool. Hosted Linux falls to about 28 job-min per run (about 6 runners on average at the peak rate). With Windows x64 on ab-ci as well, the shared hosted cap stops being the limit, and queue wait should return toward the pre-per-SHA 0 to 1 min. That is an estimate; measure as for Windows.
- **Security:** the same rules as ab-ci.
  - The exact fork guard plus a repository variable of its own, for example `AB_CI_LINUX_SELF_HOSTED`, routed only for a push or a same-repository pull request, never forks or `pull_request_target`.
  - Labels `[self-hosted, linux, x64, ab-ci-linux]`; RunnerRouting's fail-closed check already rejects any unguarded self-hosted label.
  - Per-job wipe: an ephemeral runner, or `git clean -ffdx` plus `RUNNER_TEMP` caches, as CachePolicy requires.
  - **No Docker socket shared into jobs** (no Docker on these VMs at all, since the PWA legs don't need it).
  - Playwright's browsers and apt packages baked into the VM image, so there is no per-job install (about 0.5 min saved per leg).
  - A runner account with no credentials of the fleet's. The VMs run no fleet sessions.
- **Lint:** no change to the rule. A second guarded value (the Linux label list) needs its own guard variable, and RunnerRouting would gain that variable as an allowed alternative, as `use_ab_ci` was added.
