using System.Text.Json;
using AgentBridge.Host.Migration;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal static class AgentFleetLaunchGrants
{
    const string FileName = "agent-fleet-launch-grant.json";

    // HostControl holds its settings gate over this read/change/write transaction.
    internal static AgentFleetLaunchGrantState Handle(string folder, JsonElement data, TimeProvider time)
    {
        if (!Fields(data, "action", "principal", "scope", "role", "expiresAt", "grant"))
        { throw Refused("invalid fleet-launch grant request"); }
        var current = Read(folder, time);
        var now = time.GetUtcNow();
        AgentFleetLaunchGrant next;
        switch (Text(data, "action"))
        {
            case "read" when Fields(data, "action"): return current;
            case "issue" when Fields(data, "action", "principal", "scope", "role", "expiresAt"):
                if (Text(data, "principal") != "fleet-launcher" || Text(data, "scope") != "session.launch"
                    || Text(data, "role") != "fleet")
                { throw Refused("only session.launch for the fleet-launcher principal and fleet roles may be granted"); }
                if (!data.TryGetProperty("expiresAt", out var expiry) || expiry.ValueKind != JsonValueKind.String
                    || !expiry.TryGetDateTimeOffset(out var until) || until <= now)
                { throw Refused("a future expiresAt timestamp is required"); }
                if (current.State == "active") { throw Refused("revoke the active fleet-launch grant before issuing another"); }
                next = new(Guid.NewGuid().ToString("D"), "fleet-launcher", "session.launch", "fleet", now, until);
                break;
            case "revoke" when Fields(data, "action", "grant"):
                if (current.Grant is not { } grant || Text(data, "grant") != grant.Id)
                { throw Refused("revoke must identify the exact existing grant"); }
                next = grant with { RevokedAt = grant.RevokedAt ?? now };
                break;
            default: throw Refused("expected read, issue or revoke");
        }
        try { DurableFile.Write(Path.Combine(folder, FileName), JsonSerializer.SerializeToUtf8Bytes(next, ProtocolJson.Options)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw Refused("the fleet-launch grant could not be saved", e); }
        return State(next, now);
    }

    internal static AgentFleetLaunchGrantState Read(string folder, TimeProvider time)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, FileName)));
            if (!Fields(json.RootElement, "id", "principal", "scope", "role", "issuedAt", "expiresAt", "revokedAt"))
            { throw Refused("the stored fleet-launch grant has invalid fields"); }
            var grant = json.RootElement.Deserialize<AgentFleetLaunchGrant>(ProtocolJson.Options);
            var now = time.GetUtcNow();
            if (grant is null || !Guid.TryParseExact(grant.Id, "D", out var id) || id == Guid.Empty
                || grant.Principal != "fleet-launcher" || grant.Scope != "session.launch" || grant.Role != "fleet"
                || grant.IssuedAt == default || grant.IssuedAt > now || grant.ExpiresAt <= grant.IssuedAt
                || grant.RevokedAt < grant.IssuedAt || grant.RevokedAt > now)
            { throw Refused("the stored fleet-launch grant is invalid or the clock moved backwards"); }
            return State(grant, now);
        }
        catch (FileNotFoundException) { return new("none"); }
        catch (DirectoryNotFoundException) { return new("none"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { throw Refused("the fleet-launch grant could not be read safely", e); }
    }

    static AgentFleetLaunchGrantState State(AgentFleetLaunchGrant grant, DateTimeOffset now) =>
        new(grant.RevokedAt is not null ? "revoked" : grant.ExpiresAt <= now ? "expired" : "active", grant);
    static string? Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static bool Fields(JsonElement data, params string[] allowed) => data.ValueKind == JsonValueKind.Object
        && data.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal))
        && !data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1);
    static InvalidOperationException Refused(string reason, Exception? inner = null) => new("refused: " + reason, inner);
}
