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

Keep the global lock and shorten what each run does. From the step times above:

- **Fix the evaluator cache first.** Two sampled runs missed it and built for 6 to 7 seconds (about 22% of the
  job). The key hashes the evaluator's own source, which changes often, or the cache is evicted or not saved; the
  cause is unconfirmed.
- Skipping checkout and `setup-dotnet` (about 5 seconds) needs a prebuilt artifact or image, not a cache.
- **That leaves the judge, 10 to 12 seconds**, plus overhead: about 17 to 20 seconds a job, roughly 1.5 to 1.8 times
  today's capacity (**derived** from the timings, not measured after a change).

The brief's "about 35 seconds to seconds, 5 to 10 times" (**quoted**) would need the judge itself skipped for
repeats, keyed by main's sha, the newest CI run's state and the head: a verdict cache. That touches ordering 1 too
(a cached verdict is an old read of main), so it needs the same re-check as Option 1(a). As measured, caching the
build and setup alone does not reach 5 to 10 times.

## Recommendation

Do Option 2's cache fix now (it is small and loses nothing), then Option 1 with the evaluator's main re-check
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
