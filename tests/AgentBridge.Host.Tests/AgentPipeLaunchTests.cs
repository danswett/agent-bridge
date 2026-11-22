using System.Text.Json;
using System.Security.Cryptography;
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

    sealed record Pipe(Func<IProtocolChannel, string, JsonElement, Task<object?>> Handle, Chan Channel, AgentCredentials Credentials,
        AgentPipeLaunch Launch);

    Pipe Open(string session = "parent", string scope = AgentProtocol.ScopeReadReplyLaunch, int depth = 0,
        string launch = "person-launch", AgentCredentials? credentials = null, (LaunchRequestStore?, string?)? store = null,
        P256KeyPair? grantKey = null, bool legacy = false, bool grantOnly = false, FleetPublisherTests.Source? source = null)
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
        var service = new SessionService(source ?? new FleetPublisherTests.Source(), new MachineInfo("machine", "Test", "windows", "0.1.0"), launcher: launcher);
        var agentLaunch = new AgentPipeLaunch(folder, credentials, service, audit, requests, problem, fleetGrants: grantKey is null ? null
            : new FleetGrantLaunch(folder, grantKey, () => "123", new('a', 32), credentials, launcher));
        launcher.RequestRegistered += agentLaunch.Registered;
        return new(LocalApi.Handler(service, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")),
            credentials: credentials, audit: audit, agentLaunch: legacy || grantOnly ? null : agentLaunch, grantLaunch: grantOnly ? agentLaunch : null), channel, credentials, agentLaunch);
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

    (Pipe Pipe, GrantDocument Grant, GrantStore Store) Granted(P256KeyPair key, GrantCaps? caps = null, bool grantOnly = false,
        bool allScope = false, bool allowAllLaunch = false, FleetPublisherTests.Source? source = null)
    {
        var pipe = Open(session: Guid.NewGuid().ToString(), grantKey: key, grantOnly: grantOnly, source: source);
        var caller = pipe.Credentials.Of(pipe.Channel)!;
        var now = DateTimeOffset.UtcNow;
        var scope = new GrantScope([new('a', 32)], ["App"], ["claude"], ["small"], ["default"], ["medium"], ["default"],
            allScope ? ["ask", "allowAll"] : ["ask"]);
        var store = new GrantStore(folder, key, new(folder), scope);
        var grant = new GrantDocument(1, Guid.NewGuid(), "123", new("123", new('a', 32), "AQ", now, new('a', 64)),
            new(new('a', 32), Path.GetFileName(folder)), [new("123", new('a', 32), caller.Session, caller.Id, new(caller.RootPid, caller.RootStartUtc))],
            ["launch"], scope, caps ?? new(3, 3, 2), now, 1, now.AddHours(8), AllowAllLaunch: allowAllLaunch);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Grant_registration_requires_its_matching_durable_terminal(bool late, bool terminalSucceeds)
    {
        using var key = P256KeyPair.Generate();
        var session = Guid.NewGuid().ToString("D");
        var started = DateTimeOffset.UtcNow;
        var discovery = new FleetPublisherTests.Source { Binding = id => id == session ? new(123, started) : null };
        discovery.Sessions.Add(FleetPublisherTests.NewSession(4) with { Id = session });
        launcher.RegisteredSession = session;
        launcher.LateRegistration = late;
        if (!terminalSucceeds)
        {
            launcher.TerminalFailure = () => new FileStream(
                Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl").Single(),
                FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        var (pipe, grant, _) = Granted(key, grantOnly: true, source: discovery);
        var body = GrantBody(grant);
        var first = await GrantedLaunch(pipe, body);
        Assert.Equal(LaunchOutcome.Unconfirmed, first.Result.Outcome);
        if (late) { launcher.RegisterPending!(); }
        var retry = await GrantedLaunch(pipe, body);
        Assert.Equal(terminalSucceeds ? LaunchOutcome.Started : LaunchOutcome.Unconfirmed, retry.Result.Outcome);
        Assert.Equal(terminalSucceeds, retry.Registered);
        Assert.Equal(new GrantUsage(1, 1, 1), retry.Usage);
        Assert.Equal(1, launcher.Launches);
        var (rows, complete) = GateAuditProjection.Load(folder);
        Assert.True(complete);
        Assert.Single(rows, row => row.Action == "session.launch" && row.Gate?.Phase == "intent");
        Assert.Equal(terminalSucceeds ? 1 : 0,
            rows.Count(row => row.Action == "session.launch" && row.Gate?.Phase == "terminal"));
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
        var target = new StopTarget(Guid.NewGuid().ToString(), new(99, DateTimeOffset.UtcNow));
        Assert.True(new GrantCounters(store).Commit(Guid.Parse(request), target, "ask").Accepted);
        Assert.True((await GrantedLaunch(pipe, GrantBody(grant))).Registered);
        Assert.Equal(1, launcher.Launches);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Receiver_all_launch_needs_both_flags_and_never_changes_ordinary_legacy_launch(bool flag, bool enabled)
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, _) = Granted(key, grantOnly: true, allScope: true, allowAllLaunch: flag);
        var path = Path.Combine(folder, HostSettings.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"permissions\":\"ask\"",
            "\"permissions\":\"ask\",\"allowAllLaunch\":" + enabled.ToString().ToLowerInvariant()));
        Assert.Contains("for the person", await Refusal(pipe, LaunchProtocol.Launch, Body(permissions: "allowAll")));
        var input = GrantBody(grant).Deserialize<FleetLaunchInput>(ProtocolJson.Options)! with { Permissions = "allowAll" };
        var result = await GrantedLaunch(pipe, JsonSerializer.SerializeToElement(input, ProtocolJson.Options));
        Assert.Equal(flag && enabled ? 1 : 0, launcher.Launches);
        if (!flag || !enabled) { Assert.Equal("scope", result.Reason); }
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
    public async Task Reviewer_receiver_caps_are_retained_in_the_receipt()
    {
        using var key = P256KeyPair.Generate();
        var path = Path.Combine(folder, HostSettings.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"liveAgentLaunched\":6", "\"liveAgentLaunched\":2"));
        var (pipe, grant, _) = Granted(key, new(10, 10, 10));
        var receipt = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(new GrantCaps(3, 10, 2), receipt.Caps);
    }

    [Fact]
    public async Task Reviewer_existing_uncertain_reservations_obey_receiver_live_cap_after_restart()
    {
        using var key = P256KeyPair.Generate();
        var path = Path.Combine(folder, HostSettings.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"liveAgentLaunched\":6", "\"liveAgentLaunched\":2"));
        var (pipe, grant, store) = Granted(key, new(10, 10, 10));
        var counters = new GrantCounters(store);
        foreach (var id in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            Assert.True(counters.Reserve(grant.Id, 1, id, grant.Subjects[0], new('b', 64)).Accepted);
            Assert.True(counters.Abandon(id, false).Accepted);
        }
        var receipt = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(LaunchOutcome.Refused, receipt.Result.Outcome);
        Assert.Equal("grant-exhausted", receipt.Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task Reviewer_recovery_does_not_bind_a_foreign_principals_status()
    {
        using var key = P256KeyPair.Generate();
        var source = new FleetPublisherTests.Source();
        var (pipe, grant, store) = Granted(key, source: source);
        var input = GrantBody(grant).Deserialize<FleetLaunchInput>(ProtocolJson.Options)!;
        var binding = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, ProtocolJson.Options)));
        var counters = new GrantCounters(store);
        Assert.True(counters.Reserve(grant.Id, 1, input.Request, grant.Subjects[0], binding).Accepted);
        Assert.True(counters.Abandon(input.Request, false).Accepted);
        var foreign = Guid.NewGuid().ToString("D");
        source.Sessions.Add(source.Sessions[0] with { Id = foreign });
        source.Binding = id => id == foreign ? new(int.MaxValue, DateTimeOffset.UtcNow) : null;
        launcher.Statuses.Add(new(input.Request.ToString("D"), "started", foreign, "ask", false, DateTimeOffset.UtcNow)
        { Principal = "agent:machine/another-session", SourceRequest = Guid.NewGuid().ToString("D") });
        var service = new SessionService(source, new("machine", "Test", "windows", "0.1.0"), launcher: launcher);
        var kernel = new FleetGrantLaunch(folder, key, () => "123", new('a', 32), pipe.Credentials, launcher);
        await kernel.PrepareAsync(pipe.Credentials.Of(pipe.Channel)!, input with { Request = Guid.NewGuid() });
        var replay = counters.Lookup(grant.Id, input.Request, grant.Subjects[0], binding);
        Assert.Equal(GrantLaunchOutcome.Unconfirmed, replay.Reservation!.Outcome);
        Assert.Null(replay.Reservation.Target);
    }

    [Fact]
    public async Task Reviewer_recovery_does_not_substitute_the_current_process_for_the_launched_process()
    {
        using var key = P256KeyPair.Generate();
        var source = new FleetPublisherTests.Source();
        var (pipe, grant, store) = Granted(key, source: source);
        var input = GrantBody(grant).Deserialize<FleetLaunchInput>(ProtocolJson.Options)!;
        var principal = pipe.Credentials.PrincipalOf(pipe.Credentials.Of(pipe.Channel)!);
        var binding = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, ProtocolJson.Options)));
        var counters = new GrantCounters(store);
        Assert.True(counters.Reserve(grant.Id, 1, input.Request, grant.Subjects[0], binding).Accepted);
        Assert.True(counters.Abandon(input.Request, false).Accepted);
        RegistrationEvidence(pipe, source);
        var launch = new LaunchRequest { Request = AgentPipeLaunch.LaunchId(principal, request).ToString("D"),
            Agent = input.Agent, Workspace = input.Workspace, FleetName = input.Name, Permissions = input.Permissions,
            Model = input.Model, Effort = input.Effort, Context = input.Context, Prompt = input.Prompt };
        var started = await GateLaunchAudit.LaunchAsync(launcher, launch, principal, new AgentAuditLog(folder) { DeviceKey = key },
            new("local", "agent", principal, null, grant.Id.ToString("D")));
        Assert.Equal(LaunchOutcome.Started, started.Outcome);
        var launchedAt = source.ProcessOf(started.Session!)?.StartedAt ?? throw new InvalidOperationException("fixture lost the launched process");
        source.Binding = id => id == started.Session ? new(int.MaxValue - 1, launchedAt.AddSeconds(1)) : null;
        var service = new SessionService(source, new("machine", "Test", "windows", "0.1.0"), launcher: launcher);
        var kernel = new FleetGrantLaunch(folder, key, () => "123", new('a', 32), pipe.Credentials, launcher);
        await kernel.PrepareAsync(pipe.Credentials.Of(pipe.Channel)!, input with { Request = Guid.NewGuid() });
        var replay = counters.Lookup(grant.Id, input.Request, grant.Subjects[0], binding);
        Assert.Equal(GrantLaunchOutcome.Unconfirmed, replay.Reservation!.Outcome);
        Assert.Null(replay.Reservation.Target);
        Assert.Equal(1, replay.Usage!.Live);
    }

    [Fact]
    public async Task Recovery_preserves_the_audited_launched_generation_after_the_session_process_changes()
    {
        using var key = P256KeyPair.Generate();
        var source = new FleetPublisherTests.Source();
        var (pipe, grant, store) = Granted(key, source: source);
        RegistrationEvidence(pipe, source);
        var body = GrantBody(grant);
        var input = body.Deserialize<FleetLaunchInput>(ProtocolJson.Options)!;
        var binding = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, ProtocolJson.Options)));
        var receipt = await GrantedLaunch(pipe, body);
        Assert.Equal(LaunchOutcome.Started, receipt.Result.Outcome);
        var counters = new GrantCounters(store);
        var launched = counters.Lookup(grant.Id, input.Request, grant.Subjects[0], binding).Reservation!.Target!;
        Assert.True(store.Transaction(state => (GrantStore.Next(state with { Counters = state.Counters! with
            { Reservations = state.Counters.Reservations.Select(r => r.Request == input.Request
                ? r with { State = GrantReservationState.Abandoned, Target = null, Permissions = null } : r).ToArray() } },
            grant.Id, "grant.update"), new(GrantStoreProblem.None)), false).Usable);
        source.Binding = id => id == launched.Session ? new(int.MaxValue - 1, launched.ProcessInstance.StartedAt.AddSeconds(1)) : null;
        var service = new SessionService(source, new("machine", "Test", "windows", "0.1.0"), launcher: launcher);
        var kernel = new FleetGrantLaunch(folder, key, () => "123", new('a', 32), pipe.Credentials, launcher);
        await kernel.PrepareAsync(pipe.Credentials.Of(pipe.Channel)!, input with { Request = Guid.NewGuid() });
        var replay = counters.Lookup(grant.Id, input.Request, grant.Subjects[0], binding);
        Assert.Equal(GrantLaunchOutcome.Started, replay.Reservation!.Outcome);
        Assert.Equal(launched, replay.Reservation.Target);
    }

    void RegistrationEvidence(Pipe pipe, FleetPublisherTests.Source source)
    {
        launcher.BeforeTerminal = (launch, principal, session) =>
        {
            var startedAt = DateTimeOffset.UtcNow;
            source.Sessions.Add(source.Sessions[0] with { Id = session });
            source.Binding = id => id == session ? new(int.MaxValue, startedAt) : null;
            launcher.Statuses.Add(new(launch.Request, "started", session, "ask", false, DateTimeOffset.UtcNow)
            { Principal = principal });
            launcher.Statuses.Add(new(request, "started", session, "ask", false, DateTimeOffset.UtcNow)
            { Principal = principal, SourceRequest = launch.Request });
        };
        launcher.AfterTerminal = (launch, _, session) => pipe.Launch.Registered(Guid.Parse(launch.Request), session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewer_registration_without_terminal_audit_never_replays_started(bool reconcile)
    {
        using var key = P256KeyPair.Generate();
        var source = new FleetPublisherTests.Source();
        var (pipe, grant, _) = Granted(key, source: source);
        RegistrationEvidence(pipe, source);
        launcher.SkipTerminal = true;
        var first = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(LaunchOutcome.Unconfirmed, first.Result.Outcome);
        Assert.DoesNotContain(GateAuditProjection.Load(folder).Rows,
            row => row.Action == "session.launch" && row.Gate?.Phase == "terminal");
        if (reconcile)
        {
            var kernel = new FleetGrantLaunch(folder, key, () => "123", new('a', 32), pipe.Credentials, launcher);
            await kernel.PrepareAsync(pipe.Credentials.Of(pipe.Channel)!, GrantBody(grant).Deserialize<FleetLaunchInput>(ProtocolJson.Options)! with { Request = Guid.NewGuid() });
        }
        var duplicate = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(LaunchOutcome.Unconfirmed, duplicate.Result.Outcome);
    }

    [Fact]
    public async Task Reviewer_confirmed_permission_replay_uses_registered_process_evidence()
    {
        using var key = P256KeyPair.Generate();
        var source = new FleetPublisherTests.Source();
        var (pipe, grant, store) = Granted(key, source: source);
        RegistrationEvidence(pipe, source);
        var first = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(LaunchOutcome.Started, first.Result.Outcome);
        Assert.Equal("ask", first.Permissions);
        Assert.True(store.Revoke(grant.Id, 1).Usable);
        var duplicate = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(first.Result.Session, duplicate.Result.Session);
        Assert.Equal("ask", duplicate.Permissions);
        Assert.Equal(LaunchOutcome.Started, duplicate.Result.Outcome);
        Assert.Equal(1, launcher.Launches);
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
        public string? RegisteredSession;
        public bool LateRegistration;
        public Func<IDisposable>? TerminalFailure;
        public Action? RegisterPending;
        public event Action<Guid, string>? RequestRegistered;
        public List<LaunchStatus> Statuses { get; } = [];
        public IReadOnlyList<LaunchStatus> LaunchStatuses() => Statuses;
        public Action<LaunchRequest, string, string>? BeforeTerminal, AfterTerminal;
        public bool SkipTerminal;
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
            var count = Interlocked.Increment(ref Launches);
            var session = RegisteredSession ?? (BeforeTerminal is null ? $"session-{count}" : Guid.NewGuid().ToString("D"));
            Peers.Add(peer);
            void Register()
            {
                if (RegisteredSession is not null)
                { Statuses.Add(new(request.Request!, "started", session, request.Permissions, true, DateTimeOffset.UtcNow) { Principal = peer }); }
                BeforeTerminal?.Invoke(request, peer, session);
                using var held = TerminalFailure?.Invoke();
                if (!SkipTerminal) { registered(session); }
                AfterTerminal?.Invoke(request, peer, session);
                if (RegisteredSession is not null) { RequestRegistered?.Invoke(Guid.Parse(request.Request), session); }
            }
            if (LateRegistration)
            {
                RegisterPending = Register;
                return Task.FromResult(new LaunchResult(LaunchOutcome.Unconfirmed));
            }
            Register();
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
