using Microsoft.Extensions.DependencyInjection;
using AgentBridge.Relay;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using System.Text.RegularExpressions;
using Xunit;

// session.reply end to end: a fixture Copilot session read by the real SessionSource, a
// reply sent by a client through the in-process relay, delivered by the real ReplyService
// (sharing its gate with a real AnswerService) into a fake injector that plays the CLI: it
// writes the prompt the session would record. Real keys into a real console are in
// InjectorIntegrationTests.
[Trait("Category", "Network")]
public sealed class ReplyFlowTests : IAsyncLifetime
{
    const string Id = "99999999-9999-4999-8999-999999999999";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    readonly FakeProbe probe = new();
    readonly FakeInjector injector = new();
    readonly DeliveryGate gate = new();
    readonly SessionDrivers drivers = new();
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
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System, drivers);
        await source.RefreshAsync();
        // With the in-process blob store, for files too large for the channel.
        relay = await TestRelay.StartAsync(o => o.Blobs.Backend = "local");
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

    ReplyService Replier(bool platform = true, IAgentHaBridgeProbe? g5 = null) => new(source, f.Processes, g5 ?? probe, new NoGuard(), injector, new ReplyOptions
    {
        CopilotHome = f.CopilotHome,
        PlatformSupported = platform,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
    }, gate: gate);

    AnswerService Answerer(IAgentHaBridgeProbe? g5 = null) => new(source, f.Processes, g5 ?? probe, new NoGuard(), injector, new AnswerOptions
    {
        CopilotHome = f.CopilotHome,
        // Set, not left to the default (Windows only), so these run the same on Linux.
        PlatformSupported = true,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
    }, gate: gate);

    // The service the relay's channels reach, which the app's pipe reaches too where a test serves one.
    SessionService served = null!;

    void Serve(ISessionAnswerer? answers, ISessionReplier? replies, AttachmentStore? attachments = null)
    {
        served = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), answers: answers, replies: replies, attachments: attachments) { Drivers = drivers };
        host.Hub.Handler = served.HandleAsync;
    }

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

    // #640: apps asked hello again every 10 s to see agent-ha-bridge start or stop. A client that
    // asks is told instead, once for each change, and only that client.
    [Fact]
    public async Task A_client_that_asked_hears_hello_changed_once_for_each_change_and_one_that_did_not_hears_nothing()
    {
        var time = new FakeTimeProvider();
        served = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), answers: Answerer(), replies: Replier())
        {
            Drivers = drivers,
            Time = time,
            ReadOnly = () => probe.Active(),
        };
        host.Hub.Handler = served.HandleAsync;
        var ch = await OpenAsync();
        var other = await client.Hub.OpenAsync(host.Id);
        static string[] Offered(System.Text.Json.JsonElement hello) => [.. hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];

        var asked = (await ch.RequestAsync(Ops.Hello, new { changes = true })).GetProperty("result");
        Assert.Contains(Capabilities.HelloChanged, Offered(asked));
        Assert.Contains(Capabilities.SessionsReply, Offered(asked));
        await other.RequestAsync(Ops.Hello, new { });

        // Nothing changed: nothing is said.
        time.Advance(served.HelloCheck);
        probe.Reason = "agent-ha-bridge is running for this user";
        time.Advance(served.HelloCheck);
        var off = await ch.Events.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.Equal((Events.HelloChanged, 1L), (off.Name, off.Seq));
        var now = (await ch.RequestAsync(Ops.Hello, new { changes = true })).GetProperty("result");
        Assert.DoesNotContain(Capabilities.SessionsReply, Offered(now));
        Assert.Equal(probe.Reason, now.GetProperty("machine").GetProperty("readOnly").GetString());

        // Still running: said once, not at every look.
        time.Advance(served.HelloCheck);
        probe.Reason = null;
        time.Advance(served.HelloCheck);
        var on = await ch.Events.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.Equal((Events.HelloChanged, 2L), (on.Name, on.Seq));
        now = (await ch.RequestAsync(Ops.Hello, new { changes = true })).GetProperty("result");
        Assert.Contains(Capabilities.SessionsReply, Offered(now));
        Assert.False(now.GetProperty("machine").TryGetProperty("readOnly", out _));

        // Asked again with nothing changed since: nothing more is said.
        time.Advance(served.HelloCheck);
        await Task.Delay(200);
        Assert.False(ch.Events.TryRead(out _));
        Assert.False(other.Events.TryRead(out _));
    }

    // #780: what a reboot or a crash took is said in hello, and a client that asked hears when it
    // changes, as a session whose agent's discovery was held is judged later.
    [Fact]
    public async Task Hello_says_what_a_reboot_took_and_a_client_that_asked_hears_when_that_changes()
    {
        var time = new FakeTimeProvider();
        RestoreNotice? notice = null;
        served = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), answers: Answerer(), replies: Replier())
        {
            Drivers = drivers,
            Time = time,
            Restore = () => notice,
        };
        host.Hub.Handler = served.HandleAsync;
        var ch = await OpenAsync();

        var asked = (await ch.RequestAsync(Ops.Hello, new { changes = true })).GetProperty("result");
        Assert.Contains(RestoreProtocol.Capability, asked.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.False(asked.GetProperty("machine").TryGetProperty("restore", out _));

        notice = new RestoreNotice
        {
            At = new DateTimeOffset(2026, 11, 4, 16, 2, 11, TimeSpan.Zero),
            Cause = RestoreCause.Reboot,
            Waiting = 1,
            Why = RestoreWhy.Pending,
            Offered = [new RestoreSession { Id = "s1", Agent = AgentKind.Copilot, Title = "Fix the login", MidTurn = true }],
        };
        time.Advance(served.HelloCheck);
        Assert.Equal(Events.HelloChanged, (await ch.Events.ReadAsync().AsTask().WaitAsync(Patience)).Name);
        var restore = (await ch.RequestAsync(Ops.Hello, new { changes = true })).GetProperty("result").GetProperty("machine").GetProperty("restore");
        Assert.Equal(("reboot", 1, "s1", true), (restore.GetProperty("cause").GetString(), restore.GetProperty("waiting").GetInt32(),
            restore.GetProperty("offered")[0].GetProperty("id").GetString(), restore.GetProperty("offered")[0].GetProperty("midTurn").GetBoolean()));

        // The held session judged: said again.
        notice = notice with { Waiting = 0, Offered = [.. notice.Offered, new RestoreSession { Id = "s2", Agent = AgentKind.Copilot }] };
        time.Advance(served.HelloCheck);
        Assert.Equal(Events.HelloChanged, (await ch.Events.ReadAsync().AsTask().WaitAsync(Patience)).Name);

        // Copilot on #813: only what it says changed, its counts the same (an uncertain session found
        // live, the cause, time and offers rebuilt from the rest): said again too.
        notice = notice with { Cause = RestoreCause.Crash, Since = notice.At.AddMinutes(-3), Offered = [new RestoreSession { Id = "s3", Agent = AgentKind.Claude }, .. notice.Offered.Skip(1)] };
        time.Advance(served.HelloCheck);
        Assert.Equal(Events.HelloChanged, (await ch.Events.ReadAsync().AsTask().WaitAsync(Patience)).Name);
    }

    [Fact]
    public async Task Hello_offers_replying_only_while_agent_ha_bridge_is_not_running_and_only_where_a_replier_can_deliver()
    {
        var ch = await OpenAsync();
        Assert.Contains(Capabilities.SessionsReply, await CapabilitiesAsync(ch));

        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged, Capabilities.SessionsFocus], await CapabilitiesAsync(ch));
        probe.Reason = null;

        Serve(null, new UnsupportedPlatformReplier());
        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged, Capabilities.SessionsFocus], await CapabilitiesAsync(ch));
        Assert.Equal(("refused", "replying is not supported on this platform yet; reply at the machine"), await ReplyAsync(ch, "hello"));
        Serve(null, Replier(platform: false));
        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged, Capabilities.SessionsFocus], await CapabilitiesAsync(ch));
        Assert.Equal("refused", (await ReplyAsync(ch, "hello")).Outcome);
        Serve(null, null);
        Assert.Equal(("refused", "this host does not take replies; reply at the machine"), await ReplyAsync(ch, "hello"));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_burst_of_hellos_looks_for_agent_ha_bridge_once_and_a_reply_still_looks_afresh_before_typing()
    {
        var looks = new CountingProbe(probe);
        var time = new FakeTimeProvider();
        var g5 = new SharedAgentHaBridgeProbe(looks, time);
        var shared = new ReadOnlyGate(g5, null);
        served = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), answers: Answerer(shared), replies: Replier(g5: shared))
        {
            Drivers = drivers,
            G5 = g5,
            ReadOnly = shared.Recent,
        };
        host.Hub.Handler = served.HandleAsync;
        var ch = await OpenAsync();

        for (var i = 0; i < 10; i++) { Assert.Contains(Capabilities.SessionsReply, await CapabilitiesAsync(ch)); }
        Assert.Equal(1, looks.Count);

        // agent-ha-bridge starts within the 2 s a look is kept: a hello still offers replying, but a
        // reply looks again before anything is typed, and the next hello says what it found.
        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.Contains(Capabilities.SessionsReply, await CapabilitiesAsync(ch));
        Assert.Equal(("refused", probe.Reason), await ReplyAsync(ch, "hello"));
        Assert.Empty(injector.Plans);
        Assert.Equal(2, looks.Count);
        Assert.DoesNotContain(Capabilities.SessionsReply, await CapabilitiesAsync(ch));

        probe.Reason = null;
        time.Advance(SharedAgentHaBridgeProbe.Keep);
        Assert.Contains(Capabilities.SessionsReply, await CapabilitiesAsync(ch));
        Assert.Equal(3, looks.Count);
    }

    sealed class CountingProbe(IAgentHaBridgeProbe inner) : IAgentHaBridgeProbe
    {
        public int Count;

        public string? Active()
        {
            Interlocked.Increment(ref Count);
            return inner.Active();
        }
    }

    async Task<DriverKind?> DriverAsync(DriverKind? wanted)
    {
        for (var i = 0; i < 500 && source.Get(Id)?.Driver != wanted; i++) { await Task.Delay(10); }
        return source.Get(Id)?.Driver;
    }

    [Fact]
    public async Task An_answer_or_reply_from_a_device_takes_the_session_back_from_an_agent_and_one_refused_leaves_it_with_the_agent()
    {
        var ch = await OpenAsync();
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, DateTimeOffset.UtcNow, "homeassistant:0f1e2d3c");
        Assert.Equal(DriverKind.Agent, await DriverAsync(DriverKind.Agent));
        Assert.Equal("homeassistant:0f1e2d3c", source.Get(Id)!.DriverPrincipal);

        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.Equal("refused", (await ReplyAsync(ch, "not now")).Outcome);
        probe.Reason = null;
        await source.RefreshAsync();
        Assert.Equal(DriverKind.Agent, source.Get(Id)!.Driver);

        var mine = Guid.NewGuid().ToString();
        Assert.Equal("delivered", (await ReplyAsync(ch, "I'll take it from here", mine)).Outcome);
        Assert.Null(await DriverAsync(null));
        Assert.Null(source.Get(Id)!.DriverPrincipal);

        // The same request again, after an agent has taken the session: the delivery answers it from
        // the outcome it remembers, with nothing typed, so the agent keeps it.
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, DateTimeOffset.UtcNow, "homeassistant:0f1e2d3c");
        Assert.Equal(DriverKind.Agent, await DriverAsync(DriverKind.Agent));
        Assert.Equal("delivered", (await ReplyAsync(ch, "I'll take it from here", mine)).Outcome);
        // However its UUID is spelt: the delivery knows it as the same request.
        Assert.Equal("delivered", (await ReplyAsync(ch, "I'll take it from here", mine.ToUpperInvariant())).Outcome);
        Assert.Single(injector.Plans);
        await Task.Delay(200);
        Assert.Equal(DriverKind.Agent, source.Get(Id)!.Driver);

        // An answer, the same.
        Fixture.Append(events, Ask("q1", second++));
        await source.RefreshAsync();
        Assert.Equal(DriverKind.Agent, source.Get(Id)!.Driver);
        injector.OnInject = _ => Fixture.Append(events, Copilot.Event("human_response.recorded",
            new { toolCallId = "q1", requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content = new { pick = "b" } } }, second++));
        var answered = await ch.RequestAsync(Ops.SessionAnswer, new { id = Id, question = "q1", request = Guid.NewGuid().ToString(), fields = new { pick = "b" } });
        Assert.Equal("delivered", answered.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Null(await DriverAsync(null));
    }

    [Fact]
    public async Task A_persons_reply_confirmed_only_after_an_agent_drove_the_session_meanwhile_leaves_it_with_the_agent()
    {
        var ch = await OpenAsync();
        // The keys go in; before the session records them, an agent's input arrives through the
        // Home Assistant card and arms the session.
        injector.OnInject = plan =>
        {
            drivers.For(Id).ArmDriver(SessionDrivers.Agent, DateTimeOffset.UtcNow, "homeassistant:0f1e2d3c");
            Fixture.Append(events, Prompt(plan.Sends[0].Text));
        };

        Assert.Equal("delivered", (await ReplyAsync(ch, "mine came first")).Outcome);
        await Task.Delay(200);
        await source.RefreshAsync();
        Assert.Equal(DriverKind.Agent, source.Get(Id)!.Driver);
    }

    [Fact]
    public async Task A_resend_waiting_beside_its_first_send_takes_nothing_back_from_an_agent_who_drove_the_session_meanwhile()
    {
        var ch = await OpenAsync();
        var release = new TaskCompletionSource();
        injector.Gate = release.Task;
        var request = Guid.NewGuid().ToString();
        var first = ReplyAsync(ch, "mine", request);
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the reply to start typing", Patience);

        // An agent drives the session while the person's reply is still going in, and the phone,
        // having lost the outcome, sends the same request again.
        drivers.For(Id).ArmDriver(SessionDrivers.Agent, DateTimeOffset.UtcNow, "homeassistant:0f1e2d3c");
        var again = ReplyAsync(ch, "mine", request);
        await Task.Delay(300);
        release.SetResult();

        Assert.Equal("delivered", (await again).Outcome);
        Assert.Equal("delivered", (await first).Outcome);
        Assert.Single(injector.Plans);
        await Task.Delay(200);
        await source.RefreshAsync();
        Assert.Equal(DriverKind.Agent, source.Get(Id)!.Driver);
    }

    [Fact]
    public async Task A_reply_the_transcript_then_records_as_the_persons_prompt_is_delivered_typed_as_one_line_then_Enter()
    {
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "Looks good.\nNow open the PR – please 🚀");

        Assert.Equal(("delivered", (string?)null), (outcome, reason));
        var (pid, plan) = Assert.Single(injector.Plans);
        Assert.Equal(100, pid);
        // The line, then, after Copilot's pause, its Enter alone.
        Assert.Equal([("Looks good. Now open the PR – please 🚀", false, 300), ("", true, 0)], plan.Sends.Select(s => (s.Text, s.Enter, s.Enter ? s.EnterDelayMs : s.PauseAfterMs)));
    }

    [Fact]
    public async Task A_reply_to_a_working_session_is_delivered_once_copilot_takes_it_at_its_next_step()
    {
        Fixture.Append(events, Prompt("next task"), Copilot.TurnStart(second++), Copilot.ToolStart("t1", "powershell", new { command = "dotnet test" }, second++));
        await source.RefreshAsync();
        Assert.Equal(SessionStatus.Working, source.Get(Id)!.Status);
        // While the turn runs, the footer's place holds the turn's own row (#843 review).
        injector.Screen = _ => CopilotWorking;
        // Copilot writes a steering prompt when its next round starts, after the running tool.
        injector.OnInject = plan => _ = Task.Delay(700).ContinueWith(_ => Fixture.Append(events,
            Copilot.ToolDone("t1", second++), Copilot.TurnStart(second++), Prompt(plan.Sends[0].Text, "steering")), TaskScheduler.Default);
        var ch = await OpenAsync();

        Assert.Equal(("delivered", (string?)null), await ReplyAsync(ch, "also run the e2e tests"));
    }

    // ---- progress (sessions.progress) ----

    // The frames a request heard before its result, as sent, and the result.
    static async Task<(List<string> Heard, System.Text.Json.JsonElement Result)> HearAsync(SecureChannel ch, string op, object data)
    {
        var heard = new List<string>();
        var response = await ch.RequestAsync(op, data, progress: p => { lock (heard) { heard.Add(p.GetRawText()); } });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        lock (heard) { return ([.. heard], response.GetProperty("result")); }
    }

    // A transcript line as Copilot writes it now, rather than at the fixture's fixed time, so its
    // timestamp falls after the request.
    static string Now(string line)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(line)!;
        node["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return node.ToJsonString();
    }

    const string Received = """{"stage":"received"}""";

    [Fact]
    public async Task A_reply_without_progress_hears_nothing_before_its_result_and_gets_the_result_it_always_did()
    {
        var ch = await OpenAsync();

        var (heard, result) = await HearAsync(ch, Ops.SessionReply, new { id = Id, request = Guid.NewGuid().ToString(), text = "as before" });

        Assert.Empty(heard);
        Assert.Equal("""{"outcome":"delivered"}""", result.GetRawText());
    }

    [Fact]
    public async Task A_reply_asking_for_progress_hears_received_then_typed_then_its_result_with_each_stages_milliseconds()
    {
        injector.OnInject = plan => Fixture.Append(events, Now(Prompt(plan.Sends[0].Text)));
        var ch = await OpenAsync();
        Assert.Contains(Capabilities.SessionsProgress, await CapabilitiesAsync(ch));

        var (heard, result) = await HearAsync(ch, Ops.SessionReply, new { id = Id, request = Guid.NewGuid().ToString(), text = "go on", progress = true });

        Assert.Equal([Received, """{"stage":"typed","working":false}"""], heard);
        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        var timings = result.GetProperty("timings");
        var typed = timings.GetProperty("typed").GetInt64();
        Assert.InRange(timings.GetProperty("recorded").GetInt64(), 0, 10_000);
        Assert.InRange(timings.GetProperty("confirmed").GetInt64(), typed, 10_000);
    }

    [Fact]
    public async Task A_reply_typed_into_a_working_session_says_so_the_client_can_show_it_queued()
    {
        Fixture.Append(events, Prompt("next task"), Copilot.TurnStart(second++), Copilot.ToolStart("t1", "powershell", new { command = "dotnet test" }, second++));
        await source.RefreshAsync();
        injector.Screen = _ => CopilotWorking;
        injector.OnInject = plan => Fixture.Append(events, Prompt(plan.Sends[0].Text, "steering"));
        var ch = await OpenAsync();

        var (heard, _) = await HearAsync(ch, Ops.SessionReply, new { id = Id, request = Guid.NewGuid().ToString(), text = "and the e2e tests", progress = true });

        Assert.Equal([Received, """{"stage":"typed","working":true}"""], heard);
    }

    [Fact]
    public async Task A_repeat_of_a_reply_still_being_delivered_hears_the_stages_its_first_send_has_reached()
    {
        // Typed, and not yet recorded: the first send waits on the transcript.
        injector.OnInject = null;
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();
        var data = new { id = Id, request, text = "once", progress = true };
        var first = HearAsync(ch, Ops.SessionReply, data);
        await Wait.UntilAsync(() => injector.Plans.Count == 1, "the reply to be typed", Patience);

        var heard = new List<string>();
        var repeat = (await OpenAsync()).RequestAsync(Ops.SessionReply, data, progress: p => { lock (heard) { heard.Add(p.GetRawText()); } });
        await Wait.UntilAsync(() => { lock (heard) { return heard.Count == 2; } }, "the repeat to hear the stages reached", Patience);
        Assert.False(repeat.IsCompleted);
        Fixture.Append(events, Prompt("once"));

        var answer = await repeat;
        Assert.Equal("delivered", answer.GetProperty("result").GetProperty("outcome").GetString());
        Assert.Equal([Received, """{"stage":"typed","working":false}"""], heard);
        // Its timings count from its own arrival: typed before it came.
        Assert.Equal(0, answer.GetProperty("result").GetProperty("timings").GetProperty("typed").GetInt64());
        Assert.Equal([Received, """{"stage":"typed","working":false}"""], (await first).Heard);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task An_answer_asking_for_progress_hears_received_then_typed_then_its_result_with_timings()
    {
        Fixture.Append(events, Ask("q1", second++));
        await source.RefreshAsync();
        injector.OnInject = _ => Fixture.Append(events, Now(Copilot.Event("human_response.recorded",
            new { toolCallId = "q1", requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content = new { pick = "b" } } }, second++)));
        var ch = await OpenAsync();

        var (heard, result) = await HearAsync(ch, Ops.SessionAnswer, new { id = Id, question = "q1", request = Guid.NewGuid().ToString(), fields = new { pick = "b" }, progress = true });

        Assert.Equal([Received, """{"stage":"typed","working":false}"""], heard);
        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("timings").TryGetProperty("recorded", out _));
        Assert.True(result.GetProperty("timings").TryGetProperty("confirmed", out _));
    }

    [Fact]
    public async Task Each_reply_logs_one_line_of_its_stages_in_milliseconds_after_receipt_once_its_result_has_gone()
    {
        var logged = new List<string>();
        Serve(Answerer(), new ReplyService(source, f.Processes, probe, new NoGuard(), injector, new ReplyOptions
        {
            CopilotHome = f.CopilotHome,
            PlatformSupported = true,
            ConfirmTimeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(25),
        }, m => { lock (logged) { logged.Add(m); } }, gate));
        injector.OnInject = plan => Fixture.Append(events, Now(Prompt(plan.Sends[0].Text)));
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Assert.Equal("delivered", (await ReplyAsync(ch, "timed", request)).Outcome);
        Assert.Equal("delivered", (await ReplyAsync(ch, "timed", request)).Outcome);

        static bool Timing(string m) => m.Contains(" timings ", StringComparison.Ordinal);
        await Wait.UntilAsync(() => { lock (logged) { return logged.Any(Timing); } }, "the timing line", Patience);
        await Task.Delay(200);
        var line = Assert.Single(logged, Timing);
        var match = Regex.Match(line, $@"^reply {request[..8]} timings \(ms after (?<at>\d{{4}}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d\d\dZ)\): checked \d+, turn \d+, inject \d+-\d+, typed \d+, recorded \d+, confirmed \d+, settled \d+, sent \d+$");
        Assert.True(match.Success, line);
        // UTC, not the machine's local time with a Z on it.
        var at = DateTimeOffset.Parse(match.Groups["at"].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.InRange(at, before, DateTimeOffset.UtcNow);
    }

    // ---- the fast path (#599) ----

    [Fact]
    public async Task A_reply_to_a_session_found_with_a_long_transcript_reads_only_what_was_written_after_it_was_read_ahead()
    {
        // About 2 MB of history from before the host took the session.
        Fixture.Append(events, [.. Enumerable.Range(0, 2000).Select(_ => Copilot.Assistant(new string('x', 1000), second))]);
        await source.RefreshAsync();
        var replier = Replier();
        Serve(Answerer(), replier);
        var length = new FileInfo(events).Length;
        await Wait.UntilAsync(() => replier.InputOf(Id)?.BytesRead == length, "the transcript to be read ahead", Patience);
        var ch = await OpenAsync();

        Assert.Equal("delivered", (await ReplyAsync(ch, "after the history")).Outcome);

        // Only the prompt and what came with it, never the history again.
        Assert.InRange(replier.InputOf(Id)!.BytesRead, length, length + 16 * 1024);
    }

    [Fact]
    public async Task A_write_that_changes_nothing_shown_is_still_read_ahead_once_it_is_heard()
    {
        var writes = new TranscriptWrites();
        var replier = new ReplyService(source, f.Processes, probe, new NoGuard(), injector, new ReplyOptions
        {
            CopilotHome = f.CopilotHome,
            PlatformSupported = true,
            Writes = writes,
        }, gate: gate);
        await Wait.UntilAsync(() => replier.InputOf(Id)?.BytesRead == new FileInfo(events).Length, "the transcript to be read ahead", Patience);

        // No refresh, so no upsert: as for hook and model records, which change nothing shown.
        Fixture.Append(events, [.. Enumerable.Range(0, 100).Select(_ => Copilot.Assistant(new string('x', 1000), second))]);
        writes.Written(events);

        var length = new FileInfo(events).Length;
        await Wait.UntilAsync(() => replier.InputOf(Id)?.BytesRead == length, "the write to be read ahead", Patience);
    }

    // Long enough that only a write can wake a confirmation within the client's 15 s.
    static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_record_written_while_a_reply_waits_confirms_it_at_once_rather_than_at_the_next_poll()
    {
        var writes = new TranscriptWrites();
        Serve(Answerer(), new ReplyService(source, f.Processes, probe, new NoGuard(), injector, new ReplyOptions
        {
            CopilotHome = f.CopilotHome,
            PlatformSupported = true,
            ConfirmTimeout = TimeSpan.FromSeconds(60),
            PollInterval = SlowPoll,
            Writes = writes,
        }, gate: gate));
        // Recorded a moment after the keys, as Copilot records a prompt: after the first look.
        injector.OnInject = plan => _ = Task.Delay(300).ContinueWith(_ =>
        {
            Fixture.Append(events, Now(Prompt(plan.Sends[0].Text)));
            writes.Written(events);
        }, TaskScheduler.Default);
        var ch = await OpenAsync();

        var (_, result) = await HearAsync(ch, Ops.SessionReply, new { id = Id, request = Guid.NewGuid().ToString(), text = "wake up", progress = true });

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        var timings = result.GetProperty("timings");
        Assert.InRange(timings.GetProperty("confirmed").GetInt64() - timings.GetProperty("typed").GetInt64(), 0, 5_000);
    }

    [Fact]
    public async Task An_answer_recorded_while_the_host_waits_is_confirmed_at_once_rather_than_at_the_next_poll()
    {
        Fixture.Append(events, Ask("q1", second++));
        await source.RefreshAsync();
        var writes = new TranscriptWrites();
        Serve(new AnswerService(source, f.Processes, probe, new NoGuard(), injector, new AnswerOptions
        {
            CopilotHome = f.CopilotHome,
            PlatformSupported = true,
            ConfirmTimeout = TimeSpan.FromSeconds(60),
            PollInterval = SlowPoll,
            Writes = writes,
        }, gate: gate), Replier());
        injector.OnInject = plan => _ = Task.Delay(300).ContinueWith(_ =>
        {
            Fixture.Append(events, Now(Copilot.Event("human_response.recorded",
                new { toolCallId = "q1", requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content = new { pick = "b" } } }, second++)),
                Copilot.ToolDone("q1", second++));
            writes.Written(events);
        }, TaskScheduler.Default);
        var ch = await OpenAsync();

        var (_, result) = await HearAsync(ch, Ops.SessionAnswer, new { id = Id, question = "q1", request = Guid.NewGuid().ToString(), fields = new { pick = "b" }, progress = true });

        Assert.Equal("delivered", result.GetProperty("outcome").GetString());
        var timings = result.GetProperty("timings");
        Assert.InRange(timings.GetProperty("confirmed").GetInt64() - timings.GetProperty("typed").GetInt64(), 0, 5_000);
    }

    [Fact]
    public async Task A_prompt_that_is_not_the_reply_or_none_at_all_is_unconfirmed_never_delivered()
    {
        var ch = await OpenAsync();

        injector.OnInject = _ => Fixture.Append(events, Prompt("something typed at the machine"));
        var (outcome, reason) = await ReplyAsync(ch, "carry on");
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("a different message", reason);
        Assert.DoesNotContain("at the machine", reason);

        injector.OnInject = null;
        (outcome, reason) = await ReplyAsync(ch, "carry on");
        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("did not record the reply", reason);
        Assert.DoesNotContain("at the machine", reason);

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

    // A new CLI resumed the session while an approval was open, and wrote nothing of it again: it may
    // still be on the screen, where a reply's Enter would accept it. The reply waits; one typed at the
    // machine since (the input was free) lets replies through again.
    [Fact]
    public async Task A_permission_left_open_when_a_new_cli_resumed_the_session_keeps_a_reply_out_until_the_person_types_at_the_machine()
    {
        var ch = await OpenAsync();
        Fixture.Append(events, Copilot.Event("permission.requested", new { requestId = "r1", permissionRequest = new { kind = "shell", toolCallId = "c9" } }, second++),
            Copilot.Event("session.resume", new { }, second++));
        await source.RefreshAsync();
        Assert.Null(source.Get(Id)!.Question);

        var (outcome, reason) = await ReplyAsync(ch, "carry on");

        Assert.Equal("refused", outcome);
        Assert.StartsWith("the session was resumed in a new Copilot while a question or approval was open", reason);
        Assert.Empty(injector.Plans);
        Fixture.Append(events, Prompt("typed at the machine"), Copilot.TurnStart(second++), Copilot.TurnEnd(second++));
        Assert.Equal("delivered", (await ReplyAsync(ch, "carry on")).Outcome);
    }

    // A session whose launch here has not yet seen its window ready (a restore at folder trust) is
    // not typed into: Enter would answer the question.
    [Fact]
    public async Task A_reply_is_refused_while_the_launch_of_its_session_has_not_seen_its_window_ready()
    {
        var replier = Replier();
        replier.LaunchHeld = id => id == Id ? "Copilot was started here and its window has not been seen ready" : null;
        Serve(Answerer(), replier);
        var ch = await OpenAsync();

        Assert.Equal(("refused", "Copilot was started here and its window has not been seen ready"), await ReplyAsync(ch, "carry on"));
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
    public async Task A_session_with_no_transcript_yet_may_be_asking_whether_to_trust_its_folder_so_a_reply_is_refused_and_says_why()
    {
        const string fresh = "88888888-8888-4888-8888-888888888888";
        var transcript = f.CopilotSession(fresh, 101, name: null);
        f.Processes.Run(AgentKind.Copilot, 101);
        await source.RefreshAsync();
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "hello there", id: fresh);

        Assert.Equal("refused", outcome);
        Assert.StartsWith("Copilot has not started this session's transcript yet", reason);
        Assert.Contains("trust its folder", reason);
        Assert.Empty(injector.Plans);
        Assert.False(File.Exists(transcript));
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

    // #780: Continue is an ordinary reply. Its words earn no trust a reply does not have: beside
    // agent-ha-bridge it is refused like any other, and nothing is typed.
    [Fact]
    public async Task A_reply_with_continues_words_is_refused_beside_agent_ha_bridge_like_any_reply()
    {
        var ch = await OpenAsync();
        probe.Reason = "agent-ha-bridge is running for this user";

        var words = AgentBridge.Host.Core.Restore.RestoreRecorder.ContinueText(RestoreCause.Reboot, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc);

        Assert.Equal(("refused", probe.Reason), await ReplyAsync(ch, words));
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
        Assert.Equal(("refused", "this host does not take attachments; send the text alone, or attach at the machine"), await ReplyAsync(ch, "see this", attachments: new[] { new { id = Guid.NewGuid() } }));
        Assert.Equal(("refused", "request must be a UUID"), await ReplyAsync(ch, "hello", request: "7"));
        // Claude is typed into only where its input box is seen on screen; an injector that
        // cannot read screens types nothing.
        Assert.Equal(("refused", "this host cannot read the session's screen; answer at the machine"), await ReplyAsync(ch, "hello", id: ClaudeId));
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
        Assert.DoesNotContain("at the machine", reason);

        injector.Result = new InjectionResult(InjectionStatus.NothingTyped, "the session runs as administrator");
        Assert.Equal(("refused", "the session runs as administrator"), await ReplyAsync(ch, "hello again"));
    }

    // ---- attachments ----

    string Staging => Path.Combine(f.Root, "profile", "attachments");

    AttachmentStore Store(IBlobSource? blobs = null) => new(new AttachmentOptions { StagingRoot = Staging }, sweep: false, blobs: blobs);

    // Serves replies with attachments, and plays the CLI as it records a prompt with files in
    // it: one attachment per @<path> it could read.
    AttachmentStore ServeAttachments(IBlobSource? blobs = null)
    {
        var store = Store(blobs);
        Serve(Answerer(), new ReplyService(source, f.Processes, probe, new NoGuard(), injector, new ReplyOptions
        {
            CopilotHome = f.CopilotHome,
            PlatformSupported = true,
            ConfirmTimeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(25),
        }, gate: gate, attachments: store), store);
        injector.OnInject = plan => Fixture.Append(events, WithFiles(plan.Sends[0].Text));
        return store;
    }

    string WithFiles(string text, bool attach = true) => Copilot.Event("user.message", new
    {
        content = text,
        attachments = attach
            ? text.Split(' ').Where(w => w.StartsWith('@') && File.Exists(w[1..])).Select((w, i) => new { type = "file", path = w[1..], displayName = w, mentionIndex = i }).ToArray()
            : [],
        delivery = "idle",
    }, second++);

    static byte[] Png(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    static Task<string> UploadAsync(SecureChannel ch, byte[] bytes, string name = "shot.png") => UploadAsync((op, data) => ch.RequestAsync(op, data), bytes, name);

    // As a client sends one: start, then one chunk at a time from where the host is.
    static async Task<string> UploadAsync(Func<string, object, Task<System.Text.Json.JsonElement>> request, byte[] bytes, string name = "shot.png")
    {
        var id = Guid.NewGuid().ToString();
        var state = Ok(await request(Ops.AttachmentStart, new { attachment = id, name, type = "image/png", size = bytes.Length, sha256 = Attachments.Base64Url(System.Security.Cryptography.SHA256.HashData(bytes)) }));
        while (!state.GetProperty("complete").GetBoolean())
        {
            var at = state.GetProperty("received").GetInt32();
            var n = Math.Min(AttachmentLimits.Chunk, bytes.Length - at);
            state = Ok(await request(Ops.AttachmentChunk, new { attachment = id, offset = at, data = Convert.ToBase64String(bytes, at, n) }));
        }
        return id;
    }

    static System.Text.Json.JsonElement Ok(System.Text.Json.JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static object Files(params string[] ids) => ids.Select(id => new { id }).ToArray();

    [Fact]
    public async Task Hello_offers_attachments_with_replies_only_on_a_host_that_has_somewhere_to_stage_them()
    {
        ServeAttachments();
        var ch = await OpenAsync();
        var offered = await CapabilitiesAsync(ch);
        Assert.Contains(Capabilities.SessionsReply, offered);
        Assert.Contains(Capabilities.SessionsAttach, offered);

        probe.Reason = "agent-ha-bridge is running for this user";
        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged, Capabilities.SessionsFocus], await CapabilitiesAsync(ch));
        probe.Reason = null;

        // Without a store the ops are unknown, as on a host that predates them.
        Serve(Answerer(), Replier());
        Assert.DoesNotContain(Capabilities.SessionsAttach, await CapabilitiesAsync(ch));
        var unknown = await ch.RequestAsync(Ops.AttachmentStart, new { attachment = Guid.NewGuid().ToString(), name = "a.png", size = 1, sha256 = new string('A', 43) });
        Assert.Equal("unknown op attachment.start", unknown.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_reply_with_files_sent_first_mentions_each_ahead_of_the_words_and_is_delivered_once_copilot_attaches_them()
    {
        using var store = ServeAttachments();
        var ch = await OpenAsync();
        var shot = Png(AttachmentLimits.Bytes);
        var notes = "# plan\n"u8.ToArray();
        var a = await UploadAsync(ch, shot, "Screen Shot.png");
        var b = await UploadAsync(ch, notes, "plan.md");

        var (outcome, reason) = await ReplyAsync(ch, "What do\nyou see?", attachments: Files(a, b));

        Assert.Equal(("delivered", (string?)null), (outcome, reason));
        var typed = Assert.Single(injector.Plans).Plan.Sends[0].Text;
        var root = Regex.Escape(Path.GetFullPath(Staging) + Path.DirectorySeparatorChar);
        Assert.Matches($"^@{root}[0-9a-f]{{32}}-Screen-Shot\\.png @{root}[0-9a-f]{{32}}-plan\\.md What do you see\\?$", typed);
        var paths = typed.Split(' ').Take(2).Select(w => w[1..]).ToArray();
        Assert.Equal(shot, File.ReadAllBytes(paths[0]));
        Assert.Equal(notes, File.ReadAllBytes(paths[1]));
        // The files are the agent's now: gone from the host's memory, and not reusable.
        Assert.Equal(0, store.Count);
        Assert.Contains("went with an earlier reply", (await ReplyAsync(ch, "again", attachments: Files(a))).Reason);
    }

    [Fact]
    public async Task Files_alone_are_a_reply_and_one_copilot_did_not_attach_is_unconfirmed()
    {
        ServeAttachments();
        var ch = await OpenAsync();
        injector.OnInject = plan => Fixture.Append(events, WithFiles(plan.Sends[0].Text, attach: false));

        var (outcome, reason) = await ReplyAsync(ch, "", attachments: Files(await UploadAsync(ch, Png(64))));

        Assert.Equal("unconfirmed", outcome);
        Assert.Contains("did not attach every file", reason);
        Assert.DoesNotContain("at the machine", reason);
        Assert.StartsWith("@", Assert.Single(injector.Plans).Plan.Sends[0].Text);
    }

    [Fact]
    public async Task A_reply_that_types_nothing_deletes_its_staged_copies_and_keeps_its_files_for_the_next_try()
    {
        using var store = ServeAttachments();
        Fixture.Append(events, Ask("q1", second++));
        var ch = await OpenAsync();
        var id = await UploadAsync(ch, Png(100));

        Assert.Equal("refused", (await ReplyAsync(ch, "look", attachments: Files(id))).Outcome);
        // The question was only in the transcript, so the files had been staged; now they are not.
        Assert.Empty(Directory.Exists(Staging) ? Directory.GetFiles(Staging) : []);
        Assert.Equal(1, store.Count);

        Fixture.Append(events, Copilot.Answered("q1", second++), Copilot.ToolDone("q1", second++));
        await source.RefreshAsync();
        Assert.Equal("delivered", (await ReplyAsync(ch, "look", attachments: Files(id))).Outcome);
        Assert.Single(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task Files_are_the_sending_devices_alone_and_bad_ones_fail_as_requests()
    {
        ServeAttachments();
        var ch = await OpenAsync();
        var id = await UploadAsync(ch, Png(100));
        await using var laptop = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "Laptop", "pwa");
        await laptop.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        var other = await laptop.Hub.OpenAsync(host.Id);

        var (outcome, reason) = await ReplyAsync(other, "mine now", attachments: Files(id));
        Assert.Equal("refused", outcome);
        Assert.Contains("is not on the host", reason);

        var bytes = Png(100);
        var badId = Guid.NewGuid().ToString();
        Ok(await ch.RequestAsync(Ops.AttachmentStart, new { attachment = badId, name = "a.png", size = 100, sha256 = Attachments.Base64Url(System.Security.Cryptography.SHA256.HashData(Png(100))) }));
        var bad = await ch.RequestAsync(Ops.AttachmentChunk, new { attachment = badId, offset = 0, data = Convert.ToBase64String(bytes) });
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Contains("do not match its sha256", bad.GetProperty("error").GetString());
        var big = await ch.RequestAsync(Ops.AttachmentStart, new { attachment = Guid.NewGuid().ToString(), name = "big.bin", size = AttachmentLimits.Bytes + 1, sha256 = new string('A', 43) });
        Assert.Equal("the attachment is larger than 256 KiB", big.GetProperty("error").GetString());
        Assert.Empty(injector.Plans);
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
        // Each session's screen by its process: Copilot's (pid 100) ready for a prompt, as captured
        // live; null, a host that cannot read it, for anything else.
        public Func<int, IReadOnlyList<string>?> Screen = pid => pid == 100 ? CopilotIdle : null;
        // Runs once a plan has the typing slot, before the worker reads the screen.
        public Action? Holding;
        // Plans the worker refused before their first key: held to rows the screen no longer read.
        public readonly List<KeyPlan> Refused = [];

        // Runs after each send has gone, with its index.
        public Action<int>? Sent;

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Plans) { Plans.Add((processId, plan)); }
            await Gate;
            Holding?.Invoke();
            // As the worker does: the rows each send is held to, read in the same attach, just
            // before its first key; nothing of it, or after it, typed if they read otherwise.
            for (var i = 0; i < plan.Sends.Count; i++)
            {
                var send = plan.Sends[i];
                if ((send.Expect is { Count: > 0 } || send.Checks is { Count: > 0 })
                    && (Screen(processId) is not { } rows
                        || (send.Expect ?? []).Any(e => e.Row >= rows.Count || rows[e.Row].TrimEnd(' ') != e.Text.TrimEnd(' '))
                        || (send.Checks ?? []).Any(c => !c.Passes(rows))))
                {
                    if (i > 0) { return new InjectionResult(InjectionStatus.Interrupted, AgentBridge.Host.Delivery.Windows.WorkerInjector.ScreenChangedPartway); }
                    lock (Plans) { Plans.RemoveAt(Plans.FindLastIndex(p => ReferenceEquals(p.Plan, plan))); Refused.Add(plan); }
                    return new InjectionResult(InjectionStatus.NothingTyped, AgentBridge.Host.Delivery.Windows.WorkerInjector.ScreenChanged);
                }
                Sent?.Invoke(i);
            }
            if (Result.Status == InjectionStatus.Typed) { OnInject?.Invoke(plan); }
            return Result;
        }

        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default)
        {
            var rows = Screen(processId);
            return Task.FromResult(new ConsoleScreen(rows?.Select(Worker.Read).ToList(), rows is null ? "this host cannot read the session's screen; answer at the machine" : null));
        }
    }

    // Copilot CLI 1.0.105 ready for a prompt, as captured live (LaunchFlowTests.CopilotReady):
    // its rail input box, empty, and the prompt's footer.
    static readonly IReadOnlyList<string> CopilotIdle = ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580", " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab"];

    // The same box while a turn runs: in the footer's place, the turn's own row (1.0.105's `LMe`
    // activity layout: spinner, Esc's hint, the enqueue hint), with no commands hint.
    // Drawn 80 columns wide: below that the turn's row leaves out the vim and mode labels.
    static readonly IReadOnlyList<string> CopilotWorking = Working(80, " \u25D0 Working esc interrupt \u00B7 ctrl+q enqueue");

    static IReadOnlyList<string> Working(int width, string row, string line = "") =>
        ["\u25CF Running the tests.", "", "\u257B" + new string('\u2584', width - 1), line.Length == 0 ? "\u2503" : $"\u2503 {line}", "\u2579" + new string('\u2580', width - 1), row];

    [Fact]
    public async Task A_reply_to_a_working_Copilot_in_a_window_under_80_columns_is_refused_and_nothing_is_typed()
    {
        // There the turn's row shows no shell or vim label, and a box holding only spaces in
        // shell mode draws as empty: the reply could run as a shell command.
        Fixture.Append(events, Prompt("next task"), Copilot.TurnStart(second++), Copilot.ToolStart("t1", "powershell", new { command = "dotnet test" }, second++));
        await source.RefreshAsync();
        injector.Screen = _ => Working(79, " \u25D0 Working esc interrupt \u00B7 ctrl+q enqueue");
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "also run the e2e tests");

        Assert.Equal("refused", outcome);
        Assert.Contains("reply once the turn ends", reason);
        Assert.Empty(injector.Plans);
    }

    [Theory]
    // In the turn's row: a draft, shell mode's label, or vim's normal mode, as in the idle footer.
    [InlineData("draft")]
    [InlineData("shell")]
    [InlineData("vim")]
    public async Task A_reply_to_a_working_Copilot_is_refused_for_a_draft_shell_mode_or_vim_normal_mode_as_when_idle(string state)
    {
        Fixture.Append(events, Prompt("next task"), Copilot.TurnStart(second++), Copilot.ToolStart("t1", "powershell", new { command = "dotnet test" }, second++));
        await source.RefreshAsync();
        injector.Screen = _ => state switch
        {
            "draft" => Working(80, CopilotWorking[5], "half a thought"),
            "shell" => Working(80, " Shell \u00B7 Allow All \u00B7 \u25D0 Working esc interrupt \u00B7 ctrl+q enqueue", "Execute shell command"),
            _ => Working(80, " -- NORMAL -- \u00B7 \u25D0 Working esc interrupt \u00B7 ctrl+q enqueue"),
        };
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "also run the e2e tests");

        Assert.Equal("refused", outcome);
        Assert.Contains(state switch { "draft" => "already typed", "shell" => "shell mode", _ => "vim normal mode" }, reason);
        Assert.Empty(injector.Plans);
    }

    // The same screen with words in the box (a person's draft), or in shell mode.
    // Its footer as Copilot draws it then (oDo): with text in the box, the mode label and, out
    // of shell mode, the file and issue hints; empty in shell mode, the sidebar, commands and
    // shell hints too.
    static IReadOnlyList<string> CopilotBox(string line, bool shell = false) =>
        [.. CopilotIdle.Take(3), $"\u2503 {line}", CopilotIdle[4],
            shell ? line == "Execute shell command" ? " \u2190 open sidebar \u00B7 Shell \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 esc exit shell mode" : " Shell \u00B7 Allow All"
            : " Interactive \u00B7 Allow All \u00B7 @ files \u00B7 # issues"];

    // ---- Copilot's input box, read before a reply is typed (#825) ----

    [Fact]
    public async Task A_reply_to_Copilot_whose_input_box_holds_a_draft_is_refused_with_the_draft_shown_and_nothing_typed()
    {
        injector.Screen = _ => CopilotBox("half a thought \u202E");
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        Assert.Equal("refused", outcome);
        Assert.Equal("something is already typed in Copilot's input box at the machine (16 characters: \"half a thought \\u202E\"); send or clear it there first", reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_reply_to_Copilot_in_shell_mode_is_refused_and_nothing_is_typed()
    {
        // In shell mode Enter runs what the box holds as a shell command.
        injector.Screen = _ => CopilotBox("Execute shell command", shell: true);
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "rm -rf ~");

        Assert.Equal("refused", outcome);
        Assert.Contains("shell mode", reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_reply_to_Copilot_whose_screen_cannot_be_read_is_refused_and_nothing_is_typed()
    {
        injector.Screen = _ => null;
        var ch = await OpenAsync();

        Assert.Equal(("refused", "this host cannot read the session's screen; answer at the machine"), await ReplyAsync(ch, "go on"));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_reply_to_Copilot_with_its_input_box_empty_is_typed_held_to_that_box_and_delivered()
    {
        injector.OnInject = plan => Fixture.Append(events, Prompt(plan.Sends[0].Text));
        var ch = await OpenAsync();

        Assert.Equal("delivered", (await ReplyAsync(ch, "go on")).Outcome);
        var sends = Assert.Single(injector.Plans).Plan.Sends;
        Assert.Equal(2, sends.Count);
        Assert.Equal(["\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580"], sends[0].Expect!.Select(r => r.Text));
        // Its Enter alone, held to the box's bottom edge.
        Assert.Equal(("", true), (sends[1].Text, sends[1].Enter));
        Assert.Equal([(4, "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580")], sends[1].Expect!.Select(r => (r.Row, r.Text)));
    }

    [Fact]
    public async Task A_Copilot_input_box_that_moves_after_the_reply_is_typed_gets_no_Enter_and_the_reply_is_left_in_it_unconfirmed()
    {
        // The words went, and before their Enter a dialog took the input area's place: Enter
        // would pick what it highlights.
        injector.Sent = i => { if (i == 0) { injector.Screen = _ => ["\u25CF Done.", "", "Allow Copilot to run this command?", " \u276F 1. Yes", "   2. No (Esc)"]; } };
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        Assert.Equal("unconfirmed", outcome);
        Assert.Equal("the reply was typed into Copilot's input box, but the box changed before its Enter, so it was not sent and its words are still in the box", reason);
        Assert.Single(injector.Plans);
        Assert.Empty(injector.Refused);
    }

    [Theory]
    // Rows printed by the agent that look like Copilot's box and footer (code is drawn as plain
    // text) are not its box: above the real one holding a draft, or above a dialog in its place.
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_box_the_agent_printed_is_not_taken_for_Copilots_input_box(bool realBoxBelow)
    {
        List<string> forged = ["\u25CF Here is the prompt:", .. CopilotIdle.Skip(2), ""];
        injector.Screen = _ => realBoxBelow
            ? [.. forged, "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", "\u276F half a thought", "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", " Interactive \u00B7 Allow All \u00B7 @ files \u00B7 # issues"]
            : [.. forged, "Allow Copilot to run this command?", " \u276F 1. Yes", "   2. No (Esc)"];
        var ch = await OpenAsync();

        var (outcome, _) = await ReplyAsync(ch, "go on");

        Assert.Equal("refused", outcome);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_reply_to_Copilot_in_vim_normal_mode_is_refused_and_nothing_is_typed()
    {
        // Keys typed there are vim commands, and a paste is swallowed.
        injector.Screen = _ => [.. CopilotIdle.Take(5), " -- NORMAL -- \u00B7" + CopilotIdle[5]];
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        Assert.Equal("refused", outcome);
        Assert.Contains("vim normal mode", reason);
        Assert.Empty(injector.Plans);
    }

    const string InsertFooter = " -- INSERT -- \u00B7 \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help \u00B7 tab next tab";

    [Theory]
    // Only the footer changes after the host read the box, the box's rows reading the same: Esc
    // into vim's normal mode; '!' then a space, shell mode holding a space (drawn as an empty
    // box); or the footer gone. The worker reads the footer again in the same attach, before
    // the first key, and types nothing (#843 review).
    [InlineData("vim")]
    [InlineData("shell")]
    [InlineData("gone")]
    public async Task A_footer_that_changes_after_the_box_was_read_gets_none_of_the_reply(string change)
    {
        injector.Screen = _ => change == "vim" ? [.. CopilotIdle.Take(5), InsertFooter] : CopilotIdle;
        injector.Holding = () => injector.Screen = _ => change switch
        {
            "vim" => [.. CopilotIdle.Take(5), InsertFooter.Replace("INSERT", "NORMAL")],
            "shell" => [.. CopilotIdle.Take(3), "\u2503 ", CopilotIdle[4], " Shell \u00B7 Allow All"],
            _ => [.. CopilotIdle.Take(5)],
        };
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        Assert.Equal("refused", outcome);
        Assert.Contains("input box changed just as the reply was to be typed", reason);
        Assert.Empty(injector.Plans);
        Assert.Single(injector.Refused);
    }

    [Theory]
    // Read idle, then a turn starts on its own and the person types '!' then a space: shell mode
    // holding a space, drawn as an empty box, under the turn's row. Under 80 columns that row
    // leaves out the mode label (`LMe`), so nothing in it says shell mode, and the worker
    // refuses a turn's row there outright, as the reader does one running at the read; at 80 it
    // would show the label, and a row without one is safe (Trust's #843 re-review).
    [InlineData(70, false)]
    [InlineData(80, true)]
    public async Task A_turn_that_starts_after_the_read_in_a_narrow_window_gets_none_of_the_reply(int width, bool typed)
    {
        const string Idle = " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help";
        injector.Screen = _ => Working(width, Idle);
        injector.Holding = () => injector.Screen = _ => Working(width, " \u25D0 Working esc interrupt \u00B7 ctrl+q enqueue", " ");
        injector.OnInject = plan => Fixture.Append(events, Prompt(plan.Sends[0].Text));
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        if (typed)
        {
            Assert.Equal("delivered", outcome);
            return;
        }
        Assert.Equal("refused", outcome);
        Assert.Contains("input box changed just as the reply was to be typed", reason);
        Assert.Empty(injector.Plans);
    }

    // A screen from the captured Copilot input box fixtures (tests/fixtures/screens).
    static IReadOnlyList<string> Captured(string name)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "screens", "copilot-input-box.json")));
        return [.. doc.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name).GetProperty("rows").EnumerateArray().Select(r => r.GetString()!)];
    }

    [Theory]
    // Read as the empty box at the bottom, then, its rows and footer unchanged, the screen grows
    // rows under them: the agent printed rows that read like the box and its footer, and the
    // real box (holding a draft) or a permission dialog is drawn below. The worker holds every
    // row under the footer blank, as the reader did, up to each key: none of the reply goes, or
    // no Enter (#843 review, Copilot 5780796076).
    [InlineData("forged-rail-over-dialog", false)]
    [InlineData("forged-rail-above-plain-draft", false)]
    [InlineData("forged-rail-over-dialog", true)]
    [InlineData("forged-rail-above-plain-draft", true)]
    public async Task A_screen_that_grows_rows_under_the_footer_after_the_read_gets_none_of_the_reply_or_no_Enter(string grown, bool afterWords)
    {
        injector.Screen = _ => Captured("empty-interactive");
        if (afterWords) { injector.Sent = i => { if (i == 0) { injector.Screen = _ => Captured(grown); } }; }
        else { injector.Holding = () => injector.Screen = _ => Captured(grown); }
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        if (afterWords)
        {
            Assert.Equal("unconfirmed", outcome);
            Assert.StartsWith("the reply was typed into Copilot's input box, but the box changed before its Enter", reason);
            Assert.False(Assert.Single(injector.Plans).Plan.Sends[0].Enter);
            return;
        }
        Assert.Equal("refused", outcome);
        Assert.Contains("input box changed just as the reply was to be typed", reason);
        Assert.Empty(injector.Plans);
    }

    [Theory]
    // The same, a turn starting after the words went: Home then '!' puts the box in shell mode
    // and takes the '!' away, so only the footer changes, and under 80 columns the turn's row
    // says nothing of the mode. No Enter there; at 80 the row would show the label, and the
    // Enter goes (#843 review, Copilot 4477100508).
    [InlineData(70, false)]
    [InlineData(80, true)]
    public async Task A_turn_that_starts_before_the_Enter_in_a_narrow_window_gets_no_Enter(int width, bool entered)
    {
        const string Idle = " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help";
        injector.Screen = _ => Working(width, Idle);
        injector.Sent = i => { if (i == 0) { injector.Screen = _ => Working(width, " \u25D0 Working esc interrupt \u00B7 ctrl+q enqueue", "go on"); } };
        injector.OnInject = plan => Fixture.Append(events, Prompt(plan.Sends[0].Text));
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        if (entered)
        {
            Assert.Equal("delivered", outcome);
            return;
        }
        Assert.Equal("unconfirmed", outcome);
        Assert.StartsWith("the reply was typed into Copilot's input box, but the box changed before its Enter", reason);
        Assert.False(Assert.Single(injector.Plans).Plan.Sends[0].Enter);
    }

    [Theory]
    // A box holding only Unicode spaces (no-break, figure, narrow no-break, ideographic) holds a
    // draft. Trimmed with the box's padding it read as empty, and a working box needs no
    // "/ commands" to be taken for empty, so the reply was added to the draft. Idle, the footer
    // drops "/ commands" for any text at all (`gR`: text.length === 0), so it was refused there
    // already, though not as a draft (#843 review, Copilot 4477100469).
    [InlineData("\u00A0", true)]
    [InlineData("\u2007", true)]
    [InlineData("\u202F", true)]
    [InlineData("\u3000", true)]
    [InlineData("\u00A0", false)]
    [InlineData("\u3000", false)]
    public async Task A_box_holding_only_Unicode_spaces_is_a_draft_and_gets_none_of_the_reply(string space, bool working)
    {
        injector.Screen = _ => working
            ? Working(80, " \u25D0 Working esc interrupt \u00B7 ctrl+q enqueue", space)
            : [.. CopilotIdle.Take(3), $"\u2503 {space}", CopilotIdle[4], " Interactive \u00B7 Allow All \u00B7 @ files \u00B7 # issues"];
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        Assert.Equal("refused", outcome);
        Assert.StartsWith("something is already typed in Copilot's input box at the machine", reason);
        Assert.Empty(injector.Plans);
    }

    [Theory]
    // The words went, and before their Enter: Esc into vim's normal mode; Home then '!', shell
    // mode, its label in the footer; the frame-off box's glyph turning to '!'; or the footer
    // gone. No Enter, and the words are left in the box (#843 review).
    [InlineData("vim")]
    [InlineData("shell")]
    [InlineData("bang")]
    [InlineData("gone")]
    public async Task A_footer_or_box_that_changes_before_the_Enter_gets_no_Enter(string change)
    {
        IReadOnlyList<string> plain = ["\u25CF Done.", "", "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", "\u276F", "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500", CopilotIdle[5]];
        const string Typed = " Interactive \u00B7 Allow All \u00B7 @ files \u00B7 # issues";
        injector.Screen = _ => change == "bang" ? plain : CopilotIdle;
        injector.Sent = i =>
        {
            if (i != 0) { return; }
            injector.Screen = _ => change switch
            {
                "vim" => [.. CopilotIdle.Take(3), "\u2503 go on", CopilotIdle[4], " -- NORMAL -- \u00B7" + Typed],
                "shell" => [.. CopilotIdle.Take(3), "\u2503 go on", CopilotIdle[4], " Shell \u00B7 Allow All"],
                "bang" => [.. plain.Take(3), "! go on", plain[4], Typed],
                _ => [.. CopilotIdle.Take(3), "\u2503 go on", CopilotIdle[4]],
            };
        };
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        Assert.Equal("unconfirmed", outcome);
        Assert.StartsWith("the reply was typed into Copilot's input box, but the box changed before its Enter", reason);
        Assert.False(Assert.Single(injector.Plans).Plan.Sends[0].Enter);
    }

    [Fact]
    public async Task The_words_and_Enter_go_when_only_the_footers_other_parts_change()
    {
        // The words change the footer to its with-text hints, and the turn's spinner moves: no
        // mode in it, so both go.
        injector.OnInject = plan => Fixture.Append(events, Prompt(plan.Sends[0].Text));
        injector.Sent = i => { if (i == 0) { injector.Screen = _ => [.. CopilotIdle.Take(3), "\u2503 go on", CopilotIdle[4], " Interactive \u00B7 Allow All \u00B7 @ files \u00B7 # issues"]; } };
        var ch = await OpenAsync();

        Assert.Equal("delivered", (await ReplyAsync(ch, "go on")).Outcome);
    }

    [Fact]
    public async Task A_draft_shown_to_the_person_in_the_refusal_never_reaches_the_hosts_log()
    {
        const string Secret = "ghp_SENTINEL0123456789";
        var logged = new List<string>();
        Serve(Answerer(), new ReplyService(source, f.Processes, probe, new NoGuard(), injector, new ReplyOptions
        {
            CopilotHome = f.CopilotHome,
            PlatformSupported = true,
        }, m => { lock (logged) { logged.Add(m); } }, gate));
        injector.Screen = _ => CopilotBox($"token {Secret}");
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        // The person's own client is shown it; the host's log says its length only.
        Assert.Equal("refused", outcome);
        Assert.Equal($"something is already typed in Copilot's input box at the machine (28 characters: \"token {Secret}\"); send or clear it there first", reason);
        await Wait.UntilAsync(() => { lock (logged) { return logged.Any(m => m.StartsWith("reply ", StringComparison.Ordinal) && m.Contains("refused", StringComparison.Ordinal)); } }, "the outcome line", Patience);
        lock (logged)
        {
            Assert.DoesNotContain(logged, m => m.Contains(Secret, StringComparison.Ordinal));
            Assert.Contains(logged, m => m.Contains("(28 characters, not shown here)", StringComparison.Ordinal));
        }
    }

    [Theory]
    // Words typed at the machine, or shell mode entered, after the host read the box and before
    // the first key: the worker reads the box again in the same attach, and types nothing.
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Copilot_input_box_that_changes_after_it_was_read_gets_none_of_the_reply(bool shell)
    {
        injector.Holding = () => injector.Screen = _ => shell ? CopilotBox("Execute shell command", shell: true) : CopilotBox("typed at the machine");
        var ch = await OpenAsync();

        var (outcome, reason) = await ReplyAsync(ch, "go on");

        Assert.Equal("refused", outcome);
        Assert.Contains("input box changed just as the reply was to be typed", reason);
        Assert.Empty(injector.Plans);
        Assert.Single(injector.Refused);
    }

    // ---- large files, through the relay's blob store ----

    RelayBlobs HostBlobs()
    {
        var blobs = new RelayBlobs();
        blobs.Attach(host.Relay);
        return blobs;
    }

    // As the phone sends one: sealed and uploaded for the host, then named to it in the channel
    // with its key, and watched with attachment.start until the host has it.
    async Task<string> SendLargeAsync(SecureChannel ch, byte[] bytes, string name, byte[]? key = null)
    {
        using var http = BlobHttp.CreateClient();
        var sent = await BlobTransfer.SendAsync(client.Relay, http, host.Id, bytes);
        var id = Guid.NewGuid().ToString();
        var start = new
        {
            attachment = id, name, type = "image/png", size = bytes.Length, sha256 = Attachments.Base64Url(sent.Sha256),
            blob = new { id = sent.Blob, key = Attachments.Base64Url(key ?? sent.Key) },
        };
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var response = await ch.RequestAsync(Ops.AttachmentStart, start);
            if (!response.GetProperty("ok").GetBoolean()) { throw new InvalidOperationException(response.GetProperty("error").GetString()); }
            if (response.GetProperty("result").GetProperty("complete").GetBoolean()) { return id; }
            if (DateTime.UtcNow > deadline) { throw new TimeoutException("the host never had the whole file"); }
            await Task.Delay(20);
        }
    }

    LocalBlobBackend RelayStore => (LocalBlobBackend)relay.App.Services.GetRequiredService<AgentBridge.Relay.BlobService>().Backend!;

    [Fact]
    public async Task Hello_offers_large_attachments_only_through_a_relay_with_a_blob_store()
    {
        using var blobs = HostBlobs();
        using var store = ServeAttachments(blobs);
        var ch = await OpenAsync();
        Assert.Contains(Capabilities.SessionsAttachBlob, await CapabilitiesAsync(ch));

        // Between relay connections the host can reach no blob store.
        blobs.Attach(null);
        var offered = await CapabilitiesAsync(ch);
        Assert.Contains(Capabilities.SessionsAttach, offered);
        Assert.DoesNotContain(Capabilities.SessionsAttachBlob, offered);
    }

    [Fact]
    public async Task A_photo_too_large_for_the_channel_comes_through_the_blob_store_and_is_handed_to_copilot_like_any_attachment()
    {
        using var blobs = HostBlobs();
        using var store = ServeAttachments(blobs);
        var ch = await OpenAsync();
        var photo = Png(4 * 1024 * 1024 + 123);

        var a = await SendLargeAsync(ch, photo, "IMG_4021.png");
        var (outcome, reason) = await ReplyAsync(ch, "What is in this photo?", attachments: Files(a));

        Assert.Equal("delivered", outcome);
        Assert.Null(reason);
        var said = Assert.Single(injector.Plans).Plan.Sends[0].Text.Split(' ');
        Assert.Matches(@"[\\/][0-9a-f]{32}-IMG_4021\.png$", said[0]);
        Assert.Equal(photo, File.ReadAllBytes(said[0][1..]));
        Assert.Equal("What is in this photo?", string.Join(' ', said[1..]));
        // The host deleted the blob once it had checked it, and kept nothing in incoming/.
        Assert.Empty(RelayStore.Stored);
        Assert.Empty(Directory.GetFiles(store.IncomingRoot));
    }

    [Fact]
    public async Task A_blob_the_host_cannot_open_is_refused_with_the_reason_and_nothing_is_typed()
    {
        using var blobs = HostBlobs();
        using var store = ServeAttachments(blobs);
        var ch = await OpenAsync();

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => SendLargeAsync(ch, Png(300_000), "shot.png", key: new byte[32]));

        Assert.StartsWith("the attachment in the blob store is not what was sent (a part of the blob was changed", e.Message);
        Assert.Empty(injector.Plans);
        await Wait.UntilAsync(() => RelayStore.Stored.Count == 0, "the unusable blob to be deleted");
    }

    [Fact]
    public async Task The_app_on_this_machine_sends_a_file_over_256_KiB_in_chunks_over_its_pipe_and_a_device_through_the_relay_cannot()
    {
        using var store = ServeAttachments();
        // Short: a Unix socket path must fit about 100 bytes.
        var profile = Path.Combine(Path.GetTempPath(), "abr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);
        try
        {
            await using var server = AgentBridge.Local.LocalServer.Start(AgentBridge.Local.LocalEndpoint.For(profile), served.HandleAsync);
            await using var app = await AgentBridge.Local.LocalClient.ConnectAsync(AgentBridge.Local.LocalEndpoint.For(profile), Patience);
            var hello = Ok(await app.RequestAsync(Ops.Hello, new { }));
            var offered = hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToArray();
            Assert.Contains(Capabilities.SessionsAttachDirect, offered);
            Assert.DoesNotContain(Capabilities.SessionsAttachBlob, offered);
            var pdf = Png(3 * 1024 * 1024 + 77);

            var a = await UploadAsync((op, data) => app.RequestAsync(op, data), pdf, "report.png");
            var response = await app.RequestAsync(Ops.SessionReply, new { id = Id, request = Guid.NewGuid().ToString(), text = "Summarise this", attachments = Files(a) });

            Assert.Equal("delivered", Ok(response).GetProperty("outcome").GetString());
            var said = Assert.Single(injector.Plans).Plan.Sends[0].Text.Split(' ');
            Assert.Equal(pdf, File.ReadAllBytes(said[0][1..]));
            // Written to the private folder as it arrived, and gone from it once typed.
            Assert.Empty(Directory.GetFiles(store.IncomingRoot));

            // Through the relay the cap in chunks stays 256 KiB: larger files go by the blob store.
            var ch = await OpenAsync();
            Assert.DoesNotContain(Capabilities.SessionsAttachDirect, await CapabilitiesAsync(ch));
            var big = await ch.RequestAsync(Ops.AttachmentStart, new { attachment = Guid.NewGuid().ToString(), name = "big.png", size = AttachmentLimits.Bytes + 1, sha256 = new string('A', 43) });
            Assert.Equal("the attachment is larger than 256 KiB", big.GetProperty("error").GetString());
        }
        finally
        {
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }
}