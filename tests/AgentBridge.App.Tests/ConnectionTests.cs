using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The app's connection to its host, and the deliveries that go through it, against a host on
// a real pipe.
public sealed class ConnectionTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    HostConnection connection = null!;

    public Task InitializeAsync()
    {
        host.Sessions.Add(Make.Session("s1", response: "hello"));
        host.Start();
        connection = host.Connect();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Connecting_says_hello_and_subscribes_to_the_sessions_the_usage_and_the_hosts_status()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Sessions.Get("s1") is not null, "the snapshot");

        Assert.Equal("Test PC", connection.Hello!.Machine.Name);
        Assert.Equal(4242, connection.Status!.ProcessId);
        Assert.Contains(HostOps.Subscribe, host.Received.Select(r => r.Op));
        Assert.Contains(UsageProtocol.Subscribe, host.Received.Select(r => r.Op));
        Assert.Contains(Ops.SessionsSubscribe, host.Received.Select(r => r.Op));
    }

    [Fact]
    public async Task Live_events_update_the_sessions_usage_and_status()
    {
        await Wait.UntilAsync(() => connection.Sessions.Get("s1") is not null, "the snapshot");

        await host.SendAsync(Events.SessionUpsert, Make.Session("s2", status: SessionStatus.Working));
        await host.SendAsync(Events.SessionRemove, new SessionRemoved("s1"));
        await host.SendAsync(UsageProtocol.Update, new UsageReport([new AgentUsage { Agent = AgentKind.Copilot, Label = "Copilot", Source = UsageSource.Api, MeasuredAt = Make.T0 }]));
        await host.SendAsync(HostOps.StatusEvent, FakeHost.StatusOf(RelayState.Connected));

        await Wait.UntilAsync(() => connection.Sessions.Get("s2") is not null && connection.Sessions.Get("s1") is null, "the upsert and removal");
        await Wait.UntilAsync(() => connection.Usage.Agents.Count == 1 && connection.Status!.Relay.State == RelayState.Connected, "usage and status");
    }

    [Fact]
    public async Task After_the_host_restarts_the_app_reconnects_and_drops_sessions_that_ended_meanwhile()
    {
        await Wait.UntilAsync(() => connection.Sessions.Get("s1") is not null, "the snapshot");

        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the connection to drop");
        host.Sessions.Clear();
        host.Sessions.Add(Make.Session("s3"));
        host.Start();

        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Sessions.Get("s3") is not null, "the new snapshot");
        Assert.Null(connection.Sessions.Get("s1"));
    }

    [Fact]
    public async Task With_no_host_running_the_app_says_so()
    {
        await host.StopAsync();

        await Wait.UntilAsync(() => connection.State == LinkState.NotRunning, "not running");
    }

    [Fact]
    public async Task An_answer_lost_to_a_host_restart_is_sent_again_with_the_same_request()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        var first = true;
        var gate = new TaskCompletionSource();
        host.On(Ops.SessionAnswer, data =>
        {
            if (first) { first = false; gate.SetResult(); Thread.Sleep(200); }
            return new AnswerResult(AnswerOutcome.Delivered);
        });
        var deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };

        var entry = deliveries.Submit("answer|s1|q1", Ops.SessionAnswer, r => new { id = "s1", question = "q1", request = r, text = "yes" }, o => o != "busy");
        await gate.Task.WaitAsync(FakeHost.Patience);
        await host.StopAsync();
        host.Start();

        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");
        Assert.Equal("delivered", deliveries.Get(entry.Key)!.Outcome);
        var requests = host.Sent(Ops.SessionAnswer).Select(d => d.GetProperty("request").GetString()).ToList();
        Assert.True(requests.Count >= 2);
        Assert.All(requests, r => Assert.Equal(entry.Request, r));
    }

    [Fact]
    public async Task A_busy_that_answers_our_own_resend_is_waited_out_and_never_shown()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        var calls = 0;
        host.On(Ops.SessionAnswer, _ => ++calls switch
        {
            1 => throw new InvalidOperationException("unused"),
            2 => new AnswerResult(AnswerOutcome.Busy, "being delivered"),
            _ => new AnswerResult(AnswerOutcome.Delivered),
        });
        var deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(50) };
        var shown = new List<string?>();
        deliveries.Changed += d => shown.Add(deliveries.Get(d.Key)?.Outcome);

        var entry = deliveries.Submit("k", Ops.SessionAnswer, r => new { id = "s1", question = "q1", request = r, text = "yes" }, _ => true);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Failed, "the failure");
        deliveries.Resend("k", Ops.SessionAnswer, r => new { id = "s1", question = "q1", request = r, text = "yes" });

        await Wait.UntilAsync(() => deliveries.Get("k")!.Outcome == "delivered", "delivered");
        Assert.DoesNotContain("busy", shown);
        Assert.Equal(entry.Request, host.Sent(Ops.SessionAnswer).Last().GetProperty("request").GetString());
    }

    [Fact]
    public async Task An_answer_given_while_the_host_is_down_waits_and_gives_up_as_unconfirmed_only_after_the_limit()
    {
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State == LinkState.NotRunning, "not running");
        var clock = new FakeTimeProvider(Make.T0);
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { RetryDelay = TimeSpan.FromMilliseconds(20), GiveUp = TimeSpan.FromMinutes(9) };

        deliveries.Submit("k", Ops.SessionAnswer, r => new { request = r }, _ => true);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Waiting, "waiting");
        clock.Advance(TimeSpan.FromMinutes(10));

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the give-up", TimeSpan.FromSeconds(30));
        Assert.Equal("unconfirmed", deliveries.Get("k")!.Outcome);
    }
}
