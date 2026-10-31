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
        d.Observe(At(1, SessionStatus.Working, "the agent's prompt"), Fixture.T0, drivers.Arm);

        Assert.Equal((DriverKind.Agent, Principal), d.Shown);
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
