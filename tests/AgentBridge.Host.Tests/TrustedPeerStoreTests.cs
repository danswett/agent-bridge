using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class TrustedPeerStoreTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abtrust-" + Guid.NewGuid().ToString("N"));
    readonly P256KeyPair host = P256KeyPair.Generate();
    readonly P256KeyPair person = P256KeyPair.Generate();
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));
    string account = "fixture-account";

    TrustedPeerStore Load() => new(folder, host.Public, () => account, time);

    [Fact]
    public void Missing_state_denies_until_an_explicit_reset_and_full_key_approval()
    {
        var store = Load();
        Assert.NotNull(store.Problem);
        Assert.Null(store.Find(person.Public, account));
        Assert.Throws<InvalidOperationException>(() => store.Put(person.Public, "person", time.GetUtcNow().AddHours(1)));
        store.Reset();
        Assert.Null(store.Find(person.Public, account));
        store.Put(person.Public, "person", time.GetUtcNow().AddHours(1));
        Assert.Equal("person", store.Find(person.Public, account)!.Role);
        Assert.Null(store.Find(person.Public, "other-account"));
        account = "other-account";
        Assert.Null(store.Find(person.Public, "fixture-account"));
    }

    [Fact]
    public void Revocation_survives_reload_and_reapproval_does_not_restore_an_old_lease()
    {
        var store = Load();
        store.Reset();
        store.Put(person.Public, "person", time.GetUtcNow().AddHours(1));
        var lease = store.Find(person.Public, account)!;
        store.Revoke(person.Public);
        store = Load();
        Assert.Null(store.Find(person.Public, account));
        store.Put(person.Public, "service", time.GetUtcNow().AddHours(1));
        Assert.False(store.Current(lease, account));
        Assert.Equal(2, store.Find(person.Public, account)!.Generation);
        var replacement = store.Find(person.Public, account)!;
        store.Reset();
        store.Put(person.Public, "service", time.GetUtcNow().AddHours(1));
        Assert.False(store.Current(replacement, account));
    }

    [Fact]
    public void Observed_expiry_is_durable_and_a_rolled_back_clock_cannot_revive_it()
    {
        var store = Load();
        store.Reset();
        store.Put(person.Public, "service", time.GetUtcNow().AddMinutes(1));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(store.Find(person.Public, account));
        Assert.True(Assert.Single(store.Pins).Revoked);
        var restarted = new TrustedPeerStore(folder, host.Public, () => account,
            new FakeTimeProvider(time.GetUtcNow().AddMinutes(-2)));
        Assert.NotNull(restarted.Problem);
        Assert.Null(restarted.Find(person.Public, account));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("newer-schema")]
    [InlineData("wrong-host")]
    [InlineData("unknown-role")]
    [InlineData("duplicate-field")]
    public void Invalid_state_is_never_recovered_as_authority(string failure)
    {
        var store = Load();
        store.Reset();
        store.Put(person.Public, "person", time.GetUtcNow().AddHours(1));
        var path = Path.Combine(folder, TrustedPeerStore.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        switch (failure)
        {
            case "newer-schema": json["version"] = 2; break;
            case "wrong-host": json["host"] = "not-a-full-key"; break;
            case "unknown-role": json["pins"]![0]!["role"] = "admin"; break;
        }
        var text = failure switch
        {
            "truncated" => "{",
            "duplicate-field" => json.ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            _ => json.ToJsonString(),
        };
        File.WriteAllText(path, text);
        var restarted = Load();
        Assert.NotNull(restarted.Problem);
        Assert.Null(restarted.Find(person.Public, account));
        Assert.Throws<InvalidOperationException>(() => restarted.Put(person.Public, "person", time.GetUtcNow().AddHours(1)));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void Failed_durable_mutation_latches_deny_all_instead_of_reporting_success()
    {
        var store = Load();
        store.Reset();
        store.Put(person.Public, "person", time.GetUtcNow().AddHours(1));
        var path = Path.Combine(folder, TrustedPeerStore.FileName);
        File.Move(path, path + ".before");
        Directory.CreateDirectory(path);
        var failure = Record.Exception(() => store.Revoke(person.Public));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.NotNull(store.Problem);
        Assert.Null(store.Find(person.Public, account));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("reset")]
    [InlineData("downgrade")]
    [InlineData("shorten")]
    public void A_failed_authority_change_stays_blocked_after_the_file_lock_is_released_and_the_host_restarts(string change)
    {
        // Windows file sharing can refuse replacement while leaving the old grant intact.
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Load();
        store.Reset();
        store.Put(person.Public, "person", time.GetUtcNow().AddHours(1));
        var lease = store.Find(person.Public, account)!;
        var path = Path.Combine(folder, TrustedPeerStore.FileName);
        var original = File.ReadAllText(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() =>
            {
                switch (change)
                {
                    case "revoke": store.Revoke(person.Public); break;
                    case "reset": store.Reset(); break;
                    case "downgrade": store.Put(person.Public, "service", time.GetUtcNow().AddHours(1)); break;
                    case "shorten": store.Put(person.Public, "person", time.GetUtcNow().AddMinutes(1)); break;
                }
            });
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.NotNull(store.Problem);
            Assert.Null(store.Find(person.Public, account));
        }
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));

        var restarted = Load();
        Assert.NotNull(restarted.Problem);
        Assert.Null(restarted.Find(person.Public, account));
        Assert.Throws<InvalidOperationException>(() => restarted.Put(person.Public, "person", time.GetUtcNow().AddHours(1)));
        restarted.Reset();
        Assert.Null(Load().Problem);
        Assert.Null(Load().Find(person.Public, account));
        restarted.Put(person.Public, "person", time.GetUtcNow().AddHours(1));
        Assert.False(Load().Current(lease, account));
    }

    [Fact]
    public void An_interrupted_mutation_marker_denies_an_intact_store_until_an_explicit_reset()
    {
        var store = Load();
        store.Reset();
        store.Put(person.Public, "person", time.GetUtcNow().AddHours(1));
        var path = Path.Combine(folder, TrustedPeerStore.FileName);
        var original = File.ReadAllText(path);
        var marker = Path.Combine(folder, "trusted-peers.blocked");
        File.WriteAllBytes(marker, []);

        var restarted = Load();
        Assert.NotNull(restarted.Problem);
        Assert.Null(restarted.Find(person.Public, account));
        Assert.Equal(original, File.ReadAllText(path));
        restarted.Reset();
        Assert.False(File.Exists(marker));
        Assert.Null(Load().Problem);
        Assert.Null(Load().Find(person.Public, account));
    }

    [Fact]
    public void Failure_to_persist_the_block_marker_reports_that_revocation_is_not_durable()
    {
        var store = Load();
        store.Reset();
        store.Put(person.Public, "person", time.GetUtcNow().AddHours(1));
        var path = Path.Combine(folder, TrustedPeerStore.FileName);
        var original = File.ReadAllText(path);
        Directory.CreateDirectory(Path.Combine(folder, "trusted-peers.blocked"));

        var failure = Record.Exception(() => store.Revoke(person.Public));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Contains("revocation NOT durable; do not restart", failure!.Message);
        Assert.Null(store.Find(person.Public, account));
        Assert.NotNull(store.Problem);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Null(Load().Find(person.Public, account));
    }

    public void Dispose()
    {
        host.Dispose();
        person.Dispose();
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }
}
