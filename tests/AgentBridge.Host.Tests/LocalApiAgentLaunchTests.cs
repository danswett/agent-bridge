using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class LocalApiAgentLaunchTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ab-agent-launch-").FullName;
    readonly AgentCredentials credentials;
    readonly Launcher launcher = new();
    readonly Changes source = new();
    readonly List<string> messages = [];
    readonly Channel channel = new();
    readonly Func<IProtocolChannel, string, JsonElement, Task<object?>> handle;
    const string Principal = "agent:machine/parent";

    public LocalApiAgentLaunchTests()
    {
        Policy();
        credentials = new(root, "machine");
        Authenticate();
        handle = LocalApi.Handler(new SessionService(source, new("machine", "Test", "windows", "0.1.0"), launcher: launcher, log: messages.Add),
            null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")), credentials: credentials, audit: new(root));
    }

    public void Dispose() => Directory.Delete(root, true);
    void Policy(bool enabled = true, int live = 6, int machine = 10, int attempts = 20) => File.WriteAllText(Path.Combine(root, HostSettings.FileName),
        JsonSerializer.Serialize(new { agents = new { enabled = true, launch = new { enabled },
            inbound = new { local = new { launch = new { workspaces = new[] { "App" }, agents = new[] { "claude" }, permissions = "ask" } } },
            limits = new { launchesPerOriginPerHour = 3, launchesPerMachinePerHour = machine,
                launchAttemptsPerOriginPerHour = attempts, liveAgentLaunched = live, agentLaunchedIdleHours = 2 } } }));

    void Authenticate(string scope = AgentProtocol.ScopeReadReplyLaunch, int depth = 0, string launch = "launch")
    {
        var at = DateTimeOffset.UtcNow;
        var token = credentials.Mint("parent", launch, 100, at, scope, depth);
        credentials.Accept(channel, credentials.Check(token, new ProcessAncestry([new PeerProcess(100, at, "agent.exe")], true)).Accepted!);
    }

    static JsonElement Body(string? request = null, string permissions = "ask", string workspace = "App", string prompt = "private prompt") =>
        JsonSerializer.SerializeToElement(new { request = request ?? Guid.NewGuid().ToString("D"), agent = "claude", workspace, permissions, prompt });
    async Task<LaunchResult> Launch(JsonElement body) => Assert.IsType<LaunchResult>(await handle(channel, LaunchProtocol.Launch, body));
    JsonElement[] Rows() => Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "launches-*.jsonl")
        .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
        .Where(row => row.TryGetProperty("action", out _)).ToArray();

    [Fact]
    public async Task A_permitted_agent_launch_starts_as_its_credential_principal_and_repeats_without_starting_again()
    {
        var body = Body();
        launcher.BeforePrepare = () => Assert.Equal(0, launcher.Starts);
        var result = await Launch(body);
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(Launcher.Session, result.Session);
        Assert.Equal(Principal, launcher.Principal);
        Assert.Null(result.Trust);
        Assert.Null(result.Launched?.Cwd);
        Assert.Equal(result.Session, (await Launch(body)).Session);
        Assert.Equal(1, launcher.Starts);
        Assert.Contains(Rows(), row => row.GetProperty("outcome").GetString() == "reserved");
        Assert.Contains(Rows(), row => row.GetProperty("outcome").GetString() == "started");
        Assert.DoesNotContain("private prompt", string.Join("\n", Rows().Select(row => row.GetRawText())));
        Assert.Equal(LaunchOutcome.Refused, (await Launch(Body(body.GetProperty("request").GetString(), prompt: "changed"))).Outcome);
        Assert.Equal(1, launcher.Starts);
    }

    [Theory]
    [InlineData(AgentProtocol.ScopeRead)]
    [InlineData(AgentProtocol.ScopeReadReplyLaunch)]
    public async Task A_null_session_service_never_bypasses_the_agent_launch_refusal_or_reserves_capacity(string scope)
    {
        Authenticate(scope);
        var noService = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")),
            credentials: credentials, audit: new(root));
        if (scope == AgentProtocol.ScopeRead)
        {
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => noService(channel, LaunchProtocol.Launch, Body()));
            Assert.Contains("for the person", refusal.Message);
        }
        else
        {
            var refusal = Assert.IsType<LaunchResult>(await noService(channel, LaunchProtocol.Launch, Body()));
            Assert.Equal(LaunchOutcome.Refused, refusal.Outcome);
            Assert.Contains("unavailable", refusal.Reason);
        }
        Assert.Equal(0, launcher.Starts);
        Assert.Empty(credentials.LaunchGate.Reservations());
        Assert.Equal("refused", Assert.Single(Rows()).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("switch", "agents.launch.enabled")]
    [InlineData("permissions", "permissions")]
    [InlineData("arguments", "extra arguments")]
    [InlineData("workspace", "approved workspace")]
    [InlineData("depth", "agent-started")]
    [InlineData("adopted", "adopted")]
    [InlineData("unlisted-agent", "agent")]
    [InlineData("unknown-field", "unsupported field")]
    public async Task A_disallowed_agent_launch_returns_a_readable_audited_refusal_without_dispatch(string condition, string reason)
    {
        var body = Body();
        if (condition == "switch") { Policy(false); }
        if (condition == "permissions") { body = Body(permissions: "allowAll"); }
        if (condition == "arguments") { launcher.Extras = ["--private-value"]; }
        if (condition == "workspace") { body = Body(workspace: "Other"); }
        if (condition == "depth") { Authenticate(depth: 1); }
        if (condition == "adopted") { Authenticate(launch: AgentCredentials.AdoptedLaunch); }
        if (condition == "unlisted-agent") { body = JsonSerializer.SerializeToElement(new { request = Guid.NewGuid().ToString("D"), agent = "copilot", workspace = "App", permissions = "ask" }); }
        if (condition == "unknown-field") { body = JsonSerializer.SerializeToElement(new { request = Guid.NewGuid().ToString("D"), permissions = "ask", principal = "person" }); }

        var result = await Launch(body);
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains(reason, result.Reason);
        Assert.Equal(0, launcher.Starts);
        Assert.Contains(Rows(), row => row.GetProperty("outcome").GetString() == "refused"
            && row.GetProperty("principal").GetString() == Principal);
        Assert.DoesNotContain("--private-value", string.Join("\n", Rows().Select(row => row.GetRawText())));
    }

    [Fact]
    public async Task Read_scope_stays_read_only_and_revocation_prevents_both_launch_and_readback()
    {
        Authenticate(AgentProtocol.ScopeRead);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, LaunchProtocol.Launch, Body()));
        Assert.Equal("refused", Assert.Single(Rows()).GetProperty("outcome").GetString());
        Authenticate();
        var body = Body();
        Assert.Equal(LaunchOutcome.Started, (await Launch(body)).Outcome);
        credentials.RevokeSession("parent", "fixture");
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, LaunchProtocol.Launch, body));
        Assert.Equal(1, launcher.Starts);
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("switch")]
    [InlineData("arguments")]
    public async Task The_last_prestart_check_rejects_authority_or_arguments_changed_during_dispatch(string change)
    {
        launcher.BeforePrepare = () =>
        {
            if (change == "credential") { credentials.RevokeSession("parent", "fixture"); }
            if (change == "switch") { Policy(false); }
            if (change == "arguments") { launcher.Extras = ["--changed"]; }
        };
        Assert.Equal(LaunchOutcome.Refused, (await Launch(Body())).Outcome);
        Assert.Equal(0, launcher.Starts);
        Assert.Contains(Rows(), row => row.GetProperty("outcome").GetString() == "refused");
    }

    [Fact]
    public async Task A_prestart_refund_preserves_prior_successful_hourly_charges()
    {
        Policy(machine: 2);
        Assert.Equal(LaunchOutcome.Started, (await Launch(Body())).Outcome);
        launcher.BeforeStartOutcome = LaunchOutcome.Refused;
        Assert.Equal(LaunchOutcome.Refused, (await Launch(Body())).Outcome);
        launcher.BeforeStartOutcome = null;
        Assert.Equal(LaunchOutcome.Started, (await Launch(Body())).Outcome);
        Assert.Contains("rate-limited", (await Launch(Body())).Reason);
        Assert.Equal(2, launcher.Starts);
    }

    [Theory]
    [InlineData(LaunchOutcome.Refused)]
    [InlineData(LaunchOutcome.Busy)]
    public async Task A_proven_prestart_refusal_refunds_hourly_and_live_slots(LaunchOutcome outcome)
    {
        Policy(live: 1, machine: 1);
        launcher.BeforeStartOutcome = outcome;
        for (var i = 0; i < 3; i++) { Assert.Equal(outcome, (await Launch(Body())).Outcome); }
        launcher.BeforeStartOutcome = null;
        Assert.Equal(LaunchOutcome.Started, (await Launch(Body())).Outcome);
        Assert.Contains("live-limit", (await Launch(Body())).Reason);
        Assert.Equal(1, launcher.Starts);
    }

    [Theory]
    [InlineData(LaunchOutcome.Unconfirmed)]
    [InlineData(LaunchOutcome.Failed)]
    public async Task An_uncertain_dispatch_keeps_its_live_reservation(LaunchOutcome outcome)
    {
        Policy(live: 1);
        launcher.BeforeStartOutcome = outcome;
        Assert.Equal(outcome, (await Launch(Body())).Outcome);
        launcher.BeforeStartOutcome = null;
        Assert.Contains("live-limit", (await Launch(Body())).Reason);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task A_refunded_request_stays_remembered_and_refusals_still_spend_attempts()
    {
        Policy(attempts: 2);
        var body = Body();
        launcher.BeforeStartOutcome = LaunchOutcome.Refused;
        Assert.Equal(LaunchOutcome.Refused, (await Launch(body)).Outcome);
        launcher.BeforeStartOutcome = null;
        Assert.Contains("duplicate", (await Launch(body)).Reason);
        Assert.Equal(LaunchOutcome.Started, (await Launch(Body())).Outcome);
        Assert.Contains("rate-limited", (await Launch(Body())).Reason);
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task A_busy_prestart_records_one_refusal_for_the_authenticated_request()
    {
        var body = Body();
        launcher.BeforeStartOutcome = LaunchOutcome.Busy;
        Assert.Equal(LaunchOutcome.Busy, (await Launch(body)).Outcome);
        var row = Assert.Single(Rows());
        Assert.Equal("refused", row.GetProperty("outcome").GetString());
        Assert.Equal(body.GetProperty("request").GetString(), row.GetProperty("request").GetString());
        Assert.Equal(Principal, row.GetProperty("principal").GetString());
        Assert.Contains("busy", row.GetProperty("reason").GetString());
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task A_busy_prestart_cannot_return_without_writable_refusal_evidence()
    {
        launcher.BeforeStartOutcome = LaunchOutcome.Busy;
        launcher.BeforePrepare = () =>
        {
            var folder = Path.Combine(root, AgentAuditLog.FolderName);
            Directory.Move(folder, folder + "-saved");
            File.WriteAllText(folder, "blocked");
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Launch(Body()));
        Assert.Contains("auditUnwritable", error.Message);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public async Task Authoritative_exit_closes_an_unconfirmed_launch_once_without_refunding_its_hourly_debit()
    {
        Policy(live: 1, machine: 2);
        var first = Body();
        launcher.BeforeStartOutcome = LaunchOutcome.Unconfirmed;
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Launch(first)).Outcome);
        launcher.Exited = true;
        launcher.Fail(Guid.Parse(first.GetProperty("request").GetString()!));
        launcher.Fail(Guid.Parse(first.GetProperty("request").GetString()!));
        launcher.Exited = null;
        launcher.BeforeStartOutcome = null;
        var second = Body();
        Assert.Equal(LaunchOutcome.Started, (await Launch(second)).Outcome);
        launcher.Exited = true;
        launcher.Fail(Guid.Parse(second.GetProperty("request").GetString()!));
        launcher.Exited = null;
        Assert.Contains("rate-limited", (await Launch(Body())).Reason);
    }

    [Fact]
    public async Task A_failed_signal_with_unknown_process_evidence_does_not_free_live_capacity()
    {
        Policy(live: 1);
        var first = Body();
        launcher.BeforeStartOutcome = LaunchOutcome.Unconfirmed;
        await Launch(first);
        launcher.Fail(Guid.Parse(first.GetProperty("request").GetString()!));
        launcher.BeforeStartOutcome = null;
        Assert.Contains("live-limit", (await Launch(Body())).Reason);
    }

    [Fact]
    public async Task Session_removal_needs_exit_proof_and_closes_the_bound_session_without_refund()
    {
        Policy(live: 1, machine: 2);
        var first = await Launch(Body());
        source.Remove(first.Session!);
        Assert.Contains("live-limit", (await Launch(Body())).Reason);
        launcher.Exited = true;
        source.Remove(first.Session!);
        launcher.Exited = null;
        var second = await Launch(Body());
        Assert.Equal(LaunchOutcome.Started, second.Outcome);
        launcher.Exited = true;
        source.Remove(second.Session!);
        launcher.Exited = null;
        Assert.Contains("rate-limited", (await Launch(Body())).Reason);
    }

    [Fact]
    public async Task An_unknown_bound_process_stays_held_and_surfaces_needs_person()
    {
        Policy(live: 1);
        var first = await Launch(Body());
        messages.Clear();
        source.Remove(first.Session!);
        Assert.Contains(messages, message => message.Contains("needs-person", StringComparison.Ordinal));
        Assert.Contains("live-limit", (await Launch(Body())).Reason);
    }

    [Fact]
    public void Invalid_durable_state_holds_lifecycle_capacity_without_crashing_the_observer()
    {
        Assert.True(credentials.LaunchGate.CheckLaunch(Principal, "pending", "hash").Allowed);
        var state = Path.Combine(root, "launch-admissions", "state.json");
        File.WriteAllText(state, "null");
        messages.Clear();
        Assert.Null(Record.Exception(() =>
        { _ = new AgentLaunchLifecycle(credentials.LaunchGate, launcher, source, new(root), messages.Add); }));
        Assert.Contains(messages, message => message.Contains("needs-person", StringComparison.Ordinal));
        Assert.Equal("null", File.ReadAllText(state));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restart_uses_durable_no_start_proof_but_never_a_failed_outcome_alone(bool proven)
    {
        Policy(live: 1, machine: 1);
        var body = Body();
        launcher.BeforeStartOutcome = LaunchOutcome.Unconfirmed;
        await Launch(body);
        var request = body.Deserialize<LaunchRequest>(ProtocolJson.Options)!;
        var operation = Guid.NewGuid();
        var gate = new GateAuditMetadata(1, operation, Guid.Empty, 0, "intent", "local", "agent", operation, "none",
            LaunchBinding: LaunchService.RequestBinding(request));
        var audit = new AgentAuditLog(root);
        var intent = new AgentAuditEntry("local", request.Request, Principal, [], "session.launch", request.Request, "reserved", Gate: gate);
        Assert.True(audit.Append(intent));
        Assert.True(audit.Append(intent with { Outcome = "failed", Grant = proven ? "agent-closed" : null,
            Gate = gate with { Phase = "terminal", ReasonCode = "failed" } }));
        var restarted = new AgentCredentials(root, "machine");
        _ = LocalApi.Handler(new SessionService(source, new("machine", "Test", "windows", "0.1.0"), launcher: launcher),
            null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")), credentials: restarted, audit: audit);
        Assert.Contains(proven ? "rate-limited" : "live-limit", restarted.LaunchGate.CheckLaunch("other", "next", "hash").Reason);
    }

    [Fact]
    public async Task The_hourly_cap_refuses_the_fourth_launch_and_records_the_reason()
    {
        for (var i = 0; i < 3; i++) { Assert.Equal(LaunchOutcome.Started, (await Launch(Body())).Outcome); }
        var result = await Launch(Body());
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("rate-limited", result.Reason);
        Assert.Contains(Rows(), row => row.TryGetProperty("reason", out var reason) && reason.GetString()?.Contains("rate-limited") == true);
        Assert.Equal(3, launcher.Starts);
    }

    [Fact]
    public async Task An_unwritable_audit_never_reaches_the_starter()
    {
        File.WriteAllText(Path.Combine(root, AgentAuditLog.FolderName), "blocked");
        Assert.Equal(LaunchOutcome.Refused, (await Launch(Body())).Outcome);
        Assert.Equal(0, launcher.Starts);
    }

    sealed class Changes : ISessionSource
    {
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed;
        public void Remove(string id) => Changed?.Invoke(this, new(SessionChangeKind.Removed, id, null, 0));
        public IReadOnlyList<Session> Snapshot() => [];
        public Session? Get(string id) => null;
        public SessionList List(string? cursor = null) => new([], 0);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Launcher : ISessionLauncher
    {
        public const string Session = "22222222-2222-4222-8222-222222222222";
        public bool Available => true;
        public int Starts;
        public string? Principal;
        public LaunchOutcome? BeforeStartOutcome;
        public string[] Extras = [];
        public bool? Exited;
        public bool? RequestExited(Guid request) => Exited;
        public event Action<Guid>? RequestFailed;
        public void Fail(Guid request) => RequestFailed?.Invoke(request);
        public Action? BeforePrepare;
        readonly Dictionary<string, LaunchResult> results = [];
        public LaunchDispatchSnapshot CaptureDispatch(string agent, string? principal) => new(Extras, principal);
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LaunchOptions
        {
            Agents = [new() { Id = "claude", Label = "Claude", Kind = AgentKind.Claude }],
            Workspaces = [new() { Label = "App", Path = @"C:\fixture", Isolate = true }],
            Defaults = new() { Permissions = LaunchProtocol.Ask },
        });
        public Task<LaunchResult>? ReplayAsync(LaunchRequest request, string principal) =>
            results.TryGetValue(principal + LaunchService.RequestBinding(request), out var result) ? Task.FromResult(result) : null;
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Agent launches must use the audited entry point.");
        public Task<LaunchResult> LaunchAuditedAsync(LaunchRequest request, string peer, Func<Guid, Action<string>?> prepare, CancellationToken cancellationToken = default)
        {
            BeforePrepare?.Invoke();
            if (BeforeStartOutcome is { } outcome) { return Task.FromResult(new LaunchResult(outcome, "prestart outcome")); }
            if (prepare(Guid.NewGuid()) is not { } registered) { return Task.FromResult(new LaunchResult(LaunchOutcome.Refused, "prestart refusal")); }
            Starts++;
            Principal = peer;
            var session = Starts == 1 ? Session : Guid.NewGuid().ToString("D");
            registered(session);
            var result = new LaunchResult(LaunchOutcome.Started) { Session = session };
            results.Add(peer + LaunchService.RequestBinding(request), result);
            return Task.FromResult(result);
        }
    }

    internal sealed class Channel : IProtocolChannel
    {
        public string PeerId => "untrusted-peer-label";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}

public sealed class LocalApiAgentLaunchFlowTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_real_worktree_refusal_has_one_audit_even_when_the_callback_cannot_record_it(bool callbackWrites, bool failHeadWrite)
    {
        File.WriteAllText(Path.Combine(f.Root, HostSettings.FileName), """
            {"agents":{"enabled":true,"launch":{"enabled":true},
              "inbound":{"local":{"launch":{"workspaces":["App"],"agents":["claude"],"permissions":"ask"}}},
              "limits":{"launchesPerOriginPerHour":3,"launchesPerMachinePerHour":10,"liveAgentLaunched":6,"agentLaunchedIdleHours":2}}}
            """);
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, ".mcp.json"), "{}");
        Git(app, "add", ".mcp.json");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var credentials = new AgentCredentials(f.Root, "machine");
        var channel = new LocalApiAgentLaunchTests.Channel();
        var at = DateTimeOffset.UtcNow;
        var token = credentials.Mint("parent", "launch", 100, at, AgentProtocol.ScopeReadReplyLaunch);
        credentials.Accept(channel, credentials.Check(token, new ProcessAncestry([new PeerProcess(100, at, "agent.exe")], true)).Accepted!);
        var audit = new AgentAuditLog(f.Root);
        if (failHeadWrite)
        {
            var seed = Guid.NewGuid();
            Assert.True(audit.Append(new("local", seed.ToString(), "person:local", [], "session.stop", seed.ToString(), "reserved",
                Gate: new(1, seed, Guid.NewGuid(), 0, "intent", "local", "person-client", seed, "none"))));
            Assert.True(audit.Append(new("local", seed.ToString(), "person:local", [], LaunchProtocol.Launch, "local", "started")));
        }
        var calls = 0;
        bool? callbackResult = null;
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }),
            credentials: credentials, agentWorktreeCheck: AgentLaunchWorktreeCheck.Check,
            agentWorktreeRefusalAudit: (request, principal, reason) =>
            {
                calls++;
                if (!callbackWrites) { return false; }
                var blocked = Path.Combine(f.Root, AgentAuditLog.FolderName, "launches.head.new");
                if (failHeadWrite) { Directory.CreateDirectory(blocked); }
                try
                {
                    callbackResult = audit.Append(new("local", request.ToString("D"), principal, [],
                        LaunchProtocol.Launch, "local", "refused", reason) { Grant = "agent-worktree-refusal" });
                    return callbackResult.Value;
                }
                finally { if (failHeadWrite) { Directory.Delete(blocked); } }
            });
        var handle = LocalApi.Handler(new SessionService(source, new("machine", "Test", "windows", "0.1.0"), launcher: launcher),
            null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")), credentials: credentials, audit: audit);
        var body = JsonSerializer.SerializeToElement(new { request = Guid.NewGuid().ToString("D"), agent = "claude", workspace = "App", permissions = "ask", prompt = "worktree refusal fixture" });
        var result = Assert.IsType<LaunchResult>(await handle(channel, LaunchProtocol.Launch, body));
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(callbackWrites && !failHeadWrite, callbackResult == true);
        Assert.DoesNotContain("refusalAudited", JsonSerializer.Serialize(result, ProtocolJson.Options));
        Assert.DoesNotContain("worktreeRefusalReason", JsonSerializer.Serialize(result with { WorktreeRefusalReason = "host-only" }, ProtocolJson.Options));
        Assert.Empty(starter.Specs);
        var rows = Directory.GetFiles(Path.Combine(f.Root, AgentAuditLog.FolderName), "launches-*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(row => row.TryGetProperty("action", out _) && row.GetProperty("outcome").GetString() == "refused");
        var row = Assert.Single(rows);
        Assert.Equal(body.GetProperty("request").GetString(), row.GetProperty("request").GetString());
        Assert.Equal("agent:machine/parent", row.GetProperty("principal").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Only_a_proven_CreateProcess_failure_frees_live_capacity_and_neither_outcome_refunds_the_hour(int state)
    {
        var proven = state is 0 or 3 or 4;
        File.WriteAllText(Path.Combine(f.Root, HostSettings.FileName), """
            {"agents":{"enabled":true,"launch":{"enabled":true},
              "inbound":{"local":{"launch":{"workspaces":["App"],"agents":["claude"],"permissions":"ask"}}},
              "limits":{"launchesPerOriginPerHour":3,"launchesPerMachinePerHour":1,"liveAgentLaunched":1,"agentLaunchedIdleHours":2}}}
            """);
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture");
        Git(app, "add", "README.md");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var credentials = new AgentCredentials(f.Root, "machine");
        var channel = new LocalApiAgentLaunchTests.Channel();
        var at = DateTimeOffset.UtcNow;
        var token = credentials.Mint("parent", "launch", 100, at, AgentProtocol.ScopeReadReplyLaunch);
        credentials.Accept(channel, credentials.Check(token, new ProcessAncestry([new PeerProcess(100, at, "agent.exe")], true)).Accepted!);
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }),
            credentials: credentials, agentWorktreeCheck: AgentLaunchWorktreeCheck.Check);
        var handle = LocalApi.Handler(new SessionService(source, new("machine", "Test", "windows", "0.1.0"), launcher: launcher),
            null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")), credentials: credentials, audit: new(f.Root));
        async Task<LaunchResult> Launch()
        {
            var id = Guid.NewGuid().ToString("D");
            return Assert.IsType<LaunchResult>(await handle(channel, LaunchProtocol.Launch,
                JsonSerializer.SerializeToElement(new { request = state >= 3 ? "ABCDEFAB" + id[8..].ToUpperInvariant() : id, agent = "claude", workspace = "App", permissions = "ask" })));
        }
        if (state == 2)
        {
            Assert.Equal(LaunchOutcome.Started, (await Launch()).Outcome);
            starter.Exit(Assert.Single(starter.Pids));
            var restarted = new AgentCredentials(f.Root, "machine");
            _ = LocalApi.Handler(new SessionService(source, new("machine", "Test", "windows", "0.1.0"),
                launcher: Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }), credentials: restarted)),
                null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")), credentials: restarted, audit: new(f.Root));
            Assert.Empty(restarted.LaunchGate.Reservations());
            Assert.Contains("rate-limited", (await Launch()).Reason);
            return;
        }
        starter.Throws = proven ? new System.ComponentModel.Win32Exception(2) : new IOException("start result unknown");
        Assert.Equal(proven ? LaunchOutcome.Failed : LaunchOutcome.Unconfirmed, (await Launch()).Outcome);
        starter.Throws = null;
        if (state == 4)
        {
            var restarted = new AgentCredentials(f.Root, "machine");
            _ = LocalApi.Handler(new SessionService(source, new("machine", "Test", "windows", "0.1.0"),
                launcher: Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }), credentials: restarted)),
                null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")), credentials: restarted, audit: new(f.Root));
            Assert.Empty(restarted.LaunchGate.Reservations());
        }
        Assert.Contains(proven ? "rate-limited" : "live-limit", (await Launch()).Reason);
    }

    [Fact]
    public async Task A_permitted_pipe_launch_uses_the_real_worktree_checks_and_only_the_fake_process_starter()
    {
        File.WriteAllText(Path.Combine(f.Root, HostSettings.FileName), """
            {"agents":{"enabled":true,"launch":{"enabled":true},
              "inbound":{"local":{"launch":{"workspaces":["App"],"agents":["claude"],"permissions":"ask"}}},
              "limits":{"launchesPerOriginPerHour":3,"launchesPerMachinePerHour":10,"liveAgentLaunched":6,"agentLaunchedIdleHours":2}}}
            """);
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture");
        Git(app, "add", "README.md");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var credentials = new AgentCredentials(f.Root, "machine");
        var channel = new LocalApiAgentLaunchTests.Channel();
        var at = DateTimeOffset.UtcNow;
        var token = credentials.Mint("parent", "launch", 100, at, AgentProtocol.ScopeReadReplyLaunch);
        credentials.Accept(channel, credentials.Check(token, new ProcessAncestry([new PeerProcess(100, at, "agent.exe")], true)).Accepted!);
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }),
            credentials: credentials, agentWorktreeCheck: AgentLaunchWorktreeCheck.Check);
        var handle = LocalApi.Handler(new SessionService(source, new("machine", "Test", "windows", "0.1.0"), launcher: launcher),
            null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")), credentials: credentials, audit: new(f.Root));
        var body = JsonSerializer.SerializeToElement(new { request = Guid.NewGuid().ToString("D"), agent = "claude", workspace = "App", permissions = "ask" });
        var result = Assert.IsType<LaunchResult>(await handle(channel, LaunchProtocol.Launch, body));
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.NotNull(result.Session);
        Assert.Single(starter.Specs);
        Assert.Null(result.Launched?.Cwd);
    }
}
