using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Text;
using Microsoft.Diagnostics.Runtime;

// What held the App tests when several of them stopped looking for seconds at once (#660, after
// #465 and #511). Their timeouts said only how long the longest gap between looks was and how
// many pool threads there were afterwards, which could not tell a frozen process (a GC, the
// runtime suspended, the OS not running it) from a starved pool or a blocked UI thread. A thread
// of its own looks ten times a second at every Wait.UntilAsync in progress. When one has not
// looked for Gap while this thread itself ran on time, it writes down the process's managed
// stacks (Windows, read from a snapshot of the process) with what the runtime counted meanwhile;
// when this thread was itself held, only what was counted, since nothing could be read during it.
// A wait that then times out says what was written down since it began. Nothing else is said,
// and the tests run as before.
static class StallWatch
{
    static readonly TimeSpan Gap = TimeSpan.FromSeconds(3);
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    // The watch is "held" when it wakes this late: the whole process stood still.
    static readonly TimeSpan Held = TimeSpan.FromSeconds(1);
    // Stacks are read at most this often, and this many times in a run: several tests stall
    // together, and one reading shows them all.
    static readonly TimeSpan Between = TimeSpan.FromSeconds(10);
    const int MaxStacks = 3;
    const int MaxNotes = 20;
    const int MaxFrames = 24;

    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly ConcurrentDictionary<Look, byte> looks = new();
    static readonly ConcurrentQueue<(long At, string Text)> notes = new();
    static int started;
    static int stacksRead;
    static int notesKept;
    static long probeQueuedAt = -1;
    static long timerFiredAt;
    static Timer? timer;

    public static long Now => clock.ElapsedMilliseconds;

    // One Wait.UntilAsync in progress: what it waits for, for which test, and when it last looked.
    public sealed class Look(string what, string? test)
    {
        public readonly string What = what;
        public readonly string? Test = test;
        public readonly long Began = Now;
        long last = Now;
        public long Last => Volatile.Read(ref last);
        public void Looked() => Volatile.Write(ref last, Now);
    }

    public static Look Begin(string what, string? test)
    {
        var look = new Look(what, test);
        looks[look] = 0;
        return look;
    }

    public static void End(Look look) => looks.TryRemove(look, out _);

    // What was written down while this wait was in progress, for its timeout's message.
    public static string Since(Look look)
    {
        var said = notes.Where(n => n.At >= look.Began).Select(n => n.Text).ToList();
        return said.Count == 0 ? "" : "\n" + string.Join("\n", said);
    }

    public static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) { return; }
        timerFiredAt = Now;
        timer = new Timer(_ => Volatile.Write(ref timerFiredAt, Now), null, Tick, Tick);
        new Thread(Watch) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "stall watch" }.Start();
    }

    // What the runtime has counted so far, to tell from two of them what happened in between.
    readonly record struct Counts(long At, TimeSpan GcPause, int Gen0, int Gen1, int Gen2, TimeSpan Jit, long JitMethods, TimeSpan Cpu,
        long Contention, int PoolThreads, long PoolQueued, long PoolDone)
    {
        public static Counts Take() => new(Now, GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            JitInfo.GetCompilationTime(), JitInfo.GetCompiledMethodCount(), Environment.CpuUsage.TotalTime, Monitor.LockContentionCount,
            ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, ThreadPool.CompletedWorkItemCount);

        public string Since(Counts before) =>
            $"over the last {At - before.At} ms: GC paused {(GcPause - before.GcPause).TotalMilliseconds:F0} ms "
            + $"(gen0 +{Gen0 - before.Gen0}, gen1 +{Gen1 - before.Gen1}, gen2 +{Gen2 - before.Gen2}); "
            + $"JIT {(Jit - before.Jit).TotalMilliseconds:F0} ms for {JitMethods - before.JitMethods} methods; "
            + $"process CPU {(Cpu - before.Cpu).TotalMilliseconds:F0} ms on {Environment.ProcessorCount} cores; "
            + $"lock contentions +{Contention - before.Contention}; pool items done +{PoolDone - before.PoolDone}; "
            + $"pool threads {PoolThreads}, queued {PoolQueued}";
    }

    static void Watch()
    {
        // About ten seconds of counts, to say what happened over a stall that began before it was seen.
        var history = new Queue<Counts>();
        var last = Now;
        var lastStacks = long.MinValue / 2;
        while (true)
        {
            Thread.Sleep(Tick);
            try
            {
                var now = Now;
                var late = now - last - (long)Tick.TotalMilliseconds;
                last = now;
                var counts = Counts.Take();
                var before = history.Count > 0 ? history.Peek() : counts;
                var previous = history.Count > 0 ? history.Last() : counts;
                history.Enqueue(counts);
                while (history.Count > 0 && now - history.Peek().At > 10_000) { history.Dequeue(); }

                // A work item queued now and then, to see whether the pool runs what it is given.
                if (Interlocked.CompareExchange(ref probeQueuedAt, now, -1) == -1)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_ => Volatile.Write(ref probeQueuedAt, -1), null);
                }

                if (late >= Held.TotalMilliseconds)
                {
                    // The runtime suspended (a GC, or the JIT's or a profiler's suspension) or the
                    // OS not running the process: what was counted meanwhile tells which.
                    Note(now, $"[stall watch +{now} ms] the watch itself woke {late} ms late, so the whole process was held; {counts.Since(previous)}");
                    continue;
                }

                var stalled = looks.Keys.Where(l => now - l.Last >= Gap.TotalMilliseconds).OrderBy(l => l.Last).ToList();
                if (stalled.Count == 0 || now - lastStacks < Between.TotalMilliseconds) { continue; }
                lastStacks = now;
                var probe = Volatile.Read(ref probeQueuedAt);
                var text = new StringBuilder();
                text.Append($"[stall watch +{now} ms] {stalled.Count} of {looks.Count} waits have not looked for {Gap.TotalSeconds:0} s while the watch ran on time; ")
                    .Append($"a pool item queued {(probe < 0 ? "has run" : $"{now - probe} ms ago has not run")}; ")
                    .Append($"the last timer fired {now - Volatile.Read(ref timerFiredAt)} ms ago; ")
                    .AppendLine(counts.Since(before));
                foreach (var l in stalled) { text.AppendLine($"  not looked for {now - l.Last} ms: {l.What} ({l.Test ?? "no test"})"); }
                text.Append(stacksRead++ < MaxStacks ? Stacks() : $"  (stacks were read {MaxStacks} times already)\n");
                Note(now, text.ToString());
            }
            catch (Exception e)
            {
                Note(Now, $"[stall watch] failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    static void Note(long at, string text)
    {
        if (Interlocked.Increment(ref notesKept) > MaxNotes) { return; }
        notes.Enqueue((at, text));
    }

    // Every managed thread's stack, read from a snapshot of this process (PssCaptureSnapshot), so
    // the threads go on while it is read. Threads with the same stack are listed once.
    internal static string Stacks()
    {
        if (!OperatingSystem.IsWindows()) { return "  (stacks are read on Windows only)\n"; }
        var took = Stopwatch.StartNew();
        using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        var same = new Dictionary<string, List<string>>();
        foreach (var thread in runtime.Threads)
        {
            if (!thread.IsAlive) { continue; }
            var frames = thread.EnumerateStackTrace().Take(MaxFrames * 2)
                .Select(f => f.Method?.Signature is { } s ? Short(s) : null)
                .Where(f => f is not null).ToList();
            if (frames.Count == 0) { continue; }
            var key = string.Join("\n", frames.Take(MaxFrames).Select(f => "      " + f)) + (frames.Count > MaxFrames ? "\n      ..." : "");
            var who = $"{thread.ManagedThreadId}{(thread.IsFinalizer ? " finalizer" : "")}";
            if (same.TryGetValue(key, out var ids)) { ids.Add(who); } else { same[key] = [who]; }
        }
        var text = new StringBuilder($"  managed stacks (read in {took.ElapsedMilliseconds} ms):\n");
        foreach (var (stack, ids) in same.OrderByDescending(s => s.Value.Count))
        {
            text.AppendLine($"    {ids.Count} thread(s) {string.Join(", ", ids)}:").AppendLine(stack);
        }
        return text.ToString();
    }

    // A frame without its parameters or the types a shared generic was made for, which said
    // little and made a report several times longer.
    static string Short(string signature)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(signature, @"\[\[.*?\]\]", "");
        var open = s.IndexOf('(');
        return open > 0 ? s[..open] : s;
    }
}
