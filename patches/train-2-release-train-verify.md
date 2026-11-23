# The release train with the verify job

For Quality, to apply. **WHOLE 380** (cap 400). One of three patches cut from the release-train set, **apply in order 1, 2, 3, after today's release tag:**

1. `sg-patch/train-1-webkit-stream` (183): the WebKit stream.
2. `sg-patch/train-2-verify` (380): the release train with the verify job (both architectures, Arm64 record-only by default).
3. `sg-patch/train-3-purge-report` (83): the purge job and the dry-run report.

```
git apply patches/train-2-release-train-verify.patch
```

Applies cleanly in that order on **main** and on **main + #4159** (checked on clean checkouts of both); none depends on #4159. It replaces the single combined patch on `sg-patch/release-train-arm64-check` (4f8d463c), which Quality would not push as one.

**Needs patch 1** (the `webkit` job calls `pwa-webkit.yml`). **What:** `release-train.yml` (new): `pick` (the newest verified commit, `build/select-release-commit.ps1`), `verify` per architecture on a runner of that architecture (downloads the picked run's package and installer input byte for byte, refuses missing, foreign or altered input, installs per user into its channel, checks the installed manifest is the channel's, self-tests the installed app), `webkit` (only with `PWA_WEBKIT_OFF_PUSH`), `alert`, `publish` (needs verify; a dry run stops before it). Package presence, hash, install and manifest are required on **both** architectures and so is the x64 self-test. **The Arm64 self-test only records by default** (owner Nov 22 and Nov 23; Quality): `TRAIN_ARM64_SELFTEST_GATES`, exactly `true`, is the opt-in that makes it gate. Plus `ReleaseTrainWorkflowTests`, the `merge-rule.yml` list line, the `release-train` catalog suite and the design text in `docs/app-windows.md`. Defaults change nothing until the owner sets a variable (`APP_SIGNING` unset; the train refuses if it is anything but unset or false). **Checked alone after patch 1 on main + #4159:** catalog complete; Quality.Tests 841 passed, 2 skipped, 0 failed; test-main-red 100 checks; select-release-commit 56 pass.
