using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class SessionRosterFixtureTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Subscribing_sends_host_roster_metadata_and_keeps_unknown_outcomes_unknown()
    {
        using var fixture = new Fixture();
        var events = fixture.CopilotSession(Id, 100, name: "Served session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2),
            Copilot.ToolStart("c1", "powershell", new { command = "dir" }, 3));
        var processStartedAt = Fixture.T0.AddMinutes(-5);
        fixture.Processes.Run(AgentKind.Copilot, 100, startedAt: processStartedAt);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, TimeProvider.System);
        await source.RefreshAsync();
        var service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"));
        var channel = new CaptureChannel();
        var empty = JsonSerializer.SerializeToElement(new { });
        var hello = JsonSerializer.SerializeToElement(await service.HandleAsync(channel, Ops.Hello, empty), ProtocolJson.Options);
        var machine = hello.GetProperty("machine").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("The host hello omitted its machine id.");
        var subscription = Assert.IsType<ChannelReply>(await service.HandleAsync(channel, Ops.SessionsSubscribe, empty));
        var feed = subscription.Then(channel);
        try
        {
            var snapshot = new List<ChannelEvent>();
            do { snapshot.Add(await channel.NextAsync()); } while (snapshot[^1].Name != Events.SessionsSynced);
            Assert.Equal([Events.SessionUpsert, Events.SessionsSynced], snapshot.Select(e => e.Name));
            Assert.Equal("working", snapshot[0].Data.GetProperty("status").GetString());
            AssertRosterRow(snapshot[0].Data, machine, processStartedAt);

            Fixture.Append(events, Copilot.ToolDone("c1", 4), Copilot.Assistant("finished this turn", 5), Copilot.TurnEnd(6));
            await source.RefreshAsync();
            var live = await channel.NextAsync();
            Assert.Equal(Events.SessionUpsert, live.Name);
            Assert.Equal("idle", live.Data.GetProperty("status").GetString());
            AssertRosterRow(live.Data, machine, processStartedAt);
            Assert.Equal(snapshot[^1].Seq + 1, live.Seq);
        }
        finally
        {
            channel.Close();
            await feed.WaitAsync(Patience);
        }
    }

    static void AssertRosterRow(JsonElement row, string machine, DateTimeOffset processStartedAt)
    {
        var missing = new[] { "machine", "owner", "pr", "finished", "startedAt" }
            .Where(name => !row.TryGetProperty(name, out _)).ToArray();
        Assert.Empty(missing);
        Assert.Equal(machine, row.GetProperty("machine").GetString());
        Assert.Equal(processStartedAt, row.GetProperty("startedAt").GetDateTimeOffset());
        foreach (var name in new[] { "owner", "pr", "finished" })
        {
            var evidence = row.GetProperty(name);
            Assert.Equal(JsonValueKind.Object, evidence.ValueKind);
            Assert.Equal("unknown", evidence.GetProperty("state").GetString());
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
