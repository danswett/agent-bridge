using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentBridge.Host.Core.Processes;

internal sealed record MacListedProcess(string Candidate, int Id, DateTimeOffset? StartedAt, int? Parent);
internal sealed record MacProcessDetails(int Id, string Name, DateTimeOffset? StartedAt, int? Parent);

internal interface IMacProcessListingNative
{
    int List(int[]? buffer);
    string? Path(int pid);
    MacProcessDetails? Details(int pid);
}

// Process.GetProcessesByName scans every PID and gathers task/thread information
// for matches. Repeating that for four names dominated the idle Mac host's CPU.
// This is discovery's name snapshot, never the G5 or managed-pane authority check.
internal static class MacProcessListing
{
    internal static IReadOnlyList<MacListedProcess>? Capture(IReadOnlyList<string> names) =>
        Capture(names, new MacProcessListingNative());

    internal static IReadOnlyList<MacListedProcess>? Capture(IReadOnlyList<string> names, IMacProcessListingNative native)
    {
        var pids = List(native);
        if (pids is null) { return null; }
        var found = new List<MacListedProcess>();
        foreach (var pid in pids)
        {
            var path = native.Path(pid);
            MacProcessDetails? details = null;
            var name = path is null ? (details = native.Details(pid))?.Name : System.IO.Path.GetFileName(path);
            var candidate = names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (candidate is null) { continue; }
            details ??= native.Details(pid);
            // A visible name with unreadable start/parent remains an uncertain process,
            // just as the existing inspector does; it must not inherit a cached PID generation.
            if (details?.Id != pid) { details = null; }
            found.Add(new(candidate, pid, details?.StartedAt, details?.Parent));
        }
        return found;
    }

    static int[]? List(IMacProcessListingNative native)
    {
        const int maximum = 1024 * 1024;
        var estimate = native.List(null);
        if (estimate <= 0 || estimate > maximum - 64) { return null; }
        var capacity = Math.Max(128, estimate + 64);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new int[capacity];
            var count = native.List(buffer);
            if (count <= 0 || count > capacity) { return null; }
            if (count == capacity)
            {
                if (capacity == maximum) { return null; }
                capacity = Math.Min(maximum, capacity * 2);
                continue;
            }
            var seen = new HashSet<int>();
            foreach (var pid in buffer.Take(count))
            {
                if (pid == 0) { continue; }
                if (pid < 0 || !seen.Add(pid)) { return null; }
            }
            return seen.Count == 0 ? null : [.. seen];
        }
        return null;
    }
}

internal sealed class MacProcessListingNative : IMacProcessListingNative
{
    static readonly UTF8Encoding Utf8 = new(false, true);

    public int List(int[]? buffer) => proc_listallpids(buffer, buffer is null ? 0 : checked(buffer.Length * sizeof(int)));

    public string? Path(int pid)
    {
        var buffer = new byte[4096];
        var bytes = proc_pidpath(pid, buffer, (uint)buffer.Length);
        return bytes <= 0 || bytes >= buffer.Length ? null : Text(buffer.AsSpan(0, bytes));
    }

    public MacProcessDetails? Details(int pid)
    {
        // Darwin proc_bsdinfo (sys/proc_info.h), 136 bytes on arm64 and x86_64.
        var buffer = new byte[136];
        return proc_pidinfo(pid, 3 /* PROC_PIDTBSDINFO */, 0, buffer, buffer.Length) == buffer.Length
            ? Decode(pid, buffer) : null;
    }

    internal static MacProcessDetails? Decode(int pid, ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length != 136 || BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]) != pid) { return null; }
        var parent = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);
        var seconds = BinaryPrimitives.ReadUInt64LittleEndian(buffer[120..]);
        var microseconds = BinaryPrimitives.ReadUInt64LittleEndian(buffer[128..]);
        DateTimeOffset? started = null;
        if (seconds <= 253402300799 && microseconds < 1_000_000)
        { started = DateTimeOffset.FromUnixTimeSeconds((long)seconds).AddTicks((long)microseconds * 10); }
        return new(pid, Text(buffer.Slice(48, 16)) ?? "", started, parent <= int.MaxValue ? (int)parent : null);
    }

    static string? Text(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf((byte)0);
        if (end >= 0) { buffer = buffer[..end]; }
        try { return Utf8.GetString(buffer); }
        catch (DecoderFallbackException) { return null; }
    }

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_listallpids([Out] int[]? buffer, int size);
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint size);
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_pidinfo(int pid, int flavor, ulong arg, [Out] byte[] buffer, int size);
}
