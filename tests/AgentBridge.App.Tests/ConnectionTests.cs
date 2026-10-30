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
    // Snapshots read in full (sessions.synced), counted from the first connection.
    int syncs;

    public Task InitializeAsync()
    {
        host.Sessions.Add(Make.Session("s1", response: "hello"));
        host.Start();
        connection = host.Connect(start: false);
        connection.Sessions.Synced += () => Interlocked.Increment(ref syncs);
        connection.Start();
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
    public async Task A_gap_in_the_events_after_a_reconnect_reads_the_sessions_status_and_usage_again()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Sessions.Get("s1") is not null, "the snapshot");
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host to go");
        host.Start();
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "the reconnect");
        var before = host.Sent(Ops.SessionsSubscribe).Count;

        // The dropped events may have included the host's status (a finished sign-in, say).
        host.Status = FakeHost.StatusOf(RelayState.Connected);
        foreach (var ch in host.Channels.OfType<LocalChannel>().Where(c => c.IsOpen).Distinct()) { ch.SkipEventSeq(3); }
        await host.SendAsync(Events.SessionUpsert, Make.Session("s2"));

        await Wait.UntilAsync(() => host.Sent(Ops.SessionsSubscribe).Count == before + 1, "the sessions to be subscribed again");
        await Wait.UntilAsync(() => connection.Status?.Relay.State == RelayState.Connected, "the status to be read again");
        Assert.NotEmpty(host.Sent(UsageProtocol.Get));
    }

    [Fact]
    public async Task Events_lost_before_the_first_one_arrives_are_noticed_too()
    {
        await using var fresh = new FakeHost();
        fresh.Sessions.Add(Make.Session("s1"));
        var skipped = 0;
        // As if the buffer overflowed before the app read anything: the first event it sees is 4.
        fresh.On(Ops.Hello, _ =>
        {
            if (Interlocked.Exchange(ref skipped, 1) == 0)
            {
                foreach (var ch in fresh.Channels.OfType<LocalChannel>().Where(c => c.IsOpen).Distinct()) { ch.SkipEventSeq(3); }
            }
            return new HelloResult(1, new MachineInfo("host-id", "Test PC", "windows", "0.1.0"), [.. fresh.Capabilities]);
        });
        fresh.Start();
        await using var watching = fresh.Connect();

        await Wait.UntilAsync(() => fresh.Sent(Ops.SessionsSubscribe).Count >= 2, "the sessions to be subscribed again");
    }

    [Fact]
    public async Task An_open_connection_notices_when_the_host_starts_or_stops_taking_answers()
    {
        await using var watching = new HostConnection(ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher())
        {
            RetryDelay = TimeSpan.FromMilliseconds(100),
            CapabilityRefresh = TimeSpan.FromMilliseconds(100),
        };
        watching.Start();
        await Wait.UntilAsync(() => watching.Can(Capabilities.SessionsAnswer), "the first hello");

        // agent-ha-bridge started for this user: the host only watches.
        host.Status = FakeHost.StatusOf(agentHaBridge: "agent-ha-bridge is running");
        host.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("host-id", "Test PC", "windows", "0.1.0"), [Capabilities.SessionsRead, HostOps.Capability]));
        await Wait.UntilAsync(() => !watching.Can(Capabilities.SessionsAnswer) && watching.Status?.AgentHaBridge is not null, "answering to be taken away");

        host.Status = FakeHost.StatusOf();
        host.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("host-id", "Test PC", "windows", "0.1.0"), [.. host.Capabilities]));
        await Wait.UntilAsync(() => watching.Can(Capabilities.SessionsAnswer) && watching.Status?.AgentHaBridge is null, "answering to come back");
    }

    [Fact]
    public async Task A_host_restarted_with_usage_off_leaves_no_usage_behind()
    {
        var report = new UsageReport([new AgentUsage { Agent = AgentKind.Copilot, Label = "Copilot", Source = UsageSource.Api, MeasuredAt = Make.T0 }]);
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "the connection");
        await host.SendAsync(UsageProtocol.Update, report);
        await Wait.UntilAsync(() => connection.Usage.Agents.Count == 1, "the usage");

        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the host to go");
        host.Capabilities.Remove(UsageProtocol.Capability);
        host.Start();

        await Wait.UntilAsync(() => connection.State == LinkState.Connected && connection.Usage.Agents.Count == 0, "the usage to go");
    }

    [Fact]
    public async Task After_the_host_restarts_the_app_reconnects_and_drops_sessions_that_ended_meanwhile()
    {
        await Wait.UntilAsync(() => Volatile.Read(ref syncs) > 0 && connection.Sessions.Get("s1") is not null, "the snapshot");

        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State != LinkState.Connected, "the connection to drop");
        host.Sessions.Clear();
        host.Sessions.Add(Make.Session("s3"));
        var before = Volatile.Read(ref syncs);
        host.Start();

        // Read in full: s3 arrives before sessions.synced, and only that drops s1. Waiting for
        // s3 alone let the check run in between.
        await Wait.UntilAsync(() => connection.State == LinkState.Connected && Volatile.Read(ref syncs) > before && connection.Sessions.Get("s3") is not null, "the new snapshot");
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
            // Held until the pipe has closed, so this first outcome is surely lost: a fixed sleep
            // lost the race to a slow runner's stop, and the outcome got through (CI on #81).
            if (first) { first = false; gate.SetResult(); SpinWait.SpinUntil(() => host.Channels.All(c => !c.IsOpen), FakeHost.Patience); }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reply_whose_outcome_was_lost_goes_again_only_to_the_same_host_process(bool hostRestarted)
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        var first = true;
        var gate = new TaskCompletionSource();
        host.On(Ops.SessionReply, data =>
        {
            // Held until the pipe has closed, so this first outcome is surely lost: a fixed sleep
            // lost the race to a slow runner's stop, and the outcome got through (CI on #81).
            if (first) { first = false; gate.SetResult(); SpinWait.SpinUntil(() => host.Channels.All(c => !c.IsOpen), FakeHost.Patience); }
            return new ReplyResult(AnswerOutcome.Delivered);
        });
        var deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, replayAfterRestart: false);
        await gate.Task.WaitAsync(FakeHost.Patience);
        await host.StopAsync();
        // A replacement process has forgotten the request; the same one, its pipe back, has not.
        if (hostRestarted) { host.Status = FakeHost.StatusOf() with { ProcessId = 4343 }; }
        host.Start();

        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");
        var done = deliveries.Get(entry.Key)!;
        if (hostRestarted)
        {
            Assert.Equal("unconfirmed", done.Outcome);
            Assert.Contains("restarted", done.Reason);
            Assert.Single(host.Sent(Ops.SessionReply));
        }
        else
        {
            Assert.Equal("delivered", done.Outcome);
            Assert.True(host.Sent(Ops.SessionReply).Count >= 2);
        }
    }

    // As another machine's host over the relay: no host.manage, so no status to know the run by;
    // its hello names it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_known_only_by_the_run_its_hello_names_gets_a_lost_reply_again_only_in_that_run(bool hostRestarted)
    {
        await using var remote = new FakeHost();
        remote.Capabilities.Remove(HostOps.Capability);
        var run = "run-1";
        remote.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "linux", "0.1.0", Instance: Volatile.Read(ref run)), [.. remote.Capabilities]));
        remote.Sessions.Add(Make.Session("s1"));
        remote.Start();
        await using var link = remote.Connect();
        await Wait.UntilAsync(() => link.State == LinkState.Connected && link.HostInstance == "run-1", "connected to run-1");
        var first = true;
        var gate = new TaskCompletionSource();
        remote.On(Ops.SessionReply, data =>
        {
            // Held until the pipe has closed, so this first outcome is surely lost.
            if (first) { first = false; gate.SetResult(); SpinWait.SpinUntil(() => remote.Channels.All(c => !c.IsOpen), FakeHost.Patience); }
            return new ReplyResult(AnswerOutcome.Delivered);
        });
        var deliveries = new Deliveries(link, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, replayAfterRestart: false);
        await gate.Task.WaitAsync(FakeHost.Patience);
        await remote.StopAsync();
        if (hostRestarted) { Volatile.Write(ref run, "run-2"); }
        remote.Start();

        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");
        var done = deliveries.Get(entry.Key)!;
        if (hostRestarted)
        {
            Assert.Equal("unconfirmed", done.Outcome);
            Assert.Single(remote.Sent(Ops.SessionReply));
        }
        else
        {
            Assert.Equal("delivered", done.Outcome);
            Assert.True(remote.Sent(Ops.SessionReply).Count >= 2);
        }
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
