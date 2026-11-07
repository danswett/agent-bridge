# Relay scale-out and capacity

Status: design proposal for #1148. Part of #1148. Docs only: no relay change, no deploy, no scale change. Any relay deploy or replica change needs owner approval (#55).

Audience: Engineering, SRE, Product, the owner.

## 1. Summary

Today production runs exactly one relay replica (0.25 vCPU, 0.5 GiB, `minReplicas` 1, `maxReplicas` 1 in `infra/regions/westus2.bicepparam`). That is deliberate: who is online, and which socket belongs to which device, lives in the memory of that one process. A second replica would split accounts: a phone on one replica could not reach a host on the other.

This note proposes how to go past one replica, how to prove when we need to, and how to do it safely.

Recommendations:

1. **Pick a shared routing layer outside the relay (a backplane), not sticky routing.** It matches the owner's preference that everything is stateless where possible: any replica can serve any device and replicas are disposable. Sticky routing stays as the fallback (section 3).
2. **Keep the one thing that cannot be shared: the open socket itself.** Everything else about a device (which replica holds it, presence, roster) moves to the shared layer or to storage that already exists.
3. **Measure before building.** SRE runs the staging load test in section 7 once #1134's telemetry is merged and exported. The scale-out trigger is the measured per-replica ceiling, with an alert at 60 percent of it.
4. **Ship through the rings** (section 10) with one-step rollback, starting with staging at two replicas before production ever runs more than one.

5. **Plan for many regions from the start** (section 11): a relay stamp per region, devices connect to the nearest, and the directory is a replicated, routing-only store, so relay compute can run anywhere without degradation.

Nothing here is approved to build beyond what section 12 records: the owner decided to build the code dark (flag off, no new resources, no new monthly cost). Turning anything on in production (a second replica, a backplane resource, autoscale) still needs owner approval (#55) and the load test.

## 2. What the relay keeps in memory today, and why

The owner asked for a list of the state the relay must hold in memory. From `RelayHub.cs`, `Roster.cs` and `RelayMetrics.cs`:

| State | Where | Can it leave memory? |
| --- | --- | --- |
| The open WebSocket for each device | `accounts`: account to device to connection | **No.** A socket lives in one process. This is the only state that must stay. |
| Per-socket send lock and receive buffers (16 KiB receive buffer, control-frame buffer) | `Connection`, `RelayHub` | No. Tied to the socket; it dies with it. |
| Per-socket frame window (200 frames per second limit) | `Connection.WindowFrames` | Stays local. The limit is per socket, so it needs no sharing. |
| Presence: which devices of an account are online, and the presence version | `accounts`, `presenceVersion` | **Yes.** Derived from the sockets; with several replicas it must be the union across replicas. |
| Device-to-replica routing (which replica holds device X) | Implicit today: there is only one replica | **Yes.** This is the new shared state. |
| Machine roster cache | `MachineRoster` in `Roster.cs`, backed by blob storage | Already externalised. The cache is a copy and can be rebuilt on connect. |
| Per-IP connect limit (120 per minute), sign-in and push limits | In memory | Can stay per replica (the effective cap is the limit times replicas; see section 8) or move to the shared layer later. |
| Metrics counters | `RelayMetrics` | Local by design; the exporter aggregates them. |

After this design the only per-replica state is the sockets and what hangs off each one. Losing a replica loses sockets, which devices already know how to reopen, and nothing else.

## 3. Option A: sticky routing by account

Idea: every device of an account is sent to the same replica (for example by hashing the account id), so the in-memory design keeps working inside each replica.

Container Apps ingress cannot route by account id inside a WebSocket upgrade. We would need a routing tier in front of the replicas, or the "home and forward" scheme in design §5.8: each account has a home replica, and a device that lands elsewhere is forwarded to the home. Container Apps replicas have no stable addresses to forward to, so `docs/relay-deploy.md` says this step needs a different host (AKS, or a VNet where replicas can reach each other).

Pros:
- Smallest relay code change: the in-memory model stays.
- No extra hop for frames between devices of one account.
- No new always-on paid service.

Cons:
- **Not stateless.** Each replica holds the truth for its accounts.
- Needs stable addressing or a routing tier, so a platform change or a new component.
- Uneven load: one busy account pins one replica. Rebalancing means moving an account, which drops its sockets.
- Replica loss takes every account homed there fully offline until devices reconnect and a new home is chosen; choosing homes consistently is itself shared state.
- Deploys and scale-in must move accounts, so rolling revisions are harder than for a stateless pool.

## 4. Option B: shared backplane

Idea: replicas are interchangeable. Any device connects to any replica. A frame for a device on another replica goes through a shared layer.

There are two parts: a **directory** (which replica holds device X, so presence can be answered) and a **bus** (carry a frame to that replica).

| Option | Fit | Added latency | Cost shape | Complexity |
| --- | --- | --- | --- | --- |
| Azure Cache for Redis (key per device with expiry for the directory; a channel per replica for the bus) | Covers both parts | Low, around a millisecond inside a region | Fixed monthly cost for the smallest tier; always on | Moderate. We own key layout, expiry and reconnect logic. Pub/sub is fire-and-forget: a frame sent while a replica is unreachable is lost, which matches today's "undeliverable" behaviour. |
| Azure Web PubSub | Owns the sockets, so the relay stops owning them | A hop on every frame in and out | Per-unit and per-message; grows with traffic | High. Connection model, admission and rate limits all move, and authentication checks move onto a new service. Not a drop-in. |
| Azure Service Bus (topic per replica) | Durable, ordered | Higher; tens of milliseconds is plausible | Per-operation; cheap at low volume | Wrong shape. We do not want to queue terminal output for offline devices. Fits push or roster events, not live frames. |

Pros of a backplane:
- **Matches the owner's stateless preference.** Replicas are disposable; autoscale and rolling deploys need no account moving.
- Even load, since a device lands wherever the platform sends it.
- Replica loss is a reconnect storm, not an outage for a subset of accounts.
- Fits Container Apps as it is: replicas talk to the shared layer, not to each other.

Cons:
- A new shared service to run, secure and pay for, and a new failure point. If it is down, cross-replica delivery stops; devices on the same replica still work. That is the degraded mode to design for.
- An extra hop for frames that cross replicas. Same-replica frames are delivered directly, so with few replicas much traffic never touches it.
- Presence needs care: entries expire, and a crashed replica's devices must leave the directory fast so phones do not show dead hosts.
- The backplane needs its own secret in Key Vault and managed-identity access, and carries frame bytes (section 9).

### Recommendation

**Choose Redis as the first backplane**, for the directory plus a small per-replica frame channel. It is the smallest product that gives both, adds about a millisecond, and avoids moving socket ownership (Web PubSub) or bending a durable queue into a live bus (Service Bus). Revisit Web PubSub only if socket count, not routing, becomes the real problem.

Optional stepping stone so Engineering can cut two PRs: ship the **directory** first (presence across replicas), then the **bus**.

## 5. Deploys and replica loss

### Today

Per `docs/relay-deploy.md`: on shutdown the relay sends close code 1012 to every socket, fails `/readyz` while draining, and refuses new sockets with 503. Devices reconnect with backoff from 1 to 30 seconds. State is rebuilt on connect: presence is rebroadcast and the roster reloads from storage. Production has one replica and no zero-traffic second revision, so a deploy is a short gap while every device reconnects.

### With more than one replica and a backplane

- **Rolling revision.** Replicas are replaced one at a time. A draining replica fails `/readyz`, stops accepting sockets, sends 1012, and removes its directory entries. Its devices reconnect to the others, which are already serving. At most one replica's worth of devices reconnects at a time.
- **Reconnect storm control.** The per-IP limit (120 new sockets per minute) and client backoff are the guard. The load test must measure reconnects per second a surviving replica absorbs; that number sets the safe rollout step.
- **Replica loss (crash, node move, out of memory).** Its sockets drop without a close frame; devices notice and reconnect to a healthy replica within their backoff window. Its directory entries expire on their own (design goal: a few seconds), so presence heals without a clean shutdown. Frames for a device on a dead replica count as undeliverable, as for any offline device. Nothing is persisted per replica, so only in-flight frames are lost.
- **Backplane loss.** Each replica keeps serving its own sockets. Cross-replica delivery stops until it returns. The relay reports this (metric and alert) and does not crash. The owner decided (section 12) that it stays up and alerts rather than failing `/readyz`, because failing readiness on every replica would turn a partial degradation into a full outage.
- **Ordering.** Frames from one sender to one target stay in order only while placement is stable: the sender socket and the target socket each stay on one replica, and the path (direct when local, the bus otherwise) does not change. Across a reconnect, a move to another replica or a region failover, order is not guaranteed; frames in flight around the move may arrive late or be dropped, and the protocol's own sequence handling (not the relay) must tolerate that. The load test measures reordering during these events (section 7).
- **Split-brain and fencing.** A device can reconnect to replica B while replica A still holds its stale socket (A has not yet noticed the drop). Each directory entry therefore carries an **epoch** that increases on every registration. A registration with a higher epoch wins; when A sees a newer epoch for a device it holds, it closes its stale socket (code 1012) and stops delivering to it. Delivery to a device always goes to the entry with the highest epoch, so a stale holder never receives new frames after the new one is registered. This is the same rule as "a newer connection replaces an older one" that the relay applies locally today, made global.
- **Account cap across replicas.** The 32-devices-per-account cap (`RelayHub.cs`, enforced from the local `accounts` map) and device replacement only see local sockets. With a backplane, admission counts the account's registered devices in the directory: the directory entry set per account is the source of truth, and registering the 33rd device is refused or replaces the oldest by the same rule as today. Races between two replicas admitting at once can briefly exceed the cap by a small amount; the directory resolves it by evicting the lowest-priority duplicate on the next heartbeat. The load test includes a concurrent-admission check. The per-replica 10,000-socket cap stays per replica (it protects that process), so total capacity is that cap times replicas.
- **Heartbeat and re-registration herd.** Each replica refreshes its devices' directory entries on an interval shorter than the expiry. After a backplane outage, replicas must not all re-register everything at once: re-registration is spread with random jitter over a window sized from the load test, and entries are written in batches. A replica that finds its entries expired re-registers them quietly rather than closing sockets.

## 6. Autoscale

Current limits: production 1 to 1, staging 0 to 1.

Proposed rules, only after approval and the load test:

| Setting | Production | Staging | Reason |
| --- | --- | --- | --- |
| Minimum replicas | 1 until the owner approves a second replica (section 12); 2 after that, once the backplane is on | 0, or 2 during a load test | Hosts hold sockets open, so scale-to-zero is wrong in production. Two removes the single point of failure and makes deploys rolling, at added cost. |
| Maximum replicas | Start at 3, raise from measurements | 3 | A small cap limits cost and reconnect storms if a rule misfires. |
| Scale-out trigger | Connected sockets per replica at the alert level (the lower of 60 percent of the measured ceiling and the N-1 level, below); CPU as a second trigger | Same | For mostly idle connections, sockets are the real capacity signal. |
| N-1 headroom | Alert level = the lower of 60 percent and (N-1)/N times 80 percent of the ceiling: 50 percent at 2 replicas, 60 percent from 3 on | Same | If one replica is lost (or drains in a deploy), the survivors take its sockets. At 60 percent on two replicas the survivor would be at 120 percent. Reconnects use jitter (client backoff 1 to 30 seconds) and the per-IP limit, and the load test confirms the survivors absorb a lost replica's reconnects. |
| Cost guard | Existing monthly budget alert plus the max replica cap. The current budget is 40 (`budgetAmount` in `westus2.bicepparam`); a second replica plus a Redis tier will likely exceed it, so the owner raises the budget as part of the approval to switch the backplane on. Price the exact figure before then | Same | A runaway rule cannot spend past the cap. |
| Scale-in | Only when sockets stay under about 30 percent of the ceiling for a long window | Same | Scaling in drops sockets. Avoid flapping. |

Safety rules:
- Never scale in below the minimum during a deploy.
- A replica drains (section 5) before removal; the termination grace period is at least the drain time measured in the load test.
- The 0.5 GiB memory limit is raised only if the load test shows it is the ceiling.

## 7. Load-test definition (SRE runs it on staging)

Purpose: find, per replica, the number of connected devices and the frame rate at which the relay stops meeting its targets, and turn that into the ceiling that sets the scale-out threshold and alert.

Where: staging (`agent-bridge-relay-staging`), never production, with synthetic accounts and `test`-kind devices. No real user data.

Prerequisites: #1134 merged and exported (forwarding-latency histogram, connect-time histogram, live-sockets gauge by device kind). Staging pinned to one replica at production's size (0.25 vCPU, 0.5 GiB) so the number carries over.

### What SRE drives

Each run holds a fixed shape for a fixed time (suggest 15 minutes after ramp).

1. **Idle ramp.** Connect devices in steps (for example 500, 1,000, 2,000, 4,000, 8,000), mostly idle, over many synthetic accounts (up to 32 devices each). Use many source addresses or raise the per-IP limit on staging only.
2. **Active traffic.** At about half the idle ceiling, a share of accounts exchange frames at a steady rate. Use small control frames and larger output frames up to the maximum frame size, and the 200 frames per second per-socket limit.
3. **Burst and reconnect.** Drop all sockets at once and reconnect: the reconnect rate absorbed and time to healthy. This sets the deploy step.
4. **Churn.** Steady connects and disconnects on top of active traffic.
5. **Soak.** Hold the highest passing shape for a few hours to find memory growth.

### What is measured (counts and sizes only)

| Measure | Source |
| --- | --- |
| Connected devices per replica | live-sockets gauge (`relay.connections`) |
| Frames and bytes per second forwarded | `relay.frames.forwarded`, `relay.bytes.forwarded` |
| Undeliverable frames, refusals by close code | `relay.frames.undeliverable`, `relay.refusals` |
| Forwarding latency p50, p95, p99 | #1134 histogram |
| Connect time p50, p95, p99 | #1134 histogram |
| **Memory per socket** | replica working set at each step minus the zero-socket baseline, divided by sockets; record the slope between steps |
| CPU, restarts, out-of-memory kills, garbage-collection pauses | Container Apps platform metrics |
| Reconnect rate absorbed, time to recover | the burst run |

### Pass criteria (proposed; SRE may adjust)

Over the whole hold: p99 forwarding latency under 250 ms (p95 under 100 ms), p99 connect time under 2 seconds, no unintended refusals or undeliverable frames, no restarts or out-of-memory kills, CPU under 80 percent sustained, memory stable within 5 percent across the soak.

### The numbers that come out

- **Ceiling (devices per replica):** the largest passing shape, reported separately for idle and active.
- **Frames per second per replica:** the highest passing aggregate rate at the chosen socket count.
- **Memory per socket:** in KiB, with the baseline. A code-based guess is a few tens of KiB (a 16 KiB receive buffer plus per-connection objects), which would let 0.5 GiB hold low tens of thousands of sockets before memory binds. This is only an estimate; CPU or frame rate may bind first. The existing 10,000-socket cap is a guard, not a measurement.
- **Reconnect rate:** sockets per second absorbed, which sets the deploy step.

SRE posts the table on #1148. The first run is single-replica. Once a backplane exists, repeat at two and three replicas to measure the cost of the extra hop and the real scaling factor.

## 8. When scale-out must ship

The threshold is stated in users, because that is how Product and the owner think, and derived from devices, because that is what the relay counts.

- **Ceiling C** = measured per-replica ceiling in connected devices (section 7).
- **Alert** at 60 percent of C connected sockets on any replica when running one replica or three or more (SRE's alert, per the plan), and at 50 percent at two replicas so that losing one still leaves the survivor under the ceiling (section 6, N-1 headroom).
- **Scale-out ships before connected devices reach 60 percent of C.** The work (backplane, two replicas, rings) takes weeks, so 60 to 100 percent is the runway. Reaching about 80 percent of C is an incident.
- **User-count form:** threshold users = (0.6 x C) divided by measured peak concurrent sockets per user. A user typically holds a few sockets at once (a host, an app or PWA, a phone); use the measured figure from #1134 production data, not the account cap of 32.

First estimate, a placeholder until the load test replaces it: if the first-pass ceiling is on the order of 5,000 active sockets and users average 3 concurrent sockets, the alert is near 1,000 concurrent users. **The final number is set after #1134 lands and SRE's load test.** Do not use the 10,000-socket guard as the ceiling.

Ship earlier, regardless of user count, if the owner wants a second replica to remove the single point of failure, if deploy gaps become unacceptable, or if the load test shows memory or CPU binding at a number that growth would reach before the build finishes.

The per-IP, sign-in and push limits are per replica today, so the effective limit multiplies by replica count. The 10,000-socket cap is per process by design. The 32-devices-per-account cap and device replacement are the ones that must not multiply: they move to the directory (section 5, account cap across replicas). The backplane PR decides whether the other limits also move into the shared layer; the default is to keep them per replica and note the multiplier.

## 9. Privacy

The relay keeps the line from #1134, and scale-out keeps it:

- **Metrics and logs carry counts and sizes only.** Tags stay on the allow-list (`kind`, `outcome`, `code`), held by `RelayMetricsTests`. No account, device, address or name, hashed or not, in any dimension, log line, trace or load-test report.
- **Backplane contents.** Frames cross the backplane exactly as received; the relay does not read or log payloads. The directory stores the minimum to route (an opaque key per device and a replica identifier, with an expiry). No user content in the shared layer, nothing kept after expiry, persistence and replay disabled.
- **Routing keys are not telemetry.** They are never copied into metrics, logs or load-test output; debugging uses counts by kind.
- **Load test.** Synthetic accounts and devices only, created for the test and deleted after. Reports hold counts, sizes, rates and percentiles.
- **Secrets.** The backplane credential lives in Key Vault, read by the relay's managed identity. None in the repository or bicepparam.
- **Review.** The backplane PR touches the relay, a sensitive path: two reviewer families, with the privacy of everything entering the shared layer as the focus.

## 10. Rings and rollback

This follows the pipeline in `docs/relay-deploy.md` (build, scan, staging with smoke test, production behind owner approval, rollback by hand with `rollback_to=<image digest>`).

| Ring | What | Exit criteria | Rollback |
| --- | --- | --- | --- |
| 0. Design | This note reviewed by two families and approved by the owner | Owner approves direction and the load test | n/a |
| 1. Measure | #1134 telemetry deployed (owner approval); SRE load test on single-replica staging | Ceiling table posted | n/a, no behaviour change |
| 2. Staging, backplane on | Backplane PR on staging at two replicas; load test repeated at 2 and 3 replicas; kill-a-replica and backplane-outage drills | Section 7 criteria hold; presence heals within the expiry time; same-replica delivery survives a backplane outage | Redeploy the previous digest on staging and remove the backplane setting. The relay must run with no backplane configured, exactly as today, so this is a configuration change |
| 3. Production, backplane configured, one replica | Deploy the code with the backplane present but no second replica | No change in latency or errors against the ring 1 baseline over a soak | One-step rollback to the prior digest; unset the setting |
| 4. Production, two replicas | Raise min to 2 and max to 3 (owner approval, #55). Watch alerts through a peak period | Devices balanced across replicas; no rise in undeliverable frames; reconnects as measured | Set min and max back to 1 and redeploy the previous digest. Devices reconnect to the one replica; a deploy-sized gap is the worst case |
| 5. Autoscale on | Turn on the section 6 rules | A full week with no flapping and no cost-guard alert | Disable the rule and pin replicas at 2 |

Rules for every ring: one change at a time; no step skipped; the no-backplane mode stays until the owner retires it, so rollback never needs code that is not already running; every production step needs explicit owner approval (#55).

## 11. Global expansion: many regions

Owner requirement (2026-11-07): the design must let relay compute run anywhere in the world without degradation. Stateless-first still rules: a region is a disposable pool of replicas, and the only durable things are the shared directory and existing storage.

### Topology: regional relays, not one global tier

A single global relay tier means every device pays the distance to wherever it sits. Instead, run a **relay stamp per region** (the existing stamp shape: Container App, Key Vault, storage, App Insights; today only West US 2). A device connects to the nearest healthy region through a global entry point (DNS latency or geo routing, or Azure Front Door with WebSocket support; the choice is for SRE and is not decided here). Each regional stamp is independently deployable and scalable, and holds sockets only for devices connected to it.

Why not one global tier: sockets are long-lived and latency-sensitive, so compute must be near the device. Why not region-pinned accounts (sticky by region): it brings back the pet-replica problem at region scale, and a user who travels or whose phone and host sit on different continents would be penalised.

### The routing store across regions

The directory (device to region and replica) has to be readable from every region. Options:

| Option | How | Trade-off |
| --- | --- | --- |
| Per-region directory plus cross-region lookup | Each region keeps its own directory; to find a peer, ask the other regions in parallel | No global store and no global single point of failure. Lookup cost grows with region count; mitigated by caching the answer for the life of the peer's socket. |
| Globally replicated store (Redis active geo-replication, or a multi-region document store) | One logical directory, written locally, replicated asynchronously | Fast reads everywhere. Replication lag means a stale answer for a moment after a device moves, so a sender must tolerate "not there" and retry. Conflicts need last-writer-wins with expiry. |
| Home region per account, recorded in a small global map | Account maps to a home region that holds its directory | Short, stateless lookup (one tiny read-mostly record). The home is routing metadata, not where sockets must live; sockets stay nearest. A home outage needs the failover below. |

Recommendation: **start with a globally replicated directory holding only opaque keys and expiries, with regional caches**, because it keeps replicas stateless and needs no home election. Re-evaluate per-region directories with parallel lookup if replication cost or lag measures badly. The directory remains routing metadata only (section 9).

### Cross-region frames and latency

Device A connects to its nearest region, peer B to another. A frame goes A, regional relay, backplane or inter-region link, B's region relay, B. This adds one inter-region round trip, tens to a couple hundred milliseconds depending on distance. Same-region peers (the common case: a person's own host, phone and app are usually near each other) pay nothing extra.

Rules:
- Deliver locally when the peer is local; use the cross-region path only when the directory says the peer is elsewhere.
- One path per sender and target while placement is stable; ordering across reconnects and failover is not guaranteed (section 5).
- Frames cross regions encrypted in transit on the platform's private or TLS links; the relay still does not read payloads.
- Set a latency budget for cross-region forwarding (proposed: p95 under inter-region round trip plus 100 ms) and alert on exceeding it.
- A cross-region bus is one more thing to size. Prefer a regional bus per region plus a thin inter-region forwarder, over one global bus, so a distant region's slowness cannot slow local delivery.

### Data residency and privacy

- Metrics, logs and directory entries stay counts, sizes and opaque keys only (section 9); no account, device, address or name.
- Frame payloads are transit-only. Cross-region forwarding means a payload may pass through another region. Where a residency rule (for example the EU) forbids that, the account's peers must be reachable without leaving the zone, so offer **residency zones**: a set of regions (for example EU) whose directory and bus never replicate outside the zone, and cross-zone delivery is either refused or an explicit opt-in. Zones are a policy for the owner to set; the design only needs to keep the directory and bus partitionable by zone.
- Stored data (blob store, machine roster) is per-region storage today. Decide per zone where an account's blobs and roster live; the roster is rebuilt from the host on connect, so it is the easier one to keep zone-local.
- Telemetry per region goes to that region's App Insights and rolls up by counts only.
- Each new region repeats the secrets rule: its own Key Vault and managed identity, nothing in the repository.

### Regional failure and failover

- **Region down.** Its devices reconnect through the global entry point to the next-nearest healthy region within their backoff window (as in section 5, but across a larger pool). Their directory entries expire or are overwritten on reconnect. Peers in the failed region are unreachable until their devices reconnect elsewhere, which is the same recovery as replica loss.
- **Capacity of the survivor.** Failover is only safe if the neighbour can absorb the load. Plan each region with enough headroom for a failed neighbour (for example, size so that losing the largest neighbour keeps every survivor under its alert level), and cap reconnect rates so a failover does not become a storm. The load test measures this (below).
- **Directory loss or partition.** Each region keeps serving its own sockets and its local peers; cross-region delivery degrades and alerts, as in the backplane-loss case. The relay must not fail readiness on this alone (the owner chose stay-up-degraded; section 12).
- **Entry point loss.** The global entry point is a new shared dependency; it must itself be multi-region and have a documented manual override (DNS at Cloudflare today, per `docs/relay-deploy.md`).

### Per-region capacity and rollout rings

- **Capacity.** Each region has its own min, max and alert (the lower of 60 percent and the N-1 level of that region's measured ceiling), by the section 6 rules. A new region starts at min 2, max 3 only when the owner has approved it and its cost. Region size follows measured concurrent sockets per region, not a global figure.
- **Rings across regions.** Staging remains one stamp (West US 2) for the code. New code rolls to production regions in order: the least busy region first, then the others one at a time, then the busiest, with a soak in each and one-step rollback per region (the same digest rollback, region by region). Never deploy all regions at once; never deploy a region while another is degraded.
- **Adding a region** is its own ring: stamp built from the existing Bicep (a new `regions/<name>.bicepparam`), empty traffic, synthetic probes from nearby, then a small share of devices through the entry point, then full. Every step needs owner approval (#55).
- **Version skew.** During a rollout two regions run different relay versions and can exchange frames. Cross-region frames and directory entries must therefore stay compatible across adjacent versions; that is a rule for the backplane PR and a check in ring 2.

### Cost

- Compute scales with sockets, and a region's minimum of 2 replicas is a fixed cost, so each region costs a floor even when quiet. Open a region only when its measured or forecast user count justifies the floor; below that, devices use the nearest existing region.
- Backplane: a directory and bus in each region, plus replication if the directory is global. The smallest managed tier is a fixed monthly cost per region.
- Inter-region data transfer is billed per byte, which is why local delivery is preferred and why same-region peers are the common case. The load test records cross-region bytes per second so cost can be forecast from counts, not content.
- The monthly budget alert and the max replica cap (section 6) apply per region; add a regional budget for each new region.

### What the load test must also measure

Add to section 7, once a second region exists on staging (a throwaway second staging stamp, never production):

| Measure | Why |
| --- | --- |
| Cross-region forwarding latency p50, p95, p99, by region pair | Checks the latency budget and the claim that same-region peers are unaffected |
| Directory read and write latency, and replication lag | Sizes the staleness window and cache expiry |
| Cross-region frames and bytes per second | Cost forecast and the cross-region link capacity |
| Reconnect rate and time to healthy after killing a whole region | Validates failover and the headroom rule |
| Per-region ceiling at two and three regions, for devices and frames per second | Checks that a region's ceiling does not drop when peers are elsewhere |
| Frame loss and ordering violations during a region drill, with ordering claimed only for stable placement | Confirms the ordering limits and the "undeliverable, not lost silently" behaviour |
| Stale-socket fencing: time from a reconnect on another replica to the old socket closing, and frames delivered to a stale holder (target: none after registration) | Validates the epoch rule |
| Concurrent admission of devices of one account on different replicas: how far the 32-device cap is exceeded and how fast it resolves | Validates the account cap in the directory |
| Re-registration after a backplane outage: peak directory writes per second with and without jitter | Sizes the herd window |
| A replica lost at the N-1 alert level: survivors' peak sockets, CPU and reconnect time | Validates the 50 percent alert at two replicas |
| Behaviour with an adjacent-version pair | Version skew during rollout |
| Entry-point routing: which region a probe lands in and failover time | Validates nearest-region routing |

Drive devices from probes placed near each region where possible. All figures remain counts, sizes and percentiles by region, never by account or device.

## 12. Owner decisions and open questions

Decided by the owner on 2026-11-07:

- **Build approach: build the code now, ship it dark.** The backplane code (directory, bus, region-ready keys, version-skew rules) is built behind a configuration flag that is off by default. No Redis is provisioned and production runs exactly as today, so there is no new monthly cost until measured data says scale-out is needed. Turning it on later is a setting change plus provisioning, not a rewrite. The no-backplane mode (section 10) is what runs while it is dark.
- **First regions: West US 2 only.** The design stays region-ready (section 11); no second region or residency zone is built or paid for yet.
- **No second replica yet.** Production stays at one replica until the capacity threshold (section 8) is reached or the owner changes this.
- **On backplane loss: stay up degraded and alert** (do not fail readiness).
- **Staging-only Redis spike approved in principle:** a small, temporary Redis on staging to check latency, expiry after a crash and frame ordering, deleted afterwards. The exact resource and teardown are confirmed with the owner at the time, per #55.

Still open (a second region or an EU zone comes in when the user-count data says so):

1. SRE: confirm the pass criteria and that staging can use many source addresses.
2. Messaging Lead: measured concurrent sockets per user, from #1134 production data once exported.
3. Engineering: split the backplane work into a directory PR and a bus PR (section 4)?

## 13. Out of scope

Any code, infra or app-setting change; deploying or scaling the relay; alert definitions (SRE follows up); moving the relay off Container Apps.
