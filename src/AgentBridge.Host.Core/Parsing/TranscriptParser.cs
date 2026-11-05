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
        // Every turn mark (TurnMarks) is a skim marker, so a line that may start a turn is parsed.
        if (!SkimWants(line.Span)) { return; }
        using var doc = TryParse(line);
        if (doc is { RootElement.ValueKind: JsonValueKind.Object } d)
        {
            _line = line;
            try { Count(d.RootElement); }
            finally { _line = default; }
        }
        else { Unreadable(line.Span); }
    }

    // The line being read, for the id of a turn its record starts (TurnIds): history gives the
    // turn the same id from the same bytes.
    ReadOnlyMemory<byte> _line;

    // The id of the turn the record being read starts, for activity.turn.
    protected void TurnStarts() => S.TurnId = _line.IsEmpty ? null : Conversation.TurnIds.Of(_line.Span);

    // What a record that may start a turn carries (Conversation.TurnMarks); null for an agent
    // without history.
    protected virtual Conversation.TurnMarks? Turns => null;

    bool MayStartTurn(ReadOnlySpan<byte> line) => Turns?.MayStart(line) == true;

    // A line that could not be read but may have started a turn: history shows that turn as one it
    // can't read, under that line's id, so what follows is not the last prompt's. Its id is not
    // known here, so the latest turn is unknown until a prompt that can be read.
    void Unreadable(ReadOnlySpan<byte> line)
    {
        if (MayStartTurn(line)) { S.TurnId = null; }
    }

    // A line longer than the tail reads, of which only its start was seen: as history decides
    // (HistoryReader.Examine), by that start alone.
    void ILineSink.Dropped(ReadOnlySpan<byte> head) => Unreadable(head);

    void ILineSink.Skipped()
    {
        // The latest turn may have begun in the bytes never read: its id is not known, and the
        // last one the skim saw is not it.
        S.TurnId = null;
        Skipped();
    }

    protected virtual bool SkimWants(ReadOnlySpan<byte> line)
    {
        foreach (var m in SkimMarkers)
        {
            if (line.IndexOf(m) >= 0) { return true; }
        }
        return false;
    }

    public void Parse(ReadOnlyMemory<byte> line)
    {
        using var doc = TryParse(line);
        if (doc is not { RootElement.ValueKind: JsonValueKind.Object } d)
        {
            Unreadable(line.Span);
            return;
        }
        var before = S.Effective;
        var at = Json.Time(d.RootElement, "timestamp");
        // Only events that can change what a client shows move updatedAt: hook runs,
        // usage records and model internals are a fifth of a Copilot transcript, and
        // counting them sent an upsert for every one.
        _line = line;
        bool visible;
        try { visible = Apply(d.RootElement, at); }
        finally { _line = default; }
        S.Observe(at, before, visible);
    }

    // Whatever the skim took as still going may have ended in the bytes never read.
    public virtual void Skipped() { }

    protected abstract IReadOnlyList<byte[]> SkimMarkers { get; }

    // What a skimmed line contributes: counts and first-seen facts, and the few facts that
    // outlive any window (a session's model, a background shell still running), never the
    // rest of its current state.
    protected abstract void Count(JsonElement e);

    // Applies one event and says whether it is one that can change what a client shows.
    protected abstract bool Apply(JsonElement e, DateTimeOffset? at);

    protected DateTimeOffset Now(DateTimeOffset? at) => at ?? Time.GetUtcNow();

    // A record whose text can't be decoded (Json.Decodable) is read as one that is not JSON, as
    // history reads it: it left the session unlisted, and must not give a turn an id here that
    // history can't (found in review of #292).
    static JsonDocument? TryParse(ReadOnlyMemory<byte> line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 256 }); }
        catch (JsonException) { return null; }
        if (Json.Decodable(doc.RootElement)) { return doc; }
        doc.Dispose();
        return null;
    }

    protected static byte[] Marker(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    protected void Prompted(string text, UserMessageFrom? from = null, string? label = null, string? key = null)
    {
        S.UserMessage = text;
        S.FirstUserMessage ??= text;
        S.UserMessageFrom = from;
        S.UserMessageLabel = label;
        S.UserMessageKey = key;
        TurnStarts();
    }

    // from and label: who wrote it, when not the person (#489). injected: context the agent was
    // given rather than a prompt anybody sent, which starts the turn but leaves userMessage alone.
    protected void UserPrompt(string text, DateTimeOffset? at, bool countsAsTurn = true, UserMessageFrom? from = null, string? label = null, bool injected = false, string? key = null, bool notice = false)
    {
        if (injected)
        {
            // No prompt: counted, it moved the turn a driver keys on, and an agent's reply lost its
            // edge to a skill loaded in the middle of its turn. A notice arriving while idle (a
            // shell's) starts a turn that is no prompt's, as Claude's notices do.
            if (notice && S.Status != SessionStatus.Working)
            {
                S.UncountedTurn = true;
                S.ForgetReasoning();
            }
            S.SetStatus(SessionStatus.Working);
            S.Activity(at);
            return;
        }
        Prompted(text, from, label, key);
        if (countsAsTurn) { S.Prompt(at); }
        S.UncountedTurn = !countsAsTurn;
        // A new message starts a new turn: last turn's reasoning would otherwise sit under
        // this turn's status.
        S.ForgetReasoning();
        S.SetStatus(SessionStatus.Working);
        S.Activity(at);
    }
}
