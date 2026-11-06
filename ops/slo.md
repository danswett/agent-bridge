# Service levels

*Part of the [SRE plan](plan.md) §3. Times are Pacific; windows are rolling 28 days.*

An SLO is a promise we can measure. The error budget is what's left of it, and it decides
how fast we change things: while there's budget, ship; once it's spent, ship only fixes.

## SLIs and SLOs

| # | SLI (what is measured) | Source | SLO (28 days) | Budget | Status |
|---|---|---|---|---|---|
| S1 | **Relay availability:** successful `/healthz` checks ÷ all checks, from 3 locations every 10 min, on `relay.bad-duck.com` | Availability test `wt-relay-healthz` (#573) | **99.5%** | about 3 h 22 min | Built (the test); reporting planned |
| S2 | **End to end:** canary echoes completed ÷ attempted, every 5 min (presence, channel echo, 1 MiB blob) | Synthetic canary (SYN-1, #576) | **99%** | about 6 h 43 min | Planned |
| S3 | **Round-trip latency:** canary echo round trip | Canary | **p95 < 500 ms**, p99 < 1 s, in 99% of 1-hour windows | 7 bad hours | Planned |
| S4 | **Forward latency:** in-node receive-to-send time | `relay.forward.duration` (OBS-1, #569) | **p99 ≤ 5 ms** (design §5.8) | n/a: an objective, alerted on breach | Planned |
| S5 | **Deploy recovery:** relay deploys after which `online` regained ≥ 80% of its pre-deploy count within 3 min | Bake step (DEP-1, #577; `infra/bake-production.sh`) | **100%**: any miss rolls back and opens an incident | 0 | Built (the bake); reporting planned |
| S6 | **Push delivery:** push forwards answered 2xx, or 404/410 (a gone subscription), ÷ all | The `push to {Host}: {Status}` log line (`push-outcomes` in queries/relay.kql), which keeps the exact status. The `relay.pushes` metric records only the status class, so a VAPID failure (401/403) and normal expiry (404/410) look alike there. OBS-1 adds `gone` as an outcome of its own | **99%** | 1% | Planned |
| S7 | **Release safety:** Beta (and later Stable) releases not later halted | `rollout.json` (REL-2, #580) | **No halts on Beta in 28 days.** A halt on Dev is the system working | 0 | Planned |

Why these numbers:
- **99.5%, not more.** There's one replica in one region, by design (presence is in memory).
  A deploy costs each device one reconnect of a few seconds, and platform maintenance
  restarts the replica too. 99.9% (40 min a month) would be spent by a bad afternoon,
  without the `standard` stamp's redundancy to earn it.
- **S2 is lower than S1** because it adds the blob store, Key Vault and the canary's own
  network, each with its own failures.
- **S4** is design §5.8's target, and is far inside S3: almost all of a round trip is the
  internet, not the relay.

**What S1 can't see:** an outage of one network path that our test locations don't share.
S2 and real reports cover that.

## Error-budget policy

The baseline is the nightly train (plan §6.0):
- the relay reaches production as last night's cut, after its PPE soak and the owner's approval;
- clients reach Beta after ring 3.

A lower budget tightens that train; it never loosens it.

| Budget left (28 days) | What changes |
|---|---|
| > 50% | Normal: the nightly train as in plan §6.0. Hotfix dispatches allowed, with a stated reason |
| 25–50% | A relay cut needs **two** nights on PPE (about 48 h) before it's offered to production, and the owner checks the last production bake before approving the next. Beta promotion is unchanged (ring 3 still gates it) |
| < 25% | Production gets only cuts whose changes are fixes or reliability work for the relay and the host's connection path. Beta promotion pauses for client changes that touch the connection path, unless the owner promotes one by hand |
| Spent | No relay feature cut goes to production until the budget recovers above 25%, or the owner accepts the risk in #568 with a reason. Hotfixes for the incident that spent it are still allowed |

Any one incident that spends more than 20% of a budget gets a post-incident review
([incidents.md](incidents.md)), whatever its severity.

## Reporting

Until the dashboards exist, the monthly review in `ops/capacity.md` (CAP-1, #585) records
each SLO's attainment. Once the availability tests are live, the 28-day number for S1 comes
from Application Insights' Availability blade, and S2 and S3 from the canary's results.
