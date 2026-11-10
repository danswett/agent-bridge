# A bad PWA deploy

**Alert:** A-PAGES: the Pages deploy fails, or its post-deploy check finds the live `config.js`
or `sw.js` differing from the build. Other signs are phones failing to load the PWA, sign in or
act, while hosts and the Windows app are fine, or an installed app saying a new version keeps
failing to install (a release the worker could not keep whole; #650).

## How the PWA ships

`pages.yml` runs when `ci.yml` completes on `main` (`workflow_run`), and only a successful push
run deploys: it builds the exact commit CI tested (`workflow_run.head_sha`), never the branch's
moving head. A red CI run deploys nothing. Main moves faster than CI finishes, so the newest
green commit deploys; a run is skipped only when its commit is already live or older than the
live one (the live commit is the `APP_VERSION` in the served `config.js`;
quality/pages-gate.ps1). The build job holds no publishing token; the deploy
job publishes the artifact, then fetches the live `config.js` and `sw.js` and compares their
sha256 with the built files, retrying for about five minutes while Pages propagates. A mismatch
fails the job. The service worker is stamped with a hash of the served files
(pwa/tools/build-site.mjs), so installed PWAs install a new deploy, or a rolled-back one, on
their next start or return to the front, and offer Reload. Until then they run the release they
have from the worker's cache, without asking the network (#609).

## Mitigate

1. **Roll back to the last good commit.** Find the last good `<sha>` (the commit of the last
   Pages run whose verification passed), then:
   `gh workflow run pages.yml -R danswett/agent-bridge --ref main -f ref=<last good sha>`.
   This builds that commit's files and deploys them, with the same live check. The commit
   must be recent enough for the current workflow's build options. The
   `github-pages` environment accepts only `main` as the run's ref, which is why the run is on
   `main` and the commit goes in `ref`. If the environment's rule has changed and the run is
   refused, don't relax it in an incident without the owner.
2. **Then fix forward.** Revert or fix the PR through a PR (Quality merges, AGENTS.md). Its green
   CI run on `main` deploys the fix. Until it merges, a later green `main` run would deploy `main`
   over the rollback, so hold merges that touch the PWA.
3. **On a phone, after the fix is deployed:** close and reopen the PWA, and press Reload when
   it offers it. If it still shows the bad build, pull to refresh once. The service worker
   checks for a new release on each start.

## Confirm

Load the PWA, check its build stamp if it shows one, sign in, and answer a test card on a
disposable host.

## Afterwards

If main CI would have caught it, it should have: Pages deploys only after a green main run (REL-4),
so add the missing test.
