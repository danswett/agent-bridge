# Onboarding for agent sessions

Read this first if you are an agent session joining agent-bridge, whatever team you're on. It explains how
the agents here work together. [AGENTS.md](../../AGENTS.md) holds the engineering rules, and this page doesn't
repeat them, so read both.

When you have read both, answer the [first message](#the-first-message) to your team's lead
session, and wait for the lead to check your answers before you start any work.

## 1. Who does what

One person (the owner) and a set of long-running coordinator sessions run the project. Each
coordinator owns a lane, and you reach it by putting its tag in a GitHub comment.

| Role | Owns | Tag |
|---|---|---|
| Owner | Every decision of scope, priority and policy. Anything the owner said is the final word. | |
| Head of Product | What gets built and in what order: the epics and their rank in [#696](https://github.com/danswett/agent-bridge/issues/696). Exceptions to the freeze. | `[to:product]` |
| Head of Engineering | How the work gets done: grants for new work, staffing, and **every independent review and its receipt**. | `[to:engineering]` |
| Quality | Merge coordination: admitting PRs to auto-merge, the landing order, holds, and keeping main green. | `[to:quality]` |
| TPM | Issue triage, an hourly plain-English digest, and pipeline speed. | `[to:tpm]` |
| Creative director | Words, visual design and accessibility rulings ([#477](https://github.com/danswett/agent-bridge/issues/477)). Not a merge authority. | `[to:creative]` |
| Build teams | Daily, Client Experience (CX), Connection, Trust, Platform, SRE and **Apple** (the Mac host and Apple clients). Each has a lead session and its own delegates. | `[to:daily]`, `[to:cx]`, `[to:connection]`, `[to:trust]`, `[to:platform]`, `[to:sre]`, `[to:apple]` |

`[to:all]` reaches every session. Use it rarely.

## 2. Where things live

- **[#689](https://github.com/danswett/agent-bridge/issues/689), the hub:** check-ins, decisions and anything for more than one team.
- **[#696](https://github.com/danswett/agent-bridge/issues/696), the rank:** Product's epics in order, plus the rules for changing the order. Work on what is ranked; if you can't find your work there, ask Product before starting.
- **[#929](https://github.com/danswett/agent-bridge/issues/929), how work moves:** the speed and safety rules, including Amendment 1 and its freshness check, Amendment 1a.
- **[docs/status.md](../status.md):** where the project stands, plus the decision log (§9).
- **Each PR:** its exact head, CI, reviews, threads and the reasons behind each fix. Keep the PR's own history there, not on the hub.

## 3. How a PR lands

1. **Ask before starting something new.** A new PR, a new writer on someone else's PR, or a change of scope needs a grant from Engineering and must be work Product has ranked. Fixing your own open PR doesn't need anyone's go-ahead.
2. **Work in your own worktree,** and keep the PR a draft while you iterate (drafts run a fast subset of CI). Batch fixes so each push answers a whole round of review.
3. **Mark it ready** once CI is green on the final head and Copilot has reviewed *that* head. A Copilot error, such as a billing failure or a timeout, counts as no review, so request another with `gh pr edit <n> --add-reviewer "@copilot"`.
4. **Engineering reviews it without being asked.** It sweeps for ready PRs every few minutes, ordered by Product's rank, and posts a comment with its verdict. A CLEAN verdict is tagged `[to:quality]`; a NOT CLEAN one goes to your team with its findings.
   - **Only Engineering's receipt counts.** The receipt is the `independent-review` status from an account in `independent_review.posters`. A "CLEAN" comment from you or your own reviewer isn't a receipt, however careful the review behind it.
   - Fix every finding, or reply on its thread with your reasoning, then push once. Engineering then reviews only what changed (a "delta"), rather than starting over.
5. **Quality admits it** with auto-merge, after checking freshness: if main has changed your files since your CI ran, the branch is updated first (Amendment 1a). Engineering carries the receipt across a merge-only update.
6. **Arm auto-merge yourself** for your own ready PR (AGENTS.md rule 4), pinned to the head you mean.

## 4. What catches new agents out

- **Stale CI is not green.** A PR whose CI ran before recent merges to main can pass on its own and break main once merged; this happened twice in one afternoon. If your CI is more than a few hours old, or main has changed your files, rebase onto current main before asking for review.
- **The freeze.** While more than 40 non-draft PRs are open, nobody opens a new ready PR except for P0 work or a Product exception. Keep work in drafts.
- **One writer per PR.** Never push to a PR you don't own, and never force-push over someone else's work.
- **Every thread must be resolved** before merging. A reply needs real text. If a reply reads `System.Collections.Hashtable[…]`, your posting script broke, so post it again.
- **A red main stops the line.** If `ci.yml` fails on main, nothing but its fix merges. If your CI fails on something you didn't touch, check whether main fails the same way and say so.
- **The guards are real.** #67 refuses host management from an agent session, and G5–G7 protect the live agent-ha-bridge. Hand the owner a plain one-line command instead of working around a guard.
- **Live tests** run only on disposable VMs, never on the owner's machines without the owner's say-so.

## 5. Staying in sync

- **Run the wake watcher** ([tools/agents/wake-watch.ps1](../../tools/agents/wake-watch.ps1)) so you react within a minute or two, not on a 15- or 60-minute timer. It exits, which wakes your session, when any of these happens:
  - a comment is tagged for your team or `[to:all]`;
  - a PR's head moves;
  - a PR's CI finishes;
  - a PR is merged or closed.

  Run it async, handle the wake, then restart it:
  ```
  pwsh tools/agents/wake-watch.ps1 -Team apple -Prs 406,405
  ```
  `-IgnoreIds` skips comments that someone edits in place, such as rolling digests.
- **Restart with `-Since`.** `-Since` defaults to the moment the watcher starts, so a comment posted while it was down (after a wake, while you handled it) would be missed. Restart with `-Since` set to the previous start time, or the wake's timestamp, in UTC; the watcher prints the effective value on start:
  ```
  pwsh tools/agents/wake-watch.ps1 -Team apple -Prs 406,405 -Since 2026-11-06T22:10:00Z
  ```
- **Use REST for anything that polls.** Every session shares one GitHub account's GraphQL budget, about 5,000 points an hour. `gh pr view`, `gh pr checks`, `gh pr comment` and `gh issue comment` all spend it, and one afternoon it ran out and nothing could post for minutes. Read with `gh api repos/danswett/agent-bridge/pulls/<n>` and `.../commits/<sha>/check-runs`, and post with `gh api repos/danswett/agent-bridge/issues/<n>/comments -F body=@file`.
- **Check-ins** go on #689: 8 lines or fewer, saying what landed, what's in review, what's blocking you and who you need. Edit a comment in place rather than posting a correction.
- **Acknowledge** a message tagged for you within 5 minutes, even if only to say when you'll act on it.

## 6. The Apple lane

The Apple team (Welshofer's sessions) owns the Mac host (tmux delivery, process discovery,
Keychain), the Apple clients, and their tests. It follows everything above, plus the following:

- **Rank first.** Many Apple PRs are drafts from 11-04. Product decides which proceed and where Apple sits in #696; until then, start nothing new.
- **Shared code is shared.** A change to `src/AgentBridge.Protocol`, to the host's delivery and parsing, or to anything in `sensitive_paths` affects every client. Those changes get an ENTIRE review, and Trust or Connection may need to agree.
- **Mac-only checks.** CI doesn't run on macOS. Say in the PR exactly what you ran on a Mac, and on which OS version, so the reviewer can tell what was checked by hand.

## The first message

Paste this into each new session before it starts work. The session replies to its lead with
the answers, and the lead checks them before the session begins.

```
You are joining agent-bridge as part of the <team> team. Before any work:
1. Read AGENTS.md and docs/agents/onboarding.md in full.
2. Reply, in 10 lines or fewer, with:
   a. your team, its tag, and its lead session;
   b. who decides what you work on, and who grants new work;
   c. what counts as an independent-review receipt, and who posts it;
   d. what you do when your CI is older than recent merges to main;
   e. how you hear about comments tagged for your team;
   f. which GitHub calls you use to poll and to post, and why;
   g. the one rule that outranks the rest.
3. Then wait for your lead to confirm before starting.
```
