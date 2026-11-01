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
4. **Merge rule:** CI green (SUCCESS, SKIPPED or NEUTRAL), a Copilot review on the exact head, 0 unresolved threads, and `mergeable` MERGEABLE. Then:
   ```
   gh pr merge <n> --squash --match-head-commit <sha>
   ```
   Delete the branch afterwards.
5. **Security-sensitive PRs** need one more step before merge: anything that lets a remote device act on a machine, changes authorization, keys or tokens, or touches the relay. Run an independent review (a code-review or security-review agent), then fix and re-review until clean.
6. If CI fails on something you did not touch, check whether `main` fails the same way and say so.

Delegated work: when a coordinating session hands work to delegates, each delegate
opens one PR per slice and reports "#<n> ready at <sha>". **Only the coordinator
merges.**

## Tests

- `dotnet test AgentBridge.slnx` runs on Windows and Linux in CI.
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

## Style

- Comments explain *why*, especially what went wrong before; don't restate the code.
- Update the related `docs/*.md` and protocol specs in the same PR.
- Show times in Pacific.
- When a merge changes the project's state, update [docs/status.md](docs/status.md) in the same PR or straight after.
