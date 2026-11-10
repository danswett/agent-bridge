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
    [Theory]
    [InlineData("{}", true, false)]
    [InlineData("{\"fleet\":{\"share\":false}}", false, false)]
    [InlineData("{\"fleet\":{\"share\":true}}", true, false)]
    [InlineData("{\"fleet\":{\"share\":null}}", false, true)]
    [InlineData("{\"fleet\":false}", false, true)]
    [InlineData("{\"version\":999}", false, true)]
    [InlineData("broken", false, true)]
    public void Sharing_is_explicit_and_unreadable_settings_never_enable_it(string json, bool share, bool problem)
    {
        var parsed = HostSettings.Parse(json, "config.json");
        Assert.Equal(share, parsed.Settings.FleetShare);
        Assert.Equal(problem, parsed.Problem is not null);
    }

    [Fact]
    public void The_existing_config_command_can_disable_sharing_and_rejects_an_invalid_value()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "fleet.share", "false"], fixture.Root, output, error));
        Assert.False(HostSettings.Load(fixture.Root).Settings.FleetShare);
        Assert.Equal(1, ConfigCommands.Run(["config", "set", "fleet.share", "maybe"], fixture.Root, output, error));
        Assert.False(HostSettings.Load(fixture.Root).Settings.FleetShare);
    }

    [Fact]
    public async Task Connection_publication_is_immediate_reconnect_keeps_sequence_and_disconnect_stops_the_loop()
    {
        var publication = new FleetPublication(source, "machine", share: true, time: time);
        for (var i = 1; i <= 2; i++)
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = publication.RunAsync(Put, disconnected.Task);
            Assert.Equal(i, sent.Count);
            Assert.Equal(i, sent[^1].GetProperty("seq").GetInt64());
            Assert.Equal("published", publication.Status.Result);
            disconnected.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, sent.Count);
        Assert.Single(sent.Select(r => r.GetProperty("epoch").GetString()).Distinct());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication.RunAsync(Put, Task.Delay(Timeout.Infinite), stop.Token));
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task Sharing_off_clears_the_roster_even_during_a_hold_but_sharing_on_never_claims_false_absence()
    {
        source.Held = new DiscoveryHeld("private reason", [AgentKind.Copilot]);
        var off = new FleetPublication(source, "machine", share: false, time: time);
        Assert.True(await off.PollAsync(Put));
        Assert.Empty(Assert.Single(sent).GetProperty("sessions").EnumerateArray());
        Assert.Equal("sharing-off", off.Status.Result);
        var on = new FleetPublication(source, "machine", share: true, time: time);
        Assert.False(await on.PollAsync(Put));
        Assert.Equal("discovery-held", on.Status.Result);
        source.Held = null;
        Assert.True(await on.PollAsync(Put));
        Assert.Equal(source.Sessions.Count, sent[^1].GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public async Task Sustained_failures_back_off_monotonically_cap_at_thirty_seconds_and_reset_on_success()
    {
        var logs = new List<string>();
        var publication = new FleetPublication(source, "machine", share: true, time: time, log: logs.Add);
        var calls = 0;
        Task Fail(JsonElement _, CancellationToken ct) { calls++; throw new IOException("private prompt path token"); }
        foreach (var seconds in new[] { 2, 4, 8, 16, 30, 30 })
        {
            Assert.False(await publication.PollAsync(Fail, force: true));
            Assert.Equal("failed", publication.Status.Result);
            Assert.Equal(time.GetUtcNow().AddSeconds(seconds), publication.Status.NextRetry);
            var before = calls;
            time.AdjustTime(time.GetUtcNow().AddDays(1));
            Assert.False(await publication.PollAsync(Fail, force: true));
            time.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.False(await publication.PollAsync(Fail));
            Assert.Equal(before, calls);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.True(await publication.PollAsync(Put));
        Assert.Equal(0, publication.Status.Failures);
        Assert.Null(publication.Status.NextRetry);
        Assert.DoesNotContain("private", string.Join("|", logs));
        Assert.Single(sent);
    }

    [Fact]
    public async Task A_bad_id_is_skipped_counted_in_host_status_and_does_not_refuse_healthy_sessions()
    {
        using var fixture = new Fixture();
        source.Sessions.Add(NewSession(4) with { Id = "bad-id" });
        var publication = new FleetPublication(source, "machine", share: true, time: time);
        Assert.True(await publication.PollAsync(Put));
        Assert.Equal(3, Assert.Single(sent).GetProperty("sessions").GetArrayLength());
        var control = new HostControl(new HostControlOptions
        {
            Profile = "test", Folder = fixture.Root, Version = "0.1.0", Running = new(false),
            Machine = new("machine", "Fixture", "windows", "0.1.0"), FleetPublication = () => publication.Status,
            SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => { }, Stop = () => { }, Restart = () => { },
        });
        Assert.Equal(1, control.Status().FleetPublication?.SkippedInvalidIds);
        time.Advance(TimeSpan.FromSeconds(2));
        source.Sessions.RemoveAt(3);
        await publication.PollAsync(Put);
        Assert.Equal(0, control.Status().FleetPublication?.SkippedInvalidIds);
    }

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
        Assert.Single(sessions, s => s.TryGetProperty("name", out _));
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

    [Fact, Trait("Category", "Network")]
    public async Task A_fully_named_snapshot_survives_transport_encoding_within_the_relays_record_limit()
    {
        source.Sessions = Enumerable.Range(1, 64).Select(NewSession).ToList();
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "publisher", "host");
        var displayName = new string('+', 64);
        Assert.True(await new FleetPublisher(source, host.Id, _ => displayName, time).PublishAsync(host.Relay.RegistryPutAsync));
        var sessions = Assert.Single(await host.Relay.RegistryGetAsync()).GetProperty("record").GetProperty("sessions");
        Assert.Equal(64, sessions.GetArrayLength());
        Assert.All(sessions.EnumerateArray(), s => Assert.Equal(displayName, s.GetProperty("name").GetString()));
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
