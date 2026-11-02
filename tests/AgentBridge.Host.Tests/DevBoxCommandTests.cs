using AgentBridge.Host;
using AgentBridge.Host.Core.DevBox;
using Xunit;

// agent-bridge-host devbox: what the keep-awake would do here, at the machine, as agent-ha-bridge's
// -DryRun. Dev Center is a fake that records every call; every name is made up.
public sealed class DevBoxCommandTests : IDisposable
{
    static readonly DevBoxIdentity Box = new(new Uri("https://tenant-fake-dc.westus3.devcenter.azure.com"), "FakeProject", "fake-box");
    readonly string root = Path.Combine(Path.GetTempPath(), "abdbc-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<(HttpMethod Method, string Uri)> calls = [];
    readonly List<string> logged = [];
    readonly StringWriter output = new();
    readonly StringWriter error = new();

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    sealed class Fake(List<(HttpMethod, string)> calls) : IDevCenterHttp, IDevCenterToken
    {
        public Task<DevCenterResponse> SendAsync(HttpMethod method, Uri uri, string token, CancellationToken cancellationToken = default)
        {
            calls.Add((method, uri.ToString()));
            return Task.FromResult(new DevCenterResponse(200, method == HttpMethod.Get
                ? """{"value":[{"name":"idle-stopondisconnect","actionType":"Stop","next":{"scheduledTime":"2000-01-01T00:00:00Z"}}]}""" : ""));
        }

        public Task<string> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult("fake-token");
    }

    Task<int> Run(string verb, KeepAwakeOwner? agentHaBridge = null, bool keepAwake = false)
    {
        var settings = HostSettings.Default with { DevBox = new DevBoxSettings { KeepAwake = keepAwake } };
        var fake = new Fake(calls);
        return DevBoxCommands.RunAsync(["devbox", verb], settings, null, "host.log", () => (Box, null), () => agentHaBridge,
            () => new KeepAwakeService(new KeepAwakeOptions
            {
                Settings = settings.DevBox,
                FindIdentity = () => (Box, null),
                Token = fake,
                Http = fake,
                Lock = new FileKeepAwakeLock(Path.Combine(root, "lock")),
                AgentHaBridgeKeepAwake = () => agentHaBridge,
            }, logged.Add),
            output, error);
    }

    [Fact]
    public async Task Status_says_this_is_a_Dev_Box_and_who_keeps_it_awake_without_asking_Dev_Center()
    {
        Assert.Equal(0, await Run("status", agentHaBridge: new("agent-ha-bridge's own Dev Box keep-awake is on here")));

        Assert.Contains("This machine is a Dev Box: fake-box, in project FakeProject", output.ToString());
        Assert.Contains("Keep-awake is off", output.ToString());
        Assert.Contains("agent-ha-bridge: agent-ha-bridge's own Dev Box keep-awake is on here", output.ToString());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_dry_run_reads_the_pending_stop_and_says_what_it_would_clear_but_moves_nothing()
    {
        // Even beside agent-ha-bridge's own keep-awake: reading is not a second writer.
        Assert.Equal(0, await Run("dry-run", agentHaBridge: new("agent-ha-bridge's own Dev Box keep-awake is on here")));

        Assert.Equal(HttpMethod.Get, Assert.Single(calls).Method);
        Assert.Contains(logged, l => l == "keep-awake (dry run): dry-run: would clear 'idle-stopondisconnect'");
        Assert.Equal(2, await Run("pass"));
        Assert.Single(calls);
    }

    // A settings file the host cannot use must not read as "not a Dev Box": the app hides the
    // section for that, and the box would hibernate with nothing shown.
    [Fact]
    public async Task A_Dev_Box_whose_agent_settings_cannot_be_used_stays_in_the_hosts_status_with_its_alarm()
    {
        const string problem = "its devCenterUri is not a Dev Center address";
        await using var service = new KeepAwakeService(new KeepAwakeOptions
        {
            Settings = new DevBoxSettings { KeepAwake = true },
            FindIdentity = () => (null, problem),
            Token = new Fake(calls),
            Http = new Fake(calls),
            Lock = new FileKeepAwakeLock(Path.Combine(root, "lock")),
        }, logged.Add);

        await service.PassAsync();
        var status = DevBoxCommands.Status(service.State);

        Assert.NotNull(status);
        Assert.Equal(problem, status.Problem);
        Assert.True(status.Last!.Alarming);
        Assert.Equal(KeepAwakeResult.Failed, status.Last.Outcome);
        Assert.Empty(calls);
        // And none at all on a machine with no Dev Box agent.
        await using var elsewhere = new KeepAwakeService(new KeepAwakeOptions
        {
            Settings = new DevBoxSettings { KeepAwake = true },
            FindIdentity = () => (null, null),
            Token = new Fake(calls),
            Http = new Fake(calls),
            Lock = new FileKeepAwakeLock(Path.Combine(root, "lock")),
        });
        Assert.Null(DevBoxCommands.Status(elsewhere.State));
    }

    [Fact]
    public async Task Status_says_when_agent_ha_bridges_keep_awake_could_not_be_checked_that_the_Dev_Box_may_hibernate()
    {
        Assert.Equal(0, await Run("status", agentHaBridge: new("Task Scheduler could not be read", Known: false)));

        Assert.Contains("THIS DEV BOX MAY HIBERNATE WHEN DISCONNECTED", output.ToString());
    }
}
