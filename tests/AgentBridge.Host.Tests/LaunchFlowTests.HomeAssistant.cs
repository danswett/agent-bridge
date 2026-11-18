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
    [InlineData("{}")]
    [InlineData("""{"homeAssistant":{}}""")]
    [InlineData("""{"homeAssistant":{"enabled":false}}""")]
    public async Task Default_on_without_a_configured_HA_endpoint_preserves_native_launches(string json)
    {
        var (settings, _) = HostSettings.Parse(json, "fixture-config.json");
        var result = await HaLauncher(settings.HomeAssistant.RequiresReplyPath, settings.HomeAssistant.BaseUrl).LaunchAsync(
            new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" }, "phone");

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.DoesNotContain(UrlVariable, Assert.Single(starter.Specs).Environment!.Keys);
    }

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
    [InlineData("/api")]
    [InlineData("/api/")]
    public async Task An_HA_endpoint_with_an_api_suffix_is_not_given_a_second_one(string suffix)
    {
        var result = await HaLauncher(enabled: true, ConfiguredUrl.TrimEnd('/') + suffix).LaunchAsync(
            new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" }, "phone");

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(ConfiguredUrl.TrimEnd('/') + "/api", Assert.Single(starter.Specs).Environment![UrlVariable]);
    }

    [Theory]
    [InlineData("https://endpoint.example/ha?query=fixture")]
    [InlineData("https://endpoint.example/ha#fragment")]
    [InlineData("https://fixture@endpoint.example/ha")]
    public async Task An_HA_endpoint_with_a_query_fragment_or_userinfo_is_refused(string url)
    {
        var result = await HaLauncher(enabled: true, baseUrl: new Uri(url)).LaunchAsync(
            new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" }, "phone");

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("reply path is not ready", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("homeAssistant.baseUrl", result.Reason, StringComparison.Ordinal);
        Assert.Empty(starter.Specs);
        Assert.DoesNotContain(url, string.Join('\n', logged) + result.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Credential_and_starter_failures_never_report_the_HA_endpoint_or_token(bool credentialFailure)
    {
        const string token = "synthetic-agent-principal";
        var error = new IOException($"Could not use {ConfiguredUrl} with {token}");
        if (!credentialFailure) { starter.Throws = error; }
        var launcher = HaLauncher(enabled: true, ConfiguredUrl, credentialFailure ? error : null);

        var result = await launcher.LaunchAsync(
            new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" }, "phone");

        Assert.Equal(credentialFailure ? LaunchOutcome.Started : LaunchOutcome.Failed, result.Outcome);
        Assert.Contains(logged, line => line.Contains(credentialFailure ? "no agent credential was issued" : "could not be started", StringComparison.Ordinal));
        var reported = string.Join('\n', logged) + JsonSerializer.Serialize(result, ProtocolJson.Options);
        Assert.DoesNotContain(ConfiguredUrl, reported);
        Assert.DoesNotContain(token, reported);
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

    LaunchService HaLauncher(bool enabled, string? url, Exception? credentialError = null)
    {
        var (ha, _) = HaSettings.Read(JsonSerializer.SerializeToElement(new { enabled, baseUrl = url }));
        return HaLauncher(ha.RequiresReplyPath, ha.BaseUrl, credentialError);
    }

    LaunchService HaLauncher(bool enabled, Uri? baseUrl, Exception? credentialError = null)
    {
        var options = new LaunchServiceOptions
        {
            CopilotHome = f.CopilotHome,
            ClaudeHome = f.ClaudeHome,
            CodexHome = f.CodexHome,
            Config = Config(),
            PlatformSupported = true,
            HomeAssistantEnabled = enabled,
            HomeAssistantBaseUrl = baseUrl,
            PollInterval = TimeSpan.FromMilliseconds(25),
            IsSystemDirectory = _ => false,
        };
        return new LaunchService(source, probe, screens, starter, locator, new AgencyCatalog(agency), options,
            log: logged.Enqueue, credentials: new HaTestCredentials(credentialError), account: new HaTestAccount());
    }

    static string? ChildUrlEntry(StartSpec spec) => WindowsAgentStarter.EnvironmentBlock(spec.Environment!)
        .Split('\0').SingleOrDefault(e => e.StartsWith(UrlVariable + "=", StringComparison.OrdinalIgnoreCase));

    sealed class HaTestCredentials(Exception? error = null) : ILaunchCredentials
    {
        public string? Prepare(string session, string launchId) => error is null ? "synthetic-agent-principal" : throw error;
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
