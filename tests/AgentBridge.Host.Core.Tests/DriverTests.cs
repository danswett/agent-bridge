using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

// Session.Driver: who drives a session's turn (the purple edge), from the host's one record of it,
// which the Home Assistant connector arms for presses on its cards.
public class DriverTests
{
    const string Id = "77777777-7777-4777-8777-777777777777";
    const string Principal = "homeassistant:0f1e2d3c";

    // A press's arm reaches the source on its own, not with a pass, so the change is waited for.
    static async Task<Session> Until(SessionSource source, Func<Session, bool> done)
    {
        for (var i = 0; i < 500; i++)
        {
            if (source.Get(Id) is { } s && done(s)) { return s; }
            await Task.Delay(10);
        }
        throw new TimeoutException($"the session never reached the state waited for: {source.Get(Id)?.Driver}");
    }

    static (DriverKind?, string?) Driver(Session s) => (s.Driver, s.DriverPrincipal);

    static Session At(int? turns, SessionStatus status, string? message) => new()
    {
        Id = Id, Agent = AgentKind.Codex, Status = status, StatusSince = Fixture.T0, UpdatedAt = Fixture.T0,
        Activity = new Activity { TurnCount = turns, UserMessage = message },
    };

    [Fact]
    public void A_prompt_whose_text_is_read_after_its_count_is_one_turn_not_two()
    {
        var drivers = new SessionDrivers();
        var d = drivers.For(Id);
        d.Observe(At(1, SessionStatus.Idle, "first"), Fixture.T0, drivers.Arm);
        d.ArmDriver(SessionDrivers.Agent, Fixture.T0, Principal);

        // Codex counts the prompt at task_started; its text follows in a record of its own, and a
        // read can fall between them.
        d.Observe(At(1, SessionStatus.Working, "first"), Fixture.T0, drivers.Arm);
        // Between the count and its text, the prompt shown is still the person's (#489).
        Assert.False(d.PromptIsAgents);
        d.Observe(At(1, SessionStatus.Working, "the agent's prompt"), Fixture.T0, drivers.Arm);

        Assert.Equal((DriverKind.Agent, Principal), d.Shown);
        Assert.True(d.PromptIsAgents);

        // The person's next turn, read the same way: the agent's words stay the agent's until the
        // person's own replace them.
        d.Observe(At(2, SessionStatus.Working, "the agent's prompt"), Fixture.T0, drivers.Arm);
        Assert.True(d.PromptIsAgents);
        d.Observe(At(2, SessionStatus.Working, "mine"), Fixture.T0, drivers.Arm);
        Assert.False(d.PromptIsAgents);
    }

    [Fact]
    public void A_launch_whose_opening_turn_a_listening_source_already_has_spends_its_arm_on_it()
    {
        var drivers = new SessionDrivers();
        var opening = At(0, SessionStatus.Working, "Fix the build");
        drivers.For(Id).Observe(opening, Fixture.T0, drivers.Arm);
        // A source observes as soon as it hears of a change, as the host's does.
        drivers.Changed += id => drivers.For(id).Observe(opening, Fixture.T0, drivers.Arm);

        drivers.For(Id).ArmLaunch(Fixture.T0, "Fix the build", resumed: false, Principal);

        // Spent on the opening turn, so the person's next prompt is theirs.
        Assert.Null(drivers.For(Id).PendingAt);
        Assert.Equal((DriverKind.Agent, Principal), drivers.For(Id).Shown);
    }

    [Fact]
    public async Task An_agents_press_shows_at_once_with_its_principal_its_turn_is_the_agents_and_a_turn_typed_at_the_machine_is_the_persons()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await source.RefreshAsync();
        Assert.Equal((null, null), Driver(source.Get(Id)!));

        f.Time.Advance(TimeSpan.FromSeconds(5));
        var pressed = f.Time.GetUtcNow();
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, pressed, Principal);
        var shown = await Until(source, s => s.Driver == DriverKind.Agent);
        // Something a client shows changed, so the session is dated by it.
        Assert.Equal((DriverKind.Agent, Principal, pressed), (shown.Driver, shown.DriverPrincipal, shown.UpdatedAt));
        Assert.Equal(DriverKind.Agent, Assert.Single(source.Snapshot()).Driver);

        // The turn the press was for.
        f.Time.Advance(TimeSpan.FromSeconds(1));
        Fixture.Append(events, Copilot.User("the agent's words", 10), Copilot.TurnStart(11));
        await source.RefreshAsync();
        Assert.Equal((DriverKind.Agent, Principal), Driver(source.Get(Id)!));

        // Its turn ending is no new turn: a session left with the agent still says so while idle.
        Fixture.Append(events, Copilot.TurnEnd(12));
        await source.RefreshAsync();
        Assert.Equal((SessionStatus.Idle, DriverKind.Agent, Principal), (source.Get(Id)!.Status, source.Get(Id)!.Driver, source.Get(Id)!.DriverPrincipal));

        // The next, which nobody armed: typed at the machine.
        Fixture.Append(events, Copilot.User("mine now", 13), Copilot.TurnStart(14));
        await source.RefreshAsync();
        Assert.Equal((null, null), Driver(source.Get(Id)!));
    }

    [Fact]
    public async Task An_agents_answer_drives_the_turn_it_answers_and_the_persons_next_prompt_moments_later_is_theirs()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("q1", "ask_user", new
        {
            message = "Which one?",
            requestedSchema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b" } } }, required = new[] { "pick" } },
        }, 3));
        await source.RefreshAsync();
        Assert.Equal(SessionStatus.Waiting, source.Get(Id)!.Status);

        // An agent answers it from the Home Assistant card.
        drivers.For(Id).Answered(SessionDrivers.Agent, Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);

        // The answer carries on the turn it was asked in, which ends as the agent's.
        Fixture.Append(events,
            Copilot.Event("human_response.recorded", new { toolCallId = "q1", requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content = new { pick = "b" } } }, 4),
            Copilot.ToolDone("q1", 5), Copilot.Assistant("Went with b.", 6), Copilot.TurnEnd(7));
        await source.RefreshAsync();
        Assert.Equal((SessionStatus.Idle, DriverKind.Agent), (source.Get(Id)!.Status, source.Get(Id)!.Driver));

        // It left nothing armed: the person's own prompt, well within ten minutes, is theirs.
        f.Time.Advance(TimeSpan.FromSeconds(30));
        Fixture.Append(events, Copilot.User("mine", 40), Copilot.TurnStart(41));
        await source.RefreshAsync();
        Assert.Equal((null, null), Driver(source.Get(Id)!));
    }

    // An agent's reply carries no mark in the transcript; the host knows the turn it started (#489).
    [Fact]
    public async Task A_prompt_an_agents_reply_started_is_the_agents_and_the_next_one_typed_at_the_machine_is_the_persons()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await source.RefreshAsync();
        Assert.Null(source.Get(Id)!.Activity.UserMessageFrom);

        f.Time.Advance(TimeSpan.FromSeconds(5));
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);
        // Armed, but its prompt has not come: the one shown is still the person's.
        Assert.Null(source.Get(Id)!.Activity.UserMessageFrom);

        Fixture.Append(events, Copilot.User("the agent's words", 10), Copilot.TurnStart(11));
        await source.RefreshAsync();
        Assert.Equal(("the agent's words", UserMessageFrom.Agent), (source.Get(Id)!.Activity.UserMessage, source.Get(Id)!.Activity.UserMessageFrom));
        Fixture.Append(events, Copilot.TurnEnd(12));
        await source.RefreshAsync();
        Assert.Equal(UserMessageFrom.Agent, source.Get(Id)!.Activity.UserMessageFrom);

        Fixture.Append(events, Copilot.User("mine now", 13), Copilot.TurnStart(14));
        await source.RefreshAsync();
        Assert.Equal(("mine now", (UserMessageFrom?)null), (source.Get(Id)!.Activity.UserMessage, source.Get(Id)!.Activity.UserMessageFrom));
    }

    // Context Copilot gives the agent mid-turn (a skill, a memory, a shell's notice) is no new turn:
    // counted as a prompt, it spent the agent's turn and its prompt read as the person's.
    [Fact]
    public async Task Context_given_in_the_middle_of_an_agents_turn_leaves_its_prompt_and_its_edge_the_agents()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromSeconds(5));
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);
        Fixture.Append(events, Copilot.User("the agent's words", 10), Copilot.TurnStart(11));
        await source.RefreshAsync();
        Fixture.Append(events,
            Copilot.Event("user.message", new { content = "<skill-context name=\"docx\">...</skill-context>", interactionId = "i12", source = "skill-docx" }, 12),
            Copilot.Event("user.message", new { content = "<system_notification> Shell command completed.</system_notification>", interactionId = "i13", source = "system", delivery = "steering" }, 13));
        await source.RefreshAsync();

        var s = source.Get(Id)!;
        Assert.Equal(("the agent's words", UserMessageFrom.Agent, DriverKind.Agent, 1), (s.Activity.UserMessage, s.Activity.UserMessageFrom, s.Driver, s.Activity.TurnCount));
    }

    // A notice that starts a turn of its own once the agent's has ended changes the count but not
    // the prompt: the agent's words stay the agent's.
    [Fact]
    public async Task A_notice_after_an_agents_turn_has_ended_leaves_its_prompt_the_agents()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromSeconds(5));
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);
        Fixture.Append(events, Copilot.User("the agent's words", 10), Copilot.TurnStart(11), Copilot.TurnEnd(12));
        await source.RefreshAsync();
        Fixture.Append(events,
            Copilot.Event("user.message", new { content = "<system_notification> Shell command completed.</system_notification>", interactionId = "i20", source = "system", delivery = "idle" }, 20),
            Copilot.TurnStart(21));
        await source.RefreshAsync();

        var s = source.Get(Id)!;
        Assert.Equal((SessionStatus.Working, "the agent's words", UserMessageFrom.Agent), (s.Status, s.Activity.UserMessage, s.Activity.UserMessageFrom));
    }

    // Known by its words alone, an agent's "continue" after the person's was the person's, and the
    // person's next "continue" the agent's. Each is its own record.
    [Fact]
    public async Task The_same_words_sent_by_the_person_and_by_an_agent_are_each_their_senders()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("continue", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromSeconds(5));
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);
        Fixture.Append(events, Copilot.User("continue", 10), Copilot.TurnStart(11), Copilot.TurnEnd(12));
        await source.RefreshAsync();
        Assert.Equal(UserMessageFrom.Agent, source.Get(Id)!.Activity.UserMessageFrom);

        Fixture.Append(events, Copilot.User("continue", 13), Copilot.TurnStart(14));
        await source.RefreshAsync();
        Assert.Null(source.Get(Id)!.Activity.UserMessageFrom);
    }

    // A bridge reply that comes while a notice's turn is running, uncounted, brings no new count:
    // known only by its new prompt, it is still the arm's.
    [Fact]
    public async Task A_reply_that_comes_while_a_notice_is_being_handled_is_still_the_agents()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3),
            Copilot.Event("user.message", new { content = "<system_notification> Shell command completed.</system_notification>", interactionId = "i4", source = "system" }, 4),
            Copilot.TurnStart(5));
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromSeconds(5));
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);
        Fixture.Append(events, Copilot.User("the agent's words", 10));
        await source.RefreshAsync();

        var s = source.Get(Id)!;
        Assert.Equal(("the agent's words", UserMessageFrom.Agent), (s.Activity.UserMessage, s.Activity.UserMessageFrom));
    }

    // Read in one refresh, an agent's reply and the person's prompt typed after it: the latest is
    // the person's, and the words the reply was sent with say it is not the agent's.
    [Fact]
    public async Task A_persons_prompt_read_with_an_agents_reply_in_one_refresh_is_the_persons()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromSeconds(5));
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        drivers.For(Id).Expect("the agent's words");
        await Until(source, s => s.Driver == DriverKind.Agent);
        Fixture.Append(events, Copilot.User("the agent's words", 10), Copilot.TurnStart(11), Copilot.User("mine, typed meanwhile", 12));
        await source.RefreshAsync();

        var s = source.Get(Id)!;
        Assert.Equal(("mine, typed meanwhile", (UserMessageFrom?)null), (s.Activity.UserMessage, s.Activity.UserMessageFrom));
    }

    // Read again from the start (the file was replaced), a Codex prompt now before the parsed window
    // is known by the same record as before: the agent's stays the agent's.
    [Fact]
    public async Task An_agents_codex_prompt_read_again_from_before_the_window_is_still_the_agents()
    {
        const string Codex1 = "66666666-6666-4666-8666-666666666677";
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var rollout = f.CodexTranscript(Codex1);
        f.CodexRegistration(Codex1, 950, rollout, updated: Fixture.T0);
        f.Processes.Run(AgentKind.Codex, 950);
        Fixture.Append(rollout,
            Codex.Line("session_meta", new { id = Codex1, timestamp = Copilot.At(0), cwd = "/work/codex" }, 0),
            Codex.Event(new { type = "task_started" }, 1), Codex.Event(new { type = "user_message", message = "go" }, 1),
            Codex.Event(new { type = "task_complete", last_agent_message = "Done." }, 2));
        await using var source = f.Source(tailBytes: 4096, drivers: drivers);
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromSeconds(5));
        drivers.For(Codex1).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 10), Codex.Event(new { type = "user_message", message = "the agent's words" }, 10));
        await source.RefreshAsync();
        Fixture.Append(rollout, [.. Enumerable.Range(0, 8).Select(i => Codex.Event(new { type = "agent_reasoning", text = new string('x', 1000) }, 11 + i))]);
        await source.RefreshAsync();
        Assert.Equal(UserMessageFrom.Agent, source.Get(Codex1)!.Activity.UserMessageFrom);

        // Replaced by a shorter copy: read again, the prompt now skimmed rather than in the window.
        var lines = File.ReadAllLines(rollout);
        File.WriteAllLines(rollout, lines[..^1]);
        await source.RefreshAsync();

        var s = source.Get(Codex1)!;
        Assert.Equal(("the agent's words", UserMessageFrom.Agent), (s.Activity.UserMessage, s.Activity.UserMessageFrom));
    }

    // Helpers for a Copilot session observed by a host-wide driver record.
    sealed class Driven : IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly SessionDrivers Drivers = new();
        readonly string events;
        public readonly SessionSource Source;

        public Driven()
        {
            events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source(drivers: Drivers);
        }

        public async Task<Session> After(params string[] lines)
        {
            Fixture.Append(events, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        public async Task ArmAsync(string? words)
        {
            F.Time.Advance(TimeSpan.FromSeconds(5));
            Drivers.For(Id).ArmDriver(SessionDrivers.Agent, F.Time.GetUtcNow(), Principal);
            if (words is not null) { Drivers.For(Id).Expect(words); }
            await Until(Source, s => s.Driver == DriverKind.Agent);
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            F.Dispose();
        }
    }

    // Delivery puts an attachment's mention before the reply's words; it is still the reply.
    [Theory]
    [InlineData("@C:\\staged\\shot.png look at this")]
    [InlineData("[Image #1] look at this")]
    public async Task A_reply_delivered_with_its_attachments_before_its_words_is_the_agents(string delivered)
    {
        await using var d = new Driven();
        await d.After(Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await d.ArmAsync("look at this");

        var s = await d.After(Copilot.User(delivered, 10), Copilot.TurnStart(11));

        Assert.Equal(UserMessageFrom.Agent, s.Activity.UserMessageFrom);
    }

    // Read in one refresh, the agent's "continue" and the person's after it: two prompts, and which
    // the arm was for cannot be told, so neither is taken for the agent's. Words that only begin
    // like the reply's are not it.
    [Fact]
    public async Task The_same_words_from_both_in_one_refresh_or_words_that_only_begin_like_the_reply_are_not_the_agents()
    {
        await using var d = new Driven();
        await d.After(Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await d.ArmAsync("continue");
        var both = await d.After(Copilot.User("continue", 10), Copilot.TurnStart(11), Copilot.User("continue", 12));
        Assert.Null(both.Activity.UserMessageFrom);

        await using var e = new Driven();
        await e.After(Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await e.ArmAsync("continue");
        var longer = await e.After(Copilot.User("continue, but do not deploy", 10), Copilot.TurnStart(11));
        Assert.Null(longer.Activity.UserMessageFrom);
    }

    // A card's publish arms with its words; an empty Send arms over it and is put back. The words
    // come back with it, so a person's prompt read with the reply is still theirs.
    [Fact]
    public void An_arm_put_back_keeps_the_words_it_expected()
    {
        var drivers = new SessionDrivers();
        var d = drivers.For(Id);
        d.Observe(At(1, SessionStatus.Idle, "first"), Fixture.T0, drivers.Arm, "k1\nfirst", 1);
        d.ArmDriver(SessionDrivers.Agent, Fixture.T0, Principal);
        d.Expect("the agent's words");
        var before = d.Save();
        d.ArmDriver(SessionDrivers.Agent, Fixture.T0, Principal);
        d.Restore(before);

        d.Observe(At(1, SessionStatus.Working, "mine"), Fixture.T0, drivers.Arm, "k2\nmine", 2);

        Assert.False(d.PromptIsAgents);
    }

    // A launch's arm is spent on its opening turn, but the prompt shown is the launch's only when it
    // is the launch's words: the person may already have sent another.
    [Fact]
    public void A_launch_spent_on_a_prompt_that_is_not_its_own_leaves_that_prompt_the_persons()
    {
        var drivers = new SessionDrivers();
        var d = drivers.For(Id);
        d.ArmLaunch(Fixture.T0, "Fix the build", resumed: false, Principal);

        d.Observe(At(1, SessionStatus.Working, "and the tests too"), Fixture.T0, drivers.Arm);

        Assert.Null(d.PendingAt);
        Assert.False(d.PromptIsAgents);
    }

    [Fact]
    public async Task A_persons_prompt_an_agent_then_answered_a_question_in_stays_the_persons()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("q1", "ask_user", new
        {
            message = "Which one?",
            requestedSchema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b" } } }, required = new[] { "pick" } },
        }, 3));
        await source.RefreshAsync();

        drivers.For(Id).Answered(SessionDrivers.Agent, Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);

        Assert.Equal(("go", (UserMessageFrom?)null), (source.Get(Id)!.Activity.UserMessage, source.Get(Id)!.Activity.UserMessageFrom));
    }

    [Fact]
    public async Task An_arm_whose_turn_did_not_start_within_ten_minutes_leaves_the_turn_to_the_person()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await source.RefreshAsync();
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);

        f.Time.Advance(drivers.Arm);
        Fixture.Append(events, Copilot.User("much later", 700), Copilot.TurnStart(701));
        await source.RefreshAsync();

        Assert.Equal((null, null), Driver(source.Get(Id)!));
    }

    [Fact]
    public async Task A_persons_answer_or_reply_takes_the_session_back_at_once_before_its_turn_starts()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        await source.RefreshAsync();
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);

        drivers.ArmPerson(Id, f.Time.GetUtcNow());
        await Until(source, s => s.Driver is null);

        // And the turn it was for is the person's too.
        Fixture.Append(events, Copilot.User("my answer", 10), Copilot.TurnStart(11));
        await source.RefreshAsync();
        Assert.Equal((null, null), Driver(source.Get(Id)!));
    }

    [Fact]
    public async Task A_session_that_goes_and_comes_back_starts_again_as_the_persons()
    {
        using var f = new Fixture();
        var drivers = new SessionDrivers();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source(drivers: drivers);
        Fixture.Append(events, Copilot.User("go", 1));
        await source.RefreshAsync();
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, f.Time.GetUtcNow(), Principal);
        await Until(source, s => s.Driver == DriverKind.Agent);

        f.Processes.Exit(AgentKind.Copilot, 100);
        f.Time.Advance(TimeSpan.FromMinutes(1));
        await source.RefreshAsync();
        Assert.Null(source.Get(Id));
        Assert.Equal((null, null), drivers.Shown(Id));

        f.Processes.Run(AgentKind.Copilot, 100);
        f.Time.Advance(TimeSpan.FromMinutes(1));
        await source.RefreshAsync();
        Assert.Equal((null, null), Driver(source.Get(Id)!));
    }
}
