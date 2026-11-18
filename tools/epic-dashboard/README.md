# Epic dashboard (#756, part 1)

The hourly workflow was retired in #1971; the generator and its tests remain.
Use the [live roadmap and blockers](https://roadmap.slipgates.com/) for current progress.
The workflow, credentials and setup notes below are historical, not operator steps.

## Historical workflow

Before retirement, Epic dashboard checked hourly at minute 23, on issue close/reopen,
on main-push CI completion, and on manual dispatch. Both jobs were restricted to `main`
in `danswett/agent-bridge`. Events coalesced; the only write target was
[#755's body](https://github.com/danswett/agent-bridge/issues/755), not comments or Project
fields. That issue is now historical and is no longer refreshed.

The former CI-completion trigger covered automated releases on a trusted main ref,
without consuming upstream CI artifacts, caches or code. Manual releases were left
for a later scheduled or manual run. These triggers no longer exist.

`dashboard.py` is Product's reference generator with paginated reads and offline rendering.
Its `project`, `render` and `compare` commands only read and print to stdout. The former
workflow owned the body write and isolated credentials across jobs:

- **`read`** used environment `epic-dashboard` and only `contents: read` for its
  `GITHUB_TOKEN`. The Project secret was present only in its GraphQL step. It uploaded
  only `dashboard-project.json` as a same-run artifact, retained for **one day**.
- **`write`** depended on `read`, had no environment or Project secret, and downloaded only
  that run's snapshot. Public issues, labels, releases and the body edit used its own
  `GITHUB_TOKEN` (`contents: read`, `issues: write`).

The Project token and the issue-write token were never in the same job. Both jobs had
the main/repository guard and a separate job-level concurrency group with
`cancel-in-progress: false`: skipped events could not cancel real work, reads coalesced
without interrupting a read, and only one render/compare/edit could run at a time.
`tools/epic-dashboard/**` is a sensitive merge-rule path and its unit suite is
security-sensitive, so future generator-only changes still require independent review.

The layout, epic order, baseline and Beta 1 gate follow the reference in #756. Tracker
numbers, not titles, identify epics, so renames preserve the stable dashboard name.
Closed trackers are marked; deleted/unavailable trackers and trackers removed from the
Project are shown as missing. An unset Stage defaults to Planned; unset Rank is not
offered as next up. Other missing optional fields do not stop rendering.
All displayed dates/times are Pacific (`America/Los_Angeles`, including DST).

## Historical credentials and least privilege

GitHub documents that
[`GITHUB_TOKEN` cannot access Projects](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/automating-projects-using-actions).
That page requires **organization Projects** permission for an App, not repository
Projects permission, and recommends PATs for user Projects. App
[organization permissions apply only to an organization installation](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/choosing-permissions-for-a-github-app#about-github-app-permissions).
Installing the merge-rule App on the user account does not grant user-owned Project v2
access; it has only Commit statuses permission and is unchanged here.

[Fine-grained PATs do not support user-owned Projects](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#fine-grained-personal-access-tokens-limitations).
The retired workflow required a **classic PAT with only `read:project`**, the
[documented scope for Project queries](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-api-to-manage-projects#authentication).
No `repo`, `public_repo`, `project` or other write scope. Classic scopes cannot restrict
this read token to Project 1 alone; its user-Project read scope is the remaining limitation.

## Historical owner setup (retired)

The former setup called for a main-only `epic-dashboard` environment and its
`EPIC_DASHBOARD_PROJECT_READ` secret. That credential was never configured, so the
workflow failed before any body edit. Retirement removes the setup and rotation
requirement: do not create or rotate a token to restore #755. The
[live roadmap and blockers](https://roadmap.slipgates.com/) replace it.

## Checking and troubleshooting

Offline tests (no GitHub credentials or writes):

```powershell
uv run --no-project --python 3.12 --with tzdata python -m unittest discover -s tools/epic-dashboard -v
```

Linux runners have the system timezone database; Windows uses the `tzdata` package.
The tests use `fixture.json`, not live Project/repository data.

### Historical workflow troubleshooting

#755 is frozen, not a failing refresh to repair. Its timestamp recorded the last
material body update, not a heartbeat. The retired workflow left its body intact on
read or credential failures. There is no scheduled run to inspect or restore; use the
[live roadmap and blockers](https://roadmap.slipgates.com/) instead.
