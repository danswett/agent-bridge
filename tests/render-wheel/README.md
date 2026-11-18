# Synthetic Windows wheel replay (#2738)

Run only on the host SRE names, at 125 percent display scale, from an owned worktree at the Quality-admitted head. The app sources match build 6276; this builds a separate development executable, not an installer. Use only its synthetic fixture.
The test owns its separate PID/HWND and self-test single-instance namespace. Never install, restart, automate or redirect to the owner's app. No global input or weakened foreground/ownership guard is permitted.
Use the approved package cache/mirror for missing restore assets, without changing machine setup or credentials. Build from the repository root:
```powershell
dotnet build src\AgentBridge.App\AgentBridge.App.csproj --no-restore -p:Platform=x64 -p:WarningsNotAsErrors=NU1905 -v minimal
```
Create isolated results outside OneDrive and launch only this development executable:
```powershell
$out = Join-Path ([IO.Path]::GetTempPath()) ('render-wheel-' + [guid]::NewGuid()); New-Item -ItemType Directory -Path $out | Out-Null
$exe = Resolve-Path 'src\AgentBridge.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\AgentBridge.exe'; Get-FileHash $exe -Algorithm SHA256
$p = Start-Process $exe -WorkingDirectory (Split-Path $exe) -ArgumentList '--self-test-live-list',(Join-Path $out 'result.jsonl') -PassThru
if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id; throw 'Owned wheel test exceeded 180 seconds.' }; $p.Refresh(); $p.ExitCode
```
Record the exact Git head, executable SHA-256, PID, measured scale, exit code and metadata-only result. The test attempts 136 signed wheel notches, logging before/after geometry, realised ranges and routed input.
If there is no foreground desktop/thread, owned activation is false, or focus belongs elsewhere, it stops before any notch. Report that exact state and the isolated `live-list-logs\app.log`; do not repair the desktop or relax the guard.
An exit of 1 can be an existing pinned/newest-position failure or a foreground setup failure; neither proves sustained blanking. Report completed notch/routed counts and any zero-visible-content samples separately. Never copy owner transcripts, video, credentials or other installed-app data into this fixture or its result.
