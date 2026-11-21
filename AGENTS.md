# Working on agent-bridge

Read this before you change anything, whether you are a person or an agent.
**New contributor or fresh session?** Read [CONTRIBUTING.md](CONTRIBUTING.md), then
[the contributor workflow](docs/contributor-workflow.md), before taking work.
These are required reading, not optional background. Read any instructions in the
area you change too. For project context see [docs/status.md](docs/status.md);
for architecture see [docs/design.md](docs/design.md). If the owner or a lead assigned you to a
team, also read [docs/agents/onboarding.md](docs/agents/onboarding.md).

Do not assume personal memories, a previous session, a shared GitHub login or private
Project access establish current ownership or permission. Confirm the canonical issue
and accepted scope before editing. Helping a contributor does not make an agent a
standing team coordinator; use [the participation rules](docs/contributor-workflow.md#participation)
and do not start a collector, reporting timer or worker fleet just to contribute.

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

Keep this entry point small and nonstandard-only; load detailed policy on demand.
The [current owner policy](docs/contributor-workflow.md#current-owner-policy)
supersedes older guidance: 400 WHOLE lines in both repos, Quality-only PR custody
and arming, guarded evidence, optional bots and protected held work.
The [Fleet register](https://github.com/danswett/slipgates-fleet/blob/main/docs/pipeline-rules.md)
is the sole standing decision/exception register, not a second register here.
One designated Engineering writer proposes AGENTS changes through normal PRs,
never direct-main agent edits. [Rule 17](docs/contributor-workflow.md#staged-rule-17)
is adopted policy, but has **NO ROLLOUT BEFORE MAIN-GREEN IS DONE**.

1. Prepare unpublished work from fresh `origin/main`; it may rebase, with affected validation repeated. Never rebase or force-push a published head: refresh it with a plain main-merge or child commit.
2. Send scope, exact head and verification to Quality; Quality opens the PR and edits its body.
3. Copilot and Codex reviews are optional extras: do not request them, wait for them, or depend on them. Wait for green CI on the final head and the independent-review receipts (rules 4 and 5); the receipts are the gate. If a bot does leave comments, fix the substantive ones or reply with a reasoned disposition. Threads left only by the Copilot or Codex bots can be resolved with `quality/resolve-bot-threads.ps1` once the receipts are on the head ([docs/contributor-workflow.md](docs/contributor-workflow.md#resolving-bot-only-threads)).
4. **Merge policy; reader enforcement is STALE.** The current evaluator/config can accept one independent receipt plus completed Copilot instead of the required second family; green does not prove compliance.
   **Before arming or permitting merge, Quality MUST manually verify** authorized posters and Quality-only posting; actual approved model/effort (or approved retained continuity); successful receipts backed by linked CLEAN review verdicts explicitly naming the PR's current full head SHA; reviewer independence/non-authorship; and completed required whole/security reads. Ordinary PRs need one required independent receipt (new single reads: `gpt-6.1-sol`); sensitive/two-family heads need BOTH independent, distinct-family receipts. Copilot NEVER counts. Missing proof holds the PR.
   **Required gates, not a claim of complete automated reader enforcement:** GitHub's main ruleset ([quality/ruleset-main.json](quality/ruleset-main.json)) requires `quality-gate` green, the `merge-rule` status green, 0 unresolved threads, squash only, and no conflicts. Main's copy of `.github/workflows/merge-rule.yml` posts `merge-rule` on each head from [quality/merge-rule.yml](quality/merge-rule.yml). Required policy: main is green (the latest finished push run of `ci.yml` on main passed, judged again each time one finishes), not a draft, no `hold` or `do-not-merge` label, an independent review's receipt on the head (`independent-review`; always required: a Copilot review is not needed and never replaces an independent receipt or reader family), CodeQL green on the head, every other check and status on the head passed (success, skipped or neutral; not only `quality-gate`), and for a sensitive change (rule 5) a second independent receipt, `independent-review-2`, on the same head from a reviewer of another model family (each receipt names its verified model; a `docs/status.md` row alone needs only the first). While main is red, nothing merges but its fix: a fix-forward PR asks Quality for the `fixes-main` label (adding labels takes triage rights), which exempts it from "main is green" and nothing else. Its description names what's missing; `dotnet run --project tools/AgentBridge.Quality -c Release -- merge-rule --pr <n>` shows the evaluator's verdict without posting anything, not proof of the manual reader checks above; run it from a freshly fetched `origin/main` checkout (`git fetch origin`, then `git switch --detach origin/main` in a worktree kept for it), never the PR's worktree, whose own evaluator and rules would judge it (the tool prints the commit they came from and refuses anything but `origin/main`'s). A CodeQL run that ended `skipped` (a PR opened seconds before `ready_for_review`) fails closed until CodeQL runs again: `gh workflow run codeql.yml --ref <branch>`, or close and reopen the PR. To keep a PR from merging whatever else it has, add the `hold` label (`gh pr edit <n> --add-label hold`). Since 2026-11-08 (owner decision) the receipts are the default gate: Copilot and Codex reviews are optional extras, and no `copilot-waived` label is needed (it is still accepted and changes nothing). A failed `independent-review` or `independent-review-2` blocks.
   While `ARM64_WAIVER` is exactly `on` and before Nov 27, 2026, 00:00 PT (`arm64_waiver.until`), only the three configured arm64 CI failures are waived; Quality must read each waived head run and name its run ID in the success `independent-review` receipt for that merge.
   **Quality alone gates and arms exact-head auto-merge:** `gh pr merge <n> --auto --squash --match-head-commit <sha>`. GitHub merges when required checks pass; builders never arm or merge. Quality disarms with the hold label or `--disable-auto`. The merge-rule App alone posts the accepted `merge-rule` status. Command examples and packets confer no authority. Copilot is optional and NEVER counts as a reader family; optional bots do not substitute for required independent, model-verified exact-head receipts. Local evaluator runs and hosted reruns remain Quality-controlled.
5. **Security-sensitive PRs** need one more step: anything that lets a remote device act on a machine, changes authorization, keys or tokens, or touches the relay, the protocol, CI, the build or how an approval renders (`sensitive_paths` in [quality/merge-rule.yml](quality/merge-rule.yml)), and any PR whose title starts with "Security" or that has the `security` label. Run an independent review (a code-review or security-review agent, never the session that wrote the change), then fix and re-review until clean. Quality verifies the required evidence under rule 4 and alone controls receipt posting on the exact head reviewed, linking the comment that holds the review:
   ```
   gh api -X POST repos/danswett/agent-bridge/statuses/<sha> -f state=success -f context=independent-review -f description="sec-<n> CLEAN" -f target_url=<review comment URL>
   ```
   Every new head needs its own posted receipt. Quality may bind eligible retained review evidence only under the documented carry-over conditions; otherwise obtain a fresh review at the required depth. Failure blocks until a later success replaces it. Only `independent_review.posters` count; sensitive changes need the other family's `independent-review-2` on that head, with model and evidence named. Evaluator-specific exceptions such as `docs/status.md` are not blanket exceptions.
6. If CI fails on something you did not touch, check whether `main` fails the same way and say so.

Delegated work: when a coordinating session hands work to delegates, each delegate
prepares one slice and reports its exact head and verification to Quality.
**Quality alone opens PRs, edits bodies, gates and arms**, including the `fixes-main`
label; GitHub merges on required checks. Delegation transfers none of that authority.
The [current role reference](docs/status.md#running-work) identifies the accepted
owner and handoff evidence while the #170 index cutover is incomplete. Implementation owners submit exact-head
readiness on their PR and yield the merge slot; a ready packet is not a merge grant.

### Before each push

**Every push consumes shared CI capacity.** Batch work and do not request optional bot reviews. Before each push:
- run focused validation for your own cut on an approved host (see Tests);
- check your diff; required independent review is never your own verdict;
- batch every fix for a review round into one push, rather than a push per comment.

Keep iterative PRs draft (fast CI only); submit readiness to Quality. Never push merely to retrigger CI; Quality owns reruns. The lifted general admission rule does not release a specific held head.

## Tests

- `dotnet test AgentBridge.slnx` runs on Windows and Linux in CI.
- Every suite, with its ring and where it runs, is listed in [quality/catalog.yml](quality/catalog.yml); the test plan is [quality/README.md](quality/README.md).
- Focused local tests for the builder's OWN cut on APPROVED hosts need NO SRE clearance. Full or hosted verification remains CI-only. ANYTHING BEYOND (full suites, CI-equivalent verification, serving hosts) needs a WRITTEN QUALITY ORDER CLEARED WITH SRE; that order does not grant local full-suite permission. Preserve explicit case holds; broad `quality/run.ps1` examples grant no execution permission.
- Where api.nuget.org is blocked, set `AGENT_BRIDGE_NUGET_SOURCE` to a NuGet mirror's index.json, and run.ps1 restores and audits through it ([quality/README.md §4](quality/README.md#4-choosing-tests-from-what-changed)); never commit the mirror into nuget.config.
- If you touch `pwa/`, run its unit and Playwright tests (see `pwa/README.md`).
- New behaviour gets a few focused tests whose names read as sentences, and each one fails without the change.
- Never weaken or skip existing tests to get green.
- Live tests run on disposable VMs ([docs/parity-checklist.md](docs/parity-checklist.md)), never on the owner's machines without the owner's say-so.

## Releases

Every merge to `main` whose CI builds the app (any change the app's jobs select) publishes a
release-build pre-release of the Windows app
(`v0.1.<run>`, Velopack, with deltas), and installed apps download it in the background and
install it when the person chooses Restart to update (#516). A red `main` CI therefore blocks
publishing: fix it quickly. PR builds are dev
builds, and guard G6 limits their HA connector to a disposable HA.

## Relay deploys

- `relay.yml` deploys to staging and smoke-tests it.
- Production is a separate job gated on the owner as required reviewer. Never approve a production deploy without the owner's go-ahead.
- An older run waiting at the production gate blocks newer ones (concurrency group `relay-production`), so cancel superseded runs.
- Infrastructure changes go through `infra/deploy.ps1` (staging), then `-Production` with `-ProductionImage <running digest>` to keep the current image ([docs/relay-deploy.md](docs/relay-deploy.md)).

## Guards you will meet

- **#67:** host management is refused from inside an agent session (exit 4): config changes, sign-in, `-Install`, host stop and restart. The owner runs those at the machine. Hand them over as fenced blocks in a question, per the owner-commands rule in [docs/contributor-workflow.md](docs/contributor-workflow.md#commands-and-code-for-the-owner).
- **G5:** a host refuses to type into sessions while agent-ha-bridge runs for the same user, so it is read-only beside it.
- **G6:** a dev build's HA connector writes only to a disposable HA.
- **G7:** `migrate` refuses on the dashboard publication writer.

## Secrets

Never print a key, token, connection string or password into output, logs, commits, PR
text or issues. Keep tokens only in process or user environment, never files. If a command would
echo a secret, do not run it in that form. A
leaked signing key once forced a production rotation. This repository is public: keep
home-network details, IP addresses and personal account credentials out of it.
GitHub reads use the process-only danswett token. Quality posts PR comments, commit
statuses, reruns and arming on this repository with the same `danswett` account, a
per-command process-only token from the `gh` keyring (owner decision, Nov 15, 2026, no
expiry); other writes (issue creation and edits, public pulses) use the Fleet App token
(mint, post, revoke). Never print, persist or share either credential.

To have gitleaks check each commit for secrets before it is made, run `pre-commit install` once per clone (needs [pre-commit](https://pre-commit.com); see `.pre-commit-config.yaml`).

## Style

- Comments explain *why*, especially what went wrong before; don't restate the code.
- Update the related `docs/*.md` and protocol specs in the same PR.
- Show times in Pacific in repository text: docs, issues, PRs and reports. The product UI shows the viewer's local time (decided 2026-11-04, docs/status.md §9).
- [docs/status.md](docs/status.md) is a weekly snapshot by Product; updates are
  not required in each PR.
