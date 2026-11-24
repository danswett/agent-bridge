using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

[Trait("Category", "Process")]
public sealed class FleetLaunchDispatchTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, "grant")]
    [InlineData(true, "credential")]
    [InlineData(true, "audit")]
    [InlineData(true, "command")]
    [InlineData(true, "held")]
    [InlineData(true, "activation")]
    public async Task Fleet_dispatch_requires_fresh_isolation_and_removes_unstarted_trees_on_every_refusal(bool isolated, string? failure)
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        locator.Installed["codex"] = @"C:\fake\codex.exe";
        Directory.CreateDirectory(f.CodexRegistrations);
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var profile = Path.Combine(f.Root, "fleet-profile");
        var credentials = new AgentCredentials(profile, "machine");
        if (failure == "credential") { Directory.CreateDirectory(Path.Combine(profile, AgentCredentials.FileName) + ".tmp"); }
        var config = Config(c => c with { Workspaces = [new("App", app, isolated)] });
        if (failure == "command") { config = config with { Agents = new Dictionary<string, AgentConfig> { ["claude"] = new() { ExtraArgs = [new string('x', 40000)] } } }; }
        var launcher = Launcher(config, credentials: credentials, agentWorktreeCheck: (workspace, made, _) =>
            {
                if (failure == "held") { f.Processes.Run(AgentKind.Codex, 970, "codex", DateTimeOffset.UtcNow); source.RefreshAsync().GetAwaiter().GetResult(); }
                return AgentLaunchWorktreeCheck.Check(workspace, made, "claude");
            },
            agentWorktreeRefusalAudit: (_, _, _) => true);
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = failure == "held" ? "codex" : "claude",
            Workspace = "App", Permissions = LaunchProtocol.AllowAll, Prompt = "fixture" };
        const string principal = "script:machine/dispatcher";
        var result = failure == "audit" ? await launcher.LaunchFleetAsync(request, principal, _ => null)
            : failure == "activation" ? await new SessionService(source, new("machine", "fixture", "windows", "1"), launcher: launcher)
                .LaunchFleetAsync(request, principal, new AgentAuditLog(profile), () => null, _ => null)
            : await GateLaunchAudit.LaunchAsync(launcher, request, principal, new AgentAuditLog(profile),
                new("local", "agent", principal, null), fleet: true, refusal: () => failure == "grant" ? "grant off" : null);
        var expected = failure == "held" ? LaunchOutcome.Busy : isolated && failure is null ? LaunchOutcome.Started : LaunchOutcome.Refused;
        Assert.True(result.Outcome == expected, $"{result.Outcome}: {result.Reason}");
        if (!isolated || failure is not null)
        {
            if (failure is "activation" or "grant") { Assert.Contains(failure == "activation" ? "admission" : "grant off", result.Reason); }
            Assert.Empty(starter.Specs);
            if (isolated && failure != "activation") { Assert.Empty(Directory.GetDirectories(config.WorktreeRoot)); }
            else { Assert.False(Directory.Exists(config.WorktreeRoot)); }
            return;
        }
        Assert.NotEqual(app, Assert.Single(starter.Specs).WorkingDirectory);
        Assert.True(Directory.Exists(Assert.Single(starter.Specs).WorkingDirectory));
        var process = source.ProcessOf(result.Session!);
        Assert.NotNull(process);
        Assert.True(launcher.IsFleetWorker(result.Session!, process));
        Assert.True(Launcher(config).IsFleetWorker(result.Session!, process));
        Assert.False(launcher.IsFleetWorker("another-session", process));
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, AgentCredentials.FileName)));
        Assert.Equal(AgentProtocol.ScopeReadReply, Assert.Single(saved.RootElement.GetProperty("records").EnumerateArray())
            .GetProperty("credential").GetProperty("scope").GetString());
        var rows = File.ReadAllLines(Assert.Single(Directory.GetFiles(Path.Combine(profile, "audit"), "launches-*.jsonl")));
        Assert.All(rows.Skip(1), row => Assert.Equal("fleet", JsonDocument.Parse(row).RootElement.GetProperty("grant").GetString()));
    }

    // #4318: the role a fleet launch records goes through RoleFor, never the request's own role: a launch by an agent (or script)
    // that is not itself recorded with that role records none, so no builder or dispatcher can mint a coordinator.
    [Theory]
    [InlineData("script:machine/dispatcher")]
    [InlineData("agent:machine/55555555-5555-4555-8555-555555555555")]
    public async Task A_fleet_launch_never_records_a_role_its_launcher_was_not_itself_recorded_with(string principal)
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var profile = Path.Combine(f.Root, "fleet-profile");
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }), credentials: new AgentCredentials(profile, "machine"),
            agentWorktreeCheck: (workspace, made, _) => AgentLaunchWorktreeCheck.Check(workspace, made, "claude"));
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "claude", Workspace = "App",
            Permissions = LaunchProtocol.AllowAll, Prompt = "fixture", Role = "quality-lead" };
        var result = await GateLaunchAudit.LaunchAsync(launcher, request, principal, new AgentAuditLog(profile),
            new("local", "agent", principal, null), fleet: true, refusal: () => null);

        Assert.True(result.Outcome == LaunchOutcome.Started, result.Reason);
        Assert.Null(launcher.LaunchRole(result.Session!));
    }

    // Own lane (NOTE: production's only fleet=true launch is a script's, so this agent-principal path is not reached today; see the PR body): a fleet worker a coordinator launches records the coordinator's ROLE (never its session); a launcher with no recorded role records none.
    [Theory]
    [InlineData("quality-lead", "quality-lead")]
    [InlineData(null, null)]
    public async Task A_fleet_launch_by_a_coordinator_records_the_workers_lane(string? launcherRole, string? lane)
    {
        const string coordinator = "99999999-9999-4999-8999-999999999999";
        locator.Installed["claude"] = @"C:ake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(f.Root, "profile")).FullName, "launch-provenance.json"), JsonSerializer.Serialize(
            new Dictionary<Guid, object> { [Guid.NewGuid()] = new { session = coordinator, process = new { id = 4242, startedAt = DateTimeOffset.Parse("2026-11-23T00:00:00Z") }, role = launcherRole } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var profile = Path.Combine(f.Root, "fleet-profile");
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }), credentials: new AgentCredentials(profile, "machine"),
            agentWorktreeCheck: (workspace, made, _) => AgentLaunchWorktreeCheck.Check(workspace, made, "claude"));
        var principal = "agent:machine/" + coordinator;
        var result = await GateLaunchAudit.LaunchAsync(launcher, new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "claude", Workspace = "App",
            Permissions = LaunchProtocol.AllowAll, Prompt = "fixture" }, principal, new AgentAuditLog(profile), new("local", "agent", principal, null), fleet: true, refusal: () => null);

        Assert.True(result.Outcome == LaunchOutcome.Started, result.Reason);
        Assert.Equal(lane, launcher.LaunchLane(result.Session!));
        Assert.Null(launcher.LaunchRole(result.Session!));   // the worker itself is never a coordinator
    }
}
