using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

[Trait("Category", "Process")]
public sealed class FleetLaunchFailedStartTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    [InlineData(false, 2)]
    public async Task Never_started_launches_close_reservations_but_still_consume_hourly_admissions(bool missingCredential, int cap)
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var profile = Directory.CreateDirectory(Path.Combine(f.Root, "profile")).FullName;
        File.WriteAllText(Path.Combine(profile, HostSettings.FileName), JsonSerializer.Serialize(new
        { agents = new { enabled = true, limits = new { fleetPerHour = 8, liveFleetWorkers = cap } } }));
        var credentials = new NoFleetCredentials { IssueFleet = !missingCredential };
        var config = Config(c => c with { Workspaces = [new("App", app, true)] });
        SessionService Service() => new(source, new("machine", "fixture", "windows", "1"),
            launcher: Launcher(config, credentials: credentials, agentWorktreeCheck: AgentLaunchWorktreeCheck.Check,
                agentWorktreeRefusalAudit: (_, _, _) => true)) { Time = clock };
        var service = Service();
        var audit = new AgentAuditLog(profile, clock);
        Task<LaunchResult> Launch(SessionService current, LaunchRequest request) =>
            current.LaunchFleetAsync(request, "script:machine/dispatcher", audit, () => null, _ => null);
        static LaunchRequest Request() => new() { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
            Workspace = "App", Permissions = LaunchProtocol.AllowAll };
        starter.Throws = missingCredential ? null : new System.ComponentModel.Win32Exception("start failed");
        var failed = Enumerable.Range(0, cap).Select(_ => Request()).ToArray();
        foreach (var request in failed)
        {
            Assert.Equal(missingCredential ? LaunchOutcome.Refused : LaunchOutcome.Failed, (await Launch(service, request)).Outcome);
        }
        var (rows, complete) = GateAuditProjection.Load(profile);
        Assert.True(complete);
        var outcome = missingCredential ? "refused" : "failed";
        Assert.All(failed, request => Assert.Contains(rows, row => row.Request == request.Request
            && row.Gate is { Phase: "terminal" } gate && gate.ReasonCode == outcome && row.Outcome == outcome));
        Assert.Empty(starter.Specs);
        Assert.Empty(Directory.GetDirectories(config.WorktreeRoot));
        starter.Throws = null;
        credentials.IssueFleet = true;
        Assert.Equal(LaunchOutcome.Started, (await Launch(Service(), Request())).Outcome);
        Assert.Equal(cap + 1, GateAuditProjection.Load(profile).Rows.Count(row => row.Grant == "fleet"
            && row.Gate is { Phase: "intent" } && row.Outcome == "reserved"));
    }

    sealed class NoFleetCredentials : ILaunchCredentials
    {
        public bool IssueFleet;
        public string? Prepare(string session, string launchId) => null;
        public string? PrepareFleet(string session, string launchId) => IssueFleet ? "fixture-token" : null;
        public void Bind(string launchId, int processId, DateTimeOffset startedAt) { }
        public void Abandon(string launchId) { }
    }
}
