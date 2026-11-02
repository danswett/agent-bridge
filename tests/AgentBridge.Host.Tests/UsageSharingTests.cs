using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Usage shared between the account's hosts (#172): one host asks another for its readings over a
// channel through the in-process relay, keeps them, and serves them with usage.shared once the
// machine that read them is gone, and after a restart of its own.
public sealed class UsageSharingTests : IAsyncLifetime
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    static readonly DateTimeOffset Now = new(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);

    readonly Fixture f = new();
    readonly FakeTimeProvider time = new(Now);
    readonly string folder = Path.Combine(Path.GetTempPath(), "abshare-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<IAsyncDisposable> owned = [];
    TestRelay relay = null!;
    SessionSource source = null!;
    string token = null!;

    public async Task InitializeAsync()
    {
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        relay = await TestRelay.StartAsync();
        token = relay.TokenFor(1, "octo");
    }

    public async Task DisposeAsync()
    {
        foreach (var o in owned.AsEnumerable().Reverse()) { await o.DisposeAsync(); }
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
        try { Directory.Delete(folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    static AgentUsage Copilot(double percent, DateTimeOffset at, string account = "octo") => new()
    {
        Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = account, Source = UsageSource.Api, MeasuredAt = at,
        Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = percent }],
    };

    static AgentUsage Claude(double percent, DateTimeOffset at, string key = "3f9a2c71b0de") => new()
    {
        Agent = AgentKind.Claude, Label = "Claude Code", AccountKey = key, Plan = "max", Source = UsageSource.Api, MeasuredAt = at,
        Windows = [new UsageWindow { Key = "weekly_all", Label = "Weekly", Percent = percent }],
    };

    static AgentUsage Failure(AgentKind agent, DateTimeOffset at, string error) => new()
    {
        Agent = agent, Label = agent.ToString(), Account = agent == AgentKind.Copilot ? "octo" : null, AccountKey = agent == AgentKind.Claude ? "3f9a2c71b0de" : null,
        Source = UsageSource.None, MeasuredAt = at, Error = error,
    };

    static string Id(char c) => new(c, 32);

    // A host of the account: its own readings from the given collectors, other machines' kept in
    // its own folder (null: memory only), and its UsagePeers to ask the others.
    sealed record HostUnderTest(Device Device, UsageMonitor Monitor, PeerUsage Store, UsageService Usage, UsagePeers Peers);

    async Task<HostUnderTest> HostAsync(string name, IEnumerable<Func<AgentUsage?>> readings, string? store = null, string kind = "host", string? tokenFor = null)
    {
        var device = await relay.ConnectAsync(tokenFor ?? token, name, kind);
        var monitor = new UsageMonitor([.. readings.Select(r => new Collector(r))], source,
            new UsageOptions { CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, CodexHome = f.CodexHome }, time);
        owned.Add(monitor);
        await monitor.PassAsync();
        var peers = PeerUsage.Load(store, device.Id, device.Relay.AccountId, time);
        var usage = new UsageService(monitor, peers: peers);
        var service = new SessionService(source, new MachineInfo(device.Id, name, "windows", "0.1.0"), usage: usage);
        device.Hub.JsonOptions = ProtocolJson.Options;
        device.Hub.Handler = service.HandleAsync;
        var asking = new UsagePeers(peers, device.Id, time: time);
        owned.Add(asking);
        asking.Attach(device.Relay, device.Hub);
        return new HostUnderTest(device, monitor, peers, usage, asking);
    }

    async Task<SecureChannel> OpenAsync(Device from, Device to)
    {
        await from.Relay.WaitForPeerAsync(p => p.Id == to.Id, Patience);
        return await from.Hub.OpenAsync(to.Id);
    }

    static async Task SeeAsync(Device who, string peer) => await who.Relay.WaitForPeerAsync(p => p.Id == peer, Patience);

    static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static UsageReport Report(JsonElement response) => Result(response).Deserialize<UsageReport>(ProtocolJson.Options)!;

    async Task<UsageReport> AskAsync(Device phone, Device host, bool shared)
    {
        var ch = await OpenAsync(phone, host);
        return Report(await ch.RequestAsync(UsageProtocol.Get, shared ? new { shared = true } : new { }));
    }

    [Fact]
    public async Task A_host_serves_a_peers_claude_reading_after_that_peer_goes_offline_and_after_its_own_restart()
    {
        var desk = await HostAsync("Desk", [() => Copilot(40, Now)], store: folder);
        var vm = await HostAsync("Rehearsal VM", [() => Claude(41.3, Now.AddMinutes(-4))]);
        await SeeAsync(desk.Device, vm.Device.Id);

        Assert.Equal(1, await desk.Peers.PassAsync());
        await vm.Device.DisposeAsync();
        await Wait.UntilAsync(() => desk.Device.Relay.Presence.All(p => p.Id != vm.Device.Id), "the VM to leave presence", Patience);

        var phone = await relay.ConnectAsync(token, "Phone", "pwa");
        var served = await AskAsync(phone, desk.Device, shared: true);
        Assert.Equal([AgentKind.Copilot, AgentKind.Claude], served.Agents.Select(a => a.Agent));
        Assert.Null(served.Agents[0].ReadBy);
        var claude = served.Agents[1];
        Assert.Equal((vm.Device.Id, "Rehearsal VM"), (claude.ReadBy!.Id, claude.ReadBy.Name));
        Assert.Equal((41.3, Now.AddMinutes(-4)), (claude.Windows[0].Percent, claude.MeasuredAt));

        // The desk restarts: what it kept is in its folder, and is served again.
        var restarted = PeerUsage.Load(folder, desk.Device.Id, desk.Device.Relay.AccountId, time);
        Assert.Equal(vm.Device.Id, Assert.Single(restarted.Entries).ReadBy!.Id);
        Assert.Equal([AgentKind.Copilot, AgentKind.Claude], UsageShare.Serve(desk.Monitor.Current.Report, restarted.Entries).Agents.Select(a => a.Agent));
    }

    [Fact]
    public async Task A_client_without_usage_shared_gets_only_the_hosts_own_entries()
    {
        var desk = await HostAsync("Desk", [() => Copilot(40, Now)]);
        var vm = await HostAsync("Rehearsal VM", [() => Claude(41.3, Now)]);
        await SeeAsync(desk.Device, vm.Device.Id);
        await desk.Peers.PassAsync();
        var phone = await relay.ConnectAsync(token, "Phone", "pwa");

        var ch = await OpenAsync(phone, desk.Device);
        var hello = Result(await ch.RequestAsync(Ops.Hello, new { }));
        Assert.Contains(UsageProtocol.SharedCapability, hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        var plain = Result(await ch.RequestAsync(UsageProtocol.Get, new { }));
        // Byte for byte the report it gave before #172.
        Assert.Equal(JsonSerializer.Serialize(desk.Monitor.Current.Report, ProtocolJson.Options), plain.GetRawText());
        Assert.Single(Result(await ch.RequestAsync(UsageProtocol.Subscribe, new { })).GetProperty("agents").EnumerateArray());
        Assert.Equal(2, Report(await ch.RequestAsync(UsageProtocol.Get, new { shared = true })).Agents.Count);
    }

    [Fact]
    public async Task A_shared_subscription_hears_of_a_peers_new_reading_and_a_plain_one_does_not()
    {
        var desk = await HostAsync("Desk", [() => Copilot(40, Now)]);
        var percent = 10.0;
        var vm = await HostAsync("Rehearsal VM", [() => Claude(percent, time.GetUtcNow())]);
        await SeeAsync(desk.Device, vm.Device.Id);
        var phone = await relay.ConnectAsync(token, "Phone", "pwa");
        var shared = await OpenAsync(phone, desk.Device);
        var plain = await OpenAsync(phone, desk.Device);
        Result(await shared.RequestAsync(UsageProtocol.Subscribe, new { shared = true }));
        Result(await plain.RequestAsync(UsageProtocol.Subscribe, new { }));

        await desk.Peers.PassAsync();

        using var cts = new CancellationTokenSource(Patience);
        var update = await shared.Events.ReadAsync(cts.Token);
        Assert.Equal(UsageProtocol.Update, update.Name);
        var claude = update.Data.Deserialize<UsageReport>(ProtocolJson.Options)!.Agents.Single(a => a.Agent == AgentKind.Claude);
        Assert.Equal(vm.Device.Id, claude.ReadBy!.Id);
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plain.Events.ReadAsync(quiet.Token));
    }

    [Fact]
    public async Task Peer_readings_are_taken_only_from_host_devices_of_the_same_account()
    {
        var desk = await HostAsync("Desk", [() => Copilot(40, Now)]);
        // A device of the account that is not a host, serving usage as one would.
        var pretender = await HostAsync("Not a host", [() => Claude(99, Now)], kind: "pwa");
        // A host of another account, which this account's presence never lists.
        var stranger = await HostAsync("Someone else's", [() => Claude(98, Now, key: "aaaaaaaaaaaa")], tokenFor: relay.TokenFor(2, "mallory"));
        await SeeAsync(desk.Device, pretender.Device.Id);

        Assert.Equal(0, await desk.Peers.PassAsync());
        Assert.Empty(desk.Store.Entries);
        Assert.DoesNotContain(desk.Device.Relay.Presence, p => p.Id == stranger.Device.Id);

        // And a host whose channel introduces it as something else is not believed either.
        var key = P256KeyPair.Generate();
        await using var vm = await RelayClient.ConnectAsync(relay.Url, token, key, "Rehearsal VM", "host");
        var service = new SessionService(source, new MachineInfo(vm.DeviceId, "Rehearsal VM", "windows", "0.1.0"), usage: pretender.Usage);
        var disguised = new ChannelHub(vm, key, "Rehearsal VM", "pwa") { JsonOptions = ProtocolJson.Options, Handler = service.HandleAsync };
        await SeeAsync(desk.Device, vm.DeviceId);
        Assert.Equal(1, await desk.Peers.PassAsync());
        Assert.Empty(desk.Store.Entries);
        GC.KeepAlive(disguised);
    }

    [Fact]
    public async Task A_peer_is_asked_on_appearing_then_every_five_minutes_and_never_twice_in_a_minute()
    {
        var desk = await HostAsync("Desk", [() => Copilot(40, Now)]);
        var vm = await HostAsync("Rehearsal VM", [() => Claude(41.3, Now)]);
        await SeeAsync(desk.Device, vm.Device.Id);

        Assert.Equal(1, await desk.Peers.PassAsync());
        Assert.Equal(0, await desk.Peers.PassAsync());
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, await desk.Peers.PassAsync());
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await desk.Peers.PassAsync());
    }

    [Fact]
    public async Task A_peer_on_a_new_connection_is_asked_once_a_minute_has_passed_not_at_the_next_five()
    {
        var desk = await HostAsync("Desk", [() => Copilot(40, Now)]);
        var vm = await HostAsync("Rehearsal VM", [() => Claude(41.3, Now)]);
        await SeeAsync(desk.Device, vm.Device.Id);
        Assert.Equal(1, await desk.Peers.PassAsync());

        // The desk's relay connection is made again; the VM is there on the new one.
        time.Advance(TimeSpan.FromMinutes(2));
        desk.Peers.Attach(desk.Device.Relay, desk.Device.Hub);
        Assert.Equal(1, await desk.Peers.PassAsync());
        Assert.Equal(0, await desk.Peers.PassAsync());
    }

    [Fact]
    public void A_newer_reading_from_any_host_replaces_an_older_one_and_an_error_never_replaces_a_fresher_success()
    {
        var store = PeerUsage.Load(null, Id('d'), "1", time);
        var vm = new UsageReader(Id('a'), "VM");
        var laptop = new UsageReader(Id('b'), "Laptop");

        Assert.Equal(1, store.Take([Claude(30, Now.AddHours(-2))], vm));
        Assert.Equal(1, store.Take([Claude(35, Now.AddHours(-1))], laptop));
        Assert.Equal((35, "Laptop"), (Assert.Single(store.Entries).Windows[0].Percent, store.Entries[0].ReadBy!.Name));
        // Older, or the same one again (passed back by a third host): nothing changes.
        Assert.Equal(0, store.Take([Claude(30, Now.AddHours(-2))], vm));
        Assert.Equal(0, store.Take([Claude(35, Now.AddHours(-1)) with { ReadBy = laptop }], vm));

        // A failure since is the latest word on why there is nothing fresher, never the figure. It is
        // kept, and passed on, with its own time, beside the reading.
        Assert.Equal(1, store.Take([Failure(AgentKind.Claude, Now, "The Claude usage could not be read: 500")], vm));
        Assert.Equal([(35.0, null, "Laptop"), (null, "The Claude usage could not be read: 500", "VM")],
            store.Entries.Select(e => (e.Windows.FirstOrDefault()?.Percent, e.Error, e.ReadBy!.Name)));
        // A host that already has the reading still learns of the failure.
        var other = PeerUsage.Load(null, Id('e'), "1", time);
        other.Take([Claude(35, Now.AddHours(-1)) with { ReadBy = laptop }], vm);
        Assert.Equal(1, other.Take(store.Entries, new UsageReader(Id('d'), "Desk")));
        Assert.Equal(2, other.Entries.Count);
        // An older failure says nothing about the newer reading, and is not passed on.
        var settled = PeerUsage.Load(null, Id('e'), "1", time);
        settled.Take([Claude(35, Now.AddHours(-1)), Failure(AgentKind.Claude, Now.AddHours(-2), "old")], laptop);
        Assert.Null(Assert.Single(settled.Entries).Error);
    }

    [Fact]
    public void A_hosts_own_reading_beats_a_peers_copy_of_it()
    {
        var me = Id('d');
        var store = PeerUsage.Load(null, me, "1", time);
        // This machine's own reading, passed back to it by a peer.
        Assert.Equal(0, store.Take([Copilot(40, Now) with { ReadBy = new UsageReader(me, "Desk") }], new UsageReader(Id('a'), "VM")));
        // Another machine's copy of the same allowance, no newer than this machine's own.
        Assert.Equal(1, store.Take([Copilot(38, Now.AddMinutes(-10))], new UsageReader(Id('a'), "VM")));

        var own = new UsageReport([Copilot(40, Now)]);
        Assert.Equal(own.Agents, UsageShare.Serve(own, store.Entries).Agents);
        // Its own failure, though, does not hide a reading another machine has.
        var failed = new UsageReport([Failure(AgentKind.Copilot, Now, "gh is not installed")]);
        Assert.Equal(2, UsageShare.Serve(failed, store.Entries).Agents.Count);
    }

    [Fact]
    public void A_peers_failure_newer_than_the_hosts_own_reading_is_passed_on_beside_it_and_an_older_one_is_not()
    {
        var store = PeerUsage.Load(null, Id('d'), "1", time);
        var vm = new UsageReader(Id('a'), "VM");
        store.Take([Copilot(45, Now.AddMinutes(-30)), Failure(AgentKind.Copilot, Now, "rate limited")], vm);
        var own = new UsageReport([Copilot(40, Now.AddHours(-1))]);

        Assert.Equal([(40.0, null), (45.0, null), (null, "rate limited")],
            UsageShare.Serve(own, store.Entries).Agents.Select(a => (a.Windows.FirstOrDefault()?.Percent, a.Error)));
        // Only the host's own reading of it, and a failure older than that: the failure says nothing new.
        var only = PeerUsage.Load(null, Id('d'), "1", time);
        only.Take([Failure(AgentKind.Copilot, Now.AddHours(-2), "old")], vm);
        Assert.Equal(own.Agents, UsageShare.Serve(own, only.Entries).Agents);
    }

    [Fact]
    public void A_malformed_entry_from_a_peer_is_refused_without_letting_the_store_grow_past_its_cap()
    {
        var store = PeerUsage.Load(null, Id('d'), "1", time);
        var vm = new UsageReader(Id('a'), "VM");
        var key = 0;
        for (var round = 0; round < 4; round++)
        {
            var answer = Enumerable.Range(0, 14).Select(_ => Claude(10, Now, key: $"k{key++}")).ToList();
            answer.Add(Claude(10, Now) with { Windows = null! });
            answer.Add(Claude(10, Now) with { Windows = [null!] });
            Assert.Equal(14, store.Take(answer, vm));
        }
        Assert.Equal(32, store.Entries.Count);
    }

    [Fact]
    public void Readings_older_than_two_weeks_go_and_one_stamped_in_the_future_is_refused()
    {
        var store = PeerUsage.Load(null, Id('d'), "1", time);
        var vm = new UsageReader(Id('a'), "VM");
        Assert.Equal(0, store.Take([Claude(10, Now.AddHours(1))], vm));
        Assert.Equal(1, store.Take([Claude(10, Now.AddDays(-13))], vm));
        var heard = 0;
        store.Changed += () => heard++;
        var version = store.Version;

        time.Advance(TimeSpan.FromDays(2));
        Assert.Empty(store.Entries);
        // And said, so a subscriber with nothing else changing hears it has gone.
        Assert.Equal(1, store.Prune());
        Assert.Equal((1, version + 1), (heard, store.Version));
        Assert.Equal(0, store.Prune());
    }

    [Fact]
    public async Task A_connection_from_before_a_sign_in_attached_after_it_takes_nothing_for_the_account_before()
    {
        var desk = await HostAsync("Desk", [() => Copilot(40, Now)]);
        var vm = await HostAsync("Rehearsal VM", [() => Claude(41.3, Now.AddMinutes(-4))]);
        await SeeAsync(desk.Device, vm.Device.Id);

        // The desk signs in to another account while its connection to this one is being made, and
        // that connection is attached after.
        desk.Store.Bind("another-account");
        desk.Peers.Attach(desk.Device.Relay, desk.Device.Hub);
        Assert.Equal(1, await desk.Peers.PassAsync());

        Assert.Empty(desk.Store.Entries);
    }

    [Fact]
    public void Readings_kept_for_another_account_are_replaced_when_the_host_starts_on_this_one()
    {
        var first = PeerUsage.Load(folder, Id('d'), "1", time);
        first.Take([Claude(10, Now)], new UsageReader(Id('a'), "VM"));

        Assert.Empty(PeerUsage.Load(folder, Id('d'), "2", time).Entries);
        // Back on the first account later: what was kept for it went when the host ran as the other.
        Assert.Empty(PeerUsage.Load(folder, Id('d'), "1", time).Entries);
    }

    [Fact]
    public void Readings_kept_for_one_account_are_never_served_to_another()
    {
        var store = PeerUsage.Load(folder, Id('d'), "1", time);
        store.Take([Claude(10, Now)], new UsageReader(Id('a'), "VM"));

        Assert.Empty(PeerUsage.Load(folder, Id('d'), "2", time).Entries);
        store.Bind("2");
        Assert.Empty(store.Entries);
        Assert.Empty(PeerUsage.Load(folder, Id('d'), "1", time).Entries);
        // An answer to a question asked for the account before arrives late.
        Assert.Equal(0, store.Take([Claude(10, Now)], new UsageReader(Id('a'), "VM"), forAccount: "1"));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task A_reading_taken_by_a_machine_the_account_has_forgotten_is_neither_kept_nor_taken_from_another_host()
    {
        var desk = await HostAsync("Desk", [() => Copilot(40, Now)]);
        var vm = await HostAsync("Rehearsal VM", [() => Copilot(41, Now)]);
        var old = await relay.ConnectAsync(token, "Old laptop", "host");
        var oldId = old.Id;
        // The VM had the old laptop's Claude reading, and the desk does too.
        vm.Store.Take([Claude(12, Now)], new UsageReader(oldId, "Old laptop"));
        desk.Store.Take([Claude(12, Now)], new UsageReader(oldId, "Old laptop"));
        await old.DisposeAsync();
        var phone = await relay.ConnectAsync(token, "Phone", "pwa");
        await Wait.UntilAsync(() => phone.Relay.Known?.Any(k => k.Id == oldId) == true, "the old laptop to be listed offline", Patience);

        Assert.Null(await phone.Relay.ForgetAsync(oldId));
        await Wait.UntilAsync(() => desk.Device.Relay.Known is { } k && k.All(m => m.Id != oldId), "the desk to hear it was forgotten", Patience);
        await SeeAsync(desk.Device, vm.Device.Id);
        Assert.Equal(1, await desk.Peers.PassAsync());

        Assert.DoesNotContain(desk.Store.Entries, e => e.Agent == AgentKind.Claude);
        // The VM itself, not yet asked to look, still has it; the desk did not take it back.
        Assert.Contains(vm.Store.Entries, e => e.Agent == AgentKind.Claude);
    }

    sealed class Collector(Func<AgentUsage?> read) : IUsageCollector
    {
        public AgentKind Agent => read()?.Agent ?? AgentKind.Copilot;
        public Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default) => Task.FromResult(read());
    }
}
