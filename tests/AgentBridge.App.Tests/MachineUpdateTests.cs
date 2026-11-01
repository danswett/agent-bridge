using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Another machine's updates in the app (#51): its host's update.* followed over the connection,
// listed on the Sessions page with Check and Install, and Check all. The other machine's host is a
// FakeHost here; what it does with a press is UpdateFlowTests', and what its app does is
// AutoUpdaterTests'.
public sealed class MachineUpdateTests : IAsyncLifetime
{
    readonly FakeHost localHost = new();
    readonly FakeHost remoteHost = new();
    readonly LockedDispatcher ui = new();
    readonly FakeTimeProvider clock = new(Make.T0);
    HostConnection local = null!;
    HostConnection remote = null!;
    MainViewModel main = null!;
    int localChecks;

    static readonly UpdateState Found = new()
    {
        Current = "0.4.0",
        Installer = true,
        Available = "0.5.0",
        CheckedAt = Make.T0,
        Automatic = false,
    };

    public Task InitializeAsync()
    {
        remoteHost.Capabilities.Add(UpdateProtocol.Capability);
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Desk PC", "windows", "0.4.0"), [.. remoteHost.Capabilities]));
        remoteHost.On(UpdateProtocol.Subscribe, _ => Found);
        remoteHost.On(UpdateProtocol.Get, _ => Found);
        localHost.Start();
        remoteHost.Start();
        local = localHost.Connect(ui);
        remote = new HostConnection(ct => LocalClient.ConnectAsync(remoteHost.Endpoint, TimeSpan.FromSeconds(1), ct), ui)
        {
            RetryDelay = TimeSpan.FromMilliseconds(100),
            CapabilityRefresh = TimeSpan.FromMilliseconds(200),
        };
        remote.Start();
        main = new MainViewModel(Build(Machine.LocalKey, local), new UsageViewModel(clock)) { Time = clock, CheckLocal = () => localChecks++ };
        return Task.CompletedTask;
    }

    Machine Build(string key, HostConnection host) => new(key, host, new CardContext
    {
        Deliveries = new Deliveries(host, ui, clock),
        Folds = new FoldStore(null),
        Capabilities = () => host.Capabilities,
        Host = host,
        Time = clock,
    });

    public async Task DisposeAsync()
    {
        await local.DisposeAsync();
        await remote.DisposeAsync();
        await localHost.DisposeAsync();
        await remoteHost.DisposeAsync();
    }

    async Task<MachineUpdateViewModel> ListedAsync()
    {
        TestUi.Read(() => { main.Add(Build("remote-id", remote)); return 0; });
        await Wait.UntilAsync(() => main.MachineUpdates.Count == 1 && main.MachineUpdates[0].Name == "Desk PC", "the other machine's updates listed");
        return TestUi.Read(() => main.MachineUpdates[0]);
    }

    [Fact]
    public async Task Another_machine_offering_updates_is_listed_with_what_its_app_reports_and_follows_each_change()
    {
        var row = await ListedAsync();
        Assert.Equal("0.5.0 is available (running 0.4.0).", TestUi.Read(() => row.StatusText));
        Assert.True(TestUi.Read(() => row.CanInstall));
        // This machine's own updates are in Settings, not listed here.
        Assert.DoesNotContain(TestUi.Read(() => main.MachineUpdates.ToList()), u => u.Key == Machine.LocalKey);

        await remoteHost.SendAsync(UpdateProtocol.Event, Found with { Phase = UpdatePhase.Downloading, Progress = 40 });
        await Wait.UntilAsync(() => row.StatusText == "Downloading 0.5.0 (40%)\u2026", "the download's progress");
        Assert.False(TestUi.Read(() => row.CanInstall));

        await remoteHost.SendAsync(UpdateProtocol.Event, Found with
        {
            Current = "0.5.0",
            Available = null,
            Last = new UpdateResult { Outcome = UpdateOutcome.Updated, From = "0.4.0", To = "0.5.0", At = Make.T0 },
        });
        await Wait.UntilAsync(() => row.LastText == "Updated from 0.4.0 to 0.5.0 just now.", "the outcome");
        Assert.StartsWith("Up to date: 0.5.0", TestUi.Read(() => row.StatusText));
    }

    [Fact]
    public async Task A_machine_whose_app_has_not_checked_yet_is_not_called_up_to_date()
    {
        var row = await ListedAsync();

        // Automatic updates off there, and no check yet (or the first one failed).
        await remoteHost.SendAsync(UpdateProtocol.Event, Found with { Available = null, CheckedAt = null });

        await Wait.UntilAsync(() => row.StatusText == "Running 0.4.0; not checked for updates yet.", "the unchecked state");
        Assert.True(TestUi.Read(() => row.CanCheck));
    }

    [Fact]
    public async Task A_gap_after_updates_were_offered_on_a_refresh_reads_them_again()
    {
        await using var other = new FakeHost();
        other.Capabilities.Remove(UpdateProtocol.Capability);
        other.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("other-id", "Other PC", "windows", "0.4.0"), [.. other.Capabilities]));
        other.On(UpdateProtocol.Subscribe, _ => Found);
        other.Start();
        await using var conn = new HostConnection(ct => LocalClient.ConnectAsync(other.Endpoint, TimeSpan.FromSeconds(1), ct), ui)
        {
            RetryDelay = TimeSpan.FromMilliseconds(100),
            CapabilityRefresh = TimeSpan.FromMilliseconds(200),
        };
        conn.Start();
        await Wait.UntilAsync(() => conn.State == LinkState.Connected && conn.Sessions.Complete, "the connection");
        Assert.Null(TestUi.Read(() => conn.Update));

        // agent-ha-bridge stopped there: the refresh follows its updates.
        other.Capabilities.Add(UpdateProtocol.Capability);
        await Wait.UntilAsync(() => conn.Update is not null, "updates followed from the refresh");

        // Events lost, a progress among them: read again, though the first hello offered none.
        other.On(UpdateProtocol.Subscribe, _ => Found with { Phase = UpdatePhase.Downloading, Progress = 70 });
        foreach (var ch in other.Channels.OfType<LocalChannel>().Where(c => c.IsOpen).Distinct()) { ch.SkipEventSeq(3); }
        await other.SendAsync(Events.SessionUpsert, Make.Session("s2"));

        await Wait.UntilAsync(() => conn.Update?.Progress == 70, "the update state read again");
    }

    [Fact]
    public async Task Install_sends_the_machine_a_press_of_its_own_and_shows_why_it_was_refused()
    {
        remoteHost.On(UpdateProtocol.Install, _ => new UpdateCommandResult(UpdateAck.Refused, "only a person's phone, browser or Windows app can ask a machine to update"));
        var row = await ListedAsync();

        await row.AskAsync(UpdateProtocol.Install);

        var sent = Assert.Single(remoteHost.Sent(UpdateProtocol.Install));
        Assert.True(Guid.TryParse(sent.GetProperty("request").GetString(), out _));
        Assert.Equal("Only a person's phone, browser or Windows app can ask a machine to update.", TestUi.Read(() => row.Note));
        Assert.True(TestUi.Read(() => row.CanInstall));
    }

    [Fact]
    public async Task A_machine_that_stops_offering_updates_leaves_the_list()
    {
        await ListedAsync();

        // agent-ha-bridge started there, or delivery.readOnly was set: hello offers no update.*.
        remoteHost.Capabilities.Remove(UpdateProtocol.Capability);

        await Wait.UntilAsync(() => main.MachineUpdates.Count == 0, "the machine gone from the list");
        Assert.False(TestUi.Read(() => main.ShowMachineUpdates));
    }

    [Fact]
    public async Task Check_all_checks_every_machine_listed_and_this_one()
    {
        remoteHost.On(UpdateProtocol.Check, _ => new UpdateCommandResult(UpdateAck.Accepted) { State = Found });
        await ListedAsync();

        TestUi.Read(() => { main.CheckAll(); return 0; });

        await Wait.UntilAsync(() => remoteHost.Sent(UpdateProtocol.Check).Count == 1, "the other machine asked to check");
        Assert.Equal(1, localChecks);
    }
}
