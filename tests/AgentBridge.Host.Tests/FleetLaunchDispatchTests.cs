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
    [InlineData(true, "start")]
    [InlineData(true, "uncertain")]
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
        if (failure == "start") { starter.Throws = new System.ComponentModel.Win32Exception("fixture"); }
        if (failure == "uncertain") { starter.Throws = new InvalidOperationException("fixture"); }
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
        var expected = failure == "held" ? LaunchOutcome.Busy : failure is "start" or "uncertain" ? LaunchOutcome.Failed
            : isolated && failure is null ? LaunchOutcome.Started : LaunchOutcome.Refused;
        Assert.True(result.Outcome == expected, $"{result.Outcome}: {result.Reason}");
        if (!isolated || failure is not null)
        {
            if (failure is "activation" or "grant") { Assert.Contains(failure == "activation" ? "admission" : "grant off", result.Reason); }
            Assert.Empty(starter.Specs);
            if (isolated && failure is not ("activation" or "grant" or "audit")) { Assert.Empty(Directory.GetDirectories(config.WorktreeRoot)); }
            else { Assert.False(Directory.Exists(config.WorktreeRoot)); }
            if (failure is "command" or "held" or "credential" or "start")
            {
                var (evidence, complete) = GateAuditProjection.Load(profile);
                Assert.True(complete);
                var terminal = Assert.Single(evidence, row => row.Gate?.Phase == "terminal");
                Assert.Equal(failure == "start" ? "failed" : "refused", terminal.Outcome);
                var replay = await GateLaunchAudit.LaunchAsync(Launcher(config), request, principal, new AgentAuditLog(profile),
                    new("local", "agent", principal, null), fleet: true);
                Assert.Equal(failure == "start" ? LaunchOutcome.Failed : LaunchOutcome.Refused, replay.Outcome);
                Assert.Empty(starter.Specs);
            }
            if (failure == "uncertain")
            {
                var (evidence, complete) = GateAuditProjection.Load(profile);
                Assert.True(complete);
                Assert.Single(evidence, row => row.Gate?.Phase == "intent");
                Assert.DoesNotContain(evidence, row => row.Gate?.Phase == "terminal");
            }
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

    [Fact]
    public async Task A_proven_prestart_refusal_releases_the_live_slot_for_the_next_request()
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var profile = Path.Combine(f.Root, "fleet-profile");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, HostSettings.FileName),
            """{"agents":{"enabled":true,"limits":{"liveFleetWorkers":1}}}""");
        var checks = 0;
        var config = Config(c => c with { Workspaces = [new("App", app, true)] });
        var launcher = Launcher(config, credentials: new AgentCredentials(profile, "machine"),
            agentWorktreeCheck: (workspace, made, agent) => ++checks == 1 ? "fixture refusal" : AgentLaunchWorktreeCheck.Check(workspace, made, agent),
            agentWorktreeRefusalAudit: (_, _, _) => true);
        var service = new SessionService(source, new("machine", "fixture", "windows", "1"), launcher: launcher);
        var audit = new AgentAuditLog(profile);
        Task<LaunchResult> Launch() => service.LaunchFleetAsync(new LaunchRequest { Request = Guid.NewGuid().ToString("D"),
            Agent = "claude", Workspace = "App", Permissions = LaunchProtocol.AllowAll }, "script:machine/dispatcher", audit, () => null, _ => null);

        Assert.Equal(LaunchOutcome.Refused, (await Launch()).Outcome);
        Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
        Assert.Single(starter.Specs);
    }
}
