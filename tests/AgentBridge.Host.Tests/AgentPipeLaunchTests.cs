using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Launch;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// #3237 S3: session.launch from a person-started read+reply+launch agent over the pipe, by request id.
public sealed class AgentPipeLaunchTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-pipe-launch-").FullName;
    readonly FakeLauncher launcher = new();
    readonly string request = Guid.NewGuid().ToString("D");

    public AgentPipeLaunchTests() => File.WriteAllText(Path.Combine(folder, HostSettings.FileName), """
        {"agents":{"enabled":true,"launch":{"enabled":true},
          "limits":{"launchesPerOriginPerHour":3,"launchesPerMachinePerHour":10,"liveAgentLaunched":6,"agentLaunchedIdleHours":2},
          "inbound":{"local":{"launch":{"workspaces":["App"],"permissions":"ask","agents":["claude"]}}}},
         "launch":{"workspaces":[{"label":"App","path":"
        """.TrimEnd() + folder.Replace("\\", "\\\\") + "\",\"isolate\":true}]}}");

    public void Dispose() => Directory.Delete(folder, true);

    LaunchConfig Running(params string[] extraArgs) => LaunchConfig.Default with { Workspaces = [new("App", folder, true)],
        Agents = new Dictionary<string, AgentConfig> { ["claude"] = AgentConfig.Default with { ExtraArgs = extraArgs } } };

    sealed record Pipe(Func<IProtocolChannel, string, JsonElement, Task<object?>> Handle, Chan Channel, AgentCredentials Credentials);

    Pipe Open(string session = "parent", string scope = AgentProtocol.ScopeReadReplyLaunch, int depth = 0,
        string launch = "person-launch", AgentCredentials? credentials = null, (LaunchRequestStore?, string?)? store = null)
    {
        credentials ??= new AgentCredentials(folder, "machine");
        launcher.FleetConfig ??= Running();
        var channel = new Chan();
        var token = credentials.Mint(session, launch, 100, DateTimeOffset.UtcNow, scope, depth, depth > 0 ? ["ancestor"] : null);
        credentials.Accept(channel, credentials.Check(token,
            new ProcessAncestry([new PeerProcess(100, DateTimeOffset.UtcNow, "agent.exe")], true)).Accepted!);
        var (requests, problem) = store ?? LaunchRequestStore.Open(folder, TimeProvider.System);
        var audit = new AgentAuditLog(folder);
        var service = new SessionService(new FleetPublisherTests.Source(), new MachineInfo("machine", "Test", "windows", "0.1.0"), launcher: launcher);
        var agentLaunch = new AgentPipeLaunch(folder, credentials, service, audit, requests, problem);
        return new(LocalApi.Handler(service, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")),
            credentials: credentials, audit: audit, agentLaunch: agentLaunch), channel, credentials);
    }

    JsonElement Body(string? id = null, string prompt = "hello", string workspace = "App", string? permissions = LaunchProtocol.Ask) =>
        JsonSerializer.SerializeToElement(new LaunchRequest { Request = id ?? request, Agent = "claude", Workspace = workspace,
            Permissions = permissions, Prompt = prompt }, ProtocolJson.Options);

    static async Task<LaunchResult> Launch(Pipe pipe, JsonElement body) =>
        Assert.IsType<LaunchResult>(await pipe.Handle(pipe.Channel, LaunchProtocol.Launch, body));

    static async Task<string> Refusal(Pipe pipe, string op, JsonElement body) =>
        (await Assert.ThrowsAsync<InvalidOperationException>(() => pipe.Handle(pipe.Channel, op, body))).Message;

    string[] AuditRows() => Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl")
        .SelectMany(File.ReadAllLines).Where(l => l.Contains("\"refused\"")).ToArray();

    [Theory]
    [InlineData(AgentProtocol.ScopeRead, 0, "person-launch", "read+reply+launch")]
    [InlineData(AgentProtocol.ScopeReadReply, 0, "person-launch", "read+reply+launch")]
    [InlineData(AgentProtocol.ScopeReadReplyLaunch, 1, "person-launch", "agent-started")]
    [InlineData(AgentProtocol.ScopeReadReplyLaunch, 0, AgentCredentials.AdoptedLaunch, "adopted")]
    public async Task A_caller_without_scope_at_depth_or_adopted_is_refused_audited_and_launches_nothing(string scope, int depth, string launch, string reason)
    {
        var pipe = Open(scope: scope, depth: depth, launch: launch);

        Assert.Contains(reason, await Refusal(pipe, LaunchProtocol.Launch, Body()));
        Assert.Equal(0, launcher.Launches);
        Assert.Contains(AuditRows(), r => r.Contains(request) && r.Contains("agent:machine/parent"));
    }

    public static TheoryData<string, string> BadRequests => new()
    {
        { """{"request":"REQ","agent":"claude","workspace":"Private","permissions":"ask"}""", "approved workspace" },
        { """{"request":"REQ","agent":"claude","workspace":"App","permissions":"allow-all"}""", "ask" },
        { """{"request":"REQ","agent":"claude","workspace":"App","permissions":"ask","resume":"s"}""", "resume" },
        { """{"agent":"claude","workspace":"App","permissions":"ask"}""", "request id" },
        { """{"request":"not-a-uuid","agent":"claude","workspace":"App","permissions":"ask"}""", "request id" },
        { """{"request":"REQ","agent":"codex","workspace":"App","permissions":"ask"}""", "agents.inbound.local.launch.agents" },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task A_request_with_bad_fields_or_no_request_id_is_refused_and_audited(string json, string reason)
    {
        var pipe = Open();

        Assert.Contains(reason, await Refusal(pipe, LaunchProtocol.Launch, JsonDocument.Parse(json.Replace("REQ", request)).RootElement));
        Assert.Equal(0, launcher.Launches);
        Assert.NotEmpty(AuditRows());
    }

    [Fact]
    public async Task Extra_arguments_in_the_running_launch_settings_refuse_although_config_json_has_none()
    {
        launcher.FleetConfig = Running("--allow-all-tools");
        var pipe = Open();

        var refused = await Launch(pipe, Body());

        Assert.Equal(LaunchOutcome.Refused, refused.Outcome);
        Assert.Contains("extraArgs", refused.Reason);
        Assert.Equal(0, launcher.Launches);
        Assert.Contains(AuditRows(), r => r.Contains("extraArgs"));
        Assert.Equal(LaunchOutcome.Refused, (await Launch(pipe, Body())).Outcome);
    }

    [Fact]
    public async Task A_damaged_request_record_refuses_every_launch()
    {
        File.WriteAllText(Path.Combine(folder, LaunchRequestStore.FileName), "{ damaged");
        var pipe = Open(store: LaunchRequestStore.Open(folder, TimeProvider.System));

        Assert.Contains("can't be used", await Refusal(pipe, LaunchProtocol.Launch, Body()));
        Assert.Equal(0, launcher.Launches);
        Assert.Equal("{ damaged", File.ReadAllText(Path.Combine(folder, LaunchRequestStore.FileName)));
    }

    sealed class FakeLauncher : ISessionLauncher
    {
        public int Launches;
        public List<string> Peers { get; } = [];
        public LaunchConfig? FleetConfig { get; set; }
        public bool Available => true;
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("only the audited path launches");

        public Task<LaunchResult> LaunchAuditedAsync(LaunchRequest request, string peer, Func<Guid, Action<string>?> prepare,
            CancellationToken cancellationToken = default)
        {
            if (prepare(Guid.Parse(request.Request)) is not { } registered) { return Task.FromResult(new LaunchResult(LaunchOutcome.Refused, "audit")); }
            var session = $"session-{Interlocked.Increment(ref Launches)}";
            Peers.Add(peer);
            registered(session);
            return Task.FromResult(new LaunchResult(LaunchOutcome.Started) { Session = session });
        }
    }

    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "untrusted-wire-id";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
