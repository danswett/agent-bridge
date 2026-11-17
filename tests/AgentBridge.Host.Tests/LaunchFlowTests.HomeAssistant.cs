using System.Runtime.Versioning;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

[CollectionDefinition("Launch HA environment", DisableParallelization = true)]
public sealed class LaunchHaEnvironmentCollection;

[Collection("Launch HA environment")]
[Trait("Category", "Network")]
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
            Assert.Equal(UrlVariable + "=" + url, ChildUrlEntry(spec));
            Assert.Equal(f.CopilotHome, spec.Environment["COPILOT_HOME"]);
            Assert.Contains("controlled launch", spec.Arguments);
            Assert.False(string.IsNullOrEmpty(spec.Environment[Launchers.SessionVariable]));
            Assert.Equal("synthetic-agent-principal", spec.Environment[Launchers.TokenVariable]);
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
            var spec = Assert.Single(starter.Specs);
            var environment = spec.Environment!;
            Assert.DoesNotContain(UrlVariable, environment.Keys);
            Assert.Equal(inherited is null ? null : UrlVariable + "=" + inherited, ChildUrlEntry(spec));
        });
    }

    LaunchService HaLauncher(bool enabled, string? url)
    {
        var (ha, _) = HaSettings.Read(JsonSerializer.SerializeToElement(new { enabled, baseUrl = url }));
        var options = new LaunchServiceOptions
        {
            CopilotHome = f.CopilotHome,
            ClaudeHome = f.ClaudeHome,
            CodexHome = f.CodexHome,
            Config = Config(),
            PlatformSupported = true,
            HomeAssistantEnabled = ha.Enabled || ha.EnabledRequested,
            HomeAssistantBaseUrl = ha.BaseUrl,
            PollInterval = TimeSpan.FromMilliseconds(25),
            IsSystemDirectory = _ => false,
        };
        return new LaunchService(source, probe, screens, starter, locator, new AgencyCatalog(agency), options,
            log: logged.Enqueue, credentials: new HaTestCredentials(), account: new HaTestAccount());
    }

    static string? ChildUrlEntry(StartSpec spec) => WindowsAgentStarter.EnvironmentBlock(spec.Environment!)
        .Split('\0').SingleOrDefault(e => e.StartsWith(UrlVariable + "=", StringComparison.OrdinalIgnoreCase));

    sealed class HaTestCredentials : ILaunchCredentials
    {
        public string? Prepare(string session, string launchId) => "synthetic-agent-principal";
        public void Bind(string launchId, int processId, DateTimeOffset startedAt) { }
        public void Abandon(string launchId) { }
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
