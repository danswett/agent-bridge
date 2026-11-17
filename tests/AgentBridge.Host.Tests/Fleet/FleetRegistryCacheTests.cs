using AgentBridge.Host.Fleet;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class FleetRegistryCacheTests
{
    static readonly DateTimeOffset At = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    readonly FakeTimeProvider clock = new(At);
    readonly FleetRegistryCache cache;

    public FleetRegistryCacheTests() => cache = new(clock, "account-a");

    static VerifiedRecord Record(string machine = "host", long seq = 2, string epoch = "epoch-a",
        DateTimeOffset? at = null, bool verified = true, bool online = true) =>
        new(new(machine, online, [new("agent_bridge_aaaaaaaaaaaaaaaa", "SG Fixture", "idle", At)],
            Verified: verified, HasRegistryRecord: true), epoch, seq, at ?? At);

    bool Accept(VerifiedRecord? record = null) => cache.Accept(record ?? Record(), "account-a");

    [Fact]
    public void Neither_unsolicited_nor_other_account_records_are_cached()
    {
        Assert.False(Accept());
        Assert.Empty(cache.Served());
        cache.OptIn();
        Assert.False(cache.Accept(Record(), "account-b"));
        Assert.Empty(cache.Served());
        Assert.True(Accept());
    }

    [Fact]
    public void An_unverified_record_never_enters_or_replaces_the_cache()
    {
        cache.OptIn();
        Assert.False(Accept(Record(verified: false)));
        Assert.Empty(cache.Served());
        Assert.True(Accept());
        Assert.False(Accept(Record(seq: 3, verified: false)));
        Assert.True(Assert.Single(cache.Served()).Machine.Verified);
        Assert.True(Accept(Record(seq: 3)));
    }

    [Fact]
    public void Liveness_expires_at_sixty_seconds_without_losing_offline_history()
    {
        cache.OptIn();
        Assert.True(Accept());
        var live = Assert.Single(cache.Served());
        Assert.False(live.Stale);
        Assert.True(live.Online);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(Assert.Single(cache.Served()).Stale);
        clock.Advance(TimeSpan.FromTicks(1));
        var stale = Assert.Single(cache.Served());
        Assert.True(stale.Stale);
        Assert.False(stale.Online);
        Assert.False(stale.Machine.Online);
        Assert.Single(stale.Machine.Sessions);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 61)]
    public void Offline_or_excessively_future_records_cannot_be_served_live(bool online, int seconds)
    {
        cache.OptIn();
        Assert.True(Accept(Record(at: At.AddSeconds(seconds), online: online)));
        var served = Assert.Single(cache.Served());
        Assert.True(served.Stale);
        Assert.False(served.Machine.Online);
    }

    [Fact]
    public void Removing_drops_sessions_and_blocks_replays_until_the_machine_is_listed_again()
    {
        cache.OptIn();
        Assert.True(Accept(Record(seq: 100)));
        cache.Remove("host");
        Assert.Empty(cache.Served());
        Assert.False(Accept(Record(seq: 99)));
        Assert.False(Accept(Record(seq: 101)));
        cache.Reconcile(new HashSet<string> { "host" });
        Assert.True(Accept(Record(seq: 1, epoch: "restart")));
        Assert.Single(cache.Served());
    }

    [Fact]
    public void A_forgotten_machine_can_reconnect_with_its_old_epoch_and_higher_sequence()
    {
        cache.OptIn();
        Assert.True(Accept());
        cache.Remove("host");
        cache.Reconcile(new HashSet<string> { "host" });
        Assert.True(Accept(Record(seq: 3)));
    }

    [Fact]
    public void Only_increasing_sequences_or_later_epoch_restarts_replace_a_record()
    {
        cache.OptIn();
        Assert.True(Accept());
        Assert.False(Accept(Record(seq: 1)));
        Assert.False(Accept());
        Assert.True(Accept(Record(seq: 3)));
        Assert.False(Accept(Record(epoch: "restart", seq: 1)));
        Assert.True(Accept(Record(epoch: "restart", seq: 1, at: At.AddSeconds(1))));
        Assert.False(Accept(Record(seq: 4)));
        Assert.True(Accept(Record(epoch: "restart", seq: 2, at: At.AddSeconds(2))));
    }

    [Fact]
    public void A_get_purges_omitted_machines_and_keeps_known_only_machines_without_sessions()
    {
        cache.OptIn();
        Assert.True(Accept(Record("omitted")));
        Assert.True(Accept(Record("known")));
        Assert.True(Accept(Record("registry")));
        cache.OptIn();
        Assert.True(Accept(Record("registry", seq: 3)));
        cache.Reconcile(new HashSet<string> { "known", "registry" });
        var served = cache.Served();
        Assert.Equal(2, served.Count);
        var known = Assert.Single(served, r => r.Machine.Id == "known");
        Assert.True(known.Stale);
        Assert.False(known.Machine.Online);
        Assert.Empty(known.Machine.Sessions);
        Assert.False(known.Machine.HasRegistryRecord);
        Assert.True(Assert.Single(served, r => r.Machine.Id == "registry").Online);
        Assert.False(Accept(Record("omitted", seq: 3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cached_records_are_deleted_after_twenty_four_hours_even_without_an_explicit_purge(bool explicitPurge)
    {
        cache.OptIn();
        Assert.True(Accept());
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        cache.Purge();
        Assert.True(Assert.Single(cache.Served()).Stale);
        clock.Advance(TimeSpan.FromTicks(2));
        if (explicitPurge) { cache.Purge(); }
        Assert.Empty(cache.Served());
    }

    [Fact]
    public void Reconciliation_before_opt_in_cannot_restore_a_removed_machine()
    {
        cache.Remove("host");
        cache.Reconcile(new HashSet<string> { "host" });
        cache.OptIn();
        Assert.False(Accept());
    }
}
