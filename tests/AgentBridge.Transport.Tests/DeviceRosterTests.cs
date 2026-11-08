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
    static DeviceRoster Make(World w, MemoryRosterStore? store = null)
    {
        store ??= new MemoryRosterStore();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch + w.Now);
        var options = new RelayOptions { TokenKey = Convert.ToBase64String(new byte[32]), Roster = { Time = time } };
        using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new RelayMetrics(meters.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), () => 0);
        return new DeviceRoster(options, metrics, NullLogger<DeviceRoster>.Instance, store);
    }

    static string Wire(World w, string label) => w.Entry(label).Canonical();

    static World Scenario()
    {
        var w = new World();
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
