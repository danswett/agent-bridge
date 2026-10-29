using System.Text.RegularExpressions;
using AgentBridge.Host.Delivery.Copilot;

namespace AgentBridge.Host.Delivery.AgentReplies;

// One send of a reply, and what the input box must show just before it: null for the first,
// which goes into the empty box as first read; otherwise the box as the sends before it left
// it. Each is held to the box's rows as read (KeySend.Expect).
public sealed record AgentSend(string Text, bool Enter, string? After);

// A reply as it is typed into Claude Code or Codex, and how what the agent records is told to
// be it. Words are what its prompt record must say; Composed is what its input box shows once
// typed, which is what decides whether Enter may be pressed again.
public sealed class AgentReply
{
    // How many "[Image #n]" placeholders lead the recorded text: those of the images pasted.
    readonly int images;

    AgentReply(string words, string composed, IReadOnlyList<string> files, int images, IReadOnlyList<AgentSend> sends)
    {
        Words = words;
        Composed = composed;
        Files = files;
        this.images = images;
        Sends = sends;
    }

    public string Words { get; }

    public string Composed { get; }

    public IReadOnlyList<string> Files { get; }

    public IReadOnlyList<AgentSend> Sends { get; }

    public static KeySend Key(AgentSend send, IReadOnlyList<ScreenRow> box) =>
        new(send.Text, send.Enter, send.Enter ? CopilotPlanner.ReplyEnterDelayMs : 0, 0, box);

    // Whether a recorded prompt is this reply's words. Whitespace runs are one space and the
    // ends are trimmed. For Codex, the placeholders its pasted images left at the start are not
    // words; one the person typed, anywhere, is.
    public bool Says(AgentPrompt prompt) => string.Equals(
        InjectableText.Flatten(images > 0 ? Regex.Replace(prompt.Text, $@"^(?:\s*\[Image #\d+\]){{0,{images}}}", "") : prompt.Text),
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
        return new AgentReply(line, line, staged, images: 0, [new AgentSend(line, true, null)]);
    }

    // Codex does not read an @<path> mention (it is left as words, seen live with 0.165). It
    // attaches an image when a paste is exactly a path to one, showing "[Image #n]" in its box.
    // Typed keys that reach it together are one paste, though, so a path and the words after it
    // were taken as text when sent half a second apart: each path goes only once the box shows
    // the images before it as images, and the words and Enter once it shows them all.
    public static AgentReply? Codex(string text, IReadOnlyList<string> staged)
    {
        if (staged.Any(p => string.IsNullOrWhiteSpace(p) || p.Any(char.IsWhiteSpace))) { return null; }
        var words = InjectableText.Collapse(text).Trim();
        var sends = staged.Select((path, i) => new AgentSend(path, false, i == 0 ? null : Placeholders(i))).ToList();
        var tail = staged.Count > 0 && words.Length > 0 ? " " + words : words;
        sends.Add(new AgentSend(tail, true, staged.Count == 0 ? null : Placeholders(staged.Count)));
        return new AgentReply(words, Placeholders(staged.Count) + tail, staged, images: staged.Count, sends);
    }

    static string Placeholders(int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"[Image #{i}]"));
}
