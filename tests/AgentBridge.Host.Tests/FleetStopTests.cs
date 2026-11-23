using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Lite;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetStopTests : IDisposable
{
    readonly Fixture f = new();
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    readonly string id = Guid.NewGuid().ToString();
    readonly Source source = new();
    readonly AgentCredentials credentials;
    readonly AgentCredential caller;
    readonly Control control;
    readonly Injector injector;
    readonly StopService stops;
    readonly GrantStore store;
    GrantDocument grant;
    readonly GrantCounters counters;
    readonly SessionService sessions;
    readonly Guid launch = Guid.NewGuid();
    const string Device = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    string Folder => Path.Combine(f.Root, "default");
    StopTarget Target => new(id, new(700, started));
    FleetStopCommands Open() => new(Folder, key, () => "123", Device, credentials, sessions, source, stops,
        pid => new(control.Exited ? GrantProcessStatus.Exited : GrantProcessStatus.Live, new(pid, started)));

    public FleetStopTests()
    {
        Directory.CreateDirectory(Folder);
        Settings();
        var events = f.CopilotSession(id, 700);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        source.Sessions = [FleetPublisherTests.NewSession(1) with { Id = id, Cwd = f.Root }];
        source.Binding = _ => new(700, started);
        f.Processes.Run(AgentKind.Copilot, 700, startedAt: started);
        credentials = new(Folder, Device);
        var token = credentials.Mint(Guid.NewGuid().ToString(), "person-launch", 100, started, AgentProtocol.ScopeReadReplyLaunch);
        caller = credentials.Check(token, new([new PeerProcess(100, started, "agent.exe")], true)).Accepted!;
        control = new(); injector = new(control);
        stops = new(new DeliveryGate(), source, f.Processes, new Probe(), injector, control,
            new() { CopilotHome = f.CopilotHome, PlatformSupported = true, Grace = TimeSpan.FromMilliseconds(10) });
        sessions = new(source, new(Device, "fixture", "windows", "1"), stops: stops);
        var scope = new GrantScope([Device], ["repo"], ["copilot"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
        store = new(Folder, key, new(Folder), scope);
        grant = new(1, Guid.NewGuid(), "123", new("123", Device, "AQ", started, new('a', 64)), new(Device, "default"),
            [new("123", Device, caller.Session, caller.Id, new(100, started))], ["launch", "stop"], scope,
            new(3, 3, 2), started, 1, started.AddHours(8), StopTargets: []);
        Assert.True(store.Issue(grant).Usable);
        counters = new(store);
    }
    public void Dispose() { key.Dispose(); peerSign.Dispose(); f.Dispose(); }
    void Settings(string label = "repo", bool excluded = false) => File.WriteAllText(Path.Combine(Folder, HostSettings.FileName),
        JsonSerializer.Serialize(new { agents = new { enabled = true, inbound = new { exclude = new { workspaces = excluded ? new[] { label } : [] } } },
            launch = new { workspaces = new[] { new { label, path = f.Root, isolate = true } } } }));
    void Own()
    {
        Assert.True(counters.Reserve(grant.Id, 1, launch, grant.Subjects[0], new('b', 64)).Accepted);
        Assert.True(counters.Commit(launch, Target, "ask").Accepted);
    }
    Task<FleetStopReceipt> End(FleetStopCommands? handler = null, Guid? request = null, string? session = null) =>
        (handler ?? Open()).HandleAsync(caller, JsonSerializer.SerializeToElement(
            new FleetStopInput(1, request ?? Guid.NewGuid(), grant.Id, session ?? id), ProtocolJson.Options));

    // G10: a stop from another host of this account. The receiving host's own grant is the authority; the signed envelope only
    // carries the request. Same grant gate, counters, audit and stopper as a local stop, with no local credential to check.
    readonly byte[] peerStatic = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    readonly System.Security.Cryptography.ECDsa peerSign = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    string Peer => Wire.DeviceId(peerStatic);
    GrantSubject RemoteSubject => new("123", Peer, Guid.NewGuid().ToString(), "AQEBAQEBAQEBAQEBAQEBAQ", new(900, started));
    GrantSubject GrantRemote()
    {
        var subject = RemoteSubject;
        grant = grant with { Id = Guid.NewGuid(), Subjects = [subject], StopTargets = [Target] };
        Assert.True(store.Issue(grant).Usable);
        return subject;
    }
    JsonElement EndData(Guid? request = null, string? session = null) => JsonSerializer.SerializeToElement(
        new FleetStopInput(1, request ?? Guid.NewGuid(), grant.Id, session ?? id, Device), ProtocolJson.Options);
    FleetRemoteEnd Receiver(DateTimeOffset? up = null) => new(Open(), new GrantStore(Folder, key, new(Folder), new([], [], [], [], [], [], [], [])),
        startedAt: up ?? DateTimeOffset.UtcNow.AddMinutes(-10));
    LiteChannel PeerChannel(bool trusted = true) => new(Peer, "host", peerStatic, Device, "123", trusted);
    JsonElement Sealed(GrantSubject subject, JsonElement end, string action = FleetActionEnvelope.EndAction, string audience = Device, JsonElement? payloadOf = null)
    {
        var input = (payloadOf ?? end).Deserialize<FleetStopInput>(ProtocolJson.Options)!;
        var q = peerSign.ExportParameters(false).Q;
        var body = new FleetActionBody("123", Peer, FleetActionEnvelope.SubjectDigest(subject.Account, subject.SourceHost, subject.Session,
            subject.Credential, subject.ProcessInstance.Pid, subject.ProcessInstance.StartedAt.UtcTicks), audience, action, grant.Id.ToString("D"), 0,
            end.Deserialize<FleetStopInput>(ProtocolJson.Options)!.Request, FleetActionEnvelope.NewNonce(), DateTimeOffset.UtcNow.AddSeconds(60),
            FleetActionEnvelope.PayloadSha256(JsonSerializer.SerializeToUtf8Bytes(input, ProtocolJson.Options)));
        return JsonSerializer.SerializeToElement(new { envelope = FleetActionEnvelope.Seal(body, new EcdsaEnvelopeSigner(peerSign)), end,
            signPoint = AgentBridge.Roster.Codec.Base64Url([4, .. q.X!, .. q.Y!]) }, ProtocolJson.Options);
    }

    [Fact]
    public async Task A_remote_subject_the_grant_names_ends_its_own_session_through_the_same_gate_and_audits_the_relay_path()
    {
        var subject = GrantRemote();
        var receipt = await Open().HandleRemoteAsync(subject, EndData());
        Assert.Equal(("ended", null), (receipt.Outcome, receipt.Reason));
        Assert.Equal(1, injector.Calls);
        Assert.Contains(Directory.GetFiles(Path.Combine(Folder, AgentAuditLog.FolderName), "*.jsonl").SelectMany(File.ReadAllLines),
            line => line.Contains("relay") && line.Contains(grant.Id.ToString()));
    }

    [Fact]
    public async Task A_remote_subject_from_this_host_or_another_account_or_outside_the_grant_is_refused_without_typing()
    {
        var subject = GrantRemote();
        Assert.Equal("policy", (await Open().HandleRemoteAsync(subject with { SourceHost = Device }, EndData())).Reason);
        Assert.Equal("policy", (await Open().HandleRemoteAsync(subject with { Account = "other" }, EndData())).Reason);
        Assert.Equal("scope", (await Open().HandleRemoteAsync(subject with { Session = Guid.NewGuid().ToString() }, EndData())).Reason);
        Assert.Equal(0, injector.Calls);
    }

    [Fact]
    public async Task A_signed_end_from_a_trusted_peer_stops_once_and_its_replay_is_refused()
    {
        var subject = GrantRemote();
        var receiver = Receiver();
        var data = Sealed(subject, EndData());
        Assert.Equal("ended", (await receiver.ReceiveAsync(PeerChannel(), data)).Outcome);
        Assert.Equal("policy", (await receiver.ReceiveAsync(PeerChannel(), data)).Reason);
        Assert.Equal(1, injector.Calls);
    }

    [Fact]
    public async Task An_untrusted_peer_a_launch_envelope_another_audience_a_tampered_payload_or_a_warm_up_refuse_before_any_stop()
    {
        var subject = GrantRemote();
        var end = EndData();
        Assert.Equal("policy", (await Receiver().ReceiveAsync(PeerChannel(trusted: false), Sealed(subject, end))).Reason);
        Assert.Equal("policy", (await Receiver().ReceiveAsync(PeerChannel(), Sealed(subject, end, action: FleetActionEnvelope.LaunchAction))).Reason);
        Assert.Equal("policy", (await Receiver().ReceiveAsync(PeerChannel(), Sealed(subject, end, audience: new string('c', 32)))).Reason);
        Assert.Equal("policy", (await Receiver().ReceiveAsync(PeerChannel(), Sealed(subject, end, payloadOf: EndData(session: Guid.NewGuid().ToString())))).Reason);
        Assert.Equal("policy", (await Receiver(up: DateTimeOffset.UtcNow).ReceiveAsync(PeerChannel(), Sealed(subject, end))).Reason);
        Assert.Equal("scope", (await Receiver().ReceiveAsync(PeerChannel(), Sealed(RemoteSubject, end))).Reason);
        Assert.Equal(0, injector.Calls);
    }

    [Fact]
    public async Task A_not_own_session_is_refused_but_an_exact_persisted_exception_ends()
    {
        var other = Guid.NewGuid().ToString();
        source.Sessions.Add(source.Sessions[0] with { Id = other });
        Assert.Equal("scope", (await End(session: other)).Reason);
        Assert.Equal("scope", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
        var otherSubject = grant.Subjects[0] with { Session = Guid.NewGuid().ToString() };
        grant = grant with { Id = Guid.NewGuid(), Subjects = [grant.Subjects[0], otherSubject] };
        Assert.True(store.Issue(grant).Usable);
        var otherLaunch = Guid.NewGuid();
        Assert.True(counters.Reserve(grant.Id, 1, otherLaunch, otherSubject, new('c', 64)).Accepted);
        Assert.True(counters.Commit(otherLaunch, Target, "ask").Accepted);
        Assert.Equal("scope", (await End()).Reason);
        grant = grant with { Id = Guid.NewGuid(), StopTargets = [Target] };
        Assert.True(store.Issue(grant).Usable);
        Assert.Equal("ended", (await End(session: id.Replace("-", "")[..16])).Outcome);
        Assert.Equal(0, control.Terminations);
    }
    [Fact]
    public async Task An_agent_End_is_refused_while_a_shell_that_only_aged_out_of_the_shown_background_may_still_run()
    {
        Own();
        // Shown as idle with nothing in the background, but the host still holds an aged-out shell: its command may still run.
        source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Idle, BackgroundUnaged = new Background { Shells = 1 } };
        var refused = await End();
        Assert.Equal(("refused", "person-required", 0), (refused.Outcome, refused.Reason, injector.Calls));
        // The same session with nothing held at all ends on the agent's one press, as before.
        source.Sessions[0] = source.Sessions[0] with { BackgroundUnaged = null };
        Assert.Equal("ended", (await End()).Outcome);
    }
    [Fact]
    public async Task Recycled_processes_and_revoked_grants_are_refused_without_typing()
    {
        Own();
        source.Binding = _ => new(700, started.AddSeconds(1));
        Assert.Equal("scope", (await End()).Reason);
        source.Binding = _ => new(700, started);
        Assert.True(store.Revoke(grant.Id, 1).Usable);
        Assert.Equal("grant-revoked", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
    }
    [Fact]
    public async Task Held_stops_do_not_arm_and_graceful_timeout_never_terminates()
    {
        Own();
        stops.LaunchHeld = _ => "startup question";
        Assert.Equal("person-required", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
        stops.LaunchHeld = null;
        control.Opens = 0;
        control.Before = () => { if (control.Opens == 2) { source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Working }; } };
        Assert.Equal("person-required", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
        control.Before = null;
        source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Idle };
        var confirmed = await stops.StopAuditedAsync(new() { Id = id, Request = Guid.NewGuid().ToString(), Confirm = "fake" }, null, personConfirmation: false);
        Assert.Equal(StopOutcome.Refused, confirmed.Outcome);
        control.ExitOnTyping = false;
        var request = Guid.NewGuid();
        var first = await End(request: request);
        Assert.Equal("still-running", first.Reason);
        Assert.Equal(first.Outcome, (await End(request: request)).Outcome);
        Assert.Equal(1, injector.Calls);
        Assert.Equal(new GrantUsage(1, 1, 1), counters.Read(grant.Id, 1).Usage);
        Assert.Equal(0, control.Terminations);
    }
    [Fact]
    public async Task A_process_that_exits_before_dispatch_is_reported_ended_without_typing()
    {
        Own();
        control.GoneOnSecond = true;
        Assert.Equal("ended", (await End()).Outcome);
        Assert.Equal(0, injector.Calls);
        Assert.Equal(new GrantUsage(1, 1, 0), counters.Read(grant.Id, 1).Usage);
    }
    [Fact]
    public async Task Revocation_at_dispatch_is_refused_without_typing()
    {
        Own();
        control.Before = () => { if (control.Opens == 2) { Assert.True(store.Revoke(grant.Id, 1).Usable); } };
        Assert.Equal("grant-revoked", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exception_in_an_excluded_workspace_is_refused_at_prepare_and_dispatch(bool dispatch)
    {
        grant = grant with { Id = Guid.NewGuid(), StopTargets = [Target] };
        Assert.True(store.Issue(grant).Usable);
        if (dispatch) { control.Before = () => { if (control.Opens == 2) { Settings("protected", true); } }; }
        else { Settings("protected", true); }
        Assert.Equal("scope", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
    }
    [Theory]
    [InlineData("workspace", false)]
    [InlineData("agent", false)]
    [InlineData("agent", true)]
    [InlineData("unknown", false)]
    public async Task An_exact_exception_never_overrides_the_verified_target_scope(string scope, bool dispatch)
    {
        grant = grant with { Id = Guid.NewGuid(), StopTargets = [Target] };
        Assert.True(store.Issue(grant).Usable);
        void OutsideScope()
        {
            if (scope == "workspace") { Settings("other"); }
            else
            {
                var agent = scope == "agent" ? AgentKind.Claude : AgentKind.Unknown;
                source.Sessions[0] = source.Sessions[0] with { Agent = agent };
                f.Processes.Run(agent, 700, startedAt: started);
            }
        }
        if (dispatch) { source.BeforeEnding = () => { OutsideScope(); return Task.CompletedTask; }; }
        else { OutsideScope(); }
        var result = await End();
        Assert.Equal(("refused", "scope", 0), (result.Outcome, result.Reason, injector.Calls));
    }
    [Theory]
    [InlineData("revoke")]
    [InlineData("prompt")]
    [InlineData("question")]
    [InlineData("working")]
    [InlineData("held")]
    [InlineData("pid")]
    [InlineData("policy")]
    [InlineData("workspace")]
    public async Task Changes_while_showing_ending_refuse_agent_stop_without_keys(string change)
        => await ChangedBeforeTyping(change, injectorSlot: false);
    [Theory]
    [InlineData("revoke")]
    [InlineData("prompt")]
    [InlineData("question")]
    [InlineData("working")]
    [InlineData("held")]
    [InlineData("pid")]
    [InlineData("policy")]
    [InlineData("workspace")]
    public async Task Changes_while_waiting_for_the_injector_refuse_agent_stop_without_keys(string change)
        => await ChangedBeforeTyping(change, injectorSlot: true);
    [Theory]
    [InlineData("working")]
    [InlineData("question")]
    public async Task Changes_during_the_last_grant_transaction_refuse_agent_stop_without_keys(string change)
    {
        Own();
        var changed = false;
        injector.BeforeSlot = () =>
        {
            // The grant transaction resolves its target with Get, not the safety read's GetForStop.
            source.BeforeGet = () =>
            {
                source.BeforeGet = null;
                changed = true;
                if (change == "working") { source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Working }; }
                else { Fixture.Append(Path.Combine(f.CopilotHome, "session-state", id, "events.jsonl"), Copilot.ToolStart("q9", "ask_user", new { message = "Which?" }, 5)); }
            };
            return Task.CompletedTask;
        };
        var result = await End();
        Assert.True(changed);
        Assert.Equal(("refused", "person-required", 0, 0), (result.Outcome, result.Reason, injector.Calls, control.Terminations));
    }
    async Task ChangedBeforeTyping(string change, bool injectorSlot)
    {
        Own();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Wait() { entered.SetResult(); await release.Task; }
        if (injectorSlot) { injector.BeforeSlot = Wait; }
        else { source.BeforeEnding = Wait; }
        var run = End();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            switch (change)
            {
                case "revoke": Assert.True(store.Revoke(grant.Id, 1).Usable); break;
                case "prompt": stops.LaunchHeld = _ => "startup question"; break;
                case "question": Fixture.Append(Path.Combine(f.CopilotHome, "session-state", id, "events.jsonl"), Copilot.ToolStart("q9", "ask_user", new { message = "Which?" }, 5)); break;
                case "working": source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Working }; break;
                case "held": source.Held = new("unknown", [AgentKind.Copilot]); break;
                case "pid": source.Binding = _ => new(700, started.AddSeconds(1)); break;
                case "policy": File.WriteAllText(Path.Combine(Folder, HostSettings.FileName), """{"agents":{"enabled":false}}"""); break;
                case "workspace": Settings("other"); break;
            }
        }
        finally { release.SetResult(); }
        var result = await run;
        Assert.Equal("refused", result.Outcome);
        if (change == "revoke") { Assert.Equal("grant-revoked", result.Reason); }
        Assert.Equal(0, injector.Calls);
        Assert.Equal(0, control.Terminations);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Person_stop_keeps_its_existing_path_while_waiting(bool injectorSlot)
    {
        Task Question() { stops.LaunchHeld = _ => "startup question"; return Task.CompletedTask; }
        if (injectorSlot) { injector.BeforeSlot = Question; }
        else { source.BeforeEnding = Question; }
        Assert.Equal(StopOutcome.Ended, (await stops.StopAsync(new() { Id = id, Request = Guid.NewGuid().ToString() })).Outcome);
        Assert.Equal(1, injector.Calls);
    }
    [Fact]
    public async Task Own_launches_release_only_proven_live_capacity_not_hourly_or_daily_use()
    {
        Own();
        Assert.Equal("ended", (await End()).Outcome);
        Assert.Single(GateAuditProjection.Load(Folder).Rows, r => r.Action == Ops.SessionStop && r.Gate?.Phase == "intent");
        Assert.Equal(new GrantUsage(1, 1, 0), counters.Read(grant.Id, 1).Usage);
        source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Working };
        var result = await stops.StopAuditedAsync(new() { Id = id, Request = Guid.NewGuid().ToString() }, null, personConfirmation: false);
        Assert.Equal(StopOutcome.Confirm, result.Outcome);
        Assert.Null(result.Confirm);
        Assert.Equal(0, control.Terminations);
    }
    [Fact]
    public async Task Fleet_end_from_the_cli_ends_an_own_session_through_the_pipe_route()
    {
        Own();
        var (exit, json) = await Cli(Pipe());
        Assert.Equal((0, "ended", "end", true), (exit, json.GetProperty("outcome").GetString(), json.GetProperty("action").GetString(),
            json.GetProperty("session").GetString() == id.Replace("-", "")[..16]));
        Assert.Equal(1, injector.Calls);
    }
    [Fact]
    public async Task Fleet_end_without_a_grant_for_the_caller_is_refused_and_types_nothing()
    {
        Own();
        var (exit, json) = await Cli(Pipe(), Guid.NewGuid());
        Assert.Equal((5, "refused", "grant-missing"), (exit, json.GetProperty("outcome").GetString(), json.GetProperty("reason").GetString()));
        Assert.Equal(0, injector.Calls);
    }
    [Fact]
    public async Task Fleet_end_with_a_read_only_session_credential_is_refused_by_the_host()
    {
        Own();
        var token = credentials.Mint(Guid.NewGuid().ToString(), "person-launch", 101, started, AgentProtocol.ScopeRead);
        var reader = credentials.Check(token, new([new PeerProcess(101, started, "agent.exe")], true)).Accepted!;
        var (exit, json) = await Cli(Pipe(reader));
        Assert.Equal((5, "policy"), (exit, json.GetProperty("reason").GetString()));
        Assert.Equal(0, injector.Calls);
    }
    [Theory]
    [InlineData("script")]
    [InlineData("none")]
    [InlineData("person")]
    public async Task Fleet_end_from_a_script_a_caller_without_a_credential_or_a_person_client_is_refused_at_the_pipe(string who)
    {
        Own();
        var channel = new Chan();
        if (who == "script") { credentials.Accept(channel, credentials.MintScript("dispatcher").Credential); }
        var handle = LocalApi.Handler(sessions, null!, new FixedCallerGuard(new Caller(who == "person" ? CallerKind.App : CallerKind.Agent, "fixture")),
            credentials: credentials, fleetStop: Open());
        if (who == "person") { credentials.Accept(channel, caller); }
        var body = JsonSerializer.SerializeToElement(new FleetStopInput(1, Guid.NewGuid(), grant.Id, id), ProtocolJson.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, FleetStopCommands.Op, body));
        Assert.Equal(0, injector.Calls);
    }
    Func<string, object?, Task<JsonElement>> Pipe(AgentCredential? who = null)
    {
        var channel = new Chan();
        credentials.Accept(channel, who ?? caller);
        var handle = LocalApi.Handler(sessions, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")),
            credentials: credentials, fleetStop: Open());
        return async (op, data) => op is Ops.Hello or AgentProtocol.Hello ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(await handle(channel, op, JsonSerializer.SerializeToElement(data, ProtocolJson.Options)), ProtocolJson.Options);
    }
    async Task<(int Exit, JsonElement Json)> Cli(Func<string, object?, Task<JsonElement>> exchange, Guid? grantId = null)
    {
        using var output = new StringWriter();
        var exit = await FleetEndCommands.RunAsync(["fleet", "end", "--request", Guid.NewGuid().ToString(), "--grant", (grantId ?? grant.Id).ToString(),
            "--session", id], "unused", output, new StringWriter(), "fixture", exchange);
        using var parsed = JsonDocument.Parse(output.ToString());
        return (exit, parsed.RootElement.Clone());
    }
    // A stop request id is remembered from the audit, so a host restart (a fresh StopService, an empty
    // memory) never dispatches it again (fleet-grants.md, stop replay and audit).
    AgentAuditEntry Row(Guid request, string phase, string outcome, Guid? grantId = null, string? session = null) =>
        new("local", request.ToString(), credentials.PrincipalOf(caller), caller.Chain, Ops.SessionStop, session ?? id, outcome,
            Gate: new(1, Guid.NewGuid(), Guid.Empty, 0, phase, "local", "agent",
                StopAudit.Identity(Guid.Parse(id), 700, started), outcome == "attempted" ? "none" : outcome == "ended" ? "none" : "refused"))
        { Grant = (grantId ?? grant.Id).ToString() };
    [Fact]
    public async Task An_ended_stop_replays_from_the_audit_after_a_restart_without_dispatching()
    {
        Own();
        var request = Guid.NewGuid();
        var audit = new AgentAuditLog(Folder);
        Assert.True(audit.Append(Row(request, "intent", "attempted")));
        Assert.True(audit.Append(Row(request, "terminal", "ended")));
        var receipt = await End(request: request);
        Assert.Equal(("ended", null, 0, 0), (receipt.Outcome, receipt.Reason, injector.Calls, control.Terminations));
        Assert.Contains(GateAuditProjection.Load(Folder).Rows, r => r.Action == "fleet.end.replay" && r.Request == request.ToString());
    }
    [Fact]
    public async Task A_stop_whose_outcome_was_never_recorded_is_unconfirmed_and_not_dispatched_again()
    {
        Own();
        var request = Guid.NewGuid();
        Assert.True(new AgentAuditLog(Folder).Append(Row(request, "intent", "attempted")));
        var receipt = await End(request: request);
        Assert.Equal(("unconfirmed", "duplicate-id", 0, 0), (receipt.Outcome, receipt.Reason, injector.Calls, control.Terminations));
    }
    [Fact]
    public async Task A_request_id_already_bound_to_another_grant_is_refused()
    {
        Own();
        var request = Guid.NewGuid();
        var audit = new AgentAuditLog(Folder);
        Assert.True(audit.Append(Row(request, "intent", "attempted", grantId: Guid.NewGuid())));
        Assert.True(audit.Append(Row(request, "terminal", "ended", grantId: Guid.NewGuid())));
        var receipt = await End(request: request);
        Assert.Equal(("refused", "duplicate-id", 0), (receipt.Outcome, receipt.Reason, injector.Calls));
    }
    [Fact]
    public async Task A_request_refused_before_dispatch_may_be_sent_again_with_the_same_id()
    {
        Own();
        var request = Guid.NewGuid();
        Assert.True(new AgentAuditLog(Folder).Append(Row(request, "terminal", "refused")));
        Assert.Equal("ended", (await End(request: request)).Outcome);
        Assert.Equal(1, injector.Calls);
    }
    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
    sealed class Probe : IAgentHaBridgeProbe { public string? Active() => null; }
    sealed class Source : ISessionSource
    {
        public List<Session> Sessions = [];
        public Func<string, SessionProcess?> Binding = _ => null;
        public Func<Task>? BeforeEnding;
        public Action? BeforeGet;
        bool ending;
        public DiscoveryHeld? Held { get; set; }
        public SessionProcess? ProcessOf(string id) => Binding(id);
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => Sessions;
        public SessionList List(string? cursor = null) => new(Snapshot(), 0);
        public Session? GetForStop(string id) => Sessions.Find(s => s.Id == id);
        public Session? Get(string id)
        {
            BeforeGet?.Invoke();
            return GetForStop(id) is { } s && ending ? s with { Status = SessionStatus.Ending } : GetForStop(id);
        }
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public async Task SetEndingAsync(string id, bool value) { if (value && BeforeEnding is not null) { await BeforeEnding(); } ending = value; }
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    sealed class Control : IProcessControl
    {
        public bool Exited, GoneOnSecond, ExitOnTyping = true;
        public int Terminations, Opens;
        public Action? Before;
        public HeldOpen Open(int pid, DateTimeOffset at)
        {
            Opens++; Before?.Invoke();
            if (GoneOnSecond && Opens == 2) { Exited = true; return HeldOpen.Exited(); }
            return HeldOpen.Of(new Held(this, pid));
        }
        sealed class Held(Control owner, int pid) : IHeldProcess
        {
            public int Id => pid;
            public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(owner.Exited);
            public string? Terminate() { owner.Terminations++; return null; }
            public void Dispose() { }
        }
    }
    sealed class Injector(Control control) : IConsoleInjector
    {
        public int Calls;
        public Func<Task>? BeforeSlot;
        public async Task<InjectionResult> InjectAsync(int pid, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            if (BeforeSlot is not null) { await BeforeSlot(); }
            if (plan.Proceed?.Invoke() is { } stop) { return new(InjectionStatus.NothingTyped, stop); }
            Calls++; control.Exited = control.ExitOnTyping; return new(InjectionStatus.Typed, "fixture");
        }
    }
}
