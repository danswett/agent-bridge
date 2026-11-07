using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentBridge.Host;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class ConnectorReadVectorTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string Missing = "99999999-9999-4999-8999-999999999999";
    static readonly string FileName = Path.Combine("..", "connector-read-conversations-v1.json");
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_connector_mock_replays_results_and_snapshot_events_the_host_really_serves()
    {
        using var fixture = new Fixture();
        var transcript = fixture.CopilotSession(Id, 100, name: "Connector fixture");
        // Fixed record ids: activity.turn is made from the prompt's record (#292), and a random id
        // would make the recording differ on every run.
        static string Fixed(string line, int n)
        {
            var record = JsonNode.Parse(line)!.AsObject();
            record["id"] = $"00000000-0000-4000-8000-{n:D12}";
            return record.ToJsonString();
        }
        Fixture.Append(transcript, Fixed(Copilot.User("List the fixture files", 1), 1), Fixed(Copilot.TurnStart(2), 2),
            Fixed(Copilot.ToolStart("fixture-tool", "powershell", new { command = "Get-ChildItem" }, 3), 3));
        // No machine-dependent path is part of this portable recording.
        File.WriteAllText(Path.Combine(fixture.CopilotHome, "session-state", Id, "workspace.yaml"),
            $"id: {Id}\nname: Connector fixture\ncreated_at: 2026-10-28T03:00:00.000Z\n");
        fixture.Processes.Run(AgentKind.Copilot, 100);
        await using var source = fixture.Source();
        await source.RefreshAsync();
        var service = new SessionService(source, new MachineInfo("00112233445566778899aabbccddeeff", "Fixture host", "windows", "0.1.0"));
        var channel = new CaptureChannel();
        var cases = new JsonArray();
        Task? feed = null;
        try
        {
            async Task Record(string name, string op, object data, bool snapshot = false)
            {
                var input = JsonSerializer.SerializeToElement(data, ProtocolJson.Options);
                var entry = new JsonObject
                {
                    ["name"] = name,
                    ["request"] = new JsonObject { ["op"] = op, ["data"] = JsonNode.Parse(input.GetRawText()) },
                };
                try
                {
                    var result = await service.HandleAsync(channel, op, input);
                    if (result is ChannelReply reply)
                    {
                        result = reply.Result;
                        feed = reply.Then(channel);
                    }
                    Assert.NotNull(result);
                    entry["response"] = new JsonObject { ["ok"] = true, ["result"] = JsonSerializer.SerializeToNode(result, ProtocolJson.Options) };
                }
                catch (ArgumentException e)
                {
                    entry["response"] = new JsonObject { ["ok"] = false, ["error"] = e.Message };
                }
                if (snapshot)
                {
                    var events = new JsonArray();
                    using var timeout = new CancellationTokenSource(Patience);
                    JsonObject next;
                    do
                    {
                        next = await channel.Events.Reader.ReadAsync(timeout.Token);
                        events.Add(next);
                    }
                    while (next["ev"]!.GetValue<string>() != Events.SessionsSynced);
                    entry["events"] = events;
                }
                cases.Add(entry);
            }

            await Record("hello", Ops.Hello, new { });
            await Record("list", Ops.SessionsList, new { });
            await Record("get", Ops.SessionGet, new { id = Id });
            await Record("missing-session", Ops.SessionGet, new { id = Missing });
            await Record("missing-subagent", Ops.SessionGet, new { id = Id, agent = "not-present" });
            await Record("get-without-id", Ops.SessionGet, new { });
            await Record("snapshot", Ops.SessionsSubscribe, new { }, snapshot: true);

            var generated = new JsonObject
            {
                ["profile"] = "connector-read-v1.json",
                ["generatedBy"] = "tests/AgentBridge.Host.Tests/ConnectorReadVectorTests.cs",
                ["cases"] = cases,
            };
            Assert.Empty(VectorFiles.Stale(new Dictionary<string, JsonObject> { [FileName] = generated }));
        }
        finally
        {
            channel.Close();
            if (feed is not null) { await feed.WaitAsync(Patience); }
        }
    }

    [Fact]
    public void The_profile_references_existing_payload_vectors_and_the_hosts_actual_limits()
    {
        var profile = JsonNode.Parse(File.ReadAllText(Path.Combine(VectorFiles.Folder, "..", "connector-read-v1.json")))!;
        var records = VectorFiles.Read("sessions.json")["records"]!.AsObject();
        Assert.Equal(ProtocolJson.Version, profile["protocol"]!.GetValue<int>());
        Assert.Equal(ProtocolJson.MaxMessageBytes, profile["maxMessageBytes"]!.GetValue<int>());
        Assert.Equal(SessionBudget.MaxSessionBytes, profile["maxSessionBytes"]!.GetValue<int>());
        foreach (var operation in profile["operations"]!.AsObject())
        {
            foreach (var record in operation.Value!["resultRecords"]!.AsArray())
            {
                Assert.True(records.ContainsKey(record!.GetValue<string>()), $"missing record vectors for {record}");
            }
        }
        foreach (var ev in profile["events"]!.AsObject())
        {
            Assert.True(records.ContainsKey(ev.Value!["record"]!.GetValue<string>()), $"missing event record vectors for {ev.Key}");
        }
    }

    sealed class CaptureChannel : IProtocolChannel
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long sequence;
        public Channel<JsonObject> Events { get; } = Channel.CreateUnbounded<JsonObject>();
        public string PeerId => "fixture-client";
        public string PeerName => "Fixture connector";
        public string PeerKind => "fixture";
        public bool IsOpen => !closed.Task.IsCompleted;
        public Task Closed => closed.Task;
        public void Close() => closed.TrySetResult();
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var seq = Interlocked.Increment(ref sequence);
            Assert.True(Events.Writer.TryWrite(new JsonObject
            {
                ["ev"] = name, ["data"] = JsonSerializer.SerializeToNode(data, ProtocolJson.Options),
            }));
            return Task.FromResult(seq);
        }
    }
}
