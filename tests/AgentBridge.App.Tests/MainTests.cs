using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The window's list of cards, over a host on a real pipe: its order, holding still while
// someone types, toasts only for news, and a toast's session opened once it arrives.
public sealed class MainTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly FakeTimeProvider clock = new(Make.T0);
    readonly List<ToastNotice> shown = [];
    // The tests' UI thread: the host's events and the timer's ticks take turns on it.
    readonly LockedDispatcher ui = new();
    HostConnection connection = null!;
    MainViewModel main = null!;
    int syncs;

    sealed class Sink(List<ToastNotice> shown) : IToastSink
    {
        public void Show(ToastNotice notice) { lock (shown) { shown.Add(notice); } }
        public void Withdraw(string tag) { }
    }

    public Task InitializeAsync()
    {
        host.Sessions.Add(Make.Session("old", response: "Earlier.", updated: Make.T0));
        host.Sessions.Add(Make.Session("asking", question: Make.Choice(), updated: Make.T0.AddMinutes(1)));
        host.Start();
        // Started last, as App.xaml.cs does: MainViewModel builds its cards from the upserts it
        // hears, so one constructed after the snapshot arrived had no cards, and a counter
        // attached late missed sessions.synced ("timed out waiting for the snapshot").
        connection = host.Connect(ui, start: false);
        connection.Sessions.Synced += () => Interlocked.Increment(ref syncs);
        var deliveries = new Deliveries(connection, ui);
        var planner = new ToastPlanner(new Sink(shown), clock) { Capabilities = () => connection.Capabilities };
        main = new MainViewModel(connection, new CardContext { Deliveries = deliveries, Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), toasts: planner);
        connection.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
    }

    string[] Order() => [.. main.Sessions.Select(c => c.Id)];

    // Read in full, sessions.synced included, and not just its cards: until then a session asked
    // for that is not in the snapshot is taken to have ended and is forgotten, and upserts are
    // the baseline rather than news. Waiting only for the two cards let a test act in the gap
    // between the last card and sessions.synced, and the session it asked for never opened.
    Task SnapshotAsync() => Wait.UntilAsync(() => main.Sessions.Count == 2 && Volatile.Read(ref syncs) > 0, "the snapshot");

    [Fact]
    public async Task Cards_are_most_recently_updated_first_and_move_up_when_their_session_changes()
    {
        await SnapshotAsync();
        Assert.Equal(["asking", "old"], Order());

        await host.SendAsync(Events.SessionUpsert, Make.Session("old", status: SessionStatus.Working, updated: Make.T0.AddMinutes(2)));

        await Wait.UntilAsync(() => Order()[0] == "old", "the updated card to move up");
    }

    [Fact]
    public async Task What_holds_the_hosts_discovery_is_shown_until_it_clears()
    {
        await SnapshotAsync();
        Assert.False(main.IsHeld);

        await host.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged(new DiscoveryHeld("an unrecognised copilot process (pid 4120)", [AgentKind.Copilot])));
        await Wait.UntilAsync(() => main.IsHeld, "the hold");
        Assert.StartsWith("Discovery held: an unrecognised copilot process (pid 4120).", main.HeldWhy);

        await host.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged());
        await Wait.UntilAsync(() => !main.IsHeld, "the hold to clear");

        // A host already held when the app connects says so with its snapshot.
        host.Held = new DiscoveryHeld("the claude processes could not be listed", [AgentKind.Claude]);
        await host.StopAsync();
        host.Start();
        await Wait.UntilAsync(() => main.HeldWhy?.Contains("the claude processes could not be listed", StringComparison.Ordinal) == true, "the hold from the snapshot");
    }

    [Fact]
    public async Task No_card_moves_while_someone_is_typing_in_any_card()
    {
        await SnapshotAsync();
        main.Sessions.Single(c => c.Id == "asking").Editing = true;

        await host.SendAsync(Events.SessionUpsert, Make.Session("old", status: SessionStatus.Working, updated: Make.T0.AddMinutes(2)));
        await Wait.UntilAsync(() => main.Sessions.Single(c => c.Id == "old").Session.Status == SessionStatus.Working, "the update");

        Assert.Equal(["asking", "old"], Order());
    }

    [Fact]
    public async Task A_question_pending_when_the_app_connects_is_toasted_but_a_turn_finished_before_is_not()
    {
        await SnapshotAsync();
        await Wait.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            ui.Post(main.Tick);
            lock (shown) { return shown.Count == 1; }
        }, "the question's toast");

        clock.Advance(TimeSpan.FromSeconds(5));
        ui.Post(main.Tick);
        var toast = Assert.Single(shown);
        Assert.Equal(ToastPlanner.Tag("asking"), toast.Tag);

        await host.SendAsync(Events.SessionUpsert, Make.Session("old", status: SessionStatus.Working, updated: Make.T0.AddMinutes(2)));
        await host.SendAsync(Events.SessionUpsert, Make.Session("old", status: SessionStatus.Idle, response: "Done.", updated: Make.T0.AddMinutes(3), turns: 2));
        // A second at a time: the finished turn is due three seconds after the planner hears of
        // it, which is a moment after the card does.
        await Wait.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            ui.Post(main.Tick);
            lock (shown) { return shown.Count == 2; }
        }, "the finished toast");
        Assert.Contains("finished", shown[1].Xml);
    }

    [Fact]
    public async Task A_session_asked_for_before_it_arrives_opens_when_it_does()
    {
        var opened = new List<string>();
        main.OpenRequested += c => opened.Add(c.Id);
        await SnapshotAsync();
        main.Sessions.Single(c => c.Id == "old").Folded = true;

        // On the UI thread, as a toast click or the tray asks.
        ui.Post(() => main.Open("later"));
        Assert.Empty(opened);
        await host.SendAsync(Events.SessionUpsert, Make.Session("later", updated: Make.T0.AddMinutes(5)));
        await Wait.UntilAsync(() => opened.Count == 1, "the session to open");
        Assert.Equal(["later"], opened);

        ui.Post(() => main.Open("old"));
        Assert.Equal(["later", "old"], opened);
        Assert.False(main.Sessions.Single(c => c.Id == "old").Folded);
    }
}
