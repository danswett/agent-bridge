using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

// The relay's roster of each account's machines (docs/protocol/roster.md): who is listed, who
// may forget, and what is kept across a restart.
public class RosterTests
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    static async Task<KnownMachine> UntilKnownAsync(Device d, string id)
    {
        KnownMachine? hit = null;
        await Wait.UntilAsync(() => (hit = d.Relay.Known?.FirstOrDefault(k => k.Id == id)) is not null, $"{id[..8]} listed as offline");
        return hit!;
    }

    static Task UntilGoneAsync(Device d, string id) =>
        Wait.UntilAsync(() => d.Relay.Known is { } k && k.All(m => m.Id != id), $"{id[..8]} not listed");

    static async Task LeaveAsync(TestRelay relay, Device d, Device watcher)
    {
        // Seen first, so seeing it gone is its leaving and not a presence from before it came.
        await watcher.Relay.WaitForPeerAsync(p => p.Id == d.Id, Patience);
        await d.DisposeAsync();
        await Wait.UntilAsync(() => watcher.Relay.Presence.All(p => p.Id != d.Id), "the device left presence");
    }

    [Fact]
    public async Task A_host_that_has_left_is_listed_as_offline_with_when_it_was_last_seen_and_one_online_is_not()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "laptop", "host");
        var app = await relay.ConnectAsync(token, "app", "app");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        Assert.NotNull(app.Relay.Known);
        Assert.DoesNotContain(app.Relay.Known!, k => k.Id == host.Id);

        var before = DateTimeOffset.UtcNow;
        await LeaveAsync(relay, host, app);
        var known = await UntilKnownAsync(app, host.Id);

        Assert.Equal(("laptop", "host"), (known.Name, known.Kind));
        Assert.InRange(known.LastSeen, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Only_hosts_are_recorded_and_each_only_as_itself()
    {
        var store = new MemoryRosterStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var relay = await TestRelay.StartAsync(o => { o.TokenKey = key; o.Roster.Store = store; });
        var token = relay.TokenFor(1, "octo");
        var app = await relay.ConnectAsync(token, "app", "app");
        var pwa = await relay.ConnectAsync(token, "phone", "pwa");
        var test = await relay.ConnectAsync(token, "test", "test");
        var host = await relay.ConnectAsync(token, "desk", "host");
        await LeaveAsync(relay, pwa, app);
        await LeaveAsync(relay, test, app);
        await LeaveAsync(relay, host, app);

        await UntilKnownAsync(app, host.Id);

        // The roster holds the id the host proved and the name it gave; the others left no trace,
        // in presence or in storage.
        Assert.Equal([(host.Id, "desk")], app.Relay.Known!.Select(k => (k.Id, k.Name)));
        var roster = relay.App.Services.GetRequiredService<MachineRoster>();
        await roster.FlushAsync(all: true);
        Assert.Equal([(host.Id, "desk", "host")], Read(store, roster, "1").Machines.Select(m => (m.Id, m.Name, m.Kind)));
    }

    [Fact]
    public async Task Another_accounts_machines_are_never_listed_and_cannot_be_forgotten()
    {
        await using var relay = await TestRelay.StartAsync();
        var mine = relay.TokenFor(1, "octo");
        var theirs = relay.TokenFor(2, "mallory");
        var app = await relay.ConnectAsync(mine, "app", "app");
        var host = await relay.ConnectAsync(mine, "desk", "host");
        var mallory = await relay.ConnectAsync(theirs, "mallory", "app");
        await LeaveAsync(relay, host, app);
        await UntilKnownAsync(app, host.Id);

        var refused = await mallory.Relay.ForgetAsync(host.Id);

        Assert.Equal("not-found", refused);
        Assert.Empty(mallory.Relay.Known!);
        Assert.Contains(app.Relay.Known!, k => k.Id == host.Id);
    }

    [Fact]
    public async Task Only_a_persons_device_can_forget_a_machine()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var app = await relay.ConnectAsync(token, "app", "app");
        var otherHost = await relay.ConnectAsync(token, "server", "host");
        var test = await relay.ConnectAsync(token, "test", "test");
        var gone = await relay.ConnectAsync(token, "desk", "host");
        await LeaveAsync(relay, gone, app);
        await UntilKnownAsync(app, gone.Id);

        // A host is where an agent acts; it never forgets a machine, and neither does a test device.
        Assert.Equal("not-allowed", await otherHost.Relay.ForgetAsync(gone.Id));
        Assert.Equal("not-allowed", await test.Relay.ForgetAsync(gone.Id));
        Assert.Contains(app.Relay.Known!, k => k.Id == gone.Id);

        foreach (var kind in new[] { "app", "pwa", "phone" })
        {
            var person = await relay.ConnectAsync(token, kind, kind);
            var again = await relay.ConnectAsync(token, "desk", "host", gone.Key);
            await LeaveAsync(relay, again, app);
            await UntilKnownAsync(person, gone.Id);
            Assert.Null(await person.Relay.ForgetAsync(gone.Id));
            // Every device of the account hears at once.
            await UntilGoneAsync(app, gone.Id);
            await UntilGoneAsync(otherHost, gone.Id);
        }
    }

    [Fact]
    public async Task An_online_machine_an_unknown_one_and_a_malformed_id_are_refused()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var app = await relay.ConnectAsync(token, "app", "app");
        var host = await relay.ConnectAsync(token, "desk", "host");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);

        Assert.Equal("online", await app.Relay.ForgetAsync(host.Id));
        Assert.Equal("online", await app.Relay.ForgetAsync(host.Id.ToUpperInvariant()));
        Assert.Equal("not-found", await app.Relay.ForgetAsync(Convert.ToHexString(RandomNumberGenerator.GetBytes(Wire.IdBytes)).ToLowerInvariant()));
        Assert.Equal("bad-request", await app.Relay.ForgetAsync("not a device"));
        Assert.Equal("bad-request", await app.Relay.ForgetAsync(""));
    }

    [Fact]
    public async Task A_forgotten_machine_that_comes_back_is_listed_again_when_it_next_leaves()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var app = await relay.ConnectAsync(token, "app", "app");
        var host = await relay.ConnectAsync(token, "desk", "host");
        await LeaveAsync(relay, host, app);
        await UntilKnownAsync(app, host.Id);
        Assert.Null(await app.Relay.ForgetAsync(host.Id));
        await UntilGoneAsync(app, host.Id);

        var back = await relay.ConnectAsync(token, "desk", "host", host.Key);
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        await LeaveAsync(relay, back, app);

        await UntilKnownAsync(app, host.Id);
    }

    [Fact]
    public async Task Forgets_are_rate_limited_per_account()
    {
        await using var relay = await TestRelay.StartAsync(o => o.Roster.MaxForgetsPerMinute = 2);
        var token = relay.TokenFor(1, "octo");
        var app = await relay.ConnectAsync(token, "app", "app");
        var nobody = Convert.ToHexString(RandomNumberGenerator.GetBytes(Wire.IdBytes)).ToLowerInvariant();

        Assert.Equal("not-found", await app.Relay.ForgetAsync(nobody));
        Assert.Equal("not-found", await app.Relay.ForgetAsync(nobody));
        Assert.Equal("rate-limited", await app.Relay.ForgetAsync(nobody));
        // A second device of the same account shares the limit.
        var phone = await relay.ConnectAsync(token, "phone", "pwa");
        Assert.Equal("rate-limited", await phone.Relay.ForgetAsync(nobody));
    }

    [Fact]
    public async Task The_oldest_offline_machines_go_past_the_cap()
    {
        await using var relay = await TestRelay.StartAsync(o => o.Roster.MaxMachines = 3);
        var token = relay.TokenFor(1, "octo");
        var app = await relay.ConnectAsync(token, "app", "app");
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var host = await relay.ConnectAsync(token, $"host {i}", "host");
            ids.Add(host.Id);
            await LeaveAsync(relay, host, app);
            await UntilKnownAsync(app, host.Id);
            await Task.Delay(20);
        }

        Assert.Equal(ids[2..].AsEnumerable().Reverse(), app.Relay.Known!.Select(k => k.Id));
    }

    [Fact]
    public async Task A_relay_without_a_roster_says_nothing_of_one_and_a_client_does_not_ask()
    {
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Backend = "off");
        var token = relay.TokenFor(1, "octo");
        var app = await relay.ConnectAsync(token, "app", "app");
        var host = await relay.ConnectAsync(token, "desk", "host");
        await LeaveAsync(relay, host, app);

        Assert.Null(app.Relay.Known);
        Assert.Equal("unsupported", await app.Relay.ForgetAsync(host.Id));
    }

    [Fact]
    public async Task A_restarted_relay_still_knows_the_accounts_machines_from_its_store()
    {
        var store = new MemoryRosterStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string hostId;
        await using (var relay = await TestRelay.StartAsync(o => { o.TokenKey = key; o.Roster.Store = store; o.Roster.FlushEvery = TimeSpan.FromHours(1); }))
        {
            var token = relay.TokenFor(1, "octo");
            var app = await relay.ConnectAsync(token, "app", "app");
            var host = await relay.ConnectAsync(token, "desk", "host");
            hostId = host.Id;
            await LeaveAsync(relay, host, app);
            await UntilKnownAsync(app, host.Id);
            // Written as the app stops, not by the periodic writer, whose interval is far off.
            Assert.Equal(0, store.Count);
            await relay.App.StopAsync();
        }
        // Stored under an opaque name, never the account id: an HMAC under a key from the token key.
        Assert.Equal([Replica(store, key).Roster.NameOf("1")], store.Names);
        Assert.Matches("^v1/[0-9a-f]{64}\\.json$", store.Names.Single());

        await using var again = await TestRelay.StartAsync(o => { o.TokenKey = key; o.Roster.Store = store; });
        var phone = await again.ConnectAsync(again.TokenFor(1, "octo"), "phone", "pwa");
        var known = await UntilKnownAsync(phone, hostId);
        Assert.Equal("desk", known.Name);
        // Another account sees none of it.
        var mallory = await again.ConnectAsync(again.TokenFor(2, "mallory"), "mallory", "pwa");
        Assert.Empty(mallory.Relay.Known!);
    }

    [Fact]
    public async Task A_stored_roster_needs_the_token_key_so_it_can_be_found_after_a_restart()
    {
        var o = new RelayOptions { Roster = { Store = new ThrowingStore() } };
        var e = Assert.Throws<InvalidOperationException>(() => new MachineRoster(o, Metrics(), NullLogger<MachineRoster>.Instance, o.Roster.Store));
        Assert.Contains("TokenKey", e.Message);
    }

    static RelayMetrics Metrics() => new(new TestMeterFactory(), () => 0);

    sealed class TestMeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
        public void Dispose() { }
    }

    sealed class ThrowingStore : IRosterStore
    {
        public Task<StoredRoster?> ReadAsync(string name, CancellationToken ct) => throw new IOException("down");
        public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct) => throw new IOException("down");
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => throw new IOException("down");
    }

    static string Id() => Convert.ToHexString(RandomNumberGenerator.GetBytes(Wire.IdBytes)).ToLowerInvariant();

    static (MachineRoster Roster, FakeTimeProvider Time) Replica(IRosterStore store, string key, FakeTimeProvider? time = null, Action<RosterOptions>? configure = null)
    {
        time ??= new FakeTimeProvider(DateTimeOffset.UtcNow);
        var o = new RelayOptions { TokenKey = key, Roster = { Time = time } };
        configure?.Invoke(o.Roster);
        return (new MachineRoster(o, Metrics(), NullLogger<MachineRoster>.Instance, store), time);
    }

    static RosterDocument Read(MemoryRosterStore store, MachineRoster roster, string account) =>
        JsonSerializer.Deserialize<RosterDocument>(store.ReadAsync(roster.NameOf(account), default).Result!.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task A_host_in_a_reconnect_loop_costs_one_write_per_interval_and_a_reader_writes_nothing()
    {
        var store = new MemoryRosterStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (roster, time) = Replica(store, key);
        var host = Id();

        for (var i = 0; i < 50; i++)
        {
            await roster.ConnectedAsync("1", host, "desk", "host");
            roster.Disconnected("1", host, "host");
            await roster.FlushAsync();
        }
        Assert.Equal(1, store.Writes);

        time.Advance(TimeSpan.FromSeconds(31));
        await roster.FlushAsync();
        Assert.Equal(2, store.Writes);
        await roster.FlushAsync();
        Assert.Equal(2, store.Writes);

        // A phone opening the app reads the roster and writes nothing.
        var (reader, _) = Replica(store, key, time);
        await reader.ConnectedAsync("1", Id(), "phone", "pwa");
        time.Advance(TimeSpan.FromMinutes(1));
        await reader.FlushAsync();
        Assert.Equal(2, store.Writes);
        Assert.Equal([host], reader.Known("1", _ => false)!.Select(k => k.Id));
    }

    [Fact]
    public async Task An_online_hosts_last_seen_is_kept_current_in_storage_every_refresh_interval()
    {
        var store = new MemoryRosterStore();
        var (roster, time) = Replica(store, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var host = Id();
        await roster.ConnectedAsync("1", host, "desk", "host");
        await roster.FlushAsync();
        var first = Read(store, roster, "1").Machines.Single().LastSeen;

        time.Advance(TimeSpan.FromMinutes(11));
        await roster.FlushAsync();

        Assert.Equal(first + TimeSpan.FromMinutes(11), Read(store, roster, "1").Machines.Single().LastSeen);
    }

    [Fact]
    public async Task A_forget_on_one_replica_is_not_undone_by_a_stale_copy_on_another()
    {
        var store = new MemoryRosterStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (old, _) = Replica(store, key, time);
        var (next, _) = Replica(store, key, time);
        var gone = Id();
        var stays = Id();
        await old.ConnectedAsync("1", Id(), "app", "app");
        await old.ConnectedAsync("1", gone, "desk", "host");
        old.Disconnected("1", gone, "host");
        await old.FlushAsync(all: true);

        // Mid-deploy: the new replica loads, a person forgets there; the old one still holds the
        // machine, sees another host leave, and writes last, against a copy that has changed.
        await next.ConnectedAsync("1", Id(), "phone", "pwa");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(next.Forget("1", gone, _ => false));
        await next.FlushAsync(all: true);
        await old.ConnectedAsync("1", stays, "laptop", "host");
        time.Advance(TimeSpan.FromSeconds(1));
        old.Disconnected("1", stays, "host");
        await old.FlushAsync(all: true);

        var doc = Read(store, next, "1");
        Assert.Equal([stays], doc.Machines.Select(m => m.Id));
        Assert.Contains(gone, doc.Forgotten.Keys);
        Assert.Equal([stays], old.Known("1", _ => false)!.Select(k => k.Id));
    }

    [Fact]
    public async Task A_store_that_is_down_delays_admission_only_briefly_and_the_roster_is_written_once_it_is_back()
    {
        var store = new FlakyStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (roster, time) = Replica(store, key, configure: o => o.StoreTimeout = TimeSpan.FromMilliseconds(200));
        store.Down = true;
        var host = Id();

        await roster.ConnectedAsync("1", host, "desk", "host");
        await roster.FlushAsync();
        Assert.Equal(0, store.Inner.Count);

        store.Down = false;
        time.Advance(TimeSpan.FromSeconds(31));
        await roster.FlushAsync();
        Assert.Equal(1, store.Inner.Count);
    }

    [Fact]
    public async Task A_forget_is_not_undone_by_a_stale_copy_even_once_its_marker_is_gone()
    {
        var store = new MemoryRosterStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        void Few(RosterOptions o) { o.MaxForgotten = 2; o.MaxForgetsPerMinute = 100; }
        var (old, _) = Replica(store, key, time, Few);
        var (next, _) = Replica(store, key, time, Few);
        var gone = Id();
        await old.ConnectedAsync("1", Id(), "app", "app");
        await old.ConnectedAsync("1", gone, "desk", "host");
        old.Disconnected("1", gone, "host");
        await old.FlushAsync(all: true);

        // The new relay forgets it, then more machines than it keeps markers for.
        await next.ConnectedAsync("1", Id(), "phone", "pwa");
        var others = Enumerable.Range(0, 3).Select(_ => Id()).ToList();
        foreach (var o in others) { await next.ConnectedAsync("1", o, "other", "host"); next.Disconnected("1", o, "host"); }
        foreach (var id in others.Prepend(gone))
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Null(next.Forget("1", id, _ => false));
        }
        await next.FlushAsync(all: true);
        Assert.DoesNotContain(gone, Read(store, next, "1").Forgotten.Keys);

        // The old relay, still holding it, writes against a copy that has changed.
        var seen = Id();
        await old.ConnectedAsync("1", seen, "laptop", "host");
        old.Disconnected("1", seen, "host");
        await old.FlushAsync(all: true);

        Assert.Equal([seen], Read(store, next, "1").Machines.Select(m => m.Id));
        Assert.Equal([seen], old.Known("1", _ => false)!.Select(k => k.Id));
    }

    [Fact]
    public async Task A_reader_admitted_while_the_store_was_down_is_told_the_list_once_it_is_back()
    {
        var store = new FlakyStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (writer, _) = Replica(store.Inner, key);
        var host = Id();
        await writer.ConnectedAsync("1", host, "desk", "host");
        writer.Disconnected("1", host, "host");
        await writer.FlushAsync(all: true);

        var (roster, _) = Replica(store, key, configure: o => o.StoreTimeout = TimeSpan.FromMilliseconds(200));
        var told = new List<string>();
        roster.Changed = account => { lock (told) { told.Add(account); } return Task.CompletedTask; };
        store.Down = true;
        await roster.ConnectedAsync("1", Id(), "phone", "pwa");
        // Not an empty list, which a device would take as every offline machine forgotten.
        Assert.Null(roster.Known("1", _ => false));
        await roster.FlushAsync();
        Assert.Empty(told);

        store.Down = false;
        await roster.FlushAsync();

        Assert.Equal(["1"], told);
        Assert.Equal([host], roster.Known("1", _ => false)!.Select(k => k.Id));
        // Read, not written: the phone changed nothing.
        Assert.Equal(1, store.Inner.Writes);
    }

    [Fact]
    public async Task A_relay_tells_an_accounts_devices_when_its_stored_list_arrives_late()
    {
        var store = new FlakyStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (writer, _) = Replica(store.Inner, key);
        var host = Id();
        await writer.ConnectedAsync("1", host, "desk", "host");
        writer.Disconnected("1", host, "host");
        await writer.FlushAsync(all: true);
        store.Down = true;
        await using var relay = await TestRelay.StartAsync(o =>
        {
            o.TokenKey = key;
            o.Roster.Store = store;
            o.Roster.StoreTimeout = TimeSpan.FromMilliseconds(200);
            o.Roster.FlushEvery = TimeSpan.FromMilliseconds(100);
        });
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        Assert.Null(phone.Relay.Known);

        store.Down = false;

        await UntilKnownAsync(phone, host);
    }

    [Fact]
    public async Task A_write_cut_short_by_shutdown_is_made_by_the_final_flush()
    {
        var store = new FlakyStore();
        var (roster, _) = Replica(store, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), configure: o => o.StoreTimeout = TimeSpan.FromSeconds(30));
        var host = Id();
        await roster.ConnectedAsync("1", Id(), "phone", "pwa");
        await roster.ConnectedAsync("1", host, "desk", "host");
        roster.Disconnected("1", host, "host");
        store.HoldWrites = true;
        using var stopping = new CancellationTokenSource();

        var periodic = roster.FlushAsync(ct: stopping.Token);
        await Wait.UntilAsync(() => store.WritesHeld > 0, "a write under way");
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => periodic);
        store.HoldWrites = false;
        await roster.FlushAsync(all: true);

        Assert.Equal([host], Read(store.Inner, roster, "1").Machines.Select(m => m.Id));
    }

    [Fact]
    public async Task Reconnecting_does_not_buy_a_fresh_allowance_of_forgets()
    {
        var (roster, _) = Replica(new MemoryRosterStore(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), configure: o => o.MaxForgetsPerMinute = 2);
        var nobody = Id();
        await roster.ConnectedAsync("1", "phone", "phone", "pwa");
        Assert.Equal("not-found", roster.Forget("1", nobody, _ => false));
        Assert.Equal("not-found", roster.Forget("1", nobody, _ => false));
        roster.Disconnected("1", "phone", "pwa");
        await roster.FlushAsync();

        await roster.ConnectedAsync("1", "phone", "phone", "pwa");

        Assert.Equal("rate-limited", roster.Forget("1", nobody, _ => false));
    }

    [Fact]
    public async Task A_relay_that_read_before_another_wrote_its_last_reads_again_and_tells_its_devices()
    {
        var store = new MemoryRosterStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (old, _) = Replica(store, key, time);
        var (next, _) = Replica(store, key, time);
        var gone = Id();
        var later = Id();
        await old.ConnectedAsync("1", Id(), "app", "app");
        await old.ConnectedAsync("1", gone, "desk", "host");
        old.Disconnected("1", gone, "host");
        await old.FlushAsync(all: true);

        // The new relay reads with only a phone of the account on it...
        var told = 0;
        next.Changed = _ => { Interlocked.Increment(ref told); return Task.CompletedTask; };
        await next.ConnectedAsync("1", Id(), "phone", "pwa");
        Assert.Equal([gone], next.Known("1", _ => false)!.Select(k => k.Id));
        // ...then the old one, handing over, forgets one machine and sees another leave, and
        // writes its last.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(old.Forget("1", gone, _ => false));
        await old.ConnectedAsync("1", later, "laptop", "host");
        old.Disconnected("1", later, "host");
        await old.FlushAsync(all: true);

        await next.FlushAsync();
        Assert.Equal(0, told);
        time.Advance(TimeSpan.FromSeconds(61));
        await next.FlushAsync();

        Assert.Equal(1, told);
        Assert.Equal([later], next.Known("1", _ => false)!.Select(k => k.Id));
        // Read, not written: nothing changed there.
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public async Task A_stored_copy_that_cannot_be_read_is_replaced_not_trusted()
    {
        var store = new MemoryRosterStore();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (roster, _) = Replica(store, key);
        await store.WriteAsync(roster.NameOf("1"), Encoding.UTF8.GetBytes("{not json"), null, default);
        var host = Id();

        await roster.ConnectedAsync("1", host, "desk", "host");
        await roster.FlushAsync();

        Assert.Equal([host], Read(store, roster, "1").Machines.Select(m => m.Id));
    }

    sealed class FlakyStore : IRosterStore
    {
        public readonly MemoryRosterStore Inner = new();
        public volatile bool Down;
        // Writes wait, until cancelled, while this is set.
        public volatile bool HoldWrites;
        public int WritesHeld;

        public async Task<StoredRoster?> ReadAsync(string name, CancellationToken ct)
        {
            if (Down) { await Task.Delay(Timeout.Infinite, ct); }
            return await Inner.ReadAsync(name, ct);
        }

        public async Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct)
        {
            if (HoldWrites)
            {
                Interlocked.Increment(ref WritesHeld);
                await Task.Delay(Timeout.Infinite, ct);
            }
            return Down ? throw new IOException("down") : await Inner.WriteAsync(name, body, etag, ct);
        }

        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) =>
            Down ? throw new IOException("down") : Inner.DeleteAsync(name, etag, ct);
    }
}
