using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

// #1003 PR B (docs/design/claude-approval-channel.md): the pipe op approval.wait. A Claude session's hook, a connection that passed agent.hello, hands the host the raw
// PermissionRequest event and waits for the person's verdict, which only the in-memory broker can give. The host reads the event and builds the shown text itself.
// Off unless the host is given the op; real pipe, fake clock.
[Trait("Category", "Network")]
public sealed class ClaudeApprovalWaitTests : IAsyncLifetime
{
    const string Session = "88888888-8888-4888-8888-888888888888";
    const string Person = "person:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Op = "approval.wait";
    readonly string root = Path.Combine(Path.GetTempPath(), "abaw-" + Guid.NewGuid().ToString("N")[..8]);
    readonly FakeTimeProvider clock = new();
    readonly List<string> logs = [];
    readonly Fixture fixture = new();
    readonly Callers callers = new() { Kind = CallerKind.Agent };
    readonly ClaudeApprovalBroker broker;
    readonly List<LocalServer> servers = [];
    AgentCredentials store = null!;
    SessionSource source = null!;
    SessionService sessions = null!;
    static DateTimeOffset Started { get { using var p = Process.GetCurrentProcess(); return p.StartTime.ToUniversalTime(); } }

    public ClaudeApprovalWaitTests() => broker = new ClaudeApprovalBroker(clock, TimeSpan.FromSeconds(100));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        fixture.CopilotSession(Session, Environment.ProcessId);
        fixture.Processes.Run(AgentKind.Copilot, Environment.ProcessId);
        source = new SessionSource(fixture.Options(), fixture.Processes);
        await source.RefreshAsync();
        sessions = new SessionService(source, new MachineInfo("machine1", "Fixture", "windows", "0.1.0"));
        store = new AgentCredentials(root, "machine1", clock, logs.Add, (_, _) => true);
    }

    public async Task DisposeAsync()
    {
        foreach (var s in servers) { await s.DisposeAsync(); }
        await source.DisposeAsync();
        fixture.Dispose();
        Directory.Delete(root, true);
    }

    // The host's pipe, with or without the op (without is how the host runs today).
    string Serve(bool on)
    {
        var folder = Path.Combine(root, on ? "on" : "off");
        Directory.CreateDirectory(folder);
        servers.Add(LocalServer.Start(LocalEndpoint.For(folder), LocalApi.Handler(sessions, null!, callers, logs.Add, credentials: store,
            approvalWait: on ? new ClaudeApprovalWait(broker) : null)));
        return folder;
    }

    // A connection that has passed agent.hello as the adopted Claude session, or one that has not.
    async Task<LocalChannel> ConnectAsync(string folder, bool hello = true)
    {
        store.Adopt(Session, Environment.ProcessId, Started, AgentProtocol.ScopeRead);
        var ch = await LocalClient.ConnectAsync(LocalEndpoint.For(folder));
        if (!hello) { return ch; }
        var claim = await ch.CallAsync<AgentCredentialResult>(AgentProtocol.Credential);
        await ch.CallAsync(AgentProtocol.Hello, new AgentHelloRequest { Token = claim.Token });
        return ch;
    }

    // What the hook sends: Claude's PermissionRequest event as text. The tool input is written out first so its text, which the request id is made of, is known.
    static string Input(string command) => JsonSerializer.Serialize(new { command });

    static string Event(string command, string tool = "Bash", string session = Session, string hook = "PermissionRequest", string? input = null) =>
        $$"""{"hook_event_name":{{JsonSerializer.Serialize(hook)}},"session_id":{{JsonSerializer.Serialize(session)}},"tool_name":{{JsonSerializer.Serialize(tool)}},"tool_input":{{input ?? Input(command)}}}""";

    // The id the hook's own formula (ClaudeApproval.RequestId) gives the same prompt, written out again here so the two cannot drift apart.
    static string Id(string command, string tool = "Bash", string session = Session, string? input = null) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{session}\n{tool}\n{input ?? Input(command)}")))[..16].ToLowerInvariant();

    // The digest of the text shown for a command, as the host builds it: the tool, then the command after escaping.
    static string Shown(string command, string tool = "Bash") => ClaudeApprovalBroker.Digest(ShownText.Reveal($"{tool}: {command}".Replace("\r\n", "\n"), rightToLeft: true));

    static string? Verdict(JsonElement reply) => reply.GetProperty("verdict").GetString();
    static string? Reason(JsonElement reply) => reply.TryGetProperty("reason", out var r) ? r.GetString() : null;

    // The prompt is waiting once the broker asks for another text's digest and says "shown", not "unknown".
    async Task Waiting(string request)
    {
        for (var i = 0; i < 400; i++)
        {
            if (broker.Resolve(Person, Session, request, new string('0', 64), "allow") == ClaudeApprovalBroker.Result.Shown) { return; }
            await Task.Delay(25);
        }
        Assert.Fail("the prompt was never registered");
    }

    [Fact]
    public async Task Off_the_op_is_not_available_even_to_an_agent_that_passed_hello()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: false));
        var refused = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(Op, new { @event = Event("echo hi") }));
        Assert.Contains("isn't available on this host", refused.Message);
    }

    [Theory]
    [InlineData(false, CallerKind.Agent)]
    [InlineData(true, CallerKind.App)]
    [InlineData(true, CallerKind.HostCli)]
    [InlineData(true, CallerKind.Other)]
    public async Task Only_an_agent_connection_that_passed_hello_is_heard_and_nothing_is_registered_otherwise(bool hello, CallerKind kind)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true), hello);
        callers.Kind = kind;
        await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync(Op, new { @event = Event("echo hi") }));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, Session, Id("echo hi"), Shown("echo hi"), "allow"));
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    public async Task A_persons_answer_to_the_shown_text_is_the_reply(string verdict)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        var asking = ch.CallAsync(Op, new { @event = Event("echo hi") });
        await Waiting(Id("echo hi"));

        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, Session, Id("echo hi"), Shown("echo hi"), verdict));

        var reply = await asking;
        Assert.Equal(verdict, Verdict(reply));
        Assert.Null(Reason(reply));
    }

    [Fact]
    public async Task The_host_derives_the_request_id_and_builds_the_shown_text_so_only_an_answer_to_what_it_showed_resolves_it()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        const string command = "echo ok‮; rm -rf /";
        var asking = ch.CallAsync(Op, new { @event = Event(command), request = "forged", session = "forged", principal = Person });
        await Waiting(Id(command));

        // The raw command's digest, and the digest of the escaped command without its tool, are not what the person was shown.
        Assert.Equal(ClaudeApprovalBroker.Result.Shown, broker.Resolve(Person, Session, Id(command), ClaudeApprovalBroker.Digest(command), "allow"));
        Assert.Equal(ClaudeApprovalBroker.Result.Shown, broker.Resolve(Person, Session, Id(command), ClaudeApprovalBroker.Digest(ShownText.Reveal(command, rightToLeft: true)), "allow"));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, Session, "forged", Shown(command), "allow"));
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, Session, Id(command), Shown(command), "deny"));
        Assert.Equal("deny", Verdict(await asking));
    }

    [Fact]
    public async Task A_tool_with_no_command_shows_its_input_and_the_tool_name_is_part_of_what_is_shown()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        const string input = """{"file_path":"/tmp/x"}""";
        var asking = ch.CallAsync(Op, new { @event = Event("", tool: "Write", input: input) });
        await Waiting(Id("", "Write", input: input));

        Assert.Equal(ClaudeApprovalBroker.Result.Shown, broker.Resolve(Person, Session, Id("", "Write", input: input), Shown(input, "Bash"), "allow"));
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, Session, Id("", "Write", input: input), Shown(input, "Write"), "allow"));
        Assert.Equal("allow", Verdict(await asking));
    }

    [Fact]
    public async Task An_event_for_another_session_than_the_connections_is_none_caller_and_nothing_is_registered()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        var reply = await ch.CallAsync(Op, new { @event = Event("echo hi", session: "99999999-9999-4999-9999-999999999999") });

        Assert.Equal(("none", "caller"), (Verdict(reply), Reason(reply)));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, Session, Id("echo hi"), Shown("echo hi"), "allow"));
    }

    [Fact]
    public async Task A_prompt_already_waiting_is_never_replaced_by_a_second_ask_which_is_none_busy()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        var first = ch.CallAsync(Op, new { @event = Event("echo hi") });
        await Waiting(Id("echo hi"));

        var second = await ch.CallAsync(Op, new { @event = Event("echo hi") });

        Assert.Equal(("none", "busy"), (Verdict(second), Reason(second)));
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, Session, Id("echo hi"), Shown("echo hi"), "allow"));
        Assert.Equal("allow", Verdict(await first));
    }

    [Fact]
    public async Task A_different_input_is_a_different_prompt_with_its_own_wait()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        var one = ch.CallAsync(Op, new { @event = Event("echo one") });
        await Waiting(Id("echo one"));
        var two = ch.CallAsync(Op, new { @event = Event("echo two") });
        await Waiting(Id("echo two"));

        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, Session, Id("echo two"), Shown("echo two"), "deny"));
        Assert.Equal("deny", Verdict(await two));
        Assert.False(one.IsCompleted);
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, Session, Id("echo one"), Shown("echo one"), "allow"));
        Assert.Equal("allow", Verdict(await one));
    }

    [Fact]
    public async Task No_answer_by_the_deadline_is_none_timeout_and_a_late_answer_is_unknown()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        var asking = ch.CallAsync(Op, new { @event = Event("echo hi") });
        await Waiting(Id("echo hi"));

        clock.Advance(TimeSpan.FromSeconds(100));

        var reply = await asking;
        Assert.Equal(("none", "timeout"), (Verdict(reply), Reason(reply)));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, Session, Id("echo hi"), Shown("echo hi"), "allow"));
    }

    [Fact]
    public async Task A_prompt_that_would_be_shown_cut_is_none_truncated_at_once_and_never_registered()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        var command = new string('x', ClaudeApprovalBroker.PreviewMax + 1);

        var reply = await ch.CallAsync(Op, new { @event = Event(command) });

        Assert.Equal(("none", "truncated"), (Verdict(reply), Reason(reply)));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, Session, Id(command), Shown(command), "allow"));
    }

    [Fact]
    public async Task The_hook_going_away_withdraws_its_prompt_so_nobody_answers_one_nobody_waits_for()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var ch = await ConnectAsync(Serve(on: true));
        _ = ch.CallAsync(Op, new { @event = Event("echo hi") }).ContinueWith(_ => { }, TaskScheduler.Default);
        await Waiting(Id("echo hi"));

        await ch.DisposeAsync();

        for (var i = 0; i < 400 && broker.Resolve(Person, Session, Id("echo hi"), Shown("echo hi"), "allow") != ClaudeApprovalBroker.Result.Unknown; i++) { await Task.Delay(25); }
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, Session, Id("echo hi"), Shown("echo hi"), "allow"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]
    [InlineData("""{"hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"x"}}""")]
    [InlineData("""{"hook_event_name":"PermissionRequest","session_id":"88888888-8888-4888-8888-888888888888","tool_input":{"command":"x"}}""")]
    [InlineData("""{"hook_event_name":"PermissionRequest","session_id":"88888888-8888-4888-8888-888888888888","tool_name":7,"tool_input":{"command":"x"}}""")]
    [InlineData("""{"hook_event_name":"PermissionRequest","session_id":"","tool_name":"Bash"}""")]
    [InlineData("""{"hook_event_name":"PreToolUse","session_id":"88888888-8888-4888-8888-888888888888","tool_name":"Bash","tool_input":{"command":"x"}}""")]
    [InlineData("""{"session_id":"88888888-8888-4888-8888-888888888888","tool_name":"Bash","tool_input":{"command":"x"}}""")]
    public async Task A_malformed_or_unknown_event_is_none_bad_event_and_nothing_is_registered(string text)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));

        var reply = await ch.CallAsync(Op, new { @event = text });

        Assert.Equal(("none", "bad-event"), (Verdict(reply), Reason(reply)));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, Session, Id("x"), Shown("x"), "allow"));
    }

    [Fact]
    public async Task A_request_with_no_event_text_or_one_over_sixteen_kibibytes_is_none_bad_event()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var ch = await ConnectAsync(Serve(on: true));
        var tooBig = Event(new string('x', ClaudeApprovalWait.MaxEvent));

        foreach (var request in new object[] { new { }, new { @event = 7 }, new { @event = tooBig }, new { command = "echo hi" } })
        {
            var reply = await ch.CallAsync(Op, request);
            Assert.Equal(("none", "bad-event"), (Verdict(reply), Reason(reply)));
        }
    }

    [Fact]
    public void The_host_does_not_construct_the_op_so_the_feature_stays_off()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "AgentBridge.Host"))) { dir = Path.GetDirectoryName(dir); }
        Assert.NotNull(dir);
        var users = Directory.EnumerateFiles(Path.Combine(dir!, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("ClaudeApprovalWait", StringComparison.Ordinal))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["ClaudeApprovalWait.cs", "LocalApi.cs"], users);
        Assert.DoesNotContain("new ClaudeApprovalWait", File.ReadAllText(Path.Combine(dir!, "src", "AgentBridge.Host", "LocalApi.cs")), StringComparison.Ordinal);
    }

    sealed class Callers : ICallerGuard
    {
        public CallerKind Kind { get; set; } = CallerKind.Other;
        public Caller Classify(AgentBridge.Transport.IProtocolChannel channel, bool fresh = false) => new(Kind, "fixture");
    }
}
