using System.Text.Json;
using AgentBridge.Protocol;

// Hand-built launch.options shape, NOT a recording or proof of any real machine's offers.
// Workspace paths are symbolic; the replay binds them to its disposable fixture folders.
internal static class LaunchOptionsSyntheticFixture
{
    public const string Machine = "synthetic-machine";
    public const string Provenance = "Synthetic launch.options fixture; no live machine was probed.";

    public static LaunchOptions Read() => JsonSerializer.Deserialize<LaunchOptions>("""
        {
          "agents": [
            {
              "id": "copilot", "label": "Copilot", "kind": "copilot",
              "models": ["fixture-model", "auto"],
              "efforts": ["high"], "contexts": [],
              "defaults": {}
            },
            {
              "id": "codex", "label": "Codex", "kind": "codex",
              "models": ["other-agent-model"],
              "efforts": ["high"], "contexts": [],
              "defaults": {}, "needsPrompt": true
            }
          ],
          "workspaces": [
            { "label": "App", "path": "<disposable App folder>" },
            { "label": "API", "path": "<disposable API folder>" }
          ],
          "resumable": [],
          "defaults": { "agent": "copilot", "workspace": "App", "permissions": "ask" }
        }
        """, ProtocolJson.Options)!;
}
