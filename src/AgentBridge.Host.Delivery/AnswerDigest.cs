using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// What an answer says, as a digest, so that the same answer sent again under another request
// UUID can be told from a different one (#1093). Only what is typed counts: the fields and the
// text, with an object's properties in a fixed order and a number in one form. The request's
// UUID, its progress flag and any signed confirmation are how it was sent, not what it says.
// The digest is kept in the host's memory beside the prompt it answered, never logged or sent.
internal static class AnswerDigest
{
    public static byte[] Of(AnswerRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (request.Text is not null) { writer.WriteString("text", request.Text); }
            writer.WritePropertyName("fields");
            writer.WriteStartObject();
            foreach (var (name, value) in (request.Fields ?? new Dictionary<string, JsonElement>()).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                Write(writer, value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return SHA256.HashData(stream.ToArray());
    }

    // Every value is written as a tagged pair, so a string that reads like a number, or an object
    // shaped like a tag, can never stand for another kind of value.
    static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        writer.WriteStartArray();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStringValue("o");
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStringValue("a");
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) { Write(writer, item); }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                // Compared exactly: 3, 3.0 and 3e0 are one answer, and 1e-29 and 2e-29 are two.
                // Read through decimal both were 0 (#1100).
                var raw = value.GetRawText();
                if (Canonical(raw) is { } exact) { writer.WriteStringValue("n"); writer.WriteStringValue(exact); }
                else { writer.WriteStringValue("r"); writer.WriteStringValue(raw); }
                break;
            case JsonValueKind.String:
                writer.WriteStringValue("s");
                writer.WriteStringValue(value.GetString());
                break;
            default:
                writer.WriteStringValue(value.ValueKind.ToString());
                break;
        }
        writer.WriteEndArray();
    }

    // A JSON number's exact value in one form, for any spelling of it: its significant digits
    // (leading and trailing zeros dropped) and the power of ten that scales them, so 100e-2, 1
    // and 0.1e1 read alike, and zero is zero whatever its sign or exponent. The number is never
    // expanded: only the exponent is added to, as an integer of any size, so 1e1000000001 and
    // 10e1000000000 are one value and no power of ten is ever built. The planner's
    // CopilotFieldSchema.ExactNumber gives the same form wherever it gives one; it declines
    // exponents beyond a billion, where this does not, since a resend of an answer must be
    // recognised however it is spelled. Null only for text that is not a JSON number.
    internal static string? Canonical(string raw)
    {
        var s = raw.AsSpan();
        var negative = s.Length > 0 && s[0] == '-';
        if (negative) { s = s[1..]; }
        var e = s.IndexOfAny('e', 'E');
        var mantissa = e >= 0 ? s[..e] : s;
        var dot = mantissa.IndexOf('.');
        var whole = dot >= 0 ? mantissa[..dot] : mantissa;
        var fraction = dot >= 0 ? mantissa[(dot + 1)..] : ReadOnlySpan<char>.Empty;
        if (whole.Length == 0 || !All(whole) || (dot >= 0 && (fraction.Length == 0 || !All(fraction)))) { return null; }
        BigInteger exponent = 0;
        if (e >= 0)
        {
            var written = s[(e + 1)..];
            var exponentNegative = written.Length > 0 && written[0] == '-';
            if (written.Length > 0 && (written[0] == '-' || written[0] == '+')) { written = written[1..]; }
            if (written.Length == 0 || !All(written)) { return null; }
            var trimmed = written.TrimStart('0');
            exponent = trimmed.Length == 0 ? BigInteger.Zero : BigInteger.Parse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture);
            if (exponentNegative) { exponent = -exponent; }
        }
        var digits = string.Concat(whole, fraction).AsSpan().TrimStart('0');
        if (digits.Length == 0) { return "0"; }
        var significant = digits.TrimEnd('0');
        exponent += digits.Length - significant.Length - fraction.Length;
        return $"{(negative ? "-" : "")}{significant}e{exponent.ToString(CultureInfo.InvariantCulture)}";
    }

    static bool All(ReadOnlySpan<char> digits)
    {
        foreach (var c in digits) { if (c is < '0' or > '9') { return false; } }
        return true;
    }
}
