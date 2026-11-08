using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace AgentBridge.Roster;

// I-JSON (RFC 7493) as the roster requires it (§3.2, "Strict parsing"): valid UTF-8 without a
// byte-order mark, valid JSON, no duplicate member names, no lone surrogates, at most 16 levels
// deep. Everything is checked before any member is read, in a fixed order (syntax first, then
// each member in document order: its name, whether it repeats one, then its value), which
// pwa/js/roster/strict-json.js follows, so one defect gives one refusal everywhere.
internal static class StrictJson
{
    public const int MaxDepth = 16;

    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8, string what)
    {
        var span = utf8.Span;
        if (!Utf8.IsValid(span) || span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { throw new RosterFormatException(Refusal.Json, what); }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException) { throw new RosterFormatException(Refusal.Json, what); }
        try { Walk(doc.RootElement, what); }
        catch
        {
            doc.Dispose();
            throw;
        }
        return doc;
    }

    static void Walk(JsonElement e, string what)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in e.EnumerateObject())
                    {
                        var name = Text(() => p.Name, what);
                        if (!seen.Add(name)) { throw new RosterFormatException(Refusal.Duplicate, $"{what}: {name}"); }
                        Walk(p.Value, what);
                    }
                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) { Walk(item, what); }
                break;
            case JsonValueKind.String:
                Text(e.GetString, what);
                break;
        }
    }

    // A decoded string, refused if an escape left a surrogate without its pair.
    static string Text(Func<string?> read, string what)
    {
        string text;
        try { text = read() ?? ""; }
        catch (InvalidOperationException) { throw new RosterFormatException(Refusal.Json, what); }
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; }
            else if (char.IsSurrogate(text[i])) { throw new RosterFormatException(Refusal.Json, what); }
        }
        return text;
    }

    // Members not in `allowed` are refused, the first in document order; then the first of
    // `required`, in that order, that is absent.
    public static void Members(JsonElement obj, IReadOnlyCollection<string> allowed, IEnumerable<string> required, string what)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!allowed.Contains(p.Name)) { throw new RosterFormatException(Refusal.Unknown, $"{what}.{p.Name}"); }
        }
        foreach (var name in required)
        {
            if (!obj.TryGetProperty(name, out _)) { throw new RosterFormatException(Refusal.Missing, $"{what}.{name}"); }
        }
    }

    public static JsonElement Object(JsonElement e, string what) =>
        e.ValueKind == JsonValueKind.Object ? e : throw new RosterFormatException(Refusal.Type, what);

    public static string String(JsonElement e, string what) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new RosterFormatException(Refusal.Type, what);

    public static bool Bool(JsonElement e, string what) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new RosterFormatException(Refusal.Type, what),
    };

    // A non-negative integer below 2³¹, written as its plain decimal digits: no sign, fraction,
    // exponent or leading zero. Null for any other number; Type for anything not a number.
    public static int? Integer(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Number) { throw new RosterFormatException(Refusal.Type, what); }
        var raw = e.GetRawText();
        if (raw.Length is 0 or > 10 || (raw.Length > 1 && raw[0] == '0')) { return null; }
        foreach (var c in raw)
        {
            if (c is < '0' or > '9') { return null; }
        }
        var value = long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        return value < (1L << 31) ? (int)value : null;
    }

    public static int UnicodeScalars(string text)
    {
        var n = 0;
        foreach (var _ in text.EnumerateRunes()) { n++; }
        return n;
    }

    public static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    // A C0 or C1 control, or DEL.
    public static bool HasControl(string text)
    {
        foreach (var c in text)
        {
            if (c < 0x20 || c is >= (char)0x7F and <= (char)0x9F) { return true; }
        }
        return false;
    }
}
