using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Load;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1150: per-session process load from one shared snapshot, with fake counters, clock and tree.
public class ProcessLoadTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    const long Mib = 1024 * 1024;

    sealed class Table : IProcessTable
    {
        public List<ProcessReading>? Readings { get; set; } = [];
        public int Reads;
        public IReadOnlyList<ProcessReading>? Read() { Reads++; return Readings; }
    }

    sealed class Sessions : ISessionSource
    {
        public readonly Dictionary<string, SessionProcess> Roots = [];
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => [.. Roots.Keys.Select(id => new Session
        {
            Id = id, Agent = AgentKind.Copilot, Status = SessionStatus.Idle, StatusSince = T0, UpdatedAt = T0, Activity = new Activity(),
        })];
        public SessionList List(string? cursor = null) => new(Snapshot(), 0);
        public Session? Get(string id) => Snapshot().FirstOrDefault(s => s.Id == id);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public SessionProcess? ProcessOf(string id) => Roots.TryGetValue(id, out var p) ? p : null;
    }

    sealed class Rig
    {
        public readonly FakeTimeProvider Time = new(T0.AddMinutes(1));
        public readonly Table Table = new();
        public readonly Sessions Source = new();
        public readonly ProcessLoadSampler Sampler;
        public Rig(int processors = 4, Func<int?>? runQueue = null) => Sampler = new ProcessLoadSampler(Source, Table, Time, processors, () => 8192 * Mib, runQueue: runQueue ?? (() => null));
        public void Bind(string id, int pid, DateTimeOffset? start = null) => Source.Roots[id] = new SessionProcess(pid, start ?? T0);
        public ProcessLoadReport Tick(int seconds = 5) { Time.Advance(TimeSpan.FromSeconds(seconds)); return Sampler.Sample(); }
    }

    static ProcessReading P(int id, int? parent, double cpuSeconds, long workingSetMib, DateTimeOffset? start = null) =>
        new(id, parent, start ?? T0, TimeSpan.FromSeconds(cpuSeconds), workingSetMib * Mib);

    [Fact]
    public void The_first_sample_reports_CPU_as_unknown_not_zero_but_reports_the_working_set()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 100, 50)];

        var load = r.Sampler.Sample().Sessions["s1"];

        Assert.Null(load.CpuPercent);
        Assert.Equal(50 * Mib, load.WorkingSetBytes);
        Assert.Equal(LoadState.Partial, load.State);
    }

    [Fact]
    public void CPU_is_the_counter_delta_over_the_interval_as_a_share_of_the_whole_machine()
    {
        var r = new Rig(processors: 4);
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 100, 50)];
        r.Sampler.Sample();

        // 10 CPU-seconds in 5 s on 4 cores is 50% of the machine.
        r.Table.Readings = [P(10, 1, 110, 50)];
        var load = r.Tick().Sessions["s1"];

        Assert.Equal(50.0, load.CpuPercent);
        Assert.Equal(LoadState.Ok, load.State);
    }

    [Fact]
    public void A_busy_total_is_capped_at_the_machine_capacity()
    {
        var r = new Rig(processors: 2);
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 1)];
        r.Sampler.Sample();
        r.Table.Readings = [P(10, 1, 50, 1)];

        Assert.Equal(100.0, r.Tick().Sessions["s1"].CpuPercent);
    }

    [Fact]
    public void The_tree_sums_the_children_and_leaves_out_unrelated_processes()
    {
        var r = new Rig(processors: 4);
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 10), P(11, 10, 0, 20), P(12, 11, 0, 30), P(99, 1, 0, 500)];
        r.Sampler.Sample();
        r.Table.Readings = [P(10, 1, 1, 10), P(11, 10, 2, 20), P(12, 11, 3, 30), P(99, 1, 40, 500)];

        var load = r.Tick().Sessions["s1"];

        Assert.Equal(3, load.Processes);
        Assert.Equal(60 * Mib, load.WorkingSetBytes);
        Assert.Equal(30.0, load.CpuPercent);
    }

    [Fact]
    public void A_counter_that_went_backwards_is_a_reset_not_negative_work()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 100, 10)];
        r.Sampler.Sample();
        r.Table.Readings = [P(10, 1, 3, 10)];

        var load = r.Tick().Sessions["s1"];

        Assert.Null(load.CpuPercent);
        Assert.Equal(LoadState.Partial, load.State);
    }

    [Fact]
    public void A_reused_process_id_is_not_the_session_and_its_load_is_unavailable()
    {
        var r = new Rig();
        r.Bind("s1", 10, start: T0);
        r.Table.Readings = [P(10, 1, 100, 400, start: T0.AddMinutes(30))];

        var load = r.Sampler.Sample().Sessions["s1"];

        Assert.Equal(LoadState.Unavailable, load.State);
        Assert.Null(load.CpuPercent);
        Assert.Null(load.WorkingSetBytes);
    }

    [Fact]
    public void A_reused_id_does_not_carry_the_dead_processes_counter_into_the_new_one()
    {
        var r = new Rig();
        r.Bind("s1", 10, start: T0);
        r.Table.Readings = [P(10, 1, 100, 10)];
        r.Sampler.Sample();
        // Same id, new start: the old counter is not its baseline, so its CPU is its own total.
        var restarted = r.Time.GetUtcNow().AddSeconds(2);
        r.Bind("s1", 10, start: restarted);
        r.Table.Readings = [P(10, 1, 2, 10, start: restarted)];

        var load = r.Tick().Sessions["s1"];

        Assert.Equal(2.0 / (5 * 4) * 100, load.CpuPercent);
    }

    [Fact]
    public void A_child_that_started_before_its_parent_is_reuse_of_the_parents_id_and_is_not_counted()
    {
        var r = new Rig();
        r.Bind("s1", 10, start: T0);
        r.Table.Readings = [P(10, 1, 0, 10, start: T0), P(11, 10, 0, 777, start: T0.AddHours(-5))];

        var load = r.Sampler.Sample().Sessions["s1"];

        Assert.Equal(1, load.Processes);
        Assert.Equal(10 * Mib, load.WorkingSetBytes);
    }

    [Fact]
    public void A_parent_that_exited_leaves_the_orphaned_child_out_of_the_tree()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 10), P(11, 10, 0, 20)];
        r.Sampler.Sample();
        // 10 gone, 11 re-parented to something else: the root is gone, so the session is unavailable.
        r.Table.Readings = [P(11, 1, 1, 20)];

        var load = r.Tick().Sessions["s1"];

        Assert.Equal(LoadState.Unavailable, load.State);
        Assert.Equal(0, load.Processes);
    }

    [Fact]
    public void A_session_nested_under_another_is_counted_for_itself_and_not_in_the_outer_tree()
    {
        var r = new Rig();
        r.Bind("outer", 10);
        r.Bind("inner", 20);
        r.Table.Readings = [P(10, 1, 0, 10), P(20, 10, 0, 100), P(21, 20, 0, 200), P(11, 10, 0, 1)];

        var report = r.Sampler.Sample();

        Assert.Equal(2, report.Sessions["outer"].Processes);
        Assert.Equal(11 * Mib, report.Sessions["outer"].WorkingSetBytes);
        Assert.Equal(2, report.Sessions["inner"].Processes);
        Assert.Equal(300 * Mib, report.Sessions["inner"].WorkingSetBytes);
    }

    [Fact]
    public void Two_sessions_bound_to_the_same_process_are_not_both_charged_for_it()
    {
        var r = new Rig();
        r.Bind("a", 10);
        r.Bind("b", 10);
        r.Table.Readings = [P(10, 1, 0, 40)];

        var report = r.Sampler.Sample();

        Assert.Equal(LoadState.Partial, report.Sessions["a"].State);
        Assert.Equal(40 * Mib, report.Sessions["a"].WorkingSetBytes);
        Assert.Equal(LoadState.Unavailable, report.Sessions["b"].State);
        Assert.Null(report.Sessions["b"].WorkingSetBytes);
    }

    [Fact]
    public void A_process_that_started_and_exited_between_samples_does_not_break_the_next_one()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 10), P(11, 10, 5, 5)];
        r.Sampler.Sample();
        r.Table.Readings = [P(10, 1, 1, 10)];
        var report = r.Tick();

        Assert.Equal(1, report.Sessions["s1"].Processes);
        Assert.Equal(5.0, report.Sessions["s1"].CpuPercent);
    }

    [Fact]
    public void A_child_started_since_the_last_sample_counts_all_its_CPU()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 10)];
        r.Sampler.Sample();
        r.Table.Readings = [P(10, 1, 0, 10), P(11, 10, 4, 5, start: r.Time.GetUtcNow().AddSeconds(1))];

        Assert.Equal(20.0, r.Tick().Sessions["s1"].CpuPercent);
    }

    [Fact]
    public void A_process_that_could_not_be_read_makes_the_tree_partial_and_its_size_is_not_zero()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 10), new ProcessReading(11, 10, T0, null, null)];
        r.Sampler.Sample();
        r.Table.Readings = [P(10, 1, 1, 10), new ProcessReading(11, 10, T0, null, null)];

        var load = r.Tick().Sessions["s1"];

        Assert.Equal(LoadState.Partial, load.State);
        Assert.Equal(2, load.Processes);
        Assert.Equal(10 * Mib, load.WorkingSetBytes);
    }

    [Fact]
    public void An_unreadable_machine_table_reports_every_session_unavailable_with_unknown_figures()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = null;

        var report = r.Sampler.Sample();

        var load = report.Sessions["s1"];
        Assert.Equal(LoadState.Unavailable, load.State);
        Assert.Null(load.CpuPercent);
        Assert.Null(load.WorkingSetBytes);
        Assert.Null(report.Machine.CpuPercent);
    }

    [Fact]
    public void Machine_headroom_reports_the_cores_available_memory_and_the_readable_CPU()
    {
        var r = new Rig(processors: 4);
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 1), P(50, 1, 0, 1)];
        r.Sampler.Sample();
        r.Table.Readings = [P(10, 1, 5, 1), P(50, 1, 15, 1)];

        var machine = r.Tick().Machine;

        Assert.Equal(4, machine.Processors);
        Assert.Equal(100.0, machine.CpuPercent);
        Assert.Equal(8192 * Mib, machine.AvailableMemoryBytes);
    }

    [Fact]
    public void One_process_snapshot_is_shared_by_every_session_in_a_sample()
    {
        var r = new Rig();
        for (var i = 0; i < 20; i++) { r.Bind($"s{i}", 100 + i); }
        r.Table.Readings = [.. Enumerable.Range(100, 20).Select(i => P(i, 1, 0, 1))];

        r.Sampler.Sample();

        Assert.Equal(1, r.Table.Reads);
    }

    [Fact]
    public void A_session_that_ended_leaves_nothing_behind_in_the_next_report()
    {
        var r = new Rig();
        r.Bind("gone", 10);
        r.Table.Readings = [P(10, 1, 0, 10)];
        r.Sampler.Sample();
        r.Source.Roots.Clear();

        Assert.Empty(r.Tick().Sessions);
    }

    [Fact]
    public async Task The_sampler_samples_every_five_seconds_by_default_and_stops_when_disposed()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 10)];
        var seen = new List<ProcessLoadReport>();
        r.Sampler.Sampled += seen.Add;
        Assert.Equal(TimeSpan.FromSeconds(5), ProcessLoadSampler.DefaultInterval);

        r.Sampler.Start();
        await Until(() => r.Table.Reads >= 1);
        r.Time.Advance(TimeSpan.FromSeconds(5));
        await Until(() => r.Table.Reads >= 2);
        await r.Sampler.DisposeAsync();
        var reads = r.Table.Reads;
        r.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);

        Assert.Equal(reads, r.Table.Reads);
        Assert.NotNull(r.Sampler.Current);
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_stop_the_sampler()
    {
        var r = new Rig();
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 0, 10)];
        r.Sampler.Sampled += _ => throw new InvalidCastException("subscriber bug");

        r.Sampler.Start();
        await Until(() => r.Table.Reads >= 1);
        r.Time.Advance(TimeSpan.FromSeconds(5));
        await Until(() => r.Table.Reads >= 2);
        await r.Sampler.DisposeAsync();
    }

    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) { await Task.Delay(10); }
        Assert.True(done());
    }

    [Fact]
    public void The_log_line_says_unknown_for_what_is_not_known_and_carries_no_process_ids_or_paths()
    {
        var r = new Rig();
        r.Bind("0123456789abcdef", 4242);
        r.Table.Readings = [P(4242, 1, 0, 64)];
        var line = ProcessLoadSampler.Describe(r.Sampler.Sample());

        Assert.Contains("cpu unknown", line);
        Assert.Contains("working set 64 MiB", line);
        Assert.Contains("01234567 ", line);
        Assert.DoesNotContain("0123456789abcdef", line);
        Assert.DoesNotContain("4242", line);
        Assert.DoesNotContain("\\", line);
    }

    [Fact]
    public void The_log_line_lists_only_the_busiest_sessions_and_counts_the_rest()
    {
        var r = new Rig();
        for (var i = 0; i < 8; i++) { r.Bind($"s{i}", 100 + i); }
        r.Table.Readings = [.. Enumerable.Range(100, 8).Select(i => P(i, 1, 0, 1))];
        r.Sampler.Sample();
        r.Table.Readings = [.. Enumerable.Range(100, 8).Select(i => P(i, 1, i - 99, 1))];

        var line = ProcessLoadSampler.Describe(r.Tick(), top: 3);

        Assert.Contains("s7 cpu", line);
        Assert.DoesNotContain("s0 cpu", line);
        Assert.EndsWith("; 5 more", line);
    }

    [Fact]
    public void The_ten_second_window_is_the_mean_and_peak_of_the_last_two_samples()
    {
        var queue = new Queue<int?>([1, 3, 7]);
        var r = new Rig(processors: 4, runQueue: () => queue.Dequeue());
        r.Bind("s1", 10);
        r.Table.Readings = [P(10, 1, 100, 50)];
        r.Sampler.Sample();
        r.Table.Readings = [P(10, 1, 110, 50)];
        r.Tick();
        r.Table.Readings = [P(10, 1, 114, 50)];
        var m = r.Tick().Machine;

        // 10 CPU-seconds then 4 in 5 s each on 4 cores: 50% then 20%.
        Assert.Equal(20.0, m.CpuPercent);
        Assert.Equal(35.0, m.WindowCpuMean);
        Assert.Equal(50.0, m.WindowCpuPeak);
        Assert.Equal(7, m.RunQueue);
        Assert.Equal(5.0, m.RunQueueAvg);
        Assert.Equal(7, m.RunQueueMax);
    }

    [Fact]
    public void Unknown_window_figures_stay_null_and_are_never_zero()
    {
        var r = new Rig(runQueue: () => null);
        r.Table.Readings = null;

        var m = r.Sampler.Sample().Machine;

        Assert.Null(m.WindowCpuMean);
        Assert.Null(m.WindowCpuPeak);
        Assert.Null(m.RunQueue);
        Assert.Null(m.RunQueueAvg);
        Assert.Null(m.RunQueueMax);
    }

    [Fact]
    public void A_throwing_run_queue_reading_leaves_the_sample_going()
    {
        var r = new Rig(runQueue: () => throw new InvalidOperationException("counter gone"));

        Assert.Null(r.Sampler.Sample().Machine.RunQueue);
    }
}
