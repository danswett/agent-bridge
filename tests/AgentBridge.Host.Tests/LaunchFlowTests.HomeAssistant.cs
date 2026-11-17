using System.Runtime.Versioning;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

[CollectionDefinition("Launch HA environment", DisableParallelization = true)]
public sealed class LaunchHaEnvironmentCollection;

[Collection("Launch HA environment")]
[SupportedOSPlatform("windows")]
public sealed class LaunchHaEnvironmentFlowTests : LaunchFlowFixture
{
    const string UrlVariable = "AGENT_HA_URL";
    const string ConfiguredUrl = "https://receiving-host.example/ha/";

    [Theory]
    [InlineData(null)]
    [InlineData("https://stale-sender.example/api")]
    public async Task A_remote_launch_overlays_the_receiving_hosts_endpoint_without_user_scope_dependency(string? inherited)
    {
        await WithInheritedUrl(inherited, async () =>
        {
            host.Hub.Handler = new SessionService(source,
                new MachineInfo("host-id", machineName, "windows", "0.1.0"),
                launcher: HaLauncher(enabled: true, ConfiguredUrl)).HandleAsync;
            var channel = await OpenAsync();

            var result = await LaunchAsync(channel, new { agent = "copilot", workspace = "App", prompt = "controlled launch" });

            Assert.Equal("started", result.Outcome_);
            var spec = Assert.Single(starter.Specs);
            Assert.True(spec.Environment!.TryGetValue(UrlVariable, out var url));
            Assert.Equal(ConfiguredUrl.TrimEnd('/') + "/api", url);
            var block = WindowsAgentStarter.EnvironmentBlock(spec.Environment);
            Assert.Contains(UrlVariable + "=" + url + "\0", block, StringComparison.Ordinal);
            if (inherited is not null) { Assert.DoesNotContain(inherited, block, StringComparison.Ordinal); }
            Assert.Equal(f.CopilotHome, spec.Environment["COPILOT_HOME"]);
            Assert.Contains("controlled launch", spec.Arguments);
            Assert.False(string.IsNullOrEmpty(spec.Environment[Launchers.SessionVariable]));
            Assert.DoesNotContain("AGENT_HA_AGENT_TOKEN", spec.Environment.Keys);
        });
    }

    [Fact]
    public async Task An_HA_enabled_launch_without_an_endpoint_refuses_instead_of_claiming_reply_readiness()
    {
        await WithInheritedUrl("https://stale-sender.example/api", async () =>
        {
            host.Hub.Handler = new SessionService(source,
                new MachineInfo("host-id", machineName, "windows", "0.1.0"),
                launcher: HaLauncher(enabled: true, url: null)).HandleAsync;
            var channel = await OpenAsync();

            var result = await LaunchAsync(channel, new { agent = "copilot", workspace = "App" });

            Assert.Equal("refused", result.Outcome_);
            Assert.Contains("reply path is not ready", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("homeAssistant.baseUrl", result.Reason, StringComparison.Ordinal);
            Assert.Empty(starter.Specs);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://unrelated.example/api")]
    public async Task A_non_HA_launch_leaves_its_environment_unchanged(string? inherited)
    {
        await WithInheritedUrl(inherited, async () =>
        {
            var result = await HaLauncher(enabled: false, ConfiguredUrl).LaunchAsync(
                new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" }, "phone");

            Assert.Equal(LaunchOutcome.Started, result.Outcome);
            var environment = Assert.Single(starter.Specs).Environment!;
            Assert.DoesNotContain(UrlVariable, environment.Keys);
            var block = WindowsAgentStarter.EnvironmentBlock(environment);
            if (inherited is null) { Assert.DoesNotContain(UrlVariable + "=", block, StringComparison.Ordinal); }
            else { Assert.Contains(UrlVariable + "=" + inherited + "\0", block, StringComparison.Ordinal); }
        });
    }

    LaunchService HaLauncher(bool enabled, string? url)
    {
        // A serialized options fixture lets the tests-first head compile before the new options exist.
        var options = JsonSerializer.Deserialize<LaunchServiceOptions>(JsonSerializer.Serialize(new
        {
            CopilotHome = f.CopilotHome,
            ClaudeHome = f.ClaudeHome,
            CodexHome = f.CodexHome,
            Config = Config(),
            PlatformSupported = true,
            HomeAssistantEnabled = enabled,
            HomeAssistantBaseUrl = url,
            PollInterval = TimeSpan.FromMilliseconds(25),
        }))!;
        typeof(LaunchServiceOptions).GetProperty(nameof(LaunchServiceOptions.IsSystemDirectory))!
            .SetValue(options, (Func<string, bool>)(_ => false));
        return new LaunchService(source, probe, screens, starter, locator, new AgencyCatalog(agency), options,
            log: logged.Enqueue, account: new HaTestAccount());
    }

    sealed class HaTestAccount : ILaunchAccount
    {
        public Task<LaunchAccount> CheckAsync(string? expectedLogin, CancellationToken cancellationToken) =>
            Task.FromResult(LaunchAccount.Allowed("synthetic-launch-account"));
    }

    static async Task WithInheritedUrl(string? value, Func<Task> run)
    {
        var before = Environment.GetEnvironmentVariable(UrlVariable, EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(UrlVariable, value, EnvironmentVariableTarget.Process);
            await run();
        }
        finally { Environment.SetEnvironmentVariable(UrlVariable, before, EnvironmentVariableTarget.Process); }
    }
}
