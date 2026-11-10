# Outside contributors (Apple work)

Read this before you open a pull request. It is the safe default until the owner says
otherwise. It sits next to [contributor-workflow.md](contributor-workflow.md), which
still applies.

## The ground rule

You work by **pull request only**. You get no fleet nodes, no relay or roster access, no
secrets and no access to anyone's machine. Your agents may read the public repo and its
docs, nothing else of the fleet. If a task seems to need more, stop and comment on your
PR or issue; the leads route it. Do not look for a way around it.

Fleet access for outside contributors' agents is on the owner's roadmap but has no date
yet (issue #1607). When it arrives, an approved contributor agent can get a limited,
revocable identity (no secrets, no machine login) to read fleet status and message
leads, after device approval (#55) and agent identity rules (#519). Until the owner
says so, nothing here changes.

## What to work on (Product)

- **Open Apple work today:** #1442. It assesses the stacked Apple PRs against current
  `main` and proposes landing slices of 400 changed lines or fewer. It is read-only.
  Nothing else Apple-specific is ready.
- **Pick first:** #1442. Then land the slices it proposes, smallest first, starting from
  the merged foundation (#260; `apple/` and `apple.yml` are on `main`).
- **Scope:** the Apple clients (macOS, iPhone and iPad apps) under `apple/**`, matching
  the parity checklist and the design in #665. Most real Apple slices touch
  `apple/Sources/**`, `apple/Apps/**`, `apple/Package.swift`, the Apple project file or
  scripts, which are sensitive, so expect **two receipts** (see Sensitive paths).
- **No-go:**
  - anything outside `apple/**`;
  - relay, roster, device approval and trust code;
  - `quality/**`, `.github/**`, host delivery and `*.csproj` files (sensitive; two
    receipts; ask first);
  - secrets, signing and Apple Developer account work (owner only);
  - any machine or fleet node.
- Each slice is 400 changed lines or fewer, with acceptance criteria and the three
  machine-free questions below.

## Opening a pull request (Engineering)

- Fork or branch from a freshly fetched `origin/main`. One branch per issue, named after
  it (for example `apple-1234-short-name`).
- Link the issue in the PR body. Keep the PR under about 400 lines; split anything
  bigger.
- List the tests you ran and where (platform, suite).
- Do not arm auto-merge and do not merge. Only Quality merges, after CI and receipts.
- Pushing a new commit drops existing receipts, so reviews are asked for again on the
  new head.

### Receipts

A normal PR needs one `independent-review` from the Engineering Lead (or the Second
Reviewer on request). A sensitive PR needs two, `independent-review` and
`independent-review-2`, from different model families. Reviewers check the real file
list, not what the PR says.

### Sensitive paths (two receipts)

The authoritative list is `sensitive_paths` in `quality/merge-rule.yml`; the plain-terms
list below is only a summary, and the file is the rule. In plain terms:
`AGENTS.md`, `.github/**`, `quality/**`, `build/**`, `infra/**`, all project and build
files (`*.csproj`, `*.props`, `*.targets`, `*.slnx`, `nuget.config`, `global.json`),
`docs/protocol/**`, the protocol, transport, relay, local and injector projects, host
authorization and call-guard code, the host delivery project, the Windows app's
remote-action code, `pwa/app.css`, anything under `apple/` that is a project, source or
app file (for example `apple/Sources/**`, `apple/Apps/**`, `apple/Package.swift`), many
`pwa/js` files (such as `main.js`, `answers.js`, `session-card.js`, `sw.js`, `relay.js`,
`connection.js`), `pwa/package.json`, and any `src` file whose name contains auth,
token, secret, trust, roster, permission, identity, install or update. The file is the
source of truth; when unsure, assume it is sensitive. Most Apple code is therefore
sensitive and needs two receipts.

### Machine-free design

Nothing may be tied to one machine. Before you start, answer the three questions in
[Design notes](contributor-workflow.md#design-notes-nothing-tied-to-a-machine) in the PR
description: where it runs, what happens when that node leaves, and where its state
lives. A design that cannot answer all three is not ready.

### Getting a review

Put the PR number and head SHA in a comment on your PR or issue. The leads read GitHub
The leads read GitHub and route it. Engineering reads it and posts receipts on that exact head.
For a design question, comment on the issue before writing code.

## How CI and merging work (Quality)

- **Only Quality merges.** You open a pull request; you do not merge it and you do not
  turn on auto-merge. Quality arms auto-merge on the exact head it has reviewed, and the
  merge happens by itself once the rule is met. Never ask for or expect a push grant.
- **The merge rule.** A PR merges only when all of this is true on its current head:
  - it is not a draft and carries no `hold` or `do-not-merge` label;
  - every CI check passes, including CodeQL, and the latest finished CI run on `main` is
    green;
  - the `independent-review` receipt from Engineering is on that exact head. A sensitive
    PR also needs `independent-review-2` from a different model family.
- **Receipts are per commit.** Any new push removes them, so push once when you are
  ready, not after each small edit. Receipts come from Engineering, never from Quality or
  from you.
- Copilot review and Codex never block a merge. Do not wait for them.

### What a clean PR looks like

- One purpose, small (under 400 changed lines), with "Part of #N" or "Closes #N" in the
  description.
- A draft while you iterate; marked ready when CI is green on your branch.
- Branched from the latest `origin/main`, with a clear list of what you ran and what you
  did not run (for example: "Mac build not run here").
- Tests for the behaviour you changed, and the related docs updated in the same PR.
- Touches nothing under `.github/`, `quality/`, `build/`, `infra/`, `**/*.csproj` or
  `AGENTS.md` unless the issue asks for it.
- No secrets, tokens, machine names or personal data in code, comments, logs or the
  description. Times written in Pacific.

### If CI fails

- Read the failing job before you rerun it. A real failure is yours to fix.
- A failure with no failed job, or a `quality-gate` failure that says every job passed,
  is usually a GitHub Actions incident: say so in a PR comment and Quality reruns it.
- A flaky test needs a CI run link to be filed as a flake; "fails on my machine" alone is
  not enough.

### How to ask for a rebase

- If your PR has conflicts or is behind `main`, rebase onto fresh `origin/main` yourself,
  push once and tell Quality the new head SHA.
- If you are stuck on a conflict, comment "needs rebase help" with the files in
  conflict. Quality or the owning lead replies on the PR.
- After a rebase you get a new head, so receipts are asked for again. That is normal.

## Who to message

You cannot message fleet sessions directly. Put questions on your PR or issue as a
comment; the leads read GitHub and route it. Merging is only by Quality
after CI and receipts.
