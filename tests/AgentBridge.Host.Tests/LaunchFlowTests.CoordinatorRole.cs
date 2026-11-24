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
    async Task<(LaunchService Launcher, string Session)> PersonLaunch(object request, IReadOnlyList<string>? listed = null)
    {
        var launcher = Launcher(Config(), coordinatorRoles: listed);
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

    // Typo catching: with agents.resume.roles listing roles, a person's launch naming another one is refused, saying what is allowed; an empty
    // list keeps today's behaviour (the role is recorded and has no authority until a person lists it).
    [Fact]
    public async Task A_person_launch_naming_an_unlisted_coordinator_role_is_refused_when_roles_are_listed()
    {
        var listed = new[] { "quality-lead", "sre" };
        var launcher = Launcher(Config(), coordinatorRoles: listed);
        var typo = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "go", CoordinatorRole = "qualty-lead" }, "app");
        Assert.Equal(LaunchOutcome.Refused, typo.Outcome);
        Assert.Contains("agents.resume.roles", typo.Reason);
        Assert.Contains("quality-lead, sre", typo.Reason);
        Assert.Empty(starter.Specs);
        var (ok, session) = await PersonLaunch(new { agent = "copilot", workspace = "API", prompt = "go", coordinatorRole = "sre" }, listed);
        Assert.Equal("sre", ok.LaunchRole(session));
    }

    [Fact]
    public async Task With_no_roles_listed_a_person_launch_still_records_the_role_it_names()
    {
        var (launcher, session) = await PersonLaunch(new { agent = "copilot", workspace = "API", prompt = "go", coordinatorRole = "anything-at-all" }, []);
        Assert.Equal("anything-at-all", launcher.LaunchRole(session));
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
            agents = new { enabled = true, resume = new { roles = new[] { "quality-lead" } } }, launch = new { enabled = true, workspaces = new[] { new { label = "API", path = folder } } },
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

    // R11's rule 2: the role is also on the launch's audit rows (intent and terminal), written only for a person's launch.
    [Fact]
    public async Task A_person_launch_with_a_coordinator_role_writes_it_on_both_audit_rows_and_one_without_writes_none()
    {
        var profile = Path.Combine(f.Root, "audit-role", "default");
        var launcher = Launcher(Config());
        var ingress = new GateLaunchIngress("local", "person-client", "person:local", null);
        var withRole = await GateLaunchAudit.LaunchAsync(launcher, new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "go", CoordinatorRole = "quality-lead" },
            "local", new AgentAuditLog(profile), ingress);
        var without = await GateLaunchAudit.LaunchAsync(launcher, new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "again" },
            "local", new AgentAuditLog(profile), ingress);
        Assert.Equal((LaunchOutcome.Started, LaunchOutcome.Started), (withRole.Outcome, without.Outcome));

        var (rows, complete) = GateAuditProjection.Load(profile);
        Assert.True(complete);
        var launches = rows.Where(r => r.Action == "session.launch" && r.Gate is not null).ToArray();
        Assert.Equal(new string?[] { "quality-lead", "quality-lead", null, null }, launches.Select(r => r.Gate!.CoordinatorRole).ToArray());
        Assert.True(new AgentAuditLog(profile).Verify(launch: true).Intact);
    }

    [Fact]
    public void A_coordinator_role_is_valid_on_a_persons_launch_rows_only()
    {
        var id = Guid.NewGuid();
        var person = new GateAuditMetadata(1, id, Guid.Empty, 1, "intent", "local", "person-client", id, "none", CoordinatorRole: "sre");
        Assert.True(person.Valid("session.launch", id.ToString(), "reserved"));
        Assert.False((person with { ActorKind = "agent" }).Valid("session.launch", id.ToString(), "reserved"));
        Assert.False(person.Valid("session.stop", id.ToString(), "reserved"));
        Assert.False((person with { CoordinatorRole = "not a role" }).Valid("session.launch", id.ToString(), "reserved"));
        Assert.False((person with { Phase = "control" }).Valid("session.launch", "profile", "sealed"));
        // An absent role leaves the row's bytes as they were written before the field existed.
        Assert.DoesNotContain("oordinatorRole", System.Text.Json.JsonSerializer.Serialize(person with { CoordinatorRole = null }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }
}
