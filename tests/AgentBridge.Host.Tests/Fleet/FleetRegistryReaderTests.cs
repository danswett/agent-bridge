using System.Text.Json;
using AgentBridge.Host.Fleet;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Slice RC (docs/design/fleet-session-registry.md "Freshness and replay"): the host as a registry
// reader. Pure state, no sockets: frames in, a snapshot of machines out.
public sealed class FleetRegistryReaderTests
{
    const string Epoch = "0123456789abcdef";
    const string Other = "fedcba9876543210";
    const string M = "aaaaaaaaaaaaaaaa";
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    string Record(long seq = 1, string epoch = Epoch, double ageSeconds = 0, string machine = M) =>
        $$"""{"v":1,"machine":"{{machine}}","epoch":"{{epoch}}","seq":{{seq}},"at":"{{time.GetUtcNow().AddSeconds(-ageSeconds):o}}","truncated":false,"sessions":[]}""";

    JsonElement Part(string record, bool online = true, string machine = M) =>
        J($$"""{"t":"registry","machine":"{{machine}}","name":"box","online":{{online.ToString().ToLowerInvariant()}},"lastSeen":"{{time.GetUtcNow():o}}","record":{{record}}}""");

    JsonElement Changed(string record, string machine = M) =>
        J($$"""{"t":"registry-changed","machine":"{{machine}}","online":true,"record":{{record}}}""");

    static JsonElement Known(params string[] machines) =>
        J("""{"t":"registry","known":[""" + string.Join(",", machines.Select(m => $$"""{"machine":"{{m}}","name":"k","online":false}""")) + "]}");

    static long SeqOf(RegistryMachine m) => m.Record!.Value.GetProperty("seq").GetInt64();

    [Fact]
    public void Within_an_epoch_a_lower_seq_is_refused_and_an_equal_one_accepted_and_a_new_epoch_needs_a_later_at()
    {
        var reader = new FleetRegistryReader(time);
        reader.ApplyGet([Part(Record(seq: 5)), Known()]);
        reader.ApplyChanged(Changed(Record(seq: 4)));
        Assert.Equal(5, SeqOf(Assert.Single(reader.Snapshot())));
        reader.ApplyChanged(Changed(Record(seq: 5)));
        reader.ApplyChanged(Changed(Record(seq: 6)));
        Assert.Equal(6, SeqOf(Assert.Single(reader.Snapshot())));

        time.Advance(TimeSpan.FromSeconds(10));
        reader.ApplyChanged(Changed(Record(seq: 1, epoch: Other, ageSeconds: 20)));
        Assert.Equal(6, SeqOf(Assert.Single(reader.Snapshot())));
        reader.ApplyChanged(Changed(Record(seq: 1, epoch: Other)));
        Assert.Equal(Other, Assert.Single(reader.Snapshot()).Record!.Value.GetProperty("epoch").GetString());
        reader.ApplyChanged(Changed(Record(seq: 9)));
        Assert.Equal(Other, Assert.Single(reader.Snapshot()).Record!.Value.GetProperty("epoch").GetString());
    }

    [Fact]
    public void A_record_more_than_sixty_seconds_from_the_readers_clock_is_refused_and_a_held_one_goes_stale()
    {
        var reader = new FleetRegistryReader(time);
        reader.ApplyGet([Part(Record(ageSeconds: 61)), Known()]);
        // Listed and online, but with no usable record: shown with no sessions, never live.
        Assert.Null(Assert.Single(reader.Snapshot()).Record);
        reader.ApplyChanged(Changed(Record(seq: 1, ageSeconds: -61)));
        Assert.Null(Assert.Single(reader.Snapshot()).Record);

        reader.ApplyChanged(Changed(Record(seq: 2)));
        Assert.True(Assert.Single(reader.Snapshot()).Live);
        time.Advance(TimeSpan.FromSeconds(61));
        var stale = Assert.Single(reader.Snapshot());
        Assert.False(stale.Live);
        Assert.NotNull(stale.Record);
    }

    [Fact]
    public void An_offline_change_keeps_the_last_copy_as_stale_and_the_relay_online_flag_alone_never_makes_it_live()
    {
        var reader = new FleetRegistryReader(time);
        reader.ApplyChanged(Changed(Record()));
        reader.ApplyChanged(J($$"""{"t":"registry-changed","machine":"{{M}}","online":false,"record":null}"""));
        var gone = Assert.Single(reader.Snapshot());
        Assert.False(gone.Live);
        Assert.False(gone.Online);
        Assert.NotNull(gone.Record);
        reader.ApplyGet([Part(Record(seq: 2), online: false), Known()]);
        Assert.False(Assert.Single(reader.Snapshot()).Live);
    }

    [Fact]
    public void Removed_purges_the_machine_and_its_history_so_a_reconnecting_machine_is_accepted_at_any_epoch()
    {
        var reader = new FleetRegistryReader(time);
        reader.ApplyChanged(Changed(Record(seq: 50, epoch: Other)));
        reader.ApplyChanged(J($$"""{"t":"registry-changed","machine":"{{M}}","removed":true}"""));
        Assert.Empty(reader.Snapshot());

        reader.ApplyChanged(Changed(Record(seq: 1, epoch: Epoch, ageSeconds: 5)));
        var back = Assert.Single(reader.Snapshot());
        Assert.Equal(Epoch, back.Record!.Value.GetProperty("epoch").GetString());
        Assert.True(back.Live);
    }

    [Fact]
    public void A_get_drops_machines_it_no_longer_lists_and_keeps_a_listed_but_silent_machine_offline_without_a_record()
    {
        var reader = new FleetRegistryReader(time);
        const string Gone = "bbbbbbbbbbbbbbbb";
        reader.ApplyChanged(Changed(Record(machine: Gone), Gone));
        reader.ApplyChanged(Changed(Record()));
        reader.ApplyGet([Part(Record(seq: 2)), Known(M)]);
        Assert.Equal([M], reader.Snapshot().Select(m => m.Id));

        reader.ApplyGet([Known(M, "cccccccccccccccc")]);
        var quiet = reader.Snapshot().Single(m => m.Id == "cccccccccccccccc");
        Assert.False(quiet.Online);
        Assert.Null(quiet.Record);
        Assert.NotNull(reader.Snapshot().Single(m => m.Id == M).Record);
    }

    [Fact]
    public void A_cached_copy_is_deleted_after_twenty_four_hours_on_the_monotonic_clock()
    {
        var reader = new FleetRegistryReader(time);
        reader.ApplyChanged(Changed(Record()));
        time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Single(reader.Snapshot());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(reader.Snapshot());
    }

    [Fact]
    public void Once_an_order_is_held_a_record_with_a_missing_or_malformed_epoch_seq_or_at_is_refused()
    {
        var reader = new FleetRegistryReader(time);
        reader.ApplyChanged(Changed(Record(seq: 3)));
        foreach (var bad in new[]
        {
            """{"v":1,"machine":"aaaaaaaaaaaaaaaa","seq":9,"at":"2026-11-22T12:00:00Z","truncated":false,"sessions":[]}""",
            $$"""{"v":1,"machine":"aaaaaaaaaaaaaaaa","epoch":"{{Epoch}}","seq":0,"at":"2026-11-22T12:00:00Z","truncated":false,"sessions":[]}""",
            $$"""{"v":1,"machine":"aaaaaaaaaaaaaaaa","epoch":"{{Epoch}}","seq":9,"at":"yesterday","truncated":false,"sessions":[]}""",
            $$"""{"v":1,"machine":"bbbbbbbbbbbbbbbb","epoch":"{{Epoch}}","seq":9,"at":"2026-11-22T12:00:00Z","truncated":false,"sessions":[]}""",
        })
        {
            reader.ApplyChanged(Changed(bad));
        }
        Assert.Equal(3, SeqOf(Assert.Single(reader.Snapshot())));
    }

    [Fact]
    public void A_record_is_verified_only_when_the_verifier_says_so_and_one_it_refuses_is_not_kept()
    {
        var unverified = new FleetRegistryReader(time);
        unverified.ApplyChanged(Changed(Record()));
        Assert.False(Assert.Single(unverified.Snapshot()).Verified);

        var yes = new FleetRegistryReader(time, (_, _) => true);
        yes.ApplyChanged(Changed(Record()));
        Assert.True(Assert.Single(yes.Snapshot()).Verified);

        var no = new FleetRegistryReader(time, (_, _) => false);
        no.ApplyChanged(Changed(Record()));
        Assert.Empty(no.Snapshot());
    }
}
