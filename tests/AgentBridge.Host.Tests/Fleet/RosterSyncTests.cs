using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Roster;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class RosterSyncTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 5, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task An_ended_session_is_removed_from_the_relay_without_waiting_for_the_publish_interval(int milliseconds)
    {
        var time = new FakeTimeProvider(Now);
        var source = new FleetPublisherTests.Source();
        var registry = new SessionRegistry(time);
        var publisher = new FleetPublisher(source, "host", time: time);
        Task Put(JsonElement record, CancellationToken _) =>
            Accepted(registry, "host", record);
        Assert.True(await publisher.PublishAsync(Put));
        var ended = source.Sessions[0].Id[..16];
        source.Sessions.RemoveAt(0);
        time.Advance(TimeSpan.FromMilliseconds(milliseconds));

        Assert.True(await publisher.PublishAsync(Put));

        var sessions = JsonSerializer.Deserialize<JsonElement>(Assert.Single(registry.Read(RosterFixture.Account)).Json)
            .GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.DoesNotContain(sessions, s => s.GetProperty("id").GetString() == ended);
    }

    [Fact]
    public async Task An_unreachable_host_has_no_session_rows_even_if_a_cached_record_arrives_with_it()
    {
        JsonElement record = default;
        var publisher = new FleetPublisher(new FleetPublisherTests.Source(), "host");
        Assert.True(await publisher.PublishAsync((value, _) => { record = value; return Task.CompletedTask; }));

        var machines = RelayFleetRoster.Parse([Frame("host", record, online: false)]);

        Assert.All(machines, machine => Assert.Empty(machine.Sessions));
    }

    [Fact]
    public async Task Cross_host_sync_reads_only_hosts_present_in_the_verified_device_roster()
    {
        var world = new World();
        world.Genesis();
        world.AddHostV2("host", "G", "H", "G");
        var verified = new RosterVerifier(world.AccountId, world.GenerationId, () => world.Now) { AcceptHostSign = true };
        Assert.All(world.Feed(verified, "G", "host"), offered => Assert.Equal(Verdict.Accepted, offered.Verdict));
        Assert.Contains(world.Id("H"), verified.Serving.Keys);
        var time = new FakeTimeProvider(Now);
        var reader = new RelayFleetRoster(() => verified, time);
        var frames = new List<JsonElement>();
        foreach (var device in new[] { "H", "unpaired" })
        {
            var machine = world.Id(device);
            var publisher = new FleetPublisher(new FleetPublisherTests.Source(), machine, time: time,
                sign: value => FleetRecordSigning.Sign(value, World.Keys(device).Sign.Sign));
            Assert.True(await publisher.PublishAsync((value, _) =>
            {
                frames.Add(Frame(machine, value));
                return Task.CompletedTask;
            }));
        }

        var machines = reader.Read(frames);

        Assert.Equal([world.Id("H")], machines.Where(m => m.Sessions.Count > 0).Select(m => m.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Signed_publication_preserves_only_a_bound_process_start_through_the_relay(bool bound)
    {
        var time = new FakeTimeProvider(Now);
        var source = new FleetPublisherTests.Source
        {
            Sessions = [FleetPublisherTests.NewSession(1) with { StartedAt = Now.AddDays(-1) }],
            Binding = _ => bound ? new SessionProcess(100, Now.AddMinutes(-5)) : null,
        };
        var registry = new SessionRegistry(time);
        var key = World.Keys("H").Sign;
        var publisher = new FleetPublisher(source, "host", time: time,
            sign: value => FleetRecordSigning.Sign(value, key.Sign));
        Assert.True(await publisher.PublishAsync((record, _) =>
        {
            Assert.True(FleetRecordSigning.Verify(record, key.Public));
            return Accepted(registry, "host", record);
        }));

        var held = JsonSerializer.Deserialize<JsonElement>(Assert.Single(registry.Read(RosterFixture.Account)).Json);
        var session = Assert.Single(held.GetProperty("sessions").EnumerateArray());
        if (bound) { Assert.Equal(Now.AddMinutes(-5), session.GetProperty("startedAt").GetDateTimeOffset()); }
        else { Assert.False(session.TryGetProperty("startedAt", out _)); }
        Assert.DoesNotContain("private", held.GetRawText());
    }

    [Fact]
    public async Task An_empty_snapshot_removes_ended_sessions_and_an_older_snapshot_cannot_resurrect_them()
    {
        var time = new FakeTimeProvider(Now);
        var source = new FleetPublisherTests.Source();
        var registry = new SessionRegistry(time);
        var publisher = new FleetPublisher(source, "host", time: time);
        JsonElement previous = default;
        Assert.True(await publisher.PublishAsync((record, _) =>
        {
            previous = record;
            return Accepted(registry, "host", record);
        }));
        source.Sessions.Clear();
        Assert.True(await publisher.PublishAsync((record, _) => Accepted(registry, "host", record), force: true));

        Assert.Equal("stale", registry.Put(RosterFixture.Account, "host", previous));
        var current = JsonSerializer.Deserialize<JsonElement>(Assert.Single(registry.Read(RosterFixture.Account)).Json);
        Assert.Empty(current.GetProperty("sessions").EnumerateArray());
        registry.Drop(RosterFixture.Account, "host");
        Assert.Empty(registry.Read(RosterFixture.Account));
    }

    [Fact]
    public async Task Idle_sessions_are_still_live_and_are_not_inferred_to_be_finished()
    {
        var source = new FleetPublisherTests.Source();
        var publisher = new FleetPublisher(source, "host");
        JsonElement record = default;
        Assert.True(await publisher.PublishAsync((value, _) => { record = value; return Task.CompletedTask; }));

        var sessions = Assert.Single(RelayFleetRoster.Parse([Frame("host", record)])).Sessions;

        Assert.Equal(source.Sessions.Count, sessions.Count);
        Assert.All(sessions, session => Assert.Equal("idle", session.State));
    }

    static Task Accepted(SessionRegistry registry, string machine, JsonElement record)
    {
        Assert.Null(registry.Put(RosterFixture.Account, machine, record));
        return Task.CompletedTask;
    }

    static JsonElement Frame(string machine, JsonElement record, bool online = true) =>
        JsonSerializer.SerializeToElement(new { machine, online, record }, ProtocolJson.Options);
}
