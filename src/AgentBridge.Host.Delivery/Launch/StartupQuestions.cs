namespace AgentBridge.Host.Delivery.Launch;

// A question an agent can stop on at startup, before its session registers, which only a person
// at the machine answers. This host never types into one: it reads the window, says so, and
// turns other launches away (busy) while a trust question waits, as agent-ha-bridge does for
// Claude's. Labels are matched as each agent draws them.
public enum StartupQuestion { None, Trust, HooksReview }

public static class StartupQuestions
{
    public static StartupQuestion Read(IReadOnlyList<string>? rows)
    {
        if (rows is null || rows.Count == 0) { return StartupQuestion.None; }
        var screen = string.Join('\n', rows);
        bool Has(string s) => screen.Contains(s, StringComparison.Ordinal);
        // Copilot CLI's folder trust dialog ("Confirm folder trust", 1.0.105).
        if (Has("Do you trust the files in this folder?") || Has("Confirm folder trust")) { return StartupQuestion.Trust; }
        // Claude's folder trust question, and the Bypass Permissions warning that
        // --dangerously-skip-permissions itself raises (agent-ha-bridge's
        // $script:BridgeClaudeStartupPrompts).
        if (Has("Yes, I trust this folder") || (Has("Yes, I accept") && Has("No, exit"))) { return StartupQuestion.Trust; }
        // Codex asks, in its window, for hooks to be trusted (agent-ha-bridge's BlockingPrompt).
        if (Has("Hooks need review")) { return StartupQuestion.HooksReview; }
        return StartupQuestion.None;
    }
}
