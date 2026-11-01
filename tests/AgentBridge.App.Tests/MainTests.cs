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

    string[] Order() => TestUi.Read<string[]>(() => [.. main.Sessions.Select(c => c.Id)]);

    // Read in full, sessions.synced included, and not just its cards: until then a session asked
    // for that is not in the snapshot is taken to have ended and is forgotten, and upserts are
    // the baseline rather than news. Waiting only for the two cards let a test act in the gap
    // between the last card and sessions.synced, and the session it asked for never opened.
    Task SnapshotAsync() => Wait.UntilAsync(() => main.Sessions.Count == 2 && Volatile.Read(ref syncs) > 0, "the snapshot");

    [Fact]
    public async Task Upserts_never_move_a_card_in_your_order_while_three_sessions_update_in_turn_and_a_new_one_lands_at_the_top_once()
    {
        await SnapshotAsync();
        Assert.Equal(["asking", "old"], Order());

        await host.SendAsync(Events.SessionUpsert, Make.Session("third", updated: Make.T0.AddMinutes(2)));
        await Wait.UntilAsync(() => main.Sessions.Any(c => c.Id == "third"), "the new card");
        Assert.Equal(["third", "asking", "old"], Order());

        // Each update is waited for on its own card, never by polling the order (#101).
        var turn = 0;
        foreach (var id in new[] { "old", "asking", "third", "old" })
        {
            var response = $"Update {++turn}";
            await host.SendAsync(Events.SessionUpsert, Make.Session(id, status: SessionStatus.Working, response: response, updated: Make.T0.AddMinutes(10 + turn)));
            await Wait.UntilAsync(() => main.Sessions.Single(c => c.Id == id).Response == response, $"{id}'s update");
            Assert.Equal(["third", "asking", "old"], Order());
        }
    }

    [Fact]
    public async Task In_most_recent_an_updated_card_moves_up()
    {
        await SnapshotAsync();
        ui.Post(() => main.SortIndex = (int)SessionSort.Recent);

        await host.SendAsync(Events.SessionUpsert, Make.Session("old", status: SessionStatus.Working, updated: Make.T0.AddMinutes(2)));

        await Wait.UntilAsync(() => main.Sessions.Single(c => c.Id == "old").Session.Status == SessionStatus.Working, "the update");
        Assert.Equal(["old", "asking"], Order());
    }

    [Fact]
    public async Task The_menu_and_keyboard_move_and_pin_cards_and_pinned_cards_sit_under_their_own_heading()
    {
        await SnapshotAsync();
        var old = Card("old");
        var asking = Card("asking");

        ui.Post(() => Assert.False(asking.Row!.CanMoveUp));
        ui.Post(() => Assert.True(old.Row!.MoveUp()));
        Assert.Equal(["old", "asking"], Order());

        ui.Post(() => asking.Row!.TogglePin());
        Assert.Equal(["asking", "old"], Order());
        TestUi.Read(() =>
        {
            Assert.True(asking.Pinned);
            Assert.Equal("Pinned", asking.GroupHeader);
            Assert.Equal("Sessions", old.GroupHeader);
            // Alone in its group, a pinned card can't move.
            Assert.False(asking.Row!.CanMoveDown);
            return true;
        });

        ui.Post(() => asking.Row!.TogglePin());
        Assert.Equal(["asking", "old"], Order());
        TestUi.Read(() =>
        {
            Assert.False(asking.Pinned);
            Assert.Null(asking.GroupHeader);
            Assert.Null(old.GroupHeader);
            return true;
        });

        // A drag of the top card onto the foot of the other.
        ui.Post(() => Assert.True(old.Row!.Drop(asking.Row!.Key, after: true)));
        Assert.Equal(["old", "asking"], Order());
        // The dragged card takes focus, as after a keyboard move.
        Assert.True(TestUi.Read(() => asking.WantsFocus));
    }

    [Fact]
    public async Task A_folded_card_gets_the_new_activity_dot_when_its_session_updates_and_opening_it_clears_the_dot()
    {
        await SnapshotAsync();
        var old = Card("old");
        ui.Post(() => old.Folded = true);

        await host.SendAsync(Events.SessionUpsert, Make.Session("old", response: "Later.", updated: Make.T0.AddMinutes(5)));
        await Wait.UntilAsync(() => old.Response == "Later.", "the update");
        Assert.True(TestUi.Read(() => old.Unread));

        // An open card's news is seen as it comes.
        await host.SendAsync(Events.SessionUpsert, Make.Session("asking", question: Make.Choice(), response: "Still asking.", updated: Make.T0.AddMinutes(6)));
        await Wait.UntilAsync(() => Card("asking").Response == "Still asking.", "the other update");
        Assert.False(TestUi.Read(() => Card("asking").Unread));

        ui.Post(() => old.Folded = false);
        Assert.False(TestUi.Read(() => old.Unread));
    }

    [Fact]
    public async Task A_session_its_host_removes_folds_into_ended_and_the_others_keep_their_places()
    {
        await SnapshotAsync();
        Assert.False(TestUi.Read(() => main.HasEnded));

        await host.SendAsync(Events.SessionRemove, new SessionRemoved("asking"));
        await Wait.UntilAsync(() => main.HasEnded, "the ended group");

        Assert.Equal(["old"], Order());
        TestUi.Read(() =>
        {
            Assert.Equal("Ended · 1", main.EndedHeader);
            var row = Assert.Single(main.Ended);
            Assert.Equal("A session", row.Title);
            Assert.StartsWith("ended ", row.Meta, StringComparison.Ordinal);
            return true;
        });
    }

    SessionCardViewModel Card(string id) => TestUi.Read(() => main.Sessions.Single(c => c.Id == id));

    [Fact]
    public async Task A_move_the_person_makes_while_typing_in_a_card_still_moves_it()
    {
        await SnapshotAsync();
        var old = Card("old");
        TestUi.Read(() => Card("asking").Editing = true);

        ui.Post(() => Assert.True(old.Row!.MoveUp()));

        Assert.Equal(["old", "asking"], Order());
    }

    [Fact]
    public async Task Headings_follow_the_cards_on_screen_while_an_update_is_held_for_someone_typing()
    {
        await SnapshotAsync();
        await host.SendAsync(Events.SessionUpsert, Make.Session("third", updated: Make.T0.AddMinutes(2)));
        await Wait.UntilAsync(() => main.Sessions.Any(c => c.Id == "third"), "the new card");
        ui.Post(() => main.SortIndex = (int)SessionSort.Recent);
        ui.Post(() => Card("asking").Row!.TogglePin());
        ui.Post(() => Card("old").Row!.TogglePin());
        Assert.Equal(["asking", "old", "third"], Order());
        TestUi.Read(() => Card("third").Editing = true);

        // Most recent would put "old" first among the pinned, but someone is typing.
        await host.SendAsync(Events.SessionUpsert, Make.Session("old", response: "Later.", updated: Make.T0.AddMinutes(9)));
        await Wait.UntilAsync(() => Card("old").Response == "Later.", "the update");

        Assert.Equal(["asking", "old", "third"], Order());
        TestUi.Read(() =>
        {
            Assert.Equal("Pinned", Card("asking").GroupHeader);
            Assert.Null(Card("old").GroupHeader);
            Assert.Equal("Sessions", Card("third").GroupHeader);
            return true;
        });
    }

    [Fact]
    public async Task A_seen_mark_that_cannot_be_written_at_the_next_tick_turns_the_list_to_most_recent()
    {
        await SnapshotAsync();
        var folder = Path.Combine(Path.GetTempPath(), "abo-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(folder, "order.json");
        try
        {
            var cards = new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection };
            var other = TestUi.Read(() => new MainViewModel(connection, cards, new UsageViewModel(), order: new SessionOrder(file)));
            var told = 0;
            other.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.SortIndex)) { told++; } };
            File.Delete(file);
            Directory.CreateDirectory(file);

            // An open card's update is only marked seen: nothing is written, nothing fails yet.
            await host.SendAsync(Events.SessionUpsert, Make.Session("old", response: "Later.", updated: Make.T0.AddMinutes(9)));
            await Wait.UntilAsync(() => other.Sessions.Single(c => c.Id == "old").Response == "Later.", "the update");
            Assert.Equal((int)SessionSort.Yours, TestUi.Read(() => other.SortIndex));
            Assert.Equal(["asking", "old"], TestUi.Read<string[]>(() => [.. other.Sessions.Select(c => c.Id)]));

            for (var i = 0; i < 5; i++) { ui.Post(other.Tick); }

            TestUi.Read(() =>
            {
                Assert.Equal((int)SessionSort.Recent, other.SortIndex);
                Assert.True(told > 0);
                Assert.Equal(["old", "asking"], other.Sessions.Select(c => c.Id));
                return true;
            });
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task The_sort_picker_says_most_recent_once_the_order_can_no_longer_be_saved()
    {
        await SnapshotAsync();
        var folder = Path.Combine(Path.GetTempPath(), "abo-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(folder, "order.json");
        try
        {
            var cards = new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection };
            var other = TestUi.Read(() => new MainViewModel(connection, cards, new UsageViewModel(), order: new SessionOrder(file)));
            Assert.Equal((int)SessionSort.Yours, TestUi.Read(() => other.SortIndex));
            var told = 0;
            other.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.SortIndex)) { told++; } };

            // The file's place taken by a folder: the next save fails. A pin saves at once.
            File.Delete(file);
            Directory.CreateDirectory(file);
            ui.Post(() => other.Sessions.Single(c => c.Id == "old").Row!.TogglePin());

            TestUi.Read(() =>
            {
                Assert.Equal((int)SessionSort.Recent, other.SortIndex);
                Assert.True(told > 0);
                return true;
            });
        }
        finally { Directory.Delete(folder, recursive: true); }
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
        // Most recent: the only order in which an update moves a card.
        ui.Post(() => main.SortIndex = (int)SessionSort.Recent);
        TestUi.Read(() => main.Sessions.Single(c => c.Id == "asking").Editing = true);

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
