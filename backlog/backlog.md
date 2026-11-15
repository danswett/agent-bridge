# Product backlog (epic order)

Issue numbers marked fleet are in danswett/slipgates-fleet; all others are in danswett/agent-bridge.

This file is the single ordered backlog for Slipgates. Product owns it and changes it only through a docs-only pull request. Fleet registers, handovers and dispatch link here and do not copy the list. Until this file merges, agent-bridge issue #696 holds the same order.

Last changed: 2026-11-15 11:30 PT.

Rules:
- Rank is unique and contiguous, starting at 1. Lower rank goes first.
- Each row names one epic, the outcome, the next slice or dependency, and the owner (the GitHub assignee, or unassigned).
- Detail, slice lists and discussion live in the linked issue, not here.
- A ready label goes only on a slice that is unowned and unblocked. Within a rank, the lowest slice number goes first unless a predecessor says otherwise.
- Nothing here depends on a particular machine. Product rejects any slice that does.
- A change to a rank, outcome, next step or owner updates "Last changed" in the same pull request.

| Rank | Epic | Issue | Outcome | Next slice or dependency | Owner |
|---|---|---|---|---|---|
| 1 | Merge pipeline health | [#2143](https://github.com/danswett/agent-bridge/issues/2143), [#2130](https://github.com/danswett/agent-bridge/issues/2130), [#2094](https://github.com/danswett/agent-bridge/issues/2094) | Main stays green, Actions queue under 25, PWA Pages deploy works | Main green first; Quality sets the order | unassigned |
| 2 | Coordinator keeper and stuck-prompt guard | [fleet #341](https://github.com/danswett/slipgates-fleet/issues/341), [fleet #400](https://github.com/danswett/slipgates-fleet/issues/400) | Coordinators are replaced cleanly and stuck prompts are caught | fleet #341 S1 to S3 and #400 G1, G2 (need agent-bridge #2135); the rest stay behind | unassigned |
| 3 | GitHub quota and security | [fleet #383](https://github.com/danswett/slipgates-fleet/issues/383), [fleet #406](https://github.com/danswett/slipgates-fleet/issues/406) | No quota can refuse the fleet; keys are kept clean | Fleet #384, #385, broker #408, then #386 to #389 | unassigned |
| 4 | Slipgates updates | [#2225](https://github.com/danswett/agent-bridge/issues/2225) | Fast detection, one-press Update and Update all from the Fleet page, deterministic progress | Engineering design, then Product slices it | unassigned |
| 5 | Builders always full and F7 | [fleet #394](https://github.com/danswett/slipgates-fleet/issues/394), [#2144](https://github.com/danswett/agent-bridge/issues/2144) | Idle builders get work; overwritten answers fixed | Fleet placement #394 to #397, #433, #255, #381, #413; agent-bridge #2145 then #2146 | unassigned |
| 6 | App efficiency | [#2293](https://github.com/danswett/agent-bridge/issues/2293) | Tiny memory and network use, minimal idle CPU, for native, host and PWA | #2283 and #2284 first, then the design | unassigned |
| 7 | HA retirement and load parity proof | [#1396](https://github.com/danswett/agent-bridge/issues/1396), [fleet #314](https://github.com/danswett/slipgates-fleet/issues/314) | Home Assistant path retired with proven parity | #1776 and #2151 before #2243 | unassigned |
| 8 | Owner UI | [#2068](https://github.com/danswett/agent-bridge/issues/2068), [#2037](https://github.com/danswett/agent-bridge/issues/2037) | iPad Fleet grid, reply box and fleet figures | Then #1641, then #2339 | unassigned |
| 9 | Fleet page on Windows | [#1868](https://github.com/danswett/agent-bridge/issues/1868) | Windows Fleet view with host figures | #2609, #2610, #2611, #2604, #2605 in order, after spare capacity | unassigned |
| 10 | status.slipgates.com | [#2136](https://github.com/danswett/agent-bridge/issues/2136) | Public status page with the closed incident labels | #2356, #2138, #2139, then #2140 and #2141 | unassigned |
| 11 | P2 chains | [#521](https://github.com/danswett/agent-bridge/issues/521), [#2072](https://github.com/danswett/agent-bridge/issues/2072), [#232](https://github.com/danswett/agent-bridge/issues/232) | Unread, groups and motion | After the ranks above | unassigned |
| 12 | Merge-rate friction removal | [#1248](https://github.com/danswett/agent-bridge/issues/1248) | 12 merges an hour | #1837, then #1755, #1250, #1252 to #1255; Engineering cuts and marks ready | unassigned |
| 13 | Install and first-run setup UI | [#384](https://github.com/danswett/agent-bridge/issues/384) | A guided install and first-run view that replaces the setup script | Planning only; R8 stays the way to bring a node online until then | unassigned |
