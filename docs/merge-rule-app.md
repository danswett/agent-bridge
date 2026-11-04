# The merge-rule GitHub App (prepared, not applied)

Main's ruleset requires the `merge-rule` status (quality/README.md §4.9). Today the
`merge-rule` workflow posts it with its own `GITHUB_TOKEN`, as GitHub Actions. A same-repo
pull request can add a workflow of its own with `statuses: write` and post `merge-rule:
success` on its own head, also as GitHub Actions, and the ruleset can't tell the two apart.
So, until this App exists, **auto-merge stays off** and the merge coordinator merges by hand
after running `merge-rule --pr <n>` locally from a freshly fetched `origin/main` checkout,
never the pull request's worktree (AGENTS.md, "Pull requests", rule 4).

The App closes the gap: only it posts `merge-rule`, the ruleset accepts `merge-rule` only from
it, and its private key sits in an environment that only `main` can deploy from, so no pull
request's workflow can reach the key.

## What the App may do

- Repository permissions: **Commit statuses: Read and write** (and Metadata: Read-only, which
  GitHub adds to every App). Nothing else: every read uses the workflow's `GITHUB_TOKEN`.
- No webhook, no user authorization, installed on this account's `agent-bridge` only.
- Reading pull requests, checks, workflow runs and labels stays with the workflow's own
  read-only `GITHUB_TOKEN`; the App's token is used only to post (`MERGE_RULE_POST_TOKEN`).

## Owner's steps (about 10 minutes, all in the browser)

Create the App:

1. On github.com, signed in as `danswett`: your avatar → **Settings** → **Developer settings**
   (bottom of the left sidebar) → **GitHub Apps** → **New GitHub App**.
2. **GitHub App name:** `agent-bridge-merge-rule` (names are global; if it's taken, use
   `agent-bridge-merge-rule-danswett`). The name's lowercase slug is the bot's login.
3. **Homepage URL:** `https://github.com/danswett/agent-bridge`.
4. Leave **Callback URL** empty, and leave **Request user authorization (OAuth) during
   installation** and **Enable Device Flow** unchecked.
5. **Webhook:** uncheck **Active**.
6. **Permissions → Repository permissions:** set **Commit statuses** to **Read and write**.
   Leave everything else at **No access**, Pull requests included (Metadata shows
   as Read-only; that's mandatory). Leave Organization and Account permissions at No access,
   and subscribe to no events.
7. **Where can this GitHub App be installed?** **Only on this account**.
8. **Create GitHub App**. On the page that opens, note the **App ID** (a number) and the
   **Client ID** (starts with `Iv`).
9. Scroll to **Private keys** → **Generate a private key**. The browser downloads a `.pem`
   file. Never paste it into a chat, an issue, a PR or a log.

Install it on the repository:

10. In the App's left sidebar, **Install App** → **Install** next to `danswett` →
    **Only select repositories** → `agent-bridge` → **Install**.

Give only `main` the key:

11. In the repository: **Settings** → **Environments** → **New environment**, name
    `merge-rule` → **Configure environment**.
12. Leave **Required reviewers** and **Wait timer** off (the workflow runs unattended).
13. **Deployment branches and tags:** choose **Selected branches and tags** → **Add deployment
    branch or tag rule** → Ref type **Branch**, name pattern `main` → **Add rule**.
14. **Environment secrets** → **Add environment secret**: name `MERGE_RULE_APP_KEY`, value the
    whole `.pem` file's contents (including the `BEGIN` and `END` lines) → **Add secret**.
15. **Environment variables** → **Add environment variable**: name
    `MERGE_RULE_APP_CLIENT_ID`, value the Client ID from step 8 → **Add variable**.
16. Delete the downloaded `.pem` from your machine (GitHub can generate a new one; to rotate,
    generate a new key, replace the secret, then delete the old key on the App's page).
17. Tell the coordinator the **App ID** and the App's **slug** (the name from step 2, in
    lowercase). Neither is a secret.

Why an environment rather than a repository secret: any same-repo pull request's workflow can
read repository secrets. An environment limited to `main` gives its secrets only to jobs whose
ref is `main`: `pull_request_target`, `workflow_run`, `status`, `check_suite`, `schedule` and
`workflow_dispatch` on main. A `pull_request` run (ref `refs/pull/<n>/merge`) or a push to any
other branch is refused before the job starts.

## The follow-up PR (coordinator, after the steps above)

A sensitive change (`.github/**`, `quality/**`), so it needs an independent review like any
other.

1. `.github/workflows/merge-rule.yml`, job `evaluate`: add `environment: merge-rule`; drop
   `statuses: write` to `statuses: read`; add, before the judging step:

   ```yaml
         - name: Mint the merge-rule App's token
           id: app
           uses: actions/create-github-app-token@64ab729a6294000f5aa4a4bc0b73d1f32bc45510 # v3.3.0
           with:
             client-id: ${{ vars.MERGE_RULE_APP_CLIENT_ID }}
             private-key: ${{ secrets.MERGE_RULE_APP_KEY }}
             permission-statuses: write
   ```

   and give the judging step `MERGE_RULE_POST_TOKEN: ${{ steps.app.outputs.token }}` beside
   `GH_TOKEN` (the evaluator posts with it and reads with `GH_TOKEN`).
2. `quality/merge-rule.yml`: `status_poster:` the App's slug, so the evaluator recognises its
   own earlier posts.
3. `quality/ruleset-main.json`: the `merge-rule` required check's `integration_id` becomes the
   App ID (`quality-gate` stays 15368, GitHub Actions). The test that compares the ruleset with
   the workflows needs the same number.
4. After it merges: dispatch the workflow (`gh workflow run merge-rule.yml --repo
   danswett/agent-bridge`), check that a head's newest `merge-rule` comes from the App:

   ```
   gh api repos/danswett/agent-bridge/commits/<sha>/statuses --jq '.[] | select(.context=="merge-rule") | .creator.login' | Select-Object -First 1
   ```

   prints `<slug>[bot]`; then update the ruleset (`gh api -X PUT
   repos/danswett/agent-bridge/rulesets/<id> --input quality/ruleset-main.json`), and only then
   turn on auto-merge (`gh api -X PATCH repos/danswett/agent-bridge -F allow_auto_merge=true`)
   and update AGENTS.md rule 4 and docs/status.md §8 to say sessions use it.

Rollback: `gh api -X PATCH repos/danswett/agent-bridge -F allow_auto_merge=false` first, then
put the ruleset's `merge-rule` back to 15368 and revert the workflow.

What it still doesn't protect against: the owner's own account, which can change the ruleset,
the environment or the App. These are guards against honest mistakes by sessions sharing that
account, not against the account.
