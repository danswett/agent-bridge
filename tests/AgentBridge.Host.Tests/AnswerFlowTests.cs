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
[Trait("Category", "Network")]
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

    AnswerService Answerer(bool platform = true, bool remoteApproval = true, TimeProvider? time = null) => new(source, f.Processes, probe, new NoGuard(), injector, new AnswerOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = platform,
        CopilotRemoteApproval = remoteApproval,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
        Time = time ?? TimeProvider.System,
    }, m => { lock (logged) { logged.Add(m); } });

    // What the answer service logged, for its timing lines.
    readonly List<string> logged = [];

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
        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged, Capabilities.SessionsFocus, HistoryProtocol.Capability], await CapabilitiesAsync(ch));

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

        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged, Capabilities.SessionsFocus, HistoryProtocol.Capability], await CapabilitiesAsync(ch));
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
        // The failure follows once the answerer has seen the answer alone and waits for the
        // call's completion, on its own clock, well inside the second it allows for one. It used
        // to follow 300 ms of real time later, which a loaded runner could stretch past that
        // second, and the answer read delivered (#508).
        await using var clock = new PollClock();
        Serve(Answerer(time: clock));
        injector.OnInject = () =>
        {
            Fixture.Append(events, Recorded("q1", new { pick = "b" }));
            clock.OnNextWait(() => Fixture.Append(events, Copilot.ToolDone("q1", second++, success: false)));
        };
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });

        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("error", reason);
        Assert.DoesNotContain("at the machine", reason);
    }

    [Fact]
    public async Task A_different_recorded_answer_or_none_at_all_is_unconfirmed_never_delivered()
    {
        var ch = await OpenAsync();

        injector.OnInject = () => Fixture.Append(events, Recorded("q1", new { pick = "c" }));
        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("different answer", reason);
        Assert.DoesNotContain("at the machine", reason);

        // A fresh question, which the session never answers.
        Fixture.Append(events, Ask("q2", second++));
        await source.RefreshAsync();
        injector.OnInject = null;
        (outcome, reason) = await AnswerAsync(ch, new { pick = "a" }, question: "q2");
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("did not record an answer", reason);
        Assert.DoesNotContain("at the machine", reason);
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

    // Copilot on #837: a new CLI resumed the session after it asked. The host's view may still list the
    // question, but keys typed for it would go to whatever the new CLI shows: refused as stale.
    [Fact]
    public async Task An_answer_to_a_question_asked_before_the_session_was_resumed_is_stale_and_types_nothing()
    {
        var ch = await OpenAsync();
        Fixture.Append(events, Copilot.Event("session.resume", new { }, second++));

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b" });

        Assert.Equal("stale", outcome);
        Assert.Contains("resumed in a new Copilot CLI", reason);
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
        await Wait.UntilAsync(() => injector.Attempts.Count == 1, "the first answer to start typing", Patience);
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
        await Wait.UntilAsync(() => injector.Attempts.Count == 1, "the first answer to start typing", Patience);
        var waiting = ch.RequestAsync(Ops.SessionAnswer, new { id = Second, question = "r1", request = Guid.NewGuid().ToString(), fields = new { pick = "a" } });
        await Task.Delay(300);
        // Only one answer is typed at a time, and the second session's question is answered
        // at the machine while it waits.
        Assert.Single(injector.Attempts);
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
        await Wait.UntilAsync(() => injector.Attempts.Count == 1, "the first answer to start typing", Patience);
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
        await Wait.UntilAsync(() => injector.Attempts.Count == 1, "the answer to start typing", Patience);
        // Resent while the first is still being typed, then again on a new channel after.
        var during = AnswerAsync(ch, new { pick = "b" }, request);
        release.SetResult();
        var again = await AnswerAsync(await OpenAsync(), new { pick = "b" }, request);

        Assert.Equal("delivered", (await first).Outcome);
        Assert.Equal("delivered", (await during).Outcome);
        Assert.Equal("delivered", again.Outcome);
        Assert.Single(injector.Plans);
    }

    // ---- a field that opens with its default typed in (#662) ----

    // An option list, then a number the prompt opens with 3 already in it.
    static string AskDays(string id, int s) => Copilot.ToolStart(id, "ask_user", new
    {
        message = "How many?",
        requestedSchema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b", "c" } }, days = new { type = "integer", @default = 3 } }, required = new[] { "pick" } },
    }, s);

    async Task AskDaysAsync()
    {
        Fixture.Append(events, AskDays("q2", second++));
        await source.RefreshAsync();
    }

    [Fact]
    public async Task An_answer_that_would_be_added_to_a_pre_filled_default_is_refused_with_nothing_typed_and_the_question_stays_answerable()
    {
        await AskDaysAsync();
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b", days = 33 }, question: "q2");

        Assert.Equal("refused", outcome);
        Assert.EndsWith("answer at the machine (this field has a pre-filled default)", reason);
        Assert.Empty(injector.Plans);
        // Nothing typed, so it may still be answered: with the default, left as it is.
        injector.OnInject = () => Fixture.Append(events, Recorded("q2", new { pick = "b", days = 3 }));
        Assert.Equal("delivered", (await AnswerAsync(ch, new { pick = "b", days = 3 }, question: "q2")).Outcome);
        var (_, plan) = Assert.Single(injector.Plans);
        Assert.Equal(new KeySend("", true, AgentBridge.Host.Delivery.Copilot.CopilotPlanner.FieldEnterDelayMs, AgentBridge.Host.Delivery.Copilot.CopilotPlanner.AfterFieldMs), plan.Sends[^1]);
        Assert.DoesNotContain(plan.Sends, s => s.Text.Contains('3'));
    }

    [Fact]
    public async Task A_pre_filled_default_kept_is_delivered_once_however_often_the_request_is_resent()
    {
        await AskDaysAsync();
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        injector.OnInject = () => Fixture.Append(events, Recorded("q2", new { pick = "a", days = 3 }));
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var first = AnswerAsync(ch, new { pick = "a", days = 3 }, request, "q2");
        await Wait.UntilAsync(() => injector.Attempts.Count == 1, "the answer to start typing", Patience);
        var during = AnswerAsync(ch, new { pick = "a", days = 3 }, request, "q2");
        release.SetResult();
        var again = await AnswerAsync(await OpenAsync(), new { pick = "a", days = 3 }, request, "q2");

        Assert.Equal(["delivered", "delivered", "delivered"], [(await first).Outcome, (await during).Outcome, again.Outcome]);
        Assert.Single(injector.Plans);
        // Left as it opened: the 3 is never typed, which would make it 33.
        Assert.DoesNotContain(injector.Plans[0].Plan.Sends, s => s.Text.Contains('3'));
    }

    // ---- a form's words and their Enter (#467) ----

    const string Feedback = "Very close. The middle bar does not connect all the way";

    // The question the owner's answer went to, in small: an option list, then optional text.
    static string AskForm(string id, int s) => Copilot.ToolStart(id, "ask_user", new
    {
        message = "Approve the mark?",
        requestedSchema = new
        {
            properties = new { pick = new { type = "string", @enum = new[] { "a", "b", "c" } }, feedback = new { type = "string", title = "Feedback (optional)" } },
            required = new[] { "pick" },
        },
    }, s);

    // q1 answered and done, then the form asked as q2.
    async Task AskFormAsync()
    {
        Fixture.Append(events, Recorded("q1", new { pick = "a" }), Copilot.ToolDone("q1", second++), AskForm("q2", second++));
        await source.RefreshAsync();
    }

    static readonly object FormAnswer = new { pick = "b", feedback = Feedback };

    static async Task<JsonElement> AnswerResultAsync(SecureChannel ch, object fields, string request, string question = "q2")
    {
        var response = await ch.RequestAsync(Ops.SessionAnswer, new { id = Id, question, request, fields });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static string? Stage(JsonElement result) => result.TryGetProperty("stage", out var s) ? s.GetString() : null;

    // The CLI's screen: the option list, its caret moved once its keys have gone, until it takes
    // an Enter; then the text field, with the words once it has drawn them. Before is what else
    // the option list shows; hint, what else the text field does.
    static List<string> FormScreen(bool field, bool words, string? before = null, bool moved = false, string? hint = null)
    {
        if (field)
        {
            List<string> rows = ["Approve the mark?", "Feedback (optional)"];
            if (hint is not null) { rows.Add(hint); }
            return [.. rows, "╭──────────────╮", words ? $"│ {Feedback} │" : "│              │", "╰──────────────╯"];
        }
        List<string> list = ["Approve the mark?"];
        if (before is not null) { list.Add(before); }
        return [.. list, moved ? "    a" : "  ❯ a", moved ? "  ❯ b" : "    b", "    c"];
    }

    static bool HasEnterOnly(KeyPlan plan) => plan.Sends is [{ Text: "", Enter: true }];

    // The option list has taken its Enter, and the form moved to the text field.
    bool Picked() => injector.Plans.Any(p => p.Plan.Sends.Any(s => s.Enter));

    bool Moved() => injector.Plans.Count > 0;

    bool WordsTyped() => injector.Plans.Any(p => p.Plan.Sends.Any(s => s.Text == Feedback));

    // The Enter that submits the words: alone, after them.
    bool Submits(KeyPlan plan) => HasEnterOnly(plan) && WordsTyped();

    // A CLI that moves to the text field once the option is picked, and draws the words as soon
    // as they are typed.
    void DrawsWords() => injector.Screen = () => FormScreen(Picked(), WordsTyped(), moved: Moved());

    [Fact]
    public async Task A_forms_words_the_CLI_is_slow_to_draw_get_their_Enter_only_once_they_show_on_its_screen()
    {
        await AskFormAsync();
        var drawAt = DateTime.MaxValue;
        injector.Shows = Feedback;
        injector.Screen = () => FormScreen(Picked(), DateTime.UtcNow >= drawAt, moved: Moved());
        injector.OnInject = () =>
        {
            var plan = injector.Plans[^1].Plan;
            // A busy CLI takes the words in, and draws them, well after they were typed.
            if (plan.Sends.Any(s => s.Text == Feedback)) { drawAt = DateTime.UtcNow.AddMilliseconds(700); }
            if (Submits(plan)) { Fixture.Append(events, Recorded("q2", FormAnswer)); }
        };
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.Equal(4, injector.Plans.Count);
        // The option list's keys, then its Enter alone, then the words with no Enter of their own.
        Assert.DoesNotContain(injector.Plans[0].Plan.Sends, s => s.Enter || s.Text == Feedback);
        Assert.True(HasEnterOnly(injector.Plans[1].Plan));
        Assert.Equal((Feedback, false), (Assert.Single(injector.Plans[2].Plan.Sends).Text, injector.Plans[2].Plan.Sends[0].Enter));
        // Their Enter alone, and only once the screen showed them.
        Assert.True(HasEnterOnly(injector.Plans[3].Plan));
        Assert.True(injector.At[3] >= injector.SeenAt, $"Enter at {injector.At[3]:O}, words seen at {injector.SeenAt:O}");
    }

    [Fact]
    public async Task Words_are_counted_on_the_field_they_go_to_not_on_the_screen_before_the_keys_that_open_it()
    {
        // The option list shows the same words (an earlier answer quoted in the question, say),
        // and the text field does not until they are typed. Counted before the option's keys,
        // they would seem to be there already, and typing them would not seem to add them.
        await AskFormAsync();
        injector.Screen = () => FormScreen(Picked(), WordsTyped(), before: $"  Last time: {Feedback}", moved: Moved());
        injector.OnInject = () => { if (Submits(injector.Plans[^1].Plan)) { Fixture.Append(events, Recorded("q2", FormAnswer)); } };
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.True(HasEnterOnly(injector.Plans[^1].Plan));
    }

    [Fact]
    public async Task A_list_that_repaints_before_it_takes_its_Enter_is_not_taken_for_the_field_the_words_go_to()
    {
        // A busy CLI moves the caret at once, and takes the Enter that commits the option well
        // after. The text field it then shows has a hint reading like the words: counted while
        // the list was still up, the hint alone would pass for the words, and their Enter go
        // before they were drawn.
        await AskFormAsync();
        var fieldAt = DateTime.MaxValue;
        var drawAt = DateTime.MaxValue;
        injector.Shows = $"│ {Feedback} │";
        injector.Screen = () => FormScreen(DateTime.UtcNow >= fieldAt, DateTime.UtcNow >= drawAt, moved: Moved(), hint: $"e.g. {Feedback}");
        injector.OnInject = () =>
        {
            var plan = injector.Plans[^1].Plan;
            if (fieldAt == DateTime.MaxValue && plan.Sends.Any(s => s.Enter)) { fieldAt = DateTime.UtcNow.AddMilliseconds(300); }
            if (plan.Sends.Any(s => s.Text == Feedback)) { drawAt = DateTime.UtcNow.AddMilliseconds(400); }
            if (Submits(plan)) { Fixture.Append(events, Recorded("q2", FormAnswer)); }
        };
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.True(HasEnterOnly(injector.Plans[^1].Plan));
        Assert.True(injector.At[^1] >= injector.SeenAt, $"Enter at {injector.At[^1]:O}, words seen at {injector.SeenAt:O}");
    }

    [Fact]
    public async Task Words_are_not_typed_when_the_field_they_go_to_never_shows()
    {
        // The option's keys and its Enter went, and the screen never moved on: whatever it is
        // showing, the words are not for it.
        await AskFormAsync();
        injector.Screen = () => FormScreen(field: false, words: false);
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal(("unconfirmed", "typed"), (result.GetProperty("outcome").GetString(), Stage(result)));
        Assert.Contains("did not show", result.GetProperty("reason").GetString());
        Assert.Equal(2, injector.Plans.Count);
        Assert.DoesNotContain(injector.Plans, p => p.Plan.Sends.Any(s => s.Text == Feedback));
    }

    [Fact]
    public async Task Words_the_screen_could_never_show_are_refused_before_anything_is_typed()
    {
        // Only line drawing: nothing of it is looked for on screen, so its Enter could never go.
        await AskFormAsync();
        DrawsWords();
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, new { pick = "b", feedback = "────" }, question: "q2");

        Assert.Equal("refused", outcome);
        Assert.Contains("line-drawing", reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task An_answer_typed_into_the_prompt_that_the_session_never_records_is_typed_not_delivered_with_one_Enter_and_no_more()
    {
        // The words show, their Enter goes, and the CLI keeps the form on screen: it took the
        // Enter as a new line, as the owner's did.
        await AskFormAsync();
        DrawsWords();
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal(("unconfirmed", "typed"), (result.GetProperty("outcome").GetString(), Stage(result)));
        Assert.Contains("may not have been submitted", result.GetProperty("reason").GetString());
        Assert.Equal(4, injector.Plans.Count);
        Assert.Equal(2, injector.Plans.Sum(p => p.Plan.Sends.Count(s => s.Enter)));
    }

    [Fact]
    public async Task Words_that_never_show_on_the_screen_are_left_unsubmitted_with_nothing_after_them_pressed()
    {
        // The CLI never draws the words: whatever state it is in, an Enter now could go anywhere.
        await AskFormAsync();
        injector.Screen = () => FormScreen(Picked(), words: false, moved: Moved());
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal(("unconfirmed", "typed"), (result.GetProperty("outcome").GetString(), Stage(result)));
        Assert.Contains("not submitted", result.GetProperty("reason").GetString());
        Assert.Contains("finish it at the machine", result.GetProperty("reason").GetString());
        // The option list's keys, its Enter and the words went; no Enter after the words, ever.
        Assert.Equal(3, injector.Plans.Count);
        Assert.Equal((Feedback, false), (injector.Plans[^1].Plan.Sends[^1].Text, injector.Plans[^1].Plan.Sends[^1].Enter));
        Assert.Equal(1, injector.Plans.Sum(p => p.Plan.Sends.Count(s => s.Enter)));
    }

    [Fact]
    public async Task An_answer_given_at_the_machine_while_the_words_wait_to_show_gets_no_Enter_from_the_host()
    {
        // The person answers at the terminal in the moment the host waits for the words: the
        // Enter it was about to press would go to whatever the session shows next.
        await AskFormAsync();
        DrawsWords();
        injector.OnInject = () =>
        {
            if (injector.Plans[^1].Plan.Sends.Any(s => s.Text == Feedback))
            {
                Fixture.Append(events, Recorded("q2", new { pick = "c" }), Copilot.ToolDone("q2", second++));
            }
        };
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal(("unconfirmed", "recorded"), (result.GetProperty("outcome").GetString(), Stage(result)));
        // Nothing after the words.
        Assert.Contains(injector.Plans[^1].Plan.Sends, s => s.Text == Feedback);
    }

    // ---- every key held to the screen it was planned on (sec-510) ----

    // A CLI drawing a form one field at a time, as the keys typed so far leave it: the field it is
    // on (one per Enter it has taken) and what has been typed into that field. An Enter is taken
    // lag after it went; nothing after it is drawn before then.
    List<string> FieldsScreen(string[] names, TimeSpan lag = default)
    {
        var field = 0;
        var typed = "";
        var now = DateTime.UtcNow;
        lock (injector.Plans)
        {
            for (var i = 0; i < injector.Plans.Count; i++)
            {
                foreach (var send in injector.Plans[i].Plan.Sends)
                {
                    typed += send.Text;
                    if (!send.Enter) { continue; }
                    if (injector.At[i] + lag > now) { return Rows(); }
                    (field, typed) = (field + 1, "");
                }
            }
        }
        return Rows();
        List<string> Rows() => ["Answer the form", $"Field: {names[Math.Min(field, names.Length - 1)]}", field >= names.Length ? "(sent)" : $"│ {typed} │"];
    }

    int Enters() { lock (injector.Plans) { return injector.Plans.Sum(p => p.Plan.Sends.Count(s => s.Enter)); } }

    // q1 answered and done, then a form of these fields asked as q2, recorded once every field
    // has taken its Enter.
    async Task AskFieldsAsync(object properties, int fields, object answer)
    {
        Fixture.Append(events, Recorded("q1", new { pick = "a" }), Copilot.ToolDone("q1", second++),
            Copilot.ToolStart("q2", "ask_user", new { message = "Tell me", requestedSchema = new { properties } }, second++));
        await source.RefreshAsync();
        injector.OnInject = () => { if (Enters() == fields) { Fixture.Append(events, Recorded("q2", answer)); } };
    }

    static bool HeldTo(KeyPlan plan, string text) => plan.Sends[0].Expect is { } rows && rows.Any(r => r.Text.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task A_permission_prompt_opening_after_the_words_show_gets_no_Enter_and_the_answer_is_typed_not_recorded()
    {
        // The words show, and in the moment before their Enter a permission prompt opens over the
        // form: Enter would pick what it highlights. Held to the rows that showed the words, the
        // Enter is refused by the worker in the same attach (#173, sec-510).
        await AskFormAsync();
        var prompted = false;
        injector.Screen = () =>
        {
            if (prompted) { return ["Allow Copilot to run this command?", "  ❯ 1. Yes", "    2. No"]; }
            var rows = FormScreen(Picked(), WordsTyped(), moved: Moved());
            if (WordsTyped()) { prompted = true; }
            return rows;
        };
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal(("unconfirmed", "typed"), (result.GetProperty("outcome").GetString(), Stage(result)));
        Assert.Contains("screen changed", result.GetProperty("reason").GetString());
        Assert.True(HasEnterOnly(Assert.Single(injector.Refused)));
        // Nothing went after the words.
        Assert.Contains(injector.Plans[^1].Plan.Sends, s => s.Text == Feedback);
    }

    [Fact]
    public async Task Two_option_lists_then_words_each_go_held_to_the_field_they_were_planned_for()
    {
        // The shape of the owner's form (#467): two lists, then text.
        var answer = new { first = "b", second = "c", feedback = Feedback };
        string[] names = ["first", "second", "feedback"];
        await AskFieldsAsync(new
        {
            first = new { type = "string", @enum = new[] { "a", "b", "c" } },
            second = new { type = "string", @enum = new[] { "a", "b", "c" } },
            feedback = new { type = "string" },
        }, 3, answer);
        injector.Screen = () => FieldsScreen(names);
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, answer, Guid.NewGuid().ToString());

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.Empty(injector.Refused);
        var plans = injector.Plans.Select(p => p.Plan).ToList();
        Assert.True(HeldTo(plans[0], "Field: first"));
        var words = plans.FindIndex(p => p.Sends.Any(s => s.Text == Feedback));
        Assert.True(HeldTo(plans[words], "Field: feedback"));
        // The commit before the words, and the words' own Enter, each alone.
        Assert.True(HasEnterOnly(plans[words - 1]));
        Assert.True(HasEnterOnly(plans[words + 1]) && HeldTo(plans[words + 1], Feedback));
        Assert.Equal(words + 2, plans.Count);
    }

    [Fact]
    public async Task The_second_of_two_text_fields_waits_for_the_form_to_move_on_before_its_words_go()
    {
        // A busy CLI takes the first field's Enter a while after it went: read at once, the screen
        // still shows the first field, and the second field's words would be held to it.
        var answer = new { one = "first words to type", two = "second words to type" };
        string[] names = ["one", "two"];
        await AskFieldsAsync(new { one = new { type = "string" }, two = new { type = "string" } }, 2, answer);
        injector.Screen = () => FieldsScreen(names, TimeSpan.FromMilliseconds(300));
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, answer, Guid.NewGuid().ToString());

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        var plans = injector.Plans.Select(p => p.Plan).ToList();
        Assert.Equal(4, plans.Count);
        Assert.True(HeldTo(plans[0], "Field: one"));
        Assert.True(HeldTo(plans[2], "Field: two"));
        Assert.True(HasEnterOnly(plans[1]) && HasEnterOnly(plans[3]));
    }

    [Fact]
    public async Task A_list_after_words_goes_on_its_own_held_to_the_list_never_with_the_words_Enter()
    {
        var answer = new { note = Feedback, pick = "c" };
        string[] names = ["note", "pick"];
        await AskFieldsAsync(new { note = new { type = "string" }, pick = new { type = "string", @enum = new[] { "a", "b", "c" } } }, 2, answer);
        injector.Screen = () => FieldsScreen(names, TimeSpan.FromMilliseconds(200));
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, answer, Guid.NewGuid().ToString());

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        var plans = injector.Plans.Select(p => p.Plan).ToList();
        // The words, their Enter, then the list's keys and its own Enter, each a write of its own.
        Assert.Equal(4, plans.Count);
        Assert.True(HasEnterOnly(plans[1]));
        Assert.DoesNotContain(plans[2].Sends, s => s.Text == Feedback || s.Enter);
        Assert.True(HeldTo(plans[2], "Field: pick"));
        Assert.True(HasEnterOnly(plans[3]));
    }

    [Fact]
    public async Task A_question_answered_at_the_machine_while_the_words_wait_for_the_typing_slot_gets_none_of_them()
    {
        // Answered while the host waited for the injector: the worker asks again once it holds
        // the slot (KeyPlan.Proceed), and types nothing.
        await AskFormAsync();
        DrawsWords();
        injector.Holding = plan =>
        {
            if (plan.Sends.Any(s => s.Text == Feedback))
            {
                Fixture.Append(events, Recorded("q2", new { pick = "c" }), Copilot.ToolDone("q2", second++));
            }
        };
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal("unconfirmed", result.GetProperty("outcome").GetString());
        Assert.Contains(injector.Refused, p => p.Sends.Any(s => s.Text == Feedback));
        Assert.DoesNotContain(injector.Plans, p => p.Plan.Sends.Any(s => s.Text == Feedback));
    }

    [Fact]
    public async Task A_kept_default_between_a_list_and_words_is_its_own_held_Enter_and_nothing_is_typed_into_it()
    {
        // #700's rule (a pre-filled default is kept by its Enter alone) inside #510's per-field
        // writes: the default's Enter is a field of its own, held to that field, and its 3 is
        // never typed (it would be 33).
        var answer = new { pick = "b", days = 3, note = Feedback };
        string[] names = ["pick", "days", "note"];
        await AskFieldsAsync(new
        {
            pick = new { type = "string", @enum = new[] { "a", "b", "c" } },
            days = new { type = "integer", @default = 3 },
            note = new { type = "string" },
        }, 3, answer);
        injector.Screen = () => FieldsScreen(names);
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, answer, Guid.NewGuid().ToString());

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        var plans = injector.Plans.Select(p => p.Plan).ToList();
        Assert.Equal(5, plans.Count);
        Assert.True(HasEnterOnly(plans[2]) && HeldTo(plans[2], "Field: days"));
        Assert.True(HeldTo(plans[3], "Field: note"));
        Assert.DoesNotContain(plans, p => p.Sends.Any(s => s.Text.Contains('3')));
    }

    [Fact]
    public async Task A_permission_prompt_opening_between_two_lists_gets_none_of_the_second_lists_keys()
    {
        // Each field is its own write, made only once the form shows it and the question is still
        // asked: a prompt opening as the first list takes its Enter gets nothing more (#510 review).
        var answer = new { first = "b", second = "c", feedback = Feedback };
        string[] names = ["first", "second", "feedback"];
        await AskFieldsAsync(new
        {
            first = new { type = "string", @enum = new[] { "a", "b", "c" } },
            second = new { type = "string", @enum = new[] { "a", "b", "c" } },
            feedback = new { type = "string" },
        }, 3, answer);
        var prompt = false;
        injector.OnInject = () =>
        {
            if (prompt || Enters() != 1) { return; }
            prompt = true;
            Fixture.Append(events, Copilot.ToolStart("c9", "powershell", new { command = "gh pr checks" }, second++, "helper"),
                Copilot.Permission("r1", "c9", Copilot.Prompts["commands"], second++));
        };
        injector.Screen = () => prompt ? ["Allow Copilot to run this command?", "  ❯ 1. Yes", "    2. No"] : FieldsScreen(names);
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, answer, Guid.NewGuid().ToString());

        Assert.Equal(("unconfirmed", "typed"), (result.GetProperty("outcome").GetString(), Stage(result)));
        // The first list's keys, then its Enter alone; nothing after it.
        Assert.Equal(1, Enters());
        Assert.True(HasEnterOnly(injector.Plans[^1].Plan));
        Assert.DoesNotContain(injector.Plans, p => p.Plan.Sends.Any(s => s.Text == Feedback));
    }

    [Fact]
    public async Task A_field_that_repaints_back_and_forth_is_taken_only_once_it_reads_the_same_twice_running()
    {
        // After the list's Enter the screen reads the field, the list again, the field, and only
        // then settles on the field as drawn (with its hint). Two reads of the field with the list
        // between them are not two reads running (#510 review): taken there, the words would be
        // held to rows the screen no longer shows.
        await AskFormAsync();
        var flicker = new Queue<List<string>>();
        var queued = false;
        injector.Screen = () =>
        {
            if (!Picked()) { return FormScreen(false, false, moved: Moved()); }
            if (!queued)
            {
                queued = true;
                foreach (var rows in new[] { FormScreen(true, false), FormScreen(false, false, moved: true), FormScreen(true, false) }) { flicker.Enqueue(rows); }
            }
            return flicker.Count > 0 ? flicker.Dequeue() : FormScreen(true, WordsTyped(), hint: "Say what to change");
        };
        injector.OnInject = () => { if (Submits(injector.Plans[^1].Plan)) { Fixture.Append(events, Recorded("q2", FormAnswer)); } };
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.Empty(injector.Refused);
        Assert.True(HeldTo(injector.Plans.Single(p => p.Plan.Sends.Any(s => s.Text == Feedback)).Plan, "Say what to change"));
    }

    [Fact]
    public async Task A_screen_that_changes_while_a_write_waits_for_the_typing_slot_gets_none_of_its_keys()
    {
        // The worker reads the screen once it holds the slot, not when the write was handed over:
        // a prompt drawn in between refuses it.
        await AskFormAsync();
        var prompt = false;
        injector.Screen = () => prompt ? ["Allow Copilot to run this command?", "  ❯ 1. Yes", "    2. No"] : FormScreen(Picked(), WordsTyped(), moved: Moved());
        injector.Holding = plan => { if (plan.Sends.Any(s => s.Text == Feedback)) { prompt = true; } };
        var ch = await OpenAsync();

        var result = await AnswerResultAsync(ch, FormAnswer, Guid.NewGuid().ToString());

        Assert.Equal(("unconfirmed", "typed"), (result.GetProperty("outcome").GetString(), Stage(result)));
        Assert.Contains(injector.Refused, p => p.Sends.Any(s => s.Text == Feedback));
        Assert.DoesNotContain(injector.Plans, p => p.Plan.Sends.Any(s => s.Text == Feedback));
    }

    // ---- the timing line of an answer typed in parts ----

    static async Task<(List<string> Heard, JsonElement Result)> HearAsync(SecureChannel ch, object data)
    {
        var heard = new List<string>();
        var response = await ch.RequestAsync(Ops.SessionAnswer, data, progress: p => { lock (heard) { heard.Add(p.GetRawText()); } });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        lock (heard) { return ([.. heard], response.GetProperty("result")); }
    }

    // The answer's one timing line: its injection interval, and when typed was raised, if it was.
    async Task<(long Start, long End, long? Typed)> TimingAsync(string request)
    {
        bool Mine(string m) => m.StartsWith($"answer {request[..8]} timings ", StringComparison.Ordinal);
        await Wait.UntilAsync(() => { lock (logged) { return logged.Any(Mine); } }, "the timing line", Patience);
        string line;
        lock (logged) { line = logged.Single(Mine); }
        var inject = System.Text.RegularExpressions.Regex.Match(line, @"inject (?<a>\d+)-(?<b>\d+)");
        Assert.True(inject.Success, line);
        var typed = System.Text.RegularExpressions.Regex.Match(line, @"typed (?<t>\d+)");
        return (long.Parse(inject.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(inject.Groups["b"].Value, System.Globalization.CultureInfo.InvariantCulture),
            typed.Success ? long.Parse(typed.Groups["t"].Value, System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    [Fact]
    public async Task A_form_typed_in_parts_logs_one_injection_from_its_first_key_to_its_last_and_typed_only_once_at_the_end()
    {
        // The list's keys, its Enter, the words and their Enter are four writes with screen waits
        // between them: the injection is all of them, not the final Enter alone (#510 review).
        await AskFormAsync();
        DrawsWords();
        injector.OnInject = () => { if (Submits(injector.Plans[^1].Plan)) { Fixture.Append(events, Recorded("q2", FormAnswer)); } };
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var (heard, result) = await HearAsync(ch, new { id = Id, question = "q2", request, fields = FormAnswer, progress = true });

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.Equal(["""{"stage":"received"}""", """{"stage":"typed","working":false}"""], heard);
        var (start, end, typed) = await TimingAsync(request);
        Assert.Equal(4, injector.Plans.Count);
        var span = (injector.At[^1] - injector.At[0]).TotalMilliseconds;
        Assert.True(end - start >= span - 5, $"inject {start}-{end} against {span:0} ms between the first and last write");
        Assert.True(typed >= end, $"typed {typed}, inject end {end}");
    }

    [Fact]
    public async Task A_form_stopped_partway_logs_the_injection_of_the_keys_that_went_and_is_never_typed()
    {
        // The words never show, so their Enter is not pressed: keys went, the answer is not all
        // typed, so no typed progress, but the injection of what went is in the line.
        await AskFormAsync();
        injector.Screen = () => FormScreen(Picked(), words: false, moved: Moved());
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var (heard, result) = await HearAsync(ch, new { id = Id, question = "q2", request, fields = FormAnswer, progress = true });

        Assert.Equal(("unconfirmed", "typed"), (result.GetProperty("outcome").GetString(), Stage(result)));
        Assert.Equal(["""{"stage":"received"}"""], heard);
        var (start, end, typed) = await TimingAsync(request);
        Assert.Equal(3, injector.Plans.Count);
        Assert.True(end > start, $"inject {start}-{end}");
        Assert.Null(typed);
    }

    [Fact]
    public async Task An_answer_with_words_is_not_typed_at_all_into_a_screen_the_host_cannot_read()
    {
        await AskFormAsync();
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, FormAnswer, question: "q2");

        Assert.Equal("refused", outcome);
        Assert.Contains("could not read the session's screen", reason);
        Assert.Empty(injector.Plans);
        // Nothing typed, so the prompt may still be answered once the screen can be read.
        DrawsWords();
        injector.OnInject = () => { if (Submits(injector.Plans[^1].Plan)) { Fixture.Append(events, Recorded("q2", FormAnswer)); } };
        Assert.Equal("delivered", (await AnswerAsync(ch, FormAnswer, question: "q2")).Outcome);
    }

    [Fact]
    public async Task An_answer_recorded_after_the_timeout_comes_back_delivered_to_a_resend_after_a_reconnect_without_typing_again()
    {
        await AskFormAsync();
        DrawsWords();
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var first = await AnswerResultAsync(ch, FormAnswer, request);
        Assert.Equal(("unconfirmed", "typed"), (first.GetProperty("outcome").GetString(), Stage(first)));
        var typed = injector.Plans.Count;
        // Not yet recorded: the same answer again.
        Assert.Equal("unconfirmed", (await AnswerResultAsync(ch, FormAnswer, request)).GetProperty("outcome").GetString());

        // Submitted at the machine after the host stopped watching, its Enter a new line in the
        // words, as the owner's was.
        Fixture.Append(events, Recorded("q2", new { pick = "b", feedback = Feedback + "\n" }), Copilot.ToolDone("q2", second++));
        var again = await AnswerResultAsync(await OpenAsync(), FormAnswer, request);

        Assert.Equal("delivered", again.GetProperty("outcome").GetString());
        Assert.Null(Stage(again));
        Assert.Equal(typed, injector.Plans.Count);
    }

    [Fact]
    public async Task An_answer_whose_typing_was_interrupted_comes_back_delivered_once_the_session_records_it()
    {
        // The injector failed while typing the words: some of them may have landed.
        await AskFormAsync();
        DrawsWords();
        injector.OnInject = () =>
        {
            if (injector.Plans[^1].Plan.Sends.Any(s => s.Text == Feedback)) { injector.Result = new(InjectionStatus.Interrupted, "the console went away"); }
        };
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var first = await AnswerResultAsync(ch, FormAnswer, request);
        Assert.Equal(("unconfirmed", "typed"), (first.GetProperty("outcome").GetString(), Stage(first)));
        var typed = injector.Plans.Count;

        Fixture.Append(events, Recorded("q2", FormAnswer), Copilot.ToolDone("q2", second++));
        var again = await AnswerResultAsync(await OpenAsync(), FormAnswer, request);

        Assert.Equal("delivered", again.GetProperty("outcome").GetString());
        Assert.Equal(typed, injector.Plans.Count);
    }

    [Fact]
    public async Task A_late_record_whose_completion_never_follows_settles_on_the_one_resend_a_reconnect_makes()
    {
        await AskFormAsync();
        DrawsWords();
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();
        Assert.Equal("typed", Stage(await AnswerResultAsync(ch, FormAnswer, request)));

        // Recorded at the machine, and the call's completion never written. A client asks once
        // per reconnect: the moment allowed for a completion that might say the call failed is
        // waited out within that one ask.
        Fixture.Append(events, Recorded("q2", FormAnswer));
        var again = await AnswerResultAsync(await OpenAsync(), FormAnswer, request);

        Assert.Equal("delivered", again.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_late_record_of_another_answer_is_recorded_as_such_and_a_resend_never_answers_a_newer_question()
    {
        await AskFormAsync();
        DrawsWords();
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        Assert.Equal("typed", Stage(await AnswerResultAsync(ch, FormAnswer, request)));
        var typed = injector.Plans.Count;
        // Answered differently at the machine, then the session asks something new.
        Fixture.Append(events, Recorded("q2", new { pick = "c" }), Copilot.ToolDone("q2", second++), Ask("q3", second++));
        await source.RefreshAsync();

        var again = await AnswerResultAsync(await OpenAsync(), FormAnswer, request);

        Assert.Equal(("unconfirmed", "recorded"), (again.GetProperty("outcome").GetString(), Stage(again)));
        Assert.Contains("different answer", again.GetProperty("reason").GetString());
        Assert.Equal(typed, injector.Plans.Count);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_yes_or_no_question_whose_default_is_text_is_refused_for_either_answer_and_nothing_is_typed(bool answer)
    {
        // Copilot opens it on its Other row with "yes" typed in: walked from there, Yes recorded No
        // and No reopened Other (#768). The refusal says which field, and why (#812 review).
        Fixture.Append(events, Recorded("q1", new { pick = "a" }), Copilot.ToolDone("q1", second++),
            Copilot.ToolStart("q2", "ask_user", new { message = "Go?", requestedSchema = new { properties = new { ok = new { type = "boolean", @default = "yes" } } } }, second++));
        await source.RefreshAsync();
        var ch = await OpenAsync();

        var (outcome, reason) = await AnswerAsync(ch, new { ok = answer }, question: "q2");

        Assert.Equal("refused", outcome);
        Assert.Equal("\"ok\" has a default that is not Yes or No (a text default opens it on its Other row); answer it at the machine", reason);
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
        Assert.DoesNotContain("at the machine", reason);

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
        // The session's screen as the CLI draws it, or null for a host that cannot read it.
        public Func<IReadOnlyList<string>?> Screen = () => null;
        // When each plan went, and when the screen was first seen showing what Shows names.
        public readonly List<DateTime> At = [];
        public string? Shows;
        public DateTime? SeenAt;

        // Plans the worker would have refused before their first key, as WorkerInjector does: one
        // whose question was no longer held once it had the typing slot (KeyPlan.Proceed), or one
        // held to rows (KeySend.Expect) the screen no longer reads. Not in Plans: nothing of them
        // was typed.
        public readonly List<KeyPlan> Refused = [];
        // Every plan handed to the injector, typed or not, in the order it arrived.
        public readonly List<KeyPlan> Attempts = [];
        // Runs once a plan has the typing slot, before the worker's last checks.
        public Action<KeyPlan>? Holding;

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Plans) { Attempts.Add(plan); }
            await Gate;
            Holding?.Invoke(plan);
            // The worker's checks, in its order: the question once it holds the slot, then the
            // screen as it reads in the same attach, just before the first key. Plans holds only
            // what got past them, so the screen read here is still the one before this plan's keys.
            string? refusal = plan.Proceed?.Invoke() is { } stop ? $"stopped before typing: {stop}" : null;
            if (refusal is null && plan.Sends.Count > 0 && plan.Sends[0].Expect is { Count: > 0 } expect
                && (Screen() is not { } screen || expect.Any(e => e.Row >= screen.Count || screen[e.Row].TrimEnd() != e.Text.TrimEnd())))
            {
                refusal = AgentBridge.Host.Delivery.Windows.WorkerInjector.ScreenChanged;
            }
            if (refusal is not null)
            {
                lock (Plans) { Refused.Add(plan); }
                return new InjectionResult(InjectionStatus.NothingTyped, refusal);
            }
            lock (Plans) { Plans.Add((processId, plan)); At.Add(DateTime.UtcNow); }
            if (Result.Status == InjectionStatus.Typed) { OnInject?.Invoke(); }
            return Result;
        }

        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default)
        {
            var rows = Screen();
            if (rows is not null && Shows is not null && SeenAt is null && string.Concat(rows).Contains(Shows, StringComparison.Ordinal)) { SeenAt = DateTime.UtcNow; }
            return Task.FromResult(new ConsoleScreen(rows, rows is null ? "this host cannot read the session's screen" : null));
        }
    }
}
