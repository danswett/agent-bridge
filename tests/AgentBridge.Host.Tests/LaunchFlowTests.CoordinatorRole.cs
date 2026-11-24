using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// #4318 slice 3: a person's launch records its coordinatorRole in the launch record, no agent launch can, and fleet.resume reads it from there.
public sealed partial class LaunchFlowTests
{
    async Task<(LaunchService Launcher, string Session)> PersonLaunch(object request)
    {
        var launcher = Launcher(Config());
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), launcher: launcher).HandleAsync;
        var ch = await OpenAsync();
        var o = await LaunchAsync(ch, request);
        Assert.Equal("started", o.Outcome_);
        return (launcher, o.Session!);
    }

    [Fact]
    public async Task A_person_launch_records_its_coordinator_role_and_it_narrows_no_mcp_servers()
    {
        var (launcher, session) = await PersonLaunch(new { agent = "copilot", workspace = "API", prompt = "go", coordinatorRole = "quality-lead" });
        Assert.Equal("quality-lead", launcher.LaunchRole(session));
        Assert.DoesNotContain(logged, line => line.Contains("worker role MCP"));
        var (other, bare) = await PersonLaunch(new { agent = "copilot", workspace = "API", prompt = "go" });
        Assert.Null(other.LaunchRole(bare));
    }

    [Theory]
    [InlineData("agent:machine/55555555-5555-4555-8555-555555555555", false)]
    [InlineData("script:machine/dispatcher", true)]
    public async Task A_launch_by_an_agent_or_a_script_never_carries_a_coordinator_role(string principal, bool fleet)
    {
        var launcher = Launcher(Config());
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "go", CoordinatorRole = "quality-lead" };
        var result = fleet ? await launcher.LaunchFleetAsync(request, principal, _ => null) : await launcher.LaunchAsync(request, principal);
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("coordinator role", result.Reason);
        Assert.Empty(starter.Specs);
    }

    // fleet.resume reads the role from the launch record: a coordinator launched with one resumes, the same launch without it is refused.
    [Theory]
    [InlineData(true, "resumed")]
    [InlineData(false, "refused")]
    public async Task Fleet_resume_reads_the_coordinator_role_from_the_launch_record(bool withRole, string outcome)
    {
        var (launcher, session) = await PersonLaunch(withRole
            ? new { agent = "copilot", workspace = "API", prompt = "go", coordinatorRole = "quality-lead" }
            : new { agent = "copilot", workspace = "API", prompt = "go" });
        var folder = Path.Combine(f.Root, "resume-profile");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), JsonSerializer.Serialize(new
        {
            agents = new { enabled = true, resume = new { roles = new[] { "quality-lead" } } }, launch = new { enabled = true, workspaces = Array.Empty<string>() },
        }));
        var credentials = new AgentCredentials(folder, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var caller = credentials.Check(credentials.Mint(session, "person-launch", 100, DateTimeOffset.UtcNow, AgentProtocol.ScopeReadReplyLaunch),
            new([new PeerProcess(100, DateTimeOffset.UtcNow, "agent.exe")], true)).Accepted!;
        var resumed = new List<ResumeAsk>();
        var handler = new FleetResumeCommands(folder, credentials, launcher.LaunchRole, _ => true, _ => false,
            _ => new RestoreLaunch("copilot", "API", null, null, null, null, null, LaunchProtocol.Ask),
            (ask, _) => { resumed.Add(ask); return Task.FromResult(new LaunchResult(LaunchOutcome.Started, null) { Session = ask.Id }); });

        var receipt = await handler.HandleAsync(caller, JsonSerializer.SerializeToElement(new { version = 1, request = Guid.NewGuid(), session = Guid.NewGuid().ToString() }));

        Assert.Equal(outcome, receipt.Outcome);
        Assert.Equal(withRole ? 1 : 0, resumed.Count);
    }
}
