using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;

public sealed class TranscriptDeliveryTests
{
    sealed class Transcript : IProcessInspector, IDisposable
    {
        readonly DirectoryInfo root = Directory.CreateTempSubdirectory("ab2144-");
        public const string Id = "55555555-5555-4555-8555-555555555555";
        public static readonly DateTimeOffset At = new(2026, 11, 11, 22, 27, 0, TimeSpan.Zero);
        public string Events { get; }
        public SessionSource Source { get; }
        public Transcript()
        {
            var home = Path.Combine(root.FullName, "copilot");
            var session = Directory.CreateDirectory(Path.Combine(home, "session-state", Id)).FullName;
            File.WriteAllText(Path.Combine(session, "workspace.yaml"), $"id: {Id}\nname: Fixture\ncwd: C:\\fixture\n");
            File.WriteAllText(Path.Combine(session, "inuse.100.lock"), "100");
            Events = Path.Combine(session, "events.jsonl");
            Source = new SessionSource(new SessionSourceOptions
            {
                CopilotHome = home, ClaudeHome = Path.Combine(root.FullName, "claude"),
                CodexHome = Path.Combine(root.FullName, "codex"), UseFileSystemWatcher = false,
            }, this);
        }
        public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) =>
            agent == AgentKind.Copilot ? [new(100, At.AddHours(-1), null)] : [];
        public void Dispose() => root.Delete(recursive: true);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Every_assistant_message_in_events_jsonl_reaches_the_client_transcript_in_order()
    {
        using var f = new Transcript();
        const string id = Transcript.Id;
        var events = f.Events;
        await using var source = f.Source;
        var answer = new string('a', 800);
        string Event(string type, object data, int at) => JsonSerializer.Serialize(new
        { id = $"event-{at}", type, timestamp = Transcript.At.AddSeconds(at), data });
        void Turn(string type, object data, int at, string reply) => File.AppendAllLines(events,
        [
            Event(type, data, at), Event("assistant.turn_start", new { turnId = $"t{at}" }, at + 1),
            Event("assistant.message", new { messageId = $"m{at}", content = reply }, at + 2),
            Event("assistant.turn_end", new { turnId = $"t{at}" }, at + 3),
        ]);
        Turn("user.message", new { content = "Owner question", source = "user", delivery = "idle" }, 0, answer);
        Turn("user.message", new { content = "Scheduled check", source = "schedule-1", delivery = "idle" }, 5, "Scheduled reply");
        Turn("user.message", new { content = "Background command finished", source = "system", delivery = "idle" }, 10, "Notification reply");
        Turn("user.message", new { content = "Next question", source = "user", delivery = "idle" }, 15, "Fourth reply");
        await source.RefreshAsync();
        await using var host = new FakeHost();
        host.Capabilities.Add(HistoryProtocol.Capability);
        host.OnAsync(HistoryProtocol.Op, async d =>
        {
            var before = d.TryGetProperty("before", out var b) ? b.GetString() : null;
            return await source.HistoryAsync(id, before, 2);
        });
        host.Start();
        await using var connection = host.Connect();
        await connection.WhenConnectedAsync().WaitAsync(FakeHost.Patience);
        var history = new SessionHistory(connection, id);
        await history.LoadNewestAsync();
        while (history.HasOlder) { await history.LoadOlderAsync(); }

        var recorded = File.ReadLines(events).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(e => e.GetProperty("type").GetString() == "assistant.message")
            .Select(e => e.GetProperty("data").GetProperty("content").GetString()!).ToArray();
        var rows = HistoryDraw.Rows(history.Turns, null);
        var shown = rows.SelectMany(r => r.Blocks).Where(b => b.Text is not null).Select(b => b.Text!).ToArray();
        Assert.Equal(4, recorded.Length);
        Assert.Equal(recorded, shown);
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(recorded.Select(Hash), shown.Select(Hash));
        Assert.Equal([TurnKind.User, TurnKind.Scheduled, TurnKind.Notification, TurnKind.User], history.Turns.Select(t => t.Kind));
        Assert.Equal(4, rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal(answer, rows[0].Blocks.Single().Text);
        Assert.All(history.Turns, t => Assert.NotEqual(true, t.Truncated));
        Assert.Equal(HistoryLoad.StartReached, history.State);
    }
}
