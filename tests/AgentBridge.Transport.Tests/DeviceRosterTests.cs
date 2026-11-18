using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Relay;
using AgentBridge.Roster;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

// The relay's copy of an account's signed device roster (docs/protocol/devices.md): it keeps only
// entries that are valid against its own copy, and never decides who may connect.
public class DeviceRosterTests
{
    static DeviceRoster Make(World w, IRosterStore? store = null, TimeProvider? clock = null)
    {
        store ??= new MemoryRosterStore();
        var time = clock ?? new FakeTimeProvider(DateTimeOffset.UnixEpoch + w.Now);
        var options = new RelayOptions { TokenKey = Convert.ToBase64String(new byte[32]), Roster = { Time = time } };
        using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new RelayMetrics(meters.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), () => 0);
        return new DeviceRoster(options, metrics, NullLogger<DeviceRoster>.Instance, store);
    }

    static string Wire(World w, string label) => w.Entry(label).Canonical();

    static World Scenario(string account = RosterFixture.Account)
    {
        var w = new World { AccountId = account };
        w.Genesis();
        w.Add("addB", "G", "B", "G");
        return w;
    }

    [Fact]
    public async Task A_genesis_then_an_add_signed_by_a_rostered_device_are_kept_and_served()
    {
        var w = Scenario();
        var roster = Make(w);
        var r = await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G"), Wire(w, "addB")]);
        Assert.Equal(["accepted", "accepted"], r!.Select(x => x.Verdict));
        var s = await roster.GetAsync(RosterFixture.Account);
        Assert.Equal("observe", s!.Mode);
        Assert.Equal(2, s.Entries.Count);
        Assert.Equal(RosterFixture.Generation, s.Generation);
    }

    [Fact]
    public async Task An_entry_that_is_not_a_genesis_cannot_start_a_log()
    {
        var w = Scenario();
        var roster = Make(w);
        var r = await roster.AppendAsync(RosterFixture.Account, [Wire(w, "addB")]);
        Assert.Equal("refused", r![0].Verdict);
        Assert.Empty((await roster.GetAsync(RosterFixture.Account))!.Entries);
    }

    [Fact]
    public async Task A_genesis_for_another_account_cannot_start_this_ones_log()
    {
        var w = Scenario();
        var roster = Make(w);
        var r = await roster.AppendAsync("someone-else", [Wire(w, "G")]);
        Assert.Equal("refused", r![0].Verdict);
        Assert.Null((await roster.GetAsync("someone-else"))!.Generation);
    }

    [Fact]
    public async Task A_tampered_entry_is_refused_and_not_kept()
    {
        var w = Scenario();
        var roster = Make(w);
        await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")]);
        var good = Wire(w, "addB");
        var at = good.IndexOf("\"sig\":\"", StringComparison.Ordinal) + 7;
        var bad = good[..at] + (good[at] == 'A' ? 'B' : 'A') + good[(at + 1)..];
        Assert.NotEqual(good, bad);
        var r = await roster.AppendAsync(RosterFixture.Account, [bad]);
        Assert.Equal("refused", r![0].Verdict);
        Assert.Single((await roster.GetAsync(RosterFixture.Account))!.Entries);
    }

    [Fact]
    public async Task Garbage_is_refused_without_touching_the_log_and_a_repeat_is_a_duplicate()
    {
        var w = Scenario();
        var roster = Make(w);
        var r = await roster.AppendAsync(RosterFixture.Account, ["not an entry"]);
        Assert.Equal("refused", r![0].Verdict);
        await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")]);
        r = await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")]);
        Assert.Equal("duplicate", r![0].Verdict);
        Assert.Single((await roster.GetAsync(RosterFixture.Account))!.Entries);
    }

    [Fact]
    public async Task A_device_not_on_the_roster_cannot_add_one()
    {
        var w = Scenario();
        w.Add("evil", "Z", "Y", "G");
        var roster = Make(w);
        await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")]);
        var r = await roster.AppendAsync(RosterFixture.Account, [Wire(w, "evil")]);
        Assert.Equal("refused", r![0].Verdict);
        Assert.Single((await roster.GetAsync(RosterFixture.Account))!.Entries);
    }

    [Fact]
    public async Task A_removal_is_kept_with_the_over_budget_entries_it_brought_as_evidence()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        for (var i = 0; i < RosterVerifier.SignerBudget + 1; i++) { w.Add($"+N{i}", "H", $"N{i}", "+H"); }
        w.Remove("R", "G", "+H", $"+N{RosterVerifier.SignerBudget}");
        var store = new MemoryRosterStore();
        var roster = Make(w, store);
        var wire = w.Labels.Select(l => Wire(w, l)).ToList();
        for (var i = 0; i < wire.Count; i += DeviceRoster.MaxPerAppend)
        {
            await roster.AppendAsync(RosterFixture.Account, [.. wire.Skip(i).Take(DeviceRoster.MaxPerAppend)]);
        }
        var again = Make(w, store);
        var s = await again.GetAsync(RosterFixture.Account);
        Assert.Equal(wire.Count, s!.Entries.Count);
        // Rebuilt from the store, the removal and its evidence are all still there.
        var r = await again.AppendAsync(RosterFixture.Account, [wire[^1], wire[^2]]);
        Assert.Equal(["duplicate", "duplicate"], r!.Select(x => x.Verdict));
    }

    static Entry Padded(Signed signed, int bytes, Entry? parent = null)
    {
        var body = JsonNode.Parse(signed.Body)!;
        if (parent is not null)
        {
            body["prev"] = RosterFixture.Hashes([parent.Hash]);
            body["seq"] = parent.Body.Seq + 1;
        }
        var raw = RosterFixture.Bytes(body);
        // Whitespace is signed body content; leave room for the largest DER signature.
        var padded = signed with { Body = [.. raw, .. Encoding.UTF8.GetBytes(new string(' ', bytes - 72 - raw.Length))] };
        return Entry.Parse(Encoding.UTF8.GetBytes(padded.Wire));
    }

    static async Task<(World W, MemoryRosterStore Store, DeviceRoster Roster)> FullOrdinaryLog(string account = RosterFixture.Account)
    {
        var w = Scenario(account);
        var store = new MemoryRosterStore();
        var roster = Make(w, store);
        await roster.AppendAsync(account, [Wire(w, "G"), Wire(w, "addB")]);
        var ordinary = new List<string>();
        var parent = w.Entry("addB");
        for (var i = 0; i < 22; i++)
        {
            var label = $"fill{i}";
            w.Add(label, "G", $"D{i}", "addB");
            parent = Padded(w[label], 8 * 1024, parent);
            ordinary.Add(parent.Canonical());
        }
        var filled = new List<DeviceRoster.Result>();
        foreach (var batch in ordinary.Chunk(DeviceRoster.MaxPerAppend))
        {
            filled.AddRange((await roster.AppendAsync(account, batch))!);
        }
        Assert.Contains(filled, r => r is { Verdict: "refused", Reason: Invalid.Cap });
        return (w, store, roster);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    public async Task Removal_is_accepted_when_ordinary_entries_are_refused_with_cap(bool mixed, int ancestorCount)
    {
        var (w, store, roster) = await FullOrdinaryLog();
        var before = (await roster.GetAsync(RosterFixture.Account))!;
        var parent = Entry.Parse(Encoding.UTF8.GetBytes(before.Entries[^1]));
        var ancestors = new List<string>();
        for (var i = 0; i < Math.Max(1, ancestorCount); i++)
        {
            var label = $"E{i}";
            w.Rename(label, "B", "addB", "addB", "renamed");
            var ancestor = Padded(w[label], Entry.MaxBytes, parent);
            ancestors.Add(ancestor.Canonical());
            if (ancestorCount > 0) { parent = ancestor; }
        }
        w.Remove("R", "G", "addB", "addB");
        var removal = Padded(w["R"], Entry.MaxRemoveBytes, parent).Canonical();
        if (!mixed)
        {
            var refused = await roster.AppendAsync(RosterFixture.Account, ancestors);
            Assert.Equal(("refused", Invalid.Cap), (refused![0].Verdict, refused[0].Reason));
        }
        var result = await roster.AppendAsync(RosterFixture.Account, mixed ? [.. ancestors, removal] : [removal]);
        if (ancestorCount > 4)
        {
            Assert.Equal(("refused", "removal-ancestry-cap"), (result![^1].Verdict, result[^1].Reason));
            Assert.Equal(before.Entries, (await roster.GetAsync(RosterFixture.Account))!.Entries);
            return;
        }
        Assert.Equal(ancestorCount > 0 ? "quarantined" : "accepted", result![^1].Verdict);
        if (mixed)
        {
            Assert.All(result.Take(ancestors.Count), r =>
                Assert.Equal(ancestorCount > 0 ? ("accepted", Invalid.Evidence) : ("refused", Invalid.Cap), (r.Verdict, r.Reason)));
        }
        var snapshot = (await Make(w, store).GetAsync(RosterFixture.Account))!;
        Assert.Equal(removal, snapshot.Entries[^1]);
        Assert.All(ancestors, a => Assert.Equal(ancestorCount > 0, snapshot.Entries.Any(s =>
            Entry.Parse(Encoding.UTF8.GetBytes(s)).Hash.SequenceEqual(Entry.Parse(Encoding.UTF8.GetBytes(a)).Hash))));
        var replay = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
        replay.Offer(snapshot.Entries.Select(s => Entry.Parse(Encoding.UTF8.GetBytes(s))));
        Assert.True(replay.Holds(Entry.Parse(Encoding.UTF8.GetBytes(removal)).Hash));
        Assert.False(replay.State.Active.ContainsKey(w.Id("B")));
        w.Remove("full", "G", "G", "G");
        var over = await roster.AppendAsync(RosterFixture.Account, [Padded(w["full"], 46 * 1024).Canonical()]);
        Assert.Equal(("refused", Invalid.Cap), (over![0].Verdict, over[0].Reason));
        Assert.Equal(snapshot.Entries, (await roster.GetAsync(RosterFixture.Account))!.Entries);
        Assert.True((await store.ReadAsync(store.Names.Single(), default))!.Body.Length <= DeviceRoster.MaxStoredBytes);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Maximum_removal_and_four_ancestors_reach_the_roster_over_websocket()
    {
        var (w, store, roster) = await FullOrdinaryLog("1");
        var before = (await roster.GetAsync("1"))!;
        var parent = Entry.Parse(Encoding.UTF8.GetBytes(before.Entries[^1]));
        var entries = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            w.Rename($"E{i}", "B", "addB", "addB", "renamed");
            parent = Padded(w[$"E{i}"], Entry.MaxBytes, parent);
            entries.Add(parent.Canonical());
        }
        w.Remove("R", "G", "addB", "addB");
        entries.Add(Padded(w["R"], Entry.MaxRemoveBytes, parent).Canonical());
        await using var relay = await TestRelay.StartAsync(o =>
        {
            o.TokenKey = Convert.ToBase64String(new byte[32]);
            o.Roster.DevicesStore = store;
            o.Roster.Time = new FakeTimeProvider(DateTimeOffset.UnixEpoch + w.Now);
        });
        var client = await relay.ConnectAsync(relay.TokenFor(1, "fixture"), "roster");
        var reply = new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Relay.Unhandled += m => { if (m.GetProperty("t").GetString() == "roster-append-result") { reply.TrySetResult(m.Clone()); } };
        var frame = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { t = "roster-append", id = 1, entries });
        Assert.True(frame.Length > AgentBridge.Transport.Wire.MaxFrame);
        await client.Relay.SendRawAsync(frame, System.Net.WebSockets.WebSocketMessageType.Text);
        Assert.Same(reply.Task, await Task.WhenAny(reply.Task, client.Relay.Completion).WaitAsync(TimeSpan.FromSeconds(20)));
        var results = (await reply.Task).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal("quarantined", results[^1].GetProperty("verdict").GetString());
        Assert.All(results[..^1], r => Assert.Equal("evidence", r.GetProperty("reason").GetString()));
        Assert.Equal(before.Entries.Count + 5, (await roster.GetAsync("1"))!.Entries.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ineligible_removals_cannot_promote_size_refused_ancestors_or_spend_the_reserve(bool self)
    {
        var (w, store, roster) = await FullOrdinaryLog();
        var before = (await roster.GetAsync(RosterFixture.Account))!;
        var parent = Entry.Parse(Encoding.UTF8.GetBytes(before.Entries[^1]));
        w.Rename("ineligible", "G", "addB", "addB", "renamed");
        var ancestor = Padded(w["ineligible"], Entry.MaxBytes, parent);
        w.Remove("R", self ? "B" : "G", "addB", "addB");
        var removal = Padded(w["R"], 32 * 1024, ancestor);
        var result = await roster.AppendAsync(RosterFixture.Account, [ancestor.Canonical(), removal.Canonical()]);
        Assert.Equal([("refused", Invalid.Cap), ("refused", Invalid.Evidence)], result!.Select(r => (r.Verdict, r.Reason)));
        Assert.Equal(before.Entries, (await roster.GetAsync(RosterFixture.Account))!.Entries);
        w.Remove("genuine", "G", "addB", "addB");
        var genuine = await roster.AppendAsync(RosterFixture.Account, [Padded(w["genuine"], Entry.MaxRemoveBytes, parent).Canonical()]);
        Assert.Equal("accepted", genuine![0].Verdict);
    }

    [Fact]
    public async Task Two_removals_never_promote_an_unrelated_child_of_storage_refused_evidence()
    {
        var (w, store, roster) = await FullOrdinaryLog();
        var before = (await roster.GetAsync(RosterFixture.Account))!;
        var parent = Entry.Parse(Encoding.UTF8.GetBytes(before.Entries[^1]));
        w.Rename("A", "B", "addB", "addB", "ancestor");
        var ancestor = Padded(w["A"], Entry.MaxBytes, parent);
        w.Rename("child", "G", "G", "addB", "unrelated");
        var child = Padded(w["child"], Entry.MaxBytes, ancestor);
        w.Remove("first", "G", "addB", "addB");
        var first = Padded(w["first"], Entry.MaxRemoveBytes, ancestor);
        w.Remove("second", "G", "fill0", "addB");
        var body = JsonNode.Parse(w["second"].Body)!;
        body["target"]!["add"] = Codec.Base64Url(Entry.Parse(Encoding.UTF8.GetBytes(before.Entries[2])).Hash);
        var second = Padded(w["second"] with { Body = RosterFixture.Bytes(body) }, 1024, parent);
        var result = await roster.AppendAsync(RosterFixture.Account,
            [ancestor.Canonical(), child.Canonical(), first.Canonical(), second.Canonical()]);
        Assert.Equal(("accepted", Invalid.Evidence), (result![0].Verdict, result[0].Reason));
        Assert.Equal(("refused", Invalid.Evidence), (result[1].Verdict, result[1].Reason));
        Assert.Equal("quarantined", result[2].Verdict);
        Assert.Equal("accepted", result[3].Verdict);
        var snapshot = (await Make(w, store).GetAsync(RosterFixture.Account))!;
        Assert.Equal(before.Entries.Count + 3, snapshot.Entries.Count);
        var replay = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
        replay.Offer(snapshot.Entries.Select(s => Entry.Parse(Encoding.UTF8.GetBytes(s))));
        Assert.True(replay.Holds(first.Hash));
        Assert.True(replay.Holds(second.Hash));
        Assert.Null(replay.Graph.Find(child.Hash));
    }

    [Fact]
    public async Task Storage_cap_merge_evidence_never_adopts_an_unrelated_branch_even_after_replay()
    {
        var (w, store, roster) = await FullOrdinaryLog();
        var before = (await roster.GetAsync(RosterFixture.Account))!;
        var parent = Entry.Parse(Encoding.UTF8.GetBytes(before.Entries[^1]));
        w.Add("Q", "G", "Q", "G");
        Assert.Equal("quarantined", (await roster.AppendAsync(RosterFixture.Account, [Wire(w, "Q")]))![0].Verdict);
        w.Merge("M", "B", ["addB", "Q"], adopt: true);
        var body = JsonNode.Parse(w["M"].Body)!;
        body["prev"] = RosterFixture.Hashes([parent.Hash, w.Entry("Q").Hash]);
        body["seq"] = parent.Body.Seq + 1;
        var merge = Padded(w["M"] with { Body = RosterFixture.Bytes(body) }, Entry.MaxBytes);
        w.Remove("R", "G", "addB", "addB");
        var removal = Padded(w["R"], Entry.MaxRemoveBytes, merge);
        var result = await roster.AppendAsync(RosterFixture.Account, [merge.Canonical(), removal.Canonical()]);
        Assert.Equal([("accepted", Invalid.Evidence), ("quarantined", (string?)null)], result!.Select(r => (r.Verdict, r.Reason)));
        var snapshot = (await Make(w, store).GetAsync(RosterFixture.Account))!;
        var replay = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now);
        replay.Offer(snapshot.Entries.Select(s => Entry.Parse(Encoding.UTF8.GetBytes(s))));
        Assert.True(replay.Graph[replay.Graph.Find(merge.Hash)!.Value].Evidence);
        Assert.True(replay.IsQuarantined(replay.Graph.Find(w.Entry("Q").Hash)!.Value));
        Assert.False(replay.State.Active.ContainsKey(w.Id("Q")));
        w.Rename("after", "G", "G", "G", "after replay");
        Assert.NotNull(await Make(w, store).AppendAsync(RosterFixture.Account, [Wire(w, "after")]));
    }

    [Fact]
    public void Legacy_wrappers_remain_identical_and_the_verifier_has_no_relay_storage_cap()
    {
        var w = Scenario();
        var verifier = w.Verifier();
        var parent = w.Entry("addB");
        for (var i = 0; i < 16; i++)
        {
            w.Add($"legacy{i}", "G", $"L{i}", "addB");
            parent = Padded(w[$"legacy{i}"], Entry.MaxBytes, parent);
            Assert.Equal(parent.Canonical(), Entry.Parse(Encoding.UTF8.GetBytes(parent.Canonical())).Canonical());
            Assert.Equal(Verdict.Accepted, verifier.Offer(parent)[0].Verdict);
        }
    }

    [Theory]
    [InlineData("false", Refusal.Value)]
    [InlineData("\"true\"", Refusal.Type)]
    public void Evidence_markers_are_strict_and_never_bypass_signature_checks(string marker, string code)
    {
        var w = Scenario();
        var wire = Wire(w, "addB");
        Assert.Equal(code, Assert.Throws<RosterFormatException>(() =>
            Entry.Parse(Encoding.UTF8.GetBytes(wire[..^1] + ",\"evidence\":" + marker + "}"))).Code);
        w.Rename("bad", "B", "addB", "addB", "bad", signAs: "Z");
        var bad = Wire(w, "bad");
        var entry = Entry.Parse(Encoding.UTF8.GetBytes(bad[..^1] + ",\"evidence\":true}"));
        Assert.Equal(Invalid.Signature, w.Verifier("G", "addB").Offer(entry)[0].Reason);
    }

    [Fact]
    public async Task Invalid_signatures_after_a_size_refusal_do_not_replay_unchanged_history()
    {
        var (w, store, roster) = await FullOrdinaryLog();
        var before = (await roster.GetAsync(RosterFixture.Account))!;
        var parent = Entry.Parse(Encoding.UTF8.GetBytes(before.Entries[^1]));
        var entries = new List<string>();
        w.Rename("aside", "B", "addB", "addB", "aside");
        entries.Add(Padded(w["aside"], Entry.MaxBytes, parent).Canonical());
        for (var i = 0; i < 15; i++)
        {
            w.Rename($"bad{i}", "B", "addB", "addB", $"bad{i}", signAs: "Z");
            entries.Add(Padded(w[$"bad{i}"], Entry.MaxBytes, parent).Canonical());
        }
        var clock = new CountingTime(w.Now);
        var result = await Make(w, store, clock).AppendAsync(RosterFixture.Account, entries);
        Assert.Equal(("refused", Invalid.Cap), (result![0].Verdict, result[0].Reason));
        Assert.All(result.Skip(1), r => Assert.Equal(("refused", Invalid.Signature), (r.Verdict, r.Reason)));
        // Each replay reads the clock once per accepted add; allow the initial replay and one rollback.
        Assert.InRange(clock.Reads, 1, 2 * before.Entries.Count);
        Assert.Equal(before.Entries, (await roster.GetAsync(RosterFixture.Account))!.Entries);
    }

    sealed class CountingTime(TimeSpan now) : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return DateTimeOffset.UnixEpoch + now; }
    }

    [Fact]
    public void A_stored_roster_needs_a_token_key_so_names_survive_a_restart()
    {
        var store = new AzureRosterStoreStub();
        Assert.Throws<InvalidOperationException>(() =>
            new DeviceRoster(new RelayOptions(), null!, NullLogger<DeviceRoster>.Instance, store));
    }

    sealed class AzureRosterStoreStub : IRosterStore
    {
        public Task<StoredRoster?> ReadAsync(string name, CancellationToken ct) => Task.FromResult<StoredRoster?>(null);
        public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task A_malformed_stored_log_reads_and_appends_as_unavailable_without_throwing()
    {
        var w = Scenario();
        foreach (var bad in new[] { "not json", "{\"v\":1,\"generation\":\"x\",\"entries\":null}", "null" })
        {
            var store = new MemoryRosterStore();
            var roster = Make(w, store);
            await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")]);
            var name = store.Names.Single();
            var etag = (await store.ReadAsync(name, default))!.ETag;
            await store.WriteAsync(name, System.Text.Encoding.UTF8.GetBytes(bad), etag, default);
            Assert.Null(await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")]));
            Assert.Null(await roster.GetAsync(RosterFixture.Account));
        }
    }

    [Fact]
    public async Task Appends_waiting_on_one_account_are_capped()
    {
        var w = Scenario();
        var gate = new GatedStore();
        var roster = Make(w, gate);
        var first = roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")]);
        await gate.Reading.Task;
        var queued = Enumerable.Range(0, DeviceRoster.MaxWaiters).Select(_ => roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")])).ToList();
        Assert.Null(await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G")]));
        gate.Release.SetResult();
        Assert.NotNull(await first);
        foreach (var q in queued) { Assert.NotNull(await q); }
    }

    [Fact]
    public void An_append_reply_uses_the_documented_lowercase_names()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(DeviceRoster.AppendReply(7, [new DeviceRoster.Result("h", "accepted", null)]));
        Assert.Equal("{\"t\":\"roster-append-result\",\"id\":7,\"results\":[{\"hash\":\"h\",\"verdict\":\"accepted\",\"reason\":null}]}", json);
    }

    sealed class GatedStore : IRosterStore
    {
        readonly MemoryRosterStore inner = new();
        public readonly TaskCompletionSource Reading = new(), Release = new();
        public async Task<StoredRoster?> ReadAsync(string name, CancellationToken ct)
        {
            Reading.TrySetResult();
            await Release.Task;
            return await inner.ReadAsync(name, ct);
        }
        public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct) => inner.WriteAsync(name, body, etag, ct);
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => inner.DeleteAsync(name, etag, ct);
    }

    [Fact]
    public async Task The_log_is_kept_in_the_store_so_a_new_instance_serves_it()
    {
        var w = Scenario();
        var store = new MemoryRosterStore();
        var roster = Make(w, store);
        await roster.AppendAsync(RosterFixture.Account, [Wire(w, "G"), Wire(w, "addB")]);
        var again = Make(w, store);
        Assert.Equal(2, (await again.GetAsync(RosterFixture.Account))!.Entries.Count);
    }
}
