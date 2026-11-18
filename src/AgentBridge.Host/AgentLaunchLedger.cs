using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.Host;

internal sealed class AgentLaunchLedger(string profile, Func<DateTimeOffset>? clock)
{
    const int MaxBytes = 4 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    internal T Use<T>(Func<AgentLimits, T> action)
    {
        var root = Path.Combine(profile, "launch-admissions");
        using var held = AgentAuditLog.Lock(root);
        var marker = Path.Combine(profile, "launch-admissions.initialized");
        var initialized = File.Exists(marker);
        var fresh = !Directory.Exists(root) && !initialized;
        if (!initialized)
        {
            using var stamp = PrivateFolder.CreateNew(marker);
            stamp.Write("v1\n"u8);
            stamp.Flush(flushToDisk: true);
        }
        PrivateFolder.Ensure(root, "the launch admission ledger", "no agent launch is admitted");
        var path = Path.Combine(root, "state.json");
        var limits = new AgentLimits(clock);
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > MaxBytes) { throw new InvalidDataException("Launch admission ledger is too large."); }
            limits.RestoreLaunches(JsonSerializer.Deserialize<AgentLimits.SavedLaunchState>(file, Json)
                ?? throw new InvalidDataException("Launch admission ledger is empty."));
        }
        catch (FileNotFoundException) when (fresh)
        {
            var (rows, complete) = GateAuditProjection.Load(profile);
            if (!complete || rows.Any(r => r.Action == "session.launch"
                && r.Principal.StartsWith("agent:", StringComparison.Ordinal)
                && r.Outcome is "reserved" or "started" or "failed" or "unconfirmed"))
            { throw new IOException("Existing agent launch evidence has no admission ledger; needs-person."); }
        }
        var result = action(limits);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(limits.SnapshotLaunches(), Json);
        if (bytes.Length > MaxBytes) { throw new IOException("Launch admission ledger is full."); }
        var partial = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = PrivateFolder.CreateNew(partial)) { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(partial, path, overwrite: true);
        }
        finally { File.Delete(partial); }
        return result;
    }
}
