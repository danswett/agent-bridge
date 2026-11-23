using System.Text.Json;
using System.Security.Cryptography;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host;
using AgentBridge.Host.Lite;
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
        P256KeyPair? grantKey = null, bool legacy = false, bool grantOnly = false, FleetPublisherTests.Source? source = null, TimeProvider? auditTime = null,
        FleetRemoteLaunchSender? remoteLaunch = null)
    {
        credentials ??= new AgentCredentials(folder, "machine");
        launcher.FleetConfig ??= Running();
        var channel = new Chan();
        var token = credentials.Mint(session, launch, 100, DateTimeOffset.UtcNow, scope, depth, depth > 0 ? ["ancestor"] : null);
        credentials.Accept(channel, credentials.Check(token,
            new ProcessAncestry([new PeerProcess(100, DateTimeOffset.UtcNow, "agent.exe")], true)).Accepted!);
        var (requests, problem) = store ?? LaunchRequestStore.Open(folder, TimeProvider.System);
        var audit = new AgentAuditLog(folder) { DeviceKey = grantKey };
        var service = new SessionService(source ?? new FleetPublisherTests.Source(), new MachineInfo("machine", "Test", "windows", "0.1.0"), launcher: launcher);
        var agentLaunch = new AgentPipeLaunch(folder, credentials, service, audit, requests, problem, time: auditTime, fleetGrants: grantKey is null ? null
            : new FleetGrantLaunch(folder, grantKey, () => "123", new('a', 32), credentials, launcher));
        launcher.RequestRegistered += agentLaunch.Registered;
        return new(LocalApi.Handler(service, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")),
            credentials: credentials, audit: audit, auditTime: auditTime, agentLaunch: legacy || grantOnly ? null : agentLaunch,
            grantLaunch: grantOnly ? agentLaunch : null, remoteLaunch: remoteLaunch), channel, credentials, agentLaunch);
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

    [Fact]
    public async Task Another_principal_cannot_read_or_take_over_a_request_id()
    {
        var credentials = new AgentCredentials(folder, "machine");
        var store = LaunchRequestStore.Open(folder, TimeProvider.System);
        await Launch(Open(credentials: credentials, store: store), Body());
        var other = Open("other", credentials: credentials, store: store);

        Assert.Contains("no launch request", await Refusal(other, AgentPipeLaunch.StatusOp, StatusBody()));
        Assert.Equal(1, launcher.Launches);
    }

    JsonElement StatusBody(string? id = null) => JsonSerializer.SerializeToElement(new { request = id ?? request });

    static async Task<AgentLaunchStatus> Status(Pipe pipe, JsonElement body) =>
        Assert.IsType<AgentLaunchStatus>(await pipe.Handle(pipe.Channel, AgentPipeLaunch.StatusOp, body));

    [Fact]
    public async Task Launch_status_reads_a_started_launch_and_keeps_it_for_the_retention_window()
    {
        var pipe = Open();
        var started = await Launch(pipe, Body());

        var status = await Status(pipe, StatusBody(request.ToUpperInvariant()));

        Assert.Equal(request, status.Request);
        Assert.Equal("launched", status.State);
        Assert.Equal(started.Session, status.Session);
        Assert.Equal(status.ChangedAt + LaunchRequestStore.Retention, status.KeptUntil);
    }

    [Fact]
    public async Task Launch_status_reads_a_pending_launch_then_its_late_start_or_failure()
    {
        launcher.RegisteredSession = Guid.NewGuid().ToString("D");
        launcher.LateRegistration = true;
        var pipe = Open();
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Launch(pipe, Body())).Outcome);

        var pending = await Status(pipe, StatusBody());
        Assert.Equal("launching", pending.State);
        Assert.Null(pending.Session);
        Assert.Null(pending.KeptUntil);

        launcher.RegisterPending!();
        var started = await Status(pipe, StatusBody());
        Assert.Equal("launched", started.State);
        Assert.Equal(launcher.RegisteredSession, started.Session);

        var failedRequest = Guid.NewGuid().ToString("D");
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Launch(pipe, Body(failedRequest))).Outcome);
        pipe.Launch.Failed(AgentPipeLaunch.LaunchId(pipe.Credentials.PrincipalOf(pipe.Credentials.Of(pipe.Channel)!), failedRequest));
        var failed = await Status(pipe, StatusBody(failedRequest));
        Assert.Equal("failed", failed.State);
        Assert.Equal("failed", failed.Code);
        Assert.NotNull(failed.KeptUntil);
    }

    [Fact]
    public async Task Launch_status_refuses_an_unknown_request_and_one_that_is_not_a_uuid()
    {
        var pipe = Open();

        Assert.Contains("no launch request", await Refusal(pipe, AgentPipeLaunch.StatusOp, StatusBody()));
        foreach (var bad in new[] { StatusBody("not-a-uuid"), JsonSerializer.SerializeToElement(new { }) })
        {
            Assert.Contains("request id", (await Assert.ThrowsAsync<ArgumentException>(() => pipe.Handle(pipe.Channel, AgentPipeLaunch.StatusOp, bad))).Message);
        }
        Assert.Contains("can't be used", await Refusal(Open(store: (null, "not open")), AgentPipeLaunch.StatusOp, StatusBody()));
    }

    [Theory]
    [InlineData(AgentProtocol.ScopeRead)]
    [InlineData(AgentProtocol.ScopeReadReply)]
    public async Task Launch_status_needs_read_reply_launch_scope(string scope)
    {
        Assert.Contains("read+reply+launch", await Refusal(Open(scope: scope), AgentPipeLaunch.StatusOp, StatusBody()));
    }

    [Fact]
    public async Task Launch_status_is_refused_once_the_credential_is_revoked()
    {
        var pipe = Open();
        await Launch(pipe, Body());
        pipe.Credentials.Revoke("parent", "test");

        Assert.Contains("credential is required", await Refusal(pipe, AgentPipeLaunch.StatusOp, StatusBody()));
    }

    [Fact]
    public async Task A_pipe_launch_still_queued_at_a_restart_is_unconfirmed_and_never_launched()
    {
        var credentials = new AgentCredentials(folder, "machine");
        var (store, _) = LaunchRequestStore.Open(folder, TimeProvider.System);
        var pipe = Open(credentials: credentials, store: (store, null));
        var key = AgentPipeLaunch.Key(credentials.PrincipalOf(credentials.Of(pipe.Channel)!), request);
        store!.Enqueue(key, LaunchService.RequestBinding(Body().Deserialize<LaunchRequest>(ProtocolJson.Options)!));
        store.Enqueue("hosted-request-0001", "{}");
        Assert.Equal("queued", (await Status(pipe, StatusBody())).State);

        var (restarted, _) = LaunchRequestStore.Open(folder, TimeProvider.System);
        AgentPipeLaunch.Restore(restarted);
        var after = Open(credentials: credentials, store: (restarted, null));

        var status = await Status(after, StatusBody());
        Assert.Equal("failed", status.State);
        Assert.Equal("unconfirmed", status.Code);
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Launch(after, Body())).Outcome);
        Assert.Equal(0, launcher.Launches);
        Assert.Equal(LaunchRequestState.Queued, restarted!.Get("hosted-request-0001")!.State);
    }

    (Pipe Pipe, GrantDocument Grant, GrantStore Store) Granted(P256KeyPair key, GrantCaps? caps = null, bool grantOnly = false,
        bool allScope = false, bool allowAllLaunch = false, FleetPublisherTests.Source? source = null,
        (LaunchRequestStore?, string?)? launchStore = null)
    {
        var pipe = Open(session: Guid.NewGuid().ToString(), grantKey: key, grantOnly: grantOnly, source: source, store: launchStore);
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
            launcher.TerminalFailure = () => new BlockedAuditFile(
                Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl").Single());
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
    public async Task Grant_refusals_share_the_legacy_per_principal_audit_limit(bool wired)
    {
        using var key = P256KeyPair.Generate();
        var pipe = Open(grantKey: wired ? key : null, auditTime: new FrozenAuditClock());
        var malformed = JsonSerializer.SerializeToElement(new { grant = "x" });
        var missing = JsonSerializer.SerializeToElement(new FleetLaunchInput(1, Guid.Parse(request), Guid.NewGuid(),
            "SG Builder 266", "App", "claude", Model: "small", Effort: "medium", Context: "default",
            Permissions: "ask", Prompt: "hello"), ProtocolJson.Options);
        for (var i = 0; i < 150; i++)
        {
            Assert.Equal(LaunchOutcome.Refused, (await GrantedLaunch(pipe, i % 2 == 0 ? malformed : missing)).Result.Outcome);
        }
        Assert.Equal(60, AuditRows().Length);
        await Refusal(pipe, LaunchProtocol.Launch, JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(60, AuditRows().Length);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_grant_refusal_with_an_unavailable_launch_record_is_audited_only_once()
    {
        using var key = P256KeyPair.Generate();
        var (pipe, grant, _) = Granted(key, launchStore: (null, "fixture"));
        Assert.Equal(LaunchOutcome.Refused, (await GrantedLaunch(pipe, GrantBody(grant))).Result.Outcome);
        Assert.Single(AuditRows());
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
        var source = new FleetPublisherTests.Source();
        var (pipe, grant, store) = Granted(key, source: source);
        RegistrationEvidence(pipe, source);
        var first = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(LaunchOutcome.Started, first.Result.Outcome);
        Assert.Equal("ask", first.Permissions);
        Assert.True(store.Revoke(grant.Id, 1).Usable);
        var duplicate = await GrantedLaunch(pipe, GrantBody(grant));
        Assert.Equal(first.Result.Session, duplicate.Result.Session);
        Assert.Equal(first.Permissions, duplicate.Permissions);
        Assert.Equal(first.Result.Outcome, duplicate.Result.Outcome);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal("duplicate-id", (await GrantedLaunch(pipe, GrantBody(grant, prompt: "different"))).Reason);
        var process = Assert.IsType<AgentBridge.Host.Core.SessionProcess>(source.ProcessOf(first.Result.Session!));
        var target = new StopTarget(first.Result.Session!, new(process.Id, Assert.IsType<DateTimeOffset>(process.StartedAt)));
        Assert.True(new GrantCounters(store).Commit(Guid.Parse(request), target, "ask").Accepted);
        source.Sessions.Clear();
        source.Binding = _ => null;
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

    sealed class FrozenAuditClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    // A directory blocks writes on Unix too, without relying on Windows file-sharing enforcement.
    sealed class BlockedAuditFile : IDisposable
    {
        readonly string path;

        public BlockedAuditFile(string path)
        {
            this.path = path;
            File.Move(path, path + ".held");
            Directory.CreateDirectory(path);
        }

        public void Dispose()
        {
            Directory.Delete(path);
            File.Move(path + ".held", path);
        }
    }

    // G9b1: a verified remote subject prepares through the same grant gate; a local, foreign-account or malformed one never does.
    static readonly string RemoteHost = new('b', 32), RemoteSession = "6f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f";
    (FleetGrantLaunch Launch, GrantDocument Grant, GrantSubject Subject) RemoteGrant(P256KeyPair key, string? tupleHost = null)
    {
        var now = DateTimeOffset.UtcNow;
        var subject = new GrantSubject("123", tupleHost ?? RemoteHost, RemoteSession, "AQEBAQEBAQEBAQEBAQEBAQ", new(4242, now.AddMinutes(-5)));
        var scope = new GrantScope([new('a', 32)], ["App"], ["claude"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
        var store = new GrantStore(folder, key, new(folder), scope);
        var grant = new GrantDocument(1, Guid.NewGuid(), "123", new("123", new('a', 32), "AQ", now, new('a', 64)),
            new(new('a', 32), Path.GetFileName(folder)), [subject], ["launch"], scope, new(3, 3, 2), now, 1, now.AddHours(8));
        Assert.True(store.Issue(grant).Usable);
        launcher.FleetConfig ??= Running();
        return (new FleetGrantLaunch(folder, key, () => "123", new('a', 32), new AgentCredentials(folder, "machine"), launcher), grant, subject);
    }
    FleetLaunchInput RemoteInput(GrantDocument grant) => new(1, Guid.Parse(request), grant.Id, "SG Builder 266", "App", "claude",
        Model: "small", Effort: "medium", Context: "default", Permissions: "ask", Prompt: "hello");

    [Fact]
    public async Task A_verified_remote_subject_prepares_under_its_own_grant_and_a_remote_principal()
    {
        using var key = P256KeyPair.Generate();
        var (launch, grant, subject) = RemoteGrant(key);
        var context = await launch.PrepareRemoteAsync(subject, RemoteInput(grant));
        Assert.Equal(subject, context.Subject);
        Assert.NotNull(context.Request);
        Assert.Equal(new GrantCaps(3, 3, 2), context.Caps);
        launch.Admit(context);
        Assert.True(context.Admitted);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("account")]
    [InlineData("short-host")]
    [InlineData("pid")]
    public async Task A_local_foreign_account_or_malformed_remote_subject_refuses_before_any_grant_read(string bad)
    {
        using var key = P256KeyPair.Generate();
        var (launch, grant, subject) = RemoteGrant(key);
        var broken = bad switch
        {
            "local" => subject with { SourceHost = new('a', 32) },
            "account" => subject with { Account = "other" },
            "short-host" => subject with { SourceHost = "bbbb" },
            _ => subject with { ProcessInstance = new(0, subject.ProcessInstance.StartedAt) },
        };
        var denied = await Assert.ThrowsAsync<FleetLaunchDenied>(() => launch.PrepareRemoteAsync(broken, RemoteInput(grant)));
        Assert.Equal("policy", denied.Message);
    }

    [Fact]
    public async Task A_remote_subject_the_grant_does_not_name_is_refused_by_scope()
    {
        using var key = P256KeyPair.Generate();
        var (launch, grant, subject) = RemoteGrant(key);
        var denied = await Assert.ThrowsAsync<FleetLaunchDenied>(() =>
            launch.PrepareRemoteAsync(subject with { Session = Guid.NewGuid().ToString("D") }, RemoteInput(grant)));
        Assert.Equal("scope", denied.Message);
    }

    // G9b2a: the remote subject launches through the same queue, name gate, counters and audit, with no local credential.
    [Fact]
    public async Task A_remote_subject_launches_once_through_the_shared_pipeline_and_a_retry_replays_the_receipt()
    {
        using var key = P256KeyPair.Generate();
        var (_, grant, subject) = RemoteGrant(key);
        var pipe = Open(grantKey: key, grantOnly: true);
        var first = await pipe.Launch.LaunchGrantedRemoteAsync(subject, RemoteInput(grant));
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(LaunchOutcome.Unconfirmed, first.Result.Outcome);
        Assert.Equal(grant.Id, first.Grant);
        var again = await pipe.Launch.LaunchGrantedRemoteAsync(subject, RemoteInput(grant));
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(first.Request, again.Request);
    }

    [Theory]
    [InlineData("local", "policy")]
    [InlineData("unnamed", "scope")]
    public async Task A_bad_or_unnamed_remote_subject_is_refused_before_any_launch_side_effect(string bad, string reason)
    {
        using var key = P256KeyPair.Generate();
        var (_, grant, subject) = RemoteGrant(key);
        var pipe = Open(grantKey: key, grantOnly: true);
        var broken = bad == "local" ? subject with { SourceHost = new('a', 32) } : subject with { Session = Guid.NewGuid().ToString("D") };
        var receipt = await pipe.Launch.LaunchGrantedRemoteAsync(broken, RemoteInput(grant));
        Assert.Equal(LaunchOutcome.Refused, receipt.Result.Outcome);
        Assert.Equal(reason, receipt.Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_revoked_grant_stops_a_remote_launch_at_execution_time_without_launching()
    {
        using var key = P256KeyPair.Generate();
        var (_, grant, subject) = RemoteGrant(key);
        var pipe = Open(grantKey: key, grantOnly: true);
        var store = new GrantStore(folder, key, new(folder), new([], [], [], [], [], [], [], []));
        Assert.True(store.Revoke(grant.Id, grant.Generation).Usable);
        var receipt = await pipe.Launch.LaunchGrantedRemoteAsync(subject, RemoteInput(grant));
        Assert.Equal("grant-revoked", receipt.Reason);
        Assert.Equal(0, launcher.Launches);
    }

    // G9b2b: Untrusted -> per-peer RateCap -> Verify -> replay -> grant lookup/subject match -> the shared pipeline.
    sealed record Remote(FleetRemoteLaunch Receiver, LiteChannel Channel, GrantDocument Grant, GrantSubject Subject, ECDsa Sign,
        Func<FleetRemoteLaunch> Restart)
    {
        public JsonElement Data(FleetLaunchInput? input = null, FleetActionBody? body = null, string? subject = null, string? prompt = null, string? audience = null)
        {
            var launch = input ?? new FleetLaunchInput(1, Guid.Parse(Request), Grant.Id, "SG Builder 266", "App", "claude",
                Model: "small", Effort: "medium", Context: "default", Permissions: "ask", Prompt: "hello");
            var signedFor = launch with { Prompt = prompt ?? launch.Prompt };
            var sent = body ?? new FleetActionBody("123", Channel.PeerId, subject ?? FleetActionEnvelope.SubjectDigest(Subject.Account,
                Subject.SourceHost, Subject.Session, Subject.Credential, Subject.ProcessInstance.Pid, Subject.ProcessInstance.StartedAt.UtcTicks),
                audience ?? Channel.Self, "fleet.launch", Grant.Id.ToString("D"), Grant.Generation, launch.Request, FleetActionEnvelope.NewNonce(),
                DateTimeOffset.UtcNow.AddSeconds(60), FleetActionEnvelope.PayloadSha256(JsonSerializer.SerializeToUtf8Bytes(signedFor, ProtocolJson.Options)));
            var q = Sign.ExportParameters(false).Q;
            return JsonSerializer.SerializeToElement(new { envelope = FleetActionEnvelope.Seal(sent, new EcdsaEnvelopeSigner(Sign)), launch,
                signPoint = AgentBridge.Roster.Codec.Base64Url([4, .. q.X!, .. q.Y!]) }, ProtocolJson.Options);
        }
        public string Request = "";
    }

    Remote OpenRemote(P256KeyPair key, bool trusted = true, string audience = "", bool warm = true)
    {
        var statik = RandomNumberGenerator.GetBytes(32);
        var peer = Wire.DeviceId(statik);
        var (_, grant, subject) = RemoteGrant(key, peer);
        var pipe = Open(grantKey: key, grantOnly: true);
        var store = new GrantStore(folder, key, new(folder), new([], [], [], [], [], [], [], []));
        var channel = new LiteChannel(peer, "host", statik, audience == "" ? new('a', 32) : audience, "123", trusted);
        return new(new FleetRemoteLaunch(pipe.Launch, store, startedAt: warm ? DateTimeOffset.UtcNow.AddMinutes(-10) : null), channel, grant,
            subject, ECDsa.Create(ECCurve.NamedCurves.nistP256), () => new FleetRemoteLaunch(pipe.Launch, store)) { Request = request };
    }

    [Fact]
    public async Task A_trusted_signed_launch_for_a_named_subject_starts_once_and_its_nonce_cannot_be_replayed()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key);
        var data = r.Data();
        var first = await r.Receiver.ReceiveAsync(r.Channel, data);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(LaunchOutcome.Unconfirmed, first.Result.Outcome);
        var replayed = await r.Receiver.ReceiveAsync(r.Channel, data);
        Assert.Equal((LaunchOutcome.Refused, "policy"), (replayed.Result.Outcome, replayed.Reason));
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task An_untrusted_peer_or_wrong_audience_is_refused_before_any_side_effect()
    {
        using var key = P256KeyPair.Generate();
        var untrusted = OpenRemote(key, trusted: false);
        Assert.Equal("policy", (await untrusted.Receiver.ReceiveAsync(untrusted.Channel, untrusted.Data())).Reason);
        var wrong = OpenRemote(key);
        Assert.Equal("policy", (await wrong.Receiver.ReceiveAsync(wrong.Channel, wrong.Data(audience: new string('c', 32)))).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_launch_body_that_differs_from_what_was_signed_or_a_malformed_request_is_refused()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key);
        // The signed payload hash covers a different prompt than the launch body that arrived.
        Assert.Equal("policy", (await r.Receiver.ReceiveAsync(r.Channel, r.Data(prompt: "something else"))).Reason);
        Assert.Equal("bad-argument", (await r.Receiver.ReceiveAsync(r.Channel, JsonSerializer.SerializeToElement(new { launch = 1 }))).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_subject_the_grant_does_not_name_is_scope_and_the_senders_generation_is_not_authority()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key);
        var other = FleetActionEnvelope.SubjectDigest("123", r.Channel.PeerId, Guid.NewGuid().ToString("D"), "AQEBAQEBAQEBAQEBAQEBAQ", 4242, 1);
        Assert.Equal("scope", (await r.Receiver.ReceiveAsync(r.Channel, r.Data(subject: other))).Reason);
        var stale = r.Data(body: new FleetActionBody("123", r.Channel.PeerId, FleetActionEnvelope.SubjectDigest(r.Subject.Account,
            r.Subject.SourceHost, r.Subject.Session, r.Subject.Credential, r.Subject.ProcessInstance.Pid, r.Subject.ProcessInstance.StartedAt.UtcTicks),
            r.Channel.Self, "fleet.launch", r.Grant.Id.ToString("D"), r.Grant.Generation + 1, Guid.Parse(request), FleetActionEnvelope.NewNonce(),
            DateTimeOffset.UtcNow.AddSeconds(60), FleetActionEnvelope.PayloadSha256(JsonSerializer.SerializeToUtf8Bytes(
                new FleetLaunchInput(1, Guid.Parse(request), r.Grant.Id, "SG Builder 266", "App", "claude", Model: "small", Effort: "medium",
                    Context: "default", Permissions: "ask", Prompt: "hello"), ProtocolJson.Options))));
        // A different claimed generation changes nothing: the receiving host's own current grant decides.
        Assert.Equal(LaunchOutcome.Unconfirmed, (await r.Receiver.ReceiveAsync(r.Channel, stale)).Result.Outcome);
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task Revoking_the_grant_between_prepare_and_dispatch_refuses_a_remote_launch_as_grant_revoked()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key);
        var store = new GrantStore(folder, key, new(folder), new([], [], [], [], [], [], [], []));
        launcher.BeforeDispatch = () => Assert.True(store.Revoke(r.Grant.Id, r.Grant.Generation).Usable);
        Assert.Equal("grant-revoked", (await r.Receiver.ReceiveAsync(r.Channel, r.Data())).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task An_envelope_already_accepted_before_a_restart_is_refused_by_the_restarted_receiver()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key);
        var data = r.Data();
        Assert.Equal(LaunchOutcome.Unconfirmed, (await r.Receiver.ReceiveAsync(r.Channel, data)).Result.Outcome);
        Assert.Equal(1, launcher.Launches);
        // The restart loses the in-memory replay cache. The same live envelope must still not start a second launch.
        var restarted = r.Restart();
        var again = await restarted.ReceiveAsync(r.Channel, data);
        Assert.Equal((LaunchOutcome.Refused, "policy"), (again.Result.Outcome, again.Reason));
        Assert.Equal(1, launcher.Launches);
    }

    [Fact]
    public async Task Fleet_launch_is_never_served_on_this_machines_own_pipe()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key);
        var sessions = new SessionService(new FleetPublisherTests.Source(), new MachineInfo("machine", "Test", "windows", "0.1.0"),
            launcher: launcher) { FleetRemoteLaunch = r.Receiver };
        Assert.Null(await sessions.HandleAsync(new Chan(), FleetRemoteLaunch.Op, r.Data()));
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_freshly_started_receiver_refuses_every_envelope_until_the_replay_warm_up_has_passed()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key, warm: false);
        Assert.Equal("policy", (await r.Receiver.ReceiveAsync(r.Channel, r.Data())).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task An_envelope_whose_source_device_is_not_the_authenticated_peer_is_refused()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key);
        var forged = r.Data(body: new FleetActionBody("123", new string('d', 32), FleetActionEnvelope.SubjectDigest(r.Subject.Account,
            r.Subject.SourceHost, r.Subject.Session, r.Subject.Credential, r.Subject.ProcessInstance.Pid, r.Subject.ProcessInstance.StartedAt.UtcTicks),
            r.Channel.Self, "fleet.launch", r.Grant.Id.ToString("D"), 0, Guid.Parse(request), FleetActionEnvelope.NewNonce(),
            DateTimeOffset.UtcNow.AddSeconds(60), FleetActionEnvelope.PayloadSha256(JsonSerializer.SerializeToUtf8Bytes(
                new FleetLaunchInput(1, Guid.Parse(request), r.Grant.Id, "SG Builder 266", "App", "claude", Model: "small", Effort: "medium",
                    Context: "default", Permissions: "ask", Prompt: "hello"), ProtocolJson.Options))));
        Assert.Equal("policy", (await r.Receiver.ReceiveAsync(r.Channel, forged)).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_peer_over_its_rate_cap_is_refused_before_envelope_verification()
    {
        using var key = P256KeyPair.Generate();
        var r = OpenRemote(key);
        for (var i = 0; i < RateCap.PerMinute; i++)
        { Assert.Equal("bad-argument", (await r.Receiver.ReceiveAsync(r.Channel, JsonSerializer.SerializeToElement(new { launch = 1 }))).Reason); }
        Assert.Equal("policy", (await r.Receiver.ReceiveAsync(r.Channel, r.Data())).Reason);
        Assert.Equal(0, launcher.Launches);
    }

    // G9c1: the sender host seals what the receiver verifies; offline/own-host/stale credential send nothing; no retry.
    sealed class Link(string self, IReadOnlyList<PresenceEntry> presence, Func<JsonElement, Task<JsonElement>> serve) : IFleetLaunchLink
    {
        public int Calls;
        public string Self => self;
        public string Account => "123";
        public IReadOnlyList<PresenceEntry> Presence => presence;
        public Task<JsonElement> RequestAsync(string peerId, string account, string op, JsonElement request, CancellationToken ct)
        { Calls++; Assert.Equal(FleetRemoteLaunch.Op, op); return serve(request); }
    }

    (FleetRemoteLaunchSender Sender, Link Link, Pipe Pipe, GrantDocument Grant, AgentCredential Caller) OpenSender(P256KeyPair key,
        Func<JsonElement, Task<JsonElement>>? serve = null, bool present = true)
    {
        var statik = RandomNumberGenerator.GetBytes(32);
        var self = Wire.DeviceId(statik);
        var pipe = Open(session: Guid.NewGuid().ToString(), grantKey: key, grantOnly: true);
        var caller = pipe.Credentials.Of(pipe.Channel)!;
        var now = DateTimeOffset.UtcNow;
        var scope = new GrantScope([new('a', 32)], ["App"], ["claude"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
        var grant = new GrantDocument(1, Guid.NewGuid(), "123", new("123", new('a', 32), "AQ", now, new('a', 64)),
            new(new('a', 32), Path.GetFileName(folder)), [new("123", self, caller.Session, caller.Id, new(caller.RootPid, caller.RootStartUtc))],
            ["launch"], scope, new(3, 3, 2), now, 1, now.AddHours(8));
        Assert.True(new GrantStore(folder, key, new(folder), scope).Issue(grant).Usable);
        var receiverStore = new GrantStore(folder, key, new(folder), new([], [], [], [], [], [], [], []));
        var receiver = new FleetRemoteLaunch(pipe.Launch, receiverStore, startedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var channel = new LiteChannel(self, "host", statik, new('a', 32), "123", true);
        var sign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = sign.ExportParameters(false).Q;
        var link = new Link(self, present ? [new(new('a', 32), "Target", "host", now)] : [], serve ?? (async data =>
            JsonSerializer.SerializeToElement(await receiver.ReceiveAsync(channel, data), ProtocolJson.Options)));
        return (new FleetRemoteLaunchSender(link, pipe.Credentials, () => (new EcdsaEnvelopeSigner(sign), [4, .. q.X!, .. q.Y!])), link, pipe, grant, caller);
    }
    FleetLaunchInput SenderInput(GrantDocument grant, string? target = null) => new(1, Guid.Parse(request), grant.Id, "SG Builder 266", "App",
        "claude", Target: target ?? new string('a', 32), Model: "small", Effort: "medium", Context: "default", Permissions: "ask", Prompt: "hello");

    [Fact]
    public async Task A_sender_host_seals_a_launch_the_receiving_host_verifies_and_starts_once()
    {
        using var key = P256KeyPair.Generate();
        var s = OpenSender(key);
        var receipt = await s.Sender.SendAsync(s.Caller, SenderInput(s.Grant), CancellationToken.None);
        Assert.Equal(1, launcher.Launches);
        Assert.Equal(LaunchOutcome.Unconfirmed, receipt.Result.Outcome);
        Assert.Equal(1, s.Link.Calls);
    }

    // G9c2: a granted launch that names another host's node id goes to the remote sender; nothing is started on this host.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_granted_launch_naming_another_host_is_sent_once_and_never_started_here(bool remote)
    {
        using var key = P256KeyPair.Generate();
        var statik = RandomNumberGenerator.GetBytes(32);
        var credentials = new AgentCredentials(folder, "machine");
        var sign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = sign.ExportParameters(false).Q;
        var link = new Link(Wire.DeviceId(statik), [new(new('b', 32), "Target", "host", DateTimeOffset.UtcNow)], data =>
        {
            var input = data.GetProperty("launch").Deserialize<FleetLaunchInput>(ProtocolJson.Options)!;
            return Task.FromResult(JsonSerializer.SerializeToElement(
                FleetGrantLaunch.Receipt(null, new(LaunchOutcome.Refused), input, "scope"), ProtocolJson.Options));
        });
        var sender = new FleetRemoteLaunchSender(link, credentials, () => (new EcdsaEnvelopeSigner(sign), [4, .. q.X!, .. q.Y!]));
        var pipe = Open(session: Guid.NewGuid().ToString(), grantKey: key, grantOnly: true, credentials: credentials, remoteLaunch: sender);
        var body = JsonSerializer.SerializeToElement(new FleetLaunchInput(1, Guid.Parse(request), Guid.NewGuid(), "SG Builder 266", "App", "claude",
            Target: remote ? new string('b', 32) : null, Model: "small", Effort: "medium", Context: "default", Permissions: "ask", Prompt: "hello"), ProtocolJson.Options);

        var receipt = await GrantedLaunch(pipe, body);

        Assert.Equal(remote ? 1 : 0, link.Calls);
        Assert.Equal(0, launcher.Launches);
        if (remote) { Assert.Equal("scope", receipt.Reason); }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("own-host")]
    [InlineData("revoked")]
    public async Task An_offline_or_own_host_target_or_stale_credential_sends_nothing(string case_)
    {
        using var key = P256KeyPair.Generate();
        var s = OpenSender(key, present: case_ != "absent");
        var target = case_ == "own-host" ? s.Link.Self : null;
        if (case_ == "revoked") { s.Pipe.Credentials.Revoke(s.Caller.Session, "test"); }
        var receipt = await s.Sender.SendAsync(s.Caller, SenderInput(s.Grant, target), CancellationToken.None);
        Assert.Equal(LaunchOutcome.Refused, receipt.Result.Outcome);
        Assert.Equal(case_ == "absent" ? "target-offline" : "policy", receipt.Reason);
        Assert.Equal(0, s.Link.Calls);
        Assert.Equal(0, launcher.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lost_response_or_an_unreadable_one_is_unconfirmed_and_never_retried(bool unreadable)
    {
        using var key = P256KeyPair.Generate();
        var s = OpenSender(key, serve: _ => unreadable ? Task.FromResult(JsonSerializer.SerializeToElement(new { nope = 1 }))
            : Task.FromException<JsonElement>(new IOException("lost")));
        var receipt = await s.Sender.SendAsync(s.Caller, SenderInput(s.Grant), CancellationToken.None);
        Assert.Equal(LaunchOutcome.Unconfirmed, receipt.Result.Outcome);
        Assert.Equal(1, s.Link.Calls);
        Assert.Equal(0, launcher.Launches);
    }

    [Fact]
    public async Task A_peer_the_relay_calls_offline_is_target_offline_and_not_retried()
    {
        using var key = P256KeyPair.Generate();
        var s = OpenSender(key, serve: _ => Task.FromException<JsonElement>(new LitePeerOfflineException("gone")));
        var receipt = await s.Sender.SendAsync(s.Caller, SenderInput(s.Grant), CancellationToken.None);
        Assert.Equal((LaunchOutcome.Refused, "target-offline"), (receipt.Result.Outcome, receipt.Reason));
        Assert.Equal(1, s.Link.Calls);
    }

    // G9c2: a present host that is not a trusted own-account peer is peer-not-trusted (CLI doc refusal table), not target-offline.
    [Fact]
    public async Task A_peer_that_is_not_a_trusted_own_host_is_peer_not_trusted_and_not_retried()
    {
        using var key = P256KeyPair.Generate();
        var s = OpenSender(key, serve: _ => Task.FromException<JsonElement>(new LitePeerNotTrustedException("not the same account")));
        var receipt = await s.Sender.SendAsync(s.Caller, SenderInput(s.Grant), CancellationToken.None);
        Assert.Equal((LaunchOutcome.Refused, "peer-not-trusted"), (receipt.Result.Outcome, receipt.Reason));
        Assert.Equal(1, s.Link.Calls);
        Assert.Equal(0, launcher.Launches);
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
