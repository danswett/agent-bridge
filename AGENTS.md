# Working on agent-bridge

Read this before you change anything, whether you are a person or an agent. For where the
project stands and what comes next, see [docs/status.md](docs/status.md). For the
architecture, see [docs/design.md](docs/design.md).

## The one rule that outranks the rest

**Never break the live agent-ha-bridge.** Real machines still run it, and the owner's
Home Assistant dashboard depends on it. The following are off limits unless a task
explicitly says otherwise:
- the owner's real Home Assistant and MQTT broker;
- `~/.agent-ha-bridge`;
- the installed hooks;
- the running daemon.

Never run `agent-ha-bridge restart` from an agent session. Use a disposable HA
(`homeAssistant.disposable true`, as in CI's `ha-integration` job) for any write
testing. agent-ha-bridge's source (`danswett/agent-ha-bridge`) is the spec: read it, but
make changes there only through its own PR process (its `AGENTS.md`).

## Git

Several agent sessions often work here at once.

- Work in **your own git worktree**, never in the primary clone:
  ```
  git -C <primary clone> fetch origin --quiet
  git -C <primary clone> worktree add -b <branch> <worktrees root>/agent-bridge-<slug> origin/main
  ```
- Stage only the files you changed. Never `git add -A` and never `git stash`. Never touch a branch or worktree you did not create.
- Remove a worktree you created, by name, once its PR is merged (`git worktree remove`, without `--force`). Never sweep a shared worktree root.
- Agent commits end with the trailers `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` and `Copilot-Session: <your session id>`.

## Pull requests

Everything lands through a PR, including documentation.

1. Rebase onto freshly fetched `origin/main`, never merging main in, and re-run the tests after the rebase.
2. Open the PR with `gh pr create`, saying what changed, why, and exactly what was run to verify it.
3. Copilot code review runs automatically on each push; you can also request it with `gh pr edit <n> --add-reviewer "@copilot"`. Wait for green CI on the final head **and** a Copilot review whose commit is that head. Fix every substantive comment or reply with a reasoned disposition, then resolve the threads.
4. **Merge rule**, enforced by GitHub through main's ruleset ([quality/ruleset-main.json](quality/ruleset-main.json)) once it's applied (until then, [docs/status.md](docs/status.md) §8 says it's pending, and the merge coordinator merges by hand under the same rule): `quality-gate` green, the `merge-rule` status green, 0 unresolved threads, squash only, and no conflicts. Main's copy of `.github/workflows/merge-rule.yml` posts `merge-rule` on each head from [quality/merge-rule.yml](quality/merge-rule.yml): not a draft, no `hold` or `do-not-merge` label, a Copilot review whose commit is the head, CodeQL green on the head, every other check and status on the head passed (success, skipped or neutral; not only `quality-gate`), and for a sensitive change (rule 5) an independent review's receipt on the head. Its description names what's missing; `dotnet run --project tools/AgentBridge.Quality -c Release -- merge-rule --pr <n>` shows the same verdict without posting anything; run it from a freshly fetched `origin/main` checkout (`git fetch origin`, then `git switch --detach origin/main` in a worktree kept for it), never the PR's worktree, whose own evaluator and rules would judge it (the tool prints the commit they came from and refuses anything but `origin/main`'s). A CodeQL run that ended `skipped` (a PR opened seconds before `ready_for_review`) fails closed until CodeQL runs again: `gh workflow run codeql.yml --ref <branch>`, or close and reopen the PR. To keep a PR from merging whatever else it has, add the `hold` label (`gh pr edit <n> --add-label hold`).
   **Auto-merge stays off until the merge-rule GitHub App exists** ([docs/merge-rule-app.md](docs/merge-rule-app.md)). Until then `merge-rule` is posted with the workflows' own token, and a same-repo PR's workflow with `statuses: write` could post a forged `merge-rule` on its own head; the ruleset can't tell the two apart. So the merge coordinator merges by hand: it runs `merge-rule --pr <n>` (which counts only receipts from `independent_review.posters`, today `danswett`) locally from a freshly fetched `origin/main` checkout, never the PR's worktree, rather than trusting the posted status, checks the receipt's poster and target, then merges with `gh pr merge <n> --squash --match-head-commit <sha>`. Once the App posts `merge-rule` and the ruleset pins it to the App, sessions turn on auto-merge once their PR is ready for review, pinned to the head they mean: `gh pr merge <n> --auto --squash --match-head-commit <sha>` (stop it with the `hold` label or `gh pr merge <n> --disable-auto`).
5. **Security-sensitive PRs** need one more step: anything that lets a remote device act on a machine, changes authorization, keys or tokens, or touches the relay, the protocol, CI, the build or how an approval renders (`sensitive_paths` in [quality/merge-rule.yml](quality/merge-rule.yml)), and any PR whose title starts with "Security" or that has the `security` label. Run an independent review (a code-review or security-review agent, never the session that wrote the change), then fix and re-review until clean. The coordinator who ran it then posts the receipt on the exact head reviewed, linking the comment that holds the review:
   ```
   gh api -X POST repos/danswett/agent-bridge/statuses/<sha> -f state=success -f context=independent-review -f description="sec-<n> CLEAN" -f target_url=<review comment URL>
   ```
   A receipt binds to its commit, so any later push needs a new review and a new receipt. `-f state=failure` with the finding as the description blocks the merge until a later success replaces it. Only receipts from the accounts in `independent_review.posters` count.
6. If CI fails on something you did not touch, check whether `main` fails the same way and say so.

Delegated work: when a coordinating session hands work to delegates, each delegate
opens one PR per slice and reports "#<n> ready at <sha>". **Only the designated merge
coordinator merges, or turns on auto-merge: currently Quality**, not each implementation or UI coordinator.
The [current role reference](docs/status.md#running-work) identifies the accepted
owner and handoff evidence while the #170 index cutover is incomplete. Implementation owners submit exact-head
readiness on their PR and yield the merge slot; a ready packet is not a merge grant.

### Before each push

**Every push costs every session.** Each push re-runs CI and Copilot's review, which all sessions share from one fixed pool of concurrent runners; when it's full, everyone's jobs and reviews queue, and reviews time out. So before each push:
- run `pwsh quality/run.ps1` (see Tests);
- review your own diff locally with a code-review agent, and fix what it finds;
- batch every fix for a review round into one push, rather than a push per comment.

Keep a PR a draft while you iterate (from #459 on, drafts run only a fast subset of CI), and mark it ready when it's done. Never push only to re-trigger CI: re-run the failed job with `gh run rerun <run id> --failed`.

## Tests

- `dotnet test AgentBridge.slnx` runs on Windows and Linux in CI.
- Every suite, with its ring and where it runs, is listed in [quality/catalog.yml](quality/catalog.yml); the test plan is [quality/README.md](quality/README.md).
- Before a push, `pwsh quality/run.ps1` runs the suites your change reaches on this machine; `-Explain` shows what it chose and why.
- Where api.nuget.org is blocked, set `AGENT_BRIDGE_NUGET_SOURCE` to a NuGet mirror's index.json, and run.ps1 restores and audits through it ([quality/README.md §4](quality/README.md#4-choosing-tests-from-what-changed)); never commit the mirror into nuget.config.
- If you touch `pwa/`, run its unit and Playwright tests (see `pwa/README.md`).
- New behaviour gets a few focused tests whose names read as sentences, and each one fails without the change.
- Never weaken or skip existing tests to get green.
- Live tests run on disposable VMs ([docs/parity-checklist.md](docs/parity-checklist.md)), never on the owner's machines without the owner's say-so.

## Releases

Every merge to `main` publishes a release-build pre-release of the Windows app
(`v0.1.<run>`, Velopack, with deltas), and installed apps update themselves once the host
is idle. A red `main` CI therefore blocks publishing: fix it quickly. PR builds are dev
builds, and guard G6 limits their HA connector to a disposable HA.

## Relay deploys

- `relay.yml` deploys to staging and smoke-tests it.
- Production is a separate job gated on the owner as required reviewer. Never approve a production deploy without the owner's go-ahead.
- An older run waiting at the production gate blocks newer ones (concurrency group `relay-production`), so cancel superseded runs.
- Infrastructure changes go through `infra/deploy.ps1` (staging), then `-Production` with `-ProductionImage <running digest>` to keep the current image ([docs/relay-deploy.md](docs/relay-deploy.md)).

## Guards you will meet

- **#67:** host management is refused from inside an agent session (exit 4): config changes, sign-in, `-Install`, host stop and restart. The owner runs those at the machine. Hand them over as plain-text one-liners, since they are read on a phone and copied with the PWA's Copy button; encoded commands don't wrap.
- **G5:** a host refuses to type into sessions while agent-ha-bridge runs for the same user, so it is read-only beside it.
- **G6:** a dev build's HA connector writes only to a disposable HA.
- **G7:** `migrate` refuses on the dashboard publication writer.

## Secrets

Never print a key, token, connection string or password into output, logs, commits, PR
text or issues. If a command would echo one, redirect it to a file you never read back. A
leaked signing key once forced a production rotation. This repository is public: keep
home-network details, IP addresses and personal account credentials out of it.

To have gitleaks check each commit for secrets before it is made, run `pre-commit install` once per clone (needs [pre-commit](https://pre-commit.com); see `.pre-commit-config.yaml`).

## Style

- Comments explain *why*, especially what went wrong before; don't restate the code.
- Update the related `docs/*.md` and protocol specs in the same PR.
- Show times in Pacific.
- When a merge changes the project's state, update [docs/status.md](docs/status.md) in the same PR or straight after.
