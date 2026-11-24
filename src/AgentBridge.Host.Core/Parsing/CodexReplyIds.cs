using System.Text.Json;
using AgentBridge.Host.Core.Conversation;

namespace AgentBridge.Host.Core.Parsing;

internal readonly record struct CodexReplyKey(string? Item, string? Turn, bool Ambiguous);

// Only explicit assistant-item ids join Codex's two streams; legacy text never supplies a key.
internal sealed class CodexReplyIds(bool known = false)
{
    readonly Dictionary<string, string> _parts = new(StringComparer.Ordinal);
    bool _known = known;
    string? _turn;

    public void Reset(bool known, string? turn = null)
    {
        _parts.Clear();
        (_known, _turn) = (known, turn);
    }

    public bool Validate(CodexReplyKey key)
    {
        if (!_known) { return false; }
        if (key.Ambiguous || key.Turn is { } turn && (_turn is not null ? turn != _turn : _parts.Count > 0))
        {
            Reset(false);
            return false;
        }
        _turn ??= key.Turn;
        return true;
    }

    public string? Match(CodexReplyKey key, string record, int index)
    {
        if (!Validate(key) || key.Item is not { } item) { return null; }
        if (_parts.TryGetValue(item, out var part)) { return part; }
        if (_parts.Count >= 4096) { Reset(false); return null; }
        return _parts[item] = TurnIds.Part(record, index);
    }

    public static CodexReplyKey Read(JsonElement e)
    {
        var ambiguous = false;
        var p = Object(e, "payload", ref ambiguous);
        var turn = String(p, "turn_id", ref ambiguous);
        var alternate = String(Object(p, "internal_chat_message_metadata_passthrough", ref ambiguous), "turn_id", ref ambiguous);
        ambiguous |= turn is not null && alternate is not null && turn != alternate;
        turn ??= alternate;
        var type = String(p, "type", ref ambiguous);
        var item = default(JsonElement);
        switch (String(e, "type", ref ambiguous))
        {
            case "event_msg" when type == "item_completed":
                var completed = Object(p, "item", ref ambiguous);
                if (String(completed, "type", ref ambiguous) == "AgentMessage") { item = completed; }
                break;
            case "response_item" when type == "message" && String(p, "role", ref ambiguous) == "assistant":
                item = p;
                break;
        }
        return new(String(item, "id", ref ambiguous), turn, ambiguous);
    }

    static JsonElement Object(JsonElement e, string name, ref bool ambiguous)
    {
        var value = Unique(e, name, ref ambiguous);
        if (value is null or { ValueKind: JsonValueKind.Null }) { return default; }
        if (value is { ValueKind: JsonValueKind.Object } obj) { return obj; }
        ambiguous = true;
        return default;
    }

    static string? String(JsonElement e, string name, ref bool ambiguous)
    {
        var field = Unique(e, name, ref ambiguous);
        if (field is null or { ValueKind: JsonValueKind.Null }) { return null; }
        if (field is not { ValueKind: JsonValueKind.String } value) { ambiguous = true; return null; }
        var text = Json.Text(value);
        if (text.Contains('\ufffd')) { ambiguous = true; return null; }
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static JsonElement? Unique(JsonElement e, string name, ref bool ambiguous)
    {
        if (e.ValueKind != JsonValueKind.Object) { return null; }
        JsonElement? value = null;
        foreach (var field in e.EnumerateObject())
        {
            if (!field.NameEquals(name)) { continue; }
            if (value is not null) { ambiguous = true; }
            value = field.Value;
        }
        return value;
    }
}
