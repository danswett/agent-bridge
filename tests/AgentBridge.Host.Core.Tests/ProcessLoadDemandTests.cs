using System.Threading.Channels;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Load;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class ProcessLoadDemandTests
{
    static readonly TimeSpan Active = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);
    static readonly DateTimeOffset Started = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    sealed class Clock : FakeTimeProvider
    {
        public readonly Channel<TimeSpan> Armed = Channel.CreateUnbounded<TimeSpan>();
        public TimeSpan Skew;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Skew;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Armed.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    sealed class Table : IProcessTable
    {
        public int Reads;
        public double Cpu;
        public IReadOnlyList<ProcessReading> Read()
        {
            Interlocked.Increment(ref Reads);
            return [new(10, null, Started, TimeSpan.FromSeconds(Cpu), 1024)];
        }
    }

    sealed class Sessions : ISessionSource
    {
        Session[] sessions = [];
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed;
        public IReadOnlyList<Session> Snapshot() => Volatile.Read(ref sessions);
        public void Present(bool present)
        {
            Volatile.Write(ref sessions, present ? [new Session
            {
                Id = "session", Agent = AgentKind.Copilot, Status = SessionStatus.Idle,
                StatusSince = Started, UpdatedAt = Started, Activity = new(),
            }] : []);
            Changed?.Invoke(this, new(present ? SessionChangeKind.Upserted : SessionChangeKind.Removed, "session", null, 0));
        }
        public SessionProcess? ProcessOf(string id) => new(10, Started);
        public SessionList List(string? cursor = null) => new(Snapshot(), 0);
        public Session? Get(string id) => Snapshot().FirstOrDefault();
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Rig : IAsyncDisposable
    {
        public readonly Clock Time = new();
        public readonly Table Table = new();
        public readonly Sessions Source = new();
        public readonly Channel<ProcessLoadReport> Reports = Channel.CreateUnbounded<ProcessLoadReport>();
        public readonly ProcessLoadSampler Sampler;
        public Rig()
        {
            Time.SetUtcNow(Started.AddDays(1));
            Sampler = new(Source, Table, Time, processors: 4, availableMemory: () => null, runQueue: () => null, totalMemory: () => null);
            Sampler.Sampled += report => Reports.Writer.TryWrite(report);
        }
        public Task<ProcessLoadReport> Report() => Reports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        public async Task Armed(TimeSpan expected) =>
            Assert.Equal(expected, await Time.Armed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        public ValueTask DisposeAsync() => Sampler.DisposeAsync();
    }

    [Fact]
    public async Task Empty_sessions_without_feed_consumers_keep_only_the_minute_host_log_cadence()
    {
        await using var r = new Rig();
        r.Sampler.Start();
        await r.Armed(Idle);
        Assert.Null((await r.Report()).Machine.CpuPercent);
        r.Time.Advance(Idle - TimeSpan.FromSeconds(1));
        Assert.Equal(1, r.Table.Reads);
        r.Table.Cpu = 120;
        r.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(50, (await r.Report()).Machine.CpuPercent);
        Assert.Equal(2, r.Table.Reads);
    }

    [Fact]
    public async Task First_session_gets_a_baseline_then_a_second_sample_before_CPU_is_known()
    {
        await using var r = new Rig();
        r.Sampler.Start();
        await r.Armed(Idle);
        await r.Report();
        r.Time.Advance(TimeSpan.FromSeconds(20));
        r.Table.Cpu = 80;
        r.Source.Present(true);
        await r.Armed(Active);
        var baseline = await r.Report();
        Assert.Null(baseline.Machine.CpuPercent);
        Assert.Null(baseline.Machine.WindowCpuMean);
        Assert.Null(baseline.Sessions["session"].CpuPercent);
        r.Table.Cpu = 84;
        r.Time.Advance(Active);
        Assert.Equal(20, (await r.Report()).Sessions["session"].CpuPercent);

        r.Source.Present(false);
        await r.Armed(Idle);
        var reads = r.Table.Reads;
        r.Time.Skew = TimeSpan.FromDays(-2);
        r.Time.Advance(Idle - TimeSpan.FromSeconds(1));
        Assert.Equal(reads, r.Table.Reads);
        r.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null((await r.Report()).Machine.CpuPercent);
        await r.Sampler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        r.Source.Present(true);
        r.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(reads + 1, r.Table.Reads);
    }
}
