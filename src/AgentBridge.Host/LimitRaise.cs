using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

// A temporary raise of the FLEET caps (#2768). The owner writes `agents.limits.raise` in the host's config at the machine; nothing on a wire, no CLI and nothing an
// agent can call sets it. The host stamps the first time it reads a given block (a digest and the time, in the profile folder), and the raise holds for 4 hours from
// that stamp: the expiry is STORED, so a restart keeps it, repeated reads never extend it, an edited block is a new block with a new 4 hours, and the config cannot
// carry a longer expiry because none is read from it. Anything that goes wrong (the stamp cannot be written, a clock that moved back) leaves the base limits.
internal static class LimitRaise
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);
    static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    const string FileName = "limit-raise.json";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    sealed record Stamp(string Digest, DateTimeOffset StartedAt, bool Lapsed);

    public static FleetLimits Effective(string folder, AgentLaunchLimits limits, DateTimeOffset now, AgentAuditLog? audit)
    {
        var basis = new FleetLimits(limits.FleetPerHour, limits.LiveFleetWorkers);
        if (!limits.HasRaise) { return basis; }
        var path = Path.Combine(folder, FileName);
        var digest = Digest(limits);
        var stamp = Read(path);
        if (stamp is null || stamp.Digest != digest)
        {
            stamp = new(digest, now, false);
            if (!Write(path, stamp)) { return basis; }
            Audit(audit, "started", $"{Describe(limits)} until {stamp.StartedAt + Lifetime:yyyy-MM-dd HH:mm} UTC");
        }
        if (now < stamp.StartedAt - ClockSkew) { return basis; }
        var until = stamp.StartedAt + Lifetime;
        if (now >= until)
        {
            if (!stamp.Lapsed && Write(path, stamp with { Lapsed = true })) { Audit(audit, "lapsed", Describe(limits)); }
            return basis;
        }
        return new(Math.Max(basis.PerHour, limits.FleetPerHourRaise ?? 0), Math.Max(basis.Live, limits.LiveFleetWorkersRaise ?? 0),
            $"temporary raise until {until:HH:mm} UTC");
    }

    static string Describe(AgentLaunchLimits l) =>
        string.Join(", ", new[] { l.FleetPerHourRaise is { } h ? $"fleetPerHour {h}" : null, l.LiveFleetWorkersRaise is { } w ? $"liveFleetWorkers {w}" : null }.Where(x => x is not null));

    static string Digest(AgentLaunchLimits l) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{l.FleetPerHourRaise}|{l.LiveFleetWorkersRaise}")));

    static void Audit(AgentAuditLog? audit, string outcome, string reason)
    {
        // Best effort: an unwritable log never widens or narrows the raise (the stamp is the record of when it started); the audit row is the owner's trail.
        try { audit?.Append(new AgentAuditEntry("local", Guid.NewGuid().ToString("D"), "host:config", [], "fleet.limit.raise", "agents.limits.raise", outcome, reason)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    static Stamp? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Stamp>(File.ReadAllText(path), Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static bool Write(string path, Stamp stamp)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(stamp, Json));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally { try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}
