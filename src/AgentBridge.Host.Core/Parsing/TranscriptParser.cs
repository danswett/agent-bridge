using System.Text.Json;
using AgentBridge.Host.Core.Transcripts;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// Reduces one agent's transcript lines to a SessionState.
internal abstract class TranscriptParser(SessionState state, TimeProvider time) : ILineSink
{
    protected SessionState S { get; } = state;

    protected TimeProvider Time { get; } = time;

    public virtual void Reset() => S.Reset();

    // Skimmed lines are outside the parsed window: only a few cheap facts are taken
    // from them (the first prompt, counts), and only lines a byte search says can carry
    // them are parsed at all. Markers are bare quoted values, never `"type":"…"`: JSON
    // may put whitespace after the colon, and a whole record kind then went uncounted.
    // An unescaped quoted value can only be a JSON token, never text inside a string,
    // and Count checks the parsed type anyway.
    public void Skim(ReadOnlyMemory<byte> line)
    {
        if (!SkimMarkers.Any(m => line.Span.IndexOf(m) >= 0)) { return; }
        using var doc = TryParse(line);
        if (doc is { RootElement.ValueKind: JsonValueKind.Object } d) { Count(d.RootElement); }
    }

    public void Parse(ReadOnlyMemory<byte> line)
    {
        using var doc = TryParse(line);
        if (doc is not { RootElement.ValueKind: JsonValueKind.Object } d) { return; }
        var before = S.Effective;
        var at = Json.Time(d.RootElement, "timestamp");
        // Only events that can change what a client shows move updatedAt: hook runs,
        // usage records and model internals are a fifth of a Copilot transcript, and
        // counting them sent an upsert for every one.
        var visible = Apply(d.RootElement, at);
        S.Observe(at, before, visible);
    }

    protected abstract IReadOnlyList<byte[]> SkimMarkers { get; }

    // What a skimmed line contributes: counts and first-seen facts, never current state.
    protected abstract void Count(JsonElement e);

    // Applies one event and says whether it is one that can change what a client shows.
    protected abstract bool Apply(JsonElement e, DateTimeOffset? at);

    protected DateTimeOffset Now(DateTimeOffset? at) => at ?? Time.GetUtcNow();

    static JsonDocument? TryParse(ReadOnlyMemory<byte> line)
    {
        try { return JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 256 }); }
        catch (JsonException) { return null; }
    }

    protected static byte[] Marker(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    protected void UserPrompt(string text, DateTimeOffset? at, bool countsAsTurn = true)
    {
        S.UserMessage = text;
        S.FirstUserMessage ??= text;
        if (countsAsTurn) { S.Prompts++; }
        // A new message starts a new turn: last turn's reasoning would otherwise sit under
        // this turn's status.
        S.Reasoning = null;
        S.SetStatus(SessionStatus.Working);
        S.Activity(at);
    }
}
