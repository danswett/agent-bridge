# Contributing to Slipgates

Welcome. The product is Slipgates; this repository and its `AgentBridge.*` names are
still `agent-bridge`. You can contribute as a person, with your own agents, or as part
of an explicitly accepted team role. You do not need the owner's personal agent
memories or access to the private GitHub Project to understand the process.

## Before your first change

1. Read [AGENTS.md](AGENTS.md), whether you use an agent or not. It owns safety,
   worktree, validation and merge requirements. Do not touch live installations,
   credentials, hooks, trust settings or services as part of getting started.
2. Read [the contributor workflow](docs/contributor-workflow.md). It owns where
   updates go, claiming work, participation and handoff procedures.
3. Find your canonical issue and check [Product priorities](https://github.com/danswett/agent-bridge/issues/696).
   Check its recent comments, linked PRs and accepted owner before starting. Propose
   scope there; if someone already owns overlapping work, agree a split or handoff
   with them first. An agent's assignment must remain within its contributor's
   accepted scope.
4. Create your own worktree from freshly fetched `origin/main`, following
   [AGENTS.md's Git rules](AGENTS.md#git). Never reuse another contributor's branch
   or worktree. Read the relevant design, source, tests and area instructions.

An ordinary contribution does **not** enroll you or your agents in the standing
coordinator team. See [participation](docs/contributor-workflow.md#participation);
do not copy a coordinator's timer, delegate count or machine access.

## Make and verify a focused change

Use the existing patterns and add focused tests for changed behavior. Update related
documentation in the same PR. [quality/README.md](quality/README.md) explains the
test harness and [quality/catalog.yml](quality/catalog.yml) lists suites.
Run `pwsh quality/run.ps1 -Explain` to see the selection, then
`pwsh quality/run.ps1` before pushing, as required by
[AGENTS.md](AGENTS.md#before-each-push). Its local result is not a substitute for CI:
report skipped or unavailable checks explicitly.

If a prerequisite is missing, report exactly what failed and what you need; do not
change host setup, permissions or credentials without the person's approval.
Live-system and host-management boundaries still apply even when a test or setup
guide suggests otherwise.

## Open a PR and finish

Follow [AGENTS.md's PR rules](AGENTS.md#pull-requests), including local review,
batched pushes, exact-head evidence and any independent review. The
[PR template](pull_request_template.md) prompts for scope, verification and handoff
information; filling it out is not proof that checks or reviews passed.

Keep scope and dependencies on the issue, and head-specific evidence on the PR.
Submit readiness to the designated merge coordinator. Only arm exact-head auto-merge
where AGENTS.md permits it; readiness is not a grant to hand-merge.
If you cannot set a label, post a receipt or enable auto-merge, ask the coordinator
on the PR rather than changing credentials or finding a bypass.

When stopping or changing ownership, leave
[a handoff](docs/contributor-workflow.md#handoffs), even if the work is incomplete.
After landing, update the canonical issue with the outcome and follow the owned
worktree cleanup rules.

## Find the right reference

| Need | Read |
|---|---|
| Safety, Git, tests and landing requirements | [AGENTS.md](AGENTS.md) |
| Ownership, communication and handoffs | [Contributor workflow](docs/contributor-workflow.md) |
| Current role holders and acceptance links | [Status: running work](docs/status.md#running-work) |
| Priority or a change to the order of work | [Product, #696](https://github.com/danswett/agent-bridge/issues/696) |
| Active-team dispatch and escalation | [Shiproom, #689](https://github.com/danswett/agent-bridge/issues/689) |
| Root routing index, with cutover still incomplete | [#170](https://github.com/danswett/agent-bridge/issues/170) |
| Architecture and area-specific design | [docs/design.md](docs/design.md) and the affected area's docs |
| Test selection and CI | [quality/README.md](quality/README.md) |
| Live-service operation, not contributor setup | [ops/README.md](ops/README.md) |

If records conflict, are inaccessible or appear stale, use
[the conflict procedure](docs/contributor-workflow.md#conflicting-or-unavailable-information).
Do not infer permission from silence or historical handoffs.

## Check that this guide works

The [fresh-context onboarding exercise](docs/contributor-onboarding.md) tests whether
a new person and their agents can find and apply this process without a private briefing.
Report confusing steps on the canonical onboarding issue,
[#1045](https://github.com/danswett/agent-bridge/issues/1045).
