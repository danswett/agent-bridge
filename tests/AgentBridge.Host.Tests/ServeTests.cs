using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

// The host's Protocol v1 service end to end: fixture sessions read by the real
// SessionSource, served through the in-process relay over a Noise channel, to a client.
public sealed class ServeTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string Other = "99999999-9999-4999-8999-999999999999";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    TestRelay relay = null!;
    SessionSource source = null!;
    SessionService service = null!;
    Device host = null!;
    Device client = null!;
    string events = "";

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, 100, name: "Served session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("c1", "powershell", new { command = "dir" }, 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        // Real time, not the fixture's fake clock: coalesced upserts must flush on their own
        // while the relay carries them.
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"));

        relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = service.HandleAsync;
        client = await relay.ConnectAsync(token, "Phone", "pwa");
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    async Task<SecureChannel> OpenAsync()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return await client.Hub.OpenAsync(host.Id);
    }

    static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static async Task<ChannelEvent> NextAsync(SecureChannel ch)
    {
        using var cts = new CancellationTokenSource(Patience);
        return await ch.Events.ReadAsync(cts.Token);
    }

    // Reads events up to and including sessions.synced.
    static async Task<List<ChannelEvent>> SnapshotAsync(SecureChannel ch)
    {
        var got = new List<ChannelEvent>();
        do { got.Add(await NextAsync(ch)); } while (got[^1].Name != Events.SessionsSynced);
        return got;
    }

    // A change the client shows, to the fixture's session.
    async Task ChangeAsync(string text, int second)
    {
        Fixture.Append(events, Copilot.ToolDone("c1", second), Copilot.Assistant(text, second + 1));
        await source.RefreshAsync();
    }

    [Fact]
    public async Task Hello_names_the_machine_and_offers_only_reading_sessions()
    {
        var ch = await OpenAsync();
        var hello = Result(await ch.RequestAsync(Ops.Hello, new { }));

        Assert.Equal(1, hello.GetProperty("protocol").GetInt32());
        Assert.Equal("Test host", hello.GetProperty("machine").GetProperty("name").GetString());
        Assert.Equal("windows", hello.GetProperty("machine").GetProperty("os").GetString());
        Assert.Equal([Capabilities.SessionsRead], hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Listing_and_getting_return_the_machines_sessions_in_contract_form()
    {
        var ch = await OpenAsync();

        var list = Result(await ch.RequestAsync(Ops.SessionsList, new { }));
        var only = Assert.Single(list.GetProperty("sessions").EnumerateArray());
        Assert.Equal(Id, only.GetProperty("id").GetString());
        Assert.Equal("Served session", only.GetProperty("title").GetString());
        Assert.False(only.GetProperty("activity").TryGetProperty("history", out _));
        Assert.False(list.TryGetProperty("next", out _));

        var full = Result(await ch.RequestAsync(Ops.SessionGet, new { id = Id }));
        Assert.Equal("powershell", full.GetProperty("activity").GetProperty("history")[0].GetProperty("name").GetString());

        var gone = Result(await ch.RequestAsync(Ops.SessionGet, new { id = Other }));
        Assert.True(gone.GetProperty("missing").GetBoolean());
    }

    [Fact]
    public async Task Getting_without_an_id_and_unknown_ops_are_refused_without_ending_the_channel()
    {
        var ch = await OpenAsync();

        var noId = await ch.RequestAsync(Ops.SessionGet, new { });
        Assert.False(noId.GetProperty("ok").GetBoolean());
        var unknown = await ch.RequestAsync("session.stop", new { id = Id });
        Assert.False(unknown.GetProperty("ok").GetBoolean());

        Assert.True(ch.IsOpen);
        Result(await ch.RequestAsync(Ops.Hello, new { }));
    }

    [Fact]
    public async Task Subscribing_sends_the_snapshot_then_synced_then_each_live_change_with_consecutive_seq()
    {
        var ch = await OpenAsync();

        var result = Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        Assert.Equal(1, result.GetProperty("count").GetInt32());
        var snapshot = await SnapshotAsync(ch);
        Assert.Equal([Events.SessionUpsert, Events.SessionsSynced], snapshot.Select(e => e.Name));
        Assert.Equal(Id, snapshot[0].Data.GetProperty("id").GetString());
        Assert.Equal(result.GetProperty("revision").GetInt64(), snapshot[1].Data.GetProperty("revision").GetInt64());

        await ChangeAsync("listed the folder", 4);
        var live = await NextAsync(ch);
        Assert.Equal(Events.SessionUpsert, live.Name);
        Assert.Equal("listed the folder", live.Data.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal([1L, 2L, 3L], snapshot.Append(live).Select(e => e.Seq));

        f.Processes.Exit(AgentKind.Copilot, 100);
        await source.RefreshAsync();
        var removed = await NextAsync(ch);
        Assert.Equal((Events.SessionRemove, Id, 4L), (removed.Name, removed.Data.GetProperty("id").GetString(), removed.Seq));
    }

    [Fact]
    public async Task Subscribing_again_replaces_the_subscription_resends_the_snapshot_and_continues_seq()
    {
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        var first = await SnapshotAsync(ch);

        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        var second = await SnapshotAsync(ch);
        Assert.Equal([Events.SessionUpsert, Events.SessionsSynced], second.Select(e => e.Name));
        Assert.Equal(first[^1].Seq + 1, second[0].Seq);
        Assert.Equal(1, service.Subscribers);

        // One subscription, so one upsert for one change: a second running alongside would
        // send it twice.
        await ChangeAsync("only once", 4);
        var live = await NextAsync(ch);
        Assert.Equal("only once", live.Data.GetProperty("activity").GetProperty("response").GetString());
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ch.Events.ReadAsync(quiet.Token));
    }

    [Fact]
    public async Task Two_subscribes_sent_at_once_leave_one_subscription()
    {
        var ch = await OpenAsync();

        var both = await Task.WhenAll(
            ch.RequestAsync(Ops.SessionsSubscribe, new { }),
            ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        Assert.All(both, r => Result(r));
        await Wait.UntilAsync(() => service.Subscribers == 1, "one subscription");

        // Drain both snapshots (the first may be cut short by its replacement), then one
        // change must arrive exactly once.
        var names = new List<string>();
        using (var settle = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            try { while (true) { names.Add((await ch.Events.ReadAsync(settle.Token)).Name); } }
            catch (OperationCanceledException) { }
        }
        Assert.Equal(Events.SessionsSynced, names[^1]);
        await ChangeAsync("after both", 4);
        Assert.Equal("after both", (await NextAsync(ch)).Data.GetProperty("activity").GetProperty("response").GetString());
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ch.Events.ReadAsync(quiet.Token));
    }

    [Fact]
    public async Task Subscriptions_replaced_on_a_long_lived_channel_are_let_go()
    {
        var ch = await OpenAsync();
        var made = new List<WeakReference>();
        service.FeedCreated += feed => made.Add(new WeakReference(feed));

        for (var i = 0; i < 5; i++)
        {
            Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
            await SnapshotAsync(ch);
        }
        Assert.Equal(5, made.Count);

        // Every replaced feed is collectable while the channel stays open; only the current
        // one is still held.
        await Wait.UntilAsync(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return made.Take(4).All(w => !w.IsAlive);
        }, "the replaced subscriptions to be collected", Patience);
        Assert.True(made[4].IsAlive);
        Assert.True(ch.IsOpen);
    }

    [Fact]
    public async Task A_subscription_ends_when_its_channel_does()
    {
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        await SnapshotAsync(ch);
        Assert.Equal(1, service.Subscribers);

        await client.DisposeAsync();

        await Wait.UntilAsync(() => service.Subscribers == 0, "the subscription to end with its channel", Patience);
    }

    [Fact]
    public async Task Separate_channels_each_get_their_own_subscription_and_seq()
    {
        var a = await OpenAsync();
        var b = await OpenAsync();
        Result(await a.RequestAsync(Ops.SessionsSubscribe, new { }));
        await SnapshotAsync(a);
        Result(await b.RequestAsync(Ops.SessionsSubscribe, new { }));
        var bSnapshot = await SnapshotAsync(b);
        Assert.Equal(1L, bSnapshot[0].Seq);

        await ChangeAsync("to both", 4);
        Assert.Equal("to both", (await NextAsync(a)).Data.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal("to both", (await NextAsync(b)).Data.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal(2, service.Subscribers);
    }
}
