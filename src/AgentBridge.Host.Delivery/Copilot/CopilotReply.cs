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

    // Whether a recorded prompt is the reply typed. Whitespace runs are one space and the
    // ends are trimmed, since the CLI may trim what it was given; any other difference is a
    // different prompt.
    public static bool Matches(string words, string recorded) =>
        string.Equals(InjectableText.Flatten(words), InjectableText.Flatten(recorded), StringComparison.Ordinal);
}
