using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

[Trait("Category", "Network")]
public sealed class AgentAdoptionPipeTests : IAsyncLifetime
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abap3-" + Guid.NewGuid().ToString("N")[..8]);
    readonly FakeTimeProvider clock = new();
    readonly List<string> logs = [];
    readonly Fixture fixture = new();
    readonly Callers callers = new();
    readonly Launcher launcher = new();
    AgentCredentials store = null!;
    SessionSource source = null!;
    SessionService sessions = null!;
    LocalServer server = null!;
    const string Session = "88888888-8888-4888-8888-888888888888";
    static DateTimeOffset Started { get { using var p = Process.GetCurrentProcess(); return p.StartTime.ToUniversalTime(); } }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        fixture.CopilotSession(Session, Environment.ProcessId);
        fixture.Processes.Run(AgentKind.Copilot, Environment.ProcessId);
        source = new SessionSource(fixture.Options(), fixture.Processes);
        await source.RefreshAsync();
        sessions = new SessionService(source, new MachineInfo("machine1", "Fixture", "windows", "0.1.0"), launcher: launcher);
        store = new AgentCredentials(root, "machine1", clock, logs.Add, (_, _) => true);
        server = LocalServer.Start(LocalEndpoint.For(root), LocalApi.Handler(sessions, null!, callers, logs.Add, credentials: store));
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        await source.DisposeAsync();
        fixture.Dispose();
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task The_pipe_issues_once_inside_the_window_and_never_logs_the_token()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        store.Adopt(Session, Environment.ProcessId, Started, AgentProtocol.ScopeRead);
        await using var ch = await LocalClient.ConnectAsync(LocalEndpoint.For(root));
        var claim = await ch.CallAsync<AgentCredentialResult>(AgentProtocol.Credential);
        Assert.Equal("agent:machine1/" + Session, claim.Principal);
        Assert.DoesNotContain(logs, l => l.Contains(claim.Token));
        var twice = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(AgentProtocol.Credential));
        Assert.Equal("refused: no adopted session covers this caller", twice.Message);
        await ch.CallAsync(AgentProtocol.Hello, new AgentHelloRequest { Token = claim.Token });
        var credentialed = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(AgentProtocol.Credential));
        Assert.Equal("refused: this connection already has an agent credential", credentialed.Message);
    }

    [Fact]
    public async Task The_pipe_refuses_a_lapsed_window_without_issuing()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        store.Adopt(Session, Environment.ProcessId, Started, AgentProtocol.ScopeRead);
        clock.Advance(TimeSpan.FromMinutes(AgentProtocol.AdoptionMinutes));
        await using var ch = await LocalClient.ConnectAsync(LocalEndpoint.For(root));
        var error = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(AgentProtocol.Credential));
        Assert.Equal("refused: no adopted session covers this caller", error.Message);
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData(CallerKind.App)]
    [InlineData(CallerKind.HostCli)]
    public async Task A_person_can_adopt_a_discovered_session_and_revoke_the_pending_window(CallerKind kind)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        callers.Kind = kind;
        await using var ch = await LocalClient.ConnectAsync(LocalEndpoint.For(root));
        var adopted = await ch.CallAsync<AgentAdoptResult>(AgentProtocol.Adopt, new AgentAdoptRequest { Session = Session });
        Assert.Equal(AgentProtocol.ScopeRead, adopted.Scope);
        Assert.True(callers.Fresh);
        Assert.True((await ch.CallAsync<AgentRevokeResult>(AgentProtocol.Revoke, new AgentRevokeRequest { Session = Session })).Revoked);
        Assert.Null(store.Claim(new ProcessAncestry([new PeerProcess(Environment.ProcessId, Started, "fixture")], true)).Token);
    }

    [Theory]
    [InlineData(CallerKind.Agent, AgentProtocol.Adopt)]
    [InlineData(CallerKind.Other, AgentProtocol.Adopt)]
    [InlineData(CallerKind.Agent, AgentProtocol.Revoke)]
    [InlineData(CallerKind.Other, AgentProtocol.Revoke)]
    public async Task Adoption_mutations_refuse_agents_and_unattributed_callers(CallerKind kind, string op)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        callers.Kind = kind;
        await using var ch = await LocalClient.ConnectAsync(LocalEndpoint.For(root));
        var error = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(op, new { session = Session }));
        Assert.Contains("for the person at this machine", error.Message);
        Assert.True(callers.Fresh);
    }

    [Theory]
    [InlineData(AgentProtocol.Adopt)]
    [InlineData(AgentProtocol.Revoke)]
    [InlineData(AgentProtocol.Credential)]
    public async Task The_relay_session_handler_never_serves_adoption(string op)
    {
        Assert.Null(await sessions.HandleAsync(null!, op, JsonSerializer.SerializeToElement(new { session = Session })));
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData("adopt")]
    [InlineData("revoke")]
    public async Task The_cli_refuses_person_commands_before_connecting_inside_an_agent(string verb)
    {
        var error = new StringWriter();
        Assert.Equal(4, await AgentCommands.RunAsync(["agents", verb, Session], root, () => true, TextWriter.Null, error));
        Assert.False(File.Exists(Path.Combine(root, AgentCredentials.FileName)));
    }

    [Theory]
    [InlineData("adopt")]
    [InlineData("revoke")]
    public async Task A_trailing_profile_is_rejected_before_the_person_guard_or_connection(string verb)
    {
        var guarded = false;
        var error = new StringWriter();
        var result = await AgentCommands.RunWithProfileAsync(["agents", verb, Session, "--profile"], root,
            () => { guarded = true; return true; }, TextWriter.Null, error);
        Assert.Equal(2, result);
        Assert.False(guarded);
        Assert.Contains("--profile requires one valid profile name", error.ToString());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task An_invalid_profile_is_rejected_before_the_person_guard_or_connection()
    {
        var error = new StringWriter();
        Assert.Equal(2, await AgentCommands.RunWithProfileAsync(["agents", "adopt", Session, "--profile", "..\\outside"], root,
            () => throw new InvalidOperationException("must not check"), TextWriter.Null, error));
        Assert.Contains("--profile requires one valid profile name", error.ToString());
    }

    [Fact]
    public void Adoption_never_returns_a_new_process_that_appeared_after_the_guard_ran()
    {
        var original = source.ProcessOf(Session)!;
        launcher.Checking = () =>
        {
            fixture.Processes.Run(AgentKind.Copilot, original.Id, startedAt: original.StartedAt!.Value.AddHours(1));
            Task.Run(() => source.RefreshAsync()).GetAwaiter().GetResult();
        };
        var checkedProcess = sessions.ProcessForAdoption(Session);
        Assert.NotEqual(original, source.ProcessOf(Session));
        Assert.Equal(original, checkedProcess);
    }

    [Fact]
    public async Task The_cli_prints_only_the_claimed_token_and_exits_nonzero_on_refusal()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        store.Adopt(Session, Environment.ProcessId, Started, AgentProtocol.ScopeRead);
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await AgentCommands.RunAsync(["agents", "credential"], root, () => throw new InvalidOperationException(), output, error));
        var token = output.ToString().Trim();
        Assert.Equal(AgentProtocol.TokenLength, token.Length);
        Assert.DoesNotContain(token, error.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(1, await AgentCommands.RunAsync(["agents", "credential"], root, () => false, output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("no adopted session", error.ToString());
    }

    [Fact]
    public async Task The_cli_routes_a_persons_adopt_and_revoke_with_the_selected_scope()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        callers.Kind = CallerKind.HostCli;
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await AgentCommands.RunWithProfileAsync(["agents", "adopt", Session, "--scope", "read+reply", "--profile", Path.GetFileName(root)], Path.GetDirectoryName(root)!, () => false, output, error));
        Assert.Contains("as read+reply", output.ToString());
        Assert.Contains("--profile " + Path.GetFileName(root), output.ToString());
        Assert.Equal(0, await AgentCommands.RunWithProfileAsync(["agents", "revoke", Session, "--profile", Path.GetFileName(root)], Path.GetDirectoryName(root)!, () => false, output, error));
        Assert.Contains("no longer acts", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task The_cli_treats_an_absent_revocation_as_a_successful_no_op()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        callers.Kind = CallerKind.HostCli;
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await AgentCommands.RunAsync(["agents", "revoke", Session], root, () => false, output, error));
        Assert.Contains("had no adoption or credential", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.True(callers.Fresh);
    }

    [Theory]
    [InlineData("""{"session":"missing"}""")]
    [InlineData("""{"session":null}""")]
    [InlineData("""{"session":123}""")]
    [InlineData("""{"session":"88888888-8888-4888-8888-888888888888","scope":"read+reply+launch"}""")]
    public async Task Invalid_adoption_requests_fail_without_issuing(string body)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        callers.Kind = CallerKind.App;
        await using var ch = await LocalClient.ConnectAsync(LocalEndpoint.For(root));
        await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(AgentProtocol.Adopt, JsonSerializer.Deserialize<JsonElement>(body)));
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_host_launched_process_is_not_adoptable_even_without_a_current_credential(bool revoked, bool child)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var process = source.ProcessOf(Session)!;
        launcher.Processes = [new Started(child ? process.Id + 1 : process.Id, process.StartedAt!.Value)];
        if (revoked) { store.Mint(Session, "launch1", process.Id, process.StartedAt.Value, AgentProtocol.ScopeRead); store.Revoke(Session, "fixture"); }
        callers.Kind = CallerKind.HostCli;
        await using var ch = await LocalClient.ConnectAsync(LocalEndpoint.For(root));
        var error = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(AgentProtocol.Adopt, new AgentAdoptRequest { Session = Session }));
        Assert.Contains("host launched", error.Message);
        Assert.Equal(0, store.Count);
    }

    sealed class Launcher : ISessionLauncher
    {
        public IReadOnlyList<Started> Processes { get; set; } = [];
        public Action? Checking { get; set; }
        public bool Available => false;
        public IReadOnlyList<Started> LaunchedProcesses() => Processes;
        public bool LaunchedSession(string session, SessionProcess? process)
        {
            Checking?.Invoke();
            return session == Session && Processes.Count > 0;
        }
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    sealed class Callers : ICallerGuard
    {
        public CallerKind Kind { get; set; } = CallerKind.Other;
        public bool Fresh { get; private set; }
        public Caller Classify(AgentBridge.Transport.IProtocolChannel channel, bool fresh = false) { Fresh = fresh; return new(Kind, "fixture"); }
    }
}
