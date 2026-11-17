using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Lite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class ReplyClaimStoreTests : IDisposable
{
    const string Marker = "reply-claims.marker", Migration = "reply-claims.migration.json", Ledger = "ledger";
    readonly string profile = Path.Combine(Path.GetTempPath(), "ab-claims-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() { try { Directory.Delete(profile, recursive: true); } catch (DirectoryNotFoundException) { } }
    string PathInProfile(string name) => name == Ledger ? Path.Combine(profile, "reply-claims", "claims.json") : Path.Combine(profile, name);
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

    [Theory]
    [InlineData("firstClaimUtc", null)]
    [InlineData("firstClaimUtc", "0001-01-01T00:00:00+00:00")]
    [InlineData("firstClaimUtc", "invalid")]
    [InlineData("requestHash", null)]
    [InlineData("textHash", null)]
    public void Missing_or_invalid_claim_fields_hold_acquisition(string field, string? value)
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "request", Hash("text"));
        var path = PathInProfile(Ledger);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var claim = json["claims"]![0]!.AsObject();
        if (value is null) { claim.Remove(field); } else { claim[field] = value; }
        File.WriteAllText(path, json.ToJsonString());

        Assert.Throws<InvalidDataException>(() => store.Acquire("session", "request", Hash("text")));
        var messages = new List<string>();
        var ingress = ReplyIngress.Initialize(profile, messages.Add);
        Assert.Null(ingress.HomeAssistant((_, _) => throw new InvalidOperationException()));
        Assert.NotEmpty(messages);
    }

    [Theory]
    [InlineData(Marker)]
    [InlineData(Migration)]
    public void Release_holds_when_profile_state_is_missing(string missing)
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var claim = store.Acquire("session", "request", Hash("text"));
        File.Delete(PathInProfile(missing));
        Assert.Throws<InvalidDataException>(() => store.Release("session", "request", claim.Generation));
        Assert.Throws<InvalidDataException>(() => store.Acquire("session", "request", Hash("text")));
        Assert.Contains(claim.Generation.ToString(), File.ReadAllText(PathInProfile(Ledger)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hash_case_is_normalized_and_reused_requests_cannot_release_a_claim()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var first = store.Acquire("session", "request", Hash("text").ToLowerInvariant(), Hash("attachment").ToLowerInvariant());
        var path = PathInProfile(Ledger);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var key in new[] { "requestHash", "textHash", "attachmentHash" })
        { json["claims"]![0]![key] = json["claims"]![0]![key]!.GetValue<string>().ToLowerInvariant(); }
        File.WriteAllText(path, json.ToJsonString());
        var duplicate = store.Acquire("session", "request", Hash("text"), Hash("attachment"));
        var reused = store.Acquire("session", "request", Hash("text"), Hash("other"));
        Assert.Equal(ReplyClaimStatus.Existing, duplicate.Status);
        Assert.Equal(first.Generation, duplicate.Generation);
        Assert.Equal(ReplyClaimStatus.RequestReused, reused.Status);
        Assert.Equal(Guid.Empty, reused.Generation);
        Assert.False(store.Release("session", "request", reused.Generation));
    }

    [Fact]
    public void A_full_session_refuses_without_evicting_a_live_claim()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var first = store.Acquire("session", "request", Hash("text"));
        var path = PathInProfile(Ledger);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var claims = json["claims"]!.AsArray();
        for (var i = 1; i < 4096; i++)
        {
            var claim = claims[0]!.DeepClone();
            claim["requestHash"] = Hash($"request-{i}");
            claim["generation"] = Guid.NewGuid().ToString();
            claims.Add(claim);
        }
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(ReplyClaimStatus.Full, store.Acquire("session", "overflow", Hash("text")).Status);
        Assert.Equal(first.Generation, store.Acquire("session", "request", Hash("text")).Generation);
    }

    [Fact]
    public void Corrupt_ledger_holds_without_overwriting_the_evidence()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        File.WriteAllText(PathInProfile(Ledger), "{broken");
        Assert.Throws<InvalidDataException>(() => store.Acquire("session", "request", Hash("text")));
        Assert.Equal("{broken", File.ReadAllText(PathInProfile(Ledger)));
    }

    [Fact]
    public void A_directory_flush_failure_never_acknowledges_the_new_claim()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        try
        {
            ClaimDirectorySync.BeforeFlush.Value = path =>
            {
                if (path == Path.GetDirectoryName(PathInProfile(Ledger))) { throw new IOException("injected directory flush failure"); }
            };
            Assert.Throws<IOException>(() => store.Acquire("session", "request", Hash("text")));
        }
        finally { ClaimDirectorySync.BeforeFlush.Value = null; }
        using var restarted = ReplyClaimStore.Open(profile, time);
        Assert.Equal(ReplyClaimStatus.Existing, restarted.Acquire("session", "request", Hash("text")).Status);
    }

    [Fact]
    public async Task Another_process_waits_for_the_profile_lock()
    {
        ReplyClaimStore.EnsureInitialized(profile);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(AgentBridge.Host.Tests.Program).Assembly.Location, "reply-claim-child", profile, Hash("text") })
        { start.ArgumentList.Add(arg); }
        using var held = new FileStream(PathInProfile("reply-claims.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var child = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(child.WaitForExit(300));
            held.Dispose();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.ExitCode == 0, await child.StandardError.ReadToEndAsync());
            using var store = ReplyClaimStore.Open(profile, time);
            Assert.Equal(ReplyClaimStatus.Existing, store.Acquire("session", "request", Hash("text")).Status);
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task Initialization_precedes_both_reply_ingress_paths_and_failure_holds_only_them()
    {
        var observed = new List<string>();
        var gate = ReplyIngress.Initialize(profile, observed.Add);
        Assert.True(File.Exists(PathInProfile(Marker)));
        Assert.True(File.Exists(PathInProfile(Ledger)));
        var lite = new TestDelivery();
        Assert.True((await gate.Lite(lite).DeliverAsync(null!, "text", default)).Typed);
        Assert.NotNull(gate.HomeAssistant((_, _) => throw new InvalidOperationException()));
        File.Delete(PathInProfile(Marker));
        var held = ReplyIngress.Initialize(profile, observed.Add);
        Assert.NotEmpty(observed);
        var refused = await held.Lite(lite).DeliverAsync(null!, "text", default);
        Assert.False(refused.Typed);
        Assert.Equal("claimUnavailable", refused.Code);
        Assert.Equal(1, lite.Calls);
        Assert.Null(held.HomeAssistant((_, _) => throw new InvalidOperationException()));
    }

    sealed class TestDelivery : ILiteDeliver
    {
        public int Calls;
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct)
        { Calls++; return Task.FromResult(new LiteDelivery(true, null)); }
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
