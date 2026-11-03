# Status, plan and handoff

**New contributor or fresh agent session? Start with [CONTRIBUTING.md](../CONTRIBUTING.md).**
This page supplies project context, dated snapshots and links to current role holders.
The [contributor workflow](contributor-workflow.md) owns durable coordination procedures;
canonical issues and PRs hold current assignments and exact-head evidence.
Keep this page current when a change moves the project along, and retain explicit
dates on historical snapshots rather than treating them as current assignments.

*Last updated: 2026-11-06, Pacific (contributor entry path, #1045; product decisions of
11-04 and 11-05, including the no-RDP tenet). Older implementation and machine snapshots
below retain their stated dates.*

**Product tenet (owner, 2026-11-04): you never have to RDP into a machine.** Anything that can
only be fixed or diagnosed at the machine is a product gap: it is labelled `needs-rdp` and
prioritised by [#696](https://github.com/danswett/agent-bridge/issues/696) rule 5.

| Read next | For |
|---|---|
| [CONTRIBUTING.md](../CONTRIBUTING.md) | The first-contribution path for people and their agents |
| [Contributor workflow](contributor-workflow.md) | Canonical records, ownership, participation, readiness and handoffs |
| [#696](https://github.com/danswett/agent-bridge/issues/696) | The order of work: epics, Now/Next/Later and intake rules. The Project's **Epic** and **Horizon** fields index it |
| [design.md](design.md) | The master plan and architecture: goals, protocol, security, relay, host, apps, roadmap (§10), risks |
| [parity.md](parity.md) | Every agent-ha-bridge feature mapped to this product, with evidence or an issue |
| [parity-checklist.md](parity-checklist.md) | The scripted migrate-and-rollback rehearsal |
| [rehearsal-2026-10.md](rehearsal-2026-10.md) | The rehearsal's final report and verdict |
| [migration.md](migration.md) | `migrate` and `rollback`: what moves and what doesn't |
| [/quality](../quality/) | The test plan: rings from inner loop to release, and the catalog of every suite |
| [/ops](../ops/README.md) | Running the live service: the SRE plan, SLOs, alerts, runbooks and incident process (#568) |
| [/AGENTS.md](../AGENTS.md) | How to work in this repository: rules, merge procedure, releases, relay deploys, guards |

## 1. Goal

Replace [agent-ha-bridge](https://github.com/danswett/agent-ha-bridge) (a PowerShell daemon
plus a Go hook, using Home Assistant as UI, bus, hub, identity and phone client) with a
standalone product. The product has:
- a .NET host, one per machine;
- a WinUI 3 Windows app that also installs and manages the host;
- a phone PWA on GitHub Pages, with native phone apps later;
- an end-to-end-encrypted relay on Azure Container Apps.

All current features carry over: cards, hooks, answers, replies, attachments, launch,
resume, stop, usage, updates and transfers.

**Hard rule:** the live HA bridge keeps working throughout. Machines move one at a time,
with rollback, and the publication writer (DSWETT-HOME) moves last.

**Current goal (decided 2026-10-29):** parity with agent-ha-bridge first, then migrate
real machines. New features wait, except the few marked "beyond parity" in parity.md.

**HA redirect (owner, 2026-11-02; reconciled 2026-11-05):** HA functionality must
remain without any legacy daemon. #387 redirects delivery to the separate
[HA integration and companion card](https://github.com/danswett/agent-bridge-home-assistant/issues/1);
HA owns entities/actions/presentation and Bridge remains generic at protocol/core.
The current host connector is compatibility, not that target implemented.
#318's permanent native publisher direction is superseded, not completed.
#317 remains the parent goal, #319 the verified reversible production cutover,
and #389 cleanup only after replacement. G7 still refuses writer migration.
[Disposition and dependencies](ha-connector.md#retaining-ha-without-a-legacy-daemon-317)
retain #322 and existing owners. #331/#337 closed unmerged on 2026-11-05 at
12:45 PT, with branches and heads kept. #345 closed
unmerged on 2026-11-05 at 00:59:49 PT, with sources retained. #394's original
architecture/enrollment proposal at `d9382fd31dc9fad9d34919317bf7a3265de43c18`
is preserved historical, unadopted evidence, withdrawn from its corrected
documentation-only scope. #522 is merged design and #534 a draft read profile
with reference fixtures, not proof of installable HA delivery.
Copilot CLI session `d5ac689c-a9f6-4618-8e8f-9220793487f4` retains coordination.

## 2. Where things are (2026-11-02)

### Playwright system packages (#831, 2026-11-04)

[#831](https://github.com/danswett/agent-bridge/pull/831) extends the bounded, signed
APT download and guarded Canonical fallback to every CI Playwright dependency install.
PWA UI, end-to-end and both Linux spike installs have 35-minute job budgets; browser
lists, shards and test assertions are unchanged. CI covers eight mocked download and
failure paths. Real mirror-fallback proof remains separate in
[#817](https://github.com/danswett/agent-bridge/pull/817).

### Host trusted-peer floor (#402)

An opt-in, default-off host-only authorization foundation is under review. It adds
full-key person/host/service pins, checks both channel directions and nested
principals, and binds standing push access to the current grant. See
[host config](host-config.md#trusted-peers-disposable-host-foundation). Evaluation is
disposable-host only: no relay enrollment, production enablement or live migration
is authorized by this slice. Independent implementation review and final-head CI
remain gates; the wider HA architecture redirect is tracked separately in #388.

### Built and merged

Fresh Windows hook setup now has an explicit local CLI path (`hooks install|remove`,
[host-hooks.md](host-hooks.md)) independent of legacy migration. It covers Copilot/Agency,
Claude and Codex, with ownership checks and a private runtime. Launch workspaces still
require explicit approval. This is the backend slice of #54, not the complete Settings/
first-run experience; automatic CLI installation, sign-in guidance, agent trust prompts,
optional HA and end-to-end readiness remain to be planned together with Settings (#231).

The work has been built phone-first. All merged PRs are listed in §7.

| Area | State |
|---|---|
| Spikes S1–S6 | Done. S7 (macOS), S8 (corporate policy, parked) and S9 (relay at scale) not started |
| Protocol v1 | sessions, answer, reply, attachments, stop, usage, launch, transfer, blobs, feedback upload (#739), roster, update, folder trust. Specs in `docs/protocol/` |
| Host (`agent-bridge-host`) | Read-only core, delivery (answers, replies, attachments, stop, launch, resume), worktree reclaim, hook intake, discovery holds, management refused from agent sessions, migrate and rollback, shadow comparison |
| Windows app | Local and remote machines, Launch page, transfers, toasts, folder trust, Settings, self-update (installs when the host is idle), remote update, Copy button on code blocks |
| PWA | Cards, answers, replies, attachments, launch, stop, usage, push, All machines view, Machines fold, folder trust, remote update, Copy button on code blocks |
| Relay | Production at https://relay.bad-duck.com, image `7c17d4a` (2026-11-01), with a roster store. Staging, then production on approval, via `.github/workflows/relay.yml` |
| HA connector | Stages 1–7, so a migrated machine stays on the existing HA dashboard: status, answers, replies, stop, launch, phone notifications, update buttons, upkeep and forget. Contract pinned to agent-ha-bridge v1.33.33 |
| Releases | Every merge to `main` publishes a release-build pre-release of the Windows app (`v0.1.<run>`, Velopack, with deltas), x64 and, since #222, native Arm64 in a channel of its own. Public release notes automatically include GitHub-generated changes since the previous published version, alongside install instructions and CI provenance. Installed apps check every 30 min and install when idle |
| Rehearsal (#39) | Done: four runs on a Hyper-V VM and Azure VMs. Verdict: **ready for a supervised migration of one real machine** ([rehearsal-2026-10.md](rehearsal-2026-10.md)) |

### Machines

| Machine | State |
|---|---|
| DSWETT-ULTRA | **Migrated** from agent-ha-bridge on 2026-10-31. HA connector on since 2026-11-01 08:48 (online in HA). Updated to 0.1.700 on 2026-11-01 11:06 by **Install from the PWA**, the first remote install on a real machine (#161). It then auto-updated through the unchanged x64 channel to 0.1.914 (11-01 23:47), the first release with Arm64 too. The first real machine; watch it |
| DSWETT-DEV-VM1 | **Migrated** 2026-11-02 14:16 PT on 0.1.1108, a **Dev Box**. `migration status` reads "as migrated" for the Copilot and Claude hooks, hook intake is on, and agent-ha-bridge's daemon and keep-awake tasks are disabled. `devbox status`: the host's own keep-awake is on (#47, #224). The first session launched through the new host (14:20) is the overall coordinator. **HA connector online since 14:33:** the first token stored was refused twice. The host held off as designed (#234): after 3 refusals a token is retried only once `ha token set` is run again, or sent once to a new `homeAssistant.baseUrl`. It was accepted once set again and the host restarted. 20 entities, no `_2` duplicates. The first Restart reported "the host did not start" until a second Start (#280). The read-only **shadow host** was uninstalled at 14:50, keeping its reports and profile; VM-M now carries the shadow gate alone |
| DSWETT-HOME | agent-ha-bridge, and the **dashboard publication writer**: migrate refuses there (G7), so it moves last. Since 2026-11-01 13:39 also a **view-only app install** (0.1.730 or later; `delivery.readOnly`, push off, HA connector off). The PWA lists it online and read-only, and its Claude and Codex usage rows are back. agent-ha-bridge there is unaffected |
| DSWETT-ARM8 (ARM laptop) | **Migrated** 2026-11-01 23:54 on the native **Arm64** build (0.1.914, #222), auto-updating since (0.1.1018 on 11-02). Device key in the TPM; an answer from the PWA was typed into a session, so injection works on Arm64. HA connector online since 11-02 00:13, after an IP ban (below) |
| DSWETT-DEV-VM2 | New on 11-02: the UI coordinator moved here from DEV-VM1 so DEV-VM1 could migrate |
| Mac, DASDESK | agent-ha-bridge only. The Mac waits for P5 (macOS host) |
| Hyperloop VM01 (on DEV-VM1) | Down since a reboot on 2026-10-31: its autologon failed and no one knows its generated password. Left down by the owner's decision; not needed |
| Azure `rehearsal-w`, `rehearsal-m` | **Running** a scripted shadow soak (§3) since 2026-11-01, auto-shutdown off; snapshots S0/S1 kept. `build/rehearsal-azure-vmw.ps1 -Start` / `-Deallocate`; the soak's `build/shadow-soak-vm.ps1 -Finish` deallocates. A Linux VM (`rehearsal-l`) was created for the E13 checks and deleted. Azure: the owner's Visual Studio Enterprise subscription `0e38f344…` only, named on every call (the `~/.azure-agentbridge` config's default is a trial) |

### Running work

Check `gh pr list -R danswett/agent-bridge` for open PRs. The roles and linked
acceptance receipts below are the current role reference. The #170 index cutover
is incomplete: its existing
[canonical tracker](https://github.com/danswett/agent-bridge/issues/170#issuecomment-6590748446)
and [frontier](https://github.com/danswett/agent-bridge/issues/170#issuecomment-6612294268)
remain routing references until every active destination is verified.
- **Quality is the sole merge coordinator**, accepted in
  [the takeover receipt](https://github.com/danswett/agent-bridge/issues/170#issuecomment-6617409924).
  Implementation coordinators, delegates and triage do not merge independently.
- **A, overall implementation coordinator** (session 50e5b45a, DEV-VM1), retains
  backend implementation, delegates and reviews. Its former merge role ended in
  [the stand-down receipt](https://github.com/danswett/agent-bridge/issues/170#issuecomment-6617290473).
- **B, UI implementation coordinator** (session 4d05d459, DEV-VM2), retains track B
  and client integration, yielding merge order to Quality.
- **Triage/readiness** (session 3e736b53, DEV-VM1) maintains canonical routing,
  verifies owner acceptance and consumes the existing single collector. It has no
  merge authority and must not create another periodic GitHub poller.
- **Creative director** (session c187cf6c; formerly titled VP of Design) is additive: audits, proposals and design review
  under [#477](https://github.com/danswett/agent-bridge/issues/477). Material design
  changes still require owner approval; design leadership is not merge authority.
- **Head of Product** (session 3dad9c05) owns the order of work: the epics and each
  item's Epic and Horizon, including those TPM/triage sets on new issues under
  [#696](https://github.com/danswett/agent-bridge/issues/696)'s intake rule. Issue
  triage itself stays with TPM. Changes to the order are decided on #696 with the owner.
  The role has no merge or implementation authority.

#### Operational sources of truth

The authoritative [record map](contributor-workflow.md#operational-sources-of-truth),
[ownership procedure](contributor-workflow.md#claiming-work),
[review evidence](contributor-workflow.md#readiness-and-review-evidence) and
[handoff procedure](contributor-workflow.md#handoffs) now live in the contributor
workflow. This heading remains for existing links; it is not a second policy copy.

#### Running Shiproom

The authoritative [Shiproom procedure](contributor-workflow.md#running-shiproom)
now lives in the contributor workflow; current dispatch remains on
[#689](https://github.com/danswett/agent-bridge/issues/689).
Moving the procedure does not change accepted roles, operating obligations or the
incomplete #170 cutover. See [participation](contributor-workflow.md#participation)
before assuming a standing coordinator role.

<details>
<summary>Historical handoff and resumption notes (2026-11-02; not current assignments)</summary>

The following handoff/resumption notes are **historical, 2026-11-02**; use canonical
issues and PRs for current heads, owners and gates:

**First-run foundation (#384, partial #231):** a shared local-host agent readiness
report and compact read-only Agents section in Windows Settings are implemented in this
change. It distinguishes executable/version detection, ownership/intake evidence and actual
current-run hook activity; CLI sign-in, trust and new-session activation remain unverified.
This does not complete onboarding or #231's setup/repair controls and makes no live hook,
legacy daemon or HA changes. Scope is coordinated on #170.

**Handoff, 2026-11-02 13:30 PT** (the overall coordinator's session ends for DEV-VM1's migration; background delegates are lost, their work is on branches):
- **#238 (#200, live Claude and Codex usage)**, head ac2b8498: CI green, Copilot reviewed, 0 threads. Before merge it needs:
  1. an **independent security re-review** at the final head; earlier rounds were clean of vulnerabilities, and the update-gate paths were the last findings;
  2. the **six live checks on `rehearsal-m`** listed in the PR body (E1/E2, E4, E7, E9, E11, E14), run by a delegate on the VM.
  
  Its worktree is `~/repos/wt/agent-bridge-usage-live` on DEV-VM1 (branch `feat/usage-live`).
- **The VM soak** keeps running on VM-M without a coordinator (driver at bfc4744, shadow host at #251's 670fc077). #251 labels agent-ha-bridge#289 and #290; the earliest gate pass is about 11:18 PT on 11-03 if nothing new recurs. Check with `-Status` (§3), then `-Finish`.
- **After DEV-VM1 migrates:** check its HA entities (online, version, no `_2` duplicates), `devbox status`, then `build/shadow-week.ps1 -Uninstall`. Post on #170. *Done 11-02 14:50 PT.*

**Resumed, 2026-11-02 14:20 PT** (session 50e5b45a). Delegates restarted: #238's security re-review and its six live checks on `rehearsal-m`; #263 (host name cleaning); #266 (hooks test flake); S2 (#174). The Apple client drafts (#254, #260, #264–#268, #276, #277) are another contributor's and are not coordinated here.
- **Next on track A:** S2 (#174), using the sub-agent transcripts the soak recorded (in `~/repos/wt/agent-bridge-soak-transcripts`, not committed). #175 still needs its live check.
- **Queued backend:** #231 (agents installed later), #257 (provider-neutral account ID), #239, #206, #207, #241.

Historical native publication work under #317, superseded by #387's redirect:
- #322's merged protocol-1 policy/fence contract and recordings are retained
  compatibility evidence, not publication enablement. #331/#337 closed unmerged on
  2026-11-05 at 12:45 PT, with branches and heads kept; #345 closed unmerged on
  2026-11-05 at 00:59:49 PT, with sources retained. The
  [retain/extract/supersede boundaries](ha-connector.md#publisher-work-disposition-387)
  replace the former instruction to continue that stack.
- #319 still gates writer cutover on the replacement integration/card and
  disposable-HA handover/rollback verification. No live HA or legacy-install
  changes during development; no cleanup under #389 before that gate.

</details>

## 3. Shadow gate (#40)

**Scope (owner, 2026-11-03).** The shadow gate is a checkpoint for comparing against HA and
retiring the legacy bridge. It is **not** a prerequisite for native Agent Bridge delivery. Host,
app, PWA, relay and native MCP work ships on its own gates:
- tests, review and security review on the exact head;
- truthful native product behaviour;
- a qualified main under the serial merge rule;
- explicit production approval where that is required.

Nothing native waits for Home Assistant, for a shadow PASS, for the dashboard writer cutover
(#317/#319/#389) or for retiring the legacy bridge. Those are an optional compatibility and
retirement lane with their own acceptance criteria. #238 needs its own security review and
product live checks, not a shadow PASS. #45 uses the product's native agent identity, not HA
credentials. The VM safety and finish rules below apply to this lane only.

The gate is coverage-based (design §10, Stage 4). Since DEV-VM1 migrated (11-02 14:16 PT) and
its shadow host was uninstalled (14:50), **VM-M's scripted soak is the only comparator.** Check it
from the operator's machine:

```
./build/shadow-soak-vm.ps1 -Subscription <id> -Status
```

DEV-VM1's shadow reports stay on disk under its `shadow` profile, but they are history, not a
current gate reading. The record of that shadow follows.

- **2026-11-01:** FAIL, from 43 `background.shells` episodes in one session before 09:57 PST. These are agent-ha-bridge#276, which the daemon gets wrong: a shell restarted under the same id is lost. #162 now labels them as a known difference (the shadow build is at 350bce6, which has it). The FAIL clears when they leave the 24 h window (about 10:00 PST on 11-02). Before that the verdict was **NOT YET**, on coverage; at 11:00 PST on 11-01 it read questions 40 of 100, sessions 10 of 30, status transitions 408 of 500. Claude and Codex have no sessions there yet.
- **2026-11-02 10:05 PST:** the `background.shells` FAIL has cleared. Two short episodes still hold it at FAIL, and both leave the window by 13:53 PST today:
  - `session.missingOnHost`, 74 s at 11:03 PST on 11-01: a session the daemon saw end;
  - `status`, 76 s at 13:52 PST on 11-01.
  
  Both look like timing just over the 60 s threshold. Coverage is still short: questions 31/100, sessions 7/30, status transitions 353/500, resume snapshots 65/100. DEV-VM1's shadow ends with its migration, so VM-M carries the gate.
- **Scripted soak on Azure VM-M (decided 2026-11-01, so as not to wait for organic use).** VM-M runs agent-ha-bridge and a read-only shadow host built from main, against VM-W's disposable HA. The driver is `build/shadow-soak-vm.ps1` (#208), and its sessions cover Copilot, Claude and Codex. Coverage from VM-M counts toward the gate.
  - Two agent-ha-bridge bugs are labelled as known differences under narrow rules (#226): Copilot's `name:` titles in the resume list (agent-ha-bridge#278) and Codex compactions (agent-ha-bridge#279).
  - At 11-02 03:05 PT: no unexplained mismatch since the #226 host went on (00:45 PT). Coverage: sessions 21, questions 42, status transitions 163, background 10, compactions 1, resume snapshots 58.
  - The earliest pass is about 01:00 PT on 11-03, when the older mismatches leave the 24 h window.
  - At 11-02 10:05 PT `-Status` read FAIL: compactions 1, model 2, resumable 17, session 1, status 2, usage.missingOnDaemon 3 in the last 24 h. Coverage: sessions 42 (Copilot 19, Claude 18, Codex 5), questions 133, status transitions 501, responses 300, background 50, compactions 7, resume snapshots 124. Being triaged: which of the mismatches came after 00:45 PT.
  - Triage (11-02 11:00 PT): every mismatch after 00:45 PT is an agent-ha-bridge bug, none a host bug. agent-ha-bridge#289: an ended Claude session comes back when a new one reuses its pid. agent-ha-bridge#290: a resumed Claude session's answered question is held about 60-70 s, with the model left as the launch alias. #251 labels both under narrow rules (independently reviewed clean, merged 9aa9fcf6) and has run on VM-M since about 12:35 PT. The earliest pass is about 11:18 PT on 11-03, 24 h after the last unlabelled episode, if nothing new recurs. Coverage is past every target. Codex usage "not compared" is expected: Codex is capped and has no current reading.
  - Codex is capped: its weekly allowance read 95% used.
  - Check VM-M's gate with `./build/shadow-soak-vm.ps1 -Subscription <id> -Status`, which runs `shadow summary --gate` on VM-M. Run `-Finish` only after that reads PASS.
- DEV-VM1's shadow ended with its migration (uninstalled 11-02 14:50 PT); VM-M is the comparator.
- To move VM-M's shadow host to a newer build after relevant merges, use `-Stop`, then `-Prepare -HostRef <ref>` (which runs `build/shadow-week.ps1 -Update` on VM-M), then `-Start -Caps @{ Codex = 6 }`. Always repeat the cap: a bare `-Start` recreates the driver task with no caps and would resume Codex episodes against its nearly spent allowance. DEV-VM1 has no shadow tasks left, so `-Update` there fails. Agent sessions can't use `shadow-week.ps1 -Install` on a real machine; the #67 guard refuses it.
- #163: a skipped sample splits one disagreement into many episodes. Not fixed.

## 4. Next steps, in order

The order of work across the product is
[#696](https://github.com/danswett/agent-bridge/issues/696). Trust and daily-use
reliability come first. The feature freeze for the P0s ended on 11-04 at 23:08 PT, when all four
closed. The steps below are the machine and migration steps within it.

1. **Watch ULTRA.** Its HA entities (`*_dswett_ultra_*`) and the PWA should agree, and answers, replies and launches from the phone and the HA card should all work. Install from the PWA worked on 2026-11-01 (0.1.690 to 0.1.700); Install from the HA card's button is still untried live (#161).
2. ~~**HOME view-only install**~~ Done 2026-11-01: the PWA lists HOME online and read-only.
3. **Migrate machines while the soak runs** (owner, 2026-11-01): ~~ARM8~~ done. ~~DEV-VM1~~ done 11-02 14:16 PT. Its keep-awake has landed (#224), and the commands are with the owner. Then DASDESK when it's online, and the Mac after P5. Repeat rehearsal step E6-07 (worktree reclaim) on one of them.
4. **The VM soak's gate** (§3; the legacy and HA-comparison lane): report it when it passes,
   then run `-Finish` to deallocate. No native delivery waits for it.
5. **Writer migration** (P7, #317; the optional HA compatibility lane): verify the separate
   HA integration/card replacement (#387), then reversible handover/rollback (#319). Do not
   resume superseded #318 publication work, bypass G7, or retire HA functionality as a
   migration prerequisite. Cleanup remains gated under #389. None of it gates host, app, PWA,
   relay or native MCP work.
6. **UI design track** (§5).
7. **Later:** code signing, macOS (P5), native phone apps (P8), relay scale (S9), observability (§5.10), and decommissioning the old relay stamp in the trial subscription (awaiting the owner's approval).

## 5. UI design track

The design session's output is in the repository (#182). The owner **signed off ui.md on
2026-11-01**; subagents.md's sign-off and its §8 questions sit with the overall coordinator
(track A). A UI coordinator session runs track B and keeps this section
current. Since 2026-11-02 13:10 PT that is Copilot session 4d05d459 on a Cloud PC, which
took over from f7ba0bcc when DEV-VM1 began migrating. Quality now owns all merges
(see Running work above). The sources:

- [design/ui.md](design/ui.md): the spec for every client (status language, colour, type,
  layout, surfaces, platform shells);
- [design/subagents.md](design/subagents.md): the sub-agent model and protocol (#84);
- [design/ui-plan.md](design/ui-plan.md): the slices, their order and how to do one;
- [/design](../design/README.md): `tokens.json`, the status glyphs, the icons and the
  Slipgates Sans and Slipgates Mono fonts;
- [design/mockups](design/mockups/README.md): every design-canvas board rendered as an
  image, for reference only (ui.md stays the spec). The source is in `/design/canvas` and
  the renderer in `tools/design-mockups`.

| Slice | Issue | What | State (2026-11-02, 15:00 PT) |
|---|---|---|---|
| S1 | #173 | Sub-agents: host model and protocol, Copilot first. May start before sign-off | Merged (#187) |
| S2 | #174 | Sub-agents: Claude and Codex sources | Track A, in review (#304) |
| S5 | #175 | Sub-agents: HA connector and shadow | Track A (#199) |
| U1 | #164 | Session list keeps the person's order | **Done**: #190 (PWA), #194 (app) |
| U2 | #165 | "Needs you" lane | **Done**: #202 (PWA), #203 (app); approve-once refuses bidi and invisible characters |
| U3 | #177 | Design tokens generator; both clients switch to the colour roles | **Done**: #188 (generator, PWA), #193 (app) |
| U4 | #178 | Status glyphs and icons replace card edges and glows | **Done**: #204 (PWA), #205 (app); #211 fixed the clash that turned main red |
| U5 | #179 | PWA layout (half of #166) | **Done**: #217, #218, #221. Follow-ups #249 in review (#252) |
| U6 | #180 | Windows app layout (half of #166) | **Done**: #216, #219, #245 |
| U7 | #168 | PWA platform layer | **Done**: #259 |
| U8 | #176 | Delegates in both clients | PWA part 1 (attribution) done in #230. **App done**: #250 (closed #229), after a clean security re-review. PWA part 2 (strip, tab, inspector) is pushed to `ui/u8-pwa-delegates`, stacked on #252 (in review, security review next). It becomes a PR once #252 merges |
| U9 | #232 | Motion (ui.md §5.3), signed off 2026-11-02 | Spec and tokens done in #233. In review: #261 (PWA, draft; security review before merge) and #275 (app, draft) |
| U10 | #253 | One row, one question: Needs you becomes a filter, the question lives only in the conversation, and an approval's notification only opens it. Signed off 2026-11-02 (option A, every recommended answer) | Spec: #302 (ui.md, subagents.md §7, ui-plan.md, boards). Next, three slices, each with a security review: U10a PWA, U10b Windows app (its toasts already done in #377), U10c Apple in #260 (@welshofer) |
| | #600 | Reply and answer progress: one receipt under the box or on the question card, updated in place (Sending… / Sent / Passed to the agent / Up next / Delivered). **Decided by the owner 2026-11-04**, every recommended option (D1–D7: plain words, 400 ms quiet window and dwell, 3/5/10 s stalls, no timings in the UI, ring plus check badge, text2, older hosts unchanged); follow-ups settled 09:10 PT (#477): the `check` icon badge, "You answered · value", older hosts get the same waiting words and Delivered (superseding D7); **revised on re-review the same day to "quiet unless something's wrong"** (#617): Send's ring is the only in-flight signal, Send cross-fades to a check for about 1 s on Delivered, a line appears only for exceptions, stages are still spoken, and the check badge is dropped | Spec: #617 (normative in ui.md §6.5, with §5.3, §6.4, §8, §9; proposal and boards in `docs/design/proposals/600-reply-progress`). Builds on both clients after the host side, #598 / PR #611 |
| | #167 | App icon (#167's last deliverable) | **Done**: #278. Windows and the PWA are generated from `tokens.json` `appIcon`. The SVG sources for macOS, iOS and Android are in `design/app-icon`, and their catalogs come with P5 and P8. Three proportions not on the design board need the owner's eye (#170) |
| | #231 | Settings: each agent's state and "Set up" (UI half) | Slice 1, the card and grey dot for an agent installed after the bridge, with Set up and Not now per agent and version, is in review (Windows app only, on today's one-page Settings). Not started: the sectioned Overview and Agents panes, Repair, Install… and the PWA's read-only view |
| | #181 | Monospace: **Slipgates Mono** (was Agent Bridge Mono; our OFL build of Iosevka, `design/fonts/slipgates-mono`), decided 2026-11-01 | Bundled in both clients (U3). Rebuilt at a normal 600 width for #295, and with Medium and Vietnamese for #544 |
| | #544 | Text face: **Slipgates Sans** (our tuned OFL Inter, `design/fonts/slipgates-sans`), decided 2026-11-03 | Generated into the PWA, Windows, Apple and Android outputs (#557). Device checks to follow |
| | #183 | Light-theme `needs` glyph and `text3` fail contrast | Fixed in #188 |
| | #240 | The app crashed when high contrast was turned on or off | Fixed in #243. Setting `AppWindow.TitleBar.PreferredTheme` made WinAppSDK repaint the caption buttons off the UI thread. #248 fixed the self-test flake that followed |

Track B ran as delegates, in this order:
1. U1 then U2, and U3 then U4, as two chains in parallel;
2. then U5 and U6, in parallel;
3. then U7, U8 and U9, in parallel.

Every PR that touches the Needs you lane, attribution or reply pre-fill gets an
independent security review before it merges. Delegate names come from the agent, and a
prompt-injected agent can choose them. So both clients clean each name, cap its length
and never show it as "Main agent". Every line where a delegate asks or works carries the
fixed word, `Delegate "…" asks`, because folding look-alikes misses some (a Cyrillic
`Маіn agent` got past it in #250's review). The stop pre-fill names the delegate by id,
not by name (#250, #252). The same cleaning at the host, in push notifications and in HA (the
`subagents` attribute and the asker in HA's questions and notifications) is #263 (track A). #220 is separate: bidi and invisible characters in an approval's command.

UI PRs enter Quality's merge queue as soon as each meets the merge rule; they do not
needlessly wait for unrelated backend work. An overlapping backend PR rebases onto
the accepted merge order (the earlier UI-first dependency decision was 2026-11-01,
16:05 PT).

ui-plan.md §1 shows the dependencies. Changes to the layout touch both the PWA (`pwa/`)
and the app (`src/AgentBridge.App`) and keep them at parity.

**From #182's review:** four subagents.md findings are recorded as S1 requirements on #173
(a terminal outcome for done/failed, idle delegates not counted as running, per-field
provenance, a budget for `activity.own`), one shadow finding on #175, and in U4 (#178)
ui.md §2.2 rule 7 becomes an overlay after rules 1–6 while rule 8 refines rule 4 only.

## 6. Open issues by theme

- **Parity gaps:**
  - #42: Claude free text and an empty multi-select;
  - #45: MCP server retargeted to the host;
  - #47: Dev Box keep-awake: shipped in #224 (0.1.977) and on since DEV-VM1's migration (11-02);
  - #48: macOS and Linux tmux delivery;
  - #52: usage settings;
  - #53: card wording;
  - #54: hook registration from the app;
  - #56: cancel a question from another device;
  - #78: launched sessions get the agent HA token;
  - #93: migrate carries `usage.*`;
  - #95: bound the resumable sessions' folder checks;
  - #172: usage per account, shared between hosts (merged in #186; follow-ups #206, #207).
- **Bugs and follow-ups from live use:**
  - #101: app test timing races, fixed by #401 (each app test runs on a UI thread of its own); #241, a DevBox keep-awake fake-time race, is separate;
  - #102 and #150: launch busy wording;
  - #110: an HA connector edge case;
  - #118: discovery held 2 min after an npm Copilot start;
  - #122: the Launch page's busy banner;
  - #123: Codex's cached plugin on a migrated machine;
  - #146: no reason given for a missing Other;
  - #157 and #158: folder-trust follow-ups;
  - #161: remote-update follow-ups;
  - #163: shadow episodes.
- **Beyond parity:**
  - #76: HA connector;
  - #128: Forget;
  - #134: driver edge on answers;
  - #200: live Claude and Codex readings from the agents themselves (in review; needs an independent review, and E1 to E14 against the build on a disposable VM).
- **Design:**
  - #20 and #84: sub-agents on the card. The design is [design/subagents.md](design/subagents.md), with §8 answered by the owner on 2026-11-01. The host and protocol for Copilot are S1 (#173); Claude and Codex are #174, HA and shadow #175, the clients #176. agent-ha-bridge keeps its card as it is (the latest writer's text): a known, intentional difference from the HA connector's main-agent-only card, labelled by the shadow;
  - #55: device approval with a matching code. Until then a device's kind is self-declared to the relay, which folder trust and remote update both rely on. Design decided by the owner on 2026-11-04 (all ten §11 decisions, option (a)), not yet built: [design/device-approval.md](design/device-approval.md);
  - #164 to #168: UI.

## 7. History

All merged PRs, by area. Each PR describes what it verified.
- **Spikes:**
  - #1 to #8.
- **Protocol and transport:**
  - #9, #10, #13, #16, #21.
- **Read-only core and serving:**
  - #11, #12, #14, #15, #19.
- **Interaction:**
  - answers: #17, #18, #24, #29;
  - replies: #23, #71, #72;
  - push: #22;
  - stop: #25;
  - usage: #26, #92;
  - attachments and blobs: #27, #61, #97;
  - launch: #31, #58, #75, #99, #104;
  - transfer: #70, #98;
  - folder trust: #133, #135, #137;
  - remote update: #160.
- **Discovery and safety:**
  - #59, #62, #66 (discovery holds);
  - #67 (management refused from agent sessions);
  - #103, #141, #144, #155, #330.
- **Hooks and migration:**
  - #65, #68, #116, #117.
- **HA connector:**
  - #60, #69, #79, #85, #87, #88, #107, #112, #121, #131.
- **Shadow:**
  - #63, #73, #77, #82, #86, #90, #91, #94, #96, #105, #108, #136, #162.
- **Windows app:**
  - #32, #81, #83, #97, #98, #120, #137, #145.
- **PWA:**
  - #28, #80, #111, #148, #159.
- **Relay and infrastructure:**
  - #30, #35, #36, #138.
- **Releases:**
  - #142, #145.
- **Rehearsal:**
  - #64, #109, #113, #125, #139, #156.
- **Purple edge:**
  - #132.
- **CI:**
  - #33, #34, #89, #127, #130, #152.

agent-ha-bridge releases from this work:
- **v1.33.33:** questions answered during a burst of output clear from the dashboard.
- **v1.33.34:** code blocks in replies wrap and have a Copy button.

## 8. Operating notes

- **Install on a new machine.** Download the installer for its architecture from the newest
  [release](https://github.com/danswett/agent-bridge/releases): `AgentBridgeApp-win-Setup.exe`
  on an x64 PC, `AgentBridgeApp-win-arm64-Setup.exe` on Windows on Arm (DSWETT-ARM8; Settings,
  System, About, "System type" says which). They are all pre-releases, so `releases/latest`
  does not work. Run it, then sign in from the app. Each copy updates only from its own
  architecture's channel ([app-windows.md](app-windows.md#releases-and-updates-every-merge-to-main-is-a-pre-release)).
  A plain install is read-only beside agent-ha-bridge and registers no hooks.
- **View only** (e.g. HOME), run at the machine in a normal PowerShell window, not from
  an agent session, then restart the host from the app:
  ```
  $h="$env:LOCALAPPDATA\AgentBridgeApp\current\agent-bridge-host.exe"; & $h config set delivery.readOnly true; & $h config set push.enabled false; & $h config set homeAssistant.enabled false
  ```
- **Migrate** a machine ([migration.md](migration.md)); migrate refuses on the
  publication writer. Check first, then migrate:
  ```
  $h="$env:LOCALAPPDATA\AgentBridgeApp\current\agent-bridge-host.exe"; & $h migrate --dry-run
  ```
  ```
  $h="$env:LOCALAPPDATA\AgentBridgeApp\current\agent-bridge-host.exe"; & $h migrate
  ```
  Then turn the HA connector on. `ha token set` asks for a Home Assistant long-lived
  token. Restart the host from the app afterwards:
  ```
  $h="$env:LOCALAPPDATA\AgentBridgeApp\current\agent-bridge-host.exe"; & $h config set homeAssistant.enabled true; & $h ha token set; & $h ha status
  ```
- **Rollback:**
  ```
  $h="$env:LOCALAPPDATA\AgentBridgeApp\current\agent-bridge-host.exe"; & $h rollback
  ```
- **Commands for the owner** go as plain text one-liners. They are often read on a phone
  and copied with the PWA's Copy button. Encoded commands don't wrap, so avoid them.
- **Owner-only actions:** config changes, sign-in and `-Install` (the #67 guard refuses
  them from agent sessions); production relay deploys (a required reviewer); Azure
  changes (the owner's personal Visual Studio Enterprise subscription, never a work
  account or the trial subscription).
- **Merge rule enforcement (#452, #524): applied 2026-11-03.** Main's ruleset 26510567
  (`quality/ruleset-main.json`) is active since 21:55 PT, the classic branch protection was removed
  at 22:59 PT, and merged branches delete themselves. The merge-rule GitHub App (ID 5607979,
  `agent-bridge-merge-rule`; docs/merge-rule-app.md) was created on 2026-11-04 and posts
  `merge-rule` (#623); the ruleset accepts `merge-rule` only from that App. **Auto-merge has been
  on since 2026-11-04 00:50 PT:** sessions arm it on their ready PR, pinned to the head
  (AGENTS.md rule 4), and the coordinator merges by hand only a PR whose session didn't. Since
  auto-merge doesn't pause for a red main, `merge-rule` also
  requires main green: the latest finished push run of `ci.yml` on main passed. While it hasn't,
  nothing merges but a PR the merge coordinator has labelled `fixes-main`, which skips only that
  check.

## 9. Decision log (after design.md §10)

| Date (PT) | Decision |
|---|---|
| 10-29 | Parity first, then migration; the shadow gate is coverage-based, not a fixed week |
| 10-29 | The HA connector is full, not status-only, so a migrated machine stays drivable from HA |
| 10-30 | The rehearsal ran on Azure VMs (VM-W and VM-M), reached through a key-restricted jump host |
| 10-31 | Folder trust from a device: answered by a person only, off by default per machine, with allowed devices approved in the app's Settings |
| 10-31 | Every merge to main publishes a release-build pre-release; the app auto-updates when idle (on by default), and read-only machines update too |
| 10-31 | ULTRA is the first real machine migrated, supervised by the owner |
| 11-01 | Remote update: only a person's device presses it; it obeys the same idle rules; read-only hosts don't offer it |
| 11-01 | VM01 is left down |
| 11-01 | Design, status and handoff live in this repository (this page, design.md, AGENTS.md) |
| 11-01 | A new coordinator session took over (bridge session a6ed320ca83044c5, DEV-VM1) |
| 11-01 | UI track order: #164, #165, #167, #166, #168, after the design session signs off |
| 11-01 | UI design proposed (docs/design/ui.md): system text faces with one licensed monospace, status glyphs instead of coloured edges, panes by window width, the PWA following the Windows design. Awaiting sign-off |
| 11-01 | ui.md signed off by the owner as written ("build the new UI as designed"); #182 merged. A UI coordinator session runs track B |
| 11-01 | Monospace (#181): the fallback stack for now (Cascadia Mono, SF Mono, Roboto Mono); Berkeley Mono decided later |
| 11-01 | Monospace (#181), later the same day: Agent Bridge Mono, a custom OFL build of Iosevka in the repository. Berkeley Mono rejected, because its Commercial license limits embedding to apps without text entry, requires the font to be unextractable and bars public sharing |
| 11-01 | Track B order: U1→U2 and U3→U4 in parallel, then U5 and U6, then U7, then U8 after S1 |
| 11-01 | UI PRs no longer wait for overlapping backend PRs (#186); they merge when ready and the backend PR rebases (overall coordinator, 16:05) |
| 11-01 | Usage belongs to an account: hosts share readings with each other, any online host serves the freshest per account, and the app and PWA keep the last one (#172) |
| 11-01 | Sub-agents in HA (#175): agent-ha-bridge keeps its behaviour, its card showing the latest writer's text, a delegate's included. The HA connector shows the main agent's own and lists delegates in a `subagents` attribute; the difference is known and intentional (subagents.md §7, host-shadow.md) |
| 11-01 | Dev Box keep-awake (#47) is built now, since migrating DEV-VM1 (a Dev Box) disables agent-ha-bridge's keep-awake task. It runs inside the host on agent-ha-bridge's terms (`az` login, skip then delay, no-op beyond 24 h, at most every 30 min), and any host stands back while agent-ha-bridge's own keep-awake task is enabled, so the schedule never has two writers. Migrate follows that task, not its config value: an enabled task is carried as on, or migrate refuses. Unusable Dev Box agent settings and a stand-back on an unreadable scheduler alarm on every pass rather than pass for "not a Dev Box" |
| 11-01 | Shadow coverage comes from a scripted soak on the Azure rehearsal VMs (Copilot, Claude, Codex), not only from organic use; DEV-VM1 no longer waits for its own gate |
| 11-01 | Migrate machines now, while the soak continues as the comparator. Order: ARM8 (after a native Arm64 build, #222), DEV-VM1 (after keep-awake, #47), DASDESK, the Mac (P5), HOME last |
| 11-01 | Live Claude and Codex usage (#197/#200): read through the agents themselves, never with their tokens. The host's direct Claude token read is to be retired (Anthropic's consumer terms) once the `/usage` route works; until #200 ships it is still the default collector. The build waits for the disposable-VM checks, now done (#236, #228). The Linux-only Codex checks ran on a temporary Azure Linux VM, since deleted |
| 11-01 | E14a (a real Codex revoke) was simulated on the VM. ChatGPT has no per-device revoke, and the owner declined "Log out of all devices" |
| 11-02 | ARM8 migrated on Arm64. The HA connector's 30 s retry of a rejected token got its IP banned in HA. The owner cleared the ban and restarted HA (00:12), and every machine was back within 3 min. Fix tracked as #227, merged in #234 (released in 0.1.1040): a refused token gets a bounded number of tries across processes, then stops until `ha token set`. For any migration, reuse agent-ha-bridge's admin token rather than minting one |
| 11-02 | Agents installed after the bridge should be detected and offered "Set up", and Settings should show each agent's state (#231, builds on #54). The backend belongs to the overall coordinator, the UI to the UI coordinator |
| 11-02 | New layouts merge first and are checked on ULTRA afterwards (the owner's call); a broken build is reverted |
| 11-02 | Motion (ui.md §5.3, #232) signed off. Native transitions keep their platform timing. The progress ring is the only extra looping animation. Scrolling to a question never jumps the view while the person is reading. Everything honours reduced motion |
| 11-02 | A delegate's name is untrusted text. The clients clean it, cap its length, show "Main agent" as `Delegate "Main agent"`, keep a fixed Delegate label in the inspector, and name the delegate by id in the stop pre-fill (ui.md §6.3, §8; subagents.md §6) |
| 11-02 | Monospace width (#295): Agent Bridge Mono's 560 width looked condensed beside the system face, so it is rebuilt at a normal 600 (IoskeleyMono's own, close to Cascadia Mono and Consolas) and is the one face in the app, the PWA and the Apple clients (#260) |
| 11-02 | Attention (#253), signed off with option A and every recommended answer:<br>• Needs you is a filter of the rows, in their own order, starting off at each launch.<br>• A row says only its status.<br>• The question is drawn and answered only in the conversation. Approve once escapes characters not drawn where they run, and stays offered.<br>• Approving on a phone takes two taps.<br>• An approval's notification only opens its question; other questions keep their buttons.<br>• The macOS menu bar extra names the waiting sessions.<br>Spec in #302; reasoning in docs/design/proposals/253-attention.md |
| 11-02 | Live Claude and Codex usage (#200) comes from the agents themselves: Claude Code's saved reading, refreshed by `claude -p "/usage"`, and Codex's app-server, standalone or through its daemon. The host never opens either agent's credentials, and the direct Claude token read is retired, not kept as an option (owner, 11-01). Probes are never ended by the host, run one at a time at the idle pace beside agent-ha-bridge, and back off to 2 h. A reading that names no account stays a row of its own machine and is never shared between hosts |
| 11-03 | Type (#544, #557), every recommendation approved: **Slipgates Sans** (option B, a tuned Inter) is the text face on every platform, with hinted statics in one collection on Windows and the variable pair elsewhere; the monospace is **Slipgates Mono**, with Medium 500 and Vietnamese added; no client may synthesize a weight or style (ui.md §4.2); real italic on Windows; the reading measure about 8% wider (770 px). The interim Inter Tight wordmark (#558) moves to Slipgates Sans |
| 11-03 | Type width (#544 §11): Slipgates Sans was asked whether it reads condensed, and is tuned rather than replaced: the text cut (opsz 14) at every size on every platform, +2% tracking on the Title role and the wordmark only (`type.tracking`; body text untracked), and the wordmark in Slipgates Sans SemiBold |
| 11-03 | First-run setup and Settings (#384), option (a) in all 13 decisions: Working is the neutral ring with a check, with no success colour; readiness rows reuse the `done`, `idle`, `ended` and `failed` glyphs; one-line consent, security and cost lines are allowed in Settings and setup only; setup's stepper replaces Settings' pane with the rail kept; Overview is the health list; device approval of permission prompts stays On by default, and folder-trust answers from devices Off; Subfolders is per folder, Off, once the host supports it; test sessions close afterwards by default; Local only is a neutral chip; the PWA shows Settings read only; Install Node.js goes through the same consent card; Agency is its own agent sharing Copilot CLI's hooks and sign-in. Spec in ui.md §6.8–§6.10 (#532); reasoning and boards in docs/design/proposals/384-first-run.md |
| 11-04 | An agent installed after the bridge (#231), option (a): offered as Collaboration's discovery is (§6.7), with a grey dot on Settings › Agents and an Overview card, "*Agent* *version* is installed · Set it up to see its sessions in Slipgates", with Set up and Not now. Not now holds per agent and version. Never a toast, a push or a count, and never on a read-only host. Spec in ui.md §6.9 and §9 (#532) |
| 11-04 | Product priorities (#696), signed off by the owner. A Head of Product owns the order of work. **Now:** Trust (input integrity and approval safety; #662 raised to P0) and Daily-use reliability. **Next:** Migration, Setup, Rebrand, Ops basics. **Later:** Performance, Conversation UI. **Parked:** Polish and motion (accessibility bugs exempt), Apple/macOS, Federation (#519). The P0 PRs land first, and no new slice starts in a Later or Parked epic until #209, #212, #220 and #662 close. New features start Parked |
| 11-04 | Agent messaging on the relay, same account only, becomes its own goal (#728, Next, first), split from #519's cross-account federation, which stays Parked. It is gated on #55, and #55 moves up to sixth in Trust |
| 11-04 | Dogfood readiness (#747, Next, second; absorbs Setup): 5 beta testers on Windows and the phone PWA, an opt-in beta telemetry tier with a pseudonymous install id (an amendment to design.md §5.10, written in #732), feedback in a private repository, and #55 not required before the first tester. Entry gates are on #731 |
| 11-04 | Each Now and Next goal has an `epic` tracker with sub-issues; the Project gains **Stage**, **Rank** and **Shipped in** fields; the **Beta 1** milestone holds the first tester's gates; the epic dashboard is #755 (automated by #756) |
| 11-04 | Public surfaces live under slipgates.com: app, relay, the apex (/download, /docs, /privacy), status and roadmap (#762). The owner approved DNS and certificates for the three new hostnames |
| 11-04 | Sessions are restored after a host crash or machine reboot, automatically and left idle, every session included; one that died mid-turn gets a Continue button and nothing is typed for it (#780) |
| 11-04 | An agent that needs sign-in is P0 (#800): shown as its own status, escalated, and recovered without RDP: a silent resume first, then sign-in from the phone by the agent's own device code with the URL allowlisted |
| 11-04 | **No-RDP tenet** (above): #696 rule 5, the `needs-rdp` label, and #801–#805 filed |
| 11-04 | Times: the product UI shows the viewer's local time, adding the zone where another machine's time could be misread; "Pacific" applies to repository text (AGENTS.md) |
| 11-04 | The P0 feature freeze ends at 23:08 PT: #212, #209, #662 and #220 are closed, and Migration starts ([#696](https://github.com/danswett/agent-bridge/issues/696#issuecomment-6651880884)) |
| 11-04 | Phone session header (#620), signed off with every recommended option, then revised by the owner on 11-05:<br>• At rest (11-05): the title at most two lines, one meta line, and the tabs on their own row, 152 pt on a 390 × 844 phone. There is no delegate strip on a phone: the Delegates tab carries the running count and the `needs` glyph, and questions stay in the conversation.<br>• Scrolling down slides it up under a 48 pt bar (Back, the glyph, the title on one line, More), following the finger near the top.<br>• Mid-conversation, 64 pt up brings it back and 32 pt down sends it away.<br>• The bar keeps the `needs` glyph while a session waits on you.<br>• The reply area (11-05, #935): on a phone the box starts at one line and grows to six; while a question waits, a one-line notice links to it; while reading, it compacts to a 48 pt bar on the header's 32/64 counts, the end shows it in full, and it never expands over the focused control.<br>• From 760 and in the Windows app, nothing changes.<br>Spec in ui.md §6.2 and the `collapse` motion (#629); built by #631; reasoning in docs/design/proposals/620-compact-header.md |
| 11-04 | Device approval (#55, #391): every decision in design/device-approval.md §11 is option (a): approved person's devices sign the roster under seniority, probation and confirmation; hosts approved by mutual codes; only presence-proven devices approve or act as a person; an unapproved device sees only "pending approval"; reset with no recovery key; the devices in use now enroll with mutual codes; one presence-proven Remove for a machine; `app`, `pwa` and `phone` are a person's kinds; freshness with `selfAttest` off |
| 11-05 | Remote Other on Copilot forms matches the terminal (#876). #510 may ship a time-limited interim without Other on Copilot lists, with the reason shown, until #908 lands. Schema-less and legacy-choice questions: the card says why now, remote support follows (#887) |
| 11-05 | Lab recordings (#736) sign in with dedicated lab-only accounts per agent, kept in the lab's Key Vault and provisioned by the owner |
| 11-05 | Agent messaging (#912): a standing coordination grant (an owner-approved daily group whose members message each other unattended) replaces hourly G windows for coordinators, subject to a security review; delegate isolation (Q47) accepted for now, revisited after three `delegate-tools` cases; 17 other gating recommendations accepted |
| 11-05 | Notifications (#913): Needs you interrupts, Done pushes only for Watched sessions and silently, FYI never; delegates roll up; one notification per session; only the active device alerts; one reminder at 15 min; a three-way Default/Watch/Mute setting; an Activity bell in the header; the badge counts Needs you only |
| 11-05 | **Every element has a purpose** (owner, a #696 tenet): nothing is drawn twice on one screen; if two elements show the same fact, one goes. Phones lose the delegate strip once the Delegates tab carries the count and the needs glyph (#620, #661) |
| 11-05 | Who decides what: Product owns the ranked epics (Now, Next, Later), the exceptions to any freeze, and product and design holds (with Creative). Engineering and Quality own the order PRs land in, speed and safety; they take PRs advancing the top-ranked Now item first (#689) |
| 11-05 | Order changes reach Engineering as one **ORDER CHANGE** notice on #696 (rule 7): old and new position, why, and **Next grant** (the next free slot) or **Preempt** (pause the lowest-ranked work in flight, even under a work-in-progress freeze). Engineering answers within 15 minutes, and the dashboard (#755) flags drift |
