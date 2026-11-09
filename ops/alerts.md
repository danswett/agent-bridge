# Alert catalog

*Part of the [SRE plan](plan.md) §5. Severities and routing are in plan §5.1.*

Every alert has an id, a runbook and an issue that builds it. The Status column says where each
stands:
- **Built** is in `infra/modules/monitoring.bicep` or `ops-probe.yml`. It goes live when the owner
  next runs `infra/deploy.ps1 -Production` (docs/relay-deploy.md "Monitoring").
- **Planned** isn't built yet.

**Deployment record.** The production monitoring module went live on 2026-11-07 (about 01:15 PT),
deployed by Platform SRE with `az deployment group create` of `infra/modules/monitoring.bicep`
alone (deployment `relay-monitoring-sre-20261107`, owner approval in the SRE session). A full
`infra/deploy.ps1 -Production` what-if that day also showed the unapplied feedback containers
and app updates, so the module went in alone rather than widening the change. It created the
action group `ag-agent-bridge-ops` (owner's address, email only), App Insights, `wt-relay-healthz`,
the seven metric alerts and the $40 budget. The address is not in the repo. Still to do: staging
alerts, probes for `relay.slipgates.com` and staging, blob alerts, a workbook, and a test email.

Thresholds are first estimates for one 0.25 vCPU / 0.5 GiB replica, with tens of devices.
Revisit them after two weeks of data, and when capacity changes (CAP-1).

**All alerts are email only, and none pages at night** (owner, #568, 2026-11-03). Severity sets
the order to read them in, not who gets woken.

**Cost shaped the availability design.** A standard availability test costs $0.0005 a run, so
the five locations every five minutes first planned would cost about $22 a month against the
$40 budget. Instead, A-AVAIL is one test from three locations every ten minutes, about $6.50 a
month. The certificate and DNS checks (A-CERT, A-DNS) run once a day in `ops-probe.yml`, since
they only need to warn weeks ahead.

Signals:
- **AT:** Application Insights availability test;
- **CA:** Container Apps platform metric;
- **LA:** Log Analytics query ([queries/relay.kql](queries/relay.kql));
- **M:** relay metric (OBS-1);
- **AL:** activity log;
- **B:** budget;
- **W:** the GitHub watch, a Logic App in Azure that records an availability result every 5 min;
- **P:** the daily probe ([tools/relay-probe.ps1](tools/relay-probe.ps1) in `ops-probe.yml`), which
  opens or updates one `area:ops` issue and closes it when clean.

## Production relay

| Id | Sev | Signal | Fires when | Runbook | Issue | Status |
|---|---|---|---|---|---|---|
| A-AVAIL | 1 | AT | `/healthz` on `relay.bad-duck.com` (answering `"ok":true` with a valid certificate) fails from 2 of 3 locations within 15 min. Tested every 10 min from West US, East US and West Europe | [relay-unavailable](runbooks/relay-unavailable.md) | #573 | Built |
| A-CERT | 2 | P | The TLS certificate has < 21 days left (renewal should happen about 30 days ahead), or doesn't validate | [certificate-dns](runbooks/certificate-dns.md) | #573 | Built |
| A-DNS | 2 | P | `relay` isn't a DNS-only CNAME to the production app, or `asuid.relay` doesn't hold the environment's verification id: renewal will fail | [certificate-dns](runbooks/certificate-dns.md) | #573 | Built |
| A-REPLICAS | 1 | CA `Replicas` | Maximum < 1 over 5 min | [relay-unavailable](runbooks/relay-unavailable.md) | #572 | Built |
| A-RESTART | 2 | CA `RestartCount` | Maximum > 0 over 15 min. The metric counts restarts since the replica was created, so this means the current replica has restarted, and it clears when a new replica replaces it | [relay-resources](runbooks/relay-resources.md) | #572 | Built |
| A-CRASHLOOP | 1 | CA `RestartCount` | Maximum > 2 over 15 min: a crash loop | [relay-resources](runbooks/relay-resources.md) | #572 | Built |
| A-MEM | 2 | CA `WorkingSetBytes` | Average > 400 MiB (78% of 512) over 15 min | [relay-resources](runbooks/relay-resources.md) | #572 | Built |
| A-CPU | 3 | CA `UsageNanoCores` | Average > 200M (80% of 0.25 vCPU) over 15 min | [relay-resources](runbooks/relay-resources.md) | #572 | Built |
| A-5XX | 2 | CA `Requests` (statusCodeCategory 5xx) | More than 30 in 15 min. A count, because metric alerts can't take a ratio. A deploy's drain produces a few 503s | [relay-unavailable](runbooks/relay-unavailable.md) | #572 | Built |
| A-AVAIL-ORIGIN, A-READY | 2 | AT | Separate tests on the `azurecontainerapps.io` name and `/readyz` | [relay-unavailable](runbooks/relay-unavailable.md) | #573 | Deferred for cost (about $6.50 a month each). The probe checks both by hand |
| A-CHANGE | 2 | AL | A write to the production app, its identity or its vault by anyone other than the two deploy principals and the owner's `deploy.ps1` | [security-events](runbooks/security-events.md) | #572 | Planned. An activity-log alert can't exclude callers, so this needs the activity log in Log Analytics (a subscription-level diagnostic setting) and a log query |
| A-STORM | 2 | LA `reconnect-storm` (then M) | Disconnects per minute > 5× the hour's median for 3 consecutive minutes, ≥ 20/min, and no deploy in the last 15 min | [reconnect-storm](runbooks/reconnect-storm.md) | #574 | Planned |
| A-REFUSALS | 3 | M `relay.refusals` only: the relay logs no refusals, so this waits for OBS-1 | Refusals (4000 bad hello, 4001 token, 4003 proof, 4029 too many devices, 403 origin) > 10× the day's hourly median in an hour, ≥ 50 | [reconnect-storm](runbooks/reconnect-storm.md) | #574, after #569 | Planned |
| A-RATELIMIT | 3 | LA `connections` (disconnect outcome `rate limited`), then M `relay.disconnects` | Admitted sockets closed with 4008 for exceeding the frame limit: ≥ 20 in an hour | [reconnect-storm](runbooks/reconnect-storm.md) | #574 | Planned |
| A-ROSTER | 3 | LA `dependencies` | ≥ 5 roster failures in 15 min (Sev 2 at ≥ 50) | [relay-dependencies](runbooks/relay-dependencies.md) | #574 | Planned |
| A-BLOB | 3 | LA `dependencies` | ≥ 5 blob store failures in 15 min | [relay-dependencies](runbooks/relay-dependencies.md) | #574 | Planned |
| A-PUSH | 3 | LA `dependencies` | Push failures (5xx or exceptions) > 20% of ≥ 10 pushes in 30 min | [relay-dependencies](runbooks/relay-dependencies.md) | #574 | Planned |
| A-SIGNIN | 3 | M `relay.signins` | `refused` > 50% of ≥ 10 sign-ins in 30 min | [relay-dependencies](runbooks/relay-dependencies.md) | #574 | Planned |
| A-CANARY | 2 | AT (canary results) | 2 consecutive canary failures (S2) | [relay-unavailable](runbooks/relay-unavailable.md) | #576 | Planned |
| A-CANARY-SLOW | 3 | AT | Canary p95 round trip > 500 ms for 30 min (S3) | [relay-resources](runbooks/relay-resources.md) | #576 | Planned |
| A-FORWARD | 3 | M `relay.forward.duration` | p99 > 5 ms for 30 min (S4) | [relay-resources](runbooks/relay-resources.md) | #569 | Planned |
| A-SOCKETS | 3 | M `relay.connections` | > 60% of `MaxConnections` (6,000) for 1 h; Sev 2 at 80% | [relay-resources](runbooks/relay-resources.md) | #585 | Planned |
| A-BAKE | 2 | relay.yml production job | The post-deploy bake failed and rolled back on its own (DEP-1) | [relay-bad-deploy](runbooks/relay-bad-deploy.md) | #577 | Planned |

## Stamp-wide

| Id | Sev | Signal | Fires when | Runbook | Issue | Status |
|---|---|---|---|---|---|---|
| A-LOGCAP | 2 | LA `ingestion`, `cap-hit` | Today's ingestion > 80% of the daily cap, or collection stopped (OverQuota) | [log-cap](runbooks/log-cap.md) | #571 | Planned |
| A-BUDGET | 3 | B | The resource group's month at 50%, 80% or 100% of the $40 budget, or forecast over 100%. Emailed by Cost Management, not the action group | [cost](runbooks/cost.md) | #575 | Built |
| A-GHA | 3 | W `github-actions` | GitHub's status page shows Actions or Pages not operational, or a run here has been queued over 30 min, in 2 of the last 3 checks (every 5 min, from Azure). A source that can't be reached counts as no evidence, not an outage | [github-outage](runbooks/github-outage.md) | #658 | Built |
| A-GHA-SILENT | 3 | LA `availabilityResults` | No fully read `github-actions` result in 30 min (none arrived, or one said `unreachable`, `unreadable` or `unknown`): the watch stopped or can't read a source, so A-GHA is blind | [github-outage](runbooks/github-outage.md) | #658 | Built |

## Staging and PPE

Staging has no alerts in the first slice: it scales to zero, so the replica and socket rules
don't apply, and its smoke test already runs on every deploy. To prove a production rule fires,
lower its threshold for a moment (`az monitor metrics alert update -g rg-agent-bridge-relay -n
"<name>" --condition ...`), or disable the rule and send a test notification from the action
group in the portal, then restore it. The PR that adds a rule says which was done.

PPE (#590), once it exists, gets **every** production rule (it's always on) at Sev 3, with its
own availability tests and canary. Its alerts are also promotion evidence: a PPE alert since the nightly cut holds back that cut's
offer to production (plan §6.0).

## Clients and releases

There's no telemetry from machines (design §5.10 keeps it opt-out and aggregated, and it
isn't built), so client health is read on the relay:

| Id | Sev | Signal | Fires when | Runbook | Issue | Status |
|---|---|---|---|---|---|---|
| A-RELEASE | 2 | M connects and refusals by `version` (OBS-2) | The Dev build has refusals or disconnects per device > 3× the Beta build's. This also blocks its promotion (plan §6.3) | [windows-release](runbooks/windows-release.md) | #451, #570 | Planned |
| A-PAGES | 3 | pages.yml | The Pages deploy fails, or its post-deploy hash check fails (REL-4) | [pwa](runbooks/pwa.md) | #582 | Planned |

## Rules for adding an alert

1. It has a runbook that says what to do, not only what it means.
2. Sev 1 is only for symptoms users feel now. Anything else starts at Sev 3 and moves up with
   evidence.
3. It fires once on staging before it goes to production.
4. Its query and labels hold to design §5.10: no account, device, address or name. Alert
   payloads go to email and phones, so they must be just as clean.
5. An alert that fired and needed no action twice in a month is retuned or removed.
