# WebKit off the push path, behind `PWA_WEBKIT_OFF_PUSH` (#2454, item B)

For Quality, to apply; the owner decides whether to turn it on. Apply `patches/pwa-webkit-off-push-2454.patch` with `git apply`.

**While the variable is unset (the default), nothing changes.** `pwa-webkit.yml`'s job is skipped, and `ci.yml` keeps the WebKit legs on every main push.

**With `PWA_WEBKIT_OFF_PUSH=true`:**
- `ci.yml`'s main **push** runs leave the WebKit legs out. A run by hand still includes them, and pull requests never had them.
- `pwa-webkit.yml` runs the same three legs:
  - hourly on main's head (`:41`);
  - by hand;
  - from the release train with the commit it would publish (`workflow_call`, input `ref`).
- Its steps match `pwa-ui`'s WebKit steps. The browser cache is saved only by the hourly run on main.
- The merge rule is unaffected. The WebKit legs are the catalog's post-merge tier, and "an exact post-merge job never holds main, run or not". So push runs without them don't hold main red.

**What the patch changes:**
- `.github/workflows/pwa-webkit.yml` (new).
- `ci.yml`: the pwa-ui matrix's WebKit condition gains exactly `&& (github.event_name != 'push' || vars.PWA_WEBKIT_OFF_PUSH != 'true')`.
- `tools/AgentBridge.Quality/CiPlan.cs`: the tier-shape regex accepts that one conjunct, in exactly that form. Two new `CiPlanTests` cases: the exact form passes, and a weakened `|| true` is still rejected.
- `.github/workflows/merge-rule.yml`: "PWA WebKit" joins its `workflow_run` list, which the existing test requires for every workflow.
- `quality/catalog.yml`: the pwa-ui suite's `where:` names `pwa-webkit.yml webkit`.

**Checks:** catalog check passes; Quality.Tests 791 passed, 1 skipped, on main `dd9363ef`.

## Before anyone turns it on

1. **Alerting (decide first).** `main-red.yml` listens only to CI's push runs, so a failing hourly WebKit run would raise no main-red entry. Pick one:
   - extend `main-red.yml` and `quality/main-red.ps1` to read "PWA WebKit" schedule runs as the post-merge tier. This is sensitive and a separate change.
   - **or** add a failure step in `pwa-webkit.yml` that opens or comments on a fixed issue.
2. **The release train (#3769)** isn't on main yet. When it lands, add this so a commit isn't published without WebKit:

```yaml
  webkit:
    needs: [pick]
    if: needs.pick.outputs.sha != ''
    uses: ./.github/workflows/pwa-webkit.yml
    with:
      ref: ${{ needs.pick.outputs.sha }}
  publish:
    needs: [pick, webkit]
    if: needs.pick.outputs.sha != '' && (needs.webkit.result == 'success' || needs.webkit.result == 'skipped')
```

   `skipped` covers the variable being off, when push runs already tested WebKit.
3. **Shard count.** This uses WebKit's current 3 shards. If `patches/pwa-webkit-4-shards-2454.patch` lands first, change the matrix here to `1/4` to `4/4` too. The two patches touch the same `ci.yml` line, so the second one applied needs a rebase.
