# Status, plan and handoff

**Start here** if you are picking this project up with no prior context, whether you are a
person or an agent. This page says where the project is, what runs where, what comes
next, and how the work is done. Keep it current: update it in the same PR as any change
that moves the project along (a merge that closes a stage, a machine migrated, a
decision made).

*Last updated: 2026-11-01, Pacific.*

| Read next | For |
|---|---|
| [design.md](design.md) | The master plan and architecture: goals, protocol, security, relay, host, apps, roadmap (§10), risks |
| [parity.md](parity.md) | Every agent-ha-bridge feature mapped to this product, with evidence or an issue |
| [parity-checklist.md](parity-checklist.md) | The scripted migrate-and-rollback rehearsal |
| [rehearsal-2026-10.md](rehearsal-2026-10.md) | The rehearsal's final report and verdict |
| [migration.md](migration.md) | `migrate` and `rollback`: what moves and what doesn't |
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

## 2. Where things are (2026-11-01)

### Built and merged

The work has been built phone-first. All merged PRs are listed in §7.

| Area | State |
|---|---|
| Spikes S1–S6 | Done. S7 (macOS), S8 (corporate policy, parked) and S9 (relay at scale) not started |
| Protocol v1 | sessions, answer, reply, attachments, stop, usage, launch, transfer, blobs, roster, update, folder trust. Specs in `docs/protocol/` |
| Host (`agent-bridge-host`) | Read-only core, delivery (answers, replies, attachments, stop, launch, resume), worktree reclaim, hook intake, discovery holds, management refused from agent sessions, migrate and rollback, shadow comparison |
| Windows app | Local and remote machines, Launch page, transfers, toasts, folder trust, Settings, self-update (installs when the host is idle), remote update |
| PWA | Cards, answers, replies, attachments, launch, stop, usage, push, All machines view, Machines fold, folder trust, remote update, Copy button on code blocks |
| Relay | Production at https://relay.bad-duck.com, image `7c17d4a` (2026-11-01), with a roster store. Staging, then production on approval, via `.github/workflows/relay.yml` |
| HA connector | Stages 1–7, so a migrated machine stays on the existing HA dashboard: status, answers, replies, stop, launch, phone notifications, update buttons, upkeep and forget. Contract pinned to agent-ha-bridge v1.33.33 |
| Releases | Every merge to `main` publishes a release-build pre-release of the Windows app (`v0.1.<run>`, Velopack, with deltas). Latest: v0.1.700 or later. Installed apps check every 30 min and install when idle |
| Rehearsal (#39) | Done: four runs on a Hyper-V VM and Azure VMs. Verdict: **ready for a supervised migration of one real machine** ([rehearsal-2026-10.md](rehearsal-2026-10.md)) |

### Machines

| Machine | State |
|---|---|
| DSWETT-ULTRA | **Migrated** from agent-ha-bridge on 2026-10-31. HA connector on since 2026-11-01 08:48 (online in HA). Updated to 0.1.700 on 2026-11-01 11:06 by **Install from the PWA**, the first remote install on a real machine (#161). The first real machine; watch it |
| DSWETT-DEV-VM1 | agent-ha-bridge plus a **read-only shadow host** (profile `shadow`) comparing with the daemon (#40). Gate not yet passed (§3) |
| DSWETT-HOME | agent-ha-bridge, and the **dashboard publication writer**: migrate refuses there (G7), so it moves last. Since 2026-11-01 13:39 also a **view-only app install** (0.1.730 or later; `delivery.readOnly`, push off, HA connector off). The PWA lists it online and read-only, and its Claude and Codex usage rows are back. agent-ha-bridge there is unaffected |
| ARM laptop, Mac, DASDESK | agent-ha-bridge only. The Mac waits for P5 (macOS host) |
| Hyperloop VM01 (on DEV-VM1) | Down since a reboot on 2026-10-31: its autologon failed and no one knows its generated password. Left down by the owner's decision; not needed |
| Azure `rehearsal-w`, `rehearsal-m` | Deallocated (resource group `rg-agent-bridge-rehearsal`), with OS-disk snapshots. `build/rehearsal-azure-vmw.ps1 -Start` / `-Deallocate`. Delete the group when no more rehearsals are needed |

### Running work

Check `gh pr list -R danswett/agent-bridge` for open PRs. Two coordinators work at once (decided 2026-11-01):
- **Overall coordinator** (bridge session a6ed320ca83044c5, DEV-VM1): the whole project, track A of the UI plan (sub-agents S1 #173, S2 #174, S5 #175), #172 (shared usage), the relay, migration, the shadow gate, machines, and this page outside §5.
- **UI coordinator** (bridge session f7ba0bcc0adb4319, DEV-VM1): track B (§5). It merges its own PRs under the same rule.
- Cross-track needs go on #170.

In flight on 2026-11-01: #187 (S1) and #172 (shared usage), each with a delegate. #172 is PR #186,
in review: each host asks the account's other hosts for their usage readings and serves them
(`usage.shared`), and the app and PWA keep the last reading of each allowance, so usage read on a
machine that is now offline still shows. Security-sensitive: a new host-initiated channel, read-only.

- Sub-agents S1 (#173): the host's sub-agent tree from Copilot transcripts and its additive
  protocol (`subagents`, `activity.source` and `own`, `waitingOn`, Question `agent`,
  `session.get { agent }`), in review on branch `feat/subagents-s1`.

## 3. Shadow gate (DEV-VM1, #40)

The read-only host has run beside the daemon since 2026-10-29, and the gate is coverage-based
(design §10, Stage 4). Check it on DEV-VM1:

```
%LOCALAPPDATA%\AgentBridgeShadow\bin\agent-bridge-host.exe shadow summary --gate --profile shadow
```

- **2026-11-01:** FAIL, from 43 `background.shells` episodes in one session before 09:57 PST. These are agent-ha-bridge#276, which the daemon gets wrong: a shell restarted under the same id is lost. #162 now labels them as a known difference (the shadow build is at 350bce6, which has it). The FAIL clears when they leave the 24 h window (about 10:00 PST on 11-02). Before that the verdict was **NOT YET**, on coverage; at 11:00 PST on 11-01 it read questions 40 of 100, sessions 10 of 30, status transitions 408 of 500. Claude and Codex have no sessions there yet.
- Update the shadow build with `build/shadow-week.ps1 -Update` after relevant merges. Agent sessions can't use `-Install`; the #67 guard refuses it.
- #163: a skipped sample splits one disagreement into many episodes. Not fixed.

## 4. Next steps, in order

1. **Watch ULTRA.** Its HA entities (`*_dswett_ultra_*`) and the PWA should agree, and answers, replies and launches from the phone and the HA card should all work. Install from the PWA worked on 2026-11-01 (0.1.690 to 0.1.700); Install from the HA card's button is still untried live (#161).
2. ~~**HOME view-only install**~~ Done 2026-11-01: the PWA lists HOME online and read-only.
3. **The shadow gate on DEV-VM1:** keep collecting until it passes, then cut DEV-VM1 over with `migrate` and soak it. Claude and Codex coverage is needed.
4. **The rest, one machine at a time** (design §10 machine order): the ARM laptop, the Dev Box, then the Mac after P5. Each runs on 0.1.649 or later. Repeat rehearsal step E6-07 (worktree reclaim) on the first one.
5. **HOME last** (P7): move the publication-writer role, migrate, then retire the HA dashboard as an explicit operator action.
6. **UI design track** (§5).
7. **Later:** code signing, macOS (P5), native phone apps (P8), relay scale (S9), observability (§5.10), and decommissioning the old relay stamp in the trial subscription (awaiting the owner's approval).

## 5. UI design track

The design session's output is in the repository (#182). The owner **signed off ui.md on
2026-11-01**; subagents.md's sign-off and its §8 questions sit with the overall coordinator
(track A). A UI coordinator session runs track B and merges its PRs; it keeps this section
current. The sources:

- [design/ui.md](design/ui.md): the spec for every client (status language, colour, type,
  layout, surfaces, platform shells);
- [design/subagents.md](design/subagents.md): the sub-agent model and protocol (#84);
- [design/ui-plan.md](design/ui-plan.md): the slices, their order and how to do one;
- [/design](../design/README.md): `tokens.json`, the status glyphs, the icons and the
  Agent Bridge Mono font;
- [design/mockups](design/mockups/README.md): every design-canvas board rendered as an
  image, for reference only (ui.md stays the spec). The source is in `/design/canvas` and
  the renderer in `tools/design-mockups`.

| Slice | Issue | What | State (2026-11-01, 18:30 PT) |
|---|---|---|---|
| S1 | #173 | Sub-agents: host model and protocol, Copilot first. May start before sign-off | Merged (#187) |
| S2 | #174 | Sub-agents: Claude and Codex sources | Track A |
| S5 | #175 | Sub-agents: HA connector and shadow | Track A (#199) |
| U1 | #164 | Session list keeps the person's order | **Done**: #190 (PWA), #194 (app) |
| U2 | #165 | "Needs you" lane | In review: #202 (PWA), #203 (app) |
| U3 | #177 | Design tokens generator; both clients switch to the colour roles | **Done**: #188 (generator, PWA), #193 (app) |
| U4 | #178 | Status glyphs and icons replace card edges and glows | PWA done (#204); app in review (#205) |
| U5 | #179 | PWA layout (half of #166) | Next, after U2 |
| U6 | #180 | Windows app layout (half of #166) | Next, after U2 and U4 |
| U7 | #168 | PWA platform layer | After U5 |
| U8 | #176 | Delegates in both clients | After U5 and U6 (S1 has merged) |
| | #181 | Monospace: **Agent Bridge Mono** (our OFL build of Iosevka, `design/fonts/agent-bridge-mono`), decided 2026-11-01 | Bundled in both clients (U3) |
| | #183 | Light-theme `needs` glyph and `text3` fail contrast | Fixed in #188 |

Track B runs as delegates, in this order:
1. U1 then U2, and U3 then U4, as two chains in parallel;
2. then U5 and U6, in parallel;
3. then U7;
4. U8, now that S1 has merged.

UI PRs merge as soon as each meets the merge rule. A backend PR that overlaps them, such as #186, rebases onto them (decided 2026-11-01, 16:05).

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
  - #47: Dev Box keep-awake;
  - #48: macOS and Linux tmux delivery;
  - #52: usage settings;
  - #53: card wording;
  - #54: hook registration from the app;
  - #56: cancel a question from another device;
  - #78: launched sessions get the agent HA token;
  - #93: migrate carries `usage.*`;
  - #95: bound the resumable sessions' folder checks;
  - #172: usage per account, shared between hosts (in review).
- **Bugs and follow-ups from live use:**
  - #101: app test timing races;
  - #102 and #150: launch busy wording;
  - #106: the logon task doesn't restart a crashed host;
  - #110: an HA connector edge case;
  - #118: discovery held 2 min after an npm Copilot start;
  - #122: the Launch page's busy banner;
  - #123: Codex's cached plugin on a migrated machine;
  - #146: no reason given for a missing Other;
  - #147: a host restart under a held discovery drops a Codex session;
  - #157 and #158: folder-trust follow-ups;
  - #161: remote-update follow-ups;
  - #163: shadow episodes.
- **Beyond parity:**
  - #76: HA connector;
  - #128: Forget;
  - #134: driver edge on answers.
- **Design:**
  - #20 and #84: sub-agents on the card. The design is [design/subagents.md](design/subagents.md), with §8 answered by the owner on 2026-11-01. The host and protocol for Copilot are S1 (#173); Claude and Codex are #174, HA and shadow #175, the clients #176. agent-ha-bridge keeps its card as it is (the latest writer's text): a known, intentional difference from the HA connector's main-agent-only card, labelled by the shadow;
  - #55: device approval with a matching code. Until then a device's kind is self-declared to the relay, which folder trust and remote update both rely on;
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
  - #103, #141, #144, #155.
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

- **Install on a new machine.** Download `AgentBridgeApp-win-Setup.exe` from the newest
  [release](https://github.com/danswett/agent-bridge/releases); they are all
  pre-releases, so `releases/latest` does not work. Run it, then sign in from the app.
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
