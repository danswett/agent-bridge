# S1: console injection from a console-less .NET process

**Verdict: GO, with delivery done by a separate injector worker process.**

Run on Windows 11 Enterprise build 26300 (x64), .NET SDK 10.0.403, PowerShell 7.6.9 and
GitHub Copilot CLI 1.0.104, on 2026-10-27.

## Question

The PowerShell daemon types answers into agent sessions with
`FreeConsole → AttachConsole(pid) → CreateFile("CONIN$") → WriteConsoleInputW(text) →
pause → WriteConsoleInputW(Enter) → FreeConsole`. It must be launched with a hidden
*real* console (a VBS launcher, never `conhost --headless`). The rewrite needs to
know whether a .NET `WinExe`, which has no console at all, can do the same, and what
else breaks when a long-lived host process does it.

## What was run

* `Injector/`: a `WinExe` with the daemon's `ConsoleInjector.Send` reproduced byte for
  byte, plus modes that probe the failure cases.
* `run-s1.ps1`: opens throwaway target consoles (`target.ps1` logs each line it reads,
  and which console host it is in), drives the injector, and checks every delivery
  against what the target read. It stops only the processes it started, by PID.
* `old-injector.ps1`: sends the same text with the **existing** bridge's injector
  (its C# extracted from `hooks/decision-inject.ps1`, run hidden as the daemon is),
  for a like-for-like comparison.
* `live-copilot.ps1`: a real Copilot CLI session with a throwaway `COPILOT_HOME`
  (no bridge hooks or instructions; the shared `pkg` is reached through a junction
  that is removed on its own first).

## Results

| Case | Result |
|---|---|
| Delivery into a conhost console (`ConsoleWindowClass`) | ✅ |
| Delivery into a Windows Terminal / ConPTY console (`PseudoConsoleWindow`) | ✅ |
| 40 deliveries alternating between two consoles from one process | ✅ 0 failures, each target received exactly its own lines in order. About 64 ms per delivery, of which 60 ms is the deliberate pre-Enter pause. Handle count stable (224) |
| Non-ASCII text (`héllo – 世界 ✓`, quotes, backslashes) | ✅ **Identical to the existing bridge injector** into identical targets. The `?` replacements come from the test target's `Console.ReadLine` code page, not from injection |
| Windowless child process after an attach/free cycle (`CreateNoWindow`) | ✅ Runs, no console of our own left behind |
| **Live Copilot CLI 1.0.104**: prompt typed, then Enter as a separate keystroke | ✅ `user.message` recorded **exactly** as sent, the model answered with the marker, and `/exit` ended the session |
| Ctrl+C in the target console while attached, no handler | ❌ Injector terminated (`0xC000013A STATUS_CONTROL_C_EXIT`) |
| Same, managed handler registered **before** `AttachConsole` | ❌ Terminated: the registration does not cover the console attached later |
| Same, handler registered **after** `AttachConsole`, or `SetConsoleCtrlHandler(NULL, TRUE)` | ✅ Survives |
| **Target window closed while attached** (no handler / before / after) | ❌ **Terminated in every variant.** Windows ends every process attached to a console that closes, after its handlers return |

## Consequences for the design

1. **Do not attach from the host process itself.** A person closing a session window
   at the moment a reply is being typed into it would take down the whole host, and
   every session would lose the bridge until it restarted. Delivery goes through a
   small **injector worker** (`WinExe`, NativeAOT) that the host starts and supervises:
   * One worker serializes all deliveries (one console per process at a time).
   * It sets `SetConsoleCtrlHandler(NULL, TRUE)` at start and registers its handler
     again after each attach.
   * If it dies mid-delivery, the host restarts it and reports the outcome as
     *wrote, unknown*, the same "nothing can say how much landed" rule the daemon's
     `Wrote` flag already follows. It never retries silently.
2. The host itself never needs a console, so the hidden-console launcher (VBS) and
   its logon retry loop are not needed for the host. Every console child the host
   starts must use `CreateNoWindow` (one `StartWindowless` helper).
3. Unicode behavior is unchanged from today, so no new parity risk.

## Note for the existing bridge (not acted on here)

The PowerShell daemon uses the same attach sequence in-process, so it is presumably
exposed to the close-while-attached termination as well. Its supervisor restarts it
with backoff. This was **not** tested against the daemon itself. If it ever matters,
the fix there would be the same worker pattern, but that is for the
`agent-ha-bridge` repository and its own process.

## Reproduce

```powershell
pwsh -NoProfile -File spikes\s1-console-injection\run-s1.ps1
pwsh -NoProfile -File spikes\s1-console-injection\live-copilot.ps1   # needs a logged-in Copilot CLI
```
