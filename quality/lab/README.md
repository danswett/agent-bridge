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
  B3's lab-token plan is superseded by Trust's choice A, device flow, recorded in §8.
- Current source routing: [Engineering's assignment](https://github.com/danswett/agent-bridge/issues/689#issuecomment-6705673879).
  Platform delivers the runner first, then rebases the held #854 nightly and advances #863.
  SRE supplies inventory/sweeper and cost fences; the owner supplies contract/S0 approval and
  per-run device flow. Source ownership does not authorize execution or spend.

Where this file and those comments disagree, the comments win and this file is fixed.

## 1. What exists

| Piece | State | Owner |
|---|---|---|
| This contract | **Written** (this file) | Platform |
| `provision.ps1`: the resources of one run of one role | **Draft.** Plans only; `-Apply` refuses before any Azure call until §10's prerequisites are met. Never run against Azure | Platform |
| `test-provision.ps1`: its checks against a stand-in `az` | **Written**, catalog suite `lab-scripts` (ring 0, ci.yml `light` on Linux, so a draft's fast ring runs it). Never run against Azure | Platform |
| `run.ps1` (§5) | **Nonexecuting interface draft.** Validates requests and plans only; `-Apply` always refuses. No scenario, result or evidence is produced | Platform |
| `test-run.ps1`: offline runner-interface checks | **Written**, in the same `lab-scripts` suite; no Azure or product execution | Platform |
| `beta-acceptance.ps1` and `.github/workflows/beta-acceptance.yml` (#1185): the Beta installer on hosted `windows-latest` and `windows-11-arm`, not the Azure lab | **Draft.** Steps 1, 2, 3, 6 (download and verify, silent install and first run, simulated agent, uninstall); pairing and driving from the PWA (a relay built from the Beta's source against a fake GitHub, no secrets) and feedback are NOT RUN. Not yet dispatched | Engineering |
| `fleet.yml`, the images and their S0 snapshots | **Unbuilt** | Platform |
| Scenarios and the simulated agent (`tests/AgentBridge.AgentSim`) | **Unbuilt** | Platform |
| The `lab` environment, OIDC principal and reusable workflow | **Unbuilt**; nothing is scheduled or dispatchable | Platform, owner (bootstrap) |
| The trusted inventory and expiry sweeper (§4) | **Unbuilt**, and required before anything is created | SRE |
| Run registration in the sweeper's inventory, before the first create (§4) | **Unbuilt**: `provision.ps1`'s `Register-LabRun` refuses | Platform (`provision.ps1`), using SRE's inventory |
| The temporary nightly cut and schedule (§6) | **Unbuilt**; no cron is enabled | Platform, until SRE's #590 replaces it |
| PPE, cut freeze, promotion and monitoring (#590, #451) | **Pending**, not part of this folder | SRE |
| Lab cost and age alerts (`infra/modules/monitoring.bicep`) | **Unbuilt** | SRE |
| Dogfood's checks (#736) | Consume this same lab, never a second lab or runner | Dogfood |
| Admission of lab evidence | Quality alone | Quality |
| Audio-endpoint acceptance (§12) | **Unbuilt**: future, separate and non-gating; no lab machine has a proven audio endpoint | Platform |

The catalog's `lab-e2e-simulated` stays `planned` and `lab-e2e-agents` stays `partial` (the
shadow soak, not this lab) until a scenario runs here.

## 2. Ownership

- **Platform** owns the fleet, images and S0 snapshots, `run.ps1`, the scenarios, the `lab`
  environment, its OIDC principal and reusable workflow, and the run's bounded cleanup.
- **SRE** owns PPE (#590), the cut, freeze and promotion consumers (Dev to Beta, #451), and
  the trusted inventory, independent expiry sweeper, and lab cost and age fences.
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
   of S0, which itself holds no reusable credential. The owner signs in on each run's new VM,
   never on S0; no signed-in disk is snapshotted or turned into an image (§8).
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

## 5. `run.ps1`: the nonexecuting interface draft

```
quality/lab/run.ps1 -Subscription <id> -Role <win-x64|win-arm64> -Build <immutable CI artifact or release>
                    -Scenario <name> -Relay <staging|ppe> [-RunId <id>] [-Out <folder>]
```

The first source slice is **Part of #736**, not completion of it. Without `-Apply`,
`run.ps1` validates the request and returns a draft plan (`planVersion: 1`,
`executionAllowed: false`), including `provision.ps1`'s plan and unmet prerequisites.
It makes no resource, Azure, network or sign-in calls and writes no files. The digest is
unverified input, not a provenance receipt. A plan is never a `result.json`, outcome or
coverage claim.

`-Apply` always throws a clear refusal listing what is missing, including with
`-Confirm:$false` or `-WhatIf`. There is no input or environment switch that enables it.
Scenario execution, AgentSim, transport/evidence and S0 need separate later PRs. Neither
this interface's existence nor its offline tests clear #854's execution gate.

The draft accepts only `-Scenario nightly-v1` and a pinned release build in the form
`-Build v0.1.N@sha256:<64 lowercase hex>`, matching the held nightly's intended interface.
Other build sources need a separately reviewed provenance implementation, not a URL
escape hatch. A legacy `LAB_GITHUB_TOKEN` environment variable is explicitly refused
without reading or printing its value. Offline guards are `test-run.ps1` in `lab-scripts`;
CI is the only validation path on execution-restricted hosts.

The full runner contract remains:

- `-Subscription` is required and has no default. Every Azure call names the owner-approved
  Visual Studio Enterprise subscription; the CLI's default (a trial on this machine) never
  decides anything.
- `-Build` names an immutable, provenance-verified artifact of a protected CI run or a
  release (§6), never a PR's or a dispatched run's.
- `-Relay` accepts only `staging` or `ppe`, with no default and no URL override. Each name
  resolves through the reviewed lab configuration to that environment's HTTPS endpoint;
  production, arbitrary URLs and redirects to another environment are refused. Until PPE
  exists, `ppe` is unavailable, not an alias for another relay.
- `-RunId` defaults to a fresh id; `-Out` holds `result.json` (§7) and the artifacts.
- `run.ps1` provisions through `provision.ps1`, which registers the run (§4) before any
  create; `run.ps1` never creates a lab resource itself.

## 6. One cut, one nightly source

Both roles of a nightly run test **one** cut: the same immutable, provenance-verified build
from a protected CI run or release, against the same relay digest. A PR's or a dispatched
run's artifacts are never a cut.

There is one nightly source. Until SRE's #590 delivers the cut and promotion path, Platform
may add one temporary default-branch schedule and cut resolver; #590 replaces it in one
coordinated change, so there are never two timers or two cuts. The orchestration draft is
`.github/workflows/lab-nightly.yml`: its cron is commented out and its fixed source gate always
refuses before cut resolution, OIDC or Azure sign-in. It calls the sole runner's refusing
`-Apply` entrypoint, never treating a returned plan as execution. The landed provisioner and
runner interface clear no execution prerequisite. No lab token is supplied; owner device flow
on each role's fresh VM in every run remains required (§8). Nothing is scheduled by this folder.

The nominal start is 01:00 Pacific. GitHub's cron is in UTC, so a fixed `0 9 * * *` starts at
01:00 PST and 02:00 PDT; GitHub may also start a scheduled run late. `result.json` records the
actual UTC times, and the cut is whatever the resolver picks when the run starts.

Owner decision B1 (2026-11-04, replacing the weekly Arm64 budgeted on 11-03): both roles
nightly, Arm64 included, within $75/month. Its price check assumes about an hour a run and
only S0 snapshots kept between runs (§4). Actual cost is unmeasured, and whether the Windows
licence is billed is checked at bootstrap.

Nightly is a cadence target, not unattended authenticated execution. The selected B3 path
requires the owner to complete device flow separately on each role's fresh VM in every run
(§8), within that run's existing expiry of at most 4 hours. There is no credential in S0 to
reuse. If the owner does not sign in before expiry, authenticated steps are `skipped`, the
run and cut are `invalid`, and cleanup still removes the run's resources. The schedule must
not claim a pass, extend the expiry or retain a signed-in disk to avoid the next sign-in.

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
| `relay` | `target` (`staging` or `ppe` only), `digestAtStart`, `digestAtEnd` |
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
  provenance didn't verify, or the owner did not complete that run's required sign-in before
  expiry. If either role's run is `invalid`, the cut is invalid.
- `invalid` is never admission or promotion evidence. A cut gets at most one retry, after a
  diagnosis, under a new run id; the original result is kept.
- Accounts (owner decision B3, its credential plan superseded by Trust's choice A): the lab
  signs in to the relay as `Slipgates-lab-bot`, a dedicated, revocable GitHub account with no
  organization access and no write access to any repository, never the owner's identity; the
  relay pinned to staging or PPE. There is no staging-only or other second sign-in path. The
  relay's sign-in has no allowlist, organization or invite check, so it needs only that the
  relay's OAuth app has device flow enabled and asks for no organization-membership scope;
  this file changes no OAuth setting.
  - The selected path is owner sign-in per run, not a signed-in golden image. The owner,
    who alone holds the account's password and second factor, completes GitHub device flow
    on each fresh lab VM in every run, including each role and any diagnosed retry. No
    GitHub token is provisioned as a `lab` environment or vault secret, or put in the
    repository, CI, logs or screenshots (PNGs). The account is named; its sign-in is the
    owner's alone and has not been done.
  - After sign-in the app keeps its normal local relay credential on that VM, as any
    installation does, only on that run's OS disk. The disk stays private inside
    `rg-agent-bridge-lab` and is already named in the sweeper's inventory before creation
    (§4). Platform deletes it by exact name at run end; SRE's independent sweeper
    deletes it by that same name after expiry even if the run crashes. No credential-bearing
    disk is kept between runs, exported, shared, snapshotted or made into an image; S0 stays
    credential-free. Cleanup never depends on deleting the shared resource group.
  - Nothing here authorizes a new VM, image, clone, snapshot, resource or OAuth-setting
    change: images and cloning are future work, each separately approved.
  - Devices are named `lab-<role>-<runId>`, a label and not an authorization. Each run signs
    out and forgets its devices, and the sweeper forgets those of crashed runs. Only the relay
    is shared: the lab has no path to the owner's machines, Home Assistant or broker. v1's
    agent is simulated (§3); real agent accounts are v2 only.
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

Any separately approved manual proof has its own scope; it clears none of these nightly
execution gates.

| Item | State | Owner |
|---|---|---|
| Owner authorization to execute and spend | **Absent**. Accepting the contract and this draft authorized neither | owner |
| Cadence and budget (B1) | **Decided**: nightly, both roles, $75/month. Cost unmeasured | owner |
| Native Windows Arm64: image, size, Trusted Launch, licence billing in this subscription | **Unproven** | Platform, owner |
| Bootstrap (B2): `rg-agent-bridge-lab`, its network, the `lab` environment, an OIDC principal scoped to that group only, a subscription receipt | **Absent** | owner, Platform |
| S0 snapshots per role, with the restore proof (§4) | **Absent** | owner |
| Durable expiry sweeper and trusted inventory (§4) | **Absent** | SRE |
| Run registration before the first create (§4): the trusted inventory, `provision.ps1` writing each run's exact names and expiry to it, and a verified read-back receipt | **Absent**; `Register-LabRun` refuses | Platform |
| Cost and age alerts | **Absent** | SRE |
| B3 sign-in (§8): the relay OAuth app's device flow enabled with no organization-membership scope, the owner's device-flow sign-in as `Slipgates-lab-bot` on each role's fresh VM in every run, and proof the credential, relay and device guards hold. No lab token, secret or signed-in image | **Absent**: account named, OAuth settings unverified, per-run sign-in not done | owner, Platform |
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

`-Apply` remains blocked by the prerequisites and registration stub. Any future reviewed
implementation that reaches creation also uses `ConfirmImpact='High'` and `ShouldProcess`
to request confirmation under PowerShell's default confirmation preference; `-WhatIf`
cannot create resources. Suppressing confirmation is not execution or spend authorization.

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
- It is not a prerequisite for #792 or its admission. It is separate, non-gating evidence: it
  doesn't change §3's v1 or v2 steps or bars, or §7's and §8's result and outcomes.
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
Until it is proven, every case below is `unverified`.

**What isn't proof of silence.** None of these shows that a toast is silent:

- a muted or zero-volume endpoint;
- notifications turned off, or the per-app "Play a sound when a notification arrives" setting
  turned off;
- a capture that wasn't shown to hear anything;
- the toast XML, or a unit test.

None of them is a fix either. Muting the machine or the app is never how silence is reached.

**The control, before and after the cases.** These checks use the same endpoint and session as
the cases. The control toast comes from a lab-only identity, so it can't show what the
product's own settings are; the settings are checked separately, by name.

1. **Baseline, before the product first runs:** the settings below are set from a controlled
   fixture and recorded. They are:
   - the default render endpoint: which one it is, present and active, not muted, above zero
     volume;
   - the session's system-sounds volume and mute;
   - notifications on, with no Do Not Disturb or Focus;
   - the product's own app identity: its notifications on, and its "Play a sound when a
     notification arrives" on.
2. **Before:** a control toast from a lab-only app identity (never the product's) uses Windows'
   default notification sound. The loopback capture of that endpoint detects it above a set
   threshold. The capture records what the endpoint plays, never a microphone.
3. **Recheck, after each case and after the last:** every setting in step 1 still has its
   baseline value.
4. **After:** the same control toast is detected again.

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
- **Controls and settings held:** both controls passed, and every recheck found step 1's
  settings at their baseline.

**Outcomes.** Each case gets its own verdict. The rows are read in order, and the first that
applies to a case decides it.

| What happened | The case's verdict |
|---|---|
| Before any case: no endpoint, an endpoint only through RDP, or a baseline or before-control that failed | `unverified`, with the reason |
| A recheck found any setting of step 1 changed from its baseline, whatever changed it | Every case `fail`, naming the setting |
| The case made a sound, didn't show, or its actions failed | `fail` |
| The after-control failed or couldn't run, and settings were unchanged | `invalid`, unless already `fail` |
| Both controls passed, settings were unchanged at every recheck, and the toast showed, its actions worked and it made no sound | `pass` |

The role's audio verdict is:

- `pass` only if every case passed;
- otherwise `fail` if any case failed;
- otherwise `invalid` if any case was invalid;
- otherwise `unverified`.

Missing an endpoint is not a sound regression, and a changed setting is not a claim about what
changed it. Either way, the silence isn't credited. `unverified` is only for what was known
before any case ran; a failure after the cases started is never reported as `unverified`.
Nothing short of `pass` satisfies this acceptance, and the XML and unit tests can't stand in
for it.

**Separate from the run's result, and not gating.** The verdicts go in their own report, a
future `audio-acceptance.json` beside the run's `result.json` in `-Out`. That file is design
only. It names the same `runId`, `cutId`, `role`, `architecture`, `source` and `build` as that
`result.json`, so it is bound to the same run, cut and artifacts.

The audio cases are never steps in `result.json`, and an audio verdict never changes:

- the run's `outcome` or steps (§7, §8);
- whether the cut or the other role is `invalid`;
- the one diagnosed retry;
- §3's v1 and v2 bars, Beta1's three releases included.

An audio `fail`, `invalid` or `unverified` is reported as it is, beside the run's outcome and
in the weekly #357 summary, and stays unaccepted until a later run passes. A run that passes
is never reported as an audio pass, and there is no waiver. A bad audio verdict buys no extra
attempt, VM or resource. Whether Quality admits this report, and for what, is future reviewed
work.

**Evidence.** The report records, for each run and role:

- the OS build, role and architecture, and the session's endpoint capability: present or not,
  and both controls' results;
- each setting of step 1 by name, with its baseline value and its value at every recheck, as
  plain values such as on, off, muted or a volume percentage. No raw registry export, account
  identifier or user path is recorded;
- the cut and artifact identity (§7 `source` and `build`);
- the case, and what was seen and heard: whether the toast and its actions were seen, and the
  peak captured level against the threshold.

Only that redacted record is kept, under §9. It never includes:

- raw audio;
- the owner's environment or accounts;
- credentials;
- anything recorded from a microphone.
