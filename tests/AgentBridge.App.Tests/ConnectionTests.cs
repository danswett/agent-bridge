using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The app's connection to its host, and the deliveries that go through it, against a host on
// a real pipe.
[Trait("Category", "Network")]
public sealed class ConnectionTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    HostConnection connection = null!;
    // Snapshots read in full (sessions.synced), counted from the first connection.
    int syncs;
    // The connection's states and log as they came, timed, for a failure seen only on CI (#465).
    readonly List<string> seen = [];
    LinkState? lastState;

    public Task InitializeAsync()
    {
        host.Sessions.Add(Make.Session("s1", response: "hello"));
        host.Start();
        void See(string what) { lock (seen) { seen.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {what}"); } }
        connection = host.Connect(start: false, log: See);
        connection.Sessions.Synced += () => Interlocked.Increment(ref syncs);
        connection.Changed += () =>
        {
            if (connection.State == lastState) { return; }
            lastState = connection.State;
            See($"{connection.State}: {connection.StateDetail}");
        };
        connection.Start();
        return Task.CompletedTask;
    }

    string Seen() { lock (seen) { return $"now {DateTime.UtcNow:HH:mm:ss.fff}, {connection.State}, connection {connection.ConnectionNumber}; {string.Join("; ", seen)}"; } }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_ends_a_hung_attempt_and_starts_one_new_attempt_without_overlapping_loops()
    {
        // #800: a link not Connected while its host answers. The first attempt's connect hangs
        // until it is cancelled, as no timeout of the app's own would end it.
        var attempts = 0;
        var open = 0;
        var most = 0;
        var hungCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        await using var link = new HostConnection(async ct =>
        {
            var n = Interlocked.Increment(ref attempts);
            var now = Interlocked.Increment(ref open);
            lock (lines) { most = Math.Max(most, now); }
            try
            {
                if (n == 1)
                {
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { hungCancelled.TrySetResult(); throw; }
                }
                return await LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromSeconds(1), ct);
            }
            finally { Interlocked.Decrement(ref open); }
        }, new LockedDispatcher(), l => { lock (lines) { lines.Add(l); } }) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        link.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref attempts) == 1, "the first attempt under way", TimeSpan.FromSeconds(30));

        link.Reconnect();
        link.Reconnect();

        await hungCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Wait.UntilAsync(() => link.State == LinkState.Connected, "the link made again", TimeSpan.FromSeconds(30));
        Assert.Equal(1, most);
        lock (lines) { Assert.Contains(lines, l => l.Contains("ended to reconnect", StringComparison.Ordinal)); }
    }

    [Fact]
    public async Task A_connection_that_follows_no_sessions_stops_when_disposed_rather_than_spinning()
    {
        // The relay loopback's kind (Subscribes = false). Disposed while connected, its wait for a
        // gap completed cancelled at once, every time round, and recovering did nothing without
        // sessions to resubscribe, so the loop spun a thread at full speed and DisposeAsync never
        // returned (#129). Which of the cancelled waits wins is a race, hence several.
        var connections = Enumerable.Range(0, 20).Select(_ =>
        {
            var c = new HostConnection(ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100), Subscribes = false };
            c.Start();
            return c;
        }).ToList();
        await Wait.UntilAsync(() => connections.All(c => c.State == LinkState.Connected), "every connection");

        foreach (var c in connections)
        {
            await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
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
        // A host that keeps no other machines' readings is asked for its own, as before #172.
        Assert.False(host.Sent(UsageProtocol.Subscribe).Single().GetProperty("shared").GetBoolean());
    }

    [Fact]
    public async Task A_host_that_keeps_other_machines_readings_is_asked_for_them_too()
    {
        await using var shared = new FakeHost();
        shared.Capabilities.Add(UsageProtocol.SharedCapability);
        shared.Start();
        await using var conn = shared.Connect();

        await Wait.UntilAsync(() => shared.Sent(UsageProtocol.Subscribe).Count == 1, "the usage subscription");
        Assert.True(shared.Sent(UsageProtocol.Subscribe).Single().GetProperty("shared").GetBoolean());
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

    // #493: a host past the protocol's limits (the demo's, a broken or a remote one) sent a word
    // WinUI's text layout failed on, and the app ended. Held to the limits as it arrives.
    [Fact]
    public async Task A_session_over_the_protocols_limits_is_cut_to_them_as_it_arrives()
    {
        await Wait.UntilAsync(() => connection.Sessions.Get("s1") is not null, "the snapshot");

        var question = Make.Form() with { Message = "Which one? " + new string('[', 9000) };
        await host.SendAsync(Events.SessionUpsert, Make.Session("s2", question: question, response: "Done: " + new string('[', 32000)));

        await Wait.UntilAsync(() => connection.Sessions.Get("s2") is not null, "the upsert");
        var s = connection.Sessions.Get("s2")!;
        Assert.Equal((Limits.Response, true), (s.Activity.Response!.Length, s.Activity.ResponseTruncated));
        Assert.Equal((Limits.QuestionMessage, true), (s.Question!.Message.Length, s.Question.Truncated));
    }

    // #446: a record the app could not read ended the event pump, and the connection with it; the
    // snapshot after the reconnect sent the same record, so one bad session kept the app
    // reconnecting for good. Now that message alone is refused, logged once for its kind.
    [Fact]
    public async Task A_message_the_app_cannot_read_is_refused_and_the_events_after_it_still_arrive()
    {
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var conn = new HostConnection(ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher(), logged.Enqueue) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        conn.Start();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Get("s1") is not null, "the snapshot");
        var number = conn.ConnectionNumber;

        await host.SendAsync(Events.SessionUpsert, new System.Text.Json.Nodes.JsonObject { ["agent"] = "copilot" });
        await host.SendAsync(Events.SessionUpsert, new System.Text.Json.Nodes.JsonObject { ["id"] = 7 });
        // What the host sent stays out of the log: the time converter's own message quotes it.
        await host.SendAsync(HostOps.StatusEvent, new System.Text.Json.Nodes.JsonObject { ["startedAt"] = "the owner's 4242" });
        await host.SendAsync(Events.SessionUpsert, Make.Session("s2"));
        // Missing a required field other than its id, and one of the wrong type: kept, as the PWA keeps it.
        var partial = JsonSerializer.SerializeToNode(Make.Session("s3", status: SessionStatus.Working), ProtocolJson.Options)!.AsObject();
        partial.Remove("status");
        partial["title"] = 5;
        await host.SendAsync(Events.SessionUpsert, partial);

        await Wait.UntilAsync(() => conn.Sessions.Get("s2") is not null && conn.Sessions.Get("s3") is not null, "the events after the refused ones");
        Assert.Equal((number, LinkState.Connected), (conn.ConnectionNumber, conn.State));
        Assert.Equal((SessionStatus.Idle, (string?)null), (conn.Sessions.Get("s3")!.Status, conn.Sessions.Get("s3")!.Title));
        Assert.Single(logged, l => l.StartsWith($"refused a {Events.SessionUpsert} ", StringComparison.Ordinal));
        Assert.Single(logged, l => l.StartsWith($"refused a {HostOps.StatusEvent} ", StringComparison.Ordinal));
        Assert.DoesNotContain(logged, l => l.Contains("4242", StringComparison.Ordinal));
    }

    // A sessions.synced read as anything (null, a list) ended the snapshot: the sessions it had not
    // sent were removed and the discovery hold lifted, on a record the app could not read.
    [Fact]
    public async Task A_snapshot_end_the_app_cannot_read_ends_nothing_and_the_real_one_still_does()
    {
        await using var slow = new FakeHost();
        slow.Sessions.Add(Make.Session("s1"));
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.HoldSynced = hold;
        slow.Start();
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var conn = new HostConnection(ct => LocalClient.ConnectAsync(slow.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher(), logged.Enqueue) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        conn.Start();
        await Wait.UntilAsync(() => conn.Sessions.Get("s1") is not null, "the snapshot's session");

        await slow.SendAsync(Events.SessionsSynced, new System.Text.Json.Nodes.JsonArray());
        await slow.SendAsync(Events.SessionUpsert, Make.Session("s2"));
        await Wait.UntilAsync(() => conn.Sessions.Get("s2") is not null, "the event after it");

        Assert.False(conn.Sessions.Complete);
        Assert.Single(logged, l => l.StartsWith($"refused a {Events.SessionsSynced} ", StringComparison.Ordinal));
        hold.SetResult();
        await Wait.UntilAsync(() => conn.Sessions.Complete, "the snapshot's own end");
    }

    // The subscribe's own result (revision, count) is not used; read strictly, one the app could not
    // read ended the connection, and every reconnect asked again and never showed a session.
    [Fact]
    public async Task A_subscribe_result_the_app_cannot_read_still_brings_the_sessions()
    {
        await using var odd = new FakeHost { SubscribeReply = new System.Text.Json.Nodes.JsonObject { ["revision"] = "seven" } };
        odd.Sessions.Add(Make.Session("s1"));
        odd.Start();
        await using var conn = odd.Connect();

        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Complete && conn.Sessions.Get("s1") is not null, "the snapshot");
        Assert.Equal(1L, conn.ConnectionNumber);
    }

    // Usage and update state are read on connecting, before the sessions are subscribed. One the
    // app could not read ended the connection there, and every connection after read the same
    // thing: it reconnected for good and never showed a session.
    [Fact]
    public async Task Usage_or_update_state_the_app_cannot_read_is_refused_and_the_sessions_still_arrive()
    {
        await using var odd = new FakeHost();
        odd.Capabilities.Add(UpdateProtocol.Capability);
        odd.Sessions.Add(Make.Session("s1"));
        odd.On(UsageProtocol.Subscribe, _ => "none");
        odd.On(UpdateProtocol.Subscribe, _ => new System.Text.Json.Nodes.JsonArray());
        odd.Start();
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var conn = new HostConnection(ct => LocalClient.ConnectAsync(odd.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher(), logged.Enqueue) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        conn.Start();

        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Get("s1") is not null, "the snapshot");
        Assert.Equal(1L, conn.ConnectionNumber);
        Assert.Single(logged, l => l.StartsWith($"refused a {UsageProtocol.Subscribe} ", StringComparison.Ordinal));
        Assert.Single(logged, l => l.StartsWith($"refused a {UpdateProtocol.Subscribe} ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_newer_status_event_wins_over_a_manual_read_waiting_for_the_UI()
    {
        var ui = new HoldingDispatcher();
        await using var watching = new HostConnection(ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromSeconds(1), ct), ui)
        {
            CapabilityRefresh = TimeSpan.FromHours(1),
        };
        var synced = false;
        watching.Sessions.Synced += () => synced = true;
        watching.Start();
        await Wait.UntilAsync(() => watching.State == LinkState.Connected && watching.Status is not null && synced, "the initial snapshot");
        var shown = new List<int>();
        watching.Changed += () => shown.Add(watching.Status!.ProcessId);
        ui.Hold();
        try
        {
            host.Status = host.Status with { ProcessId = 111 };
            await watching.RefreshStatusAsync();
            Assert.Equal(1, ui.QueuedCount);
            await host.SendAsync(HostOps.StatusEvent, host.Status with { ProcessId = 222 });
            await Wait.UntilAsync(() => ui.QueuedCount == 2, "the newer status callback queued");
        }
        finally { ui.Release(); }

        Assert.Equal(222, watching.Status!.ProcessId);
        Assert.DoesNotContain(111, shown);
    }

    [Fact]
    public async Task A_manual_status_read_from_a_closed_connection_is_not_applied_later()
    {
        var ui = new HoldingDispatcher();
        await using var watching = host.Connect(ui);
        await Wait.UntilAsync(() => watching.State == LinkState.Connected && watching.Status is not null, "the status");
        var shown = new List<int>();
        watching.Changed += () => shown.Add(watching.Status!.ProcessId);
        ui.Hold();
        try
        {
            host.Status = host.Status with { ProcessId = 111 };
            await watching.RefreshStatusAsync();
            await host.StopAsync();
            await Wait.UntilAsync(() => watching.HostInstance is null, "the connection closed");
        }
        finally { ui.Release(); }

        Assert.DoesNotContain(111, shown);
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
    public async Task A_snapshot_whose_sessions_synced_comes_after_lost_events_is_not_taken_as_whole_and_is_asked_for_again()
    {
        await using var other = new FakeHost();
        other.Sessions.Add(Make.Session("a"));
        other.Sessions.Add(Make.Session("b"));
        other.Start();
        await using var conn = other.Connect(start: false);
        var synced = 0;
        conn.Sessions.Synced += () => Interlocked.Increment(ref synced);
        conn.Start();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Complete, "the first snapshot");

        // A gap asks for the sessions again, and events are lost before that snapshot's end.
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.OnSubscribe = n => other.HoldSynced = n switch { 2 => second, 3 => third, _ => null };
        Skip(other, 3);
        await other.SendAsync(Events.SessionUpsert, Make.Session("a"));
        await Wait.UntilAsync(() => other.Sent(Ops.SessionsSubscribe).Count == 2, "the sessions asked for again");
        Skip(other, 1);
        second.SetResult();

        await Wait.UntilAsync(() => other.Sent(Ops.SessionsSubscribe).Count == 3, "the sessions asked for a third time");
        Assert.Equal(1, Volatile.Read(ref synced));
        Assert.False(conn.Sessions.Complete);
        third.SetResult();
        await Wait.UntilAsync(() => conn.Sessions.Complete, "the third snapshot whole");
        Assert.Equal(2, Volatile.Read(ref synced));
    }

    [Fact]
    public async Task A_sessions_synced_the_subscription_before_still_had_on_its_way_does_not_end_the_snapshot_a_gap_asked_for()
    {
        await using var other = new FakeHost();
        other.Sessions.Add(Make.Session("a"));
        other.Sessions.Add(Make.Session("b"));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.OnSubscribe = n =>
        {
            other.HoldSynced = n == 1 ? first : null;
            // Asked again before the first snapshot ended: its end goes out ahead of this answer,
            // as a host's does when it stops one subscription to answer the next.
            if (n == 2)
            {
                first.SetResult();
                SpinWait.SpinUntil(() => other.SyncedSent == 1, FakeHost.Patience);
            }
        };
        other.Start();
        await using var conn = other.Connect(start: false);
        var synced = 0;
        var removed = new List<string>();
        conn.Sessions.Synced += () => Interlocked.Increment(ref synced);
        conn.Sessions.Removed += id => { lock (removed) { removed.Add(id); } };
        conn.Start();
        await Wait.UntilAsync(() => conn.Sessions.Get("a") is not null && conn.Sessions.Get("b") is not null, "the first snapshot's sessions");

        Skip(other, 3);
        await other.SendAsync(Events.SessionUpsert, Make.Session("a"));

        await Wait.UntilAsync(() => other.SyncedSent == 2 && conn.Sessions.Complete, "the second snapshot whole");
        lock (removed) { Assert.Empty(removed); }
        Assert.Equal(1, Volatile.Read(ref synced));
        Assert.NotNull(conn.Sessions.Get("b"));
    }

    [Fact]
    public async Task An_upsert_the_subscription_before_sent_ahead_of_the_new_answer_does_not_keep_its_session_past_the_new_snapshot()
    {
        await using var other = new FakeHost();
        other.Sessions.Add(Make.Session("a"));
        other.OnSubscribe = n =>
        {
            // Asked again: the old subscription's last word is an upsert of a session that ends
            // before the new snapshot is taken, so the new one doesn't list it.
            // Sent on the channel only: FakeHost.SendAsync would list it, and the new snapshot with it.
            if (n == 2)
            {
                foreach (var ch in other.Channels.Distinct().Where(c => c.IsOpen)) { ch.SendEventAsync(Events.SessionUpsert, Make.Session("ghost")).GetAwaiter().GetResult(); }
            }
        };
        other.Start();
        await using var conn = other.Connect();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Complete, "the first snapshot");

        Skip(other, 3);
        await other.SendAsync(Events.SessionUpsert, Make.Session("a"));

        await Wait.UntilAsync(() => other.SyncedSent == 2 && conn.Sessions.Complete, "the second snapshot whole");
        Assert.Null(conn.Sessions.Get("ghost"));
        Assert.NotNull(conn.Sessions.Get("a"));
    }

    [Fact]
    public async Task A_gap_the_replaced_channel_reads_late_does_not_hold_the_rows_of_the_channel_that_replaced_it()
    {
        await using var other = new FakeHost();
        other.Sessions.Add(Make.Session("a"));
        other.Start();
        var ui = new LockedDispatcher();
        var channels = new List<GatedChannel>();
        await using var conn = new HostConnection(async ct =>
        {
            var g = new GatedChannel(await LocalClient.ConnectAsync(other.Endpoint, TimeSpan.FromSeconds(1), ct));
            lock (channels) { channels.Add(g); }
            return g;
        }, ui) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        var machine = new Machine("remote-id", conn, new CardContext { Deliveries = new Deliveries(conn, ui), Folds = new FoldStore(null), Capabilities = () => conn.Capabilities, Host = conn });
        conn.Start();
        await Wait.UntilAsync(() => machine.Current, "the first channel's rows confirmed");

        // Its pump falls behind with a gap still to read, and the channel is replaced meanwhile.
        var first = channels[0];
        first.Hold();
        Skip(other, 3);
        await other.SendAsync(Events.SessionUpsert, Make.Session("a"));
        await Wait.UntilAsync(() => first.Holding > 0, "the gap read off the first channel");
        await other.StopAsync();
        other.Start();
        await Wait.UntilAsync(() => channels.Count == 2 && machine.Current, "the second channel's rows confirmed");

        first.Release();
        await Wait.UntilAsync(() => first.Drained, "the first channel's pump to read the gap");
        await Wait.UntilAsync(() => first.Handled, "the first channel's pump to have handled it");
        Assert.True(TestUi.Read(() => machine.Current));
    }

    // A channel whose events the test can hold back from the app's pump, as a pump that falls behind.
    sealed class GatedChannel : IClientChannel
    {
        readonly IClientChannel inner;
        readonly System.Threading.Channels.Channel<AgentBridge.Transport.ChannelEvent> events = System.Threading.Channels.Channel.CreateUnbounded<AgentBridge.Transport.ChannelEvent>();
        volatile TaskCompletionSource? held;
        int holding;
        readonly PumpedReader reader;

        // The events as the app's pump reads them (HostConnection.PumpAsync: await foreach over
        // ReadAllAsync, each event handled before the next is asked for). The enumeration ends, and
        // Done is set, only once the pump has finished with the last event it was given.
        sealed class PumpedReader(System.Threading.Channels.ChannelReader<AgentBridge.Transport.ChannelEvent> inner)
            : System.Threading.Channels.ChannelReader<AgentBridge.Transport.ChannelEvent>
        {
            volatile bool done;
            public bool Done => done;
            public override Task Completion => inner.Completion;
            public override bool TryRead([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out AgentBridge.Transport.ChannelEvent item) => inner.TryRead(out item);
            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => inner.WaitToReadAsync(cancellationToken);
            public override async IAsyncEnumerable<AgentBridge.Transport.ChannelEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                try
                {
                    await foreach (var e in inner.ReadAllAsync(cancellationToken)) { yield return e; }
                }
                finally { done = true; }
            }
        }

        public GatedChannel(IClientChannel inner)
        {
            this.inner = inner;
            reader = new PumpedReader(events.Reader);
            _ = Task.Run(async () =>
            {
                await foreach (var e in inner.Events.ReadAllAsync())
                {
                    if (held is { } h) { Interlocked.Increment(ref holding); await h.Task; }
                    events.Writer.TryWrite(e);
                }
                events.Writer.TryComplete();
            });
        }

        public int Holding => Volatile.Read(ref holding);
        // Everything passed on and read by the pump.
        public bool Drained => events.Reader.Completion.IsCompleted;
        // The pump has finished with this channel's events, the last one handled: it came back for
        // more and found none, or let go of them.
        public bool Handled => reader.Done;
        public void Hold() => held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() { var h = held; held = null; h?.TrySetResult(); }

        public System.Threading.Channels.ChannelReader<AgentBridge.Transport.ChannelEvent> Events => reader;
        public Exception? CloseReason => inner.CloseReason;
        public string PeerId => inner.PeerId;
        public string PeerName => inner.PeerName;
        public string PeerKind => inner.PeerKind;
        public bool IsOpen => inner.IsOpen;
        public Task Closed => inner.Closed;
        public Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null) => inner.RequestAsync(op, data, timeout, ct, progress);
        public Task<(JsonElement Response, long EventsBefore)> RequestOrderedAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null) => inner.RequestOrderedAsync(op, data, timeout, ct, progress);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => inner.SendEventAsync(name, data, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public void A_discovery_hold_reported_after_a_sessions_synced_that_waits_on_its_answer_is_not_undone_when_the_snapshot_finishes()
    {
        var store = new SessionStore();
        var on = new object();
        var older = new DiscoveryHeld("the claude processes could not be listed", [AgentKind.Claude]);
        var newer = new DiscoveryHeld("an unrecognised copilot process (pid 4120)", [AgentKind.Copilot]);
        store.BeginSnapshot(on);
        // Read on the pump before the subscribe's answer is applied: the synced waits for it.
        store.EndSnapshot(older, on, 5);
        store.SetHeld(newer, on);

        store.Answered(on, 4);

        Assert.True(store.Complete);
        Assert.Equal(newer, store.Held);
    }

    [Fact]
    public void A_discovery_change_from_another_channel_does_not_rewrite_a_pending_snapshot_end()
    {
        var store = new SessionStore();
        var on = new object();
        var pending = new DiscoveryHeld("the snapshot's hold", [AgentKind.Claude]);
        var other = new DiscoveryHeld("another channel's hold", [AgentKind.Copilot]);
        store.BeginSnapshot(on);
        store.EndSnapshot(pending, on, 5);
        store.SetHeld(other, new object());
        Assert.Equal(other, store.Held);

        store.Answered(on, 4);

        Assert.True(store.Complete);
        Assert.Equal(pending, store.Held);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_writes_queued_by_a_replaced_channel_are_dropped_after_the_new_snapshot(bool staleHold)
    {
        var older = staleHold ? new DiscoveryHeld("old channel's hold", [AgentKind.Claude]) : null;
        var newer = staleHold ? null : new DiscoveryHeld("new channel's hold", [AgentKind.Copilot]);
        await using var other = new FakeHost { Held = older };
        other.Sessions.Add(Make.Session("a", title: "Before reconnect"));
        other.Start();
        var ui = new HoldingDispatcher();
        await using var conn = new HostConnection(ct => LocalClient.ConnectAsync(other.Endpoint, TimeSpan.FromSeconds(1), ct), ui)
        {
            RetryDelay = TimeSpan.Zero,
            CapabilityRefresh = TimeSpan.FromHours(1),
        };
        conn.Start();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Complete, "the first snapshot");
        ui.Hold();
        try
        {
            // These are wire-only events: the next snapshot must not include their stale state.
            var old = Assert.Single(other.Channels.Where(c => c.IsOpen).Distinct());
            await old.SendEventAsync(Events.SessionUpsert, Make.Session("ended"));
            await old.SendEventAsync(Events.SessionRemove, new SessionRemoved("a"));
            await old.SendEventAsync(Events.SessionUpsert, new System.Text.Json.Nodes.JsonObject { ["id"] = 7 });
            await old.SendEventAsync(Events.DiscoveryChanged, new DiscoveryChanged(older));
            await Wait.UntilAsync(() => ui.QueuedCount == 4, "the old channel's session writes queued");
            var delayed = ui.TakeHeld();

            other.Sessions[0] = Make.Session("a", title: "After reconnect");
            other.Held = newer;
            await other.StopAsync();
            other.Start();
            await Wait.UntilAsync(() => conn.ConnectionNumber == 2 && conn.Sessions.Snapshots == 2
                && conn.Sessions.Complete && conn.Sessions.Get("a")?.Title == "After reconnect", "the replacement snapshot");
            Assert.Equal(newer?.Reason, conn.Sessions.Held?.Reason);
            Assert.Equal(newer?.Agents ?? [], conn.Sessions.Held?.Agents ?? []);
            var changes = 0;
            conn.Sessions.HeldChanged += () => changes++;

            // Apply after the new snapshot, even though the old pump posted while still followed.
            foreach (var action in delayed) { ui.Post(action); }

            Assert.Null(conn.Sessions.Get("ended"));
            Assert.Equal("After reconnect", conn.Sessions.Get("a")!.Title);
            Assert.False(conn.Sessions.Unsure);
            Assert.True(conn.Sessions.Complete);
            Assert.Equal(newer?.Reason, conn.Sessions.Held?.Reason);
            Assert.Equal(newer?.Agents ?? [], conn.Sessions.Held?.Agents ?? []);
            Assert.Equal(0, changes);

            var current = new DiscoveryHeld("current channel's next change", [AgentKind.Codex]);
            await other.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged(current));
            await Wait.UntilAsync(() => conn.Sessions.Held?.Reason == current.Reason, "the current channel's discovery change");
            Assert.Equal(current.Agents, conn.Sessions.Held!.Agents);
        }
        finally { ui.Release(); }
    }

    static void Skip(FakeHost h, long count)
    {
        foreach (var ch in h.Channels.OfType<LocalChannel>().Where(c => c.IsOpen).Distinct()) { ch.SkipEventSeq(count); }
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

    // #640: every app asked every host again every 10 s, ~36 hellos a minute at a host from other
    // machines. A host that says when hello changes is asked again when it says so.
    [Fact]
    public async Task A_host_that_says_when_hello_changes_is_asked_again_when_it_says_so_and_not_every_few_seconds()
    {
        // Another machine's host: no host.* ops, which this machine's own host is still asked
        // for every few seconds.
        await using var fresh = new FakeHost();
        fresh.Capabilities.Remove(HostOps.Capability);
        fresh.Capabilities.Remove(HostOps.AgentReadinessCapability);
        fresh.Capabilities.Add(Capabilities.HelloChanged);
        fresh.Start();
        await using var watching = new HostConnection(ct => LocalClient.ConnectAsync(fresh.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher())
        {
            RetryDelay = TimeSpan.FromMilliseconds(100),
            CapabilityRefresh = TimeSpan.FromMilliseconds(100),
            ChangesFallback = TimeSpan.FromHours(1),
        };
        watching.Start();
        await Wait.UntilAsync(() => watching.State == LinkState.Connected && watching.Can(Capabilities.SessionsAnswer), "the first hello");
        // Ten refreshes' worth: a host that doesn't say would have been asked again by now.
        await Task.Delay(1000);
        var hellos = fresh.Sent(Ops.Hello);
        Assert.Single(hellos);
        Assert.True(hellos[0].GetProperty("changes").GetBoolean());

        // agent-ha-bridge started there: the host says so, and is asked what it offers now.
        var readOnly = new HelloResult(1, new MachineInfo("host-id", "Test PC", "windows", "0.1.0"), [Capabilities.SessionsRead, Capabilities.HelloChanged]);
        fresh.On(Ops.Hello, _ => readOnly);
        await fresh.SendAsync(Events.HelloChanged, new { });

        await Wait.UntilAsync(() => !watching.Can(Capabilities.SessionsAnswer), "answering to be taken away");
        Assert.Equal(2, fresh.Sent(Ops.Hello).Count);

        // Only the reason changed (a host already read-only by setting): still taken up.
        const string Reason = "agent-ha-bridge is running for this user";
        fresh.On(Ops.Hello, _ => readOnly with { Machine = readOnly.Machine with { ReadOnly = Reason } });
        await fresh.SendAsync(Events.HelloChanged, new { });
        await Wait.UntilAsync(() => watching.Hello?.Machine.ReadOnly == Reason, "the new read-only reason");
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
    public async Task A_session_the_host_announced_is_still_listed_when_the_app_reads_the_sessions_again()
    {
        await Wait.UntilAsync(() => Volatile.Read(ref syncs) > 0 && connection.Sessions.Get("s1") is not null, "the snapshot");
        await host.SendAsync(Events.SessionUpsert, Make.Session("s2"));
        await Wait.UntilAsync(() => connection.Sessions.Get("s2") is not null, "the announced session");
        var before = Volatile.Read(ref syncs);

        // As after a gap in the events or a reconnect: a host's snapshot lists what it announced,
        // and the fake's did not, so a test's announced session went from the app again (#101).
        connection.Resync();

        await Wait.UntilAsync(() => Volatile.Read(ref syncs) > before, "the sessions read again");
        Assert.NotNull(TestUi.Read(() => connection.Sessions.Get("s2")));
    }

    [Fact]
    public async Task With_no_host_running_the_app_says_so()
    {
        var stopping = DateTime.UtcNow;
        await host.StopAsync();
        var stopped = DateTime.UtcNow;

        // Timed out once on Windows CI in a loaded run (#465), and not reproduced since: the timeout
        // says what the connection went through, so the cause need not be guessed at.
        await Wait.UntilAsync(() => connection.State == LinkState.NotRunning, "not running",
            why: () => $"stop asked {stopping:HH:mm:ss.fff}, done {stopped:HH:mm:ss.fff}; {Seen()}");
    }

    // As agent-executor's host answers a device it has not approved: hello, offering nothing, and
    // a refusal of the sessions asked for next. Asked again every 5 s for hours, each time logged (#602).
    const string NotApproved = "This device is not an explicitly approved operator; approve its public device ID locally.";

    [Fact]
    public async Task A_host_that_refuses_this_device_is_asked_again_only_minutes_later_and_logged_once()
    {
        await using var refusing = new FakeHost();
        refusing.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("pool-id", "Pool", "linux", "0.1.0", ReadOnly: NotApproved), []));
        refusing.On(Ops.SessionsSubscribe, _ => throw new InvalidOperationException(NotApproved));
        refusing.Start();
        var clock = new FakeTimeProvider(Make.T0);
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var conn = new HostConnection(ct => LocalClient.ConnectAsync(refusing.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher(), logged.Enqueue)
        {
            RetryDelay = TimeSpan.FromMilliseconds(100),
            RefusedRetryDelay = TimeSpan.FromMinutes(5),
            Time = clock,
        };
        conn.Start();
        await Wait.UntilAsync(() => conn.State == LinkState.Refused, "the refusal");
        Assert.Equal(NotApproved, conn.StateDetail);

        // Nothing more is asked until the long delay has passed. Advanced a step at a time, since the
        // wait's timer starts only once the refused channel has closed.
        await Wait.UntilAsync(() =>
        {
            if (refusing.Sent(Ops.Hello).Count >= 2) { return true; }
            clock.Advance(TimeSpan.FromSeconds(30));
            return false;
        }, "the second attempt");
        Assert.True(clock.GetUtcNow() - Make.T0 >= TimeSpan.FromMinutes(5), $"asked again after {clock.GetUtcNow() - Make.T0}");
        // A third attempt begins only after the second's refusal was taken in (and logged, or not).
        await Wait.UntilAsync(() =>
        {
            if (refusing.Sent(Ops.Hello).Count >= 3) { return true; }
            clock.Advance(TimeSpan.FromMinutes(5));
            return false;
        }, "the third attempt");
        Assert.Single(logged, l => l.Contains(NotApproved));
        Assert.DoesNotContain(logged, l => l.StartsWith("host connection failed"));
    }

    [Fact]
    public async Task A_host_that_offers_its_sessions_but_fails_to_subscribe_is_still_asked_again_after_the_short_delay()
    {
        // Not a refusal: a host that lets this device read its sessions and fails for now (its
        // subscription slots full) is asked again as before, each failure logged.
        await using var busy = new FakeHost();
        busy.On(Ops.SessionsSubscribe, _ => throw new InvalidOperationException("The bounded subscription slots are occupied."));
        busy.Start();
        var clock = new FakeTimeProvider(Make.T0);
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var conn = new HostConnection(ct => LocalClient.ConnectAsync(busy.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher(), logged.Enqueue)
        {
            RetryDelay = TimeSpan.FromMilliseconds(100),
            RefusedRetryDelay = TimeSpan.FromMinutes(5),
            Time = clock,
        };
        conn.Start();

        await Wait.UntilAsync(() =>
        {
            if (busy.Sent(Ops.SessionsSubscribe).Count >= 3) { return true; }
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return false;
        }, "three attempts");
        Assert.True(clock.GetUtcNow() - Make.T0 < TimeSpan.FromMinutes(1), $"{clock.GetUtcNow() - Make.T0} passed");
        Assert.True(logged.Count(l => l.StartsWith("host connection failed")) >= 2);
        Assert.NotEqual(LinkState.Refused, conn.State);
    }

    [Fact]
    public async Task An_answer_lost_to_a_host_restart_is_sent_again_with_the_same_request()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        var first = true;
        var gate = new TaskCompletionSource();
        host.OnAsync(Ops.SessionAnswer, async data =>
        {
            // Held until the pipe has closed, so this first outcome is surely lost: a fixed sleep
            // lost the race to a slow runner's stop, and the outcome got through (CI on #81).
            if (first) { first = false; gate.SetResult(); await host.UntilClosed(); }
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
        host.OnAsync(Ops.SessionReply, async data =>
        {
            // Held until the pipe has closed, so this first outcome is surely lost: a fixed sleep
            // lost the race to a slow runner's stop, and the outcome got through (CI on #81).
            if (first) { first = false; gate.SetResult(); await host.UntilClosed(); }
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
        remote.OnAsync(Ops.SessionReply, async data =>
        {
            // Held until the pipe has closed, so this first outcome is surely lost.
            if (first) { first = false; gate.SetResult(); await remote.UntilClosed(); }
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

    // An older host over the relay: no host.manage and no instance in its hello, so nothing names
    // its run. Each connection counts as a new one: a lost reply is not confirmed, never sent again,
    // even when it was the same process all along.
    [Fact]
    public async Task A_host_that_names_no_run_gets_a_lost_reply_only_once()
    {
        await using var remote = new FakeHost();
        remote.Capabilities.Remove(HostOps.Capability);
        remote.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "linux", "0.1.0"), [.. remote.Capabilities]));
        remote.Sessions.Add(Make.Session("s1"));
        remote.Start();
        await using var link = remote.Connect();
        await Wait.UntilAsync(() => link.State == LinkState.Connected && link.HostInstance is not null, "connected");
        Assert.StartsWith("connection ", link.HostInstance);
        var first = true;
        var gate = new TaskCompletionSource();
        remote.OnAsync(Ops.SessionReply, async data =>
        {
            if (first) { first = false; gate.SetResult(); await remote.UntilClosed(); }
            return new ReplyResult(AnswerOutcome.Delivered);
        });
        var deliveries = new Deliveries(link, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, replayAfterRestart: false);
        await gate.Task.WaitAsync(FakeHost.Patience);
        await remote.StopAsync();
        remote.Start();

        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");
        Assert.Equal("unconfirmed", deliveries.Get(entry.Key)!.Outcome);
        Assert.Single(remote.Sent(Ops.SessionReply));
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
    public async Task A_try_again_long_after_the_first_send_of_a_launch_or_reply_sends_nothing_more()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        host.On(Ops.SessionReply, _ => throw new InvalidOperationException("the host failed after reading it"));
        var clock = new FakeTimeProvider(Make.T0);
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { GiveUp = TimeSpan.FromMinutes(9) };

        deliveries.Submit("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Failed, "the failure");
        clock.Advance(TimeSpan.FromMinutes(10));
        deliveries.Resend("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" });

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the give-up");
        Assert.Equal("unconfirmed", deliveries.Get("k")!.Outcome);
        Assert.StartsWith("too long has passed since it was first sent", deliveries.Get("k")!.Reason);
        Assert.Single(host.Sent(Ops.SessionReply));
    }

    [Fact]
    public async Task A_try_again_whose_files_outlast_the_first_sends_window_sends_nothing_more()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        host.On(Ops.SessionReply, _ => throw new InvalidOperationException("the host failed after reading it"));
        var clock = new FakeTimeProvider(Make.T0);
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { GiveUp = TimeSpan.FromMinutes(9) };
        var slow = false;
        Task Files(Func<string, object?, TimeSpan?, Task<JsonElement>> call, Action<long, long> progress)
        {
            if (slow) { clock.Advance(TimeSpan.FromMinutes(3)); }
            return Task.CompletedTask;
        }

        deliveries.Submit("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, before: Files, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Failed, "the failure");
        clock.Advance(TimeSpan.FromMinutes(8));
        slow = true;
        deliveries.Resend("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, Files);

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the give-up");
        Assert.Equal("unconfirmed", deliveries.Get("k")!.Outcome);
        Assert.Single(host.Sent(Ops.SessionReply));
    }

    [Fact]
    public async Task A_try_again_still_waiting_when_the_first_sends_window_closes_reads_unconfirmed()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        var calls = 0;
        var gate = new TaskCompletionSource();
        host.OnAsync(Ops.SessionReply, async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) { throw new InvalidOperationException("the host failed after reading it"); }
            await FakeHost.Until(gate.Task);
            return new ReplyResult(AnswerOutcome.Delivered);
        });
        var clock = new FakeTimeProvider(Make.T0);
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { GiveUp = TimeSpan.FromMinutes(9) };

        deliveries.Submit("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Failed, "the failure");
        clock.Advance(TimeSpan.FromMinutes(8));
        deliveries.Resend("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" });
        await Wait.UntilAsync(() => host.Sent(Ops.SessionReply).Count == 2, "the try again to reach the host");
        clock.Advance(TimeSpan.FromMinutes(2));

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the give-up");
        gate.SetResult();
        Assert.Equal("unconfirmed", deliveries.Get("k")!.Outcome);
        Assert.StartsWith("too long has passed since it was first sent", deliveries.Get("k")!.Reason);
    }

    [Fact]
    public async Task A_wall_clock_stepped_back_does_not_reopen_the_window_for_a_try_again()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        host.On(Ops.SessionReply, _ => throw new InvalidOperationException("the host failed after reading it"));
        var clock = new SteppedClock(new FakeTimeProvider(Make.T0));
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { GiveUp = TimeSpan.FromMinutes(9) };

        deliveries.Submit("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Failed, "the failure");
        // Ten minutes pass, but the wall clock is set back an hour meanwhile (a time sync, by hand).
        clock.Inner.Advance(TimeSpan.FromMinutes(10));
        clock.Step = TimeSpan.FromHours(-1);
        deliveries.Resend("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" });

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the give-up");
        Assert.Equal("unconfirmed", deliveries.Get("k")!.Outcome);
        Assert.Single(host.Sent(Ops.SessionReply));
    }

    [Fact]
    public async Task A_wall_clock_stepped_back_does_not_keep_a_first_send_going_past_its_limit()
    {
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State == LinkState.NotRunning, "not running");
        var clock = new SteppedClock(new FakeTimeProvider(Make.T0));
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { RetryDelay = TimeSpan.FromMilliseconds(20), GiveUp = TimeSpan.FromMinutes(9) };

        deliveries.Submit("k", Ops.SessionAnswer, r => new { request = r }, _ => true);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Waiting, "waiting");
        clock.Step = TimeSpan.FromHours(-1);
        clock.Inner.Advance(TimeSpan.FromMinutes(10));

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the give-up", TimeSpan.FromSeconds(30));
        Assert.Equal("unconfirmed", deliveries.Get("k")!.Outcome);
    }

    // Monotonic time and timers from the fake; the wall clock shifted by Step, as a clock set back.
    sealed class SteppedClock(FakeTimeProvider inner) : TimeProvider
    {
        public FakeTimeProvider Inner => inner;
        public TimeSpan Step { get; set; }
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Step;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task An_automatic_resend_whose_files_outlast_the_first_sends_window_sends_nothing_more()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        var first = true;
        var gate = new TaskCompletionSource();
        host.OnAsync(Ops.SessionReply, async _ =>
        {
            if (first) { first = false; gate.SetResult(); await host.UntilClosed(); }
            return new ReplyResult(AnswerOutcome.Delivered);
        });
        var clock = new FakeTimeProvider(Make.T0);
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { RetryDelay = TimeSpan.FromMilliseconds(100), GiveUp = TimeSpan.FromMinutes(9) };
        var uploads = 0;
        Task Files(Func<string, object?, TimeSpan?, Task<JsonElement>> call, Action<long, long> progress)
        {
            // The second time, the files take ten minutes to go again.
            if (Interlocked.Increment(ref uploads) == 2) { clock.Advance(TimeSpan.FromMinutes(10)); }
            return Task.CompletedTask;
        }

        deliveries.Submit("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, before: Files, replayAfterRestart: false);
        await gate.Task.WaitAsync(FakeHost.Patience);
        await host.StopAsync();
        host.Start();

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the give-up");
        Assert.Equal("unconfirmed", deliveries.Get("k")!.Outcome);
        Assert.Equal(2, uploads);
        Assert.Single(host.Sent(Ops.SessionReply));
    }

    [Fact]
    public async Task A_try_again_while_the_machine_is_away_waits_only_for_what_is_left_of_its_window()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        host.On(Ops.SessionReply, _ => throw new InvalidOperationException("the host failed after reading it"));
        var clock = new FakeTimeProvider(Make.T0);
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { RetryDelay = TimeSpan.FromMilliseconds(20), GiveUp = TimeSpan.FromMinutes(9) };

        deliveries.Submit("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Failed, "the failure");
        await host.StopAsync();
        await Wait.UntilAsync(() => connection.State == LinkState.NotRunning, "not running");
        clock.Advance(TimeSpan.FromMinutes(8));
        deliveries.Resend("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" });
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Waiting, "waiting for the machine");
        clock.Advance(TimeSpan.FromMinutes(2));

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the give-up");
        Assert.Equal("unconfirmed", deliveries.Get("k")!.Outcome);
        Assert.StartsWith("too long has passed since it was first sent", deliveries.Get("k")!.Reason);
    }

    [Fact]
    public async Task A_reply_whose_files_never_went_can_be_tried_again_however_long_after()
    {
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        host.On(Ops.SessionReply, _ => new ReplyResult(AnswerOutcome.Delivered));
        var clock = new FakeTimeProvider(Make.T0);
        var deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { GiveUp = TimeSpan.FromMinutes(9) };
        var upload = true;
        Task Files(Func<string, object?, TimeSpan?, Task<JsonElement>> call, Action<long, long> progress) =>
            upload ? throw new HostErrorException("the storage could not be reached") : Task.CompletedTask;

        deliveries.Submit("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, _ => false, before: Files, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Failed, "the failure");
        upload = false;
        clock.Advance(TimeSpan.FromMinutes(10));
        deliveries.Resend("k", Ops.SessionReply, r => new { id = "s1", request = r, text = "run the tests" }, Files);

        await Wait.UntilAsync(() => deliveries.Get("k")!.State == DeliveryState.Done, "the outcome");
        Assert.Equal("delivered", deliveries.Get("k")!.Outcome);
        Assert.Single(host.Sent(Ops.SessionReply));
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

    // A host that offers session.patch (#601), in one run of it.
    static FakeHost Patching(params Session[] sessions)
    {
        var h = new FakeHost { Instance = "run-1" };
        h.Capabilities.Add(Capabilities.SessionsPatch);
        h.Sessions.AddRange(sessions);
        return h.Start();
    }

    static bool Same(Session? held, Session sent) =>
        held is not null && JsonSerializer.Serialize(held, ProtocolJson.Options) == JsonSerializer.Serialize(sent, ProtocolJson.Options);

    static Subagent Sub(string id, SubagentStatus status) => new() { Id = id, Kind = SubagentKind.Task, Name = id, Status = status, StartedAt = Make.T0 };

    static string[] Ids(SessionStore store) => [.. store.All.Select(s => s.Id).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_host_that_offers_patches_sends_its_live_changes_as_patches_that_leave_each_session_as_its_upsert_would()
    {
        var first = Make.Session("a", response: "Reading") with { Subagents = [Sub("d1", SubagentStatus.Working), Sub("d2", SubagentStatus.Working)] };
        await using var other = Patching(first, Make.Session("b"));
        await using var conn = other.Connect();
        await Wait.UntilAsync(() => conn.Sessions.Complete, "the snapshot");
        var asked = other.Sent(Ops.SessionsSubscribe).Single();
        Assert.True(asked.GetProperty("patch").GetBoolean());
        Assert.False(asked.TryGetProperty("since", out _));

        // Text that grew, a status, one delegate of two ended and another gone.
        var working = first with { Status = SessionStatus.Working, Activity = first.Activity! with { Response = "Reading the docs" }, Subagents = [Sub("d1", SubagentStatus.Ended), Sub("d2", SubagentStatus.Working)] };
        var last = working with { Status = SessionStatus.Idle, Subagents = [Sub("d2", SubagentStatus.Working)] };
        await other.SendAsync(Events.SessionUpsert, working);
        await other.SendAsync(Events.SessionUpsert, last);
        await other.SendAsync(Events.SessionRemove, new SessionRemoved("b"));

        await Wait.UntilAsync(() => conn.Sessions.Get("b") is null && Same(conn.Sessions.Get("a"), last), "the patches applied");
        Assert.Equal(2, other.EventsSent.Count(e => e.Name == Events.SessionPatch));
        Assert.Equal(2, other.EventsSent.Count(e => e.Name == Events.SessionUpsert));
        Assert.Equal(1, conn.Sessions.Snapshots);
    }

    [Fact]
    public async Task A_patch_on_a_base_the_app_does_not_hold_is_dropped_and_the_sessions_are_asked_for_again_in_full()
    {
        await using var other = Patching(Make.Session("a"));
        await using var conn = other.Connect(start: false);
        var gapped = 0;
        conn.Gapped += () => Interlocked.Increment(ref gapped);
        conn.Start();
        await Wait.UntilAsync(() => conn.Sessions.Complete, "the snapshot");

        await other.SendAsync(Events.SessionPatch, new SessionPatch { Id = "a", Base = 9999, Set = JsonSerializer.SerializeToElement(new { status = "working" }) });

        await Wait.UntilAsync(() => other.Sent(Ops.SessionsSubscribe).Count == 2 && conn.Sessions.Complete, "a full snapshot");
        var again = other.Sent(Ops.SessionsSubscribe)[1];
        Assert.True(again.GetProperty("patch").GetBoolean());
        Assert.False(again.TryGetProperty("since", out _));
        Assert.Equal(SessionStatus.Idle, conn.Sessions.Get("a")!.Status);
        Assert.Equal(1, Volatile.Read(ref gapped));
    }

    [Fact]
    public async Task A_reconnect_to_the_same_run_of_the_host_is_sent_only_the_sessions_that_changed_and_drops_the_ones_that_ended()
    {
        await using var other = Patching(Make.Session("a"), Make.Session("b"), Make.Session("c"));
        await using var conn = other.Connect();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Complete, "the first snapshot");
        await other.SendAsync(Events.SessionUpsert, Make.Session("a", status: SessionStatus.Working));
        await Wait.UntilAsync(() => conn.Sessions.Get("a")?.Status == SessionStatus.Working, "the patch");
        var synced = 0;
        conn.Sessions.Synced += () => Interlocked.Increment(ref synced);

        await other.StopAsync();
        await Wait.UntilAsync(() => conn.State != LinkState.Connected, "the host to go");
        // While the app was away: b ended, c changed, d started.
        await other.SendAsync(Events.SessionRemove, new SessionRemoved("b"));
        await other.SendAsync(Events.SessionUpsert, Make.Session("c", status: SessionStatus.Working));
        await other.SendAsync(Events.SessionUpsert, Make.Session("d"));
        var before = other.EventsSent.Count;
        other.Start();

        await Wait.UntilAsync(() => conn.State == LinkState.Connected && Volatile.Read(ref synced) == 1, "the resumed snapshot");
        var since = other.Sent(Ops.SessionsSubscribe)[^1].GetProperty("since");
        Assert.Equal("run-1", since.GetProperty("instance").GetString());
        Assert.Equal(1, since.GetProperty("revision").GetInt64());
        string[] resent = [.. other.EventsSent.Skip(before).Where(e => e.Name == Events.SessionUpsert).Select(e => e.Data.GetProperty("id").GetString()!).Order(StringComparer.Ordinal)];
        Assert.Equal(["c", "d"], resent);
        Assert.Equal(["a", "c", "d"], Ids(conn.Sessions));
        Assert.Equal(SessionStatus.Working, conn.Sessions.Get("a")!.Status);
        Assert.Equal(SessionStatus.Working, conn.Sessions.Get("c")!.Status);
        Assert.True(conn.Sessions.Complete);
    }

    [Fact]
    public async Task A_gap_in_the_events_is_recovered_from_the_revision_the_list_was_whole_at_before_it()
    {
        await using var other = Patching(Make.Session("a"), Make.Session("b"));
        await using var conn = other.Connect();
        await Wait.UntilAsync(() => conn.Sessions.Complete, "the snapshot");
        await other.SendAsync(Events.SessionUpsert, Make.Session("a", status: SessionStatus.Working));
        await Wait.UntilAsync(() => conn.Sessions.Get("a")?.Status == SessionStatus.Working, "the patch");

        Skip(other, 3);
        await other.SendAsync(Events.SessionUpsert, Make.Session("b", status: SessionStatus.Working));

        await Wait.UntilAsync(() => other.Sent(Ops.SessionsSubscribe).Count == 2 && conn.Sessions.Complete, "the resumed snapshot");
        // Not the patch that revealed the gap's revision: what came after the gap may not all have arrived.
        Assert.Equal(1, other.Sent(Ops.SessionsSubscribe)[1].GetProperty("since").GetProperty("revision").GetInt64());
        Assert.Equal(SessionStatus.Working, conn.Sessions.Get("b")!.Status);
        Assert.Equal(["a", "b"], Ids(conn.Sessions));
    }

    [Fact]
    public async Task A_resumed_snapshot_naming_a_session_the_app_neither_holds_nor_was_sent_is_not_taken_and_the_full_one_is_asked_for()
    {
        await using var other = Patching(Make.Session("a"));
        await using var conn = other.Connect();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Complete, "the first snapshot");
        await other.StopAsync();
        await Wait.UntilAsync(() => conn.State != LinkState.Connected, "the host to go");
        other.Ghost = "ghost";
        var synced = 0;
        conn.Sessions.Synced += () => Interlocked.Increment(ref synced);
        other.Start();

        await Wait.UntilAsync(() => other.Sent(Ops.SessionsSubscribe).Count == 3 && conn.Sessions.Complete, "the full snapshot");
        Assert.True(other.Sent(Ops.SessionsSubscribe)[1].TryGetProperty("since", out _));
        Assert.False(other.Sent(Ops.SessionsSubscribe)[2].TryGetProperty("since", out _));
        Assert.Equal(1, Volatile.Read(ref synced));
        Assert.Equal(["a"], Ids(conn.Sessions));
    }

    [Fact]
    public async Task A_list_with_a_session_the_app_could_not_read_is_never_resumed()
    {
        await using var other = Patching(Make.Session("a"));
        await using var conn = other.Connect(start: false);
        var synced = 0;
        conn.Sessions.Synced += () => Interlocked.Increment(ref synced);
        conn.Start();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && Volatile.Read(ref synced) == 1, "the snapshot");
        async Task Restart(int subscribes)
        {
            await other.StopAsync();
            await Wait.UntilAsync(() => conn.State != LinkState.Connected, "the host to go");
            other.Start();
            await Wait.UntilAsync(() => other.Sent(Ops.SessionsSubscribe).Count == subscribes && Volatile.Read(ref synced) == subscribes, "the snapshot after it");
        }
        // Whole, the list is resumed.
        await Restart(2);
        Assert.True(other.Sent(Ops.SessionsSubscribe)[1].TryGetProperty("since", out _));

        await other.SendAsync(Events.SessionUpsert, new System.Text.Json.Nodes.JsonObject { ["id"] = 7 });
        await Wait.UntilAsync(() => conn.Sessions.Unsure, "the refusal");
        await Restart(3);

        var third = other.Sent(Ops.SessionsSubscribe)[2];
        Assert.True(third.GetProperty("patch").GetBoolean());
        Assert.False(third.TryGetProperty("since", out _));
    }

    [Fact]
    public void A_session_that_started_after_a_resumed_snapshot_whose_synced_was_read_before_its_answer_is_kept()
    {
        var store = new SessionStore();
        var first = new object();
        store.BeginSnapshot(first, "run-1");
        store.Answered(first, 0);
        store.Upsert(Make.Session("a"), true, first, 1);
        store.EndSnapshot(null, first, 2, 5, null);
        var second = new object();
        store.BeginSnapshot(second, "run-1");

        // Read on the pump before the subscribe's answer reached the UI thread: the resumed synced,
        // and a session that started after it.
        store.EndSnapshot(null, second, 1, 6, ["a"]);
        store.Upsert(Make.Session("b"), true, second, 2);
        store.Answered(second, 0, resumed: true);

        Assert.True(store.Complete);
        Assert.Equal(["a", "b"], Ids(store));
        Assert.Equal(6, store.ResumeFrom("run-1")!.Revision);
    }

    [Fact]
    public async Task An_event_numbered_again_may_be_an_older_state_so_the_list_is_never_resumed_from_after_it()
    {
        await using var other = Patching(Make.Session("a"));
        await using var conn = other.Connect();
        await Wait.UntilAsync(() => conn.Sessions.Complete, "the snapshot");

        Skip(other, -1);
        await other.SendAsync(Events.SessionUpsert, Make.Session("a", status: SessionStatus.Working));

        await Wait.UntilAsync(() => other.Sent(Ops.SessionsSubscribe).Count == 2 && conn.Sessions.Complete, "a full snapshot");
        var again = other.Sent(Ops.SessionsSubscribe)[1];
        Assert.True(again.GetProperty("patch").GetBoolean());
        Assert.False(again.TryGetProperty("since", out _));
    }

    [Fact]
    public async Task A_patch_the_replaced_channel_reads_late_does_not_undo_what_the_channel_that_replaced_it_sent()
    {
        await using var other = Patching(Make.Session("a"));
        var channels = new List<GatedChannel>();
        await using var conn = new HostConnection(async ct =>
        {
            var g = new GatedChannel(await LocalClient.ConnectAsync(other.Endpoint, TimeSpan.FromSeconds(1), ct));
            lock (channels) { channels.Add(g); }
            return g;
        }, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        conn.Start();
        await Wait.UntilAsync(() => conn.Sessions.Complete, "the first snapshot");

        // The first channel's pump falls behind with a change still to read, and is replaced.
        var first = channels[0];
        first.Hold();
        await other.SendAsync(Events.SessionUpsert, Make.Session("a", title: "Older"));
        await Wait.UntilAsync(() => first.Holding > 0, "the change held on the first channel");
        await other.StopAsync();
        other.Start();
        await Wait.UntilAsync(() => channels.Count == 2 && conn.Sessions.Complete && conn.Sessions.Get("a")?.Title == "Older", "the second channel's snapshot");
        await other.SendAsync(Events.SessionUpsert, Make.Session("a", title: "Newer"));
        await Wait.UntilAsync(() => conn.Sessions.Get("a")?.Title == "Newer", "the newer change");

        first.Release();
        await Wait.UntilAsync(() => first.Drained, "the first channel's pump to read its change");
        await Wait.UntilAsync(() => first.Handled, "the first channel's pump to have handled it");
        Assert.Equal("Newer", TestUi.Read(() => conn.Sessions.Get("a")!.Title));
    }

    [Fact]
    public async Task A_synced_naming_the_live_sessions_after_an_answer_that_does_not_say_it_resumed_is_not_taken_for_a_full_snapshot()
    {
        await using var other = Patching(Make.Session("a"), Make.Session("b"));
        await using var conn = other.Connect();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Complete, "the first snapshot");
        await other.StopAsync();
        await Wait.UntilAsync(() => conn.State != LinkState.Connected, "the host to go");
        // Resumed, but saying so in a way the app can't read.
        other.OnSubscribe = n => other.SubscribeReply = n == 2 ? new { revision = 0, count = 0, resumed = "true" } : null;
        other.Start();

        await Wait.UntilAsync(() => other.Sent(Ops.SessionsSubscribe).Count == 3 && conn.Sessions.Complete, "the full snapshot");
        Assert.False(other.Sent(Ops.SessionsSubscribe)[2].TryGetProperty("since", out _));
        Assert.Equal(["a", "b"], Ids(conn.Sessions));
    }

    [Fact]
    public async Task A_session_the_replaced_channel_could_not_read_leaves_the_list_of_the_channel_that_replaced_it_whole()
    {
        await using var other = Patching(Make.Session("a"));
        var channels = new List<GatedChannel>();
        await using var conn = new HostConnection(async ct =>
        {
            var g = new GatedChannel(await LocalClient.ConnectAsync(other.Endpoint, TimeSpan.FromSeconds(1), ct));
            lock (channels) { channels.Add(g); }
            return g;
        }, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        conn.Start();
        await Wait.UntilAsync(() => conn.Sessions.Complete, "the first snapshot");

        var first = channels[0];
        first.Hold();
        await other.SendAsync(Events.SessionUpsert, new System.Text.Json.Nodes.JsonObject { ["id"] = 7 });
        await Wait.UntilAsync(() => first.Holding > 0, "the unreadable session held on the first channel");
        await other.StopAsync();
        other.Start();
        await Wait.UntilAsync(() => channels.Count == 2 && conn.Sessions.Snapshots == 2 && conn.Sessions.Complete, "the second channel's snapshot");

        first.Release();
        await Wait.UntilAsync(() => first.Drained, "the first channel's pump to read it");
        await Wait.UntilAsync(() => first.Handled, "the first channel's pump to have handled it");
        Assert.False(TestUi.Read(() => conn.Sessions.Unsure));
        Assert.NotNull(TestUi.Read(() => conn.Sessions.ResumeFrom("run-1")));
    }
}