using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class FleetPublisherTests
{
    readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
    readonly Source source = new();
    readonly List<JsonElement> sent = [];
    Task Put(JsonElement record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Assert.Null(RosterFields.Check(record, "machine", out _, out _, out _));
        sent.Add(record.Clone());
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Publishing_includes_unnamed_sessions_but_never_transcript_or_workspace_names()
    {
        var publisher = new FleetPublisher(source, "machine", id => id == source.Sessions[0].Id ? "SG Builder" : "<private>", time);
        Assert.True(await publisher.PublishAsync(Put));
        var record = Assert.Single(sent);
        Assert.Equal(["at", "epoch", "machine", "seq", "sessions", "truncated", "v"], Fields(record));
        var sessions = record.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(source.Sessions.Count, sessions.Length);
        Assert.Single(sessions.Where(s => s.TryGetProperty("name", out _)));
        Assert.All(sessions, s => Assert.Subset(new HashSet<string> { "id", "node", "name", "state", "since" }, Fields(s).ToHashSet()));
        Assert.DoesNotContain("private", record.GetRawText());
        Assert.False(record.TryGetProperty("sig", out _));
    }

    [Fact]
    public async Task Changes_are_coalesced_heartbeats_refresh_and_reconnect_republishes_without_resetting_sequence()
    {
        var publisher = new FleetPublisher(source, "machine", time: time);
        Assert.True(await publisher.PublishAsync(Put));
        source.Sessions.RemoveAt(0);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await publisher.PublishAsync(Put));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await publisher.PublishAsync(Put));
        Assert.Equal(2, sent[^1].GetProperty("sessions").GetArrayLength());
        time.Advance(TimeSpan.FromSeconds(19));
        Assert.False(await publisher.PublishAsync(Put));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await publisher.PublishAsync(Put));
        Assert.True(await publisher.PublishAsync(Put, force: true));
        Assert.Equal([1L, 2L, 3L, 4L], sent.Select(r => r.GetProperty("seq").GetInt64()));
        Assert.Single(sent.Select(r => r.GetProperty("epoch").GetString()).Distinct());
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(await new FleetPublisher(source, "machine", time: time).PublishAsync(Put));
        Assert.NotEqual(sent[0].GetProperty("epoch").GetString(), sent[^1].GetProperty("epoch").GetString());
    }

    [Fact]
    public async Task Truncation_keeps_recent_state_changes_and_a_hold_never_publishes_false_absence()
    {
        source.Sessions = Enumerable.Range(1, 65).Select(NewSession).ToList();
        var publisher = new FleetPublisher(source, "machine", time: time);
        Assert.True(await publisher.PublishAsync(Put));
        Assert.True(sent[^1].GetProperty("truncated").GetBoolean());
        Assert.Equal(65, sent[^1].GetProperty("total").GetInt32());
        Assert.Equal(64, sent[^1].GetProperty("sessions").GetArrayLength());
        Assert.Equal(source.Sessions[^1].StatusSince, sent[^1].GetProperty("sessions")[0].GetProperty("since").GetDateTimeOffset());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(sent[^1].GetRawText()) <= RosterFields.MaxRecordBytes);
        source.Held = new DiscoveryHeld("discovery incomplete", [AgentKind.Copilot]);
        source.Sessions.Clear();
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(await publisher.PublishAsync(Put));
        Assert.Single(sent);
        Assert.True(await publisher.PublishAsync(Put, share: false));
        Assert.Empty(sent[^1].GetProperty("sessions").EnumerateArray());
        Assert.False(sent[^1].GetProperty("truncated").GetBoolean());
        Assert.False(sent[^1].TryGetProperty("total", out _));
    }

    [Fact]
    public async Task Failed_publication_is_explicit_not_cached_as_success_and_cancellation_stops_before_sending()
    {
        var publisher = new FleetPublisher(source, "machine", time: time);
        var error = await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync((_, _) => throw new IOException("registry publication failed")));
        Assert.Contains("registry publication failed", error.Message);
        Assert.True(await publisher.PublishAsync(Put));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishAsync(Put, force: true, ct: stop.Token));
        Assert.Single(sent);
    }

    [Fact]
    public async Task A_host_that_stops_publishing_drops_out_after_the_relays_sixty_second_expiry()
    {
        var registry = new SessionRegistry(time);
        var publisher = new FleetPublisher(source, "machine", time: time);
        Assert.True(await publisher.PublishAsync((record, _) =>
        {
            Assert.Null(registry.Put("account", "machine", record));
            return Task.CompletedTask;
        }));
        Assert.Equal(3, (await new Roster(registry).ReadAsync(default)).Sum(m => m.Sessions.Count));
        time.Advance(TimeSpan.FromSeconds(61));
        var expired = Assert.Single(await new Roster(registry).ReadAsync(default));
        Assert.False(expired.Online);
        Assert.Empty(expired.Sessions);
    }

    [WindowsFact("FleetList agent credentials use Windows process attribution")]
    public async Task The_real_fleet_list_returns_every_published_session_including_unnamed_ones()
    {
        using var fixture = new Fixture();
        var registry = new SessionRegistry(time);
        Assert.True(await new FleetPublisher(source, "machine", time: time).PublishAsync((record, _) =>
        {
            Assert.Null(registry.Put("account", "machine", record));
            return Task.CompletedTask;
        }));
        var credentials = new AgentCredentials(fixture.Root, "machine");
        var channel = new Chan();
        var started = time.GetUtcNow();
        var token = credentials.Mint("caller", "launch", 100, started, AgentProtocol.ScopeRead);
        credentials.Accept(channel, credentials.Check(token, new ProcessAncestry([new PeerProcess(100, started, "agent.exe")], true)).Accepted!);
        var list = new FleetList(credentials, new Roster(registry), time);
        var result = JsonSerializer.SerializeToElement(await list.HandleAsync(channel, JsonSerializer.SerializeToElement(new { include = "content" })), ProtocolJson.Options);
        Assert.Equal(source.Sessions.Count, result.GetProperty("sessions").GetArrayLength());
        Assert.All(result.GetProperty("sessions").EnumerateArray(), s => Assert.False(s.TryGetProperty("name", out _)));
    }

    static string[] Fields(JsonElement e) => e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
    static Session NewSession(int i) => new()
    {
        Id = i.ToString("x16") + i.ToString("x16"), Agent = AgentKind.Copilot, Status = SessionStatus.Idle,
        StatusSince = Fixture.T0.AddSeconds(i), UpdatedAt = Fixture.T0,
        Title = "private workspace title", Cwd = "private path", Model = "private model",
        Activity = new Activity { Response = "private transcript", UserMessage = "private prompt" },
    };

    sealed class Source : ISessionSource
    {
        public List<Session> Sessions = Enumerable.Range(1, 3).Select(NewSession).ToList();
        public DiscoveryHeld? Held { get; set; }
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => Sessions;
        public SessionList List(string? cursor = null) => new(Snapshot(), 0);
        public Session? Get(string id) => Sessions.Find(s => s.Id == id);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Roster(SessionRegistry registry) : IFleetRoster
    {
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) =>
            Task.FromResult(RelayFleetRoster.Parse(registry.Read("account").Select(r => JsonSerializer.SerializeToElement(new
            {
                machine = r.Machine, online = r.Fresh,
                record = r.Fresh ? JsonSerializer.Deserialize<JsonElement>(r.Json) : (JsonElement?)null,
            }))));
    }

    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
