using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Protocol;

// Shared ingress/read validation; only this closed projection can become extension data.
public sealed class SessionEvidence
{
    public static IReadOnlyCollection<string> Members { get; } = Array.AsReadOnly(new[]
        { "stale", "attention", "openAsks", "unsentLength", "unsentHash", "observedAt", "lastReplyAt", "provenance", "senderVerified" });
    readonly Dictionary<string, JsonElement> fields;
    SessionEvidence(Dictionary<string, JsonElement> fields) => this.fields = fields;
    public Dictionary<string, JsonElement> Copy() => new(fields);

    public static SessionEvidence? Read(JsonElement session, bool futureAttention = false)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in session.EnumerateObject())
        {
            if (!Members.Contains(p.Name)) { continue; }
            var v = p.Value;
            var valid = p.Name switch
            {
                "stale" => Boolean(v),
                "openAsks" or "unsentLength" => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0,
                "unsentHash" => Hash(v),
                "observedAt" or "lastReplyAt" => v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParseExact(v.GetString(),
                    ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
                "senderVerified" => Boolean(v) || Text(v, "unknown"),
                "attention" => Only(v, "kind") && v.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                    && k.GetString() is { Length: > 0 and <= 32 } && (futureAttention || Kind(k)),
                "provenance" => Only(v, "source", "sendMarker", "unsentHash") && v.TryGetProperty("source", out var source)
                    && Text(source, "host-observed", "relay-supplied") && (!v.TryGetProperty("sendMarker", out var marker) || Boolean(marker))
                    && (!v.TryGetProperty("unsentHash", out var hash) || Hash(hash)),
                _ => false,
            };
            if (!valid || !result.TryAdd(p.Name, v.Clone())) { throw new InvalidDataException("refused: invalid session evidence"); }
            if (p.Name == "attention" && !Kind(v.GetProperty("kind")))
            {
                result[p.Name] = JsonSerializer.SerializeToElement(new { kind = "unknown" });
            }
        }
        if (result.TryGetValue("provenance", out var proof) && proof.TryGetProperty("unsentHash", out var proofHash)
            && result.TryGetValue("unsentHash", out var composerHash) && proofHash.GetString() != composerHash.GetString())
        {
            throw new InvalidDataException("refused: inconsistent session evidence");
        }
        return result.Count == 0 ? null : new(result);
    }

    static bool Boolean(JsonElement v) => v.ValueKind is JsonValueKind.True or JsonValueKind.False;
    static bool Text(JsonElement v, params string[] choices) => v.ValueKind == JsonValueKind.String && choices.Contains(v.GetString(), StringComparer.Ordinal);
    static bool Kind(JsonElement v) => Text(v, "none", "question", "approval", "trust", "permission", "unknown");
    static bool Hash(JsonElement v) => v.ValueKind == JsonValueKind.String && v.GetString() is { Length: 64 } h
        && h.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    static bool Only(JsonElement v, params string[] names)
    {
        if (v.ValueKind != JsonValueKind.Object) { return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return v.EnumerateObject().All(p => names.Contains(p.Name, StringComparer.Ordinal) && seen.Add(p.Name));
    }
}
