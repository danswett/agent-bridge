# Trying the ab-ci pool by hand, before `AB_CI_SELF_HOSTED` is set (#2454)

For the owner. It uses `ci.yml`'s `use_ab_ci` input, added by `patches/ci-ab-ci-routing-2454.patch`.
Nothing here changes what pushes and pull requests do: they stay on hosted runners until the variable is set (step 6).

## 0. Before you start (Quality and SRE do these; check them)

- Quality has applied both patches to main, in this order, and main's CI is green on that commit:
  1. `patches/ci-cache-per-job-2454.patch` (branch `sg-patch/ci-cache-per-job-2454`)
  2. `patches/ci-ab-ci-routing-2454.patch` (this branch, `sg-patch/ci-ab-ci-routing-2454`)
- `AB_CI_SELF_HOSTED` is **not** set: Settings → Secrets and variables → Actions → **Variables** tab. It must not be listed, or must not be `true`.
- The runners are up: Settings → Actions → **Runners**. Expect the 8 VM7 runners, status **Idle**, labels `self-hosted`, `windows`, `x64`, `ab-ci`.

## 1. Start the run

1. Open https://github.com/danswett/agent-bridge/actions/workflows/ci.yml
2. On the right, click **Run workflow** (the grey dropdown button above the run list).
3. **Use workflow from**: leave **Branch: main**. Any other branch is refused by the plan job.
4. Tick **"Run the Windows x64 legs (product, spikes) on the ab-ci pool, before AB_CI_SELF_HOSTED is set (main only)"**.
5. Leave **force-apt-fallback** unticked. Ticking both is refused, because each is a separate run.
6. Click the green **Run workflow** button.

## 2. Check it ran where it should

Open the new run at the top of the list; its event shows **workflow_dispatch**.

- **plan** passes. If it fails with "use_ab_ci runs only on main", step 1.3 used another branch.
- Open **product (windows-latest)** and expand **Set up job**:
  - **Runner name** is one of the VM7 runners, not "GitHub Actions …";
  - **Runner group** is the repository's, and the labels include `ab-ci`.
- The same holds for **spikes**.
- Every other job (Linux, Arm64, app, app-installer, pwa) shows a GitHub-hosted runner. Only those two Windows x64 legs move.
- In **product (windows-latest)**, these steps ran:
  - **NuGet, .NET tools and MSBuild state for this job only, in a clean workspace** (near the start);
  - **Stop this job's build servers** (the last step).

## 3. Watch the pool while it runs (optional, needs PR #3878 on main)

From a clone of main, signed in to `gh` as yourself (listing runners needs admin read):

```
dotnet build tools/AgentBridge.Quality -c Release
pwsh quality/ab-ci-capacity.ps1
```

Exit 0 means the pool keeps up: at least one ab-ci runner is online, no ab-ci job is queued, and none has waited over 60 s.

## 4. Pass criteria

- The run finishes green, with **product (windows-latest)** and **spikes** on VM7 runners.
- Times to compare with hosted (median of the last main runs): product (windows-latest) 13.7 min, of which tests 9.7; spikes 1.8 min.
- **Run it a second time** (repeat step 1). It must be green too, on a reused machine: this proves the clean workspace and the per-job folders. The NuGet restore should be faster once main has saved a cache. A run by hand never saves one, so the first push run after step 6 saves it.

## 5. If something fails

- Don't set the variable. Send the run's URL to the CI lead. Nothing else needs undoing, because a run by hand is never a release, writes no cache, and isn't main's push run for the merge rule or the release train.
- A runner stuck busy or offline: SRE's runbook for VM7 (SRE R4, fleet #1110).

## 6. Turn it on for pushes and pull requests (only after 4 passes)

1. Run step 3's capacity check once more. It must exit 0.
2. Settings → Secrets and variables → Actions → **Variables** tab → **New repository variable**.
3. **Name** `AB_CI_SELF_HOSTED`, **Value** `true` → **Add variable**.
4. From then on, pushes to main and pull requests from this repository (never forks) take the two Windows x64 legs to ab-ci.
5. **To roll back at once:** the same page, the variable's ⋯ menu → **Delete** (or edit it to `false`). The next runs go back to hosted runners, with no workflow change.
