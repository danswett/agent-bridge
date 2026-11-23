using System.Text.RegularExpressions;
using AgentBridge.Host.Delivery.Copilot;

namespace AgentBridge.Host.Delivery.AgentReplies;

// One send of a reply, and what the input box must show just before it: null for the first,
// which goes into the empty box as first read; otherwise the box as the sends before it left
// it. Each is held to the box's rows as read (KeySend.Expect).
// PauseMs: how long the worker waits after typing it, before the next send.
public sealed record AgentSend(string Text, bool Enter, string? After, int PauseMs = 0);

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

    // box: the rows the send is held to, null for a send that follows another with no wait between (a chunk).
    public static KeySend Key(AgentSend send, IReadOnlyList<ScreenRow>? box) =>
        new(send.Text, send.Enter, send.Enter ? CopilotPlanner.ReplyEnterDelayMs : 0, send.PauseMs, box);

    // Claude Code takes a long burst of typed keys for a paste: one of 1000 characters arrived as about 300 typed and a
    // "[Pasted text]" block, and one of 4514 lost its head (probed in a session of the builder's own, then seen live).
    // Sent as chunks this long, this far apart, 4500 characters arrive whole in about 3 s. Claude only: Copilot and
    // Codex take the burst, and a short reply is still one send.
    public const int ChunkUnits = 100;
    public const int ChunkPauseMs = 40;

    // The line as sends of at most ChunkUnits UTF-16 units, none ending inside a surrogate pair; only the last presses Enter.
    static List<AgentSend> Chunks(string line)
    {
        var sends = new List<AgentSend>();
        for (var i = 0; i < line.Length;)
        {
            var n = Math.Min(ChunkUnits, line.Length - i);
            if (i + n < line.Length && char.IsHighSurrogate(line[i + n - 1])) { n--; }
            sends.Add(new AgentSend(line.Substring(i, n), false, null, ChunkPauseMs));
            i += n;
        }
        if (sends.Count == 0) { sends.Add(new AgentSend("", true, null)); }
        else { sends[^1] = sends[^1] with { Enter = true, PauseMs = 0 }; }
        return sends;
    }

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
        return new AgentReply(line, line, staged, images: 0, Chunks(line));
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
