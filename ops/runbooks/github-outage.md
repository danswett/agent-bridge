# GitHub outage

**Alert:** A-GHA. Also use this page when CI, merges or deploys sit queued and nothing seems wrong
with them.

Everything that changes the product runs on GitHub Actions: PR CI, Copilot reviews, `merge-rule`
and auto-merge, the Windows release, relay deploys (`relay.yml`), Pages, and the daily
`ops-probe.yml`. When Actions is down, **nothing ships, and nothing that runs on Actions can tell
you**. That's why A-GHA runs in Azure (`logic-agent-bridge-github-watch`).

What keeps working: the relay itself and every device on it. GitHub isn't on the relay's request
path, except that sign-in uses GitHub's device flow, so new sign-ins can fail while GitHub is
down. A-AVAIL and the relay's metric alerts run in Azure and keep working.

## Confirm

- https://www.githubstatus.com: the Actions and Pages components, and any open incident.
- The alert's last results carry a message naming what each source answered:

  ```powershell
  az monitor app-insights query --subscription $sub -g $rg --app appi-agent-bridge-relay-wus2 --analytics-query "availabilityResults | where name == 'github-actions' | top 6 by timestamp | project timestamp, success, message" -o table
  ```

- Our own queue: `gh run list --repo danswett/agent-bridge --status queued --limit 20`.

If the message says the status page is operational but runs have been queued for a long time,
it's either an outage GitHub hasn't posted yet, or our shared runner pool is just full (many
sessions pushing at once). `gh run list --status in_progress` tells them apart: a full pool has
many runs in progress; a stall has none.

## Mitigate

There's nothing to fix on our side. The job is to avoid adding load, and to recover cleanly.

1. **Stop pushing.** Every push queues CI for every session, so a backlog only grows. Tell the
   running agent sessions to hold their pushes.
2. **Hold anything about to merge** whose head hasn't been fully checked:
   `gh pr edit <n> --repo danswett/agent-bridge --add-label hold`. Auto-merge could otherwise
   fire on a status posted before the outage.
3. **Relay emergencies don't wait for Actions.** If production needs a change, the owner runs
   `infra/deploy.ps1 -Production` from a workstation ([relay-bad-deploy](relay-bad-deploy.md),
   docs/relay-deploy.md). **Owner.**
4. **Windows and PWA:** installed apps keep the version they have, and the PWA keeps serving
   from Pages' cache and the service worker. If Pages itself is down, phones that have the PWA
   installed keep working; new installs wait.
5. **The daily probe** may miss its run. If the outage lasts more than a day, run
   `pwsh ops/tools/relay-probe.ps1` by hand.

## When it's over

1. Cancel superseded runs, so the backlog doesn't run twice: for each branch, keep only the
   newest run (`gh run list --branch <b>`, then `gh run cancel <id>`). Remember a waiting
   production deploy blocks newer ones (AGENTS.md "Relay deploys").
2. Re-run what failed because of the outage, rather than pushing again:
   `gh run rerun <id> --failed`.
3. A CodeQL run that ended `skipped` fails `merge-rule` closed; run it again with
   `gh workflow run codeql.yml --ref <branch>` (AGENTS.md rule 4).
4. Remove the `hold` labels you added, once each head's checks are green again.
5. Check that main's last CI run passed. A red main blocks every merge and the Windows release.

## If A-GHA is noisy

- **Runs API failing:** the check calls GitHub's API without a token, and Azure Logic Apps share
  their outbound addresses, so the hourly limit for anonymous calls can run out. The message then
  shows `unknown (API 403)`. That's never counted as an outage, but it does blind the queued-run
  half. The fix is a fine-grained, read-only token with Actions read access on this repository,
  kept in Key Vault and never in the repository (#658).
- **Busy hours fire it:** runs queued over 30 min with plenty running means the pool is full,
  not down. Raise `githubStallMinutes` in `infra/modules/monitoring.bicep`.
