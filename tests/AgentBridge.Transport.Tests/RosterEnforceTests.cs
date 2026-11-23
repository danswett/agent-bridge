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
        await roster.AppendAsync(Account, [Wire(w, "G"), Wire(w, "addB")]);
        Assert.Equal(DeviceRoster.Admission.Open, await roster.AdmissionAsync(Account, w.Id("X")));
        Assert.Equal(DeviceRoster.Observe, (await roster.GetAsync(Account))!.Mode);

        Assert.False(await roster.EnforceAsync(Account, w.Id("X")));  // not in the roster
        Assert.Equal(DeviceRoster.Observe, (await roster.GetAsync(Account))!.Mode);
        Assert.True(await roster.EnforceAsync(Account, w.Id("G")));
        Assert.True(await roster.EnforceAsync(Account, w.Id("G")));   // again: still enforce
        Assert.Equal(DeviceRoster.Enforce, (await roster.GetAsync(Account))!.Mode);

        // More entries do not move it back.
        w.Remove("rmB", "G", "addB", "addB");
        await roster.AppendAsync(Account, [Wire(w, "rmB")]);
        Assert.Equal(DeviceRoster.Enforce, (await roster.GetAsync(Account))!.Mode);
    }

    [Fact]
    public async Task No_roster_means_nobody_can_switch_enforce_on()
    {
        var w = Scenario();
        var roster = Make(w, new MemoryRosterStore());
        Assert.False(await roster.EnforceAsync(Account, w.Id("G")));
        Assert.Equal(DeviceRoster.Admission.Open, await roster.AdmissionAsync(Account, w.Id("G")));
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

    [Fact]
    public async Task A_store_that_cannot_be_read_leaves_a_device_open_because_the_relay_is_never_the_gate()
    {
        var w = Scenario();
        var roster = Make(w, new BrokenStore());
        Assert.Equal(DeviceRoster.Admission.Open, await roster.AdmissionAsync(Account, w.Id("G")));
    }

    sealed class BrokenStore : IRosterStore
    {
        public Task<StoredRoster?> ReadAsync(string name, CancellationToken ct) => throw new IOException("down");
        public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct) => throw new IOException("down");
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => throw new IOException("down");
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
}
