using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentBridge.Host.Delivery.Mac;

/// <summary>G5 on macOS: an unclassified PowerShell runtime is not permission to type.</summary>
public sealed class MacAgentHaBridgeProbe : IAgentHaBridgeProbe
{
    readonly Func<MacDaemonObservation> observe;

    public MacAgentHaBridgeProbe() : this(() => ObserveBounded(Observe, Thread.Sleep)) { }
    internal MacAgentHaBridgeProbe(Func<MacDaemonObservation> observe) => this.observe = observe;

    public string? Active()
    {
        MacDaemonObservation observation;
        try { observation = observe(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return "The Mac could not check whether agent-ha-bridge is running; delivery is held.";
        }
        if (!observation.Complete)
        {
            return "The Mac could not inspect every PowerShell runtime; delivery is held to avoid a second bridge writer.";
        }
        foreach (var process in observation.Processes)
        {
            if (process.UserId == observation.UserId || process.RealUserId == observation.UserId || process.SavedUserId == observation.UserId)
            {
                // The legacy daemon/supervisor are PowerShell scripts. An interactive
                // host can load them without naming them in argv, so process arguments
                // cannot prove that a PowerShell runtime is unrelated. Until macOS has
                // an authoritative legacy-daemon lease, fail closed for that runtime.
                return $"A PowerShell runtime (pid {process.ProcessId}) could be running agent-ha-bridge for this user. Delivery is held until it exits.";
            }
        }
        return null;
    }

    static MacDaemonObservation Observe() => OperatingSystem.IsMacOS()
        ? Observe(new MacDaemonNative()) : new(false, 0, []);

    internal static MacDaemonObservation ObserveBounded(Func<MacDaemonObservation> observe, Action<TimeSpan> delay)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = observe();
            // A live Mac scan observed transient EPERM, with no established cause. Retry
            // the whole observation, never skip that PID or reuse a previous clear.
            // A seen PowerShell runtime already suffices to hold delivery, even if
            // a later process made this snapshot incomplete.
            if (current.Complete || current.Processes.Any(p => p.UserId == current.UserId
                || p.RealUserId == current.UserId || p.SavedUserId == current.UserId) || attempt == 2)
            { return current; }
            delay(TimeSpan.FromMilliseconds(25));
        }
    }

    // GetProcessesByName silently drops processes whose name/path cannot be read on
    // macOS. G5 needs to distinguish a complete empty scan from an unreadable daemon.
    internal static MacDaemonObservation Observe(IMacDaemonNative native, Func<string, bool>? include = null)
    {
        var user = native.UserId;
        if (user is null or 0) { return new(false, user ?? 0, []); }
        var found = new List<MacDaemonProcess>();
        var pids = Enumerate(native);
        if (pids is null) { return new(false, user.Value, found); }
        foreach (var pid in pids)
        {
            // SHORTBSDINFO permits owner inspection across users; full BSDINFO does
            // not. Requiring the latter first would hold every ordinary Mac on root's
            // protected processes even though their owner is independently provable.
            var inspectedOwner = native.ReadOwner(pid);
            if (inspectedOwner.Owner is not { } owner)
            {
                if (inspectedOwner.Error == MacDaemonNative.NoSuchProcess) { continue; }
                return new(false, user.Value, found);
            }
            if (owner.ProcessId != pid) { return new(false, user.Value, found); }
            if (owner.UserId != user && owner.RealUserId != user && owner.SavedUserId != user) { continue; }
            if (owner.Zombie) { continue; }
            var first = native.ReadIdentity(pid);
            if (first.Identity is not { } before)
            {
                if (first.Error == MacDaemonNative.NoSuchProcess) { continue; }
                return new(false, user.Value, found);
            }
            if (before.ProcessId != pid || before.UserId != owner.UserId || before.RealUserId != owner.RealUserId || before.SavedUserId != owner.SavedUserId)
            { return new(false, user.Value, found); }
            if (before.Zombie) { continue; }
            var path = native.ReadPath(pid);
            var second = native.ReadIdentity(pid);
            if (second.Identity is not { } after)
            {
                if (second.Error == MacDaemonNative.NoSuchProcess) { continue; }
                return new(false, user.Value, found);
            }
            if (after.Zombie) { continue; }
            if (before != after || path is null || !Path.IsPathFullyQualified(path) || path.Contains('\0'))
            { return new(false, user.Value, found); }
            var name = Path.GetFileName(path);
            if (include?.Invoke(name) ?? (name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || name.Equals("powershell", StringComparison.OrdinalIgnoreCase)))
            { found.Add(new(pid, after.UserId, after.RealUserId, after.SavedUserId)); }
        }
        return new(true, user.Value, found);
    }

    internal static int[]? Enumerate(IMacDaemonNative native)
    {
        const int maximum = 1024 * 1024;
        var estimate = native.ListPids(null);
        if (estimate <= 0 || estimate > maximum - 64) { return null; }
        var capacity = Math.Max(128, estimate + 64);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new int[capacity];
            var count = native.ListPids(buffer);
            if (count <= 0 || count > capacity) { return null; }
            // proc_listallpids returns a COUNT; its buffer size parameter is in BYTES.
            // A full buffer can be silently truncated, so it never proves completeness.
            if (count == capacity)
            {
                if (capacity == maximum) { return null; }
                capacity = Math.Min(maximum, capacity * 2);
                continue;
            }
            var seen = new HashSet<int>();
            foreach (var pid in buffer.Take(count))
            {
                if (pid == 0) { continue; } // kernel task
                if (pid < 0 || !seen.Add(pid)) { return null; }
            }
            return seen.Count == 0 ? null : [.. seen];
        }
        return null;
    }

}

internal sealed record MacDaemonObservation(bool Complete, uint UserId, IReadOnlyList<MacDaemonProcess> Processes);
internal sealed record MacDaemonProcess(int ProcessId, uint UserId, uint RealUserId, uint SavedUserId);

// This seam covers the construction of Complete, not only the final policy decision.
internal interface IMacDaemonNative
{
    uint? UserId { get; }
    int ListPids(int[]? buffer);
    MacDaemonOwnerRead ReadOwner(int processId);
    MacDaemonIdentityRead ReadIdentity(int processId);
    string? ReadPath(int processId);
}

internal sealed record MacDaemonIdentity(int ProcessId, uint UserId, uint RealUserId, uint SavedUserId,
    ulong StartedSeconds, ulong StartedMicroseconds, bool Zombie = false);
internal sealed record MacDaemonIdentityRead(MacDaemonIdentity? Identity, int Error = 0);
internal sealed record MacDaemonOwner(int ProcessId, uint UserId, uint RealUserId, uint SavedUserId, bool Zombie = false);
internal sealed record MacDaemonOwnerRead(MacDaemonOwner? Owner, int Error = 0);

internal sealed class MacDaemonNative : IMacDaemonNative
{
    public const int NoSuchProcess = 3; // ESRCH: only this error proves that a PID disappeared.
    public uint? UserId => MacProcessIdentityReader.CurrentUserId;
    public int ListPids(int[]? buffer) => proc_listallpids(buffer, buffer is null ? 0 : checked(buffer.Length * sizeof(int)));

    public MacDaemonOwnerRead ReadOwner(int processId)
    {
        var size = Marshal.SizeOf<ShortBsdInfo>();
        var count = proc_pidinfo(processId, 13 /* PROC_PIDT_SHORTBSDINFO */, 0, out ShortBsdInfo info, size);
        if (count != size) { return new(null, count <= 0 ? Marshal.GetLastPInvokeError() : 0); }
        return new(new(checked((int)info.Pid), info.Uid, info.RealUid, info.SavedUid, info.Status == 5));
    }

    public MacDaemonIdentityRead ReadIdentity(int processId)
    {
        var size = Marshal.SizeOf<BsdInfo>();
        var count = proc_pidinfo(processId, 3 /* PROC_PIDTBSDINFO */, 0, out BsdInfo info, size);
        if (count != size) { return new(null, count <= 0 ? Marshal.GetLastPInvokeError() : 0); }
        return new(new(checked((int)info.Pid), info.Uid, info.RealUid, info.SavedUid, info.Seconds, info.Microseconds, info.Status == 5));
    }

    public string? ReadPath(int processId)
    {
        var bytes = new byte[4096];
        var length = proc_pidpath(processId, bytes, (uint)bytes.Length);
        if (length <= 0 || length > bytes.Length) { return null; }
        var end = Array.IndexOf(bytes, (byte)0, 0, length);
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, end < 0 ? length : end); }
        catch (DecoderFallbackException) { return null; }
    }

    // Darwin proc_bsdinfo, independently of whether proc_pidpath is readable. Its
    // UID fields let a protected foreign process be skipped without guessing a name.
    // https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct BsdInfo
    {
        public uint Flags, Status, ExitStatus, Pid, Parent, Uid, Gid, RealUid, RealGid, SavedUid, SavedGid, Reserved;
        public fixed byte Command[16];
        public fixed byte Name[32];
        public uint Files, ProcessGroup, JobCount, Terminal, TerminalGroup;
        public int Nice;
        public ulong Seconds, Microseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ShortBsdInfo
    {
        public uint Pid, Parent, ProcessGroup, Status;
        public fixed byte Command[16];
        public uint Flags, Uid, Gid, RealUid, RealGid, SavedUid, SavedGid, Reserved;
    }

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_listallpids([Out] int[]? buffer, int bufferSize);
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_pidinfo(int pid, int flavor, ulong arg, out BsdInfo buffer, int size);
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_pidinfo(int pid, int flavor, ulong arg, out ShortBsdInfo buffer, int size);
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint size);
}
