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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    // The space this user may use at the folder itself (nearest existing parent), so a UNC share or a mount point reads as itself and not as
    // unknown, and a disk quota counts. Elsewhere than Windows, the mount the folder is on.
    static long? ReadFree(string path)
    {
        try
        {
            var directory = Path.GetFullPath(path);
            while (!Directory.Exists(directory) && Path.GetDirectoryName(directory) is { } parent) { directory = parent; }
            if (OperatingSystem.IsWindows()) { return GetDiskFreeSpaceEx(directory, out var free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : null; }
            return new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException) { return null; }
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
