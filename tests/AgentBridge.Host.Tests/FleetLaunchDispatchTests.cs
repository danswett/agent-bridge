using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

[Trait("Category", "Process")]
public sealed class FleetLaunchDispatchTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Fleet_dispatch_requires_fresh_isolation_and_persists_its_grant_with_a_reply_only_worker(bool isolated, bool revoked, bool credentialFailure)
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var profile = Path.Combine(f.Root, "fleet-profile");
        var credentials = new AgentCredentials(profile, "machine");
        var config = Config(c => c with { Workspaces = [new("App", app, isolated)] });
        var launcher = Launcher(config, credentials: credentialFailure ? new RefusedCredentials() : credentials, agentWorktreeCheck: AgentLaunchWorktreeCheck.Check,
            agentWorktreeRefusalAudit: (_, _, _) => true);
        var result = await GateLaunchAudit.LaunchAsync(launcher,
            new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
                Workspace = "App", Permissions = LaunchProtocol.AllowAll },
            "script:machine/dispatcher", new AgentAuditLog(profile),
            new("local", "agent", "script:machine/dispatcher", null), fleet: true, refusal: () => revoked ? "grant off" : null);
        Assert.Equal(isolated && !revoked && !credentialFailure ? LaunchOutcome.Started : LaunchOutcome.Refused, result.Outcome);
        if (credentialFailure)
        {
            Assert.Contains("credential", result.Reason);
            Assert.Empty(starter.Specs);
            Assert.Empty(Directory.GetDirectories(config.WorktreeRoot));
            return;
        }
        if (revoked) { Assert.Contains("grant off", result.Reason); }
        if (!isolated || revoked) { Assert.Empty(starter.Specs); return; }
        Assert.NotEqual(app, Assert.Single(starter.Specs).WorkingDirectory);
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

    sealed class RefusedCredentials : ILaunchCredentials
    {
        public string? Prepare(string session, string launchId) => throw new NotSupportedException();
        public string? PrepareFleet(string session, string launchId) => throw new IOException("fixture credential save failure");
        public void Bind(string launchId, int processId, DateTimeOffset startedAt) => throw new NotSupportedException();
        public void Abandon(string launchId) { }
    }
}
