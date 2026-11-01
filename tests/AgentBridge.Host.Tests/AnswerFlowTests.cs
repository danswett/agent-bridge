using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

// session.answer end to end: a fixture Copilot session asking a question, read by the real
// SessionSource, answered by a client through the in-process relay, delivered by the real
// AnswerService into a fake injector that plays the CLI: it writes what the session would
// record into the transcript. Real keys into a real console are InjectorIntegrationTests.
public sealed class AnswerFlowTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    readonly FakeProbe probe = new();
    readonly FakeInjector injector = new();
    TestRelay relay = null!;
    SessionSource source = null!;
    Device host = null!;
    Device client = null!;
    string events = "";
    int second = 10;

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, 100, name: "Asking session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Ask("q1", 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        client = await relay.ConnectAsync(token, "Phone", "pwa");
        Serve(Answerer());
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    AnswerService Answerer(bool platform = true, bool remoteApproval = true) => new(source, f.Processes, probe, new NoGuard(), injector, new AnswerOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = platform,
        CopilotRemoteApproval = remoteApproval,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
    });

    void Serve(ISessionAnswerer answers) =>
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), answers: answers) { ReadOnly = () => probe.Reason }.HandleAsync;

    static string Ask(string id, int s) => Copilot.ToolStart(id, "ask_user", new
    {
        message = "Which one?",
        requestedSchema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b", "c" } } }, required = new[] { "pick" } },
    }, s);

    string Recorded(string id, object content) =>
        Copilot.Event("human_response.recorded", new { toolCallId = id, requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content } }, second++);

    async Task<SecureChannel> OpenAsync()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return await client.Hub.OpenAsync(host.Id);
    }

    static async Task<(string Outcome, string? Reason)> AnswerAsync(SecureChannel ch, object fields, string? request = null, string question = "q1")
    {
        var response = await ch.RequestAsync(Ops.SessionAnswer, new { id = Id, question, request = request ?? Guid.NewGuid().ToString(), fields });
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
    public async Task Hello_offers_answering_only_while_agent_ha_bridge_is_not_running_asked_afresh_each_time()
    {
        var ch = await OpenAsync();
        var offered = await CapabilitiesAsync(ch);
        Assert.Contains(Capabilities.SessionsAnswer, offered);
        Assert.Contains(Capabilities.SessionsApprove, offered);

        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.Equal([Capabilities.SessionsRead], await CapabilitiesAsync(ch));

        probe.Reason = null;
        Assert.Contains(Capabilities.SessionsAnswer, await CapabilitiesAsync(ch));
    }

    // A phone shows the machine read-only and why, rather than only leaving the controls out.
    [Fact]
    public async Task While_agent_ha_bridge_runs_hello_says_why_the_machine_is_read_only_and_not_once_it_stops()
    {
        var ch = await OpenAsync();
        static JsonElement Machine(JsonElement hello) => hello.GetProperty("result").GetProperty("machine");
        Assert.False(Machine(await ch.RequestAsync(Ops.Hello, new { })).TryGetProperty("readOnly", out _));

        probe.Reason = "agent-ha-bridge is running for this user (AgentBridgeDaemon); answer at the machine or through it";
        Assert.Equal(probe.Reason, Machine(await ch.RequestAsync(Ops.Hello, new { })).GetProperty("readOnly").GetString());

        probe.Reason = null;
        Assert.False(Machine(await ch.RequestAsync(Ops.Hello, new { })).TryGetProperty("readOnly", out _));
    }

    [Fact]
    public async Task Hello_offers_approving_permission_prompts_only_on_a_host_with_remote_approval_on()
    {
        Serve(Answerer(remoteApproval: false));
        var ch = await OpenAsync();

        var offered = await CapabilitiesAsync(ch);
        Assert.Contains(Capabilities.SessionsAnswer, offered);
        Assert.DoesNotContain(Capabilities.SessionsApprove, offered);
    }

    [Fact]
    public async Task A_host_on_a_platform_without_delivery_never_offers_answering_and_refuses_an_answer_untyped()
    {
        Serve(new UnsupportedPlatformAnswerer());
        var ch = await OpenAsync();

        Assert.Equal([Capabilities.SessionsRead], await CapabilitiesAsync(ch));
        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });
        Assert.Equal(("refused", true), (outcome, reason!.Contains("platform")));

        Serve(Answerer(platform: false));
        Assert.Equal("refused", (await AnswerAsync(ch, new { pick = "b" })).Outcome);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task An_answer_the_transcript_then_records_is_delivered_after_typing_exactly_the_planned_keys()
    {
        injector.OnInject = () => Fixture.Append(events, Recorded("q1", new { pick = "b" }));
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });

        Assert.Equal(("delivered", (string?)null), (outcome, reason));
        var (pid, plan) = Assert.Single(injector.Plans);
        Assert.Equal(100, pid);
        Assert.Equal([(KeyPlan.Up + KeyPlan.Up + KeyPlan.Down, true)], plan.Sends.Select(s => (s.Text, s.Enter)));
    }

    [Fact]
    public async Task A_matching_answer_followed_at_once_by_the_call_failing_is_unconfirmed()
    {
        injector.OnInject = () =>
        {
            Fixture.Append(events, Recorded("q1", new { pick = "b" }));
            _ = Task.Delay(300).ContinueWith(_ => Fixture.Append(events, Copilot.ToolDone("q1", second++, success: false)), TaskScheduler.Default);
        };
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });

        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("error", reason);
    }

    [Fact]
    public async Task A_different_recorded_answer_or_none_at_all_is_unconfirmed_never_delivered()
    {
        var ch = await OpenAsync();

        injector.OnInject = () => Fixture.Append(events, Recorded("q1", new { pick = "c" }));
        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("different answer", reason);

        // A fresh question, which the session never answers.
        Fixture.Append(events, Ask("q2", second++));
        await source.RefreshAsync();
        injector.OnInject = null;
        (outcome, reason) = await AnswerAsync(ch, new { pick = "a" }, question: "q2");
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("did not record an answer", reason);
        Assert.Equal(2, injector.Plans.Count);
    }

    [Fact]
    public async Task An_answer_to_a_question_no_longer_asked_is_stale_and_types_nothing()
    {
        var ch = await OpenAsync();

        Assert.Equal("stale", (await AnswerAsync(ch, new { pick = "b" }, question: "q0")).Outcome);

        // Answered at the machine a moment ago: the transcript says so before the host's own
        // view has caught up, and the transcript is what is checked just before typing.
        Fixture.Append(events, Recorded("q1", new { pick = "a" }));
        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });
        Assert.Equal(("stale", "the question has already been answered"), (outcome, reason));
        Assert.Empty(injector.Plans);
    }

    // #173: the session's own question is the one listed while a delegate's permission prompt
    // also waits, and the CLI may be showing that dialog: keys typed for the form could approve
    // it. The transcript is read just before typing, as the host's view may be a pass behind.
    [Fact]
    public async Task An_answer_while_a_delegates_permission_prompt_also_waits_is_refused_and_types_nothing()
    {
        var ch = await OpenAsync();
        Fixture.Append(events, Copilot.ToolStart("c9", "powershell", new { command = "gh pr checks" }, second++, "helper"),
            Copilot.Permission("r1", "c9", Copilot.Prompts["commands"], second++));

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });

        Assert.Equal("refused", outcome);
        Assert.Contains("more than one prompt", reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_second_answer_to_a_session_while_one_is_being_typed_is_busy()
    {
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        injector.OnInject = () => Fixture.Append(events, Recorded("q1", new { pick = "b" }));
        var ch = await OpenAsync();

        var first = AnswerAsync(ch, new { pick = "b" });
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the first answer to start typing", Patience);
        var second = await AnswerAsync(ch, new { pick = "c" });
        release.SetResult();

        Assert.Equal("busy", second.Outcome);
        Assert.Equal("delivered", (await first).Outcome);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task An_answer_waiting_for_another_sessions_typing_checks_its_question_again_once_its_turn_comes()
    {
        const string Second = "77777777-7777-4777-8777-777777777777";
        var other = f.CopilotSession(Second, 200, name: "Second session");
        Fixture.Append(other, Copilot.User("go", 1), Ask("r1", 2));
        f.Processes.Run(AgentKind.Copilot, 200);
        await source.RefreshAsync();
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        injector.OnInject = () => Fixture.Append(events, Recorded("q1", new { pick = "b" }));
        var ch = await OpenAsync();

        var first = AnswerAsync(ch, new { pick = "b" });
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the first answer to start typing", Patience);
        var waiting = ch.RequestAsync(Ops.SessionAnswer, new { id = Second, question = "r1", request = Guid.NewGuid().ToString(), fields = new { pick = "a" } });
        await Task.Delay(300);
        // Only one answer is typed at a time, and the second session's question is answered
        // at the machine while it waits.
        Assert.Single(injector.Plans);
        Fixture.Append(other, Recorded("r1", new { pick = "c" }));
        release.SetResult();

        Assert.Equal("delivered", (await first).Outcome);
        var result = (await waiting).GetProperty("result");
        Assert.Equal(("stale", "the question has already been answered"), (result.GetProperty("outcome").GetString(), result.GetProperty("reason").GetString()));
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task Refused_stale_and_busy_outcomes_are_remembered_too_and_a_repeat_types_nothing()
    {
        var ch = await OpenAsync();

        // Refused while agent-ha-bridge runs; a repeat after it stops is still that outcome.
        probe.Reason = "agent-ha-bridge is running for this user";
        var refusedId = Guid.NewGuid().ToString();
        Assert.Equal("refused", (await AnswerAsync(ch, new { pick = "b" }, refusedId)).Outcome);
        probe.Reason = null;
        Assert.Equal(("refused", "agent-ha-bridge is running for this user"), await AnswerAsync(ch, new { pick = "b" }, refusedId));

        // Stale for a question not asked; the same request stays stale.
        var staleId = Guid.NewGuid().ToString();
        Assert.Equal("stale", (await AnswerAsync(ch, new { pick = "b" }, staleId, question: "q0")).Outcome);
        Assert.Equal("stale", (await AnswerAsync(ch, new { pick = "b" }, staleId, question: "q1")).Outcome);

        // Busy behind another answer; repeated once that one has finished, still busy.
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        injector.OnInject = () => Fixture.Append(events, Recorded("q1", new { pick = "b" }));
        var first = AnswerAsync(ch, new { pick = "b" });
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the first answer to start typing", Patience);
        var busyId = Guid.NewGuid().ToString();
        Assert.Equal("busy", (await AnswerAsync(ch, new { pick = "c" }, busyId)).Outcome);
        release.SetResult();
        Assert.Equal("delivered", (await first).Outcome);
        Assert.Equal("busy", (await AnswerAsync(ch, new { pick = "c" }, busyId)).Outcome);

        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task A_repeated_request_returns_the_first_outcome_and_never_types_again()
    {
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        injector.OnInject = () => Fixture.Append(events, Recorded("q1", new { pick = "b" }));
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var first = AnswerAsync(ch, new { pick = "b" }, request);
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the answer to start typing", Patience);
        // Resent while the first is still being typed, then again on a new channel after.
        var during = AnswerAsync(ch, new { pick = "b" }, request);
        release.SetResult();
        var again = await AnswerAsync(await OpenAsync(), new { pick = "b" }, request);

        Assert.Equal("delivered", (await first).Outcome);
        Assert.Equal("delivered", (await during).Outcome);
        Assert.Equal("delivered", again.Outcome);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task While_agent_ha_bridge_runs_an_answer_is_refused_with_its_reason_and_nothing_is_typed()
    {
        probe.Reason = "agent-ha-bridge is running for this user (AgentBridgeDaemon_x)";
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });

        Assert.Equal(("refused", probe.Reason), (outcome, reason));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Agent_ha_bridge_starting_after_the_request_arrived_still_stops_the_typing()
    {
        // Clear when the request arrives, running by the time the keys would go.
        probe.Answers = new Queue<string?>([null, "agent-ha-bridge is running for this user"]);
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });

        Assert.Equal(("refused", "agent-ha-bridge is running for this user"), (outcome, reason));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task An_invalid_answer_or_a_request_without_a_uuid_is_refused_untyped()
    {
        var ch = await OpenAsync();

        Assert.Equal(("refused", "\"pick\": not one of the choices"), await AnswerAsync(ch, new { pick = "z" }));
        Assert.Equal(("refused", "request must be a UUID"), await AnswerAsync(ch, new { pick = "b" }, request: "7"));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Keys_that_may_have_landed_before_the_injector_failed_are_unconfirmed_and_nothing_typed_is_refused()
    {
        var ch = await OpenAsync();

        injector.Result = new InjectionResult(InjectionStatus.Interrupted, "the session's console closed while the answer was being typed");
        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("console closed", reason);

        // A different question, so the first attempt's outcome is not what comes back.
        Fixture.Append(events, Ask("q2", second++));
        await source.RefreshAsync();
        injector.Result = new InjectionResult(InjectionStatus.NothingTyped, "the session runs as administrator");
        Assert.Equal(("refused", "the session runs as administrator"), await AnswerAsync(ch, new { pick = "b" }, question: "q2"));
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
        public Action? OnInject;
        public Task Gate = Task.CompletedTask;
        public InjectionResult Result = new(InjectionStatus.Typed, "ok");

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Plans) { Plans.Add((processId, plan)); }
            await Gate;
            if (Result.Status == InjectionStatus.Typed) { OnInject?.Invoke(); }
            return Result;
        }
    }
}
