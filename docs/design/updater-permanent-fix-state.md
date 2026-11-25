# Updater permanent fix (#2869): state on Nov 23, 2026 PT

Status: a verified account of where the work stands, not a new design. Read from GitHub and `main` on Nov 23
(PT); each claim below is one a person can re-check from the PR or file named. Tracking:
[#2869](https://github.com/danswett/agent-bridge/issues/2869) (the owner's P0: side-by-side layout, update
completion, version skew, auto-apply path). The coordinator design,
`docs/design/update-coordinator.md`, is **not on `main`**: it exists only on the stranded branches named below,
so this file records the state instead of editing it.

## On main

| PR | What it is | Merged (PT) |
|---|---|---|
| [#2888](https://github.com/danswett/agent-bridge/pull/2888) | Slice 1: app/host version-skew **detection** (detection only, no self-heal) | Nov 17 17:31 |
| [#3099](https://github.com/danswett/agent-bridge/pull/3099) | Holder-to-file diagnostics for blocked installs (slice A, AB2920) | Nov 18 19:35 |
| [#2884](https://github.com/danswett/agent-bridge/pull/2884) | #2866: bounded Restart Manager scans, one cached clear scan | Nov 19 04:09 |
| [#2900](https://github.com/danswett/agent-bridge/pull/2900) | Slice A: the update apply-completion observer seam | Nov 19 19:55 |
| [#2913](https://github.com/danswett/agent-bridge/pull/2913) | Updater profile refusal (#2833) | Nov 19 23:47 |

[#3343](https://github.com/danswett/agent-bridge/issues/3343) (DiagTrack holds `current\mscordaccore.dll`, so
Velopack cannot rename `current`) is an **open issue**, not a merged change.

What these add up to: updates still replace the single `...\AgentBridgeApp\current` folder in place. The app
waits for holders to release (bounded scans, named holders, the 10/20/40/60-minute backoff), and the path "never
stops a service or self-elevates; it does not replace the later slot design"
([app-windows.md](../app-windows.md), the Restart Manager paragraph under Updates). **The in-place wait is a
fallback only.** Problem 1 of #2869, the side-by-side layout with an atomic `current` switch, is not solved on
`main`, and neither is the unattended-update acceptance on a disposable VM.

## Stranded: the coordinator stack

- **B1,** [#2930](https://github.com/danswett/agent-bridge/pull/2930): open, **draft**, based on
  `builder/agent-bridge-2869-a` (slice A's branch), not on `main`.
- **B2,** [#2945](https://github.com/danswett/agent-bridge/pull/2945): **merged** on Nov 20 11:07 into
  `builder/agent-bridge-2869-b` (B1's branch), so it never reached `main`.
- `docs/design/update-coordinator.md` (the build-only feasibility proof, the B1 bounded flow, B2 parts 1 and 2)
  lives only on that branch. Nothing of the coordinator flow is live; it is **still planned**.
- Many further `builder/agent-bridge-2869-*` branches exist (B3, the C slots, D, the carries). Their state is
  not verified here.

## Open on the path to main

- [#3810](https://github.com/danswett/agent-bridge/pull/3810): the app's `--update-only` run, so the host can
  update a machine whose app is closed (PR 1 of 2). Open, against `main`.
- [#3938](https://github.com/danswett/agent-bridge/pull/3938) (H2a, `HeadlessAppUpdate`, the host's starter for
  that run) and [#3948](https://github.com/danswett/agent-bridge/pull/3948) (H2b, `update.startAppToInstall` and
  its timer): both **draft**, each stacked on the one before (#3938 on #3810's branch, #3948 on #3938's). The
  owner's Nov 22 16:50 PT answer that headless update is on by default is recorded in the
  fleet repository's `docs/owner-decisions.md`.
- [#3981](https://github.com/danswett/agent-bridge/pull/3981): `VersionSkewHeal`, the pure decision for
  app/host version skew (problem 3 of #2869; the decision only, not the repair). Open, against `main`.

## Open question

**Land or close the coordinator stack** is pending with the owner (as relayed by R8b and Chief; no answer is
recorded in a comment read for this file). What each answer means from the facts above:

- *Land it:* B1 must be retargeted from slice A's branch to `main` and B2's merge brought across from the B
  branch; the doc above then joins `main`; B3 and the slot work follow, since B1 and B2 alone change no
  production apply path.
- *Close it:* the in-place fallback stays the only path, and problem 1 needs a new design.

## Not verified here

Whether the acceptance in #2869 has been shown on a disposable VM; the state of the many slice branches; and
the issue's own slice checklist, which still shows merged slices (#2884, #2900) unchecked and is stale.
