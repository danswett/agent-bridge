namespace AgentBridge.Host.Delivery.Copilot;

// A reply typed into Copilot CLI's input, as agent-ha-bridge types one
// (hooks/decision-inject.ps1, Send-CopilotSessionPrompt): the words as one burst, then,
// 300 ms later, Enter as a keystroke of its own. The burst is a paste to the CLI, and only
// a separate Enter submits it. Every line break and other control character becomes a
// space first (InjectableText.Collapse), exactly as there: an Enter inside the words would
// submit part of the reply, so a reply is always one line.
public static class CopilotReply
{
    public static (KeyPlan Keys, string Words) Plan(string text)
    {
        var words = InjectableText.Collapse(text);
        return (new KeyPlan([new KeySend(words, true, CopilotPlanner.ReplyEnterDelayMs, 0)]), words);
    }

    // The line typed for a reply with attachments, as agent-ha-bridge builds it
    // (New-BridgeAttachmentPrompt): Copilot CLI attaches a file the prompt mentions as
    // @<path>, so each staged file leads as one, and the words, trimmed, follow. No quoting of
    // a path has been established, so one with whitespace in it, which would split into two
    // broken mentions, is refused (null) rather than sent with some attachments missing.
    public static string? WithAttachments(string? text, IReadOnlyList<string> paths)
    {
        var parts = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsWhiteSpace)) { return null; }
            parts.Add("@" + path);
        }
        var words = InjectableText.Collapse(text ?? "").Trim();
        if (words.Length > 0) { parts.Add(words); }
        return string.Join(' ', parts);
    }

    // Whether Copilot attached every staged file to the prompt it recorded: a mention it could
    // not read is left as text, and the agent never sees the file.
    public static bool Attached(IReadOnlyList<string> paths, IReadOnlyList<string>? recorded) =>
        paths.All(p => recorded?.Any(r => string.Equals(r, p, StringComparison.OrdinalIgnoreCase)) == true);

    // Whether a recorded prompt is the reply typed. Whitespace runs are one space and the
    // ends are trimmed, since the CLI may trim what it was given; any other difference is a
    // different prompt.
    public static bool Matches(string words, string recorded) =>
        string.Equals(InjectableText.Flatten(words), InjectableText.Flatten(recorded), StringComparison.Ordinal);
}
