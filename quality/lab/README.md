# The clean-VM lab: contract and status

The shared end-to-end lab of [quality/README.md §6](../README.md#6-the-lab-disposable-machines):
clean Windows machines that install the product as a person would and drive it end to end.
This file is its contract, written down from the agreement on #357 and #736, and an honest
account of what of it exists. Nothing in the lab runs yet.

The agreement it records:

- Ownership and model: [#689 charter](https://github.com/danswett/agent-bridge/issues/689#issuecomment-6643979469).
- The lab: [SRE's proposal](https://github.com/danswett/agent-bridge/issues/357#issuecomment-6643996552),
  [the six accepted amendments](https://github.com/danswett/agent-bridge/issues/357#issuecomment-6644204208)
  and [the joint ACK](https://github.com/danswett/agent-bridge/issues/357#issuecomment-6644696961).
- Coverage: [Product's v1 and v2](https://github.com/danswett/agent-bridge/issues/736#issuecomment-6644011273).
- The owner's decisions B1, B3 and B4: [#357](https://github.com/danswett/agent-bridge/issues/357#issuecomment-6645343882),
  accepted by Platform [here](https://github.com/danswett/agent-bridge/issues/357#issuecomment-6646500327).

Where this file and those comments disagree, the comments win and this file is fixed.

## 1. What exists

| Piece | State | Owner |
|---|---|---|
| This contract | **Written** (this file) | Platform |
| `provision.ps1`: the resources of one run of one role | **Draft.** Plans only; `-Apply` refuses before any Azure call until §10's prerequisites are met. Never run against Azure | Platform |
| `test-provision.ps1`: its checks against a stand-in `az` | **Written**, catalog suite `lab-scripts` (ring 0, ci.yml `light` on Linux, so a draft's fast ring runs it). Never run against Azure | Platform |
| `run.ps1` (§5) | **Unbuilt**: the interface is design only | Platform |
| `fleet.yml`, the images and their S0 snapshots | **Unbuilt** | Platform |
| Scenarios and the simulated agent (`tests/AgentBridge.AgentSim`) | **Unbuilt** | Platform |
| The `lab` environment, OIDC principal and reusable workflow | **Unbuilt**; nothing is scheduled or dispatchable | Platform, owner (bootstrap) |
| The expiry sweeper (§4) | **Unbuilt**, and required before anything is created | Platform |
| Run registration in the sweeper's inventory, before the first create (§4) | **Unbuilt**: `provision.ps1`'s `Register-LabRun` refuses | Platform (`provision.ps1`) |
| The temporary nightly cut and schedule (§6) | **Unbuilt**; no cron is enabled | Platform, until SRE's #590 replaces it |
| PPE, cut freeze, promotion and monitoring (#590, #451) | **Pending**, not part of this folder | SRE |
| Lab cost and age alerts (`infra/modules/monitoring.bicep`) | **Unbuilt** | SRE |
| Dogfood's checks (#736) | Consume this same lab, never a second lab or runner | Dogfood |
| Admission of lab evidence | Quality alone | Quality |
| Audio-endpoint acceptance (§12) | **Unbuilt**: future and capability-qualified; no lab machine has a proven audio endpoint | Platform |

The catalog's `lab-e2e-simulated` stays `planned` and `lab-e2e-agents` stays `partial` (the
shadow soak, not this lab) until a scenario runs here.

## 2. Ownership

- **Platform** owns the fleet, images and S0 snapshots, `run.ps1`, the scenarios, the `lab`
  environment, its OIDC principal and reusable workflow, and bounded cleanup (the sweeper).
- **SRE** owns PPE (#590), the cut, freeze and promotion consumers (Dev to Beta, #451), and
  the lab's cost and age alerts in `infra/modules/monitoring.bicep`.
- **Dogfood** consumes the same lab and its results. There is never a second lab, runner or
  set of timers.
- **Quality** alone decides what lab evidence is admitted (quality/README.md §4.9).

## 3. Coverage

| | v1 | v2 |
|---|---|---|
| Machines | Clean Windows 11 x64, and **native** Windows 11 Arm64 | The same |
| Agent | Simulated: real-format transcripts through the real hook | A real agent on lab accounts |
| Steps | launch, reply, answer, stop, update, with a relay test client on the other end | v1's, plus the download page (#735), the real first run (#384), a phone-equivalent client and uninstall |
| Bar | Recorded; results in the weekly #357 report | Under 10 minutes; for Beta1, green on 3 consecutive Beta releases |
| Waits for | Nothing: not #735, not #384 | #735, #384 |

A v1 result is never v2, real-provider or Beta1 evidence. The simulation stands in for the
agent only: it never fakes the interactive WinUI session, session 0 or a provider. A step that
needs an interactive desktop runs in a real signed-in desktop session on the VM, or is
`skipped`; it is never simulated from a service.

## 4. Machines and their lifecycle

| Role | Size (B1's price check, westus2) | Architecture | State |
|---|---|---|---|
| `win-x64` | `Standard_D4s_v5` | x64 | No image or S0 yet |
| `win-arm64` | `Standard_D4ps_v5` | Arm64, native | **Unproven**: no image, size, Trusted Launch or licence receipt (§10) |

Arm64 is native Windows Arm64 only. x64 emulation and Linux are no substitute, and no Arm64
size is assumed to run Windows until the feasibility receipt shows it in this subscription.

Every run:

1. Registers itself in the sweeper's trusted inventory **before its first create**:
   `provision.ps1` writes the subscription, run id, role, the exact names below and their
   expiry durably, reads the entry back, and creates nothing unless that receipt matches its
   plan exactly. No parameter, environment variable or configuration default stands in for
   the receipt.
2. Creates its OS disk from the role's S0 snapshot (`snap-lab-<x64|a64>-s0`), a NIC on the lab
   subnet, and the VM, all named `<kind>-lab-<x64|a64>-<runId>` in `rg-agent-bridge-lab` and
   tagged with the run and an expiry **at most 4 hours** out. Only S0 snapshots persist
   between runs: a deallocated VM's disk still costs money.
3. Has no public IP, no NSG of its own and no inbound from the Internet; the bootstrap's
   subnet NSG denies it. No admin user or password is passed: the disk is a specialized copy
   of S0, which itself holds no reusable credential.
4. Deletes all three by exact name when it ends.

Cleanup in a `finally` or `catch` is **best effort only**: a cancelled or killed job never
reaches it, and nothing in the run guarantees removal after a hard cancel. So before anything
is created, a **durable sweeper** must exist, independent of the run, that deletes expired
run-owned resources by exact name from the trusted inventory (never by listing the group or
matching a pattern or tag), treats a registered name that doesn't exist as already gone, and
forgets their lab devices (§8). Tags label resources; they don't make them the lab's. The
inventory is the sweeper's only source, so a resource created before its run was registered is
one nothing would ever remove: that is why registration comes first.

Neither the inventory, the registration nor the sweeper exists. `provision.ps1` has the
registration's place and its receipt check, but `Register-LabRun` refuses unconditionally, so
`-Apply` creates nothing even if every other prerequisite were marked met.

Restoring S0 must be proven, not assumed: a run from S0 has no previous run's device
identity, hooks, settings or processes. That proof is part of the S0 receipt (§10).

## 5. `run.ps1`: the interface (design only)

```
quality/lab/run.ps1 -Subscription <id> -Role <win-x64|win-arm64> -Build <immutable CI artifact or release>
                    -Scenario <name> -Relay <staging|ppe|https://...> [-RunId <id>] [-Out <folder>]
```

There is no `run.ps1` yet, and nothing here pretends to be one.

- `-Subscription` is required and has no default. Every Azure call names the owner-approved
  Visual Studio Enterprise subscription; the CLI's default (a trial on this machine) never
  decides anything.
- `-Build` names an immutable, provenance-verified artifact of a protected CI run or a
  release (§6), never a PR's or a dispatched run's.
- `-Relay` is required: `staging` until PPE exists, then `ppe`. Never production, and never a
  default.
- `-RunId` defaults to a fresh id; `-Out` holds `result.json` (§7) and the artifacts.
- `run.ps1` provisions through `provision.ps1`, which registers the run (§4) before any
  create; `run.ps1` never creates a lab resource itself.

## 6. One cut, one nightly source

Both roles of a nightly run test **one** cut: the same immutable, provenance-verified build
from a protected CI run or release, against the same relay digest. A PR's or a dispatched
run's artifacts are never a cut.

There is one nightly source. Until SRE's #590 delivers the cut and promotion path, Platform
may add one temporary default-branch schedule and cut resolver; #590 replaces it in one
coordinated change, so there are never two timers or two cuts. Neither exists yet, and nothing
is scheduled by this folder.

The nominal start is 01:00 Pacific. GitHub's cron is in UTC, so a fixed `0 9 * * *` starts at
01:00 PST and 02:00 PDT; GitHub may also start a scheduled run late. `result.json` records the
actual UTC times, and the cut is whatever the resolver picks when the run starts.

Owner decision B1 (2026-11-04, replacing the weekly Arm64 budgeted on 11-03): both roles
nightly, Arm64 included, within $75/month. Its price check assumes about an hour a run and
only S0 snapshots kept between runs (§4). Actual cost is unmeasured, and whether the Windows
licence is billed is checked at bootstrap.

## 7. `result.json`, schema version 1

One per run per role. Fields are only ever added to version 1, never renamed or removed.

| Field | Meaning |
|---|---|
| `schemaVersion` | `1` |
| `runId`, `cutId` | This run, and the cut both roles test |
| `source` | The CI run (id, attempt, workflow, protected ref) and the artifacts (name, id, digest) the build came from |
| `build` | `version`, `channel`, `sha` and `digests` of what was installed |
| `role`, `architecture` | `win-x64` or `win-arm64`; `x64` or `arm64` as the VM reports it |
| `agentMode` | `simulated` (v1) or `real` (v2) |
| `coverageVersion` | `v1` or `v2` |
| `updatePair` | The explicit `from` and `to` (version and digest) the update step installs |
| `relay` | `target` (`staging`, `ppe` or the URL), `digestAtStart`, `digestAtEnd` |
| `steps` | One per step (`launch`, `reply`, `answer`, `stop`, `update`, and v2's): `name`, `outcome` (`pass`, `fail` or `skipped`), `startedAt`, `endedAt`, sanitized `detail` |
| `outcome` | `pass`, `fail` or `invalid` (§8) |
| `artifacts` | Paths relative to `-Out`, sanitized, with a SHA-256 each |
| `startedAt`, `endedAt` | UTC |

## 8. Outcomes, retries and credentials

- `pass` only when every step of the coverage version passes. A missing update pair, or an
  update that didn't change the installed version, is a `skipped` or failed `update` step, and
  the run is not `pass`.
- `invalid` when the run can't count either way: the relay digest changed during the run
  (B5: staging churn will make this common until PPE), the VM was lost, or the build's
  provenance didn't verify. If either role's run is `invalid`, the cut is invalid.
- `invalid` is never admission or promotion evidence. A cut gets at most one retry, after a
  diagnosis, under a new run id; the original result is kept.
- Accounts (owner decision B3): the owner's own GitHub account through a dedicated,
  fine-grained lab token held only as a `lab` environment secret, never the interactive
  sign-in; the relay pinned to staging or PPE. Devices are named `lab-<role>-<runId>`, a label
  and not an authorization. Each run signs out and forgets its devices, and the sweeper
  forgets those of crashed runs. Only the relay is shared: the lab has no path to the owner's
  machines, Home Assistant or broker. Real agent accounts are v2 only.
- PPE (owner decision B4) also serves the owner's Beta machines, so it is not disposable: the
  lab consumes an immutable cut there and never rolls PPE or touches a Beta machine.

## 9. Evidence: access, retention and redaction

Recordings and raw logs are **not public**. The repository is public, and so are its workflow
artifacts, so they are never uploaded as workflow artifacts. Proposed here, for Quality's and
SRE's review, and unbuilt:

- **Public:** `result.json` (§7) and the weekly #357 summary, which carry no recording, raw log,
  account, token, IP, host name beyond `lab-<role>-<runId>`, or private path.
- **Private:** recordings and raw logs in a private container in `rg-agent-bridge-lab`, with no
  anonymous access, read by the owner, Platform and SRE.
- **Retention:** 14 days for a `pass`, 30 for a `fail` or `invalid`, then deleted by policy. A
  Beta1 record keeps `result.json` and the artifacts' digests, not the recordings.
- **Redaction:** recording starts after sign-in and never shows a sign-in, token, device code
  or account. Logs have tokens, account names, IP addresses and user-profile paths replaced
  before they are written to `-Out`.

## 10. Before anything runs

Each item blocks execution, not this draft. `provision.ps1 -Apply` lists the ones it needs and
refuses while any is unmet; each is marked met only by the reviewed change that delivers its
receipt.

| Item | State | Owner |
|---|---|---|
| Owner authorization to execute and spend | **Absent**. Accepting the contract and this draft authorized neither | owner |
| Cadence and budget (B1) | **Decided**: nightly, both roles, $75/month. Cost unmeasured | owner |
| Native Windows Arm64: image, size, Trusted Launch, licence billing in this subscription | **Unproven** | Platform, owner |
| Bootstrap (B2): `rg-agent-bridge-lab`, its network, the `lab` environment, an OIDC principal scoped to that group only, a subscription receipt | **Absent** | owner, Platform |
| S0 snapshots per role, with the restore proof (§4) | **Absent** | Platform |
| Durable expiry sweeper (§4) | **Absent** | Platform |
| Run registration before the first create (§4): the trusted inventory, `provision.ps1` writing each run's exact names and expiry to it, and a verified read-back receipt | **Absent**; `Register-LabRun` refuses | Platform |
| Cost and age alerts | **Absent** | SRE |
| Lab token as a `lab` secret, and proof the credential, relay and device guards hold (B3) | **Absent**, unverified | owner, Platform |
| PPE (B4, #590): **population decided**, the lab and the owner's Beta machines | Enrollment, keys, DNS and deploy **pending**; staging until then | SRE, owner |
| Relay churn (B5) | **Open**: makes runs `invalid` | SRE |

## 11. The provisioning draft

```
pwsh quality/lab/provision.ps1 -Subscription <id> -Role win-arm64 -RunId <id> | ConvertTo-Json -Depth 5
pwsh quality/lab/test-provision.ps1
```

`provision.ps1` returns the plan for one run of one role: the three creates and three deletes
it would run, every one naming the subscription, the group and a run-owned name, and the
prerequisites still unmet. Planning calls nothing in Azure. It never creates or changes a
resource group, network, identity, role assignment, public IP, NSG or credential, and it
holds no subscription id or account of its own.

`test-provision.ps1` checks that against a stand-in `az`: planning makes no call; every
command stays in scope; expiry is at most 4 hours; `win-arm64` is native; a missing, default
or placeholder subscription, an unknown role and a malformed run id are refused; and `-Apply`
refuses before any Azure call, listing what's missing, run registration included. The
registration stub and its receipt check sit behind that refusal, so no offline check reaches
them without editing the script; they are reviewed, not tested.

## 12. Future acceptance: toast audio on a proven endpoint (#754)

Engineering's [decision on #792](https://github.com/danswett/agent-bridge/pull/792#issuecomment-6650134565)
let #792 (every toast silent) land without a runtime OS check. Its runtime acceptance is the
owner's check on #754 after the pre-release installs, and Client Experience reverts #792 if a
toast dings. That decision also asked for audio-endpoint checks like this one in the lab, once
it is up. This section is that check, specified only:

- No runner, scenario, script or resource for it exists, and nothing here has run.
- No lab machine is known to have an audio endpoint.
- It is not a prerequisite for #792 or its admission. It doesn't change §3's v1 or v2 steps,
  and it doesn't change their bar.
- It grants no VM, resource, spend or runtime permission. It adds no second lab, no extra VM
  and no audio hardware.

**Where it may run.** Only on this lab's isolated Windows 11 machines: `win-x64` and native
`win-arm64` (§4). It runs in the interactive desktop session the toasts appear in, and only
where that session has a working audio endpoint, shown by the control below.

These never count as a target:

- the owner's machines, or VM4 or any other development VM;
- a session that has an endpoint only while someone's RDP connection is attached;
- a machine with no render endpoint at all, which is what an Azure VM has unless its image
  provides one.

How a lab image gets an endpoint, in S0, with no extra VM, hardware or spend, is unproven.
Until it is proven, every case below is `skipped`.

**What isn't proof of silence.** None of these shows that a toast is silent:

- a muted or zero-volume endpoint;
- notifications turned off, or the per-app "Play a sound when a notification arrives" setting
  turned off;
- a capture that wasn't shown to hear anything;
- the toast XML, or a unit test.

None of them is a fix either. Muting the machine or the app is never how silence is reached.
Each run checks these settings are at their defaults before the cases and again after them.

**The control, before and after the cases.** These checks use the same endpoint and session as
the cases.

1. **Before:** the default render endpoint is present and active, not muted, and above zero
   volume. Notifications and the per-app sound setting are on.
2. **Before:** a control toast from a lab-only app identity (never the product's) uses Windows'
   default notification sound. The loopback capture of that endpoint detects it above a set
   threshold. The capture records what the endpoint plays, never a microphone.
3. **After:** the same control toast is detected again.

If either control fails, the cases between them prove nothing.

**The cases.** The cut's own installed build (§6) raises each one. The cases are:

- Question;
- the same Question replaced under its tag, and shown again;
- Finished;
- Updated;
- Outcome.

Each case passes only when all of the following hold:

- **Visible as normal:** the toast appears on screen and in Action Center, as it does today.
  Its actions are present and work: an answer or reply reaches the relay test client.
- **No sound:** the capture holds nothing above the threshold from the toast being raised
  until it settles.
- **No custom sound:** the toast carries no sound of its own.

**Outcomes.** These use §7's and §8's vocabulary, with each case as its own step, named
`audio-<case>`, beside the coverage version's steps.

| What happened | The step's outcome |
|---|---|
| The control passed before and after, the toast was visible as normal, and it made no sound | `pass` |
| The control passed both times, and the toast made a sound, didn't show or its actions failed | `fail` |
| No endpoint, an endpoint only through RDP, or a control that failed before or after | `skipped`, with `detail` beginning `unverified:` and the reason |

A `skipped` audio step is unverified audio coverage. It is never a pass, and it never
satisfies this acceptance. The XML and unit tests can't stand in for it either.

The `audio-` steps are capability-qualified. They count toward the run's top-level `outcome`
(§8) only on a machine whose control passed. Without a proven endpoint they neither fail nor
pass the run, and they never excuse a v1 or v2 step.

The relay-churn and lost-VM rules of §8 still make the whole run `invalid`, audio steps
included.

**Evidence.** Each audio step records:

- the OS build, role and architecture, and the session's endpoint capability: present or not,
  and both controls' results;
- the cut and artifact identity (§7 `source` and `build`);
- the case, and what was seen and heard: whether the toast and its actions were seen, and the
  peak captured level against the threshold.

Only that redacted record is kept, under §9. It never includes:

- raw audio;
- the owner's environment or accounts;
- credentials;
- anything recorded from a microphone.
