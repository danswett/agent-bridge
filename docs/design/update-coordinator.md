# Update coordinator feasibility and slot contract

Follow-up: https://github.com/danswett/agent-bridge/issues/2869.

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
refuses without fallback or staging. Windows install/stage comparisons use retained
directory-handle final paths, resolving device/short-name aliases before writes.
Unresolvable paths refuse; other ancestor TOCTOU limits remain. No marker or launch is wired here.

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

### F: owned updater observation, not blocking-holder discovery

The early mode always wires a private persistent observation, capped at 64 KiB.
The existing ten-minute apply-observation deadline publishes Unknown before the
separate real-ownership drain; it never kills the updater or authorizes retry.
Snapshots are flushed then atomically renamed, and late ownership errors replace
the prior timeout with their step, typed error and timing. Oversized evidence
becomes an explicit observation failure, not an unbounded record or success.
Every record labels `ApplyProcessId` and `ApplyPath` as the **updater process**,
never a blocking holder. Failed/Unknown diagnostics include those values and
timing; only Completed can respawn. The Restart Manager blocker scan is unchanged.

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
