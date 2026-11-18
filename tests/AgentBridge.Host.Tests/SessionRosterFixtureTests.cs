using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class SessionRosterFixtureTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_roster_reads_and_ordered_changes_keep_unknown_outcomes_and_drop_ended_sessions(bool patch)
    {
        using var fixture = new Fixture();
        var events = fixture.CopilotSession(Id, 100);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        fixture.Processes.Run(AgentKind.Copilot, 100, startedAt: Fixture.T0.AddMinutes(-5));
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        var service = new SessionService(source, new MachineInfo("host-id", "Fixture host", "windows", "0.1.0"));
        var channel = new CaptureChannel();
        var empty = JsonSerializer.SerializeToElement(new { });
        async Task<JsonElement> Read(string op, object? data = null) =>
            JsonSerializer.SerializeToElement(await service.HandleAsync(channel, op,
                data is null ? empty : JsonSerializer.SerializeToElement(data)), ProtocolJson.Options);
        var machine = (await Read(Ops.Hello)).GetProperty("machine").GetProperty("id").GetString()!;
        AssertRow(Assert.Single((await Read(Ops.SessionsList)).GetProperty("sessions").EnumerateArray()), machine);
        AssertRow(await Read(Ops.SessionGet, new { id = Id }), machine);
        var subscription = Assert.IsType<ChannelReply>(await service.HandleAsync(channel, Ops.SessionsSubscribe,
            JsonSerializer.SerializeToElement(new { patch })));
        var feed = subscription.Then(channel);
        try
        {
            var snapshot = await channel.NextAsync();
            var synced = await channel.NextAsync();
            Assert.Equal((Events.SessionUpsert, Events.SessionsSynced), (snapshot.Name, synced.Name));
            Assert.Equal(snapshot.Seq + 1, synced.Seq);
            AssertRow(snapshot.Data, machine);
            Assert.Equal("working", snapshot.Data.GetProperty("status").GetString());

            Fixture.Append(events, Copilot.Assistant("turn done", 3), Copilot.TurnEnd(4));
            await source.RefreshAsync();
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            var live = await channel.NextAsync();
            Assert.Equal(patch ? Events.SessionPatch : Events.SessionUpsert, live.Name);
            Assert.Equal(synced.Seq + 1, live.Seq);
            var row = patch ? JsonSerializer.SerializeToElement(SessionPatches.Apply(
                JsonNode.Parse(snapshot.Data.GetRawText())!.AsObject(), snapshot.Seq,
                live.Data.Deserialize<SessionPatch>(ProtocolJson.Options)!)) : live.Data;
            AssertRow(row, machine);
            Assert.Equal("idle", row.GetProperty("status").GetString());

            await source.SetEndingAsync(Id, true);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            var ending = await channel.NextAsync();
            Assert.Equal(live.Seq + 1, ending.Seq);
            Fixture.Append(events, Copilot.Assistant("pending change", 5));
            await source.RefreshAsync();
            fixture.Processes.Exit(AgentKind.Copilot, 100);
            await source.RefreshAsync();
            var removed = await channel.NextAsync();
            Assert.Equal((Events.SessionRemove, Id, ending.Seq + 1),
                (removed.Name, removed.Data.GetProperty("id").GetString(), removed.Seq));
            Assert.Empty(source.Snapshot());
            Assert.Empty((await Read(Ops.SessionsList)).GetProperty("sessions").EnumerateArray());
            Assert.True((await Read(Ops.SessionGet, new { id = Id })).GetProperty("missing").GetBoolean());
            var replacement = Assert.IsType<ChannelReply>(await service.HandleAsync(channel, Ops.SessionsSubscribe, empty));
            Assert.Equal(0, JsonSerializer.SerializeToElement(replacement.Result).GetProperty("Count").GetInt32());
            var resynced = replacement.Then(channel);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(Events.SessionsSynced, (await channel.NextAsync()).Name);
            channel.Close();
            await resynced.WaitAsync(Patience);
        }
        finally
        {
            channel.Close();
            await feed.WaitAsync(Patience);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_crash_is_removed_only_after_discovery_knows_the_process_is_gone(bool unreadable)
    {
        using var fixture = new Fixture();
        Fixture.Append(fixture.CopilotSession(Id, 100), Copilot.User("go", 1), Copilot.TurnStart(2));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        fixture.Processes.Exit(AgentKind.Copilot, 100);
        fixture.Processes.Unreadable(AgentKind.Copilot, unreadable);
        await source.RefreshAsync();
        if (unreadable)
        {
            Assert.NotNull(source.Held);
            AssertRow(JsonSerializer.SerializeToElement(Assert.Single(source.List().Sessions), ProtocolJson.Options), "host-id");
            fixture.Processes.Unreadable(AgentKind.Copilot, false);
            await source.RefreshAsync();
        }
        Assert.Null(source.Held);
        Assert.Empty(source.List().Sessions);
        Assert.Null(source.Get(Id));
        using var subscription = source.Subscribe();
        Assert.Equal(0, subscription.Result.Count);
    }

    [Fact]
    public async Task A_host_restart_and_same_id_reregistration_do_not_reuse_ending_or_completion_evidence()
    {
        using var fixture = new Fixture();
        Fixture.Append(fixture.CopilotSession(Id, 100), Copilot.User("go", 1), Copilot.TurnStart(2));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        var options = fixture.Options(stateFile: Path.Combine(fixture.Root, "host-state.json"));
        await using (var first = new SessionSource(options, fixture.Processes, fixture.Time, hostId: "host-id"))
        {
            await first.RefreshAsync();
            await first.SetEndingAsync(Id, true);
            Assert.Equal(SessionStatus.Ending, first.Get(Id)!.Status);
        }
        await using var restarted = new SessionSource(options, fixture.Processes, fixture.Time, hostId: "host-id");
        await restarted.RefreshAsync();
        Assert.Equal(SessionStatus.Working, restarted.Get(Id)!.Status);
        AssertRow(JsonSerializer.SerializeToElement(restarted.Get(Id), ProtocolJson.Options), "host-id");
        fixture.Processes.Exit(AgentKind.Copilot, 100);
        await restarted.RefreshAsync();
        Assert.Empty(restarted.Snapshot());
        fixture.CopilotSession(Id, 101);
        fixture.Processes.Run(AgentKind.Copilot, 101);
        await restarted.RefreshAsync();
        var returned = Assert.Single(restarted.List().Sessions);
        Assert.Equal((Id, 101, SessionStatus.Working), (returned.Id, returned.ProcessId, returned.Status));
        AssertRow(JsonSerializer.SerializeToElement(returned, ProtocolJson.Options), "host-id");
    }

    [Fact]
    public async Task Roster_evidence_survives_the_session_budget_in_list_get_and_subscription()
    {
        using var fixture = new Fixture();
        var text = new string('\u0001', 16_000);
        Fixture.Append(fixture.CopilotSession(Id, 100), Copilot.User(text, 1),
            Copilot.Assistant(text, 2, reasoning: text));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.True(ProtocolJson.ByteCount(source.GetUnbudgeted(Id)!) > SessionBudget.MaxSessionBytes);
        var service = new SessionService(source, new MachineInfo("host-id", "Fixture host", "windows", "0.1.0"));
        var channel = new CaptureChannel();
        var data = JsonSerializer.SerializeToElement(new { id = Id });
        var list = Assert.IsType<SessionList>(await service.HandleAsync(channel, Ops.SessionsList, data));
        var get = Assert.IsType<Session>(await service.HandleAsync(channel, Ops.SessionGet, data));
        using var subscription = source.Subscribe();
        Assert.True(subscription.Events.TryRead(out var upsert));
        var sent = Assert.IsType<Session>(upsert!.Data);
        foreach (var row in new[] { Assert.Single(list.Sessions), get, sent })
        {
            Assert.InRange(ProtocolJson.ByteCount(row), 1, SessionBudget.MaxSessionBytes);
            AssertRow(JsonSerializer.SerializeToElement(row, ProtocolJson.Options), "host-id");
        }
        Assert.InRange(ProtocolJson.ByteCount(list), 1, ProtocolJson.MaxMessageBytes);
    }

    static void AssertRow(JsonElement row, string machine)
    {
        Assert.Equal(machine, row.GetProperty("machine").GetString());
        Assert.False(row.TryGetProperty("startedAt", out _)); // A process birth is not a proven session start.
        foreach (var name in new[] { "owner", "pr", "finished" })
        {
            Assert.Equal("unknown", row.GetProperty(name).GetProperty("state").GetString());
        }
    }

    sealed class CaptureChannel : IProtocolChannel
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Channel<ChannelEvent> events = Channel.CreateUnbounded<ChannelEvent>();
        long sequence;
        public string PeerId => "fixture-client";
        public string PeerName => "Fixture subscriber";
        public string PeerKind => "fixture";
        public bool IsOpen => !closed.Task.IsCompleted;
        public Task Closed => closed.Task;
        public void Close() => closed.TrySetResult();
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var seq = Interlocked.Increment(ref sequence);
            Assert.True(events.Writer.TryWrite(new ChannelEvent(name, seq, JsonSerializer.SerializeToElement(data, ProtocolJson.Options))));
            return Task.FromResult(seq);
        }
        public async Task<ChannelEvent> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Patience);
            return await events.Reader.ReadAsync(timeout.Token);
        }
    }
}
