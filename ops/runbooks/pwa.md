# A bad PWA deploy

**Alert:** A-PAGES (once REL-4 ships). Today, the signs are phones failing to load the PWA,
sign in or act, while hosts and the Windows app are fine.

## How the PWA ships today

`pages.yml` builds and deploys to GitHub Pages on every push to `main` touching `pwa/` (and the
spikes' web pages), **without waiting for main CI** (REL-4, #582, changes that). The service
worker is stamped with a hash of the served files (pwa/tools/build-site.mjs), so installed PWAs
install a new deploy, or a rolled-back one, on their next start or return to the front, and
offer Reload. Until then they run the release they have from the worker's cache, without
asking the network (#609).

## Mitigate

1. **Revert on `main`.** Revert the PR through a PR (Quality merges, AGENTS.md). The merge
   triggers `pages.yml`, which redeploys the previous files. This is the dependable path.
2. **Faster, if Pages' environment allows it:**
   `gh workflow run pages.yml -R danswett/agent-bridge --ref <branch at the last good commit>`.
   The `github-pages` environment usually accepts only `main`, so expect this to be refused
   unless that rule has changed. Don't relax the rule in an incident without the owner.
3. **On a phone, after the fix is deployed:** close and reopen the PWA, and press Reload when
   it offers it. If it still shows the bad build, pull to refresh once. The service worker
   checks for a new release on each start.

## Confirm

Load the PWA, check its build stamp if it shows one, sign in, and answer a test card on a
disposable host.

## Afterwards

If main CI would have caught it, REL-4 is the fix: Pages deploys only after a green main run.
