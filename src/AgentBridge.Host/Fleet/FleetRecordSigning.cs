using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using AgentBridge.Roster;

namespace AgentBridge.Host.Fleet;

public static class FleetRecordSigning
{
    public const string Context = "agent-bridge fleet registry v1";

    public static byte[] CanonicalBytes(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("bad-record"); }
        return JsonSerializer.SerializeToUtf8Bytes(Sort(record, true), ProtocolJson.Options);

        static JsonNode? Sort(JsonElement value, bool root = false)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var members = value.EnumerateObject().ToArray();
                if (members.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != members.Length)
                { throw new InvalidDataException("bad-record"); }
                return new JsonObject(members.Where(p => !root || p.Name != "sig").OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Name, Sort(p.Value))));
            }
            return value.ValueKind switch
            {
                JsonValueKind.Array => new JsonArray(value.EnumerateArray().Select(v => Sort(v)).ToArray()),
                JsonValueKind.String => JsonValue.Create(value.GetString()),
                JsonValueKind.Number => value.TryGetInt64(out var n) ? JsonValue.Create(n) : throw new InvalidDataException("bad-record"),
                JsonValueKind.True or JsonValueKind.False => JsonValue.Create(value.GetBoolean()),
                JsonValueKind.Null => null,
                _ => throw new InvalidDataException("bad-record"),
            };
        }
    }

    public static byte[] SignedBytes(JsonElement record) => Framing.F(Framing.Label(Context), CanonicalBytes(record));

    public static JsonElement Sign(JsonElement record, Func<byte[], byte[]> signer)
    {
        var result = JsonNode.Parse(record.GetRawText())!.AsObject();
        result["sig"] = Codec.Base64Url(P256.Normalize(signer(SignedBytes(record))));
        return JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
    }

    public static JsonElement SignExisting(JsonElement record, string profile)
    {
        if (OperatingSystem.IsWindows())
        {
            try { using var key = HostSignKey.OpenExisting(profile); return Sign(record, key.Sign); }
            catch (InvalidDataException e) when (e.Message == "missing-key") { }
        }
        return JsonSerializer.Deserialize<JsonElement>(CanonicalBytes(record));
    }

    // Schema, roster authority, freshness and replay ordering belong to the reader.
    public static bool Verify(JsonElement record, byte[] hostSign)
    {
        if (record.ValueKind != JsonValueKind.Object || !P256.IsPoint(hostSign)
            || !record.TryGetProperty("sig", out var sig) || sig.ValueKind != JsonValueKind.String) { return false; }
        if (!Codec.TryBase64Url(sig.GetString()!, out var der) || P256.ReadDer(der) is not { } rs || !P256.IsLowS(rs.S)) { return false; }
        try { return P256.Verify(hostSign, SignedBytes(record), der); }
        catch (InvalidDataException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
