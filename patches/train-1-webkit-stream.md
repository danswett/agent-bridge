# The WebKit stream (PWA_WEBKIT_OFF_PUSH)

For Quality, to apply. **WHOLE 183** (cap 400). One of three patches cut from the release-train set, **apply in order 1, 2, 3, after today's release tag:**

1. `sg-patch/train-1-webkit-stream` (183): the WebKit stream.
2. `sg-patch/train-2-verify` (380): the release train with the verify job (both architectures, Arm64 record-only by default).
3. `sg-patch/train-3-purge-report` (83): the purge job and the dry-run report.

```
git apply patches/train-1-webkit-stream.patch
```

Applies cleanly in that order on **main** and on **main + #4159** (checked on clean checkouts of both); none depends on #4159. It replaces the single combined patch on `sg-patch/release-train-arm64-check` (4f8d463c), which Quality would not push as one.

**What:** `pwa-webkit.yml` (new: the three WebKit legs hourly on main's head, by hand, and as a `workflow_call` with a `ref`); `ci.yml` leaves the WebKit legs out of main **push** runs only while the repository variable `PWA_WEBKIT_OFF_PUSH` is exactly `true` (unset, the default, changes nothing); `CiPlan.cs` and its two tests accept exactly that one conjunct; `main-red.yml`, `quality/main-red.ps1` and `quality/test-main-red.ps1` feed the hourly WebKit failures to main-red as their own stream; `merge-rule.yml`'s workflow list gains "PWA WebKit"; `quality/catalog.yml` names `pwa-webkit.yml webkit`. **Checked alone on main + #4159:** catalog complete; Quality.Tests 834 passed, 2 skipped, 0 failed; test-main-red 100 checks.
