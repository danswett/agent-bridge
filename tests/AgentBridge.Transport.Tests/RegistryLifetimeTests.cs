using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Relay;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

public sealed class RegistryLifetimeTests
{
    static JsonElement Record(long sequence, params string[] ids) => JsonSerializer.SerializeToElement(new
    {
        v = 1, machine = "machine", epoch = "0123456789abcdef", seq = sequence,
        at = "2026-11-17T20:00:00Z", truncated = false, sig = "test-signature",
        sessions = ids.Select(id => new { id, node = "agent_bridge_" + id, state = "idle", since = "2026-11-17T19:00:00Z" }),
    });

    [Fact]
    public void A_new_snapshot_removes_ended_sessions_immediately_without_merging_or_waiting_for_expiry()
    {
        var time = new FakeTimeProvider();
        var registry = new SessionRegistry(time);
        Assert.Null(registry.Put("account", "machine", Record(1, "0000000000000001", "0000000000000002")));
        var replacement = Record(2, "0000000000000002");
        Assert.Null(registry.Put("account", "machine", replacement));
        var held = Assert.Single(registry.Read("account"));
        Assert.True(held.Fresh);
        Assert.Equal(time.GetUtcNow(), held.LastSeen);
        Assert.Equal(replacement.GetRawText(), held.Json);
        Assert.DoesNotContain("0000000000000001", held.Json);
        Assert.Empty(registry.Read("other-account"));
        Assert.Equal("stale", registry.Put("account", "machine", Record(1, "0000000000000001")));
        Assert.Null(registry.Put("account", "machine", Record(3)));
        Assert.Empty(JsonDocument.Parse(Assert.Single(registry.Read("account")).Json).RootElement.GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public void Disconnect_drops_the_sessions_at_once_without_advancing_the_clock()
    {
        var registry = new SessionRegistry(new FakeTimeProvider());
        Assert.Null(registry.Put("account", "machine", Record(1, "0000000000000001")));
        registry.Drop("other-account", "machine");
        Assert.Single(registry.Read("account"));
        registry.Drop("account", "machine");
        Assert.Empty(registry.Read("account"));
    }

    [Fact]
    public void Expired_payloads_are_erased_without_losing_the_replay_watermark_or_offline_metadata()
    {
        var time = new FakeTimeProvider();
        var clock = new ShiftableClock(time);
        var registry = new SessionRegistry(clock);
        Assert.Null(registry.Put("account", "machine", Record(2, "0000000000000001")));
        time.Advance(RosterFields.HeartbeatWindow + TimeSpan.FromTicks(1));
        var expired = Assert.Single(registry.Read("account"));
        Assert.False(expired.Fresh);
        Assert.Equal("null", expired.Json);
        clock.Offset = -RosterFields.HeartbeatWindow;
        var rewound = Assert.Single(registry.Read("account"));
        Assert.False(rewound.Fresh);
        Assert.Equal("null", rewound.Json);
        Assert.Equal("stale", registry.Put("account", "machine", Record(1, "0000000000000001")));
        var invalid = JsonNode.Parse(Record(3).GetRawText())!.AsObject();
        invalid["prompt"] = "not allowed";
        Assert.Equal("bad-record", registry.Put("account", "machine", JsonSerializer.SerializeToElement(invalid)));
        Assert.Equal("null", Assert.Single(registry.Read("account")).Json);
        var resumed = Record(3, "0000000000000002");
        Assert.Null(registry.Put("account", "machine", resumed));
        Assert.Equal(resumed.GetRawText(), Assert.Single(registry.Read("account")).Json);
        Assert.True(Assert.Single(registry.Read("account")).Fresh);
    }

    sealed class ShiftableClock(TimeProvider inner) : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Offset;
    }
}
