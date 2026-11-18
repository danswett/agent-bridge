using System.Text.Json;
using AgentBridge.Relay;
using Xunit;

namespace AgentBridge.Transport.Tests;

public sealed class RegistryClockTests
{
    static readonly DateTimeOffset Initial = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);
    static JsonElement Record(long sequence) => JsonSerializer.SerializeToElement(new
    {
        v = 1, machine = "fixture", epoch = "0123456789abcdef", seq = sequence,
        at = Initial, truncated = false,
        sessions = new[] { new { id = "0000000000000001", node = "agent_bridge_0000000000000001", state = "idle", since = Initial } },
    });

    [Fact]
    public void A_publication_after_the_read_samples_time_is_not_erased_as_a_clock_rewind()
    {
        var clock = new Clock();
        var registry = new SessionRegistry(clock);
        var original = Record(1);
        var replacement = Record(2);
        Assert.Null(registry.Put("account", "fixture", original));
        clock.BeforeReturn = () =>
        {
            clock.Now = Initial.AddTicks(1);
            Assert.Null(registry.Put("account", "fixture", replacement));
        };

        var overlapping = Assert.Single(registry.Read("account"));
        Assert.True(overlapping.Fresh);
        Assert.Contains(overlapping.Json, new[] { original.GetRawText(), replacement.GetRawText() });
        var published = Assert.Single(registry.Read("account"));
        Assert.True(published.Fresh);
        Assert.Equal(replacement.GetRawText(), published.Json);
        Assert.Equal(Initial.AddTicks(1), published.LastSeen);
    }

    [Fact]
    public void A_clock_rewind_expires_the_payload_and_clock_recovery_cannot_resurrect_it()
    {
        var clock = new Clock();
        var registry = new SessionRegistry(clock);
        Assert.Null(registry.Put("account", "fixture", Record(1)));
        clock.Now = Initial.AddTicks(-1);
        var rewound = Assert.Single(registry.Read("account"));
        Assert.False(rewound.Fresh);
        Assert.Equal("null", rewound.Json);
        clock.Now = Initial;
        var recovered = Assert.Single(registry.Read("account"));
        Assert.False(recovered.Fresh);
        Assert.Equal("null", recovered.Json);
        Assert.Equal("stale", registry.Put("account", "fixture", Record(1)));
        Assert.Null(registry.Put("account", "fixture", Record(2)));
        Assert.True(Assert.Single(registry.Read("account")).Fresh);
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(600_000_000L, true)]
    [InlineData(600_000_001L, false)]
    public void Freshness_includes_zero_and_exactly_sixty_seconds_but_not_one_tick_more(long ticks, bool fresh)
    {
        var clock = new Clock();
        var registry = new SessionRegistry(clock);
        var record = Record(1);
        Assert.Null(registry.Put("account", "fixture", record));
        clock.Now = Initial.AddTicks(ticks);
        var held = Assert.Single(registry.Read("account"));
        Assert.Equal(fresh, held.Fresh);
        Assert.Equal(fresh ? record.GetRawText() : "null", held.Json);
    }

    [Fact]
    public async Task Concurrent_expired_reads_return_only_tombstones_and_preserve_the_replay_watermark()
    {
        var clock = new Clock();
        var registry = new SessionRegistry(clock);
        Assert.Null(registry.Put("account", "fixture", Record(2)));
        clock.Now = Initial.AddSeconds(61);
        using var readers = new Barrier(2);
        clock.Readers = readers;
        var results = await Task.WhenAll(Task.Run(() => registry.Read("account")), Task.Run(() => registry.Read("account")))
            .WaitAsync(TimeSpan.FromSeconds(15));
        clock.Readers = null;
        Assert.All(results, result =>
        {
            var held = Assert.Single(result);
            Assert.False(held.Fresh);
            Assert.Equal("null", held.Json);
            Assert.Equal(Initial, held.LastSeen);
        });
        Assert.Equal("stale", registry.Put("account", "fixture", Record(1)));
        Assert.Null(registry.Put("account", "fixture", Record(3)));
        Assert.True(Assert.Single(registry.Read("account")).Fresh);
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Initial;
        public Barrier? Readers { get; set; }
        public Action? BeforeReturn { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            var now = Now;
            var beforeReturn = BeforeReturn;
            BeforeReturn = null;
            beforeReturn?.Invoke();
            if (Readers is { } readers && !readers.SignalAndWait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Concurrent registry readers did not rendezvous"); }
            return now;
        }
    }
}
