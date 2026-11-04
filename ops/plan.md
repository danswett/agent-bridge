# Operating Agent Bridge: the SRE plan

*Written 2026-11-03, Pacific. Tracking issue: [#568](https://github.com/danswett/agent-bridge/issues/568).*

This is how the relay and everything else we deploy is watched, changed and repaired. It
builds on design §5.10 (observability, decided 2026-10-27) and on
[docs/relay-deploy.md](../docs/relay-deploy.md), which says how the relay is built and
deployed. Read this plan for the reasons behind each choice, and the files beside it for
day-to-day work:

| File | For |
|---|---|
| [slo.md](slo.md) | What "working" means, measured: SLIs, SLOs and the error-budget policy |
| [alerts.md](alerts.md) | Every alert: its condition, severity, where it goes and its runbook |
| [runbooks/](runbooks/README.md) | What to do when an alert fires, step by step |
| [incidents.md](incidents.md) | Severities, the first 15 minutes, and post-incident reviews |
| [queries/relay.kql](queries/relay.kql) | Saved Log Analytics queries for the relay |
| [tools/relay-probe.ps1](tools/relay-probe.ps1) | Read-only outside-in check of a relay (health, version, CORS, TLS, DNS) |

## 1. What we operate

| Service | Where | Who depends on it | How it changes |
|---|---|---|---|
| **Relay** (production) | Container Apps `agent-bridge-relay`, one replica, `relay.bad-duck.com` | Every host, app and PWA: presence, frames, push, blobs, roster, sign-in | `relay.yml`: staging, then production on the owner's approval |
| Relay (staging) | `agent-bridge-relay-staging`, scales to zero | The deploy pipeline's smoke test | Every relay PR and every merge |
| Relay (PPE, **planned**, #590) | `agent-bridge-relay-ppe`, always on | The lab's nightly end-to-end tests (quality ring 3), a dogfood account, the canary | The nightly cut, frozen until the next |
| Lab VMs (quality §6) | `rg-agent-bridge-lab`, restored from snapshots for each run | Ring 3 nightly end to end; ring 4 rehearsals | Quality's plan; this plan only consumes their results |
| Blob store, roster store | Storage accounts beside each app | Attachments and transfers over 256 KiB; the machine list | `infra/deploy.ps1`, run by a person |
| Key Vaults | `kv-abrelay-prod-wus2` and the staging vault | Token and VAPID keys | Key rotation (docs/relay-deploy.md) |
| PWA | GitHub Pages; `/dev/` and `/app/` paths planned (#451) | Phones and browsers | `pages.yml` on every push to `main` touching it |
| Windows app and host | Velopack pre-releases on GitHub Releases, `win` and `win-arm64` channels (Dev); Beta channels planned (#451) | Every migrated machine | Every merge to `main` publishes; installs update themselves within about 30 min |
| HA connector | Inside each host | The owner's HA dashboard | Ships with the host |
| Old relay stamp | The free-trial subscription | Nothing, once it reads 0 online for a day | Waiting to be decommissioned (docs/relay-deploy.md) |

**Dependencies outside our control:** GitHub (sign-in device flow, Releases, Pages,
Actions), Azure (Container Apps, Storage, Key Vault, Entra), Cloudflare DNS for
`bad-duck.com`, and the browser push services (Apple, Google, Mozilla, Microsoft).

**What breaking looks like, worst first:** the relay is unreachable (every remote device is
blind, and phones get no pushes); a bad Windows release reaches every machine (hosts can
fail to start, and only the person at the machine can fix them); a bad PWA (phones can't act,
but hosts keep working); a dependency degrades (blobs, roster or push fail while the rest
works).

## 2. Principles

1. **No PII, no UII, ever** (design §5.10). Metrics are aggregates with allowlisted,
   low-cardinality labels; logs carry a random per-socket tag at most; probes use canary
   identities. Each new signal in this plan is checked against that rule, and the relay's
   tag-allowlist test enforces it.
2. **Alert on symptoms users feel, and page only for them.** Sev 1 means "devices can't
   reach each other now". Causes (CPU, a dependency's error rate) are Sev 2–3 and wait for
   the working day.
3. **Every change reaches a few before it reaches all.** Relay: staging, then PPE (dogfood),
   then production, each with a bake. Clients: the Dev channel, then Beta (#451), with
   nightly end-to-end tests between them. Features: a flag, on for Dev first.
4. **The fastest mitigation is undoing the change.** Every deployable thing has a one-step
   way back that has been rehearsed: a digest for the relay, a halt and an expedited
   roll-forward for the app (installs never downgrade), promoting the previous build for the
   PWA, and a flag for a feature.
5. **Automate the mitigation only when its blast radius is smaller than the problem's.**
   Rolling back to the image production just ran is safe; scaling the relay out isn't
   (two replicas split presence, see docs/relay-deploy.md "Why one replica").
6. **Don't spend the shared CI pool on monitoring.** Synthetic checks run on Azure
   (availability tests, a Container Apps job), not on GitHub runners, which every session
   shares (AGENTS.md "Before each push").
7. **Lean costs stay lean.** Everything here fits the `lean` stamp's budget of about
   $15–30/month; what doesn't (managed Prometheus and Grafana) waits for `standard`.

## 3. Service levels

The full definitions are in [slo.md](slo.md). In short, over 28 days:

| SLO | Target | Measured by |
|---|---|---|
| Relay availability | 99.5% (about 3.4 h of downtime) | The availability test on `/healthz`, 3 locations every 10 min (#573) |
| End-to-end round trip | 99% of canary echoes complete, p95 < 500 ms | Synthetic canary (SYN-1) |
| Forward latency | p99 in-node forward ≤ 5 ms | `relay.forward.duration` (OBS-1), design §5.8's target |
| Deploy recovery | Devices back within 3 min of a relay deploy, every deploy | The bake (DEP-1) |
| Release safety | No fleet-wide release later halted (REL-2) | The halt list in `rollout.json` |

99.5% rather than more because there's one replica in one region by design. A deploy
costs each device one reconnect of a few seconds. Platform maintenance restarts the replica
too. Higher targets need the `standard` stamp.

## 4. Observability

### 4.1 Relay metrics ([#569](https://github.com/danswett/agent-bridge/issues/569))

`RelayMetrics` already has the right instruments (connections, connects by kind,
disconnects by outcome, refusals by close code, frames and bytes, undeliverable frames,
pushes, sign-ins, blobs, roster), but **no exporter**: they go nowhere today. The fix is the
Azure Monitor OpenTelemetry exporter for **metrics only**. Request and dependency traces stay
off, because they would carry URLs and client addresses.

The instruments to add are those the SLOs and alerts need:
- a forward-latency histogram;
- admission duration;
- rate-limit hits, including ASP.NET's own rejections, which are invisible today;
- drain state;
- the runtime's GC, working set and thread pool.

At `lean`, metrics go to Application Insights inside the stamp's Log Analytics workspace,
with its own daily cap. At `standard`, the same instruments go to managed Prometheus (design
§5.10).

### 4.2 Fleet versions ([#570](https://github.com/danswett/agent-bridge/issues/570))

Staged rollouts need a health signal per version. The relay knows only a device's kind. With
an optional `v` and `os` in the hello, both bounded to a fixed set or a strict pattern
(anything else reads `other`), connects and disconnects can be split by version. That answers
two questions:
- How many devices run a release?
- Does it reconnect more, or get refused more, than the last one?

Design §5.10 already allows both labels.

### 4.3 Logs ([#571](https://github.com/danswett/agent-bridge/issues/571))

The relay logs JSON lines with no identities (a random tag per socket), and Container Apps
ships them to Log Analytics.

The risk is the **0.1 GB/day cap**. Once it's reached, ingestion stops until the workspace's
daily reset hour,
which is exactly when a reconnect storm is producing the volume, so the logs and every
log-based alert go blind together. Three changes cover it:
- alert at 80% of the cap, and when collection stops;
- sample connect and disconnect lines in a storm (metrics keep the exact counts);
- keep the saved queries in [queries/relay.kql](queries/relay.kql). They were checked
  with the `Kusto.Language` parser, and are to be validated against live data in ALR-3.

Clients keep their own local logs (host 2 MiB with one rotation, hooks 1 MiB). Nothing
leaves a machine. Design §5.10's anonymous client histograms come later, opt-out, and
aggregated before they leave the device.

The relay's logs already catch one class of client bug: a device reconnecting on a fixed
cycle. #403 (DSWETT-HOME's host reconnecting about every 199 s, 117 times in an afternoon) was
found by reading another machine's host log. The `session-lengths` query shows such a cycle as
a cluster of disconnects at one duration, and A-STORM catches the large cases.

## 5. Alerting

### 5.1 Severities and routing

| Sev | Means | Example | Goes to | Response |
|---|---|---|---|---|
| **1** | Users can't use the product now | Relay unreachable from most locations; replica count 0 | Phone push (Azure mobile app) and email; at night too, if the owner agrees | Start [incidents.md](incidents.md) within 15 min |
| **2** | Degraded, or Sev 1 is likely soon | Restarts; memory > 80%; certificate < 21 days; storm | Email and phone push, working hours | Same day |
| **3** | A cause to look into | One dependency failing; CPU high; budget at 80% | Email | Within a week, or at triage |
| **4** | Information | Old stamp idle; weekly cost | Issue comment or digest | None |

Alerts go to one action group (`ag-agent-bridge-ops`). Design §5.10's dogfooding goal,
alerts as Agent Bridge push notifications, needs the relay's `ops` API (FLT-1). Until then,
Azure's mobile app push is the phone path. Staging alerts are at most Sev 3 and go by email.

### 5.2 Platform alerts ([#572](https://github.com/danswett/agent-bridge/issues/572))

These need no code, only Container Apps metrics and the activity log. A new
`infra/modules/monitoring.bicep` defines:
- replicas < 1;
- restarts;
- CPU and memory against the 0.25 vCPU / 0.5 GiB size;
- the 5xx rate;
- writes to the production app or vault by anyone but the deploy principals.

Thresholds are in [alerts.md](alerts.md).

### 5.3 Outside-in availability ([#573](https://github.com/danswett/agent-bridge/issues/573))

Built in two parts, shaped by cost: a standard availability test costs $0.0005 a run, so five
locations every five minutes would cost about $22 a month against a $40 budget.
- **Availability (A-AVAIL):** one Application Insights standard test against
  `https://relay.bad-duck.com/healthz`, every 10 min from three locations (West US, East US, West
  Europe). It goes through DNS, the certificate and ingress, as a device does. That's about $6.50
  a month. The alert fires when 2 of 3 locations fail within 15 min.
- **Certificate and DNS drift (A-CERT, A-DNS):** once a day, `ops-probe.yml` runs
  [tools/relay-probe.ps1](tools/relay-probe.ps1) against production. It checks the certificate's
  remaining life, the CNAME, and the `asuid` TXT value its renewal depends on
  (docs/relay-deploy.md warns renewal fails silently if the Cloudflare record is moved, proxied, or
  loses its TXT). Problems go on one `area:ops` issue that closes itself when clean.

Separate tests on the `azurecontainerapps.io` name and `/readyz` are deferred, for cost. The
probe checks both by hand in an incident.

### 5.4 Dependency and abuse alerts ([#574](https://github.com/danswett/agent-bridge/issues/574))

These are log queries until the metrics ship (OBS-1), then metric alerts:
- roster, blob store and push failures;
- reconnect storms (disconnects at many times the baseline, with `online` falling, outside
  a deploy);
- refusal spikes (a scan, or a client build sending bad hellos);
- sign-in failure rates.

### 5.5 Synthetic canary ([#576](https://github.com/danswett/agent-bridge/issues/576))

`/healthz` shows the process answers, not that two devices can talk. A **canary account**
with two canary devices runs `relay-smoke --canary` against production every 5 minutes:
- presence;
- a Noise channel echo;
- a 1 MiB blob round trip.

It runs as a Container Apps Job in the stamp, so no GitHub runner is used, and reports its
results as Application Insights availability results. It holds no person's data, and its
credentials can act on nothing but the canary account.

## 6. Safe deployment

### 6.0 Rings and the nightly train ([#590](https://github.com/danswett/agent-bridge/issues/590), with [#451](https://github.com/danswett/agent-bridge/issues/451) and quality/README.md §2, §7)

Every change passes through rings: services and clients alike, and the same evidence gates
both. The client rings are already decided (#451, owner, 2026-11-03):
- Dev and Beta channels on every platform, with Stable later;
- Beta is the default;
- Beta builds are promoted nightly after ring 3;
- installs never downgrade.

The quality plan's ring 3 runs the installed product end to end on lab VMs every night
(quality §6). The relay gets matching **service rings**:

| Service ring | Relay app | Gets a build | Who's on it | Answers |
|---|---|---|---|---|
| **staging** (exists) | `agent-bridge-relay-staging`, scales to zero | Every relay PR and every merge | The smoke test; ring 3's relay load and DAST | Does it start, and pass the smoke test? |
| **PPE / dogfood** (#590) | `agent-bridge-relay-ppe`, always on, production configuration, its own keys and stores | **The nightly cut only**, then frozen until the next cut (a hotfix dispatch is the one exception) | Accounts signed in to it: the lab's test accounts (ring 3), a dogfood account, the canary (§5.5) | Does it hold real traffic for a day, and pass end to end? |
| **production** | `agent-bridge-relay` | Last night's cut, once it has soaked on PPE, offered nightly and approved by the owner | Everyone else | |

PPE takes one build a night, not every merge. With several sessions merging each day, a
per-merge PPE would rarely run any digest for 12 hours. Each deploy would also send 1012 to
every lab device in the middle of ring 3.

**Two independent axes.** A device's **channel** (#451) decides which build it runs, and is set
per device. A **relay ring** decides which relay an account's devices use. It's set per account,
by where they sign in: a host's relay is in its profile (`relay.txt`), not its channel, and
presence is per relay, so every device of an account must be on the same one
(docs/relay-deploy.md "Hostnames, Pages and hosts"). So:

| | Production relay | PPE relay |
|---|---|---|
| **Dev** channel | The owner's machines as they start (#451: existing copies start on Dev), and any opted-in machine | The lab's test accounts (ring 3); the dogfood account |
| **Beta** channel | New installs (Beta is the default); the owner's machines once moved | The dogfood account's devices, if it keeps one on Beta for compatibility coverage |

The PWA therefore takes its relay from the sign-in, not from its path. Both `/dev/` and
`/app/` default to production. A PPE sign-in is an explicit choice on the sign-in screen,
remembered per install, with a warning that every device of the account has to be on that
relay. Pages' Content-Security-Policy allows both relays. That's a requirement on #590 and
#451(h): a phone that follows the Dev link must never land, unasked, on a relay where its
account's machines don't exist.

Today the owner is production's only real user, so dogfood can't simply mean "the owner's
account on PPE": that would make PPE the owner's production. Until other people use
production, the **dogfood account is separate**: the rehearsal VMs, any machine the owner
chooses, and a phone signed in to PPE. A machine moved to it leaves the owner's main view, along
with its sessions, so no machine running a coordinator session (DEV-VM1, DEV-VM2) is a
candidate. Once there are other users, the owner's own account moves to PPE, which is classic
dogfooding.

**The train, each night** (around 01:00 PT, after main's last ring 2):
1. **Cut:** the newest `main` commit with a green ring 2 becomes tonight's candidate.
   - Its relay digest goes to PPE, with the smoke test and bake (§6.1), and stays there until
     the next cut.
   - Its clients are already on Dev (every merge publishes there).
2. **Ring 3:** lab VMs install tonight's Dev build, sign in to **PPE** with test accounts, and
   run the end-to-end scenarios, with the Dev PWA under Playwright. PPE doesn't change during
   the run.
3. **Clients:** if ring 3 is green, Beta gets tonight's clients, and `/app/` gets tonight's
   PWA. This is automatic and nightly, as #451 and quality §7 say, and never waits for a relay
   approval.
4. **Relay:** **last night's** cut has now soaked about 24 h on PPE. It is offered to production
   if:
   - its ring 3 passed;
   - PPE had no automatic rollback and no alert since;
   - the canary on PPE is green;
   - no `release-blocker` issue is open.

   The approval summary shows that evidence. After approval, production bakes (§6.1).
5. **Hotfix:** a dispatch with a stated reason skips only the waiting. It still goes through
   staging and PPE smoke tests.

**Ordering without coupling.** Clients reach Beta a day before the relay that was built with
them reaches production, so a client must never depend on a relay change that production may
not have yet. A client feature that needs a new relay capability checks that the relay
advertises it in `ready`, as blobs already do (`blobs: null` without a blob store), or sits
behind a flag (§7) until production has it. Protocol changes are additive (§6.6). That way
neither train waits for the other.

A relay change now takes about a day and a half from merge to production, unless hotfixed.
That's slower, but production then only ever gets a build that has carried real traffic and
passed end to end.

### 6.1 Relay: bake and automatic rollback ([#577](https://github.com/danswett/agent-bridge/issues/577))

At a deploy, every device is told to reconnect (1012), so the clearest health signal is that
**the devices come back**. The PPE and production jobs will:
1. record `online` before rolling;
2. after the smoke test, watch for 10 minutes: version steady, no restarts, and `online` at
   80% or more of the pre-deploy count by minute 3;
3. on failure, roll back in the same approved job to the digest it replaced, and open an
   incident. Each app checks the target against **its own** revision history: production's
   `rollback-target.sh` stays production-only (the #418 guard). A digest that only PPE ran
   reaches production through the approved promotion, never as a rollback target. PPE gets a
   check of its own, against PPE's revisions.

The rollback returns to an image production just ran, so its blast radius is smaller than
the bad deploy's (§2.5). The same issue automates cancelling superseded runs that are waiting
at the production gate.

The current pipeline stays as it is: staging smoke, Trivy, attestation, the owner's approval
and rollback by digest.

### 6.2 Reconnects that don't stampede ([#578](https://github.com/danswett/agent-bridge/issues/578))

Hosts back off from 1 s to 30 s with no jitter, so after a deploy every host retries at the
same instants. That's harmless for 11 devices, but it is a reconnect storm the relay causes
itself at design scale, and the per-IP connect limit can trip for devices behind one NAT. The
fix is full jitter in every client, plus a spread after 1012 that the relay can widen later
without a client release.

### 6.3 Client channels: health-gated promotion ([#451](https://github.com/danswett/agent-bridge/issues/451))

Today a merge reaches every machine within about 30 minutes, which is the largest blast radius
we have. #451 builds the Dev and Beta channels for every platform. Ops adds three things to
its promotion gate:
- **per-version health:** refusals and disconnects per device for the Dev build, from OBS-2's
  version label, must not exceed the Beta build's;
- **the halt list** (§6.4) must not name the build;
- **no hidden coupling to the relay:** a client feature that needs a new relay capability
  checks that the relay advertises it, or is behind a flag (§6.0, "Ordering without
  coupling"). So Beta promotion never waits for a relay approval;
- **the pair Beta will actually run:** ring 3 tests tonight's clients against tonight's relay,
  but Beta runs them against **production's** relay, which is older. So promotion also needs
  tonight's clients to pass against the digest production is running now. That runs two ways:
  - #583's compatibility job runs that image in CI;
  - a short ring 3 leg runs a lab test account signed in to production.

A promotion re-publishes the same bytes, so a build that's stopped is simply not promoted.
(This replaces the separate canary and fleet rings first proposed in #579, which duplicated
#451.)

### 6.4 Halt and roll forward ([#580](https://github.com/danswett/agent-bridge/issues/580))

Installs never downgrade (#451). So a bad build is **stopped** by not promoting it and by
hiding it, and **fixed** by rolling forward. `rollout.json`, published beside the releases,
lists halted versions:
- the updater never installs a halted version, even one already downloaded;
- if the document can't be fetched, it installs nothing new, rather than guessing;
- a machine already on a halted build gets the fix through an **expedited promotion** of the
  revert: Dev, then Beta at once, with the reason recorded.

An automatic downgrade to last-known-good would be faster for a host that can't start, but it
breaks the owner's no-downgrade rule. It's proposed only as an owner-approved exception, and
only from a halted version (#580, decision 7 on #568). Without it, a machine whose new host
won't start is reached by a roll-forward sooner instead: REL-3 in §8.

### 6.5 PWA ([#582](https://github.com/danswett/agent-bridge/issues/582))

#451 gives the PWA two paths: `/agent-bridge/dev/`, published on every merge, and
`/agent-bridge/app/`, promoted, which is the URL installed phones use. Both take their relay
from the sign-in, defaulting to production (§6.0). Ops asks that:
- `/dev/` deploys from `workflow_run` of a green main CI, for the commit CI tested;
- `/app/` is promoted with Beta after ring 3 (§6.0).

To roll back `/app/`, promote the previous build. The service worker's content hash makes
installed PWAs pick up the restored files on their next load ([runbooks/pwa.md](runbooks/pwa.md)).

### 6.6 Compatibility ([#583](https://github.com/danswett/agent-bridge/issues/583))

Parts update on different schedules, so the policy is N-1:
- the relay and the PWA support hosts and apps from the last 7 days of releases;
- changes within protocol v1 are additive.

A ring-2 job tests both directions of skew:
- the previous published host against the new relay and PWA;
- the new host and PWA against **the relay digest production is running** (pulled by digest
  and run in the job), which is the pair Beta will get (§6.3).

Once versions are in the hello (OBS-2), the relay can refuse a client below a floor with a close
code that reads "update required".

### 6.7 Infrastructure changes

The current rules stay: `infra/deploy.ps1 -WhatIf`, staging first, then `-Production` with the
running digest, and PSRule advisories. New monitoring resources (§5) arrive the same way, and
can't touch the app's revision.

## 7. Safe feature rollout ([#584](https://github.com/danswett/agent-bridge/issues/584))

There's no feature-flag mechanism today. The design:

- **Flags are evaluated on the client.** The relay stays blind to which features a machine
  uses.
- **Sources, highest first:**
  1. a local override (`features.<name>` in host config), for the owner at a machine;
  2. the remote `features.json` (beside `rollout.json`, fetched with the update check);
  3. the compiled default, which is off for anything new.
- **Rules per flag:** `enabled`, `channels` (`dev`, `beta`, `stable`, #451), `minVersion`, and
  `percent`. The percentage bucket is a hash of the device's public key and the flag's name:
  stable for a device, different per flag, and never sent anywhere.
- **Kill switch:** setting `enabled: false` reaches every machine within one update interval
  (30 min). When that is too slow, the relay's `ready` frame can carry a `features` epoch
  that makes clients fetch now. If the document is unreachable, clients use the last
  document they cached, then the defaults.
- **Relay-side features** are `Relay__Features__<Name>` app settings: a new revision, but no
  image build.
- **Hygiene:** every flag names the release by which it's removed, and a test lists flags
  past that date.

A risky feature (a new delivery path, a protocol capability, an HA publication change) ships
dark. It turns on for Dev, where the nightly end to end and the dogfood account exercise it
against PPE. Then it turns on for a percentage of Beta, then for everyone.

## 8. Automatic mitigation

| Fault | Mitigation | Status |
|---|---|---|
| Relay process hangs or dies | Liveness probe restarts it (`/healthz`, 3 × 10 s) | **In place** |
| Relay revision stopping | 1012 to every device, readiness fails, new sockets 503 (RelayHub) | **In place** |
| Relay over its socket cap | Kestrel refuses upgrades past `MaxConnections` | **In place** |
| Abusive client or IP | Connect, sign-in and frame rate limits; per-account device cap | **In place** |
| Bad relay deploy | Bake and automatic rollback (§6.1) | DEP-1 |
| Reconnect storm | Client jitter (§6.2); log sampling (§4.3) | DEP-2, OBS-3 |
| Bad Windows release | Not promoted past Dev (§6.3); halted, and fixed by an expedited roll-forward (§6.4) | #451, REL-2 |
| A host that won't start after an update | The app notices (3 failed starts, or no `ready` in 10 min), reports `update.outcome = failed` to the PWA, and checks for a newer build every 5 min instead of 30, so an expedited roll-forward (§6.4) reaches it quickly. Going back to the previous version only with the owner's exception to never-downgrade ([#581](https://github.com/danswett/agent-bridge/issues/581)) | REL-3 |
| A misbehaving feature | Flag kill switch (§7) | FLG-1 |
| Log cap reached | Sampling keeps volume down; the alert fires before the cap | OBS-3 |
| Certificate or DNS drift | Alert 21 days ahead (§5.3); renewal is the platform's | ALR-2 |

**Not automated, on purpose:**
- **Scaling out:** a second replica splits presence (docs/relay-deploy.md).
- **Key rotation:** it signs every device out; a person decides.
- **Failing readiness under load:** with one replica, an unready replica is a total outage.
  Overload is shed by refusing new upgrades (503), never by `/readyz`.

## 9. Capacity, fleet and cost

### 9.1 Capacity ([#585](https://github.com/danswett/agent-bridge/issues/585))

The binding limit is memory per idle socket on one 0.5 GiB replica, capped at 10,000 sockets
(design §5.9). Production holds 11 (2026-11-03). The plan:
- measure memory per socket on staging;
- alert at 60% and 80% of the cap, and at 70% of memory;
- review monthly in `ops/capacity.md`.

The trigger to start the `standard` work (homes and forwarding, S9) is a sustained 30% of the
cap, or a forecast to cross 60% within 4 weeks, because that work takes weeks.

### 9.2 Fleet ([#586](https://github.com/danswett/agent-bridge/issues/586))

There are two views, kept apart on purpose:
- **Aggregates** (Azure workbook): devices by version, OS, kind and ring, and adoption per
  release.
- **The owner's own machines** (the Operator section in the app and PWA, design §7.1): each
  machine's version, ring, update outcome and last seen.

These come end-to-end from the machines, never from the relay, so the relay never learns
which machine runs what.

### 9.3 Cost ([#575](https://github.com/danswett/agent-bridge/issues/575))

The relay runs on a Visual Studio subscription with a monthly credit. If that credit runs out,
the subscription is disabled and the relay stops, so cost is an availability risk:
- a budget on the resource group, alerting at 50%, 80% and 100%, and on forecast;
- a monthly cost report;
- the old free-trial stamp (about $19/month) goes once
  `pwsh ops/tools/relay-probe.ps1 -Target old` reads 0 online for a day. It read 0 on
  2026-11-03.

## 10. Incidents ([#587](https://github.com/danswett/agent-bridge/issues/587))

[incidents.md](incidents.md) defines severities, the first 15 minutes (confirm with the probe,
check for a recent change, roll back first), communication, and a blameless post-incident
review for every Sev 1 and 2, using [templates/](templates/). Issue forms and a public status
page, fed by the availability tests and canary, follow in #587.

## 11. Roadmap

| Phase | Goal | Issues |
|---|---|---|
| **0 (this PR)** | The plan, SLOs, alert catalog, runbooks, queries, the probe | #568 |
| **1: know when it's broken** | Platform alerts; outside-in tests; metrics exported; log cap guarded | ALR-1 #572, ALR-2 #573, OBS-1 #569, OBS-3 #571, ALR-4 #575 |
| **2: contain bad changes** | PPE ring and the nightly train; relay bake and auto-rollback; health-gated client promotion (with #451); halt and roll forward; PWA gated on CI | PPE #590, DEP-1 #577, #451, REL-2 #580, REL-4 #582, OBS-2 #570 |
| **3: end to end, and self-repair** | Canary; dependency alerts; local rollback; jitter; flags; compatibility | SYN-1 #576, ALR-3 #574, REL-3 #581, DEP-2 #578, FLG-1 #584, REL-5 #583 |
| **4: scale and polish** | Capacity model; fleet view; incident forms and status page | CAP-1 #585, FLT-1 #586, INC-1 #587 |

The train in §6.0 depends on Quality's ring 3 (the lab and nightly end to end, quality §8
phase 3) and on #451's channels. This plan supplies the relay ring those tests run against
(PPE) and the health signals that gate promotion. It doesn't duplicate them.

Phase 1 needs the owner's decisions on alert routing and Application Insights. Phase 2 needs
decisions on PPE's population and the relay production cadence. #568 lists them all. Relay
changes (OBS-1, OBS-2, SYN-1, DEP-1, PPE) are security-sensitive under AGENTS.md and get an
independent review before merge.
