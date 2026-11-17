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
        var expected = failure == "held" ? LaunchOutcome.Busy
            : isolated && (failure is null or "activation") ? LaunchOutcome.Started : LaunchOutcome.Refused;
        Assert.True(result.Outcome == expected, $"{result.Outcome}: {result.Reason}");
        if (!isolated || failure is not (null or "activation"))
        {
            if (failure == "grant") { Assert.Contains("grant off", result.Reason); }
            Assert.Empty(starter.Specs);
            if (isolated) { Assert.Empty(Directory.GetDirectories(config.WorktreeRoot)); }
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
}
