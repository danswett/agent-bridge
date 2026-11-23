# Update coordinator feasibility and slot contract

Follow-up: https://github.com/danswett/agent-bridge/issues/2869.

## Status and landing rule (read first)

This is the design of the side-by-side update coordinator stack, carried from
`builder/ab-2869-e-076c495c` onto main in slices, each pull request Part of #2869 and
sensitive (two independent model families through Quality; the builder never arms or
merges). **Nothing described below is on main until its own pull request has merged**: a
section describes what its slice lands, and main's code is the authority on what exists.

Decision, recorded from a relay and not owner-direct: on Nov 23, 2026 (Pacific), between
08:00 and 08:16, the owner answered "Finish it" to a question about #2869, relayed by the chief
to Quality, then by the engineering lead to this builder (`docs/owner-decisions.md` in the fleet
repository records it). The relay is not the owner's approval of each CHANGE listed below; each is approved by the
owner's own yes, through the chief, in the pull request that lands it.

**Landing rule.**
- Order, each checked to compile on main plus the slices before it: this document; the retry
  class; `SlotPayloadRules`; `HostwBundle`; `SlotPreparation`; `CoordinatorImage`;
  `SlotPointer`; `SlotEntry`; `CoordinatorHandoff`; `SlotMigration`; the coordinator core; the
  native adapter; the launcher's early and stable-entry modes; **the activation setting**;
  the app and host wiring; `HeadlessUpdateTimer.MinimumApp` (defined in open #3948; needs #3810,
  the `--update-only` run, and #3948 to have shipped, and it is set in the first release after them); the acceptance proof on an existing fleet VM, least loaded, with
  rollback, run with SRE (no new Azure VM); and last, the pull request that turns activation
  on, which goes to the engineering lead first and then to the owner as its own yes.
- At most 400 lines whole per pull request (added plus deleted, tests and docs included).
  Tests come first within a pull request (a RED commit before its GREEN one, or the carried
  tests landing with the code they pin).
- A slice may add classes and tests and edit what it names below. It may not add a caller in the
  app, host or launcher of anything a later slice owns, and a slice is never live before its
  prerequisites have merged. Where a carried slice must be split or edited to meet this, the
  split is mechanical and called out in its pull request.

## Disabled until the owner turns it on

One setting, **`SlotActivation`** (name proposed here; the pull request that adds it fixes where
it is stored), per machine, **off when absent**. Required properties:
- Only the owner turns it on, on that machine, by a deliberate action. No installer, updater,
  migration, package, release feed, upgrade or default change writes it, and the last pull
  request adds **no default flip**. A release that carries these slices behaves, for a machine
  whose setting is absent, as the release before it did on the app, host and launcher paths below.
- Both states are tested for every code path it gates, with the legacy path asserted unchanged
  when off.

What each slice changes while the setting is off. "Legacy path" is the apply flow in
`docs/app-windows.md` (`InPlaceHandoff`, `--inplace-apply`, `Update.exe --norestart` without `--wait`).

| Slice | While off |
|---|---|
| This document | Nothing. |
| Retry class, `SlotPayloadRules`, `HostwBundle`, `SlotPreparation`, `CoordinatorImage`, `SlotPointer`, `SlotEntry`, `CoordinatorHandoff`, `SlotMigration`, coordinator core | New classes and tests. No caller in the app, host or launcher. Legacy path unchanged. |
| Native adapter and closed packaging | The adapter has no caller. **CHANGE C1, not gated by a setting:** `build/pack-app.ps1` publishes the launcher as one self-contained single file, and the launcher project references `App.Core`; what ships as `agent-bridge-hostw.exe` changes for every machine (the Nov 17 spike was 73,570,095 bytes), and it is stamped with the `[slot-apply-v1]` marker, which the carried host install keys on (see the wiring row). |
| Launcher early mode and stable-entry mode | New branches taken only for `--coordinator-v1` or a root alias; with the setting off no caller passes that argument and no alias exists, because `SlotMigration` writes them only under the setting. The pull request shows this with a test. |
| Activation setting | Adds the setting, off. Nothing reads it yet. |
| App and host wiring (B3c cutover, D3b and D3c) | **As carried, this is not disabled:** `Platform.ApplyAndRestart` always stages the coordinator handoff, runs `SlotMigration.Prepare` and starts `--coordinator-v1`; `Platform.KeepsHostRunning` is always true, which skips the host stop, the other-profile check and the file scans in `AutoUpdater`; `Program.cs` always sets the slot-aware locator. Carried unchanged it would replace the legacy path for every app update. **CHANGE C2 (new code, not a carry):** the wiring pull request takes the legacy path whenever the setting is off. **The host is not inert as carried either:** `HostUpdates.Locator()` returns the slot-aware locator on every Windows host, and `RefuseAtomicNativeApply()`, the first line of the host's own `ApplyAndRestart` and `ApplyAndRestartAsync`, throws when `active-slot.json` or `stable-entry.json` exists **or when the installed `agent-bridge-hostw.exe` carries the `[slot-apply-v1]` FileDescription marker**, which the launcher project stamps on every build (C1). With the setting off and no slot layout, the legacy host install would refuse on every machine once C1 ships. The wiring pull request is therefore part of CHANGE C2 for the host too: it keeps the legacy locator when the setting is off, and the refusal depends on the setting being on or a slot layout existing, never on the marker alone; the host install is tested in both states. |
| E, the retry | `UpdateApplyRetry` is called only from the native adapter and the launcher's early mode, both reachable only through the gated app path. With the setting off the legacy apply never calls it. |
| `MinimumApp` | One value in the first release after #3810, in its own small pull request; it can stop an older app from headless updates, so it is sensitive. |
| Activation on | The last pull request. Changes one machine's behaviour only after the acceptance proof passes. |

## Canonical documents, overlaps, and CHANGES

Canonical per concern; this design does not replace them. The pull request that lands a
departure edits the canonical document in the same pull request.

| Concern | Canonical | What this design does |
|---|---|---|
| Legacy app apply (`InPlaceHandoff`, helper waits for the app's PID and start time, `Update.exe --norestart` without `--wait`, completion evidence) | `docs/app-windows.md`, "Updates: Velopack, one version for app and host" | B1 to B3c restate this as `UpdateCoordinator`; legacy stays canonical for the legacy path. At activation the wiring pull request moves the slot flow's description there. |
| Departure, per-machine states, consent, restart caps, the press | `docs/design/updates-fast-fleet-update.md` sections 3 and 6 (states, restart caps) and 5 (the press); `docs/protocol/v1-update.md` | D3c bypasses the legacy departure and file-replacement gates (CHANGE C3). |
| Halt policy | `docs/design/updates-fast-fleet-update.md` section 4; `docs/design/halt-policy-without-github-api.md` | Unchanged; see Binding rules. |
| App and host version skew | `docs/app-windows.md` (an app update is a host update, so they skew mostly when an update did not finish) and `VersionSkewHeal` | Running slots report their own version (CHANGE C4). |

CHANGES for the owner's approval (none is approved by the relayed "Finish it"):
- **C1** the hostw artifact becomes one single-file bundle for every machine, carrying the
  `[slot-apply-v1] [slot-entry-v1] [coordinator-v1]` FileDescription marker. The marker alone must
  change nothing on the legacy path (see the wiring row).
- **C2** with the setting on, the app applies through the coordinator and slots, not `InPlaceHandoff`.
- **C3** with the setting on, D3c preserves the hosts (no stop) and bypasses the legacy file-replacement and departure gates.
- **C4** with the setting on, a running slot reports its own version, so app and host may differ until the host restarts onto the installed files.
- **C5** host-side native install refuses (`RefuseAtomicNativeApply`) when the setting is on or a
  slot layout exists; keyed to those, never to the marker alone.
- **C6** the activation setting itself.

Convergence of a running host onto newer installed files is `VersionSkewHeal`, not section E (E
is the retry only): detection #2888, the pure decision #3981, the banner #4078 (merged Nov 23),
and the host restarting onto its installed files once quiet, #4107 (open). That restart is not
an install.

## Binding rules (every slice, unchanged)

- **Halt policy still binds `slot-apply-v1`.** It is checked at acceptance and again at
  departure. D3c does not go through `UpdateService.DepartAsync`, so the slot path must call the
  same `PolicyCode` check wherever it bypasses it: known halts always refuse; automatic, batch
  and null policy fail closed; the digest check and the 72 h cap apply. A halted release never
  installs by this path.
- Signed press, consent and read-only checks, restart caps and the quiet-moment lease bind the slot path.
- Unasked installs stay banner-only. E's retry never starts an install. Downloads stay automatic in the background.
- No HA fallback, no raised timeouts (#2558), no change to signing or halt-list custody.

## Failure modes and loop stops

| Situation | Behaviour |
|---|---|
| App on a newer slot than the running host | Expected with the setting on (C4). Banner only; `VersionSkewHeal` restarts the host once quiet; never an install. |
| Pointer damaged or missing a slot | Stable entry fails closed, logs `stable-entry failed`, exits 1, no fallback to legacy once active. Recovery reads strict metadata without resolving the damaged payload. |
| Relay offers a release the host has halted | Refused at acceptance and at departure (Binding rules). |
| `slot-apply-v1` fails before publication | Prior pointer and legacy payloads stay authoritative; the result keeps its step; held and visible; no automatic retry of the whole update. |
| Apply fails | E only: at most three attempts, 1 s then 2 s backoff, only a known nonzero exit at `apply-completion` or a sharing or lock violation before publication; a repeated step, code or exit stops it. |
| Failure after publication | No retry from any exception. |
| Observation times out | Stays `Unknown`; never retry authority; the updater is not killed. |
| Repeated activation or migration attempts across restarts | **New, not in the carried code, required before activation can be turned on:** a failed attempt for a release is retried only by a person's action or a newer release, never by a restart; a test shows it. |
| Power loss | Not claimed durable (existing limits). |

## B: build-only feasibility proof

The existing launcher is self-contained, but is not published as a single file
(`build/pack-app.ps1`, `Publish` calls). Copying its executable alone is unsafe.
A delegated, serial build of `AgentBridge.HostLauncher` with `--self-contained`,
`-p:PublishSingleFile=true` and `-p:IncludeNativeLibrariesForSelfExtract=true`
succeeded on November 17, 2026 (Pacific), through the internal package/audit mirror.
Its output was only `agent-bridge-hostw.exe` (73,570,095 bytes) and its PDB;
no launcher DLL, deps.json or runtimeconfig.json sidecars remained.
Executable SHA256: `EADD23796B5E1263FCDD78BFE2F56FDC208F29D8D1A7D47A90FF2A7A4D274A1A`.
This establishes standalone **build feasibility**, not native execution/survival.
Nothing produced by the spike was executed.
The size and hash are evidence from that day, not a contract.

Pinned Velopack [process selection](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/bins/src/shared/util_windows.rs)
uses `QueryFullProcessImageNameW` plus `is_sub_path(root)`, not descendant ancestry.
Stage the complete helper in a unique private profile handoff subdirectory, with
working and bundle-extraction directories outside the install. Reject install-path
destinations and reparse-point ancestors before copying; retain checksum custody.
Wait for the original app's PID **and creation time** to exit, then start `Update.exe`
with `--norestart` and **without `--wait`**, which would wait on the living coordinator.
Retain the actual process handle. Pinned [apply](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/bins/src/commands/apply.rs)
awaits [Windows apply/elevation](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/bins/src/commands/apply_windows_impl.rs).
Only its authoritative result plus a fresh matching manifest under the package lock
permits respawn. A launcher exit, target files appearing or a cached locator is not
proof. Unknown remains unknown; cancelling observation never kills the updater.

### B1: bounded flow, not native wiring

`UpdateCoordinator` composes the original-app wait, operation-bound apply evidence,
package lock, fresh manifest read and respawn under one observation window. The
adapter must retain one real apply handle per generation, not launch on every probe
or retag cached evidence. Synchronous lock/read/respawn callbacks must not block.
Only current-generation worker completion with exit zero reaches the manifest read;
only its matching release identity authorizes respawn while the lock remains held.
Failures retain their step and structured error type/code, never exception messages.
B2 supplies the native adapter and closed packaging; B3 supplies private staging and
the app/launcher cutover. This layer alone changes no production apply path.

### B2: native evidence adapter and build closure

Pin the [Velopack 1.2.161 image](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/bins/build.rs)
read-only through real exit; missing or other native release metadata is unknown.
The adapter pins the original process handle before checking its UTC creation time,
and owns the real apply handle through its exit wait. A nonzero updater exit alone
cannot disprove a surviving elevated worker; it stays unknown, never retry authority.
The [pinned manifest parser](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/lib-rust/src/bundle.rs)
uses namespaced NuGet metadata. Read `current\sq.version` anew under the package lock
and check package identity, main executable and release identity before respawn.
Native starts remain injectable for in-process tests; no native binary is run locally.

Observation cancellation does not release the updater pin, package custody or real
process handle: the owned noncancellable exit wait retains them until actual exit.
The future B3 entry point must drain adapter `Ownership` before exiting, including
late native respawn completion/errors. Respawn observation uses the same deadline
and releases the package lock on timeout/cancellation. An entered native start
cannot be revoked: its late outcome stays unknown, never retry authority.

### B3a: compatibility metadata only

Read native VERSIONINFO/FileDescription from the retained image stream, with
bounded PE resource/block traversal. The fixed `[coordinator-v1]` suffix gates
CLI compatibility only, never trust; missing support refuses with
`coordinator-protocol-unavailable`. Shared bundle verification and digest custody
remain mandatory at staging. No hostw is stamped with that suffix here: it must
ship in the same build as the real early coordinator mode, not a metadata-only slice.

### B3b: private current-helper handoff, not cutover

Validate canonical paths, reparse ancestors and install exclusion before any stage
write. Copy the current trusted installed helper through its retained read-only
handle, binding source/copy SHA256 and requiring the shared C2 bundle verifier.
Read compatibility metadata from those streams, never a re-resolved path. Keep
the target package's verified checksum/copy lease separate: downgrade targets are
applied by the current coordinator, not an old target helper. A missing v1 marker
refuses without fallback or staging. Directory checks do not pin ancestor handles;
the documented ancestry TOCTOU limit remains. No title marker or launch is wired here.

### B3c: early mode and app cutover

The native v1 title marker co-ships with the real early-mode branch, before normal
host/profile/job setup. The app launches only its staged current helper, with private
working/extraction paths and the verified cached target, then exits. The single-use
mode revalidates the bounded strict JSON, layout, image bundle/marker and target
checksum, retains their leases, and drains B2 `Ownership` before exiting even after
observation times out. Its result includes step, elapsed time and apply PID/path.
No native execution is used as local proof; the closed build is checked statically.
The SDK native icon directory is 1,358,076 bytes: the bounded resource envelope is
16 MiB, matching C2's section ceiling; version nesting/block limits remain unchanged.
Legacy `current` remains authoritative until D proves activation/migration.

## C/D: approved design basis, not activation proof

Use immutable `root\slots\<release-identity>-<package-digest>` payloads. One validated
relative slot/version in `root\active-slot.json` is the activation commit point for
both stable app and host launchers. C prepares complete validated slots **without
activation**. Never rewrite a running slot or equate directory appearance with activation.

D must first prove atomic pointer publication on Windows (candidate: `ReplaceFileW`
for an existing pointer; initial publication and crash durability need separate proof),
read-once slot pinning, and legacy migration/recovery. Keep legacy `root\current`
intact until the stable launchers and pointer are validated; never delete or rename
a live legacy directory. Rollback switches the pointer, not payload bytes. Velopack's
legacy `current` contract must not silently remain authoritative after activation.
These are prerequisites, not implemented guarantees or permission to wire activation.

### D1: disabled pointer primitive

`SlotPointer` is not wired to production. It revalidates the cached package and
entire prepared payload while retaining read-only leases, then publishes a bounded
pointer by same-directory rename (first publication) or Windows `ReplaceFileW`
through `File.Replace`. Readers retain one pointer generation and payload leases;
replacement does not invalidate their old slot. Rollback revalidates the previous
slot and replaces only the pointer. Faults before publication leave the old pointer
or absent-pointer legacy layout unchanged. Payload/legacy directories never move.
Recovery loads strict bounded pointer metadata without resolving the damaged
current payload; only its destination is revalidated and pinned. Reads fail closed.
Directory ancestry is checked, not handle-pinned; the existing same-user ancestor
rename race remains. Power-loss directory durability is not claimed. D2/D3 must
prove stable routing and migration before enabling production activation.

### D2: stable entry protocol, no deployment or activation

The closed hostw bundle supports root `AgentBridge.exe` and
`agent-bridge-hostw.exe` aliases. Root copies resolve once and retain the selected
slot through the child lifetime; payload copies keep their ordinary roles.
Missing pointer means trusted legacy `current`; invalid pointer fails closed.
Malformed or corrupt slots log `stable-entry failed` and exit 1 without fallback.
After activation, a missing root launcher is logged as unavailable rather than
launching/registering the legacy payload or escaping the app's installation check.
The native slot-entry-v1 marker gates compatibility, never image trust.
Host registrations select the root helper only after it exists; build folders
and unmigrated installs are unchanged. D2 deploys no aliases and publishes no
pointer. D3 must install verified aliases before its single activation commit.

### D3a: recoverable bootstrap preparation, no publication

`SlotMigration` has no production caller yet. It caches the verified target,
prepares/revalidates its slot, and requires a closed bundle plus slot-entry-v1.
An immutable `stable-entry.json` retains the first bootstrap's verified package
provenance; later targets do not overwrite running root images. Both root aliases
are checksum-bound copies, with repeat/interrupted preparation safe while legacy
`current` stays intact. A missing pointer still routes to legacy. No pointer is
published here; D3b/D3c must complete locator/coordinator and non-stopping handover
before enabling activation. Existing ancestry/power-loss limitations remain.

### D3b: slot-aware completion and installed identity, disabled

The adapter accepts an explicit managed-apply operation, records the pointer
rather than a fictional native process, and retains one fresh verified snapshot
through version confirmation and respawn. The default production factory still
uses native apply. `SlotVelopackLocator` is also not wired yet: it preserves the
legacy installation's package/channel identity but reports the running immutable
slot's own version/content, never the latest pointer's version. D3c must wire both
with non-stopping caller behavior and the single publication point.

### D3c: atomic migration, off when absent and owner-only

Atomic `slot-apply-v1` requires verified current/target helpers and stable entries;
unsupported downgrades never fall back to native apply. Under the package writer
lock, preparation caches SHA-256 slots (after verifying either feed checksum),
then atomically publishes `active-slot.json`. Pre-commit failure/cancellation
preserves the prior authority and legacy payloads. Version confirmation binds the
verified package digest; one retained snapshot supplies respawn even on republish.
Before handoff, stable aliases and enabled task/existing Run registrations are
prepared without starting/stopping hosts. The app preserves hosts and consent/
read-only checks, bypassing legacy file-replacement and departure gates (C3; halt policy still binds, see Binding rules). Running
slots report their own version. Host-side native install refuses this layout (C5), when the setting is on or the layout exists.
Convergence of a running host is `VersionSkewHeal`, not E. Quality owns two-family review. The native update acceptance proof runs on an
existing fleet VM with SRE, before activation is turned on; focused managed tests
do not establish native or power-loss durability.

### E: bounded automatic apply retry

The adapter owns at most three known-failed apply attempts with one- then
two-second backoff, draining each failed attempt before another starts. Only
`Failed` / `exit-failed` at `apply-completion` with a known nonzero exit authorizes
this path. Repeating the same step/code/exit stops; terminal results stay unchanged.
Native nonzero launcher exits remain `Unknown` and never authorize retry.

Managed activation separately retries only sharing/lock violations from a fully
exited attempt before D3c's publication callback. Each attempt rechecks the same
cached digest; the adapter retains package custody throughout. It stops on a
repeated native error code or three attempts, with the same backoff. Once the
publication callback is crossed, no exception permits retry. Cancellation, checksum
refusal and all other failures propagate to the existing result/ownership path:
no new interpretation of `Unknown`, `Failed` or `Completed`. Existing step,
PID/path and timing reporting remains intact. Focused fake-clock/held-file tests
are not native updater or live-host proof; that is the fleet-VM acceptance proof with SRE.
