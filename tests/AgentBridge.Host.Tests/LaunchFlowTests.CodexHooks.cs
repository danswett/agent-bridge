using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

// A Codex launch on a profile whose hook setup record is absent (a migrated profile that never had
// Codex, #3648) is refused with a reason that says what to do, instead of starting a session that
// can never report (#3644). The trust prompt cannot be read, so it is only a hint in the text.
public sealed partial class LaunchFlowTests
{
    LaunchService LauncherWithHooks(Func<string, bool>? notSetUp) => new(new DeliveryGate(), source, probe, screens, starter, locator, new AgencyCatalog(agency),
        new LaunchServiceOptions { Time = clock, CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, CodexHome = f.CodexHome, Config = Config(), PlatformSupported = true, IsSystemDirectory = _ => false, HooksNotSetUp = notSetUp });

    [Fact]
    public async Task A_Codex_launch_with_no_hook_setup_on_the_profile_is_refused_with_codexHooksMissing_and_what_to_do()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        var asked = new List<string>();
        var launcher = LauncherWithHooks(id => { asked.Add(id); return true; });

        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "codex", Workspace = "App", Prompt = "add pagination" }, "phone");

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("codexHooksMissing", result.Reason);
        Assert.Contains("run Set up for Codex on this machine", result.Reason);
        Assert.Contains("hook trust prompt", result.Reason);
        Assert.Empty(starter.Specs);
        Assert.Equal(["codex"], asked.Distinct());
    }

    [Fact]
    public async Task A_Claude_launch_is_not_asked_about_hook_setup_and_starts_even_when_it_would_be_reported_absent()
    {
        locator.Installed["claude"] = @"C:\fake\claude.cmd";
        var asked = new List<string>();
        var launcher = LauncherWithHooks(id => { asked.Add(id); return true; });

        var launching = launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "claude", Workspace = "App", Prompt = "add pagination" }, "phone");
        await WaitAsync(() => Task.FromResult(starter.Pids.Count == 1));

        Assert.Empty(asked);
        Assert.Single(starter.Specs);
    }
}
