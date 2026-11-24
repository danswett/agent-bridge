namespace AgentBridge.Host;

// diskLow (docs/design/agent-launch-scope.md section 11, "No fallback once HA launch is retired"): with under 1 GiB free on the profile's disk the
// host refuses to turn the fleet grant on and refuses fleet launches, with code diskLow, and host.status shows it. Failing closed on a full disk
// is accepted there so it is rare and visible; but free space that cannot be read is not a full disk, so it never refuses: the launch is allowed,
// the reason is said once in the log, and host.status says "disk unknown".
public sealed class ProfileDisk(string folder, Func<string, long?>? freeBytes = null, Action<string>? log = null)
{
    public const long MinFreeBytes = 1L << 30;
    readonly Func<string, long?> read = freeBytes ?? ReadFree;
    bool saidUnknown;

    static long? ReadFree(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    // Null when it cannot be read.
    public long? FreeBytes()
    {
        try { return read(folder); }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    // The refusal for a fleet grant or launch (null: allowed). Unknown free space allows it and says why once.
    public string? Refusal()
    {
        var free = FreeBytes();
        if (free is null)
        {
            if (!saidUnknown) { saidUnknown = true; log?.Invoke("free space on the profile's disk could not be read, so fleet launches are allowed; host.status shows disk unknown"); }
            return null;
        }
        saidUnknown = false;
        return free < MinFreeBytes ? "diskLow: under 1 GiB free on the profile's disk" : null;
    }

    public string State() => FreeBytes() switch { null => "unknown", < MinFreeBytes => "low", _ => "ok" };

    // What host.status shows: nothing when fine, so the field stays absent.
    public static string? Describe(long? free) => free is null ? "disk unknown"
        : free < MinFreeBytes ? $"diskLow: {free.Value / (1024 * 1024)} MiB free on the profile's disk (under 1 GiB); the fleet grant and fleet launches are refused" : null;
    public string? StatusLine() => Describe(FreeBytes());
}
