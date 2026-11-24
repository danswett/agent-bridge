using AgentBridge.Host.Core.Launch;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The host's launch requests, keyed by the caller's request id (#2743): one slot per id, never a
// shared one that a second caller overwrites, and kept across a restart.
public sealed class LaunchRequestStoreTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("ablaunchq").FullName;
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 16, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    string FilePath => Path.Combine(_folder, LaunchRequestStore.FileName);

    LaunchRequestStore Open(int maxPending = 8)
    {
        var (store, problem) = LaunchRequestStore.Open(_folder, _time, maxPending);
        Assert.Null(problem);
        return store!;
    }

    [Fact]
    public void The_same_id_twice_returns_one_record_and_is_not_queued_again()
    {
        var store = Open();
        var first = store.Enqueue("req-1", "start the build");
        Assert.True(store.Begin("req-1"));
        Assert.True(store.Launched("req-1", "session-a"));

        var again = store.Enqueue("req-1", "start the build");

        Assert.Equal(EnqueueStatus.Added, first.Status);
        Assert.Equal(EnqueueStatus.Existing, again.Status);
        Assert.Equal(LaunchRequestState.Launched, again.Record!.State);
        Assert.Equal("session-a", again.Record.Session);
        Assert.Single(store.All());
    }

    [Fact]
    public void Two_ids_do_not_overwrite_each_other()
    {
        var store = Open();
        store.Enqueue("req-1", "one");
        store.Enqueue("req-2", "two");
        store.Begin("req-2");
        store.Refuse("req-2", "busy");

        Assert.Equal(LaunchRequestState.Queued, store.Get("req-1")!.State);
        Assert.Equal(LaunchRequestState.Refused, store.Get("req-2")!.State);
        Assert.Equal("busy", store.Get("req-2")!.Code);
    }

    [Fact]
    public void A_state_never_moves_backward_or_skips_launching()
    {
        var store = Open();
        store.Enqueue("req-1", "one");
        Assert.False(store.Launched("req-1", "session-a"));
        Assert.True(store.Begin("req-1"));
        Assert.False(store.Begin("req-1"));
        Assert.True(store.Fail("req-1", "agent-exited"));
        Assert.False(store.Launched("req-1", "session-a"));
        Assert.False(store.Refuse("req-1", "busy"));
        Assert.False(store.Begin("unknown"));

        var record = store.Get("req-1")!;
        Assert.Equal(LaunchRequestState.Failed, record.State);
        Assert.Equal("agent-exited", record.Code);
    }

    [Fact]
    public void A_restart_keeps_finished_and_queued_records_without_the_launch_text()
    {
        var store = Open();
        store.Enqueue("req-1", "secret prompt text");
        store.Enqueue("req-2", "other");
        store.Begin("req-2");
        store.Launched("req-2", "session-b");

        var reopened = Open();

        Assert.Equal(LaunchRequestState.Queued, reopened.Get("req-1")!.State);
        Assert.Equal("session-b", reopened.Get("req-2")!.Session);
        Assert.Equal(EnqueueStatus.Existing, reopened.Enqueue("req-1", "secret prompt text").Status);
        Assert.DoesNotContain("secret prompt text", File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_record_left_launching_fails_as_restarted_after_a_restart()
    {
        var store = Open();
        store.Enqueue("req-1", "one");
        store.Begin("req-1");

        var record = Open().Get("req-1")!;

        Assert.Equal(LaunchRequestState.Failed, record.State);
        Assert.Equal("restarted", record.Code);
    }

    [Fact]
    public void A_finished_record_is_readable_at_14_minutes_and_pruned_after_the_retention()
    {
        var store = Open();
        store.Enqueue("done", "one");
        store.Begin("done");
        store.Launched("done", "session-a");
        store.Enqueue("waiting", "two");

        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.NotNull(store.Get("done"));

        _time.Advance(LaunchRequestStore.Retention - TimeSpan.FromMinutes(14));
        Assert.Null(store.Get("done"));
        Assert.Null(Open().Get("done"));
        Assert.NotNull(store.Get("waiting"));
    }

    [Fact]
    public void A_damaged_file_is_refused_with_a_reason_and_left_in_place()
    {
        File.WriteAllText(FilePath, "{\"version\":1,\"requests\":[{\"id\":");
        var before = File.ReadAllBytes(FilePath);

        var (store, problem) = LaunchRequestStore.Open(_folder, _time);

        Assert.Null(store);
        Assert.Contains(LaunchRequestStore.FileName, problem);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void A_full_queue_refuses_a_new_id_until_one_finishes()
    {
        var store = Open(maxPending: 2);
        store.Enqueue("req-1", "one");
        store.Enqueue("req-2", "two");

        var full = store.Enqueue("req-3", "three");
        store.Begin("req-1");
        store.Refuse("req-1", "busy");
        var after = store.Enqueue("req-3", "three");

        Assert.Equal(EnqueueStatus.Full, full.Status);
        Assert.Null(full.Record);
        Assert.Equal(EnqueueStatus.Added, after.Status);
    }

    [Fact]
    public void The_same_id_with_different_text_is_a_conflict_that_keeps_the_first()
    {
        var store = Open();
        store.Enqueue("req-1", "one");

        var conflict = store.Enqueue("req-1", "two");

        Assert.Equal(EnqueueStatus.Conflict, conflict.Status);
        Assert.Equal(LaunchRequestState.Queued, conflict.Record!.State);
        Assert.Single(store.All());
    }

    // #2743: each id launches at most once, durably; the 15 minutes is only how long its outcome
    // is readable (reviewer's probe on #3404).
    [Fact]
    public void A_completed_id_cannot_be_launched_again_after_outcome_retention_and_restart()
    {
        var store = Open();
        store.Enqueue("req-1", "one");
        store.Begin("req-1");
        store.Launched("req-1", "session-a");
        _time.Advance(LaunchRequestStore.Retention);

        var reopened = Open();

        Assert.Equal(new EnqueueResult(EnqueueStatus.Used, null), reopened.Enqueue("req-1", "one"));
        Assert.False(reopened.Begin("req-1"));
        Assert.Equal(new EnqueueResult(EnqueueStatus.Conflict, null), reopened.Enqueue("req-1", "two"));
        Assert.False(reopened.Begin("req-1"));
    }

    [Fact]
    public void An_expired_outcome_is_not_readable_but_its_id_stays_used()
    {
        var store = Open();
        store.Enqueue("req-1", "one");
        store.Refuse("req-1", "busy");
        _time.Advance(LaunchRequestStore.Retention);

        Assert.Null(store.Get("req-1"));
        Assert.Empty(store.All());
        Assert.Equal(EnqueueStatus.Used, store.Enqueue("req-1", "one").Status);
        Assert.Null(store.Get("req-1"));
    }

    [Fact]
    public void A_used_id_survives_several_restarts()
    {
        var store = Open();
        store.Enqueue("req-1", "one");
        store.Fail("req-1", "failed");

        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromDays(7));
            store = Open();
            Assert.Equal(EnqueueStatus.Used, store.Enqueue("req-1", "one").Status);
        }
        Assert.Contains("\"req-1\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_file_from_before_used_ids_loads_and_its_finished_ids_stay_used()
    {
        var now = _time.GetUtcNow();
        File.WriteAllText(FilePath, $$"""
            {"version":1,"requests":[
            {"id":"old","state":"launched","textHash":"{{Hash("one")}}","session":"s-1","queuedAt":"{{now.AddHours(-1):o}}","changedAt":"{{now.AddHours(-1):o}}"},
            {"id":"recent","state":"refused","textHash":"{{Hash("two")}}","code":"busy","queuedAt":"{{now.AddMinutes(-5):o}}","changedAt":"{{now.AddMinutes(-5):o}}"}]}
            """);

        var store = Open();

        Assert.Null(store.Get("old"));
        Assert.Equal(EnqueueStatus.Used, store.Enqueue("old", "one").Status);
        Assert.Equal(EnqueueStatus.Conflict, store.Enqueue("old", "other").Status);
        Assert.Equal("busy", store.Get("recent")!.Code);
        _time.Advance(LaunchRequestStore.Retention);
        Assert.Equal(EnqueueStatus.Used, Open().Enqueue("recent", "two").Status);
    }

    [Fact]
    public void At_the_used_id_cap_a_new_id_is_refused_until_the_oldest_are_past_the_age_bound()
    {
        var at = _time.GetUtcNow().AddDays(-1).ToString("o");
        var used = Enumerable.Range(0, LaunchRequestStore.MaxUsedIds).Select(i => $$"""{"id":"u{{i}}","textHash":"{{Hash("x")}}","finishedAt":"{{at}}"}""");
        File.WriteAllText(FilePath, $$"""{"version":1,"requests":[],"used":[{{string.Join(",", used)}}]}""");

        var store = Open();
        Assert.Equal(new EnqueueResult(EnqueueStatus.Full, null), store.Enqueue("new", "one"));
        Assert.Equal(EnqueueStatus.Used, store.Enqueue("u0", "x").Status);

        _time.Advance(LaunchRequestStore.UsedKept - TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1));
        Assert.Equal(EnqueueStatus.Full, Open().Enqueue("new", "one").Status);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(EnqueueStatus.Added, Open().Enqueue("new", "one").Status);
    }

    // Documented: past the age bound an id is forgotten and may be used again.
    [Fact]
    public void A_used_id_is_forgotten_only_after_the_age_bound()
    {
        var store = Open();
        store.Enqueue("req-1", "one");
        store.Refuse("req-1", "busy");
        store.Enqueue("req-2", "two");
        _time.Advance(TimeSpan.FromDays(1));
        store.Refuse("req-2", "busy");

        _time.Advance(LaunchRequestStore.UsedKept - TimeSpan.FromDays(1) - TimeSpan.FromMinutes(1));
        Assert.Equal(EnqueueStatus.Used, Open().Enqueue("req-1", "one").Status);

        _time.Advance(TimeSpan.FromMinutes(1));
        var store2 = Open();
        Assert.Equal(EnqueueStatus.Used, store2.Enqueue("req-2", "two").Status);
        Assert.Equal(EnqueueStatus.Added, store2.Enqueue("req-1", "one").Status);
    }

    [Theory]
    [InlineData("""{"version":1,"requests":[],"used":{}}""")]
    [InlineData("""{"version":1,"requests":[],"used":[{"id":"a","textHash":"00","finishedAt":"2026-11-20T00:00:00Z"}]}""")]
    [InlineData("""{"version":1,"requests":[],"used":[{"id":"a","textHash":"HASH"}]}""")]
    [InlineData("""{"version":1,"requests":[],"used":[{"id":"a","textHash":"HASH","finishedAt":"2026-11-20T00:00:00Z"},{"id":"a","textHash":"HASH","finishedAt":"2026-11-20T00:00:00Z"}]}""")]
    [InlineData("""{"version":1,"requests":[{"id":"a","state":"queued","textHash":"HASH","queuedAt":"2026-11-20T00:00:00Z","changedAt":"2026-11-20T00:00:00Z"}],"used":[{"id":"a","textHash":"HASH","finishedAt":"2026-11-20T00:00:00Z"}]}""")]
    public void A_damaged_used_id_list_is_refused_and_left_in_place(string json)
    {
        File.WriteAllText(FilePath, json.Replace("HASH", Hash("x"), StringComparison.Ordinal));
        var before = File.ReadAllBytes(FilePath);

        Assert.Null(LaunchRequestStore.Open(_folder, _time).Store);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan())));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    public void An_id_outside_the_length_and_character_limits_is_refused(string id)
    {
        var store = Open();
        Assert.Equal(EnqueueStatus.InvalidId, store.Enqueue(id, "one").Status);
        Assert.Equal(EnqueueStatus.InvalidId, store.Enqueue(new string('a', LaunchRequestStore.MaxIdLength + 1), "one").Status);
        Assert.Empty(store.All());
    }
}
