using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Host.Core;

// Tolerant reads from agent JSON. Agents add and reshape fields freely, and a missing or
// differently typed field must cost that one value, never the whole event: the old
// bridge lost nearly half of all assistant messages to one strict property read.
internal static class Json
{
    public static JsonElement? Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static JsonElement? Obj(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static JsonElement? Arr(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.Array } v ? v : null;

    public static string? Str(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.String } v ? Text(v) : null;

    // A JSON string's text, as a client shows it. Half a surrogate pair escaped ("\uD800", from an
    // MCP result or text cut mid-character) is no text .NET reads: GetString throws, and on a
    // transcript line that dropped the session and held discovery on every pass (Trust's sec-769
    // review, #921). It reads as U+FFFD; everything else as written.
    public static string Text(JsonElement v)
    {
        try { return v.GetString()!; }
        catch (InvalidOperationException) { }
        var raw = v.GetRawText();
        var b = new System.Text.StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] != '\\') { b.Append(raw[i]); continue; }
            var c = raw[++i];
            switch (c)
            {
                case 'b': b.Append('\b'); break;
                case 'f': b.Append('\f'); break;
                case 'n': b.Append('\n'); break;
                case 'r': b.Append('\r'); break;
                case 't': b.Append('\t'); break;
                case 'u': b.Append((char)int.Parse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                default: b.Append(c); break;
            }
        }
        for (var i = 0; i < b.Length; i++)
        {
            if (char.IsHighSurrogate(b[i]) && i + 1 < b.Length && char.IsLowSurrogate(b[i + 1])) { i++; continue; }
            if (char.IsSurrogate(b[i])) { b[i] = '\uFFFD'; }
        }
        return b.ToString();
    }

    // A record's bytes with every escaped half of a surrogate pair ("\uD800" with no "\uDC00" after
    // it, or a "\uDC00" alone) written as "\uFFFD", the replacement character, as a text decoder
    // reads it. JSON allows them, and .NET parses them, but GetString and a property's Name then
    // throw InvalidOperationException. Node writes them when it cuts a tool's output by UTF-16
    // units mid-emoji, so they turn up in fields nobody reads, and refusing the record lost a
    // tool's end and a question's answer with it (found in review of #292). The same six bytes;
    // the original is returned when there is nothing to repair, and a turn's id is always hashed
    // from the original. Bytes that are not UTF-8 (a raw surrogate, a character cut short) parse
    // as well, and throw the same way when read, so a line holding any is first decoded with the
    // replacement character for each, as any text decoder would.
    public static ReadOnlyMemory<byte> Repaired(ReadOnlyMemory<byte> line)
    {
        if (!System.Text.Unicode.Utf8.IsValid(line.Span))
        {
            line = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(line.Span));
        }
        var span = line.Span;
        if (span.IndexOf("\\ud"u8) < 0 && span.IndexOf("\\uD"u8) < 0) { return line; }
        byte[]? copy = null;
        var i = span.IndexOf((byte)'\\');
        while (i >= 0 && i + 1 < span.Length)
        {
            var next = i + 2;
            if (span[i + 1] == 'u' && Unit(span, i) is { } unit)
            {
                next = i + 6;
                if (unit is >= 0xD800 and <= 0xDBFF && i + 12 <= span.Length && span[i + 6] == '\\' && span[i + 7] == 'u' && Unit(span, i + 6) is >= 0xDC00 and <= 0xDFFF)
                {
                    next = i + 12;
                }
                else if (unit is >= 0xD800 and <= 0xDFFF)
                {
                    copy ??= line.ToArray();
                    "FFFD"u8.CopyTo(copy.AsSpan(i + 2));
                }
            }
            var after = next < span.Length ? span[next..].IndexOf((byte)'\\') : -1;
            i = after < 0 ? -1 : next + after;
        }
        return copy is null ? line : copy;
    }

    // The code unit of the \uXXXX escape at `at`, if its four digits are hex.
    static int? Unit(ReadOnlySpan<byte> s, int at)
    {
        if (at + 6 > s.Length) { return null; }
        var unit = 0;
        for (var k = at + 2; k < at + 6; k++)
        {
            var digit = s[k] switch
            {
                >= (byte)'0' and <= (byte)'9' => s[k] - '0',
                >= (byte)'a' and <= (byte)'f' => s[k] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => s[k] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0) { return null; }
            unit = unit * 16 + digit;
        }
        return unit;
    }

    // True when anything in e, a name or a value at any depth, is text Text would have to rewrite
    // (half a surrogate pair) or already holds U+FFFD, which a rewritten character reads as. That
    // tolerant read is for display only: an approval's identity, its escape detection and the command
    // it shows are all worked out from the text, so two commands that differ only in which lone
    // surrogate they hold, or in holding U+FFFD, would share an id and lose their escaped flag
    // (Engineering's review of #921). Whatever asks about such text is answered at the machine.
    public static bool IsLossy(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                try { return e.GetString()!.Contains('\uFFFD'); }
                catch (InvalidOperationException) { return true; }
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) { if (IsLossy(item)) { return true; } }
                return false;
            case JsonValueKind.Object:
                foreach (var property in e.EnumerateObject())
                {
                    try { if (property.Name.Contains('\uFFFD')) { return true; } }
                    catch (InvalidOperationException) { return true; }
                    if (IsLossy(property.Value)) { return true; }
                }
                return false;
            default:
                return false;
        }
    }

    public static int? Int(JsonElement e, string name) => Get(e, name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt32(out var i) => i,
        { ValueKind: JsonValueKind.String } v when int.TryParse(Text(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    public static bool? Bool(JsonElement e, string name) => Get(e, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    public static DateTimeOffset? Time(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.String } v ? ParseTime(Text(v)) : null;

    public static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;

    // An element that outlives its document, for Field.Default.
    public static JsonElement Detach(JsonElement e) => e.Clone();

    // Whether e nests more than levels deep: a scalar nests 0, [] and {} 1, [[]] 2.
    public static bool NestsDeeper(JsonElement e, int levels)
    {
        if (e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) { return false; }
        if (levels <= 0) { return true; }
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                if (NestsDeeper(item, levels - 1)) { return true; }
            }
            return false;
        }
        foreach (var property in e.EnumerateObject())
        {
            if (NestsDeeper(property.Value, levels - 1)) { return true; }
        }
        return false;
    }
}
