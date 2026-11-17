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
