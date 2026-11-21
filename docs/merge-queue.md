# Merge queue, slice 1: dry run

For [the assigned slice](https://github.com/danswett/agent-bridge/issues/3220)
and [throughput epic](https://github.com/danswett/slipgates-fleet/issues/957).
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
with the approved per-command danswett route. One GraphQL query, no polling,
updates, merges, arming, comments, receipt posting or workflow changes.
The bounded 100-PR snapshot fails closed if incomplete; incomplete files/labels,
forks and unknown comparisons hold. Renames conservatively need two families.
The query uses main's rules (reported as `rulesBlob`), never local PR rules.
Eligibility uses configured sensitive paths, authorized receipt posters and the
latest App status, not optional Copilot credit. Quality still verifies linked
CLEAN verdicts, reader independence/model/scope, and current gates before acting.

Eligible means queue admission now, not permission to execute a printed command.
Behind candidates propose only an update. Under the owner's Nov 18 19:01
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
