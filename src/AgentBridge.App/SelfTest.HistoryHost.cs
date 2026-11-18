using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App;

// Invented transcript, real host discovery/driver metadata/history, then the app's local wire path.
// No real agent homes, process inspection, host management or input are used.
sealed class HistoryPaintHost : IAsyncDisposable
{
    public const string Id = "77777777-1111-2222-3333-444444444444";
    readonly string root = Directory.CreateTempSubdirectory("ab-paint-").FullName;
    readonly DateTimeOffset at = DateTimeOffset.UtcNow.AddHours(-1);
    readonly SessionDrivers drivers = new();
    readonly SessionSource source;
    readonly LocalServer server;
    readonly string transcript;
    int historyRequests;

    public LocalEndpoint Endpoint => server.Endpoint;
    public int HistoryRequests => Volatile.Read(ref historyRequests);
    public Session Current => source.Get(Id) ?? throw new InvalidOperationException("diagnostic session was not discovered");
    public static string Prompt(int n) => $"Agent message {n}.\n\nThese full words must stay behind Show prompt until opened.";
    public static string Reply(int n) => $"**Formatted reply {n}** with `code`.";

    public HistoryPaintHost()
    {
        var home = Path.Combine(root, "copilot");
        var session = Directory.CreateDirectory(Path.Combine(home, "session-state", Id)).FullName;
        File.WriteAllText(Path.Combine(session, "workspace.yaml"), $"id: {Id}\nname: Transcript paint\ncwd: {root}\n");
        File.WriteAllText(Path.Combine(session, "inuse.4242.lock"), "4242");
        transcript = Path.Combine(session, "events.jsonl");
        source = new SessionSource(new SessionSourceOptions
        {
            CopilotHome = home, ClaudeHome = Path.Combine(root, "claude"), CodexHome = Path.Combine(root, "codex"),
            UseFileSystemWatcher = false, CoalesceInterval = TimeSpan.Zero,
            NotRespondingAfter = TimeSpan.MaxValue, NotRespondingBusyAfter = TimeSpan.MaxValue,
        }, new Processes(at), drivers: drivers);
        server = LocalServer.Start(LocalEndpoint.For(root), Serve);
    }

    public async Task AppendAsync(int n, bool agent = true)
    {
        if (agent) { drivers.For(Id).ArmDriver(SessionDrivers.Agent, DateTimeOffset.UtcNow, words: Prompt(n)); }
        string Event(string type, object data, int offset) => JsonSerializer.Serialize(new
        {
            id = offset == 0 ? $"paint{n}" : $"paint{n}-{offset}", type,
            timestamp = at.AddSeconds(n * 4 + offset), data,
        }, ProtocolJson.Options);
        await File.AppendAllTextAsync(transcript, string.Join("\n",
            Event("user.message", new { content = Prompt(n), delivery = "steering" }, 0),
            Event("assistant.turn_start", new { turnId = $"turn{n}" }, 1),
            Event("assistant.message", new { messageId = $"reply{n}", content = Reply(n) }, 2),
            Event("assistant.turn_end", new { turnId = $"turn{n}" }, 3)) + "\n");
        await source.RefreshAsync();
    }

    Task<object?> Serve(IProtocolChannel channel, string op, JsonElement data)
    {
        if (op == Ops.Hello)
        {
            return Task.FromResult<object?>(new HelloResult(1, new MachineInfo("paint-host", "Fixture", "windows", "0.1.0"),
                [Capabilities.SessionsRead, HistoryProtocol.Capability]));
        }
        if (op == Ops.SessionGet) { return Task.FromResult<object?>(source.Get(data.GetProperty("id").GetString()!)); }
        if (op == HistoryProtocol.Op)
        {
            Interlocked.Increment(ref historyRequests);
            return HistoryAsync(data);
        }
        if (op == Ops.SessionsSubscribe)
        {
            var subscription = source.Subscribe();
            return Task.FromResult<object?>(new ChannelReply(subscription.Result, async ch =>
            {
                using (subscription)
                {
                    while (await subscription.Events.WaitToReadAsync())
                    {
                        while (subscription.Events.TryRead(out var ev))
                        {
                            if (!ch.IsOpen) { return; }
                            await ch.SendEventAsync(ev.Name, ev.Data);
                        }
                    }
                }
            }));
        }
        throw new InvalidOperationException($"unexpected diagnostic op: {op}");
    }

    async Task<object?> HistoryAsync(JsonElement data) => await source.HistoryAsync(
        data.GetProperty("id").GetString()!,
        data.TryGetProperty("before", out var before) ? before.GetString() : null, HistoryLimits.DefaultTurns);

    public async ValueTask DisposeAsync()
    {
        await source.DisposeAsync();
        await server.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    sealed class Processes(DateTimeOffset at) : IProcessInspector
    {
        public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) =>
            agent == AgentKind.Copilot ? [new(4242, at.AddHours(-1), "copilot")] : [];
    }
}
