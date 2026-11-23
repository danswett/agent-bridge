using AgentBridge.Host.Core;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class DeliveryDispatchPumpTests
{
    sealed class Source : ISessionSource
    {
        public long Revision { get; private set; }
        public event EventHandler<SessionChange>? Changed;
        public void Change(string id = "t", bool removed = false) =>
            Changed?.Invoke(this, new(removed ? SessionChangeKind.Removed : SessionChangeKind.Upserted, id, null, ++Revision));
        public IReadOnlyList<Session> Snapshot() => [];
        public Session? Get(string id) => null;
        public SessionList List(string? cursor = null) => throw new NotSupportedException();
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Fake : IAsyncDisposable, ISessionReplier
    {
        public readonly FakeTimeProvider Clock = new();
        public readonly Source Sessions = new();
        public readonly List<string> Sent = [], Logs = [];
        public readonly List<string> SentText = [];
        public readonly DeliveryGate Gate;
        public readonly LaneComposition Lane;
        public DeliveryDispatchPump Pump;
        public bool Idle = true, Flag = true, FailWrite;
        public int Reads, Opens;
        public Func<Task> Reading = () => Task.CompletedTask;
        public Func<Task<ReplyResult>> Sending = () => Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
        public Fake()
        {
            Gate = new(Clock);
            Lane = new(() => new AgentMessagingSettings { LaneDelivery = Flag }, () => true, Gate, this,
                new LaneScreens(async (_, _) => { Interlocked.Increment(ref Reads); await Reading(); return (Idle, true); }, Clock),
                "unused", Logs.Add, Clock, () => { Opens++; return new(new byte[32],
                    _ => { if (FailWrite) { throw new IOException("fake disk"); } }, _ => { }); });
            Pump = new(Lane, Sessions, Logs.Add);
        }
        public bool Available => true;
        public async Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null,
            CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        {
            var attempt = Guid.Parse(request.Request);
            Assert.True(Gate.TryClaim(request.Id, attempt));
            await Gate.WaitToTypeAsync(cancellationToken);
            try
            {
                if (request.StillCurrent?.Invoke() != true) { return new(AnswerOutcome.Refused); }
                var text = request.HandoffText!();
                Sent.Add(request.Id);
                SentText.Add(text);
                Assert.False(cancellationToken.IsCancellationRequested);
                return await Sending();
            }
            finally { Gate.DoneTyping(); Gate.Release(request.Id, attempt); }
        }
        public Guid Offer(string target = "t")
        {
            var id = Guid.NewGuid();
            Assert.Equal(DeliveryOfferState.Pending, Lane.Offer(id, target, new("agent"), "body " + id, () => true).State);
            return id;
        }
        public async ValueTask DisposeAsync() { await Pump.DisposeAsync(); Lane.Dispose(); }
    }

    [Fact]
    public async Task Offers_wait_for_idle_then_drain_once_without_timers_for_empty_sessions()
    {
        await using var f = new Fake { Idle = false };
        var first = f.Offer(); var second = f.Offer();
        await f.Pump.WhenIdleAsync();
        Assert.Empty(f.Sent); Assert.True(f.Reads > 0);
        f.Idle = true; f.Sessions.Change();
        await f.Pump.WhenIdleAsync();
        Assert.Equal(["t", "t"], f.Sent);
        Assert.Collection(f.SentText, text => Assert.Contains(first.ToString(), text), text => Assert.Contains(second.ToString(), text));
        var reads = f.Reads;
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        await f.Pump.WhenIdleAsync();
        Assert.Equal(reads, f.Reads);
    }

    [Fact]
    public async Task A_wake_burst_during_a_read_has_one_flight_and_loses_no_pending_work()
    {
        await using var f = new Fake();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reading = () => { entered.TrySetResult(); return release.Task; };
        var first = f.Offer();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 10; i++) { f.Pump.Wake("t"); }
        var second = f.Offer();
        Assert.Equal(1, f.Reads); Assert.Empty(f.Sent);
        release.SetResult();
        await f.Pump.WhenIdleAsync();
        Assert.Equal(["t", "t"], f.Sent);
        Assert.Collection(f.SentText, text => Assert.Contains(first.ToString(), text), text => Assert.Contains(second.ToString(), text));
    }

    [Fact]
    public async Task Pending_retries_back_off_from_250ms_to_two_seconds_and_stop_after_drain()
    {
        await using var f = new Fake { Idle = false };
        f.Offer(); await f.Pump.WhenIdleAsync();
        Assert.Equal(1, f.Reads);
        f.Clock.Advance(TimeSpan.FromMilliseconds(249)); await f.Pump.WhenIdleAsync();
        Assert.Equal(1, f.Reads);
        foreach (var delay in new[] { 1, 500, 1000, 2000, 2000 })
        { var before = f.Reads; f.Clock.Advance(TimeSpan.FromMilliseconds(delay)); await f.Pump.WhenIdleAsync(); Assert.Equal(before + 1, f.Reads); }
        f.Idle = true; f.Clock.Advance(TimeSpan.FromSeconds(2)); await f.Pump.WhenIdleAsync();
        Assert.Single(f.Sent);
        var reads = f.Reads; f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Pump.WhenIdleAsync();
        Assert.Equal(reads, f.Reads);
    }

    [Fact]
    public async Task Unconfirmed_latches_survive_reoffers_removal_toggles_and_pump_recreation()
    {
        await using var f = new Fake { Sending = () => Task.FromResult(new ReplyResult(AnswerOutcome.Unconfirmed)) };
        var first = f.Offer(); await f.Pump.WhenIdleAsync();
        Assert.Single(f.Sent);
        Assert.Equal(DeliveryOfferState.Unconfirmed, f.Lane.Offer(first, "t", new("agent"), "body " + first, () => true).State);
        f.Offer(); f.Sessions.Change(removed: true); f.Sessions.Change();
        f.Flag = false; f.Pump.Wake("t"); f.Flag = true; f.Pump.Wake("t");
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Pump.WhenIdleAsync();
        await f.Pump.DisposeAsync(); f.Pump = new(f.Lane, f.Sessions, f.Logs.Add);
        f.Pump.Wake("t"); await f.Pump.WhenIdleAsync();
        Assert.Single(f.Sent);
        f.Sending = () => Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
        f.Offer("other"); await f.Pump.WhenIdleAsync();
        Assert.Equal(["t", "other"], f.Sent);
    }

    [Fact]
    public async Task BeginStop_blocks_wakes_but_never_aborts_an_admitted_handoff()
    {
        await using var f = new Fake();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sending = () => { entered.TrySetResult(); return release.Task; };
        f.Offer(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Pump.BeginStop(); f.Offer(); f.Sessions.Change();
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Single(f.Sent);
        release.SetResult(new(AnswerOutcome.Delivered)); await f.Pump.WhenIdleAsync();
        Assert.Single(f.Sent); Assert.True(f.Lane.HasPending("t"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabling_or_stopping_during_refresh_sends_no_keys(bool stop)
    {
        await using var f = new Fake();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reading = () => { entered.TrySetResult(); return release.Task; };
        f.Offer(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (stop) { f.Pump.BeginStop(); } else { f.Flag = false; }
        release.SetResult(); await f.Pump.WhenIdleAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Pump.WhenIdleAsync();
        Assert.Empty(f.Sent); Assert.Equal(1, f.Reads);
        f.Flag = true; f.Pump.Wake("t"); await f.Pump.WhenIdleAsync();
        Assert.Equal(stop ? 0 : 1, f.Sent.Count);
    }

    [Fact]
    public async Task Journal_uncertainty_stops_all_sessions_and_survives_pump_recreation()
    {
        await using var f = new Fake { Idle = false };
        f.Offer(); f.Offer("other"); await f.Pump.WhenIdleAsync();
        f.FailWrite = true; f.Idle = true; f.Pump.Wake("t"); await f.Pump.WhenIdleAsync();
        Assert.Empty(f.Sent); Assert.True(f.Lane.JournalUncertain);
        Assert.Contains(f.Logs, s => s.Contains("IOException"));
        f.FailWrite = false; f.Pump.Wake("other"); f.Clock.Advance(TimeSpan.FromSeconds(20));
        await f.Pump.WhenIdleAsync();
        await f.Pump.DisposeAsync(); f.Pump = new(f.Lane, f.Sessions, f.Logs.Add);
        f.Pump.Wake("other"); await f.Pump.WhenIdleAsync();
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task Disabled_and_removed_sessions_have_no_retry_timer_or_new_journal()
    {
        await using var f = new Fake { Flag = false };
        f.Pump.Wake("t"); await f.Pump.WhenIdleAsync(); Assert.Equal(0, f.Opens);
        f.Flag = true; f.Idle = false; f.Offer(); await f.Pump.WhenIdleAsync();
        var reads = f.Reads; f.Sessions.Change(removed: true);
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Pump.WhenIdleAsync();
        Assert.Equal(reads, f.Reads);
        f.Idle = true; f.Sessions.Change(); await f.Pump.WhenIdleAsync();
        Assert.Single(f.Sent);
    }

    [Fact]
    public async Task A_refused_stop_leaves_the_pump_draining()
    {
        await using var f = new Fake();
        var delivery = f.Gate.TryEnterDelivery()!;
        var stops = new PlannedStops(f.Gate, TimeSpan.FromMilliseconds(50), _ => { }) { StopHeld = f.Pump.BeginStop };
        Assert.Null(await stops.PrepareStopAsync());
        delivery.Dispose();
        f.Offer(); await f.Pump.WhenIdleAsync();
        Assert.Single(f.Sent);
    }

    [Fact]
    public async Task A_held_stop_lets_what_is_drained_stay_delivered_then_stops_the_pump()
    {
        await using var f = new Fake();
        f.Offer(); await f.Pump.WhenIdleAsync();
        Assert.Single(f.Sent);
        var stops = new PlannedStops(f.Gate, TimeSpan.FromSeconds(30), _ => { }) { StopHeld = f.Pump.BeginStop };
        var lease = await stops.PrepareStopAsync();
        Assert.NotNull(lease);
        // Released, as a stop the host then abandoned would be: the pump must not wake again.
        lease.Dispose();
        f.Offer(); await f.Pump.WhenIdleAsync();
        Assert.Single(f.Sent);
        await f.Pump.DisposeAsync();
    }

    [Fact]
    public async Task A_stop_hook_that_throws_never_refuses_the_stop()
    {
        await using var f = new Fake();
        var logs = new List<string>();
        var stops = new PlannedStops(f.Gate, TimeSpan.FromSeconds(30), logs.Add) { StopHeld = () => throw new InvalidOperationException("boom") };
        using var lease = await stops.PrepareStopAsync();
        Assert.NotNull(lease);
        Assert.Contains(logs, line => line.Contains("lane pump"));
    }

    [Fact]
    public async Task A_committed_restart_stops_the_pump_before_the_handover()
    {
        await using var f = new Fake();
        var order = new List<string>();
        var stops = new PlannedStops(f.Gate, TimeSpan.FromSeconds(30), _ => { }) { StopHeld = () => order.Add("pump") };
        Assert.True(stops.TryCommitRestart(stops.RestartToken(), () => order.Add("handover")));
        Assert.Equal(["pump", "handover"], order);
        await Task.CompletedTask;
    }
}
