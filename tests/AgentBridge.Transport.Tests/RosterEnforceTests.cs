using AgentBridge.Relay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

// #55 R2 (devices.md, enforce): an account's relay mode starts in observe, moves to enforce only when a person's device in its
// roster asks, and never moves back; in enforce the relay tags each device approved, pending or removed. The relay is defense in
// depth, never the gate, so none of this changes what a host does.
public class RosterEnforceTests
{
    const string Account = "1";

    static DeviceRoster Make(World w, IRosterStore store)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch + w.Now);
        var options = new RelayOptions { TokenKey = Convert.ToBase64String(new byte[32]), Roster = { Time = time } };
        using var meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new RelayMetrics(meters.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), () => 0);
        return new DeviceRoster(options, metrics, NullLogger<DeviceRoster>.Instance, store);
    }

    static World Scenario()
    {
        var w = new World { AccountId = Account };
        w.Genesis();
        w.Add("addB", "G", "B", "G");
        return w;
    }

    static string Wire(World w, string label) => w.Entry(label).Canonical();

    [Fact]
    public async Task An_account_is_open_until_a_rostered_person_device_asks_and_it_never_moves_back()
    {
        var w = Scenario();
        var roster = Make(w, new MemoryRosterStore());
        Assert.False(await roster.EnforceAsync(Account, w.Id("G")));  // no roster yet: nobody can switch it
        await roster.AppendAsync(Account, [Wire(w, "G"), Wire(w, "addB")]);
        Assert.Equal(DeviceRoster.Admission.Open, await roster.AdmissionAsync(Account, w.Id("X")));
        Assert.Equal(DeviceRoster.Observe, (await roster.GetAsync(Account))!.Mode);
        Assert.False(await roster.EnforceAsync(Account, w.Id("X")));  // not in the roster
        Assert.Equal(DeviceRoster.Observe, (await roster.GetAsync(Account))!.Mode);
        Assert.True(await roster.EnforceAsync(Account, w.Id("G")));
        Assert.True(await roster.EnforceAsync(Account, w.Id("G")));   // again: still enforce
        Assert.Equal(DeviceRoster.Enforce, (await roster.GetAsync(Account))!.Mode);
        w.Remove("rmB", "G", "addB", "addB");
        await roster.AppendAsync(Account, [Wire(w, "rmB")]);
        Assert.Equal(DeviceRoster.Enforce, (await roster.GetAsync(Account))!.Mode);
    }

    [Fact]
    public async Task In_enforce_a_rostered_device_is_approved_a_stranger_pending_and_a_removed_one_removed()
    {
        var w = Scenario();
        var roster = Make(w, new MemoryRosterStore());
        await roster.AppendAsync(Account, [Wire(w, "G"), Wire(w, "addB")]);
        Assert.True(await roster.EnforceAsync(Account, w.Id("G")));
        Assert.Equal(DeviceRoster.Admission.Approved, await roster.AdmissionAsync(Account, w.Id("G")));
        Assert.Equal(DeviceRoster.Admission.Approved, await roster.AdmissionAsync(Account, w.Id("B")));
        Assert.Equal(DeviceRoster.Admission.Pending, await roster.AdmissionAsync(Account, w.Id("X")));

        w.Remove("rmB", "G", "addB", "addB");
        await roster.AppendAsync(Account, [Wire(w, "rmB")]);
        Assert.Equal(DeviceRoster.Admission.Removed, await roster.AdmissionAsync(Account, w.Id("B")));
        Assert.Equal(DeviceRoster.Admission.Approved, await roster.AdmissionAsync(Account, w.Id("G")));
        Assert.Equal(4030, CloseCodes.Removed);
    }

    // A store that fails every read once Broken, or from the next write on (BreakAfterWrite).
    sealed class FlakyStore(IRosterStore inner) : IRosterStore
    {
        public volatile bool Broken, BreakAfterWrite;
        public Task<StoredRoster?> ReadAsync(string name, CancellationToken ct) => Broken ? throw new IOException("down") : inner.ReadAsync(name, ct);
        public async Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct)
        {
            var r = await inner.WriteAsync(name, body, etag, ct);
            if (BreakAfterWrite) { Broken = true; }
            return r;
        }
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => inner.DeleteAsync(name, etag, ct);
    }

    // A latched account (seen in enforce) is Unavailable when the store cannot say; one never seen in enforce stays Open.
    [Fact]
    public async Task A_store_that_cannot_be_read_never_opens_an_account_that_is_or_may_be_in_enforce()
    {
        var w = Scenario();
        var store = new FlakyStore(new MemoryRosterStore());
        var roster = Make(w, store);
        await roster.AppendAsync(Account, [Wire(w, "G")]);

        // Never seen in enforce: an outage changes nothing for it, cold or not.
        store.Broken = true;
        Assert.Equal(DeviceRoster.Admission.Open, await roster.AdmissionAsync("cold", w.Id("X")));
        store.Broken = false;

        // Seen in observe: it stays as it was through an outage.
        Assert.Equal(DeviceRoster.Admission.Open, await roster.AdmissionAsync(Account, w.Id("X")));
        store.Broken = true;
        Assert.Equal(DeviceRoster.Admission.Open, await roster.AdmissionAsync(Account, w.Id("X")));
        store.Broken = false;

        // Latched in enforce: an outage never turns a stranger back into an open device.
        Assert.True(await roster.EnforceAsync(Account, w.Id("G")));
        Assert.Equal(DeviceRoster.Admission.Pending, await roster.AdmissionAsync(Account, w.Id("X")));
        store.Broken = true;
        Assert.Equal(DeviceRoster.Admission.Unavailable, await roster.AdmissionAsync(Account, w.Id("X")));
        Assert.Equal(DeviceRoster.Admission.Unavailable, await roster.AdmissionAsync(Account, w.Id("G")));
    }

    [Fact]
    public async Task A_garbled_or_missing_stored_roster_is_unavailable_for_a_latched_account_and_open_for_one_that_is_not()
    {
        var w = Scenario();
        var inner = new MemoryRosterStore();
        var roster = Make(w, inner);
        await roster.AppendAsync(Account, [Wire(w, "G")]);
        Assert.True(await roster.EnforceAsync(Account, w.Id("G")));
        var name = inner.Names.Single();
        var current = await inner.ReadAsync(name, CancellationToken.None);
        Assert.NotNull(await inner.WriteAsync(name, "not json"u8.ToArray(), current!.ETag, CancellationToken.None));
        Assert.Equal(DeviceRoster.Admission.Unavailable, await roster.AdmissionAsync(Account, w.Id("X")));
        // Missing: the roster is gone from the store, and a latched account does not read that as "no roster".
        Assert.True(await inner.DeleteAsync(name, (await inner.ReadAsync(name, CancellationToken.None))!.ETag, CancellationToken.None));
        Assert.Equal(DeviceRoster.Admission.Unavailable, await roster.AdmissionAsync(Account, w.Id("X")));
        // An account this relay never latched, with no roster, is open as before.
        Assert.Equal(DeviceRoster.Admission.Open, await roster.AdmissionAsync("other", w.Id("X")));
    }

    // In enforce a connected stranger (a key the roster does not hold) is pending: it sees nothing of the account, its control
    // messages are ignored, and no channel can be opened to or from it by another pending device.
    [Fact]
    public async Task A_pending_device_sees_nobody_gets_no_roster_and_cannot_open_a_channel()
    {
        var w = Scenario();
        await using var relay = await TestRelay.StartAsync();
        var devices = relay.App.Services.GetRequiredService<DeviceRoster>();
        await devices.AppendAsync(Account, [Wire(w, "G")]);
        Assert.True(await devices.EnforceAsync(Account, w.Id("G")));

        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host-a", "host");
        var app = await relay.ConnectAsync(token, "app-b", "app");
        await Task.Delay(500);

        Assert.Empty(host.Relay.Presence);
        Assert.Empty(app.Relay.Presence);
        using var shortly = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Relay.GetRosterAsync(shortly.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => app.Hub.OpenAsync(host.Id, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task In_observe_the_same_two_devices_see_each_other_and_open_a_channel_as_before()
    {
        var w = Scenario();
        await using var relay = await TestRelay.StartAsync();
        var devices = relay.App.Services.GetRequiredService<DeviceRoster>();
        await devices.AppendAsync(Account, [Wire(w, "G")]);

        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host-a", "host");
        var app = await relay.ConnectAsync(token, "app-b", "app");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        var ch = await app.Hub.OpenAsync(host.Id);
        Assert.True((await ch.RequestAsync("ping")).GetProperty("ok").GetBoolean());
    }

    // A connecting device whose key is the roster fixture's own, so the relay sees the identity the roster holds.
    static P256KeyPair KeyOf(string device)
    {
        var k = World.Keys(device).Dh;
        var d = k.D.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (d.Length < 32) { d = [.. new byte[32 - d.Length], .. d]; }
        return P256KeyPair.FromParameters(d, k.Public[1..33], k.Public[33..65]);
    }

    static async Task<(TestRelay Relay, DeviceRoster Roster, World W, string Token)> StartAsync()
    {
        var w = Scenario();
        var relay = await TestRelay.StartAsync();
        var roster = relay.App.Services.GetRequiredService<DeviceRoster>();
        await roster.AppendAsync(Account, [Wire(w, "G")]);
        return (relay, roster, w, relay.TokenFor(1, "octo"));
    }

    // The append goes over the socket, which is what re-tags connected devices.
    static Task AppendOverSocketAsync(Device from, string wrapper) => from.Relay.SendRawAsync(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { t = "roster-append", id = 1, entries = new[] { wrapper } })),
        System.Net.WebSockets.WebSocketMessageType.Text);

    [Fact]
    public async Task A_rostered_person_device_switches_the_account_and_a_connected_stranger_becomes_pending()
    {
        var (relay, roster, w, token) = await StartAsync();
        await using var lifetime = relay;
        var phone = await relay.ConnectAsync(token, "phone-g", "phone", KeyOf("G"));
        var stranger = await relay.ConnectAsync(token, "app-x", "app");
        await phone.Relay.WaitForPeerAsync(p => p.Id == stranger.Id, TimeSpan.FromSeconds(10));
        await stranger.Relay.WaitForPeerAsync(p => p.Id == phone.Id, TimeSpan.FromSeconds(10));

        Assert.True(await phone.Relay.EnforceRosterAsync());

        Assert.Equal(DeviceRoster.Enforce, (await roster.GetAsync(Account))!.Mode);
        await Wait.UntilAsync(() => stranger.Relay.Presence.Count == 0, "the stranger to be told nothing", TimeSpan.FromSeconds(10));
        Assert.Contains(phone.Relay.Presence, p => p.Id == stranger.Id);  // approvers still see who is there
    }

    [Fact]
    public async Task A_stranger_or_a_host_cannot_switch_the_account()
    {
        var (relay, roster, w, token) = await StartAsync();
        await using var lifetime = relay;
        var stranger = await relay.ConnectAsync(token, "app-x", "app");
        var host = await relay.ConnectAsync(token, "host-g", "host", KeyOf("G"));  // the roster's key, but not a person's kind
        Assert.False(await stranger.Relay.EnforceRosterAsync());
        Assert.False(await host.Relay.EnforceRosterAsync());
        Assert.Equal(DeviceRoster.Observe, (await roster.GetAsync(Account))!.Mode);
    }

    [Fact]
    public async Task A_pending_device_is_approved_the_moment_its_add_is_appended()
    {
        var (relay, roster, w, token) = await StartAsync();
        await using var lifetime = relay;
        var phone = await relay.ConnectAsync(token, "phone-g", "phone", KeyOf("G"));
        Assert.True(await phone.Relay.EnforceRosterAsync());
        var joining = await relay.ConnectAsync(token, "app-b", "app", KeyOf("B"));
        await Task.Delay(500);
        Assert.Empty(joining.Relay.Presence);

        await AppendOverSocketAsync(phone, Wire(w, "addB"));

        await joining.Relay.WaitForPeerAsync(p => p.Id == phone.Id, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_device_removed_from_the_roster_is_closed_with_4030_and_refused_when_it_returns()
    {
        var (relay, roster, w, token) = await StartAsync();
        await using var lifetime = relay;
        await roster.AppendAsync(Account, [Wire(w, "addB")]);
        var phone = await relay.ConnectAsync(token, "phone-g", "phone", KeyOf("G"));
        Assert.True(await phone.Relay.EnforceRosterAsync());
        var other = await relay.ConnectAsync(token, "app-b", "app", KeyOf("B"));
        await other.Relay.WaitForPeerAsync(p => p.Id == phone.Id, TimeSpan.FromSeconds(10));

        w.Remove("rmB", "G", "addB", "addB");
        await AppendOverSocketAsync(phone, Wire(w, "rmB"));

        await other.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloseCodes.Removed, (int?)other.Relay.CloseStatus);
        var refused = await Assert.ThrowsAsync<RelayRefusedException>(() => relay.ConnectAsync(token, "app-b", "app", KeyOf("B")));
        Assert.Equal(CloseCodes.Removed, refused.Code);
    }

    // A failed read after a roster change must not un-pend connected devices; a device connecting then is turned away.
    [Fact]
    public async Task A_store_error_after_an_append_leaves_pending_devices_pending_and_turns_new_connections_away()
    {
        var w = Scenario();
        var store = new FlakyStore(new MemoryRosterStore());
        await using var relay = await TestRelay.StartAsync(o => { o.Roster.DevicesStore = store; o.TokenKey = Convert.ToBase64String(new byte[32]); });
        var devices = relay.App.Services.GetRequiredService<DeviceRoster>();
        await devices.AppendAsync(Account, [Wire(w, "G")]);
        var token = relay.TokenFor(1, "octo");
        var phone = await relay.ConnectAsync(token, "phone-g", "phone", KeyOf("G"));
        Assert.True(await phone.Relay.EnforceRosterAsync());
        var stranger = await relay.ConnectAsync(token, "app-x", "app");
        await Task.Delay(500);
        Assert.Empty(stranger.Relay.Presence);

        store.BreakAfterWrite = true;
        await AppendOverSocketAsync(phone, Wire(w, "addB"));   // writes, then every read fails
        await Task.Delay(1000);

        Assert.Empty(stranger.Relay.Presence);                 // still pending: the failed re-tag changed nothing
        var refused = await Assert.ThrowsAsync<RelayRefusedException>(() => relay.ConnectAsync(token, "app-y", "app"));
        Assert.Equal(CloseCodes.Restarting, refused.Code);
    }
}
