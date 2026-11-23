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
