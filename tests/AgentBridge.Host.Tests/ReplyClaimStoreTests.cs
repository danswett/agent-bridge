using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
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
        RewriteFirstFrame(PathInProfile(Ledger), (row, _) =>
        {
            var claim = row["claim"]!.AsObject();
            if (value is null) { claim.Remove(field); } else { claim[field] = value; }
        });

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
        RewriteFirstFrame(path, (row, _) =>
        {
            foreach (var key in new[] { "requestHash", "textHash", "attachmentHash" })
            { row["claim"]![key] = row["claim"]![key]!.GetValue<string>().ToLowerInvariant(); }
        });
        var duplicate = store.Acquire("session", "request", Hash("text"), Hash("attachment"));
        var reused = store.Acquire("session", "request", Hash("text"), Hash("other"));
        Assert.Equal(ReplyClaimStatus.Existing, duplicate.Status);
        Assert.Equal(first.Generation, duplicate.Generation);
        Assert.Equal(ReplyClaimStatus.RequestReused, reused.Status);
        Assert.Equal(Guid.Empty, reused.Generation);
        Assert.False(store.Release("session", "request", reused.Generation));
    }

    [Fact]
    public void The_claim_cap_refuses_without_evicting_a_live_claim_and_compacts_at_the_cap()
    {
        ReplyClaimStore.EnsureInitialized(profile);
        var markerPath = PathInProfile(Marker);
        var markerJson = JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject();
        var profileKey = markerJson["profileKey"]!.GetValue<string>();
        var claims = Enumerable.Range(0, 2048).Select(i => new ReplyClaimRecord("session", Hash($"request-{i}"), Hash("text"), null, Guid.NewGuid(), time.GetUtcNow())).ToList();
        var coverageHash = Hash("coverage"); var marker = new ReplyClaimMarker(1, profileKey, "active", 1, coverageHash, ReplyClaimJournal.StateHash(claims));
        ReplyClaimJournal.Compact(PathInProfile(Ledger), profileKey, new(claims, 1, coverageHash), marker);
        markerJson["sequence"] = marker.Sequence; markerJson["hash"] = marker.Hash; markerJson["stateHash"] = marker.StateHash;
        File.WriteAllText(markerPath, markerJson.ToJsonString());

        using var store = ReplyClaimStore.Open(profile, time);
        Assert.Equal(ReplyClaimStatus.Full, store.Acquire("other", "overflow", Hash("text")).Status);
        Assert.Equal(ReplyClaimStatus.Existing, store.Acquire("session", "request-0", Hash("text")).Status);
        store.Compact();
        using var restarted = ReplyClaimStore.Open(profile, time);
        Assert.Equal(ReplyClaimStatus.Existing, restarted.Acquire("session", "request-0", Hash("text")).Status);
        Assert.Equal(claims[0].Generation, restarted.Acquire("session", "request-0", Hash("text")).Generation);
    }

    [Fact]
    public void Corrupt_ledger_holds_without_overwriting_the_evidence()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        File.WriteAllText(PathInProfile(Ledger), "{broken");
        Assert.Throws<InvalidDataException>(() => store.Acquire("session", "request", Hash("text")));
        Assert.Equal("{broken", File.ReadAllText(PathInProfile(Ledger)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_partial_final_frame_is_truncated_under_the_lock(bool partialLength)
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "request", Hash("text"));
        var path = PathInProfile(Ledger);
        var validLength = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            var partial = partialLength ? new byte[] { 0x20, 0x00 } : new byte[] { 0x05, 0x00, 0x00, 0x00, 0x7b, 0x7d };
            stream.Write(partial);
            stream.Flush(true);
        }

        using var recovered = ReplyClaimStore.Open(profile, time);
        Assert.Equal(ReplyClaimStatus.Existing, recovered.Acquire("session", "request", Hash("text")).Status);
        Assert.Equal(validLength, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_bad_complete_frame_checksum_holds_even_at_eof(bool corruptFirst)
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "first", Hash("text"));
        store.Acquire("session", "second", Hash("text"));
        var bytes = File.ReadAllBytes(PathInProfile(Ledger));
        var frameStart = 0;
        if (!corruptFirst)
        {
            frameStart = 4 + BinaryPrimitives.ReadInt32LittleEndian(bytes) + 32;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(frameStart));
        bytes[frameStart + 4 + length] ^= 0x01;
        File.WriteAllBytes(PathInProfile(Ledger), bytes);

        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.Open(profile, time));
    }

    [Fact]
    public void A_corrupt_length_before_later_claims_holds_without_truncating_evidence()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "first", Hash("text"));
        store.Acquire("session", "later", Hash("text"));
        var path = PathInProfile(Ledger);
        var damaged = File.ReadAllBytes(path).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(damaged, damaged.Length);
        File.WriteAllBytes(path, damaged);

        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.Open(profile, time));
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_snapshot_cannot_claim_coverage_without_the_covered_live_claims()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "request", Hash("text"));
        var marker = JsonNode.Parse(File.ReadAllText(PathInProfile(Marker)))!.AsObject();
        var backup = Path.Combine(profile, "empty-covering-snapshot");
        WriteSnapshot(backup, marker, new JsonArray());
        File.Delete(PathInProfile(Ledger));

        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.RecoverFromBackup(profile, backup));
        Assert.False(File.Exists(PathInProfile(Ledger)));
    }

    [Fact]
    public void A_backup_with_the_wrong_hash_is_refused()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "request", Hash("text"));
        store.Compact();
        var backup = Path.Combine(profile, "wrong-hash"); File.Copy(PathInProfile(Ledger), backup);
        RewriteFirstFrame(backup, (row, _) =>
        {
            var wrong = Hash("wrong coverage");
            row["previousHash"] = wrong;
            row["coverageHash"] = wrong;
        }, updateMarker: false);

        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.RecoverFromBackup(profile, backup));
    }

    [Fact]
    public void A_backup_starting_mid_chain_without_coverage_is_refused()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "first", Hash("text"));
        store.Acquire("session", "second", Hash("text"));
        var journal = File.ReadAllBytes(PathInProfile(Ledger)); var firstLength = BinaryPrimitives.ReadInt32LittleEndian(journal);
        var backup = Path.Combine(profile, "mid-chain");
        File.WriteAllBytes(backup, journal.AsSpan(4 + firstLength + 32).ToArray());

        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.RecoverFromBackup(profile, backup));
    }

    [Fact]
    public void A_journal_behind_the_marker_fails_without_modifying_evidence()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "request", Hash("text"));
        var markerPath = PathInProfile(Marker); var marker = JsonNode.Parse(File.ReadAllText(markerPath))!;
        marker["sequence"] = marker["sequence"]!.GetValue<long>() + 1;
        File.WriteAllText(markerPath, marker.ToJsonString());
        var journal = File.ReadAllBytes(PathInProfile(Ledger));

        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.Open(profile, time));
        Assert.Equal(journal, File.ReadAllBytes(PathInProfile(Ledger)));
    }

    [Fact]
    public void A_journal_ahead_of_the_marker_is_verified_then_advanced()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "first", Hash("text"));
        store.Acquire("session", "later", Hash("text"));
        var journal = File.ReadAllBytes(PathInProfile(Ledger));
        var firstLength = BinaryPrimitives.ReadInt32LittleEndian(journal); var firstHash = Convert.ToHexString(SHA256.HashData(journal.AsSpan(4, firstLength)));
        var markerPath = PathInProfile(Marker);
        var marker = JsonNode.Parse(File.ReadAllText(markerPath))!;
        marker["sequence"] = 1;
        marker["hash"] = firstHash;
        marker["stateHash"] = JsonNode.Parse(journal.AsSpan(4, firstLength))!["stateHash"]!.GetValue<string>();
        File.WriteAllText(markerPath, marker.ToJsonString());

        using var recovered = ReplyClaimStore.Open(profile, time);
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(markerPath))!["sequence"]!.GetValue<long>());
        Assert.Equal(ReplyClaimStatus.Existing, recovered.Acquire("session", "later", Hash("text")).Status);
    }

    [Fact]
    public void Compaction_uses_the_refreshed_marker_when_replay_is_ahead()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "first", Hash("text"));
        var oldMarker = File.ReadAllBytes(PathInProfile(Marker));
        var later = store.Acquire("session", "later", Hash("text"));
        File.WriteAllBytes(PathInProfile(Marker), oldMarker);

        store.Compact();
        using var restarted = ReplyClaimStore.Open(profile, time);
        Assert.Equal(later.Generation, restarted.Acquire("session", "later", Hash("text")).Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void State_hash_validation_holds_at_the_marker_and_at_the_journal_end(bool ahead)
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var genesis = File.ReadAllBytes(PathInProfile(Marker));
        store.Acquire("session", "request", Hash("text"));
        RewriteFirstFrame(PathInProfile(Ledger), (row, _) => row["stateHash"] = Hash("wrong state"));
        if (ahead) { File.WriteAllBytes(PathInProfile(Marker), genesis); }

        Assert.Throws<InvalidDataException>(() => store.Acquire("session", "request", Hash("text")));
    }

    [Fact]
    public void Replay_at_the_claim_cap_finishes_within_the_two_second_lock_wait()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var marker = JsonNode.Parse(File.ReadAllText(PathInProfile(Marker)))!.AsObject();
        var state = new ReplyClaimSnapshot([], 0, marker["hash"]!.GetValue<string>());
        for (var i = 0; i < 2048; i++)
        {
            var claim = new ReplyClaimRecord("session", Hash($"request-{i}"), Hash("text"), null, Guid.NewGuid(), time.GetUtcNow());
            state.Claims.Add(claim);
            state = ReplyClaimJournal.Append(PathInProfile(Ledger), marker["profileKey"]!.GetValue<string>(), state, "claimed", claim);
        }
        marker["sequence"] = state.Sequence; marker["hash"] = state.Hash;
        marker["stateHash"] = ReplyClaimJournal.StateHash(state.Claims);
        File.WriteAllText(PathInProfile(Marker), marker.ToJsonString());

        var watch = Stopwatch.StartNew();
        var duplicate = store.Acquire("session", "request-2047", Hash("text"));
        watch.Stop();
        Assert.Equal(ReplyClaimStatus.Existing, duplicate.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Replay held the lock for {watch.Elapsed}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Crossing_the_compaction_threshold_never_loses_a_successful_mutation(bool release)
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var first = store.Acquire("session", "first", Hash("text"));
        var path = PathInProfile(Ledger);
        var original = File.ReadAllBytes(path);
        var length = BinaryPrimitives.ReadInt32LittleEndian(original);
        var payload = Enumerable.Repeat((byte)' ', 1024 * 1024 - 1 - 36).ToArray();
        original.AsSpan(4, length).CopyTo(payload);
        var padded = new byte[payload.Length + 36];
        BinaryPrimitives.WriteInt32LittleEndian(padded, payload.Length);
        payload.CopyTo(padded.AsSpan(4));
        SHA256.HashData(payload).CopyTo(padded.AsSpan(4 + payload.Length));
        File.WriteAllBytes(path, padded);
        var marker = JsonNode.Parse(File.ReadAllText(PathInProfile(Marker)))!;
        marker["hash"] = Convert.ToHexString(SHA256.HashData(payload));
        File.WriteAllText(PathInProfile(Marker), marker.ToJsonString());
        try
        {
            ReplyClaimJournal.BeforeCompactReplace.Value = () => throw new IOException("injected maintenance failure");
            if (release) { Assert.True(store.Release("session", "first", first.Generation)); }
            else { Assert.Equal(ReplyClaimStatus.Acquired, store.Acquire("session", "later", Hash("text")).Status); }
            Assert.Throws<IOException>(() => store.Compact());
            Assert.Throws<IOException>(() => store.Acquire("session", "blocked", Hash("text")));
        }
        finally { ReplyClaimJournal.BeforeCompactReplace.Value = null; }

        using var restarted = ReplyClaimStore.Open(profile, time);
        Assert.Equal(release ? ReplyClaimStatus.Acquired : ReplyClaimStatus.Existing,
            restarted.Acquire("session", release ? "first" : "later", Hash("text")).Status);
        Assert.Equal(ReplyClaimStatus.Acquired, restarted.Acquire("session", "blocked", Hash("text")).Status);
    }

    [Fact]
    public void A_leftover_compaction_temp_is_preserved_and_ignored()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "request", Hash("text"));
        var journal = File.ReadAllBytes(PathInProfile(Ledger)); var temp = PathInProfile(Ledger) + ".crash.tmp";
        File.WriteAllText(temp, "incomplete compaction");

        using var restarted = ReplyClaimStore.Open(profile, time);
        Assert.Equal(journal, File.ReadAllBytes(PathInProfile(Ledger)));
        Assert.Equal("incomplete compaction", File.ReadAllText(temp));
        Assert.Equal(ReplyClaimStatus.Existing, restarted.Acquire("session", "request", Hash("text")).Status);
    }

    [Fact]
    public void A_claim_frame_cannot_contain_snapshot_data()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "request", Hash("text"));
        RewriteFirstFrame(PathInProfile(Ledger), (row, _) => row["claims"] = new JsonArray());

        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.Open(profile, time));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compaction_crashes_leave_a_replayable_ledger(bool afterReplace)
    {
        using var store = ReplyClaimStore.Open(profile, time);
        store.Acquire("session", "request", Hash("text"));
        var hook = afterReplace ? ReplyClaimJournal.AfterCompactReplace : ReplyClaimJournal.BeforeCompactReplace;
        try
        {
            hook.Value = () => throw new IOException("injected compaction crash");
            Assert.Throws<IOException>(() => store.Compact());
        }
        finally { hook.Value = null; }

        using var restarted = ReplyClaimStore.Open(profile, time);
        Assert.Equal(ReplyClaimStatus.Existing, restarted.Acquire("session", "request", Hash("text")).Status);
    }

    [Fact]
    public void A_stale_compaction_backup_is_refused_and_a_covering_copy_can_recover()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        var claim = store.Acquire("session", "request", Hash("text"));
        store.Compact();
        var path = PathInProfile(Ledger);
        var marker = JsonNode.Parse(File.ReadAllText(PathInProfile(Marker)))!;
        Assert.Equal(2, marker["sequence"]!.GetValue<long>());
        Assert.Equal(64, marker["hash"]!.GetValue<string>().Length);
        var current = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.RecoverFromBackup(profile, path + ".bak"));
        Assert.Equal(current, File.ReadAllBytes(path));

        var covering = Path.Combine(profile, "operator-copy");
        File.Copy(path, covering);
        File.Delete(path);
        Assert.Throws<InvalidDataException>(() => ReplyClaimStore.Open(profile, time));
        ReplyClaimStore.RecoverFromBackup(profile, covering);
        using var recovered = ReplyClaimStore.Open(profile, time);
        Assert.Equal(claim.Generation, recovered.Acquire("session", "request", Hash("text")).Generation);
    }

    [Fact]
    public void A_directory_flush_failure_never_acknowledges_the_new_claim()
    {
        using var store = ReplyClaimStore.Open(profile, time);
        try
        {
            ClaimDirectorySync.BeforeFlush.Value = path =>
            {
                if (path == profile) { throw new IOException("injected directory flush failure"); }
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

    void RewriteFirstFrame(string path, Action<JsonObject, string> change, bool updateMarker = true)
    {
        var old = File.ReadAllBytes(path);
        var length = BinaryPrimitives.ReadInt32LittleEndian(old);
        var payload = old.AsSpan(4, length).ToArray();
        var rowHash = Convert.ToHexString(SHA256.HashData(payload));
        var row = JsonNode.Parse(payload)!.AsObject();
        change(row, rowHash);
        payload = Encoding.UTF8.GetBytes(row.ToJsonString());
        var tail = old.AsSpan(4 + length + 32).ToArray();
        var bytes = new byte[4 + payload.Length + 32 + tail.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        SHA256.HashData(payload).CopyTo(bytes.AsSpan(4 + payload.Length));
        tail.CopyTo(bytes.AsSpan(4 + payload.Length + 32));
        File.WriteAllBytes(path, bytes);
        if (updateMarker)
        {
            var marker = JsonNode.Parse(File.ReadAllText(PathInProfile(Marker)))!.AsObject();
            marker["hash"] = Convert.ToHexString(SHA256.HashData(payload));
            File.WriteAllText(PathInProfile(Marker), marker.ToJsonString());
        }
    }

    static void WriteSnapshot(string path, JsonObject marker, JsonArray claims)
    {
        var sequence = marker["sequence"]!.GetValue<long>();
        var hash = marker["hash"]!.GetValue<string>();
        var row = new JsonObject
        {
            ["version"] = 1,
            ["profileKey"] = marker["profileKey"]!.GetValue<string>(),
            ["sequence"] = sequence + 1,
            ["previousHash"] = hash,
            ["action"] = "snapshot",
            ["claims"] = claims,
            ["coverageSequence"] = sequence,
            ["coverageHash"] = hash,
            ["coverageStateHash"] = marker["stateHash"]?.GetValue<string>() ?? Hash("[]"),
            ["stateHash"] = Hash("[]")
        };
        var payload = Encoding.UTF8.GetBytes(row.ToJsonString());
        var bytes = new byte[4 + payload.Length + 32];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        SHA256.HashData(payload).CopyTo(bytes.AsSpan(4 + payload.Length));
        File.WriteAllBytes(path, bytes);
    }
}
