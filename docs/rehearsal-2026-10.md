# agent-bridge #39: parity rehearsal, final report

*Archived here from the coordinator's notes on 2026-11-01. Current state: [status.md](status.md).*

31 October 2026 (Pacific). This covers run 2 on VM01 and Phase G on the Azure VM-M (runs g1 and g2), with run 1 for comparison. The checklist is `docs/parity-checklist.md`. Corrections from these runs are in PR #156.

## Where each run happened

| Run | VM-M | Install under test | Result (steps recorded) |
|---|---|---|---|
| 1 | VM01 (Hyper-V), as adminuser, no snapshots | p5 dev builds | 72 pass, 9 fail, 4 manual (85) |
| 2 | VM01, as standard user `rehearsal`, with a second user (adminuser) running agents | 0.1.583, then 0.1.595 through the app's own updater | See below; recorded on VM01 (`C:\rehearsal\backup-vm01\evidence-run2.zip`) |
| g1 | Azure `rehearsal-m`, single standard user, from snapshot S1 | 0.1.611 | 55 pass, 5 fail, 1 manual (61) |
| g2 | Same VM, after restoring S1 (G-01) | 0.1.611, reinstalled | 42 pass, 3 fail, 1 manual (46) |

In every run, VM-W (Azure) held the disposable Home Assistant and the PWA, and agent-ha-bridge 1.33.33 was the old install. Its owner signed in each run's agents (Copilot CLI 1.0.105, Claude Code 2.1.316, Codex 0.166.0) and the app's GitHub sign-in.

## Migrate and rollback (Phases B, C, D and F)

These are the steps that decide whether a machine can be moved over and back.

| Check | Run 2 (VM01) | g1 | g2 |
|---|---|---|---|
| B-08: the snapshot restores byte for byte | n/a (no snapshot) | pass | pass |
| C-01 to C-03: the app installs read-only; five minutes beside a live session change nothing | pass | pass | pass |
| D-01: refused on the publication writer (G7) | pass (VM-W) | covered | covered |
| D-02: `migrate --dry-run` lists everything and changes nothing | pass | pass | pass |
| D-03: daemon stopped, task disabled (not deleted) | pass | pass | pass |
| D-04: only hooks, agent config and the task changed | pass | pass | pass |
| D-05: workspaces, profiles and launch settings carried over | pass | pass | pass |
| D-06: host delivers with every capability | pass | pass | pass |
| D-07: HA connector on (the rehearsal's disposable HA), same ids, no `_2` | pass | pass | pass |
| F-01: `rollback` exits 0, old daemon running | pass | pass | pass |
| F-02/F-02b: install, hooks, agent config and task identical to the baseline | agents' own writes only (F-02c pass) | same | same |
| F-03: host back to read-only | pass | pass | pass |
| F-04 to F-07: old bridge adopts the live session, answers, replies and ends it from HA | pass | pass | pass (F-05 on the second try, see below) |
| F-08: old hooks spool to its daemon again | pass | pass | pass |
| F-09: agent-ha-bridge's own worktree untouched | pass | pass | pass |

What F-02 found in every run was only the agents' own writes:
- Codex adds `[hooks.state]` to `~/.codex/config.toml` when its changed hooks are trusted.
- Claude adds `theme` (and `skipDangerousModePermissionPrompt` after its Bypass confirmation) to `~/.claude/settings.json`.

With those keys removed, `settings.json` matches the baseline hash exactly (88f64774, 1901 bytes, in g1 and g2). The rollback put back only its own re-pointed hook commands and left Claude's later changes in place, as designed. Everything else matched byte for byte.

**F-05 in g2.** The first P1, typed 28 s after F-01 restarted agent-ha-bridge's daemon, found no fresh heartbeat. The hook gave the fixed safe reply and the question stayed at the terminal, which is agent-ha-bridge's own hook contract. A repeat a minute later passed. The checklist now says to wait for a fresh heartbeat.

## Run 2 (VM01, two users)

- **Passed:** E1-11, E2-03/04/06/07/08/09, E3-01/03/05, E4-01/02/02b/02c/03/04/05/06/06c/07 (Claude half), E5-01/02, E6-01/02/03/05/06, E8-02/03, E9-01/02, E10-01/02, and on the iPhone C-04, E2-01 and E4-06b. The self-update from GitHub releases (0.1.583 to 0.1.595) worked.
- **Failed:**
  - E2-05: Other is dropped from a multi-field form with no reason shown (#146, open).
  - E6-04 and E6-07: blocked by #143. Discovery held for as long as another Windows user ran an agent. Fixed in #144.
  - E18-04 (push tap): not run.
- **Moved to Phase G:** the Codex steps, because a host restart under a held discovery dropped a live Codex session (#147, open).

## Phase G (Azure, single user)

- **g1 passed:** all the steps moved from VM01 (E1-11, E3-04 with its expected manual exit, E4-07/07b, E5-04/04b, E12-03, E8-02), and E6-01 and E6-04.
- **E2-05** fails here too (#146), so it isn't a two-user effect.
- **E6-07 failed, and showed two new bugs, both fixed in #155:**
  - **#153:** npm's node loader. A hand-started Copilot or Codex kept every finished worktree, and every launch and host restart held discovery for 2 minutes.
  - **#154:** Codex's app-server daemon kept every finished worktree once Codex had run.
  - With both worked around, reclaim removed only the host's finished trees, never agent-ha-bridge's. The cap refused the third launch with no fallback to `$repo`.
- **g2** repeated C, D, the core E steps and F from a restored S1, with the same results. E6-07 wasn't repeated on 0.1.611.

## Issues and pull requests from this work

| | State | What |
|---|---|---|
| #140 / PR #141 | merged | Migrate refused for a standard user while another user ran an agent |
| #143 / PR #144 | merged | Another user's agent held discovery |
| #153, #154 / PR #155 | merged | npm loaders and Codex's app-server kept worktrees and held discovery |
| PR #156 | merged | Checklist corrections from run 2 and Phase G |
| #146 | open | No reason shown for the missing Other in a multi-field form |
| #147 | open | A host restart under a held discovery drops a live Codex session; HA retires it |
| #149 | open | Claude's Bypass Permissions confirmation is reported as a folder-trust question |
| #150 | open | The PWA's busy message adds a contradictory "Try again in a moment." |
| #151 / PR #152 | merged | `main` CI was red on timing-dependent tests; pre-releases resumed (0.1.649 has #155, 0.1.655 is current) |

## Housekeeping

- Both Azure VMs are deallocated. Their disks and snapshots (S0, S1) remain.
- VM01 is back with adminuser. `C:\Users\rehearsal` is still there, because the deleted user's registry hive stays loaded until a reboot. The folder-trust delegate will reboot VM01; the follow-up commands are in `C:\rehearsal\backup-vm01\RESTORE.txt`.

## Verdict

**Migrate and rollback are ready for a supervised migration of one real machine, but not yet for an unattended rollout.**

The core contract held on four runs across two kinds of machine:
- Migrate stops and disables (never deletes) the old daemon.
- It re-points only the hook registrations, carries the workspaces and launch settings over, and never takes the publication-writer role.
- Rollback restores agent-ha-bridge's install, hooks and task byte for byte, and the old bridge adopts, answers, replies to and ends a session that lived through both moves.
- Nothing was orphaned or duplicated in HA.

Before migrating a real machine:

1. **Use 0.1.649 or later.** These include #155. On 0.1.611, which the rehearsal ran, worktree reclaim never runs on an npm install (the usual one). That fails safe, but launches stop once the cap is reached. #155's fix is covered by tests but hasn't been through a live E6-07 yet; repeat that one step on the new build first.
2. **Home Assistant: already resolved.** Since #142, published pre-releases (v0.1.611 and later) are release builds, so their connector may write to the real Home Assistant under G6; only PR builds are dev builds, limited to a disposable one. The rehearsal set `homeAssistant.disposable true` only because its Home Assistant was a disposable instance. On a real machine, point the connector at the real Home Assistant (`homeAssistant.baseUrl`, `homeAssistant.enabled`, `ha token set`), and pick a machine that isn't the publication writer, or move the writer role first: migrate refuses on the writer (G7).
3. **Fix #147 or keep its risk in mind.** A host restart while discovery is held can drop a live Codex session from HA. #144 and #155 removed the two causes of long holds that were seen, so it is now much less likely.
4. **Expect the operator prompts.** Codex asks to trust its hooks again after migrate and after rollback. For about 30 s after a rollback, questions stay at the terminal until agent-ha-bridge's heartbeat is fresh.

Still to do, but not blocking a supervised migration:
- the UX issues #146, #149 and #150;
- the push-tap check (E18-04), never run;
- Phase G's two-user cases, which were covered on VM01 only.
