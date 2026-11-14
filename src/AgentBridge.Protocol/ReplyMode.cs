using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Protocol;

[UnknownAs(Steer)]
public enum ReplyMode { Steer, Queue }

[UnknownAs(Typed)]
public enum ReplyStage { Typed, Recorded, Queued }

// Requests must not use the client-side enum fallback: an unknown mode must never steer.
public sealed class ReplyRequestModeConverter : JsonConverter<ReplyMode?>
{
    public override ReplyMode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString() switch
            {
                "queue" => ReplyMode.Queue,
                "steer" => ReplyMode.Steer,
                _ => throw new JsonException("reply mode must be queue or steer"),
            },
            _ => throw new JsonException("reply mode must be queue or steer"),
        };

    public override void Write(Utf8JsonWriter writer, ReplyMode? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
