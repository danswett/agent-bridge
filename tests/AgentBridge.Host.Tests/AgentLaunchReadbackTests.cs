using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

[Trait("Category", "Network")]
public sealed class AgentLaunchReadbackTests : LaunchFlowFixture
{
    const string Principal = "agent:machine/parent";

    (Func<IProtocolChannel, string, JsonElement, Task<object?>> Handle, AgentCredentials Credentials, Chan Channel) Pipe(LaunchService launcher)
    {
        var credentials = new AgentCredentials(f.Root, "machine");
        var channel = new Chan();
        var token = credentials.Mint("parent", "launch", 100, DateTimeOffset.UtcNow, AgentProtocol.ScopeRead);
        credentials.Accept(channel, credentials.Check(token,
            new AgentBridge.Local.ProcessAncestry([new AgentBridge.Local.PeerProcess(100, DateTimeOffset.UtcNow, "agent.exe")], true)).Accepted!);
        var service = new SessionService(source, new MachineInfo("machine", "Test", "windows", "0.1.0"), launcher: launcher);
        return (LocalApi.Handler(service, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")),
            credentials: credentials, audit: new AgentAuditLog(f.Root)), credentials, channel);
    }

    static JsonElement Body(LaunchRequest request) => JsonSerializer.SerializeToElement(request, ProtocolJson.Options);

    static async Task<LaunchResult> Replay(Func<IProtocolChannel, string, JsonElement, Task<object?>> handle,
        IProtocolChannel channel, LaunchRequest request)
    {
        object? result = null;
        var error = await Record.ExceptionAsync(async () => { result = await handle(channel, LaunchProtocol.Launch, Body(request)); });
        Assert.Null(error);
        return Assert.IsType<LaunchResult>(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_agent_reads_only_its_own_result_by_repeating_the_same_request(bool started)
    {
        var launcher = Launcher(Config());
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
            Workspace = "App", Permissions = LaunchProtocol.Ask, Prompt = "private prompt" };
        // A missing launcher produces a real remembered refusal without starting a process.
        if (started)
        {
            locator.Installed["claude"] = @"C:\fake\claude.exe";
            Git(f.Root, "init", "-q", "-b", "main", app);
            File.WriteAllText(Path.Combine(app, "README.md"), "fixture");
            Git(app, "add", "README.md");
            Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
            launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }),
                agentWorktreeCheck: AgentLaunchWorktreeCheck.Check);
        }
        var first = await launcher.LaunchAsync(request, Principal);
        Assert.Equal(started ? LaunchOutcome.Started : LaunchOutcome.Refused, first.Outcome);
        var (handle, credentials, channel) = Pipe(launcher);

        var repeat = await Replay(handle, channel, request);

        Assert.Equal(first.Outcome, repeat.Outcome);
        Assert.Equal(first.Session, repeat.Session);
        Assert.Equal(first.Reason, repeat.Reason);
        Assert.Null(repeat.Launched?.Cwd);
        Assert.Null(repeat.Trust);
        Assert.Equal(started ? 1 : 0, starter.Specs.Count);
        Assert.Equal(LaunchOutcome.Refused, (await launcher.LaunchAsync(request, "agent:machine/other")).Outcome);
        Assert.Equal(LaunchOutcome.Refused, (await launcher.LaunchAsync(request, "person-device")).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, LaunchProtocol.Launch,
            Body(request with { Prompt = "changed" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, LaunchProtocol.Launch,
            Body(request with { Request = Guid.NewGuid().ToString("D") })));
        credentials.RevokeSession("parent", "fixture");
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, LaunchProtocol.Launch, Body(request)));
        Assert.Equal(started ? 1 : 0, starter.Specs.Count);
    }

    [Fact]
    public async Task The_options_readback_lists_only_offered_agents_models_and_workspace_labels_without_paths()
    {
        File.WriteAllText(Path.Combine(f.Root, HostSettings.FileName), """
            {"agents":{"enabled":true,"inbound":{"local":{"launch":{
              "workspaces":["App"],"permissions":"ask","agents":["claude"]}}}}}
            """);
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true), new("API", api, true)] }));
        var (handle, _, channel) = Pipe(launcher);

        var options = JsonSerializer.SerializeToElement(await handle(channel, LaunchProtocol.Options, Body(new LaunchRequest { Request = "unused" })), ProtocolJson.Options);

        var agent = Assert.Single(options.GetProperty("agents").EnumerateArray());
        Assert.Equal("claude", agent.GetProperty("id").GetString());
        Assert.NotEmpty(agent.GetProperty("models").EnumerateArray());
        Assert.Equal("App", Assert.Single(options.GetProperty("workspaces").EnumerateArray()).GetProperty("label").GetString());
        Assert.DoesNotContain("path", options.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.Root, options.GetRawText());
        Assert.False(options.TryGetProperty("resumable", out _));
        Assert.False(options.TryGetProperty("trust", out _));
        Assert.DoesNotContain("allowAll", options.GetRawText());
    }

    [Fact]
    public async Task A_replayed_busy_launch_hides_the_startup_question_and_never_answers_it()
    {
        screens.Rows = ["Confirm folder trust", app, "Do you trust the files in this folder?",
            "\u276f 1. Yes", "  2. Yes, and remember this folder for future sessions", "  3. No (Esc)"];
        var launcher = Launcher(Config(), wait: TimeSpan.FromMilliseconds(200));
        await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Workspace = "App" }, "person-device");
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Workspace = "App" };
        Assert.Equal(LaunchOutcome.Busy, (await launcher.LaunchAsync(request, Principal)).Outcome);
        var (handle, _, channel) = Pipe(launcher);

        var repeat = await Replay(handle, channel, request);

        Assert.Equal(LaunchOutcome.Busy, repeat.Outcome);
        Assert.Null(repeat.Trust);
        Assert.DoesNotContain(app, repeat.Reason);
        Assert.DoesNotContain("Copilot", repeat.Reason);
        Assert.Empty(screens.Typed);
        Assert.Single(starter.Specs);
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
