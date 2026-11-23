# Release train: install and self-test the exact CI bytes of BOTH architectures before it publishes (option A)

For Quality, to apply. **One patch replaces the four from last night** (v9 `release-train-3769`, B `pwa-webkit-off-push-2454`, `main-red-webkit-2454`, the
WebKit hook `release-train-webkit-hook-2454`): they were rebuilt, in that order, onto current main and checked together.

```
git apply patches/release-train-arm64-check.patch
```

Applies cleanly on **main** and on **main + #4159** (checked on clean checkouts of both); it does not depend on #4159.

## What it adds
- `release-train.yml` (new): `pick` (the newest verified commit, `build/select-release-commit.ps1`), `verify` (per architecture, on a runner of that
  architecture), `webkit` (only with `PWA_WEBKIT_OFF_PUSH`), `alert`, `publish`, `purge`, `dry-run-report`.
- `verify`, **the check R8b asked for**: downloads that run's package and installer input byte for byte, refuses missing, foreign or altered input,
  installs the package per user into its channel on `windows-latest` (x64) and `windows-11-arm` (Arm64), checks the installed manifest is the channel's,
  and self-tests the installed app. Package presence, hash, install and manifest are required on both architectures.
- **Arm64's self-test only records by default** (summary line and a warning). Owner rules: Nov 22 "Arm64 never gates the train", Nov 23 "ship now: a red Arm64
  self-test still publishes"; Quality: `TRAIN_ARM64_SELFTEST_GATES` default off. The variable stays as the opt-in (exactly `true` makes it gate).
- `pwa-webkit.yml` (new), `ci.yml` (WebKit legs off push runs behind `PWA_WEBKIT_OFF_PUSH`), `main-red.yml`/`quality/main-red.ps1` (hourly WebKit failures as their
  own main-red stream), `CiPlan.cs` and its tests, `quality/catalog.yml`, and `merge-rule.yml` (the workflow_run list gains "PWA WebKit" and "Release train").
- `docs/app-windows.md`: the design text for the verify job, the Arm64 rule, the WebKit hook and the purge job.

## Defaults: nothing new happens until the owner sets something
`APP_SIGNING` unset (the train refuses if it is anything but unset or false), `TRAIN_ARM64_SELFTEST_GATES` unset (Arm64 records), `PWA_WEBKIT_OFF_PUSH` unset (WebKit stays on
push runs; the `webkit` job is skipped). The `purge` job needs the `release-purge` environment with `CF_PURGE_TOKEN` and `CF_ZONE_ID`; without them it fails loudly **after** a publish
(the release is out, nothing is unpublished).

## Checked on main cbb9e99e + #4159 (the merge-rule.yml workflow list was hand-merged: main's list has since been pruned)
- catalog check: "The catalog is complete."
- `AgentBridge.Quality.Tests`: 841 passed, 2 skipped, 0 failed (Release, serial).
- `quality/test-main-red.ps1`: 100 checks passed. `build/test-select-release-commit.ps1`: 56 pass, 0 fail.

## First run after it lands
Run the workflow by hand with `dry_run: true`: it picks, verifies both architectures and stops (nothing published, no manifest, no purge). Read the summary: the x64 self-test must be
green, the Arm64 one is recorded either way.
