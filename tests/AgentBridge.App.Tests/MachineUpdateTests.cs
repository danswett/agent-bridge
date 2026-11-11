using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Another machine's updates in the app (#51): its host's update.* followed over the connection,
// listed on the Sessions page with Check and Install, and Check all. The other machine's host is a
// FakeHost here; what it does with a press is UpdateFlowTests', and what its app does is
// AutoUpdaterTests'.
[Trait("Category", "Network")]
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
    public async Task A_machine_whose_update_needs_you_says_so_in_any_phase()
    {
        var row = await ListedAsync();
        const string needsYou = "Needs you: the update to 0.5.0 has waited since 11/2/2026 9:00 AM because it is downloaded and waits for Restart to update on this machine";

        await remoteHost.SendAsync(UpdateProtocol.Event, Found with { Waiting = needsYou });
        await Wait.UntilAsync(() => row.StatusText == needsYou + ".", "Needs you while idle");
        Assert.True(TestUi.Read(() => row.CanInstall));

        await remoteHost.SendAsync(UpdateProtocol.Event, Found with { Phase = UpdatePhase.Checking, Waiting = needsYou });
        await Wait.UntilAsync(() => row.StatusText == needsYou + ".", "Needs you while it checks");
    }

    // Creative on #719 (6674096426): an update the backstop holds back for a question waiting reads
    // exactly as the host words it.
    [Fact]
    public async Task A_machine_whose_update_waits_for_a_question_to_be_answered_says_so_in_the_hosts_words()
    {
        var row = await ListedAsync();

        await remoteHost.SendAsync(UpdateProtocol.Event, Found with { Phase = UpdatePhase.Waiting, Automatic = true, Waiting = AutoUpdater.QuestionLine });

        await Wait.UntilAsync(() => row.StatusText == "Update ready \u00b7 waiting for a question to be answered", "the line as the host words it");
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
    public async Task Update_now_preparation_distinguishes_quiet_busy_and_unreadable_machines()
    {
        var row = await ListedAsync();
        Assert.False(row.CanUpdateNow);
        Assert.Null(row.PrepareNow());
        await NowState(Found with { CanUpdateNow = true, Busy = new() });
        var quiet = Assert.IsType<MachineUpdateNowPrompt>(row.PrepareNow());
        Assert.Null(quiet.Sheet);
        var choice = Assert.IsType<MachineUpdateNowChoice>(row.TakeNow(quiet));
        Assert.Equal("0.5.0", choice.Release);
        Assert.Null(choice.Accept);
        Assert.Null(row.TakeNow(quiet));

        await remoteHost.SendAsync(Events.SessionUpsert, Make.Session("approval", title: "Review update"));
        await Wait.UntilAsync(() => remote.Sessions.Get("approval") is not null, "the named session");
        await NowState(Found with
        {
            CanUpdateNow = true,
            Busy = new() { Working = 1, Waiting = 1, Sessions = [new("missing", UpdateBusyState.Working), new("approval", UpdateBusyState.Approval)] },
        });
        var busy = Assert.IsType<MachineUpdateNowPrompt>(row.PrepareNow());
        Assert.Equal("Update Desk PC now?", busy.Sheet!.Title);
        Assert.Equal(["Approval waiting", "Mid-turn"], busy.Sheet.Sessions.Select(s => s.State));
        Assert.Equal(["Review update", UpdateNowWords.Unnamed], busy.Sheet.Sessions.Select(s => s.Name));
        Assert.Equal(UpdateNowWords.WarnLine, busy.Sheet.Warn);
        Assert.Equal(2, row.TakeNow(busy, confirmed: true)!.Accept!.Sessions!.Count);

        await NowState(Found with { CanUpdateNow = true });
        var unknown = Assert.IsType<MachineUpdateNowPrompt>(row.PrepareNow());
        Assert.Equal(UpdateNowWords.UnknownLine, unknown.Sheet!.Unknown);
        Assert.True(row.TakeNow(unknown, confirmed: true)!.Accept!.Unknown);
        Assert.Empty(remoteHost.Sent(UpdateProtocol.Install));
    }

    [Fact]
    public async Task A_confirm_answer_reprepares_the_dialog_and_invalidates_the_old_snapshot()
    {
        var row = await ListedAsync();
        await NowState(Found with { CanUpdateNow = true, Busy = new() });
        var first = row.PrepareNow()!;
        var changed = Found with { CanUpdateNow = true, Busy = new() { Waiting = 1, Sessions = [new("question", UpdateBusyState.Question)] } };
        var again = Assert.IsType<MachineUpdateNowPrompt>(row.PrepareNow(new UpdateCommandResult(UpdateAck.Confirm, "untrusted machine text") { State = changed }));
        Assert.True(first.Cancelled);
        Assert.Null(row.TakeNow(first, confirmed: true));
        Assert.Equal("Question waiting", Assert.Single(again.Sheet!.Sessions).State);
        Assert.Equal(UpdateNowWords.PauseLine, again.Sheet.Pause);
        Assert.Equal("question", Assert.Single(row.TakeNow(again, confirmed: true)!.Accept!.Sessions!).Id);
        Assert.Empty(remoteHost.Sent(UpdateProtocol.Install));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_and_stale_or_removed_targets_never_produce_an_update_choice(bool signOut)
    {
        var row = await ListedAsync();
        await NowState(Found with { CanUpdateNow = true });
        var cancel = row.PrepareNow()!;
        Assert.Null(row.TakeNow(cancel)); // No explicit primary-button choice, including Enter or Escape.
        Assert.True(cancel.Cancelled);
        var stale = row.PrepareNow()!;
        await NowState(Found with { CanUpdateNow = true, Available = "0.6.0" });
        Assert.True(stale.Cancelled);
        Assert.Null(row.TakeNow(stale, confirmed: true));
        var removed = row.PrepareNow()!;
        if (signOut) { main.Remove(row.Key); }
        else
        {
            await remoteHost.StopAsync();
            await Wait.UntilAsync(() => removed.Cancelled, "the disconnected machine's dialog cancelled");
        }
        Assert.True(removed.Cancelled);
        Assert.Null(row.TakeNow(removed, confirmed: true));
        Assert.Null(row.PrepareNow());
        Assert.Empty(remoteHost.Sent(UpdateProtocol.Install));
    }

    async Task NowState(UpdateState state)
    {
        await remoteHost.SendAsync(UpdateProtocol.Event, state);
        await remote.ReadAsync<object>(FakeHost.Barrier);
        await Wait.UntilAsync(() => remote.Update?.Available == state.Available && remote.Update?.CanUpdateNow == state.CanUpdateNow
            && (remote.Update?.Busy is null) == (state.Busy is null)
            && remote.Update?.Busy?.Working == state.Busy?.Working, "the Update now state");
    }

    void TickRows() => TestUi.Read(() => { for (var i = 0; i < 5; i++) { main.Tick(); } return true; });
    string RemoteStatus() => TestUi.Read(() => main.MachineRows.Single(r => r.Key == "remote-id").Status);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_Update_now_reads_restarting_until_90_seconds_after_its_channel_closes(bool now)
    {
        await ListedAsync();
        await remoteHost.SendAsync(UpdateProtocol.Event, Found with { Phase = UpdatePhase.Installing, RequestNow = now });
        await Wait.UntilAsync(() => remote.Update?.Phase == UpdatePhase.Installing, "the installing state");
        Assert.Equal(now ? UpdateNowWords.Restarting : "Online", RemoteStatus());
        clock.Advance(TimeSpan.FromMinutes(2)); // The grace starts at departure, not at Installing.
        TickRows();

        await remoteHost.StopAsync();
        await Wait.UntilAsync(() => remote.State != LinkState.Connected, "the channel closed");
        TickRows();
        clock.Advance(TimeSpan.FromSeconds(89));
        TickRows();
        Assert.Equal(now ? UpdateNowWords.Restarting : "Connecting", RemoteStatus());
        clock.Advance(TimeSpan.FromSeconds(1));
        TickRows();
        Assert.Equal("Connecting", RemoteStatus());
    }

    [Fact]
    public async Task A_machine_returning_inside_the_restart_window_clears_it_before_an_unrelated_drop()
    {
        await ListedAsync();
        await remoteHost.SendAsync(UpdateProtocol.Event, Found with { Phase = UpdatePhase.Installing, RequestNow = true });
        await Wait.UntilAsync(() => remote.Update?.Phase == UpdatePhase.Installing, "installing");
        Assert.Equal(UpdateNowWords.Restarting, RemoteStatus());
        await remoteHost.StopAsync();
        await Wait.UntilAsync(() => remote.State != LinkState.Connected, "the channel closed");
        clock.Advance(TimeSpan.FromSeconds(30));
        remoteHost.Start();
        await Wait.UntilAsync(() => remote.State == LinkState.Connected && RemoteStatus() == "Online", "the machine returned");

        await remoteHost.StopAsync();
        await Wait.UntilAsync(() => remote.State != LinkState.Connected, "an unrelated drop");
        Assert.Equal("Connecting", RemoteStatus());
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

    [Fact]
    public async Task A_machines_update_sits_inside_its_own_row_on_the_machines_page_and_this_machines_row_has_its_own_check()
    {
        var update = await ListedAsync();

        var rows = TestUi.Read(() => main.MachineRows.ToArray());
        var desk = rows.Single(r => r.Name == "Desk PC");
        Assert.Same(update, desk.Update);
        Assert.True(desk.HasUpdate);
        Assert.False(desk.CanCheckHere);
        var here = rows.Single(r => r.Key == Machine.LocalKey);
        Assert.Null(here.Update);
        Assert.True(here.CanCheckHere);

        // Gone from the machine, gone from its row.
        remoteHost.Capabilities.Remove(UpdateProtocol.Capability);
        await Wait.UntilAsync(() => TestUi.Read(() => main.MachineRows.Single(r => r.Name == "Desk PC").Update) is null, "the update gone from the row");
    }
}