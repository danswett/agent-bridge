using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed class SessionRosterRestartResumeTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_undated_Claude_successor_prevents_duplicate_resume_even_after_a_host_restart(bool restart)
    {
        const string Id = "88888888-8888-4888-8888-888888888888";
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        ClaudeResumable(Id, app, "Resumed work");
        f.ClaudeRegistration(Id, 700, null);
        f.Processes.Run(AgentKind.Claude, 700, "claude.exe");
        var options = f.Options(stateFile: Path.Combine(f.Root, "sessions-listed.json"));
        var roster = new SessionSource(options, f.Processes, f.Time, hostId: "host-id");
        try
        {
            await roster.RefreshAsync();
            Assert.Equal(700, roster.Get(Id)!.ProcessId);
            var path = Path.Combine(f.ClaudeRegistrations, Id + ".json");
            var registration = JsonNode.Parse(File.ReadAllText(path))!;
            registration["Ended"] = true;
            File.WriteAllText(path, registration.ToJsonString());
            f.Processes.Exit(AgentKind.Claude, 700);
            f.Processes.RunUndated(AgentKind.Claude, 701, "claude.exe");
            f.ClaudeSessionFile(701, Id, Fixture.T0.AddMinutes(5));
            if (restart)
            {
                await roster.DisposeAsync();
                roster = new SessionSource(options, f.Processes, f.Time, hostId: "host-id");
            }
            await roster.RefreshAsync();
            var launcher = Launcher(Config(), discovery: roster);
            var result = await launcher.LaunchAsync(new LaunchRequest
            {
                Request = Guid.NewGuid().ToString(), Resume = Id,
            }, "phone");
            Assert.Equal(LaunchOutcome.Busy, result.Outcome);
            Assert.Empty(starter.Specs);
            Assert.DoesNotContain((await launcher.OptionsAsync()).Resumable, s => s.Id == Id);
            f.Processes.Exit(AgentKind.Claude, 701);
            await roster.RefreshAsync();
            Assert.Null(roster.Held);
            Assert.Contains((await launcher.OptionsAsync()).Resumable, s => s.Id == Id);
        }
        finally { await roster.DisposeAsync(); }
    }
}
