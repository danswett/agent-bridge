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

### B2 part 1: adapter seams and build closure

The shared request, process interface and launch specifications separate native
process ownership from the coordinator flow. Apply arguments keep the verified
cached package form; app restart arguments preserve the original profile.
The launcher references the shared coordinator and contracts in its self-contained
bundle instead of compiling private copies.

Respawn observation uses the same deadline and releases the package lock on
timeout/cancellation. An entered native start cannot be revoked: its late outcome
stays unknown, never retry authority. `RespawnFinished` retains that late outcome
for the native adapter in part 2; this slice starts no native updater.

### B2 part 2: native evidence adapter

Pin the [Velopack 1.2.161 image](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/bins/build.rs)
read-only through real exit; missing or other native release metadata is unknown.
The adapter pins the original process handle before checking its UTC creation time,
and owns the real apply handle through its exit wait. A nonzero updater exit alone
cannot disprove a surviving elevated worker; it stays unknown, never retry authority.
The [pinned manifest parser](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/src/lib-rust/src/bundle.rs)
uses namespaced NuGet metadata. Read `current\sq.version` anew under the package lock
and check package identity, main executable and release identity before respawn.
Observation cancellation retains updater pin, package custody and the real process
handle until actual exit; an admitted apply joins adapter `Ownership` before it runs.
The future B3 entry point must drain it before exiting, including late respawn completion/errors;
the adapter observes a faulted `Ownership` itself, so an undrained late fault never escalates as unobserved.
Native starts remain injectable for in-process tests; no native binary is run locally.

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
