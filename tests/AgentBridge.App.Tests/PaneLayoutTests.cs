using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// The Sessions page's panes by window width (ui.md §5.2, #180): three panes from 1280, two
// with tabs from 760, one column below, at the PWA's breakpoints, and the session shown
// surviving every resize.
[Trait("Category", "Network")]
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
        Assert.Equal(SessionTab.Delegates, Read(l => l.ShownTab));
        Select("quiet");
        Assert.False(Read(l => l.ShowDelegatesTab || l.ShowDelegates));
        Assert.True(Read(l => l.ShowConversation));
        // The bar shows the tab in force, not the one picked before it went.
        Assert.Equal(SessionTab.Conversation, Read(l => l.ShownTab));
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

    List<PaneMove> Moves()
    {
        var moves = new List<PaneMove>();
        ui.Post(() => layout.Moved += moves.Add);
        return moves;
    }

    [Fact]
    public void One_column_pushes_a_chosen_session_and_pops_back_to_the_list_and_a_resize_moves_nothing()
    {
        Resize(1440);
        var moves = Moves();
        // Choosing a row beside the list is not navigation: it shows at once.
        Select("quiet");
        Resize(400);
        Resize(1000);
        Resize(400);
        Assert.Empty(moves);

        ui.Post(layout.Back);
        Select("busy");
        Select("quiet");
        Assert.Equal([new(PaneMotion.Pop, true), new(PaneMotion.Push, true), new(PaneMotion.Push, true)], moves);
    }

    [Fact]
    public async Task A_session_asking_a_question_is_pushed_without_sliding_and_a_question_arriving_mid_push_settles_it()
    {
        Resize(400);
        await host.SendAsync(Events.SessionUpsert, Make.Session("asking", question: Make.Approval(), updated: Make.T0.AddMinutes(2)));
        await Wait.UntilAsync(() => TestUi.Read(() => main.Sessions.Any(c => c.Id == "asking")), "the question");
        var moves = Moves();

        Select("asking");
        Assert.Equal([new PaneMove(PaneMotion.Push, false)], moves);

        // A question arriving on the session being pushed stops its content travelling, so the
        // approval's command never slides under the pointer.
        ui.Post(layout.Back);
        Select("quiet");
        moves.Clear();
        await host.SendAsync(Events.SessionUpsert, Make.Session("quiet", question: Make.Approval("q2"), updated: Make.T0.AddMinutes(3)));
        await Wait.UntilAsync(() => TestUi.Read(() => moves.Contains(new PaneMove(PaneMotion.Settle, false))), "the settle");
    }

    [Fact]
    public async Task Only_the_inspector_toggle_slides_the_inspector_in_and_only_a_tab_the_person_chose_fades()
    {
        await host.SendAsync(Events.SessionUpsert, Make.Session("asking", question: Make.Approval(), updated: Make.T0.AddMinutes(2)));
        await Wait.UntilAsync(() => TestUi.Read(() => main.Sessions.Any(c => c.Id == "asking")), "the question");
        Resize(1440);
        Select("busy");
        var moves = Moves();
        ui.Post(layout.ToggleInspector);
        Assert.Empty(moves);
        ui.Post(layout.ToggleInspector);
        Assert.Equal([new PaneMove(PaneMotion.Inspector, true)], moves);

        moves.Clear();
        Resize(1000);
        ui.Post(() => layout.Tab = SessionTab.Tools);
        ui.Post(() => layout.Tab = SessionTab.Tools);
        Assert.Equal([new PaneMove(PaneMotion.Tab, false)], moves);

        // A session opened to be answered goes to its conversation at once, without a tab's fade.
        moves.Clear();
        ui.Post(() => main.Open("asking"));
        Assert.True(Read(l => l.ShowConversation));
        Assert.Empty(moves);
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
        Assert.True(details["Folder"].Path && !details["Branch"].Path);
        // Three panes have no Delegates tab: Details has the counts the host reports.
        Assert.Equal("2 background agents · 1 background command", details["Background"].Value);
        Assert.DoesNotContain("Background", Card("quiet").Details.Select(d => d.Label));
    }

    [Fact]
    public void A_sessions_tool_history_loads_as_the_inspector_or_tools_tab_shows_it_and_stops_following_once_hidden_or_off_screen()
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

        // Another page, or the window hidden in the tray: no history asked for until it is back.
        ui.Post(() => layout.OnScreen = false);
        Assert.False(TestUi.Read(() => Card("busy").HistoryOpen));
        ui.Post(() => layout.OnScreen = true);
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

        Assert.Equal("Delegate Read the logs asks", Card("asking").AsksText);
        Assert.Equal("A delegate asks", Card("unlisted").AsksText);
    }
    [Fact]
    public async Task A_question_whose_asker_only_waiting_on_names_is_headed_with_that_delegate_as_needs_you_says()
    {
        var subagents = new[] { new Subagent { Id = "a1", Kind = SubagentKind.Task, Name = "Read the logs", Status = SubagentStatus.Waiting } };
        await host.SendAsync(Events.SessionUpsert, Make.Session("asking", question: Make.Choice(options: ["yes", "no"]), updated: Make.T0.AddMinutes(2)) with { Subagents = subagents, WaitingOn = "a1" });
        await Wait.UntilAsync(() => TestUi.Read(() => main.Sessions.Any(c => c.Id == "asking")), "the question");

        Assert.Equal("Delegate Read the logs asks", TestUi.Read(() => Card("asking").AsksText));
        Assert.Equal("Delegate Read the logs", TestUi.Read(() => main.NeedsYou.Single(e => e.Card.Id == "asking").Card.Session is var s ? NeedsYouEntry.DelegateOf(s) : null));
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

    [Fact]
    public void Details_keep_their_rows_through_a_clock_tick_so_a_selected_path_or_id_stays_selected()
    {
        var busy = Card("busy");
        var id = TestUi.Read(() => busy.Details.Single(d => d.Label == "ID"));
        var touched = new List<string>();
        ui.Post(() => busy.Details.CollectionChanged += (_, e) =>
            touched.AddRange((e.NewItems?.Cast<DetailRow>() ?? []).Concat(e.OldItems?.Cast<DetailRow>() ?? []).Select(d => d.Label)));

        ui.Post(busy.RefreshTimes);

        Assert.Same(id, TestUi.Read(() => busy.Details.Single(d => d.Label == "ID")));
        Assert.All(touched, label => Assert.Contains(label, new[] { "For", "Updated" }));
    }

    [Fact]
    public async Task A_fact_leaving_details_keeps_the_rows_after_it_so_a_selected_id_stays_selected()
    {
        var busy = Card("busy");
        var id = TestUi.Read(() => busy.Details.Single(d => d.Label == "ID"));
        var updated = TestUi.Read(() => busy.Details.Single(d => d.Label == "Updated"));

        // Its background work finishes and it goes idle: Background and For leave Details.
        await host.SendAsync(Events.SessionUpsert, Make.Session("busy", updated: Make.T0.AddMinutes(1)));
        await Wait.UntilAsync(() => TestUi.Read(() => busy.Details.All(d => d.Label is not ("Background" or "For"))), "Background and For gone");

        Assert.Same(id, TestUi.Read(() => busy.Details.Single(d => d.Label == "ID")));
        Assert.Same(updated, TestUi.Read(() => busy.Details.Single(d => d.Label == "Updated")));
    }

    [Fact]
    public async Task A_question_opened_from_needs_you_or_a_toast_shows_conversation_but_a_row_chosen_keeps_the_tab_picked()
    {
        await host.SendAsync(Events.SessionUpsert, Make.Session("asking", question: Make.Choice(options: ["yes", "no"]), updated: Make.T0.AddMinutes(2)));
        await Wait.UntilAsync(() => TestUi.Read(() => main.Sessions.Any(c => c.Id == "asking")), "the asking session");
        Resize(1000);

        Select("asking");
        ui.Post(() => layout.Tab = SessionTab.Tools);
        Select("busy");
        Select("asking");
        Assert.Equal(SessionTab.Tools, Read(l => l.Tab));

        var wanted = new List<string>();
        ui.Post(() => layout.QuestionWanted += c => wanted.Add(c.Id));

        // Opened to be answered: Tools would hide the question, and it is brought into view.
        ui.Post(() => main.Open("asking"));
        Assert.Equal(SessionTab.Conversation, Read(l => l.Tab));
        Assert.Equal(["asking"], TestUi.Read(() => wanted.ToList()));

        // A session with nothing to answer keeps the tab, opened or chosen.
        ui.Post(() => layout.Tab = SessionTab.Tools);
        ui.Post(() => main.Open("busy"));
        Assert.Equal(SessionTab.Tools, Read(l => l.Tab));        // Chosen from the list, or with nothing to answer: nothing to bring into view.
        Select("asking");
        Assert.Equal(["asking"], TestUi.Read(() => wanted.ToList()));

    }

    [Fact]
    public async Task The_main_agent_label_shows_the_main_agents_own_words_never_a_delegates()
    {
        // A delegate wrote last: the top-level text is its, and own carries the main agent's.
        await host.SendAsync(Events.SessionUpsert, Make.Session("busy", updated: Make.T0.AddMinutes(1)) with
        {
            Activity = new Activity
            {
                Response = "The delegate's findings.",
                Reasoning = "The delegate's reasoning.",
                Source = new ActivitySource { Response = "agent-1", Reasoning = "agent-1" },
                Own = new OwnActivity { Response = "The main agent's answer.", ResponseTruncated = true },
            },
        });
        var busy = Card("busy");
        await Wait.UntilAsync(() => TestUi.Read(() => busy.Response) == "The main agent's answer.", "the main agent's own answer");
        Assert.True(TestUi.Read(() => busy.ResponseTruncated));
        // The main agent has no reasoning of its own: none is shown, not the delegate's.
        Assert.Equal("", TestUi.Read(() => busy.Reasoning));

        // Only reasoning by a delegate: the top-level response is the main agent's.
        await host.SendAsync(Events.SessionUpsert, Make.Session("busy", updated: Make.T0.AddMinutes(2)) with
        {
            Activity = new Activity
            {
                Response = "Main's reply.",
                Reasoning = "A delegate thinking.",
                Source = new ActivitySource { Reasoning = "agent-2" },
                Own = new OwnActivity { Reasoning = "Main thinking." },
            },
        });
        await Wait.UntilAsync(() => TestUi.Read(() => busy.Response) == "Main's reply.", "the top-level reply");
        Assert.Equal(("Main thinking.", false), TestUi.Read(() => (busy.Reasoning, busy.ResponseTruncated)));

        // No source, the top-level text absent: own's stands in.
        await host.SendAsync(Events.SessionUpsert, Make.Session("busy", updated: Make.T0.AddMinutes(3)) with
        {
            Activity = new Activity { Own = new OwnActivity { Response = "Skimmed main answer." } },
        });
        await Wait.UntilAsync(() => TestUi.Read(() => busy.Response) == "Skimmed main answer.", "own's answer in place of an absent one");
    }

    async Task<SessionCardViewModel> ShowAsync(Session session, string what)
    {
        await host.SendAsync(Events.SessionUpsert, session);
        var card = Card(session.Id);
        await Wait.UntilAsync(() => TestUi.Read(() => card.Response) == session.Activity.Response || TestUi.Read(() => card.Response) == session.Activity.Own?.Response, what);
        return card;
    }

    [Fact]
    public async Task Words_from_a_host_that_says_nothing_of_whose_they_are_are_labelled_with_the_agents_name_not_main_agent()
    {
        // Claude, Codex or an older host: the top-level response may be a delegate's.
        var card = await ShowAsync(Make.Session("busy", response: "Maybe a delegate's words.", updated: Make.T0.AddMinutes(1)) with { Agent = AgentKind.Claude }, "the unattributed response");

        Assert.Equal(("Maybe a delegate's words.", false, "Claude"), TestUi.Read(() => (card.Response, card.Attributed, card.Speaker)));
    }

    [Fact]
    public async Task Words_the_host_attributes_with_source_are_the_main_agents_own_and_labelled_main_agent()
    {
        var card = await ShowAsync(Make.Session("busy", updated: Make.T0.AddMinutes(1)) with
        {
            Activity = new Activity
            {
                Response = "The delegate's findings.",
                Source = new ActivitySource { Response = "agent-1" },
                Own = new OwnActivity { Response = "The main agent's answer." },
            },
        }, "the attributed response");

        Assert.Equal(("The main agent's answer.", "Main agent"), TestUi.Read(() => (card.Response, card.Speaker)));
    }

    [Fact]
    public async Task A_host_listing_delegates_without_a_source_has_its_top_level_words_labelled_main_agent()
    {
        // A host that attributes says nothing more while the main agent wrote last.
        var card = await ShowAsync(Make.Session("busy", response: "Main's own reply.", updated: Make.T0.AddMinutes(1)) with
        {
            Subagents = [new Subagent { Id = "agent-1", Kind = SubagentKind.Task, Status = SubagentStatus.Working, Name = "Read the logs" }],
        }, "the response beside a delegate list");

        Assert.Equal(("Main's own reply.", "Main agent"), TestUi.Read(() => (card.Response, card.Speaker)));
    }

    [Fact]
    public async Task A_main_agent_answer_the_budget_left_out_is_said_to_be_left_out_never_replaced_by_the_delegates()
    {
        await host.SendAsync(Events.SessionUpsert, Make.Session("busy", updated: Make.T0.AddMinutes(1)) with
        {
            Activity = new Activity
            {
                Response = "The delegate's findings.",
                Source = new ActivitySource { Response = "agent-1" },
                Own = new OwnActivity { ResponseTruncated = true },
            },
        });
        var busy = Card("busy");
        await Wait.UntilAsync(() => TestUi.Read(() => busy.ResponseDropped), "the dropped answer");

        Assert.Equal(("", false, true, "Main agent"), TestUi.Read(() => (busy.Response, busy.HasResponse, busy.HasVoice, busy.Speaker)));
    }

    [Fact]
    public async Task The_main_agents_answer_beside_a_delegates_is_dated_by_when_the_main_agent_wrote_it()
    {
        // A delegate's update moved the session's time; the main agent wrote five hours ago.
        var now = DateTimeOffset.UtcNow;
        await host.SendAsync(Events.SessionUpsert, Make.Session("busy", updated: now) with
        {
            Activity = new Activity
            {
                Response = "The delegate's findings.",
                Source = new ActivitySource { Response = "agent-1" },
                Own = new OwnActivity { Response = "The main agent's answer.", At = now.AddHours(-5) },
            },
        });
        var busy = Card("busy");
        await Wait.UntilAsync(() => TestUi.Read(() => busy.Response) == "The main agent's answer.", "the main agent's answer");
        ui.Post(busy.RefreshTimes);

        Assert.Equal(("now", "5h"), TestUi.Read(() => (busy.AgeText, busy.ResponseAgeText)));
    }
}