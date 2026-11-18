using AgentBridge.Host.Core;

namespace AgentBridge.Host.Delivery.Launch;

public static class AgentEnvironment
{
    public static Dictionary<string, string?> For(string copilotHome, string claudeHome,
        string codexHome, bool? claudeConfigDirExplicit, string? path = null, Uri? homeAssistantBaseUrl = null)
    {
        var environment = new Dictionary<string, string?>();
        var inherited = SessionSourceOptions.ForCurrentUser();
        if (!Folders.Same(copilotHome, inherited.CopilotHome)) { environment["COPILOT_HOME"] = copilotHome; }
        // Even the default path changes Claude's account file when explicitly configured.
        if (claudeConfigDirExplicit is { } explicitConfigDir) { environment["CLAUDE_CONFIG_DIR"] = explicitConfigDir ? claudeHome : null; }
        else if (!Folders.Same(claudeHome, inherited.ClaudeHome)) { environment["CLAUDE_CONFIG_DIR"] = claudeHome; }
        if (!Folders.Same(codexHome, inherited.CodexHome)) { environment["CODEX_HOME"] = codexHome; }
        if (path is not null) { environment["PATH"] = path; }
        if (homeAssistantBaseUrl is not null)
        {
            var baseUrl = homeAssistantBaseUrl.AbsoluteUri.TrimEnd('/');
            if (baseUrl.EndsWith("/api", StringComparison.Ordinal)) { baseUrl = baseUrl[..^4]; }
            environment["AGENT_HA_URL"] = baseUrl + "/api";
        }
        return environment;
    }
}
