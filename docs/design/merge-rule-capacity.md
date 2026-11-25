# Merge rule capacity: why runs wait for hours, and two ways to fix it

Status: proposed, design only (R8b, Nov 23, 2026 PT). No code, workflow change, variable or deployment is made
or authorized by this document. Sensitive: the merge gate; two independent model families through Quality.

Sources: `.github/workflows/merge-rule.yml`, `tools/AgentBridge.Quality/MergeRuleCommand.cs`,
[quality/README.md](../../quality/README.md) section 4.9, and Actions data read on Nov 23 (the runs and jobs
APIs, read only). **Measured** below means read from that data in this session. **Quoted** means the figure comes
from R8b's brief, which cites the 18eaece1 write-up (`merge-rule-triggers-proposal.md`) and P0; that file could not
be read from this session, so its figures are quoted and not re-derived.

## What happened

Between 10:00 and 11:20Z on Nov 23, 869 `Merge rule` runs were created: 322 skipped, 476 cancelled, 49 succeeded,
21 failed, 1 needed action (**measured**, about 650 an hour; the write-up gives 440 to 770 an hour, **quoted**). The
`evaluate` job has a job-level `timeout-minutes: 10`, which is correct, but it counts from the moment a runner
starts the job. These jobs were **queued**, not running: jobs show `created = started`, an empty runner and zero
steps for hours (run 45152169244: created 10:55:12, cancelled 13:08:20, never started). Others waited and then ran
for 27 to 31 seconds (run 45152099996: job created 10:00:21, started 11:29:02, succeeded). 122 of the 869 lasted
over 15 minutes; successful runs waited 84 to 97 minutes, the oldest cancelled run 188 (**measured**). A run
created at 09:12Z had waited 56 minutes, so the backlog was already there.

Cause: `evaluate` sits in one concurrency group for every run, `merge-rule`, with `cancel-in-progress: false` and
`queue: max` (workflow lines 149 to 152). `queue: max` keeps up to 100 jobs waiting, in order, one served at a
time. About 650 runs an hour arrive and about 100 an hour are served (35 seconds each, **quoted**; the queued
successes finished about a minute apart, **measured**), so the queue only grows and every waiting job still runs
its 30 seconds even when a newer event has made it obsolete. About 25 queued runs were then cancelled at
13:06 to 13:08Z, one a second; who or what did that is not known.

Where one job's 27 to 31 seconds go (**measured**, two runs): set up and tear down about 3, checkout 4,
`setup-dotnet` 1, restore the built evaluator 0 and **Build main's evaluator 6 to 7** (a cache miss in both runs:
the build step ran), mint the App token 1, **Judge and post 10 to 12**.

## What the single group protects

The workflow comment says it: one group for every writer, "no two evaluations read main's CI and post on the same
head at once, so an older read of main can never land after a newer verdict (a red main can't be overwritten by a run
that saw it green)". Concretely:

1. **An older main read never lands after a newer verdict.** The evaluator reads main's CI state once at the start
   of an evaluation (`session.Main`) and posts later. Two evaluations of the same head in parallel could post out of
   order. Before posting, the evaluator re-reads each *pull request* (labels, receipts, checks) so a newer
   pull-request-level verdict is not overwritten, but it does **not** re-read main there (`Reread` fetches the pull
   request only). So the ordering of main reads rests on the lock, not on the evaluator.
2. **Main-red sweeps against per-head runs.** A scoped run that finds main not green widens to every open pull
   request, because a run for another head may have been displaced (GitHub keeps one waiting run per group) and its
   success, posted while main was green, must not stand while main is red. Sweeps (main's CI starting and finishing,
   the 30-minute schedule, dispatch, and every run when `FLEET_MERGERULE` is `off`) judge every open pull request.
   Sweeps and per-head runs therefore write to the same heads, and share the lock.
3. **Admission already coalesces.** The workflow-level group (`merge-rule-admit-...`) keys a run by pull request
   number or head sha, or `backstop`, `dispatch`, `sweep`, so duplicates collapse to one running and one waiting per
   key, and only a new head cancels its own obsolete admission. The job-level lock is the second, global stage.

## Option 1: per-head groups, the newest supersedes

Replace the job-level group with one per head (and per sweep kind), reusing the admission key:

```yaml
concurrency:
  group: merge-rule-judge-${{ <the expression that follows "merge-rule-admit-" above, unchanged> }}
  cancel-in-progress: false
```

and **no `queue: max`**. Behaviour: per key at most one job running and one waiting; a newer arrival replaces the
waiting one (GitHub's default, which `docs/app-windows.md` also relies on for `publish`), and a running job is never
cancelled. So **no run is cancelled past its judge step**: only jobs that have not started are displaced. The
sweep keys (`sweep`, `backstop`, `dispatch`) keep serializing among themselves, and with `FLEET_MERGERULE=off` the
expression already maps every event to `sweep`, which is one group, as today.

Capacity: jobs for different heads run in parallel, and obsolete waiting jobs never run, so the work that reaches
the judge step is bounded by distinct heads in flight rather than by events.

**What it breaks, and what it needs.** Per-head runs and sweeps no longer share a lock, so ordering 1 is lost
between them: a per-head run holding an older read of main can post to head X after a sweep's newer verdict on X.
Options to close it, none of which is workflow-only: (a) the evaluator re-reads main's state (head sha and the
newest CI run's state) just before posting and re-evaluates if it moved, a small change to `MergeRuleCommand`
beside the existing pull-request re-read; or (b) sweeps keep the global lock and per-head runs take it only while
main is not green, which needs the evaluator to say so before the job starts. (a) is preferred. Without it, Option 1
should not ship.

## Option 2: do less work per run, no group change

Keep the global lock and shorten what each run does. The evaluator is already cached in `merge-rule.yml`, keyed on
the build inputs (a key on main's sha would be worse: it would miss on every merge to main). So the cache is not
the lever. R8b's figure is about 33 seconds a run, of which the **judge step is about 22 seconds** (GitHub reads
for each pull request); 205e98bd is profiling it, and its numbers are the basis for this option, not mine.

For the record, the two runs I sampled show a different split: judge 10 to 12 seconds, and a "Build main's
evaluator" step of 6 to 7 seconds that ran (its condition is a cache miss). The runs are few and the judge time
depends on how many pull requests and checks are open, so I do not set them against 205e98bd's profile; whether
those two were misses, and why, is not established here.

What this option can reach depends on the profile: the judge's GitHub reads are the part to cut (fewer or batched
reads, skipping heads whose inputs have not changed). A verdict cache keyed by main's sha, the newest CI run's
state and the head would touch ordering 1 too (a cached verdict is an old read of main), so it needs the same
re-check as Option 1(a). 205e98bd's profile (quoted from R8b's relay; not re-derived here): the judge step is about 22 seconds, and #4022
cuts GitHub reads per run from about 12 to about 4 points, which R8b puts at about 250 runs an hour. That is
the Option 2 capacity figure to use; the brief's earlier 5 to 10 times is not derived here. Against about 650 runs
an hour arriving (measured), Option 2 alone does not clear the queue, which is why Option 1 stays in the
recommendation. The runs that sat in progress for hours were runs that never started (jobs queued, no runner),
as described above.

## The GraphQL budget: when a full judgement may start

The token's budget is 1,000 GraphQL points an hour for the repository, shared by every workflow. One **full**
(`--all`) judgement costs about 115 points (R8b, quoted), so at most about 8 an hour fit even if nothing else
reads. **Rule:** a full judgement starts only when main's CI state changed (a CI push run on main completed, red
or fixes-main flips) or from the 30-minute backstop or sweep cron. Every other event is **scoped** to its head
(#4022).

### Last 24 hours (Nov 22 14:28Z to Nov 23 14:28Z), runs API counts

#4016 (the trigger cut) merged at 14:20:10Z today, so this window is almost entirely **before** the cut. The
first post-cut runs had not finished when I read (0 successes created after 14:20:10Z), so there is no post-cut
measurement yet. #4022 is open, not merged.

| Event | Executed (success + failure) | Skipped / cancelled | Scope on current main |
|---|---|---|---|
| `pull_request_target` | 273 | 0 / 138 | scoped `--pr` |
| `status` | 195 | 2,428 / 117 | scoped `--sha` (the App's own `merge-rule` statuses skip) |
| `workflow_run` | 1,172 | 1,067 / 2,378 | scoped `--sha` for a non-main head; **full** for CI's push run on main |
| `check_suite` | 60 | 0 / 138 | scoped `--sha`; a run on main skips |
| `schedule` | 3 | 0 / 0 | full (a backstop; 3 ran against 48 timer slots, so GitHub is dropping or delaying them) |
| `workflow_dispatch` | 0 | 0 / 0 | full |

How to read it: the counts are filtered runs-API totals and the filtered totals for one event can overlap
(workflow_run skipped + cancelled + success + failure exceeds its unfiltered total, which looks capped), so treat
them as approximate. The runs API does not record the scope, and this token cannot read the job logs (they came
back as a 100-character error), so the scope column is **from the workflow code on main, not from logs**. The
run's branch field is `main` for status, check_suite and workflow_run runs whatever they judged, so it can't
split full from scoped either.

Executed runs total 1,703 in 24 hours, about 71 an hour. At about 12 points for a scoped run (R8b, quoted) that
is about 850 points an hour, at the edge of the 1,000; at about 4 points (#4022) it is about 285. The split between
full and scoped inside those 1,703 is unmeasured.

### Which events still start `--all` after #4016

1. **CI's push run on main, on both `in_progress` and `completed`.** The `workflow_run` list now keeps CI, and
   admission lets only CI's push run on main through for the main branch, which sweeps. CI push runs on main
   were **105** in 24 hours (4.4 an hour, exact from the workflow's runs API). Both the start and the finish
   trigger, so up to about 8.8 full judgements an hour, about **1,000 points an hour**: the whole budget, from this
   one source. The rule asks for the completed run only; the start does not change main's state, so it is the
   obvious next cut (halves this to about 4.4 an hour, about 500 points). Runs waiting in the group coalesce
   some of them, so this is an upper bound.
2. **The 30-minute cron and manual dispatch:** the cron has 2 slots an hour, about 230 points an hour at most
   (115 each); dispatch is manual.
3. **Any scoped run when main is not green.** The evaluator widens a scoped run to every open pull request when
   main's state is not success (`MergeRuleCommand.cs`, "judging every open pull request"). So while main is red
   or pending, **every** pull-request, status and check-suite event costs a full judgement, whatever the
   workflow says. This is the path by which non-main-push events still go `--all`, and it is not closed by
   #4016. #4022 (open) reads the open list light and only the head's pull requests in full, which is aimed at it;
   I have not verified that against the code.
4. **`FLEET_MERGERULE=off`:** every event sweeps. Not the normal state.

So, yes: events other than a main CI completion still start `--all`, through items 1's start event and 3's
widening. The "scope them" patch is the next change (drop the `in_progress` start of CI's push run on main, and
make the widening cheap). It may make per-head groups unnecessary: the points budget, not lock throughput, is
the binding limit once full judgements are rare. That is a judgement, not a measurement: it needs the hours
after #4016 and #4022 to be read before Option 1 is dropped.

## Recommendation

Do Option 2's judge-step reductions as 205e98bd's profile supports, then Option 1 with the evaluator's main re-check
before posting. Do not remove `queue: max` or add per-head groups without that re-check. Do not use
`cancel-in-progress: true` on `evaluate`: it can cancel a job after it has begun posting.

## Rollback

`FLEET_MERGERULE=off` (a repository variable, read on every run) puts back the earlier path in full: the 45-second
settle, a build of main's evaluator and every run sweeping every pull request. It is slower, not safer to rely on
for capacity. For the workflow change itself, revert the commit (Quality pushes workflow changes). A change that
only adds the evaluator's main re-check is inert until the group is changed.

## The 24 hours after the cut

Not available yet. The baseline is the measured section above. After the change, read the same way (the runs API
with a `created` window, then the jobs API): runs created an hour, skipped, cancelled and succeeded; job wait from
`created_at` to the first step; the longest queue; any run with a wait over 10 minutes; and any mass cancellation.
A suggested bar, to be agreed with Quality: the 95th percentile of job wait under 5 minutes.
