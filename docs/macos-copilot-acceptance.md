# Opt-in real Copilot acceptance

`ManagedCopilotAcceptanceTests` exercises the native macOS backend with the real
Copilot CLI and the product's `SessionSource`, `LaunchService`, `ReplyService`,
`AnswerService` and `StopService`, including the host's `MacLaunchCompatibility`
policy. It makes four short model turns and is skipped unless explicitly enabled.
Ordinary CI does not launch an agent or spend model usage.

Prerequisites: macOS, .NET 10, tmux and a native Copilot executable, and a normal
Copilot authentication path already available to that CLI. The test never copies
credentials, supplies a token in argv, or starts a login flow. If the isolated
profile needs login, it fails with a needs-login diagnostic. The real Mac G5 probe
must be present and must confirm that no legacy bridge writer could be active.

From the repository root:

```sh
AGENT_BRIDGE_RUN_REAL_COPILOT=1 TMPDIR=/private/tmp dotnet test tests/AgentBridge.Host.Tests/AgentBridge.Host.Tests.csproj --filter FullyQualifiedName~ManagedCopilotAcceptanceTests
```

The executable defaults are `/opt/homebrew/bin/copilot` and
`/opt/homebrew/bin/tmux`; override them with the absolute paths in
`AGENT_BRIDGE_COPILOT_EXECUTABLE` and `AGENT_BRIDGE_TMUX_EXECUTABLE` when needed.

Each run creates an owner-only directory under `/private/tmp`, containing a fresh
`COPILOT_HOME`, approved empty workspace and private tmux socket. `HOME` and
`CODEX_HOME` are not replaced. The temporary Copilot config disables all hooks,
memory and IDE auto-connection. CLI flags disable custom instructions, remote
export, auto-update, Bash environment loading and built-in MCP servers. Only the
non-executing `ask_user` question tool is exposed; shell, write and URL access are
explicitly denied, and automatic access to the system temporary directory is
disabled. No legacy profile, hook installation or host configuration is changed.

Success requires a transcript-confirmed launch, a completed model response, a
transcript-confirmed reply, the same cached outcome on retry of that reply UUID,
exactly one recorded prompt for it, another completed response, a harmless free-text
`ask_user` question answered through the real `AnswerService`, an idempotent answer
retry, a final completed response, and a confirmed session stop. Cleanup is limited
to that backend's owned panes and still observes
G5. Successful runs remove their private artifacts after the session and source
have stopped. Failures retain them for local inspection and print only the folder
path, never terminal/authentication screens or transcript content.

On November 2, 2026 (Pacific), native Copilot 1.0.64 completed this four-turn
launch/reply/question/answer/stop cycle in 20 seconds. A fresh run with the final
integrated host code and actual `MacLaunchCompatibility` policy passed in 17
seconds. The product services confirmed every delivery from the real transcript,
both same-UUID retries returned the cached outcome, and no owned session or
successful-run artifacts remained. This verifies host discovery and delivery;
it does not by itself prove the native Apple app, relay and physical-device
connection chain.
