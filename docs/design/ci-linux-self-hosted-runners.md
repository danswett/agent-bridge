# Self-hosted Linux runners for the PWA's browser legs (design)

Part of #2454. **Design only**: this changes no workflow, registers no runner and builds no machine.
The owner decided "Windows now, Linux later" (relayed by the Chief). This is the "later", for the owner to accept or decline, and for SRE and Quality to read first.
It follows the Windows pool's design (quality/README.md, "CI: speed, runners and what gates main") and SRE's install style (slipgates-fleet `setup/README-ab-ci-runners.md`, fleet #1110).

## Why

From the jobs API, 23 main runs after per-commit concurrency (#3855) landed, Nov 23:

- A main run uses **16 Linux jobs, 80 job-minutes**. The PWA's browser legs are **52** of them: WebKit 32.5 and Chromium 19.2.
- Linux jobs wait **4.3 min median, 12.9 min p90** for a hosted runner. Before #3855 they waited 0.
- For scale, the three Windows on Arm legs (`app (win-arm64)`, `app-installer (win-arm64)`, `product (windows-11-arm)`) use **~38 job-minutes a run** on hosted Arm runners. That pool is separate and this design does not change it.
- Main starts up to ~12 runs an hour, with a median of 6 in flight at once. At 80 Linux job-minutes a run that is about 16 Linux runners busy on average, against the account's hosted concurrency cap, which Windows shares.

Moving the browser legs takes ~52 job-minutes a run off the hosted pool. Hosted Linux falls to about 28 job-minutes a run. *Estimate:* with Windows x64 on `ab-ci` as well, the hosted queue should fall back towards 0 to 1 min. Measure after, as for Windows.

## Scope

- **On these runners:** `pwa-ui`'s Chromium and WebKit legs only. They need no Docker, no secrets and no service of their own, only npm, Playwright and its browsers.
- **Stay hosted:** `product (ubuntu-latest)`, `ha-integration` and `pwa-e2e` (Docker, dev stack), `light`, `plan`, the gate and delivery jobs, and everything that ships. A job that needs a secret never runs here.

## Machines

- **Dedicated Linux VMs, not WSL on VM7.**
  - WSL shares VM7's CPU with its 8 Windows `ab-ci` runners, so both pools slow each other down.
  - By default WSL mounts the Windows drives (`/mnt/c`) and can start Windows programs. On a machine that also runs fleet sessions, a job could read their files and credentials. `/etc/wsl.conf` can turn both off (`[automount] enabled=false`, `[interop] enabled=false`), but that is a setting one edit away from undone. A separate VM is a boundary.
- **Spec, per VM:** Ubuntu 24.04 LTS (the image `ubuntu-24.04` jobs expect), x64, 16 vCPU, 32 GB RAM, 128 GB SSD. An Azure size such as `Standard_D16s_v5` is an example; SRE prices and picks it.
- **Count:** 3 VMs × 4 runners = **12 runners**. Each runner has 4 vCPU and 8 GB, as a hosted `ubuntu-24.04` runner has. Playwright runs 2 workers per leg, and `ci.yml` notes that a third made WebKit slower on 4 CPUs.
  - At the measured peak (12 runs/h × 52 job-min) that is ~10 runners busy on average, so 12 leaves a little headroom.
  - Add a VM when the capacity check below starts failing. Don't add one by guess.
- **These VMs run nothing else:** no fleet sessions, no other runners, no credentials of the fleet's.

## Image (built by SRE, read-only to jobs)

Baked into the VM image, owned by root, not writable by any runner account:

- git, curl, ca-certificates, and Node 22 (jobs still run `actions/setup-node`, which then hits its tool cache).
- **Playwright's system packages for Chromium and WebKit**, at the Playwright version `pwa/package-lock.json` pins. Jobs then download no apt packages, which takes about 0.5 min a leg today. The runner account has **no sudo**, so a job *cannot* install system packages.
- **Playwright's browsers** for that pinned version, in `/opt/ms-playwright` (root-owned, read-only). Jobs set `PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright`.
  - When `pwa/` moves to a new Playwright, the job finds no matching browser. It downloads into `RUNNER_TEMP` instead and warns, until SRE rebuilds the image.
- **No Docker engine and no Docker socket.**

## Runners and accounts

- **Repo-level, persistent runners with a per-job wipe**, as on Windows (SRE's reasoning holds: an ephemeral runner needs something that holds a registration token to re-register it).
- **One local account per runner** (`abci-linux-1` … `abci-linux-4`). This is stronger than Windows, where all runners on a VM share one account. Linux accounts are cheap, and it means a job can't read another runner's `.runner` or `.credentials`.
  - Each account is non-login, has no sudo, and is a member of no group with write access outside its own runner folder and home.
- **Labels:** `self-hosted, linux, x64, ab-ci-linux`. These are distinct from Windows' `ab-ci`, so neither pool can take the other's jobs.
- **Per-job wipe**, by the runner's job hooks (`ACTIONS_RUNNER_HOOK_JOB_STARTED` and `ACTIONS_RUNNER_HOOK_JOB_COMPLETED`), before and after every job:
  - empty `_work` (except the tool cache) and `RUNNER_TEMP`;
  - reset the account's home to a fixed skeleton: remove `~/.npm`, `~/.cache`, `~/.config`, `~/.local`, `~/.bash*`, `~/.profile` and `~/.gitconfig`, then copy the skeleton back;
  - kill every process the account still runs.
  - The hooks are root-owned and read-only to the account.
- **Runner updates:** registered with `--disableupdate`, since the account can't write the runner binaries. SRE refreshes them as for Windows.

## Routing guard and its lint

- **The guard** is the Windows one with a variable of its own, so Linux can be turned on and off apart from Windows:

  `vars.AB_CI_LINUX_SELF_HOSTED == 'true' && (github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository)`

  Like the Windows guard, it may have exactly one alternative, a run by hand on main: `|| github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && inputs.use_ab_ci`.
- **`pwa-ui`'s `runs-on`** would become `${{ <guard> && fromJSON('["self-hosted","linux","x64","ab-ci-linux"]') || 'ubuntu-24.04' }}`. The fallback is a hosted label, so with the variable unset nothing changes.
- **Lint change (`tools/AgentBridge.Quality/RunnerRouting.cs`)**, written tests first:
  - accept a second guard, the Linux one, **paired with its pool**. The Windows guard may only guard a literal naming `ab-ci` and `windows`; the Linux guard may only guard a literal naming `ab-ci-linux` and `linux`. A Windows guard in front of the Linux pool (or the reverse) is a violation, so turning one variable on can never route a job to the other pool.
  - every rule that holds today still holds: exact guard, literal pool, one hosted fallback, no `pull_request_target` or `workflow_run` in the workflow, runner groups refused, unknown labels fail closed.
- **Cache rule change (`tools/AgentBridge.Quality/CachePolicy.cs`):** a job that may land on `ab-ci-linux` must:
  - set `npm_config_cache` and any download cache under `RUNNER_TEMP`;
  - set `PLAYWRIGHT_BROWSERS_PATH` to `/opt/ms-playwright` or under `RUNNER_TEMP`;
  - run `git clean -ffdx` before it installs;
  - skip the Playwright system-packages download (no sudo, and the image already has them);
  - cache only from `${{ runner.temp }}`, as on Windows.
- **Fork pull requests never reach these runners.** The guard sends them to hosted runners. Beyond the guard:
  - the repository setting "Require approval for all external contributors" (`all_external_contributors`) stays set, and SRE's install script checks it before registering each runner, as on Windows;
  - no workflow with `pull_request_target`, `workflow_run` or `issue_comment` and a checkout of untrusted code may route here, which the lint already enforces.

## Capacity check and rollout

1. SRE builds the image and the 3 VMs and registers the 12 runners with an install script in the style of `setup/install-ab-runners.ps1`. That means `-WhatIf` first, a fork-policy check before each runner, an idempotent `-Count`, and `-Remove`. **Nothing is routed yet.**
2. Engineering lands the lint and cache-rule changes, then the `pwa-ui` `runs-on` with the variable unset (no change).
3. **A run by hand** on main with `use_ab_ci`, twice, as in the Windows test plan:
   - the browser legs show an `ab-ci-linux` runner, everything else stays hosted, and both runs are green;
   - the second run lands on reused machines.
4. **Capacity:** `quality/ab-ci-capacity.ps1` (#3878) gains a `-Label` parameter (small change; default `ab-ci`). With `-Label ab-ci-linux` it must exit 0 during a busy hour: at least 1 runner online, nothing queued, no wait over 60 s.
5. The owner sets `AB_CI_LINUX_SELF_HOSTED=true`. The check is re-run in the next busy hour.

## Rollback

- **At once:** delete `AB_CI_LINUX_SELF_HOSTED` (or set it to anything but `true`). The next runs take hosted `ubuntu-24.04`, with no workflow change.
- **Machines:** SRE's script `-Remove` unregisters the runners and removes the accounts. The VMs can then be deallocated.

## What this does NOT protect (for the owner to accept)

- Runners persist, so a job from a trusted contributor can leave something behind that a later job on the **same runner** reads: anything outside the wiped paths that the account can write. With one account per runner and no sudo, that is little: the account's home is reset, and the image and hooks are read-only.
- A job can reach the network the VM can reach. Give the VMs a network with no route to the fleet's other machines.
- The per-job wipe is hygiene, not isolation. The controls that matter are: the fork-PR approval setting, the guard and its lint, no secrets routed here, and the read-only image.

## Open items for the owner

- **Cost:** 3 VMs of the size above, always on (or on a schedule for busy hours, which SRE could script).
- **Ephemeral runners** would remove the persistence risk. They need a re-registration service holding a token; this is the upgrade path, as on Windows.
- **Order:** this goes after the Windows pool is on and measured, and after the cheaper Linux cuts (fewer re-tested commits on main; WebKit off the push path, item B). Those may make it unnecessary.
