# Release train: WebKit at the picked commit before publish (#2454 item B, prerequisite 2)

For Quality. It turns on only with `PWA_WEBKIT_OFF_PUSH=true`; with the variable unset (the default), the train behaves as v8 does.

**Apply in this order**, each with `git apply`, on main:
1. `patches/release-train-3769.patch` (v8, branch `sg-patch/release-train-3769`, dd98b6ce).
2. `patches/pwa-webkit-off-push-2454.patch` (branch `sg-patch/pwa-webkit-off-push-2454`). It conflicts with v8 on one line: `.github/workflows/merge-rule.yml`'s `workflow_run.workflows` list. Keep both names, `... Ops probe, PWA WebKit, Pages, Perf, Re-run known flakes, Relay deploy, Release train, ...` (`git apply -3`, then edit that line).
3. `patches/main-red-webkit-2454.patch` (branch `sg-patch/main-red-webkit-2454`): hourly WebKit failures feed main-red as their own stream.
4. **This patch**, `patches/release-train-webkit-hook-2454.patch`.

**What this patch changes in `release-train.yml`:**
- A new `webkit` job calls `pwa-webkit.yml` with `ref: <the pick>`, only when there is a pick and `PWA_WEBKIT_OFF_PUSH == 'true'`.
- `publish` needs `[pick, verify, webkit]`, and its `if` becomes explicit: `!cancelled()`, a pick, not a dry run, `verify` succeeded, and `webkit` succeeded or was skipped. With the switch off, the skipped `webkit` job doesn't skip publish.
- `alert` also fires when `webkit` fails, because nothing was published then.
- `quality/catalog.yml`: the pwa-ui suite's `where:` names `release-train.yml webkit`, as the catalog check requires.

**Checks, with all four applied on main 386dd407:** catalog check complete; Quality.Tests 791 passed, 1 skipped; test-main-red 100 checks passed.
