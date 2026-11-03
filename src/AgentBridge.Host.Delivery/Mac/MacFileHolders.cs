using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentBridge.Host.Delivery.Mac;

internal readonly record struct MacFileIdentity(uint Device, ulong Inode, uint Generation, ushort Mode);
internal readonly record struct MacDescriptor(int Number, uint Kind);

internal interface IMacDescriptorProbe
{
    MacFileIdentity? File(string path);
    MacProcessIdentity? Process(int pid);
    IReadOnlyList<MacDescriptor>? Descriptors(int pid);
    MacFileIdentity? Vnode(int pid, int descriptor);
}

internal static class MacFileHolders
{
    internal const int MaxProcesses = 256;
    internal const int MaxDescriptors = 4096;
    const int MaxTotalDescriptors = 16384;

    internal static IReadOnlyList<int> Find(string path, IReadOnlyList<int> candidates) =>
        OperatingSystem.IsMacOS() ? Find(path, candidates, new MacDescriptorProbe()) : [];

    internal static IReadOnlyList<int> Find(string path, IReadOnlyList<int> candidates, IMacDescriptorProbe probe,
        Func<bool>? withinBudget = null)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0') || candidates.Count is 0 or > MaxProcesses
            || candidates.Any(pid => pid <= 0) || candidates.Distinct().Count() != candidates.Count) { return []; }
        var clock = Stopwatch.StartNew();
        withinBudget ??= () => clock.Elapsed < TimeSpan.FromSeconds(3);
        if (!withinBudget() || probe.File(path) is not { } target || (target.Mode & 0xf000) != 0x8000) { return []; }
        var found = new List<int>();
        var total = 0;
        foreach (var pid in candidates)
        {
            if (!withinBudget() || probe.Process(pid) is not { } process
                || CheckedDescriptors(probe.Descriptors(pid)) is not { } descriptors) { return []; }
            total += descriptors.Length;
            if (total > MaxTotalDescriptors) { return []; }
            var files = new Dictionary<int, MacFileIdentity>();
            foreach (var descriptor in descriptors)
            {
                if (!withinBudget()) { return []; }
                if (descriptor.Kind != 1) { continue; } // PROX_FDTYPE_VNODE
                // An unreadable FD is uncertainty, not evidence it does not hold this
                // lock. A filtered lsof result can silently hide precisely this error.
                if (probe.Vnode(pid, descriptor.Number) is not { } file) { return []; }
                files.Add(descriptor.Number, file);
            }
            if (!withinBudget() || CheckedDescriptors(probe.Descriptors(pid)) is not { } after
                || !descriptors.SequenceEqual(after)) { return []; }
            // A descriptor number can be closed and reused without changing the list.
            // Verify every vnode again; do not narrow an incomplete shared-holder set.
            foreach (var (descriptor, file) in files)
            {
                if (!withinBudget() || probe.Vnode(pid, descriptor) != file) { return []; }
            }
            if (probe.Process(pid) != process) { return []; }
            if (files.Values.Contains(target)) { found.Add(pid); }
        }
        return withinBudget() && probe.File(path) == target ? found : [];
    }

    static MacDescriptor[]? CheckedDescriptors(IReadOnlyList<MacDescriptor>? descriptors)
    {
        if (descriptors is null || descriptors.Count > MaxDescriptors
            || descriptors.Any(d => d.Number < 0 || d.Kind is not (0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 9 or 10 or 11))
            || descriptors.Select(d => d.Number).Distinct().Count() != descriptors.Count) { return null; }
        return descriptors.OrderBy(d => d.Number).ToArray();
    }
}

// Darwin ABI from sys/proc_info.h and sys/stat.h. Sizes and used offsets are also
// checked against a compiled SDK-header probe; no process paths/arguments are read.
internal sealed class MacDescriptorProbe : IMacDescriptorProbe
{
    public MacFileIdentity? File(string path) => OperatingSystem.IsMacOS() && lstat64(path, out var stat) == 0
        ? new(stat.Device, stat.Inode, stat.Generation, stat.Mode) : null;
    public MacProcessIdentity? Process(int pid) => MacProcessIdentityReader.Capture(pid);

    public unsafe IReadOnlyList<MacDescriptor>? Descriptors(int pid)
    {
        if (!OperatingSystem.IsMacOS()) { return null; }
        const int elementSize = 8;
        var needed = proc_pidinfo(pid, 1 /* PROC_PIDLISTFDS */, 0, null, 0);
        if (needed <= 0 || needed % elementSize != 0 || needed > MacFileHolders.MaxDescriptors * elementSize) { return null; }
        // Leave room for concurrent opens, then reject a full buffer: a successful
        // truncated list cannot establish that an external process lacks this lock.
        var entries = new NativeDescriptor[needed / elementSize + 32];
        int bytes;
        fixed (NativeDescriptor* buffer = entries)
        { bytes = proc_pidinfo(pid, 1, 0, buffer, entries.Length * elementSize); }
        if (bytes < 0 || bytes % elementSize != 0 || bytes >= entries.Length * elementSize
            || bytes / elementSize > MacFileHolders.MaxDescriptors) { return null; }
        // Zero is also the API's failure result; do not claim an empty complete list.
        return bytes == 0 ? null : entries.Take(bytes / elementSize).Select(e => new MacDescriptor(e.Number, e.Kind)).ToArray();
    }

    public MacFileIdentity? Vnode(int pid, int descriptor) => OperatingSystem.IsMacOS()
        && proc_pidfdinfo(pid, descriptor, 1 /* PROC_PIDFDVNODEINFO */, out var vnode, 176) == 176
        ? new(vnode.Device, vnode.Inode, vnode.Generation, vnode.Mode) : null;

    [StructLayout(LayoutKind.Sequential)]
    struct NativeDescriptor { public int Number; public uint Kind; }

    [StructLayout(LayoutKind.Explicit, Size = 176)]
    struct NativeVnode
    {
        [FieldOffset(24)] public uint Device;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint Generation;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    struct NativeStat
    {
        [FieldOffset(0)] public uint Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(120)] public uint Generation;
    }

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern unsafe int proc_pidinfo(int pid, int flavor, ulong arg, NativeDescriptor* buffer, int size);
    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    static extern int proc_pidfdinfo(int pid, int fd, int flavor, out NativeVnode buffer, int size);
    // Explicit 64-bit-inode symbol gives the same struct on arm64 and x86_64.
    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    static extern int lstat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out NativeStat buffer);
}
