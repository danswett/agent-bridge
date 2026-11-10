using System.Text.Json;

namespace AgentBridge.Host.Core.Load;

// The machine-load record the fleet reads (v1): machine totals only, so no process name, path,
// session id or title can be in it. A null field is unknown, never 0. ObservedAt is when the
// sampler took the reading, and it alone decides whether the record is stale.
public sealed record FleetLoadRecord(
    int Version, string Machine, DateTimeOffset ObservedAt,
    double? CpuPercent, double? CpuPeakPercent, int? Cores, long? AvailableMemoryMiB,
    int? Sessions, int? RunQueue, double? RunQueueAvg, int? RunQueueMax)
{
    public const int CurrentVersion = 1;

    // The v1 key names, in the order they are written.
    public static readonly IReadOnlyList<string> Keys =
    [
        "version", "machine", "observedAt", "cpuPercent", "cpuPeakPercent", "cores", "availableMemoryMiB",
        "sessions", "runQueue", "runQueueAvg", "runQueueMax",
    ];

    // Sessions is the host's live-session count, which the sampler does not know.
    public static FleetLoadRecord From(ProcessLoadReport report, string machine, int? sessions)
    {
        var m = report.Machine;
        return new FleetLoadRecord(CurrentVersion, machine, report.SampledAt,
            m.WindowCpuMean, m.WindowCpuPeak, m.Processors > 0 ? m.Processors : null,
            m.AvailableMemoryBytes is { } b ? b / (1024 * 1024) : null,
            sessions, m.RunQueue, m.RunQueueAvg, m.RunQueueMax);
    }
}

// The limits the fleet judges a record against (docs/fleet-load-limits.json). A record with an
// unknown figure fails when NullFails is set: not knowing is not a pass.
public sealed record FleetLoadLimits(double MaxCpuPercent, long MinAvailableMemoryMiB, double MaxRunQueuePerCore, int MaxAgeSeconds, bool NullFails)
{
    // Throws FormatException naming the field, so a bad file fails its test with a reason.
    public static FleetLoadLimits Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var limits = new FleetLoadLimits(
            Number(root, "maxCpuPercent"), (long)Number(root, "minAvailableMemoryMiB"), Number(root, "maxRunQueuePerCore"),
            (int)Number(root, "maxAgeSeconds"), root.TryGetProperty("nullFails", out var n) && n.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? n.GetBoolean() : throw new FormatException("nullFails must be true or false"));
        if (limits.MaxCpuPercent is <= 0 or > 100) { throw new FormatException("maxCpuPercent must be above 0 and at most 100"); }
        if (limits.MinAvailableMemoryMiB <= 0) { throw new FormatException("minAvailableMemoryMiB must be above 0"); }
        if (limits.MaxRunQueuePerCore < 0) { throw new FormatException("maxRunQueuePerCore must not be negative"); }
        if (limits.MaxAgeSeconds <= 0) { throw new FormatException("maxAgeSeconds must be above 0"); }
        return limits;
    }

    static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw new FormatException($"{name} must be a number");
}
