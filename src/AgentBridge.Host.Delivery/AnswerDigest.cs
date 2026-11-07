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

    static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) { Write(writer, item); }
                writer.WriteEndArray();
                break;
            // 3 and 3.0 are the same answer; the divisor drops the trailing zeros.
            case JsonValueKind.Number when value.TryGetDecimal(out var number):
                writer.WriteNumberValue(number / 1.0000000000000000000000000000m);
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
