using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// The Sessions page's panes by window width (ui.md §5.2, #180): three panes from 1280, two
// with tabs from 760, one column below, at the PWA's breakpoints, and the session shown
// surviving every resize.
public sealed class PaneLayoutTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly LockedDispatcher ui = new();
    HostConnection connection = null!;
    MainViewModel main = null!;
    PaneLayoutViewModel layout = null!;
    readonly System.Collections.Concurrent.ConcurrentQueue<string> fetched = new();

    public Task InitializeAsync()
    {
        host.Sessions.Add(Make.Session("quiet", updated: Make.T0));
        host.Sessions.Add(Make.Session("busy", status: SessionStatus.Working, background: new Background { Agents = 2, Shells = 1 }, updated: Make.T0.AddMinutes(1)));
        host.Start();
        connection = host.Connect(ui, start: false);
        main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection, FetchDetail = id => { fetched.Enqueue(id); return Task.FromResult<Session?>(null); } }, new UsageViewModel());
        layout = new PaneLayoutViewModel(main);
        connection.Start();
        return Wait.UntilAsync(() => main.Sessions.Count == 2, "the sessions");
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
    }

    SessionCardViewModel Card(string id) => TestUi.Read(() => main.Sessions.Single(c => c.Id == id));
    void Select(string? id) => ui.Post(() => main.Selected = id is null ? null : main.Sessions.Single(c => c.Id == id));
    T Read<T>(Func<PaneLayoutViewModel, T> read) => TestUi.Read(() => read(layout));
    void Resize(double width) => ui.Post(() => layout.Width = width);

    [Theory]
    [InlineData(1920, PaneMode.Three)]
    [InlineData(1280, PaneMode.Three)]
    [InlineData(1279.5, PaneMode.Two)]
    [InlineData(1000, PaneMode.Two)]
    [InlineData(760, PaneMode.Two)]
    [InlineData(759.5, PaneMode.One)]
    [InlineData(400, PaneMode.One)]
    [InlineData(360, PaneMode.One)]
    public void The_window_width_picks_three_panes_from_1280_two_from_760_and_one_column_below(double width, PaneMode mode)
    {
        Assert.Equal(mode, PaneLayoutViewModel.ModeFor(width));
        Assert.Equal((1280, 760), (DesignLayout.ThreePane, DesignLayout.TwoPane));
    }

    [Fact]
    public void Three_panes_show_the_list_the_session_and_the_inspector_which_can_be_hidden_and_shown_again()
    {
        Resize(1440);
        Assert.True(Read(l => l.ShowList && l.ShowEmpty && !l.ShowInspector && !l.CanToggleInspector));

        Select("busy");
        Assert.True(Read(l => l.ShowList && l.ShowSession && l.ShowConversation && l.ShowInspector && l.WantsTools));
        Assert.False(Read(l => l.ShowTabs || l.ShowBack || l.ShowEmpty));

        ui.Post(layout.ToggleInspector);
        Assert.False(Read(l => l.ShowInspector || l.WantsTools));
        Assert.Equal("Show the inspector", Read(l => l.InspectorToggleName));
        ui.Post(layout.ToggleInspector);
        Assert.True(Read(l => l.ShowInspector));
    }

    [Fact]
    public void Two_panes_give_the_session_conversation_and_tools_tabs_and_delegates_only_while_the_host_reports_some()
    {
        Resize(1000);
        Select("busy");
        Assert.True(Read(l => l.ShowList && l.ShowSession && l.ShowTabs && l.ShowConversation && l.ShowDelegatesTab));
        Assert.False(Read(l => l.ShowInspector || l.CanToggleInspector || l.ShowBack));

        ui.Post(() => layout.Tab = SessionTab.Tools);
        Assert.True(Read(l => l.ShowTools && l.WantsTools && !l.ShowConversation));
        ui.Post(() => layout.Tab = SessionTab.Delegates);
        Assert.True(Read(l => l.ShowDelegates && !l.ShowTools));
        Assert.Equal("2 background agents · 1 background command", Card("busy").DelegatesText);

        // A session with nothing delegated has no Delegates tab, and shows its conversation.
        Select("quiet");
        Assert.False(Read(l => l.ShowDelegatesTab || l.ShowDelegates));
        Assert.True(Read(l => l.ShowConversation));
    }

    [Fact]
    public void One_column_shows_the_list_until_a_session_is_chosen_then_the_session_with_a_way_back()
    {
        Resize(400);
        Assert.Equal(PaneMode.One, Read(l => l.Mode));
        Assert.True(Read(l => l.ShowList && !l.ShowSession && !l.ShowEmpty));

        Select("quiet");
        Assert.True(Read(l => l.ShowSession && l.ShowBack && l.ShowTabs && l.ShowConversation));
        Assert.False(Read(l => l.ShowList || l.ShowInspector));

        ui.Post(layout.Back);
        Assert.Null(TestUi.Read(() => main.Selected));
        Assert.True(Read(l => l.ShowList && !l.ShowSession));
    }

    [Fact]
    public void The_session_shown_survives_resizing_across_every_breakpoint_and_back()
    {
        Resize(1440);
        Select("busy");
        foreach (var width in new double[] { 1000, 400, 760, 1280, 759, 1279, 1920 })
        {
            Resize(width);
            Assert.Same(Card("busy"), TestUi.Read(() => main.Selected));
            Assert.True(Read(l => l.ShowSession), $"the session shows at {width}");
        }
        // A tab picked in two panes is still picked after a trip through three.
        Resize(1000);
        ui.Post(() => layout.Tab = SessionTab.Tools);
        Resize(1600);
        Assert.True(Read(l => l.ShowConversation && l.ShowInspector));
        Resize(900);
        Assert.True(Read(l => l.ShowTools));
    }

    [Fact]
    public async Task A_session_that_ends_while_shown_leaves_one_column_at_the_list()
    {
        Resize(400);
        Select("busy");
        Assert.True(Read(l => l.ShowSession));

        await host.SendAsync(Events.SessionRemove, new SessionRemoved("busy"));
        await Wait.UntilAsync(() => TestUi.Read(() => main.Selected) is null, "the session to end");
        Assert.True(Read(l => l.ShowList && !l.ShowSession && !l.ShowBack));
    }

    [Fact]
    public void A_row_says_what_its_session_does_and_where_in_two_facts_and_keeps_the_rest_for_details()
    {
        var busy = Card("busy");
        // One machine: where is the repository, read as a name in the text face (#215).
        Assert.Equal("Working", busy.RowStatus);
        Assert.Equal("danswett/agent-bridge", busy.RowPlace);
        Assert.Equal($"{busy.RowStatus} · {busy.RowPlace}", busy.RowFacts);
        // ui.md §6.1: never the model, counts or pid on a row; they are in Details.
        var details = busy.Details.ToDictionary(d => d.Label);
        Assert.DoesNotContain("claude-opus-5.5", busy.RowFacts);
        Assert.Equal("claude-opus-5.5", details["Model"].Value);
        Assert.Equal("100", details["Process"].Value);
        Assert.True(details["Process"].Mono && details["Folder"].Mono && details["Branch"].Mono && details["ID"].Mono);
        Assert.False(details["Repository"].Mono || details["Model"].Mono);
        Assert.Equal(@"C:\src\agent-bridge", details["Folder"].Tip);
    }

    [Fact]
    public void A_sessions_tool_history_loads_as_the_inspector_or_tools_tab_shows_it_and_stops_following_once_hidden()
    {
        Resize(1440);
        Select("busy");
        Assert.True(TestUi.Read(() => Card("busy").HistoryOpen));
        Assert.Equal(["busy"], fetched);

        ui.Post(layout.ToggleInspector);
        Assert.False(TestUi.Read(() => Card("busy").HistoryOpen));

        // Two panes: only the Tools tab asks for it.
        Resize(1000);
        Select("quiet");
        Assert.False(TestUi.Read(() => Card("quiet").HistoryOpen));
        ui.Post(() => layout.Tab = SessionTab.Tools);
        Assert.True(TestUi.Read(() => Card("quiet").HistoryOpen));
        Assert.Equal(["busy", "quiet"], fetched);
        Select("busy");
        Assert.False(TestUi.Read(() => Card("quiet").HistoryOpen));
        Assert.True(TestUi.Read(() => Card("busy").HistoryOpen));
    }

    [Fact]
    public async Task A_row_keeps_only_the_first_fact_of_a_long_status_and_the_header_keeps_all_of_it()
    {
        var subagents = new[] { new Subagent { Id = "a1", Kind = SubagentKind.Task, Name = "Read the logs", Status = SubagentStatus.Working } };
        await host.SendAsync(Events.SessionUpsert, Make.Session("asking", question: Make.Choice(options: ["yes", "no"]), background: new Background { Agents = 1 }, updated: Make.T0.AddMinutes(2)) with { Subagents = subagents });
        await Wait.UntilAsync(() => TestUi.Read(() => main.Sessions.Any(c => c.Id == "asking")), "the question");
        var asking = Card("asking");

        Assert.StartsWith("Waiting on you · ", asking.StatusText);
        Assert.Equal("Waiting on you", asking.RowStatus);
        Assert.Equal("Waiting on you · danswett/agent-bridge", asking.RowFacts);
        // §6.2's one meta line: the whole status, then where; never a separator first (#214).
        Assert.Equal("danswett/agent-bridge", asking.HeaderPlace);
        Assert.Equal("Main agent asks", asking.AsksText);
    }

    [Fact]
    public async Task A_question_a_delegate_asks_is_headed_with_its_name_or_a_delegate_when_the_list_left_it_out()
    {
        var subagents = new[] { new Subagent { Id = "a1", Kind = SubagentKind.Task, Name = "Read the logs", Status = SubagentStatus.Working } };
        await host.SendAsync(Events.SessionUpsert, Make.Session("asking", question: Make.Choice(options: ["yes", "no"]) with { Agent = "a1" }, updated: Make.T0.AddMinutes(2)) with { Subagents = subagents });
        await host.SendAsync(Events.SessionUpsert, Make.Session("unlisted", question: Make.Choice(options: ["yes", "no"]) with { Agent = "a9" }, updated: Make.T0.AddMinutes(3)));
        await Wait.UntilAsync(() => TestUi.Read(() => main.Sessions.Count == 4), "the questions");

        Assert.Equal("Read the logs asks", Card("asking").AsksText);
        Assert.Equal("A delegate asks", Card("unlisted").AsksText);
    }

    [Fact]
    public void A_row_is_read_aloud_with_its_glyphs_name_and_new_activity_while_unread()
    {
        var quiet = Card("quiet");
        ui.Post(() => quiet.Unread = true);
        Assert.Equal($"{quiet.GlyphLabel}, new activity", TestUi.Read(() => quiet.RowLabel));
        Assert.False(TestUi.Read(() => quiet.ShowAge));
        Select("quiet");
        Assert.Equal(quiet.GlyphLabel, TestUi.Read(() => quiet.RowLabel));
        Assert.True(TestUi.Read(() => quiet.ShowAge));
    }
}