using System.Text.Json;
using System.Text.Unicode;

namespace AgentBridge.AgentEnvelope;

// I-JSON (RFC 7493) as #391 §3.2 requires it: valid UTF-8 without a byte-order mark, valid JSON,
// no duplicate member names, no lone surrogates, at most 16 levels deep. All of it is checked
// before any member is read, so one defect gives one refusal.
internal static class StrictJson
{
    public const int MaxDepth = 16;

    // The largest integer a JSON number can carry exactly in every implementation (2⁵³ − 1).
    public const long MaxInteger = (1L << 53) - 1;

    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8, string where)
    {
        var span = utf8.Span;
        if (!Utf8.IsValid(span) || span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { throw new EnvelopeException(Refusal.Json, where); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException) { throw new EnvelopeException(Refusal.Json, where); }
        try { Walk(doc.RootElement, where); }
        catch
        {
            doc.Dispose();
            throw;
        }
        return doc;
    }

    static void Walk(JsonElement e, string where)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in e.EnumerateObject())
                    {
                        var name = Text(() => p.Name, where);
                        if (!seen.Add(name)) { throw new EnvelopeException(Refusal.Duplicate, $"{where}.{name}"); }
                        Walk(p.Value, where);
                    }
                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) { Walk(item, where); }
                break;
            case JsonValueKind.String:
                Text(e.GetString, where);
                break;
        }
    }

    static string Text(Func<string?> read, string where)
    {
        string text;
        try { text = read() ?? ""; }
        catch (InvalidOperationException) { throw new EnvelopeException(Refusal.Json, where); }
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; }
            else if (char.IsSurrogate(text[i])) { throw new EnvelopeException(Refusal.Json, where); }
        }
        return text;
    }

    // A member not in `allowed` is refused first (the first in document order), then the first of
    // `required`, in that order, that is absent.
    public static void Members(JsonElement obj, IReadOnlyCollection<string> allowed, IEnumerable<string> required, string where)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!allowed.Contains(p.Name)) { throw new EnvelopeException(Refusal.Unknown, $"{where}.{p.Name}"); }
        }
        foreach (var name in required)
        {
            if (!obj.TryGetProperty(name, out _)) { throw new EnvelopeException(Refusal.Missing, $"{where}.{name}"); }
        }
    }

    public static JsonElement Object(JsonElement e, string where) =>
        e.ValueKind == JsonValueKind.Object ? e : throw new EnvelopeException(Refusal.Type, where);

    public static JsonElement.ArrayEnumerator Array(JsonElement e, string where) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : throw new EnvelopeException(Refusal.Type, where);

    public static string String(JsonElement e, string where) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new EnvelopeException(Refusal.Type, where);

    // A non-negative integer written as plain decimal digits: no sign, fraction, exponent or
    // leading zero, at most 2⁵³ − 1.
    public static long Integer(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Number) { throw new EnvelopeException(Refusal.Type, where); }
        var raw = e.GetRawText();
        if (raw.Length is 0 or > 16 || (raw.Length > 1 && raw[0] == '0')) { throw new EnvelopeException(Refusal.Value, where); }
        foreach (var c in raw)
        {
            if (c is < '0' or > '9') { throw new EnvelopeException(Refusal.Value, where); }
        }
        var value = long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        return value <= MaxInteger ? value : throw new EnvelopeException(Refusal.Value, where);
    }
}
