using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

// session.reply end to end: a fixture Copilot session read by the real SessionSource, a
// reply sent by a client through the in-process relay, delivered by the real ReplyService
// (sharing its gate with a real AnswerService) into a fake injector that plays the CLI: it
// writes the prompt the session would record. Real keys into a real console are in
// InjectorIntegrationTests.
public sealed class ReplyFlowTests : IAsyncLifetime
{
    const string Id = "99999999-9999-4999-8999-999999999999";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    readonly FakeProbe probe = new();
    readonly FakeInjector injector = new();
    readonly DeliveryGate gate = new();
    TestRelay relay = null!;
    SessionSource source = null!;
    Device host = null!;
    Device client = null!;
    string events = "";
    int second = 10;

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, 100, name: "Replying session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        f.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        client = await relay.ConnectAsync(token, "Phone", "pwa");
        Serve(Answerer(), Replier());
        // By default the session takes whatever is typed as a prompt of the person's.
        injector.OnInject = plan => Fixture.Append(events, Prompt(plan.Sends[0].Text));
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    ReplyService Replier(bool platform = true) => new(source, f.Processes, probe, new NoGuard(), injector, new ReplyOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = platform,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
    }, gate: gate);

    AnswerService Answerer() => new(source, f.Processes, probe, new NoGuard(), injector, new AnswerOptions
    {
        CopilotHome = f.CopilotHome,
        // Set, not left to the default (Windows only), so these run the same on Linux.
        PlatformSupported = true,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
    }, gate: gate);

    void Serve(ISessionAnswerer? answers, ISessionReplier? replies) =>
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), answers: answers, replies: replies).HandleAsync;

    string Prompt(string text, string delivery = "idle") => Copilot.Event("user.message", new { content = text, delivery }, second++);

    static string Ask(string id, int s) => Copilot.ToolStart(id, "ask_user", new
    {
        message = "Which one?",
        requestedSchema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b" } } }, required = new[] { "pick" } },
    }, s);

    async Task<SecureChannel> OpenAsync()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return await client.Hub.OpenAsync(host.Id);
    }

    static async Task<(string Outcome, string? Reason)> ReplyAsync(SecureChannel ch, string text, string? request = null, string id = Id, object? attachments = null)
    {
        var response = await ch.RequestAsync(Ops.SessionReply, attachments is null
            ? new { id, request = request ?? Guid.NewGuid().ToString(), text }
            : (object)new { id, request = request ?? Guid.NewGuid().ToString(), text, attachments });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        var result = response.GetProperty("result");
        return (result.GetProperty("outcome").GetString()!, result.TryGetProperty("reason", out var r) ? r.GetString() : null);
    }

    static async Task<string[]> CapabilitiesAsync(SecureChannel ch)
    {
        var hello = await ch.RequestAsync(Ops.Hello, new { });
        return [.. hello.GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];
    }

    [Fact]
    public async Task Hello_offers_replying_only_while_agent_ha_bridge_is_not_running_and_only_where_a_replier_can_deliver()
    {
        var ch = await OpenAsync();
        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsReply], await CapabilitiesAsync(ch));

        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.Equal([Capabilities.SessionsRead], await CapabilitiesAsync(ch));
        probe.Reason = null;

        Serve(null, new UnsupportedPlatformReplier());
        Assert.Equal([Capabilities.SessionsRead], await CapabilitiesAsync(ch));
        Assert.Equal(("refused", "replying is not supported on this platform yet; reply at the machine"), await ReplyAsync(ch, "hello"));
        Serve(null, Replier(platform: false));
        Assert.Equal([Capabilities.SessionsRead], await CapabilitiesAsync(ch));
        Assert.Equal("refused", (await ReplyAsync(ch, "hello")).Outcome);
        Serve(null, null);
        Assert.Equal(("refused", "this host does not take replies; reply at the machine"), await ReplyAsync(ch, "hello"));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_reply_the_transcript_then_records_as_the_persons_prompt_is_delivered_typed_as_one_line_then_Enter()
    {
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "Looks good.\nNow open the PR – please 🚀");

        Assert.Equal(("delivered", (string?)null), (outcome, reason));
        var (pid, plan) = Assert.Single(injector.Plans);
        Assert.Equal(100, pid);
        Assert.Equal([("Looks good. Now open the PR – please 🚀", true)], plan.Sends.Select(s => (s.Text, s.Enter)));
    }

    [Fact]
    public async Task A_reply_to_a_working_session_is_delivered_once_copilot_takes_it_at_its_next_step()
    {
        Fixture.Append(events, Prompt("next task"), Copilot.TurnStart(second++), Copilot.ToolStart("t1", "powershell", new { command = "dotnet test" }, second++));
        await source.RefreshAsync();
        Assert.Equal(SessionStatus.Working, source.Get(Id)!.Status);
        // Copilot writes a steering prompt when its next round starts, after the running tool.
        injector.OnInject = plan => _ = Task.Delay(700).ContinueWith(_ => Fixture.Append(events,
            Copilot.ToolDone("t1", second++), Copilot.TurnStart(second++), Prompt(plan.Sends[0].Text, "steering")), TaskScheduler.Default);
        var ch = await OpenAsync();

        Assert.Equal(("delivered", (string?)null), await ReplyAsync(ch, "also run the e2e tests"));
    }

    [Fact]
    public async Task A_prompt_that_is_not_the_reply_or_none_at_all_is_unconfirmed_never_delivered()
    {
        var ch = await OpenAsync();

        injector.OnInject = _ => Fixture.Append(events, Prompt("something typed at the machine"));
        var (outcome, reason) = await ReplyAsync(ch, "carry on");
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("a different message", reason);

        injector.OnInject = null;
        (outcome, reason) = await ReplyAsync(ch, "carry on");
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("did not record the reply", reason);

        // A scheduled prompt or a subagent's of the same words was not typed by anybody.
        injector.OnInject = plan => Fixture.Append(events,
            Copilot.Event("user.message", new { content = plan.Sends[0].Text, source = "schedule-1" }, second++),
            Copilot.User(plan.Sends[0].Text, second++, agentId: "helper"));
        Assert.Equal("unconfirmed", (await ReplyAsync(ch, "carry on")).Outcome);
        Assert.Equal(3, injector.Plans.Count);
    }

    [Fact]
    public async Task Two_replies_of_the_same_words_need_two_prompts_one_each()
    {
        // Both are typed into a working session before it takes either, and it records one.
        injector.OnInject = null;
        var ch = await OpenAsync();
        var first = ReplyAsync(ch, "yes");
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the first reply to be typed", Patience);
        await Task.Delay(300);
        var second = ReplyAsync(ch, "yes");
        await Wait.UntilAsync(() => injector.Plans.Count == 2, "the second reply to be typed", Patience);
        await Task.Delay(300);
        Fixture.Append(events, Prompt("yes", "steering"));

        // That prompt is the record of one of them, never of both.
        Assert.Equal(["delivered", "unconfirmed"], new[] { (await first).Outcome, (await second).Outcome }.Order());
    }

    [Fact]
    public async Task A_reply_to_a_session_that_has_gone_is_stale_and_types_nothing()
    {
        var ch = await OpenAsync();

        Assert.Equal(("stale", "the session has ended"), await ReplyAsync(ch, "hello", id: "00000000-0000-4000-8000-000000000000"));
        f.Processes.Exit(AgentKind.Copilot, 100);
        Assert.Equal(("stale", "the session's process has ended"), await ReplyAsync(ch, "hello"));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task While_the_session_asks_a_question_a_reply_is_refused_untyped_even_before_the_host_has_read_it()
    {
        var ch = await OpenAsync();

        // Asked a moment ago: in the transcript, not yet in the session source.
        Fixture.Append(events, Ask("q1", second++));
        Assert.Null(source.Get(Id)!.Question);
        Assert.Equal(("refused", "the session is waiting for an answer to its question; answer that first"), await ReplyAsync(ch, "hello"));

        await source.RefreshAsync();
        Assert.NotNull(source.Get(Id)!.Question);
        Assert.Equal("refused", (await ReplyAsync(ch, "hello")).Outcome);

        // A permission prompt holds the input the same way, and Enter would accept it.
        Fixture.Append(events, Copilot.Answered("q1", second++), Copilot.ToolDone("q1", second++),
            Copilot.Event("permission.requested", new { requestId = "r1", permissionRequest = new { kind = "shell", toolCallId = "c9" } }, second++));
        Assert.Equal("refused", (await ReplyAsync(ch, "hello")).Outcome);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_transcript_that_cannot_be_read_cannot_say_nothing_is_asking_so_nothing_is_typed()
    {
        var ch = await OpenAsync();
        File.Move(events, events + ".away");

        Assert.Equal(("refused", "the session's transcript could not be read, so nothing is typed; reply at the machine"), await ReplyAsync(ch, "hello"));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_reply_while_an_answer_to_that_session_is_being_typed_is_busy_as_is_one_behind_another_reply()
    {
        Fixture.Append(events, Ask("q1", second++));
        await source.RefreshAsync();
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        injector.OnInject = _ => Fixture.Append(events, Copilot.Event("human_response.recorded",
            new { toolCallId = "q1", requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content = new { pick = "b" } } }, second++));
        var ch = await OpenAsync();

        var answer = ch.RequestAsync(Ops.SessionAnswer, new { id = Id, question = "q1", request = Guid.NewGuid().ToString(), fields = new { pick = "b" } });
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the answer to start typing", Patience);
        Assert.Equal(("busy", "another answer or reply to this session is being delivered"), await ReplyAsync(ch, "hello"));
        release.SetResult();
        Assert.Equal("delivered", (await answer).GetProperty("result").GetProperty("outcome").GetString());

        // And a reply behind a reply.
        Fixture.Append(events, Copilot.ToolDone("q1", second++));
        await source.RefreshAsync();
        release = new TaskCompletionSource();
        injector.Gate = release.Task;
        injector.OnInject = plan => Fixture.Append(events, Prompt(plan.Sends[0].Text));
        var first = ReplyAsync(ch, "first");
        await Wait.UntilAsync(() => injector.Plans.Count == 2, "the first reply to start typing", Patience);
        Assert.Equal("busy", (await ReplyAsync(ch, "second")).Outcome);
        release.SetResult();
        Assert.Equal("delivered", (await first).Outcome);
        Assert.Equal(2, injector.Plans.Count);
    }

    [Fact]
    public async Task A_repeated_request_returns_the_first_outcome_and_never_types_again_even_on_a_new_channel()
    {
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var first = ReplyAsync(ch, "once only", request);
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the reply to start typing", Patience);
        var during = ReplyAsync(ch, "once only", request);
        release.SetResult();
        var again = await ReplyAsync(await OpenAsync(), "once only", request);

        Assert.Equal(["delivered", "delivered", "delivered"], new[] { (await first).Outcome, (await during).Outcome, again.Outcome });
        Assert.Single(injector.Plans);

        // A refusal is remembered too: the same request after the reason has gone is still it.
        probe.Reason = "agent-ha-bridge is running for this user";
        var refused = Guid.NewGuid().ToString();
        Assert.Equal("refused", (await ReplyAsync(ch, "later", refused)).Outcome);
        probe.Reason = null;
        Assert.Equal(("refused", "agent-ha-bridge is running for this user"), await ReplyAsync(ch, "later", refused));
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task While_agent_ha_bridge_runs_a_reply_is_refused_with_its_reason_even_if_it_starts_after_the_request_arrived()
    {
        var ch = await OpenAsync();

        probe.Reason = "agent-ha-bridge is running for this user (AgentBridgeDaemon_x)";
        Assert.Equal(("refused", probe.Reason), await ReplyAsync(ch, "hello"));
        probe.Reason = null;

        probe.Answers = new Queue<string?>([null, "agent-ha-bridge is running for this user"]);
        Assert.Equal(("refused", "agent-ha-bridge is running for this user"), await ReplyAsync(ch, "hello"));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Invalid_replies_attachments_and_other_agents_are_refused_untyped()
    {
        const string ClaudeId = "c1a0de00-0000-4000-8000-000000000001";
        var transcript = f.ClaudeTranscript(ClaudeId);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(ClaudeId, 300, transcript);
        f.Processes.Run(AgentKind.Claude, 300);
        await source.RefreshAsync();
        var ch = await OpenAsync();

        Assert.Equal(("refused", "the reply is empty"), await ReplyAsync(ch, " \n "));
        Assert.Equal(("refused", "the reply is longer than 16 KiB"), await ReplyAsync(ch, new string('x', Limits.ReplyText + 1)));
        Assert.Contains("cannot start with /", (await ReplyAsync(ch, "/clear")).Reason);
        Assert.Contains("attachments are not supported yet", (await ReplyAsync(ch, "see this", attachments: new[] { new { name = "a.png" } })).Reason);
        Assert.Equal(("refused", "request must be a UUID"), await ReplyAsync(ch, "hello", request: "7"));
        Assert.Equal(("refused", "replying is not supported for claude yet; reply at the machine"), await ReplyAsync(ch, "hello", id: ClaudeId));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Keys_that_may_have_landed_before_the_injector_failed_are_unconfirmed_and_nothing_typed_is_refused()
    {
        var ch = await OpenAsync();

        injector.Result = new InjectionResult(InjectionStatus.Interrupted, "the session's console closed while the reply was being typed");
        var (outcome, reason) = await ReplyAsync(ch, "hello");
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("console closed", reason);

        injector.Result = new InjectionResult(InjectionStatus.NothingTyped, "the session runs as administrator");
        Assert.Equal(("refused", "the session runs as administrator"), await ReplyAsync(ch, "hello again"));
    }

    sealed class FakeProbe : IAgentHaBridgeProbe
    {
        public string? Reason;
        public Queue<string?>? Answers;

        public string? Active() => Answers is { Count: > 0 } queue ? queue.Dequeue() : Reason;
    }

    sealed class NoGuard : ITargetGuard
    {
        public string? Refuse(int processId) => null;
    }

    sealed class FakeInjector : IConsoleInjector
    {
        public readonly List<(int Pid, KeyPlan Plan)> Plans = [];
        public Action<KeyPlan>? OnInject;
        public Task Gate = Task.CompletedTask;
        public InjectionResult Result = new(InjectionStatus.Typed, "ok");

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Plans) { Plans.Add((processId, plan)); }
            await Gate;
            if (Result.Status == InjectionStatus.Typed) { OnInject?.Invoke(plan); }
            return Result;
        }
    }
}
