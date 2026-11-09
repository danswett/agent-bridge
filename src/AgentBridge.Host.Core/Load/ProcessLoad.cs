using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AgentBridge.Host.Core.Load;

// One process as the table saw it. Cpu is the process's cumulative processor time (a counter,
// never a rate); a null field is unknown, not zero.
public sealed record ProcessReading(int Id, int? Parent, DateTimeOffset? StartedAt, TimeSpan? Cpu, long? WorkingSet);

// One snapshot of every process on the machine, taken once per sample and shared by every session.
// Null when the machine cannot be read at all (an unsupported platform).
public interface IProcessTable
{
    IReadOnlyList<ProcessReading>? Read();
}

public enum LoadState
{
    // Every process in the tree was read.
    Ok,
    // Some of the tree could not be read or has no earlier counter; the figures are what was.
    Partial,
    // Nothing is known: the session's process is gone, unbound or shared, or this is the first sample.
    Unavailable,
}

// A session's whole process tree. CpuPercent is of the whole machine's capacity (every logical
// processor busy is 100), from counter deltas since the last sample. WorkingSetBytes is the sum of
// the processes' working sets: shared pages are counted once per process, so it is not unique RAM.
public sealed record SessionLoad(LoadState State, int Processes, double? CpuPercent, long? WorkingSetBytes);

// Machine totals. CpuPercent is the readable processes' CPU over the interval, as a percentage of
// the machine's capacity: processes that exited between samples, and ones the host may not read,
// are not in it, so it can only under-state a saturated machine.
public sealed record MachineLoad(int Processors, double? CpuPercent, long? AvailableMemoryBytes);

// SampledAt is when the snapshot was taken; a consumer compares it to its own clock for the age.
public sealed record ProcessLoadReport(DateTimeOffset SampledAt, MachineLoad Machine, IReadOnlyDictionary<string, SessionLoad> Sessions);

// Reads each live session's process tree and the machine's headroom from one shared snapshot (#1150).
// The session-to-process binding is ISessionSource.ProcessOf, so there is no second discovery
// loop. Counts and sizes only: no paths, command lines or process ids leave this class.
public sealed class ProcessLoadSampler : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    readonly ISessionSource source;
    readonly IProcessTable table;
    readonly TimeProvider time;
    readonly int processors;
    readonly Func<long?> availableMemory;
    readonly TimeSpan interval;
    readonly object gate = new();
    // The previous sample's counters, replaced whole each time so nothing for a gone process stays.
    Dictionary<(int Id, long Start), TimeSpan> previousCpu = [];
    DateTimeOffset? previousAt;
    ProcessLoadReport? current;
    CancellationTokenSource? stop;
    Task? loop;

    public ProcessLoadSampler(ISessionSource source, IProcessTable table, TimeProvider? time = null, int? processors = null,
        Func<long?>? availableMemory = null, TimeSpan? interval = null)
    {
        this.source = source;
        this.table = table;
        this.time = time ?? TimeProvider.System;
        this.processors = Math.Max(1, processors ?? Environment.ProcessorCount);
        this.availableMemory = availableMemory ?? SystemMemory.Available;
        this.interval = interval ?? DefaultInterval;
    }

    public ProcessLoadReport? Current { get { lock (gate) { return current; } } }

    // Raised on the sampler's own thread after each pass.
    public event Action<ProcessLoadReport>? Sampled;

    public void Start()
    {
        if (loop is not null) { return; }
        stop = new CancellationTokenSource();
        var token = stop.Token;
        loop = Task.Factory.StartNew(() => RunAsync(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    async Task RunAsync(CancellationToken ct)
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException) { }
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                try { Sample(); }
                // One failed pass (or a faulty subscriber) must not end the sampler; the next one tries again.
                catch (Exception e) when (e is not OperationCanceledException) { }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    // One pass. Public so a test drives it with a fake clock and table.
    public ProcessLoadReport Sample()
    {
        var now = time.GetUtcNow();
        var readings = table.Read();
        var machineMemory = availableMemory();
        var roots = new List<(string Id, SessionProcess Process)>();
        foreach (var s in source.Snapshot())
        {
            if (source.ProcessOf(s.Id) is { } p) { roots.Add((s.Id, p)); }
        }
        roots.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        ProcessLoadReport report;
        lock (gate)
        {
            var elapsed = previousAt is { } at ? (now - at).TotalSeconds : 0;
            var nextCpu = new Dictionary<(int, long), TimeSpan>();
            if (readings is null)
            {
                report = new ProcessLoadReport(now, new MachineLoad(processors, null, machineMemory),
                    roots.ToDictionary(r => r.Id, _ => new SessionLoad(LoadState.Unavailable, 0, null, null)));
            }
            else
            {
                var byId = new Dictionary<int, ProcessReading>();
                foreach (var r in readings) { byId[r.Id] = r; }
                var children = new Dictionary<int, List<ProcessReading>>();
                foreach (var r in readings)
                {
                    // A child that started before its parent's recorded start cannot be that parent's:
                    // the id was reused after the real parent exited.
                    if (r.Parent is { } parent && parent != r.Id && byId.TryGetValue(parent, out var pr) && !Reused(pr, r))
                    {
                        if (!children.TryGetValue(parent, out var list)) { children[parent] = list = []; }
                        list.Add(r);
                    }
                    if (r.Cpu is { } cpu && r.StartedAt is { } started) { nextCpu[(r.Id, started.UtcTicks)] = cpu; }
                }

                // A process another session is rooted at belongs to that session alone.
                var rootIds = new HashSet<int>(roots.Select(r => r.Process.Id));
                var claimed = new HashSet<int>();
                var sessions = new Dictionary<string, SessionLoad>();
                foreach (var (id, binding) in roots)
                {
                    if (!byId.TryGetValue(binding.Id, out var root) || !Bound(root, binding) || !claimed.Add(root.Id))
                    {
                        // Gone, reused for something else, or already the root of an earlier session.
                        sessions[id] = new SessionLoad(LoadState.Unavailable, 0, null, null);
                        continue;
                    }
                    sessions[id] = Measure(root, children, rootIds, claimed, elapsed, now);
                }

                double? machineCpu = null;
                if (previousAt is not null && elapsed > 0)
                {
                    var delta = readings.Sum(r => Delta(r, now)?.TotalSeconds ?? 0);
                    machineCpu = Percent(delta, elapsed);
                }
                report = new ProcessLoadReport(now, new MachineLoad(processors, machineCpu, machineMemory), sessions);
            }
            previousCpu = nextCpu;
            previousAt = now;
            current = report;
        }
        try { Sampled?.Invoke(report); } catch (Exception) { }
        return report;
    }

    static bool Reused(ProcessReading parent, ProcessReading child) =>
        parent.StartedAt is { } p && child.StartedAt is { } c && c < p;

    // The binding's start time guards against the pid having been reused since discovery.
    static bool Bound(ProcessReading reading, SessionProcess binding) =>
        binding.StartedAt is not { } bound || reading.StartedAt is not { } started || Math.Abs((started - bound).TotalSeconds) < 2;

    SessionLoad Measure(ProcessReading root, Dictionary<int, List<ProcessReading>> children, HashSet<int> rootIds, HashSet<int> claimed, double elapsed, DateTimeOffset now)
    {
        var count = 0;
        long workingSet = 0;
        var anyWorkingSet = false;
        var partial = false;
        var cpuSeconds = 0.0;
        var anyCpu = false;
        var stack = new Stack<ProcessReading>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            count++;
            if (p.WorkingSet is { } ws) { workingSet += ws; anyWorkingSet = true; } else { partial = true; }
            if (previousAt is not null && elapsed > 0 && Delta(p, now) is { } d) { cpuSeconds += d.TotalSeconds; anyCpu = true; } else { partial = true; }
            if (!children.TryGetValue(p.Id, out var kids)) { continue; }
            foreach (var k in kids)
            {
                // Another session's root, and what is below it, is that session's; and a process is counted once.
                if (rootIds.Contains(k.Id) || !claimed.Add(k.Id)) { continue; }
                stack.Push(k);
            }
        }
        // The first sample has no earlier counter, so CPU is unknown rather than zero.
        double? cpuPercent = anyCpu ? Percent(cpuSeconds, elapsed) : null;
        var state = !anyCpu && !anyWorkingSet ? LoadState.Unavailable : partial ? LoadState.Partial : LoadState.Ok;
        return new SessionLoad(state, count, cpuPercent, anyWorkingSet ? workingSet : null);
    }

    // CPU time used since the last sample, or null with no earlier counter for this process
    // generation. A counter that went backwards is a reset, not negative work. A process that
    // started after the last sample used all its time since then.
    TimeSpan? Delta(ProcessReading p, DateTimeOffset now)
    {
        if (p.Cpu is not { } cpu || p.StartedAt is not { } started) { return null; }
        if (previousCpu.TryGetValue((p.Id, started.UtcTicks), out var before)) { return cpu >= before ? cpu - before : null; }
        return previousAt is { } at && started > at ? cpu : null;
    }

    double Percent(double cpuSeconds, double elapsedSeconds) =>
        Math.Round(Math.Min(100.0, 100.0 * cpuSeconds / (elapsedSeconds * processors)), 1);

    public async ValueTask DisposeAsync()
    {
        if (stop is null) { return; }
        await stop.CancelAsync().ConfigureAwait(false);
        try { if (loop is not null) { await loop.ConfigureAwait(false); } } catch (Exception) { }
        stop.Dispose();
        stop = null;
    }

    // One line of counts for host.log: the machine, then the busiest sessions. Ids are the short
    // form the log uses elsewhere; nothing about a process but its size.
    public static string Describe(ProcessLoadReport r, int top = 5)
    {
        static string Pct(double? v) => v is { } x ? x.ToString("F0", CultureInfo.InvariantCulture) + "%" : "unknown";
        static string Mib(long? v) => v is { } x ? (x / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB" : "unknown";
        var line = $"load: machine cpu {Pct(r.Machine.CpuPercent)} of {r.Machine.Processors} cores, {Mib(r.Machine.AvailableMemoryBytes)} memory available";
        var busiest = r.Sessions.OrderByDescending(s => s.Value.CpuPercent ?? -1).ThenBy(s => s.Key, StringComparer.Ordinal).Take(top)
            .Select(s => $"{s.Key[..Math.Min(8, s.Key.Length)]} cpu {Pct(s.Value.CpuPercent)} working set {Mib(s.Value.WorkingSetBytes)} {s.Value.Processes} proc{(s.Value.State == LoadState.Ok ? "" : " (" + s.Value.State.ToString().ToLowerInvariant() + ")")}");
        var sessions = string.Join("; ", busiest);
        return sessions.Length == 0 ? line + "; no sessions" : $"{line}; sessions: {sessions}" + (r.Sessions.Count > top ? $"; {r.Sessions.Count - top} more" : "");
    }
}

// The real table: every process the host may read. Anything it may not read is a null field.
public sealed class SystemProcessTable : IProcessTable
{
    readonly Dictionary<(int Id, long Start), int?> parents = [];

    public IReadOnlyList<ProcessReading>? Read()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) { return null; }
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        var result = new List<ProcessReading>(all.Length);
        var seen = new HashSet<(int, long)>();
        foreach (var p in all)
        {
            using (p)
            {
                DateTimeOffset? started = null;
                TimeSpan? cpu = null;
                long? ws = null;
                try { started = new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                try { cpu = p.TotalProcessorTime; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                try { ws = p.WorkingSet64; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                int? parent = null;
                if (started is { } s)
                {
                    var key = (p.Id, s.UtcTicks);
                    seen.Add(key);
                    // A generation's parent never changes, so it is asked for once.
                    if (!parents.TryGetValue(key, out parent)) { parents[key] = parent = AgentBridge.Hooks.Ancestry.ParentOf(p.Id); }
                }
                result.Add(new ProcessReading(p.Id, parent, started, cpu, ws));
            }
        }
        foreach (var key in parents.Keys.Where(k => !seen.Contains(k)).ToList()) { parents.Remove(key); }
        return result;
    }
}

static class SystemMemory
{
    // Free physical memory now (Windows: GlobalMemoryStatusEx; Linux: MemAvailable), or null where
    // the OS will not say. The runtime's own figure is only as fresh as its last collection.
    public static long? Available()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : null;
            }
            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) { continue; }
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ? kib * 1024 : null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
