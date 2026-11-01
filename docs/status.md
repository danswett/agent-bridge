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
| Releases | Every merge to `main` publishes a release-build pre-release of the Windows app (`v0.1.<run>`, Velopack, with deltas). Latest: v0.1.690 or later. Installed apps check every 30 min and install when idle |
| Rehearsal (#39) | Done: four runs on a Hyper-V VM and Azure VMs. Verdict: **ready for a supervised migration of one real machine** ([rehearsal-2026-10.md](rehearsal-2026-10.md)) |

### Machines

| Machine | State |
|---|---|
| DSWETT-ULTRA | **Migrated** from agent-ha-bridge on 2026-10-31. Auto-updating, on 0.1.690 as of 2026-11-01. HA connector on since 2026-11-01 08:48 (online in HA). The first real machine; watch it |
| DSWETT-DEV-VM1 | agent-ha-bridge plus a **read-only shadow host** (profile `shadow`) comparing with the daemon (#40). Gate not yet passed (§3) |
| DSWETT-HOME | agent-ha-bridge, and the **dashboard publication writer**: migrate refuses there (G7), so it moves last. A view-only app install (`delivery.readOnly`, push off, HA connector off), so it shows in the PWA, is pending with the owner |
| ARM laptop, Mac, DASDESK | agent-ha-bridge only. The Mac waits for P5 (macOS host) |
| Hyperloop VM01 (on DEV-VM1) | Down since a reboot on 2026-10-31: its autologon failed and no one knows its generated password. Left down by the owner's decision; not needed |
| Azure `rehearsal-w`, `rehearsal-m` | Deallocated (resource group `rg-agent-bridge-rehearsal`), with OS-disk snapshots. `build/rehearsal-azure-vmw.ps1 -Start` / `-Deallocate`. Delete the group when no more rehearsals are needed |

### Running work

Check `gh pr list -R danswett/agent-bridge` for open PRs. On 2026-11-01 there are none.

## 3. Shadow gate (DEV-VM1, #40)

The read-only host has run beside the daemon since 2026-10-29, and the gate is coverage-based
(design §10, Stage 4). Check it on DEV-VM1:

```
%LOCALAPPDATA%\AgentBridgeShadow\bin\agent-bridge-host.exe shadow summary --gate --profile shadow
```

- **2026-11-01:** FAIL, from 43 `background.shells` episodes in one session before 09:57 PST. These are agent-ha-bridge#276, which the daemon gets wrong: a shell restarted under the same id is lost. #162 now labels them as a known difference. The FAIL clears when they leave the 24 h window (about 10:00 PST on 11-02). Before that the verdict was **NOT YET**, on coverage: questions 38 of 100, sessions 8 of 30, status transitions 358 of 500. Claude and Codex have no sessions there yet.
- Update the shadow build with `build/shadow-week.ps1 -Update` after relevant merges. Agent sessions can't use `-Install`; the #67 guard refuses it.
- #163: a skipped sample splits one disagreement into many episodes. Not fixed.

## 4. Next steps, in order

1. **Watch ULTRA.** Its HA entities (`*_dswett_ultra_*`) and the PWA should agree, and answers, replies and launches from the phone and the HA card should all work. Try **Install** from the PWA once a newer release is out: remote install has never run on a real machine (#161).
2. **HOME view-only install**, by the owner (the commands are in §8). Then confirm HOME shows in the PWA, read-only.
3. **The shadow gate on DEV-VM1:** keep collecting until it passes, then cut DEV-VM1 over with `migrate` and soak it. Claude and Codex coverage is needed.
4. **The rest, one machine at a time** (design §10 machine order): the ARM laptop, the Dev Box, then the Mac after P5. Each runs on 0.1.649 or later. Repeat rehearsal step E6-07 (worktree reclaim) on the first one.
5. **HOME last** (P7): move the publication-writer role, migrate, then retire the HA dashboard as an explicit operator action.
6. **UI design track** (§5).
7. **Later:** code signing, macOS (P5), native phone apps (P8), relay scale (S9), observability (§5.10), and decommissioning the old relay stamp in the trial subscription (awaiting the owner's approval).

## 5. UI design track

The owner is running a separate design session for the apps' look and layout. Its
proposals arrive as issues:

| Issue | Proposal |
|---|---|
| #164 | Session list: keep the person's order, not most-recently-updated |
| #165 | "Needs you": questions and approvals surface above the list without moving it |
| #166 | Layout: list and detail panes that adapt to the window, with a native shell per platform |
| #167 | Design language: status glyphs, icons, colour roles and the app icon, shared by every client |
| #168 | PWA: a small platform layer (shortcut hints, safe areas, back navigation), not a reskin |

None is started. Agree on the order with the owner before building. Changes to the
layout touch both the PWA (`pwa/`) and the app (`src/AgentBridge.App`) and should keep
them at parity.

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
  - #95: bound the resumable sessions' folder checks.
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
  - #20 and #84: sub-agents on the card. Write the design doc with the owner before any code;
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
