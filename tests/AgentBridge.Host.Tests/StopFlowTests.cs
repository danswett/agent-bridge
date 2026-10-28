using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// session.stop end to end: fixture sessions read by the real SessionSource, a stop sent by a
// client through the in-process relay and carried out by the real StopService (sharing its
// gate with a real ReplyService), with a fake injector and a fake process control that play
// the agent: it exits on /exit, or not, and can be terminated. Nothing here touches a real
// process; the real worker and the real process control against a console of the tests' own
// are in InjectorIntegrationTests.
public sealed class StopFlowTests : IAsyncLifetime
{
    const string Id = "77777777-7777-4777-8777-777777777777";
    const string Other = "88888888-8888-4888-8888-888888888888";
    const int Pid = 700;
    static readonly DateTimeOffset Started = Fixture.T0.AddHours(-1);
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    readonly FakeProbe probe = new();
    readonly FakeInjector injector = new();
    readonly FakeControl control;
    readonly DeliveryGate gate = new();
    readonly FakeTimeProvider clock = new(Fixture.T0.AddHours(2));
    TestRelay relay = null!;
    SessionSource source = null!;
    Device host = null!;
    Device client = null!;
    string events = "";
    int second = 10;

    public StopFlowTests() => control = new FakeControl(f);

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, Pid, name: "Ending session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        control.Run(AgentKind.Copilot, Pid, Started);
        var o = f.Options();
        // Every change goes out at once, so the ending upsert is seen however quickly a
        // session then goes.
        source = new SessionSource(new SessionSourceOptions
        {
            CopilotHome = o.CopilotHome,
            ClaudeHome = o.ClaudeHome,
            CodexHome = o.CodexHome,
            ClaudeRegistrationRoots = o.ClaudeRegistrationRoots,
            CodexRegistrationRoots = o.CodexRegistrationRoots,
            UseFileSystemWatcher = false,
            CoalesceInterval = TimeSpan.Zero,
        }, f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        client = await relay.ConnectAsync(token, "Phone", "pwa");
        Serve(Stopper());
        // By default the agent exits on /exit, as Copilot, Claude Code and Codex do.
        injector.OnInject = (pid, _) => control.Exit(pid);
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    StopService Stopper(bool platform = true, int graceMs = 600) => new(source, f.Processes, probe, injector, control, new StopOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = platform,
        Grace = TimeSpan.FromMilliseconds(graceMs),
        TerminateWait = TimeSpan.FromMilliseconds(600),
        Time = clock,
    }, gate: gate);

    ReplyService Replier() => new(source, f.Processes, probe, new NoGuard(), injector, new ReplyOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = true,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
    }, gate: gate);

    void Serve(ISessionStopper? stops, ISessionReplier? replies = null) =>
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), replies: replies, stops: stops).HandleAsync;

    async Task<SecureChannel> OpenAsync()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return await client.Hub.OpenAsync(host.Id);
    }

    sealed record Outcome(string Outcome_, string? Reason, string? Confirm, int? Seconds, bool? Forced);

    static async Task<Outcome> StopAsync(SecureChannel ch, string? confirm = null, string? request = null, string id = Id)
    {
        var data = new Dictionary<string, object?> { ["id"] = id, ["request"] = request ?? Guid.NewGuid().ToString() };
        if (confirm is not null) { data["confirm"] = confirm; }
        var response = await ch.RequestAsync(Ops.SessionStop, data);
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        var r = response.GetProperty("result");
        string? S(string name) => r.TryGetProperty(name, out var v) ? v.GetString() : null;
        return new Outcome(S("outcome")!, S("reason"), S("confirm"),
            r.TryGetProperty("confirmSeconds", out var n) ? n.GetInt32() : null,
            r.TryGetProperty("forced", out var b) ? b.GetBoolean() : null);
    }

    static (string, string?) Pair(Outcome o) => (o.Outcome_, o.Reason);

    static async Task<string[]> CapabilitiesAsync(SecureChannel ch)
    {
        var hello = await ch.RequestAsync(Ops.Hello, new { });
        return [.. hello.GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];
    }

    async Task WorkingAsync()
    {
        Fixture.Append(events, Copilot.User("next", second++), Copilot.TurnStart(second++),
            Copilot.ToolStart("t" + second, "powershell", new { command = "dotnet test" }, second++));
        await source.RefreshAsync();
    }

    string[] Typed => [.. injector.Plans.Select(p => $"{p.Pid}:{string.Join('|', p.Plan.Sends.Select(s => s.Text + (s.Enter ? "⏎" : "")))}")];

    [Fact]
    public async Task Hello_offers_ending_sessions_only_while_agent_ha_bridge_is_not_running_and_only_where_a_stopper_can_act()
    {
        var ch = await OpenAsync();
        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsStop], await CapabilitiesAsync(ch));

        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.Equal([Capabilities.SessionsRead], await CapabilitiesAsync(ch));
        probe.Reason = null;

        Serve(new UnsupportedPlatformStopper());
        Assert.Equal([Capabilities.SessionsRead], await CapabilitiesAsync(ch));
        Assert.Equal(("refused", "ending sessions is not supported on this platform yet; end it at the machine"), Pair(await StopAsync(ch)));
        Serve(Stopper(platform: false));
        Assert.Equal([Capabilities.SessionsRead], await CapabilitiesAsync(ch));
        Assert.Equal("refused", (await StopAsync(ch)).Outcome_);
        Serve(null);
        Assert.Equal("this host does not end sessions; end it at the machine", (await StopAsync(ch)).Reason);
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Fact]
    public async Task An_idle_session_ends_on_one_press_with_exit_typed_into_its_console_shows_as_ending_and_is_then_removed()
    {
        using var feed = source.Subscribe();
        var ch = await OpenAsync();

        var result = await StopAsync(ch);

        Assert.Equal(new Outcome("ended", null, null, null, null), result);
        Assert.Equal([$"{Pid}:/exit⏎"], Typed);
        Assert.Empty(control.Terminated);
        Assert.Null(source.Get(Id));
        var seen = new List<string>();
        while (feed.Events.TryRead(out var ev))
        {
            seen.Add(ev.Data switch { Session s => $"upsert {s.Status}", SessionRemoved => "remove", _ => ev.Name });
        }
        Assert.Equal(["upsert Idle", Events.SessionsSynced, "upsert Ending", "remove"], seen);
    }

    [Fact]
    public async Task A_working_session_takes_a_second_press_with_the_token_the_first_returned_and_the_first_press_does_nothing()
    {
        await WorkingAsync();
        var ch = await OpenAsync();

        var first = await StopAsync(ch);

        Assert.Equal(("confirm", "this session is still working; the confirmation lapses in 10 s", 10), (first.Outcome_, first.Reason, first.Seconds));
        Assert.Matches("^[A-Za-z0-9_-]{22}$", first.Confirm);
        Assert.Empty(injector.Plans);
        Assert.Equal(SessionStatus.Working, source.Get(Id)!.Status);

        // A second press without the token is another first press.
        Assert.Equal("confirm", (await StopAsync(ch)).Outcome_);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("ended", (await StopAsync(ch, first.Confirm)).Outcome_);
        Assert.Equal([$"{Pid}:/exit⏎"], Typed);
    }

    [Fact]
    public async Task A_second_press_after_the_window_lapsed_asks_again_rather_than_ending()
    {
        await WorkingAsync();
        var ch = await OpenAsync();
        var first = await StopAsync(ch);

        clock.Advance(TimeSpan.FromSeconds(11));
        var late = await StopAsync(ch, first.Confirm);

        Assert.Equal(("confirm", "the first press has lapsed; this session is still working; the confirmation lapses in 10 s"), (late.Outcome_, late.Reason));
        Assert.NotEqual(first.Confirm, late.Confirm);
        Assert.Empty(injector.Plans);
        // The press it asked for is good.
        Assert.Equal("ended", (await StopAsync(ch, late.Confirm)).Outcome_);
    }

    [Fact]
    public async Task A_confirmation_never_ends_a_process_that_replaced_the_one_the_first_press_saw()
    {
        await WorkingAsync();
        var ch = await OpenAsync();
        var first = await StopAsync(ch);

        // The same pid, now another process: the session was resumed and the id reused.
        control.Run(AgentKind.Copilot, Pid, Started.AddMinutes(5));
        var replaced = await StopAsync(ch, first.Confirm);

        Assert.Equal(("confirm", "the session's process has changed since the first press; this session is still working; the confirmation lapses in 10 s"), (replaced.Outcome_, replaced.Reason));
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
        Assert.Equal("ended", (await StopAsync(ch, replaced.Confirm)).Outcome_);
    }

    [Fact]
    public async Task A_token_confirms_only_the_session_it_was_given_for()
    {
        var other = f.CopilotSession(Other, Pid + 1, name: "Another");
        Fixture.Append(other, Copilot.User("go", 1), Copilot.TurnStart(2));
        control.Run(AgentKind.Copilot, Pid + 1, Started);
        await WorkingAsync();
        var ch = await OpenAsync();

        var first = await StopAsync(ch);
        var wrong = await StopAsync(ch, first.Confirm, id: Other);

        Assert.Equal("confirm", wrong.Outcome_);
        Assert.Empty(injector.Plans);
        Assert.Equal("ended", (await StopAsync(ch, first.Confirm)).Outcome_);
        Assert.NotNull(source.Get(Other));
    }

    [Fact]
    public async Task A_session_waiting_for_background_work_or_on_you_takes_two_presses_too()
    {
        // The main agent started a background agent and ended its own turn: idle, delegating.
        Fixture.Append(events, Copilot.User("delegate it", 5), Copilot.TurnStart(6), Copilot.Assistant("", 7, tools: ["t1"]),
            Copilot.ToolStart("t1", "task", new { name = "helper", description = "Help out", mode = "background" }, 7),
            Copilot.SubagentStarted("t1", "a1", 7), Copilot.ToolDone("t1", 8), Copilot.TurnEnd(8), Copilot.TurnStart(9),
            Copilot.User("the task prompt", 9, "a1"), Copilot.TurnStart(9, "a1"), Copilot.Assistant("Started it.", 10), Copilot.TurnEnd(11));
        await source.RefreshAsync();
        Assert.Equal((SessionStatus.Idle, 1), (source.Get(Id)!.Status, source.Get(Id)!.Activity.Background!.Agents));
        var ch = await OpenAsync();

        Assert.Equal("this session is waiting for background agents it started; the confirmation lapses in 10 s", (await StopAsync(ch)).Reason);

        Fixture.Append(events, Copilot.ToolStart("q1", "ask_user", new { message = "Which?" }, 12));
        await source.RefreshAsync();
        Assert.Equal("this session is waiting on you; the confirmation lapses in 10 s", (await StopAsync(ch)).Reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_session_asking_a_question_is_terminated_without_exit_being_typed_which_would_answer_it()
    {
        Fixture.Append(events, Copilot.User("next", 5), Copilot.TurnStart(6), Copilot.ToolStart("q1", "ask_user", new { message = "Which?" }, 7));
        await source.RefreshAsync();
        var ch = await OpenAsync();
        var first = await StopAsync(ch);

        var result = await StopAsync(ch, first.Confirm);

        Assert.Equal(("ended", true), (result.Outcome_, result.Forced));
        Assert.Empty(injector.Plans);
        Assert.Equal([Pid], control.Terminated);
    }

    [Fact]
    public async Task A_subagents_question_seen_only_in_the_transcript_also_keeps_exit_from_being_typed()
    {
        // The session source does not report a subagent's question; the transcript has it.
        Fixture.Append(events, Copilot.ToolStart("q9", "ask_user", new { message = "Which?" }, 5, agentId: "a1"));
        await source.RefreshAsync();
        Assert.Null(source.Get(Id)!.Question);
        var ch = await OpenAsync();

        var result = await StopAsync(ch);

        Assert.Equal(("ended", true), (result.Outcome_, result.Forced));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_session_that_does_not_exit_on_exit_is_terminated_after_the_grace_and_says_so()
    {
        injector.OnInject = null;
        var ch = await OpenAsync();

        var result = await StopAsync(ch);

        Assert.Equal(new Outcome("ended", null, null, null, true), result);
        Assert.Equal([$"{Pid}:/exit⏎"], Typed);
        Assert.Equal([Pid], control.Terminated);
        Assert.Null(source.Get(Id));
    }

    [Fact]
    public async Task A_process_that_will_not_go_fails_and_the_session_shows_its_own_status_again()
    {
        injector.OnInject = null;
        control.TerminateWorks = false;
        var ch = await OpenAsync();

        var result = await StopAsync(ch);

        Assert.Equal(("failed", "the host could not terminate the session's process (denied)"), (result.Outcome_, result.Reason));
        Assert.Equal(SessionStatus.Idle, source.Get(Id)!.Status);
    }

    [Fact]
    public async Task What_the_host_must_not_end_is_refused_on_the_first_press_and_nothing_is_typed_or_terminated()
    {
        var ch = await OpenAsync();

        probe.Reason = "agent-ha-bridge is running for this user (AgentBridgeDaemon_x)";
        Assert.Equal(("refused", probe.Reason), Pair(await StopAsync(ch)));
        probe.Reason = null;

        control.Refusal = "the session runs as another user; end it at the machine";
        Assert.Equal(("refused", control.Refusal), Pair(await StopAsync(ch)));
        control.Refusal = null;

        Assert.Equal(("refused", "request must be a UUID"), Pair(await StopAsync(ch, request: "7")));
        Assert.Equal(("refused", "the confirmation is not one this host issued"), Pair(await StopAsync(ch, confirm: "<script>")));

        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Fact]
    public async Task Agent_ha_bridge_starting_after_the_press_stops_the_host_before_it_types()
    {
        var ch = await OpenAsync();

        // Free on arrival, running by the time the typing turn comes.
        probe.Answers = new Queue<string?>([null, "agent-ha-bridge is running for this user"]);
        var result = await StopAsync(ch);

        Assert.Equal(("refused", "agent-ha-bridge is running for this user"), (result.Outcome_, result.Reason));
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
        Assert.Equal(SessionStatus.Idle, source.Get(Id)!.Status);
    }

    [Fact]
    public async Task Ending_waits_for_an_answer_or_reply_being_typed_and_holds_the_session_against_them_until_it_has_gone()
    {
        Serve(Stopper(graceMs: 3000), Replier());
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        injector.OnInject = (_, plan) => Fixture.Append(events, Copilot.Event("user.message", new { content = plan.Sends[0].Text, delivery = "idle" }, second++));
        var ch = await OpenAsync();

        var reply = ch.RequestAsync(Ops.SessionReply, new { id = Id, request = Guid.NewGuid().ToString(), text = "hello" });
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the reply to start typing", Patience);
        Assert.Equal(("busy", "an answer or reply to this session is being delivered, or it is already being ended"), Pair(await StopAsync(ch)));
        release.SetResult();
        Assert.Equal("delivered", (await reply).GetProperty("result").GetProperty("outcome").GetString());

        // While the session is being ended (it ignores /exit), a reply is busy.
        Fixture.Append(events, Copilot.TurnStart(second++), Copilot.Assistant("Hello.", second++), Copilot.TurnEnd(second++));
        await source.RefreshAsync();
        injector.OnInject = null;
        var stopping = StopAsync(ch);
        await Wait.UntilAsync(() => source.Get(Id)?.Status == SessionStatus.Ending, "the session to show as ending", Patience);
        var during = await ch.RequestAsync(Ops.SessionReply, new { id = Id, request = Guid.NewGuid().ToString(), text = "wait" });
        Assert.Equal("busy", during.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Equal("busy", (await StopAsync(ch)).Outcome_);
        Assert.Equal(("ended", true), ((await stopping).Outcome_, (await stopping).Forced));
    }

    [Fact]
    public async Task A_repeated_request_returns_the_first_outcome_and_never_acts_again_even_on_a_new_channel()
    {
        await WorkingAsync();
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var first = await StopAsync(ch, request: request);
        var again = await StopAsync(await OpenAsync(), request: request);
        Assert.Equal(first, again);

        var confirm = Guid.NewGuid().ToString();
        Assert.Equal("ended", (await StopAsync(ch, first.Confirm, confirm)).Outcome_);
        Assert.Equal("ended", (await StopAsync(await OpenAsync(), first.Confirm, confirm)).Outcome_);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task A_session_or_process_that_has_already_gone_is_stale()
    {
        var ch = await OpenAsync();

        Assert.Equal(("stale", "the session has ended"), Pair(await StopAsync(ch, id: Other)));
        f.Processes.Exit(AgentKind.Copilot, Pid);
        Assert.Equal(("stale", "the session's process has ended"), Pair(await StopAsync(ch)));
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Fact]
    public async Task A_claude_session_ends_the_same_way_with_exit_typed_into_its_own_process()
    {
        const string ClaudeId = "c1a0de00-0000-4000-8000-000000000001";
        var transcript = f.ClaudeTranscript(ClaudeId);
        Fixture.Append(transcript, Claude.User("hi", 1), Claude.Assistant(2, "end_turn", Claude.Text("Hello.")));
        f.ClaudeRegistration(ClaudeId, 300, transcript);
        control.Run(AgentKind.Claude, 300, Started);
        await source.RefreshAsync();
        Assert.Equal(SessionStatus.Idle, source.Get(ClaudeId)!.Status);
        var ch = await OpenAsync();

        Assert.Equal("ended", (await StopAsync(ch, id: ClaudeId)).Outcome_);
        Assert.Equal(["300:/exit⏎"], Typed);
        Assert.Null(source.Get(ClaudeId));
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
        public Action<int, KeyPlan>? OnInject;
        public Task Gate = Task.CompletedTask;

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Plans) { Plans.Add((processId, plan)); }
            await Gate;
            OnInject?.Invoke(processId, plan);
            return new InjectionResult(InjectionStatus.Typed, "ok");
        }
    }

    // The agent processes the tests pretend run, and nothing else: Open finds only these.
    sealed class FakeControl(Fixture f) : IProcessControl
    {
        readonly Lock gate = new();
        readonly Dictionary<int, (AgentKind Agent, DateTimeOffset Started, TaskCompletionSource Gone)> live = [];
        public readonly List<int> Terminated = [];
        public string? Refusal;
        public bool TerminateWorks = true;

        public void Run(AgentKind agent, int pid, DateTimeOffset started)
        {
            lock (gate) { live[pid] = (agent, started, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)); }
            f.Processes.Run(agent, pid, startedAt: started);
        }

        public void Exit(int pid)
        {
            (AgentKind Agent, DateTimeOffset Started, TaskCompletionSource Gone) p;
            lock (gate) { if (!live.Remove(pid, out p)) { return; } }
            f.Processes.Exit(p.Agent, pid);
            p.Gone.TrySetResult();
        }

        public HeldOpen Open(int processId, DateTimeOffset startedAt)
        {
            if (Refusal is { } refusal) { return HeldOpen.Refused(refusal); }
            lock (gate)
            {
                if (!live.TryGetValue(processId, out var p) || p.Started != startedAt) { return HeldOpen.Exited(); }
                return HeldOpen.Of(new Held(this, processId, p.Gone.Task));
            }
        }

        sealed class Held(FakeControl control, int pid, Task gone) : IHeldProcess
        {
            public int Id => pid;

            public async Task<bool> WaitForExitAsync(TimeSpan timeout) => await Task.WhenAny(gone, Task.Delay(timeout)) == gone;

            public string? Terminate()
            {
                lock (control.gate) { control.Terminated.Add(pid); }
                if (!control.TerminateWorks) { return "the host could not terminate the session's process (denied)"; }
                control.Exit(pid);
                return null;
            }

            public void Dispose() { }
        }
    }
}
