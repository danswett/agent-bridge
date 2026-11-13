# Native and optional HA validation plans

**Design only, #2382 / #527 slice 1; November 12, 2026 (Pacific).**
This note changes no selection, check, workflow, catalog, solution or runtime.
Quality owns #357/#487's selector and gate hardening; implementation needs
separately approved slices and exact-head independent workflow review.

## Decision and retirement boundary

Native Slipgates delivery must not wait for HA shadow `PASS`, writer handover,
legacy retirement or delivery of an optional HA client. Existing supported
compatibility still needs meaningful regression coverage; separating validation
is not deleting that coverage or turning a failed check into a skip.

Read #389's reconciliation before its historical acceptance text: #319 is closed
as superseded, not a pending replacement-integration gate. Live HA removal follows
#1396's retirement gate, including load parity #1949, the HA-off rehearsal and
owner sign-off. Rollback means turning HA back on. HA is an optional client later
(#1419), not a retirement prerequisite. #387 retains ownership of held publisher
work disposition. This note grants no retirement, cutover or live access.

## Evidence read

All source citations below are repository-relative line numbers at audited main
`e16dc3674e21f4974dabd51b8bdee3ebd6ad07b3`, not #527's older `9762da98` lines.
These are observed couplings, not claims that this design is implemented.

**Read whole:** `.github/workflows/ci.yml`,
`.github/workflows/ha-contract-drift.yml`, `quality/catalog.yml`,
`quality/merge-rule.yml`, `AgentBridge.slnx`,
`tools/AgentBridge.Quality/RingCommand.cs`, `build/host-identity.ps1`,
`src/AgentBridge.Host/AgentBridge.Host.csproj`,
`src/AgentBridge.HomeAssistant/AgentBridge.HomeAssistant.csproj`,
`tools/AgentBridge.Rehearsal/AgentBridge.Rehearsal.csproj`, and
`tools/AgentBridge.Rehearsal/Cli.cs`, `docs/design/ha-optional-client.md`.

**Section reads, not whole-file reviews:** exact omitted sections are below;
isolated search hits do not count as reading an omitted section.
`Rehearsal/` below abbreviates `tools/AgentBridge.Rehearsal/`; bare build script
names retain the `build/` root. `ci.yml` and `ha-contract-drift.yml` are under
`.github/workflows/`; `catalog.yml` is under `quality/`.

| File | Sections read | Sections not read |
|---|---|---|
| `quality/README.md` | 1-220 | 221-1044 |
| `docs/ha-connector.md` | 1-220 | 221-1589 |
| `docs/host-shadow.md` | 1-220 | 221-875 |
| `Rehearsal/Program.cs` | 125-240 | 1-124, 241-331 |
| `Rehearsal/SoakDriver.cs` | 1-100 | 101-607 |
| `build/pack-app.ps1` | 1-170 | 171-172 |
| `build/publish-prerelease.ps1` | 70-180 | 1-69, 181-450 |
| `build/shadow-week.ps1` | 1-35 | 36-585 |
| `build/shadow-soak-vm.ps1` | 1-48 | 49-386 |
| `build/rehearsal-azure-vmw.ps1` | 1-60 | 61-1731 |

Every line-number citation was checked offline against the audited base before
freezing this note, including range endpoints. None lies outside the declared
reads. Exporter bodies were not read: their table row is explicitly marked
**unread implementation**, supported only by the workflow's references.
Engineering supplied the complete issue text for #2382, #527, #389, #319 and #387.
Neither hosted runs/logs nor live installations were inspected or exercised.

## Plan A: native product delivery

The existing selector/catalog/project graph remains the single selection source.
`validation_plan` already exists and is explicitly **reporting only**
(`quality/catalog.yml:9`); it must not become an exclusion switch or a second
selector. Quality approves any future plan-boundary implementation under #487.

| Change or stage | Native proof that must remain |
|---|---|
| Ordinary native changes | Affected protocol/core, host/delivery/hooks, local API, signed-command, transport/crypto/roster, client and relay tests, with existing sentinels and security escalation |
| PWA, Windows or Apple client changes | Affected unit, UI, accessibility and protocol-conformance coverage on the declared supported platforms; planned/partial suites remain explicitly incomplete |
| Packaging/build/installer changes | NativeAOT hook/injector, release/dev identity, package contents, signatures when required, architecture/channel checks, installer input and installed-app self-test |
| Native end to end | Host/session/answer/reply/launch/stop/attachment flows over the normal client protocol; relay smoke, Web Push and generic lab/conformance evidence survive |
| Selection, workflow, catalog, solution or build-root changes | Existing independently wired whole-ring escalation and catalog completeness checks; a changed selector cannot choose no tests for itself |
| Main/release | Known last-passed ancestor planning, all selected platforms, matching tested artifacts, monotonic release/commit ordering, signing and provenance; unknown base remains conservative |

Native-only paths select their affected suites, not HA containers, legacy drift
or HA cutover evidence merely because the product shares this repository.
For example, a PWA-only change must retain its PWA/protocol tests without adding
the HA integration job. Shared dependencies and mixed changes retain both plans'
affected tests while the connector is supported; an adapter change can also reach
native host packaging through today's project graph. Do not mask that closure.
Unknown/unclaimed paths or unreadable planning inputs must run conservative
coverage, including supported HA coverage when its impact cannot be excluded.

The native PR evidence remains `quality-gate` (never the draft fast-ring check),
`merge-rule`, applicable CodeQL/security/dependency/workflow checks and independent
receipts under the current rules. Selected jobs must finish successfully; missing,
skipped, cancelled, failed or unknown selected results cannot satisfy a gate.
The existing merge rule also judges other head checks and main's health.
No new required-check name, repository setting, rule exception or weaker
main-green definition is authorized here.

Target publication consumes the native validation verdict and the same run's
tested app packages. It must not depend on HA shadow reports, a writer election,
legacy credentials or retirement progress. Today `publish-gate` still depends on
the combined `quality-gate`; nothing in this note decouples it.
The first approved follow-on slice must be Quality's, not a builder's, because it
changes gate wiring.
If both plans are affected, HA failures still block the supported HA change
under its approved acceptance policy; this design is not a waiver of red checks.

## Plan B: supported HA compatibility and cutover

Optional means the person need not use HA, not that supported behavior may fail
silently. While the compatibility connector ships, preserve:

| Evidence | Scope and outcome |
|---|---|
| Connector component tests | MQTT/entity/card contract, targeting/authorization, refusal, retry and state behavior; run affected adapter tests without provisioning HA for unit coverage |
| Disposable integration | Actual HA and Mosquitto onboarding plus `Category=HomeAssistantIntegration`; require an actual successful run when selected |
| Fixtures/exporters/drift | PR fixture reproduction at pinned legacy commits, scheduled upstream comparison and explicit drift reporting; keep source/provenance and recording/reporting permission separation |
| Migration/rollback and legacy comparison | Existing evidence and failure cases remain owned and available; do not make HA shadow `PASS` or writer handover prerequisites for ordinary native delivery |
| Retirement | #1396/#1949 evidence, required parity rows, approved HA-off rehearsal and owner sign-off; missing evidence is incomplete, not permission to remove anything |

Compatibility-affecting paths include the adapter/tests, HA CI/onboarding,
exporters/fixtures, relevant migration/shadow code and scripts, and shared
dependencies that reach them. Run both plans for mixed/uncertain impact, retaining
the selector's graph, security escalation and completeness invariants.
Keep failed, not-run, unavailable and unsupported results distinguishable.
Historical shadow gate durations/counts do not replace #1396's current gate.

After the retirement gate and explicit #389/#387 disposition, separately reviewed
slices may move HA-only integration/exporter/contract evidence to the HA project,
with pinned legacy source, license/provenance and least-privilege reporting.
Bridge-owned generic checks and required rollback evidence do not move by name.
An optional future client uses the normal protocol and its own conformance proof;
its design is neither proof of present compatibility nor permission for deletion.

## Coupling dispositions and required future proofs

Each proof below is a **failing-first requirement for a later approved slice**,
not a test added or executed by this documentation PR. Keep #487's existing
test harness and owners; do not introduce a parallel selector or gate engine.
Rows describe target ownership; all listed files stay unchanged in this slice.

| Current coupling at the audited SHA | Moves / stays | Failing-closed proof required before implementation lands |
|---|---|---|
| `ci.yml:44-49,80,217,466-511`: release rationale, plan output/JOBS and disposable HA job | HA execution belongs to compatibility; genuine release identity stays native. Change plan outputs/JOBS/job wiring together only in an approved slice | A native-only fixture selects native suites without HA; an adapter/shared fixture still selects HA; removing a selected job or output makes catalog/gate checks fail |
| `ci.yml:771-800`: `publish-gate` needs `quality-gate` at 794, indirectly including HA | Target native publication verdict stays tied to selected native checks and tested app artifacts; HA shadow/retirement evidence stays outside it | Selected native failure/cancellation/skip/missing result, failed plan, or absent app artifacts prevents publish; deliberately unselected HA does not prevent an otherwise eligible native publish |
| `ci.yml:1610-1674`: aggregate `quality-gate`, HA in `needs` at 1624 | Native/compatibility aggregation must retain explicit selection and results; no drop from `needs` alone | HA selected but skipped/failed/unknown cannot pass; native selected but absent cannot pass; draft fast-ring success cannot stand in for the required gate |
| `quality/merge-rule.yml:54-89`: every head check and `main_green` includes HA | Existing merge policy stays unchanged; any later native-main/compatibility policy change needs separate Quality approval | Native gate green cannot override failed required head evidence; failed/unknown selected main checks still hold; no claimed split while the old main-green coupling remains |
| `catalog.yml:257-271,648-686`: `homeassistant`, integration, drift and fixtures | Supported regression coverage stays; later approved placement may move HA-only suites after retirement/disposition, preserving IDs/provenance or explicit mappings | Adapter/fixture changes retain their required suites; unclaimed paths, missing projects/workflow jobs and invalid plan values fail completeness checks |
| `catalog.yml:1054-1074`; `quality/README.md:91` (**section-only**): ring-4 migrate/rollback and shadow `select: always` in a general release row | Scope their evidence to compatibility/cutover, not native release prerequisites; native ring-4 production/client/accessibility evidence stays | Native release fixture does not demand legacy shadow `PASS`; a compatibility/cutover fixture cannot report success without its required evidence. Never simply change `always` to hide missing coverage |
| `RingCommand.cs:12-30`: `HomeAssistantIntegration` fast-ring exclusion | Keep the current in-process filter while HA remains; any later category retirement is atomic with suite/runner changes | Ring 0 still excludes process/network/disposable-HA tests; existing compound filters retain their meaning; later rings retain required integration coverage |
| `AgentBridge.slnx:7,24,32,42`; host project:35; rehearsal project:20; `ci.yml:306` builds the whole solution | References stay until callers/tests are dispositioned; generic native build and rehearsal checks survive any future split | Build all supported targets and graph closures; removal with an undispositioned caller fails; native startup without configured HA/MQTT succeeds and selects meaningful native tests |
| `Rehearsal/Cli.cs:45-98`, `Program.cs:166-239`, `SoakDriver.cs:10-24` (under `tools/AgentBridge.Rehearsal/`; latter two **section-only**) | Later separate `HaCheck`/`SoakHa`, token-file/entity and legacy-comparison drivers; keep host/session/transcript/baseline/evidence/conformance checks | Generic runs need no HA token, entity or broker; HA checks still refuse non-disposable targets and bad credentials; absent required evidence never reports pass |
| `ha-contract-drift.yml:13-16,19-34,60-80,104-128`; `tools/ha-contract/` exporter/report paths (**unread implementation**) | Keep current pinned fixtures and credential-free recording; move HA-only work only after authorized disposition | Changed contract or missing exporter artifact fails; PRs cannot write drift issues; upstream recording cannot inherit reporter credentials; moved fixtures reproduce the pinned source |
| `build/shadow-week.ps1:1-34`, `shadow-soak-vm.ps1:3-35`, `rehearsal-azure-vmw.ps1:3-31` (**section-only**) | Retain current rollback/choreography and provenance; later extract generic lab work, never execute/remove live infrastructure for this audit | Disposable-only guards and interrupted install/rollback refusals survive; generic lab proof needs no legacy writer; a failed/partial rehearsal cannot become native or cutover success |
| `build/host-identity.ps1:1-5`; `pack-app.ps1:79-115`; `publish-prerelease.ps1:143-176` (latter two **section-only**) | Dev/release identity, package integrity, both architectures, channel/signature checks stay native; HA rationale may become historical only after approved retirement | Wrong identity, architecture/channel, missing artifact or required signature refuses packaging/publication; removing HA rationale never removes these assertions |
| `catalog.yml:288-301,449-460,1032-1042`: rehearsal-tool, build-scripts, real-agent lab reuse | Generic evidence stays native even where a driver/test originated with HA; later split only the HA-specific portions | Generic baseline/host/session/evidence regressions still fail; HA-driven soak cannot masquerade as HA-free end-to-end coverage or turn a partial lab suite into a full pass |

The later atomic changes must also disposition permissions, dependency/cache
inputs, project/build closures, workflow job/needs/output lists, suite IDs/triggers,
ring commands and packaging expectations. Check the wiring as a whole; a green
isolated selector test is insufficient. Preserve G5/G6/G7 and owner-only guards.

## Expected follow-on slices

These are proposed boundaries, not grants, estimates or a new dependency order:

- Selector plan boundary, owned by Quality under #357/#487: **needs separate approval; blocked by #389/#387 disposition where it deletes anything**.
- Publish-gate decoupling, owned by Quality: **needs separate approval; blocked by #389/#387 disposition where it deletes anything**.
- Catalog ring-4 scoping, coordinated by Quality: **needs separate approval; blocked by #389/#387 disposition where it deletes anything**.
- Generic/HA rehearsal split: **needs separate approval; blocked by #389/#387 disposition where it deletes anything**.
- Exporter/fixture move to the HA project: **needs separate approval; blocked by #389/#387 disposition where it deletes anything**; the retirement gate also applies.

## Exclusions, ownership and acceptance

Only this design document changes. Specifically **not touched**:

- Code, workflows, gates, catalog, selector, solution/project references, settings
  or repository rules; no label, check requirement or production-approval change.
- Deletion of compatibility tests/tooling, MQTT/entity/card coverage, credentials,
  configuration or installed migration behavior. #319's superseded history is not
  deletion authority; #389's current retirement gate and #387 disposition still govern.
- #525 runtime seams, #526 credential lifecycle, held #331/#337/#345 work,
  other owners' #258/#175/#78 scope, or optional #1419 implementation.
- Live HA/MQTT, daemons, hooks, scripts, installed state, VMs, shared build output,
  owner paging or alerting. No installer, VM, exporter or rehearsal script is run.

Quality coordinates #357/#487, agrees this document in writing on the PR and owns
admission, gates and merge. Engineering coordinates content and the HA owners.
Both independent model families must read the **whole document** on the exact
frozen head; delta-only or hunk-only reads do not meet this slice's requirement.

Acceptance is both plans plus every coupling disposition/proof above, Quality's
written PR agreement, and those whole-document reviews. Validation for this
docs-only slice is offline `git diff --check`; there are no tests, builds,
`quality/run.ps1` execution or hosted-red proof. Future implementation must supply
its own failing tests and required platform/CI evidence, never inherit this note
as a passing check or permission to retire compatibility.
