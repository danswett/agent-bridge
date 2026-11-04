# GitHub outage

**Alert:** A-GHA; A-GHA-SILENT (the watch itself stopped reporting). Also use this page when CI,
merges or deploys sit queued and nothing seems wrong with them.

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
2. **Hold anything about to merge** whose head hasn't been fully checked. Turn auto-merge off
   first, then add the label:
   `gh pr merge <n> --repo danswett/agent-bridge --disable-auto`, then
   `gh pr edit <n> --repo danswett/agent-bridge --add-label hold`.
   The label alone isn't enough here: the ruleset reads the posted `merge-rule` status, not
   the label, and only the Actions-hosted `merge-rule` workflow turns a label into a failing
   status. While Actions is down that workflow sits queued, so an earlier green status would
   still let auto-merge fire. Note each PR you disarm, with its head sha.
3. **Relay emergencies don't wait for Actions.** If production needs a change, the owner runs
   `infra/deploy.ps1 -Production -ProductionImage <approved image@sha256 digest> -ProductionVersion <its sha>` from a workstation ([relay-bad-deploy](relay-bad-deploy.md),
   docs/relay-deploy.md). **Owner.**
4. **Windows and PWA:** installed apps keep the version they have, and the PWA keeps serving
   from Pages' cache and the service worker. If Pages itself is down, phones that have the PWA
   installed keep working; new installs wait.
5. **The daily probe** may miss its run. If the outage lasts more than a day, run
   `pwsh ops/tools/relay-probe.ps1` by hand.

## When it's over

1. Cancel superseded runs, so the backlog doesn't run twice. Go **per workflow and branch**,
   keeping the newest run of each: `gh run list --repo danswett/agent-bridge --branch <b>
   --workflow <w>`, then `gh run cancel <id>` for the older ones. Never cancel by branch alone:
   that would cancel the newest run of every other workflow (CodeQL, merge-rule, a deploy).
   Leave main's `relay.yml` runs and `ci.yml`'s release alone. A superseded run waiting at the
   production gate does block newer ones (AGENTS.md "Relay deploys"), but cancelling it is
   **Owner**.
2. Re-run what failed because of the outage, rather than pushing again:
   `gh run rerun <id> --failed`.
3. A CodeQL run that ended `skipped` fails `merge-rule` closed; run it again with
   `gh workflow run codeql.yml --ref <branch>` (AGENTS.md rule 4).
4. For each PR you held: once its head's other checks are green again (CI, CodeQL and the
   re-runs; `merge-rule` stays red while `hold` is on, by design), remove the `hold` label,
   then re-arm auto-merge pinned to that head:
   `gh pr merge <n> --repo danswett/agent-bridge --auto --squash --match-head-commit <sha>`.
   Re-arming before the fresh `merge-rule` posts is safe: the ruleset still waits for it to pass
   on that head. If the head moved since you held it, leave re-arming to the PR's session.
5. Check that main's last CI run passed. A red main blocks every merge and the Windows release.

## If A-GHA-SILENT fires

The watch hasn't recorded a result in 30 minutes, so A-GHA can't fire. Look at
`logic-agent-bridge-github-watch` in the portal: is it enabled, and what do the latest runs show
failed? `az resource show --subscription $sub -g $rg -n logic-agent-bridge-github-watch
--resource-type Microsoft.Logic/workflows --query properties.state` reads its state. If it was
disabled or deleted, re-running `infra/deploy.ps1 -Production -ProductionImage <running digest> -ProductionVersion <running sha>` restores it without changing the relay release (**Owner**). Until it
reports again, check https://www.githubstatus.com yourself.

## If A-GHA is noisy

- **Runs API failing:** the check calls GitHub's API without a token, and Azure Logic Apps share
  their outbound addresses, so the hourly limit for anonymous calls can run out. The message then
  shows `unknown (API 403)`. That's never counted as an outage, but it does blind the queued-run
  half. The fix is a fine-grained, read-only token with Actions read access on this repository,
  kept in Key Vault and never in the repository (#658).
- **Busy hours fire it:** runs queued over 30 min with plenty running means the pool is full,
  not down. Raise `githubStallMinutes` in `infra/modules/monitoring.bicep`.
