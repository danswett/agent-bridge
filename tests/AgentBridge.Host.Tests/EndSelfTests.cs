using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Lite;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class EndSelfTests : IDisposable
{
    const string Self = "77777777-7777-4777-8777-777777777777", Node = "agent_bridge_0123456789abcdef";
    readonly Fixture f = new();
    readonly FleetPublisherTests.Source source = new();
    readonly Actions actions = new();
    readonly AgentCredentials credentials;
    readonly AgentAuditLog audit;
    readonly SessionService service;
    readonly Chan agent = new(), person = new();
    readonly Sender sender = new();
    SessionProcess process = new(700, DateTimeOffset.UtcNow.AddMinutes(-1));

    public EndSelfTests()
    {
        source.Sessions = [source.Sessions[0] with { Id = Self, Agent = AgentKind.Claude, Status = SessionStatus.Idle }];
        source.Binding = _ => process;
        f.Processes.Run(AgentKind.Claude, process.Id, startedAt: process.StartedAt);
        credentials = new(f.Root, "fixture");
        audit = new(f.Root);
        Hello(agent, Self, process.Id);
        actions.BeforeStop = () => Assert.Contains(GateAuditProjection.Load(f.Root).Rows, r => r.Gate?.Phase == "intent");
        service = new(source, new("fixture", "Fixture", "windows", "test"), launcher: actions,
            stops: new StopService(source, f.Processes, actions, actions, actions,
                new StopOptions { CopilotHome = f.CopilotHome, PlatformSupported = true }));
    }

    void Hello(Chan channel, string session, int pid)
    {
        var token = credentials.Mint(session, "launch", pid, process.StartedAt!.Value, AgentProtocol.ScopeRead);
        credentials.Accept(channel, credentials.Check(token,
            new ProcessAncestry([new PeerProcess(pid, process.StartedAt, "agent.exe")], true)).Accepted!);
    }
    Task<object?> Call(string op, object body, CallerKind kind = CallerKind.HostCli, Chan? channel = null) =>
        LocalApi.Handler(service, null!, new FixedCallerGuard(new Caller(kind, "fixture")),
            credentials: credentials, lite: sender, audit: audit)(channel ?? person, op, JsonSerializer.SerializeToElement(body));
    async Task<string> Send() => Assert.IsType<LiteSendReply>(await Call("agent.send",
        new { machine = "target", session = Node, text = "final report" }, CallerKind.Agent, agent)).Request;
    async Task<string> Admit(string receipt)
    {
        var result = await Call("session.endSelf.admit", new { receipt, machine = "target", target = Node });
        Assert.NotNull(result);
        return JsonSerializer.SerializeToElement(result).GetProperty("grant").GetString()!;
    }
    Task<object?> End(string grant, CallerKind kind = CallerKind.HostCli, Chan? channel = null) =>
        Call("EndSelf", new { grant, request = Guid.NewGuid().ToString() }, kind, channel);
    public void Dispose() => f.Dispose();

    [Theory]
    [InlineData("missing")]
    [InlineData("forged")]
    [InlineData("unconfirmed")]
    [InlineData("sender")]
    [InlineData("machine")]
    [InlineData("target")]
    public async Task Receipt_queries_refuse_missing_unconfirmed_or_wrong_sender_and_target(string fault)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        if (fault == "unconfirmed") { sender.Kind = LiteKind.Unconfirmed; }
        var receipt = await Send();
        if (fault == "missing") { receipt = Guid.NewGuid().ToString(); }
        if (fault == "forged")
        { receipt = Guid.NewGuid().ToString(); Assert.True(audit.Append(new("out", receipt, "agent:fixture/" + Self, [], "agent.send", "target/" + Node, "delivered"))); }
        var who = agent;
        if (fault == "sender") { who = new(); Hello(who, Guid.NewGuid().ToString(), 701); }
        var result = JsonSerializer.SerializeToElement(await Call("agent.receipt", new
        {
            receipt, machine = fault == "machine" ? "wrong" : "target",
            target = fault == "target" ? "agent_bridge_ffffffffffffffff" : Node,
        }, CallerKind.Agent, who));
        Assert.False(result.GetProperty("confirmed").GetBoolean());
        Assert.Equal(0, actions.Stops);
    }

    [Theory]
    [InlineData(CallerKind.Agent)]
    [InlineData(CallerKind.App)]
    [InlineData(CallerKind.HostCli)]
    public async Task Only_a_noncredentialed_HostCli_can_admit_or_redeem_a_grant(CallerKind kind)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var receipt = await Send();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call("session.endSelf.admit",
            new { receipt, machine = "target", target = Node }, kind, kind == CallerKind.App ? person : agent));
        var grant = await Admit(receipt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => End(grant, kind, kind == CallerKind.App ? person : agent));
        Assert.Equal(0, actions.Stops);
        Assert.Equal(StopOutcome.Ended, Assert.IsType<StopResult>(await End(grant)).Outcome);
    }

    [Theory]
    [InlineData("lead")]
    [InlineData("preserved")]
    [InlineData("unknown")]
    [InlineData(null)]
    public async Task Only_a_host_owned_one_shot_role_can_receive_a_grant(string? role)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        actions.Role = role;
        var receipt = await Send();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(receipt));
        Assert.Equal(0, actions.Stops);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("other-session")]
    [InlineData("incarnation")]
    [InlineData("dispatch-race")]
    [InlineData("credential")]
    public async Task A_grant_cannot_be_forged_retargeted_or_used_after_its_binding_changes(string fault)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var grant = await Admit(await Send());
        if (fault == "absent") { grant = Guid.NewGuid().ToString(); }
        if (fault == "incarnation") { process = process with { StartedAt = process.StartedAt!.Value.AddSeconds(2) }; }
        if (fault == "credential") { credentials.RevokeSession(Self, "test"); }
        if (fault == "dispatch-race") { actions.OnOpen = () => process = process with { Id = 701 }; }
        if (fault == "other-session")
        { await Assert.ThrowsAsync<InvalidOperationException>(() => Call("EndSelf", new { grant, request = Guid.NewGuid().ToString(), session = Guid.NewGuid().ToString() })); }
        else
        {
            object? result = null;
            var error = await Record.ExceptionAsync(async () => { result = await End(grant); });
            if (error is null) { Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(result).Outcome); }
            else { Assert.IsType<InvalidOperationException>(error); }
        }
        Assert.Equal(0, actions.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receipts_and_grants_are_one_use_even_if_execution_is_interrupted_after_the_durable_intent(bool crash)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var receipt = await Send();
        var grant = await Admit(receipt);
        actions.Crash = crash;
        Assert.Equal(crash ? StopOutcome.Failed : StopOutcome.Ended, Assert.IsType<StopResult>(await End(grant)).Outcome);
        Assert.Equal(crash ? 0 : 1, actions.Stops);
        Assert.Contains(GateAuditProjection.Load(f.Root).Rows, r => r.Gate?.Phase == "intent" && r.Principal == "agent:fixture/" + Self);
        await Assert.ThrowsAsync<InvalidOperationException>(() => End(grant));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(receipt));
    }

    sealed class Sender : ILiteSender
    {
        public LiteKind Kind = LiteKind.Delivered;
        public Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct) =>
            Task.FromResult(new LiteOutcome(Kind, null, Guid.NewGuid().ToString()));
    }
    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "fixture"; public string PeerName => "fixture"; public string PeerKind => "fixture";
        public bool IsOpen => true; public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
    sealed class Actions : ISessionLauncher, IConsoleInjector, IProcessControl, IHeldProcess, IAgentHaBridgeProbe
    {
        public string? Role = "one-shot"; public int Stops; public bool Crash, Interrupted;
        public bool Available => true;
        public Action? BeforeStop, OnOpen;
        public string? RoleOf(string id, SessionProcess? process) => "one-shot";
        public string? TrustedRoleOf(string id, SessionProcess? process) => Role;
        public Task<LaunchOptions> OptionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken ct = default) => throw new NotSupportedException();
        public string? Active() => Interrupted ? "interrupted" : null;
        public HeldOpen Open(int pid, DateTimeOffset at) { OnOpen?.Invoke(); return HeldOpen.Of(this); }
        public int Id => 700; public void Dispose() { }
        public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(Stops > 0);
        public string? Terminate() => throw new InvalidOperationException("unexpected termination");
        public Task<InjectionResult> InjectAsync(int pid, KeyPlan plan, CancellationToken ct = default)
        {
            BeforeStop?.Invoke();
            if (Crash) { Interrupted = true; throw new IOException("simulated interruption before stop"); }
            Stops++;
            return Task.FromResult(new InjectionResult(InjectionStatus.Typed, "fixture"));
        }
    }
}
