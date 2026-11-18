using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Protocol;

public static class ReleaseVersions
{
    public static bool TryRun(string? version, out long run)
    {
        run = 0;
        if (version is null || !version.StartsWith("0.1.", StringComparison.Ordinal)) { return false; }
        var number = version.AsSpan(4);
        return number.Length is > 0 and <= 19 && long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out run)
            && number.SequenceEqual(run.ToString(CultureInfo.InvariantCulture));
    }
}

// Relay control frames stay untrusted JSON: successful parsing grants no update authority.
public sealed class ReleaseHints
{
    long dropped;
    public long Dropped => Interlocked.Read(ref dropped);

    public static bool IsCapable(JsonElement hello) => hello.ValueKind == JsonValueKind.Object
        && hello.EnumerateObject().Count(p => p.NameEquals("releaseHint")) == 1
        && hello.TryGetProperty("releaseHint", out var flag) && flag.ValueKind == JsonValueKind.True;

    public JsonElement? Read(byte[] frame, DateTimeOffset now, Action<string> log)
    {
        var result = Parse(frame, now, out var field);
        if (result is null)
        {
            Interlocked.Increment(ref dropped);
            log(field);
        }
        return result;
    }

    static JsonElement? Parse(byte[] frame, DateTimeOffset now, out string field)
    {
        field = "frame";
        if (frame.Length >= 256) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name is not ("t" or "tag" or "channel" or "seenAt")) { return null; }
                if (!names.Add(p.Name)) { field = p.Name; return null; }
            }
            field = "t";
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "release.hint") { return null; }
            field = "tag";
            if (!root.TryGetProperty("tag", out var tag) || tag.ValueKind != JsonValueKind.String
                || tag.GetString() is not { Length: > 1 } text || text[0] != 'v' || !ReleaseVersions.TryRun(text[1..], out _)) { return null; }
            field = "channel";
            if (!root.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.String
                || channel.GetString() is not ("win" or "win-arm64")) { return null; }
            field = "seenAt";
            if (!root.TryGetProperty("seenAt", out var seenAt) || seenAt.ValueKind != JsonValueKind.String
                || seenAt.GetString() is not { Length: > 0 and < 40 } stamp || !stamp.EndsWith('Z')
                || !seenAt.TryGetDateTimeOffset(out var at) || at.Offset != TimeSpan.Zero
                || at - now > TimeSpan.FromMinutes(5)) { return null; }
            return root.Clone();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }
}
