using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.Host.Lite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class ReplyClaimStoreTests : IDisposable
{
    const string Marker = "reply-claims.marker", Migration = "reply-claims.migration.json", Ledger = "reply-claims\\claims.json";
    readonly string profile = Path.Combine(Path.GetTempPath(), "ab-claims-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() { try { Directory.Delete(profile, recursive: true); } catch (DirectoryNotFoundException) { } }
    string PathInProfile(string name) => Path.Combine(profile, name);
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public void A_new_or_existing_profile_is_initialized_once()
    {
        Directory.CreateDirectory(profile);
        File.WriteAllText(PathInProfile("config.json"), "{}");
        ReplyClaimStore.EnsureInitialized(profile);
        var marker = File.GetLastWriteTimeUtc(PathInProfile(Marker));
        ReplyClaimStore.EnsureInitialized(profile);
        Assert.True(File.Exists(PathInProfile(Migration)));
        Assert.True(File.Exists(PathInProfile(Ledger)));
        Assert.Equal(marker, File.GetLastWriteTimeUtc(PathInProfile(Marker)));
    }

    [Fact]
    public void An_interrupted_genesis_migration_resumes_before_claims_are_opened()
    {
        ReplyClaimStore.EnsureInitialized(profile);
        SetState(Migration, "initializing");
        SetState(Marker, "initializing");
        File.Delete(PathInProfile(Ledger));
        ReplyClaimStore.EnsureInitialized(profile);
        Assert.Equal("complete", ReadState(Migration));
        Assert.Equal("active", ReadState(Marker));
        Assert.True(File.Exists(PathInProfile(Ledger)));
    }

    [Fact]
    public void An_interrupted_migration_revalidates_an_existing_active_ledger()
    {
        ReplyClaimStore.EnsureInitialized(profile);
        SetState(Migration, "initializing");
        ReplyClaimStore.EnsureInitialized(profile);
        Assert.Equal("complete", ReadState(Migration));
        Assert.Equal("active", ReadState(Marker));
    }

    [Theory]
    [InlineData(Marker)]
    [InlineData(Ledger)]
    public void A_completed_migration_with_a_missing_file_fails_closed(string missing)
    {
        ReplyClaimStore.EnsureInitialized(profile);
        File.Delete(PathInProfile(missing));
        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.EnsureInitialized(profile));
    }

    [Fact]
    public async Task Concurrent_instances_share_one_claim_and_restart_preserves_it()
    {
        ReplyClaimStore.EnsureInitialized(profile);
        using var first = ReplyClaimStore.Open(profile, time);
        using var second = ReplyClaimStore.Open(profile, time);
        var results = await Task.WhenAll(
            Task.Run(() => first.Acquire("session", "request", Hash("text"))),
            Task.Run(() => second.Acquire("session", "request", Hash("text"))));
        var acquired = Assert.Single(results, r => r.Status == ReplyClaimStatus.Acquired);
        var duplicate = Assert.Single(results, r => r.Status == ReplyClaimStatus.Existing);
        using var restarted = ReplyClaimStore.Open(profile, time);
        var afterRestart = restarted.Acquire("session", "request", Hash("text"));
        Assert.Equal(acquired.Generation, duplicate.Generation);
        Assert.Equal(acquired.Generation, afterRestart.Generation);
        Assert.Equal(ReplyClaimStatus.Existing, afterRestart.Status);
    }

    [Fact]
    public void Expiry_is_fixed_from_the_first_claim_and_a_hash_mismatch_is_refused()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var first = store.Acquire("session", "request", Hash("text"));
        time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(ReplyClaimStatus.Existing, store.Acquire("session", "request", Hash("text")).Status);
        Assert.Equal(ReplyClaimStatus.RequestReused, store.Acquire("session", "request", Hash("other")).Status);
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        var afterExpiry = store.Acquire("session", "request", Hash("text"));
        Assert.Equal(ReplyClaimStatus.Acquired, afterExpiry.Status);
        Assert.NotEqual(first.Generation, afterExpiry.Generation);
    }

    [Fact]
    public void Release_is_generation_fenced()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var first = store.Acquire("session", "request", Hash("text"));
        Assert.True(store.Release("session", "request", first.Generation));
        var second = store.Acquire("session", "request", Hash("text"));
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.False(store.Release("session", "request", first.Generation));
        Assert.Equal(second.Generation, store.Acquire("session", "request", Hash("text")).Generation);
    }

    void SetState(string file, string state)
    {
        var path = PathInProfile(file);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["state"] = state;
        File.WriteAllText(path, json.ToJsonString());
    }

    string ReadState(string file) => JsonNode.Parse(File.ReadAllText(PathInProfile(file)))!["state"]!.GetValue<string>();
}
