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

    Task<int> Run(string verb, string? agentHaBridge = null, bool keepAwake = false)
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
        Assert.Equal(0, await Run("status", agentHaBridge: "agent-ha-bridge's own Dev Box keep-awake is on here"));

        Assert.Contains("This machine is a Dev Box: fake-box, in project FakeProject", output.ToString());
        Assert.Contains("Keep-awake is off", output.ToString());
        Assert.Contains("agent-ha-bridge: agent-ha-bridge's own Dev Box keep-awake is on here", output.ToString());
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_dry_run_reads_the_pending_stop_and_says_what_it_would_clear_but_moves_nothing()
    {
        // Even beside agent-ha-bridge's own keep-awake: reading is not a second writer.
        Assert.Equal(0, await Run("dry-run", agentHaBridge: "agent-ha-bridge's own Dev Box keep-awake is on here"));

        Assert.Equal(HttpMethod.Get, Assert.Single(calls).Method);
        Assert.Contains(logged, l => l == "keep-awake (dry run): dry-run: would clear 'idle-stopondisconnect'");
        Assert.Equal(2, await Run("pass"));
        Assert.Single(calls);
    }
}
