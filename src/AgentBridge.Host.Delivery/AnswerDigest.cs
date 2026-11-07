using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Core.Parsing;
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
                // Compared exactly, as the Copilot planner compares numbers: 3, 3.0 and 3e0 are
                // one answer, and 1e-29 and 2e-29 are two. Read through decimal both were 0 (#1100).
                // A number that cannot be put in that form is kept as written.
                var raw = value.GetRawText();
                if (CopilotFieldSchema.ExactNumber(raw) is { } exact) { writer.WriteStringValue("n"); writer.WriteStringValue(exact); }
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
}
