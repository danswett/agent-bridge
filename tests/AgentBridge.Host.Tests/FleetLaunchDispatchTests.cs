using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Launch;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

[Trait("Category", "Process")]
public sealed class FleetLaunchDispatchTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

// P1 of the #4459 read, through the real flow: a worker that exits before its session registers frees its reservation, so a run of such failures never
    // starves the dispatcher (6 failed launches, then more, with the default 6 live and 8 an hour).
    [Fact]
    public async Task A_fleet_worker_that_exits_before_it_registers_frees_its_slot()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        starter.Registers = false;
        starter.ExitsAtOnce = true;
        var profile = Path.Combine(f.Root, "fleet-profile");
        var config = Config(c => c with { Workspaces = [new("App", app, true)] });
        var launcher = Launcher(config, credentials: new AgentCredentials(profile, "machine"),
            agentWorktreeCheck: (workspace, made, _) => AgentLaunchWorktreeCheck.Check(workspace, made, "claude"), agentWorktreeRefusalAudit: (_, _, _) => true);
        const string principal = "script:machine/dispatcher";
        for (var i = 0; i < 9; i++)
        {
            var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "claude", Workspace = "App", Permissions = LaunchProtocol.AllowAll, Prompt = "fixture" };
            var result = await GateLaunchAudit.LaunchAsync(launcher, request, principal, new AgentAuditLog(profile), new("local", "agent", principal, null), fleet: true, refusal: () => null);
            Assert.True(result.Outcome == LaunchOutcome.Failed, $"launch {i + 1}: {result.Outcome}: {result.Reason}");
        }
    }

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

    // #3644: a Codex fleet worker meets the worktree check with its own configuration list. The check's answer is read here, not
    // only the launch's outcome: what follows the check (a Codex launch's credential) is not this check's to decide.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_codex_fleet_worker_passes_the_worktree_check_unless_the_repository_holds_codex_configuration(bool configured)
    {
        locator.Installed["codex"] = @"C:\fake\codex.exe";
        Directory.CreateDirectory(f.CodexRegistrations);
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        if (configured)
        {
            Directory.CreateDirectory(Path.Combine(app, ".codex"));
            File.WriteAllText(Path.Combine(app, ".codex", "config.toml"), "approval_policy = \"never\"\n");
        }
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        (starter.Registers, starter.ExitsAtOnce) = (false, true);
        var profile = Path.Combine(f.Root, "fleet-profile");
        var config = Config(c => c with { Workspaces = [new("App", app, true)] });
        var answers = new List<string?>();
        var launcher = Launcher(config, credentials: new AgentCredentials(profile, "machine"), agentWorktreeCheck: (workspace, made, agent) =>
            { answers.Add(AgentLaunchWorktreeCheck.Check(workspace, made, agent, f.Root)); return answers[^1]; },
            agentWorktreeRefusalAudit: (_, _, _) => true);
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "codex", Workspace = "App", Permissions = LaunchProtocol.AllowAll, Prompt = "fixture" };
        const string principal = "script:machine/dispatcher";
        var result = await GateLaunchAudit.LaunchAsync(launcher, request, principal, new AgentAuditLog(profile), new("local", "agent", principal, null), fleet: true, refusal: () => null);

        var answer = Assert.Single(answers);
        if (!configured) { Assert.Null(answer); return; }
        Assert.Contains("Codex could load", answer);
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Empty(starter.Specs);
        Assert.Empty(Directory.GetDirectories(config.WorktreeRoot));
    }

    // #3644: an agent's launch, a local grant's and a fleet.launch from another host reach the launcher without the fleet flag.
    // Codex (#4650: its credential is issued unresolved) starts only when one was issued, and not when issuing fails; Claude is the control.
    [Theory]
    [InlineData("codex", "local-grant", "allowAll", true)]
    [InlineData("codex", "local-grant", "allowAll", false)]
    [InlineData("codex", "local-grant", "ask", true)]
    [InlineData("codex", "local-grant", "ask", false)]
    [InlineData("codex", "fleet.launch", "allowAll", true)]
    [InlineData("codex", "fleet.launch", "allowAll", false)]
    [InlineData("codex", "fleet.launch", "ask", true)]
    [InlineData("codex", "fleet.launch", "ask", false)]
    [InlineData("codex", "agent", "ask", true)]
    [InlineData("codex", "agent", "ask", false)]
    [InlineData("claude", "local-grant", "allowAll", true)]
    [InlineData("claude", "fleet.launch", "allowAll", true)]
    [InlineData("claude", "agent", "ask", true)]
    public async Task An_agent_or_granted_launch_starts_only_with_a_worker_credential(string agent, string route, string permissions, bool issues)
    {
        locator.Installed[agent] = $@"C:\fake\{agent}.exe";
        Directory.CreateDirectory(f.CodexRegistrations);
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var profile = Directory.CreateDirectory(Path.Combine(f.Root, "grant-profile")).FullName;
        File.WriteAllText(Path.Combine(profile, HostSettings.FileName), JsonSerializer.Serialize(new
        {
            agents = new { enabled = true, launch = new { enabled = true },
                limits = new { launchesPerOriginPerHour = 3, launchesPerMachinePerHour = 10, liveAgentLaunched = 6, agentLaunchedIdleHours = 2 },
                inbound = new { local = new { launch = new { workspaces = new[] { "App" }, permissions = "ask", agents = new[] { agent }, allowAllLaunch = true } } } },
            launch = new { workspaces = new[] { new { label = "App", path = app, isolate = true } } },
        }));
        var (config, credentials) = (Config(c => c with { Workspaces = [new("App", app, true)] }), new AgentCredentials(profile, "machine"));
        var answers = new List<string?>();
        var launcher = Launcher(config, credentials: credentials, agentWorktreeCheck: (workspace, made, id) =>
            { answers.Add(AgentLaunchWorktreeCheck.Check(workspace, made, id, f.Root)); return answers[^1]; },
            agentWorktreeRefusalAudit: (_, _, _) => true);
        launcher.NamedLaunch = (_, _, run) => run();
        (starter.Registers, starter.ExitsAtOnce) = (false, true);
        using var key = P256KeyPair.Generate();
        var ((requests, problem), device) = (LaunchRequestStore.Open(profile, TimeProvider.System), new string('a', 32));
        var pipe = new AgentPipeLaunch(profile, credentials, new SessionService(source, new("machine", "fixture", "windows", "1"), launcher: launcher),
            new AgentAuditLog(profile) { DeviceKey = key }, requests, problem,
            fleetGrants: new FleetGrantLaunch(profile, key, () => "123", device, credentials, launcher));
        var now = DateTimeOffset.UtcNow;
        var token = credentials.Mint(Guid.NewGuid().ToString("D"), "person-launch", 100, now, AgentProtocol.ScopeReadReplyLaunch);
        var caller = credentials.Check(token, new ProcessAncestry([new PeerProcess(100, now, "agent.exe")], true)).Accepted!;
        var subject = route == "fleet.launch"
            ? new GrantSubject("123", new('b', 32), Guid.NewGuid().ToString("D"), "AQEBAQEBAQEBAQEBAQEBAQ", new(4242, now.AddMinutes(-5)))
            : new GrantSubject("123", device, caller.Session, caller.Id, new(caller.RootPid, caller.RootStartUtc));
        var offered = (await launcher.OptionsAsync()).Agents.Single(a => a.Id == agent);
        var scope = new GrantScope([device], ["App"], [agent], [.. offered.Models], ["default"], [.. offered.Efforts], [.. offered.Contexts], ["ask", "allowAll"]);
        var grant = new GrantDocument(1, Guid.NewGuid(), "123", new("123", device, "AQ", now, new('a', 64)), new(device, Path.GetFileName(profile)),
            [subject], ["launch"], scope, new(3, 3, 2), now, 1, now.AddHours(8), AllowAllLaunch: true);
        Assert.True(new GrantStore(profile, key, new(profile), scope).Issue(grant).Usable);
        var input = new FleetLaunchInput(1, Guid.NewGuid(), grant.Id, "SG Builder 1", "App", agent, Model: offered.Models[0],
            Effort: offered.Efforts[0], Context: offered.Contexts[0], Permissions: permissions, Prompt: "fixture");
        var request = new LaunchRequest { Request = input.Request.ToString("D"), Agent = agent, Workspace = "App", Permissions = permissions, Prompt = "fixture" };

        // The store cannot be saved from here on, so the launch's credential cannot be issued.
        if (!issues) { Directory.CreateDirectory(Path.Combine(profile, AgentCredentials.FileName) + ".tmp"); }

        var result = route switch
        {
            "fleet.launch" => (await pipe.LaunchGrantedRemoteAsync(subject, input)).Result,
            "local-grant" => (await pipe.LaunchGrantedAsync(caller, JsonSerializer.SerializeToElement(input, ProtocolJson.Options))).Result,
            _ => await pipe.LaunchAsync(caller, JsonSerializer.SerializeToElement(request, ProtocolJson.Options)),
        };

        // The launch got past the worktree check, so what follows is the credential rule's answer.
        Assert.True(answers.Count == 1 && answers[0] is null, $"{result.Outcome}: {result.Reason}; check: {string.Join(" | ", answers)}");
        if (issues)
        {
            Assert.NotNull(Assert.Single(starter.Specs).Environment![Launchers.TokenVariable]);
            Assert.NotEqual(LaunchOutcome.Refused, result.Outcome);
            return;
        }
        Assert.Empty(starter.Specs);
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Empty(Directory.GetDirectories(config.WorktreeRoot));
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
}
