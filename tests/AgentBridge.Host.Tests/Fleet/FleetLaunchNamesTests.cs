using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using Xunit;

public sealed class FleetLaunchNamesTests : IDisposable
{
    readonly Fixture fixture = new();
    readonly FleetPublisherTests.Source source = new();
    readonly Launcher launcher = new();
    readonly Roster roster = new();
    readonly List<string> logs = [];
    readonly FleetNames names;
    readonly FleetLaunchNames policy;
    string Config => Path.Combine(fixture.Root, HostSettings.FileName);
    public FleetLaunchNamesTests()
    {
        source.Sessions.Clear();
        names = new(fixture.Root);
        policy = new(launcher, source, names, roster, fixture.Root, logs.Add);
    }
    public void Dispose() => fixture.Dispose();
    static LaunchRequest Ask(string name = "SG Builder 1829") => new() { Request = Guid.NewGuid().ToString(), FleetName = name, Prompt = "private prompt" };
    Task<LaunchResult> Start()
    {
        var session = FleetPublisherTests.NewSession(source.Sessions.Count + 1);
        source.Sessions.Add(session);
        return Task.FromResult(new LaunchResult(LaunchOutcome.Started) { Session = session.Id });
    }

    [Theory]
    [InlineData("SG Chief of Staff")]
    [InlineData("SG Engineering Lead")]
    [InlineData("SG Quality Lead")]
    [InlineData("SG Product Lead")]
    [InlineData("SG Creative Lead")]
    [InlineData("SG Migration Lead")]
    [InlineData("SG SRE Lead")]
    [InlineData("SG Reviewer A")]
    [InlineData("SG Reviewer Z")]
    [InlineData("SG Builder 1")]
    [InlineData("SG Worker 9999", true)]
    public async Task A_new_launch_publishes_only_its_explicit_name_and_logs_the_caller_without_prompt_text(string name, bool unreadable = false)
    {
        roster.Unreadable = unreadable;
        var result = await policy.RunAsync(Ask(name), "fixture-principal", Start);
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        JsonElement record = default;
        await new FleetPublisher(source, "fixture", names.Get).PublishAsync((r, _) => { record = r; return Task.CompletedTask; });
        Assert.Equal(name, Assert.Single(record.GetProperty("sessions").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(unreadable ? 2 : 1, logs.Count);
        var log = logs[^1];
        Assert.Contains(result.Session!, log);
        Assert.Contains("fixture-principal", log);
        Assert.Contains(name, log);
        Assert.DoesNotContain("private", log);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SG Builder 10000")]
    [InlineData("SG Reviewer AA")]
    [InlineData("sg Quality Lead")]
    [InlineData(" SG Quality Lead")]
    [InlineData("SG Quality Lead\n")]
    public async Task Names_outside_the_fixed_list_never_start_a_session(string name)
    {
        Assert.Equal(LaunchOutcome.Refused, (await policy.RunAsync(Ask(name), "fixture", Start)).Outcome);
        Assert.Empty(source.Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_live_local_or_account_duplicate_is_refused_without_replacing_its_name(bool remote)
    {
        if (remote) { roster.Machines = [new("fixture", true, [new("unused", "SG Builder 1829", "idle", DateTimeOffset.UtcNow)])]; }
        else
        {
            await Start();
            names.Rename(source, JsonSerializer.SerializeToElement(new { id = source.Sessions[0].Id, name = "SG Builder 1829" }));
        }
        var before = source.Sessions.Count;
        Assert.Contains("already", (await policy.RunAsync(Ask(), "fixture", Start)).Reason);
        Assert.Equal(before, source.Sessions.Count);
    }

    [Theory]
    [InlineData("""{"fleetNames":{"launchRule":"off"}}""")]
    [InlineData("""{"fleetNames":{"launchRule":"unknown"}}""")]
    [InlineData("""{"fleetNames":{"launchRule":null}}""")]
    [InlineData("""{"fleetNames":false}""")]
    [InlineData("broken")]
    public async Task Off_or_unreadable_policy_refuses_named_launches_on_every_fresh_read(string json)
    {
        File.WriteAllText(Config, "{}");
        Assert.Equal(LaunchOutcome.Started, (await policy.RunAsync(Ask(), "fixture", Start)).Outcome);
        File.WriteAllText(Config, json);
        Assert.Equal(LaunchOutcome.Refused, (await policy.RunAsync(Ask("SG Worker 1"), "fixture", Start)).Outcome);
        var count = logs.Count;
        Assert.Equal(LaunchOutcome.Refused, (await policy.RunAsync(Ask("SG Worker 2"), "fixture", Start)).Outcome);
        Assert.Equal(count, logs.Count);
        Assert.Single(source.Sessions);
    }

    [Fact]
    public async Task An_existing_session_returned_by_the_launcher_is_not_named()
    {
        var old = await Start();
        var result = await policy.RunAsync(Ask(), "fixture", () => Task.FromResult(old));
        Assert.Equal(LaunchOutcome.Unconfirmed, result.Outcome);
        Assert.Null(names.Get(old.Session!));
    }

    [Fact]
    public async Task A_durable_write_failure_does_not_claim_that_a_started_session_was_refused()
    {
        Directory.CreateDirectory(Path.Combine(fixture.Root, FleetNames.FileName));
        var request = Ask();
        var result = await policy.RunAsync(request, "fixture", async () =>
        {
            var started = await Start();
            launcher.Register(Guid.Parse(request.Request), started.Session!);
            return started;
        });
        Assert.Equal(LaunchOutcome.Unconfirmed, result.Outcome);
        Assert.Single(source.Sessions);
        Assert.Null(names.Get(source.Sessions[0].Id));
        Assert.Contains(logs, line => line.Contains("not saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unconfirmed_launch_reserves_its_name_until_the_new_session_registers()
    {
        var request = Ask();
        await policy.RunAsync(request, "fixture", () => Task.FromResult(new LaunchResult(LaunchOutcome.Unconfirmed)));
        Assert.Contains("already", (await policy.RunAsync(Ask(), "fixture", Start)).Reason);
        var late = await Start();
        launcher.Register(Guid.Parse(request.Request), late.Session!);
        Assert.Equal("SG Builder 1829", names.Get(late.Session!));
    }

    sealed class Roster : IFleetRoster
    {
        public IReadOnlyList<FleetMachine> Machines = [];
        public bool Unreadable;
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => Unreadable ? throw new System.Net.WebSockets.WebSocketException() : Task.FromResult(Machines);
    }
    sealed class Launcher : ISessionLauncher
    {
        public bool Available => true;
        public event Action<Guid, string>? RequestRegistered;
        public void Register(Guid request, string id) => RequestRegistered?.Invoke(request, id);
        public Task<LaunchOptions> OptionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken ct) => throw new NotSupportedException();
    }
}
