using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Launch;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet.Grants;
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
        string launch = "person-launch", AgentCredentials? credentials = null, (LaunchRequestStore?, string?)? store = null,
        P256KeyPair? grantKey = null, bool legacy = false, bool grantOnly = false)
    {
        credentials ??= new AgentCredentials(folder, "machine");
        launcher.FleetConfig ??= Running();
        var channel = new Chan();
        var token = credentials.Mint(session, launch, 100, DateTimeOffset.UtcNow, scope, depth, depth > 0 ? ["ancestor"] : null);
        credentials.Accept(channel, credentials.Check(token,
            new ProcessAncestry([new PeerProcess(100, DateTimeOffset.UtcNow, "agent.exe")], true)).Accepted!);
        var (requests, problem) = store ?? LaunchRequestStore.Open(folder, TimeProvider.System);
        var audit = new AgentAuditLog(folder) { DeviceKey = grantKey };
        if (grantKey is not null) { Assert.Null(AuditRotationTests.Rotate(audit, grantKey, DateTimeOffset.UtcNow)); }
        var service = new SessionService(new FleetPublisherTests.Source(), new MachineInfo("machine", "Test", "windows", "0.1.0"), launcher: launcher);
        var agentLaunch = new AgentPipeLaunch(folder, credentials, service, audit, requests, problem, fleetGrants: grantKey is null ? null
            : new FleetGrantLaunch(folder, grantKey, () => "123", new('a', 32), credentials, service, launcher));
        return new(LocalApi.Handler(service, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")),
            credentials: credentials, audit: audit, agentLaunch: legacy || grantOnly ? null : agentLaunch, grantLaunch: grantOnly ? agentLaunch : null), channel, credentials);
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

    [Fact]
    public async Task A_non_grant_launch_without_a_production_pipe_keeps_the_legacy_person_only_refusal()
    {
        var pipe = Open(legacy: true);
        Assert.Contains("for the person", await Refusal(pipe, LaunchProtocol.Launch, Body()));
        Assert.Equal(0, launcher.Launches);
        Assert.NotEmpty(AuditRows());
    }

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

    (Pipe Pipe, GrantDocument Grant, GrantStore Store) Granted(P256KeyPair key, bool grantOnly = false)
    {
        var pipe = Open(session: Guid.NewGuid().ToString(), grantKey: key, grantOnly: grantOnly);
        var caller = pipe.Credentials.Of(pipe.Channel)!;
        var now = DateTimeOffset.UtcNow;
        var scope = new GrantScope([new('a', 32)], ["App"], ["claude"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
        var store = new GrantStore(folder, key, new(folder), scope);
        var grant = new GrantDocument(1, Guid.NewGuid(), "123", new("123", new('a', 32), "AQ", now, new('a', 64)),
            new(new('a', 32), Path.GetFileName(folder)), [new("123", new('a', 32), caller.Session, caller.Id, new(caller.RootPid, caller.RootStartUtc))],
            ["launch"], scope, new(3, 3, 2), now, 1, now.AddHours(8));
        Assert.True(store.Issue(grant).Usable);
        return (pipe, grant, store);
    }
    JsonElement GrantBody(GrantDocument grant, string workspace = "App", string prompt = "hello") =>
        JsonSerializer.SerializeToElement(new FleetLaunchInput(1, Guid.Parse(request), grant.Id, "SG Builder 266", workspace, "claude",
            Model: "small", Effort: "medium", Context: "default", Permissions: "ask", Prompt: prompt), ProtocolJson.Options);
    static async Task<FleetLaunchReceipt> GrantedLaunch(Pipe pipe, JsonElement body) =>
        Assert.IsType<FleetLaunchReceipt>(await pipe.Handle(pipe.Channel, LaunchProtocol.Launch, body));

    [Fact]
    public async Task Production_grant_only_wiring_keeps_ordinary_refusal_and_uses_the_existing_grant_pipe()
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, _) = Granted(key, grantOnly: true);
        Assert.Contains("for the person", await Refusal(pipe, LaunchProtocol.Launch, Body()));
        Assert.Equal(0, launcher.Launches);
        var result = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(1, launcher.Launches);
        Assert.False(result.Registered);
        Assert.Equal(LaunchOutcome.Unconfirmed, result.Result.Outcome);
    }

    [Fact]
    public async Task An_unavailable_shared_request_store_does_not_change_the_ordinary_legacy_route()
    {
        var pipe = Open(store: (null, "not open"), grantOnly: true);
        Assert.Contains("for the person", await Refusal(pipe, LaunchProtocol.Launch, Body()));
        Assert.Equal(0, launcher.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grant_scope_and_excluded_launch_names_are_checked_on_the_receiver(bool excluded)
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, _) = Granted(key);
        if (excluded)
        {
            var path = Path.Combine(folder, HostSettings.FileName);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"inbound\":{", "\"inbound\":{\"exclude\":{\"sessions\":[\"SG Builder 266\"]},"));
        }
        Assert.Equal("scope", (await GrantedLaunch(pipe, GrantBody(grant, excluded ? "App" : "Private"))).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task Revocation_while_queued_refuses_before_start_and_a_non_grant_launch_stays_unchanged()
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, store) = Granted(key);
        launcher.BeforeDispatch = () => Assert.True(store.Revoke(grant.Id, 1).Usable);
        Assert.Equal("grant-revoked", (await GrantedLaunch(pipe, GrantBody(grant))).Reason);
        Assert.Equal(0, launcher.Launches);
        Assert.Equal(LaunchOutcome.Started, (await Launch(pipe, Body(Guid.NewGuid().ToString()))).Outcome);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task Exact_grant_duplicates_replay_after_revocation_but_changed_payloads_refuse()
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, store) = Granted(key);
        var first = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.True(store.Revoke(grant.Id, 1).Usable);
        var duplicate = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(first.Permissions, duplicate.Permissions);
        Assert.Equal(first.Result.Outcome, duplicate.Result.Outcome);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal("duplicate-id", (await GrantedLaunch(pipe, GrantBody(grant, prompt: "different"))).Reason);
    }

    [Fact]
    public async Task A_grant_with_unavailable_audit_never_starts_a_process()
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, _) = Granted(key);
        File.WriteAllText(Path.Combine(folder, AgentAuditLog.FolderName, $"launches-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"), "{ damaged");
        Assert.Equal("audit-unavailable", (await GrantedLaunch(pipe, GrantBody(grant))).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_missing_persisted_policy_snapshot_refuses_dispatch_and_duplicate_replay()
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, store) = Granted(key);
        launcher.BeforeDispatch = () => Assert.True(store.Transaction(state =>
            (GrantStore.Next(state with { Counters = state.Counters! with
                { Reservations = state.Counters.Reservations.Select(r => r with { Caps = null }).ToArray() } },
                grant.Id, "grant.update"), new(GrantStoreProblem.None)), false).Usable);
        Assert.Equal("policy", (await GrantedLaunch(pipe, GrantBody(grant))).Reason);
        Assert.Equal("policy", (await GrantedLaunch(pipe, GrantBody(grant))).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task Policy_narrowing_after_admission_refuses_at_the_execution_slot()
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, store) = Granted(key);
        Assert.True(new GrantCounters(store).Reserve(grant.Id, 1, Guid.NewGuid(), grant.Subjects[0], new('b', 64)).Accepted);
        var path = Path.Combine(folder, HostSettings.FileName);
        launcher.BeforeDispatch = () => File.WriteAllText(path,
            File.ReadAllText(path).Replace("\"launchesPerOriginPerHour\":3", "\"launchesPerOriginPerHour\":1"));
        Assert.Equal("grant-exhausted", (await GrantedLaunch(pipe, GrantBody(grant))).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Theory]
    [InlineData(GrantLaunchOutcome.Unconfirmed, LaunchOutcome.Unconfirmed)]
    [InlineData(GrantLaunchOutcome.Failed, LaunchOutcome.Failed)]
    [InlineData(GrantLaunchOutcome.Refused, LaunchOutcome.Refused)]
    public async Task Missing_snapshot_replay_preserves_uncertainty_and_definitive_failure(GrantLaunchOutcome terminal, LaunchOutcome expected)
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, store) = Granted(key);
        Assert.Equal(LaunchOutcome.Unconfirmed, (await GrantedLaunch(pipe, GrantBody(grant))).Result.Outcome);
        if (terminal != GrantLaunchOutcome.Unconfirmed)
        { Assert.True(new GrantCounters(store).Complete(Guid.Parse(request), terminal, "policy").Accepted); }
        Assert.True(store.Transaction(state =>
            (GrantStore.Next(state with { Counters = state.Counters! with
                { Reservations = state.Counters.Reservations.Select(r => r with { Caps = null }).ToArray() } },
                grant.Id, "grant.update"), new(GrantStoreProblem.None)), false).Usable);
        var replay = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(expected, replay.Result.Outcome);
        Assert.False(replay.Registered);
        Assert.Equal(1, launcher.Launches);
    }

    sealed class FakeLauncher : ISessionLauncher
    {
        public int Launches;
        public List<string> Peers { get; } = [];
        public LaunchConfig? FleetConfig { get; set; }
        public bool Available => true;
        public Action? BeforeDispatch;
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LaunchOptions
        { Agents = [new() { Id = "claude", Label = "Claude", Kind = AgentKind.Claude, Models = ["small"], Efforts = ["medium"], Contexts = ["default"] }],
            Workspaces = [new() { Label = "App", Path = "unused", Isolate = true }], Defaults = new() { Permissions = "ask" } });
        public Task<LaunchResult> LaunchAuditedAsync(LaunchRequest request, string peer, Func<Guid, Action<string>?> prepare,
            Func<string?> dispatch, CancellationToken cancellationToken = default)
        {
            BeforeDispatch?.Invoke();
            return dispatch() is { } denied ? Task.FromResult(new LaunchResult(LaunchOutcome.Refused, denied))
                : LaunchAuditedAsync(request, peer, prepare, cancellationToken);
        }
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
