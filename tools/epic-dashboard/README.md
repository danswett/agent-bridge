# Epic dashboard (#756, part 1)

[Epic dashboard](../../.github/workflows/epic-dashboard.yml) checks hourly at minute 23,
on issue close/reopen, on a published release, and on manual dispatch. Only `main` in
`danswett/agent-bridge` may run the job. Concurrent events coalesce. It rewrites only
[#755's body](https://github.com/danswett/agent-bridge/issues/755), never comments or Project
fields. Shipped-in stamping and Pages charts are separate work.

GitHub's [`release` event has a tag ref](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#release),
so the required main-only guard skips it. Also,
[events from `GITHUB_TOKEN` do not start downstream workflows](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow#triggering-a-workflow-from-a-workflow).
The workflow therefore also listens for completion of this repository's main-push
**CI** workflow, which publishes the releases. This trusted main-ref path refreshes
after automated releases even if a later CI job fails. It never consumes upstream
artifacts, caches or code. A release published manually outside CI is picked up by
the next hourly run (or manual dispatch on `main`), not by its tag-ref event.

`dashboard.py` is Product's reference generator with paginated reads and offline rendering.
Its `project`, `render` and `compare` commands only read and print to stdout; the final
workflow step owns the write. Project data uses a separate read credential; public issues,
labels, releases and the body edit use `GITHUB_TOKEN` (`contents: read`, `issues: write`).
The Project credential is present only in the Project GraphQL step.

The layout, epic order, baseline and Beta 1 gate follow the reference in #756. Tracker
numbers, not titles, identify epics, so renames preserve the stable dashboard name.
Closed trackers are marked; deleted/unavailable trackers and trackers removed from the
Project are shown as missing. An unset Stage defaults to Planned; unset Rank is not
offered as next up. Other missing optional fields do not stop rendering.
All displayed dates/times are Pacific (`America/Los_Angeles`, including DST).

## Credentials and least privilege

GitHub documents that
[`GITHUB_TOKEN` cannot access Projects](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/automating-projects-using-actions).
That page requires **organization Projects** permission for an App, not repository
Projects permission, and recommends PATs for user Projects. App
[organization permissions apply only to an organization installation](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/choosing-permissions-for-a-github-app#about-github-app-permissions).
Installing the merge-rule App on the user account does not grant user-owned Project v2
access; it has only Commit statuses permission and is unchanged here.

[Fine-grained PATs do not support user-owned Projects](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#fine-grained-personal-access-tokens-limitations).
Use a **classic PAT with only `read:project`**, the
[documented scope for Project queries](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-api-to-manage-projects#authentication).
No `repo`, `public_repo`, `project` or other write scope. Classic scopes cannot restrict
this read token to Project 1 alone; its user-Project read scope is the remaining limitation.

## Owner steps

No environment or credential is created by this PR. Until the owner configures it,
the workflow fails before any body edit, with an explicit missing-secret message.

1. Signed in as `danswett`: avatar → **Settings** → **Developer settings** →
   **Personal access tokens** → **Tokens (classic)** → **Generate new token (classic)**.
   Name it `epic-dashboard-project-read`, choose an **expiry** (for example 90 days),
   and select **only `read:project`**, not its parent `project`. Leave every other scope
   unchecked. Generate it; never paste it into a chat, issue, PR or log.
2. Repository **Settings** → **Environments** → **New environment** →
   `epic-dashboard`. Leave **Required reviewers** and **Wait timer** off for unattended runs.
3. **Deployment branches and tags** → **Selected branches and tags** → add a rule:
   ref type **Branch**, pattern **`main`**. Do not add a tag rule or a wildcard.
4. **Environment secrets** → add **`EPIC_DASHBOARD_PROJECT_READ`**, using the token.
   Do **not** make a repository-level secret. This mirrors
   [merge-rule's main-only environment](../../docs/merge-rule-app.md).
5. Before expiry, create a replacement with the same sole scope and a new expiry,
   replace the environment secret, and check the next scheduled run succeeds before
   revoking the old token in **Tokens (classic)**. Keep the token out of files and logs.

## Checking and troubleshooting

Offline tests (no GitHub credentials or writes):

```powershell
uv run --no-project --python 3.12 --with tzdata python -m unittest discover -s tools/epic-dashboard -v
```

Linux runners have the system timezone database; Windows uses the `tzdata` package.
The tests use `fixture.json`, not live Project/repository data.

If #755 looks old, inspect the **Epic dashboard** workflow's latest run first.
The timestamp is the **last material body update**, not a heartbeat: if only the
timestamp changed, the workflow deliberately makes no edit. GitHub schedules/runner
queues can delay checks beyond an hour. A missing/expired token, unavailable Project or
failed repository read fails closed and leaves the body intact. Restore/rotate the
environment secret, then verify the next scheduled run. Do not repair it with comments.
