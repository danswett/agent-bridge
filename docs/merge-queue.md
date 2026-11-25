# Merge queue: dry run

For [the assigned slice](https://github.com/danswett/agent-bridge/issues/3220)
and [throughput epic](https://github.com/danswett/slipgates-fleet/issues/957).
Planner corrections: [slice 2](https://github.com/danswett/agent-bridge/issues/3228),
stacked on [slice 1](https://github.com/danswett/agent-bridge/pull/3222).
Snapshot: main `8f4e9b12`, Nov 18, 2026, Pacific; no hosted experiment.

| Option | Repository evidence and cost |
|---|---|
| (a) GitHub merge queue | `quality/ruleset-main.json` requires two checks, `quality-gate` and App-pinned `merge-rule`, squash only, no bypass. No workflow currently handles `merge_group`. GitHub requires [merge-group checks](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-a-merge-queue): Quality would need workflow/ruleset changes and a trusted evaluator mapping each synthetic group head to each PR's exact-head receipts. Merely adding the trigger cannot provide that mapping. |
| (b) Shepherd | Existing evaluator already judges PR heads, receipts, main-green and all head checks. Update-branch changes the head, so wait for new-head CI and Quality's carried or fresh receipts before a pinned squash. Reuses today's checks without synthetic-group receipt semantics. A four-minute single landing slot gives a theoretical ceiling of 15/hour, not measured throughput. |

Recommend **(b) first**, then reassess (a) after tiered gating. The in-flight
[infra merge-rule change](https://github.com/danswett/agent-bridge/pull/3207)
was read but is not a dependency: consume the deployed App verdict, never
reinterpret infra failures here. Only P1s block landing; Quality files one
follow-up for P2/P3 findings, not another blocking review round.

`pwsh -NoProfile -File quality\merge-shepherd.ps1` prints JSON only; authenticate
with the approved per-command danswett route. Cursor-paginated GraphQL reads, no polling,
updates, merges, arming, comments, receipt posting or workflow changes.
Pages contain at most 100 PRs; 149 open PRs produce two reads and one complete
plan. A failed page, missing/repeated cursor or budget below 10 percent aborts
without a partial plan. Rules and main's actual ref tip are read only on the
first page; every comparison must target that tip, not the PR's lagging base.
Main movement during pagination holds affected comparisons.
Incomplete files/labels,
forks and unknown comparisons hold. Renames conservatively need two families.
The query uses main's rules (reported as `rulesBlob`), never local PR rules.
Eligibility uses configured sensitive paths, authorized receipt posters and the
latest App status, not optional Copilot credit. The newest status in either
receipt context holds if its creator is unapproved: filtering that status out
could hide an authorized failure that GraphQL no longer exposes.
Main CI is **not read or verified** by this slice. Non-exempt PRs explicitly
HOLD with `Main-green unverified`, even with a green App verdict. Bypassing that
gate requires BOTH a deployed `main_green.exempt_labels` label AND a complete,
nonempty file list entirely within `exempt_paths`, matching the deployed
evaluator's tests-only exception. Renames never qualify. This is conservative,
not proof main is red. Quality still verifies linked
CLEAN verdicts, reader independence/model/scope, and current gates before acting.

Eligible means queue admission now, not permission to execute a printed command.
Otherwise-eligible behind candidates, including a lagging PR base even if its
merge state says CLEAN, propose only an update. Under the owner's Nov 18 19:01
[carry rule](https://github.com/danswett/slipgates-fleet/blob/main/docs/pipeline-rules.md),
Quality proves a plain main-merge, no conflict and no overlap with the PR's own
changed hunks, then re-stamps receipts on the new head; otherwise obtain delta
reads. Changes elsewhere in a PR file need CI, not an automatic new reader.
Old-head statuses never qualify. Always revalidate CI, receipts, main and head.

A future authorized writer runs anywhere, one PR every four minutes, no admin,
no branch deletion or auto-merge arming. Its lock/last-landing record must live
in protected relay state, not one VM; node loss stops work and a successor
revalidates before proceeding. This one-shot dry run has no persistent state.
Fake-only tests: `pwsh -NoProfile -File quality\test-merge-shepherd.ps1`.
