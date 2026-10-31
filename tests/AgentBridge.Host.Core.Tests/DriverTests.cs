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

        // The next, which nobody armed: typed at the machine.
        Fixture.Append(events, Copilot.TurnEnd(12), Copilot.User("mine now", 13), Copilot.TurnStart(14));
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
