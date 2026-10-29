using AgentBridge.Host.Delivery.Copilot;

namespace AgentBridge.Host.Delivery.AgentReplies;

// A reply as it is typed into Claude Code or Codex, and how what the agent records is told to
// be it. Words are what its prompt record must say; Composed is what its input box shows once
// typed, which is what decides whether Enter may be pressed again.
public sealed class AgentReply
{
    readonly Func<IReadOnlyList<ScreenRow>, KeyPlan> keys;
    readonly bool images;

    AgentReply(string words, string composed, IReadOnlyList<string> files, bool images, Func<IReadOnlyList<ScreenRow>, KeyPlan> keys)
    {
        Words = words;
        Composed = composed;
        Files = files;
        this.images = images;
        this.keys = keys;
    }

    public string Words { get; }

    public string Composed { get; }

    public IReadOnlyList<string> Files { get; }

    // The keys, the first send held to the input box's rows as read (KeySend.Expect).
    public KeyPlan Keys(IReadOnlyList<ScreenRow> box) => keys(box);

    // Whether a recorded prompt is this reply's words. Whitespace runs are one space and the
    // ends are trimmed; for Codex, the placeholders standing for its images are not words.
    public bool Says(AgentPrompt prompt) => string.Equals(
        InjectableText.Flatten(images ? CodexPrompts.Words(prompt.Text) : prompt.Text),
        InjectableText.Flatten(Words), StringComparison.Ordinal);

    // Whether the agent took in every file staged for the reply.
    public bool Attached(AgentPrompt prompt) =>
        Files.All(f => prompt.Files.Any(r => string.Equals(r, f, StringComparison.OrdinalIgnoreCase)));

    // Claude Code reads a file its prompt mentions as @<path>, image or text, as Copilot does
    // (seen live with 2.1.312: an "attachment" entry follows the prompt for each), so the line
    // is the one Copilot is sent. Null when a path cannot be mentioned.
    public static AgentReply? Claude(string text, IReadOnlyList<string> staged)
    {
        var line = staged.Count > 0 ? CopilotReply.WithAttachments(text, staged) : InjectableText.Collapse(text);
        if (line is null) { return null; }
        return new AgentReply(line, line, staged, images: false,
            box => new KeyPlan([new KeySend(line, true, CopilotPlanner.ReplyEnterDelayMs, 0, box)]));
    }

    // Codex does not read an @<path> mention (it is left as words, seen live with 0.165). It
    // attaches an image when a paste is exactly a path to one, showing "[Image #n]" in its box,
    // so each image path goes as a paste of its own, then the words, then Enter.
    public static AgentReply? Codex(string text, IReadOnlyList<string> staged)
    {
        if (staged.Any(p => string.IsNullOrWhiteSpace(p) || p.Any(char.IsWhiteSpace))) { return null; }
        var words = InjectableText.Collapse(text).Trim();
        var placeholders = string.Concat(staged.Select((_, i) => $"[Image #{i + 1}]"));
        var tail = staged.Count > 0 && words.Length > 0 ? " " + words : words;
        return new AgentReply(words, placeholders + tail, staged, images: true, box =>
        {
            var sends = staged.Select((path, i) => new KeySend(path, false, 0, ImagePauseMs, i == 0 ? box : null)).ToList();
            sends.Add(new KeySend(tail, true, CopilotPlanner.ReplyEnterDelayMs, 0, staged.Count == 0 ? box : null));
            return new KeyPlan(sends);
        });
    }

    // Long enough after a pasted path for Codex to have taken it as one paste, so the next
    // keys are not read as more of it.
    const int ImagePauseMs = 500;
}
