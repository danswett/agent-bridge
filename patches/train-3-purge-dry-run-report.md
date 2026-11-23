# The purge job and the dry-run report

For Quality, to apply. **WHOLE 83** (cap 400). One of three patches cut from the release-train set, **apply in order 1, 2, 3, after today's release tag:**

1. `sg-patch/train-1-webkit-stream` (183): the WebKit stream.
2. `sg-patch/train-2-verify` (380): the release train with the verify job (both architectures, Arm64 record-only by default).
3. `sg-patch/train-3-purge-report` (83): the purge job and the dry-run report.

```
git apply patches/train-3-purge-dry-run-report.patch
```

Applies cleanly in that order on **main** and on **main + #4159** (checked on clean checkouts of both); none depends on #4159. It replaces the single combined patch on `sg-patch/release-train-arm64-check` (4f8d463c), which Quality would not push as one.

**Needs patch 2.** **What:** the `purge` job (environment `release-purge`: after a publish it purges the relay's list pages by URL and checks the relay serves the new release, #3870; failing there never unpublishes anything) and the `dry-run-report` job (a run by hand with `dry_run` says what it would have published and whether verification passed), the catalog suite's `where:` naming both, and the design bullet. `purge` needs the `release-purge` environment with `CF_PURGE_TOKEN` and `CF_ZONE_ID`; without them it fails loudly **after** a publish. **Checked:** this tree is the fully tested combined set (Quality.Tests 841 passed, 2 skipped, 0 failed; catalog complete; test-main-red 100 checks).
