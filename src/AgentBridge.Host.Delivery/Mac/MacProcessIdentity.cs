using System.Runtime.InteropServices;
using System.Text;

namespace AgentBridge.Host.Delivery.Mac;

public sealed record MacProcessIdentity(int ProcessId, int ParentProcessId, uint UserId, uint RealUserId,
    uint SavedUserId, DateTimeOffset StartedAt, string Executable)
{
    // System.Diagnostics.Process on macOS converts the same timeval through double
    // seconds. Match that exact representation at its interface, while retaining the
    // full kernel microseconds above for all native identity/generation comparisons.
    // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.OSX.cs#L41
    public DateTimeOffset ProcessStartTime
    {
        get
        {
            var seconds = StartedAt.ToUnixTimeSeconds();
            var microseconds = (StartedAt - DateTimeOffset.FromUnixTimeSeconds(seconds)).Ticks / 10;
            return DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(seconds + microseconds / 1_000_000d);
        }
    }
}

// Kernel identity, not a parsed command line. Darwin's proc_bsdinfo ABI is documented in
// https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h.
public static class MacProcessIdentityReader
{
    public static uint? CurrentUserId => OperatingSystem.IsMacOS() ? getuid() : null;

    public static MacProcessIdentity? Capture(int processId)
    {
        if (!OperatingSystem.IsMacOS() || processId <= 0) { return null; }
        var size = Marshal.SizeOf<BsdInfo>();
        if (proc_pidinfo(processId, 3 /* PROC_PIDTBSDINFO */, 0, out var before, size) != size) { return null; }
        var path = new byte[4096];
        var length = proc_pidpath(processId, path, (uint)path.Length);
        if (length <= 0 || proc_pidinfo(processId, 3, 0, out var after, size) != size
            || before.Pid != processId || before.Pid != after.Pid
            || before.Seconds != after.Seconds || before.Microseconds != after.Microseconds
            || before.Uid != after.Uid || before.RealUid != after.RealUid || before.SavedUid != after.SavedUid
            || after.Status == 5 /* SZOMB */) { return null; }
        var nul = Array.IndexOf(path, (byte)0);
        var executable = Encoding.UTF8.GetString(path, 0, nul < 0 ? length : nul);
        if (!Path.IsPathFullyQualified(executable)) { return null; }
        return new(processId, checked((int)after.Parent), after.Uid, after.RealUid, after.SavedUid,
            DateTimeOffset.FromUnixTimeSeconds(checked((long)after.Seconds)).AddTicks(checked((long)after.Microseconds * 10)), executable);
    }

    internal static bool HasExited(MacProcessIdentity identity)
    {
        if (!OperatingSystem.IsMacOS()) { return false; }
        var size = Marshal.SizeOf<BsdInfo>();
        var count = proc_pidinfo(identity.ProcessId, 3, 0, out var current, size);
        if (count != size) { return Marshal.GetLastPInvokeError() == 3 /* ESRCH */; }
        var start = DateTimeOffset.FromUnixTimeSeconds(checked((long)current.Seconds)).AddTicks(checked((long)current.Microseconds * 10));
        return current.Status == 5 || current.Pid != identity.ProcessId || start != identity.StartedAt;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct BsdInfo
    {
        public uint Flags, Status, ExitStatus, Pid, Parent, Uid, Gid, RealUid, RealGid, SavedUid, SavedGid, Reserved;
        public fixed byte Command[16];
        public fixed byte Name[32];
        public uint Files, ProcessGroup, JobCount, Terminal, TerminalGroup;
        public int Nice;
        public ulong Seconds, Microseconds;
    }

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_pidinfo(int pid, int flavor, ulong arg, out BsdInfo buffer, int size);
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint size);
    [DllImport("/usr/lib/libSystem.B.dylib")]
    static extern uint getuid();
}

internal interface IMacProcessProbe
{
    uint UserId { get; }
    MacProcessIdentity? Capture(int processId);
    bool HasExited(MacProcessIdentity identity);
}

internal sealed class MacProcessProbe : IMacProcessProbe
{
    public uint UserId => MacProcessIdentityReader.CurrentUserId ?? 0;
    public MacProcessIdentity? Capture(int processId) => MacProcessIdentityReader.Capture(processId);
    public bool HasExited(MacProcessIdentity identity) => MacProcessIdentityReader.HasExited(identity);
}
