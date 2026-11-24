# A bad PWA deploy

**Alert:** A-PAGES: the Pages deploy fails, or its post-deploy check finds the live `config.js`
or `sw.js` differing from the build. Other signs are phones failing to load the PWA, sign in or
act, while hosts and the Windows app are fine, or an installed app saying a new version keeps
failing to install (a release the worker could not keep whole; #650).

## How the PWA ships

`pages.yml` checks the newest 20 main-push CI runs every five minutes and at CI or Full completion.
Plan, all six pwa-ui shards, pwa-e2e and product (ubuntu-latest) must succeed, each in the CI run or in the same commit's Full run (full.yml; a Pages deploy waits for FULL, never deploys on CI alone); other jobs do not gate.
Only Pages-path changes proven on main and newer than a known live APP_VERSION deploy.
A FULL run that never starts or was cancelled (its concurrency group cancels by arrival, not by commit) leaves the newest commit undeployed and silent; nothing watches FULL directly, but the stale-PWA alarm (`stale-pwa.yml`, #4369/#4466, on main) notices the effect: it compares the served build's time with main's newest `pwa/` commit's time (not the clock), checked every four hours: it fires once that commit is dated more than 24 hours after the served build; a newest commit dated within 24 hours of the served build is not seen until a later `pwa/` commit widens the gap (see "The stale-PWA alarm" below). To unblock it, while that commit is still main's tip, dispatch FULL by hand on main: `gh workflow run full.yml -R danswett/agent-bridge --ref main`. A by-hand FULL run on `main` at that exact commit counts for Pages; a dispatch on any branch never does (write access is needed to dispatch).
Manual rollback below remains exempt. The build job holds no publishing token; the deploy
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
   named PWA jobs can deploy only a head proven newer than the main SHA at the rollback request.
   Cron cannot replay the same bad SHA. Pending, failed or cancelled manual runs stay conservative.
   To explicitly resume, run `gh workflow run pages.yml -R danswett/agent-bridge --ref main` without
   `ref`; only its completed-success `Pages resume` run clears the fence. This deploys current main.
   Use fresh dispatches: manual reruns (all jobs or failed jobs) are refused before publishing.
3. **On a phone, after the fix is deployed:** close and reopen the PWA, and press Reload when
   it offers it. If it still shows the bad build, pull to refresh once. The service worker
   checks for a new release on each start.

## Confirm

Load the PWA, check its build stamp if it shows one, sign in, and answer a test card on a
disposable host.

## The stale-PWA alarm

**A red `Stale PWA alarm` run** (`stale-pwa.yml`, every four hours and by hand; #4369) means https://app.slipgates.com is serving a build more than 24 hours older than main's newest commit that
changed `pwa/`, or that the check could not read what it needs. It compares the served `config.js` (`APP_VERSION` is the deployed commit, `BUILT_AT` its time) with that commit, with two read-only GETs and no secret
(`quality/stale-pwa.ps1`); an unreadable `config.js` or history is red too, never "fresh". It exists because a held deploy is silent: from Nov 12 to 24 the site served a 12-day-old build while every Pages
run was green (a rollback fence nobody had set was holding the deploys). Its run summary names the served and the newest commit. **To clear it:** open the newest `Pages` runs and read why the gate did not deploy (a
rollback fence, a failed named PWA job, or a history it could not read); if no rollback was intended, resume with `gh workflow run pages.yml -R danswett/agent-bridge --ref main` (no `ref`, as under Mitigate 2),
then dispatch `stale-pwa.yml` (or wait for its next run) and check it is green. A deliberate rollback looks the same and alarms until the fix is deployed. It watches only `pwa/` commits, while Pages also deploys
on `design/fonts/`, the spikes' web files and `pages.yml`, so it stays quiet on a stale site after only those changed.
## Afterwards

Rollback state lives in manual Pages run history: one page of at most 100 runs, ordered by creation
time, recognizing only main-branch `Pages rollback` and `Pages resume`. With no `Pages rollback` run
in the complete history the fence is open and automatic deploys proceed (no resume is needed for a
first deployment). After a rollback, deploys stay held until a successful `Pages resume` dispatch.
Unknown ancestry, a lookup failure or a truncated history (more than 100 runs, none a rollback)
blocks automation loudly. Matching is by exact run title, so a renamed or retention-expired
(90 days) `Pages rollback` run leaves the fence open.

If PWA CI would have caught it, it should have: Pages deploys only after its named jobs pass,
so add the missing test.
