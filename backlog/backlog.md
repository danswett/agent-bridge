# Product backlog (epic order)

Issue numbers marked fleet are in danswett/slipgates-fleet; all others are in danswett/agent-bridge.

This file is the single ordered backlog for Slipgates. Product owns it and changes it only through a docs-only pull request. Fleet registers, handovers and dispatch link here and do not copy the list. Until this file merges, agent-bridge issue #696 holds the same order.

Last changed: 2026-11-21 (design audit: closed issues in next steps marked closed; no rank, outcome or owner changed). Order last set 2026-11-17 13:15 PT (owner order: Windows transcript and win-arm64 OWNER-P0 first, then F7 and HA retirement, then guards).

Rules:
- Rank is unique and contiguous, starting at 1. Lower rank goes first.
- Each row names one epic, the outcome, the next slice or dependency, and the owner (the GitHub assignee, or unassigned).
- Detail, slice lists and discussion live in the linked issue, not here.
- A ready label goes only on a slice that is unowned and unblocked. Within a rank, the lowest slice number goes first unless a predecessor says otherwise.
- Nothing here depends on a particular machine. Product rejects any slice that does.
- A change to a rank, outcome, next step or owner updates "Last changed" in the same pull request.
- Only open issues are listed as live work. A closed issue named in a next step is marked closed and is not work.

| Rank | Epic | Issue | Outcome | Next slice or dependency | Owner |
|---|---|---|---|---|---|
| 1 | OWNER-P0: Windows transcript render/scroll | [#1739](https://github.com/danswett/agent-bridge/issues/1739), [#1756](https://github.com/danswett/agent-bridge/issues/1756), [#1759](https://github.com/danswett/agent-bridge/issues/1759), [#2738](https://github.com/danswett/agent-bridge/issues/2738), [#2726](https://github.com/danswett/agent-bridge/issues/2726) | Windows transcript renders and scrolls correctly | Owner priority P0; Product slices the issues in the order listed; closed stale drafts #2209 and #2196 (historical, not active work) | unassigned |
| 2 | OWNER-P0: win-arm64 wheel/self-test + motion | [#2508](https://github.com/danswett/agent-bridge/issues/2508), [#2483](https://github.com/danswett/agent-bridge/issues/2483), [#2701](https://github.com/danswett/agent-bridge/issues/2701) | win-arm64 wheel and self-test work, and motion is fixed | Owner priority P0; Product slices the issues in the order listed | unassigned |
| 3 | F7 dispatcher live placement | [fleet #395](https://github.com/danswett/slipgates-fleet/issues/395), [#2144](https://github.com/danswett/agent-bridge/issues/2144) | Idle builders get work; overwritten answers fixed | F7 order, all in fleet: fleet #347, then fleet #714, fleet #715, fleet #716 and fleet #717, then fleet #694, then fleet #395 (live placement), then fleet #396 and fleet #397; agent-bridge #2146 (agent-bridge #2145, fleet #255, fleet #394, fleet #381, fleet #413 and fleet #433 are closed) | unassigned |
| 4 | HA retirement and load parity proof | [#1396](https://github.com/danswett/agent-bridge/issues/1396), [fleet #314](https://github.com/danswett/slipgates-fleet/issues/314) | Home Assistant path retired with proven parity | gate counters #2521, #2522 and #2523 are closed; #2528 and fleet #725 (the HA Retirement Lead picks which starts first), then S4 live runs #2389 to #2395 and #2644; #1776 before #2243 (#2151 is closed) | unassigned |
| 5 | Coordinator keeper and stuck-prompt guard | [fleet #341](https://github.com/danswett/slipgates-fleet/issues/341), [fleet #400](https://github.com/danswett/slipgates-fleet/issues/400) | Coordinators are replaced cleanly and stuck prompts are caught | fleet #341 S1 to S3, fleet #400 G1 (fleet #401) and G2 (fleet #402), which need agent-bridge #2135; the rest stay behind | unassigned |
| 6 | Merge pipeline health | [#2130](https://github.com/danswett/agent-bridge/issues/2130), [#2094](https://github.com/danswett/agent-bridge/issues/2094) | Main stays green, Actions queue under 25 | Main green first; Quality sets the order (#2094 is closed). Then [#2677](https://github.com/danswett/agent-bridge/issues/2677): the merge rule must never count Copilot as the second family (technical owner Engineering Lead; not ready, no launch) | unassigned |
| 7 | GitHub quota and security | [fleet #383](https://github.com/danswett/slipgates-fleet/issues/383), [fleet #406](https://github.com/danswett/slipgates-fleet/issues/406) | No quota can refuse the fleet; keys are kept clean | fleet #384 (quota guard module, not agent-bridge #384), then fleet #386 to #389; fleet #385 and fleet #408 are closed | unassigned |
| 8 | Slipgates updates | [#2225](https://github.com/danswett/agent-bridge/issues/2225) | Fast detection, one-press Update and Update all from the Fleet page, deterministic progress | Engineering design, then Product slices it | unassigned |
| 9 | App efficiency | [#2293](https://github.com/danswett/agent-bridge/issues/2293) | Tiny memory and network use, minimal idle CPU, for native, host and PWA | #2284 first (#2283 is closed), then the design | unassigned |
| 10 | Owner UI | [#2037](https://github.com/danswett/agent-bridge/issues/2037) | iPhone reply box fixed and fleet figures (#2068 is closed) | Then #1641 (#2339 is closed) | unassigned |
| 11 | status.slipgates.com | [#2136](https://github.com/danswett/agent-bridge/issues/2136) | Public status page with the closed incident labels | #2138, #2139 (#2356 is closed), then #2140 and #2141 | unassigned |
| 12 | P2 chains | [#521](https://github.com/danswett/agent-bridge/issues/521), [#2072](https://github.com/danswett/agent-bridge/issues/2072), [#232](https://github.com/danswett/agent-bridge/issues/232), [#1868](https://github.com/danswett/agent-bridge/issues/1868) | Unread, groups, motion and the Windows Fleet grid | After the ranks above; the #1868 chain is #2610 (#2609 is closed), #2611, #2604, #2605 in order, after spare capacity | unassigned |
| 13 | Merge-rate friction removal | [#1248](https://github.com/danswett/agent-bridge/issues/1248) | 12 merges an hour | #1837, then #1755, #1250, #1252 to #1255; Engineering cuts and marks ready | unassigned |
| 14 | Install and first-run setup UI | [#384](https://github.com/danswett/agent-bridge/issues/384) (agent-bridge, not fleet #384) | A guided install and first-run view that replaces the setup script | Planning only; R8 stays the way to bring a node online until then | unassigned |
