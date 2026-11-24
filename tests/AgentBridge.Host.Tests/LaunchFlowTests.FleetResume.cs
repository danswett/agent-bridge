using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

// #4318: a fleet worker resumed in place stays a fleet worker, with what it was launched with and never wider.
public sealed partial class LaunchFlowTests
{
    const string FleetId = "79797979-7979-4979-8979-797979797979";
    const string Coordinator = "agent:machine/99999999-9999-4999-8999-999999999999";

    // The launch record of the session's first run: its process has since ended.
    void Earlier(string? grant, string? role = null, string? lane = null) => File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(f.Root, "profile")).FullName, "launch-provenance.json"), System.Text.Json.JsonSerializer.Serialize(
        new Dictionary<Guid, object> { [Guid.NewGuid()] = new { session = FleetId, process = new { id = 4242, startedAt = DateTimeOffset.Parse("2026-11-23T00:00:00Z") }, grant, role, lane } },
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

    [Fact]
    public async Task A_fleet_resume_restarts_the_same_session_as_a_fleet_worker_with_its_recorded_settings()
    {
        Resumable(FleetId, app, "A builder");
        Earlier("fleet", "quality-lead", lane: "engineering-lead");
        var profile = Path.Combine(f.Root, "fleet-profile");
        Directory.CreateDirectory(profile);
        var credentials = new AgentCredentials(profile, "machine");
        var launcher = Launcher(Config(c => c with { Permissions = LaunchProtocol.Ask, ResumeCount = 0 }), credentials: credentials);

        var result = await launcher.ResumeFleetAsync(new ResumeAsk(FleetId, AgentKind.Copilot,
            new RestoreLaunch("copilot", "App", null, null, null, null, null, LaunchProtocol.AllowAll), Coordinator));

        Assert.True(result.Outcome == LaunchOutcome.Started, result.Reason + " / " + string.Join(" | ", logged));
        Assert.Equal(FleetId, result.Session);
        Assert.Equal(["--session-id", FleetId, "--banner", "--allow-all"], Assert.Single(starter.Specs).Arguments);
        var process = source.ProcessOf(FleetId);
        Assert.NotNull(process);
        Assert.True(launcher.IsFleetWorker(FleetId, process));
        Assert.True(launcher.WasFleetLaunched(FleetId));
        Assert.Equal("engineering-lead", launcher.LaunchLane(FleetId));   // the resumed worker keeps the lane it was launched in
        Assert.Equal("quality-lead", launcher.LaunchRole(FleetId));   // a resumed coordinator keeps its recorded role and can resume others
        Assert.Empty(screens.Typed);
    }

    [Fact]
    public async Task A_fleet_resume_with_no_readable_recorded_launch_refuses_instead_of_using_defaults()
    {
        Resumable(FleetId, app, "A builder");
        var launcher = Launcher(Config(c => c with { Permissions = LaunchProtocol.AllowAll, ResumeCount = 0 }),
            credentials: new AgentCredentials(Directory.CreateDirectory(Path.Combine(f.Root, "fleet-profile")).FullName, "machine"));

        var result = await launcher.ResumeFleetAsync(new ResumeAsk(FleetId, AgentKind.Copilot, null, Coordinator));

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Empty(starter.Specs);
        Assert.False(launcher.WasFleetLaunched(FleetId));
    }

    [Fact]
    public async Task A_fleet_resume_of_a_session_a_person_launched_is_refused_before_anything_starts()
    {
        Resumable(FleetId, app, "A person's");
        Earlier(null);
        var launcher = Launcher(Config(c => c with { ResumeCount = 0 }),
            credentials: new AgentCredentials(Directory.CreateDirectory(Path.Combine(f.Root, "fleet-profile")).FullName, "machine"));

        var result = await launcher.ResumeFleetAsync(new ResumeAsk(FleetId, AgentKind.Copilot,
            new RestoreLaunch("copilot", "App", null, null, null, null, null, LaunchProtocol.Ask), Coordinator));

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Empty(starter.Specs);
        Assert.False(launcher.WasFleetLaunched(FleetId));
    }

    [Fact]
    public async Task A_restore_after_a_reboot_keeps_the_role_the_session_was_recorded_with()
    {
        Resumable(FleetId, app, "A coordinator");
        Earlier(null, "quality-lead");
        var launcher = Launcher(Config(c => c with { ResumeCount = 0 }));

        var result = await launcher.ResumeAsync(new ResumeAsk(FleetId, AgentKind.Copilot,
            new RestoreLaunch("copilot", "App", null, null, null, null, null, LaunchProtocol.Ask), RestoreRecorder.RestorePrincipal));

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal("quality-lead", launcher.LaunchRole(FleetId));
    }
}
